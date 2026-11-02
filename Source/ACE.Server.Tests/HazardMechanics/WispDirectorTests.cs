using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The pure cores of the wisps run: WispBurnBook (strikes due and expiry), WispScript (the cycle/step state machine) and
    /// WispDirector driven through a recording fake of the world. Fake clocks at 100 ms and at 1000 ms ticks.
    /// </summary>
    [TestClass]
    public class WispDirectorTests
    {
        private static readonly double[] TickSizes = { 0.1, 1.0 };

        internal static WispSettings S(string args = "") => WispSettings.From(HazardArgs.Parse(args));

        internal static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        // ---- WispBurnBook -------------------------------------------------------------------------------------

        private static readonly Func<object, bool> AllAlive = _ => true;

        [TestMethod]
        public void BurnBook_StrikesOnlyInsideTheWindow_StartInclusive_UntilExclusive()
        {
            var book = new WispBurnBook();
            var src = new object();
            book.Add(src, Vector3.Zero, 5.0, 1.5);

            Assert.AreEqual(0, book.Due(4.9, AllAlive).Count, "before the burst");
            Assert.AreEqual(1, book.Due(5.0, AllAlive).Count, "the first instant");
            Assert.AreEqual(1, book.Due(6.4, AllAlive).Count, "inside");
            Assert.AreEqual(0, book.Due(6.5, AllAlive).Count, "burst + duration is already over");
            Assert.AreEqual(0, book.Count, "expired and dropped");
            Assert.AreEqual(0, book.Due(7.0, AllAlive).Count);
        }

        [TestMethod]
        public void BurnBook_ABurstShorterThanTheTickStrikesExactlyOnce()
        {
            // added on a tick, the next tick is a full second later: the 0.2 s burst is over before it
            var book = new WispBurnBook();
            var src = new object();
            book.Add(src, Vector3.Zero, 5.0, 0.2);

            Assert.AreEqual(1, book.Due(5.0, AllAlive).Count, "the tick that adds it sees it");
            Assert.AreEqual(0, book.Due(6.0, AllAlive).Count, "and never a second time");

            // added with a start in the past (a late caller): the first look strikes once, the second finds it gone
            var late = new WispBurnBook();
            late.Add(src, Vector3.Zero, 5.0, 0.2);

            Assert.AreEqual(1, late.Due(6.0, AllAlive).Count, "the burst ended between ticks: still one strike");
            Assert.AreEqual(0, late.Due(7.0, AllAlive).Count);
            Assert.AreEqual(0, late.Count);
        }

        [TestMethod]
        public void BurnBook_StrikeFirst_SkipsThatTicksDue_StrikesOnce_ThenTheWindowRunsOn()
        {
            var book = new WispBurnBook();
            var entry = book.Add(new object(), Vector3.Zero, 5.0, 1.5);

            Assert.IsTrue(book.StrikeFirst(entry, AllAlive), "the scheduled first strike");
            Assert.AreEqual(0, book.Due(5.0, AllAlive).Count, "the same tick's Due leaves it out: struck once already");
            Assert.IsFalse(book.StrikeFirst(entry, AllAlive), "a second first strike is refused");
            Assert.AreEqual(1, book.Due(5.5, AllAlive).Count, "a later tick inside the window strikes again");
            Assert.AreEqual(1, book.Count, "still burning");
            Assert.AreEqual(0, book.Due(6.5, AllAlive).Count, "at Until it is over");
            Assert.AreEqual(0, book.Count, "and dropped");

            // a late tick past the whole burst: the first strike still lands, that tick's Due skips it and drops it
            var late = new WispBurnBook();
            var lateEntry = late.Add(new object(), Vector3.Zero, 5.0, 0.3);

            Assert.IsTrue(late.StrikeFirst(lateEntry, AllAlive), "late first strike");
            Assert.AreEqual(0, late.Due(7.0, AllAlive).Count, "no second strike in the tick past Until");
            Assert.AreEqual(0, late.Count, "removed at or past Until");

            // a dead source gets no first strike
            var dead = new WispBurnBook();
            var deadEntry = dead.Add(new object(), Vector3.Zero, 5.0, 1.5);
            Assert.IsFalse(dead.StrikeFirst(deadEntry, _ => false));
        }

        [TestMethod]
        public void BurnBook_ADeadSourceNeverStrikes_AndASourcelessPillarIsNeverAdded()
        {
            var book = new WispBurnBook();
            var dead = new object();
            var live = new object();

            book.Add(dead, Vector3.Zero, 0.0, 5.0);
            book.Add(live, Vector3.One, 0.0, 5.0);
            book.Add(null, Vector3.One, 0.0, 5.0);

            Assert.AreEqual(2, book.Count, "null is not added");

            var strikes = book.Due(1.0, o => !ReferenceEquals(o, dead));

            Assert.AreEqual(1, strikes.Count);
            Assert.AreSame(live, strikes[0].Source);
            Assert.AreEqual(1, book.Count, "the dead one was dropped");

            // dead before it ever got a look
            var late = new WispBurnBook();
            late.Add(dead, Vector3.Zero, 5.0, 0.2);
            Assert.AreEqual(0, late.Due(9.0, _ => false).Count);
        }

        // ---- WispScript on a fake clock ------------------------------------------------------------------------

        private sealed class ScriptRun
        {
            public readonly List<(double Now, WispAction Action)> Log = new List<(double, WispAction)>();
            public readonly List<(double Now, int Cycle, int Step, double Seconds)> Glides = new List<(double, int, int, double)>();
            public readonly List<(int Cycle, int Step, int Slot, double Start, double Until)> Pillars = new List<(int, int, int, double, double)>();
            public readonly Dictionary<int, List<double>> StrikeTimes = new Dictionary<int, List<double>>();
        }

        /// <summary>Drives the script like the director does: answer every Glide with offsets for `crew` wisps and `stepsPerCycle` steps.</summary>
        private static ScriptRun DriveScript(WispSettings s, double dt, int crew, int stepsPerCycle, Func<double, int> live = null)
        {
            var script = new WispScript(s);
            var run = new ScriptRun();
            var book = new WispBurnBook();
            var id = 0;

            for (var now = dt; now < 2000.0 && !script.Ended; now += dt)
            {
                foreach (var a in script.Advance(now, live?.Invoke(now) ?? 1))
                {
                    run.Log.Add((now, a));

                    switch (a.Kind)
                    {
                        case WispActionKind.Glide:
                            run.Glides.Add((now, a.Cycle, a.Step, a.Seconds));
                            script.Glided(now, WispRules.FireOffsets(s.Fire, crew, s.Hold, s.SeqGap, new Random(a.Cycle * 31 + a.Step)), a.Step + 1 < stepsPerCycle);
                            break;

                        case WispActionKind.Burst:
                            var key = ++id;
                            run.Pillars.Add((a.Cycle, a.Step, a.Slot, now, now + s.Burst));
                            run.StrikeTimes[key] = new List<double>();
                            book.Add(key, Vector3.Zero, now, s.Burst);
                            break;
                    }
                }

                foreach (var e in book.Due(now, _ => true))
                    run.StrikeTimes[(int)e.Source].Add(now);
            }

            return run;
        }

        [TestMethod]
        public void Script_NoBurstStartsBeforeGlideStartPlusMovePlusHold_AtAnyTickSize_AndAnyFireMode()
        {
            foreach (var dt in TickSizes)
            {
                foreach (var fire in new[] { "unison", "random", "sequence" })
                {
                    var s = S($"cycles=3 move=2.5 hold=1.5 burst=1.5 rest=1 sweep_move=1 fire={fire} seq_gap=0.3");
                    var run = DriveScript(s, dt, crew: 6, stepsPerCycle: 3);

                    Assert.IsTrue(run.Pillars.Count > 0);

                    foreach (var p in run.Pillars)
                    {
                        var glide = run.Glides.Single(g => g.Cycle == p.Cycle && g.Step == p.Step);

                        Assert.IsTrue(p.Start >= glide.Now + glide.Seconds + s.Hold - 1e-9,
                            $"dt={dt} fire={fire}: burst at {p.Start} but glide {glide.Now} + move {glide.Seconds} + hold {s.Hold}");
                    }
                }
            }
        }

        [TestMethod]
        public void Script_GlideSecondsAreMoveForTheFirstStepAndSweepMoveAfter()
        {
            var s = S("cycles=2 move=2.5 sweep_move=0.7 hold=1.5 burst=1.5 rest=1");
            var run = DriveScript(s, 0.1, 4, 3);

            Assert.AreEqual(6, run.Glides.Count, "2 cycles x 3 steps, one Glide each");

            foreach (var g in run.Glides)
                Assert.AreEqual(g.Step == 0 ? 2.5 : 0.7, g.Seconds, 1e-9);
        }

        [TestMethod]
        public void Script_StrikesFallOnlyInsideEachPillarsWindow_AtBothTickSizes()
        {
            foreach (var dt in TickSizes)
            {
                var s = S("cycles=2 move=2 hold=1.5 burst=1.5 rest=1");
                var run = DriveScript(s, dt, 5, 1);

                Assert.AreEqual(10, run.Pillars.Count);

                var key = 0;

                foreach (var p in run.Pillars)
                {
                    key++;
                    var times = run.StrikeTimes[key];

                    Assert.IsTrue(times.Count >= 1, $"dt={dt}: a pillar must strike at least once");

                    foreach (var t in times)
                        Assert.IsTrue(t >= p.Start - 1e-9 && t < p.Until, $"dt={dt}: strike at {t} outside [{p.Start}, {p.Until})");
                }
            }
        }

        [TestMethod]
        public void Script_ASubTickBurstStrikesExactlyOnce_At1000msTicks()
        {
            var s = S("cycles=1 move=1 hold=1.5 burst=0.2 rest=1");
            var run = DriveScript(s, 1.0, 4, 1);

            Assert.AreEqual(4, run.Pillars.Count);

            foreach (var kv in run.StrikeTimes)
                Assert.AreEqual(1, kv.Value.Count, $"pillar {kv.Key} struck {kv.Value.Count} times");
        }

        [TestMethod]
        public void Script_TheLastPillarFinishesBeforeCompleted_AtBothTickSizes()
        {
            foreach (var dt in TickSizes)
            {
                var s = S("cycles=2 move=2 hold=1.5 burst=1.5 rest=1 fire=sequence seq_gap=0.5");
                var run = DriveScript(s, dt, 4, 2);
                var end = run.Log.Single(l => l.Action.Kind == WispActionKind.End);

                Assert.AreEqual(HazardOutcome.Completed, end.Action.Outcome);
                Assert.AreEqual(run.Log.Last(), end, "End is the last action");
                Assert.IsTrue(end.Now >= run.Pillars.Max(p => p.Until) - 1e-9, $"dt={dt}: Completed at {end.Now} before the last pillar ends at {run.Pillars.Max(p => p.Until)}");
            }
        }

        [TestMethod]
        public void Script_RunsExactlyCyclesCycles_NotOneMore()
        {
            foreach (var cycles in new[] { 1, 2, 5 })
            {
                var run = DriveScript(S($"cycles={cycles}"), 0.1, 3, 1);

                Assert.AreEqual(cycles, run.Glides.Count);
                Assert.AreEqual(cycles - 1, run.Log.Count(l => l.Action.Kind == WispActionKind.Rest), "a rest between cycles, none after the last");
                Assert.AreEqual(cycles, run.Log.Where(l => l.Action.Kind == WispActionKind.Glide).Max(l => l.Action.Cycle));
            }
        }

        [TestMethod]
        public void Script_ZeroLiveWispsEndsAborted_AllWispsLost_AndOneLiveCarriesOn()
        {
            var script = new WispScript(S("cycles=3"));

            Assert.AreEqual(0, script.Advance(0.1, 1).Count);

            var lost = script.Advance(0.2, 0);

            Assert.AreEqual(1, lost.Count);
            Assert.AreEqual(WispActionKind.End, lost[0].Kind);
            Assert.AreEqual(HazardOutcome.Aborted, lost[0].Outcome);
            Assert.AreEqual("all wisps lost", lost[0].Reason);
            Assert.AreEqual(0, script.Advance(0.3, 5).Count, "nothing after the end");

            // a squad down to one wisp carries on to Completed
            var carried = DriveScript(S("cycles=2"), 0.1, 1, 1, now => 1);
            Assert.AreEqual(HazardOutcome.Completed, carried.Log.Single(l => l.Action.Kind == WispActionKind.End).Action.Outcome);

            // lost mid-run
            var mid = DriveScript(S("cycles=4"), 0.1, 3, 1, now => now < 8.0 ? 2 : 0);
            var end = mid.Log.Single(l => l.Action.Kind == WispActionKind.End);

            Assert.AreEqual(HazardOutcome.Aborted, end.Action.Outcome);
            Assert.IsTrue(end.Now >= 8.0 && end.Now < 8.3);
        }

        // ---- WispDirector through a recording fake of the world ------------------------------------------------

        internal sealed class FakeHandle
        {
            public int Id;
            public Vector3 Pos;
            public bool Dead;
            public double Until; // 0 = until Dead is set
        }

        /// <summary>The Pinnacle's west block: x 5..15, y -25..-15, top -48.00. Below the top it is solid, so no cell.</summary>
        internal static bool BlockHasCell(Vector3 p) => !(p.X >= 5f && p.X <= 15f && p.Y >= -25f && p.Y <= -15f && p.Z < -48f);

        internal sealed class FakePort : IWispPort<FakeHandle>
        {
            private double now;

            /// <summary>The fake clock. Setting it runs every scheduled step that has come due first (HazardManager runs ctx.RunDue before Mechanic.Tick).</summary>
            public double Now
            {
                get => now;
                set
                {
                    now = value;
                    RunDue();
                }
            }

            private readonly List<(double At, int Seq, Action Step)> schedule = new List<(double, int, Action)>();

            private int scheduleSeq;

            /// <summary>Every Schedule call: (seconds asked, Now when asked).</summary>
            public readonly List<(double Seconds, double At)> Scheduled = new List<(double, double)>();

            public void Schedule(double seconds, Action step)
            {
                Scheduled.Add((seconds, Now));
                schedule.Add((Now + seconds, ++scheduleSeq, step));
            }

            /// <summary>
            /// The time <see cref="Alive"/> judges a host's Until against: Now, except while a scheduled step runs, when it
            /// is that step's due time. A live host's destroy is itself a due-ordered step, so a step due before it still
            /// finds the host alive even on a late tick; Now stays the tick's time, as HazardContext.Now does.
            /// </summary>
            private double? aliveAt;

            private void RunDue()
            {
                while (true)
                {
                    var due = schedule.Where(e => e.At <= now + 1e-9).OrderBy(e => e.At).ThenBy(e => e.Seq).Cast<(double At, int Seq, Action Step)?>().FirstOrDefault();

                    if (due == null)
                        return;

                    schedule.RemoveAll(e => e.Seq == due.Value.Seq);
                    aliveAt = due.Value.At;

                    try
                    {
                        due.Value.Step();
                    }
                    finally
                    {
                        aliveAt = null;
                    }
                }
            }

            /// <summary>ceiling_beam: every beam spawned (pillar point, seconds, Now at spawn) and its handle; BeamsFail makes the spawn return null.</summary>
            public readonly List<(Vector3 Point, double Seconds, double At)> CeilingBeams = new List<(Vector3, double, double)>();

            public readonly List<FakeHandle> BeamHandles = new List<FakeHandle>();

            public bool BeamsFail;

            /// <summary>Does the region have a ceiling (default true)?</summary>
            public bool HasCeiling { get; set; } = true;

            public FakeHandle SpawnCeilingBeam(Vector3 pillar, double seconds)
            {
                if (BeamsFail)
                    return null;

                CeilingBeams.Add((pillar, seconds, Now));
                var beam = new FakeHandle { Id = 8000 + CeilingBeams.Count, Pos = pillar, Until = Now + seconds };
                BeamHandles.Add(beam);
                return beam;
            }

            public Random Rng { get; } = new Random(11);
            public Func<Vector3, bool> Open = BlockHasCell;
            public Func<Vector3, bool> SpawnFails = _ => false;

            public readonly List<(List<(FakeHandle Handle, Vector3 End)> Group, double Seconds, HazardMoveOptions Options, double At)> Moves
                = new List<(List<(FakeHandle, Vector3)>, double, HazardMoveOptions, double)>();

            public readonly List<(Vector3 Point, double Seconds, bool Ok, double At)> Spawns = new List<(Vector3, double, bool, double)>();
            public readonly List<(FakeHandle Source, Vector3 Centre, float Radius, double Window, double At)> Strikes = new List<(FakeHandle, Vector3, float, double, double)>();
            public readonly List<string> Said = new List<string>();
            public readonly List<string> Logs = new List<string>();
            public readonly List<FakeHandle> Hosts = new List<FakeHandle>();
            public HazardOutcome? Outcome;
            public string Reason;
            public double EndedAt;
            public int Ends;

            /// <summary>A2: the run's live count as the director reads it (null = n). LiveCountReads counts the director's reads.</summary>
            public int? LiveCountValue;

            public Func<double, int?> LiveCountAt;

            public int LiveCountReads;

            public int? LiveCount
            {
                get
                {
                    LiveCountReads++;
                    return LiveCountAt != null ? LiveCountAt(Now) : LiveCountValue;
                }
            }

            public readonly List<(Vector3 Point, double At)> WispSpawns = new List<(Vector3, double)>();

            public Func<Vector3, bool> WispSpawnFails = _ => false;

            public FakeHandle SpawnWisp(Vector3 point)
            {
                WispSpawns.Add((point, Now));

                if (WispSpawnFails(point))
                    return null;

                return new FakeHandle { Id = 2000 + WispSpawns.Count, Pos = point };
            }

            public bool Alive(FakeHandle h) => h != null && !h.Dead && (h.Until <= 0.0 || (aliveAt ?? Now) < h.Until);

            public Vector3 PositionAt(FakeHandle h, double now) => h.Pos;

            public Vector3 PointFor(float x, float y) => new Vector3(x, y, -51f);

            public bool HasCell(Vector3 p) => Open(p);

            public void MoveLegs(IReadOnlyList<(FakeHandle Handle, Vector3 End)> group, double seconds, HazardMoveOptions options)
            {
                Moves.Add((group.ToList(), seconds, options, Now));

                foreach (var (h, end) in group)
                    h.Pos = end;
            }

            /// <summary>The follower tests set this: where the followed player is at a given time; null = nobody in the region.</summary>
            public Func<double, Vector3?> Target = _ => null;

            public readonly List<(FakeHandle Handle, Vector3 End, double Seconds, double At)> Legs = new List<(FakeHandle, Vector3, double, double)>();

            public bool MoveLeg(FakeHandle handle, Vector3 end, double seconds)
            {
                if (!Alive(handle))
                    return false;

                Legs.Add((handle, end, seconds, Now));
                handle.Pos = end;
                return true;
            }

            /// <summary>Who the followed player is at a given time (the follower resets when this changes).</summary>
            public Func<double, uint> TargetId = _ => 1u;

            public double TickSeconds { get; set; } = 0.1;

            public bool TryFollowTarget(out Vector3 position, out uint targetId)
            {
                var t = Target(Now);
                position = t ?? default;
                targetId = TargetId(Now);
                return t.HasValue;
            }

            public FakeHandle SpawnPillar(Vector3 point, double seconds)
            {
                var ok = !SpawnFails(point);
                Spawns.Add((point, seconds, ok, Now));

                if (!ok)
                    return null;

                var host = new FakeHandle { Id = 1000 + Hosts.Count, Pos = point, Until = Now + seconds };
                Hosts.Add(host);
                return host;
            }

            public readonly List<(Vector3 Point, double Seconds, double At)> DestMarks = new List<(Vector3, double, double)>();

            /// <summary>Every mark handle spawned, in DestMarks order, so a test can ask which are still standing.</summary>
            public readonly List<FakeHandle> MarkHandles = new List<FakeHandle>();

            public readonly List<(FakeHandle Handle, double At)> Destroyed = new List<(FakeHandle, double)>();

            public FakeHandle SpawnDestMark(Vector3 point, double seconds)
            {
                DestMarks.Add((point, seconds, Now));
                var mark = new FakeHandle { Id = 5000 + DestMarks.Count, Pos = point, Until = Now + seconds };
                MarkHandles.Add(mark);
                return mark;
            }

            public void Destroy(FakeHandle handle)
            {
                if (handle == null)
                    return;

                Destroyed.Add((handle, Now));
                handle.Dead = true;
            }

            public Vector3 RestOf(FakeHandle h) => h.Pos;

            /// <summary>When set, decides how many players a strike hits (a ledger model); otherwise every strike hits one.</summary>
            public Func<FakeHandle, double, double, int> StrikeHook;

            public int Strike(FakeHandle source, Vector3 centre, float radius, double window)
            {
                Strikes.Add((source, centre, radius, window, Now));
                return StrikeHook != null ? StrikeHook(source, Now, window) : 1;
            }

            public void Say(string line) => Said.Add(line);

            public void Log(string line) => Logs.Add(line);

            public void End(HazardOutcome outcome, string reason)
            {
                Ends++;
                Outcome = outcome;
                Reason = reason;
                EndedAt = Now;
            }
        }

        internal static List<FakeHandle> Squad(int n)
            => Enumerable.Range(0, n).Select(i => new FakeHandle { Id = i, Pos = new Vector3(-2f + i * 0.5f, -30f, -51f) }).ToList();

        internal static FakePort Run(WispDirector<FakeHandle> director, FakePort port, double dt, Action<double> each = null, double maxSeconds = 1500.0)
        {
            director.Start();

            for (var t = dt; t <= maxSeconds && !director.Ended; t += dt)
            {
                port.Now = t;
                each?.Invoke(t);
                director.Tick(t);
            }

            return port;
        }

        internal static (WispDirector<FakeHandle> Director, FakePort Port, List<FakeHandle> Wisps) Setup(string args, string pattern, HazardRegion region = null, int squad = 8, FakeHandle follower = null)
        {
            var s = S(args);
            var port = new FakePort();
            var wisps = Squad(squad);

            return (new WispDirector<FakeHandle>(region ?? Pinnacle(), s, pattern, port, wisps, follower), port, wisps);
        }

        [TestMethod]
        public void Director_OneMoveLegsPerStepCoversTheWholeCrew_WithStandAsTheEnd_FollowAndSeconds()
        {
            foreach (var pattern in new[] { "diagonal", "row", "ring", "scatter" })
            {
                var (director, port, wisps) = Setup("cycles=3 move=2 hold=1.5 burst=1 rest=0.5 dir=n n=6", pattern);
                var s = S("cycles=3 move=2 hold=1.5 burst=1 rest=0.5 dir=n n=6");

                Run(director, port, 0.1);

                Assert.AreEqual(HazardOutcome.Completed, port.Outcome, pattern);
                Assert.AreEqual(3, port.Moves.Count, $"{pattern}: exactly one MoveLegs per step (3 cycles of one step), not one per wisp");

                foreach (var m in port.Moves)
                {
                    Assert.IsTrue(m.Group.Count >= 3, $"{pattern}: the whole crew moves in one call, got {m.Group.Count}");
                    Assert.AreEqual(m.Group.Count, m.Group.Select(g => g.Handle).Distinct().Count());
                    Assert.AreEqual(2.0, m.Seconds, 1e-9, "the glide takes move seconds");
                    Assert.AreEqual(HazardFollow.HazardHeight, m.Options.Follow, "wisps fly at the hazard height");

                    foreach (var (h, end) in m.Group)
                    {
                        Assert.IsTrue(BlockHasCell(end), $"{pattern}: a wisp was sent INTO a solid at {end} (the Stand point, not the Pillar point, is its end)");
                        Assert.AreEqual(-51f, end.Z, 1e-4, $"{pattern}: a wisp stays at the hazard plane; its end is never the raised Pillar point");
                        Assert.IsTrue(BlockHasCell(new Vector3(end.X, end.Y, -51f)), $"{pattern}: a wisp end over the block's footprint at wisp height");
                    }
                }
            }
        }

        [TestMethod]
        public void Director_MarkDest_MarksEachPillarPointFromTheGlideUntilItsBurst_AndNeverStrikes()
        {
            // C13: opt-in destination telegraph
            foreach (var fire in new[] { "unison", "sequence" })
            {
                var args = $"cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=5 fire={fire} mark_dest=1";
                var (director, port, _) = Setup(args, "row");

                Run(director, port, 0.1);

                Assert.AreEqual(HazardOutcome.Completed, port.Outcome, fire);
                Assert.IsTrue(port.DestMarks.Count > 0, $"{fire}: marks were placed");

                var glideStarts = port.Moves.Select(m => m.At).ToList();
                var pillars = port.Spawns.Where(p => p.Ok).ToList();

                foreach (var mark in port.DestMarks)
                {
                    Assert.IsTrue(glideStarts.Any(g => Math.Abs(g - mark.At) < 1e-9), $"{fire}: a mark goes down when its glide starts");

                    var pillar = pillars.Where(p => p.At >= mark.At && Math.Abs(p.Point.X - mark.Point.X) < 1e-4 && Math.Abs(p.Point.Y - mark.Point.Y) < 1e-4)
                        .OrderBy(p => p.At).FirstOrDefault();

                    Assert.IsTrue(pillar.Seconds > 0, $"{fire}: every mark sits under a pillar that later bursts");
                    Assert.AreEqual(pillar.At, mark.At + mark.Seconds, 0.1 + 1e-9, $"{fire}: the mark stands until its pillar bursts (within one tick)");
                }

                Assert.AreEqual(pillars.Count, port.DestMarks.Count, $"{fire}: one mark per burst");
                Assert.IsTrue(port.Strikes.All(st => st.Source.Id < 5000), $"{fire}: a mark never strikes");
            }
        }

        [TestMethod]
        public void Director_MarkDest_OffByDefault()
        {
            var (director, port, _) = Setup("cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=5", "row");

            Run(director, port, 0.1);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.AreEqual(0, port.DestMarks.Count);
            Assert.IsFalse(S("").MarkDest);
            Assert.IsTrue(S("mark_dest=1").MarkDest);
        }

        [TestMethod]
        public void MarkDest_IsDocumentedInTheSettingsDescriptionAndTheUsage()
        {
            var settings = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs");
            StringAssert.Contains(settings, "mark_dest=<1");

            var usage = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/HazardCommands.cs");
            var line = usage.Split('\n').Single(l => l.Contains("args+: mark_dest=1|0"));
            var text = line.Trim().TrimEnd('+').Trim().Trim('"');
            Assert.IsTrue(text.Length <= 250, $"usage line is {text.Length} chars");

            StringAssert.Contains(ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/WispRules.cs"), "a.GetBool(\"mark_dest\", false)");
        }

        [TestMethod]
        public void Director_ABlockedTargetStandsShort_AndItsPillarFiresRaisedOnTheSolidsTop()
        {
            var (director, port, _) = Setup("cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=7", "diagonal");

            Run(director, port, 0.1);

            var raised = port.Spawns.Where(p => p.Ok && p.Point.Z > -50f).ToList();

            Assert.IsTrue(raised.Count > 0, "the diagonal crosses the block: some pillars must fire from its top");

            foreach (var r in raised)
            {
                Assert.AreEqual(-48f, r.Point.Z, 1e-4, "raised onto the solid's top");
                Assert.IsTrue(BlockHasCell(r.Point));
            }

        }

        [TestMethod]
        public void Director_StrikesUseTheSharedRadiusAndWindow_OnlyWhileThePillarBurns_AndNeverFromADeadHost()
        {
            var (director, port, _) = Setup("cycles=1 move=2 hold=1.5 burst=1.5 rest=0.5 n=5 pillar_radius=3", "row");

            Run(director, port, 0.1);

            Assert.IsTrue(port.Strikes.Count > 0);

            foreach (var st in port.Strikes)
            {
                Assert.AreEqual(3f, st.Radius, "the hit radius is pillar_radius");
                Assert.IsTrue(double.IsPositiveInfinity(st.Window), "the hit window is one hit per pillar host, like the balls");
                Assert.IsTrue(st.At >= st.Source.Until - 1.5 - 1e-9 && st.At < st.Source.Until, "only while the host burns");
            }

            // every pillar struck, none twice in one tick
            Assert.AreEqual(port.Hosts.Count, port.Strikes.Select(s => s.Source).Distinct().Count());
            Assert.AreEqual(port.Strikes.Count, port.Strikes.Select(s => (s.Source, s.At)).Distinct().Count());
        }

        /// <summary>The live ledger rule (HazardContext.HitPlayers): one hit per (source, player) while HazardHitRules.WindowOpen says so.</summary>
        private static Func<FakeHandle, double, double, int> Ledger(Func<double, bool> playerInReach, Dictionary<(int, int), double> last)
            => (source, now, window) =>
            {
                if (!playerInReach(now))
                    return 0;

                var key = (source.Id, 1);
                double? prev = last.TryGetValue(key, out var t) ? t : (double?)null;

                if (!HazardHitRules.WindowOpen(prev, now, window))
                    return 0;

                last[key] = now;
                return 1;
            };

        [TestMethod]
        public void Director_ALedgerWithAParkedPlayer_HitsExactlyOncePerBurst()
        {
            var (director, port, _) = Setup("cycles=3 move=1 hold=1.5 burst=1.5 rest=0.5 n=1", "scatter", squad: 1);

            var hits = 0;
            var ledger = new Dictionary<(int, int), double>();

            port.StrikeHook = (src, now, win) => { var h = Ledger(_ => true, ledger)(src, now, win); hits += h; return h; };

            Run(director, port, 0.1);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.AreEqual(3, port.Hosts.Count, "one pillar per burst");
            Assert.IsTrue(port.Strikes.Count > 3 * 5, "each burst is struck on many ticks, so the ledger is really doing the work");
            Assert.AreEqual(3, hits, "three bursts, three hits: not one per tick");
        }

        [TestMethod]
        public void Director_APlayerWhoWalksInAtABurstsLastTick_GetsExactlyOneHit()
        {
            // pass 1: find the last tick that strikes the first pillar
            var (d1, p1, _) = Setup("cycles=1 move=1 hold=1.5 burst=1.5 rest=0.5 n=1", "scatter", squad: 1);
            Run(d1, p1, 0.1);
            var lastTick = p1.Strikes.Where(s => ReferenceEquals(s.Source, p1.Hosts[0])).Max(s => s.At);

            // pass 2: the player is only in reach on that last tick
            var (d2, p2, _) = Setup("cycles=1 move=1 hold=1.5 burst=1.5 rest=0.5 n=1", "scatter", squad: 1);
            var hits = 0;
            var ledger = new Dictionary<(int, int), double>();

            p2.StrikeHook = (src, now, win) => { var h = Ledger(t => t >= lastTick - 1e-9, ledger)(src, now, win); hits += h; return h; };
            Run(d2, p2, 0.1);

            Assert.AreEqual(1, hits, "the late arrival is hit once, on the last tick");
        }

        [TestMethod]
        public void Director_ASubTickBurstStrikesEachPillarExactlyOnce_At1000msTicks()
        {
            var (director, port, _) = Setup("cycles=2 move=1 hold=1.5 burst=0.2 rest=0.5 n=5", "ring");

            Run(director, port, 1.0);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.IsTrue(port.Hosts.Count >= 6);

            foreach (var host in port.Hosts)
                Assert.AreEqual(1, port.Strikes.Count(st => ReferenceEquals(st.Source, host)), $"pillar {host.Id}");
        }

        [TestMethod]
        public void Director_APillarIsOnScreenForStrikeDelayBeforeItsFirstStrike()
        {
            // a hit sent in the pillar's spawn tick could kill before the client ever drew it
            foreach (var (args, expected) in new[] { ("", 0.2), ("strike_delay=0.35", 0.35), ("strike_delay=0", 0.0) })
            {
                var (director, port, _) = Setup($"cycles=2 move=1 hold=1.5 burst=1.5 rest=0.5 n=5 {args}", "row");

                Run(director, port, 0.1);

                Assert.AreEqual(HazardOutcome.Completed, port.Outcome, args);
                Assert.IsTrue(port.Hosts.Count > 0, args);

                foreach (var host in port.Hosts)
                {
                    var spawnedAt = host.Until - 1.5;
                    var strikes = port.Strikes.Where(st => ReferenceEquals(st.Source, host)).Select(st => st.At).ToList();

                    Assert.IsTrue(strikes.Count > 0, $"{args}: pillar {host.Id} still strikes");
                    Assert.AreEqual(spawnedAt + expected, strikes.Min(), 0.1 + 1e-9, $"{args}: first strike within one tick of strike_delay");
                    Assert.IsTrue(strikes.Min() >= spawnedAt + expected - 1e-9, $"{args}: never before strike_delay");
                }
            }
        }

        [TestMethod]
        public void StrikeDelay_IsCappedAtHalfTheBurst()
        {
            Assert.AreEqual(0.2, S("").StrikeDelaySeconds, 1e-9, "the default");
            Assert.AreEqual(0.3, S("burst=0.6 strike_delay=1").StrikeDelaySeconds, 1e-9, "half the burst");
            Assert.AreEqual(0.1, S("burst=0.2").StrikeDelaySeconds, 1e-9, "half the shortest burst");
            Assert.AreEqual(1.0, S("strike_delay=5").StrikeDelay, 1e-9, "clamped to 1 s");
        }

        [TestMethod]
        public void Director_ADelayedPillarStillStrikesOnce_WhenNoTickFallsInsideItsWindow()
        {
            // 1000 ms ticks against a [0.2, 0.5) strike window: no tick ever lands inside it, and the host's destroy is
            // due before the next tick. The scheduled first strike is what keeps every pillar from striking zero times.
            foreach (var args in new[] { "burst=0.5 strike_delay=0.2", "burst=0.2", "burst=0.3 strike_delay=1" })
            {
                var (director, port, _) = Setup($"cycles=2 move=1 hold=1.5 rest=0.5 n=5 {args}", "ring");

                port.TickSeconds = 1.0;
                Run(director, port, 1.0);

                Assert.AreEqual(HazardOutcome.Completed, port.Outcome, args);
                Assert.IsTrue(port.Hosts.Count >= 6, args);

                foreach (var host in port.Hosts)
                    Assert.AreEqual(1, port.Strikes.Count(st => ReferenceEquals(st.Source, host)), $"{args}: pillar {host.Id}");
            }
        }

        [TestMethod]
        public void Director_ACompletedRunEndsOnlyAfterTheLastPillarHasBurned()
        {
            foreach (var dt in TickSizes)
            {
                var (director, port, _) = Setup("cycles=2 move=2 hold=1.5 burst=1.5 rest=1 n=5 fire=sequence seq_gap=0.4", "ring");

                Run(director, port, dt);

                Assert.AreEqual(1, port.Ends);
                Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
                Assert.IsTrue(port.EndedAt >= port.Hosts.Max(h => h.Until) - 1e-9, $"dt={dt}: ended at {port.EndedAt}, a pillar burns until {port.Hosts.Max(h => h.Until)}");
            }
        }

        [TestMethod]
        public void Director_RunsExactlyCyclesCycles()
        {
            foreach (var cycles in new[] { 1, 3 })
            {
                var (director, port, _) = Setup($"cycles={cycles} move=1 hold=1.5 burst=0.5 rest=0.5 n=4", "scatter");

                Run(director, port, 0.1);

                Assert.AreEqual(cycles, port.Moves.Count);
                Assert.AreEqual(cycles, port.Logs.Count(l => l.StartsWith("cycle ") && l.Contains("pattern=")));
            }
        }

        [TestMethod]
        public void Director_ASweepIsOneMoveLegsPerRow_FirstWithMove_ThenSweepMove()
        {
            var (director, port, _) = Setup("cycles=1 move=2 sweep_move=0.6 hold=1.5 burst=1 rest=0.5 n=5 dir=n", "sweep");

            Run(director, port, 0.1);

            Assert.AreEqual(7, port.Moves.Count, "the Pinnacle sweep from the north has 7 rows");
            Assert.AreEqual(2.0, port.Moves[0].Seconds, 1e-9);

            foreach (var m in port.Moves.Skip(1))
                Assert.AreEqual(0.6, m.Seconds, 1e-9);

            for (var i = 1; i < port.Moves.Count; i++)
                Assert.IsTrue(port.Moves[i].At >= port.Moves[i - 1].At + 0.6 + 1.5 + 1.0 - 1e-9, "a row starts only after the previous one burned out");
        }

        [TestMethod]
        public void Director_ADeadWispIsLeftOutOfTheCrew_ItsBurstIsSkipped_AndTheRunCarriesOn()
        {
            var (director, port, wisps) = Setup("cycles=3 move=1 hold=1.5 burst=1 rest=0.5 n=6", "ring");
            var victim = wisps[2];

            Run(director, port, 0.1, t => { if (t >= 4.0) victim.Dead = true; });

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome, "one lost wisp does not end the run");
            Assert.IsTrue(port.Moves.Skip(1).All(m => m.Group.All(g => !ReferenceEquals(g.Handle, victim))), "a dead wisp is not sent anywhere");
        }

        [TestMethod]
        public void Director_BurstsBeforeAWispDiesAreSkippedWhenItDiesInTheTelegraph()
        {
            var (director, port, wisps) = Setup("cycles=1 move=1 hold=1.5 burst=1 rest=0.5 n=5", "ring", squad: 5);
            var victim = wisps[0];
            var armed = false;

            director.Start();

            for (var t = 0.1; t < 60.0 && !director.Ended; t += 0.1)
            {
                port.Now = t;

                if (!armed && port.Moves.Count == 1 && t > port.Moves[0].At + 1.2)
                {
                    victim.Dead = true;
                    armed = true;
                }

                director.Tick(t);
            }

            Assert.IsTrue(armed);
            Assert.AreEqual(4, port.Hosts.Count, "5 slots, one wisp died before its burst: 4 pillars");
            Assert.IsTrue(port.Logs.Any(l => l.Contains("burst skipped")));
        }

        [TestMethod]
        public void Director_AllWispsLost_EndsAborted_WithTheReason()
        {
            var (director, port, wisps) = Setup("cycles=4 move=1 hold=1.5 burst=1 rest=0.5 n=4", "scatter", squad: 4);

            Run(director, port, 0.1, t => { if (t >= 3.0) wisps.ForEach(w => w.Dead = true); });

            Assert.AreEqual(1, port.Ends);
            Assert.AreEqual(HazardOutcome.Aborted, port.Outcome);
            Assert.AreEqual("all wisps lost", port.Reason);
            Assert.IsTrue(port.Said.Count >= 1);
        }

        [TestMethod]
        public void Director_APillarHostThatWillNotSpawn_IsRetriedAtTheStand_ThenGivesUpWithoutDamage()
        {
            // first attempt (the pillar point) fails everywhere inside the block; the retry at the stand point succeeds
            var (director, port, _) = Setup("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=7", "diagonal");
            port.SpawnFails = p => p.Z > -50f;

            Run(director, port, 0.1);

            var failed = port.Spawns.Where(sp => !sp.Ok).ToList();
            Assert.IsTrue(failed.Count > 0, "some raised pillar points were refused");
            Assert.AreEqual(port.Spawns.Count - failed.Count, port.Hosts.Count);

            foreach (var f in failed)
            {
                var retry = port.Spawns.Where(sp => sp.At == f.At && sp.Point != f.Point).ToList();
                Assert.IsTrue(retry.Any(r => r.Ok && r.Point.Z <= -50f), "retried at the wisp's stand point (hazard height)");
            }

            // both attempts fail: nothing is damaged from that slot, and the coordinates are logged
            var (d2, p2, _) = Setup("cycles=1 move=2 hold=1.5 burst=1 rest=0.5 n=7", "diagonal");
            p2.SpawnFails = p => p.Y > -26.5f; // the pillar point AND the stand point of every slot at or beyond the block

            Run(d2, p2, 0.1);

            Assert.AreEqual(HazardOutcome.Completed, p2.Outcome);
            Assert.IsTrue(d2.Unseen > 0);
            Assert.AreEqual(d2.Pillars, p2.Hosts.Count, "only spawned hosts count");
            Assert.AreEqual(p2.Hosts.Count, p2.Strikes.Select(s => s.Source).Distinct().Count(), "every strike has a visible host; the failed slots strike nothing");
            Assert.IsTrue(p2.Logs.Any(l => l.Contains("would not spawn") && l.Contains("no damage")));
        }

        [TestMethod]
        public void Director_AnEmptyPlanSaysOneLineAndEndsAborted()
        {
            // tile the whole region with small exclusions (each under a pillar's reach): no spot is placeable
            var tiles = new List<HazardExclusion>();

            for (var x = 0; x < 20; x += 2)
                for (var y = 0; y < 20; y += 2)
                    tiles.Add(HazardExclusion.Rect(x, x + 2, y, y + 2));

            var region = HazardRegion.Rect("tiled", 0x0123, 0, false, 0f, 20f, 0f, 20f, HazardGroundMode.Plane, 0f, null, 0f, null, 2f, 6f, tiles);

            foreach (var pattern in new[] { "scatter", "row", "ring", "mixed" })
            {
                var s = S("cycles=2 n=5");
                var port = new FakePort();
                var director = new WispDirector<FakeHandle>(region, s, pattern, port, Squad(5));

                Run(director, port, 0.1);

                Assert.AreEqual(1, port.Ends, pattern);
                Assert.AreEqual(HazardOutcome.Aborted, port.Outcome, pattern);
                Assert.AreEqual(1, port.Said.Count, pattern + ": one line");
                Assert.AreEqual(0, port.Moves.Count);
                Assert.AreEqual(0, port.Spawns.Count);
            }
        }

        // ---- ceiling_beam -------------------------------------------------------------------------------------

        private static bool SameXY(Vector3 a, Vector3 b) => Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Y - b.Y) < 1e-3;

        [TestMethod]
        public void Director_CeilingBeam_FallsOnEachPillarPointForTheLastSecondsBeforeItsBurst()
        {
            foreach (var fire in new[] { "unison", "sequence" })
            {
                var (director, port, _) = Setup($"cycles=2 move=2 hold=1.5 burst=1 rest=0.5 n=5 fire={fire} ceiling_beam=1", "row");

                Run(director, port, 0.1);

                Assert.AreEqual(HazardOutcome.Completed, port.Outcome, fire);

                var pillars = port.Spawns.Where(p => p.Ok).ToList();

                Assert.IsTrue(pillars.Count > 0, fire);
                Assert.AreEqual(pillars.Count, port.CeilingBeams.Count, $"{fire}: one beam per burst");

                foreach (var pillar in pillars)
                {
                    var beam = port.CeilingBeams.Single(b => SameXY(b.Point, pillar.Point) && b.At <= pillar.At);

                    Assert.IsTrue(beam.Seconds <= 1.0 + 1e-9 && beam.Seconds > 1.0 - 0.1 - 1e-9, $"{fire}: a beam lasts ceiling_beam seconds, less the tick it started late");
                    Assert.IsTrue(beam.At + beam.Seconds <= pillar.At + 1e-6, $"{fire}: a beam never outlives its burst");
                    Assert.AreEqual(pillar.At, beam.At + beam.Seconds, 0.1 + 1e-9, $"{fire}: the beam ends at the burst (within one tick)");
                }

                Assert.IsTrue(port.Strikes.All(st => st.Source.Id < 5000), $"{fire}: a beam never strikes");
                Assert.IsTrue(port.Strikes.All(st => st.Source.Id < 8000), $"{fire}: a beam is never in the burn book");
            }
        }

        [TestMethod]
        public void Director_CeilingBeam_LongerThanGlidePlusHold_StartsAtTheGlide()
        {
            var (director, port, _) = Setup("cycles=1 move=1 hold=1.5 burst=1 rest=0.5 n=5 ceiling_beam=5", "row");

            Run(director, port, 0.1);

            Assert.IsTrue(port.CeilingBeams.Count > 0);

            var glideAt = port.Moves.Single().At;

            foreach (var beam in port.CeilingBeams)
            {
                Assert.AreEqual(glideAt, beam.At, 0.1 + 1e-9, "the beam starts with the glide, not before it");
                Assert.AreEqual(glideAt + 2.5, beam.At + beam.Seconds, 1e-6, "and lasts until the burst (glide + hold, unison)");
            }
        }

        [TestMethod]
        public void Director_CeilingBeamZero_SpawnsNoBeamAndSchedulesNothing()
        {
            var (director, port, _) = Setup("cycles=2 move=1 hold=1.5 burst=1 rest=0.5 n=5 follow=1 mark_dest=1", "row", follower: new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) });
            port.Target = _ => new Vector3(20f, -30f, -51f);

            Run(director, port, 0.1);

            Assert.IsTrue(port.Hosts.Count > 0, "control: the run burst");
            Assert.AreEqual(0, port.CeilingBeams.Count);
            AssertOnlyFirstStrikesScheduled(port);
        }

        [TestMethod]
        public void Director_CeilingBeam_ADeadWispBeforeItsBeamStartsGetsNoBeam()
        {
            var (director, port, _) = Setup("cycles=1 move=1 hold=1.5 burst=1 rest=0.5 n=5 ceiling_beam=0.5", "row", squad: 5);
            FakeHandle victim = null;

            // glide 0.1..1.1, hold to 2.6, beam from ~2.1: die in the middle of the hold, before it
            Run(director, port, 0.1, t =>
            {
                if (victim == null && port.Moves.Count == 1 && t >= port.Moves[0].At + 1.5)
                {
                    victim = port.Moves[0].Group[0].Handle;
                    victim.Dead = true;
                }
            });

            Assert.IsNotNull(victim);
            Assert.AreEqual(port.Moves[0].Group.Count - 1, port.Hosts.Count, "control: the dead wisp's burst was skipped");
            Assert.AreEqual(port.Hosts.Count, port.CeilingBeams.Count, "and so was its beam");
        }

        [TestMethod]
        public void Director_CeilingBeam_FollowerBeamFallsOnTheBeatPoint()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = Setup("follow=1 cycles=1 move=1 hold=2 burst=1 rest=0.5 n=3 follow_lag=2 ceiling_beam=1", WispRules.Scatter, squad: 3, follower: follower);
            var spot = new Vector3(20f, -30f, -51f);

            port.Target = _ => spot;

            Run(director, port, 0.1);

            var burst = port.Spawns.Single(sp => sp.Ok && SameXY(sp.Point, spot));
            var beam = port.CeilingBeams.Single(b => SameXY(b.Point, spot));

            Assert.IsTrue(beam.Seconds <= 1.0 + 1e-9 && beam.Seconds > 0.9 - 1e-9);
            Assert.IsTrue(beam.At + beam.Seconds <= burst.At + 1e-6, "the follower's beam never outlives its burst");
            Assert.AreEqual(burst.At, beam.At + beam.Seconds, 0.1 + 1e-9, "the follower's beam ends at its burst");
            Assert.AreEqual(0, port.Destroyed.Count, "a burst that fires leaves its beam to burn out");
        }

        [TestMethod]
        public void Director_CeilingBeam_AnAbandonedBeatNeverSpawnsOrDestroysItsBeam()
        {
            var spot = new Vector3(20f, -30f, -51f);

            // gone before the beam starts (hold starts ~1.1, burst ~3.1, beam from ~2.1): no beam is ever spawned
            var (d1, p1, _) = Setup("follow=1 cycles=1 move=1 hold=2 burst=1 rest=0.5 n=3 follow_lag=2 ceiling_beam=1", WispRules.Scatter, squad: 3,
                follower: new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) });
            p1.Target = t => t < 1.8 ? spot : (Vector3?)null;

            Run(d1, p1, 0.1);

            Assert.AreEqual(0, p1.Spawns.Count(sp => sp.Ok && SameXY(sp.Point, spot)), "control: no follower burst");
            Assert.AreEqual(0, p1.CeilingBeams.Count(b => SameXY(b.Point, spot)), "the scheduled beam of the abandoned beat never spawns");

            // gone after it started: the beam that is up is destroyed
            var (d2, p2, _) = Setup("follow=1 cycles=1 move=1 hold=2 burst=1 rest=0.5 n=3 follow_lag=2 ceiling_beam=1", WispRules.Scatter, squad: 3,
                follower: new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) });
            p2.Target = t => t < 2.6 ? spot : (Vector3?)null;

            Run(d2, p2, 0.1);

            var index = p2.CeilingBeams.FindIndex(b => SameXY(b.Point, spot));

            Assert.AreEqual(0, p2.Spawns.Count(sp => sp.Ok && SameXY(sp.Point, spot)), "control: no follower burst");
            Assert.IsTrue(index >= 0, "control: the beam was up before the target left");
            Assert.IsTrue(p2.Destroyed.Any(d => ReferenceEquals(d.Handle, p2.BeamHandles[index])), "and was destroyed when the beat was abandoned");
        }

        [TestMethod]
        public void Director_CeilingBeam_ANullSpawnLogsOncePerRun()
        {
            // strike_delay=0: a pillar's delayed first strike is a scheduled step too, and the control counts only beam steps
            var (director, port, _) = Setup("cycles=4 move=1 hold=1.5 burst=1 rest=0.5 n=4 ceiling_beam=1 strike_delay=0", "row");
            port.BeamsFail = true;

            Run(director, port, 0.1);

            Assert.IsTrue(port.Scheduled.Count > 1, "control: several beams were attempted");
            Assert.AreEqual(0, port.CeilingBeams.Count);
            Assert.AreEqual(1, port.Logs.Count(l => l.Contains("ceiling beam would not spawn")));
        }

        /// <summary>
        /// No beam step was scheduled: the only steps are the pillars' delayed first strikes, one per pillar that spawned,
        /// each due strike_delay (default 0.2 s, burst 1) after its pillar.
        /// </summary>
        private static void AssertOnlyFirstStrikesScheduled(FakePort port)
        {
            Assert.AreEqual(port.Hosts.Count, port.Scheduled.Count, "one scheduled step per pillar that spawned (its first strike), no beam steps");
            Assert.IsTrue(port.Scheduled.All(e => e.Seconds <= 0.2 + 1e-9), "every scheduled step is a first strike, due strike_delay after its pillar");
        }

        private static void RunAt(WispDirector<FakeHandle> director, FakePort port, IEnumerable<double> times)
        {
            director.Start();

            foreach (var t in times)
            {
                if (director.Ended)
                    break;

                port.Now = t;
                director.Tick(t);
            }
        }

        /// <summary>When the first glide starts on a regular 0.1 s clock (the script has a warm-up delay).</summary>
        private static double GlideStart(string args)
        {
            var (director, port, _) = Setup(args, "row", squad: 3);

            Run(director, port, 0.1);

            return port.Moves[0].At;
        }

        private static IEnumerable<double> Ticks(double from, double to, double dt)
        {
            for (var t = from; t <= to + 1e-9; t += dt)
                yield return t;
        }

        [TestMethod]
        public void Director_CeilingBeam_ATickThatJumpsAcrossTheBurstSpawnsNoBeam()
        {
            // glide at g, burst at g + 2.5, beam due g + 1.5; the clock goes g + 1.0 -> g + 2.9
            // strike_delay=0: a pillar's delayed first strike is a scheduled step too, and the control counts only beam steps
            const string args = "cycles=1 move=1 hold=1.5 burst=1 rest=0.5 n=3 ceiling_beam=1 strike_delay=0";
            var g = GlideStart(args);
            var (director, port, _) = Setup(args, "row", squad: 3);

            RunAt(director, port, Ticks(0.1, g + 1.0, 0.1).Concat(Ticks(g + 2.9, g + 8.0, 0.1)));

            Assert.IsTrue(port.Scheduled.Count > 0, "control: beams were scheduled");
            Assert.AreEqual(0, port.CeilingBeams.Count, "the burst is already past when the step runs: no beam over a live pillar");
        }

        [TestMethod]
        public void Director_CeilingBeam_ALateTickShortensTheBeamAndNoBeamOutlivesItsBurst()
        {
            // beam due g + 1.5, but the tick is at g + 1.65: it lasts the 0.85 s that is left, to the burst at g + 2.5
            const string args = "cycles=1 move=1 hold=1.5 burst=1 rest=0.5 n=3 ceiling_beam=1";
            var g = GlideStart(args);
            var (director, port, _) = Setup(args, "row", squad: 3);

            RunAt(director, port, Ticks(0.1, g + 1.4, 0.1).Concat(new[] { g + 1.65 }).Concat(Ticks(g + 1.75, g + 8.0, 0.1)));

            var burstAt = port.Moves.Single().At + 2.5;

            Assert.IsTrue(port.CeilingBeams.Count > 0);
            Assert.IsTrue(port.CeilingBeams.All(b => Math.Abs(b.At - (g + 1.65)) < 1e-6 && Math.Abs(b.Seconds - (burstAt - b.At)) < 1e-6 && b.Seconds < 0.9), "spawned late, lasting only what is left");
            Assert.IsTrue(port.CeilingBeams.All(b => b.At + b.Seconds <= burstAt + 1e-6), "no beam outlives its burst");
        }

        [TestMethod]
        public void Director_CeilingBeam_ADeadFollowerGetsNoBeamAndLosesOneThatIsUp()
        {
            var spot = new Vector3(20f, -30f, -51f);
            const string args = "follow=1 cycles=1 move=1 hold=2 burst=1 rest=0.5 n=3 follow_lag=2 ceiling_beam=1";

            // glide at g, hold from g + 1, beam from g + 2, burst at g + 3
            var g = GlideStart(args);

            // control: left alone, the follower is beamed
            var f0 = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (d0, p0, _) = Setup(args, WispRules.Scatter, squad: 3, follower: f0);
            p0.Target = _ => spot;

            Run(d0, p0, 0.1);

            Assert.AreEqual(1, p0.CeilingBeams.Count(b => SameXY(b.Point, spot)), "control: a living follower is beamed");

            // killed between the hold start and the beam start
            var f1 = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (d1, p1, _) = Setup(args, WispRules.Scatter, squad: 3, follower: f1);
            p1.Target = _ => spot;

            Run(d1, p1, 0.1, t => { if (t >= g + 1.5) f1.Dead = true; });

            Assert.AreEqual(0, p1.CeilingBeams.Count(b => SameXY(b.Point, spot)), "no beam at the beat point of a dead follower");

            // killed after the beam is up and before the burst
            var f2 = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (d2, p2, _) = Setup(args, WispRules.Scatter, squad: 3, follower: f2);
            p2.Target = _ => spot;

            Run(d2, p2, 0.1, t => { if (t >= g + 2.5) f2.Dead = true; });

            var index = p2.CeilingBeams.FindIndex(b => SameXY(b.Point, spot));

            Assert.IsTrue(index >= 0, "control: the beam was up before the follower died");
            Assert.IsTrue(p2.Destroyed.Any(d => ReferenceEquals(d.Handle, p2.BeamHandles[index])), "and was destroyed with its beat");
        }

        [TestMethod]
        public void Director_CeilingBeam_NoCeilingSchedulesNothingAndLogsOnce()
        {
            var (director, port, _) = Setup("cycles=4 move=1 hold=1.5 burst=1 rest=0.5 n=4 ceiling_beam=1", "row");
            port.HasCeiling = false;

            Run(director, port, 0.1);

            Assert.IsTrue(port.Hosts.Count > 0, "control: the run burst");
            AssertOnlyFirstStrikesScheduled(port);
            Assert.AreEqual(0, port.CeilingBeams.Count);
            Assert.AreEqual(1, port.Logs.Count(l => l.Contains("ceiling beam would not spawn")));
        }

        [TestMethod]
        public void Validate_CeilingBeam_RefusesOutOfRangeAndBadValues()
        {
            Func<string, string> validate = a => WispRules.Validate(Pinnacle(), HazardArgs.Merge(HazardArgs.Parse(a), HazardArgs.Parse(HazardDefaults.Wisps)), WispRules.Scatter);

            StringAssert.Contains(validate("ceiling_beam=-1"), "ceiling_beam");
            StringAssert.Contains(validate("ceiling_beam=6"), "ceiling_beam");
            StringAssert.Contains(validate("ceiling_beam=x"), "ceiling_beam");
            StringAssert.Contains(validate("beam_script=zz"), "beam_script");

            Assert.IsNull(validate("ceiling_beam=0"));
            Assert.IsNull(validate("ceiling_beam=5 beam_script=0x33000791"));
            Assert.IsNull(validate("ceiling_beam=1.5 beam_script=855639953"));

            var s = S("ceiling_beam=1.5 beam_script=0x33001040");

            Assert.AreEqual(1.5, s.CeilingBeam, 1e-9);
            Assert.AreEqual(0x33001040u, s.BeamScript);
            Assert.AreEqual(0.0, S().CeilingBeam, "off by default");
            Assert.AreEqual(HazardDefaults.BeamScript, S().BeamScript);
            Assert.AreEqual(1.5, S("ceiling_beam=1.5 move_end=1 cycles=3").ForCycle(2).CeilingBeam, 1e-9, "ForCycle keeps it");
            Assert.AreEqual(0x33001040u, S("beam_script=0x33001040 move_end=1 cycles=3").ForCycle(2).BeamScript, "ForCycle keeps it");
        }

        [TestMethod]
        public void Director_LogsTheMixedPoolOnce_AndOnlyForMixed()
        {
            var (director, port, _) = Setup("cycles=3 move=1 hold=1.5 burst=0.5 rest=0.5 n=4 dir=n", "mixed");
            Run(director, port, 0.1);

            var pools = port.Logs.Where(l => l.StartsWith("mixed pool:")).ToList();

            Assert.AreEqual(1, pools.Count);
            StringAssert.Contains(pools[0], "row");
            Assert.IsFalse(pools[0].Contains("column"), "dir=n leaves no column in the pool");

            var (d2, p2, _) = Setup("cycles=1 move=1 hold=1.5 burst=0.5 rest=0.5 n=4", "ring");
            Run(d2, p2, 0.1);
            Assert.AreEqual(0, p2.Logs.Count(l => l.StartsWith("mixed pool:")));
        }
    }
}

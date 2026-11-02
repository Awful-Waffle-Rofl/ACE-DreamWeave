using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The balls' latency compensation (args hit_lag, BallHits): every point of a ball's path at path time t is hit-tested
    /// no earlier than server time t + hit_lag, the last hit_lag seconds of a leg are swept in a tail AFTER the leg end, a
    /// ball that died early never hits again, the wave (classic and cadence) waits for the tails, and hit_lag=0 is the old
    /// routine exactly. Driven without a world by <see cref="Sim"/>, which calls BallHits in the driver's own order
    /// (HazardManager.TickRun: AdvanceMotion fires the leg end, RunDue runs a Schedule(0) destroy, then the mechanic's Tick
    /// drops dead handles and sweeps). Every test names the wrong implementation it catches. Pure of PropertyManager.
    /// </summary>
    [TestClass]
    public class BallHitLagTests
    {
        private const float BallRadius = 1.8f;
        private const float PlayerRadius = 0.5f;
        private const float MaxStep = (BallRadius + PlayerRadius) * 0.5f; // the mechanic's p.Reach * 0.5f
        private const double Tick = 0.1;
        private const double Lag = 0.15;

        // a 9 m/s ball along y = -20 from x = 0 to x = 18, launched at run time 1.0: EndTime 3.0
        private const double T0 = 1.0;
        private const float Speed = 9f;
        private const float EndX = 18f;

        private static HazardPath Leg() => new HazardPath(HazardContext.PlanLeg(HazardRegionPresets.Pinnacle(0), null,
            new Vector3(0f, -20f, -45.75f), new Vector3(EndX, -20f, -45.75f), T0, EndX / Speed, new HazardMoveOptions { Follow = HazardFollow.Ground, GroundOffset = 2f }));

        /// <summary>When the ball first comes within reach of a player at (x, -20): the contact time.</summary>
        private static double ContactAt(float playerX) => T0 + (playerX - (BallRadius + PlayerRadius)) / Speed;

        private static double Now(int k) => Math.Round(k * Tick, 9);

        /// <summary>One ball through the driver's tick order; the player stands still at (PlayerX, -20).</summary>
        private sealed class Sim
        {
            public const int Id = 1;

            public readonly BallWaveClock Clock;
            public readonly double HitLag;
            public HazardPath Path;
            public HazardPath Tail;
            public bool Alive = true;
            public double? DestroyedAt;
            public double? DiedAt;
            public double? HitAt;
            public int AttemptsAfterDeath;
            public float PlayerX;
            public readonly List<(double Now, Vector3 Point)> Tested = new List<(double, Vector3)>();
            private double now;

            public Sim(double hitLag, float playerX, BallWaveClock clock = null)
            {
                HitLag = hitLag;
                PlayerX = playerX;
                Path = Leg();

                if (clock == null)
                {
                    clock = new BallWaveClock(1);
                    clock.StartWave(1);
                }

                Clock = clock;
                Clock.OnSpawned(Id);
                Clock.OnLaunched(Id);
            }

            /// <summary>ctx.HitPlayers at one centre: refuses a dead handle, once per ball per player (HitWindowSeconds = +infinity).</summary>
            private int Hit(Vector3 pt)
            {
                Tested.Add((now, pt));

                if (!Alive)
                {
                    AttemptsAfterDeath++;
                    return 0;
                }

                if (HitAt.HasValue || !HazardHitRules.InReach(pt, new Vector3(PlayerX, -20f, 0f), BallRadius, PlayerRadius))
                    return 0;

                HitAt = now;
                return 1;
            }

            /// <summary>The object dies outside the run (a landblock reload) between two ticks.</summary>
            public void Kill(double at)
            {
                Alive = false;
                DiedAt = at;
            }

            public void Step(double t)
            {
                now = t;

                // AdvanceMotion: a dead handle is dropped without its OnLegEnd; a live finished leg calls it, then the end snap clears Path
                if (Alive && Path != null && t >= Path.EndTime)
                {
                    var destroy = BallHits.LegEnded(Clock, Id, Path, ref Tail, HitLag, MaxStep, Hit);
                    Path = null;

                    // ctx.Schedule(0.0, Destroy) from AdvanceMotion runs in the same tick's RunDue, before the mechanic's Tick
                    if (destroy)
                        Destroy(t);
                }

                // Tick step 1: dead handles first (clock.Unresolved: spawned or launched)
                if (!Alive && Clock.Unresolved().Contains(Id))
                    Clock.OnDied(Id);

                // Tick step 2: every hittable ball (rolling or in its tail)
                if (Clock.IsHittable(Id) && BallHits.Tick(Clock, Id, Alive, Path, Tail, t, HitLag, MaxStep, Hit))
                    Destroy(t);
            }

            private void Destroy(double t)
            {
                if (DestroyedAt == null && Alive)
                    DestroyedAt = t;

                // ctx.Destroy clears the handle's Path too (an outside death, Kill, does not)
                Alive = false;
                Path = null;
            }

            public void RunUntil(double end, Action<int> beforeTick = null)
            {
                for (var k = (int)Math.Round(T0 / Tick); Now(k) <= end + 1e-9; k++)
                {
                    beforeTick?.Invoke(k);
                    Step(Now(k));
                }
            }
        }

        // ---- on the path ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void OnThePath_APlayerIsNotHitWhenTheServerBallReachesThem_ButByContactPlusLagPlusOneTick()
        {
            // CATCHES: the pre-change sweep to now (hit at the first tick at/after contact), and any lag applied to the
            // player instead of the path. Contact at x = 9 - 2.3 = 6.7 m: t = 1.744; the old routine hits at 1.8.
            const float x = 9f;
            var contact = ContactAt(x);

            var sim = new Sim(Lag, x);
            sim.RunUntil(4.0);

            Assert.IsNotNull(sim.HitAt, "the player is hit");
            Assert.IsTrue(sim.HitAt.Value >= contact + Lag - 1e-9, $"hit at {sim.HitAt} s, before contact {contact:0.000} + lag {Lag}");
            Assert.IsTrue(sim.HitAt.Value <= contact + Lag + Tick + 1e-9, $"hit at {sim.HitAt} s, later than contact + lag + one tick");
            Assert.IsTrue(sim.HitAt.Value > Math.Ceiling(contact / Tick) * Tick + 1e-9, "not at the first tick at/after contact, where the server's ball is");

            // control: the same run without lag hits on exactly that first tick
            var old = new Sim(0.0, x);
            old.RunUntil(4.0);
            Assert.AreEqual(Math.Ceiling(contact / Tick) * Tick, old.HitAt.Value, 1e-9);
        }

        [TestMethod]
        public void OnThePath_TheLegsStartPoint_IsStillTested_WhenTheLagReachesBackBeforeTheLaunch()
        {
            // CATCHES: recording SweptTo = now - lag while that is still before the leg's start, which marks the start as
            // swept without testing it, so a player standing at the start line is never hit. Contact at t0 itself.
            var sim = new Sim(Lag, 1.0f);
            sim.RunUntil(4.0);

            Assert.IsNotNull(sim.HitAt, "a player at the start line is hit");
            Assert.AreEqual(T0 + 0.2, sim.HitAt.Value, 1e-9, "first tick with now - 0.15 >= t0 = 1.0 is 1.2");
            Assert.AreEqual(new Vector3(0f, -20f, 0f).X, sim.Tested.First().Point.X, 1e-4f, "the first point tested is the leg's start");

            // and nothing skipped: consecutive tested points never more than half a reach apart, start to end
            var xs = sim.Tested.Select(t => t.Point.X).ToList();
            for (var i = 1; i < xs.Count; i++)
                Assert.IsTrue(xs[i] - xs[i - 1] <= MaxStep + 1e-3f && xs[i] >= xs[i - 1] - 1e-4f, $"step {i}: {xs[i - 1]} -> {xs[i]}");
            Assert.AreEqual(EndX, xs.Last(), 1e-3f, "the sweep reaches the leg end");
        }

        // ---- at the end of a leg ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void AtTheLegEnd_APlayerAtTheFinalPoint_IsHitAtEndTimePlusLag_NotAtEndTime_AndTheBallLivesUntilThen()
        {
            // CATCHES: a final sweep to EndTime in the leg end (the old OnLegEnd, which hits at 3.0), and destroying the
            // ball at its leg end (which never hits at all: HitPlayers refuses a dead handle). Player 2.25 m past the end:
            // only the last 0.05 m of the leg reaches them (contact 2.994, EndTime 3.0).
            const float x = EndX + 2.25f;
            var end = Leg().EndTime;
            var contact = ContactAt(x);

            Assert.IsTrue(contact > end - Tick && contact < end, "control: the contact is in the leg's last tick");

            var sim = new Sim(Lag, x);
            var tailSeen = false;
            sim.RunUntil(4.0, k => { if (Now(k) > end + 1e-9 && Now(k) < end + Lag - 1e-9) tailSeen |= sim.Clock.InTail(Sim.Id) && sim.Alive; });

            Assert.IsNotNull(sim.HitAt, "the player at the end is hit");
            Assert.IsTrue(sim.HitAt.Value > end + 1e-9, $"hit at {sim.HitAt}: not at the leg end");
            Assert.IsTrue(sim.HitAt.Value >= contact + Lag - 1e-9, "no earlier than contact + lag");
            Assert.IsTrue(sim.HitAt.Value <= end + Lag + Tick + 1e-9, "by EndTime + lag + one tick");
            Assert.IsTrue(tailSeen, "between EndTime and EndTime + lag the ball is alive and in its tail");
            Assert.IsNotNull(sim.DestroyedAt);
            Assert.IsTrue(sim.DestroyedAt.Value >= end + Lag - 1e-9, $"destroyed at {sim.DestroyedAt}, before EndTime + lag");
            Assert.IsTrue(sim.DestroyedAt.Value >= sim.HitAt.Value, "never destroyed before its tail hit");
            Assert.AreEqual(BallState.Ended, sim.Clock.StateOf(Sim.Id));
            Assert.AreEqual(0, sim.Clock.Lost, "a tail is not a loss");

            // control: without lag the hit is on the leg end tick and the ball goes then
            var old = new Sim(0.0, x);
            old.RunUntil(4.0);
            Assert.AreEqual(end, old.HitAt.Value, 1e-9);
            Assert.AreEqual(end, old.DestroyedAt.Value, 1e-9);
        }

        // ---- a ball that dies early -----------------------------------------------------------------------------------------

        [TestMethod]
        public void ABallThatDiesBeforeItsLegEnds_NeverHitsAgain_EvenInsideItsLagWindow()
        {
            // CATCHES: treating a death like a leg end (a tail for a dead ball) and sweeping the dead ball's lagged window.
            // Contact 1.744; killed between the 1.7 and 1.8 ticks, the lagged sweep had reached only 1.55.
            const float x = 9f;

            var sim = new Sim(Lag, x);
            sim.RunUntil(4.0, k => { if (Math.Abs(Now(k) - 1.8) < 1e-9) sim.Kill(1.75); });

            Assert.IsNull(sim.HitAt, "never hit");
            Assert.AreEqual(0, sim.AttemptsAfterDeath, "not even an attempt the context would refuse");
            Assert.IsFalse(sim.Tested.Any(t => t.Now > 1.75), "nothing is swept after the death");
            Assert.AreEqual(BallState.Dead, sim.Clock.StateOf(Sim.Id));
            Assert.AreEqual(1, sim.Clock.Lost);
            Assert.IsTrue(sim.Clock.WaveOver, "the dead ball resolves the wave at once: no tail to wait for");

            // control: alive, the same player is hit in that lag window
            var alive = new Sim(Lag, x);
            alive.RunUntil(4.0);
            Assert.AreEqual(1.9, alive.HitAt.Value, 1e-9);
        }

        [TestMethod]
        public void ABallWhoseObjectDiesInItsTail_FinishesWithoutAHit_AndIsNotLost()
        {
            const float x = EndX + 2.25f;

            var sim = new Sim(Lag, x);
            sim.RunUntil(4.0, k => { if (Math.Abs(Now(k) - 3.1) < 1e-9) sim.Kill(3.05); });

            Assert.IsNull(sim.HitAt);
            Assert.AreEqual(0, sim.AttemptsAfterDeath);
            Assert.AreEqual(BallState.Ended, sim.Clock.StateOf(Sim.Id));
            Assert.AreEqual(0, sim.Clock.Lost, "its leg had ended");
            Assert.IsTrue(sim.Clock.WaveOver);
        }

        // ---- the wave end (classic) -------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheWave_DoesNotEnd_AndTheNextClassicWaveDoesNotStart_UntilTheTailIsDone()
        {
            // CATCHES: counting a ball done at its leg end (OnLegEnd straight to Ended), which closes the wave (and starts
            // the next one after wave_gap) while its tail is unswept, and a Remaining that drops at the leg end.
            var clock = new BallWaveClock(2);
            clock.StartWave(1);
            var sim = new Sim(Lag, 30f, clock);
            var end = Leg().EndTime;
            var steps = new List<(double Now, BallWaveStep Step, int Remaining, int Visible, bool TailOnly)>();

            for (var k = 10; k <= 40; k++)
            {
                sim.Step(Now(k));
                steps.Add((Now(k), clock.Close().Step, clock.Remaining, clock.VisibleRemaining, clock.TailOnly));
            }

            var atEnd = steps.Single(s => Math.Abs(s.Now - end) < 1e-9);
            Assert.AreEqual(BallWaveStep.Wait, atEnd.Step, "the leg ended, the tail did not");
            Assert.AreEqual(1, atEnd.Remaining, "the tail ball is still remaining");
            Assert.AreEqual(0, atEnd.Visible, "but no longer visible");
            Assert.IsTrue(atEnd.TailOnly, "over on screen, still swept");
            Assert.IsTrue(steps.Where(s => s.Now < end - 1e-9).All(s => s.Visible == 1 && !s.TailOnly), "control: rolling, it is visible");

            var first = steps.First(s => s.Step != BallWaveStep.Wait);
            Assert.AreEqual(BallWaveStep.StartNextWave, first.Step);
            Assert.IsTrue(first.Now >= end + Lag - 1e-9, $"the wave ended at {first.Now}, before EndTime + lag");
            Assert.AreEqual(sim.DestroyedAt.Value, first.Now, 1e-9, "on the tick the tail finished");

            // control: hit_lag 0 ends the wave on the leg end tick, as before
            var oldClock = new BallWaveClock(2);
            oldClock.StartWave(1);
            var old = new Sim(0.0, 30f, oldClock);
            for (var k = 10; k <= 30; k++)
                old.Step(Now(k));
            Assert.AreEqual(BallWaveStep.StartNextWave, oldClock.Close().Step);
            Assert.IsFalse(oldClock.TailOnly);
        }

        [TestMethod]
        public void TheFallback_GrowsByTheLag_AndForceResolveEndsATailWithoutALoss()
        {
            // CATCHES: a fallback deadline that ignores the lag (it could write off a healthy ball in its tail), and a
            // ForceResolve that leaves a tail unresolved (the wave would never end) or counts it lost.
            var wave = new BallWave();
            wave.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(18, 0), 0.0, "a"));

            Assert.AreEqual(BallRules.FallbackSeconds(wave, 9f, 2.0, 0.1) + 0.15 + 0.1, BallRules.FallbackSeconds(wave, 9f, 2.0, 0.1, 0.15), 1e-9, "the lag plus one tick: the tail ends on the first tick at/after leg end + lag");
            Assert.AreEqual(BallRules.FallbackSeconds(wave, 9f, 2.0, 1.0) + 0.15 + 1.0, BallRules.FallbackSeconds(wave, 9f, 2.0, 1.0, 0.15), 1e-9, "a slow tick widens it by that tick");
            Assert.AreEqual(BallRules.FallbackSeconds(wave, 9f, 2.0, 0.1), BallRules.FallbackSeconds(wave, 9f, 2.0, 0.1, 0.0), 1e-9, "hit_lag 0: the old deadline");

            var clock = new BallWaveClock(1);
            clock.StartWave(2);
            clock.OnSpawned(1);
            clock.OnSpawned(2);
            clock.OnLaunched(1);
            clock.OnLaunched(2);
            clock.OnLegEnd(1, tail: true);

            Assert.AreEqual(1, clock.ForceResolve(), "only the rolling ball is written off");
            Assert.AreEqual(BallState.Ended, clock.StateOf(1));
            Assert.AreEqual(1, clock.Lost);
            Assert.IsTrue(clock.WaveOver);
        }

        // ---- the cadence path -----------------------------------------------------------------------------------------------------

        private static BallParams P(string args, string pattern)
            => BallRules.Read(HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), pattern);

        [TestMethod]
        public void Cadence_AWaveInItsTail_FreesItsMaxInFlightSlot_StaysOpenAndSwept_AndTheRunWaitsForIt()
        {
            // CATCHES: the open-wave count as the cap (wave 2 would be held through wave 1's tail), closing a wave at its
            // visible end (its tail would never be swept), and a run Completed before the last tail is done.
            var cadence = new BallCadence(P("waves=2 wave_every=1 max_in_flight=1 hit_lag=0.15", BallPatterns.Row), BallPatterns.Row);

            CollectionAssert.AreEqual(new[] { 1 }, cadence.Due(0.0).ToArray());
            var sim = new Sim(Lag, EndX + 2.25f, cadence.Open(1, 1));
            var end = Leg().EndTime;
            var launchedAt = -1.0;

            for (var k = 10; k <= 40; k++)
            {
                var t = Now(k);
                sim.Step(t);

                if (launchedAt < 0 && cadence.Due(t).Contains(2))
                {
                    launchedAt = t;

                    Assert.AreEqual(0, cadence.InFlight, "wave 1 no longer counts once it is in its tail");
                    Assert.IsTrue(cadence.OpenWaves.Contains(1), "but it is still open");
                    Assert.IsTrue(sim.Clock.InTail(Sim.Id), "with its tail still to sweep");
                    Assert.IsNull(sim.HitAt, "and its tail hit still to come");
                    cadence.Open(2, 0);
                }
                else if (launchedAt < 0)
                {
                    Assert.IsTrue(t < end - 1e-9, $"wave 2 held at {t}, after wave 1's visible end");
                }

                if (cadence.Close(1) != null)
                    Assert.IsTrue(t >= end + Lag - 1e-9, $"wave 1 closed at {t}, before EndTime + lag");
            }

            Assert.AreEqual(end, launchedAt, 1e-9, "wave 2 launches in the tick wave 1's balls end their legs");
            Assert.IsNotNull(sim.HitAt, "wave 1's tail was swept while wave 2 rolled");
            Assert.IsTrue(sim.HitAt.Value > end + 1e-9);

            // control: hit_lag 0 keeps the old order: the finished wave holds its slot until it is closed
            var oldCadence = new BallCadence(P("waves=2 wave_every=1 max_in_flight=1 hit_lag=0", BallPatterns.Row), BallPatterns.Row);
            oldCadence.Due(0.0);
            var old = new Sim(0.0, EndX + 2.25f, oldCadence.Open(1, 1));

            for (var k = 10; k <= 30; k++)
                old.Step(Now(k));

            Assert.AreEqual(0, oldCadence.Due(end).Count, "wave 1 is over but not closed: still counted in flight");
            Assert.IsNotNull(oldCadence.Close(1));
            CollectionAssert.AreEqual(new[] { 2 }, oldCadence.Due(end).ToArray(), "and launches once it is closed");
        }

        [TestMethod]
        public void Cadence_TheLastWavesTail_HoldsTheRunOpen_AndTheBallBudgetIgnoresTails()
        {
            var cadence = new BallCadence(P("waves=1 wave_every=1 hit_lag=0.15", BallPatterns.Row), BallPatterns.Row);
            cadence.Due(0.0);
            var sim = new Sim(Lag, 30f, cadence.Open(1, 1));
            var end = Leg().EndTime;
            double? completedAt = null;

            for (var k = 10; k <= 40 && completedAt == null; k++)
            {
                sim.Step(Now(k));

                if (Math.Abs(Now(k) - end) < 1e-9)
                {
                    Assert.AreEqual(0, cadence.BallsInFlight, "the budget counts what is still rolling");
                    Assert.AreEqual(1, sim.Clock.Remaining, "the clock still owes the tail");
                }

                if (cadence.Close(1) is BallWaveResult r && cadence.Verdict(1, r)?.Outcome == HazardOutcome.Completed)
                    completedAt = Now(k);
            }

            Assert.IsNotNull(completedAt);
            Assert.IsTrue(completedAt.Value >= end + Lag - 1e-9, $"Completed at {completedAt}, before the tail was done");
        }

        [TestMethod]
        public void Cadence_DistinctDirs_AWaveInItsTail_FreesItsWall()
        {
            var cadence = new BallCadence(P("waves=2 wave_every=1 max_in_flight=2 distinct_dirs=1 dir=random hit_lag=0.15", BallPatterns.Row), BallPatterns.Row);
            cadence.Due(0.0);
            var sim = new Sim(Lag, 30f, cadence.Open(1, 1, new[] { BallSide.North }));

            CollectionAssert.AreEqual(new[] { BallSide.North }, cadence.BusyWalls.ToArray(), "rolling: its wall is busy");

            for (var k = 10; k <= 30; k++)
                sim.Step(Now(k));

            Assert.IsTrue(sim.Clock.TailOnly);
            Assert.AreEqual(0, cadence.BusyWalls.Count, "in its tail: its wall is free");
        }

        // ---- no lag -------------------------------------------------------------------------------------------------------------

        /// <summary>The pre-change routine, verbatim from RollingBallMechanic.Sweep at 6242ab18c: sweep to now each tick, to EndTime at the leg end.</summary>
        private static List<(double Now, Vector3 Point)> OldRoutine(IReadOnlyList<double> ticks)
        {
            var path = Leg();
            var clock = new BallWaveClock(1);
            clock.StartWave(1);
            clock.OnSpawned(1);
            clock.OnLaunched(1);
            var tested = new List<(double, Vector3)>();
            HazardPath live = path;

            void Sweep(HazardPath p, double t1, double now)
            {
                var swept = clock.SweptTo(1);
                foreach (var pt in BallSweep.SweepPoints(p, swept ?? p.T0, t1, MaxStep, includeStart: swept == null))
                    tested.Add((now, pt));
                clock.SetSweptTo(1, Math.Min(t1, p.EndTime));
            }

            foreach (var now in ticks)
            {
                if (live != null && now >= live.EndTime)
                {
                    Sweep(live, live.EndTime, now);
                    clock.OnLegEnd(1);
                    live = null;
                }

                if (clock.IsHittable(1) && live != null)
                    Sweep(live, now, now);
            }

            return tested;
        }

        [TestMethod]
        public void NoLag_IsThePreChangeRoutine_PointForPoint_OnAnUnevenTick()
        {
            // CATCHES: any change to the hit_lag 0 path (a different sweep end, a tail, a skipped start point)
            var ticks = new List<double>();
            for (var t = T0; t < 4.0; t += 0.07 + 0.05 * Math.Sin(t * 7.0) * Math.Sin(t * 7.0))
                ticks.Add(Math.Round(t, 9));

            var sim = new Sim(0.0, 30f);
            foreach (var t in ticks)
                sim.Step(t);

            var old = OldRoutine(ticks);

            Assert.IsTrue(old.Count > 10, "control: the old routine tested points");
            Assert.AreEqual(old.Count, sim.Tested.Count, "the same number of points");

            for (var i = 0; i < old.Count; i++)
            {
                Assert.AreEqual(old[i].Now, sim.Tested[i].Now, 1e-12, $"point {i} tested at another tick");
                Assert.AreEqual(old[i].Point, sim.Tested[i].Point, $"point {i}");
            }

            Assert.AreEqual(BallState.Ended, sim.Clock.StateOf(Sim.Id));
            Assert.AreEqual(ticks.First(t => t >= Leg().EndTime), sim.DestroyedAt.Value, 1e-12, "destroyed on the leg end tick, as before");

            // and the same run with lag is NOT the old routine (the comparison can fail)
            var lagged = new Sim(Lag, 30f);
            foreach (var t in ticks)
                lagged.Step(t);
            Assert.IsFalse(lagged.Tested.Select(p => p.Now).SequenceEqual(old.Select(p => p.Now)));
        }

        // ---- settings -------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void HitLag_DefaultsTo015InCode_IsClamped_AndANonNumericValueIsRefused()
        {
            Assert.AreEqual(0.15, P("", BallPatterns.Row).HitLag, 1e-12);
            Assert.AreEqual(0.15, new BallParams().HitLag, 1e-12);
            Assert.IsFalse(HazardArgs.Parse(HazardDefaults.Balls).Has("hit_lag"), "the default lives in code, not in the settings string");

            Assert.AreEqual(0.3, P("hit_lag=0.3", BallPatterns.Row).HitLag, 1e-12);
            Assert.AreEqual(0.0, P("hit_lag=0", BallPatterns.Row).HitLag, 1e-12);
            Assert.AreEqual(0.0, P("hit_lag=-2", BallPatterns.Row).HitLag, 1e-12, "clamped up to 0");
            Assert.AreEqual(0.5, P("hit_lag=9", BallPatterns.Row).HitLag, 1e-12, "clamped down to 0.5");

            // a shard row (laid under the caller's args) sets it; the caller's own arg wins over the row
            Assert.AreEqual(0.25, BallRules.Read(HazardArgs.Merge(HazardArgs.Empty, HazardArgs.Parse("hit_lag=0.25 " + HazardDefaults.Balls)), BallPatterns.Row).HitLag, 1e-12);
            Assert.AreEqual(0.1, BallRules.Read(HazardArgs.Merge(HazardArgs.Parse("hit_lag=0.1"), HazardArgs.Parse("hit_lag=0.25 " + HazardDefaults.Balls)), BallPatterns.Row).HitLag, 1e-12);

            var refused = BallRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Merge(HazardArgs.Parse("hit_lag=soon"), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Row);
            Assert.IsNotNull(refused);
            StringAssert.Contains(refused, "hit_lag");
            StringAssert.Contains(refused, "is not a");
            Assert.IsNull(BallRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Merge(HazardArgs.Parse("hit_lag=0.2"), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Row), "control: a number is accepted");

            Assert.AreEqual(0.3, BallRules.ForWave(P("hit_lag=0.3 waves=3", BallPatterns.Row), 3).HitLag, 1e-12, "every wave keeps it");
        }
    }
}

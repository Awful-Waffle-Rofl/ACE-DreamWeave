using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The memory mechanic's Start/Tick/OnStop decisions, driven through the <see cref="IMemoryWorld"/> seam with a
    /// recording fake: no landblock, no PropertyManager, no Player.
    /// </summary>
    [TestClass]
    public class MemoryMechanicSeamTests
    {
        private sealed class FakePiece : IMemoryPiece
        {
            public bool Alive { get; set; } = true;
        }

        private sealed class FakePillar : IMemoryPiece
        {
            public bool Alive { get; set; } = true;
            public Vector3 Point { get; set; }
            public double Seconds { get; set; }
            public double SpawnedAt { get; set; }
            public string Tag { get; set; }
        }

        private sealed class FakeWorld : IMemoryWorld
        {
            public double Now { get; set; }
            public Random Rng { get; set; } = new Random(1);
            public HazardRegion Region { get; set; }
            public HazardArgs Args { get; set; }
            public float EdgeInset { get; set; } = 2f;

            public List<(uint Wcid, Vector3 Position, string Tag, FakePiece Piece)> Spawns { get; } = new List<(uint, Vector3, string, FakePiece)>();
            public List<(Vector3 Point, uint Script, double Seconds, string Tag)> Beams { get; } = new List<(Vector3, uint, double, string)>();
            public List<Quaternion> BeamOrientations { get; } = new List<Quaternion>();
            public List<(Vector3 Point, bool Raise)> CellAsks { get; } = new List<(Vector3, bool)>();
            public List<bool> BeamRaises { get; } = new List<bool>();
            public List<FakePillar> Pillars { get; } = new List<FakePillar>();
            public List<(FakePillar Pillar, Vector3 Centre, float Radius, int Hits, double Now)> Strikes { get; } = new List<(FakePillar, Vector3, float, int, double)>();
            public bool RefusePillars { get; set; }
            public List<string> Said { get; } = new List<string>();
            public List<string> Logged { get; } = new List<string>();
            public List<(HazardOutcome Outcome, string Reason)> Ended { get; } = new List<(HazardOutcome, string)>();
            public List<(uint, Vector2)> PlayerList { get; } = new List<(uint, Vector2)>();

            /// <summary>Refuse a marker spawn: (wcid, attempt number of that wcid, position).</summary>
            public Func<uint, int, Vector3, bool> RefuseSpawn { get; set; } = (_, _, _) => false;

            public Func<Vector3, bool> CellRule { get; set; } = _ => true;

            public bool RefuseBeams { get; set; }

            /// <summary>Refuse only the beams whose tag this returns true for (null: none).</summary>
            public Func<string, bool> RefuseBeamTag { get; set; }

            private readonly Dictionary<uint, int> attempts = new Dictionary<uint, int>();

            public Vector3 GroundPoint(float x, float y) => new Vector3(x, y, 5f);

            public bool HasCell(Vector3 point, bool raise)
            {
                CellAsks.Add((point, raise));

                // like the live HasCell: a raise walks the point up in 0.25 m steps, up to 1.5 m
                if (CellRule(point))
                    return true;

                if (raise)
                    for (var k = 1; k <= 6; k++)
                        if (CellRule(point + new Vector3(0, 0, 0.25f * k)))
                            return true;

                return false;
            }

            public IMemoryPiece SpawnMarker(uint wcid, Vector3 position, string tag)
            {
                attempts.TryGetValue(wcid, out var n);
                attempts[wcid] = n + 1;

                if (RefuseSpawn(wcid, n, position))
                    return null;

                var piece = new FakePiece();
                Spawns.Add((wcid, position, tag, piece));
                return piece;
            }

            public bool SpawnBeam(Vector3 point, uint script, double seconds, string tag, Quaternion orientation, bool raise)
            {
                if (RefuseBeams || (RefuseBeamTag != null && RefuseBeamTag(tag)))
                    return false;

                Beams.Add((point, script, seconds, tag));
                BeamOrientations.Add(orientation);
                BeamRaises.Add(raise);
                return true;
            }

            public IMemoryPiece SpawnPillar(Vector3 point, double seconds, string tag)
            {
                if (RefusePillars)
                    return null;

                var pillar = new FakePillar { Point = point, Seconds = seconds, Tag = tag, SpawnedAt = Now };
                Pillars.Add(pillar);
                return pillar;
            }

            public int Strike(IMemoryPiece pillar, Vector3 centre, float radius)
            {
                // like HitPlayers: the 2D distance from the centre within the radius
                var hits = PlayerList.Count(p => Vector2.Distance(p.Item2, new Vector2(centre.X, centre.Y)) <= radius);
                Strikes.Add(((FakePillar)pillar, centre, radius, hits, Now));
                return hits;
            }

            public IReadOnlyList<(uint Id, Vector2 Position)> Players() => PlayerList.ToList();

            public string NameOf(uint id) => "P" + id;

            public void Say(string line) => Said.Add(line);

            public void Log(string line) => Logged.Add(line);

            public void End(HazardOutcome outcome, string reason) => Ended.Add((outcome, reason));
        }

        private const string Defaults = "len=2 max_len=3 beams_per_pulse=3 pulse_on=1 pulse_off=0.5 answer_timeout=5";

        // 60 x 40 so that x and y are different numbers: a swapped pair is visibly wrong
        private static HazardRegion Region() => HazardRegion.Rect("seam", 0x0069, 0, false, 0, 60, 0, 40, HazardGroundMode.Plane, 5, null, 0, 11f, 2, 6);

        private static readonly Vector2 Away = new Vector2(30, 20);

        private static (MemoryMechanic Mechanic, FakeWorld World) Boot(string extra = "", int seed = 1, HazardRegion region = null, Action<FakeWorld> configure = null)
        {
            var world = new FakeWorld
            {
                Region = region ?? Region(),
                Rng = new Random(seed),
                Args = HazardArgs.Merge(HazardArgs.Parse(extra), HazardArgs.Merge(HazardArgs.Parse(Defaults), HazardArgs.Parse(HazardDefaults.Memory))),
            };

            configure?.Invoke(world);

            var mechanic = new MemoryMechanic();
            mechanic.StartCore(world);
            return (mechanic, world);
        }

        private static IReadOnlyList<Vector2> Pads(FakeWorld w) => MemoryLayout.QuadrantCentres(w.Region);

        private static int[] Secret(MemoryMechanic m)
            => m.Reveal(null).Split(' ').Select(n => MemoryRules.ColourNames.ToList().IndexOf(n)).ToArray();

        private static void Tick(MemoryMechanic m, FakeWorld w, double now)
        {
            w.Now = now;
            m.TickCore(w);
        }

        private static double ToAnswer(MemoryMechanic m, FakeWorld w)
        {
            var now = w.Now;
            var before = w.Said.Count(s => s == "Your turn.");

            w.PlayerList.Clear();
            w.PlayerList.Add((1, Away));

            for (var guard = 0; w.Said.Count(s => s == "Your turn.") == before; guard++)
            {
                Assert.IsTrue(guard < 5000, "playback never ended");
                now += 0.1;
                Tick(m, w, now);
            }

            return now;
        }

        /// <summary>Player <paramref name="id"/> walks onto the pad and off again (two ticks).</summary>
        private static double Press(MemoryMechanic m, FakeWorld w, double now, int pad, uint id = 1)
        {
            w.PlayerList.Clear();
            w.PlayerList.Add((id, Pads(w)[pad]));
            now += 0.1;
            Tick(m, w, now);

            w.PlayerList.Clear();
            w.PlayerList.Add((id, Away));
            now += 0.1;
            Tick(m, w, now);
            return now;
        }

        // ---- Start: pads and markers -----------------------------------------------------------------------------

        [TestMethod]
        public void Start_PlacesARingOfMarkersOnEachPad_AtTheGroundPoint_WithXAndYNotSwapped()
        {
            var (m, w) = Boot();
            var pads = Pads(w);

            Assert.AreEqual(0, w.Ended.Count);
            Assert.AreEqual(4 * MemoryLayout.RingCount(1f, 0.6f), w.Spawns.Count);

            for (var pad = 0; pad < 4; pad++)
            {
                var mine = w.Spawns.Where(s => s.Wcid == MemoryRules.PadWcids[pad]).ToList();

                Assert.AreEqual(11, mine.Count, $"pad {pad}");
                Assert.IsTrue(mine.All(s => Math.Abs(Vector2.Distance(new Vector2(s.Position.X, s.Position.Y), pads[pad]) - 1f) < 1e-3f),
                    $"pad {pad}: every marker on the rim round ({pads[pad].X}, {pads[pad].Y}); a swapped x/y would be metres away");
                Assert.IsTrue(mine.All(s => s.Position.Z == 5f), "at the ground point, offset 0");
                Assert.IsTrue(mine.All(s => s.Tag == "pad-" + MemoryRules.ColourName(pad)));
            }

            Assert.AreEqual(new Vector2(15, 10), pads[0]);
        }

        [TestMethod]
        public void APlayerOnTheXyPad_IsOnThatPad_AndOnTheSwappedPointIsNotOnAnyPad()
        {
            var (m, w) = Boot("len=1 max_len=1");
            var now = ToAnswer(m, w);
            var first = Secret(m)[0];

            // the pad at (x, y) = (15, 10) is red (0). The point (10, 15) is nowhere near a pad.
            w.PlayerList.Clear();
            w.PlayerList.Add((1, new Vector2(10, 15)));
            Tick(m, w, now + 0.1);
            Assert.AreEqual(0, w.Ended.Count, "(10, 15) is not a pad");

            w.PlayerList.Clear();
            w.PlayerList.Add((1, Pads(w)[first]));
            Tick(m, w, now + 0.2);
            Assert.AreEqual(HazardOutcome.Completed, w.Ended.Single().Outcome);
        }

        [TestMethod]
        public void Start_AskedTheWorldForCellsWithRaise_AndRefusesPadsOverNoCell()
        {
            var quadrants = MemoryLayout.QuadrantCentres(Region());
            var (m, w) = Boot(configure: world => world.CellRule = p => Vector2.Distance(new Vector2(p.X, p.Y), quadrants[0]) > 3f);

            Assert.IsTrue(w.CellAsks.Count > 0, "the pad acceptance asked the world");
            Assert.IsTrue(w.CellAsks.All(a => a.Raise), "stationary objects are raised (HasCell(point, raise: true))");
            Assert.IsTrue(w.CellAsks.All(a => a.Point.Z == 5f), "asked about the GROUND point of each candidate");

            Assert.AreEqual(HazardOutcome.Aborted, w.Ended.Single().Outcome);
            StringAssert.Contains(w.Ended.Single().Reason, "no room for four pads");
            Assert.AreEqual(0, w.Spawns.Count, "nothing is spawned for a refused layout");

            Tick(m, w, 5); // a refused run ticks as a no-op
            Assert.AreEqual(1, w.Ended.Count);
        }

        [TestMethod]
        public void Start_AbortsWhenAPadKeepsTooFewMarkers_AndTheBoundaryIsMaxOf3AndHalfTheRing()
        {
            // 11 markers a ring: at least 6 must be placed
            var pad2 = MemoryRules.PadWcids[2];

            var (_, ok) = Boot(configure: w => w.RefuseSpawn = (wcid, n, _) => wcid == pad2 && n < 5);
            Assert.AreEqual(0, ok.Ended.Count, "6 of 11 placed is enough");

            var (_, bad) = Boot(configure: w => w.RefuseSpawn = (wcid, n, _) => wcid == pad2 && n < 6);

            Assert.AreEqual(HazardOutcome.Aborted, bad.Ended.Single().Outcome);
            StringAssert.Contains(bad.Ended.Single().Reason, "pad 3");
            StringAssert.Contains(bad.Ended.Single().Reason, "green");
            StringAssert.Contains(bad.Ended.Single().Reason, "5 of 11");
        }

        [TestMethod]
        public void Start_AbortsWhenNoMarkerAtAllCanBePlaced()
        {
            var (_, w) = Boot(configure: world => world.RefuseSpawn = (_, _, _) => true);

            Assert.AreEqual(HazardOutcome.Aborted, w.Ended.Single().Outcome);
        }

        // ---- marker repair ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Repair_RespawnsAPlacedMarkerThatDied_OnceAndNeverTwice()
        {
            var (m, w) = Boot("len=1 max_len=1");
            var before = w.Spawns.Count;

            w.Spawns[0].Piece.Alive = false;
            w.Spawns[5].Piece.Alive = false;

            Tick(m, w, 0.5);
            Assert.AreEqual(before, w.Spawns.Count, "repair runs at most once a second");

            Tick(m, w, 1.1);
            Assert.AreEqual(before + 2, w.Spawns.Count, "the two dead markers came back");
            Assert.AreEqual(w.Spawns[0].Wcid, w.Spawns[before].Wcid);
            Assert.AreEqual(w.Spawns[0].Position, w.Spawns[before].Position, "at the same spot");

            Tick(m, w, 2.2);
            Tick(m, w, 3.3);
            Assert.AreEqual(before + 2, w.Spawns.Count, "the respawned ones are alive: no duplicate respawn");
        }

        [TestMethod]
        public void Repair_NeverRetriesAMarkerThatWasNeverPlaced()
        {
            Vector3? refused = null;
            var refusals = 0;

            var (m, w) = Boot("len=1 max_len=1", configure: world => world.RefuseSpawn = (wcid, n, pos) =>
            {
                if (wcid == MemoryRules.PadWcids[0] && n == 0)
                {
                    refused = pos;
                    return true;
                }

                if (refused.HasValue && pos == refused.Value)
                    refusals++;

                return false;
            });

            Assert.IsNotNull(refused);
            Assert.AreEqual(0, w.Ended.Count, "10 of 11 is plenty");

            for (var t = 1.1; t < 10; t += 1.1)
                Tick(m, w, t);

            Assert.AreEqual(0, refusals, "the never-placed marker was not retried");
            Assert.AreEqual(43, w.Spawns.Count);
        }

        [TestMethod]
        public void Repair_AMarkerWhoseRespawnIsRefused_IsNotRetriedForever()
        {
            var (m, w) = Boot("len=1 max_len=1");
            var target = w.Spawns[0];
            var attempts = 0;

            w.RefuseSpawn = (wcid, _, pos) =>
            {
                if (wcid == target.Wcid && pos == target.Position)
                {
                    attempts++;
                    return true;
                }

                return false;
            };

            target.Piece.Alive = false;

            for (var t = 1.1; t < 12; t += 1.1)
                Tick(m, w, t);

            Assert.AreEqual(1, attempts, "one respawn attempt, then it is treated as never placed");
        }

        [TestMethod]
        public void Repair_AWholePadLost_AndNotRespawnable_EndsAbortedWithOneLine()
        {
            var (m, w) = Boot("len=1 max_len=1");
            var pad = MemoryRules.PadWcids[0];

            w.RefuseSpawn = (wcid, _, _) => wcid == pad;

            foreach (var s in w.Spawns.Where(s => s.Wcid == pad))
                s.Piece.Alive = false;

            Tick(m, w, 1.1);

            var ended = w.Ended.Single();
            Assert.AreEqual(HazardOutcome.Aborted, ended.Outcome);
            StringAssert.Contains(ended.Reason, "lost");
            Assert.IsFalse(ended.Reason.Contains('\n'));
        }

        [TestMethod]
        public void Repair_AWholePadLost_ButRespawnable_CarriesOn()
        {
            var (m, w) = Boot("len=1 max_len=1");
            var pad = MemoryRules.PadWcids[0];

            foreach (var s in w.Spawns.Where(s => s.Wcid == pad))
                s.Piece.Alive = false;

            Tick(m, w, 1.1);

            Assert.AreEqual(0, w.Ended.Count, "a /reload-landblock wipe that the repair undoes is not a failure");
        }

        // ---- outcomes ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void ACorrectFullRun_EndsCompleted_WithTheRoundLinesInOrder()
        {
            var (m, w) = Boot("len=1 max_len=2");
            var now = ToAnswer(m, w);

            now = Press(m, w, now, Secret(m)[0]);
            Assert.AreEqual(0, w.Ended.Count);
            CollectionAssert.Contains(w.Said, "Round 1 complete.");
            CollectionAssert.Contains(w.Said, "Round 2: 2 colours");

            now = ToAnswer(m, w);
            var seq = Secret(m);
            now = Press(m, w, now, seq[0]);
            Assert.AreEqual(0, w.Ended.Count);
            Press(m, w, now, seq[1]);

            Assert.AreEqual(HazardOutcome.Completed, w.Ended.Single().Outcome);
            StringAssert.Contains(w.Ended.Single().Reason, "complete");
            Assert.IsTrue(w.Said.Any(s => s.Contains("The sequence is complete")));
            Assert.IsTrue(w.Said.Any(s => s.Contains("P1 stepped on") && s.Contains("step 2 of 2")));
        }

        [TestMethod]
        public void AWrongPad_EndsFailed_NamingWhoAndWhatWasWanted_ButTheChatLineDoesNotSpoilIt()
        {
            var (m, w) = Boot();
            var now = ToAnswer(m, w);
            var wanted = Secret(m)[0];
            var wrong = (wanted + 1) % 4;

            Press(m, w, now, wrong, 7);

            var ended = w.Ended.Single();

            Assert.AreEqual(HazardOutcome.Failed, ended.Outcome);
            StringAssert.Contains(ended.Reason, "P7");
            StringAssert.Contains(ended.Reason, MemoryRules.ColourName(wrong));
            StringAssert.Contains(ended.Reason, "wanted " + MemoryRules.ColourName(wanted));
            StringAssert.Contains(ended.Reason, "step 1 of 2");

            var line = w.Said.Last();
            StringAssert.Contains(line, "wrong colour");
            Assert.IsFalse(line.Contains("wanted"), "the room is not told the answer");
        }

        [TestMethod]
        public void NoEntryInTime_EndsFailed_WithTheTimeoutReason()
        {
            var (m, w) = Boot("answer_timeout=5");
            var now = ToAnswer(m, w);

            Tick(m, w, now + 4.9);
            Assert.AreEqual(0, w.Ended.Count);

            Tick(m, w, now + 5.1);

            Assert.AreEqual(HazardOutcome.Failed, w.Ended.Single().Outcome);
            StringAssert.Contains(w.Ended.Single().Reason, "no correct entry within 5 s");
            StringAssert.Contains(w.Ended.Single().Reason, "step 1 of 2");
        }

        [TestMethod]
        public void StopAndDescribe_ShowTheEndedOutcome()
        {
            var (m, w) = Boot();

            StringAssert.Contains(m.Describe(null), "playback round 1");

            m.StopCore(w, HazardOutcome.Failed);
            Assert.AreEqual("ended (Failed)", m.Describe(null));

            m.StopCore(w, HazardOutcome.Stopped);
            Assert.AreEqual("ended (Stopped)", m.Describe(null));
            Assert.IsTrue(w.Said.Contains("The memory game has ended."));
        }

        [TestMethod]
        public void Reveal_ShowsTheCurrentRoundsFreshSequence_NotTheOldOneGrown()
        {
            var (m, w) = Boot("len=1 max_len=3");
            var first = m.Reveal(null);
            var now = ToAnswer(m, w);

            Assert.AreEqual(1, first.Split(' ').Length);

            now = Press(m, w, now, Secret(m)[0]);
            var second = m.Reveal(null);

            Assert.AreEqual(2, second.Split(' ').Length);

            now = ToAnswer(m, w);
            var s2 = Secret(m);
            now = Press(m, w, now, s2[0]);
            Press(m, w, now, s2[1]);

            var third = m.Reveal(null);
            Assert.AreEqual(3, third.Split(' ').Length);
        }

        [TestMethod]
        public void Reveal_SecondRoundIsNotThePrefixExtensionOfTheFirst_ForSomeSeed()
        {
            var differing = 0;

            for (var seed = 1; seed <= 30; seed++)
            {
                var (m, w) = Boot("len=2 max_len=4", seed);
                var first = m.Reveal(null);
                var now = ToAnswer(m, w);
                var s1 = Secret(m);

                now = Press(m, w, now, s1[0]);
                Press(m, w, now, s1[1]);

                if (!m.Reveal(null).StartsWith(first))
                    differing++;
            }

            Assert.IsTrue(differing >= 15, $"{differing} of 30 seeds dealt a round 2 that does not begin with round 1");
        }

        // ---- playback ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Playback_FiresBeamsInTheSequenceColours_NeverOnAPad_AndInsideTheEdgeInset()
        {
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=5 answer_timeout=30");

            ToAnswer(m, w);

            var seq = Secret(m);
            var playback = w.Beams.Where(b => b.Tag.StartsWith("beam-")).ToList();
            var pads = Pads(w);

            Assert.AreEqual(3 * 5, playback.Count);

            for (var i = 0; i < 3; i++)
                Assert.IsTrue(playback.Skip(i * 5).Take(5).All(b => b.Script == MemoryRules.BeamScripts[seq[i]]), $"step {i} fires {MemoryRules.ColourName(seq[i])}");

            foreach (var b in playback)
            {
                var xy = new Vector2(b.Point.X, b.Point.Y);

                Assert.IsTrue(pads.All(p => Vector2.Distance(xy, p) > 1f + MemoryLayout.BeamClearance), $"beam at ({xy.X:0.0}, {xy.Y:0.0}) is on a pad");
                Assert.IsTrue(w.Region.DistanceToEdge(xy.X, xy.Y) >= 2f - 1e-3f);
                Assert.AreEqual(1.0, b.Seconds, "a beam stands for pulse_on");
            }
        }

        [TestMethod]
        public void Playback_BeamCorners_EveryPulseHasOneBeamInEachCorner_TheRestRandom_CountUnchanged_ColourUnchanged()
        {
            // round 4 playtest: 12 beams a pulse, one in every corner (edge_inset inside, as the zone pillars stand)
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=12 beam_corners=1 answer_timeout=30");

            ToAnswer(m, w);

            var seq = Secret(m);
            var playback = w.Beams.Where(b => b.Tag.StartsWith("beam-")).ToList();
            var corners = MemoryLayout.CornerBeamPoints(w.Region, Pads(w), 1f, w.EdgeInset);

            Assert.AreEqual(4, corners.Count);
            Assert.AreEqual(3 * 12, playback.Count);

            for (var i = 0; i < 3; i++)
            {
                var pulse = playback.Skip(i * 12).Take(12).ToList();

                Assert.IsTrue(pulse.All(b => b.Script == MemoryRules.BeamScripts[seq[i]]), $"step {i}: every beam, the corners too, shows {MemoryRules.ColourName(seq[i])}");

                for (var c = 0; c < 4; c++)
                {
                    Assert.AreEqual(corners[c].X, pulse[c].Point.X, 1e-4f, $"step {i}: corner {c} first");
                    Assert.AreEqual(corners[c].Y, pulse[c].Point.Y, 1e-4f, $"step {i}: corner {c} first");
                }

                Assert.IsTrue(pulse.Skip(4).All(b => !corners.Any(c => Vector2.Distance(c, new Vector2(b.Point.X, b.Point.Y)) < 1e-3f)), "the other 8 are random points");
            }

            // the corners stand edge_inset inside each region corner, as ZonesMechanic.PillarPoints places the pillars
            var pillars = ZonesMechanic.PillarPoints(w.Region, w.EdgeInset, (x, y) => 5f);
            for (var c = 0; c < 4; c++)
            {
                Assert.AreEqual(pillars[c].X, corners[c].X, 1e-4f);
                Assert.AreEqual(pillars[c].Y, corners[c].Y, 1e-4f);
            }
        }

        [TestMethod]
        public void Playback_BeamCorners_ACornerWithNoCell_FallsBackToARandomPoint_AndTheCountHolds()
        {
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=12 beam_corners=1 answer_timeout=30");

            w.CellRule = p => !(p.X < 10f && p.Y < 10f); // the (min, min) corner has no cell; set after the pads
            ToAnswer(m, w);

            var playback = w.Beams.Where(b => b.Tag.StartsWith("beam-")).ToList();

            Assert.AreEqual(3 * 12, playback.Count);
            Assert.IsTrue(playback.All(b => !(b.Point.X < 10f && b.Point.Y < 10f)), "no beam over the cell-less corner");
        }

        [TestMethod]
        public void Playback_BeamCornersOff_IsTheDefault_AndEveryBeamIsExactlyThePickBeamPointStream()
        {
            Assert.IsFalse(MemorySettings.Read(HazardArgs.Parse(HazardDefaults.Memory)).BeamCorners);
            Assert.IsFalse(MemorySettings.Read(HazardArgs.Empty).BeamCorners);

            // discriminating: with corners off, every beam point is exactly the PickBeamPoint stream. The world's Rng is
            // swapped after Start (the round-1 sequence is already dealt), so playback is the only consumer, and a replay
            // from the same seed must produce the same points in the same order.
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=4 answer_timeout=30");
            w.Rng = new Random(77);
            ToAnswer(m, w);

            var playback = w.Beams.Where(b => b.Tag.StartsWith("beam-")).ToList();
            Assert.AreEqual(3 * 4, playback.Count);

            var replay = new Random(77);
            var pads = Pads(w);

            for (var i = 0; i < playback.Count; i++)
            {
                Assert.IsTrue(MemoryLayout.PickBeamPoint(w.Region, replay, pads, 1f, w.EdgeInset, null, out var expected), "replay " + i);
                Assert.AreEqual(expected.X, playback[i].Point.X, "beam " + i + " x");
                Assert.AreEqual(expected.Y, playback[i].Point.Y, "beam " + i + " y");
            }

            // control: the same replay with corners on diverges at the very first beam (a corner, no draw)
            var (m2, w2) = Boot("len=3 max_len=3 beams_per_pulse=4 beam_corners=1 answer_timeout=30");
            w2.Rng = new Random(77);
            ToAnswer(m2, w2);
            var first = w2.Beams.First(b => b.Tag.StartsWith("beam-"));
            Assert.IsTrue(MemoryLayout.PickBeamPoint(w2.Region, new Random(77), Pads(w2), 1f, w2.EdgeInset, null, out var notACorner));
            Assert.IsFalse(first.Point.X == notACorner.X && first.Point.Y == notACorner.Y, "control: corners on does not match the random stream");
        }

        [TestMethod]
        public void Playback_BeamsAreOnlyAimedAtPointsTheWorldHasACellFor()
        {
            var (m, w) = Boot("beams_per_pulse=4 answer_timeout=30");

            w.CellRule = p => p.X >= 30f; // after the pads are placed, so only the beams are restricted
            ToAnswer(m, w);

            var playback = w.Beams.Where(b => b.Tag.StartsWith("beam-")).ToList();

            Assert.IsTrue(playback.Count > 0);
            Assert.IsTrue(playback.All(b => b.Point.X >= 30f), "no beam over a cell-less spot");
        }

        [TestMethod]
        public void Playback_WithNoCellAnywhere_FiresNothing_AndLogsOnce()
        {
            var calls = 0;
            var (m, w) = Boot("beams_per_pulse=4 answer_timeout=30");

            w.CellRule = p => { calls++; return false; };
            ToAnswer(m, w);

            Assert.AreEqual(0, w.Beams.Count);
            Assert.IsTrue(calls > 0);
            Assert.AreEqual(1, w.Logged.Count(l => l.Contains("no beam placed")), "logged once, not every pulse");
        }

        [TestMethod]
        public void CeilingBeams_HangJustUnderTheCeiling_Unturned_NoRaise_ByDefault()
        {
            var (m, w) = Boot("len=2 max_len=2 answer_timeout=30");

            ToAnswer(m, w);

            var playback = w.Beams.Select((b, i) => (b, o: w.BeamOrientations[i])).Where(x => x.b.Tag.StartsWith("beam-")).ToList();

            Assert.AreEqual(2 * 3, playback.Count, "beams_per_pulse=3 from the test Defaults string");
            Assert.IsTrue(playback.All(x => Math.Abs(x.b.Point.Z - (11f - MemoryLayout.CeilingBeamOffset)) < 1e-4f), "just under the ceiling face");
            Assert.IsTrue(playback.All(x => x.o == Quaternion.Identity), "unturned by default: the mesh hangs below the host");
            Assert.IsTrue(w.BeamRaises.All(r => !r), "the spawn is not raised either");

            var ceilingAsks = w.CellAsks.Where(a => Math.Abs(a.Point.Z - (11f - MemoryLayout.CeilingBeamOffset)) < 1e-4f).ToList();

            Assert.IsTrue(ceilingAsks.Count > 0);
            Assert.IsTrue(ceilingAsks.All(a => !a.Raise), "the ceiling point is checked with HasCell(raise: false)");
        }

        [TestMethod]
        public void CeilingBeams_BeamFlipOne_TurnsTheHost_AndBeamFromFloorStandsOnTheGroundWithARaise()
        {
            var (m0, w0) = Boot("len=1 max_len=1 beam_flip=1");
            ToAnswer(m0, w0);
            Assert.IsTrue(w0.Beams.Count > 0);
            Assert.IsTrue(w0.BeamOrientations.All(o => o == MemoryLayout.BeamOrientation(true)), "the swap turns a ceiling host");
            Assert.IsTrue(w0.Beams.All(b => b.Point.Z < 11f && b.Point.Z > 5f));

            var (m1, w1) = Boot("len=1 max_len=1 beam_from=floor");
            ToAnswer(m1, w1);
            Assert.IsTrue(w1.Beams.Count > 0);
            Assert.IsTrue(w1.Beams.All(b => b.Point.Z == 5f), "the ground point");
            Assert.IsTrue(w1.BeamOrientations.All(o => o == MemoryLayout.BeamOrientation(true)), "floor beams are turned so they rise from the floor");

            var (m2, w2) = Boot("len=1 max_len=1 beam_from=floor beam_flip=1");
            ToAnswer(m2, w2);
            Assert.IsTrue(w2.BeamOrientations.Count > 0 && w2.BeamOrientations.All(o => o == Quaternion.Identity), "the swap un-turns a floor host");
            Assert.IsTrue(w1.BeamRaises.All(r => r), "floor beams keep the raise");
        }

        [TestMethod]
        public void CeilingBeams_AreOnlyAimedAtPointsWhoseCeilingPointHasACell()
        {
            var (m, w) = Boot("beams_per_pulse=4 answer_timeout=30");

            w.CellRule = p => p.Z > 10f && p.X >= 30f; // a cell only at the ceiling point, in the east half
            ToAnswer(m, w);

            var playback = w.Beams.Where(b => b.Tag.StartsWith("beam-")).ToList();

            Assert.IsTrue(playback.Count > 0);
            Assert.IsTrue(playback.All(b => b.Point.X >= 30f));
        }

        [TestMethod]
        public void CeilingBeams_NeverFireWhereOnlyACellAboveTheCeilingExists()
        {
            var (m, w) = Boot("beams_per_pulse=4 answer_timeout=30");
            var hostZ = 11f - MemoryLayout.CeilingBeamOffset;

            // a cell only 0.5 m ABOVE the ceiling face (11): a raised check from the host would find it, an exact one must not
            w.CellRule = p => Math.Abs(p.Z - 11.5f) < 1e-3f;
            ToAnswer(m, w);

            Assert.AreEqual(0, w.Beams.Count, "no beam is spawned into the cell above the ceiling");
            Assert.IsTrue(w.CellAsks.Any(a => Math.Abs(a.Point.Z - hostZ) < 1e-4f && !a.Raise));
            Assert.IsTrue(w.Logged.Any(l => l.Contains("no beam placed")));
        }

        [TestMethod]
        public void CeilingBeamsWithNoCeilingZ_LogAWarning_AndFallBackToTheFloorOnce()
        {
            var noCeiling = HazardRegion.Rect("seam", 0x0069, 0, false, 0, 60, 0, 40, HazardGroundMode.Plane, 5, null, 0, null, 2, 6);
            var (m, w) = Boot("answer_timeout=30", 1, noCeiling); // Boot bypasses Validate

            ToAnswer(m, w);

            Assert.AreEqual(1, w.Logged.Count(l => l.Contains("no CeilingZ")), "warned once, not every pulse");
            Assert.IsTrue(w.Beams.Count > 0);
            Assert.IsTrue(w.Beams.All(b => b.Point.Z == 5f), "the documented fallback: floor beams");
        }

        [TestMethod]
        public void ACorrectEntry_EchoesABeamOnThatPadOnly()
        {
            var (m, w) = Boot("len=3 max_len=3 answer_timeout=30");
            var now = ToAnswer(m, w);
            var seq = Secret(m);
            var pads = Pads(w);

            Press(m, w, now, seq[0]);

            var echo = w.Beams.Single(b => b.Tag.StartsWith("echo-"));

            Assert.AreEqual("echo-" + MemoryRules.ColourName(seq[0]), echo.Tag);
            Assert.AreEqual(MemoryRules.BeamScripts[seq[0]], echo.Script);
            Assert.AreEqual(pads[seq[0]].X, echo.Point.X, 1e-4f);
            Assert.AreEqual(pads[seq[0]].Y, echo.Point.Y, 1e-4f);
            Assert.AreEqual(MemoryMechanic.EchoSeconds, echo.Seconds);
            Assert.AreEqual(5f, echo.Point.Z, "the echo stays on the pad's floor point, even with ceiling beams on");
            Assert.AreEqual(MemoryLayout.BeamOrientation(true), w.BeamOrientations[w.Beams.IndexOf(echo)], "and turned so its beam rises from the pad");
            Assert.IsTrue(w.BeamRaises[w.Beams.IndexOf(echo)], "the echo keeps the raise");
            Assert.IsTrue(Vector3.Transform(new Vector3(0, 0, -5), w.BeamOrientations[w.Beams.IndexOf(echo)]).Z > 4.99f, "the mesh's -5 m extent now spans up from the pad");
        }

        [TestMethod]
        public void BeamFlipSwap_InvertsTheEchoToo()
        {
            var (m, w) = Boot("len=3 max_len=3 answer_timeout=30 beam_flip=1");
            var now = ToAnswer(m, w);

            Press(m, w, now, Secret(m)[0]);

            var echo = w.Beams.Single(b => b.Tag.StartsWith("echo-"));

            Assert.AreEqual(Quaternion.Identity, w.BeamOrientations[w.Beams.IndexOf(echo)]);
        }

        // ---- pad_pillar --------------------------------------------------------------------------------------------

        [TestMethod]
        public void PadPillar_OnePillarPerFire_AtThePadCentreOfTheShownColour_StandingPulseOnSeconds_CeilingBeamsUnchanged()
        {
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=3 pad_pillar=1 answer_timeout=30");

            ToAnswer(m, w);

            var seq = Secret(m);
            var pads = Pads(w);

            Assert.AreEqual(3, w.Pillars.Count, "exactly one pillar per Fire");
            Assert.AreEqual(3 * 3, w.Beams.Count, "the colour-matched ceiling beams are unchanged and nothing else is a beam");
            Assert.AreEqual(3 * 3, w.Beams.Count(b => b.Tag.StartsWith("beam-")));

            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual("pillar-" + MemoryRules.ColourName(seq[i]), w.Pillars[i].Tag, $"step {i}");
                Assert.AreEqual(w.GroundPoint(pads[seq[i]].X, pads[seq[i]].Y), w.Pillars[i].Point, $"step {i}: the ground point at the centre of the pad of the shown colour");
                Assert.AreEqual(1.0, w.Pillars[i].Seconds, 1e-9, $"step {i}: pulse_on seconds");
            }
        }

        [TestMethod]
        public void PadPillar_Zero_IsTheDefault_PillarsAndStrikesBothEmpty_WithAPlayerStandingOnAPad()
        {
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=3 answer_timeout=30");

            w.PlayerList.Add((1, Pads(w)[0]));

            for (var now = 0.1; now < 6.0; now += 0.1)
                Tick(m, w, now);

            Assert.IsTrue(w.Beams.Count > 0, "positive control: the game ran and fired beams");
            Assert.AreEqual(0, w.Pillars.Count);
            Assert.AreEqual(0, w.Strikes.Count);
        }

        [TestMethod]
        public void PadPillar_EveryLivePillarIsStruckOncePerTick_AtPadRadius_AndNeverAtOrAfterItsExpiry()
        {
            var (m, w) = Boot("len=2 max_len=2 beams_per_pulse=3 pad_pillar=1 pad_radius=1.2 answer_timeout=30");
            var yourTurnAt = -1.0;

            w.PlayerList.Add((1, Away));

            for (var i = 1; i <= 80; i++)
            {
                var now = i * 0.1;
                var before = w.Strikes.Count;
                var live = w.Pillars.Count(p => p.SpawnedAt <= now && now < p.SpawnedAt + p.Seconds);

                Tick(m, w, now);

                // a pillar spawned on this very tick is already in Pillars, so count it too
                live = w.Pillars.Count(p => p.SpawnedAt <= now && now < p.SpawnedAt + p.Seconds);

                Assert.AreEqual(live, w.Strikes.Count - before, $"t={now:0.0}: one strike per live pillar");

                if (yourTurnAt < 0 && w.Said.Contains("Your turn."))
                    yourTurnAt = now;
            }

            Assert.AreEqual(2, w.Pillars.Count);
            Assert.IsTrue(w.Strikes.Count >= 2, "positive control: the pillars were struck");
            Assert.IsTrue(w.Strikes.All(s => Math.Abs(s.Radius - 1.2f) < 1e-6f), "the hit radius is pad_radius");
            Assert.IsTrue(w.Strikes.All(s => s.Now < s.Pillar.SpawnedAt + s.Pillar.Seconds), "no strike at or after spawn + pulse_on");
            Assert.IsTrue(yourTurnAt > 0, "the answer phase opened");
            Assert.IsFalse(w.Strikes.Any(s => s.Now >= yourTurnAt), "and none on the tick the answer phase opens");
        }

        [TestMethod]
        public void PadPillar_AKilledPillarIsNotStruckAgain_AndItsEntryIsDropped()
        {
            var (m, w) = Boot("len=1 max_len=1 beams_per_pulse=3 pad_pillar=1 answer_timeout=30");

            Tick(m, w, 1.5);
            Assert.AreEqual(1, w.Strikes.Count);

            w.Pillars[0].Alive = false;
            Tick(m, w, 1.6);
            Assert.AreEqual(1, w.Strikes.Count, "a destroyed pillar deals nothing more");

            // the entry is gone, not merely skipped: reviving the fake does not bring the strikes back
            w.Pillars[0].Alive = true;
            Tick(m, w, 1.7);
            Assert.AreEqual(1, w.Strikes.Count, "the entry was dropped");
        }

        [TestMethod]
        public void PadPillar_WhenRefused_NoStrikes_AndTheLogAppearsExactlyOncePerRun()
        {
            var (m, w) = Boot("len=3 max_len=3 beams_per_pulse=3 pad_pillar=1 answer_timeout=30", configure: x => x.RefusePillars = true);

            w.PlayerList.Add((1, Away));
            ToAnswer(m, w);

            var logs = w.Logged.Where(l => l.Contains("pad_pillar refused for")).ToList();

            Assert.AreEqual(0, w.Pillars.Count);
            Assert.AreEqual(0, w.Strikes.Count);
            Assert.AreEqual(1, logs.Count, "once per run, not once per pulse");
            Assert.AreEqual("memory: pad_pillar refused for " + MemoryRules.ColourName(Secret(m)[0]), logs[0]);
            Assert.AreEqual(3 * 3, w.Beams.Count(b => b.Tag.StartsWith("beam-")), "the playback beams are unaffected");
        }

        [TestMethod]
        public void PadPillar_AfterAWrongPad_FurtherTicksAddNoStrikes()
        {
            var (m, w) = Boot("len=2 max_len=2 beams_per_pulse=3 pad_pillar=1 answer_timeout=30");
            var now = ToAnswer(m, w);
            var wrong = (Secret(m)[0] + 1) % 4;

            Press(m, w, now, wrong);

            Assert.AreEqual(HazardOutcome.Failed, w.Ended.Single().Outcome, "positive control: the run failed");

            var strikes = w.Strikes.Count;

            Assert.IsTrue(strikes > 0, "positive control: the pillars struck while they stood");

            for (var i = 1; i <= 20; i++)
                Tick(m, w, now + 1.0 + i * 0.1);

            Assert.AreEqual(strikes, w.Strikes.Count);
        }

        [TestMethod]
        public void PadPillar_AfterATimeout_FurtherTicksAddNoStrikes()
        {
            var (m, w) = Boot("len=2 max_len=2 beams_per_pulse=3 pad_pillar=1 answer_timeout=5");
            var now = ToAnswer(m, w);

            for (var i = 1; i <= 70 && w.Ended.Count == 0; i++)
                Tick(m, w, now + i * 0.1);

            Assert.AreEqual(HazardOutcome.Failed, w.Ended.Single().Outcome, "positive control: the run timed out");

            var strikes = w.Strikes.Count;

            Assert.IsTrue(strikes > 0, "positive control: the pillars struck while they stood");

            for (var i = 1; i <= 20; i++)
                Tick(m, w, now + 10.0 + i * 0.1);

            Assert.AreEqual(strikes, w.Strikes.Count);
        }

        [TestMethod]
        public void PadPillar_NoStrikeAfterAnAbortOrAStop_EvenWhileAPillarStillStands()
        {
            // pulse_on 10 keeps the pillar up long after the run ends, so these are not vacuous
            var (m, w) = Boot("len=1 max_len=1 beams_per_pulse=3 pad_pillar=1 pulse_on=10 answer_timeout=30");

            Tick(m, w, 1.5);
            Assert.AreEqual(1, w.Strikes.Count);

            foreach (var s in w.Spawns)
                s.Piece.Alive = false;

            w.RefuseSpawn = (_, _, _) => true;
            Tick(m, w, 2.6);

            Assert.AreEqual(HazardOutcome.Aborted, w.Ended.Single().Outcome);
            Tick(m, w, 2.7);
            Assert.AreEqual(1, w.Strikes.Count, "an aborted run strikes nothing more");

            var (m2, w2) = Boot("len=1 max_len=1 beams_per_pulse=3 pad_pillar=1 pulse_on=10 answer_timeout=30");

            Tick(m2, w2, 1.5);
            Assert.AreEqual(1, w2.Strikes.Count);

            m2.StopCore(w2, HazardOutcome.Stopped);
            Tick(m2, w2, 1.6);
            Assert.AreEqual(1, w2.Strikes.Count, "a stopped run strikes nothing more");
        }
        // ---- touches -----------------------------------------------------------------------------------------------

        public void StepFromOnePadStraightOntoTheNext_WithoutLeaving_CountsAsAnEntry()
        {
            // find a seed whose first two colours differ
            for (var seed = 1; seed < 200; seed++)
            {
                var (m, w) = Boot("len=3 max_len=3 answer_timeout=30", seed);
                var seq = Secret(m);

                if (seq[0] == seq[1])
                    continue;

                var now = ToAnswer(m, w);
                var pads = Pads(w);

                w.PlayerList.Clear();
                w.PlayerList.Add((1, pads[seq[0]]));
                Tick(m, w, now + 0.1);
                Assert.IsTrue(w.Said.Any(s => s.Contains("step 1 of 3")));

                // straight from pad A onto pad B, never off a pad
                w.PlayerList.Clear();
                w.PlayerList.Add((1, pads[seq[1]]));
                Tick(m, w, now + 0.2);

                Assert.AreEqual(0, w.Ended.Count);
                Assert.IsTrue(w.Said.Any(s => s.Contains("step 2 of 3")), "the switch was an entry");
                return;
            }

            Assert.Fail("no seed with differing first colours");
        }

        [TestMethod]
        public void TouchesDuringPlayback_AreIgnoredByTheMechanic()
        {
            var (m, w) = Boot();
            var pads = Pads(w);

            w.PlayerList.Add((1, pads[3]));

            for (var now = 0.1; now < 1.4; now += 0.1)
                Tick(m, w, now);

            Assert.AreEqual(0, w.Ended.Count);
            Assert.IsFalse(w.Said.Contains("Your turn."));
        }

        [TestMethod]
        public void TheAnswerWindow_OpensPulseOnAfterTheLastColourActuallyFired()
        {
            var (m, w) = Boot("len=1 max_len=1 answer_timeout=30");

            // the first tick comes late: the colour is due at 1.5 s but fires at 5.0 s
            Tick(m, w, 5.0);
            Assert.AreEqual(3, w.Beams.Count(b => b.Tag.StartsWith("beam-")));

            Tick(m, w, 5.9);
            Assert.IsFalse(w.Said.Contains("Your turn."), "the beam stands until 6.0 (pulse_on 1.0 after the real fire time)");

            Tick(m, w, 6.0);
            Assert.IsTrue(w.Said.Contains("Your turn."));
        }

        [TestMethod]
        public void Validate_AndStart_AgreeOnTheSameRegion()
        {
            // Validate accepted the default settings on this region; Start must then get as far as the first round
            var region = Region();

            Assert.IsNull(new MemoryMechanic().Validate(region, HazardArgs.Parse(HazardDefaults.Memory), null));

            var (_, w) = Boot(region: region);
            Assert.AreEqual(0, w.Ended.Count);
            Assert.IsTrue(w.Said.Any(s => s.StartsWith("Round 1:")));
        }
    }
}

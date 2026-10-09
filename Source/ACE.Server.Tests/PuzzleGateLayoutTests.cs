using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;

namespace ACE.Server.Tests
{
    [TestClass]
    public class PuzzleGateLayoutTests
    {
        private static readonly Vector3 Anchor = new Vector3(100.0f, 50.0f, 12.0f);
        private const float Yaw = 37.0f;

        private static PuzzleGateOptions Opts(string line)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var o, out var err), err);
            return o;
        }

        private static PuzzleGateGenerator Gen(string line, int seed, IReadOnlyList<Vector3> spots = null, Func<Vector3, bool> validator = null)
        {
            var input = new PuzzleGateInput
            {
                AnchorPosition = Anchor,
                AnchorYawDeg = Yaw,
                Options = Opts(line),
                Seed = seed,
                RecordedSpots = spots,
                SpotValidator = validator,
            };

            Assert.IsTrue(PuzzleGateGenerator.TryCreate(input, out var g, out var err), err);
            return g;
        }

        /// <summary>Independent of the production helper: anchor + local row position rotated by Yaw.</summary>
        private static Vector3 ExpectedLever(int slot, int n)
        {
            var lx = (slot - (n - 1) / 2.0) * 1.5;
            var ly = 3.0;
            var yaw = Yaw * Math.PI / 180.0;
            return new Vector3(
                (float)(Anchor.X + lx * Math.Cos(yaw) - ly * Math.Sin(yaw)),
                (float)(Anchor.Y + lx * Math.Sin(yaw) + ly * Math.Cos(yaw)),
                Anchor.Z);
        }

        private static List<PuzzleObjectSpec> Candidates(PuzzlePlan p) => p.Specs.Where(s => s.Role == PuzzleRole.Candidate).ToList();

        private static void AssertNear(Vector3 expected, Vector3 actual, string msg)
        {
            Assert.IsTrue((expected - actual).Length() < 1e-3f, $"{msg}: expected {expected} got {actual}");
        }

        [TestMethod]
        public void Candidates_SitAtTheirSlotPosition_RegardlessOfAnswer_200Seeds()
        {
            foreach (var type in new[] { "sigil", "beam", "odd" })
            {
                for (var n = (type == "beam" ? 2 : PuzzleGateTunables.MinPickN); n <= 5; n++)
                {
                    var answers = new HashSet<int>();

                    for (var seed = 0; seed < 200; seed++)
                    {
                        var g = Gen($"{type} n={n}", seed);

                        for (var round = 0; round < 3; round++)
                        {
                            var plan = g.Next();
                            var c = Candidates(plan);
                            Assert.AreEqual(n, c.Count, $"{type} n={n} seed={seed}");

                            for (var i = 0; i < n; i++)
                            {
                                Assert.AreEqual(i, c[i].Slot, $"{type} candidates must be in slot order");
                                AssertNear(ExpectedLever(i, n), c[i].Position, $"{type} n={n} seed={seed} slot {i}");
                            }

                            answers.Add(plan.AnswerSlot);
                        }
                    }

                    Assert.AreEqual(n, answers.Count, $"{type} n={n}: every slot must be reachable as the answer");
                }
            }
        }

        [TestMethod]
        public void Candidates_AreIdenticalExceptTheAllowedChannel()
        {
            // sigil, beam, shuffle: NO channel - every field but slot/position is identical across candidates.
            foreach (var line in new[] { "sigil n=4", "beam n=5 beams=3", "beam n=4 beams=1 axis=up" })
            {
                for (var seed = 0; seed < 200; seed++)
                {
                    var plan = Gen(line, seed).Next();
                    var c = Candidates(plan).Select(s => s with { Slot = 0, Position = default, LocalPosition = default }).ToList();
                    Assert.IsTrue(c.All(s => s == c[0]), $"{line} seed={seed}: candidates differ beyond position");
                }
            }

            // odd: only the chosen channel may stand out of the shared noise band.
            foreach (var channel in new[] { "scale", "yaw", "glow" })
            {
                for (var seed = 0; seed < 200; seed++)
                {
                    var plan = Gen($"odd n=5 diff={channel}", seed).Next();
                    var c = Candidates(plan);
                    var faceYaw = Yaw + 180.0f;

                    for (var i = 0; i < c.Count; i++)
                    {
                        var isAnswer = i == plan.AnswerSlot;
                        var scaleOut = Math.Abs(c[i].Scale - 1.0f) > PuzzleGateTunables.NoiseScale + 1e-4f;
                        var yawOff = Math.Abs(Wrap(PuzzleGateGenerator.YawDegFromQuaternion(c[i].Orientation) - faceYaw));
                        var yawOut = yawOff > PuzzleGateTunables.NoiseYawDeg + 1e-2f;
                        var glow = c[i].Script != 0;

                        Assert.AreEqual(isAnswer && channel == "scale", scaleOut, $"{channel} seed={seed} slot {i} scale");
                        Assert.AreEqual(isAnswer && channel == "yaw", yawOut, $"{channel} seed={seed} slot {i} yaw");
                        Assert.AreEqual(isAnswer && channel == "glow", glow, $"{channel} seed={seed} slot {i} glow");
                        Assert.AreEqual(PuzzleRole.Candidate, c[i].Role);
                    }
                }
            }
        }

        private static float Wrap(float deg)
        {
            while (deg > 180.0f) deg -= 360.0f;
            while (deg < -180.0f) deg += 360.0f;
            return deg;
        }

        [TestMethod]
        public void Odd_AutoChannel_ReachesAllThreeChannels()
        {
            var seen = new HashSet<PuzzleOddChannel>();

            for (var seed = 0; seed < 200; seed++)
                seen.Add(Gen("odd n=4", seed).Next().OddChannel);

            Assert.AreEqual(3, seen.Count);
            Assert.IsFalse(seen.Contains(PuzzleOddChannel.Auto));
        }

        [TestMethod]
        public void SameSeed_SamePlans_DifferentSeed_ChangesTheAnswer()
        {
            foreach (var line in new[] { "sigil n=4", "beam n=5 beams=3", "odd n=5", "shuffle spots=5" })
            {
                var a = Gen(line, 77);
                var b = Gen(line, 77);

                for (var round = 0; round < 4; round++)
                {
                    var pa = a.Next();
                    var pb = b.Next();
                    Assert.AreEqual(pa.AnswerSlot, pb.AnswerSlot, line);
                    Assert.AreEqual(pa.Gate, pb.Gate, line);
                    CollectionAssert.AreEqual(pa.Specs.ToList(), pb.Specs.ToList(), line + " round " + round);
                }

                var answers = new HashSet<int>();

                for (var seed = 0; seed < 200; seed++)
                    answers.Add(Gen(line, seed).Next().AnswerSlot);

                Assert.IsTrue(answers.Count > 1, line + ": the answer never changed with the seed");
            }
        }

        [TestMethod]
        public void Reshuffle_FromTheSameStream_VariesAcrossRounds_AndIsFixedDrawCount()
        {
            // Fixed draw block: sigil n=4 and odd n=4 spend identical draws per round, so round 2 and 3
            // answers agree. An unconditional-draw break (a type skipping draws) would desync them.
            for (var seed = 0; seed < 100; seed++)
            {
                var s = Gen("sigil n=4", seed);
                var o = Gen("odd n=4", seed);

                for (var round = 0; round < 4; round++)
                    Assert.AreEqual(s.Next().AnswerSlot, o.Next().AnswerSlot, $"seed {seed} round {round}");
            }

            var moved = 0;

            for (var seed = 0; seed < 100; seed++)
            {
                var g = Gen("sigil n=4", seed);
                if (g.Next().AnswerSlot != g.Next().AnswerSlot)
                    moved++;
            }

            Assert.IsTrue(moved > 20, "reshuffle should usually move the answer");
        }

        [TestMethod]
        public void Sigil_IndicatorShowsTheAnswerLightColour_AndLightColoursAreDistinct()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var plan = Gen("sigil n=4", seed).Next();
                var lights = plan.Specs.Where(s => s.Role == PuzzleRole.Light).OrderBy(s => s.Slot).ToList();
                var indicators = plan.Specs.Where(s => s.Role == PuzzleRole.Indicator).OrderBy(s => s.Slot).ToList();

                Assert.AreEqual(4, lights.Select(l => l.Script).Distinct().Count());
                Assert.AreEqual(PuzzleGateTunables.IndicatorCount, indicators.Count);
                Assert.IsTrue(indicators.All(i => i.Script == lights[plan.AnswerSlot].Script), "all indicators show the answer light colour");
                Assert.AreEqual(PuzzleGateColours.ByIndex(plan.AnswerColourIndex).Script, indicators[0].Script);
            }
        }

        /// <summary>
        /// Sigil may have more levers than colours: the ANSWER colour must be unique among the levers, decoys
        /// never use it (one decoy colour may repeat), and the indicators show it. Every seed, every n.
        /// </summary>
        [TestMethod]
        public void Sigil_AnswerColourIsUniqueAmongLevers_DecoysNeverUseIt_EverySeedAndN()
        {
            foreach (var line in new[] { "sigil n=4", "sigil n=5", "sigil" })
            {
                for (var seed = 0; seed < 300; seed++)
                {
                    var g = Gen(line, seed);

                    for (var round = 0; round < 3; round++)
                    {
                        var plan = g.Next();
                        var lights = plan.Specs.Where(s => s.Role == PuzzleRole.Light).OrderBy(s => s.Slot).ToList();
                        var indicators = plan.Specs.Where(s => s.Role == PuzzleRole.Indicator).ToList();
                        var answerScript = PuzzleGateColours.ByIndex(plan.AnswerColourIndex).Script;
                        var ctx = $"{line} seed={seed} round={round + 1}";

                        Assert.AreEqual(g.N, lights.Count, ctx);
                        Assert.AreEqual(answerScript, lights[plan.AnswerSlot].Script, ctx + " answer lever wears the answer colour");
                        Assert.AreEqual(1, lights.Count(l => l.Script == answerScript), ctx + " answer colour unique among the levers");

                        for (var i = 0; i < lights.Count; i++)
                            if (i != plan.AnswerSlot)
                                Assert.AreNotEqual(answerScript, lights[i].Script, ctx + $" decoy {i} uses the answer colour");

                        Assert.IsTrue(indicators.Count > 0 && indicators.All(s => s.Script == answerScript), ctx + " indicators show the answer colour");

                        // With n levers and 4 colours, decoys use every other colour before any repeats.
                        var decoyScripts = lights.Where((l, i) => i != plan.AnswerSlot).Select(l => l.Script).ToList();
                        Assert.AreEqual(Math.Min(decoyScripts.Count, PuzzleGateColours.Palette.Length - 1), decoyScripts.Distinct().Count(), ctx);
                    }
                }
            }
        }

        /// <summary>Sigil and odd with n= omitted draw 4 or 5 per placement from the seed; both occur, and n never changes between rounds.</summary>
        [TestMethod]
        public void PickTypes_OmittedN_DrawsFourOrFivePerPlacement()
        {
            foreach (var type in new[] { "sigil", "odd" })
            {
                var seen = new HashSet<int>();

                for (var seed = 0; seed < 200; seed++)
                {
                    var g = Gen(type, seed);
                    Assert.IsTrue(g.N == 4 || g.N == 5, $"{type} seed={seed} n={g.N}");
                    seen.Add(g.N);

                    for (var round = 0; round < 3; round++)
                        Assert.AreEqual(g.N, Candidates(g.Next()).Count, $"{type} seed={seed} round={round + 1}");

                    Assert.AreEqual(g.N, Gen(type, seed).N, "same seed, same n");
                }

                CollectionAssert.AreEquivalent(new[] { 4, 5 }, seen.ToArray(), type + ": both 4 and 5 must occur");
            }

            // Explicit n wins, and beam keeps its own default.
            Assert.AreEqual(5, Gen("odd n=5", 3).N);
            Assert.AreEqual(PuzzleGateTunables.DefaultN, Gen("beam", 3).N);
        }

        [TestMethod]
        public void Odd_ScaleChannel_IsWellOutsideTheNoiseBand()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var plan = Gen("odd n=5 diff=scale", seed).Next();
                var odd = Candidates(plan)[plan.AnswerSlot];
                var off = Math.Abs(odd.Scale - 1.0f);
                Assert.IsTrue(off >= 0.3f, $"seed={seed}: odd scale {odd.Scale} is not distinct enough");
            }

            Assert.AreEqual(1.4f, PuzzleGateTunables.OddScaleHigh);
            Assert.AreEqual(0.65f, PuzzleGateTunables.OddScaleLow);
            Assert.AreEqual(0x3300101Bu, PuzzleGateColours.GlowScript, "glow is the God-tier frost bloom");
        }

        [TestMethod]
        public void Indicators_AreThree_InFrontOfTheGate_EvenlySpaced_AllTheAnswerColour()
        {
            foreach (var line in new[] { "sigil n=4", "beam n=5 beams=2", "beam n=5 beams=4 axis=up" })
            {
                for (var seed = 0; seed < 100; seed++)
                {
                    var plan = Gen(line, seed).Next();
                    var ind = plan.Specs.Where(s => s.Role == PuzzleRole.Indicator).OrderBy(s => s.Slot).ToList();
                    var gateLocal = PuzzleGateGenerator.ToLocal(Anchor, Yaw, plan.Gate.Position);

                    Assert.AreEqual(3, ind.Count, line);
                    CollectionAssert.AreEqual(new[] { 0, 1, 2 }, ind.Select(i => i.Slot).ToArray(), line);
                    Assert.AreEqual(1, ind.Select(i => i.Script).Distinct().Count(), line + " one colour");
                    Assert.AreEqual(PuzzleGateColours.ByIndex(plan.AnswerColourIndex).Script, ind[0].Script, line);

                    var locals = ind.Select(i => PuzzleGateGenerator.ToLocal(Anchor, Yaw, i.Position)).ToList();

                    foreach (var l in locals)
                    {
                        Assert.IsTrue(l.Y < gateLocal.Y - 0.5f, $"{line}: indicator must stand in front of the gate (y {l.Y} vs gate {gateLocal.Y})");
                        Assert.AreEqual(locals[0].Y, l.Y, 1e-3f);
                        Assert.AreEqual(locals[0].Z, l.Z, 1e-3f);
                    }

                    Assert.AreEqual(1.0f, locals[1].X - locals[0].X, 1e-3f, line);
                    Assert.AreEqual(1.0f, locals[2].X - locals[1].X, 1e-3f, line);
                    Assert.AreEqual(gateLocal.X, locals[1].X, 1e-3f, line + " centred on the door");
                }
            }
        }

        [TestMethod]
        public void Palette_HasNoDuplicateScripts_AndNoWhite()
        {
            var p = PuzzleGateColours.Palette;
            Assert.AreEqual(4, p.Length);
            Assert.AreEqual(p.Length, p.Select(c => c.Script).Distinct().Count());
            Assert.IsFalse(p.Any(c => c.Name == "white"));
            Assert.IsFalse(p.Any(c => c.Script == 0x33000799u));
            Assert.AreEqual(p.Length, PuzzleGateTunables.MaxBeams);
            Assert.AreEqual(PuzzleGateTunables.MaxPickN, PuzzleGateTunables.MaxSigilN, "sigil n is no longer palette-bound");
        }

        [TestMethod]
        public void Sigil_AndBeamAnswerColoursReachEveryPaletteEntry()
        {
            var seen = new HashSet<int>();

            for (var seed = 0; seed < 200; seed++)
                seen.Add(Gen("sigil n=4", seed).Next().AnswerColourIndex);

            Assert.AreEqual(PuzzleGateColours.Palette.Length, seen.Count);
        }

        [TestMethod]
        public void Beam_EachBeamHitsADistinctLever_AndIndicatorNamesTheAnswerBeam()
        {
            foreach (var axis in new[] { PuzzleBeamAxis.Down, PuzzleBeamAxis.Up })
            {
                for (var n = 2; n <= 5; n++)
                {
                    for (var k = 1; k <= Math.Min(n, PuzzleGateTunables.MaxBeams); k++)
                    {
                        for (var seed = 0; seed < 40; seed++)
                        {
                            var line = $"beam n={n} beams={k} axis={axis.ToString().ToLowerInvariant()}";
                            var plan = Gen(line, seed).Next();
                            var lights = plan.Specs.Where(s => s.Role == PuzzleRole.Light).ToList();
                            var indicators = plan.Specs.Where(s => s.Role == PuzzleRole.Indicator).ToList();
                            var levers = Candidates(plan);

                            Assert.AreEqual(k, lights.Count, line);
                            Assert.AreEqual(k > 1 ? PuzzleGateTunables.IndicatorCount : 0, indicators.Count, line);
                            Assert.AreEqual(k, lights.Select(l => l.Script).Distinct().Count(), line + " colours");

                            var hit = new List<int>();

                            foreach (var light in lights)
                            {
                                var d = PuzzleBeamAim.BeamDirection(light.Orientation, axis);
                                var best = -1;
                                var bestDist = float.MaxValue;

                                for (var i = 0; i < levers.Count; i++)
                                {
                                    var rel = levers[i].Position - light.Position;
                                    var along = Vector3.Dot(rel, d);
                                    var off = (rel - d * along).Length();

                                    if (along > 0 && off < bestDist)
                                    {
                                        bestDist = off;
                                        best = i;
                                    }
                                }

                                Assert.IsTrue(bestDist < 1e-2f, $"{line} seed={seed}: beam misses every lever by {bestDist}");
                                hit.Add(best);
                            }

                            Assert.AreEqual(k, hit.Distinct().Count(), line + " distinct levers");

                            var answerBeam = k > 1
                                ? lights.FindIndex(l => l.Script == indicators[0].Script)
                                : 0;

                            Assert.AreEqual(hit[answerBeam], plan.AnswerSlot, line + " answer");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Shuffle_UsesRecordedSpots_MovesEveryRound_AndMatchesSlot()
        {
            var spots = new[] { new Vector3(10, 10, 1), new Vector3(20, 10, 1), new Vector3(30, 40, 2), new Vector3(5, 5, 0) };

            for (var seed = 0; seed < 200; seed++)
            {
                var g = Gen("shuffle", seed, spots);
                var prev = -1;

                for (var round = 0; round < 5; round++)
                {
                    var plan = g.Next();
                    var c = Candidates(plan);
                    Assert.AreEqual(1, c.Count);
                    Assert.AreEqual(plan.AnswerSlot, c[0].Slot);
                    AssertNear(spots[plan.AnswerSlot], c[0].Position, "spot");
                    AssertNear(spots[plan.AnswerSlot], PuzzleGateGenerator.ToWorld(Anchor, Yaw, c[0].LocalPosition), "local roundtrip");
                    Assert.AreNotEqual(prev, plan.AnswerSlot, "lever must move every round");
                    prev = plan.AnswerSlot;
                }
            }
        }

        [TestMethod]
        public void Shuffle_FewerThanTwoRecordedSpots_FallsBackToARing()
        {
            var g = Gen("shuffle spots=4 radius=8", 5, new[] { new Vector3(1, 1, 1) });
            Assert.AreEqual(4, g.Spots.Count);

            foreach (var s in g.Spots)
                Assert.AreEqual(8.0f, new Vector2(s.X - Anchor.X, s.Y - Anchor.Y).Length(), 1e-3f);

            Assert.AreEqual(4, g.Spots.Select(s => (MathF.Round(s.X, 2), MathF.Round(s.Y, 2))).Distinct().Count());
        }

        /// <summary>
        /// A ring point at or beyond the gate line would put the lever behind the gate (unreachable in a
        /// corridor). Every ring point, for every radius and spot count and many seeds, must sit strictly more
        /// than the clearance in front of the gate, and every lever the generator then places must too.
        /// </summary>
        [TestMethod]
        public void Shuffle_RingFallback_NeverPlacesALeverAtOrBehindTheGate()
        {
            var limit = PuzzleGateGenerator.GateLocal.Y - 1.0f;

            foreach (var radius in new[] { 2.0f, 4.0f, 5.9f, 6.0f, 6.1f, 8.0f, 12.5f, 20.0f, 30.0f })
            {
                for (var k = PuzzleGateTunables.MinSpots; k <= PuzzleGateTunables.MaxSpots; k++)
                {
                    for (var seed = 1; seed <= 25; seed++)
                    {
                        var line = string.Format(System.Globalization.CultureInfo.InvariantCulture, "shuffle spots={0} radius={1} rounds=5", k, radius);
                        var g = Gen(line, seed);

                        Assert.AreEqual(k, g.Spots.Count, line);

                        foreach (var s in g.Spots)
                        {
                            var local = PuzzleGateGenerator.ToLocal(Anchor, Yaw, s);
                            Assert.IsTrue(local.Y < limit, $"{line}: ring point local y {local.Y} not in front of {limit}");
                            Assert.AreEqual(radius, new Vector2(local.X, local.Y).Length(), 1e-3f, line);
                        }

                        for (var r = 0; r < 5; r++)
                            Assert.IsTrue(Candidates(g.Next())[0].LocalPosition.Y < limit, $"{line} seed {seed} round {r + 1}");
                    }
                }
            }
        }

        [TestMethod]
        public void Shuffle_RingPointsRejectedByTheValidator_AreDropped_OrTheCreateFails()
        {
            var g = Gen("shuffle spots=4 radius=8", 5, null, p => p.X >= Anchor.X);
            Assert.IsTrue(g.Spots.Count < 4 && g.Spots.Count >= 2);
            Assert.IsTrue(g.Spots.All(p => p.X >= Anchor.X));

            var input = new PuzzleGateInput { AnchorPosition = Anchor, AnchorYawDeg = Yaw, Options = Opts("shuffle"), Seed = 1, SpotValidator = _ => false };
            Assert.IsFalse(PuzzleGateGenerator.TryCreate(input, out var none, out var err));
            Assert.IsNull(none);
            Assert.IsFalse(string.IsNullOrEmpty(err));
        }

        [TestMethod]
        public void Gate_IsIdenticalEveryRound()
        {
            var g = Gen("sigil n=4", 9);
            var first = g.Next().Gate;

            for (var i = 0; i < 4; i++)
                Assert.AreEqual(first, g.Next().Gate);

            Assert.AreEqual(PuzzleRole.Gate, first.Role);
        }

        [TestMethod]
        public void Frame_RoundTrips_AndYawFromQuaternionMatches()
        {
            var local = new Vector3(2.5f, -3.0f, 1.0f);
            var world = PuzzleGateGenerator.ToWorld(Anchor, Yaw, local);
            AssertNear(local, PuzzleGateGenerator.ToLocal(Anchor, Yaw, world), "roundtrip");

            foreach (var yaw in new[] { -170.0f, -45.0f, 0.0f, 37.0f, 90.0f, 179.0f })
                Assert.AreEqual(yaw, PuzzleGateGenerator.YawDegFromQuaternion(PuzzleGateGenerator.YawQuaternion(yaw)), 1e-2f);

            // Forward is (-sin yaw, cos yaw): yaw 90 puts +y local on -x world.
            AssertNear(new Vector3(-1, 0, 0), PuzzleGateGenerator.ToWorld(Vector3.Zero, 90.0f, new Vector3(0, 1, 0)), "forward");
        }
    }
}
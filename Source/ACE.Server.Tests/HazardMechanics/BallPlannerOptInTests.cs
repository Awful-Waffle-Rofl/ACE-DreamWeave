using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Grael;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Grael playtest round 2, ball planner (E1): the opt-in balls args origin_clear, no_overlap and distinct_dirs.
    /// All three default off and then plan, and draw, exactly what the planner always did. Pure: no PropertyManager is
    /// touched (args are the shipped HazardDefaults.Balls row with the test's keys laid over it).
    /// </summary>
    [TestClass]
    public class BallPlannerOptInTests
    {
        private static readonly BallSide[] Sides = { BallSide.North, BallSide.East, BallSide.South, BallSide.West };

        private sealed class NullHost : IHazardHost
        {
            public void OnEnded(HazardRun run, HazardOutcome outcome)
            {
            }
        }

        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static HazardArgs Merged(HazardArgs args) => HazardArgs.Merge(args, HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern) => BallRules.Read(Merged(args), pattern);

        private static string Dir(BallSide s) => BallPatterns.Name(s).Substring(0, 1);

        private static string Fingerprint(BallWave w)
            => string.Join(";", w.Routes.Select(r => FormattableString.Invariant($"{r.From.X:0.###},{r.From.Y:0.###}>{r.To.X:0.###},{r.To.Y:0.###}")))
               + "|" + string.Join(";", w.Gaps.Select(g => FormattableString.Invariant($"{g.Side}:{g.Lateral:0.###}")))
               + "|" + string.Join(";", w.Markers.Select(m => FormattableString.Invariant($"{m.X:0.###},{m.Y:0.###}")))
               + "|" + w.Summary + "|" + w.MarkerNote + "|" + (w.SafePoint.HasValue ? FormattableString.Invariant($"{w.SafePoint.Value.X:0.###},{w.SafePoint.Value.Y:0.###}") : "-");

        /// <summary>The lateral coordinate of each ball of one wall, ascending.</summary>
        private static List<float> Laterals(BallWave wave, BallSide side)
            => wave.Routes.Where(r => r.Tag == "row-" + BallPatterns.Name(side))
                .Select(r => BallPatterns.LateralIsX(side) ? r.From.X : r.From.Y)
                .OrderBy(v => v).ToList();

        private static string Coord(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        // ---- all three off: byte-identical plans and RNG ---------------------------------------------------------------

        [TestMethod]
        public void AllThreeOff_PlansAndDrawsExactlyAsBefore_EveryPattern()
        {
            var busy = new[] { BallSide.North, BallSide.East };

            foreach (var pattern in BallPatterns.All.Where(x => x != BallPatterns.Fan))
            {
                for (var seed = 1; seed <= 30; seed++)
                {
                    for (var wave = 1; wave <= 2; wave++)
                    {
                        var rngA = new Random(seed);
                        var rngB = new Random(seed);
                        var rngC = new Random(seed);
                        var today = BallPatterns.Plan(Pinnacle(), pattern, P("", pattern), rngA, wave);
                        var off = BallPatterns.Plan(Pinnacle(), pattern, P("origin_clear=0 no_overlap=0 distinct_dirs=0", pattern), rngB, wave);

                        // busy walls are ignored without distinct_dirs
                        var offBusy = BallPatterns.Plan(Pinnacle(), pattern, P("no_overlap=0 distinct_dirs=0", pattern), rngC, wave, busy);
                        var what = $"{pattern} seed {seed} wave {wave}";

                        Assert.AreEqual(Fingerprint(today), Fingerprint(off), what);
                        Assert.AreEqual(Fingerprint(today), Fingerprint(offBusy), what + " (busy walls passed)");

                        var next = rngA.Next();
                        Assert.AreEqual(next, rngB.Next(), what + ": the same draws");
                        Assert.AreEqual(next, rngC.Next(), what + ": the same draws with busy walls passed");
                    }
                }
            }

            foreach (var aim in new[] { 0.0, 1.0, -2.5 })
                Assert.AreEqual(Fingerprint(BallPatterns.PlanFanAlong(Pinnacle(), P("", BallPatterns.Fan), aim)), Fingerprint(BallPatterns.PlanFanAlong(Pinnacle(), P("origin_clear=0", BallPatterns.Fan), aim)), "fan aim " + aim);

            var p = P("", BallPatterns.Row);
            Assert.IsFalse(p.NoOverlap);
            Assert.IsFalse(p.DistinctDirs);
            Assert.AreEqual(0f, p.OriginClear);
        }

        [TestMethod]
        public void TheParentGoldenPlans_StillHold_WithAllThreeOff()
        {
            foreach (var (pattern, seed, golden) in BallGapsParentFingerprints.Plans)
            {
                var plan = BallPatterns.Plan(Pinnacle(), pattern, P("origin_clear=0 no_overlap=0 distinct_dirs=0", pattern), new Random(seed));
                var print = string.Join(";", plan.Routes.Select(r => FormattableString.Invariant($"{r.From.X:0.###},{r.From.Y:0.###}>{r.To.X:0.###},{r.To.Y:0.###}")))
                    + "|" + string.Join(";", plan.Gaps.Select(g => FormattableString.Invariant($"{g.Side}:{g.Lateral:0.###}")))
                    + "|" + string.Join(";", plan.Markers.Select(m => FormattableString.Invariant($"{m.X:0.###},{m.Y:0.###}")))
                    + "|" + plan.Summary;

                Assert.AreEqual(golden, print, $"{pattern} seed {seed}");
            }
        }

        [TestMethod]
        public void TheNewArgs_AreCopiedIntoEveryWave_AndClamped()
        {
            var p = P("origin_clear=3 no_overlap=1 distinct_dirs=1", BallPatterns.Row);
            var w = BallRules.ForWave(p, 3);

            Assert.AreEqual(3f, w.OriginClear);
            Assert.IsTrue(w.NoOverlap);
            Assert.IsTrue(w.DistinctDirs);

            Assert.AreEqual((float)HazardDefaults.BallsMaxOriginClear, P("origin_clear=99", BallPatterns.Fan).OriginClear);
            Assert.AreEqual(0f, P("origin_clear=-4", BallPatterns.Fan).OriginClear);
        }

        // ---- origin_clear -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void OriginClear_Fan_StartsEveryBallThatFarOut_AlongItsOwnRay_EndingWhereItAlwaysDid()
        {
            var o = new Vector2(10f, -20f);
            var centre = $"cx={Coord(o.X)} cy={Coord(o.Y)} spread=90";
            var off = P(centre, BallPatterns.Fan);
            var on = P(centre + " origin_clear=3", BallPatterns.Fan);

            foreach (var aim in new[] { 0.0, 0.7, 2.0, -1.2, 3.1 })
            {
                var before = BallPatterns.PlanFanAlong(Pinnacle(), off, aim);
                var after = BallPatterns.PlanFanAlong(Pinnacle(), on, aim);

                Assert.AreEqual(before.Routes.Count, after.Routes.Count, "aim " + aim);

                for (var i = 0; i < after.Routes.Count; i++)
                {
                    var r = after.Routes[i];
                    var what = $"aim {aim} ball {i}";

                    Assert.AreEqual(o.X, before.Routes[i].From.X, 1e-4, what + ": control, the default starts on the origin");
                    Assert.AreEqual(o.Y, before.Routes[i].From.Y, 1e-4, what);
                    Assert.AreEqual(3.0, Vector2.Distance(o, r.From), 1e-3, what + ": starts origin_clear from the origin");
                    Assert.AreEqual(before.Routes[i].To.X, r.To.X, 1e-4, what + ": ends where it always did");
                    Assert.AreEqual(before.Routes[i].To.Y, r.To.Y, 1e-4, what);

                    var rayDir = Vector2.Normalize(before.Routes[i].To - o);
                    var startDir = Vector2.Normalize(r.From - o);
                    Assert.AreEqual(1.0, Vector2.Dot(rayDir, startDir), 1e-4, what + ": on its own ray");
                }

                Assert.IsTrue(after.Markers.All(m => Vector2.Distance(o, m) >= 3f - 1e-3f), "aim " + aim + ": no pillar inside the clearance");
            }

            // a clearance past the first pillar (6 m) drops it: aim east from x=10 reaches x=32.6, pillars at 6, 12, 18 m
            Assert.AreEqual(3, BallPatterns.PlanFanAlong(Pinnacle(), off, 0.0).Markers.Count, "control");
            Assert.AreEqual(2, BallPatterns.PlanFanAlong(Pinnacle(), P(centre + " origin_clear=7", BallPatterns.Fan), 0.0).Markers.Count);
        }

        [TestMethod]
        public void OriginClear_RingOut_StartsThatFarOut_RingIn_StopsThatFarShort_SameRaysAsBefore()
        {
            var c = Pinnacle().Center;

            foreach (var pattern in new[] { BallPatterns.RingOut, BallPatterns.RingIn })
            {
                for (var seed = 1; seed <= 20; seed++)
                {
                    var before = BallPatterns.Plan(Pinnacle(), pattern, P("count=8", pattern), new Random(seed));
                    var after = BallPatterns.Plan(Pinnacle(), pattern, P("count=8 origin_clear=3", pattern), new Random(seed));
                    var what = $"{pattern} seed {seed}";

                    Assert.AreEqual(8, after.Routes.Count, what);
                    Assert.AreEqual(before.Routes.Count, after.Routes.Count, what);

                    for (var i = 0; i < after.Routes.Count; i++)
                    {
                        var b = before.Routes[i];
                        var a = after.Routes[i];

                        if (pattern == BallPatterns.RingOut)
                        {
                            Assert.AreEqual(0.0, Vector2.Distance(c, b.From), 1e-4, what + ": control, the default starts on the centre");
                            Assert.AreEqual(3.0, Vector2.Distance(c, a.From), 1e-3, what + " ball " + i + ": starts origin_clear out");
                            Assert.AreEqual(0.0, Vector2.Distance(b.To, a.To), 1e-4, what + ": same edge end");
                            Assert.AreEqual(1.0, Vector2.Dot(Vector2.Normalize(b.To - c), Vector2.Normalize(a.From - c)), 1e-4, what + ": on its own ray");
                        }
                        else
                        {
                            Assert.AreEqual(0.0, Vector2.Distance(c, b.To), 1e-4, what + ": control, the default converges on the centre");
                            Assert.AreEqual(3.0, Vector2.Distance(c, a.To), 1e-3, what + " ball " + i + ": stops origin_clear short");
                            Assert.AreEqual(0.0, Vector2.Distance(b.From, a.From), 1e-4, what + ": same edge start");
                            Assert.AreEqual(1.0, Vector2.Dot(Vector2.Normalize(b.From - c), Vector2.Normalize(a.To - c)), 1e-4, what + ": on its own ray");
                        }
                    }
                }
            }

            // ring-alt carries it into both of its directions
            var p = P("count=6 origin_clear=3", BallPatterns.RingAlt);
            Assert.IsTrue(BallPatterns.Plan(Pinnacle(), BallPatterns.RingAlt, p, new Random(4), 1).Routes.All(r => Math.Abs(Vector2.Distance(c, r.From) - 3f) < 1e-3f), "ring-alt wave 1 rolls out from 3 m");
            Assert.IsTrue(BallPatterns.Plan(Pinnacle(), BallPatterns.RingAlt, p, new Random(4), 2).Routes.All(r => Math.Abs(Vector2.Distance(c, r.To) - 3f) < 1e-3f), "ring-alt wave 2 rolls in to 3 m");
        }

        [TestMethod]
        public void OriginClear_Validate_RefusesAClearanceThatSwallowsARingRay_AndANonNumber()
        {
            // the Pinnacle's shortest ray from its centre is 29.2 / 2 - 2 (inset) = 12.6 m
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("origin_clear=11"), BallPatterns.RingOut));

            var refusal = BallRules.Validate(Pinnacle(), Merged("origin_clear=12"), BallPatterns.RingOut);
            StringAssert.Contains(refusal, "origin_clear=12");
            StringAssert.Contains(refusal, "lower origin_clear");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("origin_clear=12"), BallPatterns.RingIn), "origin_clear");

            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("origin_clear=wide"), BallPatterns.Fan), "origin_clear");
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("origin_clear=3"), BallPatterns.Fan));

            // the Grael fan with the clearance the wiring will pin validates at the head
            var head = GraelSettings.Defaults.P4FanArgs()
                .With("cx", GraelData.HeadPoint.X.ToString("R", CultureInfo.InvariantCulture))
                .With("cy", GraelData.HeadPoint.Y.ToString("R", CultureInfo.InvariantCulture))
                .With("origin_clear", "3");
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(head), BallPatterns.Fan));
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(GraelSettings.Defaults.P4RingArgs().With("origin_clear", "3")), BallPatterns.RingAlt));
        }

        // ---- no_overlap -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The no_overlap wall invariants on one wall: no two neighbours under one pitch (so never under 2 x reach); every
        /// neighbour pair is either one pitch (wall) or an opening of at least gap; exactly <paramref name="gaps"/> openings;
        /// the leftover the openings absorbed under one pitch in all; the ends on the lateral limits; the recorded opening
        /// centres midway between their flanks.
        /// </summary>
        private static void AssertNoOverlapWall(BallWave wave, BallSide side, BallParams p, int gaps, string what)
        {
            var (lo, hi) = BallPatterns.LateralRange(Pinnacle(), side, p);
            var laterals = Laterals(wave, side);
            var pitch = BallRules.NoOverlapPitch(p.Reach);
            var openings = new List<(float Left, float Right)>();

            Assert.AreEqual(lo, laterals.First(), 1e-3, what + ": the wall starts on its lateral limit");
            Assert.AreEqual(hi, laterals.Last(), 1e-3, what + ": and ends on the other");

            for (var i = 1; i < laterals.Count; i++)
            {
                var d = laterals[i] - laterals[i - 1];

                Assert.IsTrue(d >= 2f * p.Reach, $"{what}: lanes {i - 1} and {i} are {d:0.####} m apart, under 2 x reach {2f * p.Reach:0.###}");
                Assert.IsTrue(d >= pitch - 1e-3f, $"{what}: lanes {i - 1} and {i} are {d:0.####} m apart, under one pitch {pitch:0.###}");

                if (d > pitch + 1e-3f)
                    openings.Add((laterals[i - 1], laterals[i]));
                else
                    Assert.AreEqual(pitch, d, 1e-3, what + ": a wall neighbour is exactly one pitch");
            }

            Assert.AreEqual(gaps, openings.Count, what + ": openings in " + string.Join(", ", laterals.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))));
            Assert.IsTrue(openings.All(o => o.Right - o.Left >= p.Gap - 1e-3f), what + ": every opening at least gap wide");
            Assert.IsTrue(openings.Sum(o => o.Right - o.Left - p.Gap) < pitch + 1e-3f, what + ": the openings absorb under one pitch of leftover in all");

            var recorded = wave.Gaps.Where(g => g.Side == side).Select(g => g.Lateral).OrderBy(v => v).ToList();
            Assert.AreEqual(gaps, recorded.Count, what);

            for (var i = 0; i < gaps; i++)
                Assert.AreEqual((openings[i].Left + openings[i].Right) * 0.5f, recorded[i], 1e-3, what + ": the recorded opening is the one laid");
        }

        [TestMethod]
        public void NoOverlap_RowsGapsTwoGapSix_EverySide_ManySeeds_LanesNeverUnderTwoReach()
        {
            foreach (var side in Sides)
            {
                var args = "gaps=2 gap=6 no_overlap=1 dir=" + Dir(side);
                var p = P(args, BallPatterns.Row);

                Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(args), BallPatterns.Row), side + ": fits the Pinnacle");

                for (var seed = 1; seed <= 80; seed++)
                {
                    var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed));

                    AssertNoOverlapWall(wave, side, p, 2, $"{side} seed {seed}");
                    Assert.AreEqual(2, wave.Markers.Count, "a pillar on each opening");
                    Assert.AreEqual("a pillar marks the way through", wave.MarkerNote);
                    StringAssert.Contains(wave.Summary, "with 2 openings");
                }
            }

            // the Grael row series with the pins the wiring will add validates
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(GraelSettings.Defaults.P4RowArgs().With("no_overlap", "1").With("distinct_dirs", "1")), BallPatterns.Row));
        }

        [TestMethod]
        public void NoOverlap_RowsOneGap_EverySide_ManySeeds()
        {
            foreach (var side in Sides)
            {
                var p = P("no_overlap=1 dir=" + Dir(side), BallPatterns.Row);

                for (var seed = 1; seed <= 40; seed++)
                    AssertNoOverlapWall(BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed)), side, p, 1, $"{side} seed {seed}");
            }
        }

        [TestMethod]
        public void NoOverlap_Cross_BothWalls_ManySeeds_SafeSpotWhereTheLaidOpeningsCross()
        {
            foreach (var gaps in new[] { 1, 2 })
            {
                var args = $"gaps={gaps} no_overlap=1";
                var p = P(args, BallPatterns.Cross);

                Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(args), BallPatterns.Cross), "gaps " + gaps);

                for (var seed = 1; seed <= 80; seed++)
                {
                    var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Cross, p, new Random(seed));
                    var walls = wave.Gaps.Select(g => g.Side).Distinct().ToList();
                    var what = $"gaps {gaps} seed {seed}";

                    Assert.AreEqual(2, walls.Count, what + ": two walls");

                    foreach (var side in walls)
                        AssertNoOverlapWall(wave, side, p, gaps, what + " " + side);

                    var xs = wave.Gaps.Where(g => BallPatterns.LateralIsX(g.Side)).Select(g => g.Lateral).ToList();
                    var ys = wave.Gaps.Where(g => !BallPatterns.LateralIsX(g.Side)).Select(g => g.Lateral).ToList();

                    Assert.IsTrue(wave.SafePoint.HasValue, what);
                    Assert.IsTrue(xs.Any(x => Math.Abs(x - wave.SafePoint.Value.X) < 1e-3) && ys.Any(y => Math.Abs(y - wave.SafePoint.Value.Y) < 1e-3), what + ": the safe spot is a crossing of the openings as laid");
                    Assert.AreEqual(gaps * gaps, wave.Markers.Count, what + ": a pillar on every crossing (the Pinnacle has no exclusion)");
                }
            }

            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(GraelSettings.Defaults.P4CrossArgs().With("no_overlap", "1").With("distinct_dirs", "1").With("max_in_flight", "2")), BallPatterns.Cross), "the Grael cross with its pins validates");
        }

        [TestMethod]
        public void NoOverlap_OpeningsStillVary_OnTheLongWalls()
        {
            var p = P("gaps=2 gap=6 no_overlap=1 dir=n", BallPatterns.Row);
            var layouts = new HashSet<string>();

            for (var seed = 1; seed <= 60; seed++)
                layouts.Add(string.Join(",", BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed)).Gaps.Select(g => g.Lateral.ToString("0.0", CultureInfo.InvariantCulture))));

            Assert.IsTrue(layouts.Count >= 3, "the 36.9 m north wall still has several layouts: " + string.Join(" | ", layouts));
        }

        [TestMethod]
        public void NoOverlap_EastWestWall_GapsTwoGapSix_OpeningsMoveBetweenWaves_LanesNeverUnderTwoReach()
        {
            // the 26.9 m east/west walls fit exactly one pitch per stretch (floor((26.9 - 12) / 4.62) = 3 = n + 1), so only
            // the random split of the 1.04 m leftover moves the openings
            foreach (var side in new[] { BallSide.East, BallSide.West })
            {
                var p = P("gaps=2 gap=6 no_overlap=1 dir=" + Dir(side), BallPatterns.Row);
                var (lo, hi) = BallPatterns.LateralRange(Pinnacle(), side, p);
                var firsts = new HashSet<string>();
                var seconds = new HashSet<string>();

                Assert.AreEqual(26.9f, hi - lo, 1e-3, side + ": the wall this test is about");

                for (var seed = 1; seed <= 100; seed++)
                {
                    var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed));
                    var laterals = Laterals(wave, side);

                    for (var i = 1; i < laterals.Count; i++)
                        Assert.IsTrue(laterals[i] - laterals[i - 1] >= 2f * p.Reach, $"{side} seed {seed}: lanes {i - 1} and {i} are {laterals[i] - laterals[i - 1]:0.####} m apart");

                    AssertNoOverlapWall(wave, side, p, 2, $"{side} seed {seed}");

                    var gaps = wave.Gaps.Select(g => g.Lateral).OrderBy(v => v).ToList();
                    firsts.Add(gaps[0].ToString("0.00", CultureInfo.InvariantCulture));
                    seconds.Add(gaps[1].ToString("0.00", CultureInfo.InvariantCulture));
                }

                Assert.IsTrue(firsts.Count > 1, side + ": the first opening takes more than one position: " + string.Join(", ", firsts));
                Assert.IsTrue(seconds.Count > 1, side + ": the second opening takes more than one position: " + string.Join(", ", seconds));
            }

            // the even split (no rng: what Validate counts on) is the fixed layout the random one moves around
            var even = BallPatterns.NoOverlapWall(0f, 26.9f, new[] { 8f, 19f }, 6f, 2.3f);
            var drawn = BallPatterns.NoOverlapWall(0f, 26.9f, new[] { 8f, 19f }, 6f, 2.3f, new Random(3));

            Assert.AreEqual(even.Positions.Count, drawn.Positions.Count, "the split moves the openings, never the ball count");
        }

        [TestMethod]
        public void NoOverlapWall_StretchesAreWholePitches_TheLeftoverWidensTheOpeningsEvenly()
        {
            // 30 m, two 6 m openings, reach 2.3: pitch 4.62, 18 m of wall = floor(18 / 4.62) = 3 pitches (13.86 m), 4.14 m left over
            var (positions, openings) = BallPatterns.NoOverlapWall(0f, 30f, new[] { 8f, 20f }, 6f, 2.3f);
            var pitch = BallRules.NoOverlapPitch(2.3f);

            Assert.AreEqual(4.62f, pitch, 1e-5);
            Assert.AreEqual(0f, positions.First());
            Assert.AreEqual(30f, positions.Last());
            Assert.AreEqual(2, openings.Count);

            // every opening is widened by the same amount: (18 - 3 x 4.62) / 2 = 2.07
            var widths = new List<float>();

            for (var i = 1; i < positions.Count; i++)
            {
                var d = positions[i] - positions[i - 1];

                if (d > pitch + 1e-3f)
                    widths.Add(d);
                else
                    Assert.AreEqual(pitch, d, 1e-3);
            }

            Assert.AreEqual(2, widths.Count);
            Assert.AreEqual(6f + 2.07f, widths[0], 1e-3);
            Assert.AreEqual(widths[0], widths[1], 1e-4, "evenly");
            Assert.AreEqual(3 + 3, positions.Count, "3 pitches over 3 stretches: 6 balls");
        }

        [TestMethod]
        public void NoOverlap_Validate_RefusesSpansThatCannotFit_WhereTheOldPitchStillDid()
        {
            // east-west Pinnacle walls span 26.9 m. gaps=2: 2 x 6.54 + 3 x 4.62 = 26.94 > 26.9, while 2 x 6.54 + 3 x 4.6 = 26.88 fits
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("gaps=2 gap=6.54 dir=e"), BallPatterns.Row), "control: the old ball-width wall fits");

            var refusal = BallRules.Validate(Pinnacle(), Merged("gaps=2 gap=6.54 dir=e no_overlap=1"), BallPatterns.Row);
            StringAssert.Contains(refusal, "no_overlap=1");
            StringAssert.Contains(refusal, "do not fit the east side");
            StringAssert.Contains(refusal, "lower gaps or gap");

            // one opening: 18 + 2 x 4.62 = 27.24 > 26.9, while 18 + 4.6 = 22.6 fits
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("gap=18 dir=w"), BallPatterns.Row), "control");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("gap=18 dir=w no_overlap=1"), BallPatterns.Row), "no_overlap=1");

            // a cross checks both walls (dirs=random checks every side)
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("gap=18"), BallPatterns.Cross), "control");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("gap=18 no_overlap=1"), BallPatterns.Cross), "no_overlap=1");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("gaps=3 no_overlap=1"), BallPatterns.Cross), "do not fit");

            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("no_overlap=maybe"), BallPatterns.Row), "no_overlap=");
        }

        // ---- distinct_dirs ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void DistinctDirs_Validate_Refusals()
        {
            const string rows = "wave_every=3 waves=6 max_in_flight=2 max_in_flight_end=3 distinct_dirs=1";
            const string cross = "wave_every=3 waves=6 max_in_flight=2 distinct_dirs=1";

            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(rows), BallPatterns.Row), "rows 2 -> 3 is allowed");
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(cross), BallPatterns.Cross), "cross 2 is allowed");

            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged(rows + " dir=n"), BallPatterns.Row), "needs dir=random");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged(cross + " dirs=ne"), BallPatterns.Cross), "needs dirs=random");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=3 max_in_flight=4 distinct_dirs=1"), BallPatterns.Row), "up to 3");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=3 waves=6 max_in_flight=2 max_in_flight_end=4 distinct_dirs=1"), BallPatterns.Row), "up to 3", "a ramp past the limit is refused");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=3 max_in_flight=3 distinct_dirs=1"), BallPatterns.Cross), "up to 2");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=3 distinct_dirs=1"), BallPatterns.Row), "needs wave_every and max_in_flight");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("distinct_dirs=1"), BallPatterns.Cross), "needs wave_every and max_in_flight");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=3 max_in_flight=2 distinct_dirs=1"), BallPatterns.RingOut), "only works with the row and cross");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("distinct_dirs=sometimes"), BallPatterns.Row), "distinct_dirs=");

            // off, none of this is checked
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("wave_every=3 max_in_flight=4 distinct_dirs=0 dir=n"), BallPatterns.Row));
        }

        [TestMethod]
        public void DistinctDirs_Row_DrawsUniformlyFromTheFreeWalls_AndWithNoneBusy_ExactlyAsBefore()
        {
            var p = P("distinct_dirs=1", BallPatterns.Row);
            var counts = new Dictionary<BallSide, int>();

            for (var seed = 1; seed <= 3000; seed++)
            {
                var side = BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed), 1, new[] { BallSide.East }).Gaps[0].Side;
                counts[side] = counts.TryGetValue(side, out var c) ? c + 1 : 1;
            }

            Assert.IsFalse(counts.ContainsKey(BallSide.East), "never the busy wall");
            Assert.AreEqual(3, counts.Count);
            Assert.IsTrue(counts.Values.All(c => c > 850 && c < 1150), "about a third each: " + string.Join(", ", counts.Select(kv => kv.Key + "=" + kv.Value)));

            for (var seed = 1; seed <= 30; seed++)
            {
                var rngA = new Random(seed);
                var rngB = new Random(seed);

                Assert.AreEqual(Fingerprint(BallPatterns.Plan(Pinnacle(), BallPatterns.Row, P("", BallPatterns.Row), rngA)), Fingerprint(BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, rngB, 1, Array.Empty<BallSide>())), "seed " + seed);
                Assert.AreEqual(rngA.Next(), rngB.Next());
            }

            // three busy: the last one
            Assert.AreEqual(BallSide.West, BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(7), 1, new[] { BallSide.North, BallSide.East, BallSide.South }).Gaps[0].Side);
        }

        [TestMethod]
        public void DistinctDirs_Cross_TakesTheFreeWallOfEachAxis()
        {
            var p = P("distinct_dirs=1", BallPatterns.Cross);

            for (var seed = 1; seed <= 50; seed++)
            {
                var walls = BallPatterns.Plan(Pinnacle(), BallPatterns.Cross, p, new Random(seed), 1, new[] { BallSide.North, BallSide.West }).Gaps.Select(g => g.Side).Distinct().OrderBy(s => s).ToList();

                CollectionAssert.AreEqual(new[] { BallSide.East, BallSide.South }, walls, "seed " + seed);
            }
        }

        [TestMethod]
        public void DistinctDirs_Cadence_HoldsAWaveWhileNoWallIsFree_ThenLaunchesItWhenOneFrees()
        {
            // uncapped (Validate would refuse this; the pure clock is what holds): a cross needs a free wall on each axis
            var p = P("waves=4 wave_every=1 distinct_dirs=1", BallPatterns.Cross);
            var cadence = new BallCadence(p, BallPatterns.Cross);

            Assert.IsTrue(cadence.Distinct);
            CollectionAssert.AreEqual(new[] { 1 }, cadence.Due(0.0).ToArray());
            cadence.Open(1, 1, new[] { BallSide.North, BallSide.East }).OnSpawned(1);
            CollectionAssert.AreEqual(new[] { 2 }, cadence.Due(1.0).ToArray());
            cadence.Open(2, 1, new[] { BallSide.South, BallSide.West }).OnSpawned(1);

            Assert.AreEqual(0, cadence.Due(2.0).Count, "every wall busy: wave 3 waits");
            Assert.IsTrue(cadence.Held);
            Assert.IsTrue(cadence.HeldForWalls);
            Assert.AreEqual(0, cadence.Due(9.0).Count, "however late it gets");

            cadence.ClockOf(1).OnLegEnd(1);
            Assert.IsTrue(cadence.Close(1).HasValue);
            CollectionAssert.AreEqual(new[] { BallSide.South, BallSide.West }, cadence.BusyWalls.OrderBy(s => s).ToArray(), "wave 1's walls are free again");

            CollectionAssert.AreEqual(new[] { 3 }, cadence.Due(9.1).ToArray(), "a wall freed: wave 3 launches at once, and only wave 3");
            Assert.IsTrue(cadence.HeldForWalls, "wave 4 (due too) now waits on wave 3's walls");

            var plan = BallPatterns.Plan(Pinnacle(), BallPatterns.Cross, BallRules.ForWave(p, 3), new Random(1), 3, cadence.BusyWalls);
            CollectionAssert.AreEqual(new[] { BallSide.North, BallSide.East }, plan.Gaps.Select(g => g.Side).Distinct().OrderBy(s => s).ToArray(), "and takes the walls that freed");

            // a row: four walls, so the fifth wave waits
            var rows = new BallCadence(P("waves=5 wave_every=1 distinct_dirs=1", BallPatterns.Row), BallPatterns.Row);

            for (var w = 1; w <= 4; w++)
            {
                CollectionAssert.AreEqual(new[] { w }, rows.Due(w - 1).ToArray(), "row wave " + w);
                rows.Open(w, 1, new[] { Sides[w - 1] });
            }

            Assert.AreEqual(0, rows.Due(4.0).Count);
            Assert.IsTrue(rows.HeldForWalls);

            // two due in one call each count against the walls before either is opened
            var burst = new BallCadence(P("waves=4 wave_every=1 distinct_dirs=1", BallPatterns.Cross), BallPatterns.Cross);
            CollectionAssert.AreEqual(new[] { 1, 2 }, burst.Due(50.0).ToArray(), "a cross has walls for two at once, not three");
            Assert.IsTrue(burst.HeldForWalls);

            // off (or another pattern), walls never hold anything
            var off = new BallCadence(P("waves=4 wave_every=1", BallPatterns.Cross), BallPatterns.Cross);
            Assert.IsFalse(off.Distinct);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, off.Due(50.0).ToArray());
            Assert.IsFalse(new BallCadence(P("waves=4 wave_every=1 distinct_dirs=1", BallPatterns.RingOut), BallPatterns.RingOut).Distinct);
        }

        /// <summary>
        /// A cadence run on the pure clock with REAL wave lifetimes, exactly as the mechanic sequences it: Due, then each
        /// wave planned with the open waves' busy walls and opened with its own walls; a wave lives its ideal time
        /// (its lead plus its slowest lane, BallRules.IdealSeconds), then closes. Every launch is checked against the walls
        /// of the waves still in flight. Returns the launches and how many Due calls held for walls.
        /// </summary>
        private int Shares;

        private (int Launches, int MaxInFlight, int WallHolds) SimulateDistinct(BallParams p, string pattern, int seed, bool assertDistinct = true)
        {
            var region = Pinnacle();
            var cadence = new BallCadence(p, pattern);
            var rng = new Random(seed);
            var closesAt = new Dictionary<int, double>();
            var wallsOf = new Dictionary<int, BallSide[]>();
            var launches = 0;
            var maxInFlight = 0;
            var wallHolds = 0;

            for (var t = 0.0; t < 900.0 && !(cadence.AllLaunched && cadence.InFlight == 0); t += 0.1)
            {
                foreach (var w in closesAt.Where(kv => kv.Value <= t + 1e-9).Select(kv => kv.Key).ToList())
                {
                    var clock = cadence.ClockOf(w);

                    foreach (var id in clock.Unresolved())
                        clock.OnLegEnd(id);

                    Assert.IsTrue(cadence.Close(w).HasValue);
                    closesAt.Remove(w);
                    wallsOf.Remove(w);
                }

                foreach (var w in cadence.Due(t))
                {
                    var wp = BallRules.ForWave(p, w);
                    var plan = BallPatterns.Plan(region, pattern, wp, rng, w, cadence.BusyWalls);
                    var walls = plan.Gaps.Select(g => g.Side).Distinct().ToArray();
                    var others = wallsOf.Values.SelectMany(x => x).ToList();

                    Assert.AreEqual(pattern == BallPatterns.Cross ? 2 : 1, walls.Length, $"seed {seed} wave {w}");
                    if (walls.Any(others.Contains))
                    {
                        Shares++;

                        if (assertDistinct)
                            Assert.Fail($"seed {seed} wave {w} at t={t:0.0}: walls {string.Join(",", walls)} shared with in-flight {string.Join(",", others)}");
                    }

                    var clock = cadence.Open(w, plan.Routes.Count, walls);

                    for (var i = 0; i < plan.Routes.Count; i++)
                    {
                        clock.OnSpawned(i);
                        clock.OnLaunched(i);
                    }

                    wallsOf[w] = walls;
                    closesAt[w] = t + BallRules.IdealSeconds(plan, wp.Speed, BallRules.LeadFor(region, pattern, wp, null));
                    launches++;
                    maxInFlight = Math.Max(maxInFlight, cadence.InFlight);
                }

                if (cadence.HeldForWalls)
                    wallHolds++;
            }

            return (launches, maxInFlight, wallHolds);
        }

        [TestMethod]
        public void DistinctDirs_GraelRows_ManySeeds_NoTwoInFlightShareAWall()
        {
            var p = BallRules.Read(Merged(GraelSettings.Defaults.P4RowArgs().With("no_overlap", "1").With("distinct_dirs", "1")), BallPatterns.Row);
            var reachedThree = false;

            Assert.AreEqual(2, p.MaxInFlight);
            Assert.AreEqual(3.0, p.MaxInFlightEnd);

            for (var seed = 1; seed <= 60; seed++)
            {
                var (launches, maxInFlight, wallHolds) = SimulateDistinct(p, BallPatterns.Row, seed);

                Assert.AreEqual(p.Waves, launches, "seed " + seed + ": every wave launched");
                Assert.IsTrue(maxInFlight <= 3);
                Assert.AreEqual(0, wallHolds, "seed " + seed + ": under the 3 cap a row always finds a free wall");
                reachedThree |= maxInFlight == 3;
            }

            Assert.IsTrue(reachedThree, "control: three rows really are in flight together at the end");

            // control: the same series without distinct_dirs does put two in-flight rows on one wall, so the check can fail
            var off = BallRules.Read(Merged(GraelSettings.Defaults.P4RowArgs().With("no_overlap", "1").With("distinct_dirs", "0")), BallPatterns.Row);   // the Grael rows pin distinct_dirs=1 since wave 3: switched off for the control
            Shares = 0;

            for (var seed = 1; seed <= 60; seed++)
                SimulateDistinct(off, BallPatterns.Row, seed, assertDistinct: false);

            Assert.IsTrue(Shares > 0, "without distinct_dirs some rows share a wall");
        }

        [TestMethod]
        public void DistinctDirs_GraelCross_MaxInFlightTwo_ManySeeds_NoTwoInFlightShareAWall()
        {
            var p = BallRules.Read(Merged(GraelSettings.Defaults.P4CrossArgs().With("no_overlap", "1").With("distinct_dirs", "1").With("max_in_flight", "2")), BallPatterns.Cross);
            var reachedTwo = false;

            for (var seed = 1; seed <= 60; seed++)
            {
                var (launches, maxInFlight, wallHolds) = SimulateDistinct(p, BallPatterns.Cross, seed);

                Assert.AreEqual(p.Waves, launches, "seed " + seed);
                Assert.IsTrue(maxInFlight <= 2);
                Assert.AreEqual(0, wallHolds, "seed " + seed);
                reachedTwo |= maxInFlight == 2;
            }

            Assert.IsTrue(reachedTwo, "control: two crosses really overlap");

            var off = BallRules.Read(Merged(GraelSettings.Defaults.P4CrossArgs().With("no_overlap", "1").With("max_in_flight", "2").With("distinct_dirs", "0")), BallPatterns.Cross);   // switched off for the control (pinned on since wave 3)
            Shares = 0;

            for (var seed = 1; seed <= 60; seed++)
                SimulateDistinct(off, BallPatterns.Cross, seed, assertDistinct: false);

            Assert.IsTrue(Shares > 0, "control: without distinct_dirs some crosses share a wall");
        }

        [TestMethod]
        public void DistinctDirs_Uncapped_ManySeeds_TheWallsAreTheCap_HeldLaunchesStillNeverShare()
        {
            // no max_in_flight (Validate refuses it, so the hold is the only guard left): a tight cadence must hold for walls
            foreach (var (pattern, cap) in new[] { (BallPatterns.Row, 4), (BallPatterns.Cross, 2) })
            {
                var p = P("waves=8 wave_every=0.5 distinct_dirs=1", pattern);
                var held = 0;

                for (var seed = 1; seed <= 30; seed++)
                {
                    var (launches, maxInFlight, wallHolds) = SimulateDistinct(p, pattern, seed);

                    Assert.AreEqual(8, launches, $"{pattern} seed {seed}: every held wave still launched once a wall freed");
                    Assert.IsTrue(maxInFlight <= cap, $"{pattern} seed {seed}: {maxInFlight} in flight");
                    held += wallHolds;
                }

                Assert.IsTrue(held > 0, pattern + ": control, the walls really did hold launches");
            }
        }

        [TestMethod]
        public void DistinctDirs_ThroughTheMechanic_StartsAndRunsItsCadence()
        {
            // registry-only run (no landblock: every spawn refused), as BallCadenceRunTests: the distinct path plans,
            // opens and closes wave 1 through RollingBallMechanic without throwing
            var logs = new List<string>();
            var run = HazardManager.RegisterForTest("balls", new RollingBallMechanic(), Pinnacle(), new NullHost(), DateTime.UtcNow, BallPatterns.Cross,
                Merged("waves=3 wave_every=1 max_in_flight=2 distinct_dirs=1 no_overlap=1"));

            try
            {
                run.Context.LogForTest = logs.Add;

                for (var i = 0; i < 10 && !run.IsEnded; i++)
                    HazardManager.TickRun(run, DateTime.UtcNow);

                Assert.IsTrue(logs.Any(l => l.StartsWith("balls cadence: launches at", StringComparison.Ordinal)), string.Join(" | ", logs));
                Assert.IsTrue(run.IsEnded);
                Assert.AreEqual("all balls lost in wave 1/3", run.EndReason, "the same verdict as without the args (no landblock)");
            }
            finally
            {
                HazardManager.Stop(run, HazardOutcome.Stopped, "test cleanup");
            }
        }
    }
}

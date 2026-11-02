using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Grael;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Round 3 playtest: the balls' opt-in "gaps" arg (several openings per row or cross wall) and Grael's wider fan.
    /// Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class BallGapsTests
    {
        private static readonly BallSide[] Sides = { BallSide.North, BallSide.East, BallSide.South, BallSide.West };

        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern) => BallRules.Read(Merged(args), pattern);

        private static string Dir(BallSide s) => BallPatterns.Name(s).Substring(0, 1);

        /// <summary>The lateral coordinate of each ball of one wall, ascending.</summary>
        private static List<float> Laterals(BallWave wave, BallSide side)
            => wave.Routes.Where(r => r.Tag == "row-" + BallPatterns.Name(side))
                .Select(r => BallPatterns.LateralIsX(side) ? r.From.X : r.From.Y)
                .OrderBy(v => v).ToList();

        /// <summary>The openings a player can pass: neighbouring balls more than 2 x reach apart, as (left flank, right flank).</summary>
        private static List<(float Left, float Right)> Openings(List<float> laterals, float reach)
        {
            var list = new List<(float, float)>();

            for (var i = 1; i < laterals.Count; i++)
                if (laterals[i] - laterals[i - 1] > 2f * reach + 1e-3f)
                    list.Add((laterals[i - 1], laterals[i]));

            return list;
        }

        private static string Fingerprint(BallWave w)
            => string.Join(";", w.Routes.Select(r => FormattableString.Invariant($"{r.From.X:0.###},{r.From.Y:0.###}>{r.To.X:0.###},{r.To.Y:0.###}")))
               + "|" + string.Join(";", w.Gaps.Select(g => FormattableString.Invariant($"{g.Side}:{g.Lateral:0.###}")))
               + "|" + string.Join(";", w.Markers.Select(m => FormattableString.Invariant($"{m.X:0.###},{m.Y:0.###}")))
               + "|" + w.Summary;

        // ---- gaps=1 is today's plan ------------------------------------------------------------------------------------

        [TestMethod]
        public void GapsOne_IsTodaysPlan_ForRowAndCross_DrawForDraw()
        {
            foreach (var pattern in new[] { BallPatterns.Row, BallPatterns.Cross })
            {
                for (var seed = 1; seed <= 40; seed++)
                {
                    var rngA = new Random(seed);
                    var rngB = new Random(seed);
                    var today = BallPatterns.Plan(Pinnacle(), pattern, P("", pattern), rngA);
                    var one = BallPatterns.Plan(Pinnacle(), pattern, P("gaps=1", pattern), rngB);

                    Assert.AreEqual(Fingerprint(today), Fingerprint(one), $"{pattern} seed {seed}");
                    Assert.AreEqual(rngA.Next(), rngB.Next(), $"{pattern} seed {seed}: gaps=1 draws exactly what today's plan draws");
                }
            }

            Assert.AreEqual(1, P("", BallPatterns.Row).Gaps, "the default is one opening");
            var row = P("", BallPatterns.Row);

            Assert.AreEqual(row.Gap + 2f * row.Reach, BallRules.WallNeed(row), 1e-4, "one opening needs what it always did: gap + one ball-width");
        }

        [TestMethod]
        public void GapsOne_EqualsTheParentCommitsPlans_Golden()
        {
            // review F4: strings captured from 89e36f9f9, the commit before "gaps" existed (BallGapsParentFingerprints)
            Assert.AreEqual(10, BallGapsParentFingerprints.Plans.Length);

            foreach (var (pattern, seed, golden) in BallGapsParentFingerprints.Plans)
            {
                Assert.AreEqual(golden, Fingerprint(BallPatterns.Plan(Pinnacle(), pattern, P("", pattern), new Random(seed))), $"{pattern} seed {seed}: today's default plan");
                Assert.AreEqual(golden, Fingerprint(BallPatterns.Plan(Pinnacle(), pattern, P("gaps=1", pattern), new Random(seed))), $"{pattern} seed {seed}: gaps=1");
            }
        }

        [TestMethod]
        public void GraelRows_EastWestWall_FirstOpeningSpread_Over60Seeds()
        {
            // review F1: an east-west Pinnacle wall has little slack for two 6 m openings; report how far the first one moves
            // a fixed wall needs distinct_dirs off (the Grael rows pin it on since wave 3, and it requires dir=random)
            var args = HazardArgs.Merge(Pinnacle_GraelRows().With("dir", "e").With("distinct_dirs", "0"), HazardArgs.Parse(HazardDefaults.Balls));
            var p = BallRules.Read(args, BallPatterns.Row);
            var (lo, hi) = BallPatterns.LateralRange(Pinnacle(), BallSide.East, p);
            var slack = (hi - lo) - BallRules.WallNeed(p);
            var firsts = new List<float>();

            Assert.AreEqual(2, p.Gaps);
            Assert.IsNull(BallRules.Validate(Pinnacle(), args, BallPatterns.Row));

            for (var seed = 1; seed <= 60; seed++)
                firsts.Add(BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed)).Gaps.Min(g => g.Lateral));

            var spread = firsts.Max() - firsts.Min();

            Console.WriteLine(FormattableString.Invariant($"GRAEL ROWS EAST: span {hi - lo:0.###} m, need {BallRules.WallNeed(p):0.###} m, slack {slack:0.###} m; first opening centre {firsts.Min():0.###}..{firsts.Max():0.###} (spread {spread:0.###} m) over 60 seeds"));

            Assert.IsTrue(spread > 0f, "the first opening does move");
            Assert.IsTrue(spread <= slack + 1e-3f, "and never by more than the slack");
        }

        private static HazardArgs Pinnacle_GraelRows() => GraelSettings.Defaults.P4RowArgs();

        [TestMethod]
        public void GapsOne_RowWall_HasExactlyOneOpening()
        {
            var p = P("gaps=1 dir=n", BallPatterns.Row);
            var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(3));

            Assert.AreEqual(1, wave.Gaps.Count);
            Assert.AreEqual(1, Openings(Laterals(wave, BallSide.North), p.Reach).Count);
        }

        // ---- gaps=2 ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GapsTwo_EveryRowWall_HasTwoOpenings_EachAtLeastMinGap_NotOverlapping_WithWallBetweenAndAtTheEnds()
        {
            foreach (var side in Sides)
            {
                var p = P("gaps=2 dir=" + Dir(side), BallPatterns.Row);
                var w = BallRules.MinGap(p);
                var (lo, hi) = BallPatterns.LateralRange(Pinnacle(), side, p);

                Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("gaps=2 dir=" + Dir(side)), BallPatterns.Row), side + ": two openings fit the Pinnacle");

                for (var seed = 1; seed <= 60; seed++)
                {
                    var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed));
                    var laterals = Laterals(wave, side);
                    var openings = Openings(laterals, p.Reach);
                    var what = $"{side} seed {seed}";

                    Assert.AreEqual(2, wave.Gaps.Count, what + ": two openings recorded");
                    Assert.AreEqual(2, openings.Count, what + ": exactly two passable openings in the wall: " + string.Join(", ", laterals.Select(v => v.ToString("0.##"))));

                    foreach (var (left, right) in openings)
                    {
                        Assert.AreEqual(p.Gap, right - left, 1e-3, what + ": each opening is gap wide between its flank balls");
                        Assert.IsTrue(right - left >= w - 1e-3, what + ": each opening is at least MinGap");
                    }

                    // not overlapping, a ball-width of wall between them and at both ends
                    Assert.IsTrue(openings[1].Left - openings[0].Right >= 2f * p.Reach - 1e-3, what + ": a ball-width of wall between the openings");
                    Assert.IsTrue(openings[0].Left - lo >= 2f * p.Reach - 1e-3, what + ": a ball-width of wall at the low end");
                    Assert.IsTrue(hi - openings[1].Right >= 2f * p.Reach - 1e-3, what + ": a ball-width of wall at the high end");

                    // the rest of the wall is solid: nothing else a player could slip through, and it reaches both limits
                    Assert.AreEqual(lo, laterals.First(), 1e-3, what);
                    Assert.AreEqual(hi, laterals.Last(), 1e-3, what);
                }
            }
        }

        [TestMethod]
        public void GapsTwo_OpeningsVaryAcrossSeeds()
        {
            var p = P("gaps=2 dir=n", BallPatterns.Row);
            var layouts = new HashSet<string>();
            var firsts = new HashSet<string>();

            for (var seed = 1; seed <= 40; seed++)
            {
                var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Row, p, new Random(seed));
                layouts.Add(string.Join(",", wave.Gaps.Select(g => g.Lateral.ToString("0.0"))));
                firsts.Add(wave.Gaps[0].Lateral.ToString("0.0"));
            }

            Assert.IsTrue(layouts.Count >= 30, "40 seeds give many different layouts: " + layouts.Count);
            Assert.IsTrue(firsts.Count >= 10, "the first opening moves too, not only the second: " + firsts.Count);
        }

        [TestMethod]
        public void GapCentres_AreInOrder_AndCoverTheSlack()
        {
            var lo = 0f;
            var hi = 30f;
            var gap = 6f;
            var reach = 2.3f;
            var slack = hi - lo - 2 * gap - 3 * 2f * reach;
            var min = float.MaxValue;
            var max = float.MinValue;

            for (var seed = 1; seed <= 300; seed++)
            {
                var c = BallPatterns.GapCentres(lo, hi, 2, gap, reach, new Random(seed));

                Assert.AreEqual(2, c.Count);
                Assert.IsTrue(c[1] - c[0] >= gap + 2f * reach - 1e-4, "never overlapping");
                Assert.IsTrue(c[0] - gap / 2 >= lo + 2f * reach - 1e-4 && c[1] + gap / 2 <= hi - 2f * reach + 1e-4, "inside the end walls");

                min = Math.Min(min, c[0] - gap / 2 - (lo + 2f * reach));
                max = Math.Max(max, c[0] - gap / 2 - (lo + 2f * reach));
            }

            Assert.IsTrue(min < 0.1f * slack && max > 0.7f * slack, $"the first opening ranges over its slack {slack:0.##}: {min:0.##}..{max:0.##}");
        }

        [TestMethod]
        public void GapsTwo_Validate_RefusesWhatDoesNotFit_WithAClearMessage()
        {
            // the Pinnacle's east-west walls span 26.9 m: 2 x 12 + 3 x 4.6 = 37.8 m does not fit
            var refusal = BallRules.Validate(Pinnacle(), Merged("gaps=2 gap=12 dir=e"), BallPatterns.Row);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "gaps=2");
            StringAssert.Contains(refusal, "do not fit the east side");
            StringAssert.Contains(refusal, "lower gaps or gap");

            // the same gap with ONE opening fits (control: it is the second opening that does not)
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("gap=12 dir=e"), BallPatterns.Row));

            // a cross checks both of its walls the same way
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("gaps=4 dirs=ne"), BallPatterns.Cross), "do not fit");

            // and the arg is a whole number
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("gaps=two"), BallPatterns.Row), "gaps");
        }

        [TestMethod]
        public void GapsTwo_FairLead_NeverAboveTheOneOpeningFloor()
        {
            foreach (var side in Sides)
            {
                var one = BallRules.FairLead(Pinnacle(), BallPatterns.Row, P("dir=" + Dir(side), BallPatterns.Row));
                var two = BallRules.FairLead(Pinnacle(), BallPatterns.Row, P("gaps=2 dir=" + Dir(side), BallPatterns.Row));

                Assert.IsTrue(two <= one + 1e-9, $"{side}: {two} vs {one}");
            }

            var crossOne = BallRules.FairLead(Pinnacle(), BallPatterns.Cross, P("", BallPatterns.Cross));
            var crossTwo = BallRules.FairLead(Pinnacle(), BallPatterns.Cross, P("gaps=2", BallPatterns.Cross));

            Assert.IsTrue(crossTwo <= crossOne + 1e-9);
        }

        [TestMethod]
        public void GapsTwo_Cross_BothWallsHaveTwoOpenings_AndTheSafeSpotIsACrossing()
        {
            var p = P("gaps=2", BallPatterns.Cross);

            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("gaps=2"), BallPatterns.Cross), "two openings per wall fit a Pinnacle cross");

            for (var seed = 1; seed <= 30; seed++)
            {
                var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Cross, p, new Random(seed));
                var bySide = wave.Gaps.GroupBy(g => g.Side).ToList();

                Assert.AreEqual(2, bySide.Count, "two walls");
                Assert.IsTrue(bySide.All(g => g.Count() == 2), "two openings each");

                var xs = wave.Gaps.Where(g => BallPatterns.LateralIsX(g.Side)).Select(g => g.Lateral).ToList();
                var ys = wave.Gaps.Where(g => !BallPatterns.LateralIsX(g.Side)).Select(g => g.Lateral).ToList();

                Assert.IsTrue(wave.SafePoint.HasValue);
                Assert.IsTrue(xs.Any(x => Math.Abs(x - wave.SafePoint.Value.X) < 1e-3) && ys.Any(y => Math.Abs(y - wave.SafePoint.Value.Y) < 1e-3), "the safe spot is where an x opening meets a y opening");

                foreach (var side in bySide.Select(g => g.Key))
                    Assert.AreEqual(2, Openings(Laterals(wave, side), p.Reach).Count, $"seed {seed} {side}");
            }
        }

        [TestMethod]
        public void Gaps_IsClamped()
        {
            Assert.AreEqual(1, P("gaps=0", BallPatterns.Row).Gaps);
            Assert.AreEqual(HazardDefaults.BallsMaxGaps, P("gaps=99", BallPatterns.Row).Gaps);
            Assert.AreEqual(3, BallRules.ForWave(P("gaps=3", BallPatterns.Row), 2).Gaps, "copied into every wave");
        }

        // ---- the fan ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GraelFan_Is90Degrees_ThreeBalls_AndValidatesOnThePinnacle()
        {
            var s = GraelSettings.Defaults;
            var head = s.P4BallArgs()
                .With("cx", GraelData.HeadPoint.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .With("cy", GraelData.HeadPoint.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            var merged = HazardArgs.Merge(head, HazardArgs.Parse(HazardDefaults.Balls));
            var p = BallRules.Read(merged, BallPatterns.Fan);

            Assert.AreEqual(90f, p.Spread);
            Assert.AreEqual(3, p.Count, "count stays 3");
            Assert.IsNull(BallRules.Validate(Pinnacle(), merged, BallPatterns.Fan), "the 90 degree fan validates at the arena's extent");

            var angles = BallPatterns.FanAngles(0.0, p.Count, p.Spread);

            Assert.AreEqual(3, angles.Length);
            Assert.AreEqual(-Math.PI / 4, angles[0], 1e-9, "the outer balls are 45 degrees either side of the aim");
            Assert.AreEqual(0.0, angles[1], 1e-9);
            Assert.AreEqual(Math.PI / 4, angles[2], 1e-9);

            // wider than before: the outer balls of a planned volley end farther apart
            var wide = BallPatterns.PlanFanAlong(Pinnacle(), p, 0.0).Routes;
            var narrow = BallPatterns.PlanFanAlong(Pinnacle(), BallRules.Read(HazardArgs.Merge(head.With("spread", "30"), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Fan), 0.0).Routes;

            Assert.AreEqual(3, wide.Count);
            Assert.IsTrue(Math.Abs(wide[2].To.Y - wide[0].To.Y) > 2f * Math.Abs(narrow[2].To.Y - narrow[0].To.Y), "a significantly wider W");

            // the sidestep lead rises with the spread (the fan is aimed, so the escape is sideways)
            Assert.IsTrue(BallRules.FanLead(90, 20, p.Reach, p.EscapeSpeed) > BallRules.FanLead(30, 20, p.Reach, p.EscapeSpeed));
        }

        [TestMethod]
        public void Fan_TheMaxSpreadAlsoValidatesOnThePinnacle()
        {
            // the spread clamp is 120; on the Pinnacle even that fits under the 30 s lead cap, so 90 needed no fallback
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("spread=120"), BallPatterns.Fan));
            Assert.AreEqual(120f, P("spread=500", BallPatterns.Fan).Spread, "clamped at 120");
        }
    }
}

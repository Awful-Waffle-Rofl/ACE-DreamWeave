using System;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The band-standard health curve. Pure arithmetic with no PropertyManager read (a read throws under this
    /// harness), so everything here is asserted against the exact formula rather than through a plan.
    ///
    /// The two properties worth the most are the EXACT endpoints - the curve has to reproduce the two measured
    /// medians it was fitted to, or the anchors stop being measurements - and strict monotonicity, which is the
    /// whole reason the feature exists. The internals (the anchor sanitizers) are visible via
    /// InternalsVisibleTo in ACE.Server.csproj:15.
    /// </summary>
    [TestClass]
    public class DungeonHealthCurveTests
    {
        private const double Low = DungeonHealthCurve.DefaultAnchorLow;
        private const double High = DungeonHealthCurve.DefaultAnchorHigh;

        private static double T(int level) => DungeonHealthCurve.Target(level, Low, High);

        [TestMethod]
        public void Both_anchors_are_reproduced_exactly()
        {
            // EXACTLY, not within a tolerance. 890 and 13100 are the measured cross-family band medians at 185
            // and 375; a curve that passed near them rather than through them would make the anchors a fit
            // rather than the two data points they are.
            Assert.AreEqual(Low, T(DungeonHealthCurve.AnchorLowLevel));
            Assert.AreEqual(High, T(DungeonHealthCurve.AnchorHighLevel));
        }

        [TestMethod]
        public void The_curve_is_strictly_monotone_across_the_whole_ladder()
        {
            for (var level = 1; level < DungeonGemSpec.MaxRunLevel; level++)
                Assert.IsTrue(T(level) < T(level + 1), $"T({level}) = {T(level)} is not below T({level + 1}) = {T(level + 1)}");
        }

        [TestMethod]
        public void The_per_level_ratio_is_the_same_everywhere_and_is_derived_from_the_anchors()
        {
            var g = DungeonHealthCurve.RatioPerLevel(Low, High);

            // The derivation itself: 190 steps of g must carry the low anchor exactly onto the high one.
            Assert.AreEqual(High, Low * Math.Pow(g, DungeonHealthCurve.AnchorHighLevel - DungeonHealthCurve.AnchorLowLevel), 1e-6);

            foreach (var level in new[] { 20, 100, 184, 185, 186, 250, 300, 373 })
                Assert.AreEqual(g, T(level + 1) / T(level), 1e-12, $"the ratio drifted at level {level}");

            // The documented headline figures, so a doc-comment edit that changed one of them would fail here.
            Assert.AreEqual(1.0142, g, 1e-4, "about +1.43% per level");
            Assert.AreEqual(1.152, Math.Pow(g, 10), 1e-3, "about +15.2% per 10-level fragment rung");
        }

        [TestMethod]
        public void The_rung_values_are_pinned_to_the_nearest_integer()
        {
            // The fourteen Raw Fragment rungs are what an admin reads off the plan log line, so a representative
            // five are pinned outright. Computed from the curve equation with the compiled anchors.
            Assert.AreEqual(890, (int)Math.Round(T(185)));
            Assert.AreEqual(2081, (int)Math.Round(T(245)));
            Assert.AreEqual(3181, (int)Math.Round(T(275)));
            Assert.AreEqual(6456, (int)Math.Round(T(325)));
            Assert.AreEqual(13100, (int)Math.Round(T(375)));
        }

        /// <summary>
        /// Re-derived 2026-10-08 (owner ruling: every monster stat follows its natural curve past the authored
        /// data, run ceiling 500). This test used to pin a CLAMP at the high anchor; the curve now keeps its own
        /// g above 375 and clamps at dynamic_dungeons_health_curve_top_level (default 500). The five values are
        /// 890 x g^(L - 185) rounded, computed independently of DungeonHealthCurve (python, double precision).
        /// </summary>
        [TestMethod]
        public void Above_the_high_anchor_the_curve_extrapolates_to_the_top_level()
        {
            Assert.AreEqual(18661, (int)Math.Round(T(400)));
            Assert.AreEqual(26583, (int)Math.Round(T(425)));
            Assert.AreEqual(37868, (int)Math.Round(T(450)));
            Assert.AreEqual(53944, (int)Math.Round(T(475)));
            Assert.AreEqual(76845, (int)Math.Round(T(500)));

            // Same ratio straight through the anchor: no kink at 375.
            var g = DungeonHealthCurve.RatioPerLevel(Low, High);
            Assert.AreEqual(g, T(376) / T(375), 1e-12);
            Assert.AreEqual(g, T(499) / T(498), 1e-12);
        }

        [TestMethod]
        public void At_and_above_the_top_level_the_curve_clamps()
        {
            // Read reflectively: both are consts, so a direct AreEqual constant-folds (MSTEST0032) and could never fail.
            Assert.AreEqual(typeof(DungeonGemSpec).GetField(nameof(DungeonGemSpec.MaxRunLevel)).GetRawConstantValue(),
                typeof(DungeonHealthCurve).GetField(nameof(DungeonHealthCurve.DefaultTopLevel)).GetRawConstantValue(), "the default top level IS the run ceiling");
            Assert.AreEqual(T(500), T(501), "T(501) == T(500)");
            Assert.AreEqual(T(500), T(1000));
            Assert.AreEqual(T(500), T(int.MaxValue));
            Assert.IsTrue(T(500) > T(499), "and the clamp starts AT the top level, not below it");
        }

        [TestMethod]
        public void A_top_level_of_375_reproduces_the_old_clamp_exactly()
        {
            foreach (var level in new[] { 1, 185, 300, 374 })
                Assert.AreEqual(T(level), DungeonHealthCurve.Target(level, Low, High, topLevel: 375), $"below the anchor nothing moves (level {level})");

            foreach (var level in new[] { 375, 376, 410, 500, 1000, int.MaxValue })
                Assert.AreEqual(High, DungeonHealthCurve.Target(level, Low, High, topLevel: 375), $"pre-2026-10-08 clamp at level {level}");
        }

        [TestMethod]
        public void The_top_level_is_sanitized_into_the_anchor_to_run_ceiling_range()
        {
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(0));
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(-1));
            Assert.AreEqual(375, DungeonHealthCurve.SanitizeTopLevel(374));
            Assert.AreEqual(420, DungeonHealthCurve.SanitizeTopLevel(420));
            Assert.AreEqual(500, DungeonHealthCurve.SanitizeTopLevel(500));
            Assert.AreEqual(500, DungeonHealthCurve.SanitizeTopLevel(long.MaxValue));

            // A mid value clamps where it says: the curve is T(420) for every level from 420 up.
            Assert.AreEqual(T(420), DungeonHealthCurve.Target(450, Low, High, topLevel: 420));
            Assert.IsTrue(DungeonHealthCurve.Target(450, Low, High, topLevel: 420) < T(450));
        }

        [TestMethod]
        public void A_level_of_zero_or_below_reads_as_no_curve()
        {
            Assert.AreEqual(0.0, T(0));
            Assert.AreEqual(0.0, T(-1));
            Assert.AreEqual(0.0, T(int.MinValue));
        }

        [TestMethod]
        public void The_curve_extends_below_the_low_anchor_rather_than_flooring_there()
        {
            Assert.IsTrue(T(100) < Low, "the ladder continues downward");
            Assert.IsTrue(T(1) > 0, "and stays positive all the way down");
        }

        // ---- anchor sanitization --------------------------------------------------------------------------

        [TestMethod]
        public void A_garbled_anchor_reads_as_its_compiled_default()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -1.0, -13100.0 })
            {
                Assert.AreEqual(Low, DungeonHealthCurve.SanitizeAnchorLow(bad), $"low anchor {bad}");
                Assert.AreEqual(High, DungeonHealthCurve.SanitizeAnchorHigh(bad), $"high anchor {bad}");
            }
        }

        [TestMethod]
        public void Zero_is_not_a_disable_on_an_anchor_the_way_it_is_on_a_reward_dial()
        {
            // SanitizeDoubleDial keeps a zero, because zero is the documented way to turn a reward axis off.
            // An anchor has no off state - a zero anchor makes g undefined - so zero falls back like any other
            // garbled value. The master switch is how the feature is disabled. This pins the difference.
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDoubleDial(0.0, DungeonPopulationLimits.DefaultTrashHealthFloorRatio,
                DungeonPopulationLimits.MaxTrashHealthFloorRatio), 1e-9);
            Assert.AreEqual(Low, DungeonHealthCurve.SanitizeAnchorLow(0.0));
        }

        [TestMethod]
        public void A_fat_fingered_anchor_is_capped_at_its_ceiling()
        {
            Assert.AreEqual(DungeonHealthCurve.MaxAnchorLow, DungeonHealthCurve.SanitizeAnchorLow(DungeonHealthCurve.MaxAnchorLow + 1));
            Assert.AreEqual(DungeonHealthCurve.MaxAnchorLow, DungeonHealthCurve.SanitizeAnchorLow(double.MaxValue));
            Assert.AreEqual(DungeonHealthCurve.MaxAnchorHigh, DungeonHealthCurve.SanitizeAnchorHigh(DungeonHealthCurve.MaxAnchorHigh * 10));

            // The ceiling is the last admissible value, not the first rejected one.
            Assert.AreEqual(DungeonHealthCurve.MaxAnchorLow, DungeonHealthCurve.SanitizeAnchorLow(DungeonHealthCurve.MaxAnchorLow));
            Assert.AreEqual(DungeonHealthCurve.MaxAnchorHigh, DungeonHealthCurve.SanitizeAnchorHigh(DungeonHealthCurve.MaxAnchorHigh));
        }

        [TestMethod]
        public void An_anchor_inside_the_range_is_used_as_configured()
        {
            Assert.AreEqual(2000.0, DungeonHealthCurve.Target(DungeonHealthCurve.AnchorLowLevel, 2000.0, 20000.0));
            Assert.AreEqual(20000.0, DungeonHealthCurve.Target(DungeonHealthCurve.AnchorHighLevel, 2000.0, 20000.0));
        }

        [TestMethod]
        public void A_high_anchor_at_or_below_the_low_one_goes_flat_rather_than_inverting()
        {
            // Swapped anchors, or a high anchor typed under the low one. A falling ladder - high-level runs
            // easier than low-level ones - is the one reading an admin cannot have meant, so the curve is flat
            // at the low anchor and the misconfiguration is obvious in the plan log line.
            foreach (var level in new[] { 1, 100, 185, 300, 375, 500 })
            {
                Assert.AreEqual(5000.0, DungeonHealthCurve.Target(level, 5000.0, 4000.0), $"inverted anchors at level {level}");
                Assert.AreEqual(5000.0, DungeonHealthCurve.Target(level, 5000.0, 5000.0), $"equal anchors at level {level}");
            }

            Assert.AreEqual(1.0, DungeonHealthCurve.RatioPerLevel(5000.0, 4000.0), "and g is 1, not a fraction below 1");

            // Still "no curve" for a non-positive level - the flat arm does not bypass that.
            Assert.AreEqual(0.0, DungeonHealthCurve.Target(0, 5000.0, 4000.0));
        }

        [TestMethod]
        public void The_ceilings_can_themselves_produce_the_flat_case()
        {
            // A low anchor far above its ceiling clamps to 100000, which is above the default high anchor, so
            // the curve goes flat at the clamped low value. Worth pinning because it is the one path where two
            // independently sane-looking guards combine.
            Assert.AreEqual(DungeonHealthCurve.MaxAnchorLow, DungeonHealthCurve.Target(300, 1e9, High));
        }

        // ---- the limits overload and the master switch ----------------------------------------------------

        private static DungeonPopulationLimits Limits(bool curveOn, double low = Low, double high = High)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5,
                healthCurveEnabled: curveOn, healthCurveAnchorLow: low, healthCurveAnchorHigh: high);

        [TestMethod]
        public void The_master_switch_off_reads_as_no_curve_at_every_level()
        {
            foreach (var level in new[] { 1, 185, 245, 375, 500 })
                Assert.AreEqual(0.0, DungeonHealthCurve.Target(level, Limits(false)), $"level {level}");
        }

        [TestMethod]
        public void The_limits_overload_agrees_with_the_pure_form_when_the_switch_is_on()
        {
            foreach (var level in new[] { 1, 100, 185, 245, 375, 500 })
                Assert.AreEqual(T(level), DungeonHealthCurve.Target(level, Limits(true)), $"level {level}");
        }

        [TestMethod]
        public void The_limits_overload_carries_configured_anchors_through()
        {
            Assert.AreEqual(2000.0, DungeonHealthCurve.Target(DungeonHealthCurve.AnchorLowLevel, Limits(true, 2000.0, 20000.0)));
        }

        // NO TEST HERE FOR "the limits struct's defaults are the curve's own constants", deliberately.
        // DungeonPopulationLimits.DefaultHealthCurveAnchorLow/High are declared AS
        // DungeonHealthCurve.DefaultAnchorLow/High, so both sides of any such assertion constant-fold to the
        // same literal and it can never fail - the compiler says so (MSTEST0032, "its condition is known to be
        // always true"). The const aliasing already enforces the shared-constant contract at compile time,
        // which is strictly stronger than a test; a warning-emitting assertion that discriminates nothing is
        // worse than no assertion. Read the raw constants reflectively if this ever needs a runtime check.
    }
}

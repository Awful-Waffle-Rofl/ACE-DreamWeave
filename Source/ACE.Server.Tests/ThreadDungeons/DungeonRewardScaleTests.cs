using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The gem-level reward scaling curve (owner requirement, 2026-09-07): ratio(level) = clamp((level /
    /// anchor) ^ exponent, floor, cap), applied once in DungeonPopulationBuilder.Build to every BENEFICIAL
    /// multiplier a run grants. Pure-math coverage for <see cref="DungeonRewardMath.RewardScaleRatio"/>,
    /// <see cref="DungeonRewardMath.ApplyMultiplicativeScale"/> and
    /// <see cref="DungeonRewardMath.ApplyAdditiveScale"/>; the builder-integration coverage (master switch,
    /// which plan fields get scaled) lives in DungeonPopulationBuilderTests.
    /// </summary>
    [TestClass]
    public class DungeonRewardScaleTests
    {
        private const double Anchor = DungeonPopulationLimits.DefaultRewardScaleAnchor;
        private const double Exponent = DungeonPopulationLimits.DefaultRewardScaleExponent;
        private const double Floor = DungeonPopulationLimits.DefaultRewardScaleFloor;
        private const double Cap = DungeonPopulationLimits.DefaultRewardScaleCap;

        /// <summary>
        /// The reference table the spec is built against: 2.87 is chosen so ratio(185) = 0.25 exactly with
        /// the shipped anchor of 300, and ratio(300) = 1.00. Tolerance 0.01 per level, matching the spec.
        /// </summary>
        [TestMethod]
        public void Ratio_matches_the_reference_table_at_the_shipped_defaults()
        {
            Assert.AreEqual(300.0, Anchor, 1e-9, "guard: the shipped anchor these expectations are written against");
            Assert.AreEqual(2.87, Exponent, 1e-9, "guard: the shipped exponent these expectations are written against");
            Assert.AreEqual(0.10, Floor, 1e-9, "guard: the shipped floor these expectations are written against");
            Assert.AreEqual(0.0, Cap, 1e-9, "guard: the shipped cap (0 = uncapped) these expectations are written against");

            var expected = new (int Level, double Ratio)[]
            {
                (185, 0.25), (215, 0.38), (245, 0.56), (275, 0.78), (300, 1.00), (350, 1.56), (400, 2.28),
            };

            foreach (var (level, ratio) in expected)
                Assert.AreEqual(ratio, DungeonRewardMath.RewardScaleRatio(level, Anchor, Exponent, Floor, Cap), 0.01,
                    $"level {level}");
        }

        /// <summary>
        /// The MULTIPLICATIVE form (neutral value 1.0): effective = 1 + ratio * (raw - 1). Never ratio * raw -
        /// at ratio 0.25 that would take a 2.0x scalar down to 0.5x, i.e. paying LESS than not running a
        /// Thread at all, which inverts the whole point of a reward-scale curve.
        /// </summary>
        [TestMethod]
        public void Multiplicative_scale_uses_one_plus_ratio_times_raw_minus_one()
        {
            Assert.AreEqual(1.25, DungeonRewardMath.ApplyMultiplicativeScale(0.25, 2.0), 1e-9);
            Assert.AreNotEqual(0.5, DungeonRewardMath.ApplyMultiplicativeScale(0.25, 2.0), 1e-9,
                "the wrong form (ratio * raw) must not be what this produces");

            // Ratio 0.0 (the neutral floor of the axis, e.g. master switch semantics if it ever reached here)
            // leaves the axis at its own neutral value, 1.0, regardless of raw.
            Assert.AreEqual(1.0, DungeonRewardMath.ApplyMultiplicativeScale(0.0, 2.0), 1e-9);

            // Ratio 1.0 (at the anchor) leaves raw untouched.
            Assert.AreEqual(2.0, DungeonRewardMath.ApplyMultiplicativeScale(1.0, 2.0), 1e-9);
        }

        /// <summary>The ADDITIVE form (neutral value 0.0): effective = ratio * raw.</summary>
        [TestMethod]
        public void Additive_scale_uses_ratio_times_raw()
        {
            Assert.AreEqual(0.1, DungeonRewardMath.ApplyAdditiveScale(0.25, 0.4), 1e-9);
            Assert.AreEqual(0.0, DungeonRewardMath.ApplyAdditiveScale(0.0, 0.4), 1e-9);
            Assert.AreEqual(0.4, DungeonRewardMath.ApplyAdditiveScale(1.0, 0.4), 1e-9);
        }

        /// <summary>The ratio must never fall as level rises - the whole point of the curve.</summary>
        [TestMethod]
        public void Ratio_is_monotonically_non_decreasing_in_level()
        {
            var previous = DungeonRewardMath.RewardScaleRatio(1, Anchor, Exponent, Floor, Cap);

            for (var level = 2; level <= 500; level++)
            {
                var current = DungeonRewardMath.RewardScaleRatio(level, Anchor, Exponent, Floor, Cap);
                Assert.IsTrue(current >= previous - 1e-9, $"ratio dropped from {previous} to {current} going from level {level - 1} to {level}");
                previous = current;
            }
        }

        /// <summary>A cap of 0 (the shipped default) means uncapped - growth above the anchor is unbounded.</summary>
        [TestMethod]
        public void A_cap_of_zero_does_not_clamp()
        {
            var ratio = DungeonRewardMath.RewardScaleRatio(1000, Anchor, Exponent, Floor, 0.0);
            Assert.IsTrue(ratio > 2.28, $"level 1000 must exceed the level-400 reference ratio of 2.28 uncapped; got {ratio}");

            // Negative reads the same as zero - both mean "no upper clamp", never "clamp to 0" or "fallback".
            var ratioNegativeCap = DungeonRewardMath.RewardScaleRatio(1000, Anchor, Exponent, Floor, -5.0);
            Assert.AreEqual(ratio, ratioNegativeCap, 1e-9, "a negative cap must read the same as 0 - uncapped");
        }

        /// <summary>A positive cap clamps the ratio at that value, however high the level would otherwise push it.</summary>
        [TestMethod]
        public void A_positive_cap_clamps_the_ratio()
        {
            var ratio = DungeonRewardMath.RewardScaleRatio(1000, Anchor, Exponent, Floor, 2.0);
            Assert.AreEqual(2.0, ratio, 1e-9);
        }

        /// <summary>Below the anchor the ratio never drops below the configured floor, however low the level.</summary>
        [TestMethod]
        public void The_floor_bounds_the_ratio_at_low_levels()
        {
            Assert.AreEqual(Floor, DungeonRewardMath.RewardScaleRatio(1, Anchor, Exponent, Floor, Cap), 1e-9);
            Assert.AreEqual(Floor, DungeonRewardMath.RewardScaleRatio(0, Anchor, Exponent, Floor, Cap), 1e-9);
        }

        /// <summary>
        /// Garbage tunables (NaN, Infinity, a non-positive anchor) fall back to the compiled default rather
        /// than propagating into the arithmetic - the same defensive contract XpForKill applies to
        /// rewardScale, needed here because a limits struct built directly in code (every unit test, and any
        /// caller that skips ThreadDungeonSpawner's own sanitizing) has no upstream sanitizer.
        /// </summary>
        [TestMethod]
        public void Garbled_inputs_fall_back_to_the_compiled_defaults()
        {
            var expected = DungeonRewardMath.RewardScaleRatio(185, Anchor, Exponent, Floor, Cap);

            Assert.AreEqual(expected, DungeonRewardMath.RewardScaleRatio(185, double.NaN, Exponent, Floor, Cap), 1e-9);
            Assert.AreEqual(expected, DungeonRewardMath.RewardScaleRatio(185, 0.0, Exponent, Floor, Cap), 1e-9, "a non-positive anchor must not divide by zero or go negative");
            Assert.AreEqual(expected, DungeonRewardMath.RewardScaleRatio(185, -50.0, Exponent, Floor, Cap), 1e-9);
            Assert.AreEqual(expected, DungeonRewardMath.RewardScaleRatio(185, Anchor, double.NaN, Floor, Cap), 1e-9);
            Assert.AreEqual(expected, DungeonRewardMath.RewardScaleRatio(185, Anchor, Exponent, double.NaN, Cap), 1e-9);
            Assert.AreEqual(expected, DungeonRewardMath.RewardScaleRatio(185, Anchor, Exponent, -1.0, Cap), 1e-9, "a negative floor must not remove the floor entirely");
        }
    }
}

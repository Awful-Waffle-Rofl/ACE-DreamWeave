using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the EXPERIENCE-purchase price curve (Docs/ClassAbilities/XP-LANE-SPEC.md sec 3.1), the lane
    /// that replaced enlightenment milestones. It reuses ClassAbilityLumCurve's math rather than adding a
    /// second curve, with all three ratios set equal so the piecewise shape collapses to a single geometric
    /// run: cost(n) = 200,000,000,000 * 1.4^(n-1), uncapped.
    ///
    /// These pin the SHAPE and the tunable defaults. The live purchase
    /// (Player.TryBuyClassAbilityPointsWithXp / XpCostForClassAbilityPoints) needs a Player and is exercised
    /// in-game via /abilities buyxp and the Drift Network exchange stone.
    /// </summary>
    [TestClass]
    public class ClassAbilityXpCurveTests
    {
        // the shipped defaults from PropertyManager (class_ability_xp_*)
        private const long Base = 200_000_000_000L;
        private const double R = 1.4;
        private const int Bp1 = 1;
        private const int Bp2 = 1;

        private static long Cost(int n) => ClassAbilityLumCurve.CostForPoint(n, Base, R, R, R, Bp1, Bp2);
        private static long Range(int already, int count) => ClassAbilityLumCurve.CostForRange(already, count, Base, R, R, R, Bp1, Bp2);

        [TestMethod]
        public void FirstPointCostsTheBase()
        {
            Assert.AreEqual(Base, Cost(1));
        }

        [TestMethod]
        public void EachPointCosts1Point4TimesTheLast()
        {
            // hand-computed against 200e9 * 1.4^(n-1); these are the numbers the design was signed off on
            Assert.AreEqual(200_000_000_000L, Cost(1));
            Assert.AreEqual(280_000_000_000L, Cost(2));
            Assert.AreEqual(392_000_000_000L, Cost(3));
            Assert.AreEqual(548_800_000_000L, Cost(4));
            Assert.AreEqual(768_320_000_000L, Cost(5));
        }

        /// <summary>
        /// The reason the spec can set both breakpoints to 1 and ignore them: RawCost's three exponents always
        /// sum to n-1, so with equal ratios the product is base * r^(n-1) for ANY breakpoint values. If this
        /// ever fails, the tunables are no longer inert and the defaults need revisiting.
        /// </summary>
        [TestMethod]
        public void EqualRatiosMakeTheCostIndependentOfBreakpoints()
        {
            foreach (var n in new[] { 1, 2, 5, 10, 25 })
            {
                var expected = ClassAbilityLumCurve.CostForPoint(n, Base, R, R, R, 1, 1);

                Assert.AreEqual(expected, ClassAbilityLumCurve.CostForPoint(n, Base, R, R, R, 10, 20), $"breakpoints 10/20 changed point {n}");
                Assert.AreEqual(expected, ClassAbilityLumCurve.CostForPoint(n, Base, R, R, R, 3, 7), $"breakpoints 3/7 changed point {n}");
                Assert.AreEqual(expected, ClassAbilityLumCurve.CostForPoint(n, Base, R, R, R, 100, 200), $"breakpoints 100/200 changed point {n}");
            }
        }

        [TestMethod]
        public void RangeIsTheSumOfTheIndividualPoints()
        {
            // buying 5 at once must cost exactly what buying them one at a time costs
            long oneByOne = 0;
            for (var n = 1; n <= 5; n++)
                oneByOne += Cost(n);

            Assert.AreEqual(oneByOne, Range(0, 5));

            // and continuing from a position picks up where the curve left off
            Assert.AreEqual(Cost(6) + Cost(7), Range(5, 2));
        }

        [TestMethod]
        public void NonPositiveInputsCostNothing()
        {
            Assert.AreEqual(0, Cost(0));
            Assert.AreEqual(0, Cost(-1));
            Assert.AreEqual(0, Range(0, 0));
            Assert.AreEqual(0, Range(0, -3));
        }

        /// <summary>
        /// The curve is uncapped, so a high enough point number overflows a long. It must CLAMP - reading as
        /// unaffordable - rather than wrapping to a small or negative price that a character could pay.
        /// </summary>
        [TestMethod]
        public void AbsurdPointNumbersClampInsteadOfWrapping()
        {
            Assert.AreEqual(long.MaxValue, Cost(200));
            Assert.AreEqual(long.MaxValue, Range(200, 5));

            foreach (var n in new[] { 1, 50, 100, 150, 200, 500 })
                Assert.IsTrue(Cost(n) > 0, $"point {n} priced non-positive, which would be free or negative");
        }

        /// <summary>
        /// Guards the affordability comparison in TryBuyClassAbilityPointsWithXp: the whole reachable range of
        /// the curve has to stay inside the xp a character can physically hold. Lifetime earnable xp is the
        /// chart's ceiling total, 4,590,249,062,099,211,814 - spending ALL of it buys 47 points, so the lane
        /// terminates well before the long.MaxValue clamp above ever matters in play.
        /// </summary>
        [TestMethod]
        public void LaneTerminatesAtFortySevenPointsOnLifetimeXp()
        {
            const double lifetimeXpCeiling = 4.590249062099211814e18;

            var affordable = 0;
            double cumulative = 0;

            for (var n = 1; n <= 60; n++)
            {
                cumulative += Base * Math.Pow(R, n - 1);
                if (cumulative <= lifetimeXpCeiling)
                    affordable = n;
            }

            Assert.AreEqual(47, affordable);
        }
    }
}

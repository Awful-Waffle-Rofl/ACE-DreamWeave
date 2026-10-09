using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure luminance-purchase price curve (ClassAbilityLumCurve, DESIGN.md sec 2b): a
    /// piecewise geometric ramp (base * r1^.. * r2^.. * r3^..) with two breakpoints and NO cap. The live
    /// purchase that consumes this (Player.TryBuyClassAbilityPoints / LumCostForClassAbilityPoints) needs a
    /// Player + Luminance bank and is exercised in-game via /abilities buy.
    /// </summary>
    [TestClass]
    public class ClassAbilityLumCurveTests
    {
        // the design defaults
        private const long Base = ClassAbilityLumCurve.DefaultBaseCost;   // 1,000,000
        private const double R1 = ClassAbilityLumCurve.DefaultRatio1;      // 1.25
        private const double R2 = ClassAbilityLumCurve.DefaultRatio2;      // 1.5
        private const double R3 = ClassAbilityLumCurve.DefaultRatio3;      // 1.75
        private const int Bp1 = ClassAbilityLumCurve.DefaultBreakpoint1;   // 10
        private const int Bp2 = ClassAbilityLumCurve.DefaultBreakpoint2;   // 20

        private static long Cost(int n) => ClassAbilityLumCurve.CostForPoint(n, Base, R1, R2, R3, Bp1, Bp2);
        private static long Range(int already, int count) => ClassAbilityLumCurve.CostForRange(already, count, Base, R1, R2, R3, Bp1, Bp2);

        [TestMethod]
        public void FirstPointCostsTheBase_AndSubOnePointsCostNothing()
        {
            Assert.AreEqual(Base, Cost(1));
            Assert.AreEqual(0, Cost(0));
            Assert.AreEqual(0, Cost(-5));
        }

        [TestMethod]
        public void MatchesTheDesignTableAnchors_WithinOnePercent()
        {
            // DESIGN.md sec 2b worked table
            AssertWithin(7_500_000, Cost(10), 0.01);      // ~7.5M
            AssertWithin(430_000_000, Cost(20), 0.01);     // ~430M
            AssertWithin(752_000_000, Cost(21), 0.01);     // ~752M
            AssertWithin(2_300_000_000, Cost(23), 0.01);   // ~2.30B
        }

        [TestMethod]
        public void RatioBetweenConsecutivePointsMatchesTheActiveSegment()
        {
            // segment 1 (points 1..10): each point is r1x the previous
            for (var n = 1; n < Bp1; n++)
                AssertWithin(R1, (double)Cost(n + 1) / Cost(n), 0.001);

            // segment 2 (points 10..20): r2x
            for (var n = Bp1; n < Bp2; n++)
                AssertWithin(R2, (double)Cost(n + 1) / Cost(n), 0.001);

            // tail (points 20+): r3x
            for (var n = Bp2; n < Bp2 + 10; n++)
                AssertWithin(R3, (double)Cost(n + 1) / Cost(n), 0.001);
        }

        [TestMethod]
        public void CostIsStrictlyIncreasing()
        {
            for (var n = 1; n < 60; n++)
                Assert.IsTrue(Cost(n + 1) > Cost(n), $"cost did not increase from point {n} to {n + 1}");
        }

        [TestMethod]
        public void RangeSumsTheCurveAndRespectsTheAlreadyPurchasedOffset()
        {
            // buying the first 3 == cost(1)+cost(2)+cost(3)
            AssertWithin(Cost(1) + Cost(2) + Cost(3), Range(0, 3), 0.0001);

            // buying 1 more after 5 already bought == cost(6)
            Assert.AreEqual(Cost(6), Range(5, 1));

            // buying 0 (or fewer) costs nothing
            Assert.AreEqual(0, Range(0, 0));
            Assert.AreEqual(0, Range(3, -2));
        }

        [TestMethod]
        public void ExtremePointNumbersClampInsteadOfOverflowing()
        {
            // ~1.75^180 overflows a double->long; must clamp to long.MaxValue, never wrap negative
            Assert.AreEqual(long.MaxValue, Cost(200));
            Assert.IsTrue(Range(0, 500) > 0);
            Assert.AreEqual(long.MaxValue, Range(0, 500));
        }

        /// <summary>
        /// Pins the cumulative figures that StageTestCommands.MaxLuminancePerGrant is documented against.
        /// That cap is sized as "one grant buys the first 40 points", and its doc comment quotes these
        /// numbers to justify the size; if the curve is retuned the cap still re-derives correctly, but the
        /// comment goes stale silently - this is what catches that.
        /// </summary>
        [TestMethod]
        public void CumulativeCostsQuotedByTheMyLumGrantCapAreCorrect()
        {
            Assert.AreEqual(1_299_815_225L, Range(0, 20), "cumulative cost of the first 20 points");
            Assert.AreEqual(72_751_548_490_990L, Range(0, 40), "cumulative cost of the first 40 points");
            Assert.AreEqual(54_563_438_373_361L, Cost(41), "the 41st point on its own");

            // the point of the resize: the old flat 1B cap did not even reach point 20
            Assert.IsTrue(Range(0, 20) > 1_000_000_000L);

            // and the resized cap stays orders of magnitude clear of the 64-bit ceiling
            Assert.IsTrue(Range(0, 40) < long.MaxValue / 1000);
        }

        private static void AssertWithin(double expected, double actual, double relativeTolerance)
        {
            var tolerance = System.Math.Abs(expected) * relativeTolerance;
            Assert.IsTrue(System.Math.Abs(expected - actual) <= tolerance,
                $"expected {expected:N0} +/- {relativeTolerance:P0}, got {actual:N0}");
        }
    }
}

using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.CapacityUpgrades;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CapacityUpgradePricing is pure, so these tests read no settings. The price literals were computed
    /// independently with BigInteger ceil(500 * 120^n / 100^n) and are pinned as literals, never
    /// recomputed from the code under test.
    /// </summary>
    [TestClass]
    public class CapacityUpgradePricingTests
    {
        private const long Base = 500;
        private const long Growth = 20;

        [TestMethod]
        [DataRow(0, 500L)]
        [DataRow(1, 600L)]
        [DataRow(2, 720L)]
        [DataRow(3, 864L)]
        [DataRow(4, 1037L)]
        [DataRow(5, 1245L)]
        [DataRow(6, 1493L)]
        [DataRow(7, 1792L)]
        [DataRow(8, 2150L)]
        [DataRow(9, 2580L)]
        [DataRow(16, 9245L)]
        [DataRow(29, 98907L)]
        [DataRow(99, 34_507_489_385L)]
        public void CostMmd_MatchesTheIndependentlyComputedPrices(int owned, long expectedMmd)
        {
            Assert.AreEqual(expectedMmd, CapacityUpgradePricing.CostMmd(owned, Base, Growth));
        }

        [TestMethod]
        public void PyrealPrices_AtTheBoundaries()
        {
            Assert.IsTrue(CapacityUpgradePricing.TryCostPyreals(9245, out var n16));
            Assert.AreEqual(2_311_250_000L, n16);
            Assert.IsTrue(n16 > int.MaxValue, "n = 16 is the first price over int.MaxValue; the pyreal cost must be a long");

            Assert.IsTrue(CapacityUpgradePricing.TryCostPyreals(34_507_489_385L, out var n99));
            Assert.AreEqual(8_626_872_346_250_000L, n99);

            Assert.IsTrue(CapacityUpgradePricing.TryCostPyreals(500, out var n0));
            Assert.AreEqual(125_000_000L, n0);
        }

        [TestMethod]
        public void TryQuote_ReturnsBothPrices()
        {
            Assert.IsTrue(CapacityUpgradePricing.TryQuote(0, Base, Growth, 100, out var mmd0, out var pyr0));
            Assert.AreEqual(500L, mmd0);
            Assert.AreEqual(125_000_000L, pyr0);

            Assert.IsTrue(CapacityUpgradePricing.TryQuote(99, Base, Growth, 100, out var mmd99, out var pyr99));
            Assert.AreEqual(34_507_489_385L, mmd99);
            Assert.AreEqual(8_626_872_346_250_000L, pyr99);
        }

        [TestMethod]
        public void TryQuote_RefusesAtTheConfiguredMaximum()
        {
            Assert.IsTrue(CapacityUpgradePricing.TryQuote(9, Base, Growth, 10, out _, out _));
            AssertRefused(10, Base, Growth, 10);
            AssertRefused(11, Base, Growth, 10);
            AssertRefused(0, Base, Growth, 0);
            AssertRefused(0, Base, Growth, -5);
        }

        [TestMethod]
        public void TryQuote_HardMaxWinsOverALargerConfiguredMaximum()
        {
            Assert.AreEqual(100, Constant("HardMaxPurchases"));

            Assert.IsTrue(CapacityUpgradePricing.TryQuote(99, Base, Growth, long.MaxValue, out _, out _));
            AssertRefused(100, Base, Growth, long.MaxValue);
            AssertRefused(int.MaxValue, Base, Growth, long.MaxValue);
        }

        [TestMethod]
        public void TryQuote_RefusesBadInputs()
        {
            AssertRefused(-1, Base, Growth, 100);
            AssertRefused(0, 0, Growth, 100);
            AssertRefused(0, -1, Growth, 100);
            AssertRefused(0, Base, -1, 100);
        }

        [TestMethod]
        public void TryQuote_RefusesOnOverflow()
        {
            // MMD fits, pyreals do not: long.MaxValue / 250,000 is about 3.69e13.
            AssertRefused(0, 40_000_000_000_000L, 0, 100);

            // The MMD price itself does not fit a long.
            AssertRefused(99, long.MaxValue, 20, 100);
            AssertRefused(50, Base, long.MaxValue, 100);
        }

        [TestMethod]
        public void ZeroGrowth_IsAFlatPrice()
        {
            Assert.AreEqual(500L, CapacityUpgradePricing.CostMmd(0, Base, 0));
            Assert.AreEqual(500L, CapacityUpgradePricing.CostMmd(99, Base, 0));
        }

        [TestMethod]
        public void CostMmd_ThrowsOutsideItsDomain()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CapacityUpgradePricing.CostMmd(-1, Base, Growth));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CapacityUpgradePricing.CostMmd(101, Base, Growth));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CapacityUpgradePricing.CostMmd(0, 0, Growth));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CapacityUpgradePricing.CostMmd(0, Base, -1));
            Assert.ThrowsExactly<OverflowException>(() => CapacityUpgradePricing.CostMmd(99, long.MaxValue, 20));
        }

        [TestMethod]
        public void TryCostPyreals_RefusesNonPositiveAndOverflow()
        {
            Assert.IsFalse(CapacityUpgradePricing.TryCostPyreals(0, out var zero));
            Assert.AreEqual(0L, zero);
            Assert.IsFalse(CapacityUpgradePricing.TryCostPyreals(-1, out _));
            Assert.IsFalse(CapacityUpgradePricing.TryCostPyreals(long.MaxValue / 250_000 + 1, out var over));
            Assert.AreEqual(0L, over);
            Assert.IsTrue(CapacityUpgradePricing.TryCostPyreals(long.MaxValue / 250_000, out _));
        }

        [TestMethod]
        public void GrantConstants()
        {
            Assert.AreEqual(100, Constant("MuleEntriesPerUpgrade"));
            Assert.AreEqual(10, Constant("ListingsPerUpgrade"));
            Assert.AreEqual(250_000L, Constant("PyrealsPerMmd"));
        }

        /// <summary>
        /// Reads a public const by reflection. A direct comparison against a const is flagged by the
        /// MSTEST0032 analyzer as always true; this still fails if the constant's value changes.
        /// </summary>
        private static object Constant(string name)
            => typeof(CapacityUpgradePricing).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null);

        private static void AssertRefused(int owned, long baseMmd, long growthPct, long maxCount)
        {
            Assert.IsFalse(CapacityUpgradePricing.TryQuote(owned, baseMmd, growthPct, maxCount, out var mmd, out var pyreals),
                $"expected a refusal for owned {owned}, base {baseMmd}, growth {growthPct}, max {maxCount}");
            Assert.AreEqual(0L, mmd);
            Assert.AreEqual(0L, pyreals);
        }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.CapacityUpgrades;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CapacityUpgradeBroker.Decide: the PURE decision behind both /mule upgrade and /market upgrade -
    /// no PropertyManager, no Player, no live account state. Every refusal a player can see is driven
    /// from here.
    /// </summary>
    [TestClass]
    public class CapacityUpgradeBrokerTests
    {
        private const long BaseMmd = 500;
        private const long GrowthPct = 20;
        private const long MaxCount = 100;
        private const int CurrentCap = 500;
        private const int PerUpgradeAmount = 100;

        [TestMethod]
        public void Disabled_RefusesBeforeAnythingElse()
        {
            var result = CapacityUpgradeBroker.Decide(
                enabled: false, countOk: true, owned: 0, baseMmd: BaseMmd, growthPct: GrowthPct, maxCount: MaxCount,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.Disabled, result);
        }

        [TestMethod]
        public void CountUnavailable_RefusesEvenWhenEnabledAndFunded()
        {
            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: false, owned: 0, baseMmd: BaseMmd, growthPct: GrowthPct, maxCount: MaxCount,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.CountUnavailable, result);
        }

        [TestMethod]
        public void AtMax_WhenOwnedReachesTheConfiguredMax()
        {
            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 5, baseMmd: BaseMmd, growthPct: GrowthPct, maxCount: 5,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.AtMax, result);
        }

        [TestMethod]
        public void AtMax_WhenOwnedReachesTheHardMax_EvenIfConfiguredHigher()
        {
            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: CapacityUpgradePricing.HardMaxPurchases, baseMmd: BaseMmd, growthPct: GrowthPct,
                maxCount: CapacityUpgradePricing.HardMaxPurchases + 50,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.AtMax, result);
        }

        [TestMethod]
        public void InsufficientFunds_WhenBankIsBelowTheQuotedPrice()
        {
            Assert.IsTrue(CapacityUpgradePricing.TryQuote(0, BaseMmd, GrowthPct, MaxCount, out var mmd, out var pyreals));

            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 0, baseMmd: BaseMmd, growthPct: GrowthPct, maxCount: MaxCount,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: pyreals - 1, out var quote);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.InsufficientFunds, result);
            Assert.AreEqual(mmd, quote.Mmd);
            Assert.AreEqual(pyreals, quote.Pyreals);
        }

        [TestMethod]
        public void Ok_WhenEverythingClears_QuotesTheExactPriceAndCaps()
        {
            Assert.IsTrue(CapacityUpgradePricing.TryQuote(3, BaseMmd, GrowthPct, MaxCount, out var mmd, out var pyreals));

            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 3, baseMmd: BaseMmd, growthPct: GrowthPct, maxCount: MaxCount,
                currentCap: 800, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: pyreals, out var quote);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.Ok, result);
            Assert.AreEqual(3, quote.Owned);
            Assert.AreEqual(mmd, quote.Mmd);
            Assert.AreEqual(pyreals, quote.Pyreals);
            Assert.AreEqual(800, quote.CurrentCap);
            Assert.AreEqual(900, quote.NewCap, "NewCap must be CurrentCap plus exactly perUpgradeAmount");
        }

        [TestMethod]
        public void Ok_WhenBankedExactlyEqualsThePrice()
        {
            Assert.IsTrue(CapacityUpgradePricing.TryQuote(0, BaseMmd, GrowthPct, MaxCount, out _, out var pyreals));

            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 0, baseMmd: BaseMmd, growthPct: GrowthPct, maxCount: MaxCount,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: pyreals, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.Ok, result, "exactly enough must not be treated as short");
        }

        [TestMethod]
        public void PricingUnavailable_WhenBaseCostIsMisconfiguredToZero_NeverReportsAtMax()
        {
            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 0, baseMmd: 0, growthPct: GrowthPct, maxCount: MaxCount,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.PricingUnavailable, result,
                "a misconfigured price must never be reported to a player as having bought every upgrade");
        }

        [TestMethod]
        public void PricingUnavailable_WhenTheQuoteOverflows()
        {
            Assert.IsFalse(CapacityUpgradePricing.TryQuote(50, long.MaxValue, 1_000_000, MaxCount, out _, out _),
                "test setup assumption: this combination must actually overflow CostMmd");

            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 50, baseMmd: long.MaxValue, growthPct: 1_000_000, maxCount: MaxCount,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.PricingUnavailable, result);
        }

        [TestMethod]
        public void AtMax_StillWinsOverPricingUnavailable_WhenOwnedIsGenuinelyAtTheMax()
        {
            // Even with a misconfigured baseMmd, an account that has already reached the max must see
            // AtMax, not PricingUnavailable - the max check runs before TryQuote is ever called.
            var result = CapacityUpgradeBroker.Decide(
                enabled: true, countOk: true, owned: 5, baseMmd: 0, growthPct: GrowthPct, maxCount: 5,
                currentCap: CurrentCap, perUpgradeAmount: PerUpgradeAmount, bankedPyreals: long.MaxValue, out _);

            Assert.AreEqual(CapacityUpgradeBroker.CheckResult.AtMax, result);
        }
    }
}

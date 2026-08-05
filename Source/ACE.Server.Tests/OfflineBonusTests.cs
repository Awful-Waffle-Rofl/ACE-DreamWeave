using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Boundary math for the offline bonus system (<see cref="OfflineBonus"/>): banked time must accrue 1:1
    /// with offline time but never exceed the cap, drain 1:1 with online time but never go negative, and the
    /// combat multiplier must only fire while time remains (and never overflow a long on a bad multiplier).
    /// </summary>
    [TestClass]
    public class OfflineBonusTests
    {
        private const long Cap = 86400; // 24h default

        [TestMethod]
        public void Accrue_AddsOfflineTimeToBank()
        {
            Assert.AreEqual(3600, OfflineBonus.Accrue(0, 3600, Cap));
            Assert.AreEqual(5400, OfflineBonus.Accrue(1800, 3600, Cap));
        }

        [TestMethod]
        public void Accrue_CapsAtMax()
        {
            // 20h banked + 10h offline = 30h, capped to 24h
            Assert.AreEqual(Cap, OfflineBonus.Accrue(72000, 36000, Cap));
            // exactly at the cap stays at the cap
            Assert.AreEqual(Cap, OfflineBonus.Accrue(Cap, 1, Cap));
        }

        [TestMethod]
        public void Accrue_NonPositiveOfflineTime_LeavesBankUnchangedButClampedToCap()
        {
            Assert.AreEqual(1800, OfflineBonus.Accrue(1800, 0, Cap));
            Assert.AreEqual(1800, OfflineBonus.Accrue(1800, -100, Cap));
            // a value already over the cap (e.g. cap lowered / set out of band) clamps down
            Assert.AreEqual(Cap, OfflineBonus.Accrue(Cap + 500, 0, Cap));
        }

        [TestMethod]
        public void Drain_SubtractsOnlineTime()
        {
            Assert.AreEqual(3000, OfflineBonus.Drain(3600, 600));
        }

        [TestMethod]
        public void Drain_FloorsAtZero()
        {
            Assert.AreEqual(0, OfflineBonus.Drain(600, 3600));
            Assert.AreEqual(0, OfflineBonus.Drain(0, 100));
        }

        [TestMethod]
        public void Drain_NonPositiveElapsed_LeavesBankUnchanged()
        {
            Assert.AreEqual(3600, OfflineBonus.Drain(3600, 0));
            Assert.AreEqual(3600, OfflineBonus.Drain(3600, -50));
        }

        [TestMethod]
        public void Apply_DoublesCombatAmountAtFullMultiplier()
        {
            // +100% => x2
            Assert.AreEqual(2000, OfflineBonus.Apply(1000, remaining: 60, multiplier: 1.0));
            // +50% => x1.5
            Assert.AreEqual(1500, OfflineBonus.Apply(1000, remaining: 60, multiplier: 0.5));
        }

        [TestMethod]
        public void Apply_NoBonusWhenNoTimeRemaining()
        {
            Assert.AreEqual(1000, OfflineBonus.Apply(1000, remaining: 0, multiplier: 1.0));
            Assert.AreEqual(1000, OfflineBonus.Apply(1000, remaining: -5, multiplier: 1.0));
        }

        [TestMethod]
        public void Apply_NoOpForNonPositiveAmountOrMultiplier()
        {
            Assert.AreEqual(0, OfflineBonus.Apply(0, remaining: 60, multiplier: 1.0));
            Assert.AreEqual(-10, OfflineBonus.Apply(-10, remaining: 60, multiplier: 1.0));
            Assert.AreEqual(1000, OfflineBonus.Apply(1000, remaining: 60, multiplier: 0));
        }

        [TestMethod]
        public void Apply_OverflowGuardReturnsUnboostedAmount()
        {
            // a wildly misconfigured multiplier must not wrap the long
            Assert.AreEqual(long.MaxValue / 2, OfflineBonus.Apply(long.MaxValue / 2, remaining: 60, multiplier: 1e300));
        }
    }
}

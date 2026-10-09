using System;

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

        [TestMethod]
        public void SplitOnlineInterval_FullyActive_WhenActiveUntilCoversWholeInterval()
        {
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1300, activeUntil: 2000);

            Assert.AreEqual(300, active);
            Assert.AreEqual(0, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_FullyIdle_WhenActiveUntilBeforeIntervalStart()
        {
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1300, activeUntil: 500);

            Assert.AreEqual(0, active);
            Assert.AreEqual(300, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_FullyIdle_WhenActiveUntilIsZero()
        {
            // activeUntil == 0 means "no qualifying XP recorded yet" - treated the same as already-idle
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1300, activeUntil: 0);

            Assert.AreEqual(0, active);
            Assert.AreEqual(300, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_PartialSplit_ActivePrefixThenIdleTail()
        {
            // active for the first 120s of a 300s interval, idle for the remaining 180s
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1300, activeUntil: 1120);

            Assert.AreEqual(120, active);
            Assert.AreEqual(180, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_ActiveUntilEqualsIntervalStart_IsFullyIdle()
        {
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1300, activeUntil: 1000);

            Assert.AreEqual(0, active);
            Assert.AreEqual(300, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_ActiveUntilEqualsIntervalEnd_IsFullyActive()
        {
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1300, activeUntil: 1300);

            Assert.AreEqual(300, active);
            Assert.AreEqual(0, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_ZeroElapsed_ReturnsZeroForBoth()
        {
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1000, 1000, activeUntil: 2000);

            Assert.AreEqual(0, active);
            Assert.AreEqual(0, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_NegativeElapsed_ReturnsZeroForBoth()
        {
            var (active, idle) = OfflineBonus.SplitOnlineInterval(1300, 1000, activeUntil: 2000);

            Assert.AreEqual(0, active);
            Assert.AreEqual(0, idle);
        }

        [TestMethod]
        public void SplitOnlineInterval_ActiveAndIdleAlwaysSumToElapsed()
        {
            var cases = new (double start, double end, double activeUntil)[]
            {
                (1000, 1300, 2000),
                (1000, 1300, 500),
                (1000, 1300, 0),
                (1000, 1300, 1120),
                (1000, 1300, 1000),
                (1000, 1300, 1300),
                (1000, 1000, 2000),
                (1300, 1000, 2000),
                (0, 86400, 43200),
            };

            foreach (var (start, end, activeUntil) in cases)
            {
                var (active, idle) = OfflineBonus.SplitOnlineInterval(start, end, activeUntil);
                var elapsed = Math.Max(0, end - start);

                Assert.IsTrue(active >= 0, $"active was negative for ({start}, {end}, {activeUntil})");
                Assert.IsTrue(idle >= 0, $"idle was negative for ({start}, {end}, {activeUntil})");
                Assert.AreEqual(elapsed, active + idle, $"active + idle did not equal elapsed for ({start}, {end}, {activeUntil})");
            }
        }

        [TestMethod]
        public void ClassifyTransition_Exhausted_WhenBankDrainsToZeroWhileStillActive()
        {
            // the bug the review caught: a kill that empties the bank while still inside the active window
            // must report Exhausted, never Banking (nothing is banking - the window hasn't closed)
            Assert.AreEqual(
                OfflineBonus.OfflineBonusTransition.Exhausted,
                OfflineBonus.ClassifyTransition(wasActive: true, isActive: true, wasBanked: true, isBanked: false));
        }

        [TestMethod]
        public void ClassifyTransition_None_WhenStartingToFightWithAnEmptyBank()
        {
            // idle -> active edge, but the bank was already empty - no notice, there's nothing to spend
            Assert.AreEqual(
                OfflineBonus.OfflineBonusTransition.None,
                OfflineBonus.ClassifyTransition(wasActive: false, isActive: true, wasBanked: false, isBanked: false));
        }

        [TestMethod]
        public void ClassifyTransition_Banking_WhenGoingIdleWithAZeroBank()
        {
            // active -> idle edge fires unconditionally, even at a zero balance
            Assert.AreEqual(
                OfflineBonus.OfflineBonusTransition.Banking,
                OfflineBonus.ClassifyTransition(wasActive: true, isActive: false, wasBanked: false, isBanked: false));
        }

        [TestMethod]
        public void ClassifyTransition_Activated_WhenGoingActiveWithABankedNonEmptyBalance()
        {
            // ordinary idle -> active edge with something in the bank
            Assert.AreEqual(
                OfflineBonus.OfflineBonusTransition.Activated,
                OfflineBonus.ClassifyTransition(wasActive: false, isActive: true, wasBanked: true, isBanked: true));
        }

        [TestMethod]
        public void ClassifyTransition_AllSixteenCombinations()
        {
            // exhaustively covers every (wasActive, isActive, wasBanked, isBanked) combination, so any future
            // change to the decision table shows up here rather than only in the four named scenarios above.
            // Rule recap:
            //  - the active state changing to true yields Activated only if isBanked, else None
            //  - the active state changing to false yields Banking unconditionally
            //  - the active state staying the same yields Exhausted only for (isActive=true, wasBanked=true, isBanked=false)
            //  - everything else is None
            foreach (var wasActive in new[] { false, true })
            {
                foreach (var isActive in new[] { false, true })
                {
                    foreach (var wasBanked in new[] { false, true })
                    {
                        foreach (var isBanked in new[] { false, true })
                        {
                            var expected = ExpectedTransition(wasActive, isActive, wasBanked, isBanked);
                            var actual = OfflineBonus.ClassifyTransition(wasActive, isActive, wasBanked, isBanked);

                            Assert.AreEqual(expected, actual,
                                $"wasActive={wasActive}, isActive={isActive}, wasBanked={wasBanked}, isBanked={isBanked}");
                        }
                    }
                }
            }
        }

        private static OfflineBonus.OfflineBonusTransition ExpectedTransition(bool wasActive, bool isActive, bool wasBanked, bool isBanked)
        {
            if (wasActive != isActive)
                return isActive ? (isBanked ? OfflineBonus.OfflineBonusTransition.Activated : OfflineBonus.OfflineBonusTransition.None) : OfflineBonus.OfflineBonusTransition.Banking;

            if (isActive && wasBanked && !isBanked)
                return OfflineBonus.OfflineBonusTransition.Exhausted;

            return OfflineBonus.OfflineBonusTransition.None;
        }
    }
}

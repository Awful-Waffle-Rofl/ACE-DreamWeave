using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Hemomancy's arming rules (Blood Mage buff pass), exercised on the pure HemomancyMath /
    /// HemomancyState types that Player.OnHealthRestored delegates to. No PropertyManager read happens in
    /// this class: every tunable is passed in as a literal at its shipped default (threshold 0.05, leech
    /// window 2 s, duration 8 s, 0.02 per rank), so it needs no seeding and is order-independent.
    /// </summary>
    [TestClass]
    public class HemomancyArmingTests
    {
        private const double MaxHealth = 1000.0;
        private const double Threshold = 0.05;
        private const double Window = 2.0;
        private const double Duration = 8.0;

        private static HemomancyArmResult Heal(HemomancyState state, double now, double applied, bool isLeech)
            => state.OnHealthRestored(now, applied, isLeech, MaxHealth, Threshold, Window, Duration);

        [TestMethod]
        public void SingleNonLeechHeal_ExactlyFivePercent_Arms()
        {
            var state = new HemomancyState();

            Assert.AreEqual(HemomancyArmResult.Armed, Heal(state, 100.0, 50.0, isLeech: false));
            Assert.IsTrue(state.IsActive(100.0));
            Assert.AreEqual(108.0, state.BuffEndTime, 1e-9);
        }

        [TestMethod]
        public void SingleNonLeechHeal_JustUnderFivePercent_DoesNotArm()
        {
            var state = new HemomancyState();

            Assert.AreEqual(HemomancyArmResult.None, Heal(state, 100.0, 49.9, isLeech: false)); // 4.99%
            Assert.IsFalse(state.IsActive(100.0));
            Assert.AreEqual(0.0, state.BuffEndTime);
        }

        [TestMethod]
        public void MeetsThreshold_IsAFractionCompare_WithBinaryRoundingTolerance()
        {
            Assert.IsTrue(HemomancyMath.MeetsThreshold(50, 1000, 0.05));
            Assert.IsFalse(HemomancyMath.MeetsThreshold(49.9, 1000, 0.05));

            // 0.05 * 340 is not exactly representable; the exact-5% heal must still arm
            Assert.IsTrue(HemomancyMath.MeetsThreshold(17, 340, 0.05));

            // degenerate inputs never arm (a mis-set 0 threshold must not make every heal arm)
            Assert.IsFalse(HemomancyMath.MeetsThreshold(50, 1000, 0.0));
            Assert.IsFalse(HemomancyMath.MeetsThreshold(50, 0, 0.05));
            Assert.IsFalse(HemomancyMath.MeetsThreshold(0, 1000, 0.05));
        }

        [TestMethod]
        public void LeechHeals_TwoPlusTwoPlusOneAndAHalfPercent_InsideWindow_ArmOnTheThird()
        {
            var state = new HemomancyState();

            Assert.AreEqual(HemomancyArmResult.None, Heal(state, 10.0, 20.0, isLeech: true));
            Assert.AreEqual(HemomancyArmResult.None, Heal(state, 10.5, 20.0, isLeech: true));
            Assert.IsFalse(state.IsActive(10.5), "4% of leech is not enough");

            Assert.AreEqual(HemomancyArmResult.Armed, Heal(state, 11.0, 15.0, isLeech: true)); // sum 5.5%
            Assert.IsTrue(state.IsActive(11.0));
            Assert.AreEqual(0, state.LeechWindow.Count, "the leech window clears when it arms");
            Assert.AreEqual(0.0, state.LeechWindow.Sum);
        }

        [TestMethod]
        public void LeechHeals_AnEntryOlderThanTheWindow_IsEvicted_AndDoesNotCount()
        {
            var state = new HemomancyState();

            Heal(state, 10.0, 20.0, isLeech: true);   // 2% at t=10
            Heal(state, 12.5, 20.0, isLeech: true);   // t=12.5: the t=10 entry is 2.5 s old -> evicted

            Assert.AreEqual(1, state.LeechWindow.Count);
            Assert.AreEqual(20.0, state.LeechWindow.Sum, 1e-9);

            // 2% + 1.5% = 3.5% inside the window: still under the bar (5.5% would have armed without eviction)
            Assert.AreEqual(HemomancyArmResult.None, Heal(state, 13.0, 15.0, isLeech: true));
            Assert.IsFalse(state.IsActive(13.0));
        }

        [TestMethod]
        public void LeechWindow_EntryExactlyAtTheEdge_IsKept()
        {
            var window = new HemomancyLeechWindow();

            window.Add(10.0, 20.0, Window);
            window.Evict(12.0, Window);

            Assert.AreEqual(1, window.Count, "age == window is still inside it");

            window.Evict(12.0001, Window);
            Assert.AreEqual(0, window.Count);
            Assert.AreEqual(0.0, window.Sum);
        }

        [TestMethod]
        public void NonLeechHeal_UnderThreshold_DoesNotFeedTheLeechWindow()
        {
            var state = new HemomancyState();

            Heal(state, 10.0, 40.0, isLeech: false);   // 4%, non-leech: no arm, no accumulation
            Assert.AreEqual(0, state.LeechWindow.Count);

            Assert.AreEqual(HemomancyArmResult.None, Heal(state, 10.5, 20.0, isLeech: true));
            Assert.IsFalse(state.IsActive(10.5));
        }

        [TestMethod]
        public void SecondTrigger_RefreshesTheExpiry_WithoutDoublingTheBonus()
        {
            var state = new HemomancyState();

            Assert.AreEqual(HemomancyArmResult.Armed, Heal(state, 100.0, 50.0, isLeech: false));
            var firstBonus = HemomancyMath.DamageMultiplier(5, 0.02, 1.0, state.IsActive(100.0));

            Assert.AreEqual(HemomancyArmResult.Refreshed, Heal(state, 105.0, 80.0, isLeech: false));
            var secondBonus = HemomancyMath.DamageMultiplier(5, 0.02, 1.0, state.IsActive(105.0));

            // refresh: expiry is now + duration, not first expiry + duration
            Assert.AreEqual(113.0, state.BuffEndTime, 1e-9);
            Assert.IsTrue(state.IsActive(112.9));
            Assert.IsFalse(state.IsActive(113.1));

            // not stacked: the bonus is the same +10% both times
            Assert.AreEqual(1.10f, firstBonus, 1e-5f);
            Assert.AreEqual(firstBonus, secondBonus);
        }

        [TestMethod]
        public void ArmingAfterTheBuffLapsed_IsAFreshArm_NotARefresh()
        {
            var state = new HemomancyState();

            Heal(state, 100.0, 50.0, isLeech: false);

            Assert.AreEqual(HemomancyArmResult.Armed, Heal(state, 120.0, 50.0, isLeech: false),
                "inactive -> active again, so it announces again");
        }

        [TestMethod]
        public void Reset_DropsTheBuffAndTheWindow()
        {
            var state = new HemomancyState();

            Heal(state, 10.0, 20.0, isLeech: true);
            Heal(state, 10.0, 50.0, isLeech: false);
            Heal(state, 10.1, 20.0, isLeech: true);

            state.Reset();

            Assert.IsFalse(state.IsActive(10.2));
            Assert.AreEqual(0, state.LeechWindow.Count);
        }

        [TestMethod]
        public void DamageMultiplier_InactiveOrRankZero_IsExactlyNeutral_AndAffinityBelowOneIsFloored()
        {
            Assert.AreEqual(1.0f, HemomancyMath.DamageMultiplier(5, 0.02, 2.0, active: false));
            Assert.AreEqual(1.0f, HemomancyMath.DamageMultiplier(0, 0.02, 2.0, active: true));
            Assert.AreEqual(1.06f, HemomancyMath.DamageMultiplier(3, 0.02, 0.5, active: true), 1e-5f);
            Assert.AreEqual(1.12f, HemomancyMath.DamageMultiplier(3, 0.02, 2.0, active: true), 1e-5f);
        }
    }
}

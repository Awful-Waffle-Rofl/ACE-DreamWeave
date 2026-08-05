using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure class-tier unlock rule (ClassAbilityTierGate) used by the Drift Network trainers:
    /// T1 open, T2 = 3 earned + 5 spent-in-class, T3 = 8 earned + 15 spent-in-class. The live purchase/refund
    /// flow that consumes this (TryPurchaseClassAbilityVoucher / RefundUnusedVoucher) needs a Player and is
    /// exercised in-game via /cavoucher and the trainer NPCs.
    /// </summary>
    [TestClass]
    public class ClassAbilityTierGateTests
    {
        [TestMethod]
        public void Tier1_IsAlwaysOpen_EvenWithZeroPoints()
        {
            Assert.IsTrue(ClassAbilityTierGate.IsTierUnlocked(1, totalPointsEarned: 0, pointsSpentInClass: 0, out var reason));
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void Tier0OrBelow_IsTreatedAsOpen()
        {
            Assert.IsTrue(ClassAbilityTierGate.IsTierUnlocked(0, 0, 0, out _));
        }

        [TestMethod]
        public void Tier2_RequiresBothEarnedAndSpent()
        {
            // exactly on both thresholds unlocks
            Assert.IsTrue(ClassAbilityTierGate.IsTierUnlocked(2,
                ClassAbilityTierGate.Tier2PointsEarned, ClassAbilityTierGate.Tier2PointsSpentInClass, out _));

            // one short on earned
            Assert.IsFalse(ClassAbilityTierGate.IsTierUnlocked(2,
                ClassAbilityTierGate.Tier2PointsEarned - 1, ClassAbilityTierGate.Tier2PointsSpentInClass, out var earnedReason));
            StringAssert.Contains(earnedReason, "earned");

            // enough earned, one short on spent-in-class
            Assert.IsFalse(ClassAbilityTierGate.IsTierUnlocked(2,
                ClassAbilityTierGate.Tier2PointsEarned, ClassAbilityTierGate.Tier2PointsSpentInClass - 1, out var spentReason));
            StringAssert.Contains(spentReason, "spent in this class");
        }

        [TestMethod]
        public void Tier3_RequiresTheHigherThresholds()
        {
            Assert.IsTrue(ClassAbilityTierGate.IsTierUnlocked(3,
                ClassAbilityTierGate.Tier3PointsEarned, ClassAbilityTierGate.Tier3PointsSpentInClass, out _));

            // meeting the T2 bar is not enough for T3
            Assert.IsFalse(ClassAbilityTierGate.IsTierUnlocked(3,
                ClassAbilityTierGate.Tier2PointsEarned, ClassAbilityTierGate.Tier2PointsSpentInClass, out _));
        }

        [TestMethod]
        public void RequirementFor_MatchesTheConstants()
        {
            Assert.AreEqual((0, 0), ClassAbilityTierGate.RequirementFor(1));
            Assert.AreEqual((ClassAbilityTierGate.Tier2PointsEarned, ClassAbilityTierGate.Tier2PointsSpentInClass),
                ClassAbilityTierGate.RequirementFor(2));
            Assert.AreEqual((ClassAbilityTierGate.Tier3PointsEarned, ClassAbilityTierGate.Tier3PointsSpentInClass),
                ClassAbilityTierGate.RequirementFor(3));
        }
    }
}

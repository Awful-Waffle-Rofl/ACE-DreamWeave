using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The rewards group of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Rewards_WithNothingSet_AreExactlyAsBefore()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            Assert.AreEqual("standard", goal.Chain.RewardForStage(1));
            Assert.AreEqual("chain_final", goal.Chain.RewardForStage(2));
            Assert.AreEqual(8, HoldSettings.ParticipantsPerCacheFor(null, 8));
            Assert.AreEqual(HoldSettings.FailureRewardConsolation, HoldSettings.FailureRewardFor(null));
            Assert.IsTrue(HoldSettings.MeetsMinContribution(null, 1));
            Assert.IsNull(HoldSettings.FullChainBonusFor(null));

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupRewards).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Rewards_StageProfiles()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.ChainRewardStage1, "Hammer_Lite");
            Assert.AreEqual("hammer_lite", goal.Chain.RewardForStage(1));
            Assert.AreEqual("chain_final", goal.Chain.RewardForStage(2), "stage 2 untouched");

            scope.Set(HoldKeys.ChainRewardStage2, "stage2_box");
            Assert.AreEqual("stage2_box", goal.Chain.RewardForStage(2));

            scope.Set(HoldKeys.ChainRewardFinal, "grand_box");
            Assert.AreEqual("grand_box", goal.Chain.RewardForStage(2), "stage 2 is the last of 2: final wins");

            scope.Set(HoldKeys.ChainMaxLength, "3");
            Assert.AreEqual("stage2_box", goal.Chain.RewardForStage(2), "with 3 stages, stage 2 is not the last");
            Assert.AreEqual("grand_box", goal.Chain.RewardForStage(3));
        }

        [TestMethod]
        public void Rewards_MinContributionRule()
        {
            Assert.IsTrue(HoldSettings.MeetsMinContribution(10, 100, 0), "0 = off");
            Assert.IsTrue(HoldSettings.MeetsMinContribution(10, 100, 0.1));
            Assert.IsFalse(HoldSettings.MeetsMinContribution(9, 100, 0.1));
            Assert.IsTrue(HoldSettings.MeetsMinContribution(0, 0, 0.2), "nobody dealt damage: everyone meets it");
        }

        [TestMethod]
        public void Rewards_WcidListsAndChoices()
        {
            CollectionAssert.AreEqual(new List<uint> { 1002600, 1002601 }, HoldSettings.ParseWcidList("1002600, 1002601", out var bad));
            Assert.AreEqual(0, bad.Count);
            Assert.AreEqual(0, HoldSettings.ParseWcidList("none", out _).Count, "none = no bonus");
            HoldSettings.ParseWcidList("1002600, crate", out bad);
            CollectionAssert.AreEqual(new[] { "crate" }, bad);

            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.FailureReward, out var s));
            Assert.AreEqual("partial", HoldSettings.Normalize(s, "Partial"));
            StringAssert.Contains(HoldSettings.Check(s, "gold"), "not one of");

            Assert.IsTrue(WorldEventChain.SameChain(5, 0, 5), "the first stage is its own root");
            Assert.IsTrue(WorldEventChain.SameChain(7, 5, 5));
            Assert.IsFalse(WorldEventChain.SameChain(7, 6, 5));
            Assert.IsFalse(WorldEventChain.SameChain(5, 0, 0));
        }
    }
}

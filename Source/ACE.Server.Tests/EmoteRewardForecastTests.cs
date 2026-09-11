using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for ACE.Entity.Models.EmoteRewardForecast - a pure, offline forecast of the item
    /// rewards an emote set (and everything it branches into) could hand out. Each test below exercises
    /// one rule named in the class's own doc comments; see EmoteManager.EmoteIsBranchingType
    /// (Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs:1762-1808) for the branching-type list
    /// this forecaster mirrors.
    /// </summary>
    [TestClass]
    public class EmoteRewardForecastTests
    {
        private static PropertiesEmote MakeSet(EmoteCategory category, string quest, float probability, params PropertiesEmoteAction[] actions)
        {
            var set = new PropertiesEmote
            {
                Category = category,
                Quest = quest,
                Probability = probability,
            };

            foreach (var action in actions)
                set.PropertiesEmoteAction.Add(action);

            return set;
        }

        private static PropertiesEmoteAction Give(uint? wcid, int? stackSize = null)
        {
            return new PropertiesEmoteAction
            {
                Type = (uint)EmoteType.Give,
                WeenieClassId = wcid,
                StackSize = stackSize,
            };
        }

        private static PropertiesEmoteAction CreateTreasure()
        {
            return new PropertiesEmoteAction { Type = (uint)EmoteType.CreateTreasure };
        }

        private static PropertiesEmoteAction Branch(EmoteType type, string message)
        {
            return new PropertiesEmoteAction { Type = (uint)type, Message = message };
        }

        [TestMethod]
        public void PlainGiveRow_IsAppendedAsReward()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Give(1000001, 5));

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(1, forecast.Paths[0].Count);
            Assert.AreEqual(1000001u, forecast.Paths[0][0].WeenieClassId);
            Assert.AreEqual(5, forecast.Paths[0][0].Amount);
            Assert.IsFalse(forecast.Paths[0][0].IsRandomTreasure);
            Assert.IsFalse(forecast.Truncated);
        }

        [TestMethod]
        public void StackSizeNull_DefaultsToOne()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Give(1000001, null));

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths[0][0].Amount);
        }

        [TestMethod]
        public void StackSizeZero_TreatedAsOne()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Give(1000001, 0));

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths[0][0].Amount);
        }

        [TestMethod]
        public void StackSizeNegative_TreatedAsOne()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Give(1000001, -3));

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths[0][0].Amount);
        }

        [TestMethod]
        public void WeenieClassIdZero_IsSkipped()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Give(0, 1));

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(0, forecast.Paths[0].Count);
        }

        [TestMethod]
        public void WeenieClassIdNull_IsSkipped()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Give(null, 1));

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(0, forecast.Paths[0].Count);
        }

        [TestMethod]
        public void CreateTreasure_AppendsRandomTreasureReward()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, CreateTreasure());

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(1, forecast.Paths[0].Count);
            Assert.IsTrue(forecast.Paths[0][0].IsRandomTreasure);
            Assert.AreEqual(1, forecast.Paths[0][0].Amount);
            Assert.AreEqual(0u, forecast.Paths[0][0].WeenieClassId);
        }

        [TestMethod]
        public void TwoWayBranch_InqQuestStyle_ProducesTwoPaths()
        {
            // trailing row after the branch: EmoteManager.DoEnqueue continues the outer set's next row
            // unconditionally after a branching row runs, so every forked path must ALSO pick up 9999999.
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqQuest, "TestQuest"), Give(9999999));
            var success = MakeSet(EmoteCategory.QuestSuccess, "TestQuest", 1, Give(2000001));
            var failure = MakeSet(EmoteCategory.QuestFailure, "TestQuest", 1, Give(2000002));

            var allSets = new List<PropertiesEmote> { root, success, failure };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(2, forecast.Paths.Count);
            Assert.IsFalse(forecast.Truncated);

            foreach (var path in forecast.Paths)
            {
                Assert.AreEqual(2, path.Count);
                Assert.IsTrue(path.Any(r => r.WeenieClassId == 9999999u), "trailing outer row must appear in every forked path");
            }

            var branchWcids = forecast.Paths.Select(p => p.Single(r => r.WeenieClassId != 9999999u).WeenieClassId).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(new List<uint> { 2000001u, 2000002u }, branchWcids);
        }

        [TestMethod]
        public void Goto_RedirectsToGotoSet_ThenResumesOuterRows()
        {
            // Goto is a dispatch, not a control-flow truncation: EmoteManager.DoEnqueue still runs the
            // outer set's next row (8888888) after the Goto row's target set finishes.
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.Goto, "NextSet"), Give(8888888));
            var next = MakeSet(EmoteCategory.GotoSet, "NextSet", 1, Give(3000001));

            var allSets = new List<PropertiesEmote> { root, next };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.IsFalse(forecast.Truncated);

            var wcids = forecast.Paths[0].Select(r => r.WeenieClassId).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(new List<uint> { 3000001u, 8888888u }, wcids);
        }

        [TestMethod]
        public void GiveA_Branch_GiveB_EveryForkedPathContainsBothOuterRowsPlusBranchContribution()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1,
                Give(1111111), // A
                Branch(EmoteType.InqQuest, "TestQuest"),
                Give(2222222)); // B

            var success = MakeSet(EmoteCategory.QuestSuccess, "TestQuest", 1, Give(3333333));
            var failure = MakeSet(EmoteCategory.QuestFailure, "TestQuest", 1, Give(4444444));

            var allSets = new List<PropertiesEmote> { root, success, failure };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(2, forecast.Paths.Count);
            Assert.IsFalse(forecast.Truncated);

            foreach (var path in forecast.Paths)
            {
                var wcids = path.Select(r => r.WeenieClassId).ToList();
                Assert.AreEqual(3, wcids.Count, "each forked path must contain A, B, and the branch's own reward");
                CollectionAssert.Contains(wcids, 1111111u);
                CollectionAssert.Contains(wcids, 2222222u);
            }

            var branchWcids = forecast.Paths
                .Select(p => p.Single(r => r.WeenieClassId != 1111111u && r.WeenieClassId != 2222222u).WeenieClassId)
                .OrderBy(id => id)
                .ToList();
            CollectionAssert.AreEqual(new List<uint> { 3333333u, 4444444u }, branchWcids);
        }

        [TestMethod]
        public void NestedBranchDepth_ExceedingMaxDepth_SetsTruncated()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.Goto, "A"));
            var a = MakeSet(EmoteCategory.GotoSet, "A", 1, Branch(EmoteType.Goto, "B"));
            var b = MakeSet(EmoteCategory.GotoSet, "B", 1, Give(4000001));

            var allSets = new List<PropertiesEmote> { root, a, b };

            // root is depth 1, "A" is depth 2 - maxDepth=2 must stop before entering "B"
            var forecast = EmoteRewardForecast.Build(root, allSets, maxDepth: 2);

            Assert.IsTrue(forecast.Truncated);
        }

        [TestMethod]
        public void NestedBranchDepth_WithinMaxDepth_CompletesNormally()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.Goto, "A"));
            var a = MakeSet(EmoteCategory.GotoSet, "A", 1, Branch(EmoteType.Goto, "B"));
            var b = MakeSet(EmoteCategory.GotoSet, "B", 1, Give(4000001));

            var allSets = new List<PropertiesEmote> { root, a, b };

            var forecast = EmoteRewardForecast.Build(root, allSets, maxDepth: 8);

            Assert.IsFalse(forecast.Truncated);
            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(4000001u, forecast.Paths[0].Single().WeenieClassId);
        }

        [TestMethod]
        public void Cycle_GotoAToBToA_SetsTruncated()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.Goto, "A"));
            var a = MakeSet(EmoteCategory.GotoSet, "A", 1, Branch(EmoteType.Goto, "B"));
            var b = MakeSet(EmoteCategory.GotoSet, "B", 1, Branch(EmoteType.Goto, "A"));

            var allSets = new List<PropertiesEmote> { root, a, b };

            var forecast = EmoteRewardForecast.Build(root, allSets, maxDepth: 50, maxPaths: 50);

            Assert.IsTrue(forecast.Truncated);
        }

        [TestMethod]
        public void MaxPathsOverflow_SetsTruncated()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqQuest, "TestQuest"));
            var s1 = MakeSet(EmoteCategory.QuestSuccess, "TestQuest", 1, Give(5000001));
            var s2 = MakeSet(EmoteCategory.QuestFailure, "TestQuest", 1, Give(5000002));

            var allSets = new List<PropertiesEmote> { root, s1, s2 };

            var forecast = EmoteRewardForecast.Build(root, allSets, maxPaths: 1);

            Assert.IsTrue(forecast.Truncated);
            Assert.IsTrue(forecast.Paths.Count <= 1);
        }

        [TestMethod]
        public void BranchWithNoMatchingCandidates_ContributesNothing_PathContinues()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1,
                Branch(EmoteType.InqQuest, "NoSuchQuest"),
                Give(6000001));

            var allSets = new List<PropertiesEmote> { root };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(1, forecast.Paths[0].Count);
            Assert.AreEqual(6000001u, forecast.Paths[0][0].WeenieClassId);
            Assert.IsFalse(forecast.Truncated);
        }

        [TestMethod]
        public void ProbabilityZeroOrLess_CandidateExcluded()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqQuest, "TestQuest"));
            var deadCandidate = MakeSet(EmoteCategory.QuestSuccess, "TestQuest", 0, Give(7000001));
            var liveCandidate = MakeSet(EmoteCategory.QuestFailure, "TestQuest", 1, Give(7000002));

            var allSets = new List<PropertiesEmote> { root, deadCandidate, liveCandidate };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(1, forecast.Paths.Count);
            Assert.AreEqual(7000002u, forecast.Paths[0].Single().WeenieClassId);
        }

        [TestMethod]
        public void CaseInsensitiveQuestNameMatch()
        {
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqQuest, "TestQuest"));
            var success = MakeSet(EmoteCategory.QuestSuccess, "TESTQUEST", 1, Give(8000001));
            var failure = MakeSet(EmoteCategory.QuestFailure, "testquest", 1, Give(8000002));

            var allSets = new List<PropertiesEmote> { root, success, failure };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(2, forecast.Paths.Count);
        }

        [TestMethod]
        public void InqFellowQuest_BranchesToQuestNoFellow_RewardDiscovered()
        {
            // EmoteManager.cs:527-544: InqFellowQuest fires QuestNoFellow when the player has no
            // fellowship - a reward living only in that set must still be found.
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqFellowQuest, "GroupQuest"));
            var success = MakeSet(EmoteCategory.QuestSuccess, "GroupQuest", 1, Give(9100001));
            var failure = MakeSet(EmoteCategory.QuestFailure, "GroupQuest", 1, Give(9100002));
            var noFellow = MakeSet(EmoteCategory.QuestNoFellow, "GroupQuest", 1, Give(9100003));

            var allSets = new List<PropertiesEmote> { root, success, failure, noFellow };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(3, forecast.Paths.Count);
            Assert.IsFalse(forecast.Truncated);

            var wcids = forecast.Paths.Select(p => p.Single().WeenieClassId).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(new List<uint> { 9100001u, 9100002u, 9100003u }, wcids);
        }

        [TestMethod]
        public void InqIntStat_BranchesToTestNoQuality_RewardDiscovered()
        {
            // EmoteManager.cs:617-632: InqIntStat falls back to TestNoQuality when the inquired stat is
            // null and a TestNoQuality set exists - a reward living only there must still be found.
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqIntStat, "StatCheck"));
            var success = MakeSet(EmoteCategory.TestSuccess, "StatCheck", 1, Give(9200001));
            var failure = MakeSet(EmoteCategory.TestFailure, "StatCheck", 1, Give(9200002));
            var noQuality = MakeSet(EmoteCategory.TestNoQuality, "StatCheck", 1, Give(9200003));

            var allSets = new List<PropertiesEmote> { root, success, failure, noQuality };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(3, forecast.Paths.Count);
            Assert.IsFalse(forecast.Truncated);

            var wcids = forecast.Paths.Select(p => p.Single().WeenieClassId).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(new List<uint> { 9200001u, 9200002u, 9200003u }, wcids);
        }

        [TestMethod]
        public void NullMessageBranch_MatchesAllCandidatesInCategory_RegardlessOfQuest()
        {
            // EmoteManager.GetEmoteSet (EmoteManager.cs:1590-1591): a null questName applies NO quest
            // filter at all, so every set in the target category is a candidate - even two candidates
            // with different non-null Quest values must both be discovered.
            var root = MakeSet(EmoteCategory.Give, null, 1, Branch(EmoteType.InqQuest, null));
            var candidateA = MakeSet(EmoteCategory.QuestSuccess, "QuestA", 1, Give(9300001));
            var candidateB = MakeSet(EmoteCategory.QuestSuccess, "QuestB", 1, Give(9300002));

            var allSets = new List<PropertiesEmote> { root, candidateA, candidateB };

            var forecast = EmoteRewardForecast.Build(root, allSets);

            Assert.AreEqual(2, forecast.Paths.Count);
            Assert.IsFalse(forecast.Truncated);

            var wcids = forecast.Paths.Select(p => p.Single().WeenieClassId).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(new List<uint> { 9300001u, 9300002u }, wcids);
        }
    }
}

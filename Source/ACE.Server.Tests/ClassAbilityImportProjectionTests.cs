using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure projection behind GET .../class-abilities. No Player is built (the harness cannot), so these pin
    /// the row-to-block rules: canonical ids, clamping, retired routing, and the quest-name filter.
    /// </summary>
    [TestClass]
    public class ClassAbilityImportProjectionTests
    {
        private static ClassAbilityDefinition Implemented() => ClassAbilityRegistry.Abilities.Values.First(d => d.Implemented && d.MaxRank >= 2);

        private static ClassAbilityImportBlock Project(params ImportQuestRow[] rows)
            => ClassAbilityImportProjection.Project(rows, 0, 0, 0, 0, false);

        [TestMethod]
        public void MixedCaseRowKey_EmitsTheCanonicalDefinitionName()
        {
            var def = Implemented();
            var stored = ClassAbilityRegistry.QuestKeyPrefix.ToUpperInvariant() + def.Name.ToUpperInvariant();

            var block = Project(new ImportQuestRow(stored, 1));

            Assert.AreEqual(1, block.Ranks.Count);
            Assert.AreEqual(def.Name, block.Ranks[0].Id, "canonical def.Name, never the stored spelling");
            Assert.AreEqual(1, block.Ranks[0].Rank);
            Assert.AreEqual(0, block.RetiredRanks.Count);
        }

        [TestMethod]
        public void OverMaxRank_IsClampedToMaxRank()
        {
            var def = Implemented();

            var block = Project(new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + def.Name, def.MaxRank + 5));

            Assert.AreEqual(def.MaxRank, block.Ranks.Single().Rank);
        }

        [TestMethod]
        public void RankZeroAndNegative_AreOmitted()
        {
            var def = Implemented();

            var block = Project(new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + def.Name, 0));

            Assert.AreEqual(0, block.Ranks.Count);
        }

        [TestMethod]
        public void UnimplementedAbilityWithRanks_IsInRanks()
        {
            var def = ClassAbilityRegistry.Abilities.Values.FirstOrDefault(d => !d.Implemented);
            if (def == null)
                Assert.Inconclusive("every registered ability is implemented, so there is no unimplemented row to pin.");

            var block = Project(new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + def.Name, 1));

            Assert.AreEqual(def.Name, block.Ranks.Single().Id);
            Assert.AreEqual(0, block.RetiredRanks.Count);
        }

        [TestMethod]
        public void RetiredRow_GoesOnlyToRetiredRanks_WithTheCanonicalName_AndClampsToItsHistoricalMax()
        {
            var old = RetiredClassAbilities.All().First();

            var block = Project(new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + old.Name.ToUpperInvariant(), old.MaxRank + 4));

            Assert.AreEqual(0, block.Ranks.Count, "a retired row must never reach ranks");
            Assert.AreEqual(old.Name, block.RetiredRanks.Single().Id);
            Assert.AreEqual(old.MaxRank, block.RetiredRanks.Single().Rank);
        }

        [TestMethod]
        public void UnknownNonRetiredAbilityRow_IsInNeitherList_NorTheQuestSet()
        {
            var block = Project(new ImportQuestRow("ClassAbility_no_such_ability_xyz", 2));

            Assert.AreEqual(0, block.Ranks.Count);
            Assert.AreEqual(0, block.RetiredRanks.Count);
            Assert.AreEqual(0, block.CompletedQuests.Count);
        }

        [TestMethod]
        public void NoClassAbilityData_YieldsEmptyListsAndZeros()
        {
            var block = ClassAbilityImportProjection.Project(new List<ImportQuestRow>(), 0, 0, 0, 0, false);

            Assert.AreEqual(0, block.Ranks.Count);
            Assert.AreEqual(0, block.RetiredRanks.Count);
            Assert.AreEqual(0, block.LumPointsBought);
            Assert.AreEqual(0, block.XpPointsBought);
            Assert.AreEqual(0, block.PointsEarned);
            Assert.AreEqual(0, block.PointsAvailable);
            Assert.IsFalse(block.RespecPending);
        }

        [TestMethod]
        public void ScalarInputs_PassThrough()
        {
            var block = ClassAbilityImportProjection.Project(null, 7, 3, 40, 11, true);

            Assert.AreEqual(7, block.LumPointsBought);
            Assert.AreEqual(3, block.XpPointsBought);
            Assert.AreEqual(40, block.PointsEarned);
            Assert.AreEqual(11, block.PointsAvailable);
            Assert.IsTrue(block.RespecPending);
        }

        [TestMethod]
        public void CompletedQuests_NeedAtLeastOneCompletion_AndExcludeAbilityRows()
        {
            var def = Implemented();

            var block = Project(
                new ImportQuestRow("DoneQuest", 1),
                new ImportQuestRow("StampedNotDone", 0),
                new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + def.Name, 1));

            CollectionAssert.AreEquivalent(new[] { "DoneQuest" }, block.CompletedQuests.ToArray());
        }

        [TestMethod]
        public void FilterCompleted_EchoesRequestedSpelling_IgnoresMalformed_AndDedupes()
        {
            var block = Project(new ImportQuestRow("alpha_quest", 1), new ImportQuestRow("Beta", 2), new ImportQuestRow("bad name", 1), new ImportQuestRow("x-y", 1));

            var result = ClassAbilityImportProjection.FilterCompleted(block, new[] { "ALPHA_QUEST", "bad name", "alpha_quest", "BETA", "gamma", "", "x-y" });

            CollectionAssert.AreEqual(new[] { "ALPHA_QUEST", "BETA" }, result);
        }

        [TestMethod]
        public void FilterCompleted_ReadsAtMost32Values()
        {
            var rows = Enumerable.Range(0, 40).Select(i => new ImportQuestRow($"q{i}", 1)).ToArray();
            var block = Project(rows);

            var result = ClassAbilityImportProjection.FilterCompleted(block, Enumerable.Range(0, 40).Select(i => $"q{i}"));

            Assert.AreEqual(ClassAbilityImportProjection.MaxQuestParams, result.Count);
            Assert.IsFalse(result.Contains("q32"), "the 33rd value is ignored");
        }

        [TestMethod]
        public void QuestParamPattern_AcceptsWordCharsUpTo64_RejectsTheRest()
        {
            Assert.IsTrue(ClassAbilityImportProjection.IsValidQuestParam(new string('a', 64)));
            Assert.IsFalse(ClassAbilityImportProjection.IsValidQuestParam(new string('a', 65)));
            Assert.IsFalse(ClassAbilityImportProjection.IsValidQuestParam(""));
            Assert.IsFalse(ClassAbilityImportProjection.IsValidQuestParam("a b"));
            Assert.IsFalse(ClassAbilityImportProjection.IsValidQuestParam("a\n"), "$ must not accept a trailing newline");
            Assert.IsFalse(ClassAbilityImportProjection.IsValidQuestParam(null));
        }
    }
}

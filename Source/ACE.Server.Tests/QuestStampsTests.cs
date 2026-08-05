using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Eligibility, de-duplication and reward-threshold boundaries for the quest stamp system
    /// (<see cref="QuestStamps"/>): internal bookkeeping registry rows must never be worth a stamp, the login
    /// backfill must count one stamp per distinct quest regardless of case, and the "next reward" lookup must
    /// return the next threshold not yet reached (an account sitting exactly on a threshold has reached it).
    /// </summary>
    [TestClass]
    public class QuestStampsTests
    {
        [TestMethod]
        public void IsEligible_NormalQuestName()
        {
            Assert.IsTrue(QuestStamps.IsEligible("UnbiddenDoorHoltburg"));
        }

        [TestMethod]
        public void IsEligible_NullOrEmptyIsFalse()
        {
            Assert.IsFalse(QuestStamps.IsEligible(null));
            Assert.IsFalse(QuestStamps.IsEligible(""));
        }

        [TestMethod]
        public void IsEligible_ExcludedPrefixRejected()
        {
            Assert.IsFalse(QuestStamps.IsEligible("ClassAbility_Berserk"));
        }

        [TestMethod]
        public void IsEligible_ExcludedPrefixIsCaseInsensitive()
        {
            Assert.IsFalse(QuestStamps.IsEligible("classability_Berserk"));
            Assert.IsFalse(QuestStamps.IsEligible("CLASSABILITY_BERSERK"));
        }

        [TestMethod]
        public void IsEligible_PrefixOnlyMatchesAtTheStart()
        {
            // the exclusion is a prefix rule, not a substring rule
            Assert.IsTrue(QuestStamps.IsEligible("MyClassAbility_Quest"));
        }

        [TestMethod]
        public void IsEligible_RewardTierFlagsRejected()
        {
            // the reward claim flags this system writes itself must never be worth a stamp, or redeeming a
            // tier self-grants progress toward the next one
            Assert.IsFalse(QuestStamps.IsEligible("QuestStampTier1"));
            Assert.IsFalse(QuestStamps.IsEligible("QuestStampTier2"));
            Assert.IsFalse(QuestStamps.IsEligible("QuestStampTier3"));
            Assert.IsFalse(QuestStamps.IsEligible("QuestStampTierX"));
            Assert.IsFalse(QuestStamps.IsEligible("queststamptier1"));
        }

        [TestMethod]
        public void IsEligible_StrayContractAcceptedRowsRejected()
        {
            // tombstone: the contract-pickup hook that wrote these was reverted the same day it was written
            // and never reached master, but rows may exist on dev shards - they must never be counted
            Assert.IsFalse(QuestStamps.IsEligible("ContractAccepted_318"));
            Assert.IsFalse(QuestStamps.IsEligible("contractaccepted_318"));
        }

        [TestMethod]
        public void IsEligible_LedgerRowsRejected()
        {
            // the ledger rows this system writes to record an already-paid quest, and the one-time seed marker
            // beside them, must never be worth a stamp themselves
            Assert.IsFalse(QuestStamps.IsEligible(QuestStamps.LedgerName("AlphaQuest")));
            Assert.IsFalse(QuestStamps.IsEligible("questStampSeen_AlphaQuest"));
            Assert.IsFalse(QuestStamps.IsEligible(QuestStamps.LedgerSeededMarker));
        }

        [TestMethod]
        public void IsSystemRowName_CoversEveryRowThisSystemWrites()
        {
            // content is refused permission to erase these, so the predicate has to catch all three families
            Assert.IsTrue(QuestStamps.IsSystemRowName(QuestStamps.LedgerName("AlphaQuest")));
            Assert.IsTrue(QuestStamps.IsSystemRowName(QuestStamps.LedgerSeededMarker));
            Assert.IsTrue(QuestStamps.IsSystemRowName("QuestStampTier1"));
            Assert.IsTrue(QuestStamps.IsSystemRowName("queststamptier1"));
        }

        [TestMethod]
        public void IsSystemRowName_LeavesContentQuestsAlone()
        {
            Assert.IsFalse(QuestStamps.IsSystemRowName("CraftingForgeUsed1204"));
            Assert.IsFalse(QuestStamps.IsSystemRowName("MyQuestStampish"));
            Assert.IsFalse(QuestStamps.IsSystemRowName(null));
            Assert.IsFalse(QuestStamps.IsSystemRowName(""));
        }

        [TestMethod]
        public void LedgerName_PrefixesTheQuestName()
        {
            Assert.AreEqual("QuestStampSeen_AlphaQuest", QuestStamps.LedgerName("AlphaQuest"));
        }

        [TestMethod]
        public void CanLedger_RejectsNamesTooLongForTheRegistryColumn()
        {
            // quest_Name is varchar(255), so the prefixed name has to fit as well
            var longest = new string('q', QuestStamps.MaxQuestNameLength - QuestStamps.LedgerPrefix.Length);

            Assert.IsTrue(QuestStamps.CanLedger(longest));
            Assert.IsFalse(QuestStamps.CanLedger(longest + "q"));
            Assert.IsFalse(QuestStamps.CanLedger(null));
        }

        [TestMethod]
        public void IsEligible_UnledgerableNameIsNotWorthAStamp()
        {
            // a name that cannot be recorded as paid would earn a fresh stamp on every erase-and-restamp
            // cycle, so eligibility and ledgerability must be the same predicate
            var tooLong = new string('q', QuestStamps.MaxQuestNameLength);

            Assert.IsFalse(QuestStamps.CanLedger(tooLong));
            Assert.IsFalse(QuestStamps.IsEligible(tooLong));
        }

        [TestMethod]
        public void CountEligible_SkipsLedgerRows()
        {
            var names = new[] { "AlphaQuest", QuestStamps.LedgerName("AlphaQuest"), QuestStamps.LedgerSeededMarker, "BetaQuest" };

            Assert.AreEqual(2, QuestStamps.CountEligible(names));
        }

        [TestMethod]
        public void CountEligible_SkipsRewardTierFlags()
        {
            var names = new[] { "AlphaQuest", "QuestStampTier1", "QuestStampTier2", "BetaQuest" };

            Assert.AreEqual(2, QuestStamps.CountEligible(names));
        }

        [TestMethod]
        public void CountEligible_DedupesCaseInsensitively()
        {
            var names = new[] { "AlphaQuest", "alphaquest", "ALPHAQUEST", "BetaQuest" };

            Assert.AreEqual(2, QuestStamps.CountEligible(names));
        }

        [TestMethod]
        public void CountEligible_SkipsExcludedAndEmptyNames()
        {
            var names = new[] { "AlphaQuest", "ClassAbility_Berserk", "", null, "BetaQuest" };

            Assert.AreEqual(2, QuestStamps.CountEligible(names));
        }

        [TestMethod]
        public void CountEligible_EmptyInput()
        {
            Assert.AreEqual(0, QuestStamps.CountEligible(new string[0]));
            Assert.AreEqual(0, QuestStamps.CountEligible(null));
        }

        [TestMethod]
        public void NextThreshold_BelowFirstTier()
        {
            Assert.AreEqual(100L, QuestStamps.NextThreshold(0).Value);
            Assert.AreEqual(100L, QuestStamps.NextThreshold(99).Value);
        }

        [TestMethod]
        public void NextThreshold_SittingExactlyOnATierAdvances()
        {
            Assert.AreEqual(250L, QuestStamps.NextThreshold(100).Value);
            Assert.AreEqual(500L, QuestStamps.NextThreshold(250).Value);
            Assert.AreEqual(750L, QuestStamps.NextThreshold(500).Value);
        }

        [TestMethod]
        public void NextThreshold_JustBelowATier()
        {
            Assert.AreEqual(250L, QuestStamps.NextThreshold(249).Value);
            Assert.AreEqual(500L, QuestStamps.NextThreshold(499).Value);
            Assert.AreEqual(750L, QuestStamps.NextThreshold(749).Value);
        }

        [TestMethod]
        public void NextThreshold_TopTierReachedIsNull()
        {
            Assert.IsNull(QuestStamps.NextThreshold(750));
            Assert.IsNull(QuestStamps.NextThreshold(751));
        }
    }
}

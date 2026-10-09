using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Eligibility, de-duplication and reward-threshold boundaries for the quest stamp system
    /// (<see cref="QuestStamps"/>): internal bookkeeping registry rows must never be worth a stamp, the login
    /// backfill must count one stamp per distinct quest regardless of case, and the "next reward" lookup must
    /// return the next threshold not yet reached (an account sitting exactly on a threshold has reached it).
    ///
    /// Also covers the account-wide fold: the account total is the count of DISTINCT quest names stamped
    /// anywhere on the account, derived from the union of the per-character QuestStampSeen_ ledger rows, so two
    /// characters completing the same quest are worth one stamp to the account even though each still earns its
    /// own per-character stamp.
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

        /// <summary>
        /// The Threads survey rows are a rolling counter, not a quest. Without the exclusion every
        /// surveyed run printed "You've earned a quest stamp: DynDungeonSurveyWindow" and inflated the
        /// account total on a timer (ruling P2-R35).
        /// </summary>
        [TestMethod]
        public void IsEligible_ThreadDungeonSurveyRowsRejected()
        {
            Assert.IsFalse(QuestStamps.IsEligible("DynDungeonSurveyWindow"));
            Assert.IsFalse(QuestStamps.IsEligible("DynDungeonSurveyCount"));
            Assert.IsFalse(QuestStamps.IsEligible("DynDungeonSurveyTotal"));
            Assert.IsFalse(QuestStamps.IsEligible("dyndungeonsurveycount_filos_doom"));

            // A real quest that merely mentions a dungeon is untouched - the prefix only matches at the start.
            Assert.IsTrue(QuestStamps.IsEligible("FilosDoomDynDungeonSurveyor"));
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
        public void TryGetLedgeredQuestName_StripsThePrefix()
        {
            Assert.IsTrue(QuestStamps.TryGetLedgeredQuestName(QuestStamps.LedgerName("AlphaQuest"), out var questName));
            Assert.AreEqual("AlphaQuest", questName);
        }

        [TestMethod]
        public void TryGetLedgeredQuestName_PrefixMatchIsCaseInsensitive()
        {
            Assert.IsTrue(QuestStamps.TryGetLedgeredQuestName("queststampseen_AlphaQuest", out var questName));
            Assert.AreEqual("AlphaQuest", questName);
        }

        [TestMethod]
        public void TryGetLedgeredQuestName_RejectsNonLedgerRows()
        {
            Assert.IsFalse(QuestStamps.TryGetLedgeredQuestName("AlphaQuest", out _));
            Assert.IsFalse(QuestStamps.TryGetLedgeredQuestName(QuestStamps.LedgerSeededMarker, out _));
            Assert.IsFalse(QuestStamps.TryGetLedgeredQuestName("QuestStampTier1", out _));
            Assert.IsFalse(QuestStamps.TryGetLedgeredQuestName(null, out _));
            Assert.IsFalse(QuestStamps.TryGetLedgeredQuestName("", out _));
        }

        [TestMethod]
        public void TryGetLedgeredQuestName_RejectsABarePrefix()
        {
            // "QuestStampSeen_" with nothing after it records no quest
            Assert.IsFalse(QuestStamps.TryGetLedgeredQuestName(QuestStamps.LedgerPrefix, out _));
        }

        [TestMethod]
        public void CountAccountStamps_LedgerRowAndBareQuestNameCollapse()
        {
            // one character holding the quest AND its ledger row is one stamp, not two
            var character = new[] { "AlphaQuest", QuestStamps.LedgerName("AlphaQuest") };

            Assert.AreEqual(1, QuestStamps.CountAccountStamps(new[] { character }));
        }

        [TestMethod]
        public void CountAccountStamps_SameQuestOnTwoCharactersIsOneStamp()
        {
            // this is the whole point of the account total: alts doing the same quest do not double-grant
            var alpha = new[] { "AlphaQuest", QuestStamps.LedgerName("AlphaQuest") };
            var beta = new[] { "AlphaQuest", QuestStamps.LedgerName("AlphaQuest") };

            Assert.AreEqual(1, QuestStamps.CountAccountStamps(new[] { alpha, beta }));

            // control: the old definition was the SUM of the per-character counts, which is what
            // double-granted. Both characters legitimately keep their own per-character stamp.
            Assert.AreEqual(1, QuestStamps.CountEligible(alpha));
            Assert.AreEqual(1, QuestStamps.CountEligible(beta));
            Assert.AreEqual(2, QuestStamps.CountEligible(alpha) + QuestStamps.CountEligible(beta));
        }

        [TestMethod]
        public void CountAccountStamps_DistinctQuestsOnTwoCharactersBothCount()
        {
            var alpha = new[] { "AlphaQuest", QuestStamps.LedgerName("AlphaQuest") };
            var beta = new[] { "BetaQuest", QuestStamps.LedgerName("BetaQuest") };

            Assert.AreEqual(2, QuestStamps.CountAccountStamps(new[] { alpha, beta }));
        }

        [TestMethod]
        public void CountAccountStamps_DedupesCaseInsensitively()
        {
            var alpha = new[] { "AlphaQuest", QuestStamps.LedgerName("AlphaQuest") };
            var beta = new[] { "alphaquest", QuestStamps.LedgerName("ALPHAQUEST") };

            Assert.AreEqual(1, QuestStamps.CountAccountStamps(new[] { alpha, beta }));
        }

        [TestMethod]
        public void CountAccountStamps_LedgerRowsOverExcludedNamesAreNotAdmitted()
        {
            // a stray ledger row written over an excluded name must not slip past the exclusion by being
            // re-admitted as the name it wraps
            var character = new[]
            {
                QuestStamps.LedgerName("ClassAbility_Berserk"),
                QuestStamps.LedgerName("QuestStampTier1"),
                QuestStamps.LedgerName("ContractAccepted_318"),
            };

            Assert.AreEqual(0, QuestStamps.CountAccountStamps(new[] { character }));
        }

        [TestMethod]
        public void CountAccountStamps_SystemRowsContributeNothing()
        {
            var character = new[] { QuestStamps.LedgerSeededMarker, "QuestStampTier1", "QuestStampTier2", "QuestStampTier3", "QuestStampTier4", "QuestStampTier5" };

            Assert.AreEqual(0, QuestStamps.CountAccountStamps(new[] { character }));
        }

        [TestMethod]
        public void CountAccountStamps_EmptyAccountIsZero()
        {
            Assert.AreEqual(0, QuestStamps.CountAccountStamps(new string[0][]));
            Assert.AreEqual(0, QuestStamps.CountAccountStamps(new[] { new string[0] }));
            Assert.AreEqual(0, QuestStamps.CountAccountStamps(null));
        }

        [TestMethod]
        public void CountAccountStamps_ErasedQuestStillCountsFromItsLedgerRow()
        {
            // content erases the quest flag on completion but never the ledger row beside it, so the ledger
            // row alone has to carry the account's memory of that quest
            var alpha = new[] { QuestStamps.LedgerName("AlphaQuest") };
            var beta = new[] { "BetaQuest", QuestStamps.LedgerName("BetaQuest") };

            Assert.AreEqual(2, QuestStamps.CountAccountStamps(new[] { alpha, beta }));
        }

        [TestMethod]
        public void CountAccountStamps_SkipsExcludedAndEmptyNames()
        {
            var character = new[] { "AlphaQuest", "ClassAbility_Berserk", "", null, "BetaQuest" };

            Assert.AreEqual(2, QuestStamps.CountAccountStamps(new[] { character }));
        }

        [TestMethod]
        public void CollectAccountStamps_AccumulatesAcrossCalls()
        {
            var stamped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            QuestStamps.CollectAccountStamps(new[] { "AlphaQuest" }, stamped);
            QuestStamps.CollectAccountStamps(new[] { QuestStamps.LedgerName("alphaquest"), "BetaQuest" }, stamped);
            QuestStamps.CollectAccountStamps(null, stamped);

            Assert.AreEqual(2, stamped.Count);
            Assert.IsTrue(stamped.Contains("ALPHAQUEST"));
            Assert.IsTrue(stamped.Contains("betaquest"));
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
            Assert.AreEqual(1000L, QuestStamps.NextThreshold(750).Value);
        }

        [TestMethod]
        public void NextThreshold_JustBelowATier()
        {
            Assert.AreEqual(250L, QuestStamps.NextThreshold(249).Value);
            Assert.AreEqual(500L, QuestStamps.NextThreshold(499).Value);
            Assert.AreEqual(750L, QuestStamps.NextThreshold(749).Value);
            Assert.AreEqual(1000L, QuestStamps.NextThreshold(999).Value);
        }

        [TestMethod]
        public void NextThreshold_TopTierReachedIsNull()
        {
            Assert.IsNull(QuestStamps.NextThreshold(1000));
            Assert.IsNull(QuestStamps.NextThreshold(1001));
        }
    }
}

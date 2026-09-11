using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Pure logic for the "quest stamp" system. A character earns one stamp the first time it is ever stamped
    /// for a given quest name, so the count is the number of distinct quests that character has ever touched -
    /// monotonic, never reduced by a quest being erased, decremented or re-solved, and never increased a second
    /// time by the same quest. The account-wide total is the count of distinct quest names stamped anywhere on
    /// the account, derived from the union of the per-character QuestStampSeen_ ledger rows - so two characters
    /// completing the SAME quest are worth one stamp to the account, while each still earns its own
    /// per-character stamp. That account total is what the Quest Stamp Registrar NPC rewards against.
    ///
    /// "Ever stamped" cannot be read off the quest registry, because a registry row is not durable evidence:
    /// retail content erases a quest flag and immediately re-stamps it as its normal way of resetting a
    /// repeatable, which recreates the row from scratch. The durable record is the STAMP LEDGER - a
    /// <see cref="LedgerPrefix"/> row written next to the quest the first time it earns a stamp, which the
    /// content's EraseQuest on the quest itself never touches. See Player_QuestStamps.cs for that wiring.
    ///
    /// Some registry rows are bookkeeping rather than quests (class ability tracking flags, the ledger rows
    /// themselves) and must not be worth a stamp - see <see cref="ExcludedPrefixes"/>. Kept free of
    /// <see cref="WorldObjects.Player"/> state so eligibility, de-duplication and the reward thresholds are
    /// unit-testable in isolation.
    /// </summary>
    public static class QuestStamps
    {
        /// <summary>
        /// Prefix of a stamp ledger row: the durable "this character has already been paid for this quest"
        /// marker, written into the quest registry alongside the quest itself. Excluded from eligibility by
        /// <see cref="ExcludedPrefixes"/>, so a ledger row is never itself worth a stamp.
        /// </summary>
        public const string LedgerPrefix = "QuestStampSeen_";

        /// <summary>
        /// Registry row marking that this character's stamp ledger has been seeded from the quests it already
        /// held (a one-time login pass - see Player_QuestStamps.SeedQuestStampLedger).
        /// </summary>
        public const string LedgerSeededMarker = "QuestStampSeeded";

        /// <summary>
        /// character_properties_quest_registry.quest_Name is varchar(255), so a ledger row name has to fit in
        /// 255 characters too. Longest quest name in the shipped world database is 34.
        /// </summary>
        public const int MaxQuestNameLength = 255;

        /// <summary>
        /// Quest name prefixes that never earn a stamp. These registry rows are internal bookkeeping written by
        /// other systems, not player-facing quests, so counting them would inflate the account total for free.
        /// </summary>
        /// "QuestStamp" covers every row this system writes itself: the reward claim flags QuestStampTier1/2/3/4
        /// the Registrar NPC stamps when a tier is redeemed (counting those would let the reward self-grant
        /// stamps), the QuestStampSeen_ ledger rows, and the QuestStampSeeded marker. No content quest name
        /// starts with "QuestStamp" - the only such names in ace_world are the four tier flags above.
        ///
        /// "ContractAccepted_" is a TOMBSTONE. A ContractManager hook that stamped this flag on contract pickup
        /// was written and reverted the same day (2026-07-24) and never reached master: retail already stamps
        /// at quest hand-out through EmoteType.SetQuestCompletions (the Facility Hub Wardens), so a
        /// contract-pickup hook was non-standard versus other servers. The prefix stays listed so that any rows
        /// created on a dev shard while the hook was briefly live are neutralized - login backfills and recounts
        /// must never count them. Do not reuse this prefix for anything real.
        ///
        /// "DynDungeonSurvey" covers the six internal rows the Threads survey writes per character
        /// (DungeonSurveyRules.Window / Count / Total and their per-dungeon siblings). They are a rolling
        /// counter, not a quest: without this the player sees "You've earned a quest stamp:
        /// DynDungeonSurveyWindow" every time a run is surveyed, and the account total inflates on a timer.
        public static readonly string[] ExcludedPrefixes = { "ClassAbility_", "QuestStamp", "ContractAccepted_", "DynDungeonSurvey" };

        /// <summary>
        /// TRUE when a quest registry row by this name is worth a stamp: a non-empty name that does not carry
        /// one of the <see cref="ExcludedPrefixes"/> and is short enough to carry a ledger row. Prefix matching
        /// is case-insensitive, matching how the quest registry itself compares names.
        ///
        /// The length rule keeps eligibility and ledgerability the same predicate. A name too long to ledger
        /// could not be recorded as paid, so it would earn a fresh stamp on every erase-and-restamp cycle -
        /// exactly the exploit the ledger exists to close. Nothing in the shipped world database comes close to
        /// the limit, so refusing the stamp is the safe side of a case that should never occur.
        public static bool IsEligible(string questName)
        {
            if (string.IsNullOrEmpty(questName))
                return false;

            if (!CanLedger(questName))
                return false;

            foreach (var prefix in ExcludedPrefixes)
            {
                if (questName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// TRUE when a registry row name belongs to this system rather than to content: the tier claim flags,
        /// the ledger rows and the seed marker all share the reserved "QuestStamp" prefix. Content is refused
        /// permission to erase these (see EmoteManager's EraseQuest handling) - an emote that could delete its
        /// own ledger row would re-arm the payout the ledger exists to stop.
        /// </summary>
        public static bool IsSystemRowName(string questName)
        {
            if (string.IsNullOrEmpty(questName))
                return false;

            return questName.StartsWith("QuestStamp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// TRUE when a ledger row for this quest name still fits the registry's quest_Name column.
        /// </summary>
        public static bool CanLedger(string questName)
        {
            if (questName == null)
                return false;

            return LedgerPrefix.Length + questName.Length <= MaxQuestNameLength;
        }

        /// <summary>
        /// The stamp ledger row name for a quest. Only meaningful when <see cref="CanLedger"/> is TRUE.
        /// </summary>
        public static string LedgerName(string questName)
        {
            return LedgerPrefix + questName;
        }

        /// <summary>
        /// Counts how many stamps a set of quest names is worth: eligible names only, de-duplicated
        /// case-insensitively (the registry treats names case-insensitively, so two spellings of one quest are
        /// one stamp). Used for the login backfill of characters that predate the feature.
        /// </summary>
        public static int CountEligible(IEnumerable<string> questNames)
        {
            if (questNames == null)
                return 0;

            return questNames
                .Where(IsEligible)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        /// <summary>
        /// Splits a stamp ledger row name back into the quest name it records, so a ledger row can stand in for
        /// a quest the character no longer holds. TRUE only when the row carries the <see cref="LedgerPrefix"/>
        /// and something follows it. The prefix match is case-insensitive, matching how the quest registry
        /// itself compares names.
        /// </summary>
        public static bool TryGetLedgeredQuestName(string rowName, out string questName)
        {
            questName = null;

            if (string.IsNullOrEmpty(rowName))
                return false;

            if (!rowName.StartsWith(LedgerPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var name = rowName.Substring(LedgerPrefix.Length);

            if (name.Length == 0)
                return false;

            questName = name;

            return true;
        }

        /// <summary>
        /// Folds one character's quest registry row names into an account-wide set of stamped quest names.
        ///
        /// A ledger row contributes the quest name it records, which is the durable evidence: content erases the
        /// QUEST row on completion but never the QuestStampSeen_ row beside it, so a quest a character was paid
        /// for still counts for the account after its flag is gone. A plain eligible quest row also contributes,
        /// which covers a character that has not yet logged in since the ledger shipped and so has no ledger
        /// rows of its own. A ledger row whose stripped name is not itself eligible contributes nothing, so a
        /// stray ledger row over an excluded name cannot inflate the account.
        ///
        /// <paramref name="into"/> MUST be built with <see cref="StringComparer.OrdinalIgnoreCase"/> - the quest
        /// registry compares names case-insensitively, so two spellings of one quest are one stamp.
        /// </summary>
        public static void CollectAccountStamps(IEnumerable<string> registryRowNames, HashSet<string> into)
        {
            if (registryRowNames == null || into == null)
                return;

            foreach (var rowName in registryRowNames)
            {
                if (TryGetLedgeredQuestName(rowName, out var ledgeredQuestName))
                {
                    if (IsEligible(ledgeredQuestName))
                        into.Add(ledgeredQuestName);

                    continue;
                }

                if (IsEligible(rowName))
                    into.Add(rowName);
            }
        }

        /// <summary>
        /// The account-wide stamp total: how many DISTINCT quest names have been stamped anywhere on the
        /// account, given each character's quest registry row names. Two characters that completed the same
        /// quest are worth one, not two.
        /// </summary>
        public static int CountAccountStamps(IEnumerable<IEnumerable<string>> perCharacterRowNames)
        {
            var stamped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (perCharacterRowNames == null)
                return 0;

            foreach (var rowNames in perCharacterRowNames)
                CollectAccountStamps(rowNames, stamped);

            return stamped.Count;
        }

        /// <summary>
        /// Account-wide stamp totals at which a reward tier unlocks, ascending.
        /// </summary>
        public static readonly long[] Thresholds = { 100, 250, 500, 750 };

        /// <summary>
        /// The next reward threshold this account has NOT yet reached, i.e. the smallest threshold strictly
        /// greater than <paramref name="accountTotal"/>. NULL once every tier has been reached (an account
        /// sitting exactly on the top threshold has reached it, so it has no next one).
        /// </summary>
        public static long? NextThreshold(long accountTotal)
        {
            foreach (var threshold in Thresholds)
            {
                if (accountTotal < threshold)
                    return threshold;
            }

            return null;
        }
    }
}

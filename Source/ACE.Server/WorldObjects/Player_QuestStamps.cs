using System;
using System.Linq;

using ACE.Common;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// "Quest stamps" turn quest variety into a currency. A character earns one stamp the first time it is
        /// ever stamped for a given quest name (see QuestManager.Update / QuestManager.SetQuestCompletions,
        /// which call <see cref="HandleQuestStampRowCreated"/> from their created-row branches only), so the
        /// count measures distinct quests ever touched. It is monotonic: erasing, decrementing or re-solving a
        /// quest never reduces it, and re-earning a quest never raises it a second time.
        ///
        /// A newly created registry row is the TRIGGER for a stamp but not the EVIDENCE that one is owed: see
        /// <see cref="HandleQuestStampRowCreated"/> for why. The evidence is the stamp ledger - one
        /// QuestStampSeen_ registry row per quest name this character has already been paid for.
        ///
        /// The reward pool is account-wide - <see cref="AccountQuestStampCount"/> is the sum across every
        /// character on the account, so alts feed one shared total that the Quest Stamp Registrar NPC reads via
        /// an InqInt64Stat emote on stat 9019. That property is [Ephemeral]: it is recomputed at every login
        /// (<see cref="InitQuestStamps"/>) and kept live in memory, never persisted, so it can never drift out
        /// of sync with the per-character counts that are the real record.
        ///
        /// Wiring sites: Player_Networking.PlayerEnterWorld (login init/backfill), QuestManager.Update and
        /// QuestManager.SetQuestCompletions (earning), PlayerCommands "/quests" (display),
        /// PropertyManager "quest_stamps_enabled" (feature gate).
        /// </summary>

        /// <summary>
        /// This character's own stamp count - distinct eligible quests it has ever started. Persisted, monotonic.
        /// </summary>
        public long QuestStampCount
        {
            get => GetProperty(PropertyInt64.QuestStampCount) ?? 0;
            set => SetProperty(PropertyInt64.QuestStampCount, value);
        }

        /// <summary>
        /// The account-wide stamp total (this character plus every other character on the account). Ephemeral:
        /// computed at login and incremented live for the rest of the session.
        /// </summary>
        public long AccountQuestStampCount
        {
            get => GetProperty(PropertyInt64.AccountQuestStampCount) ?? 0;
            set => SetProperty(PropertyInt64.AccountQuestStampCount, value);
        }

        /// <summary>
        /// Backfills this character's stamp count on first login after the feature shipped (counting the quest
        /// registry rows it already has), then computes the account-wide total. Call once at login.
        ///
        /// Concurrency: only one character per account can be online at a time (the same assumption
        /// Player_AltCharacterBonus relies on), so the other characters' counts cannot change while this one is
        /// playing. They are read once here and frozen for the session; only this character's own contribution
        /// moves, via <see cref="HandleQuestStampRowCreated"/>.
        /// </summary>
        public void InitQuestStamps()
        {
            if (!PropertyManager.GetBool("quest_stamps_enabled").Item)
                return;

            // backfill: a character that predates the feature has no stored count, so derive one from the
            // quest registry rows it already has. A stored 0 is a real answer and is left alone.
            if (GetProperty(PropertyInt64.QuestStampCount) == null)
            {
                var ownQuestNames = QuestManager.GetQuests().Select(q => q.QuestName);

                QuestStampCount = QuestStamps.CountEligible(ownQuestNames);
            }

            SeedQuestStampLedger();

            var total = QuestStampCount;

            var accountId = Account?.AccountId ?? 0;

            if (accountId != 0)
            {
                foreach (var sibling in PlayerManager.GetAccountPlayersSnapshot(accountId))
                {
                    if (sibling.Guid.Full == Guid.Full)
                        continue;

                    if (sibling.IsDeleted || sibling.IsPendingDeletion)
                        continue;

                    var siblingCount = sibling.GetProperty(PropertyInt64.QuestStampCount);

                    // sibling has never logged in since the feature shipped, so it has no stored count yet -
                    // derive one from the character rows this session already loaded at connect. Read-only:
                    // the sibling is backfilled for real the next time it logs in.
                    if (siblingCount == null)
                        siblingCount = GetSiblingQuestStampCountFromSession(sibling.Guid.Full);

                    total += siblingCount ?? 0;
                }
            }

            AccountQuestStampCount = total;
        }

        /// <summary>
        /// Counts the eligible quest registry rows of another character on this account, using the Character
        /// list the session loaded at connect. Returns NULL when that character is not in the session list
        /// (nothing to count from), which the caller treats as 0. Strictly read-only - nothing on the sibling
        /// Character is mutated.
        /// </summary>
        private long? GetSiblingQuestStampCountFromSession(uint characterId)
        {
            var character = Session?.Characters?.FirstOrDefault(c => c.Id == characterId);

            if (character == null)
                return null;

            var questNames = character.CharacterPropertiesQuestRegistry.Select(q => q.QuestName);

            return QuestStamps.CountEligible(questNames);
        }

        /// <summary>
        /// Called when a quest registry row is newly created for this character. Awards one stamp the FIRST
        /// time this character is stamped for an eligible quest, bumping both its persisted count and the
        /// account-wide running total. <paramref name="notify"/> controls whether the player sees a chat
        /// message. Both live callers (QuestManager.Update and QuestManager.SetQuestCompletions) pass TRUE:
        /// SetQuestCompletions is not the silent bookkeeping path it looks like, it is how retail content
        /// stamps a quest at HAND-OUT (the Facility Hub Wardens do exactly this), so suppressing its message
        /// would hide a stamp the player genuinely earned. The parameter is kept for any future path that
        /// really does need to count a row without announcing it.
        /// </summary>
        public void HandleQuestStampRowCreated(string questName, bool notify)
        {
            if (!PropertyManager.GetBool("quest_stamps_enabled").Item)
                return;

            if (!QuestStamps.IsEligible(questName))
                return;

            // A newly created registry row does NOT mean a first completion. Erasing a quest flag and
            // immediately re-stamping it is retail content's ordinary way of resetting a repeatable: the
            // crafting forges (wcids 30460/30461/30465/30466/30467) run EraseQuest then StampQuest on
            // CraftingForgeUsed1204 on every single use, and 957 quest names in ace_world carry both an
            // EraseQuest and a StampQuest emote. Each cycle deletes the row and creates it fresh, so
            // trusting row creation alone paid a stamp per forge use without limit (alpha report, 2026-07-31).
            //
            // The ledger row is what survives that: content erases the QUEST, never the QuestStampSeen_ row
            // beside it, so it is a permanent record of "this character has already been paid for this quest".
            if (HasQuestStampLedgerEntry(questName))
                return;

            if (!TryWriteQuestStampLedgerEntry(questName))
                return;

            // the caller (QuestManager) has already set CharacterChangesDetected for the registry row itself;
            // the count lives on the biota, whose SetProperty flags its own change.
            QuestStampCount++;

            var accountTotal = AccountQuestStampCount + 1;
            AccountQuestStampCount = accountTotal;

            // ChatMessageType.Advancement (0x0D) is user-picked from the /chatcolors palette probe
            // (2026-07-24): it renders RGB(62,220,220), the only true teal the client offers for this message
            // class, and it is thematically right - Advancement is the client's progression channel, which is
            // exactly what a quest stamp is. Do NOT re-derive this from the enum's doc comments: they describe
            // a different message class and were wrong twice here (0x1E "light cyan" renders dark navy; Tell
            // renders yellow, not the cyan of a real NPC tell). See ChatMessageType.cs for the verified table.
            if (notify && Session != null)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You've earned a quest stamp: {questName}. (Account total: {accountTotal})", ChatMessageType.Advancement));
        }

        /// <summary>
        /// TRUE when this character has already been paid a stamp for this quest, whether or not it still holds
        /// the quest itself.
        /// </summary>
        private bool HasQuestStampLedgerEntry(string questName)
        {
            if (Character == null || !QuestStamps.CanLedger(questName))
                return false;

            return Character.GetQuest(QuestStamps.LedgerName(questName), CharacterDatabaseLock) != null;
        }

        /// <summary>
        /// Records that this character has been paid a stamp for this quest. Returns TRUE only when the ledger
        /// row was newly written, i.e. when a stamp is genuinely owed; a FALSE return must suppress the award,
        /// so that a stamp is never granted without the record that stops it being granted again.
        /// </summary>
        private bool TryWriteQuestStampLedgerEntry(string questName)
        {
            if (Character == null || !QuestStamps.CanLedger(questName))
                return false;

            return WriteQuestStampRegistryRow(QuestStamps.LedgerName(questName));
        }

        /// <summary>
        /// Writes one of this system's own bookkeeping rows straight into the quest registry, bypassing
        /// QuestManager. Going through QuestManager.Update would re-enter <see cref="HandleQuestStampRowCreated"/>
        /// for the row being written. Returns FALSE when the row already existed.
        /// </summary>
        private bool WriteQuestStampRegistryRow(string rowName)
        {
            var row = Character.GetOrCreateQuest(rowName, CharacterDatabaseLock, out var created);

            if (!created)
                return false;

            row.CharacterId = Guid.Full;
            row.LastTimeCompleted = (uint)Time.GetUnixTime();
            row.NumTimesCompleted = 1;

            CharacterChangesDetected = true;

            return true;
        }

        /// <summary>
        /// One-time login pass that seeds the stamp ledger from the quests this character already holds.
        /// Without it, every character created before the ledger existed - which is every character that played
        /// while the erase-and-restamp exploit was live - would have its next re-stamp of an already-held quest
        /// read as brand new and paid again. Guarded by its own marker row so it runs once per character.
        ///
        /// Deliberately does NOT correct a count the exploit already inflated: the honest total is not
        /// recoverable from the registry, because content legitimately erases quest flags on completion, so a
        /// recount would silently confiscate real progress as well as farmed progress. Seeding stops the bleed;
        /// existing totals stand.
        ///
        /// Known residual, from the same unrecoverable history: the seed can only see quests the character
        /// still HOLDS. A quest a legacy character was already paid for, whose flag content erased on
        /// completion, gets no ledger row here and will be paid once more if it is ever stamped again. That is
        /// bounded at one extra stamp per quest name and does not reopen the unlimited loop - the erase-restamp
        /// cycles that drove the exploit leave the quest row in place, so those ARE seeded.
        /// </summary>
        private void SeedQuestStampLedger()
        {
            if (Character == null)
                return;

            if (Character.GetQuest(QuestStamps.LedgerSeededMarker, CharacterDatabaseLock) != null)
                return;

            var heldQuestNames = QuestManager.GetQuests()
                .Select(q => q.QuestName)
                .Where(QuestStamps.IsEligible)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var questName in heldQuestNames)
                TryWriteQuestStampLedgerEntry(questName);

            WriteQuestStampRegistryRow(QuestStamps.LedgerSeededMarker);
        }
    }
}

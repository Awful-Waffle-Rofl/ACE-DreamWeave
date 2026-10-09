using System;
using System.Collections.Generic;
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
        /// The reward pool is account-wide and counts quests, not completions - <see cref="AccountQuestStampCount"/>
        /// is the count of distinct quest names stamped anywhere on the account, derived from the union of the
        /// per-character QuestStampSeen_ ledger rows. Two characters completing the SAME quest are worth one
        /// stamp to the account, while each still earns its own per-character <see cref="QuestStampCount"/>, so
        /// alts broaden the shared pool rather than doubling it. The Quest Stamp Registrar NPC reads that total
        /// via an InqInt64Stat emote on stat 9019. The property is [Ephemeral]: it is recomputed at every login
        /// (<see cref="InitQuestStamps"/>) and kept live in memory, never persisted, so it can never drift out
        /// of sync with the ledger rows that are the real record.
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
        /// The account-wide stamp total: how many DISTINCT quest names have been stamped anywhere on this
        /// account. Ephemeral: computed at login from <see cref="accountStampedQuests"/> and incremented live
        /// for the rest of the session, only when this character stamps a quest no character on the account has
        /// ever been stamped for.
        /// </summary>
        public long AccountQuestStampCount
        {
            get => GetProperty(PropertyInt64.AccountQuestStampCount) ?? 0;
            set => SetProperty(PropertyInt64.AccountQuestStampCount, value);
        }

        /// <summary>
        /// Every quest name stamped anywhere on this account, case-insensitive. Built once at login from the
        /// union of the account's characters' quest registries and kept live for the session: it is the memory
        /// that stops a quest an alt already completed from raising the account total a second time.
        ///
        /// Not persisted and not shared between sessions - only one character per account can be online at a
        /// time, so no other character's registry can change while this one is playing.
        /// </summary>
        private HashSet<string> accountStampedQuests;

        /// <summary>
        /// Guarantees <see cref="accountStampedQuests"/> exists AND that <see cref="AccountQuestStampCount"/>
        /// agrees with it. Normally a no-op, because <see cref="InitQuestStamps"/> built both at login; it does
        /// real work only when login skipped that, i.e. quest_stamps_enabled was FALSE at login and was turned
        /// on mid-session. Both halves matter: materializing the set without resyncing the count would leave
        /// the count at its default 0 and thereafter only ever add 1 to it, so the player and the Quest Stamp
        /// Registrar would both read a total far below the truth for the rest of the session.
        ///
        /// MUST run BEFORE this character's ledger row for the quest being stamped is written. Player.Character
        /// is the SAME object instance the session holds in Session.Characters - CharacterHandler looks the
        /// character up in that list (CharacterHandler.cs:217) and hands the instance to the Player constructor
        /// (Player.cs:133) - so a build that ran after the write would read the very row it is meant to predate,
        /// conclude some character on the account had already been stamped for the quest, and refuse a stamp
        /// that is genuinely the account's first.
        /// </summary>
        private void EnsureAccountStampState()
        {
            if (accountStampedQuests != null)
                return;

            accountStampedQuests = BuildAccountStampedQuests();

            AccountQuestStampCount = accountStampedQuests.Count;
        }

        /// <summary>
        /// Backfills this character's stamp count on first login after the feature shipped (counting the quest
        /// registry rows it already has), then computes the account-wide total. Call once at login.
        ///
        /// Concurrency: only one character per account can be online at a time (the same assumption
        /// Player_AltCharacterBonus relies on), so the other characters' registries cannot change while this
        /// one is playing. They are read once here and frozen for the session; only this character's own
        /// contribution moves, via <see cref="HandleQuestStampRowCreated"/>.
        ///
        /// Statement order matters: <see cref="SeedQuestStampLedger"/> must run BEFORE the account pass,
        /// because it writes this character's ledger rows into the very Character object that pass then reads.
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

            accountStampedQuests = BuildAccountStampedQuests();

            AccountQuestStampCount = accountStampedQuests.Count;
        }

        /// <summary>
        /// Unions the quest registry row names of every live character on this account into one case-insensitive
        /// set of stamped quest names. Costs no database work: the session already holds every character on the
        /// account with its full quest registry, eagerly loaded by ShardDatabase.GetCharacterList at connect.
        ///
        /// Sibling Character objects are STRICTLY READ-ONLY here - a ledger row is only ever written to this
        /// character's own Character (see <see cref="WriteQuestStampRegistryRow"/>).
        ///
        /// Falls back to this character's own rows when the session list is unavailable, so a character with no
        /// session context still gets a correct-for-itself total rather than zero.
        /// </summary>
        private HashSet<string> BuildAccountStampedQuests()
        {
            var stamped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var characters = Session?.Characters;

            if (characters != null && characters.Count > 0)
            {
                foreach (var character in characters)
                {
                    // a character queued for deletion is not part of the account's pool any more
                    if (character.IsDeleted || character.DeleteTime > 0)
                        continue;

                    QuestStamps.CollectAccountStamps(character.CharacterPropertiesQuestRegistry.Select(q => q.QuestName), stamped);
                }
            }
            else if (Character != null)
            {
                QuestStamps.CollectAccountStamps(Character.CharacterPropertiesQuestRegistry.Select(q => q.QuestName), stamped);
            }

            return stamped;
        }

        /// <summary>
        /// Called when a quest registry row is newly created for this character. Awards one stamp the FIRST
        /// time this character is stamped for an eligible quest, bumping its persisted count; the account-wide
        /// total moves only when NO character on the account has ever been stamped for that quest, so an alt
        /// repeating a quest a sibling already did is worth a per-character stamp and nothing to the account.
        /// <paramref name="notify"/> controls whether the player sees a chat
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

            // Before the ledger write below, never after - see EnsureAccountStampState for why the order is
            // load-bearing rather than stylistic.
            EnsureAccountStampState();

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

            // The account total counts DISTINCT quest names, not completions: a quest another character on this
            // account has already been stamped for is worth nothing to the account, even though this character
            // has genuinely earned its own stamp for it above. Add returns FALSE for exactly that case.
            var accountFirst = accountStampedQuests.Add(questName);

            var accountTotal = AccountQuestStampCount;

            if (accountFirst)
            {
                accountTotal++;
                AccountQuestStampCount = accountTotal;
            }

            // ChatMessageType.Advancement (0x0D) is user-picked from the /chatcolors palette probe
            // (2026-07-24): it renders RGB(62,220,220), the only true teal the client offers for this message
            // class, and it is thematically right - Advancement is the client's progression channel, which is
            // exactly what a quest stamp is. Do NOT re-derive this from the enum's doc comments: they describe
            // a different message class and were wrong twice here (0x1E "light cyan" renders dark navy; Tell
            // renders yellow, not the cyan of a real NPC tell). See ChatMessageType.cs for the verified table.
            //
            // The already-claimed case gets its OWN line rather than silence: this system announces every stamp
            // it awards, so saying nothing when the account total does not move would read as a bug.
            if (notify && Session != null)
            {
                var message = accountFirst
                    ? $"You've earned a quest stamp: {questName}. (Account total: {accountTotal})"
                    : $"You've earned a quest stamp: {questName}. Another character on your account already claimed this quest, so the account total is unchanged. (Account total: {accountTotal})";

                Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Advancement));
            }
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

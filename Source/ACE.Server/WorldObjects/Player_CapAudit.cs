using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // Class Ability Point (CAP) audit ledger, round 3: login-time detection.
        //
        // Recomputes the ledger identity (TotalClassAbilityPointsEarned - Available - ownedCost -
        // sinkSpend, see CapLedger.Unexplained) from this character's current counters plus its
        // character_cap_ledger rows, counts orphaned quest-registry rows the rank cache cannot see,
        // and counts rank divergences between each ability's newest known rank_After and its live
        // rank - then upserts one character_cap_audit row.
        //
        // STRICTLY READ-ONLY. This never erases, refunds, or otherwise mutates anything - a detected
        // mismatch is recorded for a human to investigate (see /caaudit, ClassAbilityCommands.cs),
        // never auto-corrected. The manual remediation route is /capadjust, same file.

        /// <summary>
        /// Audits this character's CAP ledger and upserts a `character_cap_audit` summary row.
        ///
        /// Early-returns for the large majority of characters who hold no CAPs at all
        /// (TotalClassAbilityPointsEarned == 0 && AvailableClassAbilityPoints == 0), so login does not
        /// pay a ledger read/write for a character that has never touched the CAP system.
        ///
        /// The ledger read (<see cref="DatabaseManager.Shard"/>'s GetCapLedger) is QUEUED onto
        /// SerializedShardDatabase's single dedicated worker thread, so this audit completes
        /// ASYNCHRONOUSLY, after this method (and PlayerEnterWorld) has already returned - that is fine
        /// and intended, nothing here blocks login.
        ///
        /// THE CALLBACK RUNS ON THAT WORKER THREAD, NOT THE WORLD THREAD. It may only read the plain
        /// value/string locals captured below (characterId, characterName, totalEarned, available,
        /// ownedCost, orphanRows, liveRanksByAbility) and the ledgerRows the read itself returns - it
        /// must NEVER call GetClassAbilityRank, GetClassAbilityCache(), or touch any other Player/
        /// Character state directly. classAbilityCache is a plain unsynchronized Dictionary the world
        /// thread mutates concurrently (the indexer write in ApplyClassAbilityRankCore, the .Remove in
        /// UnlearnClassAbility, and its own lazy first build), so a live read from the worker thread
        /// races those writes - found in code review. liveRanksByAbility below is a genuine snapshot
        /// copy taken synchronously on the world thread before the read is even queued, specifically so
        /// the callback has no path back to that live state.
        ///
        /// Must run AFTER GrantMilestoneClassAbilityPoints, GrantEnlightenmentClassAbilityPoints, and
        /// SweepRetiredClassAbilities in PlayerEnterWorld (Player_Networking.cs) - all three mutate
        /// the CAP counters and/or the quest-registry rows this audit reads, so auditing before them
        /// would measure a state that is about to change.
        /// </summary>
        public void AuditClassAbilityPointLedger()
        {
            if (TotalClassAbilityPointsEarned == 0 && AvailableClassAbilityPoints == 0)
                return;

            var characterId = Guid.Full;
            var characterName = Name;
            var totalEarned = TotalClassAbilityPointsEarned;
            var available = AvailableClassAbilityPoints;
            var ownedCost = ClassAbilityPointsSpentOnOwnedRanks();
            var orphanRows = CountOrphanedClassAbilityQuestRows();

            // A genuine snapshot copy, built HERE on the world thread - never the live cache itself -
            // so CountRankDivergences (called from the worker-thread callback below) has no path back
            // to classAbilityCache. Missing from the snapshot means "not currently owned", which
            // CountRankDivergences treats as rank 0, matching GetClassAbilityRank's own default.
            var liveRanksByAbility = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kvp in GetClassAbilityCache())
            {
                if (ClassAbilityRegistry.Abilities.TryGetValue(kvp.Key, out var def))
                    liveRanksByAbility[def.Name] = kvp.Value;
            }

            DatabaseManager.Shard.GetCapLedger(characterId, ShardDatabase.MaxCapLedgerRows, ledgerRows =>
            {
                // NULL means the read FAILED - distinct from an empty list (genuinely no history).
                // Upserting here anyway would write a sink_Spend/unexplained pair computed from no
                // data at all, which would read as a clean ledger during an incident when nothing was
                // actually examined. See ShardDatabase_CapLedger.cs's read failure contract.
                if (ledgerRows == null)
                {
                    log.Warn($"[CAPLEDGER] {characterName} (0x{characterId:X8}): ledger read failed during the login audit - skipping the audit summary upsert this login.");
                    return;
                }

                var sinkSpend = CapLedger.SinkSpend(ledgerRows);

                var rankDivergences = CapLedger.CountRankDivergences(ledgerRows, liveRanksByAbility);

                var unexplained = CapLedger.Unexplained(totalEarned, available, ownedCost, sinkSpend);

                var now = DateTime.UtcNow;
                var isBalanced = unexplained == 0 && rankDivergences == 0;

                DatabaseManager.Shard.UpsertCapAudit(new CharacterCapAudit
                {
                    CharacterId = characterId,
                    CharacterName = characterName,
                    TotalEarned = totalEarned,
                    Available = available,
                    OwnedCost = ownedCost,
                    SinkSpend = sinkSpend,
                    Unexplained = unexplained,
                    OrphanRows = orphanRows,
                    RankDivergences = rankDivergences,
                    // Only ever sent non-null when currently out of balance - the upsert SQL's
                    // COALESCE(existing, incoming) then locks in whichever timestamp arrives on the
                    // FIRST such upsert and ignores every later one, balanced or not.
                    FirstDetectedAt = CapLedger.FirstDetectedAtToWrite(isBalanced, now),
                    LastCheckedAt = now,
                }, null);

                if (!isBalanced)
                {
                    log.Warn($"[CAPLEDGER] {characterName} (0x{characterId:X8}): CAP audit flagged an imbalance - " +
                        $"totalEarned {totalEarned}, available {available}, ownedCost {ownedCost}, sinkSpend {sinkSpend}, " +
                        $"unexplained {unexplained}, orphanRows {orphanRows}, rankDivergences {rankDivergences}.");
                }
            });
        }

        /// <summary>
        /// Counts `ClassAbility_*` quest registry rows that <see cref="ClassAbilityRegistry.TryGetByQuestKey"/>
        /// cannot resolve. Reuses SweepRetiredClassAbilities's walk shape but is STRICTLY READ-ONLY -
        /// no erase, no refund. Runs after SweepRetiredClassAbilities in PlayerEnterWorld, so any row
        /// that sweep could price via RetiredClassAbilities has already been erased and refunded by
        /// the time this counts; what remains here is genuinely unresolvable and unpriced, which is
        /// exactly what `orphan_Rows` on `character_cap_audit` is for - see
        /// ClassAbilityPointsSpentOnOwnedRanks's doc comment for why the rank cache cannot see these
        /// rows on its own.
        /// </summary>
        private int CountOrphanedClassAbilityQuestRows()
        {
            var orphanRows = 0;

            foreach (var quest in Character.GetQuests(CharacterDatabaseLock))
            {
                if (!quest.QuestName.StartsWith(ClassAbilityRegistry.QuestKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ClassAbilityRegistry.TryGetByQuestKey(quest.QuestName, out _))
                    orphanRows++;
            }

            return orphanRows;
        }
    }
}

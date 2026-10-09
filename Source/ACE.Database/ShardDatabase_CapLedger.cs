using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Class Ability Point (CAP) audit ledger, round 2: the DAO over the two tables round 1 created
    /// (`character_cap_ledger`, `character_cap_audit` - see
    /// Database/Updates/Shard/2026-09-10-00-Add-Character-Cap-Ledger.sql).
    ///
    /// ONE CONTEXT PER CALL, ONE SaveChanges(). Every method here opens its own ShardDbContext and
    /// commits exactly one statement, which is its own implicit transaction.
    ///
    /// NOTHING IN THIS FILE MAY OPEN A USER-INITIATED EF TRANSACTION - the Begin/Commit pair on
    /// context.Database. All three DbContexts turn on EF/Pomelo's resiliency wrapper in OnConfiguring
    /// (ShardDbContext.cs's builder.EnableRetryOnFailure), and with it active
    /// MySqlRetryingExecutionStrategy refuses one outright: "The configured execution strategy
    /// 'MySqlRetryingExecutionStrategy' does not support user-initiated transactions." A bare
    /// SaveChanges() and a bare ExecuteSqlRaw are unaffected, which is why every method below is
    /// exactly one of those. If some future need genuinely has to span statements atomically it must
    /// go through context.Database.CreateExecutionStrategy().Execute(...) with an IDEMPOTENT
    /// delegate, because the strategy may re-run it on a retry. Nothing in this DAO needs one.
    /// (CapLedgerDaoShapeTests asserts the forbidden method name appears nowhere in this file, so it
    /// is deliberately not spelled out above.)
    ///
    /// THESE ARE LEDGER OPERATIONS, NOT BIOTA OPERATIONS. Nothing here is virtual and nothing is
    /// mirrored on ShardDatabaseWithCaching, for the same reason character_speed_run's methods are
    /// not: these two tables have nothing to do with the in-memory biota cache. A CAP ledger row is
    /// never read back to build a WorldObject, never keyed by a biota id the cache tracks, and never
    /// invalidated by a biota save. Overriding them on the caching subclass would add a layer that
    /// could only ever hand a ledger read a stale or disposed context.
    ///
    /// WRITE FAILURE CONTRACT: a ledger write LOGS AND SWALLOWS. It returns false so a caller that
    /// cares can notice, but it never throws, and no caller is expected to unwind on false. This is
    /// the same call the vault audit log makes (ShardDatabase_AccountVault.AddAccountVaultLog):
    /// the ledger is what makes a CAP-shortfall incident answerable, but refusing a player's
    /// purchase, learn or respec because the forensic row could not be written would turn a logging
    /// outage into a gameplay outage. The mutation the row describes has already happened on the
    /// biota by the time we get here. Log loudly so the gap in the ledger is visible.
    ///
    /// READ FAILURE CONTRACT, and it is load-bearing: the two reads return NULL when the read
    /// FAILED and an EMPTY LIST when there genuinely are no rows. Round 3's admin commands depend on
    /// telling those apart - "this character has no recorded CAP history" and "the ledger could not
    /// be read" lead to opposite conclusions during an incident, and answering a transient database
    /// failure with an empty list would report a character as having a clean ledger when nothing was
    /// actually examined. Callers must branch on null before they branch on Count.
    ///
    /// Both reads clamp their limit to <see cref="MaxCapLedgerRows"/>, following the vault log's read
    /// clamp: retention is unbounded, so without it an admin passing int.MaxValue would materialise a
    /// whole table into a List on the calling thread.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// The hard ceiling both CAP ledger reads clamp their limit to, however large a caller asks
        /// for. 1000 rows is far past what an admin command can usefully show in a chat window, and
        /// neither table has any retention policy trimming it.
        /// </summary>
        public const int MaxCapLedgerRows = 1000;

        /// <summary>
        /// The audit-summary upsert. Deliberately a single INSERT ... ON DUPLICATE KEY UPDATE against
        /// the character_Id PRIMARY KEY rather than a read-modify-write: the summary is a full
        /// overwrite of a recomputed snapshot, so there is nothing here a lost update could destroy,
        /// and doing it in one statement means the sweep never holds a row read across a write.
        ///
        /// first_Detected_At is the ONE column that is not overwritten. COALESCE(existing, new)
        /// preserves the first-seen timestamp forever once set, and fills it in on the first upsert
        /// that carries one. Overwriting it would erase the only record of HOW LONG a character has
        /// been out of balance, which is the first question an incident asks.
        ///
        /// Named @parameters rather than {0}..{10} placeholders - see UpsertCapAudit, which binds
        /// @firstDetectedAt with an explicit MySqlParameter so a null FirstDetectedAt (the ordinary
        /// state of a balanced character) binds as DBNull.Value by construction instead of depending
        /// on how EF maps a null CLR argument.
        /// </summary>
        private const string UpsertCapAuditSql =
            "INSERT INTO `character_cap_audit` " +
            "(`character_Id`, `character_Name`, `total_Earned`, `available`, `owned_Cost`, `sink_Spend`, " +
            "`unexplained`, `orphan_Rows`, `rank_Divergences`, `first_Detected_At`, `last_Checked_At`) " +
            "VALUES (@characterId, @characterName, @totalEarned, @available, @ownedCost, @sinkSpend, " +
            "@unexplained, @orphanRows, @rankDivergences, @firstDetectedAt, @lastCheckedAt) " +
            "ON DUPLICATE KEY UPDATE " +
            "`character_Name` = VALUES(`character_Name`), " +
            "`total_Earned` = VALUES(`total_Earned`), " +
            "`available` = VALUES(`available`), " +
            "`owned_Cost` = VALUES(`owned_Cost`), " +
            "`sink_Spend` = VALUES(`sink_Spend`), " +
            "`unexplained` = VALUES(`unexplained`), " +
            "`orphan_Rows` = VALUES(`orphan_Rows`), " +
            "`rank_Divergences` = VALUES(`rank_Divergences`), " +
            "`first_Detected_At` = COALESCE(`first_Detected_At`, VALUES(`first_Detected_At`)), " +
            "`last_Checked_At` = VALUES(`last_Checked_At`)";

        /// <summary>
        /// Appends one CAP mutation to `character_cap_ledger`. Append-only: nothing in gameplay
        /// updates or deletes a row.
        ///
        /// Ts is stamped with DateTime.UtcNow here if the caller left it at default, for the same
        /// reason AddAccountVaultLog stamps its Timestamp. The column carries
        /// HasDefaultValueSql("CURRENT_TIMESTAMP") and EF omits a store-default column from the
        /// INSERT while the property still holds the CLR default, so an unstamped row would silently
        /// take the DATABASE SERVER'S LOCAL time. Every reader treats these as UTC and the reads
        /// below order by id within a character, so a row skewed by the machine's UTC offset would
        /// misreport when a spend happened.
        /// </summary>
        /// <returns>
        /// false when the insert failed. LOGGED AND SWALLOWED - see the class header. A caller must
        /// not unwind the player-facing operation this row describes on a false: that operation has
        /// already been applied to the biota, and refusing it now would trade a missing forensic row
        /// for a corrupted character.
        /// </returns>
        public bool AddCapLedgerRow(CharacterCapLedger row)
        {
            if (row == null)
            {
                log.Error("[CAPLEDGER] AddCapLedgerRow called with a null row.");
                return false;
            }

            if (row.Ts == default)
                row.Ts = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.CharacterCapLedger.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[CAPLEDGER] AddCapLedgerRow failed for character 0x{row.CharacterId:X8}:{row.CharacterName}, reason '{row.Reason}', deltaAvailable {row.DeltaAvailable}, deltaTotal {row.DeltaTotal}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Creates or replaces one character's audit summary row.
        ///
        /// NOT a read-modify-write: the whole statement is <see cref="UpsertCapAuditSql"/>, one
        /// INSERT ... ON DUPLICATE KEY UPDATE with every value parameterised. Reading the row back
        /// first would buy nothing (the summary is a recomputed snapshot, not an accumulator) and
        /// would open a window in which a concurrent sweep's write is lost.
        ///
        /// LastCheckedAt is stamped with DateTime.UtcNow if left at default. FirstDetectedAt is
        /// passed through as given - including null, which is the ordinary state of a balanced
        /// character - and is never overwritten once the row holds one, because the SQL coalesces it.
        /// </summary>
        /// <returns>
        /// false when the upsert failed. LOGGED AND SWALLOWED, same contract as
        /// <see cref="AddCapLedgerRow"/>.
        /// </returns>
        public bool UpsertCapAudit(CharacterCapAudit row)
        {
            if (row == null)
            {
                log.Error("[CAPLEDGER] UpsertCapAudit called with a null row.");
                return false;
            }

            if (row.LastCheckedAt == default)
                row.LastCheckedAt = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Database.ExecuteSqlRaw(
                        UpsertCapAuditSql,
                        new MySqlParameter("@characterId", row.CharacterId),
                        new MySqlParameter("@characterName", row.CharacterName ?? string.Empty),
                        new MySqlParameter("@totalEarned", row.TotalEarned),
                        new MySqlParameter("@available", row.Available),
                        new MySqlParameter("@ownedCost", row.OwnedCost),
                        new MySqlParameter("@sinkSpend", row.SinkSpend),
                        new MySqlParameter("@unexplained", row.Unexplained),
                        new MySqlParameter("@orphanRows", row.OrphanRows),
                        new MySqlParameter("@rankDivergences", row.RankDivergences),
                        new MySqlParameter("@firstDetectedAt", (object)row.FirstDetectedAt ?? DBNull.Value),
                        new MySqlParameter("@lastCheckedAt", row.LastCheckedAt));
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[CAPLEDGER] UpsertCapAudit failed for character 0x{row.CharacterId:X8}:{row.CharacterName}, unexplained {row.Unexplained}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// The most recent CAP ledger rows for one character, NEWEST FIRST.
        ///
        /// Ordered by id descending rather than by ts, deliberately: id is the AUTO_INCREMENT append
        /// order, so it is a total order even when several rows in one respec batch share a
        /// whole-second timestamp, and it can never be reordered by a clock adjustment.
        ///
        /// limit is clamped to between 1 and <see cref="MaxCapLedgerRows"/> inclusive; a caller
        /// asking for more silently gets the ceiling rather than the whole table.
        ///
        /// Returns NULL if the read FAILED and an EMPTY LIST when this character genuinely has no
        /// recorded CAP history - which is the ordinary state of every character that has not
        /// mutated a point since this ledger shipped. See the read failure contract on the class
        /// header for why round 3 must not conflate them.
        /// </summary>
        public List<CharacterCapLedger> GetCapLedger(uint characterId, int limit)
        {
            if (limit < 1)
                limit = 1;
            else if (limit > MaxCapLedgerRows)
                limit = MaxCapLedgerRows;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.CharacterCapLedger
                        .Where(r => r.CharacterId == characterId)
                        .OrderByDescending(r => r.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[CAPLEDGER] GetCapLedger failed for character 0x{characterId:X8}, limit {limit}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Reads one character's `character_cap_audit` row directly by id - the single-row counterpart
        /// to <see cref="GetCapAuditFailures"/>, which only ever returns currently-unbalanced rows and
        /// therefore cannot answer "does THIS character have a recorded row at all, and is it balanced".
        ///
        /// THREE OUTCOMES, not the usual two - a bare null return cannot carry the distinction alone
        /// here, so <paramref name="found"/> is the real signal and must be read first:
        ///   - found == true,  return non-null: the character has a recorded row (its Unexplained may
        ///                                       be zero - a balanced character still has a row once it
        ///                                       has ever been through the login audit or an admin
        ///                                       correction).
        ///   - found == true,  return null:     the read did not fail, but this character has never had
        ///                                       a row written - the ordinary state before its first
        ///                                       audit or upsert.
        ///   - found == false, return null:     the read FAILED. A caller must report this distinctly
        ///                                       from "never audited" - see the class header's read
        ///                                       failure contract, which this method extends to a third
        ///                                       outcome rather than overloading null to mean two of
        ///                                       them.
        /// </summary>
        public CharacterCapAudit GetCapAudit(uint characterId, out bool found)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var row = context.CharacterCapAudit.FirstOrDefault(r => r.CharacterId == characterId);

                    found = true;
                    return row;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[CAPLEDGER] GetCapAudit failed for character 0x{characterId:X8}: {ex.GetFullMessage()}");
                found = false;
                return null;
            }
        }

        /// <summary>
        /// Every audit summary row that is currently out of balance (unexplained &lt;&gt; 0), worst
        /// MAGNITUDE first (largest |unexplained|, so a large overcredit outranks a small shortfall -
        /// an overcredit is equally a defect, not a lesser one), then oldest-detected first.
        ///
        /// This is the incident-sweep read: it answers "which characters are affected right now"
        /// without re-scanning the ledger. A character whose unexplained has since returned to zero
        /// keeps its row (and its first_Detected_At) but drops out of this result, which is the
        /// intended behaviour - the row is history, this list is the open queue.
        ///
        /// limit is clamped to between 1 and <see cref="MaxCapLedgerRows"/> inclusive.
        ///
        /// Returns NULL if the read FAILED and an EMPTY LIST when no character is out of balance -
        /// which is the state a healthy shard is expected to be in, and is therefore exactly the
        /// answer a failed read must never be allowed to counterfeit.
        /// </summary>
        public List<CharacterCapAudit> GetCapAuditFailures(int limit)
        {
            if (limit < 1)
                limit = 1;
            else if (limit > MaxCapLedgerRows)
                limit = MaxCapLedgerRows;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.CharacterCapAudit
                        .Where(r => r.Unexplained != 0)
                        .OrderByDescending(r => Math.Abs(r.Unexplained))
                        .ThenBy(r => r.FirstDetectedAt)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[CAPLEDGER] GetCapAuditFailures failed for limit {limit}: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}

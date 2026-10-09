using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Account-wide banked pyreals - the two account_bank* tables (Docs/AccountBank/DESIGN.md).
    ///
    /// These are ledger operations, NOT biota operations, so like the vault DAO beside them they
    /// deliberately do not run through SerializedShardDatabase's worker thread: putting them there
    /// would give a balance read the biota queue's head-of-line blocking for no benefit. The caller is
    /// AccountBankManager, which serializes per account itself.
    ///
    /// NOTHING in this file may open a user-initiated EF transaction. ShardDbContext.OnConfiguring
    /// enables EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses one outright. Every
    /// statement here is a single ExecuteSqlRaw or a single LINQ read, each of which is its own
    /// implicit transaction and is unaffected. Anything that genuinely had to span statements
    /// atomically would have to go through context.Database.CreateExecutionStrategy().Execute(...)
    /// with an idempotent delegate; nothing here does.
    ///
    /// READ FAILURE CONTRACT: <see cref="GetAccountBankBalance"/> returns NULL when the read FAILED
    /// and 0 when the account genuinely has no pool row. Conflating them is a phantom empty bank: a
    /// transient shard failure would tell a player their money is gone, and - far worse - would let a
    /// caller cache that zero. Nothing may cache a null. <see cref="GetAllAccountBankBalances"/> draws
    /// the same line for the bulk read: NULL is a failed read, an EMPTY dictionary is a successful one
    /// over a table where no account has banked yet.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Applies the signed delta to one account's pool, and refuses to if the result would leave the
        /// signed 64-bit range in either direction. One statement, both guards in the WHERE, so the
        /// database both applies and adjudicates.
        ///
        /// WHY THE BOUNDS ARE PARAMETERS AND NOT ARITHMETIC. The natural spelling of the guards is
        /// `banked_Pyreals + @d &gt;= 0 AND banked_Pyreals &lt;= 9223372036854775807 - @d`, and BOTH halves
        /// of that overflow in MySQL for the exact inputs they exist to reject. `banked_Pyreals + @d`
        /// overflows on the large credit the upper guard is meant to refuse; `9223372036854775807 - @d`
        /// overflows on any negative delta, which is every debit. MySQL raises "BIGINT value is out of
        /// range" rather than evaluating to false, so the guard would throw instead of refusing, and
        /// there is no term ordering that fixes it - a WHERE clause is not required to short-circuit.
        ///
        /// So both bounds are computed in C# by <see cref="TryAdjustAccountBank"/>, where the overflow
        /// is checkable, and arrive here as plain comparison values. The semantics are unchanged: the
        /// upper bound IS long.MaxValue - delta, just evaluated somewhere it cannot trap.
        ///
        /// The guard lives in the WHERE, not in an IF() inside an ON DUPLICATE KEY UPDATE, and that is
        /// load-bearing for the same reason it is on the vault ledger: MySqlConnector's UseAffectedRows
        /// defaults to false and ACE never sets it (ACE.Common/MySqlConfiguration.cs), so the
        /// connection runs with CLIENT_FOUND_ROWS, under which an ON DUPLICATE KEY UPDATE that writes a
        /// row's current values reports 1 rather than 0 - a refused over-withdraw would look like a
        /// success. A WHERE that matches nothing yields zero rows under either setting, because "found"
        /// and "changed" agree at zero.
        /// </summary>
        private const string ApplyAccountBankDeltaSql =
            "UPDATE `account_bank` SET `banked_Pyreals` = `banked_Pyreals` + {1}, `updated_At` = UTC_TIMESTAMP() " +
            "WHERE `account_Id` = {0} AND `banked_Pyreals` >= {2} AND `banked_Pyreals` <= {3}";

        /// <summary>
        /// Ensures a pool row exists for an account without touching its balance. A single
        /// INSERT ... ON DUPLICATE KEY UPDATE against the account_Id PRIMARY KEY, so it is atomic at
        /// the row and idempotent: the duplicate branch assigns the balance to itself and therefore
        /// cannot lose a concurrent delta.
        ///
        /// RUNS ONLY ON THE CREDIT PATH. <see cref="TryAdjustAccountBank"/> attempts the guarded UPDATE
        /// first and reaches this statement only when that matched nothing AND the delta is positive.
        /// It must never run for a debit: a debit against an account with no pool row is a refusal, and
        /// creating a zero row there would let a later read report "balance 0" as an established fact
        /// rather than as an absent row, hiding whether the account was ever folded.
        /// </summary>
        private const string EnsureAccountBankRowSql =
            "INSERT INTO `account_bank` (`account_Id`, `banked_Pyreals`, `updated_At`) VALUES ({0}, 0, UTC_TIMESTAMP()) " +
            "ON DUPLICATE KEY UPDATE `banked_Pyreals` = `banked_Pyreals`";

        /// <summary>
        /// Claims the one-and-only fold of a character's old per-character 9004 balance into its
        /// account's pool. INSERT IGNORE against the character_Guid PRIMARY KEY, so exactly one caller
        /// anywhere ever sees a row inserted, however many times the bulk migration and the per-login
        /// lazy fold race.
        /// </summary>
        private const string ClaimAccountBankFoldSql =
            "INSERT IGNORE INTO `account_bank_fold` (`character_Guid`, `account_Id`, `amount`, `folded_At`) " +
            "VALUES ({0}, {1}, {2}, UTC_TIMESTAMP())";

        /// <summary>
        /// One account's banked pyreal balance.
        ///
        /// Returns NULL if the read FAILED. Returns 0 when the account has no pool row, which is the
        /// ordinary state of an account that has never banked anything. The two must never be
        /// conflated and a null must never be cached: caching a failure as zero shows a player an empty
        /// bank and, if any spend path trusted it, would let the pool be re-credited from nothing.
        /// </summary>
        public long? GetAccountBankBalance(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var row = context.AccountBank.FirstOrDefault(b => b.AccountId == accountId);

                    return row?.BankedPyreals ?? 0;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] GetAccountBankBalance failed for account {accountId}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Every account's banked pyreal balance in one read, as account id to balance.
        ///
        /// Returns NULL if the read FAILED. Returns an EMPTY dictionary when the read succeeded and no
        /// account has a pool row yet, and an account absent from a non-null result has no row, which is
        /// balance 0. This is <see cref="GetAccountBankBalance"/>'s null-versus-zero contract widened to
        /// the whole table, and it matters more here rather than less: the consumers rank and chart these
        /// balances, so a failure conflated with emptiness renders as "every account is broke" - a
        /// leaderboard of nobody, and a telemetry series that records zeros no later read will correct.
        /// Nothing may cache a null.
        ///
        /// FOR RANKING AND TELEMETRY ONLY, never for a spend or an affordability check. It is a bulk
        /// snapshot taken outside AccountBankManager's per-account gate, so an entry can be stale the
        /// moment it is read; only <see cref="TryAdjustAccountBank"/>'s guarded UPDATE adjudicates.
        ///
        /// account_bank is one row per account that has ever banked, so this stays a small read - it is
        /// bounded by account count, not by character count or by history.
        /// </summary>
        public Dictionary<uint, long> GetAllAccountBankBalances()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    // account_Id is the PRIMARY KEY, so ToDictionary cannot hit a duplicate key here.
                    return context.AccountBank.ToDictionary(b => b.AccountId, b => b.BankedPyreals);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] GetAllAccountBankBalances failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Applies a signed delta to one account's pyreal pool, creating the row if absent and the
        /// delta is a credit, and reads the result back.
        ///
        /// The delta is applied BY THE DATABASE, never by a read-modify-write in C#. This is the whole
        /// reason the pool is a table rather than a property: two characters on one account can only
        /// briefly coexist (a logging-out player and a newly entering one - the in-world guard at
        /// CharacterHandler is per-character, not per-account), and a lost update in that window is
        /// duplicated or destroyed currency.
        ///
        /// OVERFLOW IS A REFUSAL, NOT A CLAMP. This replaces Player_Bank's SaturatingAdd for pyreals.
        /// Saturating was the right shape for a property that could not report a failure - a balance
        /// that stops climbing beats one that wraps negative - but a row that can refuse should say so,
        /// and a silent clamp on a deposit destroys the difference.
        ///
        /// newBalance is meaningful ONLY for <see cref="AccountBankAdjustResult.Applied"/>; it is left
        /// at 0 for every other value, and 0 is not a balance there.
        /// </summary>
        public AccountBankAdjustResult TryAdjustAccountBank(uint accountId, long delta, out long newBalance)
        {
            newBalance = 0;

            // Whether the delta is KNOWN to have committed. Set only after a statement reported rows
            // affected, so it is never a guess: it is what separates AppliedCountUnknown (the pyreals
            // moved, we just cannot say to what) from Failed (nobody knows whether they moved).
            var committed = false;

            // long.MinValue has no positive counterpart, so -delta traps and the lower bound below
            // cannot be expressed. No caller can produce it (every debit is derived from a balance,
            // which is non-negative), and refusing is the honest answer for a delta that cannot be
            // adjudicated at all.
            if (delta == long.MinValue)
            {
                log.Error($"[BANK] TryAdjustAccountBank refused a delta of long.MinValue for account {accountId}: it has no representable bound.");
                return AccountBankAdjustResult.Refused;
            }

            // The two guards, evaluated here rather than in SQL because both overflow in MySQL for the
            // very inputs they exist to reject - see ApplyAccountBankDeltaSql.
            //
            //   lowerBound: the smallest balance that can absorb this delta without going negative.
            //               -delta. For a credit this is negative and the guard is vacuously true,
            //               which is correct: a credit cannot make a non-negative balance negative.
            //   upperBound: the largest balance that can absorb this delta without passing
            //               long.MaxValue. long.MaxValue - delta for a credit; for a debit that
            //               expression exceeds long.MaxValue, and long.MaxValue is the right vacuous
            //               bound there for the same reason mirrored - a debit cannot overflow upward.
            var lowerBound = -delta;
            var upperBound = delta > 0 ? long.MaxValue - delta : long.MaxValue;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    // delta == 0 runs no statement at all. The UPDATE would match the row and change
                    // nothing, and how many rows that reports is exactly the UseAffectedRows-dependent
                    // answer this method is built not to rely on. It must not CREATE a row either: a
                    // no-op has no business establishing that an account has a pool.
                    if (delta != 0)
                    {
                        int applied;

                        try
                        {
                            applied = context.Database.ExecuteSqlRaw(ApplyAccountBankDeltaSql, accountId, delta, lowerBound, upperBound);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[BANK] TryAdjustAccountBank: the guarded UPDATE threw for account {accountId}, delta {delta}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");
                            return AccountBankAdjustResult.Failed;
                        }

                        if (applied == 0)
                        {
                            // Zero matched rows means either the row does not exist or a guard refused,
                            // and the sign of the delta separates them. A debit must never create a
                            // row, so a negative delta that matched nothing is refused either way and
                            // needs no second look. A positive delta can only be refused by the upper
                            // guard, which requires a balance within `delta` of long.MaxValue - so a
                            // credit that matched nothing usually means the row is simply absent, and
                            // the retry below settles which it was.
                            if (delta < 0)
                                return AccountBankAdjustResult.Refused;

                            try
                            {
                                context.Database.ExecuteSqlRaw(EnsureAccountBankRowSql, accountId);

                                applied = context.Database.ExecuteSqlRaw(ApplyAccountBankDeltaSql, accountId, delta, lowerBound, upperBound);
                            }
                            catch (Exception ex)
                            {
                                log.Error($"[BANK] TryAdjustAccountBank: the ensure-row re-apply threw for account {accountId}, delta {delta}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");
                                return AccountBankAdjustResult.Failed;
                            }

                            if (applied == 0)
                                return AccountBankAdjustResult.Refused;
                        }

                        committed = true;
                    }

                    try
                    {
                        var row = context.AccountBank.FirstOrDefault(b => b.AccountId == accountId);

                        // Absent after a zero delta is the honest answer that the account has no pool
                        // yet, and 0 is its balance. Absent after an APPLIED delta would mean the row
                        // was deleted underneath us - so rather than call that a balance of zero,
                        // report the balance as unknown and let the caller re-read.
                        if (row == null)
                            return committed ? AccountBankAdjustResult.AppliedCountUnknown : AccountBankAdjustResult.Applied;

                        newBalance = row.BankedPyreals;
                        return AccountBankAdjustResult.Applied;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[BANK] TryAdjustAccountBank: the read-back failed for account {accountId}, delta {delta}: {ex.GetFullMessage()}. The delta itself {(committed ? "DID commit" : "was a no-op")}.");

                        return committed ? AccountBankAdjustResult.AppliedCountUnknown : AccountBankAdjustResult.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                // Only the context construction and disposal are left out here, both of which run
                // before any statement or after the last one. Nothing can have been applied without
                // `committed` recording it, so this reports Failed unless a delta is already known to
                // have landed.
                log.Error($"[BANK] TryAdjustAccountBank failed for account {accountId}, delta {delta}: {ex.GetFullMessage()}");

                return committed ? AccountBankAdjustResult.AppliedCountUnknown : AccountBankAdjustResult.Failed;
            }
        }

        /// <summary>
        /// Claims the right to fold one character's old 9004 balance into its account's pool. Returns
        /// TRUE only when this call inserted the row, which is the one caller that may then credit.
        ///
        /// FALSE MEANS "NOT CLAIMED BY ME", WHICH IS NOT THE SAME AS "ALREADY FOLDED". A duplicate key
        /// and a database failure both land here, and the caller's two responses are opposite: after a
        /// genuine duplicate the character's 9004 property is stale and must be removed, while after a
        /// failure removing it would destroy a balance that was never credited anywhere. Callers must
        /// therefore confirm with <see cref="HasAccountBankFold"/> before removing anything - the extra
        /// read costs one round trip on a path that runs at most once per character, ever.
        /// </summary>
        public bool TryClaimAccountBankFold(uint characterGuid, uint accountId, long amount)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    return context.Database.ExecuteSqlRaw(ClaimAccountBankFoldSql, characterGuid, accountId, amount) == 1;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] TryClaimAccountBankFold failed for character 0x{characterGuid:X8}, account {accountId}, amount {amount}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Whether a fold row already exists for this character. NULL means the read FAILED.
        ///
        /// This exists only to make a FALSE from <see cref="TryClaimAccountBankFold"/> safe to act on.
        /// Without it the lazy fold cannot tell "somebody else folded this character" from "the shard
        /// is unreachable", and the response to the first (drop the stale 9004 property) destroys the
        /// player's balance if the second was the truth. A null here means neither: leave the property
        /// alone and let the next login retry.
        /// </summary>
        public bool? HasAccountBankFold(uint characterGuid)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountBankFold.Any(f => f.CharacterGuid == characterGuid);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] HasAccountBankFold failed for character 0x{characterGuid:X8}: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}

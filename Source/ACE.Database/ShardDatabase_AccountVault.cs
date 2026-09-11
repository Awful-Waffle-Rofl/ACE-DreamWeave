using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Mule Vendor - the four account_vault* tables (DESIGN section 6).
    ///
    /// These are index and ledger operations, NOT biota operations, so they deliberately do not run
    /// through SerializedShardDatabase's worker thread: putting them there would give vault reads the
    /// biota queue's head-of-line blocking for no benefit. Callers are AccountVaultManager and
    /// AccountVaultStore, which serialize per store themselves.
    ///
    /// NOTHING in this file may open a user-initiated EF transaction. ShardDbContext.OnConfiguring
    /// enables EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses one outright. Every
    /// statement here is a single SaveChanges(), a single ExecuteSqlRaw or a single ExecuteDelete,
    /// each of which is its own implicit transaction and is unaffected. Anything that genuinely had
    /// to span statements atomically would have to go through
    /// context.Database.CreateExecutionStrategy().Execute(...) with an idempotent delegate; nothing
    /// here does.
    ///
    /// READ FAILURE CONTRACT, and it is load-bearing for every one of the four read methods here:
    /// they return NULL when the read FAILED, and an EMPTY LIST when the account genuinely owns none.
    /// Callers must never conflate the two. The reason is DESIGN 7.1: insertion scans an account's
    /// vaults oldest-first and creates a new one only when all are full, so a read that answered a
    /// transient database failure with an empty list would tell Task 4 the account owns no vaults, a
    /// fresh empty vault would be created, the player's items would appear to be gone, and repeated
    /// flapping would accumulate orphan containers. Returning null forces the caller to decide.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Every vault container the account owns, oldest first, which is the order DESIGN 7.1's
        /// free-slot search depends on.
        ///
        /// Returns NULL if the read failed. An empty list means the account owns no vaults; null means
        /// we do not know. Conflating them makes Task 4 create a duplicate vault over a transient
        /// database failure, so the caller must branch on null before it branches on Count.
        /// </summary>
        public List<AccountVault> GetAccountVaults(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVault.Where(v => v.AccountId == accountId).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaults failed for account {accountId}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Every vault container guid on the shard, across every account. Read ONCE at process start
        /// to seed AccountVaultSpawnFilter (DESIGN 7.2, risk R1), never on a landblock activation.
        ///
        /// Why the whole table rather than a per-account read. The spawn filter's landblock predicate
        /// only knows the landblocks account_vault_landblock names RIGHT NOW and the value it ships
        /// with. A container created while the key pointed at some third landblock is covered by
        /// neither, and the filter's incremental registry only learns about accounts this process has
        /// actually touched - which landblock activation normally precedes. This read is what closes
        /// that gap, and it is one query per process for complete coverage.
        ///
        /// Returns NULL if the read FAILED and an EMPTY LIST when the table is genuinely empty, which
        /// is the ordinary state of a shard where nobody has used a vault yet. Collapsing the two here
        /// would turn a startup database hiccup into a silently unseeded filter, which is exactly the
        /// state the caller has to be able to tell apart in order to log it.
        ///
        /// Projected to the guid column on purpose: the caller wants a HashSet of ids, and selecting
        /// whole AccountVault entities would carry three unused columns per row for no benefit.
        /// </summary>
        public List<uint> GetAllAccountVaultContainerGuids()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVault.Select(v => v.ContainerGuid).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAllAccountVaultContainerGuids failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Registers a newly created vault container. Ordered oldest-first by the caller's read, which
        /// is what makes the free-slot search deterministic (DESIGN 7.1).
        ///
        /// CreatedAt is stamped with DateTime.UtcNow here if the caller left it at default. That is a
        /// defence, not a convenience: created_At carries HasDefaultValueSql("CURRENT_TIMESTAMP"), and
        /// EF omits a store-default column from the INSERT while the property still holds the CLR
        /// default, so an unstamped row silently takes the DATABASE SERVER'S LOCAL time. Every consumer
        /// reads these as UTC, and GetAccountVaults orders by this column to drive the free-slot search,
        /// so a value skewed by the machine's UTC offset reorders insertion.
        /// </summary>
        public bool AddAccountVault(AccountVault row)
        {
            if (row == null)
            {
                log.Error("[VAULT] AddAccountVault called with a null row.");
                return false;
            }

            if (row.CreatedAt == default)
                row.CreatedAt = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.AccountVault.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] AddAccountVault failed for account {row.AccountId}, container 0x{row.ContainerGuid:X8}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Removes a vault index row. The CALLER is responsible for having proved the vault empty
        /// first: risk R2 in DESIGN section 14 is that destroying a non-empty vault orphans its
        /// children, and dynamic guids return to the pool after 360 minutes
        /// (Managers/GuidManager.cs:126, DynamicGuidAllocator.recycleTime), at which point a recycled
        /// guid re-parents them into somebody else's container. This method cannot check that itself -
        /// the contents live in the container biota, not here - which is why
        /// AccountVaultStore.TryReapEmptyVault is the only permitted caller.
        /// </summary>
        public bool DeleteAccountVault(uint id)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    return context.AccountVault.Where(v => v.Id == id).ExecuteDelete() > 0;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] DeleteAccountVault failed for id {id}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// The account's whole collapsed-stackable ledger, ordered by wcid.
        ///
        /// Returns NULL if the read failed; an empty list means the account holds no stackables. See the
        /// read failure contract on the class doc - the two must never be conflated.
        /// </summary>
        public List<AccountVaultStack> GetAccountVaultStacks(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultStack.Where(s => s.AccountId == accountId).OrderBy(s => s.Wcid).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultStacks failed for account {accountId}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Ensures a ledger row exists for (account, wcid) without touching its count.
        ///
        /// A single INSERT ... ON DUPLICATE KEY UPDATE against the account_vault_stack_uidx unique key,
        /// so it is atomic at the row and idempotent: the duplicate branch assigns count to itself and
        /// therefore cannot lose a concurrent delta. Creating the row is deliberately separated from
        /// applying the delta so that the delta statement can carry its non-negative guard in a WHERE
        /// clause, which is the only form of the guard that reports a rejection reliably - see
        /// TryAdjustAccountVaultStack.
        ///
        /// RUNS ONLY ON THE CREATE PATH. TryAdjustAccountVaultStack attempts the guarded UPDATE first
        /// and reaches this statement only when that matched nothing AND the delta is positive, which
        /// is the one case that genuinely needs a new row. It must never be reached for a withdraw: a
        /// refused withdraw that ran this would leave a count = 0 row behind, and DESIGN 7.3 counts one
        /// ledger row as one entry against the capacity cap, so a run of refused withdraws would eat an
        /// account's capacity with rows it does not own. Reaping is only opportunistic, so nothing
        /// would clean them up.
        ///
        /// It is also why this is not run unconditionally: MySQL allocates an AUTO_INCREMENT value on
        /// the DUPLICATE branch too, not just on the insert branch. Measured on 8.0.46 with
        /// innodb_autoinc_lock_mode = 2, one create plus five duplicate-branch calls moved the counter
        /// from 2 to 7 and the next genuinely new row landed on id 7. account_vault_stack.id is
        /// int unsigned, so running this per call would consume the id space at one per deposit AND per
        /// withdraw rather than one per distinct wcid; on exhaustion the INSERT throws duplicate-PRIMARY
        /// and every stackable deposit and withdraw returns false permanently until someone runs an
        /// ALTER TABLE.
        ///
        /// Worst case if the process dies between this statement and the delta: a count = 0 row, which
        /// the store filters out and DeleteEmptyAccountVaultStacks reaps.
        /// </summary>
        private const string EnsureAccountVaultStackRowSql =
            "INSERT INTO `account_vault_stack` (`account_Id`, `wcid`, `count`) VALUES ({0}, {1}, 0) " +
            "ON DUPLICATE KEY UPDATE `count` = `count`";

        /// <summary>
        /// Applies the signed delta to one existing ledger row, and refuses to if the result would go
        /// negative. One statement, guard in the WHERE, so the database both applies and adjudicates.
        /// </summary>
        private const string ApplyAccountVaultStackDeltaSql =
            "UPDATE `account_vault_stack` SET `count` = `count` + {2} " +
            "WHERE `account_Id` = {0} AND `wcid` = {1} AND `count` + {2} >= 0";

        /// <summary>
        /// Applies a signed delta to one ledger row, creating it if absent, and reads the result back.
        ///
        /// The delta is applied BY THE DATABASE, never by a read-modify-write in C#. That is risk R3:
        /// two vendor windows on one store withdrawing the same wcid would otherwise both read the old
        /// count and both write old-minus-n, duplicating items. Both statements below are atomic at the
        /// row via the account_vault_stack_uidx unique key.
        ///
        /// WHY TWO STATEMENTS AND NOT ONE. The obvious form is a single
        /// INSERT ... ON DUPLICATE KEY UPDATE `count` = IF(`count` + d >= 0, `count` + d, `count`),
        /// with an over-withdraw detected as "zero rows affected". That form is WRONG here, in two
        /// independent ways, both measured against a scratch shard through ACE's own connection options
        /// (MySqlConnector 2.4.0):
        ///
        ///   1. MySqlConnector's UseAffectedRows defaults to false and ACE never sets it
        ///      (ACE.Common/MySqlConfiguration.cs, ConnectionOptions), so the connection runs with
        ///      CLIENT_FOUND_ROWS. Under that flag MySQL reports 1, not 0, for an ON DUPLICATE KEY
        ///      UPDATE that sets a row to its current values. A rejected over-withdraw therefore looks
        ///      like a success: measured, -25 against a held 20 returned affected = 1 and left the
        ///      count at 20, so the caller would have been told the withdraw succeeded while the ledger
        ///      still held the items. That is the item-duplication bug R3 exists to prevent, arriving
        ///      through the guard rather than around it.
        ///   2. The IF() guard lives only in the duplicate branch. The VALUES branch is unguarded, so a
        ///      negative delta for a wcid with no ledger row INSERTS a negative count: measured, -20
        ///      against a missing row created a row holding -20.
        ///
        /// Putting the guard in a WHERE clause fixes both. A WHERE that matches nothing yields zero
        /// rows under either UseAffectedRows setting, because "found" and "changed" agree at zero, so
        /// the rejection signal no longer depends on a connection-string option nobody reading this
        /// method would think to check. Measured on the same scratch shard: over-withdraw -25 against
        /// 20 returns 0 rows and leaves 20; a legal -5 returns 1 row and leaves 15; a withdraw that
        /// lands exactly on zero returns 1 row and leaves 0; a negative delta against a missing row
        /// returns 0 rows and leaves the row at 0.
        ///
        /// UPDATE FIRST, CREATE ONLY IF THAT MISSED. The guarded UPDATE runs before the ensure-row, and
        /// the ensure-row is reached only when the UPDATE matched nothing AND the delta is positive.
        /// Creating the row up front instead would leave a junk count = 0 row behind every refused
        /// withdraw (which eats DESIGN 7.3 capacity, since one ledger row is one entry against the cap)
        /// and would burn an AUTO_INCREMENT id on every single call, deposit and withdraw alike - see
        /// EnsureAccountVaultStackRowSql for both measurements. It also removes a round trip from the
        /// hot path, since an existing row now takes one statement rather than two.
        ///
        /// WHY delta &lt; 0 CAN REFUSE IMMEDIATELY. Zero matched rows means either the row does not exist
        /// or the guard refused, and the sign of the delta separates them. A withdraw must never create
        /// a row, so a negative delta that matched nothing is refused either way and needs no second
        /// look. A positive delta can only be refused by the guard if count is ALREADY negative, and no
        /// path can produce that once the unguarded-insert hole above is closed, so a positive delta
        /// that matched nothing means the row is simply absent.
        ///
        /// No statement here is a user-initiated transaction, so none trips
        /// MySqlRetryingExecutionStrategy. They are not atomic WITH EACH OTHER, and do not need to be:
        /// the ensure-row only creates a count = 0 row, which is inert.
        ///
        /// The follow-up SELECT is a separate statement, so it is not atomic with the update in the
        /// database. It does not need to be either: AccountVaultStore serializes every mutation for one
        /// account through a single queue, and ACE runs one server process per shard, so no other
        /// writer can interleave. If that ever stops being true, this method must become a stored
        /// procedure or the callers must stop needing the read-back.
        ///
        /// FOUR OUTCOMES, NOT TWO (fix round 2, F1). This used to return a bool, and every caller read
        /// false as "the ledger did NOT move" - which was true for a refusal and false for a database
        /// failure, because the two halves below fail independently:
        ///
        ///   - the guarded UPDATE goes through ExecuteSqlRaw, which does NOT run under
        ///     MySqlRetryingExecutionStrategy (EF's retry wrapper covers the LINQ pipeline, not raw
        ///     SQL execution), so it gets exactly one attempt;
        ///   - the read-back is a LINQ query and therefore DOES retry, up to the ten attempts
        ///     ShardDbContext.OnConfiguring's EnableRetryOnFailure(10) asks for.
        ///
        /// So the interesting failure is an UPDATE that committed followed by a read-back that did
        /// not - and reporting that as "did not move" made the withdraw path write a balancing Return
        /// audit row, hand the player nothing, and leave the ledger genuinely debited. A silent loss
        /// with a balanced audit trail. See <see cref="AccountVaultStackAdjustResult"/> for what each
        /// value obliges a caller to do; only <see cref="AccountVaultStackAdjustResult.Refused"/> means
        /// the ledger provably did not move.
        ///
        /// A delta that would take the count below zero is rejected rather than clamped silently, so
        /// the caller sees a refusal instead of a phantom success.
        ///
        /// newCount is meaningful ONLY for <see cref="AccountVaultStackAdjustResult.Applied"/>; it is
        /// left at 0 for every other value, and 0 is not a count there.
        /// </summary>
        public AccountVaultStackAdjustResult TryAdjustAccountVaultStack(uint accountId, uint wcid, long delta, out long newCount)
        {
            newCount = 0;

            // Whether the delta is KNOWN to have committed. Set only after a statement reported rows
            // affected, so it is never a guess: it is what separates AppliedCountUnknown (the units
            // moved, we just cannot say to what) from Failed (nobody knows whether they moved).
            var committed = false;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    // delta == 0 runs no statement at all. The UPDATE would match the row and change
                    // nothing, and how many rows that reports is exactly the UseAffectedRows-dependent
                    // answer this method is built to avoid relying on. It also must not CREATE a row:
                    // a no-op has no business consuming an id or a capacity slot.
                    if (delta != 0)
                    {
                        int applied;

                        try
                        {
                            applied = context.Database.ExecuteSqlRaw(ApplyAccountVaultStackDeltaSql, accountId, wcid, delta);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[VAULT] TryAdjustAccountVaultStack: the guarded UPDATE threw for account {accountId}, wcid {wcid}, delta {delta}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");
                            return AccountVaultStackAdjustResult.Failed;
                        }

                        if (applied == 0)
                        {
                            // A withdraw against an absent row is a refusal, never a reason to create
                            // one. See "WHY delta < 0 CAN REFUSE IMMEDIATELY" above.
                            if (delta < 0)
                                return AccountVaultStackAdjustResult.Refused;

                            try
                            {
                                context.Database.ExecuteSqlRaw(EnsureAccountVaultStackRowSql, accountId, wcid);

                                applied = context.Database.ExecuteSqlRaw(ApplyAccountVaultStackDeltaSql, accountId, wcid, delta);
                            }
                            catch (Exception ex)
                            {
                                log.Error($"[VAULT] TryAdjustAccountVaultStack: the ensure-row re-apply threw for account {accountId}, wcid {wcid}, delta {delta}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");
                                return AccountVaultStackAdjustResult.Failed;
                            }

                            if (applied == 0)
                                return AccountVaultStackAdjustResult.Refused;
                        }

                        committed = true;
                    }

                    try
                    {
                        var row = context.AccountVaultStack.FirstOrDefault(s => s.AccountId == accountId && s.Wcid == wcid);

                        // Absent after a zero delta is the honest answer that the account holds none,
                        // not a failure. Absent after an APPLIED delta would mean the row was deleted
                        // underneath us, which the per-store queue rules out - so rather than call it a
                        // count of zero, report the count as unknown and let the caller re-read.
                        if (row == null)
                            return committed ? AccountVaultStackAdjustResult.AppliedCountUnknown : AccountVaultStackAdjustResult.Applied;

                        newCount = row.Count;
                        return AccountVaultStackAdjustResult.Applied;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[VAULT] TryAdjustAccountVaultStack: the read-back failed for account {accountId}, wcid {wcid}, delta {delta}: {ex.GetFullMessage()}. The delta itself {(committed ? "DID commit" : "was a no-op")}.");

                        return committed ? AccountVaultStackAdjustResult.AppliedCountUnknown : AccountVaultStackAdjustResult.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                // Only the context construction and disposal are left out here, both of which run
                // before any statement or after the last one. Nothing can have been applied without
                // `committed` recording it, so this reports Failed unless a delta is already known to
                // have landed.
                log.Error($"[VAULT] TryAdjustAccountVaultStack failed for account {accountId}, wcid {wcid}, delta {delta}: {ex.GetFullMessage()}");

                return committed ? AccountVaultStackAdjustResult.AppliedCountUnknown : AccountVaultStackAdjustResult.Failed;
            }
        }

        /// <summary>
        /// Drops ledger rows that have fallen to zero. Cosmetic, not correctness: a zero row is a
        /// harmless empty line the store filters out anyway. Called opportunistically after a withdraw.
        /// </summary>
        public int DeleteEmptyAccountVaultStacks(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    return context.AccountVaultStack.Where(s => s.AccountId == accountId && s.Count <= 0).ExecuteDelete();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] DeleteEmptyAccountVaultStacks failed for account {accountId}: {ex.GetFullMessage()}");
                return 0;
            }
        }

        /// <summary>
        /// Every grant on the owner's store, ordered by the snapshot grantee name.
        ///
        /// Returns NULL if the read failed; an empty list means the store is unshared. See the read
        /// failure contract on the class doc - an authorization check that read null must FAIL CLOSED,
        /// which is precisely why this cannot answer a failure with an empty list.
        /// </summary>
        public List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultGrant.Where(g => g.OwnerAccountId == ownerAccountId).OrderBy(g => g.GranteeCharacterName).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultGrants failed for owner {ownerAccountId}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Creates or updates one grant. Re-granting an existing grantee with a different CanWithdraw
        /// is an update, not a duplicate, which is what the account_vault_grant_uidx unique key exists
        /// for.
        ///
        /// GrantedAt is stamped with DateTime.UtcNow here if the caller left it at default, for the
        /// same reason AddAccountVault stamps CreatedAt: granted_At carries a CURRENT_TIMESTAMP store
        /// default, EF omits the column from the INSERT while the property holds the CLR default, and
        /// the resulting value is the DATABASE SERVER'S LOCAL time while every consumer reads it as UTC.
        /// </summary>
        public bool UpsertAccountVaultGrant(AccountVaultGrant row)
        {
            if (row == null)
            {
                log.Error("[VAULT] UpsertAccountVaultGrant called with a null row.");
                return false;
            }

            if (row.GrantedAt == default)
                row.GrantedAt = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    var existing = context.AccountVaultGrant.FirstOrDefault(g =>
                        g.OwnerAccountId == row.OwnerAccountId && g.GranteeCharacterGuid == row.GranteeCharacterGuid);

                    if (existing == null)
                    {
                        context.AccountVaultGrant.Add(row);
                    }
                    else
                    {
                        existing.CanWithdraw = row.CanWithdraw;
                        existing.GranteeCharacterName = row.GranteeCharacterName;
                        existing.GrantedAt = row.GrantedAt;
                    }

                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] UpsertAccountVaultGrant failed for owner {row.OwnerAccountId}, grantee 0x{row.GranteeCharacterGuid:X8}: {ex.GetFullMessage()}");
                return false;
            }
        }

        public int DeleteAccountVaultGrant(uint ownerAccountId, uint granteeCharacterGuid)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    return context.AccountVaultGrant
                        .Where(g => g.OwnerAccountId == ownerAccountId && g.GranteeCharacterGuid == granteeCharacterGuid)
                        .ExecuteDelete();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] DeleteAccountVaultGrant failed for owner {ownerAccountId}, grantee 0x{granteeCharacterGuid:X8}: {ex.GetFullMessage()}");
                return 0;
            }
        }

        /// <summary>
        /// Appends one audit row. A failure here is logged and swallowed: the audit trail is what makes
        /// a theft report answerable, but refusing a player's deposit because the log write failed
        /// would turn a logging outage into an item-storage outage. Log loudly so the gap is visible.
        ///
        /// Timestamp is stamped with DateTime.UtcNow here if the caller left it at default, for the
        /// same reason AddAccountVault stamps CreatedAt. An audit row is the one artifact whose time
        /// has to be right, and GetAccountVaultLog orders by this column, so a row that fell back to
        /// the CURRENT_TIMESTAMP store default would sort by the database server's LOCAL time among
        /// rows recorded in UTC.
        /// </summary>
        public bool AddAccountVaultLog(AccountVaultLog row)
        {
            if (row == null)
            {
                log.Error("[VAULT] AddAccountVaultLog called with a null row.");
                return false;
            }

            if (row.Timestamp == default)
                row.Timestamp = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.AccountVaultLog.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] AddAccountVaultLog failed for owner {row.OwnerAccountId}, action {row.Action}, wcid {row.Wcid}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// The hard ceiling GetAccountVaultLog clamps its limit to, however large a caller asks for.
        /// Retention is unbounded in v1 (see the migration header), so without this an admin passing
        /// int.MaxValue would materialise an account's entire audit history into a List on the calling
        /// thread. 1000 rows is far past what /mule log can usefully show in a chat window.
        /// </summary>
        public const int MaxAccountVaultLogRows = 1000;

        /// <summary>
        /// The most recent audit rows for one store, newest first.
        ///
        /// limit is clamped to between 1 and MaxAccountVaultLogRows inclusive; a caller asking for more
        /// silently gets the ceiling rather than the whole table.
        ///
        /// Returns NULL if the read failed; an empty list means the store has no recorded activity. See
        /// the read failure contract on the class doc.
        /// </summary>
        public List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit)
        {
            if (limit < 1)
                limit = 1;
            else if (limit > MaxAccountVaultLogRows)
                limit = MaxAccountVaultLogRows;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultLog
                        .Where(l => l.OwnerAccountId == ownerAccountId)
                        .OrderByDescending(l => l.Timestamp)
                        .ThenByDescending(l => l.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultLog failed for owner {ownerAccountId}, limit {limit}: {ex.GetFullMessage()}");
                return null;
            }
        }

        // ---- account_vault_barrel (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6) ----
        //
        // Same shape as everything above: one ShardDbContext per call, one SaveChanges (its own
        // implicit transaction), and NULL from a read means the read failed rather than that nothing
        // matched. Nothing here opens a user-initiated EF transaction - see the class header for why
        // MySqlRetryingExecutionStrategy would refuse one.

        /// <summary>
        /// The hard ceiling <see cref="GetAccountVaultBarrels"/> and
        /// <see cref="GetExpiredAccountVaultBarrels"/> clamp their limit to. account_vault_barrel rows
        /// are never deleted by design - a purge stamps purged_At and keeps the row - so this table
        /// only grows, and an unclamped limit would materialise all of it onto the calling thread.
        /// </summary>
        public const int MaxAccountVaultBarrelRows = 1000;

        private static int ClampBarrelLimit(int limit)
        {
            if (limit < 1)
                return 1;

            return limit > MaxAccountVaultBarrelRows ? MaxAccountVaultBarrelRows : limit;
        }

        /// <summary>
        /// One account's barrelings, newest first, restored and purged rows included - an
        /// investigation needs to see what has already been dealt with, not only what is still open.
        ///
        /// Returns NULL if the read failed; an empty list means the account has never barreled
        /// anything. See the read failure contract on the class doc.
        /// </summary>
        public List<AccountVaultBarrel> GetAccountVaultBarrels(uint accountId, int limit)
        {
            limit = ClampBarrelLimit(limit);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultBarrel
                        .Where(b => b.AccountId == accountId)
                        .OrderByDescending(b => b.BarreledAt)
                        .ThenByDescending(b => b.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultBarrels failed for account {accountId}, limit {limit}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// One barrel row by its id, or null.
        ///
        /// This is the ONE read here whose null is genuinely ambiguous: it is both "no such row" and
        /// "the read failed", and a single-row signature has nowhere to put the difference. The
        /// restore path therefore REFUSES on null rather than inferring anything from it, which is the
        /// only safe reading - acting on a row this method could not confirm is how an item gets
        /// restored twice.
        /// </summary>
        public AccountVaultBarrel GetAccountVaultBarrel(uint id)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultBarrel.FirstOrDefault(b => b.Id == id);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultBarrel failed for row {id}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Rows that STILL HOLD an item and are past the retention cutoff: neither restored nor
        /// purged, barreled before <paramref name="cutoffUtc"/>. Oldest first, so a bounded batch
        /// always takes the rows that have waited longest.
        ///
        /// Restored and purged are both terminal and both mean the row no longer owns anything to
        /// destroy - restoring gave the item back, purging already destroyed it. Including either
        /// would make the sweep destroy an item the player got back, or stamp a second purge over a
        /// row whose biota is long gone.
        ///
        /// Returns NULL if the read failed; an empty list means nothing has expired.
        /// </summary>
        public List<AccountVaultBarrel> GetExpiredAccountVaultBarrels(DateTime cutoffUtc, int limit)
        {
            limit = ClampBarrelLimit(limit);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultBarrel
                        .Where(b => b.RestoredAt == null && b.PurgedAt == null && b.BarreledAt < cutoffUtc)
                        .OrderBy(b => b.BarreledAt)
                        .ThenBy(b => b.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetExpiredAccountVaultBarrels failed for cutoff {cutoffUtc:o}, limit {limit}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Records one barreling. Returns false if the write failed, and the caller must then NOT move
        /// the item: an item in the barrel with no row naming it is unrecoverable, because nothing
        /// else records that it was ever barreled.
        ///
        /// BarreledAt is stamped with DateTime.UtcNow here if the caller left it at default, for the
        /// same reason AddAccountVault stamps CreatedAt: barreled_At carries
        /// HasDefaultValueSql("CURRENT_TIMESTAMP"), EF omits a store-default column from the INSERT
        /// while the property still holds the CLR default, and the row would then take the DATABASE
        /// SERVER'S LOCAL time. The retention sweep compares this column against a UTC cutoff, so a
        /// value skewed by the machine's UTC offset moves the purge by that offset.
        /// </summary>
        public bool AddAccountVaultBarrel(AccountVaultBarrel row)
        {
            if (row == null)
            {
                log.Error("[VAULT] AddAccountVaultBarrel called with a null row.");
                return false;
            }

            if (row.BarreledAt == default)
                row.BarreledAt = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.AccountVaultBarrel.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] AddAccountVaultBarrel failed for account {row.AccountId}, wcid {row.Wcid}, item 0x{row.ItemGuid ?? 0:X8}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Writes back the two terminal stamps on an existing row. The only columns anything ever
        /// updates are restored_At and purged_At, so this deliberately writes just those rather than
        /// attaching the whole entity: the rest of the row is a snapshot of what happened at barrel
        /// time and must never be rewritten by a later event.
        ///
        /// Returns false if the write failed. Both callers have ALREADY moved the item by the time
        /// they reach here, so a false is a real divergence - the item is back (or destroyed) while
        /// the row still reads open - and both log it loudly rather than treating it as a refusal.
        /// </summary>
        public bool UpdateAccountVaultBarrel(AccountVaultBarrel row)
        {
            if (row == null)
            {
                log.Error("[VAULT] UpdateAccountVaultBarrel called with a null row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    var existing = context.AccountVaultBarrel.FirstOrDefault(b => b.Id == row.Id);

                    if (existing == null)
                    {
                        log.Error($"[VAULT] UpdateAccountVaultBarrel found no account_vault_barrel row {row.Id}.");
                        return false;
                    }

                    existing.RestoredAt = row.RestoredAt;
                    existing.PurgedAt = row.PurgedAt;

                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] UpdateAccountVaultBarrel failed for row {row.Id}: {ex.GetFullMessage()}");
                return false;
            }
        }
    }
}

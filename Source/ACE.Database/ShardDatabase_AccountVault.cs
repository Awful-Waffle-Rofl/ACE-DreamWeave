using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

        // ---- the BATCHED deposit form (SPEC-vault-batch-deposit.md sections 2, 5 step 1 and 6) ----
        //
        // One sale credits the ledger once per ITEM today, through a private ShardDbContext plus an
        // UPDATE and a read-back per item. A measured 53-item sale paid 53 contexts and 106 synchronous
        // round trips against usually ONE row, on the world thread. The pair of statements below make
        // the synchronous cost proportional to the number of DISTINCT KEYS instead.
        //
        // DEPOSIT ONLY, and that is a correctness property rather than a scope decision. Every delta is
        // asserted strictly positive, so with every stored count non-negative the `count + CASE >= 0`
        // guard can never refuse; MATCHED therefore equals PRESENT exactly, which is what makes
        // "absent = carried - present" a sound inference and the multi-row forms safe.

        /// <summary>
        /// The multi-row guarded UPDATE that credits every ALREADY-PRESENT key of one batch in a single
        /// statement. Built by <see cref="BuildAccountVaultStackBatchUpdateSql"/>, which replaces
        /// &lt;CASE&gt; with a CASE over the key column and &lt;KEYS&gt; with the IN list. Both are made
        /// ENTIRELY of positional parameter placeholders: no wcid and no count is ever interpolated
        /// into this text.
        ///
        /// The non-negative guard stays in the WHERE for exactly the reason
        /// <see cref="ApplyAccountVaultStackDeltaSql"/> documents at length. MySqlConnector runs with
        /// CLIENT_FOUND_ROWS, under which an IF() guard in the SET clause reports a refused row as one
        /// affected row, so a rejection would read as a success. It matters more here than there,
        /// because this statement's affected count is also the only evidence of WHICH keys were
        /// credited.
        ///
        /// MySQL's CASE takes the FIRST matching WHEN, so a key appearing twice would silently drop the
        /// second delta AND leave matched equal to the distinct-key count, so no mismatch would fire and
        /// the identifying SELECT would never run: an under-credit that presents as a clean success.
        /// That is why the plan is built from a dictionary before this is ever generated.
        /// </summary>
        private const string AdjustAccountVaultStackBatchUpdateSql =
            "UPDATE `account_vault_stack` SET `count` = `count` + <CASE> " +
            "WHERE `account_Id` = {0} AND `wcid` IN (<KEYS>) AND `count` + <CASE> >= 0";

        /// <summary>
        /// The multi-row INSERT ... ON DUPLICATE KEY UPDATE that credits the keys the UPDATE provably
        /// did not match. &lt;ROWS&gt; becomes one parenthesised placeholder triple per key.
        ///
        /// It carries NO non-negative guard, and that is sound ONLY because
        /// <see cref="TryAdjustAccountVaultStackBatch"/> refuses any batch holding a non-positive delta.
        /// The unguarded-VALUES-branch hole documented on <see cref="TryAdjustAccountVaultStack"/> - a
        /// negative delta INSERTing a negative count - is therefore unreachable here rather than merely
        /// unlikely.
        ///
        /// It runs for the genuinely absent keys ONLY, which keeps the AUTO_INCREMENT burn at one id per
        /// genuinely new row; see <see cref="EnsureAccountVaultStackRowSql"/> for the measurement behind
        /// that discipline and for what exhausting account_vault_stack.id costs.
        ///
        /// ON DUPLICATE KEY UPDATE rather than a plain INSERT because the caller's "already present"
        /// oracle is an in-memory index and may be stale: a key it called absent that actually exists
        /// takes the duplicate branch and is credited once, never twice.
        /// </summary>
        private const string AdjustAccountVaultStackBatchInsertSql =
            "INSERT INTO `account_vault_stack` (`account_Id`, `wcid`, `count`) VALUES <ROWS> " +
            "ON DUPLICATE KEY UPDATE `count` = `count` + VALUES(`count`)";

        /// <summary>
        /// Generates the guarded multi-row UPDATE for <paramref name="keyCount"/> keys. Parameter 0 is
        /// the account; key i takes parameters 1 + 2i (the wcid) and 2 + 2i (the delta).
        /// </summary>
        private static string BuildAccountVaultStackBatchUpdateSql(int keyCount)
        {
            var caseExpression = new StringBuilder("CASE `wcid`");
            var keyList = new StringBuilder();

            for (var i = 0; i < keyCount; i++)
            {
                var keyArg = 1 + (i * 2);
                var deltaArg = keyArg + 1;

                caseExpression.Append(" WHEN {").Append(keyArg).Append("} THEN {").Append(deltaArg).Append('}');

                if (i > 0)
                    keyList.Append(", ");

                keyList.Append('{').Append(keyArg).Append('}');
            }

            caseExpression.Append(" END");

            return AdjustAccountVaultStackBatchUpdateSql
                .Replace("<CASE>", caseExpression.ToString())
                .Replace("<KEYS>", keyList.ToString());
        }

        /// <summary>
        /// Generates the multi-row INSERT ... ON DUPLICATE KEY UPDATE for <paramref name="keyCount"/>
        /// keys. Parameter 0 is the account; key i takes parameters 1 + 2i (the wcid) and 2 + 2i (the
        /// delta), the same layout the UPDATE uses so one argument builder serves both.
        /// </summary>
        private static string BuildAccountVaultStackBatchInsertSql(int keyCount)
        {
            var rows = new StringBuilder();

            for (var i = 0; i < keyCount; i++)
            {
                var keyArg = 1 + (i * 2);
                var deltaArg = keyArg + 1;

                if (i > 0)
                    rows.Append(", ");

                rows.Append("({0}, {").Append(keyArg).Append("}, {").Append(deltaArg).Append("})");
            }

            return AdjustAccountVaultStackBatchInsertSql.Replace("<ROWS>", rows.ToString());
        }

        /// <summary>
        /// The positional argument array both statements above consume: the account, then one
        /// (wcid, delta) pair per key in <paramref name="keys"/>.
        /// </summary>
        private static object[] BuildAccountVaultStackBatchArgs(uint accountId, List<uint> keys, Dictionary<uint, long> plan)
        {
            var args = new object[1 + (keys.Count * 2)];

            args[0] = accountId;

            for (var i = 0; i < keys.Count; i++)
            {
                args[1 + (i * 2)] = keys[i];
                args[2 + (i * 2)] = plan[keys[i]];
            }

            return args;
        }

        /// <summary>
        /// Marks every key of a batch <see cref="AccountVaultStackAdjustResult.Refused"/>, for a
        /// precondition that refuses before any statement runs.
        /// </summary>
        private static void RefuseWholeAccountVaultStackBatch(IReadOnlyList<(uint Wcid, long Delta)> deltas,
                                                              Dictionary<uint, AccountVaultStackAdjustResult> outcomes)
        {
            foreach (var entry in deltas)
                outcomes[entry.Wcid] = AccountVaultStackAdjustResult.Refused;
        }

        /// <summary>
        /// Credits N DISTINCT ledger keys in one or two statements against one context. Deposit only.
        ///
        /// Returns true when every key reported <see cref="AccountVaultStackAdjustResult.Applied"/>.
        /// <paramref name="perKey"/> is the real answer and is always populated for every key supplied,
        /// whatever the return value.
        ///
        /// STATEMENT ORDER, and it is never the reverse:
        ///
        ///   1. one guarded multi-row UPDATE carrying the keys <paramref name="knownPresent"/> claims
        ///      already exist;
        ///   2. ONLY when that matched a different number of rows than it carried, ONE identifying
        ///      SELECT over those same keys;
        ///   3. one multi-row INSERT ... ON DUPLICATE KEY UPDATE for the genuinely absent keys, which is
        ///      the keys the oracle called absent plus the keys step 2 proved absent.
        ///
        /// A KEY THE UPDATE MATCHED IS NEVER RE-APPLIED. That is the sharpest hazard in this shape: the
        /// INSERT's duplicate branch ADDS its delta, so carrying an already-credited key into step 3
        /// double-credits it. Step 3's key list is built only from keys the UPDATE never carried and
        /// keys the identifying SELECT proved absent, never from "the ones I am unsure about".
        ///
        /// A FAILED IDENTIFYING SELECT FAILS THE WHOLE CALL. Every key is reported
        /// <see cref="AccountVaultStackAdjustResult.Failed"/> and no INSERT is attempted. This has to be
        /// stated rather than left to judgement, because the natural fallback - "I do not know which
        /// keys are present, so upsert them all" - double-credits every key the UPDATE already matched.
        /// The single-row method's equivalent failure is benign (it proceeds as AppliedCountUnknown), so
        /// this cannot be pattern-matched off the code this one is modelled on.
        ///
        /// PRECONDITIONS, both asserted, both refusing the WHOLE call:
        ///
        ///   - every delta STRICTLY POSITIVE. This is what makes the multi-row forms safe and why the
        ///     method is deposit-only: with every delta positive and every stored count non-negative the
        ///     `count + CASE >= 0` guard can never refuse, so MATCHED equals PRESENT exactly and
        ///     "absent = carried - present" is correct. It also guarantees every matched row genuinely
        ///     CHANGES, so the affected count means the same thing under either UseAffectedRows setting.
        ///   - every key DISTINCT. MySQL's CASE takes the first matching WHEN, so a repeated key drops
        ///     the second delta while the matched count still equals the distinct-key count - no
        ///     mismatch fires, the identifying SELECT never runs, and the result is an under-credit with
        ///     the items destroyed: a loss presenting as a clean success. The plan is built from a
        ///     dictionary so duplication is impossible by construction, and the assert is the second
        ///     line rather than the first.
        ///
        /// NO READ-BACK. The store reloads the whole account separately, which is the round trip this
        /// method exists to stop paying per item.
        ///
        /// OUTCOMES. <see cref="AccountVaultStackAdjustResult.Applied"/> on success;
        /// <see cref="AccountVaultStackAdjustResult.Failed"/> for every key carried by a statement that
        /// threw; <see cref="AccountVaultStackAdjustResult.Refused"/> only where the ledger provably did
        /// not move. Given the preconditions above the database can never refuse a key, which is the
        /// same fact that makes MATCHED equal PRESENT, so Refused here means the whole call was refused
        /// before any statement ran.
        ///
        /// <paramref name="knownPresent"/> is a HINT, not a promise. A stale oracle is bounded rather
        /// than fatal: a key it wrongly calls present simply misses the UPDATE and is picked up by the
        /// identifying SELECT and the INSERT, and a key it wrongly calls absent takes the INSERT's
        /// duplicate branch and is credited exactly once.
        ///
        /// No user-initiated transaction, per this file's header: each statement is its own implicit
        /// transaction, so one (table, key) group never half-commits. The groups are NOT atomic with
        /// each other and do not need to be - the caller's contract is per group.
        ///
        /// <see cref="TryAdjustAccountVaultStack"/>'s remark that no other writer can interleave applies
        /// here unchanged and is not widened: AccountVaultStore serializes every mutation for one
        /// account through a single queue and ACE runs one server process per shard. If that ever stops
        /// being true, this method must become a stored procedure.
        /// </summary>
        public bool TryAdjustAccountVaultStackBatch(uint accountId, IReadOnlyList<(uint Wcid, long Delta)> deltas, IReadOnlySet<uint> knownPresent,
                                                    out IReadOnlyDictionary<uint, AccountVaultStackAdjustResult> perKey)
        {
            var outcomes = new Dictionary<uint, AccountVaultStackAdjustResult>();

            perKey = outcomes;

            if (deltas == null || deltas.Count == 0)
                return true;

            // Precondition 1: strictly positive deltas. Precondition 2: distinct keys. Both refuse the
            // whole call - see the remarks for why neither is hygiene.
            var plan = new Dictionary<uint, long>(deltas.Count);
            var order = new List<uint>(deltas.Count);

            foreach (var entry in deltas)
            {
                if (entry.Delta <= 0)
                {
                    log.Error($"[VAULT] TryAdjustAccountVaultStackBatch: account {accountId} carried a non-positive delta {entry.Delta} for wcid {entry.Wcid}. This method is deposit-only and its multi-row forms are only safe while every delta is positive. Refusing the whole call of {deltas.Count} key(s).");

                    RefuseWholeAccountVaultStackBatch(deltas, outcomes);
                    return false;
                }

                if (plan.ContainsKey(entry.Wcid))
                {
                    log.Error($"[VAULT] TryAdjustAccountVaultStackBatch: account {accountId} carried wcid {entry.Wcid} more than once. A repeated key would silently drop the second delta AND suppress the mismatch that would have caught it. Refusing the whole call of {deltas.Count} key(s).");

                    RefuseWholeAccountVaultStackBatch(deltas, outcomes);
                    return false;
                }

                plan.Add(entry.Wcid, entry.Delta);
                order.Add(entry.Wcid);
            }

            // Partition, per the owner's C1 ruling: keys the caller's index already holds take the
            // UPDATE, everything else goes straight to the INSERT, so the AUTO_INCREMENT burn stays at
            // one id per genuinely new row.
            var updateKeys = new List<uint>(order.Count);
            var insertKeys = new List<uint>(order.Count);

            foreach (var wcid in order)
            {
                if (knownPresent != null && knownPresent.Contains(wcid))
                    updateKeys.Add(wcid);
                else
                    insertKeys.Add(wcid);
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    if (updateKeys.Count > 0)
                    {
                        var matched = 0;
                        var updateThrew = false;

                        try
                        {
                            matched = context.Database.ExecuteSqlRaw(BuildAccountVaultStackBatchUpdateSql(updateKeys.Count),
                                                                    BuildAccountVaultStackBatchArgs(accountId, updateKeys, plan));
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[VAULT] TryAdjustAccountVaultStackBatch: the guarded multi-row UPDATE threw for account {accountId} carrying {updateKeys.Count} key(s): {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");

                            updateThrew = true;

                            // Every key this statement carried is Failed, and NONE of them is added to
                            // the INSERT's key list. The duplicate branch ADDS, so retrying a key whose
                            // UPDATE may have committed would double-credit it.
                            foreach (var wcid in updateKeys)
                                outcomes[wcid] = AccountVaultStackAdjustResult.Failed;
                        }

                        if (!updateThrew && matched == updateKeys.Count)
                        {
                            // Every key the statement carried was present, and every present key was
                            // credited: the guard cannot refuse a positive delta.
                            foreach (var wcid in updateKeys)
                                outcomes[wcid] = AccountVaultStackAdjustResult.Applied;
                        }
                        else if (!updateThrew)
                        {
                            // ONE identifying SELECT, and only here. A row present NOW was present when
                            // the UPDATE ran - nothing else writes this account - so present means
                            // credited and absent means the UPDATE could not have touched it.
                            List<uint> present;

                            try
                            {
                                present = context.AccountVaultStack
                                    .Where(s => s.AccountId == accountId && updateKeys.Contains(s.Wcid))
                                    .Select(s => s.Wcid)
                                    .ToList();
                            }
                            catch (Exception ex)
                            {
                                log.Error($"[VAULT] TryAdjustAccountVaultStackBatch: the identifying SELECT failed for account {accountId} after the UPDATE matched {matched} of {updateKeys.Count} key(s): {ex.GetFullMessage()}. The WHOLE call is Failed and no INSERT is attempted - upserting the unknown keys would double-credit every key the UPDATE already matched.");

                                foreach (var wcid in order)
                                    outcomes[wcid] = AccountVaultStackAdjustResult.Failed;

                                return false;
                            }

                            var presentKeys = new HashSet<uint>(present);

                            foreach (var wcid in updateKeys)
                            {
                                if (presentKeys.Contains(wcid))
                                    outcomes[wcid] = AccountVaultStackAdjustResult.Applied;
                                else
                                    insertKeys.Add(wcid);
                            }
                        }
                    }

                    if (insertKeys.Count > 0)
                    {
                        try
                        {
                            context.Database.ExecuteSqlRaw(BuildAccountVaultStackBatchInsertSql(insertKeys.Count),
                                                           BuildAccountVaultStackBatchArgs(accountId, insertKeys, plan));

                            // One statement, one implicit transaction: it committed for every row it
                            // carried or for none. Its affected count is deliberately not read - under
                            // ON DUPLICATE KEY UPDATE it is 1 per inserted row and 2 per updated row,
                            // which answers nothing this method asks.
                            foreach (var wcid in insertKeys)
                                outcomes[wcid] = AccountVaultStackAdjustResult.Applied;
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[VAULT] TryAdjustAccountVaultStackBatch: the multi-row INSERT threw for account {accountId} carrying {insertKeys.Count} key(s): {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");

                            foreach (var wcid in insertKeys)
                                outcomes[wcid] = AccountVaultStackAdjustResult.Failed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Only the context construction and disposal are left out above. Any key that never
                // reached an outcome is swept into Failed by the loop below: nobody knows whether it
                // moved, and an outcome already recorded is left alone because it is known.
                log.Error($"[VAULT] TryAdjustAccountVaultStackBatch failed for account {accountId} carrying {order.Count} key(s): {ex.GetFullMessage()}");
            }

            foreach (var wcid in order)
            {
                if (!outcomes.ContainsKey(wcid))
                    outcomes[wcid] = AccountVaultStackAdjustResult.Failed;
            }

            return order.All(wcid => outcomes[wcid] == AccountVaultStackAdjustResult.Applied);
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
        /// Every grant naming this character as the grantee, across every owner account, ordered by
        /// owner account. The /mule search all read: "which mules are shared with me".
        ///
        /// Returns NULL if the read failed; an empty list means nothing is shared with this character.
        /// See the read failure contract on the class doc - the caller reports a failed read as
        /// "shared mules unavailable", never as "nothing is shared with you".
        ///
        /// Served by account_vault_grant_grantee_idx
        /// (Database/Updates/Shard/2026-09-22-00-Account-Vault-Grant-Grantee-Index.sql); without it
        /// this is a full scan, because the unique key leads with owner_Account_Id.
        /// </summary>
        public List<AccountVaultGrant> GetAccountVaultGrantsForGrantee(uint granteeCharacterGuid)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultGrant.Where(g => g.GranteeCharacterGuid == granteeCharacterGuid).OrderBy(g => g.OwnerAccountId).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultGrantsForGrantee failed for grantee 0x{granteeCharacterGuid:X8}: {ex.GetFullMessage()}");
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

                        // A re-grant from a different character on the owner account re-labels the
                        // shared mule after that character. A caller that knows no granter (null)
                        // leaves the stored one alone rather than erasing it.
                        if (row.GrantedByCharacterGuid != null)
                        {
                            existing.GrantedByCharacterGuid = row.GrantedByCharacterGuid;
                            existing.GrantedByCharacterName = row.GrantedByCharacterName;
                        }
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
        /// Appends N audit rows in ONE round trip: one ShardDbContext, N Add, one SaveChanges.
        ///
        /// Why it exists. <see cref="AddAccountVaultLog"/> opens its own context and calls SaveChanges
        /// per row, which is one blocking MySQL round trip per DEPOSITED ITEM. A 512-item mule sale
        /// therefore paid 512 of them, on the world thread, inside the sell transaction. This method is
        /// what AccountVaultAuditWriter drains its queue through, so the same 512 rows cost a handful
        /// of round trips on a background thread instead.
        ///
        /// Still exactly one SaveChanges() and NO user-initiated transaction, per this file's header:
        /// ShardDbContext enables EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses a
        /// user-initiated transaction outright. A single SaveChanges is its own implicit transaction,
        /// so a batch of audit rows is all-or-nothing without one being opened here.
        ///
        /// Same failure contract as the single-row method, and for the same reason: a failure is logged
        /// and reported, never thrown. The rows describe operations that have ALREADY happened, so
        /// losing them costs the audit trail and must never cost the player their items.
        ///
        /// Null entries are skipped rather than refused, because refusing would discard every good row
        /// beside the bad one and the good rows are the evidence an operator needs.
        /// </summary>
        public bool AddAccountVaultLogBatch(List<AccountVaultLog> rows)
        {
            if (rows == null || rows.Count == 0)
                return true;

            var stampedNow = DateTime.UtcNow;
            var usable = new List<AccountVaultLog>(rows.Count);

            foreach (var row in rows)
            {
                if (row == null)
                {
                    log.Error("[VAULT] AddAccountVaultLogBatch skipped a null row; the rest of the batch is still written.");
                    continue;
                }

                // Same stamp rule as AddAccountVaultLog, and load-bearing for the same reason:
                // GetAccountVaultLog orders by this column, so a row that fell through to the store
                // default would sort by the database server's LOCAL time among rows recorded in UTC.
                if (row.Timestamp == default)
                    row.Timestamp = stampedNow;

                usable.Add(row);
            }

            if (usable.Count == 0)
                return true;

            try
            {
                using (var context = new ShardDbContext())
                {
                    foreach (var row in usable)
                        context.AccountVaultLog.Add(row);

                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] AddAccountVaultLogBatch failed for {usable.Count} row(s), first owner {usable[0].OwnerAccountId}: {ex.GetFullMessage()}");
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

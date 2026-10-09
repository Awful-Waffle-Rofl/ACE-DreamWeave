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
    /// Mule Vendor - the counted item-CLASS ledger (account_vault_class), kept in its own file for the
    /// same reason ShardDatabase_AccountVaultScan.cs is: one concern per file, so two unrelated vault
    /// changes cannot collide in one 900-line file.
    ///
    /// Everything the header of ShardDatabase_AccountVault.cs says applies here unchanged, and is
    /// repeated rather than assumed because a reader who opened this file did not open that one:
    ///
    /// - These are ledger operations, NOT biota operations, so they deliberately do not run through
    ///   SerializedShardDatabase's worker thread. The caller is AccountVaultStore, which serializes
    ///   every mutation for one account through its own queue.
    /// - NOTHING here may open a user-initiated EF transaction, and nothing here needs one.
    ///   ShardDbContext.OnConfiguring enables EnableRetryOnFailure and MySqlRetryingExecutionStrategy
    ///   refuses a user-initiated transaction outright. Every statement below is a single ExecuteSqlRaw
    ///   or a single LINQ read, each its own implicit transaction. Atomicity comes from the
    ///   account_vault_class_uidx unique key plus that per-account mutation queue, not from the
    ///   database.
    /// - READ FAILURE CONTRACT: a read returns NULL when it FAILED and an EMPTY LIST when the account
    ///   genuinely holds no classes. Callers must never conflate the two - a store that read a
    ///   transient failure as "holds none" would draw an empty panel over a full vault.
    ///
    /// COUNT IS ITEMS HERE, NOT UNITS. account_vault_stack.count is total units across stacks; a class
    /// row stands for indivisible objects, so its count is a number of items and there is no stack size
    /// anywhere in this file. The two columns share a name and mean different things.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Ensures a class row exists for (account, class key) without touching its count or total.
        ///
        /// One INSERT ... ON DUPLICATE KEY UPDATE against account_vault_class_uidx, so it is atomic at
        /// the row and idempotent: the duplicate branch assigns count to itself and therefore cannot
        /// lose a concurrent delta. The seed columns are written only on the INSERT branch, which is
        /// what makes a re-run harmless - an existing row's canonical form is never overwritten, and it
        /// must not be, because the key is a hash of it and a row whose text stopped hashing to its own
        /// key could never be parsed back into an item.
        ///
        /// RUNS ONLY ON THE CREATE PATH, exactly as EnsureAccountVaultStackRowSql does, and for the
        /// same two measured reasons. A refused withdraw that ran this would leave a count = 0 row
        /// behind, and one ledger row is one entry against the DESIGN 7.3 capacity cap, so a run of
        /// refused withdraws would eat an account's capacity with rows it does not own and only an
        /// opportunistic reap would ever clean them up. And MySQL allocates an AUTO_INCREMENT value on
        /// the DUPLICATE branch too, so running it unconditionally would consume account_vault_class.id
        /// (int unsigned) at one per deposit AND per withdraw rather than one per distinct class.
        /// </summary>
        private const string EnsureAccountVaultClassRowSql =
            "INSERT INTO `account_vault_class` (`account_Id`, `class_Key`, `wcid`, `count`, `total_Value`, `value_Band_Pct`, `canonical_Form`) " +
            "VALUES ({0}, {1}, {2}, 0, 0, {3}, {4}) " +
            "ON DUPLICATE KEY UPDATE `count` = `count`";

        /// <summary>
        /// Applies both signed deltas to one existing class row, and refuses to if either result would
        /// go negative.
        ///
        /// ONE STATEMENT FOR BOTH COLUMNS, and that is the whole point rather than a tidiness
        /// preference: count and total_Value describe the same pool, so applying them separately would
        /// leave a window - and, on a failure between the two, a permanent state - in which the row
        /// says it holds N items worth a total that belongs to N+1. A withdraw computed from a diverged
        /// pair hands out the wrong Value per item, which is an unrecoverable rewrite of a player's
        /// property.
        ///
        /// The guard lives in the WHERE rather than in an IF(), for exactly the reason
        /// ApplyAccountVaultStackDeltaSql documents at length: MySqlConnector runs with
        /// CLIENT_FOUND_ROWS, under which an ON DUPLICATE KEY UPDATE that changes nothing still reports
        /// one row, so a rejection detected as "zero rows affected" would read as a success. A WHERE
        /// that matches nothing reports zero under either setting.
        /// </summary>
        private const string ApplyAccountVaultClassDeltaSql =
            "UPDATE `account_vault_class` SET `count` = `count` + {2}, `total_Value` = `total_Value` + {3} " +
            "WHERE `account_Id` = {0} AND `class_Key` = {1} AND `count` + {2} >= 0 AND `total_Value` + {3} >= 0";

        /// <summary>
        /// Applies a signed item-count delta and a signed pooled-value delta to one class row, creating
        /// it from <paramref name="seed"/> if absent, and reads the result back.
        ///
        /// This is <see cref="TryAdjustAccountVaultStack"/>'s shape with a second column, and it shares
        /// that method's contract exactly - read its remarks for the measurements behind every
        /// decision here. The differences are only these:
        ///
        ///   - the row is keyed on a 32-hex class key rather than a wcid, so a brand new class needs
        ///     more than the key to exist at all; that is what <paramref name="seed"/> carries;
        ///   - both columns move in ONE statement, so they can never diverge (see
        ///     ApplyAccountVaultClassDeltaSql);
        ///   - <paramref name="countDelta"/> counts ITEMS, never units.
        ///
        /// A NEGATIVE countDelta NEVER CREATES A ROW. Zero matched rows with a negative delta is a
        /// refusal, full stop: the row is absent, or the guard refused an over-withdraw, and both mean
        /// the player asked for something the ledger does not hold.
        ///
        /// Both deltas zero runs no statement at all and falls straight to the read-back, which is how
        /// a caller asks "what does this row hold" without consuming an id or a capacity slot.
        ///
        /// <paramref name="newCount"/> and <paramref name="newTotalValue"/> are meaningful ONLY for
        /// <see cref="AccountVaultStackAdjustResult.Applied"/>. They are left at 0 for every other
        /// value, and 0 is not a count or a total there.
        /// </summary>
        public AccountVaultStackAdjustResult TryAdjustAccountVaultClass(uint accountId, string classKey, long countDelta, long valueDelta,
                                                                       AccountVaultClassSeed seed, out long newCount, out long newTotalValue)
        {
            newCount = 0;
            newTotalValue = 0;

            if (string.IsNullOrEmpty(classKey))
            {
                log.Error($"[VAULT] TryAdjustAccountVaultClass was called with no class key for account {accountId}; refusing.");
                return AccountVaultStackAdjustResult.Refused;
            }

            // Whether the deltas are KNOWN to have committed. Set only after a statement reported rows
            // affected, so it is never a guess: it is what separates AppliedCountUnknown (the items
            // moved, we just cannot say to what) from Failed (nobody knows whether they moved).
            var committed = false;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    if (countDelta != 0 || valueDelta != 0)
                    {
                        int applied;

                        try
                        {
                            applied = context.Database.ExecuteSqlRaw(ApplyAccountVaultClassDeltaSql, accountId, classKey, countDelta, valueDelta);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[VAULT] TryAdjustAccountVaultClass: the guarded UPDATE threw for account {accountId}, class {classKey}, count delta {countDelta}, value delta {valueDelta}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");
                            return AccountVaultStackAdjustResult.Failed;
                        }

                        if (applied == 0)
                        {
                            // A withdraw against an absent row is a refusal, never a reason to create
                            // one. See EnsureAccountVaultClassRowSql for what a stray zero row costs.
                            if (countDelta < 0)
                                return AccountVaultStackAdjustResult.Refused;

                            if (seed == null || string.IsNullOrEmpty(seed.CanonicalForm))
                            {
                                log.Error($"[VAULT] TryAdjustAccountVaultClass: account {accountId} has no row for class {classKey} and no seed was supplied, so one cannot be created. Refusing.");
                                return AccountVaultStackAdjustResult.Refused;
                            }

                            try
                            {
                                context.Database.ExecuteSqlRaw(EnsureAccountVaultClassRowSql, accountId, classKey, seed.Wcid, seed.ValueBandPct, seed.CanonicalForm);

                                applied = context.Database.ExecuteSqlRaw(ApplyAccountVaultClassDeltaSql, accountId, classKey, countDelta, valueDelta);
                            }
                            catch (Exception ex)
                            {
                                log.Error($"[VAULT] TryAdjustAccountVaultClass: the ensure-row re-apply threw for account {accountId}, class {classKey}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");
                                return AccountVaultStackAdjustResult.Failed;
                            }

                            if (applied == 0)
                                return AccountVaultStackAdjustResult.Refused;
                        }

                        committed = true;
                    }

                    try
                    {
                        var row = context.AccountVaultClass.FirstOrDefault(c => c.AccountId == accountId && c.ClassKey == classKey);

                        // Absent after a zero delta is the honest answer that the account holds none of
                        // this class, not a failure. Absent after an APPLIED delta would mean the row
                        // was deleted underneath us, which the per-store queue rules out - so rather
                        // than call it a count of zero, report it as unknown and let the caller
                        // re-read.
                        if (row == null)
                            return committed ? AccountVaultStackAdjustResult.AppliedCountUnknown : AccountVaultStackAdjustResult.Applied;

                        newCount = row.Count;
                        newTotalValue = row.TotalValue;

                        return AccountVaultStackAdjustResult.Applied;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[VAULT] TryAdjustAccountVaultClass: the read-back failed for account {accountId}, class {classKey}: {ex.GetFullMessage()}. The deltas themselves {(committed ? "DID commit" : "were a no-op")}.");

                        return committed ? AccountVaultStackAdjustResult.AppliedCountUnknown : AccountVaultStackAdjustResult.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                // Only the context construction and disposal are left out here, both of which run
                // before any statement or after the last one. Nothing can have been applied without
                // `committed` recording it.
                log.Error($"[VAULT] TryAdjustAccountVaultClass failed for account {accountId}, class {classKey}: {ex.GetFullMessage()}");

                return committed ? AccountVaultStackAdjustResult.AppliedCountUnknown : AccountVaultStackAdjustResult.Failed;
            }
        }

        // ---- the BATCHED deposit form (SPEC-vault-batch-deposit.md sections 2, 5 step 1 and 6) ----
        //
        // This is TryAdjustAccountVaultStackBatch's shape one column wider, and section 6 of the spec
        // requires the two to be IDENTICAL on the statement order, the preconditions, the
        // failed-identifying-SELECT rule, the never-re-apply rule, the context discipline and the
        // naming. Read that method's remarks for the reasoning behind every one of them; only the
        // differences are restated here.
        //
        // DEPOSIT ONLY, for the same reason: every count delta is asserted strictly positive, so with
        // every stored count non-negative neither WHERE guard can ever refuse, MATCHED equals PRESENT
        // exactly, and "absent = carried - present" is a sound inference rather than a guess.

        /// <summary>
        /// The multi-row guarded UPDATE that credits every ALREADY-PRESENT class key of one batch in a
        /// single statement. Built by <see cref="BuildAccountVaultClassBatchUpdateSql"/>, which replaces
        /// &lt;COUNTCASE&gt; and &lt;VALUECASE&gt; with a CASE over the key column and &lt;KEYS&gt; with
        /// the IN list. All three are made ENTIRELY of positional parameter placeholders: no class key,
        /// count or value is ever interpolated into this text.
        ///
        /// BOTH COLUMNS STILL MOVE IN ONE STATEMENT, exactly as
        /// <see cref="ApplyAccountVaultClassDeltaSql"/> requires: count and total_Value describe the
        /// same pool, and a row whose count says N while its total belongs to N+1 hands out the wrong
        /// Value per item on the next withdraw, which is an unrecoverable rewrite of a player's
        /// property.
        ///
        /// Both non-negative guards stay in the WHERE, never in an IF() in the SET clause, because
        /// MySqlConnector runs with CLIENT_FOUND_ROWS and would report a refused row as one affected
        /// row. It matters more here than on the single-row statement, because this statement's affected
        /// count is also the only evidence of WHICH keys were credited.
        ///
        /// MySQL's CASE takes the FIRST matching WHEN, so a key appearing twice would silently drop the
        /// second pair of deltas AND leave matched equal to the distinct-key count, so no mismatch would
        /// fire and the identifying SELECT would never run. That is why the plan is built from a
        /// dictionary before this is ever generated.
        /// </summary>
        private const string AdjustAccountVaultClassBatchUpdateSql =
            "UPDATE `account_vault_class` SET `count` = `count` + <COUNTCASE>, `total_Value` = `total_Value` + <VALUECASE> " +
            "WHERE `account_Id` = {0} AND `class_Key` IN (<KEYS>) AND `count` + <COUNTCASE> >= 0 AND `total_Value` + <VALUECASE> >= 0";

        /// <summary>
        /// The multi-row INSERT ... ON DUPLICATE KEY UPDATE that credits the class keys the UPDATE
        /// provably did not match. &lt;ROWS&gt; becomes one parenthesised placeholder row per key.
        ///
        /// It carries NO non-negative guard, and that is sound ONLY because
        /// <see cref="TryAdjustAccountVaultClassBatch"/> refuses any batch holding a non-positive count
        /// delta or a negative value delta.
        ///
        /// The seed columns are written only on the INSERT branch, which is what makes a duplicate
        /// harmless: an existing row's canonical form is never overwritten, and it must not be, because
        /// the key is a hash of it and a row whose text stopped hashing to its own key could never be
        /// parsed back into an item. That is the same rule
        /// <see cref="EnsureAccountVaultClassRowSql"/> follows.
        ///
        /// It runs for the genuinely absent keys ONLY, which keeps the AUTO_INCREMENT burn on
        /// account_vault_class.id at one per genuinely new row. ON DUPLICATE KEY UPDATE rather than a
        /// plain INSERT because the caller's "already present" oracle is an in-memory index and may be
        /// stale: a key it called absent that actually exists takes the duplicate branch and is credited
        /// once, never twice.
        /// </summary>
        private const string AdjustAccountVaultClassBatchInsertSql =
            "INSERT INTO `account_vault_class` (`account_Id`, `class_Key`, `wcid`, `count`, `total_Value`, `value_Band_Pct`, `canonical_Form`) " +
            "VALUES <ROWS> " +
            "ON DUPLICATE KEY UPDATE `count` = `count` + VALUES(`count`), `total_Value` = `total_Value` + VALUES(`total_Value`)";

        /// <summary>
        /// One planned class group: the two deltas and the seed that would create the row if it turns
        /// out to be absent. A seed is required for EVERY key rather than only for the ones the caller
        /// believes are new, because a key the caller called present can still migrate into the INSERT
        /// after the identifying SELECT proves it absent.
        /// </summary>
        private readonly struct AccountVaultClassBatchGroup
        {
            public AccountVaultClassBatchGroup(long countDelta, long valueDelta, AccountVaultClassSeed seed)
            {
                CountDelta = countDelta;
                ValueDelta = valueDelta;
                Seed = seed;
            }

            public long CountDelta { get; }

            public long ValueDelta { get; }

            public AccountVaultClassSeed Seed { get; }
        }

        /// <summary>
        /// Generates the guarded multi-row UPDATE for <paramref name="keyCount"/> keys. Parameter 0 is
        /// the account; key i takes parameters 1 + 3i (the class key), 2 + 3i (the count delta) and
        /// 3 + 3i (the value delta).
        /// </summary>
        private static string BuildAccountVaultClassBatchUpdateSql(int keyCount)
        {
            var countCase = new StringBuilder("CASE `class_Key`");
            var valueCase = new StringBuilder("CASE `class_Key`");
            var keyList = new StringBuilder();

            for (var i = 0; i < keyCount; i++)
            {
                var keyArg = 1 + (i * 3);
                var countArg = keyArg + 1;
                var valueArg = keyArg + 2;

                countCase.Append(" WHEN {").Append(keyArg).Append("} THEN {").Append(countArg).Append('}');
                valueCase.Append(" WHEN {").Append(keyArg).Append("} THEN {").Append(valueArg).Append('}');

                if (i > 0)
                    keyList.Append(", ");

                keyList.Append('{').Append(keyArg).Append('}');
            }

            countCase.Append(" END");
            valueCase.Append(" END");

            return AdjustAccountVaultClassBatchUpdateSql
                .Replace("<COUNTCASE>", countCase.ToString())
                .Replace("<VALUECASE>", valueCase.ToString())
                .Replace("<KEYS>", keyList.ToString());
        }

        /// <summary>
        /// The positional argument array <see cref="BuildAccountVaultClassBatchUpdateSql"/> consumes:
        /// the account, then one (class key, count delta, value delta) triple per key.
        /// </summary>
        private static object[] BuildAccountVaultClassBatchUpdateArgs(uint accountId, List<string> keys,
                                                                     Dictionary<string, AccountVaultClassBatchGroup> plan)
        {
            var args = new object[1 + (keys.Count * 3)];

            args[0] = accountId;

            for (var i = 0; i < keys.Count; i++)
            {
                var group = plan[keys[i]];

                args[1 + (i * 3)] = keys[i];
                args[2 + (i * 3)] = group.CountDelta;
                args[3 + (i * 3)] = group.ValueDelta;
            }

            return args;
        }

        /// <summary>
        /// Generates the multi-row INSERT ... ON DUPLICATE KEY UPDATE for <paramref name="keyCount"/>
        /// keys. Parameter 0 is the account; key i takes parameters 1 + 6i through 6 + 6i, which is a
        /// WIDER layout than the UPDATE's because a new class row needs its seed columns as well as its
        /// deltas. That is the one place this method's shape cannot mirror the stack batch's, where a
        /// single argument builder serves both statements.
        /// </summary>
        private static string BuildAccountVaultClassBatchInsertSql(int keyCount)
        {
            var rows = new StringBuilder();

            for (var i = 0; i < keyCount; i++)
            {
                var keyArg = 1 + (i * 6);

                if (i > 0)
                    rows.Append(", ");

                rows.Append("({0}");

                for (var arg = keyArg; arg < keyArg + 6; arg++)
                    rows.Append(", {").Append(arg).Append('}');

                rows.Append(')');
            }

            return AdjustAccountVaultClassBatchInsertSql.Replace("<ROWS>", rows.ToString());
        }

        /// <summary>
        /// The positional argument array <see cref="BuildAccountVaultClassBatchInsertSql"/> consumes:
        /// the account, then (class key, wcid, count delta, value delta, value band, canonical form)
        /// per key.
        /// </summary>
        private static object[] BuildAccountVaultClassBatchInsertArgs(uint accountId, List<string> keys,
                                                                      Dictionary<string, AccountVaultClassBatchGroup> plan)
        {
            var args = new object[1 + (keys.Count * 6)];

            args[0] = accountId;

            for (var i = 0; i < keys.Count; i++)
            {
                var group = plan[keys[i]];

                args[1 + (i * 6)] = keys[i];
                args[2 + (i * 6)] = group.Seed.Wcid;
                args[3 + (i * 6)] = group.CountDelta;
                args[4 + (i * 6)] = group.ValueDelta;
                args[5 + (i * 6)] = group.Seed.ValueBandPct;
                args[6 + (i * 6)] = group.Seed.CanonicalForm;
            }

            return args;
        }

        /// <summary>
        /// Marks every key of a batch <see cref="AccountVaultStackAdjustResult.Refused"/>, for a
        /// precondition that refuses before any statement runs.
        /// </summary>
        private static void RefuseWholeAccountVaultClassBatch(IReadOnlyList<(string ClassKey, long CountDelta, long ValueDelta, AccountVaultClassSeed Seed)> groups,
                                                              Dictionary<string, AccountVaultStackAdjustResult> outcomes)
        {
            foreach (var entry in groups)
            {
                if (!string.IsNullOrEmpty(entry.ClassKey))
                    outcomes[entry.ClassKey] = AccountVaultStackAdjustResult.Refused;
            }
        }

        /// <summary>
        /// Credits N DISTINCT class keys in one or two statements against one context. Deposit only.
        ///
        /// This is <see cref="TryAdjustAccountVaultStackBatch"/>'s contract with a second column and a
        /// seed, and it is deliberately identical to it on every point section 6 of the spec names.
        /// Read that method's remarks for the reasoning; restated here only because a reader who opened
        /// this file did not open that one:
        ///
        ///   1. STATEMENT ORDER, never the reverse: one guarded multi-row UPDATE for the keys
        ///      <paramref name="knownPresent"/> claims exist; ONLY on a matched-count mismatch, ONE
        ///      identifying SELECT over those same keys; then one multi-row
        ///      INSERT ... ON DUPLICATE KEY UPDATE for the genuinely absent keys.
        ///   2. A KEY THE UPDATE MATCHED IS NEVER RE-APPLIED. The INSERT's duplicate branch ADDS its
        ///      deltas, so carrying an already-credited key into it double-credits that key. The
        ///      INSERT's key list is built only from keys the UPDATE never carried and keys the
        ///      identifying SELECT proved absent, never from "the ones I am unsure about".
        ///   3. A FAILED IDENTIFYING SELECT FAILS THE WHOLE CALL: every key
        ///      <see cref="AccountVaultStackAdjustResult.Failed"/>, no INSERT attempted. The natural
        ///      fallback - "I do not know which keys are present, so upsert them all" - double-credits
        ///      every key the UPDATE already matched. The single-row method's equivalent failure is
        ///      benign, so this cannot be pattern-matched off the code this one is modelled on.
        ///   4. PRECONDITIONS, asserted, refusing the WHOLE call: every count delta STRICTLY POSITIVE,
        ///      every value delta NON-NEGATIVE, every key DISTINCT and non-empty, and every key carrying
        ///      a usable seed. The plan is built from a dictionary so a duplicate key is impossible by
        ///      construction and the assert is the second line of defence rather than the first.
        ///   5. NO READ-BACK. The store reloads the whole account separately.
        ///
        /// WHY THE VALUE DELTA IS NON-NEGATIVE RATHER THAN STRICTLY POSITIVE, and it is the one place
        /// this method's preconditions are not word-for-word the stack batch's. An item's pooled value
        /// contribution is its Value clamped at zero (AccountVaultStore's class branch reads
        /// item.Value ?? 0 and clamps a negative to 0), so a legitimately worthless item contributes a
        /// value delta of exactly 0 and a strictly-positive rule would refuse its whole sale. Every
        /// property the positive rule buys is carried by the COUNT delta alone: count is what the
        /// guards adjudicate, a positive count delta always CHANGES the row so the affected count means
        /// the same thing under either UseAffectedRows setting, and total_Value + 0 >= 0 holds for any
        /// non-negative stored total.
        ///
        /// THE DISTINCT-KEY PRECONDITION IS ORDINAL, THE COLUMN IS NOT, AND THE ALPHABET IS WHAT KEEPS
        /// THEM IN AGREEMENT. Every key comparison on this side is ordinal - the plan dictionary, the
        /// outcome dictionary and the duplicate check all use <c>StringComparer.Ordinal</c> - while
        /// <c>account_vault_class.class_Key</c> is <c>char(32)</c> declared with no COLLATE clause
        /// (Database/Updates/Shard/2026-09-24-00-Add-Account-Vault-Class.sql), so it inherits the
        /// database's default utf8mb4 collation, which is case- and accent-INSENSITIVE on both engines
        /// this runs against. Two keys that are ordinal-distinct but collation-EQUAL would therefore pass
        /// the duplicate check here and then collide inside <c>CASE class_Key WHEN ...</c>, where MySQL
        /// takes the FIRST matching WHEN: the second group's deltas would be silently dropped while the
        /// matched count still equalled the distinct-key count, so no mismatch would fire, the identifying
        /// SELECT would never run, and the sale would report clean with its items destroyed. That is
        /// exactly the under-credit the precondition exists to prevent, arrived at through the one door it
        /// does not watch.
        ///
        /// It cannot happen today, and the reason is the KEY ALPHABET rather than anything in this file:
        /// <c>VaultItemClass.ClassKey</c> renders 16 SHA-256 bytes with <c>ToString("x2")</c>, so every key
        /// is 32 characters drawn from [0-9a-f] - no letter has an uppercase twin in the set and no
        /// character has an accented form, which leaves collation-equality and ordinal-equality the same
        /// relation. A key format that ever admitted uppercase hex, base64 or any non-ASCII text would
        /// break that agreement silently, in this method, with no test here to catch it. Change the key
        /// format and either pin the column to a binary collation or make this check collation-aware.
        ///
        /// OUTCOMES. <see cref="AccountVaultStackAdjustResult.Applied"/> on success;
        /// <see cref="AccountVaultStackAdjustResult.Failed"/> for every key carried by a statement that
        /// threw; <see cref="AccountVaultStackAdjustResult.Refused"/> only where the ledger provably did
        /// not move. Given the preconditions the database can never refuse a key, so Refused here means
        /// the whole call was refused before any statement ran.
        ///
        /// <paramref name="knownPresent"/> is a HINT, not a promise, and a stale one is bounded rather
        /// than fatal - see the stack batch's remarks.
        ///
        /// No user-initiated transaction, per this file's header: each statement is its own implicit
        /// transaction, so one (table, key) group never half-commits. The groups are NOT atomic with
        /// each other and do not need to be.
        /// </summary>
        public bool TryAdjustAccountVaultClassBatch(uint accountId,
                                                    IReadOnlyList<(string ClassKey, long CountDelta, long ValueDelta, AccountVaultClassSeed Seed)> groups,
                                                    IReadOnlySet<string> knownPresent,
                                                    out IReadOnlyDictionary<string, AccountVaultStackAdjustResult> perKey)
        {
            var outcomes = new Dictionary<string, AccountVaultStackAdjustResult>(StringComparer.Ordinal);

            perKey = outcomes;

            if (groups == null || groups.Count == 0)
                return true;

            var plan = new Dictionary<string, AccountVaultClassBatchGroup>(groups.Count, StringComparer.Ordinal);
            var order = new List<string>(groups.Count);

            foreach (var entry in groups)
            {
                if (string.IsNullOrEmpty(entry.ClassKey))
                {
                    log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: account {accountId} carried a group with no class key. Refusing the whole call of {groups.Count} key(s).");

                    RefuseWholeAccountVaultClassBatch(groups, outcomes);
                    return false;
                }

                if (entry.CountDelta <= 0 || entry.ValueDelta < 0)
                {
                    log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: account {accountId} carried count delta {entry.CountDelta} and value delta {entry.ValueDelta} for class {entry.ClassKey}. This method is deposit-only and its multi-row forms are only safe while every count delta is positive and every value delta is non-negative. Refusing the whole call of {groups.Count} key(s).");

                    RefuseWholeAccountVaultClassBatch(groups, outcomes);
                    return false;
                }

                if (entry.Seed == null || string.IsNullOrEmpty(entry.Seed.CanonicalForm))
                {
                    log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: account {accountId} carried no usable seed for class {entry.ClassKey}. A seed is required for every key, not only the ones believed new, because the identifying SELECT can prove any key absent. Refusing the whole call of {groups.Count} key(s).");

                    RefuseWholeAccountVaultClassBatch(groups, outcomes);
                    return false;
                }

                if (plan.ContainsKey(entry.ClassKey))
                {
                    log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: account {accountId} carried class {entry.ClassKey} more than once. A repeated key would silently drop the second pair of deltas AND suppress the mismatch that would have caught it. Refusing the whole call of {groups.Count} key(s).");

                    RefuseWholeAccountVaultClassBatch(groups, outcomes);
                    return false;
                }

                plan.Add(entry.ClassKey, new AccountVaultClassBatchGroup(entry.CountDelta, entry.ValueDelta, entry.Seed));
                order.Add(entry.ClassKey);
            }

            // Partition, per the owner's C1 ruling: keys the caller's index already holds take the
            // UPDATE, everything else goes straight to the INSERT, so the AUTO_INCREMENT burn stays at
            // one id per genuinely new row.
            var updateKeys = new List<string>(order.Count);
            var insertKeys = new List<string>(order.Count);

            foreach (var classKey in order)
            {
                if (knownPresent != null && knownPresent.Contains(classKey))
                    updateKeys.Add(classKey);
                else
                    insertKeys.Add(classKey);
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
                            matched = context.Database.ExecuteSqlRaw(BuildAccountVaultClassBatchUpdateSql(updateKeys.Count),
                                                                    BuildAccountVaultClassBatchUpdateArgs(accountId, updateKeys, plan));
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: the guarded multi-row UPDATE threw for account {accountId} carrying {updateKeys.Count} key(s): {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");

                            updateThrew = true;

                            // Every key this statement carried is Failed, and NONE of them is added to
                            // the INSERT's key list. The duplicate branch ADDS, so retrying a key whose
                            // UPDATE may have committed would double-credit it.
                            foreach (var classKey in updateKeys)
                                outcomes[classKey] = AccountVaultStackAdjustResult.Failed;
                        }

                        if (!updateThrew && matched == updateKeys.Count)
                        {
                            // Every key the statement carried was present, and every present key was
                            // credited: neither guard can refuse a positive count delta paired with a
                            // non-negative value delta.
                            foreach (var classKey in updateKeys)
                                outcomes[classKey] = AccountVaultStackAdjustResult.Applied;
                        }
                        else if (!updateThrew)
                        {
                            // ONE identifying SELECT, and only here. A row present NOW was present when
                            // the UPDATE ran - nothing else writes this account - so present means
                            // credited and absent means the UPDATE could not have touched it.
                            List<string> present;

                            try
                            {
                                present = context.AccountVaultClass
                                    .Where(c => c.AccountId == accountId && updateKeys.Contains(c.ClassKey))
                                    .Select(c => c.ClassKey)
                                    .ToList();
                            }
                            catch (Exception ex)
                            {
                                log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: the identifying SELECT failed for account {accountId} after the UPDATE matched {matched} of {updateKeys.Count} key(s): {ex.GetFullMessage()}. The WHOLE call is Failed and no INSERT is attempted - upserting the unknown keys would double-credit every key the UPDATE already matched.");

                                foreach (var classKey in order)
                                    outcomes[classKey] = AccountVaultStackAdjustResult.Failed;

                                return false;
                            }

                            var presentKeys = new HashSet<string>(present, StringComparer.Ordinal);

                            foreach (var classKey in updateKeys)
                            {
                                if (presentKeys.Contains(classKey))
                                    outcomes[classKey] = AccountVaultStackAdjustResult.Applied;
                                else
                                    insertKeys.Add(classKey);
                            }
                        }
                    }

                    if (insertKeys.Count > 0)
                    {
                        try
                        {
                            context.Database.ExecuteSqlRaw(BuildAccountVaultClassBatchInsertSql(insertKeys.Count),
                                                           BuildAccountVaultClassBatchInsertArgs(accountId, insertKeys, plan));

                            // One statement, one implicit transaction: it committed for every row it
                            // carried or for none. Its affected count is deliberately not read - under
                            // ON DUPLICATE KEY UPDATE it is 1 per inserted row and 2 per updated row,
                            // which answers nothing this method asks.
                            foreach (var classKey in insertKeys)
                                outcomes[classKey] = AccountVaultStackAdjustResult.Applied;
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[VAULT] TryAdjustAccountVaultClassBatch: the multi-row INSERT threw for account {accountId} carrying {insertKeys.Count} key(s): {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");

                            foreach (var classKey in insertKeys)
                                outcomes[classKey] = AccountVaultStackAdjustResult.Failed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Only the context construction and disposal are left out above. Any key that never
                // reached an outcome is swept into Failed by the loop below: nobody knows whether it
                // moved, and an outcome already recorded is left alone because it is known.
                log.Error($"[VAULT] TryAdjustAccountVaultClassBatch failed for account {accountId} carrying {order.Count} key(s): {ex.GetFullMessage()}");
            }

            foreach (var classKey in order)
            {
                if (!outcomes.ContainsKey(classKey))
                    outcomes[classKey] = AccountVaultStackAdjustResult.Failed;
            }

            return order.All(classKey => outcomes[classKey] == AccountVaultStackAdjustResult.Applied);
        }

        /// <summary>
        /// The account's whole counted class ledger, ordered by class key so the panel's row order is
        /// stable across reads.
        ///
        /// Returns NULL if the read failed; an empty list means the account holds no classes. See the
        /// read failure contract on the class doc - the two must never be conflated.
        /// </summary>
        public List<AccountVaultClass> GetAccountVaultClasses(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVaultClass.Where(c => c.AccountId == accountId).OrderBy(c => c.ClassKey).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAccountVaultClasses failed for account {accountId}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Drops class rows that have fallen to zero items. Cosmetic, not correctness: a zero row is a
        /// harmless empty line the store filters out anyway. Called opportunistically after a withdraw,
        /// mirroring DeleteEmptyAccountVaultStacks.
        ///
        /// Keyed on the COUNT alone. A row at zero items whose total_Value is somehow non-zero is a
        /// pool that has lost its items, and deleting it is the only way the leftover total stops
        /// being handed out; leaving it would make the next deposit into that class inherit a stranger's
        /// value.
        /// </summary>
        public int DeleteEmptyAccountVaultClasses(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    return context.AccountVaultClass.Where(c => c.AccountId == accountId && c.Count <= 0).ExecuteDelete();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] DeleteEmptyAccountVaultClasses failed for account {accountId}: {ex.GetFullMessage()}");
                return 0;
            }
        }
    }
}

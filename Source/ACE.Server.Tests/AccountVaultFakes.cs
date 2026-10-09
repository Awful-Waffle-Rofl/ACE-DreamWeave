using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Recording in-memory stand-in for the four account_vault* tables.
    ///
    /// It models the two DAO behaviours the store's correctness rests on:
    ///
    /// - a read that FAILS returns null, and a read that finds nothing returns an empty list. The
    ///   failure switches exist so the "refuses rather than creating a vault" tests can drive that
    ///   path, which is the one that would otherwise regress silently.
    ///
    /// - TryAdjustAccountVaultStack refuses a delta that would take the count below zero and reports
    ///   that as false, with the ledger unmoved.
    ///
    /// The adjust is deliberately implemented as a NON-atomic read-modify-write with an optional
    /// delay between the two halves. The real DAO applies the delta in one guarded UPDATE, so this is
    /// not a model of it - it is a model of what the store must survive when the guard is not there,
    /// which is what makes the concurrency tests able to fail.
    /// </summary>
    internal class FakeVaultBackend : IAccountVaultBackend
    {
        public readonly List<ShardAccountVault> Vaults = new List<ShardAccountVault>();
        public readonly List<AccountVaultStack> Stacks = new List<AccountVaultStack>();
        public readonly List<AccountVaultGrant> Grants = new List<AccountVaultGrant>();
        public readonly List<AccountVaultLog> Logs = new List<AccountVaultLog>();

        public bool FailVaultRead;
        public bool FailStackRead;
        public bool FailGrantRead;
        public bool FailLogRead;

        public bool FailAddVault;

        /// <summary>
        /// Wcids whose ledger adjust is REFUSED: the ledger provably does not move. This is what the
        /// pre-F1 bool false meant at every call site, so every test written against the old switch
        /// keeps its exact meaning.
        /// </summary>
        public readonly HashSet<uint> FailLedgerAdjustWcids = new HashSet<uint>();

        /// <summary>
        /// Fix round 2, F1: wcids whose adjust APPLIES the delta and then reports
        /// AppliedCountUnknown - the real DAO's "guarded UPDATE committed, LINQ read-back failed" case.
        /// The delta really is applied here, which is what lets a test assert that the store re-reads
        /// the ledger and arrives at the right number without ever being told it.
        /// </summary>
        public readonly HashSet<uint> LedgerAdjustCountUnknownWcids = new HashSet<uint>();

        /// <summary>
        /// Fix round 2, F1: wcids whose adjust reports Failed - the UPDATE threw, so nobody knows
        /// whether it committed. Modelled as NOT applying the delta, which is the harder half for the
        /// callers: every caller has to be correct for a Failed that did not land AND for one that did,
        /// and only the not-landed half is observable from a fake.
        /// </summary>
        public readonly HashSet<uint> LedgerAdjustFailedWcids = new HashSet<uint>();

        /// <summary>Adjust calls, so a test can prove a second attempt was or was not made.</summary>
        public int LedgerAdjustCalls;

        /// <summary>
        /// Per-call override for the ledger adjust, consulted before every switch: return Refused or
        /// Failed to force that outcome (with the ledger unmoved), or null to adjust normally.
        /// </summary>
        public Func<uint, uint, long, AccountVaultStackAdjustResult?> LedgerAdjustOverride;

        /// <summary>The class ledger's equivalent of <see cref="LedgerAdjustOverride"/>, keyed by class key and count delta.</summary>
        public Func<string, long, AccountVaultStackAdjustResult?> ClassAdjustOverride;

        /// <summary>Milliseconds slept between the ledger read and the ledger write. 0 by default.</summary>
        public int LedgerAdjustDelayMs;

        public int VaultReads;

        /// <summary>
        /// Reads of the stack ledger. Fix round 2, F1: the store re-reads the ledger after a mutation
        /// that committed with an unknown resulting count, and this counter is how a test observes that
        /// the re-read genuinely HAPPENED rather than inferring it from a count that could also be
        /// right by accident.
        /// </summary>
        public int StackReads;

        public int GrantReads;
        public int AddedVaults;
        public int DeletedEmptyStackCalls;

        /// <summary>
        /// Write counts, so a test can assert that a repeat grant on identical terms costs NOTHING.
        /// Grants.Count cannot see this: the upsert updates the existing row rather than adding one, so
        /// a re-grant that rewrote the row and appended an audit row would leave the list length
        /// unchanged and look identical to a re-grant that did nothing at all.
        /// </summary>
        public int UpsertGrantCalls;

        public int AddLogCalls;

        /// <summary>
        /// Calls to the BATCH audit write, and rows those calls carried. Both are needed and neither
        /// is enough alone: <see cref="Logs"/>.Count proves the rows landed but says nothing about how
        /// many round trips paid for them, and the call count alone cannot prove nothing was lost.
        /// </summary>
        public int AddLogBatchCalls;

        public int AddLogRowsWritten;

        /// <summary>
        /// Makes both audit writes report FAILURE - which is exactly what the real DAO does when MySQL
        /// refuses, since it catches and returns false rather than throwing.
        /// </summary>
        public bool FailAddLog;

        /// <summary>
        /// Makes the next N audit writes THROW instead of reporting failure. Distinct from
        /// <see cref="FailAddLog"/> on purpose: the audit path's promise is that a logging outage can
        /// never cost a player their deposit, and a polite false exercises only half of that. The DAO
        /// catches everything it can reach, so this models what escapes when it cannot.
        /// </summary>
        public int ThrowFromAddLogCount;

        /// <summary>Every count this ledger has ever held. Asserted never to go negative.</summary>
        public readonly List<long> LedgerCountHistory = new List<long>();

        private uint nextRowId = 1;

        public List<ShardAccountVault> GetAccountVaults(uint accountId)
        {
            VaultReads++;

            if (FailVaultRead)
                return null;

            return Vaults.Where(v => v.AccountId == accountId).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToList();
        }

        /// <summary>
        /// The whole-table read the spawn filter seeds from. Honours <see cref="FailVaultRead"/> the
        /// same way GetAccountVaults does, because the seeding path's whole contract is that a null
        /// read is not an empty table.
        /// </summary>
        /// <summary>
        /// Makes the whole-table read THROW rather than return null. A DAO failure normally surfaces as
        /// null, but AccountVaultManager.TrySeedSpawnFilter also wraps the call in a try/catch so that
        /// nothing can escape into the startup path, and that catch is only reachable from here.
        /// </summary>
        public bool ThrowOnVaultRead;

        public List<uint> GetAllAccountVaultContainerGuids()
        {
            VaultReads++;

            if (ThrowOnVaultRead)
                throw new InvalidOperationException("simulated shard failure reading account_vault");

            if (FailVaultRead)
                return null;

            return Vaults.Select(v => v.ContainerGuid).ToList();
        }

        public bool AddAccountVault(ShardAccountVault row)
        {
            if (FailAddVault)
                return false;

            row.Id = nextRowId++;
            Vaults.Add(row);
            AddedVaults++;
            return true;
        }

        public bool DeleteAccountVault(uint id)
        {
            return Vaults.RemoveAll(v => v.Id == id) > 0;
        }

        public List<AccountVaultStack> GetAccountVaultStacks(uint accountId)
        {
            StackReads++;

            if (FailStackRead)
                return null;

            return Stacks.Where(s => s.AccountId == accountId).OrderBy(s => s.Wcid).ToList();
        }

        public AccountVaultStackAdjustResult TryAdjustAccountVaultStack(uint accountId, uint wcid, long delta, out long newCount)
        {
            newCount = 0;

            LedgerAdjustCalls++;

            // Consulted first, so a test can fail ONE adjust in a sequence - the Nth deposit of a
            // batch - which none of the per-wcid switches can express: they fail every call for the
            // wcid, including the earlier deposits the test needs to land.
            var overridden = LedgerAdjustOverride?.Invoke(accountId, wcid, delta);

            if (overridden == AccountVaultStackAdjustResult.Refused || overridden == AccountVaultStackAdjustResult.Failed)
                return overridden.Value;

            if (FailLedgerAdjustWcids.Contains(wcid))
                return AccountVaultStackAdjustResult.Refused;

            // Modelled as the UPDATE throwing: nothing is applied, and nobody can tell that from the
            // outside. See LedgerAdjustFailedWcids.
            if (LedgerAdjustFailedWcids.Contains(wcid))
                return AccountVaultStackAdjustResult.Failed;

            var row = Stacks.FirstOrDefault(s => s.AccountId == accountId && s.Wcid == wcid);

            if (row == null)
            {
                row = new AccountVaultStack { Id = nextRowId++, AccountId = accountId, Wcid = wcid, Count = 0 };
                Stacks.Add(row);
            }

            // Read half.
            var current = row.Count;

            if (LedgerAdjustDelayMs > 0)
                Thread.Sleep(LedgerAdjustDelayMs);

            var proposed = current + delta;

            if (proposed < 0)
                return AccountVaultStackAdjustResult.Refused;   // refused; the ledger did not move

            // Write half.
            row.Count = proposed;
            LedgerCountHistory.Add(proposed);

            // The delta IS applied above before this returns - that is the whole point of this case:
            // only the read-back is lost, so newCount stays at 0 and the caller must not use it.
            if (LedgerAdjustCountUnknownWcids.Contains(wcid))
                return AccountVaultStackAdjustResult.AppliedCountUnknown;

            newCount = proposed;
            return AccountVaultStackAdjustResult.Applied;
        }

        // ---- the BATCHED deposit form (SPEC-vault-batch-deposit.md step 1) ----
        //
        // Both batch fakes model the real DAO's two preconditions exactly - strictly positive deltas
        // and distinct keys, each refusing the WHOLE call - because a caller that only ever sees a
        // permissive fake would ship a plan the shard refuses.

        /// <summary>Calls to the batched stack adjust, so a test can count round trips exactly.</summary>
        public int StackBatchCalls;

        /// <summary>
        /// How many GROUPS the batched stack adjust COMMITS before it THROWS. -1, the default,
        /// disables it; 0 throws before committing anything.
        /// <para>
        /// The distinction from a fake that merely FAILS is the whole point of this switch, and the
        /// partial-failure contract cannot be exercised without it: a batch that throws outright leaves
        /// nothing committed, which is the easy half. The dangerous shape is k groups CREDITED and the
        /// call never returning, because the caller is then holding items whose credit has already
        /// landed and must not hand a single one of them back.
        /// </para>
        /// </summary>
        public int ThrowFromStackBatchAfterGroups = -1;

        /// <summary>
        /// How many GROUPS the batched stack adjust COMMITS before it reports every REMAINING group
        /// Failed. -1, the default, disables it.
        /// <para>
        /// This is the shape the real DAO actually produces - it catches its own exceptions and answers
        /// per key - while <see cref="ThrowFromStackBatchAfterGroups"/> models what escapes when it
        /// cannot. Both are needed: the first exercises the caller's Failed handling, the second its
        /// throw handling, and a caller can be correct for one and wrong for the other.
        /// </para>
        /// </summary>
        public int FailStackBatchAfterGroups = -1;

        public bool TryAdjustAccountVaultStackBatch(uint accountId, IReadOnlyList<(uint Wcid, long Delta)> deltas, IReadOnlySet<uint> knownPresent,
                                                    out IReadOnlyDictionary<uint, AccountVaultStackAdjustResult> perKey)
        {
            var outcomes = new Dictionary<uint, AccountVaultStackAdjustResult>();

            perKey = outcomes;

            StackBatchCalls++;

            // BEFORE ANY DELTA IS APPLIED, so a test can run arbitrary work at the exact moment a batch has
            // claimed its entries and not yet credited them - the interval VaultDepositWindow.ClaimsInFlight
            // covers. Fired before the precondition check as well, because the hook's point is WHEN it runs,
            // not whether this call went on to succeed.
            OnBeforeStackBatch?.Invoke();

            if (deltas == null || deltas.Count == 0)
                return true;

            var seen = new HashSet<uint>();

            foreach (var entry in deltas)
            {
                if (entry.Delta > 0 && seen.Add(entry.Wcid))
                    continue;

                // Precondition refused: nothing ran, so the ledger provably did not move.
                foreach (var refused in deltas)
                    outcomes[refused.Wcid] = AccountVaultStackAdjustResult.Refused;

                return false;
            }

            var committedGroups = 0;
            var identifyingReadNeeded = false;

            foreach (var entry in deltas)
            {
                if (ThrowFromStackBatchAfterGroups >= 0 && committedGroups >= ThrowFromStackBatchAfterGroups)
                    throw new InvalidOperationException($"fake failure after committing {committedGroups} of {deltas.Count} stack group(s) for account {accountId}");

                if (FailStackBatchAfterGroups >= 0 && committedGroups >= FailStackBatchAfterGroups)
                {
                    outcomes[entry.Wcid] = AccountVaultStackAdjustResult.Failed;
                    continue;
                }

                // ONE GROUP IS ONE GUARDED STATEMENT, so it counts as one adjust. Every pre-existing
                // single-item deposit test counts LedgerAdjustCalls and must go on seeing exactly what it
                // saw while TryDeposit called the single-key method directly - a one-item deposit is one
                // group, so the number is unchanged.
                LedgerAdjustCalls++;

                // The same per-key switches the single-key method honours, in its order, so a test that
                // arms one keeps working whichever method the store reaches for.
                var overridden = LedgerAdjustOverride?.Invoke(accountId, entry.Wcid, entry.Delta);

                if (overridden == AccountVaultStackAdjustResult.Refused || overridden == AccountVaultStackAdjustResult.Failed)
                {
                    outcomes[entry.Wcid] = overridden.Value;
                    continue;
                }

                if (FailLedgerAdjustWcids.Contains(entry.Wcid))
                {
                    outcomes[entry.Wcid] = AccountVaultStackAdjustResult.Refused;
                    continue;
                }

                if (LedgerAdjustFailedWcids.Contains(entry.Wcid))
                {
                    outcomes[entry.Wcid] = AccountVaultStackAdjustResult.Failed;
                    continue;
                }

                var row = Stacks.FirstOrDefault(s => s.AccountId == accountId && s.Wcid == entry.Wcid);

                // The real DAO's PARTITION, modelled rather than skipped, because a caller's in-memory
                // index is only a HINT and the whole safety argument for this shape is that a stale hint
                // is bounded. A key the oracle calls present that is NOT present is what forces the ONE
                // identifying SELECT, and it must still be credited exactly once - by the INSERT and not
                // also by the UPDATE.
                var oracleSaysPresent = knownPresent != null && knownPresent.Contains(entry.Wcid);

                if (!oracleSaysPresent)
                    StackBatchKeysOracleCalledAbsent++;

                if (oracleSaysPresent && row == null)
                {
                    identifyingReadNeeded = true;
                    StackBatchKeysUpsertedAfterIdentifyingRead++;
                }

                if (row == null)
                {
                    row = new AccountVaultStack { Id = nextRowId++, AccountId = accountId, Wcid = entry.Wcid, Count = 0 };
                    Stacks.Add(row);
                }

                if (LedgerAdjustDelayMs > 0)
                    Thread.Sleep(LedgerAdjustDelayMs);

                row.Count += entry.Delta;
                LedgerCountHistory.Add(row.Count);

                // EXACTLY ONCE per key per call, which is the property the UPDATE-matched-fewer-rows path
                // exists to protect: a test asserts this is 1 for every key, because a double credit is
                // invisible in an outcome and visible only in a count.
                StackBatchCreditsApplied.TryGetValue(entry.Wcid, out var credits);
                StackBatchCreditsApplied[entry.Wcid] = credits + 1;

                // LedgerAdjustCountUnknownWcids is deliberately INERT here, and that is a modelling
                // decision rather than an omission: the batch DAO has no read-back at all, so "the delta
                // landed but its count could not be read" is not an outcome it can produce. The store's
                // whole-account reload is what supplies every count on this path.
                outcomes[entry.Wcid] = AccountVaultStackAdjustResult.Applied;
                committedGroups++;
            }

            if (identifyingReadNeeded)
                StackBatchIdentifyingReads++;

            return outcomes.Values.All(o => o == AccountVaultStackAdjustResult.Applied);
        }

        /// <summary>
        /// How many times each wcid has had a batched delta APPLIED to it, across every call. The
        /// UPDATE-matched-fewer-rows path is the one place a bug double-credits, and a double credit is
        /// invisible in the returned outcome - it shows up only as a count that is twice what it should
        /// be. This counts the applications themselves so a test can assert exactly one per key.
        /// </summary>
        public readonly Dictionary<uint, int> StackBatchCreditsApplied = new Dictionary<uint, int>();

        /// <summary>
        /// Batched stack CALLS in which at least one key the oracle called present turned out to be
        /// absent - the mismatch that forces the real DAO's ONE identifying SELECT. Counted per call, not
        /// per key, because the real statement is one read over all of them.
        /// </summary>
        public int StackBatchIdentifyingReads;

        /// <summary>
        /// Keys the oracle called ABSENT, across every call, and which therefore route to the multi-row
        /// INSERT..ON DUPLICATE KEY UPDATE rather than to the guarded UPDATE. Calling a PRESENT row absent
        /// is not a correctness bug - the upsert lands the same delta - but each one burns an
        /// AUTO_INCREMENT id for a row that already exists, and account_vault_stack.id is int unsigned.
        /// Counted per KEY rather than per call, because the burn is per key.
        /// </summary>
        public int StackBatchKeysOracleCalledAbsent;

        /// <summary>Keys the oracle called present that were not, and so reached the upsert instead of the UPDATE.</summary>
        public int StackBatchKeysUpsertedAfterIdentifyingRead;

        /// <summary>
        /// Run at the TOP of <see cref="TryAdjustAccountVaultStackBatch"/>, before any delta is applied.
        ///
        /// It exists to let a test inject a reader at the one instant that is otherwise unreachable from a
        /// single-threaded test: a batch has recorded its cap claims in phase A and has not yet credited
        /// them in phase B. A reload landing there is what makes the claims interval a cap question rather
        /// than a bookkeeping one, and the reader does not need a second thread - EntryCount passes
        /// tolerateDeferredReload: false, so calling it from inside this hook performs the re-read on the
        /// deposit's own thread.
        ///
        /// Re-arm or clear it from inside the handler to target one specific call.
        /// </summary>
        public Action OnBeforeStackBatch;

        /// <summary>
        /// Fix round 2, F4: how many further DeleteEmptyAccountVaultStacks calls must THROW. This is
        /// the one hook in the ledger withdraw path that runs AFTER the debit and AFTER the built
        /// stacks have been handed to the caller's out parameter, which is exactly the shape the
        /// vendor's throw handling has to survive: the vault is already short, the objects already
        /// exist, and the call never returns.
        /// </summary>
        public int ThrowFromDeleteEmptyStacksCount;

        public int DeleteEmptyAccountVaultStacks(uint accountId)
        {
            if (ThrowFromDeleteEmptyStacksCount > 0)
            {
                ThrowFromDeleteEmptyStacksCount--;
                throw new InvalidOperationException($"fake failure reaping empty ledger rows for account {accountId}");
            }

            DeletedEmptyStackCalls++;

            return Stacks.RemoveAll(s => s.AccountId == accountId && s.Count <= 0);
        }

        // ---- account_vault_class, the counted item-class ledger ----

        public readonly List<AccountVaultClass> Classes = new List<AccountVaultClass>();

        public bool FailClassRead;

        /// <summary>
        /// Reads of the class ledger. Same purpose as <see cref="StackReads"/>: it is how a test
        /// observes that an invalidating RE-READ genuinely happened rather than inferring it from a
        /// count that could also be right by accident.
        /// </summary>
        public int ClassReads;

        /// <summary>Adjust calls against the class ledger, so a test can count per-deposit work exactly.</summary>
        public int ClassAdjustCalls;

        public int DeletedEmptyClassCalls;

        /// <summary>
        /// Class keys whose adjust is REFUSED: the row provably does not move. The mirror of
        /// <see cref="FailLedgerAdjustWcids"/>.
        /// </summary>
        public readonly HashSet<string> FailClassAdjustKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Class keys whose adjust APPLIES the deltas and then reports AppliedCountUnknown. The deltas
        /// really are applied, which is what lets a test assert the store re-reads and arrives at the
        /// right numbers without ever being told them.
        /// </summary>
        public readonly HashSet<string> ClassAdjustCountUnknownKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Class keys whose adjust reports Failed. Modelled as NOT applying the deltas, which is the
        /// harder half for the callers: every caller has to be correct for a Failed that did not land
        /// AND for one that did, and only the not-landed half is observable from a fake.
        /// </summary>
        public readonly HashSet<string> ClassAdjustFailedKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Class keys whose adjust reports Failed for a BOUNDED number of calls and then behaves
        /// normally - a countdown per key, decremented on every call for that key.
        /// <para>
        /// This exists because <see cref="ClassAdjustFailedKeys"/> is permanent, which silently couples
        /// a debit to its own refund: the refund issued to undo a Failed debit hits the same switch and
        /// is refused too, so the row cannot move and the genuinely dangerous shape is unreachable from
        /// a fake. That shape is a debit that reported Failed WITHOUT landing, followed by a refund that
        /// DOES land - the row ends over-credited by exactly the refunded amount, and nothing in the
        /// data says so. Set the countdown to 1 to get it.
        /// </para>
        /// </summary>
        public readonly Dictionary<string, int> ClassAdjustFailedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        public List<AccountVaultClass> GetAccountVaultClasses(uint accountId)
        {
            ClassReads++;

            if (FailClassRead)
                return null;

            return Classes.Where(c => c.AccountId == accountId).OrderBy(c => c.ClassKey, StringComparer.Ordinal).ToList();
        }

        public AccountVaultStackAdjustResult TryAdjustAccountVaultClass(uint accountId, string classKey, long countDelta, long valueDelta,
                                                                       AccountVaultClassSeed seed, out long newCount, out long newTotalValue)
        {
            newCount = 0;
            newTotalValue = 0;

            ClassAdjustCalls++;

            if (string.IsNullOrEmpty(classKey))
                return AccountVaultStackAdjustResult.Refused;

            var overridden = ClassAdjustOverride?.Invoke(classKey, countDelta);

            if (overridden == AccountVaultStackAdjustResult.Refused || overridden == AccountVaultStackAdjustResult.Failed)
                return overridden.Value;

            if (FailClassAdjustKeys.Contains(classKey))
                return AccountVaultStackAdjustResult.Refused;

            if (ClassAdjustFailedKeys.Contains(classKey))
                return AccountVaultStackAdjustResult.Failed;

            if (ClassAdjustFailedCounts.TryGetValue(classKey, out var remainingFailures) && remainingFailures > 0)
            {
                ClassAdjustFailedCounts[classKey] = remainingFailures - 1;
                return AccountVaultStackAdjustResult.Failed;
            }

            var row = Classes.FirstOrDefault(c => c.AccountId == accountId && c.ClassKey == classKey);

            if (row == null)
            {
                // A withdraw NEVER creates a row, exactly as the real DAO refuses to. Nor does an
                // adjust with no seed to create one from.
                if (countDelta < 0 || seed == null || string.IsNullOrEmpty(seed.CanonicalForm))
                    return AccountVaultStackAdjustResult.Refused;

                row = new AccountVaultClass
                {
                    Id = nextRowId++,
                    AccountId = accountId,
                    ClassKey = classKey,
                    Wcid = seed.Wcid,
                    Count = 0,
                    TotalValue = 0,
                    ValueBandPct = seed.ValueBandPct,
                    CanonicalForm = seed.CanonicalForm,
                };

                Classes.Add(row);
            }

            var proposedCount = row.Count + countDelta;
            var proposedValue = row.TotalValue + valueDelta;

            // BOTH guards, matching the real statement's WHERE clause. Either one failing refuses the
            // whole adjust and leaves the row exactly as it was, which is what keeps the count and the
            // total from ever describing different numbers of items.
            if (proposedCount < 0 || proposedValue < 0)
                return AccountVaultStackAdjustResult.Refused;

            row.Count = proposedCount;
            row.TotalValue = proposedValue;

            if (ClassAdjustCountUnknownKeys.Contains(classKey))
                return AccountVaultStackAdjustResult.AppliedCountUnknown;

            newCount = proposedCount;
            newTotalValue = proposedValue;

            return AccountVaultStackAdjustResult.Applied;
        }

        /// <summary>Calls to the batched class adjust, so a test can count round trips exactly.</summary>
        public int ClassBatchCalls;

        /// <summary>
        /// How many GROUPS the batched class adjust COMMITS before it THROWS. -1, the default,
        /// disables it; 0 throws before committing anything. The mirror of
        /// <see cref="ThrowFromStackBatchAfterGroups"/>, and it exists for the same reason: a fake that
        /// only fails outright leaves nothing committed, which is the easy half of the contract.
        /// </summary>
        public int ThrowFromClassBatchAfterGroups = -1;

        /// <summary>
        /// How many GROUPS the batched class adjust COMMITS before it reports every REMAINING group
        /// Failed. -1, the default, disables it. The mirror of
        /// <see cref="FailStackBatchAfterGroups"/>, and the shape the real DAO actually produces.
        /// </summary>
        public int FailClassBatchAfterGroups = -1;

        public bool TryAdjustAccountVaultClassBatch(uint accountId,
                                                    IReadOnlyList<(string ClassKey, long CountDelta, long ValueDelta, AccountVaultClassSeed Seed)> groups,
                                                    IReadOnlySet<string> knownPresent,
                                                    out IReadOnlyDictionary<string, AccountVaultStackAdjustResult> perKey)
        {
            var outcomes = new Dictionary<string, AccountVaultStackAdjustResult>(StringComparer.Ordinal);

            perKey = outcomes;

            ClassBatchCalls++;

            if (groups == null || groups.Count == 0)
                return true;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in groups)
            {
                // The real DAO's preconditions, in its order: a usable key, a strictly positive count
                // delta, a NON-negative value delta, a usable seed for EVERY key, and distinctness.
                if (!string.IsNullOrEmpty(entry.ClassKey) && entry.CountDelta > 0 && entry.ValueDelta >= 0 &&
                    entry.Seed != null && !string.IsNullOrEmpty(entry.Seed.CanonicalForm) && seen.Add(entry.ClassKey))
                {
                    continue;
                }

                foreach (var refused in groups)
                {
                    if (!string.IsNullOrEmpty(refused.ClassKey))
                        outcomes[refused.ClassKey] = AccountVaultStackAdjustResult.Refused;
                }

                return false;
            }

            var committedGroups = 0;
            var identifyingReadNeeded = false;

            foreach (var entry in groups)
            {
                if (ThrowFromClassBatchAfterGroups >= 0 && committedGroups >= ThrowFromClassBatchAfterGroups)
                    throw new InvalidOperationException($"fake failure after committing {committedGroups} of {groups.Count} class group(s) for account {accountId}");

                if (FailClassBatchAfterGroups >= 0 && committedGroups >= FailClassBatchAfterGroups)
                {
                    outcomes[entry.ClassKey] = AccountVaultStackAdjustResult.Failed;
                    continue;
                }

                // One group is one guarded statement, counted as one adjust - see the stack batch's own
                // remark for why the pre-existing per-deposit counts must not move.
                ClassAdjustCalls++;

                var overridden = ClassAdjustOverride?.Invoke(entry.ClassKey, entry.CountDelta);

                if (overridden == AccountVaultStackAdjustResult.Refused || overridden == AccountVaultStackAdjustResult.Failed)
                {
                    outcomes[entry.ClassKey] = overridden.Value;
                    continue;
                }

                if (FailClassAdjustKeys.Contains(entry.ClassKey))
                {
                    outcomes[entry.ClassKey] = AccountVaultStackAdjustResult.Refused;
                    continue;
                }

                if (ClassAdjustFailedKeys.Contains(entry.ClassKey))
                {
                    outcomes[entry.ClassKey] = AccountVaultStackAdjustResult.Failed;
                    continue;
                }

                if (ClassAdjustFailedCounts.TryGetValue(entry.ClassKey, out var remainingFailures) && remainingFailures > 0)
                {
                    ClassAdjustFailedCounts[entry.ClassKey] = remainingFailures - 1;
                    outcomes[entry.ClassKey] = AccountVaultStackAdjustResult.Failed;
                    continue;
                }

                var row = Classes.FirstOrDefault(c => c.AccountId == accountId && c.ClassKey == entry.ClassKey);

                var oracleSaysPresent = knownPresent != null && knownPresent.Contains(entry.ClassKey);

                if (oracleSaysPresent && row == null)
                {
                    identifyingReadNeeded = true;
                    ClassBatchKeysUpsertedAfterIdentifyingRead++;
                }

                if (row == null)
                {
                    row = new AccountVaultClass
                    {
                        Id = nextRowId++,
                        AccountId = accountId,
                        ClassKey = entry.ClassKey,
                        Wcid = entry.Seed.Wcid,
                        Count = 0,
                        TotalValue = 0,
                        ValueBandPct = entry.Seed.ValueBandPct,
                        CanonicalForm = entry.Seed.CanonicalForm,
                    };

                    Classes.Add(row);
                }

                row.Count += entry.CountDelta;
                row.TotalValue += entry.ValueDelta;

                ClassBatchCreditsApplied.TryGetValue(entry.ClassKey, out var credits);
                ClassBatchCreditsApplied[entry.ClassKey] = credits + 1;

                // ClassAdjustCountUnknownKeys is INERT here, for the reason the stack batch's remark
                // gives: the batch DAO has no read-back, so it cannot produce AppliedCountUnknown.
                outcomes[entry.ClassKey] = AccountVaultStackAdjustResult.Applied;
                committedGroups++;
            }

            if (identifyingReadNeeded)
                ClassBatchIdentifyingReads++;

            return outcomes.Values.All(o => o == AccountVaultStackAdjustResult.Applied);
        }

        /// <summary>
        /// <see cref="StackBatchCreditsApplied"/>'s counterpart: how many times each class key has had a
        /// batched delta applied, so a test can assert exactly one per key per sale.
        /// </summary>
        public readonly Dictionary<string, int> ClassBatchCreditsApplied = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Batched class CALLS in which the oracle was wrong about at least one key.</summary>
        public int ClassBatchIdentifyingReads;

        /// <summary>Class keys the oracle called present that were not.</summary>
        public int ClassBatchKeysUpsertedAfterIdentifyingRead;

        public int DeleteEmptyAccountVaultClasses(uint accountId)
        {
            DeletedEmptyClassCalls++;

            return Classes.RemoveAll(c => c.AccountId == accountId && c.Count <= 0);
        }

        public List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId)
        {
            GrantReads++;

            if (FailGrantRead)
                return null;

            return Grants.Where(g => g.OwnerAccountId == ownerAccountId).OrderBy(g => g.GranteeCharacterName).ToList();
        }

        /// <summary>Reads of the grantee-keyed grant list, counted apart from <see cref="GrantReads"/>.</summary>
        public int GranteeGrantReads;

        public List<AccountVaultGrant> GetAccountVaultGrantsForGrantee(uint granteeCharacterGuid)
        {
            GranteeGrantReads++;

            if (FailGrantRead)
                return null;

            return Grants.Where(g => g.GranteeCharacterGuid == granteeCharacterGuid).OrderBy(g => g.OwnerAccountId).ToList();
        }

        public bool UpsertAccountVaultGrant(AccountVaultGrant row)
        {
            UpsertGrantCalls++;

            var existing = Grants.FirstOrDefault(g =>
                g.OwnerAccountId == row.OwnerAccountId && g.GranteeCharacterGuid == row.GranteeCharacterGuid);

            if (existing == null)
            {
                row.Id = nextRowId++;
                Grants.Add(row);
            }
            else
            {
                existing.CanWithdraw = row.CanWithdraw;
                existing.GranteeCharacterName = row.GranteeCharacterName;
                existing.GrantedAt = row.GrantedAt;

                // Mirrors the real DAO: a null granter leaves the stored one alone.
                if (row.GrantedByCharacterGuid != null)
                {
                    existing.GrantedByCharacterGuid = row.GrantedByCharacterGuid;
                    existing.GrantedByCharacterName = row.GrantedByCharacterName;
                }
            }

            return true;
        }

        public int DeleteAccountVaultGrant(uint ownerAccountId, uint granteeCharacterGuid)
        {
            return Grants.RemoveAll(g => g.OwnerAccountId == ownerAccountId && g.GranteeCharacterGuid == granteeCharacterGuid);
        }

        public bool AddAccountVaultLog(AccountVaultLog row)
        {
            AddLogCalls++;

            if (ThrowFromAddLogCount > 0)
            {
                ThrowFromAddLogCount--;
                throw new InvalidOperationException("fake shard failure writing an audit row");
            }

            if (FailAddLog)
                return false;

            row.Id = nextRowId++;
            Logs.Add(row);
            return true;
        }

        /// <summary>
        /// Mirrors the real DAO's batch: ONE call, N rows appended in order, ids assigned in that same
        /// order - which is what GetAccountVaultLog's OrderByDescending(Id) reads as "newest first".
        /// </summary>
        public bool AddAccountVaultLogBatch(List<AccountVaultLog> rows)
        {
            AddLogBatchCalls++;

            if (ThrowFromAddLogCount > 0)
            {
                ThrowFromAddLogCount--;
                throw new InvalidOperationException("fake shard failure writing an audit batch");
            }

            if (FailAddLog)
                return false;

            if (rows == null)
                return true;

            foreach (var row in rows)
            {
                if (row == null)
                    continue;

                row.Id = nextRowId++;
                Logs.Add(row);
                AddLogRowsWritten++;
            }

            return true;
        }

        public List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit)
        {
            if (FailLogRead)
                return null;

            return Logs.Where(l => l.OwnerAccountId == ownerAccountId)
                .OrderByDescending(l => l.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        /// <summary>
        /// Saved mule forms, one per account. A dictionary rather than a list because the real table
        /// is keyed by account_Id and the write is an upsert; a list would let a test pass while the
        /// production upsert was silently appending duplicates.
        /// </summary>
        public readonly Dictionary<uint, AccountMuleForm> MuleForms = new Dictionary<uint, AccountMuleForm>();

        /// <summary>
        /// Makes the form read report the FAILURE answer, (false, null). Distinct from simply having
        /// no row, which is the success answer (true, null) - the store must cache one and refuse to
        /// cache the other.
        /// </summary>
        public bool FailMuleFormRead;

        /// <summary>Makes the form upsert report false with the stored row left untouched.</summary>
        public bool FailMuleFormUpsert;

        /// <summary>Form reads, so a test can prove the store caches rather than re-reading.</summary>
        public int MuleFormReads;

        /// <summary>Form writes, so a test can see a write that the stored row cannot reveal.</summary>
        public int UpsertMuleFormCalls;

        public (bool Ok, AccountMuleForm Row) GetAccountMuleForm(uint accountId)
        {
            MuleFormReads++;

            if (FailMuleFormRead)
                return (false, null);

            return (true, MuleForms.TryGetValue(accountId, out var row) ? row : null);
        }

        public bool UpsertAccountMuleForm(AccountMuleForm row)
        {
            UpsertMuleFormCalls++;

            if (FailMuleFormUpsert)
                return false;

            MuleForms[row.AccountId] = row;
            return true;
        }

        // ---- account_vault_barrel ----

        /// <summary>
        /// Every barrel row ever written here, in insertion order. The real table is never deleted
        /// from - a purge stamps purged_At and keeps the row - so this list models it exactly by never
        /// removing anything either.
        /// </summary>
        public readonly List<AccountVaultBarrel> Barrels = new List<AccountVaultBarrel>();

        /// <summary>Makes the two list reads report the FAILURE answer, null, rather than an empty list.</summary>
        public bool FailBarrelRead;

        /// <summary>Makes <see cref="AddAccountVaultBarrel"/> report false with nothing recorded.</summary>
        public bool FailAddBarrel;

        /// <summary>Makes <see cref="UpdateAccountVaultBarrel"/> report false with the row left untouched.</summary>
        public bool FailUpdateBarrel;

        public List<AccountVaultBarrel> GetAccountVaultBarrels(uint accountId, int limit)
        {
            if (FailBarrelRead)
                return null;

            return Barrels.Where(b => b.AccountId == accountId)
                .OrderByDescending(b => b.BarreledAt)
                .ThenByDescending(b => b.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        /// <summary>
        /// Returns the LIVE row object, not a copy, which is what makes an in-place stamp visible to a
        /// test the way the real DAO's re-read of the table is. <see cref="FailBarrelRead"/> is
        /// honoured here too, because the restore path refuses on null and that arm needs to be
        /// reachable.
        /// </summary>
        public AccountVaultBarrel GetAccountVaultBarrel(uint id)
        {
            if (FailBarrelRead)
                return null;

            return Barrels.FirstOrDefault(b => b.Id == id);
        }

        public List<AccountVaultBarrel> GetExpiredAccountVaultBarrels(DateTime cutoffUtc, int limit)
        {
            if (FailBarrelRead)
                return null;

            return Barrels.Where(b => b.RestoredAt == null && b.PurgedAt == null && b.BarreledAt < cutoffUtc)
                .OrderBy(b => b.BarreledAt)
                .ThenBy(b => b.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public bool AddAccountVaultBarrel(AccountVaultBarrel row)
        {
            if (FailAddBarrel || row == null)
                return false;

            // The real column carries a CURRENT_TIMESTAMP default and the DAO stamps UtcNow over it
            // when the caller left it at the CLR default; model that, or a row seeded without a time
            // would sort as DateTime.MinValue and always look expired.
            if (row.BarreledAt == default)
                row.BarreledAt = DateTime.UtcNow;

            row.Id = nextRowId++;
            Barrels.Add(row);
            return true;
        }

        public bool UpdateAccountVaultBarrel(AccountVaultBarrel row)
        {
            if (FailUpdateBarrel || row == null)
                return false;

            var existing = Barrels.FirstOrDefault(b => b.Id == row.Id);

            if (existing == null)
                return false;

            // Only the two terminal stamps, exactly as the DAO does: everything else on the row is a
            // snapshot of the barreling and must not be rewritten by a later event.
            existing.RestoredAt = row.RestoredAt;
            existing.PurgedAt = row.PurgedAt;

            return true;
        }
    }

    /// <summary>
    /// In-memory stand-in for everything the store needs from the world-object layer.
    ///
    /// Objects are built from hand-made weenies with STATIC-range guids, so Destroy() and the guid
    /// manager are never reached, following the same discipline as ContainerStackTests.
    ///
    /// <see cref="MaxConcurrentCalls"/> is the serialization detector: every call in here brackets a
    /// short sleep with an increment and a decrement, so a store that stopped serializing its
    /// mutations would record a maximum above 1 and the concurrency tests would fail rather than
    /// merely observing that two tasks completed.
    /// </summary>
    internal class FakeVaultWorld : IAccountVaultWorldSource
    {
        private static int nextGuid = 0x7F200000;

        /// <summary>What the vault weenie claims. 300 by default, so the byte wrap (R7) is live.</summary>
        public int SeedVaultItemsCapacity = 300;

        public int ItemMaxStackSize = 100;

        /// <summary>What IsPristine answers. False keeps the item's biota, which is the vault path.</summary>
        public bool PristineResult;

        /// <summary>
        /// Guids <see cref="IsPristine"/> answers TRUE for whatever <see cref="PristineResult"/> says.
        /// Null disables it.
        ///
        /// The blanket flag cannot express a MIXED sale - pristine stackables collapsing to the stack
        /// ledger alongside classifiable bags collapsing to the class ledger, in one batch - and that is
        /// precisely the shape a batched deposit has to get right, because it is the one that puts two
        /// tables in one call.
        /// </summary>
        public HashSet<uint> PristineGuids;

        public readonly HashSet<uint> FailCreateWcids = new HashSet<uint>();

        /// <summary>
        /// How many further DestroyItem calls must THROW before the next one behaves. Set to 1 for
        /// "throw once, then succeed". This models a failure in the teardown half of a ledger return -
        /// the half that runs AFTER the ledger credit has already committed - which is the only way to
        /// reach the caller's retry with the units already restored.
        /// </summary>
        public int ThrowFromDestroyItemCount;

        public readonly List<Container> CreatedContainers = new List<Container>();
        public readonly List<WorldObject> Destroyed = new List<WorldObject>();
        public readonly List<WorldObject> Saved = new List<WorldObject>();

        public readonly Dictionary<uint, Container> Containers = new Dictionary<uint, Container>();

        public readonly Dictionary<string, (uint guid, string name, uint accountId)> Characters =
            new Dictionary<string, (uint, string, uint)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sleep inside every call, to widen the window a lost serialization would open.</summary>
        public int CallDelayMs;

        private int concurrentCalls;
        private int maxConcurrentCalls;

        public int MaxConcurrentCalls => Volatile.Read(ref maxConcurrentCalls);

        private IDisposable EnterCall()
        {
            var now = Interlocked.Increment(ref concurrentCalls);

            while (true)
            {
                var observed = Volatile.Read(ref maxConcurrentCalls);

                if (now <= observed || Interlocked.CompareExchange(ref maxConcurrentCalls, now, observed) == observed)
                    break;
            }

            if (CallDelayMs > 0)
                Thread.Sleep(CallDelayMs);

            return new CallScope(this);
        }

        private sealed class CallScope : IDisposable
        {
            private readonly FakeVaultWorld owner;

            public CallScope(FakeVaultWorld owner) => this.owner = owner;

            public void Dispose() => Interlocked.Decrement(ref owner.concurrentCalls);
        }

        public WorldObject CreateNewWorldObject(uint weenieClassId)
        {
            using (EnterCall())
            {
                if (FailCreateWcids.Contains(weenieClassId))
                    return null;

                if (weenieClassId == AccountVaultStore.VaultContainerWcid)
                {
                    var container = MakeContainer(SeedVaultItemsCapacity);
                    CreatedContainers.Add(container);
                    Containers[container.Guid.Full] = container;
                    return container;
                }

                return MakeStack(weenieClassId, 1, ItemMaxStackSize);
            }
        }

        /// <summary>
        /// Container guids whose load reports a READ FAILURE rather than an absence. The two are
        /// modelled separately here because the store treats them as opposites: an absent biota drops
        /// one index row, an unreadable one refuses the whole account.
        /// </summary>
        public readonly HashSet<uint> FailLoadContainerGuids = new HashSet<uint>();

        public VaultContainerLoad LoadContainer(uint containerGuid, out Container container)
        {
            using (EnterCall())
            {
                container = null;

                if (FailLoadContainerGuids.Contains(containerGuid))
                    return VaultContainerLoad.Failed;

                if (!Containers.TryGetValue(containerGuid, out container))
                    return VaultContainerLoad.Absent;

                return VaultContainerLoad.Loaded;
            }
        }

        /// <summary>
        /// Calls to <see cref="IsPristine"/>. The real one constructs a reference weenie, computes its
        /// object description and destroys it, so it is the most expensive thing on the deposit path
        /// and the number of times it runs per item is a correctness-shaped cost question, not a
        /// micro-optimisation: the sell pre-flight and the deposit each used to ask independently.
        /// Interlocked because the concurrency tests drive this fake from several threads.
        /// </summary>
        public int IsPristineCalls => Volatile.Read(ref isPristineCalls);

        private int isPristineCalls;

        /// <summary>Zeroes <see cref="IsPristineCalls"/>, so a test can count one phase at a time.</summary>
        public void ResetIsPristineCalls() => Interlocked.Exchange(ref isPristineCalls, 0);

        /// <summary>
        /// Runs at the top of <see cref="IsPristine"/>, and may throw. This is the PHASE A hook, and it
        /// is the counterpart of <see cref="BeforeDestroyItem"/> at the other end of a batched deposit:
        /// IsPristine is the first thing TryDepositBatch's per-item planning loop calls that a test can
        /// reach, so throwing from here on a chosen item is how a test drives "planning THIS item failed"
        /// without disturbing any other item of the batch.
        ///
        /// It existed nowhere before, which is why a phase A throw had no test at all: every hook on this
        /// fake fired at or after the credit, so the whole class of failures BEFORE anything is credited
        /// was unreachable.
        /// </summary>
        public Action<WorldObject> BeforeIsPristine;

        public bool IsPristine(WorldObject item)
        {
            using (EnterCall())
            {
                BeforeIsPristine?.Invoke(item);

                Interlocked.Increment(ref isPristineCalls);

                if (PristineGuids != null && item != null && PristineGuids.Contains(item.Guid.Full))
                    return true;

                return PristineResult;
            }
        }

        // ---- the counted item-class seam ----

        /// <summary>
        /// Calls to <see cref="TryDescribeClass"/>. Same reason <see cref="IsPristineCalls"/> exists:
        /// the real predicate builds a reference weenie, diffs every biota collection and takes the
        /// item's own lock, so how many times it runs PER DEPOSIT is a correctness-shaped cost question.
        /// A deposit path that ran it once per already-stored item would still pass every functional
        /// test while being O(N) in the size of the vault.
        /// </summary>
        public int DescribeClassCalls => Volatile.Read(ref describeClassCalls);

        private int describeClassCalls;

        /// <summary>Calls to <see cref="MaterializeClass"/>. The withdraw path's per-item cost, and the deposit path's self-check.</summary>
        public int MaterializeClassCalls => Volatile.Read(ref materializeClassCalls);

        private int materializeClassCalls;

        /// <summary>Zeroes both class counters, so a test can count one phase at a time.</summary>
        public void ResetClassCalls()
        {
            Interlocked.Exchange(ref describeClassCalls, 0);
            Interlocked.Exchange(ref materializeClassCalls, 0);
        }

        /// <summary>
        /// When false, <see cref="TryDescribeClass"/> refuses everything, which is the "nothing is
        /// classifiable" world every pre-existing test runs in.
        /// </summary>
        public bool ClassifyResult;

        /// <summary>
        /// Guids the predicate refuses even when <see cref="ClassifyResult"/> is on, so a test can seed
        /// a vault holding a mix of foldable and unfoldable items.
        /// </summary>
        public readonly HashSet<uint> UnclassifiableGuids = new HashSet<uint>();

        /// <summary>
        /// Wcids whose <see cref="MaterializeClass"/> returns null, for the "the materializer could not
        /// build it" paths on both the deposit self-check and the withdraw.
        /// </summary>
        public readonly HashSet<uint> FailMaterializeWcids = new HashSet<uint>();

        /// <summary>
        /// When set, every object <see cref="MaterializeClass"/> builds carries this Structure instead
        /// of the payload's. It is how a test makes the deposit ROUND-TRIP SELF-CHECK fail: the rebuilt
        /// item then describes to a different class key than the one being deposited, which must leave
        /// the original item alive and stored as an ordinary biota.
        /// </summary>
        public int? MaterializeStructureOverride;

        /// <summary>
        /// Describes an item by reading the five whitelisted properties straight off it, which is what
        /// the real predicate's payload capture does once its refusals have passed. The refusals
        /// themselves are modelled by <see cref="ClassifyResult"/> and
        /// <see cref="UnclassifiableGuids"/> rather than reimplemented - a second copy of the real
        /// predicate's rules here would be a fake that agrees with itself and nothing else.
        /// </summary>
        public bool TryDescribeClass(WorldObject item, out VaultItemClassOverrides overrides)
        {
            using (EnterCall())
            {
                Interlocked.Increment(ref describeClassCalls);

                overrides = null;

                if (item == null || !ClassifyResult || UnclassifiableGuids.Contains(item.Guid.Full))
                    return false;

                overrides = DescribeOverrides(item);

                return true;
            }
        }

        /// <summary>The five whitelisted keys read off a live object, every one present, null meaning ABSENT.</summary>
        public static VaultItemClassOverrides DescribeOverrides(WorldObject item)
        {
            var ints = new List<KeyValuePair<PropertyInt, int?>>
            {
                new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, item.GetProperty(PropertyInt.Value)),
                new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, item.GetProperty(PropertyInt.Structure)),
                new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, item.GetProperty(PropertyInt.ItemWorkmanship)),
                new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, item.GetProperty(PropertyInt.NumItemsInMaterial)),
            };

            var strings = new List<KeyValuePair<PropertyString, string>>
            {
                new KeyValuePair<PropertyString, string>(PropertyString.Name, item.GetProperty(PropertyString.Name)),
            };

            return new VaultItemClassOverrides(ints, strings);
        }

        /// <summary>
        /// A countdown, independent of <see cref="MaterializeClassCalls"/>: the next this many calls
        /// succeed and EVERY call after them returns null. Null disables it.
        ///
        /// It exists because <see cref="FailMaterializeWcids"/> cannot express the case that matters
        /// most on the withdraw path - a build that fails PART WAY THROUGH, after the debit has already
        /// committed. That is the only state in which a refund is needed at all, and a blanket per-wcid
        /// switch fails the probe instead, which refuses before the row ever moves.
        ///
        /// IT IS A COUNTDOWN, NOT A CALL INDEX, and the two read the same at a glance. Setting it to N
        /// does not mean "call N fails" - it means calls 1..N succeed and call N+1 and every call after
        /// it return null. A test that wants the k-th call to fail sets it to k-1.
        ///
        /// AND A DEPOSIT CONSUMES A TICK. AccountVaultStore.ClassRoundTripHolds materializes one probe
        /// per class deposit, so a test that seeds rows by depositing and then sets this before the
        /// withdraw is counting from a clean slate, while one that sets it BEFORE the deposits has
        /// already spent part of the countdown on them - silently, because a deposit whose self-check
        /// cannot materialize does not fail loudly, it simply stores an ordinary biota instead and the
        /// class row the test was seeding never appears. Set it after the seeding, and work out the
        /// call sequence of the path under test rather than guessing a number.
        /// </summary>
        public int? MaterializeFailAfterCalls;

        /// <summary>
        /// Applied to every object <see cref="MaterializeClass"/> builds, so a test can make a display
        /// object look like something other than the salvage bag the fake always makes (e.g. give it a
        /// ValidLocations). Null leaves the bag untouched.
        /// </summary>
        public Func<WorldObject, WorldObject> MaterializeTransform;

        public WorldObject MaterializeClass(uint wcid, VaultItemClassOverrides overrides, int? pooledValue)
        {
            using (EnterCall())
            {
                Interlocked.Increment(ref materializeClassCalls);

                if (FailMaterializeWcids.Contains(wcid))
                    return null;

                if (MaterializeFailAfterCalls.HasValue)
                {
                    if (MaterializeFailAfterCalls.Value <= 0)
                        return null;

                    MaterializeFailAfterCalls = MaterializeFailAfterCalls.Value - 1;
                }

                var structure = MaterializeStructureOverride ?? overrides.GetInt(PropertyInt.Structure) ?? 0;

                var built = MakeSalvageBag(wcid, structure,
                                           overrides.GetInt(PropertyInt.ItemWorkmanship),
                                           overrides.GetInt(PropertyInt.NumItemsInMaterial),
                                           pooledValue ?? overrides.GetInt(PropertyInt.Value) ?? 0,
                                           overrides.GetString(PropertyString.Name));

                return MaterializeTransform != null ? MaterializeTransform(built) : built;
            }
        }

        /// <summary>When true every save reports FAILURE to its callback and records nothing.</summary>
        public bool FailSaveBiota;

        /// <summary>
        /// Per-guid countdown of saves that must report FAILURE before the next one succeeds. Set an
        /// entry to 1 for "fail this object's first save, then behave". Same shape as
        /// <see cref="ThrowFromDestroyItemCount"/>, keyed the way <see cref="FailLoadContainerGuids"/>
        /// is, because a retry test has to fail ONE object rather than the whole world source: the
        /// blanket <see cref="FailSaveBiota"/> switch also fails the vault container's own save, which
        /// aborts the deposit long before the path under test is reached.
        /// </summary>
        public readonly Dictionary<uint, int> FailSaveBiotaCounts = new Dictionary<uint, int>();

        private readonly Dictionary<uint, int> saveBiotaCalls = new Dictionary<uint, int>();

        /// <summary>
        /// How many times SaveBiota has been ATTEMPTED for this object, failures included.
        /// <see cref="Saved"/> counts only the successes, so it cannot distinguish "never retried"
        /// from "retried and failed again" - which is the whole question a retry test asks.
        /// </summary>
        public int SaveBiotaCalls(WorldObject worldObject)
        {
            if (worldObject == null)
                return 0;

            return saveBiotaCalls.TryGetValue(worldObject.Guid.Full, out var calls) ? calls : 0;
        }

        /// <summary>
        /// Review round, finding 1: how many further SaveBiota calls must THROW rather than report
        /// failure. Distinct from <see cref="FailSaveBiota"/> and
        /// <see cref="FailSaveBiotaCounts"/>, which model a save that fails politely - this models the
        /// real production throw the store's retry path can hit, SerializedShardDatabase's
        /// BlockingCollection.Add after CompleteAdding() at shutdown.
        /// </summary>
        public int ThrowFromSaveBiotaCount;

        /// <summary>
        /// Runs INSIDE SaveBiota, before its callback. This is the observation seam for the store's
        /// mid-mutation window, and it exists because that window is otherwise unreachable from a
        /// single-threaded test.
        ///
        /// Every vault mutation applies its change to the vault container under stateLock, RELEASES
        /// that lock, calls SaveBiota - a real database round-trip in production - and only then runs
        /// Interlocked.Increment(ref version). A reader taking stateLock inside that gap sees mutated
        /// vaults carrying an unchanged version. Hooking here puts a test on exactly that thread at
        /// exactly that moment, deterministically and with no sleeps.
        /// </summary>
        public Action<WorldObject> DuringSaveBiota;

        /// <summary>
        /// Completes the save inline and answers the callback before returning. The real world source
        /// answers it on the database worker thread instead; the store's SaveAndWait is written to
        /// handle both, and this covers the inline case.
        /// </summary>
        public void SaveBiota(WorldObject worldObject, Action<bool> callback = null)
        {
            using (EnterCall())
            {
                DuringSaveBiota?.Invoke(worldObject);

                var ok = !FailSaveBiota;

                if (worldObject != null)
                {
                    var guid = worldObject.Guid.Full;

                    saveBiotaCalls.TryGetValue(guid, out var calls);
                    saveBiotaCalls[guid] = calls + 1;

                    // AFTER the attempt is counted, deliberately: a test asserting that the throwing
                    // call was actually REACHED needs the count to move, or it cannot tell a throw that
                    // happened from a call that never ran.
                    if (ThrowFromSaveBiotaCount > 0)
                    {
                        ThrowFromSaveBiotaCount--;
                        throw new InvalidOperationException($"fake shard failure saving 0x{guid:X8}");
                    }

                    if (FailSaveBiotaCounts.TryGetValue(guid, out var failuresLeft) && failuresLeft > 0)
                    {
                        FailSaveBiotaCounts[guid] = failuresLeft - 1;
                        ok = false;
                    }
                }

                if (ok)
                    Saved.Add(worldObject);

                callback?.Invoke(ok);
            }
        }

        /// <summary>
        /// Runs at the top of <see cref="DestroyItem"/>, and may throw. The ledger and class deposit
        /// arms destroy their carrier only AFTER the credit has committed, so throwing from here on a
        /// chosen item is how a test reaches "the deposit committed and then threw" for that one item
        /// and no other - <see cref="ThrowFromDestroyItemCount"/> cannot skip the items before it.
        /// </summary>
        public Action<WorldObject> BeforeDestroyItem;

        public void DestroyItem(WorldObject item)
        {
            using (EnterCall())
            {
                BeforeDestroyItem?.Invoke(item);

                if (ThrowFromDestroyItemCount > 0)
                {
                    ThrowFromDestroyItemCount--;
                    throw new InvalidOperationException($"fake teardown failure destroying 0x{item.Guid.Full:X8}");
                }

                Destroyed.Add(item);
            }
        }

        public bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId)
        {
            characterGuid = 0;
            canonicalName = null;
            accountId = 0;

            if (characterName == null || !Characters.TryGetValue(characterName, out var found))
                return false;

            characterGuid = found.guid;
            canonicalName = found.name;
            accountId = found.accountId;
            return true;
        }

        // ---- object construction helpers, also used directly by the tests to seed vaults ----

        public static uint NextGuid()
        {
            return (uint)Interlocked.Increment(ref nextGuid);
        }

        public static Container MakeContainer(int itemsCapacity, int containersCapacity = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = AccountVaultStore.VaultContainerWcid,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Container },
                    { PropertyInt.ItemsCapacity, itemsCapacity },
                    { PropertyInt.ContainersCapacity, containersCapacity },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Backpack" },
                },
            };

            return new Container(weenie, new ObjectGuid(NextGuid()));
        }

        public static Stackable MakeStack(uint wcid, int stackSize, int maxStackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    // A real weenie always declares an ItemType, and PersonalVendor.NeedsDisplayProxy
                    // reads it: an item with no ItemType at all is treated as unfilable by the client's
                    // vendor panel and is presented through a display proxy. A fake with the property
                    // missing therefore modelled the rarest case rather than the ordinary one.
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.StackSize, stackSize },
                    { PropertyInt.MaxStackSize, maxStackSize },
                    { PropertyInt.StackUnitEncumbrance, 1 },
                    { PropertyInt.StackUnitValue, 1 },
                    { PropertyInt.EncumbranceVal, stackSize },
                    { PropertyInt.Value, stackSize },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, $"Test Item {wcid}" },
                },
            };

            return new Stackable(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// A salvage-bag-shaped object, for the read-time grouping rule.
        ///
        /// Two halves matter and neither is decoration. The weenie carries ItemType.TinkeringMaterial
        /// and MaxStackSize 1, which is what real salvage weenies 20980-21075 carry (verified against
        /// ace_world 2026-08-31: WeenieType 44 CraftTool, ItemType 0x40000000, MaxStackSize 1,
        /// MaxStructure 100). And the five per-instance properties a real bag gets from
        /// Player_Crafting.TryAddSalvage (Player_Crafting.cs:249-286) - Structure, ItemWorkmanship,
        /// NumItemsInMaterial, Value and the derived Name - are stamped ON THE INSTANCE and are absent
        /// from the weenie, exactly as they are in production. That absence is why nothing
        /// salvage-shaped can ever be pristine, and therefore why a bag is a STORED BIOTA rather than a
        /// ledger row - the only shape grouping applies to.
        /// </summary>
        /// <remarks>
        /// itemWorkmanship and numItemsInMaterial are NULLABLE so a test can seed a bag carrying
        /// neither - the shape the bucket key's "no workmanship at all" sentinel exists for, and the
        /// one a corrupted item must never be bucketed with.
        /// </remarks>
        public static Stackable MakeSalvageBag(uint wcid, int structure, int? itemWorkmanship, int? numItemsInMaterial, int value, string name = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, 1 },
                    { PropertyInt.StackUnitEncumbrance, 100 },
                    { PropertyInt.StackUnitValue, 0 },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.MaxStructure, 100 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Salvage" },
                },
            };

            var bag = new Stackable(weenie, new ObjectGuid(NextGuid()));

            bag.Structure = (ushort)structure;
            bag.ItemWorkmanship = itemWorkmanship;
            bag.NumItemsInMaterial = numItemsInMaterial;
            bag.Value = value;
            bag.Name = name ?? $"Salvage ({structure})";

            return bag;
        }

        /// <summary>
        /// Container.InventoryLoaded has a private setter and is normally flipped by the async
        /// inventory load, which needs a live shard. Reflection is the only way to reach the R4 state
        /// from a unit test, and reaching it through the real property is the point: a test that
        /// modelled "unloaded" with its own flag would not be testing what the store reads.
        /// </summary>
        public static void SetInventoryLoaded(Container container, bool value)
        {
            var property = typeof(Container).GetProperty(nameof(Container.InventoryLoaded), BindingFlags.Public | BindingFlags.Instance);

            property.GetSetMethod(nonPublic: true).Invoke(container, new object[] { value });
        }
    }
}

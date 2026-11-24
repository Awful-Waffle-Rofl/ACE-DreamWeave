using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Retention for the barrel (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6.3): the sweep
    /// that turns a soft delete into a hard one.
    ///
    /// A barreled item is recoverable by an administrator for vault_barrel_retention_days and then it
    /// is gone. THE AUDIT ROW IS NOT: purged_At is stamped and the account_vault_barrel row stays, so
    /// "what did this player throw away" remains answerable after the item itself no longer exists.
    /// That asymmetry is the whole point of the table and is why nothing here ever deletes a row.
    ///
    /// Threading is modelled on MarketRejectionLog's pruning pass: one background thread, an hourly
    /// pass, tunables re-read on the worker thread rather than cached at construction, and a bounded
    /// batch so one sweep cannot pin the thread. <see cref="RunOnce"/> does all the work and is what
    /// the tests call; the loop only schedules it.
    ///
    /// NOTHING HERE READS PropertyManager OFF THE WORKER THREAD. An uncached PropertyManager read
    /// falls through to DatabaseManager.ShardConfig and opens a ShardDbContext, which throws outright
    /// in the unit-test process - the same trap AccountVaultStoreTests' Setup and MarketRejectionLog
    /// both document. <see cref="RetentionDays"/> is a plain field the worker refreshes.
    ///
    /// HOW MANY LOADS THE REAPER CAN HAVE UNSETTLED. While loads settle within the per-account wait
    /// (<see cref="StillLoadingRetryAttempts"/> x <see cref="StillLoadingRetryDelay"/>, 5 s), the reaper
    /// starts the next account's load only after the previous one has settled, so at most one of its
    /// loads is ever unsettled. A load that does not settle in that wait is abandoned (one WARN) and the
    /// pass moves on, so when loads are stuck the bound is per PASS: at most
    /// <see cref="MaxColdLoadsPerPass"/> loads started, all of which may be left unsettled. It is NOT a
    /// bound across passes. A stuck store that is still resident is not loaded again (it reads
    /// Unsettled, not Cold), but the next pass can start up to the cap on OTHER cold accounts, and once
    /// the idle sweep retires a stuck store (AccountVaultStore.TryEvict does not check load state) a
    /// later pass loads that account again. So while loads stay stuck, up to
    /// <see cref="MaxColdLoadsPerPass"/> more can be left unsettled each hourly pass.
    /// </summary>
    public sealed class AccountVaultBarrelReaper
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string RetentionKey = "vault_barrel_retention_days";

        /// <summary>Clamped exactly as market_reject_retention_days is, and to the same bounds.</summary>
        public const int MinRetentionDays = 1;

        public const int MaxRetentionDays = 3650;

        /// <summary>
        /// Rows examined per pass. Bounded so a shard that has accumulated a large backlog drains over
        /// several passes instead of holding this thread for an unbounded stretch; the query is
        /// oldest-first, so a bounded batch always takes the rows that have waited longest.
        /// </summary>
        public const int MaxRowsPerPass = 200;

        private static readonly TimeSpan PassInterval = TimeSpan.FromHours(1);

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

        private readonly IAccountVaultBackend backend;

        private readonly Func<uint, AccountVaultStore> storeFor;

        /// <summary>
        /// Defaults to the tunable's own default so a process that never starts the worker - every
        /// unit test - still has a sane window. The worker overwrites it from the tunable.
        /// </summary>
        private volatile int retentionDays = 30;

        public int RetentionDays
        {
            get => retentionDays;
            internal set => retentionDays = value;
        }

        /// <summary>
        /// <paramref name="storeFor"/> resolves the account's live <see cref="AccountVaultStore"/>,
        /// defaulting to <see cref="AccountVaultManager.GetStore"/>.
        ///
        /// The purge deliberately goes THROUGH the store rather than destroying a biota this class
        /// loaded itself, and that is not indirection for its own sake. The store owns the barrel
        /// container and its load (the reaper asks it to load a cold store on this thread through
        /// AccountVaultStore.TryLoadForRetention, and the purge refuses until the async inventory load has
        /// finished), and serializes every mutation on its own queue - so routing through it is what stops a purge racing a restore of the same
        /// row, and stops a second live WorldObject existing on a guid the store is already holding.
        /// Injecting the lookup is what lets a test drive a store built over the fakes.
        ///
        /// <paramref name="waitBetweenRetries"/> is how <see cref="RunOnce"/> waits between its in-pass
        /// retries of a row whose barrel is still loading, defaulting to Thread.Sleep on this thread. A
        /// test passes its own to stand in for the async inventory load completing, or not.
        /// </summary>
        public AccountVaultBarrelReaper(IAccountVaultBackend backend, Func<uint, AccountVaultStore> storeFor = null, Action<TimeSpan> waitBetweenRetries = null)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.storeFor = storeFor ?? AccountVaultManager.GetStore;
            this.waitBetweenRetries = waitBetweenRetries ?? Thread.Sleep;
        }

        private readonly Action<TimeSpan> waitBetweenRetries;

        /// <summary>
        /// Most distinct COLD accounts one pass will load. A cold load reads the account's index and every
        /// container biota on this thread, queues one inventory read per container on the single shard
        /// worker (ahead of player saves) and then one inventory sort per container on the WorldManager
        /// action queue, which runs on the world thread. Stores that are already loaded do not count.
        /// Rows past the cap are untouched, stay expired, and are taken by a later pass.
        /// </summary>
        internal const int MaxColdLoadsPerPass = 25;

        /// <summary>How many times one account waits for its load to settle before the pass moves on.</summary>
        internal const int StillLoadingRetryAttempts = 10;

        /// <summary>
        /// The wait between those checks. With <see cref="StillLoadingRetryAttempts"/> this bounds the
        /// time one account can hold this thread at 5 s. The load it waits on is one shard-queue read per
        /// container (Container.cs GetInventoryInParallel) plus one WorldManager action-queue sort each.
        /// </summary>
        internal static readonly TimeSpan StillLoadingRetryDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Consecutive passes an account may end with its rows still waiting on a load before one WARN
        /// names it. Logged once, when the count REACHES this, not on every pass after.
        /// </summary>
        internal const int DeferredEscalationPasses = 3;

        /// <summary>
        /// The DAO's own ceiling on one expired read, which its ClampBarrelLimit enforces. Referenced, not
        /// restated, because the reaper sizes its request against it.
        /// </summary>
        internal const int MaxRowsPerRead = ShardDatabase.MaxAccountVaultBarrelRows;

        /// <summary>
        /// Most row ids <see cref="permanentlyRefused"/> holds. With <see cref="MaxRowsPerPass"/> on top
        /// this keeps every request within <see cref="MaxRowsPerRead"/>.
        /// </summary>
        internal const int MaxRefusedRows = MaxRowsPerRead - MaxRowsPerPass;

        /// <summary>
        /// Every this many passes the refused set is forgotten and those rows are tried again - about a
        /// day at the hourly cadence - so an operator's repair is picked up without a restart.
        /// </summary>
        internal const int RefusedResetPasses = 24;

        private volatile bool stopRequested;

        /// <summary>
        /// Makes any <see cref="RunOnce"/> in progress stop before its next account, its next row and its
        /// next wait. A purge already inside the store's queue finishes; no new one starts.
        /// </summary>
        internal void RequestStop()
        {
            stopRequested = true;
        }

        // ---- per-instance state. Only ever touched by the one thread running RunOnce; the lock is there
        // so a test reading it from another thread sees a consistent value. ----

        private sealed class AccountHistory
        {
            /// <summary>Passes in a row that ended with this account's rows waiting on a load.</summary>
            public int ConsecutiveDeferred;

            /// <summary>The last pass that visited this account and did not purge all of its rows.</summary>
            public long LastTroubledPass;

            /// <summary>True once the "load did not settle" WARN has been written; cleared when it settles.</summary>
            public bool UnsettledWarned;
        }

        private readonly object historyLock = new object();

        private readonly Dictionary<uint, AccountHistory> history = new Dictionary<uint, AccountHistory>();

        /// <summary>
        /// Rows refused for a reason that does not heal by itself (the barrel does not hold the item, or the
        /// loaded store has no barrel). Filtered out of each pass's batch so they cannot sit at the head of the
        /// oldest-first query and hide every row behind them. Capped at <see cref="MaxRefusedRows"/> and
        /// cleared every <see cref="RefusedResetPasses"/> passes.
        /// </summary>
        private readonly HashSet<uint> permanentlyRefused = new HashSet<uint>();

        private long passCounter;

        internal int ConsecutiveDeferredPassesForTest(uint accountId)
        {
            lock (historyLock)
                return history.TryGetValue(accountId, out var h) ? h.ConsecutiveDeferred : 0;
        }

        internal int PermanentlyRefusedCountForTest
        {
            get { lock (historyLock) return permanentlyRefused.Count; }
        }

        /// <summary>
        /// One retention pass. Returns how many rows were purged - destroyed and stamped - which is
        /// what the tests assert on.
        ///
        /// A row is purged only when there is provably nothing left to destroy: the item was found in
        /// the barrel and destroyed, or the row is a ledger barreling that never had a biota. A row
        /// this cannot settle is left OPEN and retried next pass, because purged_At is an audit
        /// statement that retention destroyed something and must not be written over an item that is
        /// still somewhere.
        ///
        /// Order per row is destroy first, stamp second. A crash between them leaves a destroyed item
        /// on an open row, which reads as restorable and then refuses - visible and harmless. The
        /// reverse order would leave a row saying "destroyed" over an item still sitting in the
        /// barrel, which nothing would ever clean up.
        ///
        /// ONE ACCOUNT AT A TIME. Rows are grouped by account; for each account the store is loaded if it
        /// is cold (on this thread, <see cref="AccountVaultStore.TryLoadForRetention"/>), this thread then
        /// waits up to <see cref="StillLoadingRetryAttempts"/> x <see cref="StillLoadingRetryDelay"/> for
        /// that load to settle, the account's rows are purged, and only then is the next account touched.
        /// A load still unsettled after that budget is ABANDONED with one WARN naming the account (once,
        /// until it settles), and the pass moves on; that budget is the bound on how long one reaper load
        /// can hold back the others. A pass starts at most <see cref="MaxColdLoadsPerPass"/> cold loads.
        /// See the class remarks for what that does and does not bound once loads stop settling.
        ///
        /// WHY IT CONVERGES. A purged row is stamped and leaves the expired query for good. A row refused
        /// for a reason that does not heal by itself is remembered and filtered out of later batches, and
        /// each request asks for that many extra rows (up to the DAO's ceiling), so refused rows can never
        /// stop the purgeable rows behind them from being read. Accounts this pass did not reach (the cap)
        /// keep their rows, which stay the oldest and so come back first. An account whose rows did NOT
        /// all purge is moved behind every account with no such history, and among themselves those are
        /// taken least-recently-tried first, so an account that keeps failing cannot hold the front.
        /// </summary>
        public int RunOnce(DateTime nowUtc)
        {
            var days = retentionDays;

            if (days < MinRetentionDays)
                days = MinRetentionDays;
            else if (days > MaxRetentionDays)
                days = MaxRetentionDays;

            var cutoff = nowUtc.AddDays(-days);

            long pass;
            int request;

            lock (historyLock)
            {
                pass = ++passCounter;

                if (pass % RefusedResetPasses == 0 && permanentlyRefused.Count > 0)
                {
                    log.Info($"[VAULT] barrel retention: retrying {permanentlyRefused.Count} previously refused row(s) this pass (periodic reset every {RefusedResetPasses} passes).");
                    permanentlyRefused.Clear();
                }

                request = Math.Min(MaxRowsPerPass + permanentlyRefused.Count, MaxRowsPerRead);
            }

            var read = backend.GetExpiredAccountVaultBarrels(cutoff, request);

            if (read == null)
            {
                // NULL is a failed read, never an empty table. Purging nothing is the right answer to a
                // read that failed, and the next pass tries again.
                log.Error($"[VAULT] the barrel retention sweep could not read account_vault_barrel; nothing was purged this pass (cutoff {cutoff:o}).");
                return 0;
            }

            List<(uint AccountId, List<AccountVaultBarrel> Rows)> ordered;

            lock (historyLock)
            {
                var rows = read.Where(r => !permanentlyRefused.Contains(r.Id)).Take(MaxRowsPerPass).ToList();

                // First appearance in an oldest-first list is each account's oldest row, so this is
                // oldest-account-first. Fresh accounts lead; accounts with trouble follow, least-recently
                // tried first (a fresh account's key is 0, every troubled one's is the pass number it last
                // failed on). OrderBy is stable, so ties keep the oldest-first order.
                ordered = rows
                    .GroupBy(r => r.AccountId)
                    .Select(g => (AccountId: g.Key, Rows: g.ToList()))
                    .OrderBy(g => history.TryGetValue(g.AccountId, out var h) ? h.LastTroubledPass : 0)
                    .ToList();
            }

            var purged = 0;
            var coldLoads = 0;
            var notReached = 0;

            var deferredRows = 0;
            var deferredAccounts = new List<uint>();

            foreach (var (accountId, accountRows) in ordered)
            {
                if (stopRequested)
                    break;

                var result = RunAccount(accountId, accountRows, ref coldLoads);

                if (result.NotReached)
                {
                    notReached++;
                    continue;
                }

                purged += result.Purged;

                if (result.Deferred > 0)
                {
                    deferredRows += result.Deferred;
                    deferredAccounts.Add(accountId);
                }

                RecordAccount(accountId, pass, result);
            }

            if (deferredRows > 0)
            {
                // INFO, not WARN: an inventory load can outlast the wait behind a busy shard queue. The
                // rows are untouched and come back next pass; RecordAccount escalates a repeat.
                log.Info($"[VAULT] barrel retention deferred {deferredRows} row(s) to the next pass: the barrel inventory was still loading after {StillLoadingRetryAttempts} checks (account(s) {string.Join(", ", deferredAccounts)}).");
            }

            if (notReached > 0)
                log.Info($"[VAULT] barrel retention left {notReached} account(s) for a later pass ({coldLoads} cold load(s) this pass, cap {MaxColdLoadsPerPass}).");

            if (purged > 0)
                log.Info($"[VAULT] barrel retention purged {purged} row(s) barreled before {cutoff:o} ({days} day window).");

            return purged;
        }

        private struct AccountResult
        {
            public bool NotReached;
            public int Purged;
            public int Deferred;
            public int Rows;

            /// <summary>True when the store's own load was still unsettled after this account's wait.</summary>
            public bool LoadUnsettled;

            /// <summary>True when the store was seen fully settled this pass.</summary>
            public bool LoadSettled;
        }

        /// <summary>
        /// One account: load if cold (within the cap), wait for the load, purge its rows. Guarded on its
        /// own so a single account's failure costs that account and not the rest of the batch.
        /// </summary>
        private AccountResult RunAccount(uint accountId, List<AccountVaultBarrel> accountRows, ref int coldLoads)
        {
            var result = new AccountResult { Rows = accountRows.Count };

            try
            {
                var store = storeFor(accountId);

                if (store == null)
                {
                    log.Error($"[VAULT] barrel retention could not resolve a store for account {accountId} ({accountRows.Count} row(s), first {accountRows[0].Id}); leaving the rows open.");
                    return result;
                }

                var ownLoad = false;

                if (store.GetRetentionLoadState(out _) == AccountVaultStore.RetentionLoadState.Cold)
                {
                    if (coldLoads >= MaxColdLoadsPerPass)
                    {
                        result.NotReached = true;
                        return result;
                    }

                    coldLoads++;

                    // On THIS thread, outside the store's queue - see TryLoadForRetention for why.
                    if (!store.TryLoadForRetention())
                    {
                        // A failed read, already logged at ERROR by the store. Nothing is in flight; the
                        // rows stay open for the next pass.
                        log.Warn($"[VAULT] barrel retention could not load the store for account {accountId}; leaving {accountRows.Count} row(s) open.");
                        return result;
                    }

                    ownLoad = true;
                }

                // The answer is not needed here: every row still goes through the store, which refuses a
                // barrel that is not ready with the distinct still-loading reason and a missing one with
                // the named barrel-null WARN.
                WaitForLoad(store, ownLoad);

                var state = store.GetRetentionLoadState(out _);

                result.LoadSettled = state == AccountVaultStore.RetentionLoadState.Settled;
                result.LoadUnsettled = ownLoad && state == AccountVaultStore.RetentionLoadState.Unsettled;

                foreach (var row in accountRows)
                {
                    if (stopRequested)
                        break;

                    var outcome = TryPurgeRow(store, row);

                    if (outcome == RowOutcome.Purged)
                    {
                        result.Purged++;
                    }
                    else if (outcome == RowOutcome.StillLoading)
                    {
                        result.Deferred++;
                    }
                    else if (outcome == RowOutcome.PermanentlyRefused)
                    {
                        lock (historyLock)
                        {
                            if (permanentlyRefused.Count < MaxRefusedRows)
                                permanentlyRefused.Add(row.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] barrel retention failed on account {accountId}: {ex.GetFullMessage()}");
            }

            return result;
        }

        /// <summary>
        /// Waits, bounded, for <paramref name="store"/>'s load. For a load this reaper started it waits
        /// for the WHOLE store to settle, because every container's read is queued on the shard worker
        /// and the next account must not pile more on top; for a store someone else loaded it waits only
        /// for the barrel. Returns whether the barrel is ready to purge against.
        /// </summary>
        private bool WaitForLoad(AccountVaultStore store, bool ownLoad)
        {
            for (var attempt = 0; ; attempt++)
            {
                var state = store.GetRetentionLoadState(out var barrelReady);

                if (state == AccountVaultStore.RetentionLoadState.Settled || (!ownLoad && barrelReady))
                    return barrelReady;

                if (attempt >= StillLoadingRetryAttempts || stopRequested)
                    return barrelReady;

                waitBetweenRetries(StillLoadingRetryDelay);
            }
        }

        /// <summary>
        /// Updates one account's history after a pass visited it, and writes the two deduplicated WARNs:
        /// the reaper's own load still unsettled after its wait (once, until the store settles), and the
        /// barrel deferred <see cref="DeferredEscalationPasses"/> passes running (once, when it reaches it).
        /// </summary>
        private void RecordAccount(uint accountId, long pass, AccountResult result)
        {
            var warnUnsettled = false;
            var escalate = false;
            var count = 0;

            lock (historyLock)
            {
                history.TryGetValue(accountId, out var h);

                if (result.Purged == result.Rows && !result.LoadUnsettled)
                {
                    history.Remove(accountId);
                    return;
                }

                if (h == null)
                {
                    h = new AccountHistory();
                    history[accountId] = h;
                }

                if (result.Purged != result.Rows)
                    h.LastTroubledPass = pass;

                if (result.LoadSettled)
                    h.UnsettledWarned = false;

                if (result.LoadUnsettled && !h.UnsettledWarned)
                {
                    h.UnsettledWarned = true;
                    warnUnsettled = true;
                }

                if (result.Deferred > 0)
                {
                    h.ConsecutiveDeferred++;
                    count = h.ConsecutiveDeferred;
                    escalate = count == DeferredEscalationPasses;
                }
                else
                {
                    h.ConsecutiveDeferred = 0;
                }
            }

            if (warnUnsettled)
                log.Warn($"[VAULT] barrel retention: account {accountId}'s store load did not settle within {StillLoadingRetryAttempts} x {StillLoadingRetryDelay.TotalMilliseconds:N0} ms (a vault, barrel or escrow container's inventory never arrived). Moving on to the next account; logged once until it settles.");

            if (escalate)
                log.Warn($"[VAULT] barrel retention: account {accountId}'s barrel inventory has not finished loading in {count} consecutive passes. Its expired rows cannot purge until it does; look for a failed GetInventoryInParallel for that account's barrel container on the shard worker.");
        }

        private enum RowOutcome
        {
            Purged,
            LeftOpen,
            StillLoading,

            /// <summary>Refused for a reason that does not heal by itself; see <see cref="permanentlyRefused"/>.</summary>
            PermanentlyRefused,
        }

        /// <summary>
        /// One row, through <paramref name="store"/>'s mutation queue. The destroy AND the purged_At
        /// stamp both run inside the queued closure: the row was re-read there, and a restore of the
        /// same row also runs on that queue, so nothing can change the row between the check and the
        /// stamp.
        ///
        /// ACCEPTED TRADE-OFF. The re-read (AccountVaultStore.IsRowStillPurgeable) and the stamp
        /// (UpdateAccountVaultBarrel) are synchronous database round trips INSIDE the closure, because
        /// that is what makes check, destroy and stamp atomic against a restore. The queue is a combining
        /// one, so a world-thread caller draining the same account's store at that moment can end up
        /// running them; the cost is two single-row statements per row, and only for an account whose
        /// store a player is using at that instant.
        /// </summary>
        private RowOutcome TryPurgeRow(AccountVaultStore store, AccountVaultBarrel row)
        {
            try
            {
                var destroyed = false;
                bool? stamped = null;
                string failReason = null;

                // Enqueue returns false when the store was retired by the idle sweep between the
                // lookup and here. Nothing ran, so the row is simply left for the next pass.
                var ran = store.Enqueue(() =>
                {
                    // The shared destroy-and-stamp primitive; a full barrel's early eviction uses it too.
                    stamped = store.TryPurgeAndStamp(row, AccountVaultStore.PurgeOp, out failReason, out destroyed);
                }, out var thrown);

                if (!ran)
                    return RowOutcome.LeftOpen;

                if (destroyed && stamped != true)
                {
                    // The item is already gone and cannot come back. The row still reading open means
                    // a later /vaultrestore will try it, find nothing in the barrel and refuse, and
                    // the next sweep will try to purge it again and do the same - noisy, but never
                    // destructive. AccountVaultStore.TryPurgeAndStamp has already logged this at ERROR
                    // (it also catches a stamp that throws), so it is not logged a second time here.
                    return RowOutcome.LeftOpen;
                }

                if (thrown != null)
                {
                    log.Error($"[VAULT] barrel retention threw purging row {row.Id} for account {row.AccountId}: {thrown.GetFullMessage()}. The row is left open.");
                    return RowOutcome.LeftOpen;
                }

                if (!destroyed)
                {
                    if (failReason == AccountVaultStore.BarrelStillLoadingMessage)
                    {
                        // RunAccount already waited for this barrel. DEBUG per row; RunOnce reports
                        // whatever is still outstanding once per pass, at INFO.
                        log.Debug($"[VAULT] barrel retention: row {row.Id} (account {row.AccountId}) waits on the barrel's inventory load.");
                        return RowOutcome.StillLoading;
                    }

                    log.Warn($"[VAULT] barrel retention left row {row.Id} (account {row.AccountId}, item 0x{row.ItemGuid ?? 0:X8}) open: {failReason}");

                    // Neither heals by itself: the item is not in a loaded barrel, or the loaded store has
                    // no barrel at all. Both were logged by the store at ERROR or WARN with the branch.
                    if (failReason == AccountVaultStore.BarrelRowItemMissingMessage || failReason == AccountVaultStore.BarrelUnavailableMessage)
                        return RowOutcome.PermanentlyRefused;

                    return RowOutcome.LeftOpen;
                }

                return RowOutcome.Purged;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] barrel retention failed on row {row?.Id} for account {row?.AccountId}: {ex.GetFullMessage()}");
                return RowOutcome.LeftOpen;
            }
        }

        // ---- the worker ----

        private static volatile bool running;

        private static Thread worker;

        private static AccountVaultBarrelReaper instance;

        /// <summary>
        /// Starts the hourly sweep. Called once from Program's startup sequence, after
        /// <see cref="AccountVaultManager.Initialize"/>. Idempotent.
        /// </summary>
        public static void Start()
        {
            if (running)
                return;

            instance = new AccountVaultBarrelReaper(new ShardAccountVaultBackend());
            running = true;

            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "AccountVaultBarrelReaper" };
            worker.Start();

            // The window is logged at startup so an operator can see the setting without querying the
            // shard. Read here rather than inside RunOnce, because this runs after PropertyManager is
            // up and RunOnce may not.
            var days = ReadRetentionDays(instance.retentionDays);

            instance.retentionDays = days;

            log.Info($"[VAULT] barrel retention sweep started; a barreled item is restorable for {days} day(s), then destroyed. Passes run every {PassInterval.TotalHours:N0} hour(s), at most {MaxRowsPerPass} row(s) each.");
        }

        /// <summary>Stops the sweep. Idempotent, and a no-op when Start was never called.</summary>
        public static void Stop()
        {
            running = false;

            // Before the join, so a pass in progress stops at its next account, row or wait.
            instance?.RequestStop();

            var thread = worker;
            worker = null;
            instance = null;

            if (thread == null)
                return;

            try
            {
                // Bounded: a wedged sweep must not hold up server shutdown. Nothing is lost by not
                // joining - a pass that does not finish simply leaves its rows for the next process.
                thread.Join(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                log.Warn($"[VAULT] the barrel retention sweep did not stop cleanly: {ex.GetFullMessage()}");
            }
        }

        private static void WorkerLoop()
        {
            var lastPassUtc = DateTime.UtcNow;

            while (running)
            {
                Thread.Sleep(TickInterval);

                try
                {
                    var self = instance;

                    if (self == null)
                        continue;

                    self.retentionDays = ReadRetentionDays(self.retentionDays);

                    var now = DateTime.UtcNow;

                    if (now - lastPassUtc < PassInterval)
                        continue;

                    lastPassUtc = now;

                    self.RunOnce(now);
                }
                catch (Exception ex)
                {
                    // The loop must outlive any single failure. This runs on a bare thread rather than
                    // a System.Timers.Timer precisely so a throw cannot be swallowed the way an
                    // Elapsed handler's is - but it still has to be caught here, or the thread ends and
                    // the sweep silently stops happening with nothing further in the log.
                    log.Error($"[VAULT] the barrel retention sweep's tick failed: {ex.GetFullMessage()}");
                }
            }
        }

        /// <summary>
        /// Reads and clamps the tunable, keeping <paramref name="current"/> if the read throws. Only
        /// ever called from the worker thread and from <see cref="Start"/>, both of which run in a
        /// process with a shard; see the class remarks for why RunOnce must not call it.
        /// </summary>
        private static int ReadRetentionDays(int current)
        {
            try
            {
                var days = PropertyManager.GetLong(RetentionKey, 30).Item;

                if (days < MinRetentionDays)
                    return MinRetentionDays;

                if (days > MaxRetentionDays)
                    return MaxRetentionDays;

                return (int)days;
            }
            catch (Exception ex)
            {
                log.Warn($"[VAULT] could not refresh {RetentionKey}; keeping {current} day(s): {ex.GetFullMessage()}");
                return current;
            }
        }
    }
}

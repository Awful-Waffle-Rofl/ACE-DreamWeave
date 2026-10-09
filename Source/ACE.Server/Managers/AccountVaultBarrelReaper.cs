using System;
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
        /// loaded itself, and that is not indirection for its own sake. The store already owns the
        /// barrel container, has finished its async inventory load, and serializes every mutation on
        /// its own queue - so routing through it is what stops a purge racing a restore of the same
        /// row, and stops a second live WorldObject existing on a guid the store is already holding.
        /// Injecting the lookup is what lets a test drive a store built over the fakes.
        /// </summary>
        public AccountVaultBarrelReaper(IAccountVaultBackend backend, Func<uint, AccountVaultStore> storeFor = null)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.storeFor = storeFor ?? AccountVaultManager.GetStore;
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
        /// </summary>
        public int RunOnce(DateTime nowUtc)
        {
            var days = retentionDays;

            if (days < MinRetentionDays)
                days = MinRetentionDays;
            else if (days > MaxRetentionDays)
                days = MaxRetentionDays;

            var cutoff = nowUtc.AddDays(-days);

            var rows = backend.GetExpiredAccountVaultBarrels(cutoff, MaxRowsPerPass);

            if (rows == null)
            {
                // NULL is a failed read, never an empty table. Purging nothing is the right answer to a
                // read that failed, and the next pass tries again.
                log.Error($"[VAULT] the barrel retention sweep could not read account_vault_barrel; nothing was purged this pass (cutoff {cutoff:o}).");
                return 0;
            }

            var purged = 0;

            foreach (var row in rows)
            {
                if (!TryPurgeRow(row))
                    continue;

                purged++;
            }

            if (purged > 0)
                log.Info($"[VAULT] barrel retention purged {purged} row(s) barreled before {cutoff:o} ({days} day window).");

            return purged;
        }

        /// <summary>
        /// One row. Guarded on its own so a single account's failure - an unreadable store, a barrel
        /// that has not loaded - costs that row and not the rest of the batch.
        /// </summary>
        private bool TryPurgeRow(AccountVaultBarrel row)
        {
            try
            {
                var store = storeFor(row.AccountId);

                if (store == null)
                {
                    log.Error($"[VAULT] barrel retention could not resolve a store for account {row.AccountId} (row {row.Id}); leaving the row open.");
                    return false;
                }

                var destroyed = false;
                string failReason = null;

                // Enqueue returns false when the store was retired by the idle sweep between the
                // lookup and here. Nothing ran, so the row is simply left for the next pass.
                if (!store.Enqueue(() => destroyed = store.TryPurgeFromBarrel(row, out failReason), out var thrown))
                    return false;

                if (thrown != null)
                {
                    log.Error($"[VAULT] barrel retention threw purging row {row.Id} for account {row.AccountId}: {thrown.GetFullMessage()}. The row is left open.");
                    return false;
                }

                if (!destroyed)
                {
                    log.Warn($"[VAULT] barrel retention left row {row.Id} (account {row.AccountId}, {row.ItemName}) open: {failReason}");
                    return false;
                }

                row.PurgedAt = DateTime.UtcNow;

                if (!backend.UpdateAccountVaultBarrel(row))
                {
                    // The item is already gone and cannot come back. The row still reading open means
                    // a later /vaultrestore will try it, find nothing in the barrel and refuse, and
                    // the next sweep will try to purge it again and do the same - noisy, but never
                    // destructive. Say it out loud rather than pretending it landed.
                    log.Error($"[VAULT] barrel retention DESTROYED 0x{row.ItemGuid ?? 0:X8} ({row.ItemName}) for account {row.AccountId} but could not stamp purged_At on row {row.Id}. The row still reads open over an item that no longer exists.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] barrel retention failed on row {row?.Id} for account {row?.AccountId}: {ex.GetFullMessage()}");
                return false;
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

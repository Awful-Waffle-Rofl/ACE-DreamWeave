using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using log4net;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Off-thread writer for account_vault_log.
    ///
    /// WHY IT EXISTS. AccountVaultStore.WriteLog used to call IAccountVaultBackend.AddAccountVaultLog
    /// inline, and that DAO method opens a ShardDbContext and calls SaveChanges - one blocking MySQL
    /// round trip PER DEPOSITED ITEM, on the world thread, inside the sell transaction. A mule sale of
    /// 512 items paid 512 of them. Everything a deposit needs in order to be correct has already
    /// happened by the time the audit row is built, so the row does not have to be written before the
    /// deposit returns; it only has to be written.
    ///
    /// Shaped after AnalyticsManager's Tier-2 writer (Managers/Analytics/AnalyticsManager.cs:54-72,
    /// :99-101): a BOUNDED queue that drops with a counter under backpressure, drained by one
    /// dedicated background thread.
    ///
    /// THE UNSTARTED STATE IS A WRITE-THROUGH, NOT A DROP, and that is deliberate rather than
    /// defensive. Unit tests, offline tools and any startup path that never reaches
    /// <see cref="Initialize"/> keep exactly the behaviour they had before this class existed - one
    /// synchronous row per call, in order, visible the moment the call returns. Only a process that
    /// has explicitly started the writer pays the latency for the throughput.
    ///
    /// WHAT CHANGES FOR AN OPERATOR once it is started: an audit row can lag the operation it
    /// describes by up to <see cref="FlushIntervalMs"/> plus one round trip, so `/mule log` read
    /// immediately after a deposit can be one row short. Ordering does NOT change - the queue is FIFO
    /// and there is exactly one drain thread, so rows still reach the table in the order they were
    /// recorded, which is what GetAccountVaultLog's OrderByDescending(Id) depends on.
    ///
    /// A DROPPED OR FAILED ROW NEVER AFFECTS THE OPERATION IT DESCRIBES. That was already the DAO's
    /// contract (ShardDatabase_AccountVault.cs, AddAccountVaultLog: failures are logged and swallowed,
    /// because refusing a player's deposit over a logging outage turns it into a storage outage), and
    /// it is preserved here in the stronger form the move makes necessary: the batch write is wrapped
    /// so a THROW from the backend cannot escape onto any caller's thread either.
    /// </summary>
    public static class AccountVaultAuditWriter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Bounded, per invariant 4. Audit rows are small and the drain is cheap, so this is generous:
        /// reaching it means the shard has been unwritable for a long time, and at that point dropping
        /// rows is better than growing without limit on a process that still has players in it.
        /// </summary>
        internal const int QueueCap = 100_000;

        /// <summary>Rows per SaveChanges. One round trip each; 500 keeps any single statement sane.</summary>
        private const int MaxRowsPerBatch = 500;

        /// <summary>Rows one worker pass may write, so a huge backlog cannot monopolise the thread.</summary>
        private const int MaxRowsPerPass = 5_000;

        private const int FlushIntervalMs = 250;

        private sealed class Pending
        {
            public IAccountVaultBackend Backend;
            public AccountVaultLog Row;
        }

        private static readonly ConcurrentQueue<Pending> queue = new ConcurrentQueue<Pending>();

        private static readonly object lifecycleLock = new object();

        /// <summary>When false, <see cref="Write"/> writes through synchronously. See the class remarks.</summary>
        private static volatile bool queueing;

        private static volatile bool running;

        private static Thread worker;

        private static long dropped;

        /// <summary>Audit rows discarded because the queue was full. Never resets while the process runs.</summary>
        public static long Dropped => Interlocked.Read(ref dropped);

        /// <summary>Rows waiting to be written. Exposed for a shutdown log line and for tests.</summary>
        public static int QueuedRows => queue.Count;

        /// <summary>
        /// Starts queueing and spins up the drain thread. Called once from Program's startup sequence,
        /// beside AccountVaultManager.Initialize. Idempotent.
        /// </summary>
        public static void Initialize()
        {
            lock (lifecycleLock)
            {
                if (running)
                    return;

                running = true;
                queueing = true;

                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VaultAuditWriter" };
                worker.Start();
            }

            log.Info($"AccountVaultAuditWriter: started, flushing every {FlushIntervalMs}ms, queue cap {QueueCap}.");
        }

        /// <summary>
        /// Stops queueing and flushes everything still held, per invariant 4.
        ///
        /// Order matters and is not arbitrary. Queueing is turned off FIRST, so anything written while
        /// this runs takes the synchronous path and cannot land in a queue nobody will drain again.
        /// The worker is then asked to stop, and whatever it did not reach is written on the CALLER's
        /// thread - which is also what makes this correct when the worker thread never existed, died,
        /// or is wedged on an unreachable database.
        ///
        /// Bounded by a deadline for the same reason ServerManager.DrainShardQueue is: a database that
        /// is down must not hold a container stop open until SIGKILL.
        /// </summary>
        public static void Shutdown()
        {
            Thread stopping;

            lock (lifecycleLock)
            {
                queueing = false;
                running = false;

                stopping = worker;
                worker = null;
            }

            if (stopping != null)
            {
                try
                {
                    stopping.Join(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] audit writer: joining the drain thread failed: {ex.GetFullMessage()}");
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            var total = 0;

            while (!queue.IsEmpty && DateTime.UtcNow < deadline)
            {
                var written = Flush(MaxRowsPerPass);

                total += written;

                // Nothing moved and the queue is still not empty: the backend is refusing. Stop rather
                // than spin, because every further pass would fail identically.
                if (written == 0)
                    break;
            }

            if (total > 0 || !queue.IsEmpty || Dropped > 0)
                log.Info($"[VAULT] audit writer stopped: flushed {total} row(s) on shutdown, {queue.Count} unwritten, {Dropped} dropped over the process lifetime.");
        }

        /// <summary>
        /// Records one audit row: queued when the writer is running, written synchronously otherwise.
        ///
        /// Never throws, whatever the backend does. The caller is a vault mutation that has already
        /// committed.
        /// </summary>
        public static void Write(IAccountVaultBackend backend, AccountVaultLog row)
        {
            if (backend == null || row == null)
                return;

            // Stamped HERE, on the thread that performed the operation, and not when the row is
            // eventually written. The whole point of an audit row is when the thing happened.
            if (row.Timestamp == default)
                row.Timestamp = DateTime.UtcNow;

            if (!queueing)
            {
                WriteThrough(backend, row);
                return;
            }

            if (queue.Count >= QueueCap)
            {
                var total = Interlocked.Increment(ref dropped);

                // Logged on the first drop and then sparsely: the condition that produces one produces
                // thousands, and an error line per dropped row would bury the outage that caused it.
                if (total == 1 || total % 1000 == 0)
                    log.Error($"[VAULT] audit writer queue is at its cap of {QueueCap}; {total} audit row(s) dropped so far. The operations themselves are unaffected - account_vault_log is incomplete.");

                return;
            }

            queue.Enqueue(new Pending { Backend = backend, Row = row });
        }

        private static void WriteThrough(IAccountVaultBackend backend, AccountVaultLog row)
        {
            try
            {
                backend.AddAccountVaultLog(row);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] audit row for owner {row.OwnerAccountId}, action {row.Action}, wcid {row.Wcid} threw: {ex.GetFullMessage()}. The operation it describes already happened and is NOT being undone.");
            }
        }

        private static void WorkerLoop()
        {
            while (running)
            {
                Thread.Sleep(FlushIntervalMs);

                try
                {
                    Flush(MaxRowsPerPass);
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] audit writer flush failed: {ex.GetFullMessage()}");
                }
            }
        }

        /// <summary>
        /// Drains up to <paramref name="maxRows"/> queued rows and writes them, batching each RUN of
        /// consecutive rows that share a backend into one <see cref="IAccountVaultBackend.AddAccountVaultLogBatch"/>
        /// call. Returns the number of rows the backend accepted.
        ///
        /// Grouping by consecutive run rather than by sorting the whole drain is what preserves FIFO
        /// order. In production it also groups everything: AccountVaultManager holds a single static
        /// ShardAccountVaultBackend that every store shares, so one pass is one batch per 500 rows.
        /// </summary>
        internal static int Flush(int maxRows)
        {
            var written = 0;
            var taken = 0;

            IAccountVaultBackend batchBackend = null;
            List<AccountVaultLog> batch = null;

            while (taken < maxRows && queue.TryDequeue(out var pending))
            {
                taken++;

                if (batch != null && (!ReferenceEquals(batchBackend, pending.Backend) || batch.Count >= MaxRowsPerBatch))
                {
                    written += WriteBatch(batchBackend, batch);
                    batch = null;
                }

                if (batch == null)
                {
                    batchBackend = pending.Backend;
                    batch = new List<AccountVaultLog>();
                }

                batch.Add(pending.Row);
            }

            if (batch != null && batch.Count > 0)
                written += WriteBatch(batchBackend, batch);

            return written;
        }

        private static int WriteBatch(IAccountVaultBackend backend, List<AccountVaultLog> rows)
        {
            try
            {
                return backend.AddAccountVaultLogBatch(rows) ? rows.Count : 0;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] audit batch of {rows.Count} row(s) threw: {ex.GetFullMessage()}. Those rows are lost; the operations they describe already happened and are NOT being undone.");
                return 0;
            }
        }

        /// <summary>
        /// Turns queueing on WITHOUT starting the drain thread, so a test can enqueue, flush on its own
        /// thread and assert against the result with no sleeps and no race. Visible to ACE.Server.Tests
        /// via InternalsVisibleTo (ACE.Server.csproj:15).
        ///
        /// Every caller must pair it with <see cref="StopQueueingForTest"/> in a finally: this state is
        /// process-static, and leaving queueing on makes every other vault test in the run stop seeing
        /// its audit rows synchronously.
        /// </summary>
        internal static void StartQueueingForTest()
        {
            lock (lifecycleLock)
            {
                if (running)
                    throw new InvalidOperationException("the audit writer's drain thread is running; a test must not race it.");

                queueing = true;
            }
        }

        /// <summary>Restores the default write-through state and discards anything still queued.</summary>
        internal static void StopQueueingForTest()
        {
            lock (lifecycleLock)
            {
                queueing = false;
            }

            while (queue.TryDequeue(out _))
            {
            }

            Interlocked.Exchange(ref dropped, 0);
        }
    }
}

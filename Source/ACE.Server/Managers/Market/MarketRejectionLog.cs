using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using log4net;

using ACE.Common.Extensions;

using MarketRejectedAttempt = ACE.Database.Models.Shard.MarketRejectedAttempt;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The audit trail for market attempts that were REFUSED (Docs/Market/DESIGN.md section 5.5).
    ///
    /// Why it exists: every refusal in <see cref="MarketManager.List"/>,
    /// <see cref="MarketManager.Delist"/> and step 0 of <see cref="MarketManager.Buy"/> returns a
    /// failed MarketResult and writes nothing anywhere, so "I tried to buy this and it failed" is
    /// uninvestigable. This is that record.
    ///
    /// WHAT IS DELIBERATELY NOT RECORDED:
    ///   - <see cref="MarketError.Disabled"/>. That is the operator's kill switch being off, not a
    ///     player event; one flipped switch would write a row per click for every player on the shard.
    ///   - Transport-layer refusals inside MarketApiHost (bad shared key, expired bearer, rate-limit
    ///     429). Those have no resolved actor, so a row would name nobody.
    ///   - Anything that REACHES the Pending row in market_transaction. Past step 0 the purchase IS
    ///     recorded and market_transaction is the authority on it; a second record here would give an
    ///     investigator two disagreeing accounts of one event. A Pending row that FAILED to write is
    ///     the exception and IS recorded, under <see cref="RepositoryRefusedCode"/> - there is no other
    ///     account of that attempt for a row to disagree with.
    ///
    /// THREADING CONTRACT, and it is the whole design of this class:
    /// <see cref="Record"/> is called from the market's hot request path, which
    /// MarketManager_Purchase's step-0 comment fixes as allocation-cheap and DB-free after the
    /// 2026-09-02 duplicate-Pending-row incident. So Record does exactly one bounded enqueue and
    /// returns. It never writes, never blocks, never throws, and NEVER READS PropertyManager - an
    /// uncached PropertyManager read falls through to DatabaseManager.ShardConfig and opens a
    /// ShardDbContext, which throws outright in the unit-test process (see AccountVaultStoreTests'
    /// Setup for the same trap). The enabled flag and the retention window are refreshed on the
    /// writer thread instead and read here from plain volatile fields.
    /// </summary>
    public static class MarketRejectionLog
    {
        private static readonly ILog log = LogManager.GetLogger("MarketRejectionLog");

        /// <summary>
        /// The one synthetic reason code, for the two branches where the SHARD ITSELF refused the write
        /// and nothing was written either way: AddListing in MarketManager.List, and the Pending-row
        /// AddTransaction in MarketManager.Buy. Both tell the player something else (already_listed,
        /// server_error), so the audit row must carry its own code rather than repeat that guess.
        /// </summary>
        public const string RepositoryRefusedCode = "repository_refused";

        /// <summary>
        /// Queue ceiling. Past it Record increments <see cref="Stats"/>.Dropped and returns - an audit
        /// trail must never be the thing that stalls a purchase, and a shard producing 20,000 unwritten
        /// refusals has a bigger problem than the tail of that burst.
        /// </summary>
        public const int QueueCap = 20_000;

        /// <summary>Queue depth at which the writer flushes without waiting for the interval.</summary>
        private const int FlushThreshold = 200;

        /// <summary>Rows per SaveChanges. A burst drains over several batches rather than one huge insert.</summary>
        private const int MaxBatchRows = 500;

        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

        private const string EnabledKey = "market_reject_log_enabled";
        private const string RetentionKey = "market_reject_retention_days";

        private static readonly ConcurrentQueue<MarketRejectedAttempt> queue = new ConcurrentQueue<MarketRejectedAttempt>();

        /// <summary>
        /// Tracked alongside the queue rather than read from ConcurrentQueue.Count, which walks the
        /// segment list. Record consults it on every refusal.
        /// </summary>
        private static int depth;

        private static long dropped;

        /// <summary>
        /// Defaults TRUE so a process that never starts the writer - every unit test - still records.
        /// The writer overwrites it from the tunable on its own thread.
        /// </summary>
        private static volatile bool enabled = true;

        private static volatile int retentionDays = 30;

        private static volatile bool running;
        private static Thread worker;

        private static IMarketRepository repository;

        private static long lastFlushTicks = DateTime.UtcNow.Ticks;
        private static DateTime lastPruneUtc = DateTime.UtcNow;

        /// <summary>Queue depth, drops since boot, and the last successful flush - the /marketadmin status line.</summary>
        public sealed class RejectionLogStats
        {
            public int QueueDepth { get; internal set; }
            public long DroppedSinceBoot { get; internal set; }
            public DateTime LastFlushUtc { get; internal set; }
            public bool WriterRunning { get; internal set; }
            public bool RecordingEnabled { get; internal set; }
            public int RetentionDays { get; internal set; }
        }

        public static RejectionLogStats Stats => new RejectionLogStats
        {
            QueueDepth = Volatile.Read(ref depth),
            DroppedSinceBoot = Interlocked.Read(ref dropped),
            LastFlushUtc = new DateTime(Interlocked.Read(ref lastFlushTicks), DateTimeKind.Utc),
            WriterRunning = running,
            RecordingEnabled = enabled,
            RetentionDays = retentionDays,
        };

        /// <summary>
        /// Starts the writer thread. Called from the PRODUCTION MarketManager.Initialize() only - the
        /// three-seam Initialize the tests drive deliberately does not start it, so a test never races
        /// a background flush against its own assertions and never needs a live shard.
        /// </summary>
        public static void Start(IMarketRepository marketRepository)
        {
            if (running)
                return;

            repository = marketRepository;
            Interlocked.Exchange(ref lastFlushTicks, DateTime.UtcNow.Ticks);
            lastPruneUtc = DateTime.UtcNow;
            running = true;

            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MarketRejectionLog" };
            worker.Start();

            log.Info($"[MARKET] rejection log writer started; queue cap {QueueCap}, flushing every {FlushInterval.TotalSeconds:N0}s.");
        }

        /// <summary>
        /// Stops the writer, drains what is queued ONCE, then clears. Idempotent, and a no-op when
        /// Start was never called (the drain has nowhere to write, so the queue is simply dropped).
        /// </summary>
        public static void Stop()
        {
            running = false;

            var thread = worker;
            worker = null;

            if (thread != null)
            {
                try
                {
                    // Bounded: a wedged writer must not hold up server shutdown. The drain below runs
                    // either way, and a double write is impossible because Flush dequeues.
                    thread.Join(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    log.Warn($"[MARKET] rejection log writer did not stop cleanly: {ex.GetFullMessage()}");
                }
            }

            try
            {
                DrainAll();
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] rejection log final drain failed; queued rows are LOST: {ex.GetFullMessage()}");
            }

            repository = null;

            while (queue.TryDequeue(out _))
                Interlocked.Decrement(ref depth);

            Volatile.Write(ref depth, 0);
        }

        // ---- the hot path ----

        /// <summary>
        /// Enqueues one refusal. ENQUEUE ONLY: no database, no PropertyManager, no lock, no throw.
        /// Callers pass the MarketError they are about to return; the stored reason is
        /// <see cref="MarketErrorCodes.ToCode"/> of it, never the enum ordinal, because the ordinals
        /// are implicit and renumber when a member is inserted while the wire codes are a published
        /// contract.
        /// </summary>
        public static void Record(MarketRejectOperation operation, MarketError error, MarketChannel channel,
                                  MarketActor actor, uint? listingId, uint? itemGuid, uint wcid, int count,
                                  long priceMmd, string detail = null)
            => Record(operation, MarketErrorCodes.ToCode(error), channel, actor, listingId, itemGuid, wcid, count, priceMmd, detail);

        /// <summary>
        /// The explicit-code form, for the ONE synthetic code
        /// (<see cref="RepositoryRefusedCode"/>). Nothing else may invent a code.
        /// </summary>
        public static void Record(MarketRejectOperation operation, string reasonCode, MarketChannel channel,
                                  MarketActor actor, uint? listingId, uint? itemGuid, uint wcid, int count,
                                  long priceMmd, string detail = null)
        {
            if (!enabled || string.IsNullOrEmpty(reasonCode))
                return;

            if (Volatile.Read(ref depth) >= QueueCap)
            {
                Interlocked.Increment(ref dropped);
                return;
            }

            Interlocked.Increment(ref depth);

            queue.Enqueue(new MarketRejectedAttempt
            {
                Operation = (int)operation,
                ReasonCode = Truncate(reasonCode, 32),
                AccountId = actor.AccountId,
                CharacterGuid = actor.CharacterGuid,
                CharacterName = Truncate(actor.Name ?? string.Empty, 255),
                ListingId = listingId,
                ItemGuid = itemGuid,
                Wcid = wcid,
                Count = count,
                PriceMmd = priceMmd,
                Channel = (int)channel,
                Timestamp = DateTime.UtcNow,
                Detail = detail == null ? null : Truncate(detail, 255),
            });
        }

        /// <summary>The columns are bounded; a value longer than the column would fail the whole batch insert.</summary>
        private static string Truncate(string value, int max)
            => value != null && value.Length > max ? value.Substring(0, max) : value;

        // ---- the writer ----

        private static void WorkerLoop()
        {
            while (running)
            {
                Thread.Sleep(TickInterval);

                try
                {
                    RefreshTunables();

                    var now = DateTime.UtcNow;
                    var sinceFlush = now - new DateTime(Interlocked.Read(ref lastFlushTicks), DateTimeKind.Utc);

                    if (Volatile.Read(ref depth) >= FlushThreshold || sinceFlush >= FlushInterval)
                        FlushOnce();

                    if (now - lastPruneUtc >= PruneInterval)
                    {
                        lastPruneUtc = now;
                        Prune();
                    }
                }
                catch (Exception ex)
                {
                    // The loop must outlive any single failure; a dead writer silently stops the whole
                    // audit trail, which is exactly the failure mode this table exists to prevent.
                    log.Error($"[MARKET] rejection log writer tick failed: {ex.GetFullMessage()}");
                }
            }
        }

        /// <summary>
        /// Reads the two tunables on THIS thread only. Wrapped because an uncached PropertyManager read
        /// opens a ShardDbContext and can throw; the previous values then stand, which is the safe
        /// direction (recording keeps working while the shard is unreachable).
        /// </summary>
        private static void RefreshTunables()
        {
            try
            {
                enabled = PropertyManager.GetBool(EnabledKey, true).Item;

                var days = PropertyManager.GetLong(RetentionKey, 30).Item;

                if (days < 1)
                    days = 1;
                else if (days > 3650)
                    days = 3650;

                retentionDays = (int)days;
            }
            catch (Exception ex)
            {
                log.Warn($"[MARKET] could not refresh the rejection log tunables; keeping enabled={enabled}, retention={retentionDays}d: {ex.GetFullMessage()}");
            }
        }

        /// <summary>
        /// One batch, at most <see cref="MaxBatchRows"/> rows. A failed write DROPS the batch rather
        /// than requeueing it: the DAO has already logged loudly, and requeueing against an unreachable
        /// shard is an unbounded retry loop that would fill the queue and then start dropping anyway.
        /// </summary>
        private static bool FlushOnce()
        {
            var repo = repository;

            Interlocked.Exchange(ref lastFlushTicks, DateTime.UtcNow.Ticks);

            if (repo == null || queue.IsEmpty)
                return false;

            var rows = new List<MarketRejectedAttempt>();

            while (rows.Count < MaxBatchRows && queue.TryDequeue(out var row))
            {
                Interlocked.Decrement(ref depth);
                rows.Add(row);
            }

            if (rows.Count == 0)
                return false;

            repo.AddRejectedAttempts(rows);

            return true;
        }

        /// <summary>Batches until the queue is empty. Used by <see cref="Stop"/>.</summary>
        private static void DrainAll()
        {
            while (FlushOnce())
            {
            }
        }

        private static void Prune()
        {
            var repo = repository;

            if (repo == null)
                return;

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var removed = repo.PruneRejectedAttempts(cutoff);

            if (removed > 0)
                log.Info($"[MARKET] pruned {removed} rejected-attempt row(s) older than {retentionDays} day(s).");
        }

        /// <summary>
        /// Test seam: the rows currently queued, oldest first, without consuming them. There is no
        /// production reader - the writer thread owns the queue - so this exists so a test can assert
        /// "exactly one row, with this reason code" without a shard database.
        /// </summary>
        internal static IReadOnlyList<MarketRejectedAttempt> Snapshot() => new List<MarketRejectedAttempt>(queue);

        /// <summary>Test seam: empties the queue and resets the counters and the cached tunables.</summary>
        internal static void ResetForTests()
        {
            while (queue.TryDequeue(out _))
            {
            }

            Volatile.Write(ref depth, 0);
            Interlocked.Exchange(ref dropped, 0);
            enabled = true;
            retentionDays = 30;
        }

        /// <summary>Test seam: forces the enabled flag the writer would otherwise own.</summary>
        internal static void SetEnabledForTests(bool value) => enabled = value;

        /// <summary>Test seam: drains into <paramref name="marketRepository"/> the way the writer would.</summary>
        internal static void DrainForTests(IMarketRepository marketRepository)
        {
            var previous = repository;
            repository = marketRepository;

            try
            {
                DrainAll();
            }
            finally
            {
                repository = previous;
            }
        }
    }
}

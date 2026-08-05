using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Analytics
{
    /// <summary>
    /// Off-thread analytics writer for the monitoring initiative (Docs/Monitoring/DESIGN.md §4).
    /// Accumulates per-character xp/lum in memory (lock-free) and, on a background thread every
    /// N seconds, snapshots the online roster + per-block population and flushes the accumulated
    /// rates to ace_analytics. Nothing here runs on a sim thread; the hot-path hooks are a single
    /// Interlocked.Add. All disabled unless Server.EnableAnalytics is true.
    ///
    /// Tier-2 (trade/give/bank audit events) and the read-side dashboard are separate follow-ups.
    /// </summary>
    public static class AnalyticsManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static volatile bool enabled;
        private static TimeSpan flushInterval;
        private static int retentionDays;
        private static Thread worker;
        private static volatile bool running;

        private static DateTime lastFlushUtc;
        private static DateTime lastPruneUtc;

        private sealed class RateAccum
        {
            public string Name;
            public long Xp;
            public long Lum;
        }

        // Double-buffered: hot path adds into this dictionary; the flush atomically swaps in a fresh one.
        private static ConcurrentDictionary<uint, RateAccum> accumulator = new ConcurrentDictionary<uint, RateAccum>();

        // Tier-2 audit events (trades, gives, bank transfers): enqueued on the sim thread, drained
        // and batch-inserted by the writer. Bounded — drop with a counter under backpressure.
        private const int Tier2QueueCap = 100_000;
        private static readonly ConcurrentQueue<AnalyticsDatabase.ItemFlowRow> itemFlowQueue = new ConcurrentQueue<AnalyticsDatabase.ItemFlowRow>();
        private static readonly ConcurrentQueue<AnalyticsDatabase.CurrencyFlowRow> currencyFlowQueue = new ConcurrentQueue<AnalyticsDatabase.CurrencyFlowRow>();
        private static long tier2Dropped;

        public static void Initialize()
        {
            var config = ConfigManager.Config.Server;
            if (!config.EnableAnalytics)
                return;

            flushInterval = TimeSpan.FromSeconds(Math.Max(10, config.AnalyticsFlushIntervalSeconds));
            retentionDays = (int)Math.Max(1, config.AnalyticsRetentionDays);

            try
            {
                AnalyticsDatabase.Initialize(ConfigManager.Config.MySql.Analytics);
            }
            catch (Exception ex)
            {
                log.Error("AnalyticsManager: failed to initialize ace_analytics; analytics disabled.", ex);
                return;
            }

            lastFlushUtc = DateTime.UtcNow;
            lastPruneUtc = DateTime.UtcNow;
            enabled = true;
            running = true;

            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "AnalyticsWriter" };
            worker.Start();

            log.Info($"AnalyticsManager: enabled, flushing every {flushInterval.TotalSeconds:N0}s, retaining {retentionDays}d.");
        }

        public static void Shutdown() => running = false;

        // --- hot-path hooks (called from the XP / Luminance grant path) ---

        public static void RecordXp(Player player, long amount)
        {
            if (!enabled || amount <= 0 || player == null)
                return;

            var entry = accumulator.GetOrAdd(player.Guid.Full, _ => new RateAccum { Name = player.Name });
            Interlocked.Add(ref entry.Xp, amount);
        }

        public static void RecordLuminance(Player player, long amount)
        {
            if (!enabled || amount <= 0 || player == null)
                return;

            var entry = accumulator.GetOrAdd(player.Guid.Full, _ => new RateAccum { Name = player.Name });
            Interlocked.Add(ref entry.Lum, amount);
        }

        // --- Tier-2 audit hooks (called on the sim thread; capture values now, never hold the WorldObject) ---

        /// <summary>A single item moved between two players in a trade (one call per item, per direction).</summary>
        public static void RecordTradeItem(Player from, Player to, WorldObject item) => EnqueueItem("trade", from, to, item);

        /// <summary>A player-to-player give.</summary>
        public static void RecordGive(Player from, Player to, WorldObject item) => EnqueueItem("give", from, to, item);

        private static void EnqueueItem(string kind, Player from, Player to, WorldObject item)
        {
            if (!enabled || from == null || to == null || item == null)
                return;

            if (itemFlowQueue.Count + currencyFlowQueue.Count >= Tier2QueueCap)
            {
                Interlocked.Increment(ref tier2Dropped);
                return;
            }

            itemFlowQueue.Enqueue(new AnalyticsDatabase.ItemFlowRow
            {
                Ts = DateTime.UtcNow,
                Kind = kind,
                FromId = from.Guid.Full,
                FromName = from.Name,
                ToId = to.Guid.Full,
                ToName = to.Name,
                ToIsPlayer = true,
                ItemWcid = item.WeenieClassId,
                ItemName = item.Name ?? string.Empty,
                StackSize = item.StackSize ?? 1,
                Value = item.Value ?? 0
            });
        }

        /// <summary>A banked-currency transfer between characters (recipient may be offline).</summary>
        public static void RecordBankTransfer(Player from, uint toId, string toName, string currency, long amount)
        {
            if (!enabled || from == null || amount <= 0)
                return;

            if (itemFlowQueue.Count + currencyFlowQueue.Count >= Tier2QueueCap)
            {
                Interlocked.Increment(ref tier2Dropped);
                return;
            }

            currencyFlowQueue.Enqueue(new AnalyticsDatabase.CurrencyFlowRow
            {
                Ts = DateTime.UtcNow,
                Kind = "bank_transfer",
                FromId = from.Guid.Full,
                FromName = from.Name,
                ToId = toId,
                ToName = toName ?? string.Empty,
                Currency = currency,
                Amount = amount
            });
        }

        // --- background writer ---

        private static void WorkerLoop()
        {
            while (running)
            {
                Thread.Sleep(1000);

                // Drain Tier-2 audit events every tick (~1s latency), independent of the 60s snapshot flush.
                DrainTier2();

                if (DateTime.UtcNow - lastFlushUtc < flushInterval)
                    continue;

                try
                {
                    Flush();
                }
                catch (Exception ex)
                {
                    log.Error("AnalyticsManager: flush failed.", ex);
                }
            }
        }

        private static void DrainTier2()
        {
            if (itemFlowQueue.IsEmpty && currencyFlowQueue.IsEmpty)
                return;

            var items = new List<AnalyticsDatabase.ItemFlowRow>();
            while (items.Count < 5000 && itemFlowQueue.TryDequeue(out var item))
                items.Add(item);

            var currencies = new List<AnalyticsDatabase.CurrencyFlowRow>();
            while (currencies.Count < 5000 && currencyFlowQueue.TryDequeue(out var cur))
                currencies.Add(cur);

            try
            {
                AnalyticsDatabase.WriteTier2(items, currencies);
            }
            catch (Exception ex)
            {
                log.Error("AnalyticsManager: Tier-2 write failed.", ex);
            }
        }

        private static void Flush()
        {
            var now = DateTime.UtcNow;
            var secs = (now - lastFlushUtc).TotalSeconds;
            lastFlushUtc = now;

            // Atomically take the accumulated rates and reset for the next interval.
            var drained = Interlocked.Exchange(ref accumulator, new ConcurrentDictionary<uint, RateAccum>());

            // Snapshot the online roster (cheap copy under PlayerManager's read lock), then work off-lock.
            var online = PlayerManager.GetAllOnline();

            var roster = new List<AnalyticsDatabase.RosterRow>(online.Count);
            var blockPops = new Dictionary<int, int>();
            foreach (var p in online)
            {
                var landblock = p.Location != null ? (int)p.Location.LandblockId.Landblock : 0;
                roster.Add(new AnalyticsDatabase.RosterRow(p.Guid.Full, p.Name, p.Level ?? 0, landblock));

                if (landblock != 0)
                    blockPops[landblock] = blockPops.TryGetValue(landblock, out var c) ? c + 1 : 1;
            }

            var rates = new List<AnalyticsDatabase.RateRow>(drained.Count);
            foreach (var kvp in drained)
            {
                var a = kvp.Value;
                if (a.Xp > 0 || a.Lum > 0)
                    rates.Add(new AnalyticsDatabase.RateRow(kvp.Key, a.Name, a.Xp, a.Lum));
            }

            AnalyticsDatabase.WriteFlush(roster, blockPops, rates, now, secs);

            if (now - lastPruneUtc >= TimeSpan.FromHours(1))
            {
                lastPruneUtc = now;
                AnalyticsDatabase.PruneRates(retentionDays);
            }
        }
    }
}

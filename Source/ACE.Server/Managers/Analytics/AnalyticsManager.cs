using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Server.ThreadDungeons;
using ACE.Server.Entity;
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

        // Last banked-pyreal balance written per character guid. This is what makes char_bank_snapshot
        // CHANGE-ONLY: a character whose balance has not moved since its last snapshot writes no row.
        // Touched only by the writer thread inside Flush, but concurrent by type to match the rest of this
        // class rather than invite a plain Dictionary to be read from somewhere else later.
        private static readonly ConcurrentDictionary<uint, long> lastBankedPyreals = new ConcurrentDictionary<uint, long>();

        // Tier-2 audit events (trades, gives, bank transfers): enqueued on the sim thread, drained
        // and batch-inserted by the writer. Bounded — drop with a counter under backpressure.
        private const int Tier2QueueCap = 100_000;
        private static readonly ConcurrentQueue<AnalyticsDatabase.ItemFlowRow> itemFlowQueue = new ConcurrentQueue<AnalyticsDatabase.ItemFlowRow>();
        private static readonly ConcurrentQueue<AnalyticsDatabase.CurrencyFlowRow> currencyFlowQueue = new ConcurrentQueue<AnalyticsDatabase.CurrencyFlowRow>();

        // Chat is capped and drained separately from the Tier-2 audit - see RecordChat for why.
        private const int ChatQueueCap = 200_000;
        private const int MaxChatLength = 512;   // matches chat_event.message; longer is truncated, never rejected
        private static readonly ConcurrentQueue<AnalyticsDatabase.ChatRow> chatQueue = new ConcurrentQueue<AnalyticsDatabase.ChatRow>();
        private static long chatDropped;
        private static int chatRetentionDays;
        private static long tier2Dropped;

        // Finished Thread runs, with their modifier and placement children attached. Its OWN cap
        // and counter rather than a share of the Tier-2 budget, for the same reason chat has its own: the
        // streams have unrelated cadences, and a burst in one must not silently starve the other. This one
        // is by far the quietest of the three - one row per dungeon run that FINISHES - so the cap is small
        // and reaching it would mean the analytics DB has been unreachable for a very long time.
        private const int DungeonRunQueueCap = 10_000;
        private static readonly ConcurrentQueue<AnalyticsDatabase.DungeonRunRow> dungeonRunQueue = new ConcurrentQueue<AnalyticsDatabase.DungeonRunRow>();
        private static long dungeonRunDropped;

        public static void Initialize()
        {
            var config = ConfigManager.Config.Server;
            if (!config.EnableAnalytics)
                return;

            flushInterval = TimeSpan.FromSeconds(Math.Max(10, config.AnalyticsFlushIntervalSeconds));
            retentionDays = (int)Math.Max(1, config.AnalyticsRetentionDays);
            chatRetentionDays = (int)Math.Max(1, config.AnalyticsChatRetentionDays);

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

        /// <summary>
        /// The Thread earnings hook, on both grant paths.
        ///
        /// It lives HERE rather than at the grant call sites in Player_Xp / Player_Luminance because those
        /// sites already call into this class and adding a second one would put a dungeon dependency on the
        /// XP path proper. A run's id IS its ephemeral instance id, so this is one dictionary lookup keyed on
        /// where the player is standing, and it attributes everything earned inside the copy - not only run
        /// creature kills - which is what the row's xp_gained/lum_gained columns claim.
        ///
        /// The null Location guard is not theoretical: a player mid-teleport has none.
        ///
        /// Both callers have already returned when analytics is off, and that is correct for this feature
        /// too - the counters exist only to be written to the analytics DB, so banking them with analytics
        /// disabled would cost the hot path for nothing.
        /// </summary>
        private static void BankDungeonEarnings(Player player, long amount, bool luminance)
        {
            var location = player.Location;

            if (location == null)
                return;

            var run = ThreadDungeonManager.GetRun(location.Instance);

            if (run == null)
                return;

            if (luminance)
                run.AddLum(amount);
            else
                run.AddXp(amount);
        }

        public static void RecordXp(Player player, long amount)
        {
            if (!enabled || amount <= 0 || player == null)
                return;

            BankDungeonEarnings(player, amount, luminance: false);

            var entry = accumulator.GetOrAdd(player.Guid.Full, _ => new RateAccum { Name = player.Name });
            Interlocked.Add(ref entry.Xp, amount);
        }

        public static void RecordLuminance(Player player, long amount)
        {
            if (!enabled || amount <= 0 || player == null)
                return;

            BankDungeonEarnings(player, amount, luminance: true);

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

        /// <summary>
        /// One item bought from a vendor. Call this at the point the item actually reaches the
        /// player's inventory, never on validation - a transaction that was validated and then
        /// refused must not leave a row saying goods changed hands.
        /// </summary>
        public static void RecordVendorBuy(Player player, WorldObject vendor, WorldObject item)
            => EnqueueVendorItem("buy", player, vendor, item, true);

        /// <summary>One item sold to a vendor, recorded once it has actually left the player's pack.</summary>
        public static void RecordVendorSell(Player player, WorldObject vendor, WorldObject item)
            => EnqueueVendorItem("sell", player, vendor, item, false);

        private static void EnqueueVendorItem(string kind, Player player, WorldObject vendor, WorldObject item, bool playerIsBuyer)
        {
            if (!enabled || player == null || vendor == null || item == null)
                return;

            if (itemFlowQueue.Count + currencyFlowQueue.Count >= Tier2QueueCap)
            {
                Interlocked.Increment(ref tier2Dropped);
                return;
            }

            var vendorId = VendorId(vendor);
            var vendorName = vendor.Name ?? string.Empty;

            itemFlowQueue.Enqueue(new AnalyticsDatabase.ItemFlowRow
            {
                Ts = DateTime.UtcNow,
                Kind = kind,
                FromId = playerIsBuyer ? vendorId : player.Guid.Full,
                FromName = playerIsBuyer ? vendorName : player.Name,
                ToId = playerIsBuyer ? player.Guid.Full : vendorId,
                ToName = playerIsBuyer ? player.Name : vendorName,
                ToIsPlayer = playerIsBuyer,
                ItemWcid = item.WeenieClassId,
                ItemName = item.Name ?? string.Empty,
                StackSize = item.StackSize ?? 1,
                Value = item.Value ?? 0
            });
        }

        /// <summary>
        /// The currency leg of a vendor transaction: what the player actually paid or was paid.
        /// That is NOT the sum of the items' Value - vendor markup and markdown sit between the
        /// two - so the price is recorded separately rather than inferred from the item rows.
        /// </summary>
        public static void RecordVendorPayment(Player player, WorldObject vendor, bool playerPays, uint currencyWcid, long amount)
        {
            if (!enabled || player == null || vendor == null || amount <= 0)
                return;

            if (itemFlowQueue.Count + currencyFlowQueue.Count >= Tier2QueueCap)
            {
                Interlocked.Increment(ref tier2Dropped);
                return;
            }

            var vendorId = VendorId(vendor);
            var vendorName = vendor.Name ?? string.Empty;

            currencyFlowQueue.Enqueue(new AnalyticsDatabase.CurrencyFlowRow
            {
                Ts = DateTime.UtcNow,
                Kind = playerPays ? "buy" : "sell",
                FromId = playerPays ? player.Guid.Full : vendorId,
                FromName = playerPays ? player.Name : vendorName,
                ToId = playerPays ? vendorId : player.Guid.Full,
                ToName = playerPays ? vendorName : player.Name,
                // Resolving an alternate currency's display name would mean a world-DB lookup on the
                // sim thread for a string, so the wcid is recorded as-is and left for the reader to
                // resolve. Pyreals are by far the common case and are spelled out.
                Currency = currencyWcid == (uint)ACE.Entity.Enum.WeenieClassName.W_COINSTACK_CLASS
                    ? "Pyreals"
                    : $"wcid:{currencyWcid}",
                Amount = amount
            });
        }

        /// <summary>
        /// Vendors are keyed by WeenieClassId, not by guid: a vendor's guid is reallocated whenever
        /// its landblock reloads, so guids group nothing over a window worth looking at. This shares
        /// the from_id/to_id columns with player character ids, which is safe because the two ranges
        /// are disjoint - player guids start at ObjectGuid.PlayerMin (0x50000001) and every wcid is
        /// far below that - so a counterparty id is never ambiguous about which kind it is.
        /// </summary>
        private static uint VendorId(WorldObject vendor) => vendor.WeenieClassId;

        /// <summary>
        /// The one and only chat allowlist. Owner ruling: all public channels plus local `say`;
        /// allegiance (with its patron/vassal/monarch/co-vassal variants), fellowship, the staff
        /// channels and direct messages are excluded.
        ///
        /// This is deliberately a single set rather than a decision at each call site. The excluded
        /// half is the half that matters, and a per-site judgement drifts the moment a channel is
        /// added - a caller passing an unrecognised channel is silently dropped rather than logged,
        /// which is the safe direction to fail for something carrying player speech.
        /// </summary>
        private static readonly HashSet<string> ChatChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "say", "general", "trade", "lfg", "roleplay", "society", "olthoi"
        };

        /// <summary>
        /// One delivered public chat message. Both callers sit inside the server's own gag check,
        /// so a gagged player's attempts never reach here.
        /// </summary>
        public static void RecordChat(Player player, string channel, string message)
        {
            if (!enabled || player == null || string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(message))
                return;

            if (!ChatChannels.Contains(channel))
                return;

            // Chat gets its OWN cap rather than sharing the Tier-2 budget. It is the only analytics
            // stream not bounded by the online player count - it scales with how much people talk -
            // so letting a chat flood consume the shared allowance would starve the item/currency
            // audit, which is the more valuable of the two.
            if (chatQueue.Count >= ChatQueueCap)
            {
                Interlocked.Increment(ref chatDropped);
                return;
            }

            if (message.Length > MaxChatLength)
                message = message.Substring(0, MaxChatLength);

            chatQueue.Enqueue(new AnalyticsDatabase.ChatRow
            {
                Ts = DateTime.UtcNow,
                CharacterId = player.Guid.Full,
                Name = player.Name ?? string.Empty,
                Channel = channel.ToLowerInvariant(),
                // Only meaningful for `say`; global channels have no position worth recording.
                Landblock = channel.Equals("say", StringComparison.OrdinalIgnoreCase) && player.Location != null
                    ? (int)player.Location.LandblockId.Landblock
                    : 0,
                Message = message
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

        /// <summary>
        /// One completed market sale. Takes guids and names rather than a Player because neither
        /// side is necessarily online, and records TWO rows - the MMD leg and the item leg - so the
        /// market is not the one flow where goods move with no item_flow row for mule detection.
        /// </summary>
        public static void RecordMarketSale(uint buyerId, string buyerName, uint sellerId, string sellerName,
                                            uint wcid, string itemName, int count, long priceMmdTotal)
        {
            if (!enabled || count <= 0)
                return;

            if (itemFlowQueue.Count + currencyFlowQueue.Count >= Tier2QueueCap)
            {
                Interlocked.Increment(ref tier2Dropped);
                return;
            }

            var now = DateTime.UtcNow;

            currencyFlowQueue.Enqueue(new AnalyticsDatabase.CurrencyFlowRow
            {
                Ts = now,
                Kind = "market_sale",
                FromId = buyerId,
                FromName = buyerName ?? string.Empty,
                ToId = sellerId,
                ToName = sellerName ?? string.Empty,
                Currency = "mmd",
                Amount = priceMmdTotal
            });

            itemFlowQueue.Enqueue(new AnalyticsDatabase.ItemFlowRow
            {
                Ts = now,
                Kind = "market_sale",
                FromId = sellerId,
                FromName = sellerName ?? string.Empty,
                ToId = buyerId,
                ToName = buyerName ?? string.Empty,
                ToIsPlayer = true,
                ItemWcid = wcid,
                ItemName = itemName ?? string.Empty,
                StackSize = count,
                Value = priceMmdTotal
            });
        }

        /// <summary>
        /// One FINISHED Thread run, with its modifier and placement children already attached.
        /// Called from ThreadDungeonManager.EndRun, the single exit path, immediately after the
        /// exactly-once MarkEnded gate - so exactly one row per run.
        ///
        /// The row is fully materialised by the caller. Nothing is read off the run here, deliberately:
        /// EndRun is about to evict the players and queue the copy for destruction, and this row may not be
        /// written for another second.
        /// </summary>
        /// <remarks>
        /// internal, not public, because AnalyticsDatabase is internal and so its row type is too. Every
        /// other hook on this class takes public engine types and can be public; this one takes a
        /// fully-materialised analytics row, which is exactly the point - the caller does the snapshotting.
        /// </remarks>
        internal static void RecordDungeonRun(AnalyticsDatabase.DungeonRunRow row)
        {
            if (!enabled || row == null)
                return;

            if (dungeonRunQueue.Count >= DungeonRunQueueCap)
            {
                Interlocked.Increment(ref dungeonRunDropped);
                return;
            }

            dungeonRunQueue.Enqueue(row);
        }

        // --- background writer ---

        private static void WorkerLoop()
        {
            while (running)
            {
                Thread.Sleep(1000);

                // Drain Tier-2 audit events every tick (~1s latency), independent of the 60s snapshot flush.
                DrainTier2();
                DrainChat();
                DrainDungeonRuns();

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

        private static void DrainChat()
        {
            if (chatQueue.IsEmpty)
                return;

            var rows = new List<AnalyticsDatabase.ChatRow>();
            while (rows.Count < 5000 && chatQueue.TryDequeue(out var row))
                rows.Add(row);

            try
            {
                AnalyticsDatabase.WriteChat(rows);
            }
            catch (Exception ex)
            {
                log.Error("AnalyticsManager: chat write failed.", ex);
            }
        }

        /// <summary>
        /// Drains finished dungeon runs. The batch cap is far smaller than the other two streams' because
        /// each row carries its own children and each parent costs its own INSERT plus a LAST_INSERT_ID
        /// round trip; 200 finished runs in one second is already far past anything real.
        /// </summary>
        private static void DrainDungeonRuns()
        {
            if (dungeonRunQueue.IsEmpty)
                return;

            var rows = new List<AnalyticsDatabase.DungeonRunRow>();
            while (rows.Count < 200 && dungeonRunQueue.TryDequeue(out var row))
                rows.Add(row);

            try
            {
                AnalyticsDatabase.WriteDungeonRuns(rows);
            }
            catch (Exception ex)
            {
                log.Error("AnalyticsManager: dungeon run write failed.", ex);
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

            // Null when the account pool could not be read this flush. The roster and rate rows still go
            // in - they do not depend on the bank - but no bank row is written and nothing is committed,
            // so the next flush re-emits whatever it finds.
            var bank = BuildBankSnapshots();

            AnalyticsDatabase.WriteFlush(roster, blockPops, rates, bank?.Rows, now, secs);

            // ONLY once the write has committed. WriteFlush runs on a raw MySqlConnector connection with no
            // retry wrapper, so an unreachable database throws out of here into WorkerLoop's catch, which
            // logs and carries on to the next flush. Advancing the map before that point would record
            // balances that were never written, and because the series is CHANGE-ONLY those characters would
            // then never emit a row again until their balance moved a second time - a permanently stale
            // "current balance" on a table whose newest row per character is kept forever and read with no
            // time filter. Leaving the map untouched on a throw makes the next flush re-emit the same rows.
            if (bank != null)
                CommitBankSnapshots(bank);

            if (now - lastPruneUtc >= TimeSpan.FromHours(1))
            {
                lastPruneUtc = now;
                AnalyticsDatabase.PruneRates(retentionDays);
                AnalyticsDatabase.PruneBankSnapshots(retentionDays);
                AnalyticsDatabase.PruneChat(chatRetentionDays);
            }
        }

        /// <summary>
        /// One flush's worth of bank snapshot work: the rows to write, and the balances that were observed
        /// while building them. The two are kept apart so the observations can be applied to
        /// <see cref="lastBankedPyreals"/> AFTER the write commits rather than while the rows are built.
        /// </summary>
        private sealed class BankSnapshotBatch
        {
            public readonly List<AnalyticsDatabase.BankSnapshotRow> Rows = new List<AnalyticsDatabase.BankSnapshotRow>();

            /// <summary>Every character seen this flush and its balance, changed or not. Doubles as the set of guids that still exist.</summary>
            public readonly Dictionary<uint, long> Observed = new Dictionary<uint, long>();
        }

        /// <summary>
        /// Builds this flush's char_bank_snapshot rows: one per character whose banked pyreals CHANGED
        /// since the last COMMITTED snapshot, plus every character seen for the first time. Returns NULL
        /// when the account pool could not be read at all, which means "write no bank rows this flush".
        ///
        /// This method is a pure read of <see cref="lastBankedPyreals"/> - it never advances it. That is
        /// what makes a failed write survivable; see CommitBankSnapshots.
        ///
        /// THE VALUE IS THE ACCOUNT'S POOL, NOT A PER-CHARACTER BALANCE. Banked pyreals live in one
        /// account_bank row per account, so every character on an account carries the same number here
        /// and rows sharing an account_id must never be summed. The rows stay CHARACTER-keyed anyway,
        /// because that is what lets the per-character panels resolve a character and what makes the
        /// history readable per name.
        ///
        /// A FAILED POOL READ WRITES NOTHING. AccountBankManager.GetAllBalances returns null only when
        /// no snapshot is available at all, and recording zeros for that would be indistinguishable from
        /// every account genuinely emptying at once - on a CHANGE-ONLY series whose newest row per
        /// character is kept forever, that lie would then persist until the balance next moved.
        ///
        /// PlayerManager.GetAllPlayers() is the in-memory offline + online player list, so this covers
        /// OFFLINE characters and matches exactly what /top bank ranks - and, importantly, it issues no
        /// per-player database read. That is a standing rule of this snapshot path rather than a tuning
        /// choice: this runs on the analytics writer thread every flush interval over the whole character
        /// roster, and a per-player query here would turn one cheap in-memory walk into thousands of shard
        /// round trips. The pool itself costs at most ONE query per 30s across the whole process, shared
        /// with /top bank, because GetAllBalances is a TTL snapshot rather than a per-caller read.
        /// Nothing here holds a lock either - GetAllPlayers copies under PlayerManager's own read lock
        /// and returns, and everything after that works off that copy.
        ///
        /// The first flush after a restart writes one row for every known character, because the map starts
        /// empty and so every balance reads as new. That is intended, not a burst to suppress: it re-baselines
        /// the whole series (a CHANGE-ONLY table has no other way to state a balance that never moves) and
        /// marks in the data where the restart happened.
        /// </summary>
        private static BankSnapshotBatch BuildBankSnapshots()
        {
            var balances = AccountBankManager.GetAllBalances();

            if (balances == null)
            {
                log.Warn("AnalyticsManager: no banked pyreal snapshot was available this flush; writing no char_bank_snapshot rows.");
                return null;
            }

            var players = PlayerManager.GetAllPlayers();

            var batch = new BankSnapshotBatch();

            foreach (var p in players)
            {
                var guid = p.Guid.Full;

                // Keyed exactly as /top bank keys it, guid fallback included, so the board and this
                // series can never disagree about which account a character belongs to. An orphan's key
                // is its guid, which cannot appear in account_bank, so it reads 0 - correct, since an
                // account-less character has no pool.
                var banked = balances.TryGetValue(AccountLeaderboard.AccountKeyFor(p), out var pool) ? pool : 0;

                batch.Observed[guid] = banked;

                if (!lastBankedPyreals.TryGetValue(guid, out var previous) || previous != banked)
                {
                    // name is coalesced because char_bank_snapshot.name is NOT NULL and a null would fail
                    // the INSERT, taking the roster and rate rows of the same transaction down with it
                    batch.Rows.Add(new AnalyticsDatabase.BankSnapshotRow(guid, p.Name ?? string.Empty,
                        p.Account?.AccountId ?? 0, p.Level ?? 0, banked));
                }
            }

            return batch;
        }

        /// <summary>
        /// Accepts a built batch as written: advances <see cref="lastBankedPyreals"/> to the balances that
        /// were observed, and forgets guids PlayerManager no longer lists.
        ///
        /// Called ONLY after AnalyticsDatabase.WriteFlush returns without throwing, which is the whole
        /// point of the split. The map is the sole record of what has been written for a CHANGE-ONLY
        /// series, so advancing it for rows that never reached the database would silently suppress those
        /// balances forever - the next flush would compare against a value the table does not contain and
        /// conclude nothing had changed. Keeping the two steps apart makes a failed flush cost one retry
        /// instead of a permanently wrong balance.
        ///
        /// Runs on the analytics writer thread, same as the build, and takes no lock.
        /// </summary>
        private static void CommitBankSnapshots(BankSnapshotBatch batch)
        {
            foreach (var observation in batch.Observed)
                lastBankedPyreals[observation.Key] = observation.Value;

            // Forget characters PlayerManager no longer lists (deleted, or purged), so a long uptime cannot
            // grow this map without bound. Deferred to here with the rest of the bookkeeping: evicting on a
            // flush that then failed would make the next flush treat a returning character as brand new and
            // re-emit a row it had already written. ConcurrentDictionary.Keys hands back a snapshot, so
            // removing while walking it is safe.
            foreach (var guid in lastBankedPyreals.Keys)
            {
                if (!batch.Observed.ContainsKey(guid))
                    lastBankedPyreals.TryRemove(guid, out _);
            }
        }
    }
}

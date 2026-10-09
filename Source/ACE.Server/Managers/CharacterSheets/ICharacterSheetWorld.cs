using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum.Properties;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.CharacterSheets
{
    public enum CharacterPresence { Missing, Deleted, Online, Offline }

    /// <summary>
    /// The world-facing seam of CharacterSheetService: presence, the two projections and the rank snapshot.
    /// Every method blocks the CALLER for up to its timeout and must never be called from a world thread.
    /// </summary>
    public interface ICharacterSheetWorld
    {
        CharacterPresence Presence(uint characterGuid);

        /// <summary>Blocks up to timeout. null = timed out / not ticking / refused.</summary>
        CharacterSheet BuildOnline(uint characterGuid, bool includeClassAbilities, TimeSpan timeout, bool ownerView = false);

        /// <summary>Blocks up to timeout. null = timed out / refused (e.g. came online) / failed.</summary>
        CharacterSheet BuildOffline(uint characterGuid, bool includeClassAbilities, TimeSpan timeout, bool ownerView = false);

        /// <summary>
        /// Character guid -> ranks (1..20 only) across every sheet board, exempt characters absent.
        /// Built on the world action queue; blocks up to timeout; null = not built in time.
        /// </summary>
        IReadOnlyDictionary<uint, List<SheetRank>> BuildRankSnapshot(TimeSpan timeout);
    }

    /// <summary>
    /// Live ICharacterSheetWorld over PlayerManager, ProfileBuilder and WorldManager.
    ///
    /// WHERE THE PROJECTION RUNS. CharacterSheetProjector.Project fills unlocked in-memory caches on the
    /// Player (Creature.Skills wrappers, the class ability rank cache), so it runs only on the thread that
    /// owns that Player: an online player's own action queue (Player.EnqueueAction, Player_Tick.cs), or,
    /// offline, inside the LoadOfflinePlayerAsync projection, whose pool thread is the sole owner of the
    /// throwaway Player it hydrates. Never on the request thread.
    ///
    /// TIMEOUTS. Each build publishes into a TaskCompletionSource and the caller waits at most the timeout.
    /// A player that is not ticking (portal space, logging out) never runs the action and times out; an
    /// offline load that never calls back (ProfileBuilder's "TWO SHAPES YIELD NO CALLBACK AT ALL") times
    /// out. A late result is dropped by TrySetResult on a task nobody is waiting on.
    ///
    /// VERIFIED FINDINGS (Task 5, read at 3230bcb8e):
    /// (a) /top's handler runs on the WorldManager.UpdateWorld thread. Talk arrives through
    ///     InboundMessageManager.HandleClientMessage, which enqueues the handler onto
    ///     NetworkManager.InboundMessageQueue (InboundMessageManager.cs:96); UpdateWorld runs that queue
    ///     (WorldManager.cs:661) and then WorldManager's own ActionQueue (WorldManager.cs:666) on the same
    ///     thread in the same loop iteration, and both before UpdateGameWorld (WorldManager.cs:675), whose
    ///     landblock ticks run inside a blocking Parallel.ForEach. So the WorldManager action queue reads
    ///     players under exactly the conditions /top does, and BuildRankSnapshot runs there.
    /// (b) new Spell(id) inside MarketSnapshot.FromItem is safe on the offline pool thread: Spell.Init reads
    ///     DatManager.PortalDat.SpellTable (a get-only property populated at dat load,
    ///     PortalDatDatabase.cs:37, read-only afterwards) and DatabaseManager.World.GetCachedSpell, which is
    ///     a ConcurrentDictionary lookup (WorldDatabaseWithEntityCache.cs:1084) falling back to a fresh
    ///     WorldDbContext per miss (:1119). Neither needs the world thread. Spell names are kept offline.
    /// (c) PropertyManager.GetBool/GetLong/GetString read ConcurrentDictionary caches
    ///     (PropertyManager.cs:19-22) and on a miss do one ShardConfigDatabase read on a fresh context
    ///     (e.g. ShardConfigDatabase.cs:112-116), so they are safe off the world thread. The signature used
    ///     is GetLong(string key, long fallback = 0, bool cacheFallback = true) returning Property&lt;long&gt;
    ///     (PropertyManager.cs:150).
    ///
    /// PRESENCE costs at most ONE shard read: the in-memory PlayerManager lookup first, then one character
    /// stub read (ShardDatabase.GetCharacterStubByGuid, a direct context on the request thread, not the
    /// serialized worker). A vanished row is Missing, never a thrown NullReferenceException. A row that
    /// vanishes between Presence and the offline load answers Busy (accepted residual).
    ///
    /// ABANDONED WORK. An online build whose waiter timed out is marked abandoned and its queued action
    /// returns without projecting (RunQueued). Rank computations are single-flight: while one is queued and
    /// not yet run, new waiters wait on it rather than queueing another, so a stalled world thread
    /// accumulates at most one.
    ///
    /// OFFLINE LOADS cannot be abandoned the same way: once LoadOfflinePlayerAsync has queued GetCharacter
    /// and the possession load onto the single shard worker (ProfileBuilder.cs, SerializedShardDatabase.cs),
    /// that work runs whether or not anyone still waits. So BuildOffline bounds how much is ever queued:
    /// per-character single-flight (a repeat request joins the in-flight load), a hard ceiling of
    /// MaxOutstandingOfflineLoads (4) outstanding loads with timed-out ones still counted (beyond it, null
    /// without queueing), and an OfflineLoadDeadAfterMs (30s) expiry on the monotonic clock that frees an
    /// entry whose load never called back. Each load leaves the outstanding count exactly once, by callback
    /// or by expiry. A late result is not used to warm the service's response cache: the world has no handle
    /// on the service or the slug, and adding one was judged not worth the coupling.
    /// </summary>
    public sealed class LiveCharacterSheetWorld : ICharacterSheetWorld
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string SpeedBoardKey = "speed";

        private readonly Action<IAction> enqueueWorld;
        private readonly Func<IReadOnlyDictionary<uint, List<SheetRank>>> computeRanks;
        private readonly Action<uint, Func<Player, CharacterSheet>, Action<CharacterSheet>> loadOffline;
        private readonly Func<long> monotonicMs;

        public LiveCharacterSheetWorld() : this(WorldManager.EnqueueAction, ComputeRankSnapshot, DefaultLoadOffline, DefaultMonotonicMs)
        {
        }

        /// <summary>Test seam: the world queue and the rank computation, with the live offline loader and clock.</summary>
        internal LiveCharacterSheetWorld(Action<IAction> enqueueWorld, Func<IReadOnlyDictionary<uint, List<SheetRank>>> computeRanks)
            : this(enqueueWorld, computeRanks, DefaultLoadOffline, DefaultMonotonicMs)
        {
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo, ACE.Server.csproj:15): the world queue, the rank computation, the offline
        /// loader (ProfileBuilder.LoadOfflinePlayerAsync in production) and the monotonic clock that ages
        /// in-flight offline loads.
        /// </summary>
        internal LiveCharacterSheetWorld(
            Action<IAction> enqueueWorld,
            Func<IReadOnlyDictionary<uint, List<SheetRank>>> computeRanks,
            Action<uint, Func<Player, CharacterSheet>, Action<CharacterSheet>> loadOffline,
            Func<long> monotonicMs)
        {
            this.enqueueWorld = enqueueWorld ?? throw new ArgumentNullException(nameof(enqueueWorld));
            this.computeRanks = computeRanks ?? throw new ArgumentNullException(nameof(computeRanks));
            this.loadOffline = loadOffline ?? throw new ArgumentNullException(nameof(loadOffline));
            this.monotonicMs = monotonicMs ?? throw new ArgumentNullException(nameof(monotonicMs));
        }

        private static void DefaultLoadOffline(uint characterGuid, Func<Player, CharacterSheet> project, Action<CharacterSheet> callback)
            => ProfileBuilder.LoadOfflinePlayerAsync(characterGuid, project, callback);

        private static long DefaultMonotonicMs() => Environment.TickCount64;

        /// <summary>
        /// Enqueues <paramref name="work"/> through <paramref name="enqueue"/> and waits up to the timeout for it.
        /// A waiter that gives up marks the build ABANDONED, and the queued action then returns without running
        /// the work, so a player whose queue stalls does not project a pile of sheets nobody will read once it
        /// resumes. An action already running when the waiter gives up still finishes; its result is dropped.
        /// Work that throws answers null.
        /// </summary>
        internal static T RunQueued<T>(Action<IAction> enqueue, Func<T> work, TimeSpan timeout, string what) where T : class
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = new AbandonFlag();

            enqueue(new ActionEventDelegate(() =>
            {
                if (abandoned.IsSet)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                try
                {
                    tcs.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] {what} failed: {ex.GetFullMessage()}");
                    tcs.TrySetResult(null);
                }
            }));

            if (tcs.Task.Wait(timeout))
                return tcs.Task.Result;

            abandoned.Set();
            return null;
        }

        private sealed class AbandonFlag
        {
            private int value;

            public bool IsSet => System.Threading.Volatile.Read(ref value) != 0;

            public void Set() => System.Threading.Interlocked.Exchange(ref value, 1);
        }

        public CharacterPresence Presence(uint characterGuid)
        {
            // in memory first, so an unknown guid costs no database read
            if (PlayerManager.FindByGuid(characterGuid) == null)
                return CharacterPresence.Missing;

            // ONE character stub read. A missing row is Missing (the same 404 as every other not-found cause),
            // never the NullReferenceException OfflinePlayer.IsDeleted would throw on it.
            var stub = global::ACE.Database.DatabaseManager.Shard.BaseDatabase.GetCharacterStubByGuid(characterGuid);

            if (stub == null)
                return CharacterPresence.Missing;

            // the same pair Player.IsDeleted / IsPendingDeletion read (Player.cs:293-294)
            if (stub.IsDeleted || stub.DeleteTime > 0)
                return CharacterPresence.Deleted;

            return PlayerManager.GetOnlinePlayer(characterGuid) != null ? CharacterPresence.Online : CharacterPresence.Offline;
        }

        public CharacterSheet BuildOnline(uint characterGuid, bool includeClassAbilities, TimeSpan timeout, bool ownerView = false)
        {
            var player = PlayerManager.GetOnlinePlayer(characterGuid);

            if (player == null)
                return null;

            // the projection runs on the player's own action queue, the only thread that owns this Player
            return RunQueued<CharacterSheet>(
                player.EnqueueAction,
                () => CharacterSheetProjector.Project(player, includeClassAbilities, DateTime.UtcNow, ownerView),
                timeout,
                $"online projection for 0x{characterGuid:X8}");
        }

        /// <summary>Hard ceiling on offline loads outstanding at once, timed-out ones included.</summary>
        internal const int MaxOutstandingOfflineLoads = 4;

        /// <summary>An in-flight offline load older than this is treated as dead (it may never call back).</summary>
        internal const long OfflineLoadDeadAfterMs = 30_000;

        /// <summary>Test seam: offline loads started and not yet settled (called back or expired).</summary>
        internal int OutstandingOfflineLoads => System.Threading.Volatile.Read(ref outstandingOfflineLoads);

        private readonly object offlineGate = new object();
        // Keyed by (character, ownerView): a public-sheet request must never join an owner-view load (or the
        // reverse), because the two project different things. Both kinds count against the same ceiling.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(uint, bool), OfflineLoad> offlineInFlight =
            new System.Collections.Concurrent.ConcurrentDictionary<(uint, bool), OfflineLoad>();
        private int outstandingOfflineLoads;   // Interlocked; incremented only under offlineGate

        /// <summary>One started offline load. Settled EXACTLY ONCE: by its callback or by expiry, whichever comes first.</summary>
        private sealed class OfflineLoad
        {
            public OfflineLoad(uint characterGuid, bool ownerView, long startedMs)
            {
                CharacterGuid = characterGuid;
                OwnerView = ownerView;
                StartedMs = startedMs;
            }

            public readonly uint CharacterGuid;
            public readonly bool OwnerView;
            public readonly long StartedMs;
            public readonly TaskCompletionSource<CharacterSheet> Result =
                new TaskCompletionSource<CharacterSheet>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Settled;   // 0 = outstanding, 1 = settled; Interlocked only
        }

        /// <summary>
        /// Blocks up to timeout for an offline sheet. A request for a character whose load is already in flight
        /// JOINS that load (waiting only its own timeout) instead of queueing another GetCharacter and possession
        /// load on the single shard worker. A new load is started only while fewer than
        /// <see cref="MaxOutstandingOfflineLoads"/> are outstanding - timed-out loads still count, because their
        /// shard work is still queued - and beyond that the answer is null (Busy) with nothing queued. An entry
        /// is settled by its callback, or treated as dead after <see cref="OfflineLoadDeadAfterMs"/> on the
        /// monotonic clock, since some ProfileBuilder failures never call back; the outstanding count drops
        /// exactly once either way. A joiner receives the in-flight load's projection, built with the
        /// includeClassAbilities flag of the request that started it.
        /// </summary>
        public CharacterSheet BuildOffline(uint characterGuid, bool includeClassAbilities, TimeSpan timeout, bool ownerView = false)
        {
            OfflineLoad load;
            var started = false;

            lock (offlineGate)
            {
                ExpireDeadOfflineLoads(monotonicMs());

                if (!offlineInFlight.TryGetValue((characterGuid, ownerView), out load))
                {
                    if (System.Threading.Volatile.Read(ref outstandingOfflineLoads) >= MaxOutstandingOfflineLoads)
                        return null;

                    load = new OfflineLoad(characterGuid, ownerView, monotonicMs());
                    offlineInFlight[(characterGuid, ownerView)] = load;
                    System.Threading.Interlocked.Increment(ref outstandingOfflineLoads);
                    started = true;
                }
            }

            if (started)
            {
                var owned = load;

                try
                {
                    // Started OUTSIDE the gate: the callback can arrive synchronously (character came online),
                    // and settling never takes the gate anyway. The projection runs on ProfileBuilder's pool
                    // thread, the throwaway Player's only owner.
                    loadOffline(
                        characterGuid,
                        offline => CharacterSheetProjector.Project(offline, includeClassAbilities, DateTime.UtcNow, ownerView),
                        sheet => SettleOfflineLoad(owned, sheet));
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] offline load could not start for 0x{characterGuid:X8}: {ex.GetFullMessage()}");
                    SettleOfflineLoad(owned, null);
                    return null;
                }
            }

            return WaitFor(load.Result.Task, timeout);
        }

        /// <summary>Called under offlineGate. Settles every in-flight load at least OfflineLoadDeadAfterMs old.</summary>
        private void ExpireDeadOfflineLoads(long nowMs)
        {
            foreach (var kv in offlineInFlight)
            {
                if (nowMs - kv.Value.StartedMs < OfflineLoadDeadAfterMs)
                    continue;

                if (System.Threading.Volatile.Read(ref kv.Value.Settled) == 0)
                    log.Warn($"[CHARSHEET] offline load for 0x{kv.Key.Item1:X8} never called back within {OfflineLoadDeadAfterMs}ms; treating it as dead.");

                SettleOfflineLoad(kv.Value, null);
            }
        }

        private void SettleOfflineLoad(OfflineLoad load, CharacterSheet sheet)
        {
            if (System.Threading.Interlocked.Exchange(ref load.Settled, 1) == 0)
            {
                System.Threading.Interlocked.Decrement(ref outstandingOfflineLoads);

                // conditional: never removes a newer load that has since taken this character's slot
                offlineInFlight.TryRemove(new KeyValuePair<(uint, bool), OfflineLoad>((load.CharacterGuid, load.OwnerView), load));
            }

            load.Result.TrySetResult(sheet);
        }

        private readonly object rankGate = new object();
        private Task<IReadOnlyDictionary<uint, List<SheetRank>>> rankInFlight;   // guarded by rankGate

        public IReadOnlyDictionary<uint, List<SheetRank>> BuildRankSnapshot(TimeSpan timeout)
        {
            TaskCompletionSource<IReadOnlyDictionary<uint, List<SheetRank>>> tcs = null;
            Task<IReadOnlyDictionary<uint, List<SheetRank>>> task;

            lock (rankGate)
            {
                // At most ONE rank computation queued at a time. While one has not run yet (a stalled world
                // thread), every new waiter waits on it instead of stacking more work onto the queue.
                if (rankInFlight == null || rankInFlight.IsCompleted)
                {
                    tcs = new TaskCompletionSource<IReadOnlyDictionary<uint, List<SheetRank>>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rankInFlight = tcs.Task;
                }

                task = rankInFlight;
            }

            if (tcs != null)
            {
                var owned = tcs;

                try
                {
                    // finding (a): the WorldManager queue is the thread /top reads players on
                    enqueueWorld(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            owned.TrySetResult(computeRanks());
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[CHARSHEET] rank snapshot failed: {ex.GetFullMessage()}");
                            owned.TrySetResult(null);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    // complete it, or the gate above would hold every later waiter on a build that never runs
                    log.Error($"[CHARSHEET] rank snapshot could not be queued: {ex.GetFullMessage()}");
                    owned.TrySetResult(null);
                }
            }

            return WaitFor(task, timeout);
        }

        /// <summary>
        /// Runs on the world thread. Every LeaderboardRanking board whose feature gate is open, over the same
        /// candidates and exemption /top uses, plus the ACTIVE speed season only; positions 1..LeaderboardSize.
        /// </summary>
        private static IReadOnlyDictionary<uint, List<SheetRank>> ComputeRankSnapshot()
        {
            var result = new Dictionary<uint, List<SheetRank>>();

            var exemptNames = LeaderboardExemptionManager.GetExemptAccountNames();
            var players = PlayerManager.GetAllPlayers();

            // the same IsExempt decision /top makes per candidate, taken once instead of once per board
            var exempt = new HashSet<uint>();
            foreach (var p in players)
            {
                if (LeaderboardExemptionManager.IsExempt(p, exemptNames))
                    exempt.Add(p.Guid.Full);
            }

            foreach (var board in LeaderboardRanking.Boards)
            {
                if (board.FeatureGate != null && !PropertyManager.GetBool(board.FeatureGate).Item)
                    continue;

                var ranked = LeaderboardRanking.Rank(
                    players,
                    p => exempt.Contains(p.Guid.Full),
                    board.Score,
                    board.TieBreaker,
                    null,
                    p => p.Level ?? 0,
                    p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                    p => p.Guid.Full);

                for (var i = 0; i < ranked.Count && i < LeaderboardRanking.LeaderboardSize; i++)
                {
                    Add(result, ranked[i].Guid.Full, new SheetRank
                    {
                        Board = board.Key,
                        Title = board.Title,
                        Rank = i + 1,
                        ScoreText = board.Format(board.Score(ranked[i])),
                    });
                }
            }

            var season = SpeedSeasonManager.GetActiveSeason();

            if (season != null)
            {
                // the exact resolve-then-exempt filter /top speed applies
                var ranked = LeaderboardRanking.RankSpeed(
                    SpeedBoardManager.GetSeasonBoard(season.Id),
                    e => e.Centiseconds,
                    e =>
                    {
                        var player = PlayerManager.FindByGuid(e.CharacterId);
                        return player != null && LeaderboardExemptionManager.IsExempt(player, exemptNames);
                    });

                var title = LeaderboardRanking.SpeedSeasonTitle(season);

                for (var i = 0; i < ranked.Count && i < LeaderboardRanking.LeaderboardSize; i++)
                {
                    Add(result, ranked[i].CharacterId, new SheetRank
                    {
                        Board = SpeedBoardKey,
                        Title = title,
                        Rank = i + 1,
                        ScoreText = Player.FormatSpeedRunTime(ranked[i].Centiseconds),
                    });
                }
            }

            return result;
        }

        private static void Add(Dictionary<uint, List<SheetRank>> result, uint characterGuid, SheetRank rank)
        {
            if (!result.TryGetValue(characterGuid, out var list))
            {
                list = new List<SheetRank>();
                result[characterGuid] = list;
            }

            list.Add(rank);
        }

        private static T WaitFor<T>(Task<T> task, TimeSpan timeout) where T : class
        {
            return task.Wait(timeout) ? task.Result : null;
        }
    }
}

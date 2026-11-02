using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The claim-time half of World Events pooled loot (world_events_pooled_loot_enabled): builds ONE owner-only chest
    /// holding the claimant's share, beside them, after their Weave Cache claim has been granted.
    ///
    /// ONE SHARED BUDGET PER TICK. Rolling a share is a LootGenerationFactory call per banked kill, inside a landblock
    /// action on the world heartbeat, and the owner ruling is that chest generation must not become the next spike. A
    /// budget per claimant would not hold that: twenty claimants clicking in the same second would each spend a full
    /// step per tick. So every claimant's work on a landblock goes through ONE <see cref="WorldEventShareDriver"/>, whose
    /// single self-re-enqueuing action spends ONE <see cref="ThreadLootRollBudget"/> per tick
    /// (world_events_share_roll_batch_size units, hard cap; world_events_share_step_budget_ms, the time unit - both are
    /// TOTALS per tick across every claimant) and hands it out one unit at a time, round-robin, resuming each tick where
    /// the last one stopped, so no claimant starves. ActionQueue.RunActions snapshots its count, so only an action that
    /// re-enqueues ITSELF during the drain lands in a later tick, which is why the driver re-enqueues once per tick.
    ///
    /// The chest is filled OUT of the world (the corpse order: TryAddToInventory needs no landblock) and enters the
    /// world once, full, so the create broadcast happens once per claimant instead of once per item. Only then is the
    /// share marked delivered; anything that stops a chain short hands the share back as far as it got, and the
    /// claimant's next click on the cache retries it (WorldEventCacheHandler, AlreadyClaimedCharacter).
    ///
    /// The chest is NOT a Weave Cache: it never carries PropertyBool.WorldEventCache, because GenericObject.ActOnUse
    /// dispatches the reward claim on that flag. It shares the cache LIFETIME through WorldEvent.AddCacheDecor instead,
    /// so DestroyCaches takes it at window end, and it is held through evt.AddHeld (never WorldEventSpawner.Adopt, which
    /// refuses once the creature pass has run - and the claim window is always after it).
    /// </summary>
    public static class WorldEventShareDelivery
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The share chest weenie: the Thread Cache (Content/sql/weenies/1003603 Thread Cache.sql), a plain Chest with no
        /// generator rows, whose name and description are rewritten here before it enters the world.
        /// </summary>
        internal const uint ShareChestWcid = ThreadDungeonRewardSpawner.BossCacheWcid;

        /// <summary>The unadorned chest name; the chest is labelled "Holder's Weave Spoils".</summary>
        internal const string ShareChestBaseName = "Weave Spoils";

        internal const string ShareChestLongDesc = "Your share of what fell in the fight, gathered from the Weave. It answers only to the adventurer whose name it bears, and it fades when the claim window closes.";

        internal const string ShareArrivedMessage = "Your share of the spoils settles beside you. It answers only to you.";

        internal const string ShareRetryMessage = "Your share of the spoils could not settle here. Use the cache again to retry.";

        /// <summary>Metres in front of the claimant the chest is placed.</summary>
        internal const float FrontDistance = 1.5f;

        public const string RollBatchTunable = "world_events_share_roll_batch_size";
        public const string StepBudgetTunable = "world_events_share_step_budget_ms";

        /// <summary>One driver per (pool, landblock). Weak on the pool, so a finished run's drivers go with it.</summary>
        private static readonly ConditionalWeakTable<WorldEventLootPool, Dictionary<Landblock, WorldEventShareDriver>> drivers =
            new ConditionalWeakTable<WorldEventLootPool, Dictionary<Landblock, WorldEventShareDriver>>();

        /// <summary>
        /// Starts delivering <paramref name="player"/>'s share, if they have a seat and an undelivered, non-empty share.
        /// Called OUTSIDE the claim lock. Never throws: any failure leaves the crate claim untouched and the share
        /// retryable.
        /// </summary>
        public static void TryDeliver(WorldEvent evt, WorldObject cache, Player player)
        {
            var pool = evt?.LootPool;

            if (pool == null || cache == null || player == null)
                return;

            var seat = player.Guid.Full;
            WorldEventLootShare share = null;
            WorldEventShareChain chain = null;

            try
            {
                var landblock = cache.CurrentLandblock;
                var cachePosition = cache.Location;

                // A driver whose landblock unloaded (its queued tick went with the queue) is retired FIRST, its
                // unfinished chains handed back to Pending - so this very click can begin the share again below.
                if (landblock != null)
                    RetireStaleDrivers(pool, landblock);

                if (!pool.TryBeginDelivery(seat, out share))
                    return;

                if (landblock == null || cachePosition == null)
                {
                    log.Warn($"[WORLDEVENT] run={evt.RunId} share for {player.Name} (0x{seat:X8}) not started: the cache is not in the world");
                    HandBack(pool, seat, share);
                    return;
                }

                var driver = DriverFor(evt, pool, landblock);
                chain = new WorldEventShareChain(driver.Env, pool, share, seat, player.Name, FrontOf(player.Location, cachePosition), new Position(cachePosition));

                if (!pool.TryRegisterChain(chain))
                {
                    // Disposed between the begin and here: nothing will ever run this share.
                    share.DestroyContents();
                    return;
                }

                driver.Add(chain);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} share delivery for {player.Name} (0x{seat:X8}) threw before it started; still retryable", ex);

                // A chain that was built owns the hand-back (and unregisters itself), so a throwing Add can never
                // leave a registered chain beside a share that is already Pending again.
                if (chain != null)
                    chain.FailFromDriver();
                else if (share != null)
                    HandBack(pool, seat, share);
            }
        }

        private static WorldEventShareDriver DriverFor(WorldEvent evt, WorldEventLootPool pool, Landblock landblock)
        {
            var map = drivers.GetValue(pool, _ => new Dictionary<Landblock, WorldEventShareDriver>());

            // Nothing in here reads a tunable or calls out: the env reads its tunables per tick, outside every lock.
            lock (map)
            {
                if (!map.TryGetValue(landblock, out var driver))
                {
                    driver = new WorldEventShareDriver(ProductionEnv(evt, landblock));
                    map[landblock] = driver;
                }

                return driver;
            }
        }

        /// <summary>
        /// Retires every driver of this pool whose landblock OBJECT is no longer the one LandblockManager holds at its
        /// id and instance: unloaded, or unloaded and loaded again as a new object. Unload clears the landblock's action
        /// queue, so such a driver's tick will never run again and its chains would sit Delivering forever. The simplest
        /// correct recovery is chosen: its unfinished chains are handed back to Pending (their rolled items and rares
        /// go back into the share), and the next click starts them on the live driver.
        /// NOT covered: a queue cleared while the SAME landblock object stays loaded (/reload-landblock). That driver
        /// still looks current, so its chains stay Delivering until the pool is disposed at window end.
        /// The staleness test runs outside the map lock (it takes LandblockManager's lock).
        /// </summary>
        private static void RetireStaleDrivers(WorldEventLootPool pool, Landblock current)
        {
            if (!drivers.TryGetValue(pool, out var map))
                return;

            List<Landblock> keys;

            lock (map)
                keys = new List<Landblock>(map.Keys);

            var stale = keys.Where(k => IsStaleDriver(ReferenceEquals(k, current), () => LandblockManager.IsCurrentInstance(k))).ToList();

            FailEach(RetireDrivers(map, stale), c => c.FailFromDriver(), "retiring a stale share driver");
        }

        /// <summary>
        /// A driver keyed on a landblock object is stale unless that object is the one the cache stands on now, or is
        /// still the very object loaded at its id and instance (a different live object at the same key is stale).
        /// </summary>
        internal static bool IsStaleDriver(bool isCurrentLandblock, Func<bool> isCurrentInstance)
        {
            if (isCurrentLandblock)
                return false;

            return !isCurrentInstance();
        }

        /// <summary>
        /// Runs <paramref name="fail"/> on every chain, each in its own try/catch, so one chain that throws can never
        /// strand the ones after it. Returns how many threw.
        /// </summary>
        internal static int FailEach(IEnumerable<WorldEventShareChain> chains, Action<WorldEventShareChain> fail, string context)
        {
            var threw = 0;

            foreach (var chain in chains)
            {
                try
                {
                    fail(chain);
                }
                catch (Exception ex)
                {
                    threw++;
                    log.Error($"[WORLDEVENT] share chain hand-back threw while {context}; continuing with the rest", ex);
                }
            }

            return threw;
        }

        /// <summary>
        /// Removes <paramref name="staleKeys"/>' drivers from <paramref name="map"/> and returns their unfinished chains,
        /// for the caller to fail outside the lock. Generic over the key so the tests can drive it without a Landblock.
        /// </summary>
        internal static List<WorldEventShareChain> RetireDrivers<TKey>(Dictionary<TKey, WorldEventShareDriver> map, IEnumerable<TKey> staleKeys)
        {
            var stranded = new List<WorldEventShareChain>();
            var retired = new List<WorldEventShareDriver>();

            lock (map)
            {
                foreach (var key in staleKeys)
                {
                    if (map.TryGetValue(key, out var driver))
                    {
                        map.Remove(key);
                        retired.Add(driver);
                    }
                }
            }

            foreach (var driver in retired)
                stranded.AddRange(driver.Retire());

            return stranded;
        }

        /// <summary>The live seams: the real chest, EnterWorld, the run's held list, the landblock queue and a Stopwatch.</summary>
        private static WorldEventShareEnv ProductionEnv(WorldEvent evt, Landblock landblock)
        {
            var runId = evt.RunId;
            var clock = Stopwatch.StartNew();

            return new WorldEventShareEnv
            {
                RunId = runId,
                RollBatch = ReadRollBatch,
                StepBudgetMs = ReadStepBudgetMs,
                Clock = () => clock.Elapsed.TotalMilliseconds,
                Enqueue = work => landblock.EnqueueAction(new ActionEventDelegate(work)),
                CreateChest = (owner, name) => CreateShareChest(runId, owner, name),
                EnterWorld = TryEnter,
                AddHeld = wo => evt.AddHeld(wo),
                MarkCacheLifetime = guid => evt.AddCacheDecor(guid),
                RunValid = () => evt.RunValid(runId) && evt.State == WorldEventState.Rewarding,
                Materialize = ThreadCacheFiller.MaterializeEntry,
                AnnounceRares = AnnounceRares,
                SendOwner = SendOwner,
            };
        }

        /// <summary>Returns a share to Pending; if the pool was disposed meanwhile, its contents are destroyed instead.</summary>
        internal static void HandBack(WorldEventLootPool pool, uint seat, WorldEventLootShare share)
        {
            if (!pool.EndDelivery(seat, false))
                share.DestroyContents();
        }

        /// <summary>
        /// The chest position: <see cref="FrontDistance"/> in front of the claimant, re-addressed to the cell it lands in
        /// and snapped to terrain outdoors. Null - meaning "use the cache position" - when the claimant's position is
        /// unknown or the point is not on the cache's landblock (the chain runs on THAT landblock's queue).
        /// </summary>
        internal static Position FrontOf(Position playerLocation, Position cachePosition)
        {
            if (playerLocation == null || cachePosition == null)
                return null;

            try
            {
                var front = new Position(playerLocation);
                front.SetPosition(playerLocation.Pos + playerLocation.GetCurrentDir() * FrontDistance);

                if (front.InstancedLandblock != cachePosition.InstancedLandblock)
                    return null;

                if (!front.Indoors)
                    front.AdjustMapCoords();

                return front;
            }
            catch (Exception ex)
            {
                log.Debug("[WORLDEVENT] share chest front position failed; using the cache position", ex);
                return null;
            }
        }

        /// <summary>world_events_share_roll_batch_size (units per tick, all claimants together), at least 1; the default on a failed read.</summary>
        internal static int ReadRollBatch()
        {
            try
            {
                var configured = PropertyManager.GetLong(RollBatchTunable, ThreadLootRollBudget.DefaultRollsPerStep).Item;
                return (int)Math.Clamp(configured, 1, 1000);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] could not read {RollBatchTunable}; using the shipped default", ex);
                return ThreadLootRollBudget.DefaultRollsPerStep;
            }
        }

        /// <summary>world_events_share_step_budget_ms (per tick, all claimants together), clamped to the Threads step range.</summary>
        internal static int ReadStepBudgetMs()
        {
            try
            {
                var configured = PropertyManager.GetLong(StepBudgetTunable, ThreadLootRollBudget.DefaultStepBudgetMs).Item;
                return (int)Math.Clamp(configured, ThreadLootRollBudget.MinStepBudgetMs, ThreadLootRollBudget.MaxStepBudgetMs);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] could not read {StepBudgetTunable}; using the shipped default", ex);
                return ThreadLootRollBudget.DefaultStepBudgetMs;
            }
        }

        /// <summary>A fresh, EMPTY share chest with every stamp and no Location. Null, logged, when the wcid is not a Chest.</summary>
        internal static Chest CreateShareChest(uint runId, uint ownerGuid, string ownerName)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(ShareChestWcid);

            if (!(wo is Chest chest))
            {
                log.Error($"[WORLDEVENT] run={runId} share chest wcid {ShareChestWcid} did not resolve to a Chest");
                wo?.Destroy();
                return null;
            }

            // The run stamp: the persistence exclusion (WorldObject_Database) and the orphan sweep's marker. The
            // contents stay unstamped, so an item a player takes out persists normally.
            chest.SetProperty(PropertyInt.WorldEventId, (int)runId);

            // Owner-only access, read by Chest.CheckUseRequirements.
            chest.P_DungeonCacheOwnerGuid = ownerGuid;

            // No Generator back-reference, so without this the landblock heartbeat would decay it mid-window.
            chest.TimeToRot = -1;

            // Chest.Open schedules Reset (which deletes hand-added loot, i.e. all of it) unless the interval is
            // infinite; MySQL cannot store infinity, so it is set in code, exactly as the Thread Cache does.
            chest.SetProperty(PropertyFloat.ResetInterval, double.PositiveInfinity);

            // Before EnterWorld, so the create message already carries them.
            chest.SetProperty(PropertyString.Name, ThreadDungeonRewardSpawner.ComposeCacheName(ownerName, ShareChestBaseName));
            chest.SetProperty(PropertyString.LongDesc, ShareChestLongDesc);

            return chest;
        }

        private static bool TryEnter(Container target, Position position)
        {
            target.Location = new Position(position);

            try
            {
                return target.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] share chest EnterWorld threw at {position.ToLOCString()}", ex);
                return false;
            }
        }

        private static void AnnounceRares(Container chest, List<(WorldObject Rare, string FinderName)> landed)
        {
            foreach (var (rare, finder) in landed)
                chest.EnqueueBroadcast(new GameMessageSystemChat(string.Format(Corpse.RareDiscoveredFormat, finder, rare.Name), ChatMessageType.System));

            if (landed.Count > 0)
                chest.ApplySoundEffects(Sound.TriggerActivated, 10);
        }

        private static void SendOwner(uint ownerGuid, string text)
        {
            try
            {
                PlayerManager.GetOnlinePlayer(ownerGuid)?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] could not message 0x{ownerGuid:X8} about their share", ex);
            }
        }
    }

    /// <summary>
    /// Everything a share chain and its driver touch outside themselves, as seams: production wires the real chest,
    /// EnterWorld, the run's held list and the landblock queue (WorldEventShareDelivery.ProductionEnv); tests wire fakes.
    /// </summary>
    internal sealed class WorldEventShareEnv
    {
        public uint RunId;

        /// <summary>Units (rolls, plus one per chest placement) per tick across every chain. Read once at the top of each tick, outside every lock, so it is live mid-run.</summary>
        public Func<int> RollBatch = () => ThreadLootRollBudget.DefaultRollsPerStep;

        /// <summary>Milliseconds per tick across every chain. Read like <see cref="RollBatch"/>.</summary>
        public Func<int> StepBudgetMs = () => ThreadLootRollBudget.DefaultStepBudgetMs;

        /// <summary>A monotonic millisecond clock.</summary>
        public Func<double> Clock;

        /// <summary>Puts one action on the landblock queue (a later tick, for an action that is itself running).</summary>
        public Action<Action> Enqueue;

        public Func<uint, string, Container> CreateChest;
        public Func<Container, Position, bool> EnterWorld;
        public Func<WorldObject, bool> AddHeld;
        public Action<uint> MarkCacheLifetime;
        public Func<bool> RunValid;
        public Func<ThreadLootLedgerEntry, List<WorldObject>> Materialize;
        public Action<Container, List<(WorldObject Rare, string FinderName)>> AnnounceRares;
        public Action<uint, string> SendOwner;
    }

    /// <summary>
    /// The one action per (run, landblock) that advances every pending share chain. Each tick it builds ONE
    /// <see cref="ThreadLootRollBudget"/> and hands it out one unit at a time, round-robin across the chains, starting
    /// with the chain after the last one served, so the per-tick cost is bounded by the budget plus the one unit that
    /// crosses it, however many claimants are waiting, and every claimant advances.
    /// </summary>
    internal sealed class WorldEventShareDriver
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object sync = new object();
        private readonly List<WorldEventShareChain> chains = new List<WorldEventShareChain>();
        private bool scheduled;

        // Where the next tick STARTS: the first live chain after the last one served, chosen at the end of the tick
        // that served it and kept by identity, because done chains leave the list between ticks.
        private WorldEventShareChain resumeAt;

        public WorldEventShareDriver(WorldEventShareEnv env)
        {
            Env = env;
        }

        public WorldEventShareEnv Env { get; }

        /// <summary>Ticks this driver has run, and the most units / milliseconds any one tick spent.</summary>
        public int Ticks { get; private set; }
        public int MaxTickUnits { get; private set; }
        public double MaxTickMs { get; private set; }

        public int Pending
        {
            get { lock (sync) return chains.Count; }
        }

        /// <summary>
        /// Adds a chain and schedules the driver if it is idle. Assumes a single caller thread: its only caller is
        /// TryDeliver, from the world thread's cache-use handling (an assumption, not enforced here). The sync lock keeps the chain list and the scheduled flag consistent with
        /// Tick, but two concurrent Adds racing a throwing Enqueue are not designed for.
        /// </summary>
        public void Add(WorldEventShareChain chain)
        {
            bool schedule;

            lock (sync)
            {
                chains.Add(chain);
                schedule = !scheduled;
                scheduled = true;
            }

            if (!schedule)
                return;

            try
            {
                Env.Enqueue(Tick);
            }
            catch
            {
                // Nothing will run: take the chain back out so the caller's failure path owns it, and stay idle.
                lock (sync)
                {
                    chains.Remove(chain);
                    scheduled = false;
                }

                throw;
            }
        }

        /// <summary>
        /// The landblock this driver runs on is gone: empties it (it will never tick again) and returns its unfinished
        /// chains for the caller to hand back.
        /// </summary>
        public List<WorldEventShareChain> Retire()
        {
            lock (sync)
            {
                var stranded = chains.Where(c => !c.Done).ToList();
                chains.Clear();
                scheduled = false;
                resumeAt = null;
                return stranded;
            }
        }

        /// <summary>
        /// The chain the next tick starts at: the first one after <paramref name="servedIndex"/> (wrapping, and ending
        /// on the served chain itself) that is not done; null when none is live. Pure, for the fairness tests.
        /// </summary>
        internal static T Successor<T>(IReadOnlyList<T> list, int servedIndex, Func<T, bool> isDone) where T : class
        {
            var n = list.Count;

            if (n == 0 || servedIndex < 0)
                return null;

            for (var j = 1; j <= n; j++)
            {
                var candidate = list[(servedIndex + j) % n];

                if (!isDone(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>Where a tick starts: <paramref name="resumeAt"/>'s index, or 0 when it is unset or no longer listed.</summary>
        internal static int StartIndex<T>(IReadOnlyList<T> list, T resumeAt) where T : class
        {
            if (resumeAt != null)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list[i], resumeAt))
                        return i;
                }
            }

            return 0;
        }

        /// <summary>One landblock tick of share work. Never throws.</summary>
        public void Tick()
        {
            // The tunables first, outside every lock (a PropertyManager read can reach the database on a miss).
            int rollBatch;
            int stepBudgetMs;

            try
            {
                rollBatch = Env.RollBatch();
                stepBudgetMs = Env.StepBudgetMs();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={Env.RunId} share driver could not read its tunables; using the shipped defaults", ex);
                rollBatch = ThreadLootRollBudget.DefaultRollsPerStep;
                stepBudgetMs = ThreadLootRollBudget.DefaultStepBudgetMs;
            }

            List<WorldEventShareChain> snapshot;
            int start;

            lock (sync)
            {
                snapshot = new List<WorldEventShareChain>(chains);
                start = StartIndex(snapshot, resumeAt);
            }

            var tickStart = Env.Clock();
            var budget = new ThreadLootRollBudget(rollBatch, () => Env.Clock() - tickStart, stepBudgetMs);
            WorldEventShareChain served = null;
            var servedIndex = -1;

            try
            {
                var n = snapshot.Count;
                var progressed = true;
                var k = 0;

                // One unit per turn, round-robin, until the budget is spent or every chain is done.
                while (n > 0 && progressed && !budget.Exhausted)
                {
                    progressed = false;

                    for (var i = 0; i < n && !budget.Exhausted; i++)
                    {
                        var index = (start + k + i) % n;
                        var chain = snapshot[index];

                        if (chain.Done)
                            continue;

                        chain.Turn(budget);
                        served = chain;
                        servedIndex = index;
                        progressed = true;
                    }

                    k += n;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={Env.RunId} share driver tick threw", ex);
            }

            var spentMs = Env.Clock() - tickStart;

            Ticks++;
            MaxTickUnits = Math.Max(MaxTickUnits, budget.Spent);
            MaxTickMs = Math.Max(MaxTickMs, spentMs);

            bool again;

            lock (sync)
            {
                if (served != null)
                    resumeAt = Successor(snapshot, servedIndex, c => c.Done);

                chains.RemoveAll(c => c.Done);
                again = chains.Count > 0;
                scheduled = again;
            }

            if (!again)
            {
                log.Info($"[WORLDEVENT] run={Env.RunId} share driver idle: ticks={Ticks} maxTickUnits={MaxTickUnits} maxTickMs={MaxTickMs:F1} budgetMs={stepBudgetMs} rollCap={rollBatch}");
                return;
            }

            try
            {
                Env.Enqueue(Tick);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={Env.RunId} share driver could not re-enqueue; failing its chains", ex);

                List<WorldEventShareChain> stranded;

                lock (sync)
                {
                    stranded = new List<WorldEventShareChain>(chains);
                    chains.Clear();
                    scheduled = false;
                }

                WorldEventShareDelivery.FailEach(stranded, c => c.FailFromDriver(), "the driver could not re-enqueue");
            }
        }
    }

    /// <summary>
    /// One claimant's delivery, advanced one unit at a time by its <see cref="WorldEventShareDriver"/>. A unit is one
    /// banked kill rolled into the chest, or the one chest placement at the end. Registered with the pool while live, so
    /// a pool Dispose destroys what it holds even if the landblock queue that would have run it was cleared
    /// (<see cref="DestroyHeld"/>).
    /// </summary>
    internal sealed class WorldEventShareChain : IWorldEventShareChain
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object sync = new object();

        private readonly WorldEventShareEnv env;
        private readonly WorldEventLootPool pool;
        private readonly WorldEventLootShare share;
        private readonly uint ownerGuid;
        private readonly string ownerName;
        private readonly Position primary;
        private readonly Position fallback;

        private readonly List<WorldObject> prebuilt = new List<WorldObject>();
        private readonly Dictionary<WorldObject, string> rareFinders = new Dictionary<WorldObject, string>(ReferenceEqualityComparer.Instance);
        private readonly List<WorldObject> refused = new List<WorldObject>();

        private Container chest;
        private int nextPlacement;
        private bool readyToPlace;
        private WorldEventShareFill.StepStop finalStop = WorldEventShareFill.StepStop.Drained;
        private int entriesAtStart;

        public WorldEventShareChain(WorldEventShareEnv env, WorldEventLootPool pool, WorldEventLootShare share, uint ownerGuid, string ownerName,
            Position primary, Position fallback)
        {
            this.env = env;
            this.pool = pool;
            this.share = share;
            this.ownerGuid = ownerGuid;
            this.ownerName = ownerName;
            this.primary = primary;
            this.fallback = fallback;
        }

        /// <summary>True once the chain reached any terminal state (delivered, handed back, abandoned, destroyed).</summary>
        public bool Done { get; private set; }

        /// <summary>How it ended: "delivered", "handed-back", "abandoned", "destroyed", or null while live.</summary>
        public string Outcome { get; private set; }

        public int Turns { get; private set; }
        public int Rolls { get; private set; }

        /// <summary>Test seam: the chest while it is being filled (null before the first turn and after the end).</summary>
        internal Container ChestForTest => chest;

        /// <summary>
        /// One turn: at most ONE unit of budgeted work - one banked kill rolled, or the chest placed. Handed-back items
        /// and rares ahead of the rolls cost nothing. Never throws.
        /// </summary>
        public void Turn(ThreadLootRollBudget budget)
        {
            lock (sync)
            {
                if (Done)
                    return;

                Turns++;

                try
                {
                    if (pool.IsDisposed || !env.RunValid())
                    {
                        Abandon("the claim window closed");
                        return;
                    }

                    if (chest == null && !Start())
                        return;

                    if (!readyToPlace)
                    {
                        var before = budget.Spent;

                        var stop = WorldEventShareFill.RunStep(prebuilt, share.Entries, env.Materialize, Place,
                            () => ThreadCacheFiller.IsFull(chest), refused, budget, maxEntries: 1);

                        Rolls += budget.Spent - before;

                        if (stop == WorldEventShareFill.StepStop.Exhausted || stop == WorldEventShareFill.StepStop.Yielded)
                            return;

                        readyToPlace = true;
                        finalStop = stop;
                    }

                    // The placement is a unit of its own, so it never rides on the back of a roll that already crossed
                    // the tick's budget; it waits for the next tick instead.
                    if (budget.Exhausted)
                        return;

                    budget.Spend(1);
                    Complete();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={env.RunId} share chain for {ownerName} (0x{ownerGuid:X8}) threw on turn {Turns}; the share is handed back", ex);

                    if (!Done)
                        Fail();
                }
            }
        }

        /// <summary>The driver could not keep running: hand the share back so a later click retries it.</summary>
        public void FailFromDriver()
        {
            lock (sync)
            {
                if (!Done)
                    Fail();
            }
        }

        /// <summary>
        /// Pool dispose (window end or abort), for a chain whose landblock queue may never run it again: destroys the
        /// chest and everything this chain or its share holds. Returns the count destroyed.
        /// </summary>
        public int DestroyHeld()
        {
            lock (sync)
            {
                if (Done)
                    return 0;

                var destroyed = DestroyEverything();
                End("destroyed");
                return destroyed;
            }
        }

        private int DestroyEverything()
        {
            var destroyed = 0;

            foreach (var item in prebuilt)
            {
                if (item != null && !item.IsDestroyed)
                {
                    item.Destroy();
                    destroyed++;
                }
            }

            foreach (var item in refused)
            {
                if (item != null && !item.IsDestroyed)
                {
                    item.Destroy();
                    destroyed++;
                }
            }

            prebuilt.Clear();
            refused.Clear();

            if (chest != null)
            {
                destroyed += chest.Inventory.Count;
                chest.Destroy();
                chest = null;
            }

            destroyed += share.DestroyContents();
            return destroyed;
        }

        private void End(string outcome)
        {
            Done = true;
            Outcome = outcome;
            pool.UnregisterChain(this);
        }

        /// <summary>The first turn: the empty chest, and the handed-over rares and built items queued ahead of any roll.</summary>
        private bool Start()
        {
            chest = env.CreateChest(ownerGuid, ownerName);

            if (chest == null)
            {
                End("handed-back");
                WorldEventShareDelivery.HandBack(pool, ownerGuid, share);
                return false;
            }

            nextPlacement = ThreadCacheFiller.NextPlacementPosition(chest);

            foreach (var (rare, finder) in share.Rares)
            {
                if (rare == null)
                    continue;

                prebuilt.Add(rare);
                rareFinders[rare] = finder;
            }

            share.Rares.Clear();
            prebuilt.AddRange(share.Built);
            share.Built.Clear();

            entriesAtStart = share.Entries.Count;
            return true;
        }

        private bool Place(WorldObject item)
        {
            if (item == null)
                return true;

            if (chest.Inventory.ContainsKey(item.Guid))
                return true;

            if (!chest.TryAddToInventory(item, nextPlacement))
                return false;

            nextPlacement++;
            return true;
        }

        /// <summary>The placement: drop the remainder, put the full chest into the world once, hand off or hand back.</summary>
        private void Complete()
        {
            // Remainder: whatever did not fit (refused items and anything still queued) and every entry never rolled.
            var unrolled = share.Entries.Count;
            var leftover = refused.Count + prebuilt.Count;

            foreach (var item in refused)
                item?.Destroy();

            foreach (var item in prebuilt)
                item?.Destroy();

            refused.Clear();
            prebuilt.Clear();
            share.Entries.Clear();

            var items = chest.Inventory.Count;

            if (items == 0)
            {
                chest.Destroy();
                chest = null;
                pool.EndDelivery(ownerGuid, true);
                End("delivered");

                log.Info($"[WORLDEVENT] run={env.RunId} share char={ownerName} guid=0x{ownerGuid:X8} items=0 entries={entriesAtStart} turns={Turns} rolls={Rolls} - nothing rolled, no chest");
                return;
            }

            var placedAt = "front";

            if (primary == null || !env.EnterWorld(chest, primary))
            {
                placedAt = "cache";

                var moved = env.CreateChest(ownerGuid, ownerName);

                if (moved != null)
                {
                    Transfer(chest, moved);
                    chest.Destroy();
                    chest = moved;
                }

                if (moved == null || fallback == null || !env.EnterWorld(chest, fallback))
                {
                    log.Error($"[WORLDEVENT] run={env.RunId} share chest for {ownerName} (0x{ownerGuid:X8}) failed to enter the world in front of them and at the cache; handed back for retry");

                    Fail();
                    env.SendOwner(ownerGuid, WorldEventShareDelivery.ShareRetryMessage);
                    return;
                }
            }

            if (!env.AddHeld(chest))
            {
                // Placed after cleanup closed the held list: nothing would ever remove it.
                chest.Destroy();
                chest = null;
                pool.EndDelivery(ownerGuid, true);
                End("abandoned");

                log.Info($"[WORLDEVENT] run={env.RunId} share chest for {ownerName} (0x{ownerGuid:X8}) placed after cleanup closed the held list; destroyed");
                return;
            }

            // Delivered from here on: nothing below may hand the share back.
            var placed = chest;
            chest = null;
            End("delivered");
            pool.EndDelivery(ownerGuid, true);

            // Same lifetime as the caches: DestroyCaches takes it when the window ends.
            env.MarkCacheLifetime(placed.Guid.Full);

            var landed = new List<(WorldObject Rare, string FinderName)>();

            foreach (var item in placed.Inventory.Values)
            {
                if (item != null && rareFinders.TryGetValue(item, out var finder))
                    landed.Add((item, finder));
            }

            try
            {
                env.AnnounceRares(placed, landed);
                env.SendOwner(ownerGuid, WorldEventShareDelivery.ShareArrivedMessage);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={env.RunId} share chest for {ownerName} (0x{ownerGuid:X8}) announce threw; the share stands", ex);
            }

            log.Info($"[WORLDEVENT] run={env.RunId} share char={ownerName} guid=0x{ownerGuid:X8} chest=0x{placed.Guid.Full:X8} at={placedAt} items={items} rares={landed.Count} entries={entriesAtStart} turns={Turns} rolls={Rolls} attempt={share.Attempts} stop={finalStop} remainderItems={leftover} remainderEntries={unrolled}");
        }

        /// <summary>Moves every item out of <paramref name="from"/> (removed first: Destroy destroys a container's contents).</summary>
        private static void Transfer(Container from, Container to)
        {
            var next = ThreadCacheFiller.NextPlacementPosition(to);

            foreach (var item in new List<WorldObject>(from.Inventory.Values))
            {
                if (!from.TryRemoveFromInventory(item.Guid, out var removed))
                    continue;

                if (to.TryAddToInventory(removed, next))
                    next++;
                else
                    removed.Destroy();
            }
        }

        /// <summary>
        /// Hands the share back as far as it got: the chest's contents and anything still queued return to it (rares
        /// keep their finder), the empty chest is destroyed, and the share is Pending again for the next click.
        /// </summary>
        private void Fail()
        {
            var back = new List<WorldObject>(prebuilt);
            back.AddRange(refused);
            prebuilt.Clear();
            refused.Clear();

            if (chest != null)
            {
                foreach (var item in new List<WorldObject>(chest.Inventory.Values))
                {
                    if (chest.TryRemoveFromInventory(item.Guid, out var removed))
                        back.Add(removed);
                }

                chest.Destroy();
                chest = null;
            }

            foreach (var item in back)
            {
                if (item == null)
                    continue;

                if (rareFinders.TryGetValue(item, out var finder))
                    share.Rares.Add((item, finder));
                else
                    share.Built.Add(item);
            }

            End("handed-back");
            WorldEventShareDelivery.HandBack(pool, ownerGuid, share);
        }

        /// <summary>The run is closing: everything this chain holds is destroyed and the share is finished.</summary>
        private void Abandon(string why)
        {
            DestroyEverything();
            End("abandoned");
            pool.EndDelivery(ownerGuid, true);

            log.Info($"[WORLDEVENT] run={env.RunId} share chain for {ownerName} (0x{ownerGuid:X8}) abandoned after {Turns} turn(s): {why}");
        }
    }
}

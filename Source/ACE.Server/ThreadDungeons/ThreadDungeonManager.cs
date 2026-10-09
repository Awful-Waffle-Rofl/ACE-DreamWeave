using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Registry of live runs (TECH-DESIGN S5). Runs are keyed by instance id. Tick() runs on the world thread
    /// from WorldManager.UpdateGameWorld; it hands every Starting run whose copy has finished loading to
    /// ThreadDungeonSpawner.TryPopulate, and reaps expired runs and runs whose landblock has already
    /// unloaded. EndRun is the single exit path and is what destroys the bound gem (cleanup layer (a), Q10).
    ///
    /// Threading. Tick() runs on the world thread. Nothing else here is guaranteed to: OnRunCreatureDied()
    /// runs on whichever landblock tick thread killed the creature, and TryStart() runs on whatever thread
    /// reached the gem's ActOnUse - a gem used from the ground arrives through a landblock action chain
    /// (Player_Move.cs:71-76), not the world thread. So the registry is a ConcurrentDictionary, every mutable
    /// field of a ThreadDungeonRun is guarded by that run's own lock (see ThreadDungeonRun), and the
    /// admission sequence in TryStart - cap checks, instance-id allocation, registration - is serialised
    /// under <see cref="startLock"/> so two simultaneous gem uses cannot both pass the same cap.
    /// </summary>
    public static class ThreadDungeonManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<uint, ThreadDungeonRun> runs = new ConcurrentDictionary<uint, ThreadDungeonRun>();

        /// <summary>
        /// Copies of ENDED runs that still held players when EndRun evicted them, keyed by instance id. EndRun cannot
        /// queue an occupied copy (its eviction teleports are asynchronous, see EndRun), so before this set existed
        /// such a copy waited for its own heartbeat to see it idle for Landblock.UnloadInterval - five minutes after the
        /// last evicted player left. <see cref="QueueEmptiedEndedCopies"/> queues it on the first 1 s tick that finds
        /// it empty instead. Concurrent because EndRun can run on a landblock thread; drained on the world thread.
        /// A released PvP arena match space is tracked here too (see <see cref="QueueCopyForDestructionWhenEmpty"/>):
        /// the entry is keyed by instance id and compared by reference, so it needs nothing run-specific.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, ACE.Server.Entity.Landblock> endedOccupiedCopies = new ConcurrentDictionary<uint, ACE.Server.Entity.Landblock>();

        /// <summary>
        /// What <see cref="QueueEmptiedEndedCopies"/> does with one tracked ended copy. Pure, for the unit tests.
        ///   - Drop: the instance no longer resolves to the tracked landblock (it unloaded, or - after an unload - the id
        ///     was re-issued to a new copy, which must never be queued on this run's behalf). Compared by REFERENCE.
        ///   - Wait: a player is still inside (an eviction teleport not yet landed, or a refused one).
        ///   - Queue: the copy is empty; queue it. The entry is kept until the copy is actually gone, so a queue that
        ///     is refused at the drain (Landblock.TryClaimForDestruction: someone arrived) is retried once it empties
        ///     again; a repeat queue of a copy already waiting is deduplicated by EphemeralUnloadThrottle.
        /// </summary>
        internal enum EndedCopyAction { Drop, Wait, Queue }

        internal static EndedCopyAction DecideEndedCopy(object tracked, object live, bool playerInside)
        {
            if (tracked == null || !ReferenceEquals(tracked, live))
                return EndedCopyAction.Drop;

            return playerInside ? EndedCopyAction.Wait : EndedCopyAction.Queue;
        }

        /// <summary>Test seam: is an ended copy being watched for emptiness under this instance id?</summary>
        internal static bool IsWatchingEndedCopy(uint instance) => endedOccupiedCopies.ContainsKey(instance);

        /// <summary>
        /// World thread, every 1 s tick: queue each tracked ended copy for destruction once no player is left in it.
        /// The occupancy read is the same GetAllWorldObjectsForDiagnostics scan the reap pass and EndRun already use
        /// from this thread; it does not see a player still staged in pendingAdditions, and it does not need to,
        /// because the drain's TryClaimForDestruction flushes pending additions and refuses an occupied copy.
        /// </summary>
        private static void QueueEmptiedEndedCopies()
        {
            if (endedOccupiedCopies.IsEmpty)
                return;

            foreach (var entry in endedOccupiedCopies)
            {
                var live = LandblockManager.GetEphemeralLandblock(entry.Key);
                var playerInside = ReferenceEquals(live, entry.Value) && entry.Value.GetAllWorldObjectsForDiagnostics().OfType<Player>().Any();

                switch (DecideEndedCopy(entry.Value, live, playerInside))
                {
                    case EndedCopyAction.Drop:
                        endedOccupiedCopies.TryRemove(entry);
                        break;

                    case EndedCopyAction.Queue:
                        LandblockManager.AddToDestructionQueue(entry.Value);
                        break;
                }
            }
        }

        /// <summary>
        /// Two cadences, one pass. The tick is 1 s because a Starting run must be populated as soon as its
        /// copy finishes loading - the player is standing in it waiting - while the reaping decision (TTL,
        /// landblock gone, load timeout) is measured in minutes and does not need to be re-taken every
        /// second, so it keeps its own 15 s timer.
        /// </summary>
        private static DateTime nextTick = DateTime.MinValue;
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private static DateTime nextReap = DateTime.MinValue;
        private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(15);

        /// <summary>How often the puzzle fail ledger drops expired keys and prunes its table (ThreadPuzzleIpLedger.Sweep).</summary>
        private static readonly TimeSpan PuzzleLedgerSweepInterval = TimeSpan.FromMinutes(10);
        private static DateTime nextPuzzleLedgerSweep = DateTime.MinValue;

        /// <summary>
        /// The puzzle fail policy's world-thread share (ThreadPuzzleFailPolicy). On the reap cadence (and so on the
        /// very first tick) it re-reads the policy settings and household allowlists into the snapshot the landblock
        /// threads read; every tick it drains queued lockout sweeps, so a removal lands within about a second of the
        /// pull that triggered it; every <see cref="PuzzleLedgerSweepInterval"/> it sweeps the ledger. Guarded: none of
        /// it may stop the run loop.
        /// </summary>
        private static void TickPuzzleFailPolicy(DateTime now, bool refreshConfig)
        {
            if (refreshConfig)
            {
                try
                {
                    ThreadPuzzlePolicyConfig.Refresh();
                }
                catch (Exception ex)
                {
                    log.Error("[PUZZLE_POLICY] settings refresh threw; keeping the previous snapshot", ex);
                }
            }

            try
            {
                ThreadPuzzleFailPolicy.DrainSweeps(now);

                if (now >= nextPuzzleLedgerSweep)
                {
                    nextPuzzleLedgerSweep = now + PuzzleLedgerSweepInterval;
                    var dropped = ThreadPuzzleIpLedger.Shared.Sweep(now);

                    if (dropped > 0)
                        log.Debug($"[PUZZLE_POLICY] ledger sweep dropped {dropped} expired key(s)");
                }
            }
            catch (Exception ex)
            {
                log.Error("[PUZZLE_POLICY] world-thread tick threw", ex);
            }
        }

        /// <summary>
        /// Serialises the admission sequence in TryStart. Held only across the cap checks, the instance-id
        /// allocation and the registry insert - never across GetNewThreadDungeonLandblock, which loads a
        /// landblock and must not run under a static lock.
        /// </summary>
        private static readonly object startLock = new object();

        /// <summary>
        /// How long a run may sit in Starting with its landblock loaded but CreateWorldObjects unfinished
        /// before it is reaped. The seam in Landblock.CreateWorldObjects rethrows on a filter failure, which
        /// leaves CreateWorldObjectsCompleted false forever; a player teleporting in then retries
        /// materialisation on a 0.1s chain indefinitely and stays in pink bubbles (Player_Location.cs:906-917).
        /// </summary>
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long after a run reaches Cleared it may still be reaped for standing empty. The run ends on the
        /// first reap pass that finds BOTH no player inside the copy AND this much time elapsed since the
        /// clear; either condition alone keeps it alive.
        ///
        /// 30 seconds, chosen for the one case where "nobody is inside" is a lie rather than a fact: entry is
        /// asynchronous. A run whose plan placed nothing is Cleared the instant it is populated - which can be
        /// before its owner has materialised - and the teleport in can defer a second behind a fog chain on
        /// the player's own landblock queue (Player_Location.cs:798-807) and then retry materialisation on a
        /// 0.1s chain. A player who is inside and looting is covered by the player probe on its own and needs
        /// no grace at all; this window exists so a player who is on their way in is never met with a
        /// collapsing dungeon. It is an order of magnitude longer than the entry path needs and still an order
        /// of magnitude shorter than the five minutes an empty copy of a Cleared run would otherwise idle for
        /// (the empty grace applies only while a run is Starting or Active, so a Cleared run's copy is on
        /// Landblock.UnloadInterval - except a group run on the loot hold, which this grace does not reap).
        ///
        /// Not a PropertyManager tunable, on purpose: ShouldEnd is pure (see its doc comment) and every input
        /// it needs is passed in.
        /// </summary>
        private static readonly TimeSpan ClearedGrace = TimeSpan.FromSeconds(30);

        /// <summary>
        /// dynamic_dungeons_empty_grace_minutes: its default, and the clamp applied at read. The floor is the
        /// ordinary landblock UnloadInterval, so the tunable can lengthen a live copy's empty window but never
        /// make it shorter than any other landblock's.
        /// </summary>
        internal const long EmptyGraceMinutesDefault = 10;
        internal const long EmptyGraceMinutesMin = 5;
        internal const long EmptyGraceMinutesMax = 60;

        /// <summary>
        /// dynamic_dungeons_cache_roll_batch_size: the clamp applied at read. The floor of 1 keeps every step
        /// making progress; the ceiling is a sanity bound, not a tuned value - at 1,000 rolls a step the batching
        /// is effectively off again and one step would stall the world exactly as the unbatched pass did.
        /// </summary>
        internal const long CacheRollBatchMin = 1;
        internal const long CacheRollBatchMax = 1000;

        /// <summary>
        /// dynamic_dungeons_cache_step_budget_ms: the clamp applied at read, [1, 250]. 1 ms still materialises one
        /// roll per step (progress is guaranteed); 250 ms is a sanity bound at which the roll cap alone decides a
        /// step, which is the #1210 behaviour (the control setting for a live comparison).
        /// </summary>
        internal const long CacheStepBudgetMsMin = ThreadLootRollBudget.MinStepBudgetMs;
        internal const long CacheStepBudgetMsMax = ThreadLootRollBudget.MaxStepBudgetMs;

        public static ThreadDungeonStore Store { get; private set; } = ThreadDungeonStore.Empty;

        public static bool IsEnabled => PropertyManager.GetBool("dynamic_dungeons_enabled").Item;

        public static IReadOnlyList<ThreadDungeonRun> LiveRuns => runs.Values.Where(r => r.State != ThreadDungeonRunState.Ended).ToList();

        /// <summary>Runs not yet Ended, without the list copy: for the ace.threads.runs.live gauge on the scrape thread.</summary>
        public static int LiveRunCount => runs.Values.Count(r => r.State != ThreadDungeonRunState.Ended);

        /// <summary>
        /// Validate into a LOCAL, then publish once. Both entry points do this identically, and the ordering
        /// is load-bearing rather than stylistic: <see cref="ValidateWorldWcids"/> can DROP a salvage_affinity
        /// modifier whose base wcid does not resolve, and that drop is a write to the plain Dictionary backing
        /// <see cref="ThreadDungeonStore.Modifiers"/>.
        ///
        /// <see cref="Reload"/> runs live from an admin command while runs are in flight, and Modifiers is read
        /// unsynchronized from several other threads by then - the world thread through
        /// ThreadDungeonSpawner.TryPopulate into DungeonPopulationBuilder.Build, a landblock action-chain
        /// thread through the gem's ActOnUse, and the appraisal path in ThreadDungeonGemHandler.
        /// Dictionary&lt;TKey,TValue&gt; guarantees nothing under a concurrent write: it can throw or return
        /// corrupted data. Assigning Store first and validating afterwards would open exactly that window on
        /// every reload that happened to find an unresolvable row.
        ///
        /// Keeping the assignment LAST closes it entirely, because nothing else can hold a reference to the
        /// new store until the only thread that can mutate it has finished.
        /// </summary>
        public static void Initialize()
        {
            var store = ThreadDungeonStore.LoadFromServerConfig();
            ValidateBossWeenies(store);
            ValidateWorldWcids(store);
            Store = store;

            // The clearance rows this store carries are an input to the population-plan cache's fit projection
            // (ThreadPlanCache), and unlike the species tables they are not part of its key - the key names the
            // clearance HEIGHT, which a reload can change for the same dungeon id. Drop the cache here rather
            // than key on the store reference as well, because a height edit and a roster edit are two
            // different reloads and only one of them swaps the dictionary the key holds.
            ThreadPlanCache.Invalidate();
        }

        /// <summary>Reloads the store from disk. Same validate-then-publish ordering as <see cref="Initialize"/>, and for the reason given there.</summary>
        public static void Reload()
        {
            var store = ThreadDungeonStore.LoadFromServerConfig();
            ValidateBossWeenies(store);
            ValidateWorldWcids(store);
            Store = store;

            // See Initialize: a reloaded clearance height is not in the plan cache's key.
            ThreadPlanCache.Invalidate();
        }

        /// <summary>
        /// Logs one WARN per attunement.json component and per salvage_affinity modifier whose wcid has no
        /// weenie in the world database. A no-op when DatabaseManager.World is null (unit-test harness),
        /// which is also why a missing salvage base cannot be checked at parse time. See
        /// ThreadDungeonStore.ValidateWorldWcids for which of the two is dropped and which is only reported.
        /// </summary>
        private static void ValidateWorldWcids(ThreadDungeonStore store)
        {
            // Deliberately NOT also gated on store.Attunement: the salvage half of this check has nothing to
            // do with attunement, and gating on it would silently skip the modifier sweep whenever
            // attunement.json failed to load.
            if (DatabaseManager.World == null || store == null)
                return;

            foreach (var missing in store.ValidateWorldWcids(wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null))
                log.Warn($"[DYNDUNGEON] {missing}");
        }

        /// <summary>
        /// Diagnostic only: logs one warning per bosses.json entry whose weenie cannot actually be killed by a
        /// player (missing, not a Creature, Attackable false, or PlayerKillerStatus.NPC). It does not filter
        /// Store.Bosses - DungeonPopulationBuilder still draws from the full list - because
        /// ThreadDungeonSpawner.TryPlace refuses such a creature at spawn time and reports bossWcid 0 to
        /// MarkPopulated, which already keeps the run clearable. This pass exists so a bad bosses.json entry is
        /// caught in the log at load time rather than only discovered the next time that entry's level window
        /// happens to be drawn.
        ///
        /// A no-op when DatabaseManager.World is null, which is the case under the unit-test harness.
        /// </summary>
        private static void ValidateBossWeenies(ThreadDungeonStore store)
        {
            if (DatabaseManager.World == null || store?.Bosses == null)
                return;

            foreach (var boss in store.Bosses)
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(boss.Wcid);

                if (weenie == null)
                {
                    log.Warn($"[DYNDUNGEON] bosses.json wcid {boss.Wcid} '{boss.Name}': no such weenie in the world database");
                    continue;
                }

                if (weenie.WeenieType != WeenieType.Creature)
                {
                    log.Warn($"[DYNDUNGEON] bosses.json wcid {boss.Wcid} '{boss.Name}': weenie type is {weenie.WeenieType}, not Creature");
                    continue;
                }

                var attackable = weenie.GetProperty(PropertyBool.Attackable) ?? true;
                var playerKillerStatus = (PlayerKillerStatus?)weenie.GetProperty(PropertyInt.PlayerKillerStatus) ?? PlayerKillerStatus.NPK;

                if (!DungeonPopulationBuilder.IsKillable(attackable, playerKillerStatus))
                {
                    var reason = !attackable ? "not attackable" : "flagged PlayerKillerStatus.NPC";
                    log.Warn($"[DYNDUNGEON] bosses.json wcid {boss.Wcid} '{boss.Name}': {reason}");
                }
            }
        }

        public static ThreadDungeonRun GetRun(uint instance)
            => runs.TryGetValue(instance, out var run) && run.State != ThreadDungeonRunState.Ended ? run : null;

        /// <summary>
        /// The owner's non-Ended run, or null. Normally there is at most one. The exception is a Cleared GROUP run
        /// (see <see cref="IsHeldGroupRun"/>): it survives its owner's next start, so the owner can have more than
        /// one. The Starting or Active run is preferred, then the newest Cleared one, so the
        /// answer is deterministic instead of depending on dictionary order. A solo owner never has two, so solo
        /// behaviour is unchanged.
        /// </summary>
        public static ThreadDungeonRun GetRunForOwner(uint ownerGuid)
            => GetRunForOwnerCore(runs.Values, ownerGuid);

        /// <summary>The pure half of <see cref="GetRunForOwner"/>, over any run set, for the tests.</summary>
        internal static ThreadDungeonRun GetRunForOwnerCore(IEnumerable<ThreadDungeonRun> candidates, uint ownerGuid)
        {
            if (candidates == null)
                return null;

            ThreadDungeonRun live = null;
            ThreadDungeonRun cleared = null;

            foreach (var run in candidates)
            {
                if (run == null || run.OwnerGuid != ownerGuid)
                    continue;

                var state = run.State;

                if (state == ThreadDungeonRunState.Starting || state == ThreadDungeonRunState.Active)
                {
                    if (live == null || run.StartedUtc > live.StartedUtc)
                        live = run;
                }
                else if (state == ThreadDungeonRunState.Cleared)
                {
                    if (cleared == null || run.StartedUtc > cleared.StartedUtc)
                        cleared = run;
                }
            }

            return live ?? cleared;
        }

        public const string OwnerRunOpenReason = "You already have a dungeon open. Give its gem to the Fragment Press to close it.";
        public const string HeldGroupRunLimitReason = "Your last group Thread is still open while your fellowship collects its loot. You can open a new one when it closes.";
        public const string OnLiveRosterReason = "You are already part of an open Thread.";
        public const string ServerBusyReason = "The dungeon gates are busy. Try again in a few minutes.";
        public const string AccountRunOpenReason = "Your account already has a dungeon open.";

        /// <summary>
        /// How many Cleared GROUP runs an owner (and, separately, an account) may keep open alongside a new start.
        /// One: a group can always open its next Thread while the last one's members finish looting, but cannot
        /// stack held copies and fill the server.
        /// </summary>
        internal const int HeldGroupRunAllowance = 1;

        /// <summary>
        /// A Cleared GROUP run. Members may still be inside looting placed caches, so the owner's next start never
        /// ends it (ruling R29 as amended in review): it ends only through the timed reap or its TTL. A solo run is
        /// never held.
        /// </summary>
        internal static bool IsHeldGroupRun(ThreadDungeonRunState state, bool isGroup)
            => state == ThreadDungeonRunState.Cleared && isGroup;

        /// <summary>Does the owner's next start end this run (EndClearedRunsForOwner)? Every Cleared SOLO run, exactly as before Group Threads.</summary>
        internal static bool EndsAtOwnersNextStart(ThreadDungeonRunState state, bool isGroup)
            => state == ThreadDungeonRunState.Cleared && !isGroup;

        /// <summary>
        /// Does this run refuse its owner's next start with <see cref="OwnerRunOpenReason"/>? Every non-Ended run
        /// except a held group run, which is governed by <see cref="HeldGroupRunAllowance"/> instead.
        /// </summary>
        internal static bool BlocksOwnersNextStart(ThreadDungeonRunState state, bool isGroup)
            => state != ThreadDungeonRunState.Ended && !IsHeldGroupRun(state, isGroup);

        /// <summary>Does this run count toward dynamic_dungeons_max_concurrent_runs? Every non-Ended run, held or not.</summary>
        internal static bool CountsTowardServerCap(ThreadDungeonRunState state)
            => state != ThreadDungeonRunState.Ended;

        /// <summary>The registry counts the start gates are decided on. Plain values, gathered in one pass.</summary>
        internal readonly struct StartGateCounts
        {
            public StartGateCounts(int ownerBlocking, int ownerHeld, int serverLive, int accountLive, int accountHeld)
            {
                OwnerBlocking = ownerBlocking;
                OwnerHeld = ownerHeld;
                ServerLive = serverLive;
                AccountLive = accountLive;
                AccountHeld = accountHeld;
            }

            /// <summary>The owner's runs for which <see cref="BlocksOwnersNextStart"/> is true.</summary>
            public int OwnerBlocking { get; }
            /// <summary>The owner's held group runs (<see cref="IsHeldGroupRun"/>).</summary>
            public int OwnerHeld { get; }
            /// <summary>Runs for which <see cref="CountsTowardServerCap"/> is true.</summary>
            public int ServerLive { get; }
            /// <summary>The account's non-Ended runs.</summary>
            public int AccountLive { get; }
            /// <summary>The account's held group runs.</summary>
            public int AccountHeld { get; }
        }

        /// <summary>
        /// One pass over (owner, account, state, isGroup) tuples. The state is read by the caller once per run, so a
        /// run moving mid-pass is counted under one state only.
        /// </summary>
        internal static StartGateCounts CountStartGates(IEnumerable<(uint OwnerGuid, uint AccountId, ThreadDungeonRunState State, bool IsGroup)> runStates, uint ownerGuid, uint accountId)
        {
            int ownerBlocking = 0, ownerHeld = 0, serverLive = 0, accountLive = 0, accountHeld = 0;

            if (runStates != null)
            {
                foreach (var r in runStates)
                {
                    var held = IsHeldGroupRun(r.State, r.IsGroup);

                    if (r.OwnerGuid == ownerGuid)
                    {
                        if (BlocksOwnersNextStart(r.State, r.IsGroup)) ownerBlocking++;
                        if (held) ownerHeld++;
                    }

                    if (CountsTowardServerCap(r.State)) serverLive++;

                    if (r.AccountId == accountId && r.State != ThreadDungeonRunState.Ended)
                    {
                        accountLive++;
                        if (held) accountHeld++;
                    }
                }
            }

            return new StartGateCounts(ownerBlocking, ownerHeld, serverLive, accountLive, accountHeld);
        }

        /// <summary>
        /// The start gates, in order, as one pure decision:
        ///   1. the owner has a run that blocks (<see cref="BlocksOwnersNextStart"/>) -> <see cref="OwnerRunOpenReason"/>;
        ///   2. the owner already holds more than <see cref="HeldGroupRunAllowance"/> Cleared group runs -> <see cref="HeldGroupRunLimitReason"/>;
        ///   3. the owner is on a live roster (ruling R12) -> <see cref="OnLiveRosterReason"/>;
        ///   4. the server cap counts every non-Ended run -> <see cref="ServerBusyReason"/>;
        ///   5. the per-account cap counts every non-Ended run of the account except up to
        ///      <see cref="HeldGroupRunAllowance"/> held group runs -> <see cref="AccountRunOpenReason"/>.
        /// A cap of 0 or less is off, as before. With no group runs anywhere this is exactly the pre-Group-Threads
        /// rule plus the R12 roster check.
        /// </summary>
        internal static bool PassesStartGates(StartGateCounts counts, bool ownerOnLiveRoster, long maxRuns, long perAccount, out string reason)
        {
            reason = null;

            if (counts.OwnerBlocking > 0) { reason = OwnerRunOpenReason; return false; }
            if (counts.OwnerHeld > HeldGroupRunAllowance) { reason = HeldGroupRunLimitReason; return false; }
            if (ownerOnLiveRoster) { reason = OnLiveRosterReason; return false; }
            if (maxRuns > 0 && counts.ServerLive >= maxRuns) { reason = ServerBusyReason; return false; }

            var accountCounted = counts.AccountLive - Math.Min(counts.AccountHeld, HeldGroupRunAllowance);
            if (perAccount > 0 && accountCounted >= perAccount) { reason = AccountRunOpenReason; return false; }

            return true;
        }

        /// <summary>
        /// Group Threads: admits the non-owner seats of an ordered roster (<see cref="ThreadDungeonRun.OrderRoster"/>).
        /// The owner (index 0) is always kept. A non-owner is dropped when <paramref name="onLiveRoster"/> says they
        /// are already on a live roster (ruling R12), or when the kept roster has reached
        /// <see cref="GroupScaling.MaxRosterSize"/> (unreachable through a fellowship, whose own cap is the same 100:
        /// the snapshot clamps N there, and the run constructor refuses a roster the snapshot does not describe).
        /// Dropped names come back in roster order. Empty lists for an empty roster.
        /// </summary>
        internal static (List<RosterSeat> Kept, List<string> Dropped) AdmitSeats(IReadOnlyList<RosterSeat> ordered, Func<uint, bool> onLiveRoster)
        {
            var kept = new List<RosterSeat>();
            var dropped = new List<string>();

            if (ordered == null || ordered.Count == 0)
                return (kept, dropped);

            kept.Add(ordered[0]);

            for (var i = 1; i < ordered.Count; i++)
            {
                var seat = ordered[i];

                if (kept.Count >= GroupScaling.MaxRosterSize || (onLiveRoster != null && onLiveRoster(seat.Guid)))
                    dropped.Add(seat.Name);
                else
                    kept.Add(seat);
            }

            return (kept, dropped);
        }
        /// <summary>
        /// The run a roster member belongs to (Group Threads ruling R12), or null. One character can sit on at most
        /// one LIVE roster, but can still be on a Cleared roster (their released run) while a newer one is live,
        /// so ties break in this order:
        ///   1. the run whose instance <paramref name="currentInstance"/> the player is standing in (0 = none);
        ///   2. a Starting or Active run;
        ///   3. the newest Cleared run.
        /// Without that order a Cleared run would shadow its member's live one until it was reaped.
        /// </summary>
        public static ThreadDungeonRun GetRunForMember(uint guid, uint currentInstance = 0)
            => GetRunForMemberCore(runs.Values, guid, currentInstance);

        /// <summary>
        /// Every non-Ended run with <paramref name="guid"/> on its roster (a live one and any Cleared ones it still
        /// holds). The puzzle fail policy's sweep removes the character from all of them (ThreadPuzzleFailPolicy).
        /// </summary>
        internal static List<ThreadDungeonRun> LiveRunsForMember(uint guid)
            => runs.Values.Where(r => r != null && r.IsRosterMember(guid) && r.State != ThreadDungeonRunState.Ended).ToList();

        /// <summary>The pure half of <see cref="GetRunForMember"/>, over any run set, for the tests.</summary>
        internal static ThreadDungeonRun GetRunForMemberCore(IEnumerable<ThreadDungeonRun> candidates, uint guid, uint currentInstance)
        {
            if (candidates == null)
                return null;

            ThreadDungeonRun live = null;
            ThreadDungeonRun cleared = null;

            foreach (var run in candidates)
            {
                if (run == null || !run.IsRosterMember(guid))
                    continue;

                // One read of the state per run: it can move under us, and the tie-break must use one value.
                var state = run.State;
                if (state == ThreadDungeonRunState.Ended)
                    continue;

                if (currentInstance != 0 && run.Instance == currentInstance)
                    return run;

                if (state == ThreadDungeonRunState.Starting || state == ThreadDungeonRunState.Active)
                {
                    if (live == null || run.StartedUtc > live.StartedUtc)
                        live = run;
                }
                else if (state == ThreadDungeonRunState.Cleared)
                {
                    if (cleared == null || run.StartedUtc > cleared.StartedUtc)
                        cleared = run;
                }
            }

            return live ?? cleared;
        }

        /// <summary>
        /// Ruling R12: is this character on the roster of a run in Starting or Active? Owner or member alike. A
        /// Cleared run releases its roster, so its members may join or open another Thread.
        /// </summary>
        public static bool IsOnLiveRoster(uint guid) => IsOnLiveRosterCore(runs.Values, guid);

        /// <summary>The pure half of <see cref="IsOnLiveRoster"/>, over any run set, for the tests.</summary>
        internal static bool IsOnLiveRosterCore(IEnumerable<ThreadDungeonRun> runs, uint guid)
        {
            if (runs == null)
                return false;

            foreach (var run in runs)
            {
                if (run == null || !run.IsRosterMember(guid))
                    continue;

                var state = run.State;
                if (state == ThreadDungeonRunState.Starting || state == ThreadDungeonRunState.Active)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Group Threads are offered only when both the group switch and pooled loot are on (ruling R13: group
        /// runs are always pooled, because personal piles exist only in the pooled model).
        /// </summary>
        public static bool GroupModeEnabled
            => PropertyManager.GetBool("dynamic_dungeons_group_enabled", true).Item
               && PropertyManager.GetBool("dynamic_dungeons_pooled_loot_enabled", true).Item;

        /// <summary>
        /// The lock-time scaling snapshot for a roster of <paramref name="rosterSize"/> (ruling R28). Every group
        /// key is read with its compiled default as the explicit fallback; the plateau is read exactly as
        /// Fellowship.SplitXp reads it, so T(N) matches the split the XP will actually go through. A roster of one
        /// returns <see cref="GroupScaling.Solo"/> without reading anything.
        /// </summary>
        public static GroupScaling ReadGroupScaling(int rosterSize)
        {
            if (rosterSize <= 1)
                return GroupScaling.Solo;

            return GroupTunables.Read().Compute(rosterSize);
        }

        /// <summary>
        /// The raw group tunables, read once. Split from <see cref="ReadGroupScaling"/> so TryStart can read them
        /// BEFORE taking startLock (a PropertyManager cache miss is a database read) and compute the snapshot inside
        /// it, once the kept roster size is known.
        /// </summary>
        private readonly struct GroupTunables
        {
            private readonly double effortPerMember;
            private readonly double countShare;
            private readonly long groupMaxMonsters;
            private readonly long damageRatingPerMember;
            private readonly long damageRatingCap;
            private readonly double rewardBonusPerMember;
            private readonly double rewardBonusCap;
            private readonly double sharePlateau;
            private readonly long lootHoldMinutes;

            private GroupTunables(double effortPerMember, double countShare, long groupMaxMonsters, long damageRatingPerMember,
                long damageRatingCap, double rewardBonusPerMember, double rewardBonusCap, double sharePlateau, long lootHoldMinutes)
            {
                this.effortPerMember = effortPerMember;
                this.countShare = countShare;
                this.groupMaxMonsters = groupMaxMonsters;
                this.damageRatingPerMember = damageRatingPerMember;
                this.damageRatingCap = damageRatingCap;
                this.rewardBonusPerMember = rewardBonusPerMember;
                this.rewardBonusCap = rewardBonusCap;
                this.sharePlateau = sharePlateau;
                this.lootHoldMinutes = lootHoldMinutes;
            }

            public static GroupTunables Read() => new GroupTunables(
                PropertyManager.GetDouble("dynamic_dungeons_group_effort_per_member", GroupScaling.DefaultEffortPerMember).Item,
                PropertyManager.GetDouble("dynamic_dungeons_group_count_share", GroupScaling.DefaultCountShare).Item,
                PropertyManager.GetLong("dynamic_dungeons_group_max_monsters_per_run", GroupScaling.DefaultGroupMaxMonsters).Item,
                PropertyManager.GetLong("dynamic_dungeons_group_damage_rating_per_member", GroupScaling.DefaultDamageRatingPerMember).Item,
                PropertyManager.GetLong("dynamic_dungeons_group_damage_rating_cap", GroupScaling.DefaultDamageRatingCap).Item,
                PropertyManager.GetDouble("dynamic_dungeons_group_reward_bonus_per_member", GroupScaling.DefaultRewardBonusPerMember).Item,
                PropertyManager.GetDouble("dynamic_dungeons_group_reward_bonus_cap", GroupScaling.DefaultRewardBonusCap).Item,
                PropertyManager.GetDouble("fellowship_share_group_plateau").Item,
                PropertyManager.GetLong("dynamic_dungeons_group_loot_hold_minutes", GroupScaling.DefaultLootHoldMinutes).Item);

            public GroupScaling Compute(int rosterSize) => GroupScaling.Compute(rosterSize, effortPerMember, countShare, groupMaxMonsters,
                damageRatingPerMember, damageRatingCap, rewardBonusPerMember, rewardBonusCap, sharePlateau, lootHoldMinutes);
        }

        /// <summary>
        /// Starting a run implicitly ends the owner's own FINISHED one (owner ruling, 2026-09-06: "This
        /// should not need to be manually managed by the player"). A Cleared run has already destroyed its
        /// gem (AnnounceCleared -> GemDestroyer, ruling A1), so there is nothing left for the player to spend
        /// or hand back and no way for them to re-enter it - but the copy itself lingers, and while it does
        /// the start gate below refuses their next dungeon with "You already have a dungeon open".
        ///
        /// Reaping it AT THE MOMENT THEY ACT is strictly better than either alternative. Letting a Cleared
        /// run through the gate would leave a player free to chain-open copies faster than they reap and
        /// occupy several of the server-wide slots at once; making them wait for the automatic reap is the
        /// very thing the ruling forbids. This way the player never waits AND the per-owner footprint is
        /// pinned at exactly one loaded run.
        ///
        /// Scoped to the OWNER, never to the account, and that asymmetry is load-bearing. A gem cannot be
        /// used from inside an ephemeral realm (DungeonGemRules.RefusesFromInstance), so the caller is
        /// provably not standing in the run being ended, and a SOLO copy admits nobody but its owner
        /// (DungeonGemRules.DecideReEntry) so nobody else can be in there either. Sweeping the whole ACCOUNT
        /// would carry no such proof: it could collapse a copy around a DIFFERENT character of the same
        /// account who is still inside looting. That case is left to the timed reap in ShouldEnd, which does
        /// probe for players first.
        ///
        /// Group Threads: that proof holds for SOLO runs only. A GROUP copy admits every roster member (ruling
        /// R10), so members may still be inside looting placed caches when the owner, standing outside, opens
        /// their next gem. This sweep therefore NEVER ends a Cleared group run, whatever it owes
        /// (<see cref="EndsAtOwnersNextStart"/>); such a run ends only through the timed reap or its TTL, and
        /// <see cref="HeldGroupRunAllowance"/> bounds how many an owner can keep open. Solo runs are swept exactly
        /// as before.
        /// </summary>
        private static void EndClearedRunsForOwner(uint ownerGuid)
        {
            foreach (var stale in runs.Values.Where(r => r.OwnerGuid == ownerGuid && EndsAtOwnersNextStart(r.State, r.IsGroup)).ToList())
                EndRun(stale, DungeonRunTelemetry.EndReasons.Superseded);
        }

        /// <summary>
        /// Opens the private copy. On success the run is registered, the landblock is loading (Init runs async;
        /// the next Tick that sees CreateWorldObjectsCompleted hands it to ThreadDungeonSpawner.TryPopulate)
        /// and run.EntryPosition / run.ExitTo are set.
        /// Every refusal returns false with a player-facing reason and registers nothing.
        ///
        /// Order matters: every refusal is decided BEFORE the ephemeral instance id is allocated, so a refused
        /// start leaks neither an instance id nor a registry entry, and the caller's gem is never consumed.
        /// </summary>
        /// <param name="startGroup">
        /// Telemetry grouping for the runs one GEM USE produced (see ThreadDungeonRun.StartGroup). Null
        /// mints a fresh group, which is correct for every caller today - nothing retries, so each use is
        /// its own group of one and COUNT(DISTINCT start_group) equals the row count. A retry loop passes
        /// the SAME id into each of its attempts, and the two counts then diverge by exactly the fault rate.
        /// </param>
        public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon, out ThreadDungeonRun run, out string reason, string startGroup = null)
            => TryStart(owner, gem, spec, dungeon, null, out run, out reason, out _, startGroup);

        /// <summary>The owner's roster seat, captured from the Player at the moment of the start.</summary>
        private static RosterSeat OwnerSeat(Player owner)
            => new RosterSeat(owner.Guid.Full, owner.Name, owner.Account.AccountId, owner.Level ?? 0);

        /// <summary>
        /// The roster form of TryStart (Group Threads). Identical to the solo form above, which delegates here with
        /// no extra seats (a roster of one), plus the roster admission taken inside the same startLock critical
        /// section:
        ///   * an owner already on a live roster (someone else's Starting or Active run) is refused;
        ///   * a non-owner seat already on a live roster is dropped and named in <paramref name="droppedNames"/>;
        ///   * the group snapshot is computed for the KEPT roster size (tunables read before the lock).
        /// The per-account and server caps stay owner-only.
        ///
        /// The owner's seat is always built from <paramref name="owner"/>, once, after the IsEnabled, dungeon and
        /// entry checks (so a disabled server reads nothing from the Player); any seat in <paramref name="seats"/>
        /// carrying the owner's guid is ignored, so the caller cannot misstate the owner's level or account.
        /// <paramref name="seats"/> may be null.
        /// </summary>
        /// <param name="droppedNames">Names of non-owner seats dropped at admission, in roster order. Empty on refusal.</param>
        public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon,
            IReadOnlyList<RosterSeat> seats, out ThreadDungeonRun run, out string reason,
            out IReadOnlyList<string> droppedNames, string startGroup = null)
        {
            run = null;
            reason = null;
            droppedNames = Array.Empty<string>();

            if (!IsEnabled) { reason = "Threads are not enabled on this server."; return false; }
            if (dungeon == null || !dungeon.Enabled) { reason = "That dungeon is not currently available."; return false; }
            if (!TryParseEntry(dungeon.Entry, out var entry)) { reason = "That dungeon's entry point is misconfigured."; log.Error($"[DYNDUNGEON] dungeon {dungeon.Id} entry '{dungeon.Entry}' unparseable"); return false; }

            var maxRuns = (int)PropertyManager.GetLong("dynamic_dungeons_max_concurrent_runs").Item;
            var perAccount = (int)PropertyManager.GetLong("dynamic_dungeons_max_runs_per_account").Item;
            var ttl = TimeSpan.FromMinutes(Math.Max(5, PropertyManager.GetLong("dynamic_dungeons_run_ttl_minutes").Item));
            var clearFraction = Math.Clamp(PropertyManager.GetDouble("dynamic_dungeons_clear_fraction").Item, 0.0, 1.0);
            var bossWeight = Math.Clamp(PropertyManager.GetDouble("dynamic_dungeons_boss_clear_weight").Item, 0.0, 1.0);

            // The candidate roster: the owner's own seat first, then the offered seats (owner-guid repeats, nulls
            // and duplicates removed by the one roster-order definition). Formed outside the lock because it
            // reads nothing shared.
            // Puzzle fail policy backstop (ThreadPuzzleFailPolicy). The gem handler's use gates and roster formation
            // refuse a barred connection first; this catches a lockout that landed in between. A barred owner is
            // refused outright; a barred seat is dropped before admission, exactly as formation would have excluded
            // it. Reads only the ledger's memory and the policy snapshot, so it is safe on any thread.
            //
            // Not airtight, deliberately: a lockout landing between this check and the run's registration below opens
            // the run anyway, for at most about one world tick. The sweep that lockout queued drains on the next Tick
            // and removes every online character on the key from every live run, this new one included.
            var puzzleNow = DateTime.UtcNow;
            var ownerBarred = ThreadPuzzleFailPolicy.LockoutRemaining(owner.Session, puzzleNow);
            if (ownerBarred.HasValue) { reason = ACE.Server.PuzzleGates.PuzzleGateText.Barred(ownerBarred.Value); return false; }

            var candidateSeats = new List<RosterSeat> { OwnerSeat(owner) };
            if (seats != null)
            {
                foreach (var seat in seats)
                {
                    var seated = seat == null ? null : PlayerManager.GetOnlinePlayer(seat.Guid);

                    if (seated != null && seated != owner && ThreadPuzzleFailPolicy.LockoutRemaining(seated.Session, puzzleNow).HasValue)
                    {
                        log.Info($"[PUZZLE_POLICY] TryStart dropped barred seat {seat.Name} (0x{seat.Guid:X8}) from {owner.Name}'s start");
                        continue;
                    }

                    candidateSeats.Add(seat);
                }
            }
            var candidates = ThreadDungeonRun.OrderRoster(candidateSeats);

            // Group tunables are read BEFORE startLock (a cache miss is a database read); the snapshot is computed
            // inside it for the kept size (ruling R28). A roster of one reads nothing here: a solo run is never
            // held for loot (owner ruling, 2026-09-27), so none of the nine group dials, loot hold included, are
            // read for it.
            var groupTunables = candidates.Count > 1 ? GroupTunables.Read() : default;

            // Explicit fallback: an unseeded GetLong returns 0, which the clamp would silently turn into the
            // old 5 minutes rather than the intended 10.
            var emptyGrace = TimeSpan.FromMinutes(Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_empty_grace_minutes", EmptyGraceMinutesDefault).Item, EmptyGraceMinutesMin, EmptyGraceMinutesMax));

            // Read HERE, on the world thread with every other dial, and stamped on the run below: the delivery
            // batch steps that consume it run on a LANDBLOCK thread, where a PropertyManager read does not belong.
            // Same explicit-fallback reason as the empty grace above - an unseeded GetLong returns 0, and a batch
            // of 0 would be clamped up to 1 roll per world tick, which is far slower than the old single pass.
            var cacheRollBatch = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_cache_roll_batch_size", ThreadLootRollBudget.DefaultRollsPerStep).Item,
                CacheRollBatchMin, CacheRollBatchMax);

            // Same place, same reason, same explicit fallback: an unseeded 0 would clamp up to a 1 ms step.
            var cacheStepBudgetMs = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_cache_step_budget_ms", ThreadLootRollBudget.DefaultStepBudgetMs).Item,
                CacheStepBudgetMsMin, CacheStepBudgetMsMax);

            // Threads item 4, the loot trickle: same place, same reason. 0 is a meaningful setting here (trickle off),
            // so it is NOT clamped up; the explicit fallback still matters for an unseeded store.
            var lootTrickleBudgetMs = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_loot_trickle_budget_ms", ThreadLootTrickle.DefaultBudgetMs).Item,
                ThreadLootTrickle.MinBudgetMs, ThreadLootTrickle.MaxBudgetMs);

            var lootTrickleMaxPrebuilt = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_loot_trickle_max_prebuilt", ThreadLootTrickle.DefaultMaxPrebuilt).Item,
                ThreadLootTrickle.MinMaxPrebuilt, ThreadLootTrickle.MaxMaxPrebuilt);

            // Puzzle gates: same place, same reason - the puzzle pass runs on the run copy's LANDBLOCK thread. Explicit
            // fallbacks equal to the shipped defaults, so an unseeded store reads as shipped rather than as off/0.
            var puzzleGatesEnabled = PropertyManager.GetBool("dynamic_dungeons_puzzle_gates_enabled", true).Item;
            var puzzleGatesPerRun = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_puzzle_gates_per_run", 1).Item, 0, ThreadPuzzleSitePicker.MaxGatesPerRun);
            var puzzleRewardScene = PropertyManager.GetBool("dynamic_dungeons_puzzle_reward_scene_enabled", true).Item;

            uint instance;

            // Retire this owner's finished run before the gates below look at it. See EndClearedRunsForOwner.
            //
            // OUTSIDE startLock, deliberately. EndRun is not registry arithmetic: it evicts players, reaches
            // into LandblockManager to queue a copy for destruction and enqueues gem destruction onto the
            // owner's action queue. startLock is documented to cover only the cheap admission sequence and is
            // never held across a landblock lifecycle call (GetNewThreadDungeonLandblock is kept out of it
            // for the same reason), and widening it to span EndRun would put a static lock in front of
            // LandblockManager's own locking for every start. Nothing else in the process takes startLock, so
            // there is no deadlock to reason about either way - this is about not creating one later.
            //
            // The gap this leaves is benign and self-correcting. A run of this owner's would have to reach
            // Cleared between the sweep and the lock, and the owner cannot be the one clearing it: a gem is
            // refused from inside any ephemeral realm (DungeonGemRules.RefusesFromInstance), and a private
            // copy admits nobody but its owner, so with the caller standing outside there is no one left in
            // there to land the last kill. If it somehow happened, the gate below returns the ordinary
            // refusal and the player's very next attempt sweeps the run and succeeds - no stuck state, so a
            // retry loop here would buy nothing but a second EndRun call site.
            EndClearedRunsForOwner(owner.Guid.Full);

            // The admission sequence is one critical section. Every check below is a read of `runs` followed by
            // a write to it, and TryStart is not guaranteed to run on the world thread (a gem used from the
            // ground reaches ActOnUse through a landblock action chain), so two simultaneous gem uses could
            // otherwise both observe "one run below the cap" and both register. The lock covers the checks, the
            // id allocation and the insert; loading the landblock happens after it is released.
            lock (startLock)
            {
                // The gates, as one pure decision (PassesStartGates; its doc lists them in order). Every non-Ended
                // run counts toward the server cap. The sweep above ends every Cleared SOLO run first, so for a
                // solo player the owner and account gates stay the pre-Group-Threads "one run per owner" rule. A
                // Cleared GROUP run is never swept (members may be inside); instead the owner and the account may
                // each keep HeldGroupRunAllowance of them open alongside a new start, and a second one refuses.
                // The state of each run is read once for the whole decision.
                var gateCounts = CountStartGates(runs.Values.Select(r => (r.OwnerGuid, r.AccountId, r.State, r.IsGroup)), owner.Guid.Full, owner.Account.AccountId);
                if (!PassesStartGates(gateCounts, IsOnLiveRoster(owner.Guid.Full), maxRuns, perAccount, out reason))
                    return false;

                // Non-owner seats already on a live roster are dropped, not refused: the rest of the group still
                // opens. Decided under the lock, so two simultaneous starts cannot both admit the same character.
                var (kept, dropped) = AdmitSeats(candidates, IsOnLiveRoster);

                var group = kept.Count > 1 ? groupTunables.Compute(kept.Count) : GroupScaling.Solo;

                instance = LandblockManager.RequestNewEphemeralInstanceIDv1(0);

                // owner.Level is captured HERE (in the owner seat), not resolved at EndRun, because the level that
                // answers "was this gem appropriate for this player" is the level they walked in with. See
                // ThreadDungeonRun.OwnerLevelAtStart for why the end-time value is biased toward clears.
                run = new ThreadDungeonRun(instance, kept, gem.Guid.Full, spec, dungeon, DateTime.UtcNow, ttl, group, startGroup)
                {
                    ExitTo = new Position(owner.Location),
                    ClearFraction = clearFraction,
                    BossWeight = bossWeight,
                    FellowshipAtLock = owner.Fellowship,
                    EmptyGrace = emptyGrace,
                    CacheRollBatchSize = cacheRollBatch,
                    CacheStepBudgetMs = cacheStepBudgetMs,
                    LootTrickleBudgetMs = lootTrickleBudgetMs,
                    LootTrickleMaxPrebuilt = lootTrickleMaxPrebuilt,
                    PuzzleGatesEnabled = puzzleGatesEnabled,
                    PuzzleGatesPerRun = puzzleGatesPerRun,
                    PuzzleRewardSceneEnabled = puzzleRewardScene,
                };

                droppedNames = dropped;

                entry.Instance = instance;
                run.EntryPosition = entry;

                if (!runs.TryAdd(instance, run)) { reason = "The dungeon could not be opened."; run = null; droppedNames = Array.Empty<string>(); return false; }
            }

            // ace.threads.lb_create.duration: the whole creation, LandblockManager's lock wait and Landblock.Init
            // included, with the lock wait also reported on its own (LandblockManager.LastLockWaitMs is per thread and
            // describes the call that just returned on this one).
            var lbCreateStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var landblock = RealmManager.GetNewThreadDungeonLandblock(new LandblockId((uint)dungeon.Landblock << 16), owner, run, instance);
            ServerMetrics.RecordThreadsLandblockCreate(System.Diagnostics.Stopwatch.GetElapsedTime(lbCreateStart).TotalMilliseconds, LandblockManager.LastLockWaitMs);

            // TECH-DESIGN section 6 step 3d. A landblock with a traversable overworld is not a dungeon and must
            // never be instanced: the copy would be a private slice of the open world. Mirrors the guard
            // Portal.cs:569-573 applies to PortalInstancing. Curation should make this unreachable, so it is an
            // error rather than a warning, and the half-built copy is disposed of rather than left loaded.
            if (landblock == null || !landblock.IsDungeon)
            {
                log.Error($"[DYNDUNGEON] dungeon {dungeon.Id} landblock 0x{dungeon.Landblock:X4} is not a dungeon (or failed to load) - refusing to open a run");

                if (landblock != null)
                    LandblockManager.AddToDestructionQueue(landblock);

                runs.TryRemove(instance, out _);
                reason = "The dungeon could not be opened.";
                run = null;
                droppedNames = Array.Empty<string>();
                return false;
            }

            log.Info(run.IsGroup
                ? $"[DYNDUNGEON] started {run} spec={spec.Serialize()} {run.Group} roster={string.Join(",", run.Roster.Select(m => m.Name))} dropped={string.Join(",", droppedNames)}"
                : $"[DYNDUNGEON] started {run} spec={spec.Serialize()}");
            return true;
        }

        /// <summary>
        /// "0x0150018A 30 0 0.005 1 0 0 0" -> Position (instance 0; the caller stamps it).
        ///
        /// The tail is the /loc quaternion order qw qx qy qz, while the Position constructor
        /// (ACE.Entity/Position.cs:246) takes its rotation w LAST, so field 4 is passed after fields 5-7.
        /// </summary>
        public static bool TryParseEntry(string entry, out Position position)
        {
            position = null;
            if (string.IsNullOrWhiteSpace(entry)) return false;
            var p = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return false;
            var cellText = p[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p[0].Substring(2) : p[0];
            if (!uint.TryParse(cellText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var cell)) return false;
            float F(int i, float dflt) => i < p.Length && float.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;
            position = new Position(cell, F(1, 0), F(2, 0), F(3, 0), F(5, 0), F(6, 0), F(7, 0), F(4, 1), 0);
            return true;
        }

        public static void OnRunCreatureDied(Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager = null, ACE.Server.Entity.DamageHistoryInfo topDamager = null)
        {
            var run = creature.P_DungeonRun;
            if (run == null || !runs.TryGetValue(run.RunId, out var live) || !ReferenceEquals(live, run))
                return;

            run.RecordKill(creature.DungeonRole == DungeonRole.Boss);

            // The boss death, BEFORE the clear announcement. A pooled run only marks the boss bonus here
            // (MarkPooledBonus); the pooled delivery below forms the cache. The OFF path spawns the boss-death
            // cache (TrySpawnBossCache) at the boss's AUTHORED anchor, not at the corpse, so on the kill that does
            // both the player reads "A Thread Cache has formed where the boss fell." above "... is cleared and
            // your gem crumbles to dust." rather than below it. That cache's exactly-once is the run's (TryClaimBossChest).
            if (creature.DungeonRole == DungeonRole.Boss)
                DispatchBossDeath(run);

            AnnounceCleared(run);

            // Puzzle reward scene: the kill that would have cleared a SEALED run places the scene near the clearing
            // player and says where (once).
            AnnounceRewardArmed(run, ResolveClearingPlayer(run, creature, lastDamager, topDamager));

            // Pooled loot (spec section 4 trigger): the clearing kill and every later kill ask for delivery. After
            // AnnounceCleared so the clear line reads first; TriggerPooledLoot is a no-op until the run is Cleared.
            if (run.PooledLoot)
                PooledLootTrigger(run);

            // Ordered AFTER AnnounceCleared, and the order is load-bearing rather than cosmetic. RecordKill
            // has already run CheckClearedLocked, so the kill that finishes the run left Active before either
            // call; TryGetKillProgress reports nothing in that case and the clear line stands alone, which is
            // ruling R32's "the clear message is the only message" rule. Putting this first would still be
            // correct for the same reason, but reading the two calls in the order the player sees them is
            // what makes that obvious.
            AnnounceProgress(run);
        }

        /// <summary>
        /// Called by ThreadDungeonSpawner on the landblock action queue, immediately after the run's one
        /// MarkPopulated. Its only job today is the clear announcement: a plan that placed NOTHING (no
        /// eligible family, no boss, or a copy whose every placement failed) reaches Cleared the instant it
        /// is populated, and no creature death will ever arrive to announce it - the run would look broken
        /// to the player instead of empty.
        /// </summary>
        public static void OnRunPopulated(ThreadDungeonRun run)
        {
            if (run == null)
                return;

            AnnounceCleared(run);
            AnnounceRewardArmed(run, null);

            // A run whose last kills landed while it was still Starting is Cleared here, with loot already banked.
            if (run.PooledLoot)
                PooledLootTrigger(run);
        }

        /// <summary>
        /// The puzzle reward scene's seal was lifted (a solve, or a fail-open removal; ThreadDungeonRun.UnsealReward
        /// returned true to exactly one caller, which calls this). Does exactly what <see cref="OnRunPopulated"/>
        /// does: if lifting the seal let the run reach Cleared, the clear is announced (latched once by
        /// TryClaimClearedAnnouncement) and pooled loot is asked for. A seal lifted before the kills were done
        /// leaves the run Active, and both calls are no-ops; the clearing kill then announces as usual. Safe from a
        /// landblock thread or the world thread, like OnRunPopulated.
        /// </summary>
        internal static void OnRewardUnsealed(ThreadDungeonRun run)
        {
            if (run == null || run.State == ThreadDungeonRunState.Ended)
                return;

            OnRunPopulated(run);
        }

        /// <summary>
        /// The arming moment, once per run (TryClaimRewardArmedNotice): the run's kills are done but its reward is
        /// sealed. A run sealed PENDING gets its reward scene placed now, near <paramref name="clearing"/>
        /// (ThreadPuzzlePass.PlaceRewardAtArming, which unseals on any failure - the run then clears through the
        /// usual follow-up and no armed line is sent). Every recipient is then told where the scene is, measured
        /// from where THEY stand. Recipients as AnnounceCleared's.
        /// </summary>
        private static void AnnounceRewardArmed(ThreadDungeonRun run, Player clearing)
        {
            if (!run.TryClaimRewardArmedNotice())
                return;

            var placement = run.IsRewardPending
                ? RewardArmingPlacer(run, clearing)
                : run.RewardPuzzle as ACE.Server.PuzzleGates.PuzzleGatePlacement;

            // A failed arming placement already unsealed (and the run cleared): nothing is sealed, so say nothing.
            if (!run.IsRewardSealed)
                return;

            // One message object per recipient, built only once a recipient exists (as AnnounceCleared does).
            var recipients = run.IsGroup
                ? ThreadRunPresence.MessageRecipients(run)
                : new[] { PlayerManager.GetOnlinePlayer(run.OwnerGuid) }.Where(p => p != null).ToList();

            // Each member is measured from where THEY stand: their own walk (the closed gates of this run blocked), the
            // scene's height against theirs, and the boss-chamber clause decided once for the scene.
            var notice = ThreadPuzzlePass.BuildArmedNotice(run, placement);

            foreach (var member in recipients)
            {
                var line = notice.LineFor(IsInRunCopy(run, member), member.Location);
                member.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ACE.Entity.Enum.ChatMessageType.Broadcast));
            }

            log.Info($"[DYNDUNGEON] {run} kills done; reward sealed behind its puzzle");
        }

        /// <summary>
        /// Test seam: the arming placement. Production places through ThreadPuzzlePass.PlaceRewardAtArming from the
        /// clearing player's position (null: no player resolved, the placer falls back to the curated site).
        /// </summary>
        internal static Func<ThreadDungeonRun, Player, ACE.Server.PuzzleGates.PuzzleGatePlacement> RewardArmingPlacer = (run, player) =>
        {
            player ??= FallbackClearingPlayer(run);
            return ThreadPuzzlePass.PlaceRewardAtArming(run, player?.Location?.Pos, player?.Name, player?.Location?.Cell ?? 0);
        };

        /// <summary>Test seam: the online-player lookup the clearing-player chain uses. Production is PlayerManager.</summary>
        internal static Func<uint, Player> ClearingPlayerLookup = PlayerManager.GetOnlinePlayer;

        /// <summary>Stands in the run's copy: its instance and the dungeon's landblock (ThreadRunPresence's location clause).</summary>
        private static bool IsInRunCopy(ThreadDungeonRun run, Player player)
            => player?.Location != null
               && run.Dungeon != null
               && player.Location.Instance == run.Instance
               && player.Location.LandblockId.Landblock == run.Dungeon.Landblock;

        /// <summary>
        /// ThreadRunPresence.IsMemberInside over a guid, with the online check in front: the damage history holds
        /// its attackers through a WeakReference that can outlive a logout, so a candidate is looked up again here.
        /// </summary>
        private static Player EligibleClearer(ThreadDungeonRun run, uint guid)
        {
            var p = ClearingPlayerLookup(guid);
            return ThreadClearingPlayer.IsEligible(run, guid, p != null, p != null && IsInRunCopy(run, p)) ? p : null;
        }

        /// <summary>
        /// The player who cleared the run, for placing its reward scene (ThreadClearingPlayer: killing blow, its pet's
        /// owner, top damager, damagers by total damage, run owner, roster). Every candidate must be an online roster
        /// member inside the copy and not removed by the puzzle fail policy.
        /// </summary>
        private static Player ResolveClearingPlayer(ThreadDungeonRun run, Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager, ACE.Server.Entity.DamageHistoryInfo topDamager)
        {
            // Only the arming kill needs this; every other kill skips the damage-history walk.
            if (!run.IsRewardPending || !run.IsRewardArmed)
                return null;

            static uint? GuidOf(ACE.Server.Entity.DamageHistoryInfo info) => info?.Guid.Full;
            static uint? OwnerOf(ACE.Server.Entity.DamageHistoryInfo info) => info?.PetOwner != null ? info.TryGetPetOwner()?.Guid.Full : null;
            static uint? Credited(ACE.Server.Entity.DamageHistoryInfo info) => OwnerOf(info) ?? GuidOf(info);

            var killer = lastDamager ?? creature?.DamageHistory?.LastDamager;

            var damagers = creature?.DamageHistory?.Damagers?
                .Select(d => Credited(d) is uint g ? new ThreadClearingPlayer.Damager(g, d.TotalDamage) : (ThreadClearingPlayer.Damager?)null)
                .Where(d => d.HasValue)
                .Select(d => d.Value)
                .ToList();

            var candidates = ThreadClearingPlayer.Candidates(GuidOf(killer), OwnerOf(killer), Credited(topDamager), damagers, run.OwnerGuid, run.Roster.Select(m => m.Guid));
            var guid = ThreadClearingPlayer.Resolve(candidates, g => EligibleClearer(run, g) != null);

            return guid.HasValue ? EligibleClearer(run, guid.Value) : null;
        }

        /// <summary>The run owner if eligible, else the first eligible roster member (roster order), else null.</summary>
        private static Player FallbackClearingPlayer(ThreadDungeonRun run)
        {
            var candidates = ThreadClearingPlayer.Candidates(null, null, null, null, run.OwnerGuid, run.Roster.Select(m => m.Guid));
            var guid = ThreadClearingPlayer.Resolve(candidates, g => EligibleClearer(run, g) != null);

            return guid.HasValue ? EligibleClearer(run, guid.Value) : null;
        }

        /// <summary>
        /// Tells the owner their dungeon is clear, once. Both entry points reach this from a LANDBLOCK
        /// thread, never the world thread, which is fine: it only reads the run under its own lock and hands
        /// a message to the session's own network queue.
        ///
        /// Exactly-once is the run's job: TryClaimClearedAnnouncement latches under the run's own lock and
        /// returns true to one caller only, so the two entry points cannot both print the message.
        ///
        /// Group Threads. A GROUP run sends <see cref="GroupClearedMessage"/> (S7) to every message recipient
        /// (ThreadRunPresence.MessageRecipients: online members inside, in roster order), owner included, and
        /// runs the clear rewards once per recipient (ruling R26). Its owner gem and member keys are NOT destroyed
        /// here (ruling R29 as amended): members who died or stepped out still need them to return for their
        /// piles, so they die at run end through OnRunEnded. The solo path is unchanged.
        /// </summary>
        private static void AnnounceCleared(ThreadDungeonRun run)
        {
            if (!run.TryClaimClearedAnnouncement())
                return;

            if (run.IsGroup)
            {
                var recipients = ThreadRunPresence.MessageRecipients(run);

                log.Info($"[DYNDUNGEON] cleared {run} recipients={recipients.Count}");

                // Per recipient: the line first, then that member's effect and summoned exit, for the same
                // reason the solo path orders them (the effect lands on a message already read). The owner holds
                // the gem, everyone else a key, so the line names the right one.
                foreach (var member in recipients)
                {
                    var line = GroupClearedMessage(run.Dungeon.Name, member.Guid.Full == run.OwnerGuid);
                    member.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ACE.Entity.Enum.ChatMessageType.Broadcast));
                    ClearRewardHandler(run, member);
                }
            }
            else
            {
                var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
                owner?.Session?.Network.EnqueueSend(new GameMessageSystemChat($"{run.Dungeon.Name} is cleared and your gem crumbles to dust. Take the exit portal when you are ready; the dungeon stands for a while yet.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                log.Info($"[DYNDUNGEON] cleared {run}");

                // The completion visual and the summoned exit, owner-only. AFTER the clear line, so the effect
                // lands on a message the player has already read, and BEFORE the gem/survey bookkeeping, which is
                // invisible to them. Owner may be null (offline); the handler returns on that before it reads a
                // single tunable, which is what keeps AnnounceCleared callable from the unit tests.
                ClearRewardHandler(run, owner);
            }

            // Solo: the gem dies at the clear (owner ruling A1). Group: the gem and keys survive to run end (R29).
            if (!run.IsGroup)
                GemDestroyer(run);

            SurveyRecorder(run);

            // Thread-Guide seams, beside the survey and on the same pattern (swappable delegates, each player's
            // own action queue). The guide credit is the run owner's; the lifetime clear count is every survey
            // recipient's, guide run or not.
            GuideRecorder(run);
            ClearCounter(run);
        }

        /// <summary>
        /// The group clear line. S7 (a key) for a non-owner member; the owner, who holds the gem rather than a key,
        /// gets the same sentence naming the gem.
        /// </summary>
        internal static string GroupClearedMessage(string dungeonName, bool isOwner)
            => isOwner
                ? $"{dungeonName} is cleared. Your gem will let you return until this Thread closes."
                : $"{dungeonName} is cleared. Your key will let you return until this Thread closes.";

        /// <summary>S8, the one-time warning to a member inside a group run who is no longer in its fellowship.</summary>
        internal const string FellowshipLeftWarning = "You are no longer in this Thread's fellowship. Kill experience here is no longer shared with the group.";

        /// <summary>
        /// Test seam for the boss cache, same shape as <see cref="GemDestroyer"/> and
        /// <see cref="SurveyRecorder"/> below: a swappable delegate so a test can observe the call without a
        /// world. Production points at ThreadDungeonRewardSpawner.
        /// </summary>
        internal static Action<ThreadDungeonRun> BossCacheSpawner = ThreadDungeonRewardSpawner.TrySpawnBossCache;

        /// <summary>
        /// Test seam for the clear-time rewards - the completion visual and the summoned exit - on the same
        /// swappable-delegate pattern as <see cref="BossCacheSpawner"/>. The Player is passed rather than
        /// re-resolved so the handler cannot disagree with the owner AnnounceCleared just messaged.
        /// </summary>
        internal static Action<ThreadDungeonRun, Player> ClearRewardHandler = ThreadDungeonRewardSpawner.OnRunCleared;

        /// <summary>
        /// A boss death under either loot model (spec section 1). OFF: the boss-death cache at the anchor,
        /// exactly as before. ON: no cache; its contents become the pool's one-shot bonus.
        /// </summary>
        internal static void DispatchBossDeath(ThreadDungeonRun run)
        {
            if (run.PooledLoot)
                PooledBonusMarker(run);
            else
                BossCacheSpawner(run);
        }

        /// <summary>Test seam for pooling the boss bonus, same shape as <see cref="BossCacheSpawner"/>.</summary>
        internal static Action<ThreadDungeonRun> PooledBonusMarker = MarkPooledBonus;

        /// <summary>
        /// Test seam for requesting pooled-loot delivery, same swappable-delegate pattern as <see cref="GemDestroyer"/>.
        /// Returns the placer's outcome so <see cref="RetryWaitingDelivery"/> can tell a request that ran from one
        /// that found the latch held.
        /// </summary>
        internal static Func<ThreadDungeonRun, CacheRequestOutcome> PooledLootTrigger = TriggerPooledLoot;

        private static void MarkPooledBonus(ThreadDungeonRun run)
        {
            // User ruling 2026-09-14: in pooled mode the boss bonus (Trade Notes, salvage bags, Legendary-table
            // items) is ALWAYS added when the boss dies. The boss-chest switch governs only the OFF path's
            // boss-death cache (TrySpawnBossCache), so no switch is read here.
            if (run.TryMarkLootBonusPending())
                log.Info($"[DYNDUNGEON] {run} boss bonus pooled");
        }

        /// <summary>Asks for delivery when a Cleared pooled run has loot left. Safe from any thread: the placer latches.</summary>
        internal static CacheRequestOutcome TriggerPooledLoot(ThreadDungeonRun run)
        {
            if (run == null || !run.PooledLoot || run.State != ThreadDungeonRunState.Cleared || !run.HasUnclaimedLoot)
                return CacheRequestOutcome.NotApplicable;

            return ThreadCachePlacer.RequestPlacement(run, CacheRequestMode.Auto);
        }

        /// <summary>
        /// The Tick retry for a delivery that found the owner outside the copy (spec section 4). Claims the waiting
        /// flag and asks for delivery. A Busy outcome ran nothing - another delivery or move holds the run's latch -
        /// so the flag is put back and the next Tick tries again; without that the claim would silently drop the
        /// wait. Queued hands the wait to ThreadCachePlacer.Execute, which marks it again itself if the owner has
        /// left by the time it runs, and NotApplicable has nothing left to deliver.
        /// </summary>
        internal static void RetryWaitingDelivery(ThreadDungeonRun run)
        {
            if (!run.TryClaimPlacementWanted())
                return;

            if (PooledLootTrigger(run) == CacheRequestOutcome.Busy)
                run.MarkPlacementWanted();
        }

        /// <summary>
        /// Tells the owner where they stand after EVERY kill (owner ruling R32 as amended 2026-09-08:
        /// "After each kill, the player should see a message 'Monsters killed x/y. Boss still remaining'").
        /// This replaced the 25/50/75 "% remaining" milestones the first form of R32 shipped - the counts say
        /// the same thing without asking the player to convert a percentage back into corpses.
        ///
        /// y is the CLEAR TARGET, not the population: a run does not have to be emptied, so quoting the total
        /// placed would show a target the player never has to reach. See ThreadDungeonRun.TryGetKillProgress
        /// for why the boss is a separate sentence rather than a term in that fraction.
        ///
        /// Called only from OnRunCreatureDied, i.e. from the LANDBLOCK thread that recorded the kill, exactly
        /// as AnnounceCleared is - it reads the run only through the run's own lock and then hands a message
        /// to the session's own network queue, so nothing here touches the world thread. There is no
        /// OnRunPopulated entry point on purpose: a run that reaches Cleared on populate has no progress to
        /// narrate, and one that does not has had no kills to report.
        ///
        /// The line-per-kill discipline is the run's: TryGetKillProgress reports only while Active, so the
        /// kill that clears the run says nothing here and the clear message stands alone.
        /// </summary>
        private static void AnnounceProgress(ThreadDungeonRun run)
        {
            if (!run.TryGetKillProgress(out var killed, out var required, out var bossRemaining))
                return;

            var line = bossRemaining
                ? $"Monsters killed {killed}/{required}. Boss still remaining."
                : $"Monsters killed {killed}/{required}.";

            // Invariant 5: a solo run's only recipient is its online owner (no inside test, as before); a group
            // run's are the online members inside, in roster order.
            foreach (var recipient in ThreadRunPresence.MessageRecipients(run))
                recipient.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ACE.Entity.Enum.ChatMessageType.Broadcast));

            // Debug: one line per kill is most of a run's log volume, and the run summary at EndRun carries the count.
            if (log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] progress {killed}/{required} boss_remaining={bossRemaining} for {run}");
        }

        /// <summary>
        /// Test seam: production code always sets this to RecordSurvey. Tests swap it for a counting
        /// delegate (and restore it in a finally) to observe the trigger without a live player.
        /// </summary>
        internal static Action<ThreadDungeonRun> SurveyRecorder = RecordSurvey;

        /// <summary>
        /// Thread-Guide clear seam, same swappable-delegate pattern as <see cref="SurveyRecorder"/>. Production
        /// target ThreadGuideStation.RecordGuideClear: guide runs only, the online owner only, on their own action
        /// queue, and the ladder advances only on the rung ThreadGuideLadder.NextRung names (ThreadGuideFlow.ApplyClear).
        /// XP is paid here and nowhere else in the feature.
        /// </summary>
        internal static Action<ThreadDungeonRun> GuideRecorder = ThreadGuideStation.RecordGuideClear;

        /// <summary>
        /// Lifetime clears counter seam (PropertyInt.ThreadClearsLifetime), same pattern. Every clear, guide or not,
        /// for the survey's recipients under the survey's nothing-spawned gate (ThreadGuideFlow.ClearCountRecipients).
        /// </summary>
        internal static Action<ThreadDungeonRun> ClearCounter = RecordClearCount;

        /// <summary>
        /// Thread-Guide fail seam, called from EndRun after OnRunEnded when ThreadGuideFlow.ShouldRegrantOnEnd says
        /// so (the run never cleared and was a guide run). Production target ThreadGuideStation.RegrantAfterFail.
        /// </summary>
        internal static Action<ThreadDungeonRun> GuideFailRegranter = ThreadGuideStation.RegrantAfterFail;

        /// <summary>The production <see cref="ClearCounter"/>: one increment per recipient, on that player's own queue.</summary>
        public static void RecordClearCount(ThreadDungeonRun run)
        {
            if (run == null)
                return;

            var recipients = ThreadGuideFlow.ClearCountRecipients(run.SnapshotRoster(), guid => PlayerManager.GetOnlinePlayer(guid) != null, run.Spawned);

            foreach (var guid in recipients)
                ThreadGuideStation.IncrementClears(PlayerManager.GetOnlinePlayer(guid));
        }

        /// <summary>
        /// Daily-survey stamp hook (PHASE-2-DESIGN.md section 4.1, owner ruling B3). Files a survey for the
        /// owner when the run's gem level meets the floor (the floor defaults to 0 since owner ruling
        /// 2026-09-11, which replaced B3's 185), on the owner's own action queue - never inline on
        /// the landblock thread that calls AnnounceCleared. A run whose owner is offline at the moment of the
        /// clear files no survey (ruling P2-R4): there is no queue to enqueue onto.
        ///
        /// Group Threads (spec 6.5, ruling R22). The recipients are <see cref="SurveyRecipients"/>: the owner on
        /// today's rule (online at the clear), and every other roster member who is online AND has entered the
        /// run. The nothing-spawned and gem-level gates are taken once for the run; each recipient then runs the
        /// unchanged per-player body on THEIR OWN action queue. A solo run's only candidate is its owner, so an
        /// offline solo owner still returns before either gate, exactly as before.
        /// </summary>
        public static void RecordSurvey(ThreadDungeonRun run)
        {
            var recipients = SurveyRecipients(run.SnapshotRoster(), guid => PlayerManager.GetOnlinePlayer(guid) != null);
            if (recipients.Count == 0)
                return;

            // Ruling P2-R22: a run whose plan placed nothing reaches Cleared via OnRunPopulated with no
            // clear to speak of - there was nothing to kill - so it must not file a survey either.
            if (run.Spawned <= 0)
            {
                log.Info($"[DYNDUNGEON] survey not filed for {run}: nothing spawned");
                return;
            }

            var minLevel = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_survey_min_level").Item, 0, int.MaxValue);
            if (!DungeonSurveyRules.Counts(run.Spec.Level, minLevel))
            {
                log.Info($"[DYNDUNGEON] survey not filed for {run}: gem level {run.Spec.Level} < {minLevel}");
                return;
            }

            var runText = run.ToString();

            // Captured before the closure for the same reason runText is: the run's spec is what the survey
            // records, and reading it later would read whatever the run has become by then.
            var gemLevel = run.Spec.Level;

            foreach (var guid in recipients)
            {
                // Re-resolved per recipient: the snapshot above only said they were online a moment ago.
                var owner = PlayerManager.GetOnlinePlayer(guid);
                if (owner == null)
                    continue;

                var isRunOwner = guid == run.OwnerGuid;

                owner.EnqueueAction(new ActionEventDelegate(() =>
                {
                    // Built HERE, inside the delegate: the daily-survey day this filing counts against is
                    // whichever day it is when the delegate actually runs, not when RecordSurvey was called.
                    var clock = SurveyArchivistStation.BuildClock();
                    var n = DungeonSurveyRules.Record(new QuestManagerSurveyLedger(owner.QuestManager), clock);

                    // The difficulty ring (owner ruling R1: GEM level, not the levels actually spawned). Written
                    // INSIDE this delegate, after the ledger write and before MarkSurveyFiled, so the ring can
                    // never record a survey that was not filed - which is the whole reason MarkSurveyFiled sits
                    // where it does. Parse tolerates an absent or corrupt value by reading an empty ring, so this
                    // write also self-heals a damaged property.
                    var ring = SurveyLevelRing.Parse(owner.GetProperty(PropertyString.DungeonSurveyLevels)).Push(gemLevel);
                    owner.SetProperty(PropertyString.DungeonSurveyLevels, ring.Serialize());

                    // Latched HERE, inside the enqueued action and after the ledger write, not above where the
                    // decision to try was taken. The two differ whenever the owner detaches between the clear and
                    // this delegate running - EnqueueAction discards the work in that case
                    // (WorldObject_Tick.cs:144-155) - and survey_filed has to mean the survey exists.
                    //
                    // The run-level latch is the OWNER's (it feeds the owner-keyed dungeon_run row and the owner's
                    // participant row), so a non-owner member latches only their own.
                    if (isRunOwner)
                        run.MarkSurveyFiled();
                    run.MarkMemberSurveyFiled(guid);

                    owner.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your survey is filed. The ledger counts {n} under your name today.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                    log.Info($"[DYNDUNGEON] survey filed by {owner.Name} count={n} for {runText}");
                }));
            }
        }

        /// <summary>
        /// Who a clear files a survey for (spec 6.5, ruling R22), in roster order: the owner when online (today's
        /// rule, no entry test), and any other member when online AND their Entered latch is set. A null roster or
        /// lookup yields nobody.
        /// </summary>
        internal static IReadOnlyList<uint> SurveyRecipients(IReadOnlyList<RosterMemberSnapshot> roster, Func<uint, bool> isOnline)
        {
            var recipients = new List<uint>();

            if (roster == null || isOnline == null)
                return recipients;

            foreach (var member in roster)
            {
                if (member == null)
                    continue;

                // A member the puzzle fail policy removed earns nothing from the clear (ThreadPuzzleFailPolicy).
                if (member.PuzzleRemoved)
                    continue;

                if ((member.IsOwner || member.Entered) && isOnline(member.Guid))
                    recipients.Add(member.Guid);
            }

            return recipients;
        }

        /// <summary>
        /// The presence sample (ruling R24): the guids of <paramref name="roster"/> members for whom
        /// <paramref name="isInside"/> is true, in roster order. The caller passes
        /// ThreadRunPresence.IsMemberInside over the online player, so this stays Player-free.
        /// </summary>
        internal static IReadOnlyList<uint> PresenceSample(IReadOnlyList<ThreadRosterMember> roster, Func<uint, bool> isInside)
        {
            var inside = new List<uint>();

            if (roster == null || isInside == null)
                return inside;

            foreach (var member in roster)
            {
                if (member != null && isInside(member.Guid))
                    inside.Add(member.Guid);
            }

            return inside;
        }

        /// <summary>
        /// Group Threads arrival hook, called from Player.OnTeleportComplete once the player has materialised in
        /// <paramref name="instance"/>. Latches the run's PlayerEverObserved (what the hook always did), then for a
        /// roster member latches Entered, and asks for delivery when <see cref="ArrivalWantsDelivery"/> says so
        /// (ruling R1). Resolved by instance, never by owner guid, so a held cleared run and a newer live one of the
        /// same owner cannot be confused.
        /// </summary>
        public static void OnPlayerArrived(Player player, uint instance)
        {
            if (player == null || instance == 0)
                return;

            var run = GetRun(instance);
            if (run == null)
                return;

            run.MarkPlayerObserved();

            var guid = player.Guid.Full;
            if (!run.IsRosterMember(guid))
                return;

            run.MarkMemberEntered(guid);

            if (ArrivalWantsDelivery(run.IsGroup, run.State, run.IsMemberDeliveryWanted(guid), run.HasHeldPile(guid)))
            {
                var outcome = PooledLootTrigger(run);

                // Busy ran nothing (another delivery holds the run's latch), so the arrival would be lost. Flagging the
                // member hands the delivery to the Tick's group retry, which serves a flagged member who is inside.
                if (ArrivalNeedsTickRetry(outcome))
                    run.MarkMemberDeliveryWanted(guid);
            }
        }

        /// <summary>Task 10 fix round 1: an arrival whose delivery request found the latch held (Busy) waits for the Tick retry.</summary>
        internal static bool ArrivalNeedsTickRetry(CacheRequestOutcome outcome) => outcome == CacheRequestOutcome.Busy;

        /// <summary>
        /// Does a roster member's arrival ask for delivery (ruling R1)? Only in a Cleared GROUP run, and only for a
        /// member who is flagged delivery-wanted OR holds a pile. The pile clause covers the member who was inside
        /// when delivery hit NoRoom (not flagged, ruling R20) and then left: coming back delivers their pile.
        /// </summary>
        internal static bool ArrivalWantsDelivery(bool isGroup, ThreadDungeonRunState state, bool deliveryWanted, bool hasHeldPile)
            => isGroup && state == ThreadDungeonRunState.Cleared && (deliveryWanted || hasHeldPile);

        /// <summary>
        /// Test seam: production code always sets this to DestroyGem. Tests swap it for a counting
        /// delegate (and restore it in a finally) to observe the trigger without a live player.
        /// </summary>
        internal static Action<ThreadDungeonRun> GemDestroyer = DestroyGem;

        /// <summary>
        /// The reaping decision, extracted as a pure function so it can be tested without a world (visible to
        /// ACE.Server.Tests via InternalsVisibleTo in ACE.Server.csproj:15). It reads no PropertyManager
        /// tunable, deliberately: PropertyManager reads throw under the unit-test harness.
        /// Returns true when the run must be ended, with the reason EndRun should log.
        ///
        /// The four ways a run dies on its own, in priority order:
        ///
        /// 1. TTL. Checked first, so an expired run reports "expired" whatever else is wrong with it.
        /// 2. Its landblock is gone. A copy unloads by itself once it has stood empty for its idle window -
        ///    the run's EmptyGrace (dynamic_dungeons_empty_grace_minutes, default 10) while Starting or
        ///    Active, a held GROUP run's lock-time loot hold (dynamic_dungeons_group_loot_hold_minutes, see
        ///    ThreadDungeonRun.ResolveUnloadInterval) while Cleared and owing loot, Landblock.UnloadInterval
        ///    (five minutes) otherwise - a solo run is never held (owner ruling, 2026-09-27), so it always
        ///    takes this last case while Cleared - and
        ///    once it is gone the run has nowhere to happen. This is deliberately NOT gated on the state:
        ///    GetLandblock registers the instance in LandblockManager's ephemeral instance registry
        ///    (EphemeralInstanceRegistry, reached through LandblockManager.GetEphemeralLandblock)
        ///    synchronously, inside the write lock and before Init() runs, so a healthy Starting run always
        ///    has a landblock. A Starting run with none is one whose copy never came up, and gating this on
        ///    state left it pinned in the registry for the whole TTL - blocking its owner out of the feature
        ///    for three hours by way of the owner and per-account start gates.
        /// 3. Its landblock is up but never finished loading. The content-filter seam in
        ///    Landblock.CreateWorldObjects rethrows on failure, which leaves CreateWorldObjectsCompleted false
        ///    permanently; a player who teleports in then re-queues OnTeleportComplete every 0.1s forever and
        ///    never materialises (Player_Location.cs:906-917). Only meaningful while Starting, since the flag
        ///    is set once and never cleared.
        /// 4. It is Cleared, someone HAS been inside it, nobody is now, and it is past
        ///    <see cref="ClearedGrace"/>. A finished run has nothing left to do: its gem was destroyed at the
        ///    clear, so nobody can re-enter it, and the last player has left. Before this branch existed such
        ///    a run idled to its TTL - hours - which is what made a player who had finished and walked out
        ///    wait to open their next dungeon.
        ///
        ///    Three conditions, and the order they are written in is the order of their importance.
        ///    <paramref name="playerEverObserved"/> is the correctness one: "nobody is inside" is a fact for
        ///    a run somebody entered and left, but for one whose owner is still in transit it only means
        ///    "not yet", and the caller's probe cannot tell those apart (see
        ///    ThreadDungeonRun.PlayerEverObserved). A run nobody ever entered is left to branches 1 and 3.
        ///    <paramref name="playerPresent"/> is the behavioural one: a player still looting inside keeps
        ///    the copy up indefinitely, exactly as before. The grace is then a second line rather than the
        ///    only protection, for the entry race described on ClearedGrace.
        ///
        ///    Group Threads: a GROUP run's gem and keys survive the clear (ruling R29), so its members CAN
        ///    re-enter it; branch 4 therefore stands down while the run is on the group loot hold (see the
        ///    overload taking groupLootHeld). "Nobody inside" needs no group change (ruling R23): only roster
        ///    members can be in a Thread copy, and the probe counts any player in the landblock.
        ///
        ///    A SOLO run is never put on the hold (owner ruling, 2026-09-27): its gem is destroyed at the clear
        ///    same as a group run's owner-gem exception does not apply to it, and with nobody able to re-enter,
        ///    branch 4 fires for it exactly as it did before Group Threads - undelivered loot left behind is
        ///    forfeit at run end. Tick always passes false for a solo run's groupLootHeld (GroupLootHoldActive
        ///    requires isGroup), so the overload below never stands branch 4 down for one.
        /// </summary>
        internal static bool ShouldEnd(ThreadDungeonRunState state, bool landblockPresent, bool createCompleted, bool playerEverObserved, bool playerPresent, TimeSpan sinceStart, TimeSpan sinceCleared, DateTime now, DateTime expiresUtc, out string reason)
            => ShouldEnd(state, landblockPresent, createCompleted, playerEverObserved, playerPresent, groupLootHeld: false, sinceStart, sinceCleared, now, expiresUtc, out reason);

        /// <summary>
        /// <see cref="ShouldEnd(ThreadDungeonRunState, bool, bool, bool, bool, TimeSpan, TimeSpan, DateTime, DateTime, out string)"/>
        /// plus the loot hold (ruling R29, unified with the empty grace). <paramref name="groupLootHeld"/> is
        /// ThreadDungeonRun.GroupLootHoldActive, which is always false for a solo run (never held for loot, owner
        /// ruling 2026-09-27) and true only for a Cleared group run that still owes loot with a positive lock-time
        /// hold: while it is true, branch 4 (cleared and empty) does not fire, so a member who died or stepped out
        /// can still come back for their pile. The hold is bounded by the empty-copy mechanism, not here: the
        /// copy's idle window is the lock-time hold (ThreadDungeonRun.ResolveUnloadInterval / Group.LootHold), so
        /// once no member has been inside for it the copy unloads and branch 2 ends the run (end_state stays
        /// "cleared", DungeonRunTelemetry.EndStateFor keys on the prior state), and the piles still held are
        /// forfeited at run end. Branches 1-3 are unaffected.
        /// </summary>
        internal static bool ShouldEnd(ThreadDungeonRunState state, bool landblockPresent, bool createCompleted, bool playerEverObserved, bool playerPresent, bool groupLootHeld, TimeSpan sinceStart, TimeSpan sinceCleared, DateTime now, DateTime expiresUtc, out string reason)
        {
            reason = null;

            if (state == ThreadDungeonRunState.Ended)
                return false;

            // The reason strings are constants rather than literals because DungeonRunTelemetry.EndStateFor
            // maps them onto the analytics end_state vocabulary. Retyping one here and leaving the mapping
            // alone would silently reclassify the run as 'abandoned', which is the mapping's default.
            if (now >= expiresUtc)
            {
                reason = DungeonRunTelemetry.EndReasons.Expired;
                return true;
            }

            if (!landblockPresent)
            {
                reason = DungeonRunTelemetry.EndReasons.LandblockUnloaded;
                return true;
            }

            if (state == ThreadDungeonRunState.Starting && !createCompleted && sinceStart > LoadTimeout)
            {
                reason = DungeonRunTelemetry.EndReasons.LoadFailed;
                return true;
            }

            if (state == ThreadDungeonRunState.Cleared && playerEverObserved && !playerPresent && sinceCleared >= ClearedGrace && !groupLootHeld)
            {
                reason = DungeonRunTelemetry.EndReasons.ClearedAndEmpty;
                return true;
            }

            return false;
        }

        public static void Tick()
        {
            var now = DateTime.UtcNow;
            if (now < nextTick) return;
            nextTick = now + TickInterval;

            var reap = now >= nextReap;
            if (reap) nextReap = now + ReapInterval;

            TickPuzzleFailPolicy(now, reap);

            // Before the run loop: an EndRun in that loop has just issued eviction teleports, so its copy is still
            // occupied now and is picked up on a later tick, never this one.
            QueueEmptiedEndedCopies();

            foreach (var run in runs.Values.ToList())
            {
                // One snapshot per pass. State is read under the run's own lock and a landblock thread can
                // move it (a kill that clears the run) between two reads, so the reap decision and the
                // populate decision below must be taken against the same value.
                var state = run.State;

                if (state == ThreadDungeonRunState.Ended)
                {
                    // Belt and braces: EndRun already removes the run. This catches anything that reached
                    // Ended by another path.
                    runs.TryRemove(run.RunId, out _);
                    continue;
                }

                var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);
                var createCompleted = landblock != null && landblock.CreateWorldObjectsCompleted;

                if (reap)
                {
                    // Presence sampling (ruling R24), before the reap decision so a run ended on this pass still
                    // books its last interval.
                    SampleMemberPresence(run);

                    // Sampled only for a Cleared run, and only on a reap pass: this copies the copy's whole
                    // object dictionary (the same call EndRun's eviction uses), and ShouldEnd's Cleared branch
                    // is the only one that reads it. Everything else short-circuits to false without paying
                    // for the scan.
                    var playerPresent = state == ThreadDungeonRunState.Cleared && landblock != null
                        && landblock.GetAllWorldObjectsForDiagnostics().OfType<Player>().Any();

                    // Second, free source for the entry latch. The primary one is Player.OnTeleportComplete;
                    // this catches a player who reached the copy by some path that never runs it (an admin
                    // teleport, say) and then left, which would otherwise pin the run to its TTL. It costs
                    // nothing: the scan has already happened, and the latch is idempotent.
                    if (playerPresent)
                        run.MarkPlayerObserved();

                    var sinceCleared = run.ClearedUtc is DateTime clearedUtc ? now - clearedUtc : TimeSpan.Zero;

                    // Ruling R29 on the unified empty-copy mechanism: a Cleared GROUP run that still owes loot is
                    // on the loot hold - GroupLootOwed, reading placed caches, asked only for a Cleared group run,
                    // and only here on the world thread. The answer is stored on the run for the landblock
                    // heartbeat (ThreadDungeonRun.UnloadIntervalOverride), which then holds the empty copy for the
                    // lock-time hold (run.Group.LootHold) instead of Landblock.UnloadInterval; once no member has
                    // been inside for that long the copy unloads and ShouldEnd's landblock-gone branch ends the run.
                    //
                    // A SOLO run is never held (owner ruling, 2026-09-27): GroupLootHoldActive requires isGroup, so
                    // groupLootHeld is always false for one. Its gem is destroyed at the clear (AnnounceCleared ->
                    // GemDestroyer, ruling A1), so nobody can re-enter it, and undelivered loot left behind is
                    // forfeit at run end - a Cleared solo run falls straight to ShouldEnd's cleared-and-empty branch
                    // once it is empty and past ClearedGrace, exactly as before Group Threads.
                    var groupLootOwed = state == ThreadDungeonRunState.Cleared && run.IsGroup && ThreadRunPresence.GroupLootOwed(run);
                    var groupLootHeld = ThreadDungeonRun.GroupLootHoldActive(state, run.IsGroup, groupLootOwed, run.Group.LootHold);
                    run.SetGroupLootHeld(groupLootHeld);

                    if (ShouldEnd(state, landblock != null, createCompleted, run.PlayerEverObserved, playerPresent, groupLootHeld, now - run.StartedUtc, sinceCleared, now, run.ExpiresUtc, out var why))
                    {
                        EndRun(run, why);
                        continue;
                    }
                }

                // Empty-grace countdown. Every tick rather than every reap: the last warning is 30 seconds out, and
                // a 15 s cadence could skip past it entirely. The window is whatever the landblock heartbeat is
                // measuring against right now - the grace while Starting or Active, the loot hold for a held group
                // run - and null (no countdown) otherwise, so a solo run warns exactly as before.
                var emptyWindow = ThreadDungeonRun.ResolveUnloadInterval(state, run.EmptyGrace, run.IsGroupLootHeld, run.Group.LootHold);
                if (emptyWindow != null && landblock != null)
                    WarnIfEmptyGraceRunningOut(run, landblock.LastActiveUtc, emptyWindow.Value, now);

                // Pooled loot: a delivery that found the owner outside the copy waits for them (spec section 4).
                // The flag is read first so the owner lookup runs only for a run that is actually waiting.
                // SOLO only: a group run waits per member (ruling R20) and retries through GroupDeliveryWanted.
                if (!run.IsGroup && state == ThreadDungeonRunState.Cleared && run.IsPlacementWanted
                    && ThreadCachePlacer.IsOwnerInside(run, PlayerManager.GetOnlinePlayer(run.OwnerGuid)))
                {
                    RetryWaitingDelivery(run);
                }
                else if (state == ThreadDungeonRunState.Cleared && run.IsGroup && ThreadRunPresence.GroupDeliveryWanted(run))
                {
                    PooledLootTrigger(run);
                }

                // Thread cache display sort: a cache that had a VIEWER when its delivery chain finished was left
                // in arrival order (ThreadCacheSort's doc comment says why a server must not reorder a container
                // the client is already displaying). This is the retry that picks it up once the player closes
                // it. Solo only, and skipped while a delivery holds the run's latch so the pass cannot land
                // between two steps of a chain that is about to sort anyway.
                //
                // The work runs on the run landblock's own action queue, not here on the world thread: it reads
                // and writes Container inventories, which belong to that landblock, and queueing it there is also
                // what makes it unable to interleave with a delivery step.
                if (!run.IsGroup && landblock != null && run.IsCacheSortWanted && !run.IsCachePlacementInProgress(now))
                {
                    var sortRun = run;

                    landblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            var pass = ThreadCacheSort.SortDeferred(sortRun);

                            if (pass.Caches > 0 || pass.LeftWork)
                                log.Debug($"[DYNDUNGEON] {sortRun} deferred cache sort:{ThreadCacheSort.LogSuffix(pass)}");
                        }
                        catch (Exception ex)
                        {
                            // EnqueueAction escapes the caller's try/catch, so this is the action's only guard.
                            log.Error($"[DYNDUNGEON] {sortRun} deferred cache sort threw", ex);
                        }
                    }));
                }

                // Populate a Starting run the moment its copy has finished loading. TryPopulate is safe to
                // call every tick - it latches per run and returns immediately once a populate is in flight -
                // and it is what calls run.MarkPopulated, which moves the run to Active.
                //
                // This sits BELOW the ShouldEnd call deliberately. ShouldEnd is the only thing that reaps a
                // copy whose CreateWorldObjects threw (ruling R18), and a populate gated on createCompleted
                // would simply never fire for such a run rather than noticing it is broken.
                if (state == ThreadDungeonRunState.Starting && createCompleted)
                    ThreadDungeonSpawner.TryPopulate(run, landblock);

                // Puzzle watchdog: the puzzle manager's own reap is lazy, so a run reaps its dead placements here and a
                // SEALED run whose reward scene is gone (landblock reload/unload, a spawn that never ran) is unsealed
                // instead of sitting Active with every kill done until its TTL. Free for a run that placed no puzzle.
                if (run.HasPuzzles)
                {
                    try
                    {
                        ThreadPuzzleWatchdog.Check(run, now);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} puzzle watchdog threw", ex);
                    }
                }

                // Threads item 4: start a loot-trickle chain on the copy when an Active run has ledger rolls to build and
                // no chain is running. Cheap when there is nothing to do; see ThreadLootTrickle for every gate.
                if (state == ThreadDungeonRunState.Active && landblock != null)
                {
                    try
                    {
                        ThreadLootTrickle.Kick(run, landblock, now);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} starting the loot trickle threw", ex);
                    }
                }
            }
        }

        /// <summary>
        /// One presence sample on the reap cadence (ruling R24). Every roster member inside the copy right now
        /// (ThreadRunPresence.IsMemberInside over the online player) is credited one reap interval of
        /// seconds_inside and latched Entered. In a GROUP run, an inside member for whom
        /// <see cref="FellowshipLeftWarningDue"/> holds gets S8 once (TryMarkFellowshipWarned). Solo runs sample too,
        /// for their owner's participant row, and are never warned.
        /// </summary>
        private static void SampleMemberPresence(ThreadDungeonRun run)
        {
            var seconds = (int)ReapInterval.TotalSeconds;
            var inside = PresenceSample(run.Roster, g => ThreadRunPresence.IsMemberInside(run, PlayerManager.GetOnlinePlayer(g)));

            foreach (var guid in inside)
            {
                run.AddMemberSecondsInside(guid, seconds);
                run.MarkMemberEntered(guid);

                if (!run.IsGroup)
                    continue;

                var player = PlayerManager.GetOnlinePlayer(guid);
                if (player == null)
                    continue;

                // Read on the world thread after LandblockManager.Tick has returned (see Tick), so no fellowship
                // operation is mutating the member dictionary underneath this lookup.
                var fellowship = player.Fellowship;
                var fellows = fellowship?.FellowshipMembers;
                Func<uint, bool> inFellowship = fellows == null ? null : g => fellows.ContainsKey(g);

                if (FellowshipLeftWarningDue(ReferenceEquals(fellowship, run.FellowshipAtLock), inFellowship, run.Roster) && run.TryMarkFellowshipWarned(guid))
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat(FellowshipLeftWarning, ACE.Entity.Enum.ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// Should an inside member get S8 (ruling R24)? Not while they are in the fellowship the roster locked with
        /// (<paramref name="sameFellowshipAsLock"/>). Final review F5: not either when their CURRENT fellowship
        /// (<paramref name="inCurrentFellowship"/>, null when they have none) contains every roster member guid, so a
        /// group that disbanded and re-formed with the same people is not told it left. The warning is due otherwise,
        /// including for a member with no fellowship at all. Pure; the caller latches it once per member.
        /// </summary>
        internal static bool FellowshipLeftWarningDue(bool sameFellowshipAsLock, Func<uint, bool> inCurrentFellowship, IReadOnlyList<ThreadRosterMember> roster)
        {
            if (sameFellowshipAsLock)
                return false;

            if (inCurrentFellowship == null || roster == null || roster.Count == 0)
                return true;

            foreach (var member in roster)
            {
                if (member == null || !inCurrentFellowship(member.Guid))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Warns the run's relevant players who are online and OUTSIDE its copy as the copy's empty window runs out
        /// (owner ruling 2026-09-17: 5m, 2m, 1m, 30s). Called from Tick on the world thread whenever
        /// ThreadDungeonRun.ResolveUnloadInterval gives the copy a window: <paramref name="window"/> is the empty
        /// grace for a Starting/Active run, or the loot hold for a Cleared group run on the hold.
        ///
        /// Recipients come from ThreadDungeonRun.GraceWarningRecipients: a SOLO run's owner (exactly the recipient
        /// this had before Group Threads), or a GROUP run's owner and every keyed member. A recipient who is offline
        /// or inside is skipped, and when nobody is left the latch is NOT touched: someone inside is refreshing the
        /// idle clock, so the first look after they leave sees a full window and the latch resets itself
        /// (ThreadDungeonRun.GraceWarningDue); nobody online means nothing can be delivered, and a recipient who logs
        /// back in mid-countdown gets one warning worded from the time actually left. The latch is shared by the
        /// run, so each crossing goes out once, to whoever is reachable at that moment.
        ///
        /// The latch is advanced here, but the gem lookup and the send run on each recipient's own action queue,
        /// the same route DestroyGem takes to their inventory: the world thread does not read another player's
        /// inventory. The lookup therefore happens only when a warning is actually going out.
        /// </summary>
        private static void WarnIfEmptyGraceRunningOut(ThreadDungeonRun run, DateTime lastActiveUtc, TimeSpan window, DateTime now)
        {
            var recipients = ThreadDungeonRun.GraceWarningRecipients(run.IsGroup, run.OwnerGuid, run.GemGuid, run.Roster, run.MemberKeyGem,
                guid =>
                {
                    var player = PlayerManager.GetOnlinePlayer(guid);
                    return player?.Session != null && !ThreadCachePlacer.IsOwnerInside(run, player);
                });

            if (recipients.Count == 0)
                return;

            var remaining = ThreadDungeonRun.GraceRemaining(lastActiveUtc, window, run.ExpiresUtc, now);

            if (!run.TryAdvanceGraceWarning(remaining))
                return;

            var dungeonName = run.Dungeon?.Name;
            var runText = run.ToString();
            var isGroup = run.IsGroup;

            foreach (var (guid, gemGuid) in recipients)
            {
                var recipient = PlayerManager.GetOnlinePlayer(guid);
                if (recipient == null)
                    continue;

                // The solo log line is master's, character for character; a group line names the recipient.
                var to = isGroup ? $" to 0x{guid:X8}" : "";

                // A non-owner recipient re-enters with their key, so their warning says "Key"; the owner (and every
                // solo recipient, who is always the owner) keeps master's exact text.
                var isKeyHolder = guid != run.OwnerGuid;

                recipient.EnqueueAction(new ActionEventDelegate(() =>
                {
                    var gem = recipient.GetInventoryItem(gemGuid);
                    var canReEnter = ThreadDungeonRun.GemCanReEnter(gem != null, gem?.Structure ?? 0);

                    recipient.Session?.Network.EnqueueSend(new GameMessageSystemChat(ThreadDungeonRun.BuildGraceWarning(dungeonName, remaining, canReEnter, isKeyHolder), ACE.Entity.Enum.ChatMessageType.Broadcast));
                    log.Info($"[DYNDUNGEON] empty-grace warning ({ThreadDungeonRun.FormatRemaining(remaining)} left, gem_sentence={canReEnter}){to} for {runText}");
                }));
            }
        }

        /// <summary>
        /// The login reroute line for a player whose saved instance is their OWN run that is still Starting or
        /// Active with its copy loaded, or null when that is not the case (every other reroute keeps
        /// WorldManager's generic "expired" line). Such a player is rerouted only because EphemeralRealm.Accepts
        /// compares the owner by Player reference and a login builds a new Player; the run itself is fine, and
        /// telling them it expired would send them to the Fragment Press with a gem that still works.
        ///
        /// A Cleared run returns null on purpose. For a solo run that is the gem-is-gone case: the gem is already
        /// destroyed, so there is nothing to step back in with. A held GROUP run's owner gem and keys survive the
        /// clear until the run ends (ruling R29), but the owner still gets no login line here, by ruling: this
        /// method only ever answers for a Starting or Active run.
        /// </summary>
        public static string GetStillOpenLoginMessage(Player player, uint savedInstance)
        {
            if (player == null)
                return null;

            var run = GetRun(savedInstance);
            if (run == null || run.OwnerGuid != player.Guid.Full)
                return null;

            var state = run.State;
            if (state != ThreadDungeonRunState.Starting && state != ThreadDungeonRunState.Active)
                return null;

            if (LandblockManager.GetEphemeralLandblock(run.Instance) == null)
                return null;

            var gem = player.GetInventoryItem(run.GemGuid);
            return ThreadDungeonRun.BuildStillOpenLoginMessage(run.Dungeon?.Name, ThreadDungeonRun.GemCanReEnter(gem != null, gem?.Structure ?? 0));
        }

        /// <summary>
        /// The run's one Info summary line, written once by <see cref="EndRun"/>: what the run cost and where the
        /// time went. Pure over the run (its locked getters and its Interlocked ThreadRunPerfStats), so it is testable
        /// without a world. planMs and bandLow read -1 for a run whose plan was never built, and doorMs -1 for one
        /// whose door step never ran.
        ///
        /// pooledLeft (pooled boss rolls never built) and buffered (built group items never dealt) are what the run-end
        /// drain found. EndRun logs this line AFTER the drain has zeroed both on the run, so it passes the drain in;
        /// with no drain they are read live from the run.
        /// </summary>
        internal static string RunSummaryLine(ThreadDungeonRun run, LootPoolDrain drained = null)
        {
            var perf = run.Perf;
            var pooledLeft = drained?.PooledBonusRolls ?? run.PooledBonusRollsPending;
            var buffered = drained?.Buffered.Count ?? run.DealBufferCount;
            var prebuiltLeft = drained?.Prebuilt.Count ?? run.PrebuiltObjects;

            return $"[DYNDUNGEON] run summary run=0x{run.RunId:X8} dungeon={run.Dungeon?.Id} level={run.Spec?.Level} bandLow={perf.BandLow} " +
                   $"seats={run.Roster?.Count ?? 0} group={run.IsGroup} placed={run.Spawned}/{run.Planned} kills={run.Killed} " +
                   $"planMs={perf.PlanMs} planCached={perf.PlanCached} planMemo={perf.PlanMemoHits}/{perf.PlanMemoLookups} " +
                   $"placeMs={perf.PlaceMs} placeSteps={perf.PlaceSteps} doorMs={perf.DoorPassMs} doors={perf.DoorsUnlocked} " +
                   $"lootChains={perf.LootChains} lootSteps={perf.LootSteps} lootTotalMs={perf.LootTotalMs} lootMaxStepMs={perf.LootMaxStepMs} weenieMiss={perf.WeenieMisses} " +
                   $"pooledLeft={pooledLeft} buffered={buffered} " +
                   $"prebuilt={perf.Prebuilt} builtAtClear={perf.BuiltAtClear} prebuiltLeft={prebuiltLeft} trickleSteps={perf.TrickleSteps} trickleMs={perf.TrickleMs} trickleMaxStepMs={perf.TrickleMaxStepMs} trickleDroppedOnEnd={perf.TrickleDroppedItems}/{perf.TrickleDroppedRares} stacksMerged={perf.StacksMerged} " +
                   $"end={run.EndReason}";
        }

        /// <summary>
        /// The run-end "unclaimed pooled loot" line, or null when the drain found nothing left. A pooled boss roll never
        /// built (drained.PooledBonusRolls) counts even after the bonus latch was claimed, and so does a built group item
        /// still in the deal buffer (drained.Buffered): both are loot a player was owed and never received, and gating on
        /// the latch alone (drained.BonusPending) hid them (code review of #1212, finding 1).
        /// </summary>
        internal static string UnclaimedPooledLootLine(ThreadDungeonRun run, LootPoolDrain drained)
        {
            if (drained == null)
                return null;

            if (drained.LedgerEntries <= 0 && !drained.BonusPending && drained.PooledBonusRolls <= 0 && drained.Overflow.Count == 0 &&
                drained.HeldRares.Count == 0 && drained.Buffered.Count == 0 && drained.Prebuilt.Count == 0 &&
                run.Perf.TrickleDroppedItems == 0 && run.Perf.TrickleDroppedRares == 0)
                return null;

            return $"[DYNDUNGEON] {run} ended with unclaimed pooled loot: ledger={drained.LedgerEntries} bonus={drained.BonusPending} " +
                   $"pooledRolls={drained.PooledBonusRolls} buffered={drained.Buffered.Count} prebuilt={drained.Prebuilt.Count} trickleDroppedOnEnd={run.Perf.TrickleDroppedItems}/{run.Perf.TrickleDroppedRares} overflow={drained.Overflow.Count} " +
                   $"rares={drained.HeldRares.Count} caches={drained.PlacedCaches}";
        }

        /// <summary>
        /// Where a player leaving a run's copy is sent: EndRun's eviction, and the puzzle fail policy's removal of one
        /// group member (ThreadPuzzleFailPolicy), so the two can never disagree. EphemeralRealmExitTo first: the gem
        /// handler re-stamps it from the player's CURRENT location on every entry, so a player who re-entered from a
        /// second town is evicted back THERE rather than to run.ExitTo, which records only where the run was first
        /// opened.
        /// </summary>
        internal static Position EvictionExit(Player player, ThreadDungeonRun run)
            => player.GetPosition(PositionType.EphemeralRealmExitTo) ?? run.ExitTo ?? player.Sanctuary ?? player.Instantiation;

        /// <summary>The single exit path: marks the run ended, evicts anyone still inside, queues the landblock for destruction, destroys the gem.</summary>
        public static void EndRun(ThreadDungeonRun run, string why)
        {
            // MarkEnded returns true only for the caller that made the transition, so the eviction, the
            // destruction-queue push and the gem destruction below happen exactly once even if the world
            // thread and a landblock thread call EndRun on the same run at the same moment.
            //
            // The overload that reports the PRIOR state is what the telemetry row's end_state is derived
            // from. It has to come out of this same critical section: reading run.State first would leave a
            // window in which a landblock thread lands the clearing kill, and the run would be filed as
            // abandoned when the player had just finished it.
            if (run == null || !run.MarkEnded(why, out var priorState)) return;

            RecordRunTelemetry(run, priorState, why);

            // Drop the registry entry now rather than waiting for the next reap. An Ended run left in `runs`
            // for up to 15 seconds can collide with a re-issued instance id, and the TryAdd in TryStart would
            // then fail for the new run - pinning that id as pending in LandblockManager's ephemeral instance
            // registry (EphemeralInstanceRegistry.ClearPending) forever, because only a successful
            // GetLandblock clears it.
            runs.TryRemove(run.RunId, out _);

            // Drop the spawner's in-flight latch for the same reason. A run that ends mid-populate takes its
            // pending action delegate down with it when Landblock.Unload clears the landblock's action queue,
            // so the chain's own terminal step may never run and nothing else would release the latch. The
            // latch is keyed on the ephemeral instance id, which LandblockManager re-issues, and a re-issued
            // id that is still latched would never populate.
            ThreadDungeonSpawner.ForgetRun(run.RunId);

            // Puzzle placements are registered with the puzzle manager, which reaps lazily: clear this run's now, so
            // nothing outlives the run in the registry. Unconditional rather than gated on HasPuzzles: the puzzle pass
            // latches that flag on the landblock thread, and a run ended mid-pass must still be swept (the pass also
            // re-checks for Ended after it registers). Guarded like the telemetry above: nothing here may stop the
            // eviction below.
            try
            {
                ACE.Server.PuzzleGates.PuzzleGateManager.ClearForRun(run.RunId);

                // A run sealed PENDING (its reward scene was to be placed at arming) has no placement whose removal
                // would lift the seal: lift it here, so no ended run stays sealed. The run is Ended, so the clear
                // check this re-runs is a no-op and there is no follow-up to owe.
                if (ThreadPuzzlePass.ReleasePendingSealOnEnd(run))
                    log.Info($"[DYNDUNGEON] {run} ended with its reward scene still pending; reward unsealed");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] failed to clear puzzle placements for {run}", ex);
            }

            // Pooled loot invariant 5: overflow items and held rares are out of world, so nothing else would ever
            // destroy them. Placed caches are in the copy and die with it. Guarded like the telemetry above:
            // nothing here may stop the eviction below.
            LootPoolDrain drained = null;

            try
            {
                drained = ThreadLootPool.DisposeForRunEnd(run);

                var unclaimed = UnclaimedPooledLootLine(run, drained);

                if (unclaimed != null)
                    log.Info(unclaimed);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] failed to dispose pooled loot for {run}", ex);
            }

            log.Info($"[DYNDUNGEON] ended {run} ({why})");

            // The one Info line that describes the run's cost, now that the per-creature and per-kill detail is at
            // Debug. Guarded like the telemetry above: nothing here may stop the eviction below.
            try
            {
                log.Info(RunSummaryLine(run, drained));
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] failed to build the run summary for {run}", ex);
            }

            var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);
            if (landblock != null)
            {
                var players = landblock.GetAllWorldObjectsForDiagnostics().OfType<Player>().ToList();

                foreach (var player in players)
                {
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat("The dungeon collapses around you.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                    var exit = EvictionExit(player, run);
                    if (exit != null)
                        WorldManager.ThreadSafeTeleport(player, new Position(exit));
                }

                // Only an EMPTY copy is queued for destruction here, and the hazard is ordering rather than
                // thread safety (the queue itself is a ConcurrentBag, LandblockManager.cs:120,843). The
                // eviction above is asynchronous - ThreadSafeTeleport lands on the next ActionQueue pass, and
                // Player.Teleport can defer itself a further second behind a fog chain on the player's own
                // landblock queue (Player_Location.cs:798-807). Unloading runs at the end of the same
                // LandblockManager.Tick, and Landblock.Unload clears that action queue and detaches players,
                // so queueing an occupied copy here would drop the teleport it was meant to follow. Left alone, an
                // occupied copy reaches the destruction queue on its own only once it has stood
                // empty for Landblock.UnloadInterval (the ShouldQueueForUnload call in the heartbeat block of
                // Landblock.TickMultiThreadedWork) - five minutes, because an Ended run supplies no
                // empty-grace override - which the eviction has just arranged.
                //
                // Rather than leave an occupied copy to that five-minute idle window, it is watched: the first 1 s
                // tick that finds it empty - the evictions landed - queues it (QueueEmptiedEndedCopies).
                QueueCopyForDestructionWhenEmpty(landblock, occupied: players.Count > 0);
            }

            OnRunEnded(run);

            // Thread-Guide fail regrant: AFTER OnRunEnded, whose gem consume is queued on the owner's action queue
            // ahead of the regrant's own delegate, so the dead guide gem is gone before the outstanding check runs.
            if (ThreadGuideFlow.ShouldRegrantOnEnd(priorState, run.Spec))
                GuideFailRegranter(run);
        }

        /// <summary>
        /// The tail of <see cref="EndRun"/>, shared with the PvP arena's EphemeralMatchSpaceProvider.Release so a
        /// finished arena match space goes through exactly this destruction path rather than a second lifecycle:
        /// an empty copy is queued for destruction now, and an occupied one is watched by
        /// <see cref="QueueEmptiedEndedCopies"/> (world thread, every 1 s tick) and queued on the first tick that
        /// finds it empty. Does not evict anyone - the caller must already have sent its players out, for the
        /// ordering reason given in EndRun. World thread, because <paramref name="occupied"/> is expected to come
        /// from a GetAllWorldObjectsForDiagnostics scan taken on it.
        /// </summary>
        internal static void QueueCopyForDestructionWhenEmpty(ACE.Server.Entity.Landblock landblock, bool occupied)
            => QueueCopyForDestructionWhenEmpty(landblock, occupied, LandblockManager.AddToDestructionQueue, copy => endedOccupiedCopies[copy.Instance] = copy);

        /// <summary>
        /// The branch of <see cref="QueueCopyForDestructionWhenEmpty(ACE.Server.Entity.Landblock, bool)"/>, with the two
        /// effects passed in. A test seam only: a Landblock cannot be built without the client dats, and the real
        /// destruction queue and watch set are process-wide statics a test must not leave entries in.
        /// </summary>
        internal static void QueueCopyForDestructionWhenEmpty<T>(T copy, bool occupied, Action<T> queueNow, Action<T> watch) where T : class
        {
            if (copy == null)
                return;

            if (!occupied)
                queueNow(copy);
            else
                watch(copy);
        }

        /// <summary>
        /// Files the run's analytics row. Called from EndRun and nowhere else, immediately after the
        /// exactly-once MarkEnded gate, so there is exactly one row per run.
        ///
        /// Everything is snapshotted HERE, on the caller's thread, and the queued payload holds no reference
        /// to the run or to any WorldObject: EndRun is about to evict players and queue the copy for
        /// destruction, and the analytics writer thread reads its queue up to a second later.
        ///
        /// The level resolved here is the END-TIME one, and it is the lesser half of the pair: the row also
        /// carries the level captured when the run opened (ThreadDungeonRun.OwnerLevelAtStart), which is
        /// the unbiased one. It is resolved through PlayerManager.FindByGuid rather than GetOnlinePlayer,
        /// because a run can end long after its owner logged out (a TTL expiry, most obviously) and
        /// FindByGuid answers for an offline character too. 0 means the character could not be resolved.
        ///
        /// Wrapped in a try/catch on purpose. EndRun is the single exit path for a run - it evicts players,
        /// destroys the gem and releases the copy - and none of that may be skipped because a telemetry
        /// projection threw.
        /// </summary>
        private static void RecordRunTelemetry(ThreadDungeonRun run, ThreadDungeonRunState priorState, string why)
        {
            try
            {
                var charLevel = PlayerManager.FindByGuid(run.OwnerGuid)?.Level ?? 0;

                // Every roster member's end level, through the same offline-capable lookup, BEFORE BuildRow
                // snapshots the roster: a member left at 0 would write level_end = 0 on their participant row.
                foreach (var member in run.Roster)
                    run.SetMemberLevelEnd(member.Guid, member.IsOwner ? charLevel : PlayerManager.FindByGuid(member.Guid)?.Level ?? 0);

                AnalyticsManager.RecordDungeonRun(DungeonRunTelemetry.BuildRow(run, priorState, why, DateTime.UtcNow, charLevel));
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] failed to record run telemetry for {run}", ex);
            }
        }

        /// <summary>
        /// The end-of-run trigger: destroys the gem, whatever ended the run (Cleared already destroyed it
        /// via AnnounceCleared -> GemDestroyer, and DestroyGem's own re-resolution makes a second call here
        /// a harmless no-op).
        ///
        /// For a GROUP run this is the ONLY destroy call: AnnounceCleared skips it (ruling R29), so the owner gem
        /// and every member key are destroyed here, at run end.
        /// </summary>
        public static void OnRunEnded(ThreadDungeonRun run)
        {
            GemDestroyer(run);
        }

        /// <summary>
        /// Cleanup layer (a): destroy the bound gem wherever it is reachable right now (owner's inventory
        /// online). TECH-DESIGN Q10 requires this to run on the HOLDER's action queue, never inline on the
        /// caller's thread - EndRun reaches here from the world thread, and mutating another player's
        /// inventory from there races the landblock tick that owns them. EnqueueAction routes the work to the
        /// owner's landblock queue, or discards it if they have already detached (WorldObject_Tick.cs:144-155).
        ///
        /// Everything the delegate needs is captured by value, and the gem is re-resolved inside it: by the
        /// time it runs the owner may have banked, dropped or traded the gem, in which case there is nothing
        /// to do here and ThreadDungeonSweeper catches it on login, on vault withdraw, or on next use
        /// (layers b and c).
        ///
        /// Idempotent by re-resolution, deliberately not latched: called from both AnnounceCleared (owner
        /// ruling A1, the gem dies the moment the run is Cleared) and OnRunEnded (so a run that ends without
        /// ever clearing still destroys it). If the owner is offline when the run clears, this call is a
        /// no-op and OnRunEnded's later call is what actually reaches the gem; if the owner is online for
        /// both, the second call simply finds no gem left to consume.
        ///
        /// Group Threads: after the owner's gem, every member key (run.MemberKeys()) whose holder is online gets
        /// the same consume, on THAT member's queue. An offline member's key falls to the lazy use and login
        /// paths, like an offline owner's gem. A solo run has no keys, so its behaviour is unchanged. Call sites:
        /// a solo run at the clear and at the end; a group run only at the end (ruling R29).
        /// </summary>
        public static void DestroyGem(ThreadDungeonRun run)
        {
            var runText = run.ToString();

            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            if (owner != null)
                EnqueueGemConsume(owner, run.GemGuid, runText);

            foreach (var (memberGuid, keyGemGuid) in run.MemberKeys())
            {
                var member = PlayerManager.GetOnlinePlayer(memberGuid);
                if (member != null)
                    EnqueueGemConsume(member, keyGemGuid, runText);
            }
        }

        /// <summary>
        /// Consumes <paramref name="gemGuid"/> from <paramref name="holder"/>'s inventory on the holder's own
        /// action queue, re-resolving the gem inside the delegate (see <see cref="DestroyGem"/>).
        /// </summary>
        private static void EnqueueGemConsume(Player holder, uint gemGuid, string runText)
        {
            holder.EnqueueAction(new ActionEventDelegate(() =>
            {
                var gem = holder.FindObject(gemGuid, Player.SearchLocations.MyInventory);
                if (gem == null)
                    return;

                var gemName = gem.Name;
                holder.TryConsumeFromInventoryWithNetworking(gem);
                holder.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your {gemName} crumbles to dust.", ACE.Entity.Enum.ChatMessageType.Broadcast));
                log.Info($"[DYNDUNGEON] destroyed gem 0x{gemGuid:X8} for {runText}");
            }));
        }
    }
}

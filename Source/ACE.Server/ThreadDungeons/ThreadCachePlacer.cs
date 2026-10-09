using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    public enum CacheRequestMode
    {
        /// <summary>Clear or late kill: top up placed caches, then form new ones for what is left.</summary>
        Auto,

        /// <summary>/spawncache with unclaimed loot: form a cache at the player, then fill it.</summary>
        Summon,

        /// <summary>/spawncache with nothing unclaimed: move every non-empty cache to the player.</summary>
        Move,
    }

    public enum CacheRequestOutcome
    {
        Queued,
        Busy,
        NotApplicable,
    }

    /// <summary>What one ThreadCachePlacer.FormAndFill pass did.</summary>
    internal sealed class CacheFormPass
    {
        /// <summary>Caches accepted, filled with at least one item, and delivered.</summary>
        public int CachesFormed { get; set; }

        /// <summary>The candidate chain found no spot; nothing was claimed.</summary>
        public bool NoRoom { get; set; }

        /// <summary>A fresh cache took no item and was destroyed; what it refused stays pooled.</summary>
        public bool FreshCacheAcceptedNothing { get; set; }
    }

    /// <summary>
    /// Delivers pooled loot into Thread Caches (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md sections 4-6).
    ///
    /// Threading. A request can come from a landblock thread (a kill, AnnounceCleared) or the world thread
    /// (ThreadDungeonManager.Tick, /spawncache). Every request takes the run's token latch and enqueues a CHAIN of
    /// actions on the run landblock's own queue (Landblock.EnqueueAction, as ThreadDungeonSpawner does), so all
    /// world-object work - materialising, creating chests, EnterWorld, filling, moving - runs on the thread
    /// that owns those objects, and never twice at once for one run (invariant 3).
    ///
    /// Batching. Each step of that chain materialises loot until its time budget is spent - it stops after the
    /// roll during which the step's Stopwatch crosses run.CacheStepBudgetMs (dynamic_dungeons_cache_step_budget_ms)
    /// - and never more than run.CacheRollBatchSize rolls (dynamic_dungeons_cache_roll_batch_size, now the hard
    /// cap), always at least one; then it re-enqueues itself, which genuinely spreads the work
    /// across world ticks: ActionQueue.RunActions snapshots Queue.Count before its loop
    /// (Entity/Actions/ActionQueue.cs:24-26), so a re-enqueued action runs on the NEXT tick. That matters because
    /// a landblock action runs inside LandblockManager's Parallel.ForEach barrier on the single world thread
    /// (Managers/LandblockManager.cs:477-494, Managers/WorldManager.cs:723), so an unbatched pass that
    /// materialised a whole run's ledger froze the entire world heartbeat for as long as it took. Same shape, and
    /// the same reason, as ThreadDungeonSpawner.PlaceBatch.
    ///
    /// The whole chain runs under ONE placement token, re-stamped each step (ThreadDungeonRun.RefreshCachePlacement)
    /// so the CachePlacementStaleAfter takeover cannot fire on a chain that is still running. Holding the token
    /// across the chain rather than re-latching per step is what keeps the ledger's kill order (invariant 1): no
    /// second delivery can interleave with a spread one.
    ///
    /// Invariant 4. Each attempt creates a FRESH empty chest (a failed AddPhysicsObj leaves a used PhysicsObj
    /// behind, DungeonPlacementFallback.cs:18-20); nothing is claimed from the pool until a chest has entered
    /// the world and passed every post-landing check.
    /// </summary>
    public static class ThreadCachePlacer
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string FormedMessage = "Your Thread Cache has formed nearby.";
        public const string NoRoomMessage = "Your Thread Cache could not find room to form. Move to open ground and use /spawncache.";

        /// <summary>Safety bound on new caches in one landblock action; what is left waits for the next trigger.</summary>
        public const int MaxCachesPerPass = 16;

        /// <summary>
        /// Hard stop on how many steps one delivery chain may take. At the 60 Hz world target 400 steps is under
        /// seven seconds, and at the default roll batch it is 4,800 rolls - an order of magnitude past the largest
        /// ledger the per-run monster caps can produce. It exists so a chain can never become the thing it was
        /// built to prevent: a run that keeps banking late kills cannot hold the placement latch indefinitely. A
        /// chain cut here marks placement wanted, so ThreadDungeonManager.Tick's retry picks up what is left.
        /// </summary>
        public const int MaxChainSteps = 400;

        /// <summary>How many of the most-missed wcids the delivery finish line names.</summary>
        internal const int TopMissWcids = 8;

        /// <summary>
        /// The " topMiss=wcid:misses,..." tail of the delivery finish line, or "" when this delivery took no miss or
        /// attribution is off. Process-wide and cumulative (see the call site).
        /// </summary>
        internal static string TopMissSuffix(long deliveryMisses)
        {
            if (deliveryMisses <= 0 || !ACE.Database.WorldDatabaseWithEntityCache.MissAttributionEnabled)
                return "";

            var top = ACE.Database.WorldDatabaseWithEntityCache.TopMissedWcids(TopMissWcids);

            if (top.Count == 0)
                return "";

            var overflow = ACE.Database.WorldDatabaseWithEntityCache.MissedWcidOverflow;

            return $" topMiss={string.Join(",", top.Select(m => $"{m.Wcid}:{m.Misses}"))}" +
                   $" topMissTracked={ACE.Database.WorldDatabaseWithEntityCache.MissedWcidCount}{(overflow > 0 ? $" topMissOverflow={overflow}" : "")}";
        }

        /// <summary>A request on behalf of the run owner: every solo caller, and the group Auto triggers, which need no requester.</summary>
        public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode)
        {
            return RequestPlacement(run, mode, run?.OwnerGuid ?? 0);
        }

        /// <summary>
        /// Group Threads (Task 10): <paramref name="requesterGuid"/> is the roster member a Summon or Move acts for. A solo
        /// run ignores it (the owner is the only player its delivery serves).
        /// </summary>
        public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode, uint requesterGuid)
        {
            if (run == null || !run.PooledLoot)
                return CacheRequestOutcome.NotApplicable;

            var landblock = LandblockManager.GetEphemeralLandblock(run.Instance);

            if (landblock == null)
                return CacheRequestOutcome.NotApplicable;

            var token = run.TryBeginCachePlacement(DateTime.UtcNow);

            if (token == 0)
                return CacheRequestOutcome.Busy;

            // The chain's own state. Every step runs on this landblock's queue, one at a time, so none of it needs
            // a lock. stepMode is the one field that changes: only the FIRST step performs the request's own mode
            // (a Summon forms the requester's cache once, a Move moves once); every later step is an Auto top-up
            // of what this chain has already placed.
            var stepMode = mode;
            var steps = 0;
            var totalMs = 0L;
            var maxStepMs = 0L;
            var totalRolls = 0L;
            var totalMisses = 0L;
            var scrollMissesBefore = ACE.Database.WorldDatabaseWithEntityCache.ScrollWeenieMissesOnCurrentThread;

            // Miss ATTRIBUTION (which wcids), read off the tunable once per chain: the recording itself is one
            // ConcurrentDictionary update on a path that is about to run a synchronous world-DB read, so it is free
            // next to the miss, but the switch is here for a shard that wants none of it.
            ACE.Database.WorldDatabaseWithEntityCache.MissAttributionEnabled =
                PropertyManager.GetBool("dynamic_dungeons_loot_miss_attribution", true).Item;

            run.Perf.RecordLootChain();

            void DeliverBatch()
            {
                var more = false;
                var requeued = false;
                var watch = Stopwatch.StartNew();
                var missesBefore = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread;

                try
                {
                    try
                    {
                        // A superseded token (the latch went stale and another request took over) does nothing.
                        if (run.IsCachePlacementCurrent(token))
                        {
                            run.RefreshCachePlacement(token, DateTime.UtcNow);

                            // TIME is the step's unit (dynamic_dungeons_cache_step_budget_ms, read off this step's
                            // own Stopwatch); the roll batch size stays as the hard cap. See ThreadLootRollBudget.
                            var budget = new ThreadLootRollBudget(run.CacheRollBatchSize, () => watch.Elapsed.TotalMilliseconds, run.CacheStepBudgetMs);

                            Execute(run, landblock, stepMode, requesterGuid, budget);

                            totalRolls += budget.Spent;

                            // The ThreadDungeonSpawner.PlaceBatch contract: a chain continues ONLY because this
                            // step ran out of budget (time or roll cap) with loot still pooled. A step that stopped for any other
                            // reason - nothing left, no room to form, a fresh cache that refused everything, the
                            // run no longer Cleared - is terminal, and the existing retries (MarkPlacementWanted
                            // plus the Tick, /spawncache) own whatever is left, exactly as before the batching.
                            more = budget.Exhausted && run.HasUnclaimedLoot;

                            stepMode = CacheRequestMode.Auto;
                        }
                    }
                    catch (Exception ex)
                    {
                        // EnqueueAction escapes the caller's try/catch, so this guard is the chain's only one. A
                        // throwing step ends the chain rather than retrying into the same failure at 60 Hz.
                        log.Error($"[DYNDUNGEON] {run} cache delivery ({mode}) threw", ex);
                        more = false;
                    }

                    steps++;

                    // The display sort (solo only), run ONCE at the terminal step of the chain rather than per
                    // batch: loot arrives across ticks, so a per-batch sort could only ever order the part that
                    // had landed. `terminal` is the exact negation of the re-enqueue test below - it is computed
                    // here, and the re-enqueue reads it, so the two can never drift apart.
                    //
                    // It sits INSIDE the step's own Stopwatch on purpose: its cost then lands in totalMs,
                    // maxStepMs, ServerMetrics.RecordThreadsLootStep and run.Perf.RecordLootStep like any other
                    // work this step did, instead of being invisible to the budget accounting it is charged
                    // against. sortMs on the finish line is the same work measured on its own.
                    //
                    // SortGuarded, never SortRunCaches directly: this call site is OUTSIDE the step guard
                    // above, so a throw here would skip MarkPlacementWanted below - see SortGuarded's own
                    // doc comment for what that would cost.
                    var terminal = !(more && steps < MaxChainSteps);
                    var sort = terminal ? SortGuarded(run) : default;

                    watch.Stop();
                    totalMs += watch.ElapsedMilliseconds;
                    maxStepMs = Math.Max(maxStepMs, watch.ElapsedMilliseconds);

                    // Per-step instrumentation. Misses are read on THIS thread only, so another landblock's
                    // materialisation is never charged to this run.
                    var stepMisses = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread - missesBefore;
                    totalMisses += stepMisses;
                    ServerMetrics.RecordThreadsLootStep(watch.Elapsed.TotalMilliseconds);
                    run.Perf.RecordLootStep(watch.ElapsedMilliseconds, stepMisses);

                    if (!terminal)
                    {
                        landblock.EnqueueAction(new ActionEventDelegate(DeliverBatch));
                        requeued = true;
                        return;
                    }

                    // Cut by the step cap (or by a re-enqueue that threw) with loot still pooled: hand the rest to
                    // the Tick retry rather than dropping it.
                    if (more)
                        run.MarkPlacementWanted();

                    // Scroll misses are counted separately because GetScrollWeenie is a different cache from the wcid
                    // one, and its misses were invisible to weenieMiss entirely (2026-09-19). The top-miss list is
                    // PROCESS-WIDE and cumulative, not this run's: it answers "which weenies is this shard missing",
                    // which is what a warm-up gap looks like.
                    var scrollMisses = ACE.Database.WorldDatabaseWithEntityCache.ScrollWeenieMissesOnCurrentThread - scrollMissesBefore;

                    log.Info($"[DYNDUNGEON] {run} cache delivery ({mode}) finished: steps={steps} rolls={totalRolls} totalMs={totalMs} maxStepMs={maxStepMs} weenieMiss={totalMisses} scrollMiss={scrollMisses} budgetMs={run.CacheStepBudgetMs} rollCap={run.CacheRollBatchSize} unclaimed={run.HasUnclaimedLoot}{TopMissSuffix(totalMisses)}{ThreadCacheSort.LogSuffix(sort)}");
                }
                finally
                {
                    // Released by the TERMINAL step only: the chain is one delivery (invariant 3). A step that
                    // re-enqueued keeps the latch for its successor; anything else here, a failed EnqueueAction
                    // included, must release it rather than leave the run latched until the 30 s takeover.
                    if (!requeued)
                        run.EndCachePlacement(token);
                }
            }

            landblock.EnqueueAction(new ActionEventDelegate(DeliverBatch));

            return CacheRequestOutcome.Queued;
        }

        /// <summary>
        /// The terminal step's display sort, guarded. NEVER throws.
        ///
        /// Its call site sits after the step's own try/catch and before `if (more) run.MarkPlacementWanted();`,
        /// which is the line that hands a chain cut at <see cref="MaxChainSteps"/> to ThreadDungeonManager.Tick's
        /// retry. An unguarded throw out of the sort would skip that line and the finish log, and
        /// ActionQueue.RunActions would swallow and log it - so the world would survive, but the run would sit
        /// with IsPlacementWanted false and loot still pooled, waiting for an unrelated kill to start a fresh
        /// chain. The sort is presentation only; it is never worth that.
        ///
        /// A throw therefore degrades to "contents left in arrival order, delivery completes normally", which
        /// is exactly the kill switch's behaviour for that one pass.
        /// </summary>
        internal static CacheSortPass SortGuarded(ThreadDungeonRun run)
        {
            try
            {
                return ThreadCacheSort.SortRunCaches(run);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} cache display sort threw; contents are left in arrival order", ex);

                return default;
            }
        }

        private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)
        {
            // Group Threads (ruling R19): a group run deals personal piles and delivers per member, in its own file. The
            // solo body below is untouched.
            if (run.IsGroup)
            {
                ThreadGroupCacheDelivery.Execute(run, landblock, mode, requesterGuid, budget);
                return;
            }

            if (run.State != ThreadDungeonRunState.Cleared)
                return;

            // Re-read every step of the chain, never cached across steps: the owner can log out or walk out between
            // one batch and the next, and a run can end (ThreadDungeonSpawner.PlaceBatch models the same re-test).
            var owner = PlayerManager.GetOnlinePlayer(run.OwnerGuid);
            var ownerInside = IsOwnerInside(run, owner);

            if (mode == CacheRequestMode.Move)
            {
                if (ownerInside)
                    MoveCaches(run, landblock, owner);

                // Loot banked after /spawncache chose Move (a kill whose own trigger found this latch held), or overflow a
                // transfer produced, has no other trigger.
                if (run.HasUnclaimedLoot)
                    run.MarkPlacementWanted();

                return;
            }

            // BUILD (owner ruling 2026-09-22): materialise the shared pool into the owner's held pile,
            // budget-limited, never touching a cache directly - see ThreadCacheFiller.BuildPile. Runs for Auto
            // and Summon alike, exactly where the old per-cache Fill used to draw from the ledger and bonus.
            //
            // ledgerSnapshot (item 1a fix, 2026-09-22): the ledger count taken BEFORE the build, so a late kill
            // that appends to the ledger while this very call is draining it - or one that lands between this
            // step and the next of a multi-step chain - is never counted toward what THIS segment waits for.
            // ledgerClaimed, what BuildPile actually drained of that snapshot, is what the gate below reads
            // instead of run.HasUndealtLoot's raw ledger clause.
            var ledgerSnapshot = run.LedgerCount;
            var ledgerClaimed = 0;

            if (run.HasUndealtLoot && !budget.Exhausted)
                ledgerClaimed = ThreadCacheFiller.BuildPile(run, budget, ledgerSnapshot);

            // PLACEMENT WAITS FOR THE SNAPSHOT, NOT FOR EVERY LATER KILL (owner ruling 2026-09-22; snapshot gate,
            // item 1a fix, 2026-09-22). ledgerClaimed < ledgerSnapshot means the budget ran out before this
            // segment's OWN ledger entries were fully built - keep waiting exactly as before (the chain's own
            // continuation test, budget.Exhausted && run.HasUnclaimedLoot, brings a later step back). Once the
            // snapshot is satisfied, HasUndealtNonLedgerLoot is the only remaining "still building" signal
            // (bonus, overflow and the deal buffers are never fed by a late kill the way the ledger is, so they
            // still gate on the whole pending amount); a late kill's own ledger entry, appended to the ledger
            // AFTER the snapshot, is left for this run's NEXT placement pass rather than blocking this one.
            // MarkPlacementWanted covers the case where this step's budget is not exhausted (so the chain would
            // otherwise treat "still building" as terminal) or the owner has left.
            if (ledgerClaimed < ledgerSnapshot || run.HasUndealtNonLedgerLoot)
            {
                run.MarkPlacementWanted();
                return;
            }

            if (!run.HasHeldPile(run.OwnerGuid))
                return;

            if (!ownerInside)
            {
                // Retried by ThreadDungeonManager.Tick the next time the owner is seen inside.
                run.MarkPlacementWanted();
                return;
            }

            if (mode == CacheRequestMode.Auto)
            {
                // Spec section 4 trigger: top up caches already standing before forming a new one. Reuses the
                // group pile-placement path unchanged (ThreadGroupCacheDelivery.FillFromPile): a solo run's owner
                // is a roster member like any other, so the same merge, sort and top-up apply with no new code.
                foreach (var cache in run.PlacedCachesSnapshot())
                {
                    if (!run.HasHeldPile(run.OwnerGuid))
                        break;

                    Deliver(run, cache, FillDeliveringLanded(run, cache, (r, c) => ThreadGroupCacheDelivery.FillFromPile(r, c, run.OwnerGuid), (c, landed) => Deliver(run, c, landed)));
                }

                if (!run.HasHeldPile(run.OwnerGuid))
                    return;
            }

            // Invariant 4 lives in FormAndFill: a chest is filled only after an attempt accepted it. Reuses
            // FormAndFillAtMember, the exact function a group member's pass runs, rather than re-wiring
            // candidates/placement/fill for solo a second time.
            var pass = FormAndFillAtMember(run, landblock, owner, () => run.HasHeldPile(run.OwnerGuid));

            // A fresh, EMPTY cache refused everything left in the pile, so no cache ever will: destroyed rather
            // than left to hold the copy alive for nothing (the same fix group's ServeMember applies).
            if (pass.FreshCacheAcceptedNothing)
                ThreadGroupCacheDelivery.DestroyUnplaceable(run, run.OwnerGuid);

            // Spec section 5: one success line per delivery pass, however many caches it formed.
            if (pass.CachesFormed > 0)
                owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(FormedMessage, ChatMessageType.Broadcast));

            // Total failure: nothing was claimed (invariant 4). No periodic retry; /spawncache is the fallback.
            if (pass.NoRoom)
                owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(NoRoomMessage, ChatMessageType.Broadcast));
        }

        /// <summary>
        /// The form-and-fill loop, with the world behind seams so invariant 4 is testable with real Containers. Each
        /// iteration asks <paramref name="tryPlaceEmpty"/>, through PlaceFirstAccepted over
        /// <paramref name="candidatesForNextCache"/>, for an accepted EMPTY chest, and only then calls
        /// <paramref name="fill"/>: a pass that finds no room claims and creates nothing. A fresh cache that accepted
        /// nothing is unregistered, destroyed and logged, and ends the pass so it cannot spin. Sends no chat; the
        /// caller tells the owner once per pass.
        ///
        /// A fill that throws partway still delivers what it already landed (FillDeliveringLanded); a fresh cache the
        /// throw left empty is destroyed the same way; then the exception propagates to the action's catch.
        ///
        /// <paramref name="hasMore"/> is the loop's "is there anything left to deliver" test: run.HasUnclaimedLoot for the
        /// solo overload below, one member's held pile for a group member's pass. Asked before every new cache.
        ///
        /// <paramref name="onFreshCacheAcceptedNothing"/>, when given, replaces the solo log line for a fresh cache that
        /// accepted nothing; the group pass handles and logs that case itself (its items are not "left pooled").
        /// </summary>
        internal static CacheFormPass FormAndFill(ThreadDungeonRun run, int maxCaches, Func<bool> hasMore, Func<IReadOnlyList<CacheCandidate>> candidatesForNextCache,
            Func<CacheCandidate, Container> tryPlaceEmpty, Func<ThreadDungeonRun, Container, CacheFillResult> fill, Action<Container, CacheFillResult> deliver,
            Action onFreshCacheAcceptedNothing = null)
        {
            var pass = new CacheFormPass();

            while (pass.CachesFormed < maxCaches && hasMore())
            {
                var chest = PlaceFirstAccepted(candidatesForNextCache(), tryPlaceEmpty);

                if (chest == null)
                {
                    pass.NoRoom = true;
                    break;
                }

                CacheFillResult result;

                try
                {
                    result = FillDeliveringLanded(run, chest, fill, deliver);
                }
                catch
                {
                    if (chest.Inventory.Count == 0)
                    {
                        run.UnregisterCache(chest);
                        chest.Destroy();
                        log.Warn($"[DYNDUNGEON] {run} a fresh cache was destroyed empty after its fill threw");
                    }

                    throw;
                }

                if (result.Added.Count == 0)
                {
                    // Everything left was refused by an empty cache (e.g. pack-slot items only). Do not litter.
                    run.UnregisterCache(chest);
                    chest.Destroy();
                    pass.FreshCacheAcceptedNothing = true;

                    if (onFreshCacheAcceptedNothing != null)
                        onFreshCacheAcceptedNothing();
                    else
                        log.Warn($"[DYNDUNGEON] {run} a fresh cache accepted nothing and was destroyed; {run.OverflowCount} overflow item(s) left pooled");

                    break;
                }

                deliver(chest, result);
                pass.CachesFormed++;
            }

            return pass;
        }

        /// <summary>
        /// The solo loop: the overload above, running while the run has unclaimed loot AND the delivery step still
        /// has roll budget. The budget clause is what stops a step from standing up a cache it has nothing left to
        /// fill; a null budget is unbudgeted, for tests that assert on a whole pool at once. Declared after the
        /// shared loop on purpose: ThreadCachePlacerTests reads the FIRST FormAndFill body in this file as the loop
        /// body.
        /// </summary>
        internal static CacheFormPass FormAndFill(ThreadDungeonRun run, int maxCaches, Func<IReadOnlyList<CacheCandidate>> candidatesForNextCache,
            Func<CacheCandidate, Container> tryPlaceEmpty, Func<ThreadDungeonRun, Container, CacheFillResult> fill, Action<Container, CacheFillResult> deliver,
            ThreadLootRollBudget budget = null)
        {
            var step = budget ?? ThreadLootRollBudget.Unlimited();

            return FormAndFill(run, maxCaches, () => run.HasUnclaimedLoot && !step.Exhausted, candidatesForNextCache, tryPlaceEmpty, fill, deliver);
        }

        /// <summary>
        /// Runs one fill. If it throws partway, the items it already added are in the cache and any rare among them
        /// has landed, so that partial result (ThreadCacheFiller.PartialResultOf) is delivered - the live update for
        /// an open cache and the rare broadcasts - before the original exception propagates unchanged.
        /// </summary>
        internal static CacheFillResult FillDeliveringLanded(ThreadDungeonRun run, Container cache,
            Func<ThreadDungeonRun, Container, CacheFillResult> fill, Action<Container, CacheFillResult> deliver)
        {
            try
            {
                return fill(run, cache);
            }
            catch (Exception ex)
            {
                var landed = ThreadCacheFiller.PartialResultOf(ex);

                if (landed != null && landed.Added.Count > 0)
                {
                    try
                    {
                        deliver(cache, landed);
                    }
                    catch (Exception deliverEx)
                    {
                        // Logged here so the fill's own exception, the one that explains the failure, is what propagates.
                        log.Error($"[DYNDUNGEON] {run} delivering a partial fill of cache 0x{cache.Guid.Full:X8} threw", deliverEx);
                    }
                }

                throw;
            }
        }

        /// <summary>
        /// Group Threads invariant 1: a forward to ThreadRunPresence.IsMemberInside, the one inside test. Kept under this
        /// name because source-text pins name the call. Callers pass a roster member: the run owner, or an
        /// empty-grace warning recipient. Every such member is on the roster, so the added roster clause changes
        /// no answer.
        /// </summary>
        internal static bool IsOwnerInside(ThreadDungeonRun run, Player owner)
            => ThreadRunPresence.IsMemberInside(run, owner);

        /// <summary>
        /// Group Threads (Task 10): one member's form-and-fill pass, at that member, from that member's held pile. The
        /// solo attempt (TryPlaceEmpty), candidates and exclusions measured from the member, and the solo live delivery.
        /// Each accepted chest is stamped for the member (TryPlaceEmpty stamps the player it forms at). Sends no chat.
        /// </summary>
        internal static CacheFormPass FormAndFillAtMember(ThreadDungeonRun run, Landblock landblock, Player member, Func<bool> hasMore)
        {
            var memberGuid = member.Guid.Full;
            var scan = new ExclusionScan(run, landblock);
            List<(float X, float Y, float Z)> exclusions = null;

            return FormAndFill(run, MaxCachesPerPass, hasMore,
                () => BuildCandidatesAt(run, scan, member, out exclusions),
                c => TryPlaceEmpty(run, member, c, exclusions),
                (r, cache) => ThreadGroupCacheDelivery.FillFromPile(r, cache, memberGuid),
                (cache, result) => Deliver(run, cache, result),
                () => { });
        }

        /// <summary>Group Threads (Task 10): the /spawncache move for one member, over that member's caches only.</summary>
        internal static void MoveMemberCaches(ThreadDungeonRun run, Landblock landblock, Player member) => MoveCaches(run, landblock, member);

        /// <summary>Group Threads (Task 10): the solo live delivery and rare broadcast, for a group top-up.</summary>
        internal static void DeliverFill(ThreadDungeonRun run, Container cache, CacheFillResult result) => Deliver(run, cache, result);

        private static Chest PlaceNewCache(ThreadDungeonRun run, ExclusionScan scan, Player owner)
        {
            var candidates = BuildCandidatesAt(run, scan, owner, out var exclusions);

            return PlaceFirstAccepted(candidates, c => TryPlaceEmpty(run, owner, c, exclusions)) as Chest;
        }

        /// <summary>The section 5 candidates from the owner's position, and the exclusions they were filtered by.</summary>
        private static IReadOnlyList<CacheCandidate> BuildCandidatesAt(ThreadDungeonRun run, ExclusionScan scan, Player owner, out List<(float X, float Y, float Z)> exclusions)
        {
            exclusions = scan.Collect();
            var at = owner.Location;

            return ThreadCachePlacement.BuildCandidates(at.Cell, at.PositionX, at.PositionY, at.PositionZ, at.RotationZ, at.RotationW,
                run.Dungeon?.Points, exclusions);
        }

        /// <summary>Tries candidates in order; returns the first accepted, EMPTY container, or null.</summary>
        internal static Container PlaceFirstAccepted(IReadOnlyList<CacheCandidate> candidates, Func<CacheCandidate, Container> tryPlaceEmpty)
        {
            if (candidates == null || tryPlaceEmpty == null)
                return null;

            foreach (var candidate in candidates)
            {
                var placed = tryPlaceEmpty(candidate);

                if (placed == null)
                    continue;

                if (placed.Inventory.Count != 0)
                    throw new InvalidOperationException("Invariant 4: a Thread Cache must enter the world empty");

                return placed;
            }

            return null;
        }

        /// <summary>One attempt: a fresh chest, every acceptance rule from spec section 5, destroyed on any failure.</summary>
        private static Container TryPlaceEmpty(ThreadDungeonRun run, Player owner, CacheCandidate candidate, IReadOnlyList<(float X, float Y, float Z)> exclusions)
        {
            // Argument order as ThreadDungeonRewardSpawner.TrySpawnBossCache: rotation w LAST.
            var position = new Position(candidate.Cell, candidate.X, candidate.Y, candidate.Z, 0f, 0f, candidate.QZ, candidate.QW, run.Instance);

            if (candidate.RecomputeCell)
                position.LandblockId = new LandblockId(position.GetCell());

            if (position.LandblockId.Landblock != run.Dungeon.Landblock)
                return null;

            if (!owner.IsDirectVisible(position))
                return null;

            // A cache belongs to the player it forms at. The run owner (every solo cache) takes the owner-stamped factory
            // exactly as before; a group member's cache is stamped for that member (Task 7's three-argument factory).
            var chest = owner.Guid.Full == run.OwnerGuid
                ? ThreadDungeonRewardSpawner.CreateCacheChest(run, "pooled cache")
                : ThreadDungeonRewardSpawner.CreateCacheChest(run, "pooled cache", owner.Guid.Full);

            if (chest == null)
                return null;

            chest.Location = position;

            // User ruling 2026-09-14: a pooled cache is walk-through, so a cache formed in a one-cell corridor can
            // never block the way out, and it may form overlapping the owner or a creature. Set BEFORE EnterWorld:
            // InitPhysicsObj builds the physics state from the property (CalculatedPhysicsState). Walls, floors and
            // Static objects still collide (plan Verified facts Q1b). Pooled caches only - the boss-death cache
            // stays solid, which is why this is here and not in CreateCacheChest.
            chest.Ethereal = true;

            // A rejected attempt must not flash; the accepted chest plays the effect below.
            chest.SuppressGenerateEffect = true;

            if (!chest.EnterWorld())
            {
                chest.Destroy();
                return null;
            }

            // AddPhysicsObj synced Location from physics, so this is where the chest actually came to rest.
            var landed = chest.Location;

            // Everything physics could have changed is re-checked on the resting spot: still on this run's landblock,
            // still within 2 m of the owner's height, still 1.5 m clear of every exclusion.
            if (landed == null || landed.LandblockId.Landblock != run.Dungeon.Landblock
                || Math.Abs(landed.PositionZ - owner.Location.PositionZ) > ThreadCachePlacement.MaxZDelta
                || ThreadCachePlacement.IsExcluded(landed.PositionX, landed.PositionY, landed.PositionZ, exclusions))
            {
                chest.Destroy();
                return null;
            }

            if (!run.TryRegisterCache(chest))
            {
                chest.Destroy();
                return null;
            }

            chest.ApplyVisualEffects(PlayScript.Create);

            return chest;
        }

        /// <summary>
        /// The placement exclusions for one pass, split by how often each half can actually change.
        ///
        /// The exit-portal half needs Landblock.GetAllWorldObjectsForDiagnostics, which copies the landblock's
        /// whole object dictionary to a list (Entity/Landblock.cs:1798-1803). Nothing a pass itself does creates,
        /// moves or destroys this run's summoned exit portal - that is ThreadDungeonRewardSpawner.TrySummonExit,
        /// once, from AnnounceCleared, before the first delivery is even requested - so re-running that scan for
        /// EVERY cache attempt, which is what a per-attempt CollectExclusions did, bought nothing. It is scanned
        /// once, lazily, and reused for the rest of the pass; a fresh scan starts with each delivery step. The
        /// worst a stale portal set could cost is the 1.5 m spacing nicety for the remaining caches of ONE pass,
        /// not an invariant: nothing downstream depends on the exclusion list being complete.
        ///
        /// The placed-cache half IS re-read per attempt, and must be: a cache this pass just formed has to exclude
        /// the next one, which is the 1.5 m rule the old comment on the loop describes.
        ///
        /// One instance belongs to one pass on one landblock thread, so it needs no synchronisation.
        /// </summary>
        private sealed class ExclusionScan
        {
            private readonly ThreadDungeonRun run;
            private readonly Landblock landblock;
            private List<(float X, float Y, float Z)> portals;

            public ExclusionScan(ThreadDungeonRun run, Landblock landblock)
            {
                this.run = run;
                this.landblock = landblock;
            }

            /// <summary>Placed caches and this run's summoned exit portal(s): nothing forms within 1.5 m of them.</summary>
            public List<(float X, float Y, float Z)> Collect()
            {
                portals ??= CollectExitPortals(run, landblock);

                var list = new List<(float X, float Y, float Z)>();

                foreach (var cache in run.PlacedCachesSnapshot())
                    if (cache.Location != null)
                        list.Add((cache.Location.PositionX, cache.Location.PositionY, cache.Location.PositionZ));

                list.AddRange(portals);

                return list;
            }
        }

        /// <summary>This run's summoned exit portal(s). One full landblock object scan; see <see cref="ExclusionScan"/>.</summary>
        private static List<(float X, float Y, float Z)> CollectExitPortals(ThreadDungeonRun run, Landblock landblock)
        {
            var list = new List<(float X, float Y, float Z)>();

            foreach (var wo in landblock.GetAllWorldObjectsForDiagnostics())
            {
                if (wo is Portal && wo.WeenieClassId == ThreadDungeonRewardSpawner.SummonedExitWcid
                    && wo.GetProperty(PropertyInt.ThreadDungeonRunId) == (int)run.RunId && wo.Location != null)
                {
                    list.Add((wo.Location.PositionX, wo.Location.PositionY, wo.Location.PositionZ));
                }
            }

            return list;
        }

        /// <summary>
        /// Live update for an open cache (mirrors Container.SendInventory, Container.cs:788-812) and the rare
        /// broadcast Corpse.EnterWorld makes (Corpse.cs:357-365), fired when the rare lands.
        /// </summary>
        private static void Deliver(ThreadDungeonRun run, Container cache, CacheFillResult result)
        {
            if (result.Added.Count > 0 && cache.IsOpen)
            {
                var viewer = PlayerManager.GetOnlinePlayer(cache.Viewer);

                if (viewer?.Session != null)
                {
                    viewer.Session.Network.EnqueueSend(new GameEventViewContents(viewer.Session, cache));

                    foreach (var item in result.Added)
                        viewer.Session.Network.EnqueueSend(new GameMessageCreateObject(item));
                }
            }

            foreach (var landed in result.RaresLanded)
            {
                cache.EnqueueBroadcast(new GameMessageSystemChat(string.Format(Corpse.RareDiscoveredFormat, landed.FinderName, landed.Rare.Name), ChatMessageType.System));
                cache.ApplySoundEffects(Sound.TriggerActivated, 10);
            }

            // Debug, not Info: a time-budgeted chain fills caches once per step, so this is dozens to hundreds of
            // lines per clear. The chain's finished line and the run summary carry the totals at Info.
            if ((result.Added.Count > 0 || result.Overflowed > 0 || result.Destroyed > 0) && log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] {run} cache 0x{cache.Guid.Full:X8} +{result.Added.Count} item(s) entries={result.EntriesClaimed} bonus={result.BonusClaimed} overflowed={result.Overflowed} destroyed={result.Destroyed} ms={result.ElapsedMs} weenieMiss={result.WeenieMisses}");
        }

        /// <summary>
        /// Spec section 6 move: for each non-empty placed cache, form a new EMPTY cache at the owner, then move
        /// the items, then destroy the emptied old cache. A failed placement leaves that old cache untouched.
        /// </summary>
        private static void MoveCaches(ThreadDungeonRun run, Landblock landblock, Player owner)
        {
            var moved = 0;
            var scan = new ExclusionScan(run, landblock);

            foreach (var old in run.PlacedCachesSnapshot())
            {
                if (old.Inventory.Count == 0)
                    continue;

                // Group Threads: a member moves only their own caches. A solo run never takes this branch.
                if (run.IsGroup && !ThreadLootPool.IsCacheFor(old, owner.Guid.Full))
                    continue;

                var fresh = PlaceNewCache(run, scan, owner);

                if (fresh == null)
                {
                    owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(NoRoomMessage, ChatMessageType.Broadcast));
                    break;
                }

                if (old.IsOpen && old is Chest openChest)
                {
                    var viewer = PlayerManager.GetOnlinePlayer(old.Viewer);

                    // tryReset false: Chest.Reset would ClearUnmanagedInventory (Chest.cs:287).
                    if (viewer != null)
                        openChest.Close(viewer, false);
                }

                ThreadCacheFiller.TransferContents(run, old, fresh);

                if (old.Inventory.Count == 0)
                {
                    run.UnregisterCache(old);
                    old.Destroy();
                }
                else
                {
                    log.Error($"[DYNDUNGEON] {run} move left {old.Inventory.Count} item(s) in cache 0x{old.Guid.Full:X8}; it stays placed");
                }

                moved++;
            }

            if (moved > 0)
                owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(FormedMessage, ChatMessageType.Broadcast));
        }
    }
}

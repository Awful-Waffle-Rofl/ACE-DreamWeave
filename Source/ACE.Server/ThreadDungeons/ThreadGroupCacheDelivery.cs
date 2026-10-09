using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Group Threads pooled-loot delivery (rulings R19, R20, R21; Task 10). A group run never fills a cache straight
    /// from the shared pool. Each pass first DEALS: it claims up to <see cref="MaxEntriesPerDeal"/> ledger entries, the
    /// pending boss bonus and the shared overflow, materialises them, and hands every item to exactly one roster
    /// member's held pile. Then each member's pile is delivered into caches that belong to that member alone.
    ///
    /// Threading. Everything here runs inside ThreadCachePlacer's latched landblock action chain (Execute is only
    /// reached from ThreadCachePlacer.Execute), so dealing, materialising, chest creation and filling all run on the
    /// thread that owns the copy's world objects, and never twice at once for one run. Pile state is the run's, under
    /// its stateLock.
    ///
    /// Batching. One Execute call is one STEP of that chain, and <see cref="Deal"/> claims and materialises one
    /// claim at a time until the step's ThreadLootRollBudget is spent - by TIME in production (one roll per claim,
    /// stopping after the roll that crosses dynamic_dungeons_cache_step_budget_ms), with the roll cap as a hard
    /// bound - a tighter bound than <see cref="MaxRollsPerDeal"/>, which stays as the per-deal ceiling for any
    /// caller that passes no budget. A fat ledger entry and the pooled half of the boss bonus are both consumed a
    /// slice at a time across steps. The chain runs another step on the next world tick while undealt loot remains,
    /// which is what turned the old once-a-second retrigger off ThreadDungeonManager.Tick into a 60 Hz one. What a
    /// step builds waits in the run's deal buffer until a full set is ready (<see cref="DealFloor"/>, and the whole
    /// pooled boss half), so the value-sorted snake deal still balances over sets the size #1210 dealt.
    ///
    /// Conservation. An item is out of the world from the moment it is materialised until it lands in a cache, and in
    /// that window it is always held by exactly one of: this method's local lists, the run's deal buffers, one member's
    /// held pile, or (once the run has Ended and a buffer add or hand-off is refused) destroyed on the spot; the run-end
    /// drain destroys whatever is still buffered. A refused cache add returns the item to the
    /// SAME member's pile with its original deal round, never to the shared overflow.
    ///
    /// Retry flags (ruling R20). Every served member's delivery-wanted flag is cleared first and set again only for a
    /// member who still holds a pile and is NOT inside. A member inside whose pass found no room is not flagged, so
    /// ThreadDungeonManager.Tick's once-a-second group retry does not re-fire for them; /spawncache and the next
    /// arrival are the fallback. The run-level placement flag is set only while undealt loot remains.
    /// </summary>
    public static class ThreadGroupCacheDelivery
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Ledger entries one deal claims. The rest waits for the next pass (run-level placement wanted).</summary>
        public const int MaxEntriesPerDeal = 60;

        /// <summary>
        /// Final review F3: the sum of ThreadLootLedgerEntry.Rolls one deal claims, alongside <see cref="MaxEntriesPerDeal"/>.
        /// A group kill can carry up to ThreadLootPool.MaxRollsPerKill rolls, so the entry cap alone does not bound how
        /// much one landblock action materialises. The first entry of a deal is always claimed, whatever its rolls.
        /// </summary>
        public const int MaxRollsPerDeal = 120;

        /// <summary>
        /// The deal floor (code review of #1212, finding 2): how many LEDGER rolls must sit in the run's deal buffer
        /// before a deal hands them out, unless the ledger is empty or the deal hit a per-deal cap. It is the step's
        /// roll cap (dynamic_dungeons_cache_roll_batch_size, default 12 - ThreadLootRollBudget.DefaultRollsPerStep),
        /// bounded by <see cref="MaxRollsPerDeal"/>.
        ///
        /// Why that number. Under #1210 every delivery step dealt what it claimed, and a step claimed up to the roll
        /// cap, so #1210's group deal value-sorted and snake-dealt sets of about 12 rolls. The time budget cuts a step
        /// to however many rolls fit in 8 ms (often 1-3), and dealing those alone would all but lose the within-set
        /// balancing. Buffering to the roll cap restores exactly #1210's resolution under any tuning of the batch
        /// size, and the budget=250 control (where every step claims the full roll cap) deals every step, as #1210 did.
        /// </summary>
        internal static int DealFloor(ThreadLootRollBudget budget)
            => Math.Max(1, Math.Min(budget?.MaxRolls ?? MaxRollsPerDeal, MaxRollsPerDeal));

        /// <summary>
        /// Test seam for the DUPLICATED half of the boss bonus - the Trade Note stack and the salvage bags, one
        /// full set per deal seat. Production is ThreadDungeonRewardSpawner.BuildBossBonusPerSeatItems(run, B).
        /// A group run never reaches ThreadCacheFiller's one-argument BonusBuilder, which is the solo path and
        /// still builds all three components together.
        /// </summary>
        internal static Func<ThreadDungeonRun, double, List<WorldObject>> BonusBuilder = ThreadDungeonRewardSpawner.BuildBossBonusPerSeatItems;

        /// <summary>
        /// Test seam for the POOLED half - the Legendary-table rolls, built once for the whole deal. Production is
        /// ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls(run, seats * B, cap * seats).
        /// </summary>
        internal static Func<ThreadDungeonRun, double, int, List<WorldObject>> PooledRollBuilder = ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls;

        /// <summary>
        /// Test seam for COUNTING the pooled half, which is what makes it resumable across delivery steps (see
        /// <see cref="Deal"/>). Production is ThreadDungeonRewardSpawner.BossBonusPooledRollCount(run, seats * B, cap * seats).
        /// </summary>
        internal static Func<ThreadDungeonRun, double, int, int> PooledRollCounter = ThreadDungeonRewardSpawner.BossBonusPooledRollCount;

        /// <summary>The world one delivery pass touches, behind seams so the pass is testable without a Player or a landblock.</summary>
        internal sealed class DeliveryWorld
        {
            /// <summary>Is this roster member online and inside the run (ThreadRunPresence.IsMemberInside)?</summary>
            public Func<uint, bool> IsInside;

            /// <summary>One form-and-fill pass at the member while the predicate holds; null when the member cannot be found.</summary>
            public Func<uint, Func<bool>, CacheFormPass> FormAt;

            /// <summary>The /spawncache move for one member.</summary>
            public Action<uint> MoveFor;

            /// <summary>A chat line to one member.</summary>
            public Action<uint, string> Tell;

            /// <summary>The live update and rare broadcast for one filled cache.</summary>
            public Action<Container, CacheFillResult> Deliver;

            public Func<ThreadLootLedgerEntry, List<WorldObject>> Materialize;

            /// <summary>The duplicated half of the boss bonus, per seat: Trade Notes and salvage bags.</summary>
            public Func<ThreadDungeonRun, double, List<WorldObject>> BuildBonus;

            /// <summary>The pooled half: the Legendary-table rolls. (run, factor, cap); the cap is the batch size when <see cref="CountPooledRolls"/> is set.</summary>
            public Func<ThreadDungeonRun, double, int, List<WorldObject>> BuildPooledRolls;

            /// <summary>How many pooled rolls a claimed bonus is worth, (run, factor, cap). Null builds the pooled half whole, in one deal.</summary>
            public Func<ThreadDungeonRun, double, int, int> CountPooledRolls;
        }

        /// <summary>
        /// The group branch of ThreadCachePlacer.Execute, on the run landblock's thread. One call is ONE step of
        /// that delivery chain: <paramref name="budget"/> is how many loot rolls this step may materialise before
        /// the chain re-enqueues itself (see ThreadCachePlacer for why).
        /// </summary>
        internal static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)
        {
            if (run == null || landblock == null)
                return;

            Run(run, mode, requesterGuid, LiveWorld(run, landblock), budget);
        }

        private static DeliveryWorld LiveWorld(ThreadDungeonRun run, Landblock landblock)
        {
            return new DeliveryWorld
            {
                IsInside = guid => ThreadRunPresence.IsMemberInside(run, PlayerManager.GetOnlinePlayer(guid)),
                FormAt = (guid, hasMore) =>
                {
                    var member = PlayerManager.GetOnlinePlayer(guid);
                    return ThreadRunPresence.IsMemberInside(run, member) ? ThreadCachePlacer.FormAndFillAtMember(run, landblock, member, hasMore) : null;
                },
                MoveFor = guid =>
                {
                    var member = PlayerManager.GetOnlinePlayer(guid);
                    if (ThreadRunPresence.IsMemberInside(run, member))
                        ThreadCachePlacer.MoveMemberCaches(run, landblock, member);
                },
                Tell = (guid, text) => PlayerManager.GetOnlinePlayer(guid)?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast)),
                Deliver = (cache, result) => ThreadCachePlacer.DeliverFill(run, cache, result),
                Materialize = ThreadCacheFiller.EntryMaterializer,
                BuildBonus = BonusBuilder,
                BuildPooledRolls = PooledRollBuilder,
                CountPooledRolls = PooledRollCounter,
            };
        }

        /// <summary>
        /// One group delivery pass (task 10 steps 3 and 4).
        ///   Auto: deal, then serve every roster member in roster order.
        ///   Summon: deal, then serve the requester by forming caches at them from their pile (no top-up, as the solo
        ///   Summon); when the deal handed anything out, every other member is served as Auto serves them, so a pile
        ///   dealt by this pass is never left without a delivery attempt or a retry flag.
        ///   Move: the requester's own caches move to them; nothing is dealt.
        /// Every mode ends by leaving the run-level placement flag set when undealt loot remains, so a kill banked while
        /// this pass held the latch (its own trigger was dropped as Busy) is retried by the Tick.
        /// </summary>
        internal static void Run(ThreadDungeonRun run, CacheRequestMode mode, uint requesterGuid, DeliveryWorld world, ThreadLootRollBudget budget = null)
        {
            if (run == null || world == null || run.State != ThreadDungeonRunState.Cleared)
                return;

            switch (mode)
            {
                case CacheRequestMode.Move:
                    if (run.IsRosterMember(requesterGuid) && world.IsInside(requesterGuid))
                        world.MoveFor(requesterGuid);
                    break;

                case CacheRequestMode.Summon:
                {
                    var round = Deal(run, world.Materialize, world.BuildBonus, world.BuildPooledRolls, budget, world.CountPooledRolls);

                    if (run.IsRosterMember(requesterGuid))
                        ServeMember(run, requesterGuid, false, world);

                    if (round > 0)
                    {
                        foreach (var member in run.Roster)
                        {
                            if (member.Guid != requesterGuid)
                                ServeMember(run, member.Guid, true, world);
                        }
                    }

                    break;
                }

                default:
                    Deal(run, world.Materialize, world.BuildBonus, world.BuildPooledRolls, budget, world.CountPooledRolls);

                    foreach (var member in run.Roster)
                        ServeMember(run, member.Guid, true, world);

                    break;
            }

            if (run.HasUndealtLoot)
                run.MarkPlacementWanted();
        }

        /// <summary>
        /// Delivers one member's held pile (task 10 step 4). The member's delivery-wanted flag is cleared first: this
        /// pass serves them. Then, when <paramref name="topUp"/>, their existing caches are topped up (no presence
        /// needed, like the solo top-up). If a pile remains: a member inside gets caches formed at them and one
        /// Formed / NoRoom line; a member not inside is flagged for the arrival and Tick retries. A throw for one
        /// member is logged and does not stop the others; FillFromPile has already returned what did not land.
        /// </summary>
        internal static void ServeMember(ThreadDungeonRun run, uint memberGuid, bool topUp, DeliveryWorld world)
        {
            try
            {
                run.TryClaimMemberDeliveryWanted(memberGuid);

                if (!run.HasHeldPile(memberGuid))
                    return;

                if (topUp)
                {
                    foreach (var cache in run.PlacedCachesSnapshot())
                    {
                        if (!run.HasHeldPile(memberGuid))
                            break;

                        if (!ThreadLootPool.IsCacheFor(cache, memberGuid))
                            continue;

                        world.Deliver(cache, ThreadCachePlacer.FillDeliveringLanded(run, cache, (r, c) => FillFromPile(r, c, memberGuid), world.Deliver));
                    }

                    if (!run.HasHeldPile(memberGuid))
                        return;
                }

                var pass = world.IsInside(memberGuid) ? world.FormAt(memberGuid, () => run.HasHeldPile(memberGuid)) : null;

                if (pass == null)
                {
                    // Not inside (or gone since the test): wait for their arrival (ruling R20).
                    run.MarkMemberDeliveryWanted(memberGuid);
                    return;
                }

                if (pass.CachesFormed > 0)
                    world.Tell(memberGuid, ThreadCachePlacer.FormedMessage);

                // Inside and no room: not flagged, matching solo. /spawncache is the fallback (ruling R20).
                if (pass.NoRoom)
                    world.Tell(memberGuid, ThreadCachePlacer.NoRoomMessage);

                // A fresh, EMPTY cache refused everything left in the pile, so no cache ever will. Kept, those items would
                // hold the copy alive (GroupLootOwed) until TTL for nothing (fix round 1, minor 2).
                if (pass.FreshCacheAcceptedNothing)
                    DestroyUnplaceable(run, memberGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} group cache delivery for 0x{memberGuid:X8} threw", ex);
            }
        }

        /// <summary>
        /// Destroys what is left of a member's pile after an empty cache refused all of it. Roster-generic (see
        /// ThreadDungeonRun_Roster.cs): a solo run's owner reaches this the same way a group member does.
        /// </summary>
        internal static int DestroyUnplaceable(ThreadDungeonRun run, uint memberGuid)
        {
            var refused = run.TakeHeldPile(memberGuid);

            foreach (var held in refused)
                held.Item?.Destroy();

            if (refused.Count > 0)
                log.Warn($"[DYNDUNGEON] {run} an empty cache for 0x{memberGuid:X8} accepted none of {refused.Count} pile item(s); they were destroyed");

            return refused.Count;
        }

        /// <summary>
        /// Ruling R19, the deal. Claims the run-level placement flag, the pending boss bonus and the shared overflow,
        /// then, within this step's budget, ledger rolls in kill order (up to <see cref="MaxEntriesPerDeal"/> claims and
        /// <see cref="MaxRollsPerDeal"/> rolls) and pending pooled boss-bonus rolls; takes the next deal round, buffers
        /// what it built on the run, and hands out whatever buffer is due (<see cref="BufferAndFlush"/>) over this deal's seats (ThreadDungeonRun.DealSeats, read once: the owner and every keyed
        /// member, ruling R32):
        ///   - a held rare whose RareRecipientGuid holds a seat goes straight to that member's pile (ruling Q-T7-1
        ///     included); a rare with no recipient, or a keyless or off-roster one, is dealt like any item;
        ///   - the DUPLICATED half of the boss bonus (Trade Notes and salvage bags) is built ONCE PER SEAT with
        ///     run.Group.RewardBonus, straight into that member's pile, in the deal that claims the bonus;
        ///   - the POOLED half (the Legendary-table rolls) is COUNTED once for the whole bonus and then built a few
        ///     rolls per step by this and later deals (resumable, 2026-09-18), each batch joining that deal's hand-out;
        ///   - everything else is sorted by Value descending (stable) and snake-dealt over the seats (invariant 4).
        /// An item a pile refuses (the run has Ended) is destroyed. Each set's first pick rotates one seat per set
        /// (ThreadDungeonRun.ReserveSnakeSet). Undealt loot left over (a cap, the step budget, pending pooled rolls,
        /// or a kill banked meanwhile) sets the run-level placement flag, on every exit. Returns the round, or 0 when
        /// there was nothing to deal (no round is consumed then).
        /// </summary>
        /// <param name="buildPooledRolls">
        /// The pooled half of the boss bonus: (run, factor, cap). Null pools nothing, which is what the tests that
        /// exercise only the ledger pass. Production always supplies it (DeliveryWorld.BuildPooledRolls).
        /// </param>
        /// <param name="countPooledRolls">
        /// How many pooled rolls the bonus is worth: (run, factor, cap). When supplied (production), the pooled half is
        /// resumable: the count is parked on the run and <paramref name="buildPooledRolls"/> is called per batch with
        /// the batch size as its cap. Null keeps the pre-2026-09-18 atomic build, one call with the full cap.
        /// </param>
        internal static int Deal(ThreadDungeonRun run, Func<ThreadLootLedgerEntry, List<WorldObject>> materialize,
            Func<ThreadDungeonRun, double, List<WorldObject>> buildBonus,
            Func<ThreadDungeonRun, double, int, List<WorldObject>> buildPooledRolls = null, ThreadLootRollBudget rollBudget = null,
            Func<ThreadDungeonRun, double, int, int> countPooledRolls = null)
        {
            if (run == null || run.Roster == null || run.Roster.Count == 0)
                return 0;

            run.TryClaimPlacementWanted();

            var watch = Stopwatch.StartNew();
            var missesBefore = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread;

            // The step budget is a TIGHTER bound than the deal caps, never a looser one: the caps still hold for a
            // caller that passes no budget (every test, and any future non-chained caller). A null budget is the
            // pre-batching behaviour, except that a fat head entry is now split at the roll cap.
            var budget = rollBudget ?? ThreadLootRollBudget.Unlimited();

            var bonusClaimed = run.TryClaimLootBonus();
            var overflow = run.TakeAllOverflow();
            var canClaim = budget.NextClaimRolls > 0 && (run.PrebuiltCount > 0 || run.LedgerCount > 0 || run.PooledBonusRollsPending > 0);

            if (!bonusClaimed && overflow.Count == 0 && !canClaim)
                return 0;

            var round = run.NextDealRound();
            var seats = run.DealSeats();
            var seen = new HashSet<WorldObject>(ReferenceEqualityComparer.Instance);
            var dealt = new List<(WorldObject Item, string RareFinderName)>();
            var pooledDealt = new List<(WorldObject Item, string RareFinderName)>();
            var tally = new DealTally();

            try
            {
                MaterializeAndPile(run, seats, overflow, bonusClaimed, round, seen, dealt, pooledDealt, materialize, buildBonus, buildPooledRolls, countPooledRolls, budget, tally, DealFloor(budget));
            }
            finally
            {
                // Both run on every exit, a throw included. BufferAndFlush never throws: it buffers what was collected,
                // hands out whatever buffer is due, and destroys whatever it could not hold. Undealt loot (the buffers
                // included) must still reach the Tick retry.
                tally.HandedOut += BufferAndFlush(run, seats, dealt, pooledDealt, tally.LedgerRolls, tally.HitDealCap, DealFloor(budget), round);

                if (run.HasUndealtLoot)
                    run.MarkPlacementWanted();
            }

            // Debug, not Info: with time-budgeted steps a group clear deals once per step, which is dozens to hundreds
            // of these per run. The chain's own finished line (ThreadCachePlacer) and the run summary carry the totals.
            if (log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] {run} group deal round {round}: seats={seats.Count}/{run.Roster.Count} entries={tally.Claims} rolls={tally.Rolls} bonus={bonusClaimed} pooled={tally.PooledRolls} pooledLeft={run.PooledBonusRollsPending} overflow={overflow.Count} built={dealt.Count + pooledDealt.Count} handedOut={tally.HandedOut} buffered={run.DealBufferCount} direct={tally.Direct} ledgerLeft={run.LedgerCount} ms={watch.ElapsedMilliseconds} weenieMiss={ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread - missesBefore}");

            return round;
        }

        /// <summary>What one <see cref="Deal"/> claimed and piled, for its log line.</summary>
        private sealed class DealTally
        {
            public int Claims;
            public int Rolls;
            public int LedgerRolls;
            public int PooledRolls;
            public int Direct;
            public bool HitDealCap;
            public int HandedOut;
        }

        /// <summary>
        /// The hand-out half of a deal under the deal buffer (code review of #1212, finding 2). Adds this deal's built
        /// items to the run's buffers (<paramref name="pooledDealt"/> to the pooled boss-half buffer), then hands out:
        ///   - the LEDGER buffer (ledger rolls, overflow, rares with no seated recipient) once it holds at least
        ///     <paramref name="floor"/> ledger rolls, or the ledger is empty, or this deal hit a per-deal cap;
        ///   - the POOLED buffer once the pooled half is fully built (nothing pending, no bonus waiting to be claimed),
        ///     so the boss's pooled rolls are value-sorted and snake-dealt as ONE set, as before they became resumable.
        /// Whatever is due goes out in one <see cref="HandOut"/>. A refused buffer (the run has Ended) destroys the
        /// items at once. Never throws. Returns the number of items handed to a pile.
        /// </summary>
        internal static int BufferAndFlush(ThreadDungeonRun run, IReadOnlyList<uint> seats, List<(WorldObject Item, string RareFinderName)> dealt,
            List<(WorldObject Item, string RareFinderName)> pooledDealt, int ledgerRolls, bool hitDealCap, int floor, int round)
        {
            BufferOrDestroy(run, dealt, ledgerRolls, pooled: false, round);
            BufferOrDestroy(run, pooledDealt, 0, pooled: true, round);

            List<(WorldObject Item, string RareFinderName)> due;

            try
            {
                var ledgerDue = run.DealBufferRolls >= floor || (run.LedgerCount == 0 && run.PrebuiltCount == 0) || hitDealCap;
                var pooledDue = run.PooledBonusRollsPending == 0 && !run.IsLootBonusPending;

                due = run.TakeDealBuffer(ledgerDue, pooledDue);
            }
            catch (Exception ex)
            {
                // Nothing was taken (TakeDealBuffer only moves items under its lock), so everything is still buffered.
                log.Error($"[DYNDUNGEON] {run} deciding the deal buffer for round {round} threw; the buffer waits for the next deal", ex);
                return 0;
            }

            return HandOut(run, seats, due, round, run.AddToHeldPile);
        }

        private static void BufferOrDestroy(ThreadDungeonRun run, List<(WorldObject Item, string RareFinderName)> items, int rolls, bool pooled, int round)
        {
            if (items.Count == 0 && rolls == 0)
                return;

            var buffered = false;

            try
            {
                buffered = run.TryBufferForDeal(items, rolls, pooled);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} buffering group deal round {round} threw; its items are destroyed", ex);
            }

            if (buffered)
                return;

            // The run has Ended (or the buffer threw): no deal will ever hand these out, so they must not be stranded.
            foreach (var d in items)
            {
                try
                {
                    d.Item?.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} destroying an unbufferable item threw", ex);
                }
            }
        }

        /// <summary>
        /// The materialise half of <see cref="Deal"/>, in this order: the shared overflow; the boss bonus when this deal
        /// claimed it (the DUPLICATED half into piles, the POOLED half counted onto the run, or built whole without a
        /// counter); ledger rolls in kill order; then pending pooled rolls. Claims and materialisation interleave, one
        /// claim at a time, so a timed budget stops at the roll that crosses it. Everything not piled directly goes
        /// into <paramref name="dealt"/> for the hand-out.
        /// </summary>
        private static void MaterializeAndPile(ThreadDungeonRun run, IReadOnlyList<uint> seats, List<(WorldObject Item, string RareFinderName)> overflow,
            bool bonusClaimed, int round, HashSet<WorldObject> seen, List<(WorldObject Item, string RareFinderName)> dealt,
            List<(WorldObject Item, string RareFinderName)> pooledDealt,
            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, Func<ThreadDungeonRun, double, List<WorldObject>> buildBonus,
            Func<ThreadDungeonRun, double, int, List<WorldObject>> buildPooledRolls, Func<ThreadDungeonRun, double, int, int> countPooledRolls,
            ThreadLootRollBudget budget, DealTally tally, int floor)
        {
            foreach (var carried in overflow)
            {
                if (carried.Item != null && seen.Add(carried.Item))
                    dealt.Add((carried.Item, carried.RareFinderName));
            }

            if (bonusClaimed)
            {
                // OWNER RULING, 2026-09-17 ("each player should get the mmds and salvage. other loot should be
                // split"), which REVERSES the Task 7 / ruling R32 contract this block used to carry: "one bonus per
                // deal seat, each built with B, each into that member's own pile". The bonus is now two halves.
                //
                // BUDGET CHARGING. The two halves charge DIFFERENTLY, and the difference is the whole point of the
                // split:
                //   - the DUPLICATED half (currency and salvage) is built once per seat and charges the budget
                //     once per seat, below;
                //   - the POOLED half (the item rolls) is ONE build for the whole deal, charged once per roll it
                //     builds, never once per seat.
                // ThreadCacheBatchingTests pins the charging.
                //
                // RESUMABLE POOLED HALF (2026-09-18). TryClaimLootBonus is a one-shot latch, and it still fires exactly
                // once: HERE. What is resumable is only the pooled build, because until a roll is built there is no
                // object to lose: this deal counts the rolls and parks the count on the run
                // (ThreadDungeonRun.TrySetPooledBonusRolls), and every deal from here on - this one included - builds a
                // few of them within its budget. Item conservation is unaffected: a built roll is in `pooledDealt` the
                // moment it exists and in the run's pooled deal buffer by the end of Deal's finally (destroyed by the
                // run-end drain if the run ends first), and a roll never built is just a number the drain zeroes. The DUPLICATED half stays atomic: it is a handful of stackables per seat, and building
                // it across steps would split one member's currency across deal rounds for no measurable gain.
                //
                // DUPLICATED half, unchanged from that contract: the Trade Note stack and the salvage bags, one
                // full set per seat at B, straight into that member's pile. Currency and salvage scale per player.
                foreach (var seat in seats)
                {
                    List<WorldObject> bonus = null;

                    try
                    {
                        bonus = buildBonus != null ? buildBonus(run, run.Group.RewardBonus) : null;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} building the per-seat boss bonus for 0x{seat:X8} threw", ex);
                    }

                    // Charged per seat: this half is built once for EVERY seat, so it costs once per seat.
                    budget.Spend(bonus?.Count ?? 0);

                    // Stack consolidation (Threads item 8): this seat's Trade Notes, if the builder returned several
                    // identical stacks, become as few as MaxStackSize allows. Charged above at the count BUILT.
                    bonus = ThreadLootStacking.Consolidate(bonus, out var mergedBonus);
                    run.Perf.RecordStacksMerged(mergedBonus);

                    foreach (var item in bonus ?? new List<WorldObject>())
                    {
                        if (item != null && seen.Add(item))
                            tally.Direct += Pile(run, seat, item, null, round);
                    }
                }

                // POOLED half: the Legendary-table rolls, dealt through HandOut so they share the per-set seat rotation and
                // value sort with the rest of the loot. Built items wait in the run's pooled deal buffer until the whole
                // half is built, then go out as ONE value-sorted set (BufferAndFlush). That is what makes a boss pay the same per-player multiplier
                // the trash already pays.
                //
                // The factor is seats.Count * B, NOT run.BossLootFactor (E * B). E comes from the LOCKED ROSTER and
                // carries the tunable g, while this divides over DealSeats(), so E * B / seats is only B at default
                // tuning with every member keyed; seats * B is B per player by construction. The cap rises with the
                // seats for the same reason - see ThreadDungeonRewardSpawner.DefaultLootCountCap. Both are fixed HERE,
                // at the claim, so a later batch builds at the seat count the bonus was claimed at.
                var factor = seats.Count * run.Group.RewardBonus;
                var cap = ThreadDungeonRewardSpawner.DefaultLootCountCap * seats.Count;

                if (countPooledRolls != null)
                {
                    var count = 0;

                    try
                    {
                        count = countPooledRolls(run, factor, cap);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} counting the pooled boss rolls threw", ex);
                    }

                    run.TrySetPooledBonusRolls(count, factor);
                }
                else
                {
                    // No counter: the atomic build, one call with the full cap, charged once for the one build.
                    List<WorldObject> pooled = null;

                    try
                    {
                        pooled = buildPooledRolls?.Invoke(run, factor, cap);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} building the pooled boss rolls threw", ex);
                    }

                    budget.Spend(pooled?.Count ?? 0);
                    tally.PooledRolls += pooled?.Count ?? 0;

                    foreach (var item in pooled ?? new List<WorldObject>())
                    {
                        if (item != null && seen.Add(item))
                            pooledDealt.Add((item, null));
                    }
                }
            }

            // Ledger rolls, kill order, one claim at a time within the step budget and the deal caps. The first claim
            // of a step may split a fat entry (and a timed budget always claims one roll at a time), so no deal ever
            // materialises more than the roll cap - the fat-entry hole #1210 left open.
            //
            // Pieces the trickle built ahead of the clear (ThreadLootTrickle) come first: they are the OLDEST claims, so
            // kill order holds across the two stores. Each is charged exactly what claiming it here would have been
            // charged (ThreadLootRollBudget.TryTakePrebuilt) and goes through the same MaterializeClaim with its items
            // already built, so a prebuilt roll lands in the same deal set, and so reaches the same value-sorted snake,
            // as the same roll built at the clear.
            //
            // EXACT SETS (2026-09-18). A deal claims no further than the set it is filling: the ledger buffer never goes
            // past the floor, so every ledger set is exactly `floor` rolls in kill order (the last one of a clear
            // excepted). Which rolls are sorted and snake-dealt together is then a function of the roll count alone,
            // never of how many rolls a step's time budget happened to fit, or of whether a roll was prebuilt: the
            // property that keeps the trickle from changing anyone's share. (Before this, a set could close at 12-14
            // rolls depending on step timing.) A set that completes while the step can still claim is handed out on
            // the spot and the step goes on filling the next one, so the boundary never costs a step its budget.
            while (tally.Claims < MaxEntriesPerDeal)
            {
                var setRoom = floor - run.DealBufferRolls - tally.LedgerRolls;

                if (setRoom < 1 && Math.Min(MaxRollsPerDeal - tally.Rolls, budget.NextClaimRolls) >= 1 && (run.PrebuiltCount > 0 || run.LedgerCount > 0))
                {
                    tally.HandedOut += BufferAndFlush(run, seats, dealt, new List<(WorldObject Item, string RareFinderName)>(), tally.LedgerRolls, false, floor, round);
                    dealt.Clear();
                    tally.LedgerRolls = 0;
                    setRoom = floor - run.DealBufferRolls;
                }

                var allowance = Math.Min(Math.Min(MaxRollsPerDeal - tally.Rolls, budget.NextClaimRolls), setRoom);

                if (allowance < 1)
                    break;

                ThreadLootLedgerEntry entry;
                List<WorldObject> prebuilt = null;

                if (budget.TryTakePrebuilt(run, out var built))
                {
                    entry = built.Piece;
                    prebuilt = built.Items;
                }
                else if (run.TryClaimLootRolls(allowance, budget.NextClaimMayBePartial, out entry))
                {
                    budget.Spend(entry.Rolls);
                }
                else
                {
                    break;
                }

                tally.Claims++;
                tally.Rolls += entry.Rolls;
                tally.LedgerRolls += entry.Rolls;

                MaterializeClaim(run, seats, entry, round, seen, dealt, prebuilt != null ? (_ => prebuilt) : materialize, tally, builtAtClear: prebuilt == null);
            }

            // A deal stopped by a per-deal cap (not the step budget) hands its buffer out regardless of the floor, as the
            // pre-buffer deal did: only reachable without a step budget, whose floor is MaxRollsPerDeal.
            tally.HitDealCap = tally.Claims >= MaxEntriesPerDeal || tally.Rolls >= MaxRollsPerDeal;

            // Pending pooled boss rolls, after the ledger, within whatever the step has left. Charged per roll BUILT
            // FOR, so a roll that came back null still costs the step what it cost the world thread.
            while (buildPooledRolls != null && tally.Rolls < MaxRollsPerDeal)
            {
                var allowance = Math.Min(MaxRollsPerDeal - tally.Rolls, budget.NextClaimRolls);

                if (allowance < 1 || !run.TryClaimPooledBonusRolls(allowance, out var rolls, out var factor))
                    break;

                tally.Rolls += rolls;
                tally.PooledRolls += rolls;
                budget.Spend(rolls);

                List<WorldObject> pooled = null;

                try
                {
                    // The cap IS the batch size here: ScaledLootCount clamps the bonus's full count down to it, so the
                    // builder rolls exactly this batch (the count parked on the run already bounds the total).
                    pooled = buildPooledRolls(run, factor, rolls);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} building {rolls} pooled boss roll(s) threw; they are skipped", ex);
                }

                foreach (var item in pooled ?? new List<WorldObject>())
                {
                    if (item != null && seen.Add(item))
                        pooledDealt.Add((item, null));
                }
            }
        }

        /// <summary>
        /// One claimed ledger piece into the deal: its rolls materialised, a held rare (only ever on a final slice) to
        /// its recipient or the dealt list. A materialisation that throws loses only its rolls; the rare, a live
        /// object with no other owner, is still dealt.
        /// </summary>
        private static void MaterializeClaim(ThreadDungeonRun run, IReadOnlyList<uint> seats, ThreadLootLedgerEntry entry, int round,
            HashSet<WorldObject> seen, List<(WorldObject Item, string RareFinderName)> dealt, Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, DealTally tally,
            bool builtAtClear = true)
        {
            List<WorldObject> items = null;

            try
            {
                items = materialize != null ? materialize(entry) : null;
            }
            catch (Exception ex)
            {
                // The claim already removed the rolls. Its held rare is a live object with no other owner, so it is
                // still dealt below; only the rolls that never happened are lost, as on the solo path.
                log.Error($"[DYNDUNGEON] {run} materialising a ledger entry for group deal round {round} threw", ex);
            }

            // The run summary's builtAtClear: rolls materialised here, at the clear, rather than by the trickle.
            if (builtAtClear)
                run.Perf.RecordBuiltAtClear(items?.Count(i => i != null && !ReferenceEquals(i, entry.HeldRare)) ?? 0);

            var rareSeen = false;

            foreach (var item in items ?? new List<WorldObject>())
            {
                if (item == null || !seen.Add(item))
                    continue;

                if (entry.HeldRare != null && ReferenceEquals(item, entry.HeldRare))
                {
                    rareSeen = true;
                    tally.Direct += RouteRare(run, seats, entry, round, dealt);
                }
                else
                {
                    dealt.Add((item, null));
                }
            }

            // A materialisation that came back null, threw, or left out the rare still owes the held rare a home.
            if (entry.HeldRare != null && !rareSeen && seen.Add(entry.HeldRare))
                tally.Direct += RouteRare(run, seats, entry, round, dealt);
        }

        /// <summary>
        /// The hand-out half of <see cref="Deal"/>: reserves the run's next set (ThreadDungeonRun.ReserveSnakeSet, which
        /// rotates the first pick one seat per set from a per-run random offset), snake-deals the UNMERGED
        /// <paramref name="dealt"/> by Value over <paramref name="seats"/> as a fresh snake from that seat (ruling R32),
        /// then consolidates each hand's own share separately (Threads item 8 round 2, 2026-09-22 - merging before the
        /// deal let one seat take a whole set's worth of a dense-value stackable as a single pick; merging is now
        /// confined to each seat's own share), and piles each item through <paramref name="addToPile"/> (a refusal
        /// destroys the item). A throw partway, or no seat at all, is caught and logged, and every dealt item not yet
        /// resolved - handed out, or merged away and destroyed within its own hand - is destroyed, so nothing is
        /// stranded out of the world (fix round 1, minor 4) and nothing merged away is piled or destroyed a second
        /// time. Returns the number of items handed to a pile.
        /// </summary>
        internal static int HandOut(ThreadDungeonRun run, IReadOnlyList<uint> seats, List<(WorldObject Item, string RareFinderName)> dealt, int round, Func<uint, HeldPileItem, bool> addToPile)
        {
            var handed = new HashSet<WorldObject>(ReferenceEqualityComparer.Instance);

            // Every original dealt item this method has finished dealing with, one way or another: handed out
            // (piled, or destroyed on refusal), or merged away and destroyed inside a Consolidate call that returned
            // successfully. Marked per item, not per hand, so an exception partway through a hand still protects the
            // items that hand already finished. The catch block below destroys only what is missing from this set,
            // so a merge-away is never re-piled or double-destroyed, an already-piled item is never re-destroyed,
            // and a truly untouched item is never stranded.
            var resolved = new HashSet<WorldObject>(ReferenceEqualityComparer.Instance);

            try
            {
                if (dealt.Count == 0)
                    return 0;

                // Unreachable through Deal (the owner always holds a seat), but an empty seat list must not strand the items.
                if (seats == null || seats.Count == 0)
                    throw new InvalidOperationException("no deal seats");

                // A fresh snake per set, its first pick rotated to the run's next seat (ThreadDungeonRun.ReserveSnakeSet):
                // hand h of the snake goes to seat (first + h) mod seats. Dealt on the UNMERGED items, so identical
                // stackables spread across seats exactly as they would if never merged at all.
                var first = run.ReserveSnakeSet(seats.Count);
                var hands = SnakeDeal(dealt, d => d.Item.Value ?? 0, seats.Count);

                for (var hand = 0; hand < hands.Count; hand++)
                {
                    var seat = seats[(first + hand) % seats.Count];
                    var handItems = hands[hand];

                    // Stack consolidation (Threads item 8) AFTER the deal, per seat: identical stackables THIS seat
                    // received become as few stacks as MaxStackSize allows, and a merged stack is then one pick worth
                    // its whole Value, but only within this seat's own share - merging never crosses a seat boundary.
                    // The objects merged away are destroyed here, never handed out. Pure object-count reduction: see
                    // ThreadLootStacking for what counts as identical.
                    var seatDealt = ThreadLootStacking.Consolidate(handItems, out var merged);
                    run.Perf.RecordStacksMerged(merged);

                    // Consolidate returning at all means every merge it made is done: the survivors are in seatDealt,
                    // and the objects merged away are already destroyed. Anything from this hand's original items
                    // missing from seatDealt was merged away, so it is resolved now, before a single item is piled.
                    var survivors = new HashSet<WorldObject>(seatDealt.Select(d => d.Item), ReferenceEqualityComparer.Instance);

                    foreach (var d in handItems)
                    {
                        if (!survivors.Contains(d.Item))
                            resolved.Add(d.Item);
                    }

                    foreach (var d in seatDealt)
                    {
                        // Marked handed only once the pile call returned: an item whose call threw is destroyed below.
                        if (!addToPile(seat, new HeldPileItem(d.Item, d.RareFinderName, round)))
                            d.Item.Destroy();

                        handed.Add(d.Item);
                        resolved.Add(d.Item);
                    }
                }
            }
            catch (Exception ex)
            {
                var destroyed = 0;

                foreach (var d in dealt)
                {
                    if (resolved.Contains(d.Item))
                        continue;

                    try
                    {
                        d.Item.Destroy();
                        destroyed++;
                    }
                    catch (Exception destroyEx)
                    {
                        log.Error($"[DYNDUNGEON] {run} destroying an undealt item after a failed hand-out threw", destroyEx);
                    }
                }

                log.Error($"[DYNDUNGEON] {run} group deal round {round} hand-out threw; destroyed {destroyed} item(s) not yet handed out", ex);
            }

            return handed.Count;
        }

        /// <summary>
        /// A rare to its recipient's pile when the recipient holds a deal seat, else into the dealt list: a keyless or
        /// off-roster recipient is treated as no recipient (ruling R32). Returns 1 when piled directly.
        /// </summary>
        private static int RouteRare(ThreadDungeonRun run, IReadOnlyList<uint> seats, ThreadLootLedgerEntry entry, int round, List<(WorldObject Item, string RareFinderName)> dealt)
        {
            if (entry.RareRecipientGuid != 0 && seats.Contains(entry.RareRecipientGuid))
                return Pile(run, entry.RareRecipientGuid, entry.HeldRare, entry.HeldRareFinderName, round);

            dealt.Add((entry.HeldRare, entry.HeldRareFinderName));
            return 0;
        }

        /// <summary>Adds one item to a member's pile; a refused item (the run has Ended) is destroyed. Returns 1 when piled.</summary>
        private static int Pile(ThreadDungeonRun run, uint memberGuid, WorldObject item, string rareFinderName, int round)
        {
            if (run.AddToHeldPile(memberGuid, new HeldPileItem(item, rareFinderName, round)))
                return 1;

            item.Destroy();
            return 0;
        }

        /// <summary>
        /// Snake (boustrophedon) deal: the items sorted by <paramref name="valueOf"/> descending (a stable sort, so equal
        /// values keep their input order), then handed out 0, 1, ..., seats-1, seats-1, ..., 1, 0, 0, 1, ... so every
        /// seat alternates between picking early and picking late. Returns exactly <paramref name="seats"/> hands (none
        /// when seats is below 1); every item lands in exactly one hand.
        ///
        /// <paramref name="start"/> is the snake position of the first item (item i takes position start + i), so a
        /// deal can continue where the run's previous deal stopped. A negative start reads as 0.
        /// </summary>
        internal static List<List<T>> SnakeDeal<T>(IReadOnlyList<T> items, Func<T, long> valueOf, int seats, int start = 0)
        {
            var hands = new List<List<T>>();

            if (seats < 1)
                return hands;

            for (var i = 0; i < seats; i++)
                hands.Add(new List<T>());

            if (items == null || items.Count == 0)
                return hands;

            var ordered = valueOf == null ? items.ToList() : items.OrderByDescending(valueOf).ToList();

            var first = Math.Max(0, start) % (2 * seats);

            for (var i = 0; i < ordered.Count; i++)
            {
                var k = first + i;
                var lap = k / seats;
                var position = k % seats;
                var seat = lap % 2 == 0 ? position : seats - 1 - position;

                hands[seat].Add(ordered[i]);
            }

            return hands;
        }

        /// <summary>
        /// Task 10 step 5. Merges and sorts the member's WHOLE held pile - every round currently in it - BEFORE any
        /// of it is split into chests (owner correction, 2026-09-22: the pile IS the pre-chest list, so each of a
        /// member's caches ends up holding a contiguous, sorted segment of one larger list, not per-round
        /// fragments), then places it into <paramref name="cache"/> at strictly increasing PlacementPositions after
        /// the highest one already there (the ThreadCacheFiller.Place rule). Before taking a fresh slot, each item
        /// first tries to top up a matching stack already sitting in THIS cache from an earlier deal round
        /// (<see cref="ThreadLootStacking.TopUpExisting"/>) - the reason a member refilling the same chest across
        /// rounds does not end up with several separate stacks of the same thing. An item the cache refuses (or that
        /// found no room to top up) goes back to the SAME member's pile with its original round (destroyed if the run
        /// has Ended); the pile is re-consolidated and re-sorted from scratch on the next fill, so refused items need
        /// no ordering of their own here. Every round that landed at least one item, directly or via a merge
        /// (<see cref="HeldPileItem.MergedRounds"/>), is marked received (ruling R21). A landed item carrying a rare
        /// finder name is reported in RaresLanded, so the caller's Deliver broadcasts it like solo delivery.
        ///
        /// A throw partway returns every unplaced item to the pile, then propagates with the partial result attached
        /// (ThreadCacheFiller.PartialResultKey), so ThreadCachePlacer.FillDeliveringLanded still delivers what landed.
        /// </summary>
        internal static CacheFillResult FillFromPile(ThreadDungeonRun run, Container cache, uint memberGuid)
        {
            var result = new CacheFillResult();

            if (run == null || cache == null)
                return result;

            var pile = run.TakeHeldPile(memberGuid);

            try
            {
                pile = ThreadLootStacking.ConsolidatePile(pile, out var merged);
                run.Perf.RecordStacksMerged(merged);
                pile = ThreadLootStacking.SortPileForPlacement(pile);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} consolidating/sorting 0x{memberGuid:X8}'s pile threw; placing it as collected", ex);
            }

            var index = 0;
            var watch = Stopwatch.StartNew();

            try
            {
                var next = ThreadCacheFiller.NextPlacementPosition(cache);

                for (; index < pile.Count; index++)
                {
                    var held = pile[index];
                    var item = held.Item;

                    if (item == null)
                        continue;

                    // Code review of PR #1284, finding 3: a ConsolidatePile/SortPileForPlacement throw partway
                    // leaves `pile` at its PRE-consolidate value, which can still reference an object an earlier
                    // MergeGroup call inside that same failed pass already merged away and destroyed. Skip it here
                    // rather than trying to place (or later return to the pile) a destroyed object.
                    if (item.IsDestroyed)
                        continue;

                    // Already in this cache: returning it to the pile would give it a second home.
                    if (cache.Inventory.ContainsKey(item.Guid))
                        continue;

                    // Top up a matching stack already sitting in this cache before taking a fresh slot: a member
                    // refilling the SAME chest across deal rounds must not grow a second stack beside one already there.
                    if (held.RareFinderName == null && ThreadLootStacking.TopUpExisting(run, cache, item))
                    {
                        run.Perf.RecordStacksMerged(1);
                        MarkRoundsReceived(run, memberGuid, held);
                        continue;
                    }

                    if (cache.TryAddToInventory(item, next))
                    {
                        next++;
                        result.Added.Add(item);
                        MarkRoundsReceived(run, memberGuid, held);

                        if (held.RareFinderName != null)
                            result.RaresLanded.Add((item, held.RareFinderName));

                        continue;
                    }

                    ReturnToPile(run, memberGuid, held, result);
                }
            }
            catch (Exception ex)
            {
                ex.Data[ThreadCacheFiller.PartialResultKey] = result;
                throw;
            }
            finally
            {
                // Only reached with items left over when the loop threw: the item that threw is included, unless its add
                // actually landed it in the cache, or a top-up already absorbed and destroyed it.
                for (var i = index; i < pile.Count; i++)
                {
                    var held = pile[i];

                    if (held.Item != null && !held.Item.IsDestroyed && !cache.Inventory.ContainsKey(held.Item.Guid))
                        ReturnToPile(run, memberGuid, held, result);
                }

                // Set in the finally so the partial result a throw carries out is timed too.
                result.ElapsedMs = watch.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>Marks <paramref name="held"/>'s own round received, plus every round its merge absorbed (ruling R21).</summary>
        private static void MarkRoundsReceived(ThreadDungeonRun run, uint memberGuid, HeldPileItem held)
        {
            run.MarkPileReceived(memberGuid, held.Round);

            if (held.MergedRounds == null)
                return;

            foreach (var round in held.MergedRounds)
                run.MarkPileReceived(memberGuid, round);
        }

        private static void ReturnToPile(ThreadDungeonRun run, uint memberGuid, HeldPileItem held, CacheFillResult result)
        {
            if (run.AddToHeldPile(memberGuid, held))
                return;

            held.Item.Destroy();
            result.Destroyed++;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>What one Fill call put where.</summary>
    public sealed class CacheFillResult
    {
        public List<WorldObject> Added { get; } = new List<WorldObject>();
        public List<(WorldObject Rare, string FinderName)> RaresLanded { get; } = new List<(WorldObject Rare, string FinderName)>();
        public int Overflowed { get; internal set; }

        /// <summary>Items the cache refused after the run had ended, destroyed instead of overflowed (invariant 2's third home).</summary>
        public int Destroyed { get; internal set; }

        public int EntriesClaimed { get; internal set; }
        public bool BonusClaimed { get; internal set; }

        /// <summary>
        /// Wall-clock milliseconds this fill spent, set by whichever filler produced the result (ThreadCacheFiller.Fill
        /// or ThreadGroupCacheDelivery.FillFromPile) and logged on the cache line by ThreadCachePlacer.Deliver. It is
        /// the only measurement that separates "we think loot generation dominates a clear-time stall" from a number,
        /// so it is part of the result rather than something a caller has to remember to time.
        /// </summary>
        public long ElapsedMs { get; internal set; }

        /// <summary>
        /// World-database weenie cache misses this fill caused (WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread,
        /// read before and after on the landblock thread that ran it, so another landblock's misses are not counted).
        /// Each one is a synchronous world-DB read inside the landblock action; the loot weenie warm-up exists to drive
        /// this to 0.
        /// </summary>
        public long WeenieMisses { get; internal set; }
    }

    /// <summary>
    /// Moves pooled loot into one cache (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 4, "Fill order").
    /// Runs on the run landblock's thread, inside ThreadCachePlacer's single delivery action, and only on a
    /// cache that is already in the world (invariant 4 is the caller's).
    ///
    /// Order: overflow first, then the boss bonus, then ledger entries in kill order, one entry materialised
    /// at a time, and only as many rolls as the delivery step's <see cref="ThreadLootRollBudget"/> still allows.
    /// Every add passes an explicit, strictly increasing PlacementPosition that starts after the
    /// highest one in the cache: Container.TryAddToInventory defaults to position 0 and shifts every existing
    /// item up (Container.cs:568), which is what reversed the boss cache's order in the client before #1118.
    /// An item the cache refuses goes to the run's overflow; if the run has ended it is destroyed.
    /// </summary>
    public static class ThreadCacheFiller
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam for entry materialisation. Production is <see cref="MaterializeEntry"/>.</summary>
        internal static Func<ThreadLootLedgerEntry, List<WorldObject>> EntryMaterializer = MaterializeEntry;

        /// <summary>
        /// Test seam for the FIXED half of the boss bonus (Trade Notes and salvage bags). Production is
        /// ThreadDungeonRewardSpawner.BuildBossBonusFixedItems. The Legendary-table half is counted and built in
        /// batches behind <see cref="BonusRollCounter"/> and <see cref="BonusRollBuilder"/>.
        /// </summary>
        internal static Func<ThreadDungeonRun, List<WorldObject>> BonusBuilder = ThreadDungeonRewardSpawner.BuildBossBonusFixedItems;

        /// <summary>A solo run has no group reward bonus, so its bonus rolls are counted and built at exactly 1.0.</summary>
        internal const double SoloBonusFactor = 1.0;

        /// <summary>Test seam for the bonus' Legendary-roll COUNT, taken once at the claim. Production is ThreadDungeonRewardSpawner.BossBonusPooledRollCount.</summary>
        internal static Func<ThreadDungeonRun, double, int, int> BonusRollCounter = ThreadDungeonRewardSpawner.BossBonusPooledRollCount;

        /// <summary>Test seam for one BATCH of the bonus' Legendary rolls. Production is ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls.</summary>
        internal static Func<ThreadDungeonRun, double, int, List<WorldObject>> BonusRollBuilder = ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls;

        /// <summary>Test seam for the DeathTreasure roll inside <see cref="MaterializeEntry"/>. Null (the default) is production: Creature.RollDeathTreasureItems.</summary>
        internal static Func<ACE.Database.Models.World.TreasureDeath, List<WorldObject>> DeathTreasureRoller;

        /// <summary>Test seam for the salvage-affinity roll inside <see cref="MaterializeEntry"/>. Null (the default) is production: Creature.RollSalvageAffinityItems.</summary>
        internal static Func<IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)>, int, List<WorldObject>> SalvageAffinityRoller;

        /// <summary>
        /// Exactly what the creature's corpse would have held (spec section 3), in the corpse's order: the
        /// DeathTreasure roll, then the salvage-affinity roll, then the held rare. An Olthoi-killer entry
        /// materialises nothing (GenerateTreasure_Olthoi's no-normal-loot rule).
        ///
        /// Group Threads ruling R17: the DeathTreasure and salvage-affinity pair runs <see cref="ThreadLootLedgerEntry.Rolls"/>
        /// times, pair by pair; the held rare is added once, last. A solo entry has Rolls 1, so its rolls and draws
        /// are exactly the corpse's.
        /// </summary>
        public static List<WorldObject> MaterializeEntry(ThreadLootLedgerEntry entry)
        {
            var items = new List<WorldObject>();

            if (entry == null || entry.KillerIsOlthoiPlayer)
                return items;

            var deathTreasure = DeathTreasureRoller;
            var salvageAffinity = SalvageAffinityRoller;

            for (var roll = 0; roll < entry.Rolls; roll++)
            {
                var loot = deathTreasure != null ? deathTreasure(entry.Profile) : Creature.RollDeathTreasureItems(entry.Profile);

                if (loot != null)
                    items.AddRange(loot);

                var salvage = salvageAffinity != null
                    ? salvageAffinity(entry.SalvageAffinities, entry.Profile?.Tier ?? 1)
                    : Creature.RollSalvageAffinityItems(entry.SalvageAffinities, entry.Profile?.Tier ?? 1);

                if (salvage != null)
                    items.AddRange(salvage);
            }

            if (entry.HeldRare != null)
                items.Add(entry.HeldRare);

            return items;
        }

        /// <summary>The Exception.Data key under which a throwing Fill leaves what it had already placed.</summary>
        internal const string PartialResultKey = "ACE.Server.ThreadDungeons.ThreadCacheFiller.PartialResult";

        /// <summary>
        /// What a Fill that threw had already placed, or null when the exception did not come out of Fill. Items it
        /// added are in the cache and rares among them have landed, so ThreadCachePlacer still delivers them.
        /// </summary>
        internal static CacheFillResult PartialResultOf(Exception ex) => ex?.Data[PartialResultKey] as CacheFillResult;

        /// <summary>
        /// UNBUDGETED: fills until the cache is full or the pool is empty. Production never takes this overload -
        /// ThreadCachePlacer always passes the step's <see cref="ThreadLootRollBudget"/> - because an unbudgeted
        /// fill is exactly the unbounded producer that could materialise MaxCachesPerPass x ItemsCapacity items
        /// inside one landblock action. Kept for callers outside the delivery chain and for tests that assert on a
        /// whole pool at once.
        /// </summary>
        public static CacheFillResult Fill(ThreadDungeonRun run, Container cache) => Fill(run, cache, ThreadLootRollBudget.Unlimited());

        /// <summary>The production entry point: one cache, filled within <paramref name="budget"/>'s remaining rolls.</summary>
        public static CacheFillResult Fill(ThreadDungeonRun run, Container cache, ThreadLootRollBudget budget)
            => Fill(run, cache, EntryMaterializer, BonusBuilder, budget, BonusRollCounter, BonusRollBuilder);

        /// <param name="countBonusRolls">
        /// The bonus' Legendary-roll count, taken once at the claim. Null (every caller that passes only a bonus
        /// builder) means the bonus is whatever <paramref name="buildBonus"/> returns and nothing is batched.
        /// </param>
        /// <param name="buildBonusRolls">One batch of the bonus' Legendary rolls: (run, factor, rolls).</param>
        internal static CacheFillResult Fill(ThreadDungeonRun run, Container cache,
            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, Func<ThreadDungeonRun, List<WorldObject>> buildBonus, ThreadLootRollBudget budget = null,
            Func<ThreadDungeonRun, double, int, int> countBonusRolls = null, Func<ThreadDungeonRun, double, int, List<WorldObject>> buildBonusRolls = null)
        {
            var result = new CacheFillResult();

            if (run == null || cache == null)
                return result;

            var watch = Stopwatch.StartNew();
            var missesBefore = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread;

            try
            {
                FillInto(run, cache, materialize, buildBonus, countBonusRolls, buildBonusRolls, budget ?? ThreadLootRollBudget.Unlimited(), result);
            }
            catch (Exception ex)
            {
                // The exception propagates unchanged (type, message, stack); it only carries the partial result.
                result.ElapsedMs = watch.ElapsedMilliseconds;
                result.WeenieMisses = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread - missesBefore;
                ex.Data[PartialResultKey] = result;
                throw;
            }

            result.ElapsedMs = watch.ElapsedMilliseconds;
            result.WeenieMisses = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread - missesBefore;

            return result;
        }

        /// <summary>
        /// Owner ruling 2026-09-22: the solo build phase. Materialises the shared pool - leftover overflow, the
        /// boss bonus, then ledger rolls in kill order - into the OWNER's held pile (the same HeldPileItem pile a
        /// group seat uses), never touching a cache directly, so the run's WHOLE loot list can be merged and
        /// sorted as one before any of it is split into chests (ThreadGroupCacheDelivery.FillFromPile does the
        /// placing, reused as-is since a solo run's owner is a roster member like any other - see
        /// ThreadDungeonRun_Roster.cs, which is not group-specific).
        ///
        /// Bounded ONLY by <paramref name="budget"/>, never by a single cache's ItemCapacity: the old solo Fill
        /// bounded its ledger loop by one cache's main-slot count because it filled that one cache directly, but a
        /// build step here has no cache to fill, and the pile it is building may end up split across several
        /// chests once placement runs. This mirrors ThreadGroupCacheDelivery.Deal, which has never bounded a
        /// group's deal by any one cache either. One call is one delivery step; ThreadCachePlacer.Execute calls it
        /// while budget remains and the shared pool (run.HasUndealtLoot) still has something to build.
        ///
        /// A round number (run.NextDealRound()) tags every item this call piles, exactly as a group deal round
        /// does, so ThreadLootStacking.ConsolidatePile's round tracking and ruling R21 (marking a round received)
        /// apply unchanged.
        /// </summary>
        /// <param name="maxLedgerEntries">
        /// Item 1a fix (2026-09-22): caps how many FRESH ledger entries (ThreadLootRollBudget.TryClaimNext claims,
        /// never a TryTakePrebuilt continuation) this call may start, regardless of remaining budget. Null is
        /// unbounded, the pre-fix behaviour. ThreadCachePlacer.Execute passes run.LedgerCount, snapshotted before
        /// the call, so a kill that lands on the SAME landblock action - appending to the back of the FIFO ledger
        /// while this call is already draining its front - cannot inflate what this segment waits for; it is left
        /// for the run's next placement pass. Every solo entry is exactly 1 roll (ThreadLootLedgerEntry's own
        /// doc comment), so one TryClaimNext success is always one whole entry here - no partial-slice accounting
        /// needed.
        /// </param>
        internal static int BuildPile(ThreadDungeonRun run, ThreadLootRollBudget budget, int? maxLedgerEntries = null)
            => BuildPileInto(run, EntryMaterializer, BonusBuilder, budget, BonusRollCounter, BonusRollBuilder, maxLedgerEntries);

        internal static int BuildPileInto(ThreadDungeonRun run,
            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, Func<ThreadDungeonRun, List<WorldObject>> buildBonus,
            ThreadLootRollBudget budget, Func<ThreadDungeonRun, double, int, int> countBonusRolls = null,
            Func<ThreadDungeonRun, double, int, List<WorldObject>> buildBonusRolls = null, int? maxLedgerEntries = null)
        {
            if (run == null)
                return 0;

            budget ??= ThreadLootRollBudget.Unlimited();
            var round = run.NextDealRound();

            void Pile(WorldObject item, string rareFinderName)
            {
                if (item == null)
                    return;

                if (!run.AddToHeldPile(run.OwnerGuid, new HeldPileItem(item, rareFinderName, round)))
                    item.Destroy();
            }

            // Leftover shared overflow joins the pile like anything else: the pile is now the one place unplaced
            // loot waits, so nothing stored here from before this feature (or by any future TryAddOverflow caller)
            // is stranded outside it.
            foreach (var carried in run.TakeAllOverflow())
                Pile(carried.Item, carried.RareFinderName);

            // The boss bonus: the fixed half built and piled whole, the Legendary-table half counted here and
            // built a batch at a time below, exactly as ThreadCacheFiller.FillInto's cache-bound version does.
            if (!budget.Exhausted && run.TryClaimLootBonus())
            {
                var bonus = buildBonus(run) ?? new List<WorldObject>();
                budget.Spend(bonus.Count);

                foreach (var item in bonus)
                    Pile(item, null);

                if (countBonusRolls != null)
                {
                    var count = 0;

                    try
                    {
                        count = countBonusRolls(run, SoloBonusFactor, ThreadDungeonRewardSpawner.DefaultLootCountCap);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} counting the boss bonus rolls threw", ex);
                    }

                    run.TrySetPooledBonusRolls(count, SoloBonusFactor);
                }
            }

            while (buildBonusRolls != null && budget.NextClaimRolls >= 1
                   && run.TryClaimPooledBonusRolls(budget.NextClaimRolls, out var rolls, out var factor))
            {
                budget.Spend(rolls);

                List<WorldObject> built = null;

                try
                {
                    built = buildBonusRolls(run, factor, rolls);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} building {rolls} boss bonus roll(s) threw; they are skipped", ex);
                }

                foreach (var item in built ?? new List<WorldObject>())
                    Pile(item, null);
            }

            // Ledger, kill order, bounded by budget alone - never a cache's capacity, since the pile may end up
            // split across several chests once placement runs. Also bounded by maxLedgerEntries (item 1a fix): a
            // fresh claim past the snapshot stops the loop exactly as an exhausted budget would, leaving a late
            // kill's entry at the head of the ledger for the run's next placement pass.
            var ledgerEntriesClaimed = 0;

            while (true)
            {
                ThreadLootLedgerEntry entry;
                List<WorldObject> items;

                if (budget.TryTakePrebuilt(run, out var built))
                {
                    entry = built.Piece;
                    items = built.Items;
                }
                else if (maxLedgerEntries.HasValue && ledgerEntriesClaimed >= maxLedgerEntries.Value)
                {
                    break;
                }
                else if (budget.TryClaimNext(run, out entry))
                {
                    ledgerEntriesClaimed++;

                    try
                    {
                        items = materialize(entry);
                    }
                    catch
                    {
                        // Invariant 2: the claim already removed the entry, so its held rare has no other owner.
                        // Overflow it (destroyed if the run has Ended); the next BuildPile call piles it like any
                        // other overflow item.
                        ThreadLootPool.OverflowOrDestroy(run, entry.HeldRare, entry.HeldRareFinderName);
                        throw;
                    }

                    run.Perf.RecordBuiltAtClear(items?.Count(i => i != null && !ReferenceEquals(i, entry.HeldRare)) ?? 0);
                }
                else
                {
                    break;
                }

                var rareSeen = false;

                foreach (var item in items ?? new List<WorldObject>())
                {
                    if (item == null)
                        continue;

                    var isRare = entry.HeldRare != null && ReferenceEquals(item, entry.HeldRare);
                    rareSeen |= isRare;
                    Pile(item, isRare ? entry.HeldRareFinderName : null);
                }

                // A materialisation that came back null, or without the rare, still owes the held rare a home.
                if (entry.HeldRare != null && !rareSeen)
                    Pile(entry.HeldRare, entry.HeldRareFinderName);
            }

            return ledgerEntriesClaimed;
        }

        private static void FillInto(ThreadDungeonRun run, Container cache,
            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, Func<ThreadDungeonRun, List<WorldObject>> buildBonus,
            Func<ThreadDungeonRun, double, int, int> countBonusRolls, Func<ThreadDungeonRun, double, int, List<WorldObject>> buildBonusRolls,
            ThreadLootRollBudget budget, CacheFillResult result)
        {
            var next = NextPlacementPosition(cache);

            // 1. Overflow. Taken whole and re-added item by item on refusal, so a refused item keeps its place
            // ahead of anything this call adds to overflow later.
            foreach (var carried in run.TakeAllOverflow())
                Place(run, cache, carried.Item, carried.RareFinderName, ref next, result);

            // 2. The boss bonus, claimed only when there is room to start it and the step still has budget. The latch
            // is one-shot (TryClaimLootBonus), and the claiming step does two things:
            //   - it builds the FIXED half whole - the Trade Notes and salvage bags, a handful of cheap creates - and
            //     charges the budget its item count, since each is a loot roll of its own;
            //   - it COUNTS the Legendary-table half and parks the count on the run (TrySetPooledBonusRolls), exactly
            //     as the group deal has done since the bonus was pooled.
            // The rolls themselves are then built a batch at a time, below, within whatever budget each step has left.
            // Before this (2026-09-19) the whole bonus was built unbudgeted in one go, which is what put a 72 ms step
            // in a stage clear with a budget of 8 ms.
            //
            // ORDER is unchanged: the fixed half lands first, then the rolls, then the ledger. A step that spends its
            // budget on bonus rolls simply claims no ledger rolls (the loop below stops on an exhausted budget), so no
            // ledger item can overtake a bonus roll.
            if (!IsFull(cache) && !budget.Exhausted && run.TryClaimLootBonus())
            {
                result.BonusClaimed = true;

                var bonus = buildBonus(run) ?? new List<WorldObject>();
                budget.Spend(bonus.Count);

                // Stack consolidation (Threads item 8), charged above at the count BUILT.
                bonus = ThreadLootStacking.Consolidate(bonus, out var mergedBonus) ?? new List<WorldObject>();
                run.Perf.RecordStacksMerged(mergedBonus);

                foreach (var item in bonus)
                    Place(run, cache, item, null, ref next, result);

                if (countBonusRolls != null)
                {
                    var count = 0;

                    try
                    {
                        // Counted at the solo factor of 1.0 and one cache's cap, which is what the unbatched build
                        // asked BuildCacheLoot for: the same total, term for term.
                        count = countBonusRolls(run, SoloBonusFactor, ThreadDungeonRewardSpawner.DefaultLootCountCap);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {run} counting the boss bonus rolls threw", ex);
                    }

                    run.TrySetPooledBonusRolls(count, SoloBonusFactor);
                }
            }

            // 2b. The bonus' Legendary rolls, a batch at a time. The rolls are a count on the run until they are
            // built, so a step that stops here leaves nothing stranded, HasUnclaimedLoot still reports them (which is
            // what keeps the delivery chain going), and a run that ends first simply never builds them.
            while (buildBonusRolls != null && !IsFull(cache) && budget.NextClaimRolls >= 1
                   && run.TryClaimPooledBonusRolls(budget.NextClaimRolls, out var rolls, out var factor))
            {
                budget.Spend(rolls);

                List<WorldObject> built = null;

                try
                {
                    // The batch size IS the cap here: ScaledLootCount clamps the same total down to it, so the builder
                    // rolls exactly this batch.
                    built = buildBonusRolls(run, factor, rolls);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} building {rolls} boss bonus roll(s) threw; they are skipped", ex);
                }

                built = ThreadLootStacking.Consolidate(built, out var mergedRolls) ?? new List<WorldObject>();
                run.Perf.RecordStacksMerged(mergedRolls);

                foreach (var item in built)
                    Place(run, cache, item, null, ref next, result);
            }

            // 3. Ledger, kill order. Invariant 1: the claim removes the rolls before their items exist, and a
            // partly fitting claim sends its remainder to overflow, never back to the ledger. The claim goes
            // through the step's budget - one roll at a time when timed - so this loop stops at the roll that
            // crosses the step's time budget (or at the roll cap) and lets ThreadCachePlacer's chain continue on the
            // next tick. A fat entry is claimed a slice at a time; only its final slice carries the held rare.
            //
            // Pieces the trickle built ahead of the clear (ThreadLootTrickle) are taken FIRST - they are the oldest
            // claims, so kill order holds - charged exactly as claiming them here would be, with their items already
            // built.
            //
            // The step's items are collected in `pending` and placed together, so identical stackables from different
            // kills in the same step can be consolidated first (Threads item 8). Pending is placed before the cache
            // could fill, and on every exit, a throw included, so nothing collected is stranded.
            var pending = new List<(WorldObject Item, string RareFinderName)>();

            try
            {
                // One predicate with IsFull: main-slot items only (UseBackpackSlot items do not take a main slot), in the
                // cache and in pending alike. Place still overflows anything the cache refuses.
                while (MainSlotsUsed(cache) + pending.Count(p => p.Item != null && !p.Item.UseBackpackSlot) < (cache.ItemCapacity ?? 0))
                {
                    ThreadLootLedgerEntry entry;
                    List<WorldObject> items;

                    if (budget.TryTakePrebuilt(run, out var built))
                    {
                        result.EntriesClaimed++;
                        entry = built.Piece;
                        items = built.Items;
                    }
                    else if (budget.TryClaimNext(run, out entry))
                    {
                        // Counted before the roll, as before: a claim whose materialisation throws was still claimed.
                        result.EntriesClaimed++;

                        try
                        {
                            items = materialize(entry);
                        }
                        catch
                        {
                            // Invariant 2: the claim already removed the entry, so its held rare (a live object rolled at
                            // kill time) has no other owner. Overflow it (destroyed if the run has ended), then let the
                            // failure propagate unchanged.
                            ThreadLootPool.OverflowOrDestroy(run, entry.HeldRare, entry.HeldRareFinderName);
                            throw;
                        }

                        run.Perf.RecordBuiltAtClear(items?.Count(i => i != null && !ReferenceEquals(i, entry.HeldRare)) ?? 0);
                    }
                    else
                    {
                        break;
                    }

                    var rareSeen = false;

                    foreach (var item in items ?? new List<WorldObject>())
                    {
                        if (item == null)
                            continue;

                        var isRare = entry.HeldRare != null && ReferenceEquals(item, entry.HeldRare);
                        rareSeen |= isRare;
                        pending.Add((item, isRare ? entry.HeldRareFinderName : null));
                    }

                    // A materialisation that came back null, or without the rare, still owes the held rare a home.
                    if (entry.HeldRare != null && !rareSeen)
                        pending.Add((entry.HeldRare, entry.HeldRareFinderName));
                }
            }
            finally
            {
                PlacePending(run, cache, pending, ref next, result);
            }
        }

        /// <summary>
        /// Consolidates the step's collected items (Threads item 8; round 2, 2026-09-22) and places them in kill
        /// order. Before taking a fresh slot, each non-rare item first tries to top up a matching stack a PRIOR step
        /// already placed in this same cache (<see cref="ThreadLootStacking.TopUpExisting"/>) - the fix for the
        /// fragmentation a time-budgeted multi-step fill used to leave behind, since consolidation here only ever
        /// saw one step's own pending batch. A true whole-cache merge+sort spanning every step (buffering placement
        /// until the cache's fill is complete) was considered and NOT built: it would hold materialised items out of
        /// the world across an unbounded number of steps, needing new run-level state and a new run-end drain to
        /// stay safe, for a purely cosmetic gain once top-up already fixes the duplicate-stack bug.
        ///
        /// NO VALUE SORT here, deliberately: <see cref="ThreadCacheSort"/> already owns solo display ordering, at
        /// the delivery chain's terminal step, over the WHOLE cache (every step's items, not just one batch), by
        /// the shared /mule vault key (VaultDisplaySort.Order) - see its class doc comment for why that is the
        /// right layer (it also handles a cache a player has open, which placement time cannot). A batch-level
        /// value sort here would run BEFORE that pass, get overwritten by it in production, and in the meantime
        /// break every test and log line that reads "placement is kill/arrival order, the sort has not run yet".
        /// ThreadLootStacking.SortForPlacement/SortPileForPlacement exist for the GROUP path instead
        /// (ThreadGroupCacheDelivery.FillFromPile), because ThreadCacheSort explicitly excludes group runs
        /// ("a group cache keeps the loose value ordering its per-member deal already produces").
        ///
        /// Anything the cache refuses goes to overflow, or is destroyed once the run has ended (<see cref="Place"/>).
        /// </summary>
        private static void PlacePending(ThreadDungeonRun run, Container cache, List<(WorldObject Item, string RareFinderName)> pending, ref int next, CacheFillResult result)
        {
            if (pending.Count == 0)
                return;

            var toPlace = pending;

            try
            {
                toPlace = ThreadLootStacking.Consolidate(pending, out var merged);
                run.Perf.RecordStacksMerged(merged);
            }
            catch (Exception)
            {
                // Consolidation is an optimisation: on any failure, place what was collected as it stands.
                toPlace = pending.Where(p => p.Item != null && !p.Item.IsDestroyed).ToList();
            }

            foreach (var (item, finder) in toPlace)
            {
                if (finder == null && ThreadLootStacking.TopUpExisting(run, cache, item))
                {
                    run.Perf.RecordStacksMerged(1);
                    continue;
                }

                Place(run, cache, item, finder, ref next, result);
            }
        }

        private static void Place(ThreadDungeonRun run, Container cache, WorldObject item, string rareFinder, ref int next, CacheFillResult result)
        {
            if (item == null)
                return;

            // Already in this cache: a refusal here would send it to overflow and give it a second home.
            if (cache.Inventory.ContainsKey(item.Guid))
                return;

            if (cache.TryAddToInventory(item, next))
            {
                next++;
                result.Added.Add(item);

                if (rareFinder != null)
                    result.RaresLanded.Add((item, rareFinder));

                return;
            }

            // OverflowOrDestroy: true = added to the run's overflow; false = the run refused it (ended) and it was destroyed.
            if (ThreadLootPool.OverflowOrDestroy(run, item, rareFinder))
                result.Overflowed++;
            else
                result.Destroyed++;
        }

        /// <summary>True when the cache's main slots are all taken (the capacity Container.TryAddToInventory checks at :527).</summary>
        internal static bool IsFull(Container cache)
            => MainSlotsUsed(cache) >= (cache.ItemCapacity ?? 0);

        /// <summary>Items taking one of the cache's main slots (UseBackpackSlot items do not).</summary>
        internal static int MainSlotsUsed(Container cache)
            => cache.Inventory.Values.Count(i => !i.UseBackpackSlot);

        /// <summary>One past the highest PlacementPosition in the cache, so an add never shifts an existing item.</summary>
        internal static int NextPlacementPosition(Container cache)
            => cache.Inventory.Count == 0 ? 0 : cache.Inventory.Values.Max(i => i.PlacementPosition ?? 0) + 1;

        /// <summary>
        /// The /spawncache move (spec section 6): every item of <paramref name="from"/>, in slot order, into
        /// <paramref name="to"/> at increasing positions. Items are REMOVED from the source first because
        /// WorldObject.Destroy destroys a container's contents (WorldObject.cs:894-898). A refused item goes to
        /// overflow. Returns how many landed in <paramref name="to"/>.
        /// </summary>
        internal static int TransferContents(ThreadDungeonRun run, Container from, Container to)
        {
            var next = NextPlacementPosition(to);
            var moved = 0;

            foreach (var item in from.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).ToList())
            {
                if (!from.TryRemoveFromInventory(item.Guid, out var removed))
                    continue;

                if (to.TryAddToInventory(removed, next))
                {
                    next++;
                    moved++;
                    continue;
                }

                ThreadLootPool.OverflowOrDestroy(run, removed, null);
            }

            return moved;
        }
    }
}

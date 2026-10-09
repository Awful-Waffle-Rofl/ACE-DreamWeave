using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Invariant 4 ("a chest enters the world EMPTY; contents are claimed only after EnterWorld succeeds") and the
    /// move path's ordering. The world half of one attempt (EnterWorld, physics, LOS) needs a live landblock, so the
    /// form-and-fill loop is driven behaviourally through FormAndFill's tryPlaceEmpty and fill seams with real
    /// Containers, and only the attempt's own ordering is pinned on source.
    /// </summary>
    [TestClass]
    public class ThreadCachePlacerTests
    {
        private static ThreadDungeonRun PooledRunWithLoot(int entries)
        {
            var run = PooledRun();
            for (var i = 0; i < entries; i++)
                run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));
            run.TryMarkLootBonusPending();
            return run;
        }

        private static List<CacheCandidate> Candidates(int n)
            => Enumerable.Range(0, n).Select(i => new CacheCandidate(CacheCandidateStage.Ring, 0x01500100, i, 0f, 0f, 0f, 1f, true)).ToList();

        private static readonly Func<ThreadLootLedgerEntry, List<WorldObject>> ThreeItems = _ => new List<WorldObject> { Item(1), Item(2), Item(3) };
        private static readonly Func<ThreadDungeonRun, List<WorldObject>> OneBonus = _ => new List<WorldObject> { Item(900) };

        private static CacheFillResult FillWith(ThreadDungeonRun run, Container cache) => ThreadCacheFiller.Fill(run, cache, ThreeItems, OneBonus);

        /// <summary>A fresh in-memory cache, registered on the run the way TryPlaceEmpty registers an accepted chest.</summary>
        private static Container AcceptedCache(ThreadDungeonRun run, int capacity = 120)
        {
            var cache = Cache(capacity);
            Assert.IsTrue(run.TryRegisterCache(cache));
            return cache;
        }

        [TestMethod]
        public void Invariant4_a_failed_placement_consumes_no_entry_creates_no_item_and_leaves_overflow_untouched()
        {
            var run = PooledRunWithLoot(3);
            var carried = Item(700);
            Assert.IsTrue(run.TryAddOverflow(carried, "Tester"));
            var attempts = 0;
            var materialised = 0;
            var bonusBuilt = 0;
            var delivered = 0;

            var pass = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(5),
                _ => { attempts++; return null; },
                (r, cache) => ThreadCacheFiller.Fill(r, cache, e => { materialised++; return ThreeItems(e); }, b => { bonusBuilt++; return OneBonus(b); }),
                (cache, result) => delivered++);

            Assert.IsTrue(pass.NoRoom);
            Assert.AreEqual(0, pass.CachesFormed);
            Assert.AreEqual(5, attempts, "every candidate tried");
            Assert.AreEqual(0, materialised, "no entry was materialised, so no item was created");
            Assert.AreEqual(0, bonusBuilt, "the bonus was not built either");
            Assert.AreEqual(0, delivered);
            Assert.AreEqual(3, run.LedgerCount, "no ledger entry consumed");
            Assert.IsTrue(run.IsLootBonusPending);
            Assert.AreEqual(1, run.OverflowCount, "overflow untouched");
            Assert.IsFalse(carried.IsDestroyed);
            Assert.AreEqual(0, run.PlacedCachesSnapshot().Count);
        }

        [TestMethod]
        public void Invariant4_contents_are_claimed_only_after_a_chest_is_accepted()
        {
            var run = PooledRunWithLoot(3);
            var attempts = 0;
            var ledgerAtAccept = -1;
            var bonusPendingAtAccept = false;
            Container accepted = null;
            var delivered = new List<Container>();

            var pass = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(5), _ =>
            {
                attempts++;
                if (attempts < 3) return null;
                ledgerAtAccept = run.LedgerCount;
                bonusPendingAtAccept = run.IsLootBonusPending;
                accepted = AcceptedCache(run);
                return accepted;
            }, FillWith, (cache, result) => delivered.Add(cache));

            Assert.AreEqual(3, attempts, "stops at the first accepted candidate, and one cache holds everything");
            Assert.AreEqual(3, ledgerAtAccept, "no entry claimed before acceptance");
            Assert.IsTrue(bonusPendingAtAccept, "the bonus is not claimed before acceptance either");
            Assert.AreEqual(1, pass.CachesFormed);
            Assert.IsFalse(pass.NoRoom);
            CollectionAssert.AreEqual(new[] { accepted }, delivered);
            Assert.AreEqual(10, accepted.Inventory.Count, "bonus 1 + 3 entries x 3 items");
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_claim_after_a_failed_placement_still_claims_each_entry_exactly_once()
        {
            var run = PooledRunWithLoot(4);
            var results = new List<CacheFillResult>();

            var failed = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(3), _ => null, FillWith, (cache, result) => results.Add(result));

            Assert.IsTrue(failed.NoRoom);
            Assert.AreEqual(0, results.Count);
            Assert.AreEqual(4, run.LedgerCount, "the failed pass left every entry in the ledger");

            var formed = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(3), _ => AcceptedCache(run), FillWith, (cache, result) => results.Add(result));

            Assert.AreEqual(1, formed.CachesFormed);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(4, results[0].EntriesClaimed, "every entry, once");
            Assert.IsTrue(results[0].BonusClaimed);
            Assert.AreEqual(13, results[0].Added.Distinct().Count(), "bonus 1 + 4 entries x 3 items, no duplicates");
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_fresh_cache_that_accepts_nothing_is_destroyed_and_the_loot_stays_pooled()
        {
            var run = PooledRunWithLoot(2);
            Container fresh = null;
            var attempts = 0;
            var delivered = 0;

            var pass = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(3),
                _ => { attempts++; fresh = AcceptedCache(run); return fresh; },
                (r, cache) => new CacheFillResult(),
                (cache, result) => delivered++);

            Assert.IsTrue(pass.FreshCacheAcceptedNothing);
            Assert.AreEqual(0, pass.CachesFormed);
            Assert.AreEqual(1, attempts, "the pass ends rather than forming an empty cache on every loop");
            Assert.AreEqual(0, delivered);
            Assert.IsTrue(fresh.IsDestroyed);
            Assert.AreEqual(0, run.PlacedCachesSnapshot().Count);
            Assert.AreEqual(2, run.LedgerCount);
            Assert.IsTrue(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_pass_stops_at_its_cache_bound_and_leaves_the_rest_pooled()
        {
            var run = PooledRunWithLoot(3);
            var formed = new List<Container>();

            var pass = ThreadCachePlacer.FormAndFill(run, 2, () => Candidates(1), _ => AcceptedCache(run, capacity: 1), FillWith, (cache, result) => formed.Add(cache));

            Assert.AreEqual(2, pass.CachesFormed);
            Assert.AreEqual(2, formed.Count);
            Assert.IsTrue(formed.All(c => c.Inventory.Count == 1));
            Assert.IsTrue(run.HasUnclaimedLoot, "what did not fit waits for the next trigger");
        }

        // ------------------------------------------------------------------------------------------------------
        // Round 4 (owner ruling 2026-09-22): solo builds into the owner's held pile, then places the WHOLE
        // merged/sorted list via ThreadGroupCacheDelivery.FillFromPile, spanning as many caches as it takes -
        // exactly the shape ThreadCachePlacer.Execute now drives (BuildPile, then FormAndFillAtMember with
        // FillFromPile as its fill callback). Driven here the same way the invariant-4 tests above drive
        // FormAndFill: through its seams, with real Containers, so no Player or Landblock is needed.
        // ------------------------------------------------------------------------------------------------------

        private static Stackable Coins(uint wcid, int size, int max = 1000, int unitValue = 1, int unitBurden = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, size },
                    { PropertyInt.MaxStackSize, max },
                    { PropertyInt.StackUnitEncumbrance, unitBurden },
                    { PropertyInt.StackUnitValue, unitValue },
                },
            };

            return new Stackable(weenie, new ObjectGuid(NextGuid()));
        }

        private static WorldObject ValuedItem(uint wcid, int value)
            => new GenericObject(new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Value, value } },
            }, new ObjectGuid(NextGuid()));

        /// <summary>
        /// Two kills each drop a 60-unit coin stack (wcid 9800, MaxStackSize 100, unit value 1): merging the
        /// whole pile before placement (ThreadLootStacking.ConsolidatePile) collapses them into EXACTLY two
        /// stacks - 100 and 20 - never three, and never split further once merged. A third kill drops a single
        /// gear item worth far more than the coins' combined Value (999999 against 120), so the discriminating
        /// assertion is the CATEGORY rule: stackables place before ANY non-stackable regardless of Value
        /// (ThreadLootStacking.SortPileForPlacement). Cache 1's capacity is 1, so only the TOP of the sorted
        /// list (one coin stack) fits there; the rest of the list - the OTHER coin stack, then the gear item -
        /// is a contiguous segment that lands in cache 2, proving the list was merged and sorted ONCE, before
        /// either cache was filled, not per cache and not per kill.
        /// </summary>
        [TestMethod]
        public void Solo_pile_splits_the_merged_sorted_list_across_two_caches_and_never_fragments_a_stack_beyond_max_stack_size()
        {
            var run = PooledRun();

            var coinsA = Coins(9800u, 60, max: 100, unitValue: 1);
            var coinsB = Coins(9800u, 60, max: 100, unitValue: 1);
            var gear = ValuedItem(4242u, 999999);

            // The gear kills FIRST in kill order, both coin kills after - so an implementation that placed in
            // kill order (no whole-list sort) would put gear in cache 1, not a coin stack. Only the owner-key
            // sort (stackables ALWAYS before non-stackables, whatever kill order or Value says) puts a coin
            // stack at the front of the list despite gear's Value (999999) dwarfing the coins' total (120) and
            // gear having been rolled first.
            var batches = new Queue<List<WorldObject>>();
            batches.Enqueue(new List<WorldObject> { gear });
            batches.Enqueue(new List<WorldObject> { coinsA });
            batches.Enqueue(new List<WorldObject> { coinsB });

            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));
            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));
            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));

            ThreadCacheFiller.BuildPileInto(run, _ => batches.Count > 0 ? batches.Dequeue() : new List<WorldObject>(),
                _ => new List<WorldObject>(), ThreadLootRollBudget.Unlimited());

            Assert.IsFalse(run.HasUndealtLoot, "fixture: the whole ledger built into the pile in one call");
            Assert.IsTrue(run.HasHeldPile(run.OwnerGuid));

            var cache1 = Cache(capacity: 1);
            var cache2 = Cache(capacity: 10);

            var result1 = ThreadGroupCacheDelivery.FillFromPile(run, cache1, run.OwnerGuid);
            var result2 = ThreadGroupCacheDelivery.FillFromPile(run, cache2, run.OwnerGuid);

            Assert.AreEqual(1, cache1.Inventory.Count, "cache 1's capacity of 1 takes only the top of the list");
            Assert.IsInstanceOfType(cache1.Inventory.Values.Single(), typeof(Stackable));
            Assert.AreEqual(9800u, cache1.Inventory.Values.Single().WeenieClassId, "the top of the list is a coin stack, not the far-higher-value gear");

            Assert.AreEqual(2, cache2.Inventory.Count, "the rest of the list: the other coin stack, then the gear");

            var cache2InOrder = cache2.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).ToList();
            Assert.AreEqual(9800u, cache2InOrder[0].WeenieClassId, "the OTHER coin stack lands first in cache 2 - still ahead of gear");
            Assert.AreEqual(4242u, cache2InOrder[1].WeenieClassId, "gear lands last, after every stackable");

            var coinStackSizes = new[] { cache1.Inventory.Values.Single(), cache2InOrder[0] }
                .Select(i => i.StackSize ?? 0).OrderByDescending(s => s).ToArray();

            CollectionAssert.AreEqual(new[] { 100, 20 }, coinStackSizes, "merged into exactly two stacks - 60+60 respecting MaxStackSize 100 - never three, never re-fragmented across the cache boundary");
            Assert.AreEqual(120, coinStackSizes.Sum(), "quantity conserved: 60 + 60 = 120 total coins across both caches");

            Assert.IsFalse(run.HasHeldPile(run.OwnerGuid), "everything the pile held landed somewhere");
            CollectionAssert.AreEqual(new WorldObject[0], result1.Added.Intersect(result2.Added).ToArray(), "nothing double-placed");
        }

        /// <summary>
        /// A held rare is never merged and is still announced (RaresLanded) once it lands, through the SAME
        /// pile path everything else in the run now uses - proving round 4 did not change the rare rule.
        /// </summary>
        [TestMethod]
        public void Solo_pile_never_merges_a_rare_and_still_announces_it_when_it_lands()
        {
            var run = PooledRun();
            var rare = Item(8801);

            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, rare, "Hero"));

            ThreadCacheFiller.BuildPileInto(run, e => new List<WorldObject> { e.HeldRare }, _ => new List<WorldObject>(), ThreadLootRollBudget.Unlimited());

            Assert.IsTrue(run.HasHeldPile(run.OwnerGuid));

            var cache = Cache();
            var result = ThreadGroupCacheDelivery.FillFromPile(run, cache, run.OwnerGuid);

            Assert.AreEqual(1, cache.Inventory.Count);
            Assert.AreSame(rare, cache.Inventory.Values.Single());
            Assert.AreEqual(1, result.RaresLanded.Count);
            Assert.AreSame(rare, result.RaresLanded[0].Rare);
            Assert.AreEqual("Hero", result.RaresLanded[0].FinderName);
        }

        /// <summary>
        /// Run-end drain (round 4): items a build step piled but that no placement pass has claimed yet are, on
        /// a solo run, in the OWNER's held pile - the same store a group member's undelivered pile lives in
        /// (ThreadDungeonRun_Roster.cs is not group-specific). ThreadLootPool.DisposeForRunEnd's DrainHeldPiles
        /// call is roster-generic, so it must destroy a solo owner's pile exactly as it destroys a group
        /// member's, with nothing stranded.
        /// </summary>
        [TestMethod]
        public void Solo_run_end_drains_and_destroys_the_owners_undelivered_pile()
        {
            var run = PooledRun();
            var item = Item(9001);

            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));

            ThreadCacheFiller.BuildPileInto(run, _ => new List<WorldObject> { item }, _ => new List<WorldObject>(), ThreadLootRollBudget.Unlimited());

            Assert.IsTrue(run.HasHeldPile(run.OwnerGuid), "fixture: the item is piled, not yet placed anywhere");
            Assert.IsFalse(item.IsDestroyed);

            run.MarkEnded("test");
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.IsTrue(item.IsDestroyed, "an undelivered piled item must not be stranded out of the world");
            Assert.AreEqual(1, drain.HeldPileItems);
            Assert.IsFalse(run.HasHeldPile(run.OwnerGuid));
        }

        /// <summary>
        /// Item 1a fix (2026-09-22, second review of PR #1284): a late kill's ledger entry, appended AFTER the
        /// snapshot ThreadCachePlacer.Execute takes before calling BuildPile, must not re-arm the "still waiting"
        /// gate for the segment the snapshot already covers - it is left for the run's NEXT placement pass. This
        /// mirrors Execute's own snapshot/gate/build shape (ThreadCacheFiller.BuildPile's maxLedgerEntries cap,
        /// then ledgerClaimed &lt; ledgerSnapshot || run.HasUndealtNonLedgerLoot) directly, since Execute itself
        /// is private and needs a live landblock/owner the rest of this file avoids requiring.
        /// </summary>
        [TestMethod]
        public void A_late_kill_after_the_snapshot_does_not_block_placing_the_already_built_segment()
        {
            var run = PooledRun();
            var first = Item(9101);
            var second = Item(9102);
            var late = Item(9103);

            // The "main clear": two kills already in the ledger when a placement pass takes its snapshot.
            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));
            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));

            var ledgerSnapshot = run.LedgerCount;
            Assert.AreEqual(2, ledgerSnapshot, "fixture: the snapshot covers the two already-banked kills");

            // The straggler: a THIRD kill lands and banks its entry AFTER the snapshot was taken, before this
            // pass's build even runs - ThreadDungeonRun_LootPool.TryAppendLootEntry accepts it in Cleared too.
            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));

            var items = new Queue<WorldObject>(new[] { first, second, late });
            var ledgerClaimed = ThreadCacheFiller.BuildPileInto(run, _ => new List<WorldObject> { items.Dequeue() },
                _ => new List<WorldObject>(), ThreadLootRollBudget.Unlimited(), maxLedgerEntries: ledgerSnapshot);

            Assert.AreEqual(2, ledgerClaimed, "only the snapshot's worth of entries claimed this call");
            Assert.AreEqual(1, run.LedgerCount, "the late kill's entry is left at the head of the ledger, untouched");

            // Execute's own gate (item 1a fix): satisfied once the snapshot is claimed, whatever the ledger still
            // holds from a later kill. Before the fix this read run.HasUndealtLoot, which is STILL true here
            // (LedgerCount 1 > 0) - proving the old gate would have kept waiting on the straggler forever.
            var gateStillWaiting = ledgerClaimed < ledgerSnapshot || run.HasUndealtNonLedgerLoot;
            Assert.IsFalse(gateStillWaiting, "the snapshot is satisfied, so placement proceeds without the late kill");
            Assert.IsTrue(run.HasUndealtLoot, "control: the OLD gate (run.HasUndealtLoot) would still be blocked by the late entry");

            // First segment places now, without the late kill.
            var cache1 = Cache();
            var result1 = ThreadGroupCacheDelivery.FillFromPile(run, cache1, run.OwnerGuid);

            Assert.AreEqual(2, cache1.Inventory.Count, "the segment the snapshot covered lands in full");
            CollectionAssert.AreEquivalent(new WorldObject[] { first, second }, cache1.Inventory.Values.ToArray());
            Assert.IsFalse(cache1.Inventory.Values.Contains(late), "the late kill's item has not been built yet, let alone placed");
            Assert.IsFalse(run.HasHeldPile(run.OwnerGuid), "the pile is empty again: only the snapshot's items were ever piled");

            // Next pass: the late kill's own entry builds and places, merged into the standing cache via the SAME
            // top-up path Execute's Auto mode already runs (ThreadGroupCacheDelivery.FillFromPile / TopUpExisting).
            var nextSnapshot = run.LedgerCount;
            Assert.AreEqual(1, nextSnapshot);

            var lateClaimed = ThreadCacheFiller.BuildPileInto(run, _ => new List<WorldObject> { late },
                _ => new List<WorldObject>(), ThreadLootRollBudget.Unlimited(), maxLedgerEntries: nextSnapshot);

            Assert.AreEqual(1, lateClaimed);
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsTrue(run.HasHeldPile(run.OwnerGuid), "the late kill is piled, ready for this run's next placement pass");

            var result2 = ThreadGroupCacheDelivery.FillFromPile(run, cache1, run.OwnerGuid);

            Assert.AreEqual(3, cache1.Inventory.Count, "the late item lands in the SAME already-placed cache (a matching stack would TopUpExisting; a fresh item like this one is simply added)");
            Assert.IsTrue(cache1.Inventory.Values.Contains(late), "the late kill's item lands on the next pass");
            Assert.IsFalse(run.HasHeldPile(run.OwnerGuid));
            CollectionAssert.AreEqual(new WorldObject[0], result1.Added.Intersect(result2.Added).ToArray(), "nothing double-placed across the two passes");
        }

        [TestMethod]
        public void A_fill_that_throws_partway_still_delivers_what_already_landed()
        {
            var run = PooledRunWithLoot(2);
            var rare = Item(8800);
            Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, rare, "Tester")));
            Container accepted = null;
            var delivered = new List<(Container Cache, CacheFillResult Result)>();
            var calls = 0;

            // Entry 1 lands 3 items, entry 2 throws, and entry 3 (the rare) is never reached.
            Func<ThreadLootLedgerEntry, List<WorldObject>> throwsOnSecond = e =>
            {
                if (++calls == 2) throw new InvalidOperationException("roll failed");
                return ThreeItems(e);
            };

            Assert.ThrowsExactly<InvalidOperationException>(() => ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(1),
                _ => accepted = AcceptedCache(run),
                (r, cache) => ThreadCacheFiller.Fill(r, cache, throwsOnSecond, OneBonus),
                (cache, result) => delivered.Add((cache, result))));

            Assert.AreEqual(1, delivered.Count, "what landed before the throw is delivered once");
            Assert.AreSame(accepted, delivered[0].Cache);
            Assert.AreEqual(4, delivered[0].Result.Added.Count, "bonus 1 + entry 1 x 3 items");
            Assert.AreEqual(4, accepted.Inventory.Count);
            Assert.IsFalse(accepted.IsDestroyed, "a cache holding items is kept");
            CollectionAssert.AreEqual(new[] { accepted }, run.PlacedCachesSnapshot());
            Assert.AreEqual(1, run.LedgerCount, "the rare's entry is still pooled");
        }

        [TestMethod]
        public void A_fill_that_throws_before_anything_lands_destroys_the_fresh_cache()
        {
            var run = PooledRun();
            run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null));
            Container accepted = null;
            var delivered = 0;

            Assert.ThrowsExactly<InvalidOperationException>(() => ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => Candidates(1),
                _ => accepted = AcceptedCache(run),
                (r, cache) => ThreadCacheFiller.Fill(r, cache, _ => throw new InvalidOperationException("roll failed"), OneBonus),
                (cache, result) => delivered++));

            Assert.AreEqual(0, delivered);
            Assert.IsTrue(accepted.IsDestroyed, "an empty fresh cache is not left standing");
            Assert.AreEqual(0, run.PlacedCachesSnapshot().Count);
        }

        [TestMethod]
        public void A_chest_that_is_not_empty_is_refused_as_a_bug()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => ThreadCachePlacer.PlaceFirstAccepted(Candidates(1), _ =>
            {
                var c = Cache();
                c.TryAddToInventory(Item(5));
                return c;
            }));
        }

        [TestMethod]
        public void Player_facing_lines_are_the_spec_text()
        {
            Assert.AreEqual("Your Thread Cache has formed nearby.", ThreadCachePlacer.FormedMessage);
            Assert.AreEqual("Your Thread Cache could not find room to form. Move to open ground and use /spawncache.", ThreadCachePlacer.NoRoomMessage);

            // A landed rare broadcasts the corpse's own text, through the one constant Task 3 extracted.
            var deliver = PooledLootSourceText.MethodBody(Src(), "private static void Deliver(ThreadDungeonRun run, Container cache, CacheFillResult result)");
            StringAssert.Contains(deliver, "string.Format(Corpse.RareDiscoveredFormat, landed.FinderName, landed.Rare.Name)");
            Assert.IsFalse(Src().Contains("has discovered"), "no second copy of the rare text");
        }

        private static string Src() => PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs");

        [TestMethod]
        public void Each_attempt_is_a_fresh_chest_that_enters_empty_and_is_checked_after_landing()
        {
            var body = PooledLootSourceText.MethodBody(Src(), "private static Container TryPlaceEmpty(");

            var landblock = body.IndexOf("run.Dungeon.Landblock", StringComparison.Ordinal);
            var los = body.IndexOf("owner.IsDirectVisible(position)", StringComparison.Ordinal);
            var create = body.IndexOf("ThreadDungeonRewardSpawner.CreateCacheChest(run, \"pooled cache\")", StringComparison.Ordinal);
            var ethereal = body.IndexOf("chest.Ethereal = true;", StringComparison.Ordinal);
            var suppress = body.IndexOf("SuppressGenerateEffect = true", StringComparison.Ordinal);
            var enter = body.IndexOf("chest.EnterWorld()", StringComparison.Ordinal);
            var landedBlock = body.IndexOf("landed.LandblockId.Landblock != run.Dungeon.Landblock", StringComparison.Ordinal);
            var height = body.IndexOf("ThreadCachePlacement.MaxZDelta", StringComparison.Ordinal);
            var register = body.IndexOf("run.TryRegisterCache(chest)", StringComparison.Ordinal);
            var effect = body.IndexOf("ApplyVisualEffects(PlayScript.Create)", StringComparison.Ordinal);

            Assert.IsTrue(landblock >= 0 && landblock < los && los < create && create < ethereal && ethereal < suppress && suppress < enter,
                "user ruling 2026-09-14: pooled caches are ethereal, set before EnterWorld so InitPhysicsObj builds the state with it");
            Assert.IsTrue(enter < landedBlock && landedBlock < height && height < register && register < effect,
                "after landing, the landblock, the height and the exclusions are all re-checked before the chest is registered");
            Assert.IsFalse(body.Contains("Fill("), "no contents inside an attempt");
            Assert.IsTrue(body.Split(new[] { "chest.Destroy();" }, StringSplitOptions.None).Length - 1 >= 3, "every failure after creation destroys the attempt");
        }

        [TestMethod]
        public void Move_places_the_new_cache_before_touching_the_old_one()
        {
            var body = PooledLootSourceText.MethodBody(Src(), "private static void MoveCaches(");

            var place = body.IndexOf("PlaceNewCache(run, scan, owner)", StringComparison.Ordinal);
            var bail = body.IndexOf("if (fresh == null)", StringComparison.Ordinal);
            var transfer = body.IndexOf("ThreadCacheFiller.TransferContents(run, old, fresh)", StringComparison.Ordinal);
            var destroy = body.IndexOf("old.Destroy()", StringComparison.Ordinal);

            Assert.IsTrue(place >= 0 && place < bail && bail < transfer && transfer < destroy);

            // A moved cache is a fresh chest placed through the same attempt, so it is ethereal too.
            StringAssert.Contains(PooledLootSourceText.MethodBody(Src(), "private static Chest PlaceNewCache("), "TryPlaceEmpty(run, owner, c, exclusions)");
        }

        [TestMethod]
        public void Delivery_runs_as_one_latched_landblock_action_chain_and_tells_the_owner_once_per_pass()
        {
            var src = Src();
            var request = PooledLootSourceText.MethodBody(src, "public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode, uint requesterGuid)");

            StringAssert.Contains(request, "run.TryBeginCachePlacement(DateTime.UtcNow)");
            StringAssert.Contains(request, "landblock.EnqueueAction(new ActionEventDelegate(");
            StringAssert.Contains(request, "run.IsCachePlacementCurrent(token)");
            StringAssert.Contains(request, "finally");
            StringAssert.Contains(request, "run.EndCachePlacement(token)");

            var execute = PooledLootSourceText.MethodBody(src, "private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)");

            // Round 4 (owner ruling 2026-09-22): the solo body builds into the pile (budget-limited), waits for
            // the shared pool to fully drain before placing anything, then reuses the group pile-placement path.
            // Item 1a fix (2026-09-22): the wait is now bounded by a ledger snapshot, not "no undealt loot ever" -
            // see ledgerSnapshot/ledgerClaimed and HasUndealtNonLedgerLoot below.
            var build = execute.IndexOf("ThreadCacheFiller.BuildPile(run, budget, ledgerSnapshot)", StringComparison.Ordinal);
            var snapshotGate = build < 0 ? -1 : execute.IndexOf("ledgerClaimed < ledgerSnapshot || run.HasUndealtNonLedgerLoot", build, StringComparison.Ordinal);
            var topUp = snapshotGate < 0 ? -1 : execute.IndexOf("run.PlacedCachesSnapshot()", snapshotGate, StringComparison.Ordinal);
            // FillFromPile is used TWICE: once in the top-up loop (between topUp and form), once as
            // FormAndFillAtMember's own fill callback (which lives inside ThreadCachePlacer.cs, not repeated
            // here in Execute's own body) - so the pin below only needs the top-up loop's occurrence, between
            // topUp and form.
            var fillFromPile = topUp < 0 ? -1 : execute.IndexOf("ThreadGroupCacheDelivery.FillFromPile(r, c, run.OwnerGuid)", topUp, StringComparison.Ordinal);
            var form = fillFromPile < 0 ? -1 : execute.IndexOf("FormAndFillAtMember(run, landblock, owner,", fillFromPile, StringComparison.Ordinal);
            var formed = form < 0 ? -1 : execute.IndexOf("FormedMessage", form, StringComparison.Ordinal);

            // Only BUILDING is time-limited (round 4): the budget gates the build call, and neither the top-up
            // loop nor FormAndFillAtMember takes it any more.
            StringAssert.Contains(execute, "run.HasUndealtLoot && !budget.Exhausted", "the build call is the one budget-gated step");
            StringAssert.Contains(execute, "FormAndFillAtMember(run, landblock, owner, () => run.HasHeldPile(run.OwnerGuid))",
                "placement runs through the roster-generic member pass, unbudgeted");
            StringAssert.Contains(execute, "if (ledgerClaimed < ledgerSnapshot || run.HasUndealtNonLedgerLoot)",
                "placement waits for THIS segment's snapshot, not for a kill that lands after it (item 1a fix)");

            Assert.IsTrue(build >= 0 && build < snapshotGate && snapshotGate < topUp && topUp < fillFromPile && fillFromPile < form,
                "build the snapshot's worth of the list, wait for it to complete, top up placed caches, then form and fill through the group pile path");
            Assert.IsTrue(formed > form && execute.IndexOf("FormedMessage", formed + 1, StringComparison.Ordinal) < 0,
                "spec section 5: one success line per delivery pass, sent once after the loop");

            var loop = PooledLootSourceText.MethodBody(src, "internal static CacheFormPass FormAndFill(");
            Assert.IsFalse(loop.Contains("EnqueueSend"), "the loop tells nobody; Execute sends one line per pass");
            StringAssert.Contains(loop, "log.Warn(", "a fresh cache that accepted nothing is logged, not silent");
        }

        [TestMethod]
        public void A_move_pass_leaves_loot_banked_meanwhile_wanted_for_the_tick_retry()
        {
            // A kill that banks loot while a /spawncache Move is queued finds the latch held and its own trigger is
            // discarded, so the Move pass is the last chance to hand that loot (or transfer overflow) to the Tick retry.
            var execute = PooledLootSourceText.MethodBody(Src(), "private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)");

            var branch = execute.IndexOf("if (mode == CacheRequestMode.Move)", StringComparison.Ordinal);
            var branchEnd = branch < 0 ? -1 : execute.IndexOf("if (mode == CacheRequestMode.Auto)", branch, StringComparison.Ordinal);
            Assert.IsTrue(branch >= 0 && branchEnd > branch, "Move branch anchors moved in Execute; re-read the method");

            var move = execute.Substring(branch, branchEnd - branch);
            var moved = move.IndexOf("MoveCaches(run, landblock, owner)", StringComparison.Ordinal);
            var unclaimed = moved < 0 ? -1 : move.IndexOf("run.HasUnclaimedLoot", moved, StringComparison.Ordinal);
            var wanted = unclaimed < 0 ? -1 : move.IndexOf("run.MarkPlacementWanted()", unclaimed, StringComparison.Ordinal);
            var returns = wanted < 0 ? -1 : move.IndexOf("return;", wanted, StringComparison.Ordinal);

            Assert.IsTrue(moved >= 0 && unclaimed > moved && wanted > unclaimed && returns > wanted,
                "the Move branch must move the caches, then mark placement wanted when loot is still unclaimed, before it returns");
        }
    }
}

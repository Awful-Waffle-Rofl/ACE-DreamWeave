using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Threads item 8 (2026-09-18): identical stackables are merged before a set of pooled loot is handed out, a pure
    /// object-count reduction. Objects are in-memory (static-range guids), so Destroy never reaches the database.
    /// </summary>
    [TestClass]
    public class ThreadLootStackingTests
    {
        private static Stackable Stack(uint wcid, int size, int max = 100, int unitValue = 5, int unitBurden = 10, Action<Stackable> tweak = null)
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

            var stack = new Stackable(weenie, new ObjectGuid(NextGuid()));
            tweak?.Invoke(stack);
            return stack;
        }

        private static long Total(IEnumerable<WorldObject> items, Func<WorldObject, int?> of) => items.Sum(i => (long)(of(i) ?? 0));

        [TestMethod]
        public void Identical_stacks_merge_into_one_conserving_quantity_value_and_burden_and_the_rest_are_destroyed()
        {
            var a = Stack(9001, 10);
            var b = Stack(9001, 15);
            var c = Stack(9001, 5);
            Assert.AreEqual(50, a.Value, "fixture: a self-consistent stack");

            var merged = ThreadLootStacking.Consolidate(new List<WorldObject> { a, b, c }, out var removed);

            Assert.AreEqual(2, removed);
            Assert.AreEqual(1, merged.Count);
            Assert.AreSame(a, merged[0], "the first object carries the merged stack");
            Assert.AreEqual(30, a.StackSize);
            Assert.AreEqual(150, a.Value);
            Assert.AreEqual(300, a.EncumbranceVal);
            Assert.IsTrue(b.IsDestroyed && c.IsDestroyed);
            Assert.IsFalse(a.IsDestroyed);
        }

        [TestMethod]
        public void A_group_past_MaxStackSize_splits_into_the_fewest_full_stacks()
        {
            var items = new List<WorldObject> { Stack(9002, 60), Stack(9002, 60), Stack(9002, 60) };
            var value = Total(items, i => i.Value);

            var merged = ThreadLootStacking.Consolidate(items, out var removed);

            Assert.AreEqual(1, removed);
            CollectionAssert.AreEqual(new int?[] { 100, 80 }, merged.Select(i => i.StackSize).ToList());
            Assert.AreEqual(value, Total(merged, i => i.Value));
            Assert.AreEqual(1800L, Total(merged, i => i.EncumbranceVal));
        }

        [TestMethod]
        public void Any_differing_property_keeps_two_stacks_of_the_same_wcid_apart()
        {
            var plain = Stack(9003, 10);
            var worn = Stack(9003, 10, tweak: s => s.SetProperty(PropertyInt.Structure, 3));
            var named = Stack(9003, 10, tweak: s => s.SetProperty(PropertyString.Name, "Odd Coin"));
            var dearer = Stack(9003, 10, unitValue: 6);
            var spelled = Stack(9003, 10, tweak: s => s.Biota.GetOrAddKnownSpell(2001, s.BiotaDatabaseLock, out _));

            var merged = ThreadLootStacking.Consolidate(new List<WorldObject> { plain, worn, named, dearer, spelled }, out var removed);

            Assert.AreEqual(0, removed);
            Assert.AreEqual(5, merged.Count);
            Assert.IsTrue(merged.All(i => i.StackSize == 10 && !i.IsDestroyed));
        }

        [TestMethod]
        public void A_different_CreationTimestamp_alone_does_not_stop_a_merge()
        {
            var a = Stack(9004, 10, tweak: s => s.SetProperty(PropertyInt.CreationTimestamp, 1000));
            var b = Stack(9004, 10, tweak: s => s.SetProperty(PropertyInt.CreationTimestamp, 1001));

            var merged = ThreadLootStacking.Consolidate(new List<WorldObject> { a, b }, out var removed);

            Assert.AreEqual(1, removed);
            Assert.AreEqual(20, merged.Single().StackSize);
        }

        [TestMethod]
        public void Rares_non_stackables_single_unit_stacks_and_inconsistent_stacks_are_left_alone()
        {
            var rareA = Stack(9005, 1);
            var rareB = Stack(9005, 1);
            var genericA = Item(9006);
            var genericB = Item(9006);
            var singleA = Stack(9007, 1, max: 1);
            var singleB = Stack(9007, 1, max: 1);
            var skewA = Stack(9008, 10, tweak: s => s.Value = 51);
            var skewB = Stack(9008, 10, tweak: s => s.Value = 51);

            var list = new List<(WorldObject Item, string RareFinderName)>
            {
                (rareA, "Finder"), (rareB, "Finder"), (genericA, null), (genericB, null),
                (singleA, null), (singleB, null), (skewA, null), (skewB, null),
            };

            var merged = ThreadLootStacking.Consolidate(list, out var removed);

            Assert.AreEqual(0, removed);
            CollectionAssert.AreEqual(list.Select(l => l.Item).ToList(), merged.Select(m => m.Item).ToList());
            Assert.AreEqual(2, merged.Count(m => m.RareFinderName == "Finder"), "rares keep their finder for the broadcast");
            Assert.IsFalse(list.Any(l => l.Item.IsDestroyed));
        }

        [TestMethod]
        public void A_merged_stack_takes_its_first_items_position_and_everything_else_keeps_its_order()
        {
            var coinA = Stack(9010, 10);
            var sword = Item(9011);
            var gemA = Stack(9012, 1, unitValue: 100);
            var coinB = Stack(9010, 20);
            var gemB = Stack(9012, 2, unitValue: 100);
            var shield = Item(9013);

            var merged = ThreadLootStacking.Consolidate(new List<WorldObject> { coinA, sword, gemA, coinB, gemB, shield }, out var removed);

            Assert.AreEqual(2, removed);
            CollectionAssert.AreEqual(new WorldObject[] { coinA, sword, gemA, shield }, merged);
            Assert.AreEqual(30, coinA.StackSize);
            Assert.AreEqual(3, gemA.StackSize);
            Assert.AreEqual(300, gemA.Value);
        }

        // ------------------------------------------------------------------------------------------------------
        // Fairness and object count over a simulated group clear
        // ------------------------------------------------------------------------------------------------------

        private sealed class FakeClock
        {
            public double Ms;
            public double Read() => Ms;
        }

        private static ThreadDungeonRun ClearedGroupRun(int seatCount, int snakeOffset)
        {
            var seats = Enumerable.Range(0, seatCount).Select(i => new RosterSeat(0x50000010u + (uint)i * 0x10u, $"P{i}", 7, 150)).ToList();
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var group = GroupScaling.Compute(seatCount, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

            var run = new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.MarkPooledLoot(true);

            foreach (var seat in seats.Skip(1))
                run.SetMemberKey(seat.Guid, 0x80001000u + (seat.Guid & 0xFFFu));

            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: Cleared");
            run.SetSnakeRunOffset(snakeOffset);
            return run;
        }

        [TestMethod]
        public void Six_identical_stackables_split_evenly_across_three_seats_after_the_deal()
        {
            // Owner ruling 2026-09-22: merging identical stackables BEFORE the deal let a whole set's worth of one
            // dense-value stackable (Pyreal Peas and the like) land on a single seat as one pick. Merging now happens
            // per seat, AFTER the snake deal, so six identical units over three seats must land as two units (one
            // merged stack of size 2) on EACH seat, never six on one.
            var run = ClearedGroupRun(3, 0);
            var seats = run.DealSeats();
            Assert.AreEqual(3, seats.Count, "fixture: three seats");

            var dealt = Enumerable.Range(0, 6)
                .Select(_ => ((WorldObject)Stack(9300u, 1, max: 1000, unitValue: 10, unitBurden: 0), (string)null))
                .ToList();

            var handed = ThreadGroupCacheDelivery.HandOut(run, seats, dealt, 1, run.AddToHeldPile);

            Assert.AreEqual(3, handed, "one merged stack handed to each of the three seats");

            foreach (var seat in seats)
            {
                var pile = run.TakeHeldPile(seat).Select(h => h.Item).ToList();
                Assert.AreEqual(1, pile.Count, $"seat 0x{seat:X8} should hold exactly one merged stack, not zero or several");
                Assert.AreEqual(2, pile[0].StackSize, $"seat 0x{seat:X8} should hold 2 units, not the whole six-unit set");
            }
        }

        [TestMethod]
        public void A_heavy_tailed_six_seat_clear_with_stackables_conserves_everything_and_keeps_seat_shares_inside_the_realized_bound()
        {
            // Loot mix per roll: 55% one generic item U{1..999} (5% of those x50, the #1212 heavy tail), 30% a stack of
            // one of three coin-like wcids (1..50 units at 1 pyreal), 15% a stack of one of two gem wcids (1..3 units at
            // 100). Stackables of one wcid are identical, so each seat's own dealt share merges them (round 2,
            // 2026-09-22) - merging happens AFTER the snake deal, so it never changes what the deal itself spread.
            //
            // TOLERANCE, from the picks the snake actually dealt: each materialised item is its own pick, exactly as
            // before item 8 (merging is confined to within a seat, after dealing, and conserves that seat's total
            // Value, so it cannot move share between seats). Random-assignment model: seat total S has
            // Var(S) <= sum(x^2) / n over the picks x, so a pair's share difference has sd <= sqrt(2 sum(x^2) / n) / sum(x).
            // The snake gives each seat an equal count per set and is at least as balanced (the #1212 argument). 4.5 sd
            // per pair over the C(6,2) = 15 pairs.
            const int seatsN = 6;
            const int rolls = 6000;
            const double z = 4.5;

            var rng = new Random(20260918);
            var run = ClearedGroupRun(seatsN, 1);

            for (var i = 0; i < rolls; i++)
                Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null, 1, 0)));

            var created = new List<WorldObject>();
            var clock = new FakeClock();

            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize = e =>
            {
                var items = new List<WorldObject>();

                for (var r = 0; r < e.Rolls; r++)
                {
                    var draw = rng.NextDouble();
                    WorldObject item;

                    if (draw < 0.55)
                    {
                        item = Item();
                        var v = rng.Next(1, 1000);
                        item.Value = rng.NextDouble() < 0.05 ? v * 50 : v;
                    }
                    else if (draw < 0.85)
                        item = Stack(9100u + (uint)rng.Next(0, 3), rng.Next(1, 51), max: 1000, unitValue: 1, unitBurden: 0);
                    else
                        item = Stack(9110u + (uint)rng.Next(0, 2), rng.Next(1, 4), max: 25, unitValue: 100, unitBurden: 5);

                    created.Add(item);
                    items.Add(item);
                }

                clock.Ms += 3 * e.Rolls;
                return items;
            };

            for (var steps = 0; run.HasUndealtLoot && steps < 20000; steps++)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
            }

            Assert.IsFalse(run.HasUndealtLoot);
            var survivors = created.Where(i => !i.IsDestroyed).ToList();

            var perSeat = run.DealSeats().Select(g => run.TakeHeldPile(g).Select(h => h.Item).ToList()).ToList();
            var held = perSeat.SelectMany(p => p).ToList();

            // Conservation: every object that survived merging is held exactly once (per-wcid totals are pinned by the next test).
            Assert.AreEqual(survivors.Count, held.Count);
            Assert.AreEqual(held.Count, held.Distinct().Count());
            Assert.IsTrue(survivors.All(held.Contains));

            var pickValues = created.Select(i => (double)(i.Value ?? 0)).ToList();
            var sum = pickValues.Sum();
            var sumSq = pickValues.Sum(x => x * x);
            var tolerance = z * Math.Sqrt(2 * sumSq / seatsN) / sum;
            var shares = perSeat.Select(p => p.Sum(i => (double)(i.Value ?? 0)) / sum).ToArray();

            Console.WriteLine($"rolls={rolls} objectsBuilt={created.Count} objectsHandedOut={held.Count} stacksMerged={run.Perf.StacksMerged} " +
                              $"reduction={(1.0 - (double)held.Count / created.Count):P1}");
            Console.WriteLine($"shares {string.Join(" ", shares.Select(s => s.ToString("F4")))} spread={shares.Max() - shares.Min():F4} bound={tolerance:F4}");

            Assert.AreEqual(created.Count - held.Count, (int)run.Perf.StacksMerged, "every object merged away is counted once");
            Assert.IsTrue(run.Perf.StacksMerged > 0, "the mix does merge");
            Assert.IsTrue(shares.Max() - shares.Min() <= tolerance, $"spread {shares.Max() - shares.Min():F4} > {tolerance:F4}");
        }

        // ------------------------------------------------------------------------------------------------------
        // SortForPlacement / SortPileForPlacement (owner correction, 2026-09-22): stackables grouped by wcid first,
        // groups ordered by the group's total Value descending, then everything else by Value descending. Stable.
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void SortForPlacement_groups_stackables_by_wcid_before_gear_and_orders_both_by_value_descending()
        {
            var cheapStack = Stack(9500, 2, unitValue: 3); // group total 6
            var pricierStack1 = Stack(9501, 1, unitValue: 50); // group total 100 (split across two objects)
            var pricierStack2 = Stack(9501, 1, unitValue: 50);
            var sword = Item(9502);
            sword.Value = 40;
            var shield = Item(9503);
            shield.Value = 90;

            var items = new List<(WorldObject Item, string RareFinderName)>
            {
                (sword, null), (cheapStack, null), (pricierStack1, null), (shield, null), (pricierStack2, null),
            };

            var sorted = ThreadLootStacking.SortForPlacement(items);

            // Stackables first (wcid 9501's group, total 100, before wcid 9500's group, total 6), then gear by
            // Value descending (shield 90 before sword 40).
            CollectionAssert.AreEqual(new WorldObject[] { pricierStack1, pricierStack2, cheapStack, shield, sword }, sorted.Select(s => s.Item).ToList());
        }

        [TestMethod]
        public void SortForPlacement_is_stable_for_ties_and_leaves_short_lists_alone()
        {
            var a = Item(9504);
            a.Value = 10;
            var b = Item(9505);
            b.Value = 10;

            var sorted = ThreadLootStacking.SortForPlacement(new List<(WorldObject Item, string RareFinderName)> { (a, null), (b, null) });
            CollectionAssert.AreEqual(new WorldObject[] { a, b }, sorted.Select(s => s.Item).ToList(), "equal values keep input order");

            Assert.IsNull(ThreadLootStacking.SortForPlacement(null));
            var single = new List<(WorldObject Item, string RareFinderName)> { (a, null) };
            Assert.AreSame(single, ThreadLootStacking.SortForPlacement(single));
        }

        [TestMethod]
        public void SortPileForPlacement_matches_the_tuple_overload_for_a_held_pile()
        {
            var stackA = Stack(9600, 3, unitValue: 20); // total 60
            var stackB = Stack(9601, 1, unitValue: 5); // total 5
            var gearHigh = Item(9602);
            gearHigh.Value = 200;
            var gearLow = Item(9603);
            gearLow.Value = 15;

            var pile = new List<HeldPileItem>
            {
                new HeldPileItem(gearLow, null, 1),
                new HeldPileItem(stackB, null, 1),
                new HeldPileItem(gearHigh, null, 2),
                new HeldPileItem(stackA, null, 2),
            };

            var sorted = ThreadLootStacking.SortPileForPlacement(pile);

            CollectionAssert.AreEqual(new WorldObject[] { stackA, stackB, gearHigh, gearLow }, sorted.Select(h => h.Item).ToList());
        }

        // ------------------------------------------------------------------------------------------------------
        // ConsolidatePile (owner correction, 2026-09-22): merges a HELD PILE across every round in it, and records
        // the rounds a merged-away item carried so the caller can still mark them received (ruling R21).
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ConsolidatePile_merges_identical_stacks_across_rounds_and_tracks_the_absorbed_rounds()
        {
            var a = Stack(9700, 10);
            var b = Stack(9700, 15);
            var c = Stack(9700, 5);
            var gear = Item(9701);

            var pile = new List<HeldPileItem>
            {
                new HeldPileItem(a, null, 1),
                new HeldPileItem(gear, null, 1),
                new HeldPileItem(b, null, 2),
                new HeldPileItem(c, null, 3),
            };

            var merged = ThreadLootStacking.ConsolidatePile(pile, out var removed);

            Assert.AreEqual(2, removed);
            Assert.AreEqual(2, merged.Count, "the merged stack plus the untouched gear");
            var survivor = merged.Single(h => ReferenceEquals(h.Item, a));
            Assert.AreEqual(30, a.StackSize);
            Assert.AreEqual(1, survivor.Round, "the survivor keeps its own (first) round");
            CollectionAssert.AreEquivalent(new[] { 2, 3 }, survivor.MergedRounds.ToList(), "the absorbed rounds are recorded");
            Assert.IsTrue(b.IsDestroyed && c.IsDestroyed);

            var gearHeld = merged.Single(h => ReferenceEquals(h.Item, gear));
            Assert.IsNull(gearHeld.MergedRounds, "an item nothing merged into carries no extra rounds");
        }

        [TestMethod]
        public void ConsolidatePile_leaves_a_short_or_all_distinct_pile_unchanged()
        {
            Assert.IsNull(ThreadLootStacking.ConsolidatePile(null, out var removedNull));
            Assert.AreEqual(0, removedNull);

            var single = new List<HeldPileItem> { new HeldPileItem(Item(9702), null, 1) };
            Assert.AreSame(single, ThreadLootStacking.ConsolidatePile(single, out var removedSingle));
            Assert.AreEqual(0, removedSingle);

            var distinct = new List<HeldPileItem> { new HeldPileItem(Item(9703), null, 1), new HeldPileItem(Item(9704), null, 2) };
            var result = ThreadLootStacking.ConsolidatePile(distinct, out var removedDistinct);
            Assert.AreEqual(0, removedDistinct);
            CollectionAssert.AreEqual(distinct.Select(h => h.Item).ToList(), result.Select(h => h.Item).ToList());
        }

        [TestMethod]
        public void ConsolidatePile_with_two_surviving_stacks_never_gives_one_survivor_the_others_own_round()
        {
            // Code review of PR #1284, finding 1. The A_group_past_MaxStackSize_splits_into_the_fewest_full_stacks
            // shape (3x60, max 100) merges to TWO surviving stacks (100 + 80), with only the third (index 2)
            // destroyed. Each survivor's MergedRounds must carry ONLY the destroyed round (3) - never the OTHER
            // survivor's own round (a bug the previous "every round except my own" rule produced).
            var a = Stack(9705, 60, max: 100);
            var b = Stack(9705, 60, max: 100);
            var c = Stack(9705, 60, max: 100);

            var pile = new List<HeldPileItem>
            {
                new HeldPileItem(a, null, 1),
                new HeldPileItem(b, null, 2),
                new HeldPileItem(c, null, 3),
            };

            var merged = ThreadLootStacking.ConsolidatePile(pile, out var removed);

            Assert.AreEqual(1, removed);
            Assert.AreEqual(2, merged.Count, "two surviving stacks");
            Assert.IsTrue(c.IsDestroyed);
            Assert.IsFalse(a.IsDestroyed && b.IsDestroyed);

            var survivorA = merged.Single(h => ReferenceEquals(h.Item, a));
            var survivorB = merged.Single(h => ReferenceEquals(h.Item, b));

            Assert.AreEqual(1, survivorA.Round);
            CollectionAssert.AreEqual(new[] { 3 }, survivorA.MergedRounds.ToList(), "only the destroyed round - never b's own round 2");

            Assert.AreEqual(2, survivorB.Round);
            CollectionAssert.AreEqual(new[] { 3 }, survivorB.MergedRounds.ToList(), "only the destroyed round - never a's own round 1");
        }

        // ------------------------------------------------------------------------------------------------------
        // TopUpExisting (owner correction, 2026-09-22; code review of PR #1284, findings 2 and 4)
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TopUpExisting_updates_the_caches_own_aggregate_value_and_burden()
        {
            var run = PooledRun();
            var cache = Cache(capacity: 10);
            var already = Stack(9706, 20);
            Assert.IsTrue(cache.TryAddToInventory(already, 0));
            Assert.AreEqual(100, cache.Value, "fixture: Container.TryAddToInventory already totals its contents");
            Assert.AreEqual(200, cache.EncumbranceVal);

            var topUp = Stack(9706, 5);

            Assert.IsTrue(ThreadLootStacking.TopUpExisting(run, cache, topUp));

            Assert.AreEqual(25, already.StackSize);
            Assert.AreEqual(125, cache.Value, "the cache's own aggregate grows with the topped-up stack, not just the stack object");
            Assert.AreEqual(250, cache.EncumbranceVal);
        }

        // TopUpExisting_invalidates_the_caches_recorded_display_sort_signature removed (CLEANUP item 3, code review
        // of PR #1284, 2026-09-22): ThreadDungeonRun's cache-sort-signature store (SetCacheSortSignature,
        // TryGetCacheSortSignature, InvalidateCacheSortSignature) is gone. It existed only to serve
        // ThreadCacheSort.SortRunCaches's per-cache loop, which round 4 deleted, so TopUpExisting no longer
        // invalidates anything - see ThreadLootStacking.cs's TopUpExisting for the removed call site.

        [TestMethod]
        public void Merging_after_the_deal_conserves_total_value_burden_and_quantity_per_wcid()
        {
            var rng = new Random(7);
            var run = ClearedGroupRun(4, 0);

            for (var i = 0; i < 480; i++)
                Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null, 1, 0)));

            var before = new Dictionary<uint, (long Qty, long Value, long Burden)>();

            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize = e =>
            {
                var s = Stack(9200u + (uint)rng.Next(0, 4), rng.Next(1, 40), max: 100, unitValue: 3, unitBurden: 2);
                before.TryGetValue(s.WeenieClassId, out var t);
                before[s.WeenieClassId] = (t.Qty + (s.StackSize ?? 1), t.Value + (s.Value ?? 0), t.Burden + (s.EncumbranceVal ?? 0));
                return new List<WorldObject> { s };
            };

            for (var steps = 0; run.HasUndealtLoot && steps < 5000; steps++)
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>());

            var held = run.DealSeats().SelectMany(g => run.TakeHeldPile(g).Select(h => h.Item)).ToList();
            var after = held.GroupBy(i => i.WeenieClassId).ToDictionary(g => g.Key,
                g => (Qty: g.Sum(i => (long)(i.StackSize ?? 1)), Value: g.Sum(i => (long)(i.Value ?? 0)), Burden: g.Sum(i => (long)(i.EncumbranceVal ?? 0))));

            CollectionAssert.AreEquivalent(before.Keys.ToList(), after.Keys.ToList());

            foreach (var wcid in before.Keys)
                Assert.AreEqual(before[wcid], after[wcid], $"wcid {wcid}");

            Assert.IsTrue(held.Count < 480, $"merged into {held.Count} objects");
            Assert.IsTrue(held.All(i => i.StackSize <= 100));
        }
    }
}

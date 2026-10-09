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
    [TestClass]
    public class ThreadCacheFillerTests
    {
        /// <summary>An entry whose fake materialisation is `count` items with wcids tag*10+0.., then its rare.</summary>
        private static ThreadLootLedgerEntry Entry(uint tag, WorldObject rare = null)
            => new ThreadLootLedgerEntry(false, false, new ACE.Database.Models.World.TreasureDeath { Tier = (int)tag }, null, rare, rare == null ? null : "Tester");

        private static Func<ThreadLootLedgerEntry, List<WorldObject>> Items(int count)
            => e =>
            {
                var list = Enumerable.Range(0, count).Select(i => Item((uint)e.Profile.Tier * 10 + (uint)i)).ToList();
                if (e.HeldRare != null) list.Add(e.HeldRare);
                return list;
            };

        private static readonly Func<ThreadDungeonRun, List<WorldObject>> NoBonus = _ => new List<WorldObject>();

        private static uint[] WcidsInSlotOrder(Container cache)
            => cache.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).Select(i => i.WeenieClassId).ToArray();

        [TestMethod]
        public void Fill_order_is_overflow_then_bonus_then_ledger_at_increasing_positions()
        {
            var run = PooledRun();
            run.TryAddOverflow(Item(1), null);
            run.TryMarkLootBonusPending();
            run.TryAppendLootEntry(Entry(2));
            run.TryAppendLootEntry(Entry(3));
            var cache = Cache();

            var result = ThreadCacheFiller.Fill(run, cache, Items(2), _ => new List<WorldObject> { Item(900), Item(901) });

            CollectionAssert.AreEqual(new uint[] { 1, 900, 901, 20, 21, 30, 31 }, WcidsInSlotOrder(cache));
            CollectionAssert.AreEqual(Enumerable.Range(0, 7).ToArray(), cache.Inventory.Values.Select(i => i.PlacementPosition ?? -1).OrderBy(p => p).ToArray(),
                "explicit increasing positions: no item was inserted at 0 and shifted the rest");
            Assert.IsTrue(result.BonusClaimed);
            Assert.AreEqual(2, result.EntriesClaimed);
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void Fill_appends_after_the_highest_existing_position()
        {
            var run = PooledRun();
            var cache = Cache();
            var a = Item(7001);
            var b = Item(7002);
            var c = Item(7003);
            Assert.IsTrue(cache.TryAddToInventory(a, 0));
            Assert.IsTrue(cache.TryAddToInventory(b, 1));
            Assert.IsTrue(cache.TryAddToInventory(c, 5));
            run.TryAppendLootEntry(Entry(4));

            ThreadCacheFiller.Fill(run, cache, Items(2), NoBonus);

            Assert.AreEqual(0, a.PlacementPosition);
            Assert.AreEqual(1, b.PlacementPosition);
            Assert.AreEqual(5, c.PlacementPosition);
            CollectionAssert.AreEqual(new uint[] { 7001, 7002, 7003, 40, 41 }, WcidsInSlotOrder(cache));
        }

        [TestMethod]
        public void Overflow_splits_at_120_and_the_next_cache_takes_it_first()
        {
            var run = PooledRun();
            for (uint tag = 1; tag <= 18; tag++)
                run.TryAppendLootEntry(Entry(tag));

            var first = Cache();
            var r1 = ThreadCacheFiller.Fill(run, first, Items(7), NoBonus);

            Assert.AreEqual(120, first.Inventory.Count);
            Assert.AreEqual(18, r1.EntriesClaimed, "the 18th entry is claimed even though only one of its items fits");
            Assert.AreEqual(6, run.OverflowCount);
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsTrue(run.HasUnclaimedLoot);

            var second = Cache();
            var r2 = ThreadCacheFiller.Fill(run, second, Items(7), NoBonus);

            CollectionAssert.AreEqual(new uint[] { 181, 182, 183, 184, 185, 186 }, WcidsInSlotOrder(second));
            Assert.AreEqual(0, r2.EntriesClaimed);
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_full_cache_claims_nothing()
        {
            var run = PooledRun();
            var cache = Cache(capacity: 1);
            Assert.IsTrue(cache.TryAddToInventory(Item(5000), 0));
            run.TryMarkLootBonusPending();
            run.TryAppendLootEntry(Entry(6));

            var result = ThreadCacheFiller.Fill(run, cache, Items(3), _ => throw new AssertFailedException("the bonus must not be built"));

            Assert.AreEqual(0, result.EntriesClaimed);
            Assert.IsFalse(result.BonusClaimed);
            Assert.AreEqual(1, run.LedgerCount);
            Assert.IsTrue(run.IsLootBonusPending);
        }

        [TestMethod]
        public void Invariants_1_and_2_a_partly_fitting_entry_is_claimed_once_and_every_item_has_one_home()
        {
            var run = PooledRun();
            run.TryAppendLootEntry(Entry(7));
            var small = Cache(capacity: 3);

            ThreadCacheFiller.Fill(run, small, Items(5), NoBonus);

            Assert.AreEqual(3, small.Inventory.Count);
            Assert.AreEqual(2, run.OverflowCount);
            Assert.AreEqual(0, run.LedgerCount, "never returned to the ledger once its items exist");

            var big = Cache();
            ThreadCacheFiller.Fill(run, big, Items(5), NoBonus);

            var all = small.Inventory.Values.Concat(big.Inventory.Values).Select(i => i.WeenieClassId).OrderBy(w => w).ToArray();
            CollectionAssert.AreEqual(new uint[] { 70, 71, 72, 73, 74 }, all);
            Assert.AreEqual(0, run.OverflowCount);
        }

        [TestMethod]
        public void A_rare_reports_its_finder_when_it_lands_even_from_overflow()
        {
            var run = PooledRun();
            var rare = Item(8888);
            run.TryAppendLootEntry(Entry(8, rare));
            var small = Cache(capacity: 5);

            var r1 = ThreadCacheFiller.Fill(run, small, Items(5), NoBonus);
            Assert.AreEqual(0, r1.RaresLanded.Count, "the rare overflowed");

            var r2 = ThreadCacheFiller.Fill(run, Cache(), Items(5), NoBonus);
            Assert.AreEqual(1, r2.RaresLanded.Count);
            Assert.AreSame(rare, r2.RaresLanded[0].Rare);
            Assert.AreEqual("Tester", r2.RaresLanded[0].FinderName);
        }

        [TestMethod]
        public void A_held_rare_left_out_of_the_materialisation_still_gets_a_home()
        {
            var run = PooledRun();
            var rare = Item(8889);
            run.TryAppendLootEntry(Entry(10, rare));
            var cache = Cache();

            var result = ThreadCacheFiller.Fill(run, cache, _ => null, NoBonus);

            Assert.AreEqual(1, result.EntriesClaimed);
            Assert.AreSame(rare, cache.Inventory.Values.Single(), "the claim removed the entry, so the held rare must land or overflow");
            Assert.AreEqual(1, result.RaresLanded.Count);
            Assert.AreEqual("Tester", result.RaresLanded[0].FinderName);
            Assert.AreEqual(0, run.OverflowCount);
        }

        [TestMethod]
        public void A_held_rare_that_lands_from_the_materialisation_is_not_placed_again()
        {
            var run = PooledRun();
            var rare = Item(8892);
            run.TryAppendLootEntry(Entry(13, rare));
            var cache = Cache();

            var result = ThreadCacheFiller.Fill(run, cache, Items(2), NoBonus);

            Assert.AreEqual(3, cache.Inventory.Count);
            Assert.AreEqual(1, result.RaresLanded.Count);
            Assert.AreEqual(0, result.Overflowed);
            Assert.AreEqual(0, run.OverflowCount, "invariant 2: the landed rare is not also in overflow");
            Assert.IsFalse(rare.IsDestroyed);
        }

        [TestMethod]
        public void A_held_rare_that_lands_from_the_materialisation_on_an_ended_run_is_not_destroyed_in_the_cache()
        {
            var run = PooledRun();
            var rare = Item(8893);
            run.TryAppendLootEntry(Entry(14, rare));
            run.MarkEnded("test");
            var cache = Cache();

            var result = ThreadCacheFiller.Fill(run, cache, Items(2), NoBonus);

            Assert.AreEqual(3, cache.Inventory.Count);
            Assert.IsTrue(cache.Inventory.ContainsKey(rare.Guid));
            Assert.AreEqual(0, result.Overflowed);
            Assert.IsFalse(rare.IsDestroyed, "a rare sitting in the cache must not be destroyed by a second placement");
        }

        [TestMethod]
        public void A_held_rare_that_overflows_from_the_materialisation_is_overflowed_once()
        {
            var run = PooledRun();
            var rare = Item(8894);
            run.TryAppendLootEntry(Entry(15, rare));
            var cache = Cache(capacity: 2);

            var result = ThreadCacheFiller.Fill(run, cache, Items(2), NoBonus);

            Assert.AreEqual(2, cache.Inventory.Count);
            Assert.AreEqual(1, result.Overflowed);
            var carried = run.TakeAllOverflow();
            Assert.AreEqual(1, carried.Count, "invariant 2: the rare holds one overflow slot, not two");
            Assert.AreSame(rare, carried[0].Item);
        }

        [TestMethod]
        public void The_same_object_materialised_twice_has_one_home()
        {
            var run = PooledRun();
            run.TryAppendLootEntry(Entry(16));
            var cache = Cache();
            var x = Item(8895);

            ThreadCacheFiller.Fill(run, cache, _ => new List<WorldObject> { x, x }, NoBonus);

            Assert.AreEqual(1, cache.Inventory.Count);
            Assert.AreEqual(0, run.OverflowCount, "an item already in this cache is not also sent to overflow");
            Assert.IsFalse(x.IsDestroyed);
        }

        [TestMethod]
        public void A_throwing_materialisation_overflows_the_held_rare_instead_of_stranding_it()
        {
            var run = PooledRun();
            var rare = Item(8890);
            run.TryAppendLootEntry(Entry(11, rare));
            var cache = Cache();

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ThreadCacheFiller.Fill(run, cache, _ => throw new InvalidOperationException("roll failed"), NoBonus));

            Assert.AreEqual(0, run.LedgerCount, "invariant 1: the claimed entry is not re-queued");
            Assert.AreEqual(0, cache.Inventory.Count);
            var carried = run.TakeAllOverflow();
            Assert.AreEqual(1, carried.Count);
            Assert.AreSame(rare, carried[0].Item);
            Assert.AreEqual("Tester", carried[0].RareFinderName);
            Assert.IsFalse(rare.IsDestroyed);
        }

        [TestMethod]
        public void A_throwing_materialisation_on_an_ended_run_destroys_the_held_rare()
        {
            var run = PooledRun();
            var rare = Item(8891);
            run.TryAppendLootEntry(Entry(12, rare));
            run.MarkEnded("test");

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ThreadCacheFiller.Fill(run, Cache(), _ => throw new InvalidOperationException("roll failed"), NoBonus));

            Assert.AreEqual(0, run.OverflowCount);
            Assert.IsTrue(rare.IsDestroyed, "no out-of-world item may outlive an ended run");
        }

        [TestMethod]
        public void An_ended_run_destroys_what_the_cache_refuses()
        {
            var run = PooledRun();
            run.TryAppendLootEntry(Entry(9));
            run.MarkEnded("test");
            var cache = Cache(capacity: 2);
            var produced = new List<WorldObject>();

            var result = ThreadCacheFiller.Fill(run, cache, e => { var l = Items(3)(e); produced.AddRange(l); return l; }, NoBonus);

            Assert.AreEqual(2, cache.Inventory.Count);
            Assert.AreEqual(0, run.OverflowCount);
            Assert.IsTrue(produced[2].IsDestroyed, "no out-of-world item may outlive an ended run");
            Assert.AreEqual(0, result.Overflowed, "a destroyed item is not counted as overflowed");
            Assert.AreEqual(1, result.Destroyed);
        }

        [TestMethod]
        public void A_fill_that_throws_partway_leaves_what_already_landed_on_the_exception()
        {
            var run = PooledRun();
            run.TryAppendLootEntry(Entry(17));
            run.TryAppendLootEntry(Entry(18));
            var cache = Cache();
            var calls = 0;

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => ThreadCacheFiller.Fill(run, cache, e =>
            {
                if (++calls == 2) throw new InvalidOperationException("roll failed");
                return Items(2)(e);
            }, NoBonus));

            var partial = ThreadCacheFiller.PartialResultOf(ex);
            Assert.IsNotNull(partial, "the caller can still deliver what landed");
            Assert.AreEqual(2, partial.Added.Count);
            Assert.AreEqual(2, partial.EntriesClaimed);
            CollectionAssert.AreEquivalent(cache.Inventory.Values.ToList(), partial.Added);
            Assert.IsNull(ThreadCacheFiller.PartialResultOf(new InvalidOperationException("unrelated")));
        }

        // ------------------------------------------------------------------------------------------------------
        // Stack consolidation round 2 (owner correction, 2026-09-22): a solo fill is time-budgeted across several
        // steps, so identical stackables landing in the same cache from DIFFERENT steps must still merge - the
        // root-cause bug behind the placeSteps=4 stage runs that never converged into one stack.
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

        [TestMethod]
        public void A_second_fill_step_tops_up_an_identical_stack_the_first_step_already_placed()
        {
            var run = PooledRun();
            var cache = Cache();

            run.TryAppendLootEntry(Entry(20));
            var firstStack = Coins(9800u, 10);
            ThreadCacheFiller.Fill(run, cache, _ => new List<WorldObject> { firstStack }, NoBonus);

            Assert.AreEqual(1, cache.Inventory.Count);
            Assert.AreEqual(10, firstStack.StackSize);

            run.TryAppendLootEntry(Entry(21));
            var secondStack = Coins(9800u, 15);
            ThreadCacheFiller.Fill(run, cache, _ => new List<WorldObject> { secondStack }, NoBonus);

            Assert.AreEqual(1, cache.Inventory.Count, "the second step's identical stack topped up the first, not a second stack");
            Assert.AreSame(firstStack, cache.Inventory.Values.Single());
            Assert.AreEqual(25, firstStack.StackSize);
            Assert.IsTrue(secondStack.IsDestroyed);
        }

        [TestMethod]
        public void Materialize_entry_parity_edges()
        {
            Assert.AreEqual(0, ThreadCacheFiller.MaterializeEntry(null).Count);

            var olthoi = new ThreadLootLedgerEntry(false, true, new ACE.Database.Models.World.TreasureDeath { Tier = 6 }, null, null, null);
            Assert.AreEqual(0, ThreadCacheFiller.MaterializeEntry(olthoi).Count, "an Olthoi kill materialises nothing");

            var rare = Item(9999);
            var rareOnly = new ThreadLootLedgerEntry(false, false, null, null, rare, "Tester");
            CollectionAssert.AreEqual(new[] { rare }, ThreadCacheFiller.MaterializeEntry(rareOnly), "no profile and no affinities leave only the held rare, last");
        }

        [TestMethod]
        public void Materialize_entry_calls_the_same_rolls_as_GenerateTreasure()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCacheFiller.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> MaterializeEntry(ThreadLootLedgerEntry entry)");

            var loot = body.IndexOf("Creature.RollDeathTreasureItems(entry.Profile)", StringComparison.Ordinal);
            var salvage = body.IndexOf("Creature.RollSalvageAffinityItems(entry.SalvageAffinities, entry.Profile?.Tier ?? 1)", StringComparison.Ordinal);
            var rare = body.IndexOf("items.Add(entry.HeldRare)", StringComparison.Ordinal);

            Assert.IsTrue(loot >= 0 && loot < salvage && salvage < rare, "corpse order: death treasure, salvage affinity, then the rare");
        }

        [TestMethod]
        public void TransferContents_keeps_slot_order_and_empties_the_source()
        {
            var run = PooledRun();
            var from = Cache();
            Assert.IsTrue(from.TryAddToInventory(Item(1), 0));
            Assert.IsTrue(from.TryAddToInventory(Item(2), 1));
            Assert.IsTrue(from.TryAddToInventory(Item(3), 2));
            var to = Cache();

            var moved = ThreadCacheFiller.TransferContents(run, from, to);

            Assert.AreEqual(3, moved);
            Assert.AreEqual(0, from.Inventory.Count);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, WcidsInSlotOrder(to));
        }

        [TestMethod]
        public void TransferContents_overflows_what_the_destination_refuses()
        {
            var run = PooledRun();
            var from = Cache();
            Assert.IsTrue(from.TryAddToInventory(Item(1), 0));
            Assert.IsTrue(from.TryAddToInventory(Item(2), 1));
            Assert.IsTrue(from.TryAddToInventory(Item(3), 2));
            var to = Cache(capacity: 2);

            ThreadCacheFiller.TransferContents(run, from, to);

            Assert.AreEqual(0, from.Inventory.Count);
            Assert.AreEqual(2, to.Inventory.Count);
            Assert.AreEqual(1, run.OverflowCount);
        }
    }
}

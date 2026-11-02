using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Knots;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Owner rulings 2026-10-30: the Durable Legendary Key joins the fixed boss bonus, and the player's first cache opens
    /// with the Knot family, then note, key, salvage, then everything else. Behavioural: driven through the buildBonus
    /// parameters and FillFromPile, no world database and no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadFixedBonusOrderTests
    {
        private static Stackable Note(int size, int unitValue = 5, int max = 1000)
            => new Stackable(new Weenie
            {
                WeenieClassId = 20630,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, size }, { PropertyInt.MaxStackSize, max },
                    { PropertyInt.StackUnitEncumbrance, 10 }, { PropertyInt.StackUnitValue, unitValue },
                },
            }, new ObjectGuid(NextGuid()));

        private static WorldObject Valued(uint wcid, int value)
        {
            var item = Item(wcid);
            item.Value = value;
            return item;
        }

        private static WorldObject Knotty(int kind)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 990001,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.KnotItemKind, kind } },
            }, new ObjectGuid(NextGuid()));

        private static List<HeldPileItem> Held(ThreadDungeonRun run, uint guid)
        {
            var taken = run.TakeHeldPile(guid);
            foreach (var held in taken)
                Assert.IsTrue(run.AddToHeldPile(guid, held));
            return taken;
        }

        [TestMethod]
        public void Solo_pile_tags_the_fixed_bonus_by_index_and_the_first_cache_opens_fiber_note_key_bag_then_loot()
        {
            var run = PooledRun();
            Assert.IsTrue(run.TryMarkLootBonusPending());
            Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, null, null, 1, 0)));

            var note = Note(2);
            var key = Item(51954);
            key.Value = 1;
            var bag = Item(21013);
            bag.Value = 2;
            var rich = Valued(30001, 9_000_000);
            var fiber = Knotty(KnotItemKind.Fiber);

            ThreadCacheFiller.BuildPileInto(run, _ => new List<WorldObject> { rich, fiber },
                _ => new List<WorldObject> { note, key, bag }, ThreadLootRollBudget.Unlimited());

            var pile = Held(run, run.OwnerGuid);
            Assert.AreEqual(0, pile.Single(h => ReferenceEquals(h.Item, note)).FixedOrder);
            Assert.AreEqual(1, pile.Single(h => ReferenceEquals(h.Item, key)).FixedOrder);
            Assert.AreEqual(2, pile.Single(h => ReferenceEquals(h.Item, bag)).FixedOrder);
            Assert.IsNull(pile.Single(h => ReferenceEquals(h.Item, rich)).FixedOrder);
            Assert.IsNull(pile.Single(h => ReferenceEquals(h.Item, fiber)).FixedOrder);

            var cache = Cache();
            ThreadGroupCacheDelivery.FillFromPile(run, cache, run.OwnerGuid);

            var order = cache.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).ToList();
            CollectionAssert.AreEqual(new WorldObject[] { fiber, note, key, bag, rich }, order, "Fiber, note, key, bag, then loot");
        }

        [TestMethod]
        public void Knot_family_beats_a_fixed_tag_and_IsKnotDrop_is_unchanged()
        {
            Assert.IsTrue(KnotItems.IsKnotFamily(Knotty(KnotItemKind.Fiber)));
            Assert.IsTrue(KnotItems.IsKnotFamily(Knotty(KnotItemKind.Pattern)));
            Assert.IsTrue(KnotItems.IsKnotFamily(Knotty(KnotItemKind.Knot)));
            Assert.IsFalse(KnotItems.IsKnotFamily(Item()));
            Assert.IsFalse(KnotItems.IsKnotFamily(null));

            var knot = Knotty(KnotItemKind.Knot);
            Assert.IsFalse(KnotItems.IsKnotDrop(knot), "a Knot still does not make a Knotted cache");

            var fixedNote = Valued(1, 1);
            var pile = new List<HeldPileItem>
            {
                new HeldPileItem(fixedNote, null, 1, null, 0),
                new HeldPileItem(knot, null, 1, null, 1),
            };

            CollectionAssert.AreEqual(new WorldObject[] { knot, fixedNote }, ThreadLootStacking.SortPileForPlacement(pile).Select(h => h.Item).ToList());
        }

        [TestMethod]
        public void ConsolidatePile_keeps_the_fixed_rank_when_the_loot_note_is_first_and_the_fixed_note_second()
        {
            var lootNote = Note(5);
            var fixedNote = Note(10);

            var merged = ThreadLootStacking.ConsolidatePile(new List<HeldPileItem>
            {
                new HeldPileItem(lootNote, null, 1),
                new HeldPileItem(fixedNote, null, 2, null, 0),
            }, out var removed);

            Assert.AreEqual(1, removed);
            Assert.AreEqual(1, merged.Count);
            Assert.AreSame(lootNote, merged[0].Item, "the first object survives");
            Assert.AreEqual(0, merged[0].FixedOrder, "and carries the fixed rank of the note merged into it");
        }

        [TestMethod]
        public void ConsolidatePile_with_two_survivors_gives_the_group_min_rank_to_the_first_only()
        {
            var a = Note(60, max: 100);
            var b = Note(60, max: 100);
            var c = Note(60, max: 100);

            var merged = ThreadLootStacking.ConsolidatePile(new List<HeldPileItem>
            {
                new HeldPileItem(a, null, 1),
                new HeldPileItem(b, null, 2, null, 3),
                new HeldPileItem(c, null, 3, null, 0),
            }, out _);

            Assert.AreEqual(2, merged.Count, "3 x 60 into a 100 cap leaves two stacks");
            Assert.IsTrue(c.IsDestroyed);
            Assert.AreEqual(0, merged[0].FixedOrder, "the first survivor carries the group min rank");
            Assert.IsNull(merged[1].FixedOrder, "a later survivor carries none");
        }
    }
}
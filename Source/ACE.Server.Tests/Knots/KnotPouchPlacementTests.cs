using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Knots;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// The Knotting Pouch never receives an automatically placed item (Container.TryAddToInventory's side-pack
    /// spill skips it, and GetFreeInventorySlots does not count its slots), while an ordinary side pack still
    /// receives spill and the Pouch still takes a Fiber added to it directly (the explicit-move path).
    /// Objects are in-memory weenies with static-range guids, as in ContainerStackTests.
    /// </summary>
    [TestClass]
    public class KnotPouchPlacementTests
    {
        private static uint nextGuid = 0x7E200000;

        private static Container Pack(int itemCapacity, int containerCapacity = 0, int? kind = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemsCapacity, itemCapacity },
                    { PropertyInt.ContainersCapacity, containerCapacity },
                },
            };
            if (kind.HasValue)
                weenie.PropertiesInt[PropertyInt.KnotItemKind] = kind.Value;

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        private static Container Pouch() => Pack(KnotItems.FibersPerKnot, 0, KnotItemKind.Pouch);

        private static WorldObject Item(int? kind = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 900100,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Gem } },
            };
            if (kind.HasValue)
                weenie.PropertiesInt[PropertyInt.KnotItemKind] = kind.Value;

            return WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A main pack with one item slot, already full, holding the given side packs.</summary>
        private static Container FullMainPack(params Container[] sidePacks)
        {
            var main = Pack(itemCapacity: 1, containerCapacity: sidePacks.Length);
            foreach (var side in sidePacks)
                Assert.IsTrue(main.TryAddToInventory(side), "side pack added");
            Assert.IsTrue(main.TryAddToInventory(Item()), "filler item fills the main pack");
            Assert.AreEqual(0, main.GetFreeInventorySlots(includeSidePacks: false));
            return main;
        }

        [TestMethod]
        public void Spill_OrdinarySidePack_StillReceivesSpill()
        {
            // Positive control: without it, a refusal that broke all spill would pass the Pouch tests below.
            var pack = Pack(itemCapacity: 5);
            var main = FullMainPack(pack);
            var item = Item();

            Assert.IsTrue(main.TryAddToInventory(item, out var landedIn));
            Assert.AreSame(pack, landedIn);
            Assert.IsTrue(pack.Inventory.ContainsKey(item.Guid));
        }

        [TestMethod]
        public void Spill_PouchIsTheOnlySidePack_FailsAsIfItWereAbsent()
        {
            var pouch = Pouch();
            var main = FullMainPack(pouch);
            var item = Item();

            Assert.IsFalse(main.TryAddToInventory(item, out var landedIn));
            Assert.IsNull(landedIn);
            Assert.AreEqual(0, pouch.Inventory.Count);
            Assert.IsNull(item.ContainerId);
        }

        [TestMethod]
        public void Spill_FiberIsNotAutoPlacedInThePouchEither()
        {
            var pouch = Pouch();
            var main = FullMainPack(pouch);
            var fiber = Item(KnotItemKind.Fiber);

            Assert.IsFalse(main.TryAddToInventory(fiber));
            Assert.AreEqual(0, pouch.Inventory.Count);
        }

        [TestMethod]
        public void Spill_PouchAndOrdinaryPack_ItemGoesToTheOrdinaryPack()
        {
            var pouch = Pouch();
            var pack = Pack(itemCapacity: 5);
            var main = FullMainPack(pouch, pack);

            for (var i = 0; i < 5; i++)
            {
                var item = Item(KnotItemKind.Fiber);
                Assert.IsTrue(main.TryAddToInventory(item, out var landedIn), $"item {i}");
                Assert.AreSame(pack, landedIn, $"item {i} landed in the ordinary pack");
            }

            // The ordinary pack is now full too: the next item fails even though the Pouch has ten free slots.
            Assert.IsFalse(main.TryAddToInventory(Item()));
            Assert.AreEqual(0, pouch.Inventory.Count);
        }

        [TestMethod]
        public void FreeSlots_DoNotCountThePouch_ButDoCountAnOrdinaryPack()
        {
            var pouch = Pouch();
            var pack = Pack(itemCapacity: 4);
            var main = FullMainPack(pouch, pack);

            Assert.AreEqual(4, main.GetFreeInventorySlots());
            Assert.IsFalse(main.CanAddToInventory(new List<WorldObject> { Item(), Item(), Item(), Item(), Item() }), "five items, four real slots");
            Assert.IsTrue(main.CanAddToInventory(new List<WorldObject> { Item(), Item(), Item(), Item() }));
        }

        [TestMethod]
        public void DirectAdd_PouchStillTakesAFiberNamedExplicitly()
        {
            // The explicit-move path (DoHandleActionPutItemInContainer) adds to the named container directly with
            // limitToMainPackOnly: true; the spill refusal must not touch it.
            var pouch = Pouch();
            var fiber = Item(KnotItemKind.Fiber);

            Assert.IsTrue(pouch.TryAddToInventory(fiber, 0, true));
            Assert.AreSame(pouch, fiber.Container);
        }

        [TestMethod]
        public void NestedPouch_InAMainPackWithRoom_ItemGoesToTheMainPack()
        {
            var pouch = Pouch();
            var main = Pack(itemCapacity: 3, containerCapacity: 1);
            Assert.IsTrue(main.TryAddToInventory(pouch));

            var item = Item();
            Assert.IsTrue(main.TryAddToInventory(item, out var landedIn));
            Assert.AreSame(main, landedIn);
            Assert.AreEqual(0, pouch.Inventory.Count);
        }
    }
}

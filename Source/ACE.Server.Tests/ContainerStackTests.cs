using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Inventory bookkeeping and stack-merge conservation (Container). The intent enforced here
    /// is conservation: no add/remove/merge sequence may create or destroy units, encumbrance,
    /// or value - the dupe/item-loss class of live-server bug. Objects are built from in-memory
    /// weenies (never persisted, so Destroy() skips the database), with static-range guids so
    /// Destroy() also stays clear of GuidManager recycling.
    /// </summary>
    [TestClass]
    public class ContainerStackTests
    {
        private const int UnitEncumbrance = 10;
        private const int UnitValue = 5;

        private static uint nextGuid = 0x7F000000;

        private static Container CreateContainer(int itemCapacity = 10, int containerCapacity = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,   // arbitrary chest-like wcid; nothing reads it back from the db
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemsCapacity, itemCapacity },
                    { PropertyInt.ContainersCapacity, containerCapacity },
                },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        private static Stackable CreateStack(uint wcid, int stackSize, int maxStackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, stackSize },
                    { PropertyInt.MaxStackSize, maxStackSize },
                    { PropertyInt.StackUnitEncumbrance, UnitEncumbrance },
                    { PropertyInt.StackUnitValue, UnitValue },
                },
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        // ---- add / remove bookkeeping ----

        [TestMethod]
        public void Add_UpdatesEncumbranceValueAndOwnership()
        {
            var container = CreateContainer();
            var stack = CreateStack(9001, stackSize: 50, maxStackSize: 100);

            Assert.IsTrue(container.TryAddToInventory(stack));

            Assert.AreEqual(50 * UnitEncumbrance, container.EncumbranceVal);
            Assert.AreEqual(50 * UnitValue, container.Value);
            Assert.AreEqual(container.Guid.Full, stack.ContainerId);
            Assert.AreEqual(container.Guid.Full, stack.OwnerId);
        }

        [TestMethod]
        public void Remove_RestoresBookkeepingAndClearsOwnership()
        {
            var container = CreateContainer();
            var keep = CreateStack(9001, stackSize: 30, maxStackSize: 100);
            var remove = CreateStack(9002, stackSize: 20, maxStackSize: 100);

            container.TryAddToInventory(keep);
            container.TryAddToInventory(remove);

            Assert.IsTrue(container.TryRemoveFromInventory(remove.Guid, out var removed));

            Assert.AreSame(remove, removed);
            Assert.AreEqual(30 * UnitEncumbrance, container.EncumbranceVal);
            Assert.AreEqual(30 * UnitValue, container.Value);
            Assert.IsNull(removed.ContainerId);
            Assert.IsNull(removed.OwnerId);
            Assert.IsNull(removed.PlacementPosition);
        }

        [TestMethod]
        public void ItemCapacity_RejectsOverfillWithoutSideEffects()
        {
            var container = CreateContainer(itemCapacity: 2);

            Assert.IsTrue(container.TryAddToInventory(CreateStack(9001, 10, 100)));
            Assert.IsTrue(container.TryAddToInventory(CreateStack(9002, 10, 100)));

            var overflow = CreateStack(9003, 10, 100);
            Assert.IsFalse(container.TryAddToInventory(overflow));

            Assert.AreEqual(2, container.Inventory.Count);
            Assert.AreEqual(20 * UnitEncumbrance, container.EncumbranceVal);
            Assert.IsNull(overflow.ContainerId);
        }

        [TestMethod]
        public void AddRemove_KeepsPlacementPositionsContiguous()
        {
            var container = CreateContainer();
            var a = CreateStack(9001, 1, 100);
            var b = CreateStack(9002, 1, 100);
            var c = CreateStack(9003, 1, 100);

            // each insert at position 0 shifts the others up
            container.TryAddToInventory(a);
            container.TryAddToInventory(b);
            container.TryAddToInventory(c);

            container.TryRemoveFromInventory(b.Guid);

            var positions = container.Inventory.Values.Select(i => i.PlacementPosition ?? -1).OrderBy(p => p).ToList();
            CollectionAssert.AreEqual(new List<int> { 0, 1 }, positions);
        }

        [TestMethod]
        public void NestedRemove_PropagatesBookkeepingToTheParent()
        {
            // removing from a side pack through the parent must decrement both containers'
            // encumbrance/value - the parent-side half of the classic relog-dupe window
            var parent = CreateContainer(itemCapacity: 5, containerCapacity: 1);
            var sidePack = CreateContainer(itemCapacity: 5);
            var stack = CreateStack(9001, stackSize: 10, maxStackSize: 100);

            Assert.IsTrue(sidePack.TryAddToInventory(stack));
            Assert.IsTrue(parent.TryAddToInventory(sidePack));
            Assert.AreEqual(10 * UnitEncumbrance, parent.EncumbranceVal);

            Assert.IsTrue(parent.TryRemoveFromInventory(stack.Guid, out var removed));

            Assert.AreSame(stack, removed);
            Assert.AreEqual(0, sidePack.EncumbranceVal);
            Assert.AreEqual(0, parent.EncumbranceVal);
            Assert.AreEqual(0, sidePack.Value);
            Assert.AreEqual(0, parent.Value);
        }

        // ---- stack merging ----

        [TestMethod]
        public void Merge_ConservesUnitsAndRespectsMaxStackSize()
        {
            var container = CreateContainer();
            container.TryAddToInventory(CreateStack(9001, stackSize: 60, maxStackSize: 100));
            container.TryAddToInventory(CreateStack(9001, stackSize: 70, maxStackSize: 100));

            container.MergeAllStackables();

            var stacks = container.Inventory.Values.ToList();
            Assert.AreEqual(2, stacks.Count);
            Assert.AreEqual(130, stacks.Sum(i => i.StackSize ?? 0));
            Assert.IsTrue(stacks.All(i => i.StackSize <= i.MaxStackSize));
            Assert.AreEqual(130 * UnitEncumbrance, stacks.Sum(i => i.EncumbranceVal ?? 0));
            Assert.AreEqual(130 * UnitEncumbrance, container.EncumbranceVal);
            Assert.AreEqual(130 * UnitValue, container.Value);
        }

        [TestMethod]
        public void Merge_DestroysAFullyDrainedSourceStack()
        {
            var container = CreateContainer();
            var a = CreateStack(9001, stackSize: 30, maxStackSize: 100);
            var b = CreateStack(9001, stackSize: 70, maxStackSize: 100);
            container.TryAddToInventory(a);
            container.TryAddToInventory(b);

            container.MergeAllStackables();

            var survivor = container.Inventory.Values.Single();
            Assert.AreEqual(100, survivor.StackSize);
            Assert.AreEqual(100 * UnitEncumbrance, container.EncumbranceVal);
            Assert.AreEqual(100 * UnitValue, container.Value);

            var drained = survivor == a ? b : a;
            Assert.IsTrue(drained.IsDestroyed);
        }

        [TestMethod]
        public void Merge_LeavesDifferentWeeniesAlone()
        {
            var container = CreateContainer();
            container.TryAddToInventory(CreateStack(9001, stackSize: 10, maxStackSize: 100));
            container.TryAddToInventory(CreateStack(9002, stackSize: 10, maxStackSize: 100));

            container.MergeAllStackables();

            Assert.AreEqual(2, container.Inventory.Count);
            Assert.IsTrue(container.Inventory.Values.All(i => i.StackSize == 10));
        }

        [TestMethod]
        public void CanMergeToInventory_RejectsExceedingMaxStackSize()
        {
            var container = CreateContainer();
            var target = CreateStack(9001, stackSize: 80, maxStackSize: 100);
            var source = CreateStack(9001, stackSize: 30, maxStackSize: 100);
            container.TryAddToInventory(target);

            Assert.IsFalse(container.CanMergeToInventory(source, target, 30));
            Assert.IsTrue(container.CanMergeToInventory(source, target, 20));
        }
    }
}

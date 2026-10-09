using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WorldObject.SetStackSize when the weenie carries no per-unit properties.
    ///
    /// StackUnitValue / StackUnitEncumbrance are not guaranteed to exist on a stackable weenie -
    /// retail ships MaxStackSize with no INT 15 row on 41507/41508/41509 (Item Tinkering Armatures,
    /// Value up to 10,000), 38794/38795 (Black Market elixirs, Value 100,000) and 29159 (Yeast
    /// Liquid). Treating the missing property as 0 wrote the item's own Value away on the first
    /// SetStackSize, which a vendor performs on EVERY purchase (Vendor.ItemProfileToWorldObjects,
    /// even for quantity 1), so those items priced and appraised at 0 and Vendor.GetSellCost's
    /// max(1, ...) floor sold any quantity of them for a single pyreal.
    ///
    /// Objects are built from in-memory weenies, never persisted, with static-range guids.
    /// </summary>
    [TestClass]
    public class SetStackSizeUnitValueTests
    {
        private static uint nextGuid = 0x7E000000;

        private static Stackable CreateStack(int? value, int? stackUnitValue, int? encumbrance, int? stackUnitEncumbrance, int? stackSize)
        {
            var props = new Dictionary<PropertyInt, int> { { PropertyInt.MaxStackSize, 100 } };

            if (stackSize != null) props[PropertyInt.StackSize] = stackSize.Value;
            if (value != null) props[PropertyInt.Value] = value.Value;
            if (stackUnitValue != null) props[PropertyInt.StackUnitValue] = stackUnitValue.Value;
            if (encumbrance != null) props[PropertyInt.EncumbranceVal] = encumbrance.Value;
            if (stackUnitEncumbrance != null) props[PropertyInt.StackUnitEncumbrance] = stackUnitEncumbrance.Value;

            var weenie = new Weenie
            {
                WeenieClassId = 41509,   // nothing reads it back from the db
                WeenieType = WeenieType.Stackable,
                PropertiesInt = props,
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void StackUnitValue_WhenPresent_StillDrivesTheResult()
        {
            var stack = CreateStack(value: 700, stackUnitValue: 7, encumbrance: 500, stackUnitEncumbrance: 5, stackSize: 100);

            stack.SetStackSize(10);

            Assert.AreEqual(10, stack.StackSize);
            Assert.AreEqual(70, stack.Value);
            Assert.AreEqual(50, stack.EncumbranceVal);
        }

        [TestMethod]
        public void MissingStackUnitValue_SingleItem_KeepsItsOwnValue()
        {
            // The retail-vendor case: a Minor Item Tinkering Armature is Value 5000 with no INT 15 row,
            // and the vendor calls SetStackSize(1) on it before pricing it.
            var stack = CreateStack(value: 5000, stackUnitValue: null, encumbrance: 50, stackUnitEncumbrance: null, stackSize: 1);

            stack.SetStackSize(1);

            Assert.AreEqual(5000, stack.Value);
            Assert.AreEqual(50, stack.EncumbranceVal);
        }

        [TestMethod]
        public void MissingStackUnitValue_MultipleUnits_ScaleFromTheWeeniesValue()
        {
            var stack = CreateStack(value: 5000, stackUnitValue: null, encumbrance: 50, stackUnitEncumbrance: null, stackSize: 1);

            stack.SetStackSize(100);

            Assert.AreEqual(100, stack.StackSize);
            Assert.AreEqual(500000, stack.Value);
            Assert.AreEqual(5000, stack.EncumbranceVal);
        }

        [TestMethod]
        public void MissingStackUnitValue_ExistingStack_DividesTheAggregateBackOut()
        {
            var stack = CreateStack(value: 500000, stackUnitValue: null, encumbrance: 5000, stackUnitEncumbrance: null, stackSize: 100);

            stack.SetStackSize(40);

            Assert.AreEqual(200000, stack.Value);
            Assert.AreEqual(2000, stack.EncumbranceVal);
        }

        [TestMethod]
        public void MissingStackUnitValue_RepeatedCalls_AreIdempotent()
        {
            var stack = CreateStack(value: 5000, stackUnitValue: null, encumbrance: 50, stackUnitEncumbrance: null, stackSize: 1);

            stack.SetStackSize(10);
            stack.SetStackSize(20);
            stack.SetStackSize(3);

            Assert.AreEqual(15000, stack.Value);
            Assert.AreEqual(150, stack.EncumbranceVal);
        }

        [TestMethod]
        public void GenuinelyValuelessStack_StaysAtZero()
        {
            // Stale bread and the like really are Value 0. Nothing may invent value for them.
            var stack = CreateStack(value: 0, stackUnitValue: null, encumbrance: 0, stackUnitEncumbrance: null, stackSize: 100);

            stack.SetStackSize(100);

            Assert.AreEqual(0, stack.Value);
            Assert.AreEqual(0, stack.EncumbranceVal);
        }

        [TestMethod]
        public void MissingValueEntirely_IsTreatedAsZero()
        {
            var stack = CreateStack(value: null, stackUnitValue: null, encumbrance: null, stackUnitEncumbrance: null, stackSize: null);

            stack.SetStackSize(5);

            Assert.AreEqual(5, stack.StackSize);
            Assert.AreEqual(0, stack.Value);
            Assert.AreEqual(0, stack.EncumbranceVal);
        }

        [TestMethod]
        public void NonStackable_IsLeftAlone()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 5000 } },
            };

            var container = new Container(weenie, new ObjectGuid(nextGuid++));

            container.SetStackSize(10);

            Assert.IsNull(container.StackSize);
            Assert.AreEqual(5000, container.Value);
        }
    }
}

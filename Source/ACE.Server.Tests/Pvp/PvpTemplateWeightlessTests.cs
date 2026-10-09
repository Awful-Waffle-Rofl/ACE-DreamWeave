using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Issued Ammunition and SpellComponent kit items are weightless (Docs/Pvp/TEMPLATES.md "Issued kit"):
    /// EncumbranceVal and StackUnitEncumbrance are both 0 on the issued biota, a stack split off an issued stack
    /// stays 0 (it is built fresh from the weenie, which carries the real burden), and no other item type changes.
    /// </summary>
    [TestClass]
    public class PvpTemplateWeightlessTests
    {
        private const uint Owner = 0x50000001;

        private static PvpTemplateKitItem Item(WeenieType type, int encumbrance = 300, int unit = 3, int stack = 100)
        {
            var item = new PvpTemplateKitItem { WeenieClassId = 4242, WeenieType = (int)type };
            item.Ints[(int)PropertyInt.EncumbranceVal] = encumbrance;
            item.Ints[(int)PropertyInt.StackUnitEncumbrance] = unit;
            item.Ints[(int)PropertyInt.StackSize] = stack;
            item.Ints[(int)PropertyInt.MaxStackSize] = 250;
            return item;
        }

        [DataTestMethod]
        [DataRow(WeenieType.Ammunition)]
        [DataRow(WeenieType.SpellComponent)]
        public void IssuedAmmoAndComponents_AreIssuedWeightless(WeenieType type)
        {
            var biota = PvpTemplateKit.BuildIssuedBiota(Item(type), 0x80000001, Owner);

            Assert.AreEqual(0, biota.PropertiesInt[PropertyInt.EncumbranceVal]);
            Assert.AreEqual(0, biota.PropertiesInt[PropertyInt.StackUnitEncumbrance]);
            Assert.AreEqual(100, biota.PropertiesInt[PropertyInt.StackSize], "the stack size itself is untouched");
        }

        [DataTestMethod]
        [DataRow(WeenieType.Generic)]
        [DataRow(WeenieType.MeleeWeapon)]
        [DataRow(WeenieType.MissileLauncher)]
        [DataRow(WeenieType.Caster)]
        [DataRow(WeenieType.Clothing)]
        [DataRow(WeenieType.Food)]
        [DataRow(WeenieType.Stackable)]
        public void OtherIssuedItems_KeepTheirCapturedBurden(WeenieType type)
        {
            var biota = PvpTemplateKit.BuildIssuedBiota(Item(type), 0x80000001, Owner);

            Assert.AreEqual(300, biota.PropertiesInt[PropertyInt.EncumbranceVal]);
            Assert.AreEqual(3, biota.PropertiesInt[PropertyInt.StackUnitEncumbrance]);
        }

        [TestMethod]
        public void IsWeightlessIssuedType_IsExactlyAmmunitionAndSpellComponent()
        {
            foreach (WeenieType type in Enum.GetValues(typeof(WeenieType)))
            {
                var expected = type == WeenieType.Ammunition || type == WeenieType.SpellComponent;
                Assert.AreEqual(expected, PvpTemplateKit.IsWeightlessIssuedType(type), type.ToString());
            }
        }

        private static Stackable Seed(WeenieType type, bool issued, int size, int unit)
        {
            var wo = (Stackable)RuntimeHelpers.GetUninitializedObject(typeof(Stackable));

            var biota = new Biota
            {
                WeenieClassId = 4242,
                WeenieType = type,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, size },
                    { PropertyInt.MaxStackSize, 250 },
                    { PropertyInt.StackUnitEncumbrance, unit },
                    { PropertyInt.EncumbranceVal, unit * size },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Stack" } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
            };

            if (issued)
                biota.PropertiesBool[PropertyBool.PvpTemplateIssued] = true;

            PvpTemplatePlayerTests.SeedWorldObject(wo, biota, 0x80400000u + (uint)Interlocked.Increment(ref next));

            return wo;
        }

        private static int next;

        [DataTestMethod]
        [DataRow(WeenieType.Ammunition)]
        [DataRow(WeenieType.SpellComponent)]
        public void SplitOffAnIssuedStack_IsWeightless_EvenThoughTheWeenieCarriesBurden(WeenieType type)
        {
            // The issued source (0 burden) and a fresh-from-the-weenie child (unit burden 3), exactly as the split paths build them.
            var issued = Seed(type, true, 100, 0);
            var child = Seed(type, false, 1, 3);
            child.SetStackSize(40);
            Assert.AreEqual(120, child.EncumbranceVal, "precondition: the fresh child is priced at the weenie's burden, which is the bug this guards");

            Player.StampPvpTemplateSplitStack(issued, child);

            Assert.AreEqual(0, child.StackUnitEncumbrance);
            Assert.AreEqual(0, child.EncumbranceVal);

            // later size changes of the child (merge back, consumption) derive from the 0 unit, so it stays weightless
            child.SetStackSize(15);
            Assert.AreEqual(0, child.EncumbranceVal, "SetStackSize recomputes from StackUnitEncumbrance, which is 0");
        }

        [TestMethod]
        public void SplitOffAnIssuedStack_OfAnotherType_OrOfAPersonalStack_KeepsTheWeenieBurden()
        {
            var issuedGeneric = Seed(WeenieType.Stackable, true, 100, 3);
            var childOfGeneric = Seed(WeenieType.Stackable, false, 1, 3);
            childOfGeneric.SetStackSize(10);

            Player.StampPvpTemplateSplitStack(issuedGeneric, childOfGeneric);

            Assert.AreEqual(30, childOfGeneric.EncumbranceVal, "an issued non-ammo stack keeps its burden");

            var personalAmmo = Seed(WeenieType.Ammunition, false, 100, 3);
            var childOfPersonal = Seed(WeenieType.Ammunition, false, 1, 3);
            childOfPersonal.SetStackSize(10);

            Player.StampPvpTemplateSplitStack(personalAmmo, childOfPersonal);

            Assert.AreEqual(30, childOfPersonal.EncumbranceVal, "a personal ammo stack's child is never made weightless");
        }

        [TestMethod]
        public void IssuedStack_StaysZeroThroughASizeChange()
        {
            // AdjustStack (private) writes EncumbranceVal = StackUnitEncumbrance * StackSize, and charges the container
            // StackUnitEncumbrance * amount; with a unit of 0 both are 0. SetStackSize is the public twin of the first.
            var issued = Seed(WeenieType.Ammunition, true, 100, 0);

            issued.SetStackSize(60);
            Assert.AreEqual(0, issued.EncumbranceVal);
        }
    }
}
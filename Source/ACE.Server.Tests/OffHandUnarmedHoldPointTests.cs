using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Off-hand unarmed weapons (claws, katars) hold at ParentLocation.LeftUnarmed (9), the left hand's
    /// 180 degree flip of LeftWeapon (8), so they stop rendering backwards. Because the off-hand then holds items
    /// at three different parent locations (shield 3, sword 8, unarmed 9), slot occupancy must be decided by
    /// CurrentWieldedLocation rather than ParentLocation or a claw and a shield could be worn together.
    /// </summary>
    [TestClass]
    public class OffHandUnarmedHoldPointTests
    {
        private static uint nextGuid = 0x7D100000;
        private static uint nextWcid = 992000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();   // Creature construction reads the vital formula tables
        }

        private static WorldObject MakeWeapon(WeaponType type, string name = "Test Weapon", string longDesc = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.WeaponType, (int)type },
                },
            };
            if (longDesc != null)
                weenie.PropertiesString[PropertyString.LongDesc] = longDesc;

            return new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject MakeShield()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Shield" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                },
            };
            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        private static Creature MakeCreature()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Wielder" } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemsCapacity, 102 }, { PropertyInt.ContainersCapacity, 7 } },
            };
            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static void Place(WorldObject item, EquipMask slot, out Placement placement, out ParentLocation parentLocation)
            => Creature.GetPlacementLocation(item, slot, out placement, out parentLocation);

        // ---------------- hold point ----------------

        [TestMethod]
        public void OffHandUnarmed_UsesLeftUnarmed_AndKeepsRightHandNonCombatPlacement()
        {
            Place(MakeWeapon(WeaponType.Unarmed), EquipMask.Shield, out var placement, out var parent);

            Assert.AreEqual(ParentLocation.LeftUnarmed, parent);
            Assert.AreEqual(Placement.RightHandNonCombat, placement);
        }

        [TestMethod]
        public void OffHandSword_StillUsesLeftWeapon()
        {
            Place(MakeWeapon(WeaponType.Sword), EquipMask.Shield, out var placement, out var parent);

            Assert.AreEqual(ParentLocation.LeftWeapon, parent);
            Assert.AreEqual(Placement.RightHandNonCombat, placement);
        }

        [TestMethod]
        public void Shield_StillUsesShieldLocation()
        {
            Place(MakeShield(), EquipMask.Shield, out var placement, out var parent);

            Assert.AreEqual(ParentLocation.Shield, parent);
            Assert.AreEqual(Placement.Shield, placement);
        }

        [TestMethod]
        public void MainHandUnarmed_Unchanged()
        {
            Place(MakeWeapon(WeaponType.Unarmed), EquipMask.MeleeWeapon, out var placement, out var parent);

            Assert.AreEqual(ParentLocation.RightHand, parent);
            Assert.AreEqual(Placement.RightHandCombat, placement);
        }

        // ---------------- slot occupancy ----------------

        [TestMethod]
        public void ClawInOffHand_BlocksShieldAndOffHandSword()
        {
            var wielder = MakeCreature();
            var claw = MakeWeapon(WeaponType.Unarmed);
            Assert.IsTrue(wielder.TryEquipObject(claw, EquipMask.Shield));
            Assert.AreEqual(ParentLocation.LeftUnarmed, claw.ParentLocation);

            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeShield(), EquipMask.Shield));
            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Sword), EquipMask.Shield));
            Assert.IsFalse(wielder.TryEquipObject(MakeShield(), EquipMask.Shield));
        }

        [TestMethod]
        public void ShieldInOffHand_BlocksClaw()
        {
            var wielder = MakeCreature();
            Assert.IsTrue(wielder.TryEquipObject(MakeShield(), EquipMask.Shield));

            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Unarmed), EquipMask.Shield));
        }

        [TestMethod]
        public void OffHandSword_BlocksClaw_AndAnotherSword()
        {
            var wielder = MakeCreature();
            Assert.IsTrue(wielder.TryEquipObject(MakeWeapon(WeaponType.Sword), EquipMask.Shield));

            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Unarmed), EquipMask.Shield));
            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Sword), EquipMask.Shield));
        }

        [TestMethod]
        public void OffHandClaw_DoesNotBlockMainHand_AndMainHandDoesNotBlockOffHand()
        {
            var wielder = MakeCreature();
            Assert.IsTrue(wielder.TryEquipObject(MakeWeapon(WeaponType.Unarmed), EquipMask.Shield));

            Assert.IsTrue(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Sword), EquipMask.MeleeWeapon));

            var other = MakeCreature();
            Assert.IsTrue(other.TryEquipObject(MakeWeapon(WeaponType.Sword), EquipMask.MeleeWeapon));
            Assert.IsTrue(other.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Unarmed), EquipMask.Shield));
        }

        [TestMethod]
        public void MainHandStillBlocksMainHand()
        {
            var wielder = MakeCreature();
            Assert.IsTrue(wielder.TryEquipObject(MakeWeapon(WeaponType.Sword), EquipMask.MeleeWeapon));

            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Unarmed), EquipMask.MeleeWeapon));
        }

        // ---------------- monster path ----------------

        /// <summary>
        /// Monster_Inventory.SelectWieldedTreasure appends a shield after a dual-wield pair, and
        /// EquipInventoryItems puts back whatever TryWieldObject refuses. With slot occupancy keyed on the
        /// wielded location, a monster whose off hand already holds the dual-wield weapon refuses the shield
        /// (on master both stacked in the Shield slot).
        /// </summary>
        [TestMethod]
        public void Monster_DualWieldOffHandWeaponWielded_ThenShieldIsRefusedAndStaysInInventory()
        {
            var monster = MakeCreature();
            var offHand = MakeWeapon(WeaponType.Sword);
            var shield = MakeShield();

            Assert.IsTrue(monster.TryAddToInventory(offHand));
            Assert.IsTrue(monster.TryAddToInventory(shield));

            // the same sequence EquipInventoryItems runs for each selected item: pull from inventory, wield, put back on failure
            Assert.IsTrue(monster.TryRemoveFromInventory(offHand.Guid));
            Assert.IsTrue(monster.TryEquipObject(offHand, EquipMask.Shield));

            Assert.IsTrue(monster.TryRemoveFromInventory(shield.Guid));
            Assert.IsFalse(monster.TryEquipObject(shield, EquipMask.Shield));
            Assert.IsTrue(monster.TryAddToInventory(shield));

            Assert.IsTrue(monster.EquippedObjects.ContainsKey(offHand.Guid), "the off-hand weapon stays wielded");
            Assert.AreEqual(EquipMask.Shield, offHand.CurrentWieldedLocation);
            Assert.IsFalse(monster.EquippedObjects.ContainsKey(shield.Guid));
            Assert.IsTrue(monster.Inventory.ContainsKey(shield.Guid), "the refused shield returns to the pack");
        }

        // ---------------- nekode exception ----------------

        [TestMethod]
        public void OffHandNekode_UsesLeftWeapon_NotLeftUnarmed()
        {
            Place(MakeWeapon(WeaponType.Unarmed, "Banished Nekode"), EquipMask.Shield, out var placement, out var parent);

            Assert.AreEqual(ParentLocation.LeftWeapon, parent);
            Assert.AreEqual(Placement.RightHandNonCombat, placement);
        }

        [TestMethod]
        public void OffHandNekode_NameMatchIsCaseInsensitive_AndCoversPrefixedNames()
        {
            Place(MakeWeapon(WeaponType.Unarmed, "Spectral Lightning NEKODE"), EquipMask.Shield, out _, out var parent);

            Assert.AreEqual(ParentLocation.LeftWeapon, parent);
        }

        [TestMethod]
        public void OffHandSteelButterfly_NekodeOnlyInLongDesc_UsesLeftWeapon()
        {
            Place(MakeWeapon(WeaponType.Unarmed, "Steel Butterfly", "Elegant. Beautiful. Deadly. these nekodes would serve as beautiful decorations."),
                EquipMask.Shield, out _, out var parent);

            Assert.AreEqual(ParentLocation.LeftWeapon, parent);
        }

        [TestMethod]
        public void OffHandClawAndKatarAndCestus_StillUseLeftUnarmed()
        {
            foreach (var name in new[] { "Claw", "Acid Katar", "Cestus", "Blighted Claw", "Fist of the Quiddity" })
            {
                Place(MakeWeapon(WeaponType.Unarmed, name), EquipMask.Shield, out _, out var parent);
                Assert.AreEqual(ParentLocation.LeftUnarmed, parent, name);
            }
        }

        [TestMethod]
        public void MainHandNekode_Unchanged()
        {
            Place(MakeWeapon(WeaponType.Unarmed, "Nekode"), EquipMask.MeleeWeapon, out var placement, out var parent);

            Assert.AreEqual(ParentLocation.RightHand, parent);
            Assert.AreEqual(Placement.RightHandCombat, placement);
        }

        [TestMethod]
        public void OffHandNekode_BlocksShieldAndClaw_AndRelogRecomputesToLeftWeapon()
        {
            var wielder = MakeCreature();
            var nekode = MakeWeapon(WeaponType.Unarmed, "Nekode");
            Assert.IsTrue(wielder.TryEquipObject(nekode, EquipMask.Shield));
            Assert.AreEqual(ParentLocation.LeftWeapon, nekode.ParentLocation);

            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeShield(), EquipMask.Shield));
            Assert.IsFalse(wielder.WieldedLocationIsAvailable(MakeWeapon(WeaponType.Unarmed, "Claw"), EquipMask.Shield));

            // a nekode persisted at 9 by the claw fix (#1748) goes back to 8 on load
            nekode.ParentLocation = ParentLocation.LeftUnarmed;
            wielder.ResetChild(nekode);

            Assert.AreEqual(ParentLocation.LeftWeapon, nekode.ParentLocation);
            Assert.AreEqual(Placement.RightHandNonCombat, nekode.Placement);
        }

        // ---------------- persisted pre-fix values ----------------

        [TestMethod]
        public void ResetChild_RecomputesPersistedLeftWeaponToLeftUnarmed()
        {
            var wielder = MakeCreature();
            var claw = MakeWeapon(WeaponType.Unarmed);

            // what a pre-fix biota loads as: wielded in the off hand, parent location saved as 8
            claw.CurrentWieldedLocation = EquipMask.Shield;
            claw.ParentLocation = ParentLocation.LeftWeapon;
            wielder.EquippedObjects[claw.Guid] = claw;

            wielder.ResetChild(claw);

            Assert.AreEqual(ParentLocation.LeftUnarmed, claw.ParentLocation);
            Assert.AreEqual(Placement.RightHandNonCombat, claw.Placement);
        }    }
}
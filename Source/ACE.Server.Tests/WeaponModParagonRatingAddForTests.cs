using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WeaponModParagon.RatingAddFor, the call the three rating getters in Creature_Rating.cs make, driven on
    /// uninitialized creatures (RuntimeHelpers.GetUninitializedObject, as in JewelryFortitudeTests) whose
    /// EquippedObjects dictionary is supplied through an UnsafeAccessor. Items are built through
    /// WorldObjectFactory with their REAL weenie type, because the read pre-filters on it. Covers the carrier
    /// restriction (only wielded weapons count, ammo and armor are inert), every weapon slot shape, the Hunter's
    /// Echo scope, per-wielder suppression and the live gate. Both tunables it reads are seeded per test and
    /// restored in a finally.
    /// </summary>
    [TestClass]
    public class WeaponModParagonRatingAddForTests
    {
        private const double Tol = 1e-9;

        private static uint nextGuid = 0x7D7A0000;   // static range, clear of GuidManager and the other kits
        private static uint nextWcid = 994700;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<EquippedObjects>k__BackingField")]
        private static extern ref Dictionary<ObjectGuid, WorldObject> EquippedField(Creature creature);

        private static T Bare<T>(out Dictionary<ObjectGuid, WorldObject> equipped) where T : Creature
        {
            var creature = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            equipped = new Dictionary<ObjectGuid, WorldObject>();
            EquippedField(creature) = equipped;
            return creature;
        }

        /// <summary>An item of a real weenie type, carrying <paramref name="devastationGems"/> Devastation gems.</summary>
        private static WorldObject Item(WeenieType weenieType, ItemType itemType, int devastationGems, CombatUse? combatUse = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = weenieType,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Paragon Test Item" } },
            };

            if (combatUse != null)
                weenie.PropertiesInt[PropertyInt.CombatUse] = (int)combatUse.Value;

            var item = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            Assert.IsNotNull(item, $"fixture: no WorldObject for {weenieType}");
            Assert.AreEqual(weenieType, item.WeenieType, "fixture: weenie type");

            for (var i = 0; i < devastationGems; i++)
                item.SetProperty(ParagonModRegistry.Slots[i], (int)WeaponModId.Devastation);

            return item;
        }

        private static WorldObject Wield(Dictionary<ObjectGuid, WorldObject> equipped, WorldObject item, EquipMask location, ParentLocation? parent)
        {
            item.SetProperty(PropertyInt.CurrentWieldedLocation, (int)location);

            if (parent != null)
                item.SetProperty(PropertyInt.ParentLocation, (int)parent.Value);

            equipped[item.Guid] = item;
            return item;
        }

        private static WorldObject Sword(Dictionary<ObjectGuid, WorldObject> equipped, int gems) =>
            Wield(equipped, Item(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, gems, CombatUse.Melee), EquipMask.MeleeWeapon, ParentLocation.RightHand);

        private static WorldObject ChestWithStraySlot(Dictionary<ObjectGuid, WorldObject> equipped) =>
            Wield(equipped, Item(WeenieType.Clothing, ItemType.Armor, 2), EquipMask.ChestArmor, null);

        private static void WithLive(bool enabled, bool playerSuppressed, Action body)
        {
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;
            var priorSource = WeaponModSuppression.PlayerSuppressedSource;

            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", enabled));
                Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0));
                WeaponModSuppression.PlayerSuppressedSource = _ => playerSuppressed;

                body();
            }
            finally
            {
                WeaponModSuppression.PlayerSuppressedSource = priorSource;
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
            }
        }

        private static int Add(Creature creature) => WeaponModParagon.RatingAddFor(creature, PropertyInt.GearCritDamage);

        // ================= carriers =================

        [TestMethod]
        public void NonPlayerCreature_GemmedWeapon_AddApplied()
        {
            var creature = Bare<Creature>(out var equipped);
            Sword(equipped, 3);

            WithLive(true, false, () =>
            {
                Assert.AreEqual(4, Add(creature), "Devastation, 3 gems");
                Assert.AreEqual(0, WeaponModParagon.RatingAddFor(creature, PropertyInt.GearCrit), "no Weak Point gems");
            });
        }

        [TestMethod]
        public void ArmorPlusWeapon_SameAsWeaponAlone()
        {
            var weaponOnly = Bare<Creature>(out var equippedA);
            Sword(equippedA, 3);

            var armored = Bare<Creature>(out var equippedB);
            Sword(equippedB, 3);
            ChestWithStraySlot(equippedB);

            WithLive(true, false, () => Assert.AreEqual(Add(weaponOnly), Add(armored), "armor must not change the add"));
        }

        [TestMethod]
        public void ArmorCarryingSlot1_AddsNothing()
        {
            var creature = Bare<Creature>(out var equipped);
            var chest = ChestWithStraySlot(equipped);

            Assert.IsFalse(WeaponModParagon.HasParagonMods(chest), "a non-weapon is never a Paragon carrier");
            Assert.AreEqual(0.0, WeaponModParagon.ParagonFraction(chest, WeaponModId.Devastation), Tol);
            Assert.AreEqual(0, WeaponModParagon.TierARatingAdd(new[] { chest }, PropertyInt.GearCritDamage, true, 1.0), "the pure sum ignores it too");

            WithLive(true, false, () => Assert.AreEqual(0, Add(creature)));
        }

        /// <summary>Ammo carries ItemType MissileWeapon, so only the weenie-type pre-filter keeps a stray slot inert.</summary>
        [TestMethod]
        public void AmmoCarryingSlot1_AddsNothing()
        {
            var creature = Bare<Creature>(out var equipped);
            var arrows = Wield(equipped, Item(WeenieType.Ammunition, ItemType.MissileWeapon, 3), EquipMask.MissileAmmo, null);

            Assert.IsTrue(WeaponModParagon.HasParagonMods(arrows), "control: the item-type test alone would accept it");
            WithLive(true, false, () => Assert.AreEqual(0, Add(creature)));
        }

        [TestMethod]
        public void MissileLauncher_IsACarrier()
        {
            var creature = Bare<Creature>(out var equipped);
            Wield(equipped, Item(WeenieType.MissileLauncher, ItemType.MissileWeapon, 3, CombatUse.Missile), EquipMask.MissileWeapon, ParentLocation.LeftHand);

            WithLive(true, false, () => Assert.AreEqual(4, Add(creature)));
        }

        [TestMethod]
        public void TwoHander_IsACarrier()
        {
            var creature = Bare<Creature>(out var equipped);
            Wield(equipped, Item(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, 2, CombatUse.TwoHanded), EquipMask.TwoHanded, ParentLocation.RightHand);

            WithLive(true, false, () => Assert.AreEqual(2, Add(creature)));
        }

        [TestMethod]
        public void ThrownWeapon_IsACarrier()
        {
            var creature = Bare<Creature>(out var equipped);
            Wield(equipped, Item(WeenieType.Missile, ItemType.MissileWeapon, 1, CombatUse.Missile), EquipMask.MissileWeapon, ParentLocation.RightHand);

            WithLive(true, false, () => Assert.AreEqual(1, Add(creature)));
        }

        [TestMethod]
        public void DualWieldOffHand_AndWand_AreCarriers()
        {
            var dual = Bare<Creature>(out var equipped);
            Sword(equipped, 1);
            Wield(equipped, Item(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, 1, CombatUse.Melee), EquipMask.Shield, ParentLocation.LeftWeapon);

            var caster = Bare<Creature>(out var casterEquipped);
            Wield(casterEquipped, Item(WeenieType.Caster, ItemType.Caster, 1), EquipMask.Held, ParentLocation.RightHand);

            WithLive(true, false, () =>
            {
                Assert.AreEqual(2, Add(dual), "main hand 1 + off hand 1");
                Assert.AreEqual(1, Add(caster), "a wand is a carrier");
            });
        }

        [TestMethod]
        public void AtMostTwoCarriers_Count()
        {
            var creature = Bare<Creature>(out var equipped);
            Sword(equipped, 1);
            Sword(equipped, 1);
            Sword(equipped, 1);

            WithLive(true, false, () => Assert.AreEqual(WeaponModParagon.MaxRatingCarriers, Add(creature), "a malformed third weapon is not counted"));
        }

        // ================= Hunter's Echo =================

        [TestMethod]
        public void HuntersEchoScope_SnapshotWeaponCounts_LiveMainHandIgnored()
        {
            var creature = Bare<Creature>(out var equipped);
            Sword(equipped, 3);
            var snapshotBow = Item(WeenieType.MissileLauncher, ItemType.MissileWeapon, 1, CombatUse.Missile);

            WithLive(true, false, () =>
            {
                Assert.AreEqual(4, Add(creature), "control: outside the scope the live sword counts");

                var previous = creature.EnterHuntersEchoWeaponScope(new HuntersEchoWeapons(snapshotBow, snapshotBow, null));

                try
                {
                    Assert.AreEqual(1, Add(creature), "inside the scope only the snapshot bow counts");
                }
                finally
                {
                    Creature.RestoreHuntersEchoWeaponScope(previous);
                }

                Assert.AreEqual(4, Add(creature), "the scope was restored");
            });
        }

        // ================= suppression and the gate =================

        [TestMethod]
        public void SuppressedPlayer_ReadsZero()
        {
            var player = Bare<Player>(out var equipped);
            Sword(equipped, 3);

            WithLive(true, true, () => Assert.AreEqual(0, Add(player), "suppressed"));
            WithLive(true, false, () => Assert.AreEqual(4, Add(player), "control: the same player unsuppressed"));
        }

        [TestMethod]
        public void GateOff_ReadsZero()
        {
            var creature = Bare<Creature>(out var equipped);
            Sword(equipped, 3);

            WithLive(false, false, () => Assert.AreEqual(0, Add(creature)));
        }

        [TestMethod]
        public void NullCreature_ReadsZero() => Assert.AreEqual(0, Add(null));
    }
}

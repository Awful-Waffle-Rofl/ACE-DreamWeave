using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CLOAK MANTLE rear guard: a Mantle cloak gives a PvE rear-arc hit a shield-like reduction of
    /// (cloak item level * cloak_rear_guard_armor_per_level) armor level, through Creature.GetShieldMod.
    /// Drives the REAL GetShieldMod on an uninitialized Player (the DefensiveWeaponTests harness shape).
    /// Every PropertyManager key read here is seeded per test and restored after, so the class runs alone.
    /// </summary>
    [TestClass]
    public class CloakRearGuardTests
    {
        private static readonly string[] SeededBools = { "cloak_rear_guard_enabled", "shield_applies_in_noncombat", "weapon_mods_enabled", "equipment_mods_enabled", "class_abilities_enabled" };
        private static readonly string[] SeededDoubles = { "cloak_rear_guard_armor_per_level", "weapon_mod_magnitude_scale" };

        private static uint nextGuid = 0x7EA00000;
        private static uint nextWcid = 997600;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void SeedTunables()
        {
            RestoreTunables();

            PropertyManager.ModifyBool("cloak_rear_guard_enabled", true);
            PropertyManager.ModifyBool("shield_applies_in_noncombat", true);
            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", 20.0);
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            foreach (var key in SeededBools)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            foreach (var key in SeededDoubles)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        private const long XpPerItemLevel = 100;

        /// <summary>An equippable cloak of the given item level; a Mantle when <paramref name="mantle"/>.</summary>
        private static WorldObject MakeCloak(int itemLevel, bool mantle)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)EquipMask.Cloak },
                    { PropertyInt.ItemType, (int)ItemType.Clothing },
                },
            };

            var wo = new Clothing(weenie, new ObjectGuid(nextGuid++));

            wo.SetProperty(PropertyInt64.ItemBaseXp, XpPerItemLevel);
            wo.SetProperty(PropertyInt.ItemMaxLevel, 10);
            wo.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.Fixed);
            wo.SetProperty(PropertyInt64.ItemTotalXp, itemLevel * XpPerItemLevel);

            if (mantle)
                wo.SetProperty(PropertyBool.CloakRearGuard, true);

            Assert.AreEqual(itemLevel, wo.ItemLevel ?? 0, "control: the fixture must really carry that item level");

            return wo;
        }

        private static WorldObject MakeShield()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.ArmorLevel, 100 },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ArmorModVsSlash, 1.0 } },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A seeded Player at (55, 45) facing +Y: an attacker at (55, 60) is frontal, one at (55, 30) is behind.</summary>
        private static Player DefenderPlayer(CombatMode mode, params WorldObject[] equipped)
        {
            var player = ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SeededPlayer();
            ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SetField(typeof(WorldObject), player, "ephemeralPositions", new Dictionary<PositionType, Position>());
            ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SetField(typeof(WorldObject), player, "positionCache", new Dictionary<PositionType, Position>());
            player.Location = new Position(0x00010064, 55.0f, 45.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            // skills come from a fully-constructed donor creature (an uninitialized Player has no skill table)
            var donor = TestCreatures.CreateDefender(shieldSkill: 0);
            donor.GetCreatureSkill(Skill.Shield);
            typeof(Creature).GetField(nameof(Creature.Skills)).SetValue(player, donor.Skills);

            foreach (var item in equipped)
                player.EquippedObjects[item.Guid] = item;

            typeof(Creature).GetProperty(nameof(Creature.CombatMode)).GetSetMethod(true).Invoke(player, new object[] { mode });

            return player;
        }

        private static Creature At(Creature creature, float x, float y)
        {
            creature.Location = new Position(0x00010064, x, y, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            return creature;
        }

        private static Creature Behind() => At(TestCreatures.CreateAttacker(), 55.0f, 30.0f);
        private static Creature Front() => At(TestCreatures.CreateAttacker(), 55.0f, 60.0f);

        [TestMethod]
        public void NoShieldNoWard_RearHit_UsesRearGuard()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            Assert.AreEqual(0.25f, defender.GetShieldMod(Behind(), DamageType.Slash, null), 1e-4f, "item level 10 * 20 = AL 200 -> 0.25");
        }

        [TestMethod]
        public void Shield_RearHit_UsesRearGuardNotShield()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeShield(), MakeCloak(10, mantle: true));

            Assert.AreEqual(0.25f, defender.GetShieldMod(Behind(), DamageType.Slash, null), 1e-4f, "the rear arc reads the Mantle even with a shield equipped");
        }

        [TestMethod]
        public void FrontHit_Unchanged()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            Assert.AreEqual(1.0f, defender.GetShieldMod(Front(), DamageType.Slash, null), 0.0f, "a frontal blow with no shield is untouched by the Mantle");
        }

        [TestMethod]
        public void RearHit_NoMantle_IsOne()
        {
            var plainCloak = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: false));
            Assert.AreEqual(1.0f, plainCloak.GetShieldMod(Behind(), DamageType.Slash, null), 0.0f, "a cloak without the Mantle flag gives nothing");

            var noCloak = DefenderPlayer(CombatMode.Melee);
            Assert.AreEqual(1.0f, noCloak.GetShieldMod(Behind(), DamageType.Slash, null), 0.0f, "no cloak at all gives nothing");

            var shielded = DefenderPlayer(CombatMode.Melee, MakeShield());
            Assert.AreEqual(1.0f, shielded.GetShieldMod(Behind(), DamageType.Slash, null), 0.0f, "a shield alone still gives nothing from behind");
        }

        [TestMethod]
        public void RearHit_NonCombat_NoNonCombatShield_IsOne()
        {
            PropertyManager.ModifyBool("shield_applies_in_noncombat", false);

            var defender = DefenderPlayer(CombatMode.NonCombat, MakeCloak(10, mantle: true));
            Assert.AreEqual(1.0f, defender.GetShieldMod(Behind(), DamageType.Slash, null), 0.0f, "peace mode with the noncombat shield setting off");

            // POSITIVE CONTROL: lifting only that gate makes the same call bite
            PropertyManager.ModifyBool("shield_applies_in_noncombat", true);
            Assert.AreEqual(0.25f, defender.GetShieldMod(Behind(), DamageType.Slash, null), 1e-4f, "control: same defender, gate lifted");
        }

        [TestMethod]
        public void RearHit_PvpAttacker_IsOne()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            var attackerPlayer = ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SeededPlayer();
            ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SetField(typeof(WorldObject), attackerPlayer, "ephemeralPositions", new Dictionary<PositionType, Position>());
            ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SetField(typeof(WorldObject), attackerPlayer, "positionCache", new Dictionary<PositionType, Position>());
            attackerPlayer.Location = new Position(0x00010064, 55.0f, 30.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            Assert.IsTrue(Math.Abs(defender.GetAngle(attackerPlayer)) > 90f, "geometry control: the player attacker really is behind the defender");
            Assert.AreEqual(1.0f, defender.GetShieldMod(attackerPlayer, DamageType.Slash, null), 0.0f, "PvE only: a player attacker is inert");
        }

        [TestMethod]
        public void RearHit_PhantomWeapon_IsOne()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            var phantom = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.ImbuedEffect, unchecked((int)ImbuedEffectType.IgnoreAllArmor) } });
            var ordinary = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);

            Assert.IsTrue(phantom.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor), "harness: the weapon is phantom");
            Assert.AreEqual(1.0f, defender.GetShieldMod(Behind(), DamageType.Slash, phantom), 0.0f, "a phantom weapon ignores the rear guard");
            Assert.AreEqual(0.25f, defender.GetShieldMod(Behind(), DamageType.Slash, ordinary), 1e-4f, "control: an ordinary weapon is guarded on the same defender");
        }

        [TestMethod]
        public void RearHit_IgnoreShieldAttacker_ScalesTheGuard()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            var attacker = Behind();
            attacker.SetProperty(PropertyFloat.IgnoreShield, 0.5);

            var ignoreMod = attacker.GetIgnoreShieldMod(null);
            Assert.AreEqual(0.5f, ignoreMod, 1e-6f, "harness: the attacker ignores half the shield");

            var expected = WeaponModCombat.ArcaneShieldMod(200.0, ignoreMod);
            Assert.AreNotEqual(0.25f, expected, 1e-4f, "control: the expected value differs from the unscaled guard");
            Assert.AreEqual(expected, defender.GetShieldMod(attacker, DamageType.Slash, null), 1e-5f, "IgnoreShield scales the rear guard like a shield");
        }

        [TestMethod]
        public void RearHit_NonPlayerDefender_IsOne()
        {
            var defender = At(TestCreatures.CreateDefender(shieldSkill: 0), 55.0f, 45.0f);
            var cloak = MakeCloak(10, mantle: true);
            defender.EquippedObjects[cloak.Guid] = cloak;

            var attacker = Behind();
            Assert.IsTrue(Math.Abs(defender.GetAngle(attacker)) > 90f, "geometry control: the attacker really is behind the defender");
            Assert.AreEqual(1.0f, defender.GetShieldMod(attacker, DamageType.Slash, null), 0.0f, "only a Player can use a Mantle");
        }

        [TestMethod]
        public void RearHit_Disabled_IsOne()
        {
            PropertyManager.ModifyBool("cloak_rear_guard_enabled", false);

            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            Assert.AreEqual(1.0f, defender.GetShieldMod(Behind(), DamageType.Slash, null), 0.0f);
        }

        [TestMethod]
        public void PerLevelSetting_IsRead()
        {
            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", 25.0);

            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(4, mantle: true));

            Assert.AreEqual(0.4f, defender.GetShieldMod(Behind(), DamageType.Slash, null), 1e-4f, "level 4 * 25 = AL 100 -> 0.4");
        }

        [TestMethod]
        public void NullAttacker_ReturnsOne()
        {
            var defender = DefenderPlayer(CombatMode.Melee, MakeCloak(10, mantle: true));

            Assert.AreEqual(1.0f, defender.GetCloakRearGuardMod(null, null), 0.0f);
        }
    }
}

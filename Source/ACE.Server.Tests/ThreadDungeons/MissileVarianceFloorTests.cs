using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads missile variance floor (<see cref="MissileVarianceFloor"/>): every ammunition and thrown weapon a run
    /// creature carries, equipped or in its pack, is raised to the floor and never lowered; Damage (the max hit), launchers
    /// and melee weapons are untouched; 0 disables it; and the engine's missile base damage (Monster_Missile.GetMissileDamage)
    /// rolls from the floored minimum. The PrepareForPlacement test seeds and restores every key that path reads.
    /// </summary>
    [TestClass]
    public class MissileVarianceFloorTests
    {
        private const double Floor = MissileVarianceFloor.DefaultFloor;

        private static uint nextGuid = 0x7F0E0001;
        private static uint nextWcid = 0x7F0E8001;

        private static Weenie CreatureWeenie() => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "missilevariance",
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 300 },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Variance Probe" },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 300 },
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 300 },
                [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 200 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 500 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 500 },
            },
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Hand] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = 500, DVar = 0.75f, BaseArmor = 300 },
            },
        };

        private static WorldObject NewItem(WeenieType type, int damage, double? variance, double? damageMod = null)
            => FromWeenie(ItemWeenie(type, damage, variance, damageMod));

        private static WorldObject FromWeenie(Weenie weenie)
        {
            var guid = new ObjectGuid(nextGuid++);

            return weenie.WeenieType switch
            {
                WeenieType.MissileLauncher => new MissileLauncher(weenie, guid),
                WeenieType.Ammunition => new Ammunition(weenie, guid),
                WeenieType.MeleeWeapon => new MeleeWeapon(weenie, guid),
                WeenieType.Missile => new Missile(weenie, guid),
                _ => new GenericObject(weenie, guid),
            };
        }

        private static Weenie ItemWeenie(WeenieType type, int damage, double? variance, double? damageMod = null)
        {
            var floats = new Dictionary<PropertyFloat, double>();

            if (variance.HasValue) floats[PropertyFloat.DamageVariance] = variance.Value;
            if (damageMod.HasValue) floats[PropertyFloat.DamageMod] = damageMod.Value;

            var ints = new Dictionary<PropertyInt, int>();

            if (damage != 0) ints[PropertyInt.Damage] = damage;

            // a launcher is an ammo launcher only with a bow combat style (WorldObject.IsAmmoLauncher)
            if (type == WeenieType.MissileLauncher) ints[PropertyInt.DefaultCombatStyle] = (int)CombatStyle.Bow;

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                ClassName = $"varianceitem{type}",
                WeenieType = type,
                PropertiesInt = ints,
                PropertiesFloat = floats,
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Variance {type}" },
            };

            return weenie;
        }

        private static Creature NewCreature()
        {
            TestGameTables.EnsureInitialized();
            return new Creature(CreatureWeenie(), new ObjectGuid(nextGuid++));
        }

        private static void Equip(Creature creature, WorldObject item, EquipMask slot)
        {
            item.CurrentWieldedLocation = slot;
            creature.EquippedObjects[item.Guid] = item;
            item.Wielder = creature;
        }

        private static double VarianceOf(WorldObject item) => item.GetProperty(PropertyFloat.DamageVariance) ?? 0.0;

        // ---- the floor on live items ------------------------------------------------------------------------

        [TestMethod]
        public void Equipped_ammo_below_the_floor_is_raised_and_its_Damage_is_unchanged()
        {
            var c = NewCreature();
            var bow = NewItem(WeenieType.MissileLauncher, 0, 0.1, damageMod: 2.0);
            var arrow = NewItem(WeenieType.Ammunition, 400, 0.25);
            Equip(c, bow, EquipMask.MissileWeapon);
            Equip(c, arrow, EquipMask.MissileAmmo);

            var changed = MissileVarianceFloor.Apply(c, Floor);

            Assert.AreEqual(Floor, VarianceOf(arrow), 1e-9, "the arrow is raised to the floor");
            Assert.AreEqual(400, arrow.GetProperty(PropertyInt.Damage), "the max hit (Damage) is untouched");
            Assert.AreEqual(0.1, VarianceOf(bow), 1e-9, "the launcher is not a damage source and is untouched");
            Assert.AreEqual(2.0, bow.GetProperty(PropertyFloat.DamageMod) ?? 0, 1e-9);
            Assert.AreEqual(1, changed.Count);
            Assert.AreEqual((0.25, Floor), (changed[0].From, changed[0].To));
        }

        [TestMethod]
        public void The_engine_missile_hit_rolls_from_the_floored_minimum_at_the_same_max()
        {
            var c = NewCreature();
            var bow = NewItem(WeenieType.MissileLauncher, 0, null, damageMod: 2.0);
            var arrow = NewItem(WeenieType.Ammunition, 400, 0.25);
            Equip(c, bow, EquipMask.MissileWeapon);
            Equip(c, arrow, EquipMask.MissileAmmo);

            var before = c.GetMissileDamage();
            var maxBefore = before.MaxDamage;
            var minBefore = before.MinDamage;

            MissileVarianceFloor.Apply(c, Floor);

            var after = c.GetMissileDamage();

            Assert.AreEqual(maxBefore, after.MaxDamage, 1e-3, "the max hit is unchanged");
            Assert.AreEqual(800f, after.MaxDamage, 1e-3, "guard: ammo Damage x launcher DamageMod");
            Assert.AreEqual(800f * (1f - (float)Floor), after.MinDamage, 1e-2, "the minimum the engine rolls from is Max x (1 - floor)");
            Assert.IsTrue(after.MinDamage < minBefore, "guard: the floor widened the roll");
        }

        [TestMethod]
        public void A_variance_already_above_the_floor_is_never_lowered()
        {
            var c = NewCreature();
            var rock = NewItem(WeenieType.Missile, 300, 0.8);
            Equip(c, rock, EquipMask.MissileWeapon);

            var changed = MissileVarianceFloor.Apply(c, Floor);

            Assert.AreEqual(0.8, VarianceOf(rock), 1e-9);
            Assert.AreEqual(0, changed.Count);
            Assert.AreEqual(0.8, MissileVarianceFloor.Floored(0.8, Floor), 1e-9);
            Assert.AreEqual(Floor, MissileVarianceFloor.Floored(Floor, Floor), 1e-9);
        }

        [TestMethod]
        public void Thrown_weapons_and_pack_ammo_are_covered_and_melee_weapons_are_not()
        {
            var c = NewCreature();
            var thrown = NewItem(WeenieType.Missile, 300, 0.25);
            var packArrow = NewItem(WeenieType.Ammunition, 200, null);
            var sword = NewItem(WeenieType.MeleeWeapon, 300, 0.25);
            var packSword = NewItem(WeenieType.MeleeWeapon, 300, 0.3);

            Equip(c, thrown, EquipMask.MissileWeapon);
            Equip(c, sword, EquipMask.MeleeWeapon);
            c.Inventory[packArrow.Guid] = packArrow;
            c.Inventory[packSword.Guid] = packSword;

            MissileVarianceFloor.Apply(c, Floor);

            Assert.AreEqual(Floor, VarianceOf(thrown), 1e-9, "an equipped thrown weapon is raised");
            Assert.AreEqual(Floor, VarianceOf(packArrow), 1e-9, "ammo in the pack (no variance row at all) is raised too");
            Assert.AreEqual(0.25, VarianceOf(sword), 1e-9, "an equipped melee weapon is untouched");
            Assert.AreEqual(0.3, VarianceOf(packSword), 1e-9, "a melee weapon in the pack is untouched");
            Assert.AreEqual(300, thrown.GetProperty(PropertyInt.Damage));
            Assert.AreEqual(200, packArrow.GetProperty(PropertyInt.Damage));
        }

        [TestMethod]
        public void A_floor_of_zero_disables_it_and_the_tunable_is_sanitized()
        {
            var c = NewCreature();
            var arrow = NewItem(WeenieType.Ammunition, 400, 0.25);
            Equip(c, arrow, EquipMask.MissileAmmo);

            Assert.AreEqual(0, MissileVarianceFloor.Apply(c, 0.0).Count);
            Assert.AreEqual(0.25, VarianceOf(arrow), 1e-9, "0 leaves every source as authored");

            Assert.AreEqual(0.0, MissileVarianceFloor.Sanitize(0.0), 0.0, "0 is off");
            Assert.AreEqual(0.0, MissileVarianceFloor.Sanitize(-0.4), 0.0, "a negative value reads as off");
            Assert.AreEqual(0.95, MissileVarianceFloor.Sanitize(1.5), 0.0, "clamped to 0.95");
            Assert.AreEqual(MissileVarianceFloor.DefaultFloor, MissileVarianceFloor.Sanitize(double.PositiveInfinity), 0.0, "Infinity reads as the default, as the sibling dials");
            Assert.AreEqual(MissileVarianceFloor.DefaultFloor, MissileVarianceFloor.Sanitize(double.NegativeInfinity), 0.0);
            Assert.IsTrue(MissileVarianceFloor.NeedsWarning(-0.4) && MissileVarianceFloor.NeedsWarning(1.5) && MissileVarianceFloor.NeedsWarning(double.NaN)
                && MissileVarianceFloor.NeedsWarning(double.PositiveInfinity), "every out-of-range value warns");
            Assert.IsFalse(MissileVarianceFloor.NeedsWarning(0.0) || MissileVarianceFloor.NeedsWarning(0.5) || MissileVarianceFloor.NeedsWarning(0.95), "an in-range value does not");
            Assert.AreEqual(MissileVarianceFloor.DefaultFloor, MissileVarianceFloor.Sanitize(double.NaN), 0.0, "NaN reads as the default");
            Assert.AreEqual(0.5, MissileVarianceFloor.Sanitize(0.5), 0.0);
        }

        [TestMethod]
        public void The_default_ships_on_at_the_derived_value()
        {
            Assert.AreEqual(0.69, MissileVarianceFloor.DefaultFloor, 0.0, "derived from the L500 probe: 2 x (1 - 0.6563), rounded");
            Assert.AreEqual(MissileVarianceFloor.DefaultFloor,
                DefaultPropertyManager.DefaultDoubleProperties[MissileVarianceFloor.Tunable].Item, 0.0, "the registered default is the compiled one");
        }

        // ---- per instance, and the engine reads the floored value ----------------------------------------------

        [TestMethod]
        public void Flooring_one_creature_leaves_a_sibling_built_from_the_same_weenies_and_the_weenies_alone()
        {
            var creatureWeenie = CreatureWeenie();
            var arrowWeenie = ItemWeenie(WeenieType.Ammunition, 400, 0.25);

            TestGameTables.EnsureInitialized();
            var floored = new Creature(creatureWeenie, new ObjectGuid(nextGuid++));
            var sibling = new Creature(creatureWeenie, new ObjectGuid(nextGuid++));
            var flooredArrow = FromWeenie(arrowWeenie);
            var siblingArrow = FromWeenie(arrowWeenie);
            Equip(floored, flooredArrow, EquipMask.MissileAmmo);
            Equip(sibling, siblingArrow, EquipMask.MissileAmmo);

            MissileVarianceFloor.Apply(floored, Floor);

            Assert.AreEqual(Floor, VarianceOf(flooredArrow), 1e-9, "guard: the floored creature's ammo moved");
            Assert.AreEqual(0.25, VarianceOf(siblingArrow), 1e-9, "the sibling's ammo, built from the same weenie, is untouched");
            Assert.AreEqual(0.25, arrowWeenie.PropertiesFloat[PropertyFloat.DamageVariance], 1e-9, "the ammo weenie's dictionary is untouched");
            Assert.AreEqual(0.75f, creatureWeenie.PropertiesBodyPart[CombatBodyPart.Hand].DVar, 1e-6, "the creature weenie is untouched");
        }

        [TestMethod]
        public void Pack_ammo_floored_then_equipped_is_what_the_engine_fires()
        {
            var c = NewCreature();
            var bow = NewItem(WeenieType.MissileLauncher, 0, null, damageMod: 2.0);
            var arrow = NewItem(WeenieType.Ammunition, 400, 0.25);
            Equip(c, bow, EquipMask.MissileWeapon);
            c.Inventory[arrow.Guid] = arrow;

            MissileVarianceFloor.Apply(c, Floor);

            // the mid-fight swap: EquipInventoryItems moves pack ammo into the ammo slot
            c.Inventory.Remove(arrow.Guid);
            Equip(c, arrow, EquipMask.MissileAmmo);

            Assert.AreSame(arrow, c.GetMissileAmmo(), "guard: the engine now fires the pack arrow");

            var hit = c.GetMissileDamage();

            Assert.AreEqual(800f, hit.MaxDamage, 1e-3, "ammo Damage x launcher DamageMod, unchanged");
            Assert.AreEqual(800f * (1f - (float)Floor), hit.MinDamage, 1e-2, "the floored variance was carried by the pack arrow");
        }

        [TestMethod]
        public void A_missile_attack_through_GetBaseDamage_rolls_from_Max_times_one_minus_the_floor()
        {
            var c = NewCreature();
            var bow = NewItem(WeenieType.MissileLauncher, 0, null, damageMod: 2.0);
            var arrow = NewItem(WeenieType.Ammunition, 400, 0.25);
            Equip(c, bow, EquipMask.MissileWeapon);
            Equip(c, arrow, EquipMask.MissileAmmo);

            MissileVarianceFloor.Apply(c, Floor);

            c.CurrentAttack = CombatType.Missile;
            var part = c.Biota.PropertiesBodyPart[CombatBodyPart.Hand];
            var hit = c.GetBaseDamage(part);

            Assert.AreEqual(800f, hit.MaxDamage, 1e-3, "the missile path (ammo x launcher), not the body part's DVal 500");
            Assert.AreEqual(hit.MaxDamage * (1f - (float)Floor), hit.MinDamage, 1e-2, "Monster_Melee.GetBaseDamage's missile branch rolls from the floored minimum");
        }

        // ---- through PrepareForPlacement --------------------------------------------------------------------

        [TestMethod]
        public void PrepareForPlacement_floors_missile_variance_and_zero_turns_it_off()
        {
            var bools = new (string Key, bool Value)[]
            {
                ("dynamic_dungeons_uplift_attributes", DungeonPopulationLimits.DefaultUpliftAttributes),
                ("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled),
            };

            try
            {
                foreach (var (key, value) in bools)
                    Assert.IsTrue(PropertyManager.ModifyBool(key, value), $"ModifyBool({key}) returned false: key not registered");

                Assert.IsTrue(PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds));

                (Creature Creature, WorldObject Arrow, WorldObject Sword) Armed()
                {
                    var c = NewCreature();
                    var bow = NewItem(WeenieType.MissileLauncher, 0, null, damageMod: 1.5);
                    var arrow = NewItem(WeenieType.Ammunition, 400, 0.25);
                    var sword = NewItem(WeenieType.MeleeWeapon, 300, 0.25);
                    Equip(c, bow, EquipMask.MissileWeapon);
                    Equip(c, arrow, EquipMask.MissileAmmo);
                    c.Inventory[sword.Guid] = sword;
                    return (c, arrow, sword);
                }

                var entry = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);

                Assert.IsTrue(PropertyManager.ModifyDouble(MissileVarianceFloor.Tunable, Floor));
                var on = Armed();
                ThreadDungeonSpawner.PrepareForPlacement(null, on.Creature, entry, new DungeonSpawnPlan(), observedMaxNonBoss: 0);

                Assert.AreEqual(Floor, VarianceOf(on.Arrow), 1e-9, "PrepareForPlacement floored the equipped ammo");
                Assert.AreEqual(400, on.Arrow.GetProperty(PropertyInt.Damage), "the max hit is unchanged (no median: the ceiling is off)");
                Assert.AreEqual(0.25, VarianceOf(on.Sword), 1e-9, "melee weapons are untouched");

                Assert.IsTrue(PropertyManager.ModifyDouble(MissileVarianceFloor.Tunable, 0.0));
                var off = Armed();
                ThreadDungeonSpawner.PrepareForPlacement(null, off.Creature, entry, new DungeonSpawnPlan(), observedMaxNonBoss: 0);

                Assert.AreEqual(0.25, VarianceOf(off.Arrow), 1e-9, "0 disables the floor");
            }
            finally
            {
                foreach (var (key, value) in bools)
                    PropertyManager.ModifyBool(key, value);

                PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds);
                PropertyManager.ModifyDouble(MissileVarianceFloor.Tunable, MissileVarianceFloor.DefaultFloor);
            }
        }
    }
}

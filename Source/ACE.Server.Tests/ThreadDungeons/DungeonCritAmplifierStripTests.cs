using System;
using System.IO;
using System.Linq;
using System.Reflection;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The crit-amplifier half of the combat-trait strip (owner ruling 2026-11-21): a Threads run to level 506 killed a
    /// player with one 2271-damage hit from a stretched Tendril of T'thuun whose thrown weapon (wcid 33459) was never
    /// stripped of crit amplifiers. No enemy may carry a crippling-blow style crit-damage amplifier, on any item
    /// DamageEvent reads as the Weapon - and for a missile hit that is the launcher, or the ammo when there is no launcher
    /// (a thrown weapon).
    ///
    /// Each test builds a bare WorldObject (a live Creature cannot be constructed in ACE.Server.Tests) and calls
    /// DungeonCreatureNormalizer.StripItemCombatTraits, the exact per-item function CombatTraitGuard.Apply sweeps every
    /// equipped item and Inventory entry through.
    /// </summary>
    [TestClass]
    public class DungeonCritAmplifierStripTests
    {
        private static Weenie W(uint wcid, WeenieType type) => new Weenie { WeenieClassId = wcid, WeenieType = type };

        private static WorldObject Generic(uint wcid = 30000) => new GenericObject(W(wcid, WeenieType.Generic), new ObjectGuid(0x70000001));
        private static WorldObject Caster(uint wcid = 30001) => new Caster(W(wcid, WeenieType.Caster), new ObjectGuid(0x70000002));
        private static WorldObject Launcher(uint wcid = 30002) => new MissileLauncher(W(wcid, WeenieType.MissileLauncher), new ObjectGuid(0x70000003));
        private static WorldObject Thrown(uint wcid = 33459) => new Missile(W(wcid, WeenieType.Missile), new ObjectGuid(0x70000004));

        private static System.Collections.Generic.List<string> Strip(WorldObject item) => DungeonCreatureNormalizer.StripItemCombatTraits(item);

        // ---- imbues -------------------------------------------------------------------------------------

        [TestMethod]
        public void CripplingBlow_imbue_is_cleared_and_the_property_removed_at_zero()
        {
            var item = Generic();
            item.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CripplingBlow);
            Assert.IsTrue(item.HasImbuedEffect(ImbuedEffectType.CripplingBlow), "control: the predicate DamageEvent reads is set before the strip");

            CollectionAssert.AreEqual(new[] { "CripplingBlow" }, Strip(item));

            Assert.IsFalse(item.HasImbuedEffect(ImbuedEffectType.CripplingBlow));
            Assert.IsFalse(item.GetProperty(PropertyInt.ImbuedEffect).HasValue, "an emptied ImbuedEffect is removed, not left as 0");
        }

        [TestMethod]
        public void CriticalStrike_imbue_is_cleared()
        {
            var item = Generic();
            item.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CriticalStrike);

            CollectionAssert.AreEqual(new[] { "CriticalStrike" }, Strip(item));

            Assert.IsFalse(item.HasImbuedEffect(ImbuedEffectType.CriticalStrike));
            Assert.IsFalse(item.GetProperty(PropertyInt.ImbuedEffect).HasValue);
        }

        [TestMethod]
        public void Every_damage_imbue_bit_goes_and_only_the_defense_and_spellbook_bits_are_kept()
        {
            // Owner ruling 2026-11-23 widened the 2026-11-21 crit-only mask: no imbue that changes damage survives.
            var item = Generic();
            item.SetProperty(PropertyInt.ImbuedEffect, (int)(ImbuedEffectType.CripplingBlow | ImbuedEffectType.CriticalStrike | ImbuedEffectType.ArmorRending | ImbuedEffectType.SlashRending | ImbuedEffectType.MeleeDefense));

            CollectionAssert.AreEquivalent(new[] { "CripplingBlow", "CriticalStrike", "ArmorRending", "SlashRending" }, Strip(item));

            Assert.AreEqual((int)ImbuedEffectType.MeleeDefense, item.GetProperty(PropertyInt.ImbuedEffect), "positive control: a defense imbue is not a damage imbue and stays");
        }

        [TestMethod]
        public void An_imbue_with_only_kept_bits_is_untouched_and_logs_nothing()
        {
            var item = Generic();
            var kept = (int)(ImbuedEffectType.MeleeDefense | ImbuedEffectType.MissileDefense | ImbuedEffectType.MagicDefense | ImbuedEffectType.Spellbook);
            item.SetProperty(PropertyInt.ImbuedEffect, kept);

            Assert.AreEqual(0, Strip(item).Count);
            Assert.AreEqual(kept, item.GetProperty(PropertyInt.ImbuedEffect));
        }

        [TestMethod]
        public void The_damage_bits_are_cleared_from_every_one_of_the_five_imbue_slots()
        {
            var item = Generic();
            item.SetProperty(PropertyInt.ImbuedEffect2, (int)(ImbuedEffectType.CripplingBlow | ImbuedEffectType.FireRending | ImbuedEffectType.MagicDefense));
            item.SetProperty(PropertyInt.ImbuedEffect5, (int)ImbuedEffectType.CriticalStrike);

            Strip(item);

            Assert.AreEqual((int)ImbuedEffectType.MagicDefense, item.GetProperty(PropertyInt.ImbuedEffect2));
            Assert.IsFalse(item.GetProperty(PropertyInt.ImbuedEffect5).HasValue);
            Assert.AreEqual(ImbuedEffectType.MagicDefense, item.GetImbuedEffects(), "what GetImbuedEffects ORs across the slots");
        }

        // ---- CriticalMultiplier / CriticalFrequency -----------------------------------------------------

        [TestMethod]
        public void A_CriticalMultiplier_above_default_is_removed_and_combat_reads_the_default()
        {
            var item = Generic();
            item.SetProperty(PropertyFloat.CriticalMultiplier, 3.0);
            Assert.AreEqual(3.0f, WorldObject.GetWeaponCritDamageMod(item, null, null, null), "control: combat reads the authored 3.0 before the strip");

            CollectionAssert.AreEqual(new[] { "CriticalMultiplier" }, Strip(item));

            Assert.IsFalse(item.GetProperty(PropertyFloat.CriticalMultiplier).HasValue);
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(item, null, null, null), "the consumer DamageEvent calls now returns the 1.0 default");
        }

        [TestMethod]
        public void A_CriticalMultiplier_at_or_below_default_is_left_alone()
        {
            foreach (var value in new[] { 1.0, 0.8 })
            {
                var item = Generic();
                item.SetProperty(PropertyFloat.CriticalMultiplier, value);

                Assert.AreEqual(0, Strip(item).Count, $"CriticalMultiplier {value}");
                Assert.AreEqual(value, item.GetProperty(PropertyFloat.CriticalMultiplier));
            }
        }

        [TestMethod]
        public void A_CriticalFrequency_above_default_is_removed_on_a_physical_weapon()
        {
            var item = Launcher();
            item.SetProperty(PropertyFloat.CriticalFrequency, 0.3);

            CollectionAssert.AreEqual(new[] { "CriticalFrequency" }, Strip(item));

            Assert.IsNull(item.CriticalFrequency, "the property combat reads (weapon?.CriticalFrequency ?? default) now falls back to the default");
        }

        [TestMethod]
        public void A_CriticalFrequency_at_or_below_default_is_left_alone_because_removing_it_would_raise_the_chance()
        {
            foreach (var value in new[] { 0.1, 0.05, 0.02 })
            {
                var item = Generic();
                item.SetProperty(PropertyFloat.CriticalFrequency, value);

                Assert.AreEqual(0, Strip(item).Count, $"CriticalFrequency {value}");
                Assert.AreEqual(value, item.GetProperty(PropertyFloat.CriticalFrequency), $"CriticalFrequency {value} must survive");
            }
        }

        [TestMethod]
        public void A_caster_uses_the_lower_magic_default()
        {
            var above = Caster();
            above.SetProperty(PropertyFloat.CriticalFrequency, 0.07);
            CollectionAssert.AreEqual(new[] { "CriticalFrequency" }, Strip(above), "0.07 is above a wand's 0.05 default, though below a physical weapon's 0.1");

            var atDefault = Caster();
            atDefault.SetProperty(PropertyFloat.CriticalFrequency, 0.05);
            Assert.AreEqual(0, Strip(atDefault).Count);

            var below = Caster();
            below.SetProperty(PropertyFloat.CriticalFrequency, 0.03);
            Assert.AreEqual(0, Strip(below).Count, "negative control: below the magic default is never touched");
            Assert.AreEqual(0.03, below.GetProperty(PropertyFloat.CriticalFrequency));
        }

        [TestMethod]
        public void The_default_constants_match_the_private_ones_combat_falls_back_to()
        {
            double Const(string name)
            {
                var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(field, name + " moved or was renamed in WorldObject_Weapon.cs; update DungeonCombatNormalizer's mirrored default");
                return Convert.ToDouble(field.GetRawConstantValue());
            }

            Assert.AreEqual(Const("defaultPhysicalCritFrequency"), DungeonCombatNormalizer.DefaultPhysicalCritFrequency, 1e-6);
            Assert.AreEqual(Const("defaultMagicCritFrequency"), DungeonCombatNormalizer.DefaultMagicCritFrequency, 1e-6);
            Assert.AreEqual(Const("defaultCritDamageMultiplier"), DungeonCombatNormalizer.DefaultCritMultiplier, 1e-6);
        }

        // ---- Execution and the rating -------------------------------------------------------------------

        [TestMethod]
        public void The_Execution_weapon_mod_record_is_removed()
        {
            Assert.AreEqual(WeaponModRegistry.Get(WeaponModId.Execution).Record, PropertyFloat.WeaponModExecution, "the strip must name the property Execution actually reads");

            var item = Launcher();
            item.SetProperty(PropertyFloat.WeaponModExecution, 0.4);

            CollectionAssert.AreEqual(new[] { "Execution" }, Strip(item));
            Assert.IsFalse(item.GetProperty(PropertyFloat.WeaponModExecution).HasValue);
        }

        [TestMethod]
        public void CritDamageRating_is_a_creature_level_strip_and_the_item_sweep_leaves_property_314_alone()
        {
            // Creature level (CombatTraitGuard.ApplyCore, see BandUpliftCoreTests). Items are not swept for 314: combat never
            // reads it there - an item's crit damage rides on GearCritDamage via the equip-time rating cache.
            CollectionAssert.Contains(DungeonCombatNormalizer.BypassPositiveInts, PropertyInt.CritDamageRating);

            var item = Generic();
            item.SetProperty(PropertyInt.CritDamageRating, 40);

            Assert.AreEqual(0, Strip(item).Count);
            Assert.AreEqual(40, item.GetProperty(PropertyInt.CritDamageRating));
        }

        // ---- the missile hit ----------------------------------------------------------------------------

        /// <summary>
        /// DamageEvent.DoCalculateDamage (Entity/DamageEvent.cs:428): Weapon = damageSource.ProjectileLauncher ??
        /// damageSource.ProjectileAmmo. Creature_Missile.LaunchProjectile stamps those two from the creature's EQUIPPED
        /// launcher and ammo instances (Monster_Missile.cs:151 passes GetEquippedMissileLauncher() and the equipped ammo, or
        /// the equipped thrown weapon itself when there is no launcher) - the projectile it spawns is a fresh object from
        /// the ammo wcid, but it is never the Weapon. So the strip reaches a missile hit exactly when it strips the
        /// equipped instance, which this pins for both shapes.
        /// </summary>
        [TestMethod]
        public void A_missile_hit_reads_the_stripped_launcher_instance()
        {
            var launcher = Launcher();
            var ammo = Generic(30003);
            launcher.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.CripplingBlow);
            launcher.SetProperty(PropertyFloat.CriticalMultiplier, 4.0);
            ammo.SetProperty(PropertyFloat.CriticalMultiplier, 4.0);

            Strip(launcher);
            Strip(ammo);

            var projectile = Generic(30004);
            projectile.ProjectileLauncher = launcher;
            projectile.ProjectileAmmo = ammo;
            var weapon = projectile.ProjectileLauncher ?? projectile.ProjectileAmmo;

            Assert.AreSame(launcher, weapon);
            Assert.IsFalse(weapon.HasImbuedEffect(ImbuedEffectType.CripplingBlow));
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(weapon, null, null, null));
        }

        [TestMethod]
        public void A_thrown_weapon_hit_reads_the_stripped_ammo_instance_when_there_is_no_launcher()
        {
            // wcid 33459 "Shadow Bolt": thrown missile ammo, no launcher.
            var thrown = Thrown();
            thrown.SetProperty(PropertyInt.ImbuedEffect, (int)(ImbuedEffectType.CripplingBlow | ImbuedEffectType.CriticalStrike));
            thrown.SetProperty(PropertyFloat.CriticalMultiplier, 4.0);
            thrown.SetProperty(PropertyFloat.CriticalFrequency, 0.5);

            var changes = Strip(thrown);

            CollectionAssert.IsSubsetOf(new[] { "CripplingBlow", "CriticalStrike", "CriticalMultiplier", "CriticalFrequency" }, changes);

            var projectile = Generic(33459);
            projectile.ProjectileLauncher = null;
            projectile.ProjectileAmmo = thrown;
            var weapon = projectile.ProjectileLauncher ?? projectile.ProjectileAmmo;

            Assert.AreSame(thrown, weapon);
            Assert.IsFalse(weapon.HasImbuedEffect(ImbuedEffectType.CripplingBlow));
            Assert.IsFalse(weapon.HasImbuedEffect(ImbuedEffectType.CriticalStrike));
            Assert.IsNull(weapon.CriticalFrequency);
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(weapon, null, null, null));
        }

        /// <summary>
        /// Source pin for the instance chain the two tests above model, because a real monster missile hit cannot be run
        /// here. Each line is the seam: the hit reads launcher-or-ammo, the launch stamps the EQUIPPED instances onto the
        /// projectile, and the monster passes the equipped launcher/ammo (not a fresh weenie copy) into LaunchProjectile.
        /// </summary>
        [TestMethod]
        public void The_missile_instance_chain_is_the_equipped_instance()
        {
            var root = FindRepoRoot();

            string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root, "Source", "ACE.Server" }.Concat(parts).ToArray()));

            StringAssert.Contains(Read("Entity", "DamageEvent.cs"), "(damageSource.ProjectileLauncher ?? damageSource.ProjectileAmmo)");

            var launch = Read("WorldObjects", "Creature_Missile.cs");
            StringAssert.Contains(launch, "proj.ProjectileLauncher = weapon;");
            StringAssert.Contains(launch, "proj.ProjectileAmmo = ammo;");

            var monster = Read("WorldObjects", "Monster_Missile.cs");
            StringAssert.Contains(monster, "var ammo = weapon.IsAmmoLauncher ? GetEquippedAmmo() : weapon;");
            StringAssert.Contains(monster, "var launcher = GetEquippedMissileLauncher();");
            StringAssert.Contains(monster, "LaunchProjectile(launcher, ammo, target, origin, orientation, velocity)");
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Source", "property-registry.tsv")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new AssertFailedException($"Could not find the repo root by walking up from {AppContext.BaseDirectory}.");
        }
    }
}

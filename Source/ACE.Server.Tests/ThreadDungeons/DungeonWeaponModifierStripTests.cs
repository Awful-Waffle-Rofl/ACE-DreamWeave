using System;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The 2026-11-23 widening of the per-item combat-trait strip (owner ruling: Thread family weapons carry no special
    /// modifiers - no hollow, no imbues - so every enemy is on the same footing for damage, ammo included). Each test builds
    /// a bare WorldObject (a live Creature cannot be constructed in ACE.Server.Tests) and calls
    /// DungeonCreatureNormalizer.StripItemCombatTraits, the per-item function CombatTraitGuard.Apply sweeps every equipped
    /// and Inventory item through, and that Creature_Missile.LaunchProjectile now runs on every projectile a stripped
    /// creature fires. The crit half (CriticalMultiplier, CriticalFrequency, Execution) is DungeonCritAmplifierStripTests.
    /// </summary>
    [TestClass]
    public class DungeonWeaponModifierStripTests
    {
        private static Weenie W(uint wcid, WeenieType type) => new Weenie { WeenieClassId = wcid, WeenieType = type };

        private static WorldObject Melee(uint wcid = 31000) => new MeleeWeapon(W(wcid, WeenieType.MeleeWeapon), new ObjectGuid(0x70000011));
        private static WorldObject Caster(uint wcid = 31001) => new Caster(W(wcid, WeenieType.Caster), new ObjectGuid(0x70000012));
        private static WorldObject Launcher(uint wcid = 31002) => new MissileLauncher(W(wcid, WeenieType.MissileLauncher), new ObjectGuid(0x70000013));
        private static WorldObject Ammo(uint wcid = 31003, uint guid = 0x70000014) => new Ammunition(W(wcid, WeenieType.Ammunition), new ObjectGuid(guid));

        private static System.Collections.Generic.List<string> Strip(WorldObject item) => DungeonCreatureNormalizer.StripItemCombatTraits(item);

        // ---- imbues beyond the crit pair ---------------------------------------------------------------

        [TestMethod]
        public void ArmorRending_is_cleared_and_the_rending_mod_combat_reads_falls_back_to_one()
        {
            var item = Melee();
            item.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.ArmorRending);
            Assert.IsTrue(item.HasImbuedEffect(ImbuedEffectType.ArmorRending), "control: the predicate GetArmorRendingModFor reads is set");

            CollectionAssert.AreEqual(new[] { "ArmorRending" }, Strip(item));

            Assert.IsFalse(item.GetProperty(PropertyInt.ImbuedEffect).HasValue);
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(item, null, null, null), "DamageEvent.ResolveDefenderHalf's armor rending term");
        }

        [TestMethod]
        public void IgnoreAllArmor_the_sign_bit_is_cleared_without_disturbing_a_kept_bit()
        {
            var item = Melee();
            item.SetProperty(PropertyInt.ImbuedEffect, unchecked((int)(ImbuedEffectType.IgnoreAllArmor | ImbuedEffectType.MissileDefense)));
            Assert.IsTrue(item.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor), "control");

            CollectionAssert.AreEqual(new[] { "IgnoreAllArmor" }, Strip(item));

            Assert.IsFalse(item.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor));
            Assert.AreEqual((int)ImbuedEffectType.MissileDefense, item.GetProperty(PropertyInt.ImbuedEffect));
        }

        [TestMethod]
        public void Every_rend_bit_including_the_fork_HealthRending_is_cleared()
        {
            var rends = new[]
            {
                ImbuedEffectType.SlashRending, ImbuedEffectType.PierceRending, ImbuedEffectType.BludgeonRending, ImbuedEffectType.AcidRending,
                ImbuedEffectType.ColdRending, ImbuedEffectType.ElectricRending, ImbuedEffectType.FireRending, ImbuedEffectType.NetherRending,
                ImbuedEffectType.HealthRending,
            };

            var item = Caster();
            item.SetProperty(PropertyInt.ImbuedEffect, (int)rends.Aggregate(ImbuedEffectType.Undef, (m, b) => m | b));

            CollectionAssert.AreEquivalent(rends.Select(r => r.ToString()).ToArray(), Strip(item));
            Assert.AreEqual(ImbuedEffectType.Undef, item.GetImbuedEffects());
        }

        [TestMethod]
        public void IgnoreSomeMagicProjectileDamage_goes_so_the_imbue_absorb_reads_nothing()
        {
            var item = Launcher();
            item.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.IgnoreSomeMagicProjectileDamage);
            Assert.AreEqual(0.25, item.GetAbsorbMagicDamage(), "control: the imbue form of AbsorbMagicDamage is live");

            Strip(item);

            Assert.IsNull(item.GetAbsorbMagicDamage());
        }

        // ---- int and float modifiers -------------------------------------------------------------------

        [TestMethod]
        public void Slayer_type_and_bonus_are_removed()
        {
            var item = Melee();
            item.SlayerCreatureType = CreatureType.Human;
            item.SlayerDamageBonus = 2.5;

            CollectionAssert.AreEquivalent(new[] { "SlayerCreatureType", "SlayerDamageBonus" }, Strip(item));

            Assert.IsNull(item.SlayerCreatureType, "GetWeaponCreatureSlayerModifier returns its default without a slayer type");
            Assert.IsNull(item.SlayerDamageBonus);
        }

        [TestMethod]
        public void Resistance_cleaving_type_and_amount_are_removed()
        {
            var item = Melee();
            item.ResistanceModifierType = DamageType.Fire;
            item.ResistanceModifier = 1.0;

            CollectionAssert.AreEquivalent(new[] { "ResistanceModifierType", "ResistanceModifier" }, Strip(item));

            Assert.IsNull(item.ResistanceModifierType);
            Assert.IsNull(item.ResistanceModifier);
        }

        [TestMethod]
        public void The_offensive_gear_ratings_are_removed_and_a_defensive_one_is_kept()
        {
            var item = Melee();
            item.SetProperty(PropertyInt.GearDamage, 10);
            item.SetProperty(PropertyInt.GearCrit, 5);
            item.SetProperty(PropertyInt.GearCritDamage, 8);
            item.SetProperty(PropertyInt.GearDamageResist, 6);

            CollectionAssert.AreEquivalent(new[] { "GearDamage", "GearCrit", "GearCritDamage" }, Strip(item));

            Assert.IsNull(item.GearDamage);
            Assert.IsNull(item.GearCrit);
            Assert.IsNull(item.GearCritDamage);
            Assert.AreEqual(6, item.GearDamageResist, "positive control: a defensive gear rating is not damage the item deals");
        }

        [TestMethod]
        public void A_caster_loses_its_elemental_damage_mod_and_both_caster_weapon_mod_records()
        {
            var item = Caster();
            item.SetProperty(PropertyFloat.ElementalDamageMod, 1.3);
            item.SetProperty(PropertyFloat.WeaponModAttunement, 0.5);
            item.SetProperty(PropertyFloat.WeaponModFocus, 0.5);

            CollectionAssert.AreEquivalent(new[] { "ElementalDamageMod", "WeaponModAttunement", "WeaponModFocus" }, Strip(item));

            Assert.IsNull(item.ElementalDamageMod, "SumCasterElementalDamageMod reads ElementalDamageMod ?? 1.0");
            Assert.IsFalse(item.GetProperty(PropertyFloat.WeaponModAttunement).HasValue);
            Assert.IsFalse(item.GetProperty(PropertyFloat.WeaponModFocus).HasValue);
        }

        [TestMethod]
        public void The_Paragon_gem_slots_are_removed()
        {
            var item = Melee();
            foreach (var slot in new[] { PropertyInt.ParagonModSlot1, PropertyInt.ParagonModSlot2, PropertyInt.ParagonModSlot3, PropertyInt.ParagonModSlot4, PropertyInt.ParagonModSlot5 })
                item.SetProperty(slot, 1);

            Assert.AreEqual(5, Strip(item).Count(c => c.StartsWith("ParagonModSlot")));
            Assert.IsFalse(ACE.Server.WeaponMods.WeaponModParagon.HasParagonMods(item), "every Paragon read gates on HasParagonMods");
        }

        [TestMethod]
        public void A_weapon_proc_is_removed_with_its_rate_and_target_flag()
        {
            var item = Melee();
            item.SetProperty(PropertyDataId.ProcSpell, 1234u);
            item.SetProperty(PropertyFloat.ProcSpellRate, 0.2);
            item.SetProperty(PropertyBool.ProcSpellSelfTargeted, false);
            Assert.IsTrue(item.HasProc, "control: TryProcEquippedItems fires on HasProc");

            CollectionAssert.AreEquivalent(new[] { "ProcSpell", "ProcSpellRate" }, Strip(item));

            Assert.IsFalse(item.HasProc);
            Assert.IsFalse(item.GetProperty(PropertyFloat.ProcSpellRate).HasValue);
            Assert.IsNull(item.GetProperty(PropertyBool.ProcSpellSelfTargeted));
        }

        [TestMethod]
        public void Base_damage_and_unrelated_properties_survive_the_strip()
        {
            var item = Launcher();
            item.SetProperty(PropertyInt.Damage, 40);
            item.SetProperty(PropertyFloat.DamageMod, 2.1);
            item.SetProperty(PropertyFloat.DamageVariance, 0.3);
            item.SetProperty(PropertyInt.DamageType, (int)DamageType.Fire);
            item.SetProperty(PropertyInt.ElementalDamageBonus, 12);
            item.SetProperty(PropertyFloat.WeaponOffense, 1.1);
            item.SetProperty(PropertyInt.ImbuedEffect, (int)ImbuedEffectType.FireRending);
            item.SetProperty(PropertyDataId.ProcSpell, 99u);

            var changes = Strip(item);

            CollectionAssert.AreEquivalent(new[] { "FireRending", "ProcSpell" }, changes, "control: the strip did run on this item");
            Assert.AreEqual(40, item.GetProperty(PropertyInt.Damage), "Damage is normalization's, never the strip's");
            Assert.AreEqual(2.1, item.GetProperty(PropertyFloat.DamageMod));
            Assert.AreEqual(0.3, item.GetProperty(PropertyFloat.DamageVariance));
            Assert.AreEqual((int)DamageType.Fire, item.GetProperty(PropertyInt.DamageType));
            Assert.AreEqual(12, item.GetProperty(PropertyInt.ElementalDamageBonus), "unread on the monster path (DamageEvent.GetBaseDamage(Player) only), so not stripped");
            Assert.AreEqual(1.1, item.GetProperty(PropertyFloat.WeaponOffense));
        }

        // ---- ammo and the projectile -------------------------------------------------------------------

        [TestMethod]
        public void Hollow_and_imbued_ammo_is_stripped()
        {
            var ammo = Ammo();
            ammo.SetProperty(PropertyBool.IgnoreMagicArmor, true);
            ammo.SetProperty(PropertyBool.IgnoreMagicResist, true);
            ammo.SetProperty(PropertyFloat.HollowIntensity, 1.0);
            ammo.SetProperty(PropertyInt.ImbuedEffect, (int)(ImbuedEffectType.ArmorRending | ImbuedEffectType.PierceRending));
            ammo.SetProperty(PropertyDataId.ProcSpell, 77u);

            CollectionAssert.AreEquivalent(new[] { "IgnoreMagicArmor", "IgnoreMagicResist", "HollowIntensity", "ArmorRending", "PierceRending", "ProcSpell" }, Strip(ammo));
        }

        /// <summary>
        /// The projectile is a FRESH object from the ammo wcid (Creature_Missile.LaunchProjectile:
        /// WorldObjectFactory.CreateNewWorldObject(ammo.WeenieClassId)), so the strip of the equipped ammo instance never
        /// reaches it. Modelled here as a second instance of the same weenie: stripping the equipped one leaves the
        /// projectile's own proc live, and only stripping the projectile itself (what LaunchProjectile now does) clears it.
        /// </summary>
        [TestMethod]
        public void A_fresh_projectile_from_the_same_weenie_keeps_its_proc_until_it_is_stripped_itself()
        {
            var weenie = W(31003, WeenieType.Ammunition);
            var equipped = new Ammunition(weenie, new ObjectGuid(0x70000021));
            var projectile = new Ammunition(weenie, new ObjectGuid(0x70000022));
            equipped.SetProperty(PropertyDataId.ProcSpell, 55u);
            projectile.SetProperty(PropertyDataId.ProcSpell, 55u);

            Strip(equipped);
            Assert.IsTrue(projectile.HasProc, "control: the spawn-time strip of the equipped ammo does not reach the projectile");

            Strip(projectile);
            Assert.IsFalse(projectile.HasProc);
        }

        /// <summary>
        /// Source pins for the seams no unit test can run (no live Creature here): the guard sets the projectile flag, the
        /// launch strips each projectile under it before the projectile is added to the world, the projectile's own proc is
        /// what the collision fires, and an equipped item's gear ratings leave and re-enter the rating cache around the strip.
        /// </summary>
        [TestMethod]
        public void The_projectile_and_rating_cache_seams_are_wired()
        {
            var root = FindRepoRoot();

            string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root, "Source", "ACE.Server" }.Concat(parts).ToArray()));

            var guard = Read("ThreadDungeons", "CombatTraitGuard.cs");
            StringAssert.Contains(guard, "creature.StripProjectileCombatTraits = true;");
            StringAssert.Contains(guard, "creature.RemoveItemFromEquippedItemsRatingCache(item);");
            StringAssert.Contains(guard, "creature.AddItemToEquippedItemsRatingCache(item);");
            Assert.IsTrue(guard.IndexOf("creature.RemoveItemFromEquippedItemsRatingCache(item);", StringComparison.Ordinal)
                < guard.IndexOf("DungeonCreatureNormalizer.StripItemCombatTraits(item)", StringComparison.Ordinal), "the cache entry must come out BEFORE the strip");
            Assert.IsTrue(guard.IndexOf("DungeonCreatureNormalizer.StripItemCombatTraits(item)", StringComparison.Ordinal)
                < guard.IndexOf("creature.AddItemToEquippedItemsRatingCache(item);", StringComparison.Ordinal), "and go back in AFTER it");

            var launch = Read("WorldObjects", "Creature_Missile.cs");
            var create = launch.IndexOf("var proj = WorldObjectFactory.CreateNewWorldObject(ammo.WeenieClassId);", StringComparison.Ordinal);
            var strip = launch.IndexOf("if (StripProjectileCombatTraits)", StringComparison.Ordinal);
            var add = launch.IndexOf("LandblockManager.AddObject(proj)", StringComparison.Ordinal);
            Assert.IsTrue(create >= 0 && strip > create && add > strip, "LaunchProjectile must strip the new projectile after creating it and before adding it to the world");
            StringAssert.Contains(launch.Substring(strip, 200), "DungeonCreatureNormalizer.StripItemCombatTraits(proj);");

            StringAssert.Contains(Read("WorldObjects", "ProjectileCollisionHelper.cs"), "worldObject.TryProcEquippedItems(sourceCreature, targetCreature, false, worldObject.ProjectileLauncher);");
        }

        // ---- damage-changing item spells --------------------------------------------------------------

        // Real retail spells seen on creature weapon items in ace_world (2026-11-23), with their stored
        // stat_Mod_Type / stat_Mod_Key. A Spell cannot be built here (it needs the dat and the world DB), so the
        // strip is driven through this table, the same shape LiveSpellStat returns.
        private const EnchantmentTypeFlags IntAura = (EnchantmentTypeFlags)33591300;    // Beneficial|Additive|SingleStat|Int
        private const EnchantmentTypeFlags FloatAura = (EnchantmentTypeFlags)33591304;  // Beneficial|Additive|SingleStat|Float
        private const EnchantmentTypeFlags FloatPlain = (EnchantmentTypeFlags)36872;    // Additive|SingleStat|Float
        private const EnchantmentTypeFlags IntPlain = (EnchantmentTypeFlags)36868;      // Additive|SingleStat|Int
        private const EnchantmentTypeFlags SkillPlain = (EnchantmentTypeFlags)36880;    // Additive|SingleStat|Skill
        private const EnchantmentTypeFlags AttributePlain = (EnchantmentTypeFlags)36865; // Additive|SingleStat|Attribute

        private static readonly System.Collections.Generic.Dictionary<int, (EnchantmentTypeFlags Type, uint Key)> SpellTable = new()
        {
            [1616] = (IntAura, 360),        // Aura of Blood Drinker Self VI  - WeaponAuraDamage, stripped
            [2598] = (IntPlain, 44),        // Minor Blood Thirst             - Damage, stripped
            [3259] = (FloatPlain, 170),     // Aura of Infected Spirit Caress - WeaponAuraElemental, stripped
            [2415] = (FloatPlain, 63),      // Eye of the Hunter              - DamageMod, stripped
            [1592] = (FloatAura, 168),      // Aura of Heart Seeker Self VI   - WeaponAuraOffense, kept
            [1625] = (IntAura, 361),        // Aura of Swift Killer Self IV   - WeaponAuraSpeed, kept
            [1605] = (FloatPlain, 169),     // Aura of Defender Self VI       - WeaponAuraDefense, kept
            [2566] = (SkillPlain, 44),      // Minor Heavy Weapon Aptitude    - Skill 44, kept (same key number as Damage)
            [1332] = (AttributePlain, 1),   // Strength Self VI               - kept
        };

        private static (EnchantmentTypeFlags Type, uint Key)? Stat(int id) => SpellTable.TryGetValue(id, out var s) ? s : null;

        private static void AddSpells(WorldObject item, params int[] ids)
        {
            foreach (var id in ids)
                item.Biota.GetOrAddKnownSpell(id, item.BiotaDatabaseLock, out _);
        }

        private static int[] Book(WorldObject item) => item.Biota.GetKnownSpellsIds(item.BiotaDatabaseLock).OrderBy(i => i).ToArray();

        [TestMethod]
        public void IsDamageSpell_classifies_the_real_retail_creature_weapon_spells()
        {
            foreach (var (id, expected) in new[] { (1616, true), (2598, true), (3259, true), (2415, true), (1592, false), (1625, false), (1605, false), (2566, false), (1332, false) })
                Assert.AreEqual(expected, DungeonCombatNormalizer.IsDamageSpell(SpellTable[id].Type, SpellTable[id].Key), $"spell {id}");
        }

        [TestMethod]
        public void A_weapon_loses_its_damage_spells_and_their_applied_enchantments_and_keeps_the_rest()
        {
            var item = Melee();
            AddSpells(item, 1616, 2598, 3259, 2415, 1592, 1625, 1605, 2566, 1332);
            var taken = new System.Collections.Generic.List<int>();

            var changes = DungeonCreatureNormalizer.StripItemDamageSpells(item, Stat, taken.Add);

            CollectionAssert.AreEquivalent(new[] { "Spell:1616", "Spell:2598", "Spell:3259", "Spell:2415" }, changes);
            CollectionAssert.AreEquivalent(new[] { 1616, 2598, 3259, 2415 }, taken, "each removed spell's applied enchantment is taken back");
            CollectionAssert.AreEqual(new[] { 1332, 1592, 1605, 1625, 2566 }, Book(item), "positive control: attack, speed, defense, skill and attribute spells survive");
        }

        [TestMethod]
        public void A_spell_that_cannot_be_resolved_is_left_alone()
        {
            var item = Caster();
            AddSpells(item, 99999);

            Assert.AreEqual(0, DungeonCreatureNormalizer.StripItemDamageSpells(item, Stat, _ => Assert.Fail("nothing to take back")).Count);
            CollectionAssert.AreEqual(new[] { 99999 }, Book(item));
        }

        [TestMethod]
        public void A_non_weapon_item_keeps_its_spells()
        {
            var robe = new GenericObject(W(31010, WeenieType.Clothing), new ObjectGuid(0x70000031));
            AddSpells(robe, 1616);

            Assert.AreEqual(0, DungeonCreatureNormalizer.StripItemDamageSpells(robe, Stat, null).Count);
            CollectionAssert.AreEqual(new[] { 1616 }, Book(robe));
        }

        [TestMethod]
        public void A_projectile_loses_its_damage_spells_with_no_enchantment_step()
        {
            var projectile = Ammo(31003, 0x70000032);
            AddSpells(projectile, 1616, 1592);

            CollectionAssert.AreEqual(new[] { "Spell:1616" }, DungeonCreatureNormalizer.StripItemDamageSpells(projectile, Stat, null));
            CollectionAssert.AreEqual(new[] { 1592 }, Book(projectile));
        }

        [TestMethod]
        public void The_item_spell_strip_is_wired_into_the_sweep_and_the_launch()
        {
            var root = FindRepoRoot();

            string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root, "Source", "ACE.Server" }.Concat(parts).ToArray()));

            var guard = Read("ThreadDungeons", "CombatTraitGuard.cs");
            StringAssert.Contains(guard, "DungeonCreatureNormalizer.StripItemDamageSpells(item, DungeonCreatureNormalizer.LiveSpellStat,");
            StringAssert.Contains(guard, "spellId => creature.RemoveItemSpell(item, (uint)spellId, silent: true)");

            StringAssert.Contains(Read("WorldObjects", "Creature_Missile.cs"), "DungeonCreatureNormalizer.StripItemDamageSpells(proj, ACE.Server.ThreadDungeons.DungeonCreatureNormalizer.LiveSpellStat, null);");
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

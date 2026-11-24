using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

using static ACE.Server.Tests.SummonerTransferFixtures;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The elemental rends on a Summoner focus reach the owner's combat pets for hits OF THAT ELEMENT only (owner ruling
    /// Q5), sized by the pet's own attack skill, through GetWeaponResistanceModifier (now taking the target, required) and
    /// the Volatile Elements overload. The golden pins hold a player's own rend values exactly as origin/master computed
    /// them (the body of GetWeaponResistanceModifier was byte-identical to origin/master before this change; values
    /// captured by running the old signature).
    /// </summary>
    [TestClass]
    public class SummonerFocusRendTests
    {
        private static uint nextGuid = 0x7E760000;
        private static uint nextWcid = 997500;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedKeys() => Seed();

        [TestCleanup]
        public void RestoreKeys() => Restore();

        private static void SetImbueShare(double f) => PropertyManager.ModifyDouble(SummonerFocus.PetImbueTransferPercentKey, f);

        private static WorldObject Item(WeenieType type, ImbuedEffectType imbue, DamageType? questType = null, double? questMod = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ImbuedEffect, (int)imbue } },
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
            };

            if (questType != null)
                weenie.PropertiesInt[PropertyInt.ResistanceModifierType] = (int)questType.Value;
            if (questMod != null)
                weenie.PropertiesFloat[PropertyFloat.ResistanceModifier] = questMod.Value;

            return type == WeenieType.Caster ? new Caster(weenie, new ObjectGuid(nextGuid++)) : new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        private static CreatureSkill SkillAt(Skill skill, uint level) => Pet(null, skill, level).GetCreatureSkill(skill);

        /// <summary>
        /// GOLDEN, from the pre-change code (whose GetWeaponResistanceModifier body equals origin/master's): a player's own
        /// rend reads, with a monster target and with no target, must not move when the target parameter is added.
        /// Skill bases: war 288, unarmed 367 (init 300 plus attributes), life 400, war 500.
        /// </summary>
        [TestMethod]
        public void Golden_player_rend_values_are_unchanged_by_the_target_parameter()
        {
            var player = PlayerTarget();
            var war288 = SkillAt(Skill.WarMagic, 288);
            var unarmed300 = SkillAt(Skill.UnarmedCombat, 300);
            var life400 = SkillAt(Skill.LifeMagic, 400);
            var war500 = SkillAt(Skill.WarMagic, 500);

            Assert.AreEqual(288, (int)war288.Base, "fixture");
            Assert.AreEqual(367, (int)unarmed300.Base, "fixture");

            foreach (var target in new Creature[] { Monster(), null })
            {
                Assert.AreEqual(2.0f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.Caster, ImbuedEffectType.FireRending), player, war288, DamageType.Fire, target), "G1");
                Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.Caster, ImbuedEffectType.FireRending), player, war288, DamageType.Cold, target), "G2");
                Assert.AreEqual(2.29375f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.MeleeWeapon, ImbuedEffectType.SlashRending), player, unarmed300, DamageType.Slash, target), "G3");
                Assert.AreEqual(2.0f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.MeleeWeapon, ImbuedEffectType.Undef, DamageType.Fire, 1.0), player, unarmed300, DamageType.Fire, target), "G4");
                Assert.AreEqual(2.5f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.Caster, ImbuedEffectType.FireRending, DamageType.Fire, 1.0), player, war500, DamageType.Fire, target), "G5");
                Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, player, war288, DamageType.Fire, target), "G6");
                Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.Caster, ImbuedEffectType.FireRending), player, null, DamageType.Fire, target), "G7");
                Assert.AreEqual(2.5f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.Caster, ImbuedEffectType.HealthRending), player, life400, DamageType.Health, target), "G8");
                Assert.AreEqual(2.0f, VolatileElementsMath.ResolveWeaponResistanceMod(Item(WeenieType.Caster, ImbuedEffectType.FireRending), player, war288, DamageType.Fire, DamageType.Cold, target), "G9");
                Assert.AreEqual(0.5f, WorldObject.GetWeaponResistanceModifier(Item(WeenieType.MeleeWeapon, ImbuedEffectType.Undef, DamageType.Fire, -0.5), player, unarmed300, DamageType.Fire, target), "G10: a below-1 quest term is never raised");
            }
        }

        [TestMethod]
        public void Fire_Rending_focus_reaches_a_caster_pets_fire_spell_and_not_its_slash_hit()
        {
            var pet = Pet(Owner(Wand(true, ImbuedEffectType.FireRending)), Skill.WarMagic, 288);
            var skill = pet.GetCreatureSkill(Skill.WarMagic);
            var monster = Monster();
            var rend = WorldObject.GetRendingMod(skill);

            Assert.AreEqual(2.0f, rend, 1e-6f, "positive control: base 288 / 144");

            Assert.AreEqual(rend, WorldObject.GetWeaponResistanceModifier(null, pet, skill, DamageType.Fire, monster), 1e-6f, "no launcher: the pet's fire spell");
            Assert.AreEqual(rend, VolatileElementsMath.ResolveWeaponResistanceMod(null, pet, skill, null, DamageType.Fire, monster), 1e-6f, "the war/void overload");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, pet, skill, DamageType.Slash, monster), "element mismatch: a Slash hit gets nothing (Q5)");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, pet, skill, DamageType.Cold, monster), "element mismatch: Cold");

            SetImbueShare(0.5);
            Assert.AreEqual(1.5f, WorldObject.GetWeaponResistanceModifier(null, pet, skill, DamageType.Fire, monster), 1e-6f, "half the bonus over 1");
            SetImbueShare(0.0);
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, pet, skill, DamageType.Fire, monster), "share 0: none");
        }

        [TestMethod]
        public void Slash_Rending_focus_reaches_a_melee_pets_weapon_hit()
        {
            var pet = Pet(Owner(Wand(true, ImbuedEffectType.SlashRending)), Skill.UnarmedCombat, 400);
            var skill = pet.GetCreatureSkill(Skill.UnarmedCombat);
            var monster = Monster();
            var weapon = Item(WeenieType.MeleeWeapon, ImbuedEffectType.Undef);
            var rend = WorldObject.GetRendingMod(skill);

            Assert.IsTrue(rend > 2.0f, $"positive control (got {rend})");
            Assert.AreEqual(rend, WorldObject.GetWeaponResistanceModifier(weapon, pet, skill, DamageType.Slash, monster), 1e-6f, "the weapon path");
            Assert.AreEqual(rend, VolatileElementsMath.ResolveWeaponResistanceMod(weapon, pet, skill, DamageType.Slash, DamageType.Fire, monster), 1e-6f, "a re-typed hit keeps the original type's rend");

            // the pet's own weapon rend is never weakened by the share
            var own = Item(WeenieType.MeleeWeapon, ImbuedEffectType.SlashRending);
            SetImbueShare(0.0);
            Assert.AreEqual(rend, WorldObject.GetWeaponResistanceModifier(own, pet, skill, DamageType.Slash, monster), 1e-6f, "own weapon rend whole at share 0");
        }

        [TestMethod]
        public void Rend_negatives()
        {
            var owner = Owner(Wand(true, ImbuedEffectType.FireRending));
            var skill = SkillAt(Skill.WarMagic, 288);
            var monster = Monster();

            Assert.AreEqual(2.0f, WorldObject.GetWeaponResistanceModifier(null, Pet(owner, Skill.WarMagic, 288), skill, DamageType.Fire, monster), 1e-6f, "positive control");

            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, Pet(owner, Skill.WarMagic, 288), skill, DamageType.Fire, PlayerTarget()), "Player target");
            Assert.AreEqual(1.0f, VolatileElementsMath.ResolveWeaponResistanceMod(null, Pet(owner, Skill.WarMagic, 288), skill, null, DamageType.Fire, PlayerTarget()), "Player target, overload");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, Pet(owner, Skill.WarMagic, 288, echo: true), skill, DamageType.Fire, monster), "Echo");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, Pet(Owner(Wand(false, ImbuedEffectType.FireRending)), Skill.WarMagic, 288), skill, DamageType.Fire, monster), "plain wand");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, Pet(owner, Skill.WarMagic, 288), null, DamageType.Fire, monster), "null skill");

            var pvpOwner = Owner(Wand(true, ImbuedEffectType.FireRending));
            pvpOwner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.AreEqual(1.0f, WorldObject.GetWeaponResistanceModifier(null, Pet(pvpOwner, Skill.WarMagic, 288), skill, DamageType.Fire, monster), "owner in a PvP match");
        }

        /// <summary>
        /// Risk R2: the rend joins the target's Vulnerability by the larger-wins rule, so a 3.1 Incantation Vulnerability
        /// leaves GetResistanceMod at the vuln whatever the focus rend; on an un-vulned target the rend shows.
        /// </summary>
        [TestMethod]
        public void A_stronger_Vulnerability_wins_over_the_focus_rend()
        {
            var pet = Pet(Owner(Wand(true, ImbuedEffectType.FireRending)), Skill.WarMagic, 360);
            var skill = pet.GetCreatureSkill(Skill.WarMagic);

            var clean = Monster();
            var vulned = Monster();
            SummonDuelBuilder.ApplyTargetDebuff(vulned, 4481, SpellCategory.FireVulnerability, 400, (EnchantmentTypeFlags)20488, (uint)PropertyFloat.ResistFire, 3.1f);

            var rend = WorldObject.GetWeaponResistanceModifier(null, pet, skill, DamageType.Fire, vulned);
            Assert.AreEqual(2.5f, rend, 1e-6f, "the full rend at the 360 cap");

            Assert.AreEqual(clean.GetResistanceMod(DamageType.Fire, pet, null, 1.0f) * 2.5f, clean.GetResistanceMod(DamageType.Fire, pet, null, rend), 1e-5f, "un-vulned: the rend multiplies in");
            Assert.AreEqual(vulned.GetResistanceMod(DamageType.Fire, pet, null, 1.0f), vulned.GetResistanceMod(DamageType.Fire, pet, null, rend), 1e-5f, "under x3.1 the vuln wins: the rend adds nothing");
            Assert.IsTrue(vulned.GetResistanceMod(DamageType.Fire, pet, null, 1.0f) > clean.GetResistanceMod(DamageType.Fire, pet, null, 1.0f) * 3.0f, "positive control: the vuln is on");
        }
    }
}

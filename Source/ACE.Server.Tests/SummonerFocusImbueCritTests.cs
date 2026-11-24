using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

using static ACE.Server.Tests.SummonerTransferFixtures;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Critical Strike and Crippling Blow imbues on a Summoner focus reach the owner's combat pets, sized by the
    /// pet's own attack skill (owner ruling Q1), through the crit helpers every hit reads (GetWeaponCriticalChance,
    /// GetWeaponMagicCritFrequency with and without a launcher, GetWeaponCritDamageMod). Each test asserts the helper's
    /// return, the value the hit consumes. The negatives: a plain wand, a Player target, an Echo, an owner in a PvP match,
    /// an unimbued focus, and a null skill.
    /// </summary>
    [TestClass]
    public class SummonerFocusImbueCritTests
    {
        private static uint nextGuid = 0x7E740000;
        private static uint nextWcid = 997300;

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

        private static CreatureSkill SkillOf(Creature c, Skill skill) => c.GetCreatureSkill(skill);

        /// <summary>A pet weapon (a melee or caster item) carrying <paramref name="imbue"/>, for the "pet's own gear" cases.</summary>
        private static WorldObject PetWeapon(WeenieType type, ImbuedEffectType imbue)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ImbuedEffect, (int)imbue } },
            };

            return type == WeenieType.Caster ? new Caster(weenie, new ObjectGuid(nextGuid++)) : new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------- Critical Strike, physical ----------------

        [TestMethod]
        public void Melee_pet_crit_chance_takes_the_focus_Critical_Strike_sized_by_its_skill()
        {
            var pet = Pet(Owner(Wand(true, ImbuedEffectType.CriticalStrike)), Skill.UnarmedCombat, 400);
            var skill = SkillOf(pet, Skill.UnarmedCombat);
            var monster = Monster();

            var bare = Pet(Owner(Wand(true)), Skill.UnarmedCombat, 400);
            var baseline = WorldObject.GetWeaponCriticalChancePreResist(null, bare, SkillOf(bare, Skill.UnarmedCombat), monster);
            var cs = WorldObject.GetCriticalStrikeMod(skill);

            Assert.AreEqual(0, pet.GetCritRating(), "fixture: no crit rating, so the chance is the imbue term alone");
            Assert.IsTrue(cs > baseline + 0.2f, $"positive control: the pet's skill sizes a real Critical Strike (cs {cs}, base {baseline})");

            var pre = WorldObject.GetWeaponCriticalChancePreResist(null, pet, skill, monster);
            Assert.AreEqual(Math.Max(baseline, cs), pre, 1e-6f, "max(default, CS(pet skill)) + rating");
            Assert.AreEqual(WorldObject.ApplyCritResist(pre, monster.GetCritResistRating()), WorldObject.GetWeaponCriticalChance(null, pet, skill, monster), 1e-6f, "the DamageEvent read");

            SetImbueShare(0.5);
            Assert.AreEqual(baseline + (cs - baseline) * 0.5f, WorldObject.GetWeaponCriticalChancePreResist(null, pet, skill, monster), 1e-6f, "half the imbue bonus");

            SetImbueShare(0.0);
            Assert.AreEqual(baseline, WorldObject.GetWeaponCriticalChancePreResist(null, pet, skill, monster), 1e-6f, "share 0: no transfer");

            SetImbueShare(double.NaN);
            Assert.AreEqual(Math.Max(baseline, cs), WorldObject.GetWeaponCriticalChancePreResist(null, pet, skill, monster), 1e-6f, "NaN reads as the full imbue");
        }

        [TestMethod]
        public void Critical_Strike_negatives()
        {
            var monster = Monster();
            var baselinePet = Pet(Owner(Wand(true)));
            var baseline = WorldObject.GetWeaponCriticalChancePreResist(null, baselinePet, SkillOf(baselinePet, Skill.UnarmedCombat), monster);

            float Read(CombatPet p, Creature t) => WorldObject.GetWeaponCriticalChancePreResist(null, p, SkillOf(p, Skill.UnarmedCombat), t);

            var imbuedOwner = Owner(Wand(true, ImbuedEffectType.CriticalStrike));
            Assert.IsTrue(Read(Pet(imbuedOwner), monster) > baseline + 0.2f, "positive control");

            Assert.AreEqual(baseline, Read(Pet(Owner(Wand(false, ImbuedEffectType.CriticalStrike))), monster), 1e-6f, "plain wand: not a focus");
            Assert.AreEqual(baseline, Read(Pet(imbuedOwner, echo: true), monster), 1e-6f, "Echo");
            Assert.AreEqual(baseline, Read(Pet(imbuedOwner), PlayerTarget()), 1e-6f, "Player target");
            Assert.AreEqual(baseline, Read(Pet(Owner(Wand(true, ImbuedEffectType.CripplingBlow))), monster), 1e-6f, "a focus with another imbue");

            var pvpOwner = Owner(Wand(true, ImbuedEffectType.CriticalStrike));
            pvpOwner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.IsTrue(pvpOwner.IsInPvpMatch, "fixture: in-match");
            Assert.AreEqual(baseline, Read(Pet(pvpOwner), monster), 1e-6f, "owner in a PvP match");

            Assert.AreEqual(baseline, WorldObject.GetWeaponCriticalChancePreResist(null, Pet(imbuedOwner), null, monster), 1e-6f, "null skill reads no imbue, and does not throw");
        }

        [TestMethod]
        public void A_pets_own_Critical_Strike_weapon_is_never_weakened_by_the_share()
        {
            var pet = Pet(Owner(Wand(true, ImbuedEffectType.CriticalStrike)));
            var skill = SkillOf(pet, Skill.UnarmedCombat);
            var monster = Monster();
            var weapon = PetWeapon(WeenieType.MeleeWeapon, ImbuedEffectType.CriticalStrike);

            var own = WorldObject.GetWeaponCriticalChancePreResist(weapon, Pet(Owner(Wand(true))), skill, monster);
            SetImbueShare(0.0);
            Assert.AreEqual(own, WorldObject.GetWeaponCriticalChancePreResist(weapon, pet, skill, monster), 1e-6f, "share 0 leaves the pet's own imbue whole");
            SetImbueShare(1.0);
            Assert.AreEqual(own, WorldObject.GetWeaponCriticalChancePreResist(weapon, pet, skill, monster), 1e-6f, "the same imbue from both sources does not stack");
        }

        // ---------------- Critical Strike, magic ----------------

        [TestMethod]
        public void Caster_pet_spell_crit_takes_the_focus_Critical_Strike_with_and_without_a_launcher()
        {
            var pet = Pet(Owner(Wand(true, ImbuedEffectType.CriticalStrike)), Skill.WarMagic, 360);
            var skill = SkillOf(pet, Skill.WarMagic);
            var monster = Monster();
            var cs = WorldObject.GetCriticalStrikeMod(skill);

            Assert.IsTrue(cs > 0.3f, $"positive control: the war skill sizes a real Critical Strike (got {cs})");

            Assert.AreEqual(Math.Max(0.05f, cs), WorldObject.GetWeaponMagicCritFrequency(null, pet, skill, monster), 1e-6f, "the no-launcher return");
            Assert.AreEqual(Math.Max(0.05f, cs), WorldObject.GetWeaponMagicCritFrequencyPreResist(null, pet, skill, monster), 1e-6f, "the pre-resist no-launcher return");

            var launcher = SummonerFocusTests.Wand(false);
            var bareLauncher = WorldObject.GetWeaponMagicCritFrequencyPreResist(launcher, Pet(Owner(Wand(true)), Skill.WarMagic, 360), skill, monster);
            Assert.AreEqual(Math.Max(bareLauncher, cs), WorldObject.GetWeaponMagicCritFrequencyPreResist(launcher, pet, skill, monster), 1e-6f, "the launcher path");

            SetImbueShare(0.5);
            Assert.AreEqual(0.05f + (cs - 0.05f) * 0.5f, WorldObject.GetWeaponMagicCritFrequency(null, pet, skill, monster), 1e-6f, "half, no launcher");
            Assert.AreEqual(bareLauncher + (cs - bareLauncher) * 0.5f, WorldObject.GetWeaponMagicCritFrequencyPreResist(launcher, pet, skill, monster), 1e-6f, "half, launcher");

            Assert.AreEqual(0.05f, WorldObject.GetWeaponMagicCritFrequency(null, Pet(Owner(Wand(true, ImbuedEffectType.CriticalStrike)), Skill.WarMagic, 360, echo: true), skill, monster), 1e-6f, "Echo");
            Assert.AreEqual(0.05f, WorldObject.GetWeaponMagicCritFrequencyPreResist(null, pet, skill, PlayerTarget()), 1e-6f, "Player target");
            Assert.AreEqual(0.05f, WorldObject.GetWeaponMagicCritFrequency(null, pet, null, monster), 1e-6f, "null skill");
        }

        // ---------------- Crippling Blow ----------------

        [TestMethod]
        public void Pet_crit_damage_takes_the_focus_Crippling_Blow_sized_by_its_skill()
        {
            var monster = Monster();

            foreach (var (attack, level) in new[] { (Skill.UnarmedCombat, 400u), (Skill.MissileWeapons, 360u), (Skill.WarMagic, 360u) })
            {
                var pet = Pet(Owner(Wand(true, ImbuedEffectType.CripplingBlow)), attack, level);
                var skill = SkillOf(pet, attack);
                var cb = WorldObject.GetCripplingBlowMod(skill);

                Assert.IsTrue(cb > 3.0f, $"positive control: {attack} sizes a real Crippling Blow (got {cb})");

                SetImbueShare(1.0);
                Assert.AreEqual(cb, WorldObject.GetWeaponCritDamageMod(null, pet, skill, monster), 1e-6f, $"{attack}: CB(pet skill)");

                SetImbueShare(0.5);
                Assert.AreEqual(1.0f + (cb - 1.0f) * 0.5f, WorldObject.GetWeaponCritDamageMod(null, pet, skill, monster), 1e-6f, $"{attack}: half the bonus over 1");

                SetImbueShare(0.0);
                Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, pet, skill, monster), 1e-6f, $"{attack}: no transfer");
            }
        }

        [TestMethod]
        public void Crippling_Blow_negatives_and_no_stacking_with_the_pets_own()
        {
            var monster = Monster();
            var owner = Owner(Wand(true, ImbuedEffectType.CripplingBlow));
            var pet = Pet(owner, Skill.WarMagic, 360);
            var skill = SkillOf(pet, Skill.WarMagic);

            Assert.IsTrue(WorldObject.GetWeaponCritDamageMod(null, pet, skill, monster) > 3.0f, "positive control");

            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, Pet(Owner(Wand(false, ImbuedEffectType.CripplingBlow)), Skill.WarMagic, 360), skill, monster), "plain wand");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, Pet(owner, Skill.WarMagic, 360, echo: true), skill, monster), "Echo");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, pet, skill, PlayerTarget()), "Player target");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, Pet(Owner(Wand(true)), Skill.WarMagic, 360), skill, monster), "unimbued focus");
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, pet, null, monster), "null skill reads no imbue, and does not throw");

            var pvpOwner = Owner(Wand(true, ImbuedEffectType.CripplingBlow));
            pvpOwner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(null, Pet(pvpOwner, Skill.WarMagic, 360), skill, monster), "owner in a PvP match");

            // the Revenant Lich case: the pet's own wand already carries Crippling Blow
            var lichWand = PetWeapon(WeenieType.Caster, ImbuedEffectType.CripplingBlow);
            var own = WorldObject.GetWeaponCritDamageMod(lichWand, Pet(Owner(Wand(true)), Skill.WarMagic, 360), skill, monster);
            Assert.AreEqual(own, WorldObject.GetWeaponCritDamageMod(lichWand, pet, skill, monster), 1e-6f, "no stacking: max of two");

            SetImbueShare(0.0);
            Assert.AreEqual(own, WorldObject.GetWeaponCritDamageMod(lichWand, pet, skill, monster), 1e-6f, "the pet's own Crippling Blow is never weakened");
        }
    }
}

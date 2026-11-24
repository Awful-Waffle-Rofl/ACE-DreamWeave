using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.SummonerTransferFixtures;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Armor Rending on a Summoner focus reaches the owner's melee and missile combat pets through
    /// WorldObject.GetArmorRendingModFor, the one helper DamageEvent and the simulator's AttackerTerms.ArmorIgnoreMod now
    /// share. A caster pet's skill has no Armor Rending formula, so it reads none. The golden pins hold the pre-change
    /// values of AttackerTerms.ArmorIgnoreMod (whose rending block was the same formula as DamageEvent's, and both
    /// identical to origin/master), captured by running the old code.
    /// </summary>
    [TestClass]
    public class SummonerFocusArmorRendingTests
    {
        private static uint nextGuid = 0x7E780000;
        private static uint nextWcid = 997700;

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

        private static WorldObject Item(WeenieType type, ImbuedEffectType imbue)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ImbuedEffect, (int)imbue } },
            };

            return type == WeenieType.Caster ? new Caster(weenie, new ObjectGuid(nextGuid++))
                : type == WeenieType.MissileLauncher ? new MissileLauncher(weenie, new ObjectGuid(nextGuid++))
                : new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>GOLDEN (pre-change run): an Armor Rending weapon at melee 400 (base 467, capped 400), melee 250 (base 317), missile 360, missile 200, magic 360, and unimbued.</summary>
        [TestMethod]
        public void Golden_weapon_armor_rending_values_are_unchanged()
        {
            var player = PlayerTarget();

            foreach (var target in new Creature[] { Monster(), null })
            {
                foreach (var (sk, level, type, expected) in new[]
                {
                    (Skill.UnarmedCombat, 400u, WeenieType.MeleeWeapon, 0.39999998f),
                    (Skill.UnarmedCombat, 250u, WeenieType.MeleeWeapon, 0.60749996f),
                    (Skill.MissileWeapons, 360u, WeenieType.MissileLauncher, 0.39999998f),
                    (Skill.MissileWeapons, 200u, WeenieType.MissileLauncher, 0.84444445f),
                    (Skill.WarMagic, 360u, WeenieType.Caster, 1.0f),
                })
                {
                    var skill = Pet(null, sk, level).GetCreatureSkill(sk);

                    Assert.AreEqual(expected, AttackerTerms.ArmorIgnoreMod(Item(type, ImbuedEffectType.ArmorRending), player, skill, target), $"{sk} {level} imbued");
                    Assert.AreEqual(expected, WorldObject.GetArmorRendingModFor(Item(type, ImbuedEffectType.ArmorRending), player, skill, target), $"{sk} {level} helper");
                    Assert.AreEqual(1.0f, AttackerTerms.ArmorIgnoreMod(Item(type, ImbuedEffectType.Undef), player, skill, target), $"{sk} {level} unimbued");
                    Assert.AreEqual(1.0f, AttackerTerms.ArmorIgnoreMod(null, player, skill, target), $"{sk} {level} no weapon");
                }
            }
        }

        [TestMethod]
        public void Armor_Rending_focus_reaches_melee_and_missile_pets_and_not_a_caster_pet()
        {
            var monster = Monster();
            var owner = Owner(Wand(true, ImbuedEffectType.ArmorRending));

            foreach (var (sk, level, expected) in new[] { (Skill.UnarmedCombat, 400u, 0.39999998f), (Skill.MissileWeapons, 360u, 0.39999998f), (Skill.WarMagic, 360u, 1.0f) })
            {
                var pet = Pet(owner, sk, level);
                var skill = pet.GetCreatureSkill(sk);

                SetImbueShare(1.0);
                Assert.AreEqual(expected, WorldObject.GetArmorRendingModFor(null, pet, skill, monster), 1e-6f, $"{sk}: the DamageEvent read");
                Assert.AreEqual(expected, AttackerTerms.ArmorIgnoreMod(null, pet, skill, monster), 1e-6f, $"{sk}: the simulator read");

                SetImbueShare(0.5);
                Assert.AreEqual(1.0f - (1.0f - expected) * 0.5f, WorldObject.GetArmorRendingModFor(null, pet, skill, monster), 1e-6f, $"{sk}: half the armor ignored");

                SetImbueShare(0.0);
                Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, pet, skill, monster), $"{sk}: share 0");
            }
        }

        [TestMethod]
        public void Armor_Rending_negatives_and_the_pets_own_weapon()
        {
            var monster = Monster();
            var owner = Owner(Wand(true, ImbuedEffectType.ArmorRending));
            var skillPet = Pet(owner);
            var skill = skillPet.GetCreatureSkill(Skill.UnarmedCombat);

            Assert.AreEqual(0.39999998f, WorldObject.GetArmorRendingModFor(null, skillPet, skill, monster), 1e-6f, "positive control");

            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, Pet(owner), skill, PlayerTarget()), "Player target");
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, Pet(owner, echo: true), skill, monster), "Echo");
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, Pet(Owner(Wand(false, ImbuedEffectType.ArmorRending))), skill, monster), "plain wand");
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, Pet(Owner(Wand(true))), skill, monster), "unimbued focus");
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, Pet(owner), null, monster), "null skill reads no imbue, and does not throw");

            var pvpOwner = Owner(Wand(true, ImbuedEffectType.ArmorRending));
            pvpOwner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingModFor(null, Pet(pvpOwner), skill, monster), "owner in a PvP match");

            var own = Item(WeenieType.MeleeWeapon, ImbuedEffectType.ArmorRending);
            SetImbueShare(0.0);
            Assert.AreEqual(0.39999998f, WorldObject.GetArmorRendingModFor(own, skillPet, skill, monster), 1e-6f, "the pet's own Armor Rending weapon is never weakened");
        }
    }
}

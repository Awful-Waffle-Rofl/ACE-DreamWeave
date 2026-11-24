using System;
using System.Collections.Generic;
using System.IO;

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
    /// A caster summon's war and void bolts read its wand (owner decision 2026-11-02, the Revenant Lich's Crippling Blow
    /// wand). Monster_Magic.CastSpellAt hands HandleCastSpell the weapon that becomes SpellProjectile.ProjectileLauncher;
    /// WarVoidSpellLauncher gives a caster pet (CombatPet with IsCasterPet) its wand there and every other monster,
    /// a melee or missile pet included, null, exactly as before, so an ordinary monster caster's spell damage stays
    /// bit-identical. The call site is pinned on source (single code-line match) because it needs a live cast. The crit
    /// frequency test reaches WeaponModRegistry.Enabled through GetWeaponMagicCritFrequency, so the default properties
    /// are loaded once; no key is modified.
    /// </summary>
    [TestClass]
    public class CasterPetWandLauncherTests
    {
        private static uint nextGuid = 0x7F2D0000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static Weenie Fixture(WeenieType type, string name)
        {
            return new Weenie
            {
                WeenieClassId = 43,
                WeenieType = type,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
            };
        }

        private static Caster CripplingBlowWand()
        {
            TestGameTables.EnsureInitialized();

            var weenie = Fixture(WeenieType.Caster, "Launcher Test Wand");
            weenie.PropertiesInt[PropertyInt.ImbuedEffect] = (int)ImbuedEffectType.CripplingBlow;

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        private static CombatPet Pet(bool casterBrain = true)
        {
            TestGameTables.EnsureInitialized();

            var weenie = Fixture(WeenieType.CombatPet, "Launcher Test Pet");

            if (casterBrain)
                weenie.PropertiesBool[PropertyBool.SummonCasterBrain] = true;

            return new CombatPet(weenie, new ObjectGuid(nextGuid++));
        }

        private static Creature Monster()
        {
            TestGameTables.EnsureInitialized();
            return new Creature(Fixture(WeenieType.Creature, "Launcher Test Monster"), new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void CombatPet_LaunchesWithItsWand()
        {
            var wand = CripplingBlowWand();
            Assert.AreSame(wand, Creature.WarVoidSpellLauncher(Pet(), wand));
        }

        [TestMethod]
        public void OrdinaryMonster_LaunchesWithNoWeapon_EvenWithAWand()
        {
            Assert.IsNull(Creature.WarVoidSpellLauncher(Monster(), CripplingBlowWand()),
                "a non-pet monster's war/void bolt must keep a null launcher, as before the change");
        }

        [TestMethod]
        public void NonCasterCombatPet_LaunchesWithNoWeapon_EvenWithAWand()
        {
            var pet = Pet(casterBrain: false);
            Assert.IsFalse(pet.IsCasterPet, "the fixture must be a plain (melee or missile) pet");
            Assert.IsNull(Creature.WarVoidSpellLauncher(pet, CripplingBlowWand()), "only a caster pet's bolts read its wand");
        }

        /// <summary>
        /// The intended side effect (owner ruling 2026-11-02): with the wand as launcher the pet's dagger Crit rating
        /// reaches its bolts - default magic crit 0.05 plus 20 points x 0.01 = 0.25. With the null launcher the
        /// function returns the flat 0.05 before it reads the rating.
        /// </summary>
        [TestMethod]
        public void CasterPet_CritRating_ReachesItsBolts_ThroughTheWand()
        {
            var wand = CripplingBlowWand();
            var pet = Pet();
            pet.CritRating = 20;
            var target = Monster();
            var skill = pet.GetCreatureSkill(Skill.WarMagic);

            Assert.AreEqual(0.25f, WorldObject.GetWeaponMagicCritFrequency(Creature.WarVoidSpellLauncher(pet, wand), pet, skill, target), 1e-6f);
            Assert.AreEqual(0.05f, WorldObject.GetWeaponMagicCritFrequency(null, pet, skill, target), 1e-6f, "control: no launcher, no rating");
        }

        [TestMethod]
        public void CombatPet_WithNoWand_LaunchesWithNoWeapon() => Assert.IsNull(Creature.WarVoidSpellLauncher(Pet(), null));

        /// <summary>
        /// The launcher is what carries the imbue into the damage: SpellProjectile's crit multiplier is
        /// GetWeaponCritDamageMod(ProjectileLauncher, ...). With the wand it is the Crippling Blow mod (base skill / 60,
        /// at least 1.0); with the null launcher an ordinary monster gets, it is the flat 1.0 default.
        /// </summary>
        [TestMethod]
        public void TheLauncher_CarriesCripplingBlow_IntoTheCritMultiplier()
        {
            var wand = CripplingBlowWand();
            var pet = Pet();
            var monster = Monster();
            var target = Monster();

            var skill = pet.GetCreatureSkill(Skill.WarMagic);
            skill.InitLevel = 300;

            var expected = WorldObject.GetCripplingBlowMod(skill);
            Assert.IsTrue(expected > 1.0f, $"the fixture skill must give a Crippling Blow mod above 1.0, got {expected}");

            Assert.AreEqual(expected, WorldObject.GetWeaponCritDamageMod(Creature.WarVoidSpellLauncher(pet, wand), pet, skill, target), 1e-6f);

            var monsterSkill = monster.GetCreatureSkill(Skill.WarMagic);
            monsterSkill.InitLevel = 300;

            Assert.AreEqual(1.0f, WorldObject.GetWeaponCritDamageMod(Creature.WarVoidSpellLauncher(monster, wand), monster, monsterSkill, target), 0f);
        }

        [TestMethod]
        public void WarVoidCase_PassesTheLauncher()
        {
            const string file = "ACE.Server/WorldObjects/Monster_Magic.cs";
            Pin(file, "HandleCastSpell(spell, target, caster, WarVoidSpellLauncher(this, caster));", 1);
            Pin(file, "HandleCastSpell(spell, target, caster);", 0);
            Pin(file, "return castingCreature is CombatPet { IsCasterPet: true } ? wand : null;", 1);
        }

        private static void Pin(string file, string code, int expected)
        {
            var path = Path.Combine(FindSourceRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var hits = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var codeOnly = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (codeOnly == code)
                    hits++;
            }

            Assert.AreEqual(expected, hits, $"expected exactly {expected} code line(s) in {file} matching `{code}`");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.sln by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}

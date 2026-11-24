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
    /// Focus spell damage for caster summons (owner ruling 2026-11-02): a caster pet (the Ossuary casters and the Revenant
    /// Lich) earns magic_damage_per_focus_point from its OWN buffed Focus; an ordinary monster caster and a melee or
    /// missile pet earn exactly nothing. The four damage sites are pinned on source (single code-line match, the
    /// FocusDotWiringPinTests style) because they need a live projectile or a live enchantment tick.
    /// magic_damage_per_focus_point is seeded in every test and restored afterwards.
    /// </summary>
    [TestClass]
    public class CasterPetFocusSpellDamageTests
    {
        private const float Epsilon = 1e-5f;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed() => Restore();

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyDouble("magic_damage_per_focus_point",
                DefaultPropertyManager.DefaultDoubleProperties["magic_damage_per_focus_point"].Item);
        }

        private static uint nextGuid = 0x7F2C0000;

        private static Weenie FocusWeenie(WeenieType type, uint focus, bool casterBrain)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 42,
                WeenieType = type,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Focus Test" } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Focus, new PropertiesAttribute { InitLevel = focus } },
                },
            };

            if (casterBrain)
                weenie.PropertiesBool[PropertyBool.SummonCasterBrain] = true;

            return weenie;
        }

        private static CombatPet Pet(uint focus, bool casterBrain)
        {
            TestGameTables.EnsureInitialized();
            return new CombatPet(FocusWeenie(WeenieType.CombatPet, focus, casterBrain), new ObjectGuid(nextGuid++));
        }

        private static Creature MonsterCaster(uint focus)
        {
            TestGameTables.EnsureInitialized();
            return new Creature(FocusWeenie(WeenieType.Creature, focus, casterBrain: false), new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void CasterPet_EarnsTheBonus_FromItsOwnFocus()
        {
            PropertyManager.ModifyDouble("magic_damage_per_focus_point", 0.001);

            var pet = Pet(focus: 500, casterBrain: true);
            Assert.IsTrue(pet.IsCasterPet);
            Assert.AreEqual(500u, pet.Focus.Current, "the fixture's Focus reaches the attribute");

            Assert.AreEqual(1.5f, pet.GetFocusSpellDamageMod(), Epsilon, "1 + 500 x 0.001");
            Assert.AreEqual(1.5f, CombatPet.FocusSpellDamageModFor(pet), Epsilon, "the site helper answers the same for a caster pet");
        }

        [TestMethod]
        public void CasterPet_FollowsTheTunable_AndZeroDisablesIt()
        {
            var pet = Pet(focus: 300, casterBrain: true);

            PropertyManager.ModifyDouble("magic_damage_per_focus_point", 0.002);
            Assert.AreEqual(1.6f, CombatPet.FocusSpellDamageModFor(pet), Epsilon);

            PropertyManager.ModifyDouble("magic_damage_per_focus_point", 0.0);
            Assert.AreEqual(1.0f, CombatPet.FocusSpellDamageModFor(pet), 0f, "the player's off switch is the pet's off switch");
        }

        [TestMethod]
        public void NonPetMonsterCaster_GetsNoFocusBonus()
        {
            PropertyManager.ModifyDouble("magic_damage_per_focus_point", 0.001);

            var monster = MonsterCaster(focus: 500);
            Assert.AreEqual(500u, monster.Focus.Current, "the monster really has the Focus, so 1.0 is the exclusion and not a zero stat");
            Assert.AreEqual(1.0f, CombatPet.FocusSpellDamageModFor(monster), 0f);
        }

        [TestMethod]
        public void NonCasterPet_GetsNoFocusBonus()
        {
            PropertyManager.ModifyDouble("magic_damage_per_focus_point", 0.001);

            var pet = Pet(focus: 500, casterBrain: false);
            Assert.IsFalse(pet.IsCasterPet);
            Assert.AreEqual(1.0f, CombatPet.FocusSpellDamageModFor(pet), 0f, "a melee or missile pet is not a caster summon");
        }

        [TestMethod]
        public void NullSource_GetsNoFocusBonus() => Assert.AreEqual(1.0f, CombatPet.FocusSpellDamageModFor(null), 0f);

        // ---- the damage sites, pinned on source ----

        [TestMethod]
        public void LifeProjectile_GatesTheCasterPetBranch()
        {
            const string file = "ACE.Server/WorldObjects/SpellProjectile.cs";
            Pin(file, "else if (sourceCreature is CombatPet { IsCasterPet: true } && targetPlayer == null", 1);
            Pin(file, "&& Player.QualifiesForFocusSpellDamage(IncomingDamageOrigin == IncomingDamageOrigin.DirectHit, false, FromProc, IsClassAbilityProc))", 2);
            Pin(file, "finalDamage *= CombatPet.FocusSpellDamageModFor(sourceCreature);", 2);
        }

        [TestMethod]
        public void WarVoidProjectile_GatesTheCasterPetBranch() => Pin(
            "ACE.Server/WorldObjects/SpellProjectile.cs",
            "else if (sourceCreature is CombatPet { IsCasterPet: true } && target is not Player", 1);

        [TestMethod]
        public void Harm_GatesTheCasterPetBranch()
        {
            const string file = "ACE.Server/WorldObjects/WorldObject_Magic.cs";
            Pin(file, "else if (isHarmfulLifeBoost && this is CombatPet { IsCasterPet: true } focusPet", 1);
            Pin(file, "&& Player.QualifiesForFocusSpellDamage(true, targetCreature is Player, fromProc, false))", 1);
            Pin(file, "lifeClassAbilityMod *= focusPet.GetFocusSpellDamageMod();", 1);
        }

        [TestMethod]
        public void DotTick_GatesTheCasterPetBranch()
        {
            const string file = "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs";
            Pin(file, "else if (damager is CombatPet { IsCasterPet: true } focusPet && targetPlayer == null", 1);
            Pin(file, "&& ShouldApplyFocusDotMod(true, false, aetheria, ProcDotMarks.IsProcSourced(enchantment)))", 1);
            Pin(file, "tickAmount *= focusPet.GetFocusSpellDamageMod();", 1);
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

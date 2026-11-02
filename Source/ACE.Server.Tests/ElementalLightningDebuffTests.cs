using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Elemental Proficiency Lightning (Archmage T3): Creature.TryApplyElementalLightning and its read side,
    /// Creature.GetElementalLightningMod, multiplied into Creature.GetResistanceMod beside Hunter's Mark.
    ///
    /// Reads no PropertyManager key: every magnitude and duration is passed explicitly, so this class needs no
    /// seeding and is safe to run alone or in any order.
    /// </summary>
    [TestClass]
    public class ElementalLightningDebuffTests
    {
        private const float Tolerance = 1e-5f;

        private static uint nextCasterGuid = 0x70000900;

        private static WorldObject CreateBareWorldObject()
        {
            var biota = new Biota
            {
                Id = nextCasterGuid++,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        private static Creature LiveTarget()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 50);

            Assert.IsFalse(target.IsDead, "the fixture target must be alive, or a refusal proves nothing");

            return target;
        }

        // ---- merge / refresh -------------------------------------------------------------------------

        [TestMethod]
        public void FirstApplication_IsANewLanding_RefreshIsNot()
        {
            var target = LiveTarget();

            Assert.AreEqual(1.0f, target.GetElementalLightningMod(100.0), 0.0f, "untouched creature reads exactly 1.0");

            Assert.IsTrue(target.TryApplyElementalLightning(0.2, 10.0, 100.0), "first application is a new landing");
            Assert.IsFalse(target.TryApplyElementalLightning(0.2, 10.0, 105.0), "a refresh of a live debuff is not");

            Assert.AreEqual(1.2f, target.GetElementalLightningMod(105.0), Tolerance);
        }

        [TestMethod]
        public void Refresh_KeepsTheStrongerBonus_AndTheLaterEnd()
        {
            var target = LiveTarget();

            Assert.IsTrue(target.TryApplyElementalLightning(0.3, 10.0, 100.0));   // ends 110

            // weaker but later: the 0.3 bonus stays, the end moves out to 115
            Assert.IsFalse(target.TryApplyElementalLightning(0.1, 10.0, 105.0));

            Assert.AreEqual(1.3f, target.GetElementalLightningMod(112.0), Tolerance, "a weaker hit must not downgrade");
            Assert.AreEqual(1.3f, target.GetElementalLightningMod(114.9), Tolerance, "the later end time wins");
            Assert.AreEqual(1.0f, target.GetElementalLightningMod(115.0), 0.0f, "and the debuff lapses at that end");
        }

        [TestMethod]
        public void Refresh_StrongerButShorter_UpgradesTheBonus_WithoutShorteningTheWindow()
        {
            var target = LiveTarget();

            Assert.IsTrue(target.TryApplyElementalLightning(0.1, 20.0, 100.0));   // ends 120
            Assert.IsFalse(target.TryApplyElementalLightning(0.3, 5.0, 101.0));   // would end 106

            Assert.AreEqual(1.3f, target.GetElementalLightningMod(110.0), Tolerance, "stronger bonus is taken");
            Assert.AreEqual(1.3f, target.GetElementalLightningMod(119.0), Tolerance, "window not shortened");
        }

        [TestMethod]
        public void TwoCasters_ShareOneDebuff_NotOnePerCaster()
        {
            // ONE debuff per target (owner ruling): two applications never multiply with each other
            var target = LiveTarget();

            target.TryApplyElementalLightning(0.2, 10.0, 100.0);
            target.TryApplyElementalLightning(0.2, 10.0, 100.0);

            Assert.AreEqual(1.2f, target.GetElementalLightningMod(101.0), Tolerance, "not 1.2 x 1.2 and not 1.4");
        }

        [TestMethod]
        public void AfterLapse_TheNextApplicationIsANewLandingAgain()
        {
            var target = LiveTarget();

            Assert.IsTrue(target.TryApplyElementalLightning(0.3, 10.0, 100.0));
            Assert.AreEqual(1.0f, target.GetElementalLightningMod(110.0), 0.0f, "lapsed");

            // a weak application after the lapse is a fresh landing at its own value - the lapsed 0.3 does not linger
            Assert.IsTrue(target.TryApplyElementalLightning(0.1, 10.0, 111.0));
            Assert.AreEqual(1.1f, target.GetElementalLightningMod(112.0), Tolerance);
        }

        // ---- refusals --------------------------------------------------------------------------------

        [TestMethod]
        public void NonPositiveBonusOrDuration_IsRefused()
        {
            var target = LiveTarget();

            Assert.IsFalse(target.TryApplyElementalLightning(0.0, 10.0, 100.0));
            Assert.IsFalse(target.TryApplyElementalLightning(-0.1, 10.0, 100.0));
            Assert.IsFalse(target.TryApplyElementalLightning(0.2, 0.0, 100.0));
            Assert.IsFalse(target.TryApplyElementalLightning(0.2, -1.0, 100.0));

            Assert.AreEqual(1.0f, target.GetElementalLightningMod(100.0), 0.0f);
        }

        [TestMethod]
        public void DeadCreature_IsRefused()
        {
            var target = LiveTarget();
            target.Health.Current = 0;

            Assert.IsTrue(target.IsDead, "control: the fixture is dead");
            Assert.IsFalse(target.TryApplyElementalLightning(0.2, 10.0, 100.0));
            Assert.AreEqual(1.0f, target.GetElementalLightningMod(100.0), 0.0f);
        }

        [TestMethod]
        public void GamePiece_IsRefused()
        {
            var piece = ElementalProficiencyTestFixtures.LiveGamePiece();

            Assert.IsFalse(piece.TryApplyElementalLightning(0.2, 10.0, 100.0), "IsEligibleTarget refuses a GamePiece");
            Assert.IsFalse(piece.TryApplyElementalLightning(0.2, 10.0), "and through the public clock too");
            Assert.AreEqual(1.0f, piece.GetElementalLightningMod(100.0), 0.0f);
        }

        // ---- composition in GetResistanceMod ---------------------------------------------------------

        [TestMethod]
        public void Composition_MultipliesIntoGetResistanceMod()
        {
            var target = LiveTarget();

            Assert.AreEqual(1.0f, target.GetResistanceMod(DamageType.Fire, null, null), Tolerance, "control before");

            Assert.IsTrue(target.TryApplyElementalLightning(0.2, 60.0));

            Assert.AreEqual(1.2f, target.GetResistanceMod(DamageType.Fire, null, null), Tolerance);
            Assert.AreEqual(1.2f, target.GetResistanceMod(DamageType.Slash, null, null), Tolerance, "element independent");
            Assert.AreEqual(1.2, target.GetResistanceMod(ResistanceType.Electric, null, null), 1e-4, "ResistanceType overload");
        }

        [TestMethod]
        public void Composition_StacksMultiplicativelyWithHuntersMark()
        {
            var target = LiveTarget();

            HuntersMarkAbility.ApplyMark(target, CreateBareWorldObject(), 0.05, 60.0);
            Assert.IsTrue(target.TryApplyElementalLightning(0.2, 60.0));

            Assert.AreEqual(1.05f * 1.2f, target.GetResistanceMod(DamageType.Pierce, null, null), Tolerance);
        }

        [TestMethod]
        public void Composition_StacksWithTheVulnerabilityVersusWeaponMax()
        {
            var target = LiveTarget();

            AddRetailPierceVulnerability(target, 1.6f);
            Assert.IsTrue(target.TryApplyElementalLightning(0.2, 60.0));

            Assert.AreEqual(1.6f * 1.2f, target.GetResistanceMod(DamageType.Pierce, null, null), Tolerance);
            Assert.AreEqual(Math.Max(1.6f, 2.5f) * 1.2f, target.GetResistanceMod(DamageType.Pierce, null, null, 2.5f), Tolerance);
        }

        [TestMethod]
        public void Composition_IgnoreMagicResistEarlyReturn_CarriesLightning()
        {
            var target = LiveTarget();
            var hollowWeapon = CreateBareWorldObject();
            hollowWeapon.IgnoreMagicResist = true;

            Assert.IsTrue(target.TryApplyElementalLightning(0.2, 60.0));

            Assert.AreEqual(2.5f * 1.2f, target.GetResistanceMod(DamageType.Slash, null, hollowWeapon, 2.5f), Tolerance);
        }

        /// <summary>
        /// The HealthDrain case of GetResistanceMod(ResistanceType, ...) reads PropertyManager and is not run here,
        /// so a source count is its guard: Lightning must ride on exactly the three lines that carry Hunter's Mark.
        /// </summary>
        [TestMethod]
        public void CreatureProperties_LightningRidesEveryHuntersMarkSite()
        {
            var path = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Creature_Properties.cs");

            Assert.IsTrue(File.Exists(path), $"could not locate {path}");

            var code = File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")).ToList();

            var lightningLines = code.Where(line => Regex.IsMatch(line, @"GetElementalLightningMod\(\)")).ToList();
            var markLines = code.Where(line => Regex.IsMatch(line, @"GetHuntersMarkMod\(\)")).ToList();

            Assert.AreEqual(3, markLines.Count, "control: the three Hunter's Mark sites are still there");
            Assert.AreEqual(3, lightningLines.Count, "Lightning must multiply in at all three sites");
            CollectionAssert.AreEqual(markLines, lightningLines, "each Lightning factor sits on a Hunter's Mark line");
        }

        private static void AddRetailPierceVulnerability(Creature creature, float value)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)SpellId.PiercingVulnerabilityOther6,
                SpellCategory = SpellCategory.PierceVulnerability,
                LayerId = 1,
                PowerLevel = 250,
                StartTime = 0,
                Duration = 60.0,
                StatModType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative,
                StatModKey = (uint)PropertyFloat.ResistPierce,
                StatModValue = value,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            creature.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, creature.BiotaDatabaseLock);
        }
    }

    /// <summary>Fixtures shared by the three Elemental Proficiency target-state test classes.</summary>
    internal static class ElementalProficiencyTestFixtures
    {
        /// <summary>
        /// A LIVE chess piece - alive on purpose, so a refusal is attributable to the GamePiece exclusion in
        /// ElementalProficiencyMath.IsEligibleTarget and not to the dead-creature check.
        /// </summary>
        public static GamePiece LiveGamePiece()
        {
            var wcid = TestCreatures.NextWcid();

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.GamePiece,
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 50 } },
                },
            };

            var piece = new GamePiece(weenie, new ObjectGuid(0x7D000000 + wcid));

            Assert.IsFalse(piece.IsDead, "the fixture piece must be alive, or a refusal proves nothing");

            return piece;
        }
    }
}

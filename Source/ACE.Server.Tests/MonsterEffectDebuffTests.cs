using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DebuffEffect: applies an Imperil/Vulnerability/attack-skills debuff through
    /// EnchantmentManager.AddClassAbilityDebuff. Every variant writes a hand-made enchantment entry directly
    /// (no resist check, no live Spell/dat lookup), so it is fully exercisable through the real dispatch
    /// site on a bare non-Player Creature - the same shape MonsterEffectDispelTests uses.
    /// </summary>
    [TestClass]
    public class MonsterEffectDebuffTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_debuff_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_debuff_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_debuff_vuln_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_debuff_vuln_cap"].Item);
        }

        private static readonly DebuffEffect Effect = new DebuffEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"debuff {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("what=imperil mag=20 secs=15"), out _));
            Assert.IsTrue(Effect.Validate(Spec("what=vuln mag=0.3 secs=15 chance=0.5"), out _));
            Assert.IsTrue(Effect.Validate(Spec("what=attackskills mag=30 secs=20"), out _));

            Assert.IsFalse(Effect.Validate(Spec("mag=20 secs=15"), out var error), "what= is required");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("what=bogus mag=20 secs=15"), out _), "what= must be a known token");
            Assert.IsFalse(Effect.Validate(Spec("what=imperil mag=0 secs=15"), out _), "mag= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("what=imperil mag=20 secs=0"), out _), "secs= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("what=imperil mag=20 secs=15 chance=1.5"), out _), "chance= must be <= 1");
        }

        [TestMethod]
        public void Magnitude_AttackSkillsAppliesAFlatNegativeDebuffOnALandedHit()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("what=attackskills mag=30 secs=20 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count);

            var entry = defender.Biota.PropertiesEnchantmentRegistry.Single();

            Assert.AreEqual(EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills, entry.StatModType);
            Assert.AreEqual(-30.0f, entry.StatModValue);
            Assert.AreEqual(20.0, entry.Duration);
        }

        [TestMethod]
        public void Magnitude_VulnMatchesTheLandedHitsOwnDamageType()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("what=vuln mag=0.5 secs=15 chance=1.0"))));

            // CreateAttacker's single body part deals Slash - ApplyVuln must key off THAT type, not an
            // authored one, so the entry lands on ResistSlash
            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee, DamageType = DamageType.Slash };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count);

            var entry = defender.Biota.PropertiesEnchantmentRegistry.Single();

            Assert.AreEqual(EnchantmentTypeFlags.Float | EnchantmentTypeFlags.Multiplicative, entry.StatModType);
            Assert.AreEqual((uint)PropertyFloat.ResistSlash, entry.StatModKey);
            Assert.AreEqual(1.5f, entry.StatModValue);
        }

        /// <summary>
        /// mag= is the applied StatModValue directly (see the handler's own doc comment), so an authored
        /// magnitude with no server ceiling is a one-typo boss-breaker: "what=vuln mag=50" is an easy slip
        /// for 0.5 and would otherwise apply a 51x resist multiplier for the whole authored duration.
        ///
        /// BOTH UNITS, because mag= is not one thing. imperil and attackskills spend it as points removed
        /// and answer to monster_effect_debuff_cap; vuln adds it to a resist multiplier and answers to
        /// monster_effect_debuff_vuln_cap. Asserted against the enchantment that actually reached the target,
        /// not against the clamp arithmetic, so a future refactor that clamps in the wrong place fails here.
        /// </summary>
        [TestMethod]
        public void CapClamp_MagIsClampedPerShapeHoweverHighItIsAuthored()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_debuff_cap", 40.0));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_debuff_vuln_cap", 0.75));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee, DamageType = DamageType.Slash };

            // points shape: an absurd armor strip is clamped to the points cap
            var imperilAttacker = TestCreatures.CreateAttacker();
            var imperilDefender = TestCreatures.CreateDefender();

            imperilAttacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("what=imperil mag=100000 secs=15 chance=1.0"))));

            imperilAttacker.ApplyOutgoingHitMonsterEffects(imperilDefender, damageEvent);

            Assert.AreEqual(-40.0f, imperilDefender.Biota.PropertiesEnchantmentRegistry.Single().StatModValue,
                "an armor strip of 100000 must be clamped to monster_effect_debuff_cap");

            // points shape, the other one
            var skillsAttacker = TestCreatures.CreateAttacker();
            var skillsDefender = TestCreatures.CreateDefender();

            skillsAttacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("what=attackskills mag=9000 secs=20 chance=1.0"))));

            skillsAttacker.ApplyOutgoingHitMonsterEffects(skillsDefender, damageEvent);

            Assert.AreEqual(-40.0f, skillsDefender.Biota.PropertiesEnchantmentRegistry.Single().StatModValue,
                "an attack-skill strip of 9000 must be clamped to the same points cap");

            // fraction shape: the mag=50 typo, which the points cap would have waved straight through
            var vulnAttacker = TestCreatures.CreateAttacker();
            var vulnDefender = TestCreatures.CreateDefender();

            vulnAttacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("what=vuln mag=50 secs=30 chance=1.0"))));

            vulnAttacker.ApplyOutgoingHitMonsterEffects(vulnDefender, damageEvent);

            Assert.AreEqual(1.75f, vulnDefender.Biota.PropertiesEnchantmentRegistry.Single().StatModValue,
                "mag=50 must land as 1 + the vuln cap (1.75x), never as a 51x multiplier");
        }

        [TestMethod]
        public void CapClamp_ChanceIsClampedByTheProcChanceCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 0.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("what=attackskills mag=30 secs=20 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(0, defender.Biota.PropertiesEnchantmentRegistry.Count, "a cap of zero must let nothing through");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheDebuff()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("what=attackskills mag=30 secs=20 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(0, defender.Biota.PropertiesEnchantmentRegistry.Count, "the master switch must stop the debuff entirely");
        }
    }
}

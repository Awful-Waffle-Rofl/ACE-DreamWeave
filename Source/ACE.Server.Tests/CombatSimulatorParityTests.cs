using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    using CombatSimulator = ACE.Server.CombatSimulator.CombatSimulator;

    /// <summary>
    /// Pins the simulator's composed mitigation product against the engine's own
    /// DamageEvent.CalculateDamage. The simulator calls production factor functions, so no
    /// factor is reimplemented - but the ORDER and GROUPING of the final multiply is, and that
    /// is what drifts. Without this test the simulator degrades silently into producing
    /// plausible wrong numbers.
    /// </summary>
    [TestClass]
    public class CombatSimulatorParityTests
    {
        private const float Epsilon = 0.001f;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void ArmorMod_MatchesEngineForKnownArmorLevel()
        {
            // the engine's own decomposition, asserted by DamageEventTests, is
            // baseDamage x attributeMod x armorMod - so armorMod from a known armor level is
            // directly comparable
            var attacker = TestCreatures.CreateAttacker(maxDamage: 10, variance: 0.5f, strength: 100);
            var defender = TestCreatures.CreateDefender(baseArmor: 200);

            var engineEvent = DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.IsFalse(engineEvent.Evaded);

            var profile = new DefenderProfile
            {
                Label = "parity",
                MaxHealth = 100,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, 0u } },
                Armor = new Dictionary<ArmorKey, ArmorRow>
                {
                    { new ArmorKey(BodyPart.Chest, DamageType.Slash, false, false), new ArmorRow(200.0f) },
                },
                Resistance = new Dictionary<DamageType, ResistancePair>(),
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Melee, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };

            var spec = new AttackerSpec { DamageType = DamageType.Slash };

            Assert.AreEqual(engineEvent.ArmorMod, MitigationMath.ArmorMod(profile, spec, BodyPart.Chest), Epsilon);
        }

        [TestMethod]
        public void ExpectedDamage_MatchesEngineMeanOverManyRolls()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 10, variance: 0.5f, strength: 100);
            var defender = TestCreatures.CreateDefender(baseArmor: 200);

            var total = 0.0f;
            const int rolls = 20000;

            for (var i = 0; i < rolls; i++)
                total += DamageEvent.CalculateDamage(attacker, defender, null).Damage;

            var engineMean = total / rolls;

            var attributeMod = SkillFormula.GetAttributeMod(100);

            // TestCreatures.CreateDefender's single PropertiesBodyPart entry (Chest, every
            // quadrant flag set) means the ENGINE can only ever strike Chest at armor level 200 -
            // that is where the engine's own collapse happens. The simulator does NOT collapse to
            // match it: CombatSimulator.MeanMitigation averages ArmorMod across every part in the
            // attack height's BodyParts.Mid band, and MitigationMath.ArmorMod fails soft to 1.0
            // (unmitigated) for any part missing from DefenderProfile.Armor. So every part in the
            // band has to carry this defender's armor value, or the two sides describe different
            // defenders - do not shrink this back to a Chest-only entry.
            var armor = new Dictionary<ArmorKey, ArmorRow>();

            foreach (var bodyPart in BodyParts.GetFlags(BodyParts.Mid))
                armor[new ArmorKey(bodyPart, DamageType.Slash, false, false)] = new ArmorRow(200.0f);

            var profile = new DefenderProfile
            {
                Label = "parity",
                MaxHealth = 100,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, 0u } },
                Armor = armor,
                Resistance = new Dictionary<DamageType, ResistancePair>
                {
                    { DamageType.Slash, new ResistancePair(1.0f, 1.0f) },
                },
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Melee, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = 5.0f,
                BaseDamageMax = 10.0f,
                CritChance = 0.1f,
                CritDamageMod = 2.0f,
                PreMitigationMod = attributeMod,
            };

            var analytic = CombatSimulator.Analytic(spec, profile);

            // 2% tolerance: the engine mean is itself a sampled quantity at 20k rolls
            Assert.AreEqual(engineMean, analytic.MeanDamagePerLandedHit, engineMean * 0.02f);
        }
    }
}

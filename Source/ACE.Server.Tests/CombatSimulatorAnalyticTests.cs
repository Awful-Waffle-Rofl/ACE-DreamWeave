using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    using CombatSimulator = ACE.Server.CombatSimulator.CombatSimulator;

    [TestClass]
    public class CombatSimulatorAnalyticTests
    {
        private const float Epsilon = 0.01f;

        private static DefenderProfile UnmitigatedProfile(uint maxHealth, uint defenseSkill)
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>();

            foreach (var bodyPart in BodyParts.GetFlags(BodyParts.Mid))
                armor[new ArmorKey(bodyPart, DamageType.Slash, false, false)] = new ArmorRow(0.0f);

            return new DefenderProfile
            {
                Label = "unmitigated",
                MaxHealth = maxHealth,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, defenseSkill } },
                Armor = armor,
                Resistance = new Dictionary<DamageType, ResistancePair>
                {
                    { DamageType.Slash, new ResistancePair(1.0f, 1.0f) },
                },
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Melee, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };
        }

        [TestMethod]
        public void Analytic_NoCritNoMitigation_MeanIsRangeMidpoint()
        {
            var profile = UnmitigatedProfile(maxHealth: 1000, defenseSkill: 0);

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 0.0f,
                CritDamageMod = 2.0f,
            };

            var result = CombatSimulator.Analytic(spec, profile);

            Assert.AreEqual(150.0f, result.MeanDamagePerLandedHit, Epsilon);
            Assert.AreEqual(1000.0f / 150.0f, result.LandedHitsToKill, Epsilon);
        }

        [TestMethod]
        public void Analytic_CritUsesMaxDamageNotTheMean()
        {
            var profile = UnmitigatedProfile(maxHealth: 1000, defenseSkill: 0);

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 1.0f,
                CritDamageMod = 2.0f,
            };

            var result = CombatSimulator.Analytic(spec, profile);

            // 200 max damage doubled, NOT the 150 midpoint doubled
            Assert.AreEqual(400.0f, result.MeanDamagePerLandedHit, Epsilon);
        }

        [TestMethod]
        public void Analytic_SwingsToKillAccountsForHitChance()
        {
            var profile = UnmitigatedProfile(maxHealth: 1000, defenseSkill: 500);

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 500,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 100.0f,
                CritChance = 0.0f,
                CritDamageMod = 2.0f,
            };

            var result = CombatSimulator.Analytic(spec, profile);

            Assert.AreEqual(0.5f, result.HitChance, 0.001f);
            Assert.AreEqual(result.LandedHitsToKill / 0.5f, result.SwingsToKill, Epsilon);
        }

        /// <summary>
        /// A crit rating is added to the crit chance unbounded (WorldObject_Weapon.cs:365), so a
        /// weenie carrying CritRating 100 derives 1.1. The engine never notices, because its only
        /// consumer is "if (CriticalChance > ThreadSafeRandom.Next(0.0f, 1.0f))"
        /// (DamageEvent.cs:302), which saturates. The closed form does notice - its non-crit term is
        /// (1 - CritChance) and goes negative - so Analytic saturates to match.
        /// </summary>
        [TestMethod]
        public void Analytic_CritChanceAboveOne_SaturatesLikeTheEngine()
        {
            var profile = UnmitigatedProfile(maxHealth: 1000, defenseSkill: 0);

            var atCap = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 1.0f,
                CritDamageMod = 2.0f,
            };

            var overCap = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 1.1f,
                CritDamageMod = 2.0f,
            };

            var atCapResult = CombatSimulator.Analytic(atCap, profile);
            var overCapResult = CombatSimulator.Analytic(overCap, profile);

            Assert.AreEqual(atCapResult.MeanDamagePerLandedHit, overCapResult.MeanDamagePerLandedHit, Epsilon);
        }

        [TestMethod]
        public void Sampling_MeanAgreesWithAnalytic()
        {
            var profile = UnmitigatedProfile(maxHealth: 1000, defenseSkill: 0);

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 0.25f,
                CritDamageMod = 2.0f,
            };

            var analytic = CombatSimulator.Analytic(spec, profile);
            var sampled = CombatSimulator.Sample(spec, profile, 200000, new System.Random(12345));

            // 1% tolerance at 200k samples
            var tolerance = analytic.MeanDamagePerLandedHit * 0.01f;

            Assert.AreEqual(analytic.MeanDamagePerLandedHit, sampled.MeanDamagePerLandedHit, tolerance);
            Assert.IsTrue(sampled.P90Damage >= sampled.P50Damage);
        }
    }
}

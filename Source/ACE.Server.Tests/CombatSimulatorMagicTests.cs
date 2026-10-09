using System;
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
    public class CombatSimulatorMagicTests
    {
        private const float Epsilon = 0.01f;

        [TestMethod]
        public void Magic_IgnoresTheArmorTableEntirely()
        {
            // heavy armor in the physical table must not reduce magic damage
            var armor = new Dictionary<ArmorKey, ArmorRow>();

            foreach (var bodyPart in BodyParts.GetFlags(BodyParts.Mid))
                armor[new ArmorKey(bodyPart, DamageType.Fire, false, false)] = new ArmorRow(2000.0f);

            var profile = new DefenderProfile
            {
                Label = "magic",
                MaxHealth = 1000,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Magic, 0u } },
                Armor = armor,
                Resistance = new Dictionary<DamageType, ResistancePair>
                {
                    { DamageType.Fire, new ResistancePair(1.0f, 1.0f) },
                },
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Magic, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Magic,
                DamageType = DamageType.Fire,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 100.0f,
                CritChance = 0.0f,
                CritDamageMod = 2.0f,
            };

            var result = CombatSimulator.AnalyticMagic(spec, profile);

            // unmitigated: the 2000 armor level must not be consulted
            Assert.AreEqual(100.0f, result.MeanDamagePerLandedHit, Epsilon);
        }

        /// <summary>
        /// A defender with no mitigation at all, so the whole assertion is about the crit shape.
        /// </summary>
        private static DefenderProfile NeutralFireProfile(uint maxHealth = 1000)
        {
            return new DefenderProfile
            {
                Label = "magic",
                MaxHealth = maxHealth,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Magic, 0u } },
                Armor = new Dictionary<ArmorKey, ArmorRow>(),
                Resistance = new Dictionary<DamageType, ResistancePair>
                {
                    { DamageType.Fire, new ResistancePair(1.0f, 1.0f) },
                },
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Magic, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };
        }

        /// <summary>
        /// The magic crit ADDS to the rolled base; it does not replace it, which is the melee rule.
        ///
        /// Hand-computed, so the next reader can check it without re-deriving from SpellProjectile:
        ///
        ///   E[base]   = (100 + 200) / 2                  = 150
        ///   crit term = 0.10 * 200 * 0.5 * 2.0           =  20
        ///   expected  = 150 + 20                         = 170
        ///
        /// The 0.5 is the PvE half-of-MAX bonus at SpellProjectile.cs:638, the 2.0 is
        /// MagicCritDamageMod (:646-648), and the base term carries NO (1 - critChance) weighting
        /// because the roll at :665 happens whether or not the hit crits (:683).
        ///
        /// For contrast, the melee shape this replaced would give
        /// (1 - 0.1) * 150 + 0.1 * 200 * 2.0 = 175 - overstated by roughly 3 percent here, and by
        /// more as the crit multiplier rises.
        /// </summary>
        [TestMethod]
        public void MagicCrit_AddsHalfOfMaxToTheRolledBase_RatherThanReplacingIt()
        {
            var spec = new AttackerSpec
            {
                CombatType = CombatType.Magic,
                DamageType = DamageType.Fire,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 0.1f,
                MagicCritDamageMod = 2.0f,

                // deliberately set, and deliberately NOT the value the magic path uses: the melee
                // field must have no effect here
                CritDamageMod = 7.0f,
            };

            var result = CombatSimulator.AnalyticMagic(spec, NeutralFireProfile());

            Assert.AreEqual(170.0f, result.MeanDamagePerLandedHit, Epsilon);
        }

        /// <summary>
        /// MagicCritDamageMod defaults to 1.0, the engine's defaultCritDamageMultiplier for a
        /// weapon with no CriticalMultiplier (WorldObject_Weapon.cs:422, :429). The melee field's
        /// neutral value is 2.0, so a spec that forgets to set the magic one must not silently pick
        /// up the melee neutral.
        ///
        ///   E[base]   = 150
        ///   crit term = 0.10 * 200 * 0.5 * 1.0 = 10
        ///   expected  = 160
        /// </summary>
        [TestMethod]
        public void MagicCritDamageMod_DefaultsToOne_NotTheMeleeNeutralOfTwo()
        {
            var spec = new AttackerSpec
            {
                CombatType = CombatType.Magic,
                DamageType = DamageType.Fire,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 0.1f,
            };

            var result = CombatSimulator.AnalyticMagic(spec, NeutralFireProfile());

            Assert.AreEqual(160.0f, result.MeanDamagePerLandedHit, Epsilon);
        }

        /// <summary>
        /// Analytic-versus-sampling agreement for the magic path, R3's requirement.
        ///
        /// CombatSimulator.Sample CANNOT cover this and is deliberately not used. It hardcodes the
        /// physical shape twice over: it applies MitigationMath.ArmorMod and ShieldMod, which the
        /// magic path must never consult, and its crit branch REPLACES the roll with
        /// BaseDamageMax * CritDamageMod, which is the very rule this method exists not to use.
        /// Making it cover magic would mean either a mode flag through the sampler or a parallel
        /// SampleMagic, and both would add production surface that no shipped caller uses.
        ///
        /// So the sampler here is local to the test and reproduces SpellProjectile's stated
        /// arithmetic directly - a uniform base roll, plus half of MAX times the weapon crit mod on
        /// a crit - which is what makes it an independent check on the closed form rather than a
        /// restatement of it.
        /// </summary>
        [TestMethod]
        public void AnalyticMagic_AgreesWithASamplingOfTheEngineRule()
        {
            var spec = new AttackerSpec
            {
                CombatType = CombatType.Magic,
                DamageType = DamageType.Fire,
                AttackSkill = 10000,
                BaseDamageMin = 110.0f,
                BaseDamageMax = 180.0f,
                CritChance = 0.08f,
                MagicCritDamageMod = 1.5f,
                PreMitigationMod = 1.25f,
            };

            var profile = NeutralFireProfile();

            // fixed seed: this test must not be able to fail intermittently
            var rng = new Random(20260908);

            const int rolls = 400000;
            var total = 0.0;

            for (var i = 0; i < rolls; i++)
            {
                // SpellProjectile.cs:665 - the base is rolled across the whole range every time
                var baseDamage = spec.BaseDamageMin + (float)rng.NextDouble() * (spec.BaseDamageMax - spec.BaseDamageMin);

                // :638 and :648 - PvE adds half of MAX, scaled by the weapon crit damage mod
                var critBonus = rng.NextDouble() < spec.CritChance
                    ? spec.BaseDamageMax * 0.5f * spec.MagicCritDamageMod
                    : 0.0f;

                // :683 then :685 - the sum, then the multiplicative terms
                total += (baseDamage + critBonus) * spec.PreMitigationMod;
            }

            var sampledMean = (float)(total / rolls);
            var analytic = CombatSimulator.AnalyticMagic(spec, profile).MeanDamagePerLandedHit;

            // 0.5 percent: the sampled side is a Monte Carlo estimate, not an exact quantity
            Assert.AreEqual(sampledMean, analytic, sampledMean * 0.005f);
        }
    }
}

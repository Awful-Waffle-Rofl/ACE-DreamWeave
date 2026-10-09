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
    public class TimeToKillSolverTests
    {
        private const float Epsilon = 0.001f;

        private static DefenderProfile Profile(uint maxHealth)
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>();

            foreach (var bodyPart in BodyParts.GetFlags(BodyParts.Mid))
                armor[new ArmorKey(bodyPart, DamageType.Slash, false, false)] = new ArmorRow(0.0f);

            return new DefenderProfile
            {
                Label = "solver",
                MaxHealth = maxHealth,
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
        }

        private static AttackerSpec Spec(float min, float max)
        {
            return new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 10000,
                BaseDamageMin = min,
                BaseDamageMax = max,
                CritChance = 0.0f,
                CritDamageMod = 2.0f,
            };
        }

        [TestMethod]
        public void Solver_HalvingTheTargetDoublesTheScale()
        {
            // 1000 health, 100 mean damage -> 10 landed hits to kill; target 5 needs 2x
            var profiles = new List<DefenderProfile> { Profile(1000) };

            var scale = TimeToKillSolver.ScaleForTarget(Spec(100.0f, 100.0f), profiles, 5.0f);

            Assert.AreEqual(2.0f, scale, Epsilon);
        }

        [TestMethod]
        public void Solver_AppliedScaleReachesTheTarget()
        {
            var profiles = new List<DefenderProfile> { Profile(1000), Profile(2000) };
            var spec = Spec(50.0f, 150.0f);

            var scale = TimeToKillSolver.ScaleForTarget(spec, profiles, 3.0f);

            var scaled = new AttackerSpec
            {
                CombatType = spec.CombatType,
                DamageType = spec.DamageType,
                AttackHeight = spec.AttackHeight,
                AttackSkill = spec.AttackSkill,
                BaseDamageMin = spec.BaseDamageMin * scale,
                BaseDamageMax = spec.BaseDamageMax * scale,
                CritChance = spec.CritChance,
                CritDamageMod = spec.CritDamageMod,
            };

            var total = 0.0f;

            foreach (var profile in profiles)
                total += CombatSimulator.Analytic(scaled, profile).LandedHitsToKill;

            Assert.AreEqual(3.0f, total / profiles.Count, 0.01f);
        }

        [TestMethod]
        public void Solver_NonFiniteMean_ReturnsUnityScale()
        {
            // A spec with zero damage makes every profile infinitely hard to kill
            var profiles = new List<DefenderProfile> { Profile(1000) };

            var scale = TimeToKillSolver.ScaleForTarget(Spec(0.0f, 0.0f), profiles, 5.0f);

            Assert.AreEqual(1.0f, scale, Epsilon);
        }
    }
}

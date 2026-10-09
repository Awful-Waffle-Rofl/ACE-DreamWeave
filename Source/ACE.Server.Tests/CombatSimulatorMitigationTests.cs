using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class CombatSimulatorMitigationTests
    {
        private const float Epsilon = 0.0001f;

        private static DefenderProfile ProfileWithArmor(float effectiveAl)
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>
            {
                { new ArmorKey(BodyPart.Chest, DamageType.Slash, false, false), new ArmorRow(effectiveAl) },
            };

            return new DefenderProfile
            {
                Label = "test",
                MaxHealth = 100,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, 0u } },
                Armor = armor,
                Resistance = new Dictionary<DamageType, ResistancePair>(),
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
            };
        }

        [TestMethod]
        public void ArmorMod_NoRending_MatchesEngineCurve()
        {
            var profile = ProfileWithArmor(200.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash };

            Assert.AreEqual(SkillFormula.CalcArmorMod(200.0f),
                MitigationMath.ArmorMod(profile, spec, BodyPart.Chest), Epsilon);
        }

        [TestMethod]
        public void ArmorMod_RendingScalesArmorLevelBeforeTheCurve()
        {
            var profile = ProfileWithArmor(200.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, ArmorRendingMod = 0.5f };

            // 0.5 applied to the ARMOR LEVEL, not to the resulting mod
            Assert.AreEqual(SkillFormula.CalcArmorMod(100.0f),
                MitigationMath.ArmorMod(profile, spec, BodyPart.Chest), Epsilon);

            Assert.AreNotEqual(SkillFormula.CalcArmorMod(200.0f) * 0.5f,
                MitigationMath.ArmorMod(profile, spec, BodyPart.Chest), Epsilon);
        }

        [TestMethod]
        public void ArmorMod_NegativeArmorLevel_IsNotScaledByRending()
        {
            // the engine guards the rending multiply with if (effectiveAL > 0)
            var profile = ProfileWithArmor(-50.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, ArmorRendingMod = 0.5f };

            Assert.AreEqual(SkillFormula.CalcArmorMod(-50.0f),
                MitigationMath.ArmorMod(profile, spec, BodyPart.Chest), Epsilon);
        }

        [TestMethod]
        public void ArmorMod_IgnoreAllArmor_IsUnmitigated()
        {
            var profile = ProfileWithArmor(200.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, IgnoreAllArmor = true };

            Assert.AreEqual(1.0f, MitigationMath.ArmorMod(profile, spec, BodyPart.Chest), Epsilon);
        }

        private static DefenderProfile ProfileWithShieldAndResistance(float cappedShieldLevel, float p0, float v0)
        {
            return new DefenderProfile
            {
                Label = "test",
                MaxHealth = 100,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, 0u } },
                Armor = new Dictionary<ArmorKey, ArmorRow>(),
                Resistance = new Dictionary<DamageType, ResistancePair>
                {
                    { DamageType.Slash, new ResistancePair(p0, v0) },
                },
                Shield = new Dictionary<ShieldKey, ShieldRow>
                {
                    { new ShieldKey(DamageType.Slash, false), new ShieldRow(cappedShieldLevel) },
                },
                DamageResistRatingBase = new Dictionary<CombatType, float>
                {
                    { CombatType.Melee, 1.0f },
                },
                BattleHardenedMultiplier = 0.9f,
            };
        }

        [TestMethod]
        public void ShieldMod_OutsideFrontalArc_DoesNotApply()
        {
            var profile = ProfileWithShieldAndResistance(300.0f, 1.0f, 1.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, WithinShieldArc = false };

            Assert.AreEqual(1.0f, MitigationMath.ShieldMod(profile, spec), Epsilon);
        }

        [TestMethod]
        public void ShieldMod_IgnoreShieldScalesCappedLevelBeforeTheCurve()
        {
            var profile = ProfileWithShieldAndResistance(300.0f, 1.0f, 1.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, IgnoreShieldMod = 0.5f };

            Assert.AreEqual(SkillFormula.CalcArmorMod(150.0f),
                MitigationMath.ShieldMod(profile, spec), Epsilon);
        }

        [TestMethod]
        public void ResistanceMod_WeaponResistanceFloorsVulnerability()
        {
            // stored V0 of 1.0 is floored up to the attacker's 1.5
            var profile = ProfileWithShieldAndResistance(0.0f, 0.8f, 1.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, WeaponResistanceMod = 1.5f };

            Assert.AreEqual(0.8f * 1.5f, MitigationMath.ResistanceMod(profile, spec), Epsilon);
        }

        [TestMethod]
        public void ResistanceMod_StoredVulnerabilityWinsWhenHigher()
        {
            var profile = ProfileWithShieldAndResistance(0.0f, 0.8f, 2.0f);
            var spec = new AttackerSpec { DamageType = DamageType.Slash, WeaponResistanceMod = 1.5f };

            Assert.AreEqual(0.8f * 2.0f, MitigationMath.ResistanceMod(profile, spec), Epsilon);
        }

        [TestMethod]
        public void DamageResistRating_BattleHardenedIsPvEOnly()
        {
            var profile = ProfileWithShieldAndResistance(0.0f, 1.0f, 1.0f);

            // CombatType is explicit because DamageResistRatingBase is now keyed by it, and the
            // fixture carries exactly the CombatType.Melee entry these two specs look up
            var fromMonster = new AttackerSpec { CombatType = CombatType.Melee, AttackerIsPlayer = false };
            var fromPlayer = new AttackerSpec { CombatType = CombatType.Melee, AttackerIsPlayer = true };

            Assert.AreEqual(0.9f, MitigationMath.DamageResistRatingMod(profile, fromMonster), Epsilon);
            Assert.AreEqual(1.0f, MitigationMath.DamageResistRatingMod(profile, fromPlayer), Epsilon);
        }
    }
}

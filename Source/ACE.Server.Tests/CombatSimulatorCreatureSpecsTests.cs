using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    using CombatSimulator = ACE.Server.CombatSimulator.CombatSimulator;

    /// <summary>
    /// Parity of the creature-side builders against the engine itself: CreatureAttackerSpecs (melee, missile,
    /// spell) against the terms DamageEvent.CalculateDamage computes, and MonsterDefenderProfile with
    /// MonsterCombatSimulator against the engine's creature-defender mitigation and its sampled mean. Same
    /// discipline as CombatSimulatorParityTests: the simulator calls production factor functions, so what these
    /// pin is the composition, which is what drifts.
    /// </summary>
    [TestClass]
    public class CombatSimulatorCreatureSpecsTests
    {
        private const float Epsilon = 0.0005f;

        private static uint nextGuid = 0x7E200000;  // static guid range, clear of GuidManager
        private static uint nextWcid = 996000;      // unique per creature: the body part table cache is keyed by wcid

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static Creature MakeCreature(Weenie weenie, float x, bool registerBodyParts)
        {
            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.Location = new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            if (registerBodyParts)
                Creature.SetBodyPartTable(creature.WeenieClassId, new BodyPartTable(weenie));

            return creature;
        }

        /// <summary>
        /// Two body parts with different armor, reachable from the medium front AND back quadrants on both sides
        /// with weights 1 and 3, so the engine's draw is 25/75 whatever direction GetQuadrant resolves - which is
        /// what lets the frontal-only analytic be compared against it.
        /// </summary>
        private static Creature MakeTwoPartDefender(double? resistSlash = null, double? armorVsSlash = null, uint meleeDefense = 0, uint maxHealth = 500)
        {
            PropertiesBodyPart Part(int armor, float weight) => new PropertiesBodyPart
            {
                DType = DamageType.Bludgeon,
                DVal = 1,
                DVar = 0.5f,
                BaseArmor = armor,
                MLF = weight, MRF = weight, MLB = weight, MRB = weight,
            };

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Endurance, new PropertiesAttribute { InitLevel = 20 } },
                    { PropertyAttribute.Quickness, new PropertiesAttribute { InitLevel = 40 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = 20 } },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = maxHealth } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.MeleeDefense, new PropertiesSkill { InitLevel = meleeDefense, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Chest, Part(100, 1.0f) },
                    { CombatBodyPart.Abdomen, Part(400, 3.0f) },
                },
            };

            if (resistSlash != null)
                weenie.PropertiesFloat[PropertyFloat.ResistSlash] = resistSlash.Value;

            if (armorVsSlash != null)
                weenie.PropertiesFloat[PropertyFloat.ArmorModVsSlash] = armorVsSlash.Value;

            return MakeCreature(weenie, 55.0f, registerBodyParts: true);
        }

        private static Creature MakeHollowAttacker()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.IgnoreMagicArmor, true },
                    { PropertyBool.IgnoreMagicResist, true },
                },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Overpower, 100 } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 100 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.UnarmedCombat, new PropertiesSkill { InitLevel = 400, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, new PropertiesBodyPart { DType = DamageType.Slash, DVal = 10, DVar = 0.5f } },
                },
            };

            return MakeCreature(weenie, 50.0f, registerBodyParts: false);
        }

        private static DamageEvent EngineHit(Creature attacker, Creature defender, WorldObject damageSource, Func<DamageEvent, bool> want, int tries = 2000)
        {
            for (var i = 0; i < tries; i++)
            {
                var ev = DamageEvent.CalculateDamage(attacker, defender, damageSource);

                if (want(ev))
                    return ev;
            }

            Assert.Fail("the engine never produced the event shape this test needs");
            return null;
        }

        [TestMethod]
        public void Melee_SpecTermsMatchTheEngineEvent()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 10, variance: 0.5f, strength: 100);
            var defender = TestCreatures.CreateDefender(baseArmor: 200);

            var spec = CreatureAttackerSpecs.Melee(attacker);

            var normal = EngineHit(attacker, defender, null, e => e.HasDamage && !e.IsCritical);

            Assert.AreEqual(CombatType.Melee, spec.CombatType);
            Assert.AreEqual(normal.DamageType, spec.DamageType);
            Assert.AreEqual(normal.BaseDamageMod.MinDamage, spec.BaseDamageMin, Epsilon);
            Assert.AreEqual(normal.BaseDamageMod.MaxDamage, spec.BaseDamageMax, Epsilon);
            Assert.AreEqual(normal.AttributeMod * normal.PowerMod * normal.SlayerMod * normal.DamageRatingMod, spec.PreMitigationMod, Epsilon);
            Assert.AreEqual(normal.CriticalChance, spec.CritChance, Epsilon);
            Assert.AreEqual(attacker.GetEffectiveAttackSkill(), spec.AttackSkill);

            var crit = EngineHit(attacker, defender, null, e => e.HasDamage && e.IsCritical);

            Assert.AreEqual(crit.CriticalDamageMod, spec.CritDamageMod, Epsilon);
            Assert.AreEqual(2.0f, spec.CritDamageMod, Epsilon, "an unimbued weaponless monster crits at 2.0");
        }

        [TestMethod]
        public void Melee_WithADefender_ReadsTheDefendersCritResistAndMatchesTheEngineChance()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(baseArmor: 50);

            defender.SetProperty(PropertyInt.CritResistRating, 25);

            var withDefender = CreatureAttackerSpecs.Melee(attacker, defender);
            var withoutDefender = CreatureAttackerSpecs.Melee(attacker);

            var ev = EngineHit(attacker, defender, null, e => e.HasDamage);

            Assert.AreEqual(ev.CriticalChance, withDefender.CritChance, Epsilon);
            Assert.IsTrue(withDefender.CritChance < withoutDefender.CritChance, "sanity: the crit resist is live");
            Assert.AreEqual(withoutDefender.CritChance * Creature.GetNegativeRatingMod(25), withDefender.CritChance, Epsilon);
        }

        [TestMethod]
        public void MonsterDefender_RowsMatchTheEngineCreatureDefenderTerms()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 20, variance: 0.25f, strength: 150);
            var defender = MakeTwoPartDefender(resistSlash: 0.8, armorVsSlash: 0.5);

            var spec = CreatureAttackerSpecs.Melee(attacker);
            var profile = MonsterDefenderProfile.Build(defender);

            var ev = EngineHit(attacker, defender, null, e => e.HasDamage && !e.IsCritical);

            Assert.AreEqual(ev.ResistanceMod, MonsterCombatSimulator.ResistanceMod(profile, spec), Epsilon);
            Assert.AreEqual(ev.ShieldMod, MonsterCombatSimulator.ShieldMod(profile, spec), Epsilon);
            Assert.AreEqual(ev.DamageResistanceRatingMod, MonsterCombatSimulator.DamageResistRatingMod(profile, spec), Epsilon);
            Assert.AreEqual(ev.ArmorMod, MonsterCombatSimulator.ArmorMod(profile, spec, ev.PropertiesBodyPart.Key), Epsilon);

            // the stored no-hollow armor level IS the production function's return value
            foreach (var kvp in defender.Biota.PropertiesBodyPart)
            {
                var part = new Creature_BodyPart(defender, kvp);
                var expected = part.GetEffectiveArmorVsType(DamageType.Slash, part.GetArmorLayers(kvp.Key), attacker, null, 1.0f);

                Assert.AreEqual(expected, profile.EffectiveArmorLevel[new MonsterArmorKey(kvp.Key, DamageType.Slash, false, false)], Epsilon);
            }

            Assert.AreEqual(0.8f * 1.0f, MonsterCombatSimulator.ResistanceMod(profile, spec), Epsilon, "sanity: ResistSlash is applied");
        }

        [TestMethod]
        public void MonsterDefender_AnalyticMeanMatchesTheEngineMeanOverManyRolls()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 20, variance: 0.25f, strength: 150);
            var defender = MakeTwoPartDefender(resistSlash: 0.8, armorVsSlash: 0.5, maxHealth: 5000);

            var spec = CreatureAttackerSpecs.Melee(attacker);
            var profile = MonsterDefenderProfile.Build(defender);

            const int rolls = 40000;
            var total = 0.0;

            for (var i = 0; i < rolls; i++)
                total += DamageEvent.CalculateDamage(attacker, defender, null).Damage;

            var engineMean = (float)(total / rolls);

            var analytic = MonsterCombatSimulator.Analytic(spec, profile);

            // the 25/75 armor split must actually move the mean, or this test proves nothing about the weighting
            var chestOnly = MonsterCombatSimulator.ArmorMod(profile, spec, CombatBodyPart.Chest);
            var abdomenOnly = MonsterCombatSimulator.ArmorMod(profile, spec, CombatBodyPart.Abdomen);
            Assert.IsTrue(chestOnly - abdomenOnly > 0.1f, "sanity: the two parts mitigate differently");

            Assert.AreEqual(engineMean, analytic.MeanDamagePerLandedHit, engineMean * 0.02f);
            Assert.AreEqual(1.0f, analytic.HitChance, Epsilon, "overpower attacker vs zero defense: every quadrant lands");
        }

        [TestMethod]
        public void MonsterDefender_HitChanceIsTheEnginesEvadeComplement()
        {
            var attacker = TestCreatures.CreateAttacker(attackSkill: 300, overpower: false);
            var defender = MakeTwoPartDefender(meleeDefense: 250);

            var spec = CreatureAttackerSpecs.Melee(attacker);
            var profile = MonsterDefenderProfile.Build(defender);

            var ev = DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.AreEqual(defender.GetEffectiveDefenseSkill(CombatType.Melee), profile.DefenseSkills[CombatType.Melee]);
            Assert.AreEqual(ev.EffectiveDefenseSkill, profile.DefenseSkills[CombatType.Melee]);
            Assert.AreEqual(1.0f - ev.EvasionChance, MonsterCombatSimulator.Analytic(spec, profile).HitChance, Epsilon);
        }

        [TestMethod]
        public void MonsterDefender_HollowRowsMatchTheProductionHollowPath()
        {
            var hollow = MakeHollowAttacker();
            var defender = MakeTwoPartDefender(resistSlash: 0.6, armorVsSlash: 0.75);

            var spec = CreatureAttackerSpecs.Melee(hollow);
            var profile = MonsterDefenderProfile.Build(defender);

            Assert.IsTrue(spec.IgnoreMagicArmor && spec.IgnoreMagicResist, "sanity: the attacker is hollow");

            foreach (var wrm in new[] { 1.0f, 1.25f })
            {
                var engine = (float)Math.Max(0.0, defender.GetResistanceMod(ResistanceType.Slash, hollow, null, wrm));
                var specAtWrm = new AttackerSpec { DamageType = DamageType.Slash, IgnoreMagicResist = true, WeaponResistanceMod = wrm };

                Assert.AreEqual(engine, MonsterCombatSimulator.ResistanceMod(profile, specAtWrm), Epsilon);
            }

            foreach (var kvp in defender.Biota.PropertiesBodyPart)
            {
                var part = new Creature_BodyPart(defender, kvp);
                var expected = part.GetEffectiveArmorVsType(DamageType.Slash, part.GetArmorLayers(kvp.Key), hollow, null, 1.0f);

                Assert.AreEqual(expected, profile.EffectiveArmorLevel[new MonsterArmorKey(kvp.Key, DamageType.Slash, true, true)], Epsilon);
            }
        }

        [TestMethod]
        public void MonsterDefender_AQuadrantWithNoPartsIsAnEvadeNotADamageTerm()
        {
            var profile = new MonsterDefenderProfile
            {
                Label = "half",
                MaxHealth = 100,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, 0u } },
                EffectiveArmorLevel = new Dictionary<MonsterArmorKey, float>
                {
                    { new MonsterArmorKey(CombatBodyPart.Chest, DamageType.Slash, false, false), 0.0f },
                },
                QuadrantWeights = Enumerable.Range(0, 12).Select(i => (IReadOnlyList<KeyValuePair<CombatBodyPart, float>>)(
                    i == (int)QuadrantIndex.MLF
                        ? new List<KeyValuePair<CombatBodyPart, float>> { new KeyValuePair<CombatBodyPart, float>(CombatBodyPart.Chest, 1.0f) }
                        : new List<KeyValuePair<CombatBodyPart, float>>())).ToList(),
            };

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 100000,
                BaseDamageMin = 10.0f,
                BaseDamageMax = 10.0f,
            };

            var result = MonsterCombatSimulator.Analytic(spec, profile);

            Assert.AreEqual(0.5f, result.HitChance, 0.001f, "only the left-front medium quadrant holds a part");
            Assert.AreEqual(10.0f, result.MeanDamagePerLandedHit, Epsilon, "a landed hit is unaffected by the empty quadrant");
        }

        private static (Creature archer, MissileLauncher bow, Ammunition arrows, Weenie arrowWeenie) MakeArcher()
        {
            var archerWeenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Overpower, 100 } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 60 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = 180 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.MissileWeapons, new PropertiesSkill { InitLevel = 350, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, new PropertiesBodyPart { DType = DamageType.Bludgeon, DVal = 5, DVar = 0.5f } },
                },
            };

            var archer = MakeCreature(archerWeenie, 50.0f, registerBodyParts: false);

            var bowWeenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileWeapon },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.Bow },
                    { PropertyInt.WeaponSkill, (int)Skill.MissileWeapons },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageMod, 2.0 } },
            };

            var arrowWeenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileAmmo },
                    { PropertyInt.Damage, 30 },
                    { PropertyInt.DamageType, (int)DamageType.Pierce },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } },
            };

            var bow = new MissileLauncher(bowWeenie, new ObjectGuid(nextGuid++));
            var arrows = new Ammunition(arrowWeenie, new ObjectGuid(nextGuid++));

            foreach (var item in new WorldObject[] { bow, arrows })
            {
                archer.EquippedObjects[item.Guid] = item;
                item.Wielder = archer;
            }

            return (archer, bow, arrows, arrowWeenie);
        }

        [TestMethod]
        public void Missile_SpecTermsMatchTheEngineProjectileEvent()
        {
            var (archer, bow, arrows, arrowWeenie) = MakeArcher();
            var defender = MakeTwoPartDefender(maxHealth: 5000);

            var spec = CreatureAttackerSpecs.Missile(archer);

            // the engine's projectile: a fresh object from the ammo weenie, carrying source, launcher and ammo
            var projectile = new Ammunition(arrowWeenie, new ObjectGuid(nextGuid++))
            {
                ProjectileSource = archer,
                ProjectileLauncher = bow,
                ProjectileAmmo = arrows,
            };

            // Monster_Melee.GetBaseDamage takes the missile branch only while the monster's current attack is one
            archer.CurrentAttack = CombatType.Missile;

            var ev = EngineHit(archer, defender, projectile, e => e.HasDamage && !e.IsCritical);

            Assert.AreEqual(CombatType.Missile, ev.CombatType, "sanity: the engine took the missile path");
            Assert.AreEqual(CombatType.Missile, spec.CombatType);
            Assert.AreEqual(ev.DamageType, spec.DamageType);
            Assert.AreEqual(DamageType.Pierce, spec.DamageType);
            Assert.AreEqual(ev.BaseDamageMod.MinDamage, spec.BaseDamageMin, Epsilon);
            Assert.AreEqual(ev.BaseDamageMod.MaxDamage, spec.BaseDamageMax, Epsilon);
            Assert.AreEqual(60.0f, spec.BaseDamageMax, Epsilon, "sanity: the arrow's 30 is scaled by the bow's DamageMod 2.0");
            Assert.AreEqual(ev.AttributeMod * ev.PowerMod * ev.SlayerMod * ev.DamageRatingMod, spec.PreMitigationMod, Epsilon);
            Assert.AreEqual(SkillFormula.GetAttributeMod(180, true), ev.AttributeMod, Epsilon, "sanity: a bow reads Coordination");
            Assert.AreEqual(ev.CriticalChance, spec.CritChance, Epsilon);
            Assert.AreEqual(ev.WeaponResistanceMod, spec.WeaponResistanceMod, Epsilon);

            var profile = MonsterDefenderProfile.Build(defender);
            Assert.AreEqual(ev.ArmorMod, MonsterCombatSimulator.ArmorMod(profile, spec, ev.PropertiesBodyPart.Key), Epsilon);
            Assert.AreEqual(ev.DamageResistanceRatingMod, MonsterCombatSimulator.DamageResistRatingMod(profile, spec), Epsilon);
        }

        [TestMethod]
        public void Missile_RefusesACreatureWithoutAmmo()
        {
            var attacker = TestCreatures.CreateAttacker();

            Assert.ThrowsExactly<InvalidOperationException>(() => CreatureAttackerSpecs.Missile(attacker));
        }

        [TestMethod]
        public void Spell_MonsterCasterUsesTheSpellsOwnRangeAndNoSkillBonus()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.DamageRating, 20 } },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.WarMagic, new PropertiesSkill { InitLevel = 450, SAC = SkillAdvancementClass.Trained } },
                },
            };

            var caster = MakeCreature(weenie, 50.0f, registerBodyParts: false);

            var spec = CreatureAttackerSpecs.SpellFromValues(caster, "bolt", MagicSchool.WarMagic, DamageType.Fire, 100, 200, 300, null);

            Assert.AreEqual(CombatType.Magic, spec.CombatType);
            Assert.AreEqual(DamageType.Fire, spec.DamageType);
            Assert.AreEqual(caster.GetCreatureSkill(MagicSchool.WarMagic).Current, spec.AttackSkill);
            Assert.AreEqual(100.0f, spec.BaseDamageMin);
            Assert.AreEqual(200.0f, spec.BaseDamageMax);
            Assert.AreEqual(0.0f, spec.MagicSkillBonus, "a monster caster never gets the war magic skill bonus");
            Assert.AreEqual(0.05f, spec.CritChance, Epsilon, "no wand: the flat magic default");
            Assert.AreEqual(1.0f, spec.MagicCritDamageMod, Epsilon, "no wand: the raw multiplier, no +1.0");
            Assert.AreEqual(Creature.AdditiveCombine(Creature.GetPositiveRatingMod(20), 1.0f, 1.0f), spec.PreMitigationMod, Epsilon, "the monster's damage rating applies to its spells");
            Assert.IsFalse(spec.IgnoreMagicResist);
            Assert.IsFalse(spec.AttackerIsPlayer);
        }

        [TestMethod]
        public void AnalyticMagic_AddsThePlayerSkillBonusToEveryHitBeforeTheMultipliers()
        {
            var profile = new DefenderProfile
            {
                Label = "magic",
                MaxHealth = 1000,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Magic, 0u } },
                Resistance = new Dictionary<DamageType, ResistancePair> { { DamageType.Fire, new ResistancePair(1.0f, 1.0f) } },
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Magic, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };

            var spec = new AttackerSpec
            {
                CombatType = CombatType.Magic,
                DamageType = DamageType.Fire,
                AttackSkill = 10000,
                BaseDamageMin = 100.0f,
                BaseDamageMax = 200.0f,
                CritChance = 0.1f,
                MagicCritDamageMod = 1.0f,
                MagicSkillBonus = 15.0f,
                PreMitigationMod = 2.0f,
            };

            // (E[base] + skillBonus + critChance * max * 0.5 * mod) * pre
            var expected = (150.0f + 15.0f + 0.1f * 200.0f * 0.5f) * 2.0f;

            Assert.AreEqual(expected, CombatSimulator.AnalyticMagic(spec, profile).MeanDamagePerLandedHit, 0.01f);

            // the monster-defender magic path applies the same shape
            var monster = new MonsterDefenderProfile
            {
                Label = "monster",
                MaxHealth = 1000,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Magic, 0u } },
                Resistance = new Dictionary<DamageType, MonsterResistanceRow> { { DamageType.Fire, new MonsterResistanceRow(1.0f, 1.0f, 1.0f, 1.0f) } },
                DamageResistRatingMod = new Dictionary<CombatType, float> { { CombatType.Magic, 1.0f } },
            };

            Assert.AreEqual(expected, MonsterCombatSimulator.Analytic(spec, monster).MeanDamagePerLandedHit, 0.01f);
        }
    }
}

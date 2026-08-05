using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Combat modifiers on bare creatures (TestCreatures builds them with no database or dat
    /// files). Covers the retail armor curve, the strength damage bonus, effective defense
    /// skill, overpower chance, and the no-shield baseline.
    /// </summary>
    [TestClass]
    public class CreatureCombatTests
    {
        private const float Epsilon = 0.0001f;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void TrainedSkill_AddsTheAttributeFormulaToInitLevel()
        {
            // retail: unarmed combat = init + (Strength + Coordination) / 3
            var attacker = TestCreatures.CreateAttacker(strength: 100, coordination: 80, attackSkill: 400);

            Assert.AreEqual(100u, attacker.Strength.Current);
            Assert.AreEqual(460u, attacker.GetCreatureSkill(Skill.UnarmedCombat).Current);
        }

        [TestMethod]
        public void UntrainedSkill_GetsNoAttributeContribution()
        {
            // the advancement-class gate: an inactive skill stays at zero even with high attributes
            var attacker = TestCreatures.CreateAttacker(strength: 100, coordination: 80);

            Assert.AreEqual(0u, attacker.GetCreatureSkill(Skill.LightWeapons).Current);
        }

        [TestMethod]
        public void MaxHealth_AddsHalfEnduranceToInitLevel()
        {
            // retail: max health = init + Endurance / 2
            var defender = TestCreatures.CreateDefender(maxHealth: 50, endurance: 100);

            Assert.AreEqual(100u, defender.Health.MaxValue);
            Assert.AreEqual(100u, defender.Health.Current);

            var frail = TestCreatures.CreateDefender(maxHealth: 50, endurance: 20);
            Assert.AreEqual(60u, frail.Health.MaxValue);
        }

        [TestMethod]
        public void ArmorCurve_MatchesRetailFormula()
        {
            // retail armor curve: mod = (200/3) / (AL + 200/3); negative AL amplifies damage linearly
            Assert.AreEqual(1.0f, SkillFormula.CalcArmorMod(0), Epsilon);
            Assert.AreEqual(0.5f, SkillFormula.CalcArmorMod(200.0f / 3), Epsilon);
            Assert.AreEqual(0.25f, SkillFormula.CalcArmorMod(200), Epsilon);
            Assert.AreEqual(2.0f, SkillFormula.CalcArmorMod(-200.0f / 3), Epsilon);

            // more armor can never increase damage taken, and positive armor can never heal
            var prev = float.MaxValue;
            for (var al = -100; al <= 1000; al += 10)
            {
                var mod = SkillFormula.CalcArmorMod(al);

                Assert.IsTrue(mod <= prev + Epsilon, $"armor mod rose at AL {al}");
                Assert.IsTrue(mod > 0.0f, $"armor mod not positive at AL {al}");
                if (al > 0)
                    Assert.IsTrue(mod < 1.0f, $"positive AL {al} failed to reduce damage");

                prev = mod;
            }
        }

        [TestMethod]
        public void AttributeDamageBonus_MatchesRetailFormula()
        {
            // melee: 1 + (attr - 55) * 0.011, floored at 1.0; bows use the shallower 0.008 factor
            Assert.AreEqual(1.0f, SkillFormula.GetAttributeMod(55), Epsilon);
            Assert.AreEqual(1.0f, SkillFormula.GetAttributeMod(10), Epsilon);
            Assert.AreEqual(1.495f, SkillFormula.GetAttributeMod(100), Epsilon);
            Assert.AreEqual(1.36f, SkillFormula.GetAttributeMod(100, isBow: true), Epsilon);

            // a weaker attacker never hits harder
            var prev = 0.0f;
            for (var attr = 0; attr <= 500; attr += 5)
            {
                var mod = SkillFormula.GetAttributeMod(attr);
                Assert.IsTrue(mod >= prev - Epsilon, $"attribute mod fell at {attr}");
                Assert.IsTrue(mod >= 1.0f - Epsilon, $"attribute mod below the 1.0 floor at {attr}");
                prev = mod;
            }
        }

        [TestMethod]
        public void GetAttributeMod_UsesStrengthForUnarmedMelee()
        {
            var attacker = TestCreatures.CreateAttacker(strength: 100);

            Assert.AreEqual(SkillFormula.GetAttributeMod(100), attacker.GetAttributeMod((WorldObject)null), Epsilon);
        }

        [TestMethod]
        public void EffectiveDefenseSkill_IsCurrentSkillAndZeroWhenExhausted()
        {
            var defender = TestCreatures.CreateDefender(meleeDefense: 300);

            // retail: melee defense = init + (Quickness + Coordination) / 3 = 300 + (40+20)/3,
            // and a bare creature has no defender bonus, burden, stance, or imbues on top
            Assert.AreEqual(320u, defender.GetEffectiveDefenseSkill(CombatType.Melee));

            // running out of stamina must drop defense to zero - the retail exhaustion penalty
            defender.Stamina.Current = 0;
            Assert.AreEqual(0u, defender.GetEffectiveDefenseSkill(CombatType.Melee));
        }

        [TestMethod]
        public void OverpowerChance_BothMethodsMatchTheirFormulas()
        {
            var attacker = TestCreatures.CreateAttacker();          // Overpower = 100
            var defender = TestCreatures.CreateDefender();

            // no resist: both methods give a certain overpower at 100
            Assert.AreEqual(1.0f, Creature.GetOverpowerChance_Method_A(attacker, defender), Epsilon);
            Assert.AreEqual(1.0f, Creature.GetOverpowerChance_Method_B(attacker, defender), Epsilon);

            // method A subtracts resist; method B multiplies through the resist chance
            defender.SetProperty(PropertyInt.OverpowerResist, 30);
            Assert.AreEqual(0.7f, Creature.GetOverpowerChance_Method_A(attacker, defender), Epsilon);
            Assert.AreEqual(0.7f, Creature.GetOverpowerChance_Method_B(attacker, defender), Epsilon);

            // resist at or above the overpower value fully suppresses method A
            defender.SetProperty(PropertyInt.OverpowerResist, 120);
            Assert.AreEqual(0.0f, Creature.GetOverpowerChance_Method_A(attacker, defender), Epsilon);

            // an attacker without the property can never overpower
            var mundane = TestCreatures.CreateAttacker(overpower: false);
            Assert.AreEqual(0.0f, Creature.GetOverpowerChance_Method_A(mundane, defender), Epsilon);
            Assert.AreEqual(0.0f, Creature.GetOverpowerChance_Method_B(mundane, defender), Epsilon);
        }

        [TestMethod]
        public void ShieldMod_IsNeutralWithoutAShield()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            Assert.AreEqual(1.0f, defender.GetShieldMod(attacker, DamageType.Slash, null), Epsilon);
        }
    }
}

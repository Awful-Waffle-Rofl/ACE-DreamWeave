using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// End-to-end melee damage pipeline (DamageEvent.CalculateDamage) between two bare creatures
    /// built by TestCreatures - no database, dat files, or landblocks; formula tables come from
    /// TestGameTables. The pipeline rolls its own randomness (base damage, hit location, crits),
    /// so tests pin the deterministic components exactly and bound the random ones: damage must
    /// always decompose into baseDamage x attributeMod x armorMod, with crits dealing
    /// max damage x critMultiplier. The attacker fixture has Overpower 100, which retail-legally
    /// bypasses the evasion roll and makes every attack connect.
    /// </summary>
    [TestClass]
    public class DamageEventTests
    {
        private const float Epsilon = 0.001f;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void UnarmedStrike_DecomposesIntoRetailComponents()
        {
            // attacker: max damage 10, variance 0.5, strength 100, unarmed 400, overpower
            // defender: base armor 200 on its single body part
            var attacker = TestCreatures.CreateAttacker(maxDamage: 10, variance: 0.5f, strength: 100);
            var defender = TestCreatures.CreateDefender(baseArmor: 200);

            var expectedAttributeMod = SkillFormula.GetAttributeMod(100);   // 1.495
            var expectedArmorMod = SkillFormula.CalcArmorMod(200);          // 0.25

            var sawCritical = false;
            var sawNonCritical = false;

            for (var i = 0; i < 500; i++)
            {
                var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

                Assert.IsFalse(damageEvent.Evaded, "overpower attack must never be evaded");
                Assert.IsFalse(damageEvent.GeneralFailure);

                // base damage rolls within the attack part's DVal/DVar range
                Assert.IsTrue(damageEvent.BaseDamage >= 5.0f - Epsilon && damageEvent.BaseDamage <= 10.0f + Epsilon,
                    $"base damage {damageEvent.BaseDamage} outside [5, 10]");

                Assert.AreEqual(expectedAttributeMod, damageEvent.AttributeMod, Epsilon);
                Assert.AreEqual(expectedArmorMod, damageEvent.ArmorMod, Epsilon);

                if (damageEvent.IsCritical)
                {
                    sawCritical = true;

                    // crits deal max damage doubled (default crit multiplier 1.0 = +100%)
                    Assert.AreEqual(2.0f, damageEvent.CriticalDamageMod, Epsilon);
                    Assert.AreEqual(10.0f * expectedAttributeMod * 2.0f * expectedArmorMod, damageEvent.Damage, Epsilon);
                }
                else
                {
                    sawNonCritical = true;

                    Assert.AreEqual(damageEvent.BaseDamage * expectedAttributeMod * expectedArmorMod, damageEvent.Damage, Epsilon);
                }
            }

            // 10% base crit rate: 500 attacks without both outcomes is astronomically unlikely
            Assert.IsTrue(sawCritical, "no critical hit in 500 attacks");
            Assert.IsTrue(sawNonCritical, "no normal hit in 500 attacks");
        }

        [TestMethod]
        public void UnarmoredDefender_TakesUnmitigatedDamage()
        {
            var attacker = TestCreatures.CreateAttacker(maxDamage: 10, strength: 100);
            var defender = TestCreatures.CreateDefender(baseArmor: 0);

            var expectedAttributeMod = SkillFormula.GetAttributeMod(100);

            for (var i = 0; i < 50; i++)
            {
                var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

                Assert.AreEqual(1.0f, damageEvent.ArmorMod, Epsilon);

                if (!damageEvent.IsCritical)
                    Assert.AreEqual(damageEvent.BaseDamage * expectedAttributeMod, damageEvent.Damage, Epsilon);
            }
        }

        [TestMethod]
        public void OverwhelmingDefense_AlwaysEvades()
        {
            // without overpower, a 400 attack skill against 10000 melee defense loses the
            // skill check with probability ~1: the evade path must zero the damage
            var attacker = TestCreatures.CreateAttacker(attackSkill: 400, overpower: false);
            var defender = TestCreatures.CreateDefender(meleeDefense: 10000);

            var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.IsTrue(damageEvent.Evaded);
            Assert.AreEqual(0.0f, damageEvent.Damage, Epsilon);
            Assert.IsFalse(damageEvent.HasDamage);
            Assert.AreEqual(10020u, damageEvent.EffectiveDefenseSkill);   // 10000 + (Quick 40 + Coord 20)/3
        }

        [TestMethod]
        public void InvincibleDefender_TakesNothing()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();
            defender.SetProperty(PropertyBool.Invincible, true);

            var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.AreEqual(0.0f, damageEvent.Damage, Epsilon);
        }
    }
}

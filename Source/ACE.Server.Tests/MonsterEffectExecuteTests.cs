using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ExecuteEffect: scales a landed hit's damage up when the defender is at or below a health threshold.
    /// Mutates DamageEvent.Damage synchronously (the same shape ExecutionerAbility itself uses), so it is
    /// fully exercisable through the real dispatch site with no ActionChain involved.
    /// </summary>
    [TestClass]
    public class MonsterEffectExecuteTests
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

            PropertyManager.ModifyDouble("monster_effect_damage_rider_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_damage_rider_cap"].Item);
        }

        private static readonly ExecuteEffect Effect = new ExecuteEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"execute {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("bonus=0.5"), out _), "hp= defaults to 0.25");

            Assert.IsFalse(Effect.Validate(Spec("bonus=0"), out var error), "bonus= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("bonus=0.5 hp=0"), out _), "hp= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("bonus=0.5 hp=1.5"), out _), "hp= must be <= 1");
        }

        [TestMethod]
        public void Magnitude_ScalesDamageWhenTheDefenderIsBelowTheThreshold()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 100);
            defender.Health.Current = 20;   // 20% <= the default 25% hp= threshold

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("bonus=0.5"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(15.0f, damageEvent.Damage, 0.001f, "10 * (1 + 0.5) = 15");
        }

        [TestMethod]
        public void CapClamp_TheAddedDamageIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 3.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 100);
            defender.Health.Current = 20;

            // bonus=5.0 on a 10-damage hit would add 50 without the cap
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("bonus=5.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(13.0f, damageEvent.Damage, 0.001f, "10 + the 3.0-capped addition, not 10 + 50");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheScaling()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 100);
            defender.Health.Current = 20;

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("bonus=0.5"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(10.0f, damageEvent.Damage, 0.001f, "the master switch must stop the scaling entirely");
        }
    }
}

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
    /// RangeRampEffect: scales a landed MISSILE hit's damage by distance to the defender. TestCreatures
    /// places the attacker and defender 5 metres apart (x: 50.0 / 55.0), which is the distance every test
    /// below relies on. Mutates DamageEvent.Damage synchronously, so it needs no ActionChain to exercise.
    /// </summary>
    [TestClass]
    public class MonsterEffectRangeRampTests
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

        private static readonly RangeRampEffect Effect = new RangeRampEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"rangeramp {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("min=0 max=10 peak=1.0"), out _));

            Assert.IsFalse(Effect.Validate(Spec("min=0 max=10 peak=0"), out var error), "peak= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("min=10 max=10 peak=1.0"), out _), "max= must be > min=");
            Assert.IsFalse(Effect.Validate(Spec("min=10 max=5 peak=1.0"), out _), "max= must be > min=");
        }

        [TestMethod]
        public void Magnitude_ScalesMissileDamageByDistance()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            // 5m apart (fixture default); min=0, max=10 => halfway up the ramp
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("min=0 max=10 peak=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Missile };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(15.0f, damageEvent.Damage, 0.001f, "10 + (10 * 0.5 peak fraction) = 15");
        }

        [TestMethod]
        public void CapClamp_TheAddedDamageIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 3.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            // peak=10 on a 10-damage hit at half ramp would add 50 without the cap
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("min=0 max=10 peak=10.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Missile };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(13.0f, damageEvent.Damage, 0.001f, "10 + the 3.0-capped addition, not 10 + 50");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheScaling()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("min=0 max=10 peak=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Missile };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(10.0f, damageEvent.Damage, 0.001f, "the master switch must stop the scaling entirely");
        }
    }
}

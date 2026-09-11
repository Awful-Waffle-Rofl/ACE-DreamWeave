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
    /// FlatDamageEffect: a landed hit has a chance to deal a separate flat damage rider after a delay.
    /// The rider itself is delivered from inside a deferred ActionChain, which a bare test Creature (no
    /// landblock, so EnqueueAction silently discards) cannot be made to run - see
    /// WorldObject_Tick.EnqueueAction's detached-Creature branch. Magnitude and clamping are therefore
    /// asserted against FlatDamageEffect.ComputeDealtAmount, the pure helper OnOutgoingHit itself calls, and
    /// the disabled-tunable case is asserted against MonsterEffectState.Announced, which OnOutgoingHit sets
    /// the instant a roll succeeds - before scheduling - for exactly this reason.
    /// </summary>
    [TestClass]
    public class MonsterEffectFlatDamageTests
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

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);
        }

        private static readonly FlatDamageEffect Effect = new FlatDamageEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"flatdamage {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("type=fire amount=30"), out _));

            Assert.IsFalse(Effect.Validate(Spec("amount=30"), out var noType), "type= is required");
            Assert.IsFalse(string.IsNullOrEmpty(noType));

            Assert.IsFalse(Effect.Validate(Spec("type=fire amount=0"), out _), "amount= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("type=fire amount=30 chance=0"), out _), "chance= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("type=fire amount=30 chance=1.5"), out _), "chance= must be <= 1");
        }

        [TestMethod]
        public void Magnitude_DealtAmountMatchesTheAuthoredAmount()
        {
            Assert.AreEqual(30u, FlatDamageEffect.ComputeDealtAmount(30.0));

            // an authored chance=1.0 to actually be certain (default cap is 0.75)
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=fire amount=30 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.IsTrue(attacker.MonsterEffectStates[0].Announced, "a certain roll must mark the proc as having fired");
        }

        /// <summary>
        /// A hit that landed for NOTHING must proc nothing. DamageEvent.HasDamage stays true when the
        /// defender is Invincible - DoCalculateDamage returns 0 damage without setting Evaded, Blocked,
        /// Parried or LifestoneProtection - so a rider with no zero-damage guard fires off a hit the
        /// defender never felt. leech, execute, rangeramp, ramp and debuff always had this guard; this
        /// kind did not.
        /// </summary>
        [TestMethod]
        public void ZeroDamageHit_ProcsNothing()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=fire amount=30 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 0.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.IsFalse(attacker.MonsterEffectStates[0].Announced, "a hit that dealt nothing must not proc a damage rider");
        }

        [TestMethod]
        public void CapClamp_DealtAmountIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 50.0));

            Assert.AreEqual(50u, FlatDamageEffect.ComputeDealtAmount(10000.0), "the runaway guard must clamp an oversized authored amount");
            Assert.AreEqual(20u, FlatDamageEffect.ComputeDealtAmount(20.0), "an amount under the cap is untouched");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheEffectFromRollingAtAll()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=fire amount=30 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.IsFalse(attacker.MonsterEffectStates[0].Announced, "the master switch must stop dispatch before the handler ever rolls");
        }
    }
}

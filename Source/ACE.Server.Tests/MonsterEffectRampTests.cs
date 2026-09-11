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
    /// RampEffect: a general stacking buff. Accrual is exercised through the real dispatch sites
    /// (ApplyOutgoingHitMonsterEffects for on=hit), and the held multiplier through
    /// MonsterEffectSet.GetRampMultiplier - the seam another effect reads without knowing which effect (if
    /// any) is doing the ramping. Lapse is exercised through the real heartbeat dispatch
    /// (Creature.MonsterEffectHeartbeat) after backdating state.LastTime directly (MonsterEffectStates
    /// exposes the live array, so mutating an element mutates the creature's real state).
    /// </summary>
    [TestClass]
    public class MonsterEffectRampTests
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

            PropertyManager.ModifyLong("monster_effect_ramp_stack_cap",
                DefaultPropertyManager.DefaultLongProperties["monster_effect_ramp_stack_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_speed_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_speed_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_avoidance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_avoidance_cap"].Item);
        }

        private static readonly RampEffect Effect = new RampEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"ramp {args}", out var specs, out _);
            return specs[0];
        }

        private static MonsterEffectSpec AvoidSpec(string args)
        {
            MonsterEffectParser.Parse($"avoid {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("axis=attackspeed on=hit per=0.04 max=8 window=12"), out _));
            Assert.IsTrue(Effect.Validate(Spec("axis=magicdamage per=0.1 max=5 window=10"), out _), "on= defaults to hit");

            Assert.IsFalse(Effect.Validate(Spec("per=0.04 max=8 window=12"), out var noAxis), "axis= is required");
            Assert.IsFalse(string.IsNullOrEmpty(noAxis));
            Assert.IsFalse(Effect.Validate(Spec("axis=bogus per=0.04 max=8 window=12"), out _), "axis= must be a real MonsterRampAxis");
            Assert.IsFalse(Effect.Validate(Spec("axis=attackspeed max=8 window=12"), out _), "per= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("axis=attackspeed per=0.04 window=12"), out _), "max= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("axis=attackspeed per=0.04 max=8"), out _), "window= must be > 0");
        }

        /// <summary>
        /// Both halves in one test: accrual builds the held multiplier, and once window= has elapsed with no
        /// further trigger the next heartbeat drops the whole pool back to 1.0 - a ramp that never lapses is
        /// the failure a player would actually feel, per the class's own doc comment on OnHeartbeat.
        /// </summary>
        [TestMethod]
        public void Magnitude_AccruesOnTheNamedTriggerAndLapsesAfterTheWindow()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            var spec = Spec("axis=attackspeed on=hit per=0.05 max=8 window=10");

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, spec)));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(2, attacker.MonsterEffectStates[0].Stacks, "two landed hits must accrue two stacks");

            var mult = attacker.MonsterEffects.GetRampMultiplier(MonsterRampAxis.AttackSpeed, attacker.MonsterEffectStates);
            Assert.AreEqual(1.10, mult, 0.0001, "1 + 2 stacks * 0.05 per stack");

            // backdate the last-trigger time past the window, then run the real heartbeat dispatch
            attacker.MonsterEffectStates[0].LastTime -= 11.0;

            attacker.MonsterEffectHeartbeat();

            Assert.AreEqual(0, attacker.MonsterEffectStates[0].Stacks, "the pool must lapse once window= has elapsed with no trigger");
            Assert.AreEqual(1.0, attacker.MonsterEffects.GetRampMultiplier(MonsterRampAxis.AttackSpeed, attacker.MonsterEffectStates), 0.0001);
        }

        /// <summary>
        /// FRENZY, END TO END. ramp axis=attackspeed is the subsystem's flagship effect, and for its whole
        /// first phase it accrued and lapsed stacks that changed absolutely nothing: the composer
        /// Creature.GetMonsterEffectSpeedMultiplier read only the IMonsterSpeedMod bucket and never touched
        /// the ramp seam a ramp publishes on.
        ///
        /// So this goes through the REAL entry point, Creature.GetAnimSpeed - the number monster melee and
        /// missile actually schedule against - and not through the composer or GetRampMultiplier, either of
        /// which would have passed happily while the effect stayed inert in play. Both halves are asserted,
        /// because a ramp that lifts speed and never gives it back is the failure a player feels.
        /// </summary>
        [TestMethod]
        public void Frenzy_AttackSpeedRampMovesGetAnimSpeedAndReturnsToBaselineWhenItLapses()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            var baseline = attacker.GetAnimSpeed();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("axis=attackspeed on=hit per=0.10 max=8 window=10"))));

            Assert.AreEqual(baseline, attacker.GetAnimSpeed(), 0.001f,
                "an attached ramp holding no stacks must not move the speed at all");

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(3, attacker.MonsterEffectStates[0].Stacks, "three landed hits must accrue three stacks");

            Assert.AreEqual(baseline * 1.30f, attacker.GetAnimSpeed(), 0.001f,
                "1 + 3 stacks * 0.10 per stack must reach GetAnimSpeed, not just the ramp seam");

            // backdate the last-trigger time past the window, then run the real heartbeat dispatch
            attacker.MonsterEffectStates[0].LastTime -= 11.0;

            attacker.MonsterEffectHeartbeat();

            Assert.AreEqual(baseline, attacker.GetAnimSpeed(), 0.001f,
                "a lapsed ramp must hand the speed back, through the same entry point");
        }

        /// <summary>
        /// The combined product takes ONE clamp, applied last. A cap of 1.25 against three stacks of 0.10
        /// (1.30) must land on 1.25 exactly, which is also what pins the clamp to the composed number rather
        /// than to the ramp's own published multiplier.
        /// </summary>
        [TestMethod]
        public void CapClamp_TheAttackSpeedRampIsBoundedByTheSpeedCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_speed_cap", 1.25));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            var baseline = attacker.GetAnimSpeed();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("axis=attackspeed on=hit per=0.10 max=8 window=10"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(baseline * 1.25f, attacker.GetAnimSpeed(), 0.001f,
                "the ramp's 1.30 must be clamped to the cap, not passed through uncapped");
        }

        /// <summary>
        /// axis=procchance reaches every effect that rolls a chance=, because they all read their number
        /// through Creature.ScaleMonsterEffectProcChance. Asserted against that composer directly rather than
        /// through a handler, so the test measures the ramp rather than a proc roll's luck; the cap is
        /// applied AFTER the ramp, which the second half pins.
        /// </summary>
        [TestMethod]
        public void ProcChance_RampScalesAnAuthoredChanceAndTheCapStillWinsAfterwards()
        {
            var monster = TestCreatures.CreateDefender();

            monster.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("axis=procchance on=hit per=0.10 max=8 window=10"))));

            Assert.AreEqual(0.20, monster.ScaleMonsterEffectProcChance(0.20), 0.0001, "no stacks yet");

            // set directly: this test is about the READ, and accrual has its own test above
            monster.MonsterEffectStates[0].Stacks = 3;

            Assert.AreEqual(0.26, monster.ScaleMonsterEffectProcChance(0.20), 0.0001,
                "0.20 * (1 + 3 stacks * 0.10 per stack)");

            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 0.22));

            Assert.AreEqual(0.22, monster.ScaleMonsterEffectProcChance(0.20), 0.0001,
                "the cap is applied after the ramp, so a ramp can never walk a proc past the ceiling");
        }

        /// <summary>
        /// axis=avoid scales the POOLED avoid grant in Creature.RollMonsterEffectAvoidance. The roll is
        /// random, so the test is written to be deterministic from the arithmetic rather than from luck: a
        /// 0.25 grant times a x4 ramp reaches 1.0, which the roll can never exceed. Unramped the same monster
        /// avoids about a quarter of the time, so the twenty-iteration loop is what makes the difference
        /// observable (an unwired ramp passing all twenty is a 0.25^20 coincidence).
        /// </summary>
        [TestMethod]
        public void Avoid_RampScalesThePooledAvoidChance()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            var attacker = TestCreatures.CreateDefender();
            var defender = TestCreatures.CreateDefender();

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (new AvoidEffect(), AvoidSpec("pct=0.25 silent=true")),
                (Effect, Spec("axis=avoid on=hit per=1.0 max=8 window=10"))));

            // set directly: this test is about the READ, and accrual has its own test above. The slot is
            // looked up rather than hardcoded because the set sorts into dispatch order - ramp is a damage
            // mutator, so it does NOT sit at the authored index 1
            defender.MonsterEffectStates[TestCreatures.MonsterEffectSlot(defender, Effect)].Stacks = 3;

            for (var i = 0; i < 20; i++)
            {
                Assert.IsTrue(defender.RollMonsterEffectAvoidance(attacker, CombatType.Melee),
                    "0.25 grant * (1 + 3 stacks * 1.0 per stack) = 1.0, which the pooled roll can never beat");
            }
        }

        [TestMethod]
        public void CapClamp_StacksAreClampedByTheRampStackCap()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_ramp_stack_cap", 2));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            // authored max=8 is far above the server cap of 2; the cap must win
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("axis=attackspeed on=hit per=0.05 max=8 window=10"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(2, attacker.MonsterEffectStates[0].Stacks, "three landed hits must still cap at 2 stacks");
        }

        [TestMethod]
        public void Disabled_TunableStopsAccrual()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("axis=attackspeed on=hit per=0.05 max=8 window=10"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(0, attacker.MonsterEffectStates[0].Stacks, "the master switch must stop accrual entirely");
        }
    }
}

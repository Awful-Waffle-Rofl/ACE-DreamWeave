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
    /// RiposteEffect: when this monster avoids a melee attack, it counters with a free strike at pct= of a
    /// normal swing.
    ///
    /// REACHABILITY: riposte grants no avoidance of its own (GetAvoidChance is a pure 0.0), so it needs a
    /// granter on the same monster - AvoidEffect here - for the pooled roll to succeed at all. Once it does,
    /// every carried handler is notified, so Magnitude drives the REAL dispatch site
    /// (Creature.RollMonsterEffectAvoidance) rather than calling OnAvoided directly. That pairing failed for
    /// the whole of this effect's first phase, when the site credited one weighted winner instead; keeping
    /// the test on the real site is what stops it regressing.
    ///
    /// HARNESS LIMIT (a different thing entirely): the actual counter-swing runs from inside a deferred
    /// ActionChain, which a bare test Creature (no landblock) silently discards - see
    /// WorldObject_Tick.EnqueueAction's detached-Creature branch. OnAvoided sets state.Announced the instant
    /// its chance rolls succeed and the counter is scheduled, before the chain runs, for exactly this reason
    /// (mirroring FlatDamageEffect/DotEffect); the chance roll itself is further split into the pure,
    /// testable RiposteEffect.ShouldCounter.
    /// </summary>
    [TestClass]
    public class MonsterEffectRiposteTests
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

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_avoidance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_avoidance_cap"].Item);
        }

        private static readonly RiposteEffect Effect = new RiposteEffect();
        private static readonly AvoidEffect AvoidGranter = new AvoidEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"riposte {args}", out var specs, out _);
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
            Assert.IsTrue(Effect.Validate(Spec("pct=0.5"), out _), "chance= defaults to 1.0");
            Assert.IsTrue(Effect.Validate(Spec("pct=0.5 chance=0.3"), out _));

            Assert.IsFalse(Effect.Validate(Spec("pct=0"), out var error), "pct= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("pct=0.5 chance=0"), out _), "chance= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("pct=0.5 chance=1.5"), out _), "chance= must be <= 1");
        }

        /// <summary>
        /// Driven through the REAL dispatch site, paired with a granter, because that is the path that was
        /// broken: riposte contributes no avoidance chance of its own, so a site that credits one weighted
        /// winner can never reach it. state.Announced is read back out of the creature's live state array,
        /// slot 1 - the same index riposte occupies in the set built below.
        /// </summary>
        [TestMethod]
        public void Magnitude_SchedulesACounterWhenAvoided()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            // no overpower: overpower skips the avoidance block entirely, which would defeat this test
            var attacker = TestCreatures.CreateAttacker(overpower: false);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (AvoidGranter, AvoidSpec("pct=1.0")),
                (Effect, Spec("pct=0.5 chance=1.0"))));

            var avoided = defender.RollMonsterEffectAvoidance(attacker, CombatType.Melee);

            Assert.IsTrue(avoided, "the pooled roll must succeed - avoid grants it at pct=1.0");

            Assert.IsTrue(defender.MonsterEffectStates[TestCreatures.MonsterEffectSlot(defender, Effect)].Announced,
                "riposte must reach OnAvoided through the real dispatch site, despite granting no avoidance itself");
        }

        [TestMethod]
        public void CapClamp_ChanceIsClampedByTheProcChanceCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 0.0));

            Assert.IsFalse(RiposteEffect.ShouldCounter(TestCreatures.CreateDefender(), Spec("pct=0.5 chance=1.0")),
                "a cap of zero must let nothing through");
        }

        /// <summary>
        /// The master switch is a DISPATCH-SITE precondition, not something OnAvoided itself checks, so this
        /// goes through the real dispatch (Creature.RollMonsterEffectAvoidance): with the switch off the
        /// pooled roll never succeeds, so nothing downstream of it runs either.
        /// </summary>
        [TestMethod]
        public void Disabled_TunableStopsTheAvoidanceRoll()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker(overpower: false);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (AvoidGranter, AvoidSpec("pct=1.0")),
                (Effect, Spec("pct=0.5 chance=1.0"))));

            var avoided = defender.RollMonsterEffectAvoidance(attacker, CombatType.Melee);

            Assert.IsFalse(avoided, "the master switch must stop the avoidance dispatch entirely");
        }

        /// <summary>
        /// The invariant the class doc comment is built around: OnAvoided must never re-enter
        /// Creature.MeleeAttack, which unconditionally advances NextAttackTime at the end of every real
        /// swing. Calling OnAvoided directly (bypassing the ActionChain, which a bare test Creature discards
        /// anyway) and asserting NextAttackTime is untouched catches a regression that calls
        /// defender.MeleeAttack() synchronously instead of building its own DamageEvent - MeleeAttack sets
        /// NextAttackTime before returning, so that bug would fail this assertion immediately.
        /// </summary>
        [TestMethod]
        public void OnAvoided_DoesNotAdvanceNextAttackTime()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateDefender();
            var defender = TestCreatures.CreateDefender();

            defender.NextAttackTime = 12345.0;

            var spec = Spec("pct=0.5 chance=1.0");
            var state = new MonsterEffectState();

            Effect.OnAvoided(defender, attacker, spec, ref state);

            Assert.AreEqual(12345.0, defender.NextAttackTime, "OnAvoided must never touch the counter-attacker's own swing timer");
        }
    }
}

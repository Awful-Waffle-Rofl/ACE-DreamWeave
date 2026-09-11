using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WardEffect: an absorb pool granted on spawn/heartbeat/hpbelow and spent against incoming damage.
    /// Both hooks are synchronous (no ActionChain), so it is fully exercisable through the real dispatch
    /// sites - MonsterEffectHeartbeat for the grant, AbsorbMonsterEffectDamage for the absorb.
    /// </summary>
    [TestClass]
    public class MonsterEffectWardTests
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

            PropertyManager.ModifyDouble("monster_effect_ward_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_ward_cap"].Item);
        }

        private static readonly WardEffect Effect = new WardEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"ward {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("amount=100 secs=15"), out _));
            Assert.IsTrue(Effect.Validate(Spec("pcthp=0.25 secs=15 on=hpbelow trigger=0.5"), out _));
            Assert.IsTrue(Effect.Validate(Spec("amount=100 secs=15 on=heartbeat"), out _));

            Assert.IsFalse(Effect.Validate(Spec("secs=15"), out var error), "exactly one of amount=/pcthp= is required");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("amount=100 pcthp=0.25 secs=15"), out _), "amount= and pcthp= together must be rejected");
            Assert.IsFalse(Effect.Validate(Spec("amount=0 secs=15"), out _), "amount= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("pcthp=1.5 secs=15"), out _), "pcthp= must be <= 1");
            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=0"), out _), "secs= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=15 on=bogus"), out _), "on= must be spawn/heartbeat/hpbelow");
            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=15 on=hpbelow"), out _), "on=hpbelow requires trigger=");
        }

        /// <summary>
        /// on= is a comma list here, the same as it is on reflect, debuff, castspell and ramp. It was not,
        /// and the worked example in Docs/MonsterEffects/DESIGN.md - "on=spawn,heartbeat" - did not parse.
        ///
        /// STRICTNESS IS THE OTHER HALF, and it is why this arg is hand-parsed rather than read through
        /// GetFlags: a list must still reject a typo'd token outright instead of silently dropping it and
        /// shipping whatever survived. Both failure shapes are pinned below, along with a numeric token,
        /// which the parser's "never by number" rule says must never resolve either.
        /// </summary>
        [TestMethod]
        public void Parse_AcceptsACommaSeparatedOnListAndStillRejectsABadToken()
        {
            Assert.IsTrue(Effect.Validate(Spec("pcthp=0.25 secs=30 on=spawn,heartbeat"), out _), "the DESIGN.md worked example must parse");
            Assert.IsTrue(Effect.Validate(Spec("amount=100 secs=15 on=spawn,hpbelow trigger=0.5"), out _), "shield on arrival and again in the second phase");
            // NO SPACE AFTER THE COMMA - the parser splits a record's tokens on whitespace before any arg is
            // read, so "on=heartbeat, spawn" is two tokens and the second one has no '=' at all. That is a
            // parser-level rule every comma-list arg in the catalog lives under, not a ward quirk.
            Assert.IsTrue(Effect.Validate(Spec("amount=100 secs=15 on=heartbeat,spawn"), out _), "order within the list does not matter");

            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=15 on=spawn,bogus"), out var error), "one bad token must reject the whole record");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=15 on=spawn,hpbelow"), out _), "hpbelow in a list still requires trigger=");
            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=15 on=1"), out _), "a numeric token must never resolve to a trigger");
            Assert.IsFalse(Effect.Validate(Spec("amount=100 secs=15 on=,"), out _), "a list with nothing in it names no trigger");
        }

        /// <summary>
        /// The sentinels have to stay per-trigger, or a combined record grants once and stops: spawn latches
        /// on state.Announced and hpbelow on state.Stacks, so "on=spawn,hpbelow" grants on arrival AND again
        /// when the carrier drops into the trigger= window, and neither latch consumes the other.
        /// </summary>
        [TestMethod]
        public void Magnitude_SpawnAndHpBelowEachGrantOnceOnACombinedRecord()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("amount=50 secs=300 on=spawn,hpbelow trigger=0.25"))));

            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount, "the spawn half must grant on the first heartbeat");
            Assert.AreEqual(0, creature.MonsterEffectStates[0].Stacks, "the hpbelow half must NOT have fired at full health");

            // spend the pool, then drop into the trigger window
            creature.MonsterEffectStates[0].WardAmount = 0;
            creature.Health.Current = 100;

            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount, "the hpbelow half must grant again once the carrier is low");
            Assert.AreEqual(1, creature.MonsterEffectStates[0].Stacks, "and latch, so it is a one-shot rather than a per-tick regrant");

            // and it really is a one-shot: another heartbeat at low health must not refill a spent pool
            creature.MonsterEffectStates[0].WardAmount = 0;

            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "neither latch may grant a third time");
        }

        [TestMethod]
        public void Magnitude_SpawnGrantsOnceAndAbsorbsIncomingDamage()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));

            // first heartbeat: grants the ward (on=spawn simulated as "first heartbeat sighting")
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount, "the first heartbeat must grant the full authored amount");

            // second heartbeat: on=spawn must NOT regrant - the sentinel (Announced) must hold
            var attacker = TestCreatures.CreateAttacker();
            var absorbedFirst = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20);
            Assert.AreEqual(0u, absorbedFirst, "the ward must eat the full 20 out of its 50");
            Assert.AreEqual(30u, creature.MonsterEffectStates[0].WardAmount, "50 - 20 = 30 left");

            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(30u, creature.MonsterEffectStates[0].WardAmount, "on=spawn must never regrant once Announced is set");
        }

        [TestMethod]
        public void CapClamp_ResolvedGrantIsClampedByTheWardCapForBothAuthoringPaths()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_ward_cap", 0.5));

            // maxHealth=500 plus TestCreatures.CreateDefender's endurance-20 bonus (+10) resolves to a real
            // Health.MaxValue of 510 - see the formula note on CreateDefender's own doc comment - so the
            // clamp ceiling here is 0.5 * 510 = 255, not 250.
            var flatCreature = TestCreatures.CreateDefender(maxHealth: 500);
            var maxHealth = flatCreature.Health.MaxValue;
            var expectedCeiling = (uint)(maxHealth * 0.5);

            // amount= path: an oversized flat amount is clamped to 0.5x the carrier's own max health
            flatCreature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=1000000 secs=15"))));
            flatCreature.MonsterEffectHeartbeat();
            Assert.AreEqual(expectedCeiling, flatCreature.MonsterEffectStates[0].WardAmount, "an oversized amount= must be clamped to 0.5x max health");

            // pcthp= path: the same clamp covers the resolved amount regardless of which authoring path produced it
            var pctCreature = TestCreatures.CreateDefender(maxHealth: 500);
            pctCreature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pcthp=1.0 secs=15"))));
            pctCreature.MonsterEffectHeartbeat();
            Assert.AreEqual(expectedCeiling, pctCreature.MonsterEffectStates[0].WardAmount, "pcthp=1.0 of max health must be clamped to 0.5x max health the same way");

            // an amount under the cap is untouched
            var underCapCreature = TestCreatures.CreateDefender(maxHealth: 500);
            underCapCreature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=100 secs=15"))));
            underCapCreature.MonsterEffectHeartbeat();
            Assert.AreEqual(100u, underCapCreature.MonsterEffectStates[0].WardAmount, "an amount under the cap is untouched");
        }

        [TestMethod]
        public void Disabled_TunableStopsBothTheGrantAndTheAbsorb()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));

            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "the master switch must stop the grant entirely");

            var attacker = TestCreatures.CreateAttacker();
            var absorbed = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20);
            Assert.AreEqual(20u, absorbed, "with no ward granted, the full amount must land");
        }
    }
}

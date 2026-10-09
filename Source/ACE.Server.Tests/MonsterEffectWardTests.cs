using System.Collections.Generic;

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

            // the test hook is a static field shared by every test in the process - never leave one
            // test's delegate installed for the next to (silently) also observe
            WardEffect.AnnounceHookForTests = null;
        }

        /// <summary>
        /// Captures every (transition, message) pair Announce hands to the test hook. A creature built by
        /// TestCreatures carries no PhysicsObj/CurrentLandblock, so this is the only way these tests can
        /// observe that an announcement happened at all - the real network send is unreachable here, exactly
        /// as the project's test-tree fact says (no live Player/Session can be built in this tree).
        /// </summary>
        private static List<(WardTransition Transition, string Message)> CaptureAnnouncements()
        {
            var captured = new List<(WardTransition, string)>();
            WardEffect.AnnounceHookForTests = (creature, transition, message) => captured.Add((transition, message));
            return captured;
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
            var absorbedFirst = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
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
            var absorbed = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(20u, absorbed, "with no ward granted, the full amount must land");
        }

        [TestMethod]
        public void Announce_GrantFiresUpExactlyOnce()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));

            var captured = CaptureAnnouncements();

            // first heartbeat: on=spawn grants and must announce UP once
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(1, captured.Count, "a landed grant must announce exactly once");
            Assert.AreEqual(WardTransition.Up, captured[0].Transition);

            // second heartbeat: the spawn sentinel holds, so no further grant and no further announcement
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(1, captured.Count, "on=spawn must never regrant (and never re-announce) once Announced is set");
        }

        [TestMethod]
        public void Announce_DrainingHitFiresShatteredAndAPartialDrainFiresNothing()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));
            creature.MonsterEffectHeartbeat();

            var captured = CaptureAnnouncements();

            var attacker = TestCreatures.CreateAttacker();

            // a partial drain (20 of 50) must fire nothing
            var partial = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(0u, partial, "the ward must eat the full 20 out of its 50");
            Assert.AreEqual(0, captured.Count, "a partial drain of a live pool must not announce anything");

            // the remaining 30 fully drains the pool - this hit must announce SHATTERED
            var draining = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 30, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(0u, draining, "the ward must eat the remaining 30");
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "the pool is now empty");
            Assert.AreEqual(1, captured.Count, "fully draining a live pool must announce exactly once");
            Assert.AreEqual(WardTransition.Shattered, captured[0].Transition);
        }

        [TestMethod]
        public void Announce_HeartbeatAfterExpiryFiresFadedOnceAndZeroesThePool()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            // secs=0.001 so the pool is already expired well before the next heartbeat below
            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=0.001"))));
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount, "the first heartbeat must grant");

            System.Threading.Thread.Sleep(50);

            var captured = CaptureAnnouncements();

            // second heartbeat: on=spawn (the default) never regrants, so this tick only sees the expiry
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "an expired pool must be zeroed");
            Assert.AreEqual(0.0, creature.MonsterEffectStates[0].WardExpire, "and its expiry cleared alongside it");
            Assert.AreEqual(1, captured.Count, "the lapse must announce exactly once");
            Assert.AreEqual(WardTransition.Faded, captured[0].Transition);

            // a further heartbeat over an already-zeroed pool must not re-announce
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(1, captured.Count, "an already-zeroed pool must not fire FADED a second time");
        }

        [TestMethod]
        public void Announce_HitAfterExpiryBeforeAnyHeartbeatFiresFaded()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=0.001"))));
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount);

            System.Threading.Thread.Sleep(50);

            var captured = CaptureAnnouncements();

            var attacker = TestCreatures.CreateAttacker();
            // no heartbeat runs between the grant and this hit - OnIncomingDamage alone must catch the lapse
            var landed = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(20u, landed, "an already-expired pool absorbs nothing, so the full hit lands");
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "the lapsed pool must be zeroed");
            Assert.AreEqual(1, captured.Count, "the lapse caught on a hit must announce exactly once");
            Assert.AreEqual(WardTransition.Faded, captured[0].Transition);
        }

        [TestMethod]
        public void Announce_OnHeartbeatWardFiresFadedThenUpOnTheExpiryTickInOrder()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=0.001 on=heartbeat"))));
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount, "the first heartbeat must grant (an empty pool counts as expired)");

            System.Threading.Thread.Sleep(50);

            var captured = CaptureAnnouncements();

            // this tick must see the lapse (FADED) and then, in the same OnHeartbeat call, regrant (UP)
            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount, "on=heartbeat must regrant on the same tick it lapsed");
            Assert.AreEqual(2, captured.Count, "the lapse and the regrant must each announce once");
            Assert.AreEqual(WardTransition.Faded, captured[0].Transition, "FADED must be reported first");
            Assert.AreEqual(WardTransition.Up, captured[1].Transition, "then UP, from the same-tick regrant");
        }

        [TestMethod]
        public void Announce_SilentTrueFiresNoNotifications()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=0.001 on=heartbeat silent=true"))));

            var captured = CaptureAnnouncements();

            // grant (UP)
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount);
            Assert.AreEqual(0, captured.Count, "silent=true must suppress the UP announcement");

            // drain it fully (SHATTERED)
            var attacker = TestCreatures.CreateAttacker();
            creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 50, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount);
            Assert.AreEqual(0, captured.Count, "silent=true must suppress the SHATTERED announcement");

            // regrant, then let it lapse (FADED then UP on the same on=heartbeat tick)
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(50u, creature.MonsterEffectStates[0].WardAmount);
            System.Threading.Thread.Sleep(50);
            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(0, captured.Count, "silent=true must suppress every transition, including FADED and the regrant's UP");
        }

        [TestMethod]
        public void Announce_MessageTextIsPinnedForEachTransition()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=0.001 on=heartbeat"))));

            var captured = CaptureAnnouncements();

            // UP
            creature.MonsterEffectHeartbeat();
            StringAssert.Contains(captured[0].Message, "is surrounded by a shimmering ward!");

            // SHATTERED
            var attacker = TestCreatures.CreateAttacker();
            creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 50, IncomingDamageOrigin.DirectHit);
            StringAssert.Contains(captured[1].Message, "'s ward shatters!");

            // regrant, then let it lapse: FADED, then UP again from the same-tick regrant
            creature.MonsterEffectHeartbeat();
            System.Threading.Thread.Sleep(50);
            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(5, captured.Count);
            StringAssert.Contains(captured[3].Message, "'s ward fades away.");
            StringAssert.Contains(captured[4].Message, "is surrounded by a shimmering ward!");
        }

        /// <summary>
        /// Regression: once SHATTERED has fired and zeroed the pool, a SECOND hit against the now-empty pool
        /// must not re-fire FADED. IsExpired(current, now) returns true both when Amount == 0 and when time
        /// has lapsed, so a naive expiry check with no "was this pool ever live" guard re-announces FADED on
        /// every subsequent hit forever after the pool is first drained.
        /// </summary>
        [TestMethod]
        public void Announce_HitAgainstAnAlreadyShatteredPoolFiresNothing()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));
            creature.MonsterEffectHeartbeat();

            var attacker = TestCreatures.CreateAttacker();

            // drain the pool fully - SHATTERED fires once
            var draining = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 50, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(0u, draining, "the ward must eat the full 50");
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "the pool is now empty");

            var captured = CaptureAnnouncements();

            // a second hit against the now-empty pool must fire nothing and let the full hit land
            var second = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(20u, second, "an already-shattered pool absorbs nothing, so the full hit lands");
            Assert.AreEqual(0, captured.Count, "a hit against an already-shattered pool must not re-announce FADED");
        }

        /// <summary>
        /// Regression: a creature whose ward was never granted (on=hpbelow before the trigger is crossed)
        /// must take damage silently - it has no live pool to expire in the first place.
        /// </summary>
        [TestMethod]
        public void Announce_UngrantedWardFiresNothingOnIncomingDamage()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec("amount=50 secs=15 on=hpbelow trigger=0.5"))));

            // heartbeat at full health: hpbelow's trigger has not been crossed, so no grant happens
            creature.MonsterEffectHeartbeat();
            Assert.AreEqual(0u, creature.MonsterEffectStates[0].WardAmount, "hpbelow must not grant above its trigger");

            var captured = CaptureAnnouncements();

            var attacker = TestCreatures.CreateAttacker();
            var landed = creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(20u, landed, "a never-granted ward absorbs nothing, so the full hit lands");
            Assert.AreEqual(0, captured.Count, "a never-granted ward must not announce FADED on a hit");
        }

        // ---- FormatHitSuffix (the hit-line text) -----------------------------------------------------
        //
        // Pure string formatting, one place both spell-hit call sites (SpellProjectile, life magic) share so
        // the wording can never drift between them.

        [TestMethod]
        public void FormatHitSuffix_NonzeroRemainingUsesThousandsSeparatorAndExactWording()
        {
            // a value under 1000 must carry no grouping separator at all
            Assert.AreEqual(" (Ward: 41 remaining)", WardEffect.FormatHitSuffix(41));

            // a value at/above 1000 must be grouped - this is the digit that actually exercises N0's
            // thousands separator, matching the worked example in the spec (41,880)
            Assert.AreEqual(" (Ward: 41,880 remaining)", WardEffect.FormatHitSuffix(41880));
        }

        [TestMethod]
        public void FormatHitSuffix_ZeroRemainingReportsShattered()
        {
            Assert.AreEqual(" (Ward shattered)", WardEffect.FormatHitSuffix(0));
        }

        // ---- TryGetMonsterWardRemaining ------------------------------------------------------------

        [TestMethod]
        public void TryGetMonsterWardRemaining_NoEffectsReturnsFalse()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);

            Assert.IsFalse(creature.TryGetMonsterWardRemaining(out var remaining), "a creature with no monster effects at all carries no ward");
            Assert.AreEqual(0u, remaining);
        }

        /// <summary>
        /// CONTROL: a creature carrying a non-ward incoming-damage effect (reflect) must report false, not
        /// merely a remaining of 0 - proving the method actually distinguishes "carries a ward" from
        /// "carries some IMonsterIncomingDamage effect".
        /// </summary>
        [TestMethod]
        public void TryGetMonsterWardRemaining_NonWardIncomingDamageEffectReturnsFalse()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);

            MonsterEffectParser.Parse("reflect", out var specs, out var errors);
            Assert.AreEqual(0, errors.Count);

            var reflect = new ReflectEffect();
            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((reflect, specs[0])));

            Assert.IsFalse(creature.TryGetMonsterWardRemaining(out var remaining), "reflect is not a ward and must not be reported as one");
            Assert.AreEqual(0u, remaining);
        }

        [TestMethod]
        public void TryGetMonsterWardRemaining_TrueWithTheGrantedAmountAfterAGrant()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));

            // before the grant: the entry exists, but it has never fired
            Assert.IsTrue(creature.TryGetMonsterWardRemaining(out var beforeGrant), "the ward entry itself makes this true even before any grant");
            Assert.AreEqual(0u, beforeGrant, "nothing has been granted yet");

            creature.MonsterEffectHeartbeat();

            Assert.IsTrue(creature.TryGetMonsterWardRemaining(out var afterGrant));
            Assert.AreEqual(50u, afterGrant, "the full granted amount must be reported");
        }

        [TestMethod]
        public void TryGetMonsterWardRemaining_DecreasesAfterAPartialAbsorb()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));
            creature.MonsterEffectHeartbeat();

            var attacker = TestCreatures.CreateAttacker();
            creature.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.IsTrue(creature.TryGetMonsterWardRemaining(out var remaining));
            Assert.AreEqual(30u, remaining, "50 - 20 = 30 left, matching the pool's own state");
        }

        [TestMethod]
        public void TryGetMonsterWardRemaining_ZeroAfterExpiry()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=0.001"))));
            creature.MonsterEffectHeartbeat();

            Assert.IsTrue(creature.TryGetMonsterWardRemaining(out var beforeExpiry));
            Assert.AreEqual(50u, beforeExpiry);

            System.Threading.Thread.Sleep(50);

            // still reports true (a ward entry is carried), but the lapsed pool must not count toward the sum
            Assert.IsTrue(creature.TryGetMonsterWardRemaining(out var afterExpiry), "the entry is still carried even though the pool has lapsed");
            Assert.AreEqual(0u, afterExpiry, "an expired pool must not be reported as remaining");
        }

        /// <summary>
        /// Mirrors Disabled_TunableStopsBothTheGrantAndTheAbsorb above: the master switch is one of
        /// TryGetMonsterWardRemaining's own early-outs, read live, so a ward granted while the switch was on
        /// must stop being reported the moment it is turned off - matching AbsorbMonsterEffectDamage's own
        /// behavior at the same site.
        /// </summary>
        [TestMethod]
        public void TryGetMonsterWardRemaining_DisabledTunableReturnsFalse()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            creature.Health.Current = 500;

            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("amount=50 secs=15"))));
            creature.MonsterEffectHeartbeat();

            Assert.IsTrue(creature.TryGetMonsterWardRemaining(out var whileEnabled), "sanity: the ward is up before the switch is touched");
            Assert.AreEqual(50u, whileEnabled);

            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            Assert.IsFalse(creature.TryGetMonsterWardRemaining(out var whileDisabled), "the master switch must stop the method entirely, not just zero its result");
            Assert.AreEqual(0u, whileDisabled);

            // RestoreTunables (TestCleanup) already restores monster_effects_enabled, but this test flips it
            // mid-method rather than at setup, so restore explicitly too in case a future assertion is added
            // between here and cleanup and throws before TestCleanup would otherwise run - never leave a
            // process-wide switch flipped off for a test that follows this one in the same run.
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);
        }

        // NOTE: a "TryGetMonsterWardRemaining returns false for a Player" test is deliberately not included
        // here. No test in this project constructs a live Player - PersonalVendorTests.cs and
        // StageTestCommandsTests.cs both document (and StageTestCommandsTests.cs re-verified by grepping this
        // project for "new Player(") that Player's constructor calls
        // DatabaseManager.Authentication.GetAccountById unconditionally, a live MySQL round-trip unavailable
        // here, and SetEphemeralValues separately requires a real client dat lookup. Faking a Player via
        // FormatterServices.GetUninitializedObject was rejected there for the same reason it would be rejected
        // here: it skips every field initializer up the WorldObject/Container/Creature chain, so almost any
        // method touched afterward NREs on a null collection. The `this is Player` early return is therefore
        // untestable in this project the same way it is for every other Player-specific branch in the
        // codebase; every other test in this class exercises the Creature side of the same method instead.
    }
}

using System.Runtime.CompilerServices;

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
    /// ReflectEffect: on=hit sends back a FLAT amount scaled by the reflecting monster's level
    /// (monster_effect_reflect_per_level, optional mult=, clamped by monster_effect_damage_rider_cap), only for
    /// an initial direct hit (IncomingDamageOrigin.DirectHit) from an attacker within
    /// monster_effect_reflect_max_range metres. on=avoid is a flat amount (flat=, dispatched from OnAvoided,
    /// called directly here - see MonsterEffectAvoidIntegrationTests for on=avoid reached through the real
    /// avoidance roll).
    ///
    /// Every on=hit test places both creatures with TestCreatures.AttachBarePhysics, because the range gate
    /// measures WorldObject.GetCylinderDistance and fails closed without a PhysicsObj: a test that forgot the
    /// placement would see "no reflect" for the wrong reason. Each negative case therefore has a positive
    /// control that differs from it in exactly one input.
    /// </summary>
    [TestClass]
    public class MonsterEffectReflectTests
    {
        private static readonly string[] SeededDoubles =
        {
            "monster_effect_reflect_per_level",
            "monster_effect_reflect_max_range",
            "monster_effect_damage_rider_cap",
            "monster_effect_reflect_cooldown",
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Seeds every key these tests read to its code default, so a run of this class alone and a run after
        /// some other class moved a key both start from the same numbers.
        /// </summary>
        [TestInitialize]
        public void SeedTunables()
        {
            RestoreTunables();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            foreach (var key in SeededDoubles)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        private static readonly ReflectEffect Effect = new ReflectEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"reflect {args}".Trim(), out var specs, out var errors);
            Assert.AreEqual(1, specs.Count, string.Join(" | ", errors));
            return specs[0];
        }

        /// <summary>
        /// A reflecting monster of the given level at x=50 and an attacker at 100 health standing
        /// <paramref name="distance"/> metres away along x.
        /// </summary>
        private static (Creature defender, Creature attacker) Pair(int level, float distance, string args = "on=hit")
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;
            defender.Health.Current = 500;
            defender.Level = level;

            TestCreatures.AttachBarePhysics(defender, 50.0f);
            TestCreatures.AttachBarePhysics(attacker, 50.0f + distance);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec(args))));

            return (defender, attacker);
        }

        // ---- authoring -------------------------------------------------------------------------------------

        [TestMethod]
        public void Validate_RejectsPctAndAcceptsTheFlatShapes()
        {
            // pct= is gone from reflect entirely, whatever trigger it rides with
            Assert.IsFalse(Effect.Validate(Spec("pct=0.5"), out var pctError), "on=hit pct= must be rejected");
            StringAssert.Contains(pctError, "pct=");
            Assert.IsFalse(Effect.Validate(Spec("pct=0.5 on=hit"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=hit,avoid pct=0.5 flat=15"), out _), "pct= alongside a valid flat= is still rejected");
            Assert.IsFalse(Effect.Validate(Spec("on=avoid pct=0.5 flat=15"), out _));

            // on=hit needs no argument at all now; mult= is optional and must be positive when present
            Assert.IsTrue(Effect.Validate(Spec("on=hit"), out var bareError), bareError);
            Assert.IsTrue(Effect.Validate(Spec(""), out _), "on defaults to hit");
            Assert.IsTrue(Effect.Validate(Spec("on=hit mult=1.5"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=hit mult=0"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=hit mult=-1"), out _));

            // on=avoid unchanged: flat= > 0 required
            Assert.IsTrue(Effect.Validate(Spec("on=avoid flat=15"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=avoid"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=avoid flat=0"), out _));
            Assert.IsTrue(Effect.Validate(Spec("on=hit,avoid flat=15"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=hit,avoid"), out _), "on=hit,avoid still needs flat= for its avoid half");
        }

        [TestMethod]
        public void Validate_PctIsRejectedThroughTheRealBuildPath()
        {
            // the same rejection seen through MonsterEffectSet.Build, which is what a weenie actually goes
            // through at load: a pct= record must be dropped with an error naming it, not built
            MonsterEffectParser.Parse("reflect pct=0.2 on=hit", out var specs, out _);

            var errors = new System.Collections.Generic.List<string>();
            var set = MonsterEffectSet.Build(specs, errors);

            Assert.IsTrue(set == null || set.Count == 0, "no reflect record may survive the build");
            Assert.IsTrue(errors.Exists(e => e.Contains("pct=")), string.Join(" | ", errors));
        }

        // ---- (a) magnitude is level x rate -----------------------------------------------------------------

        [TestMethod]
        public void Magnitude_IsLevelTimesTheRate_TwoLevelsGiveTwoAmounts()
        {
            var (low, lowAttacker) = Pair(level: 100, distance: 1.0f);
            var (high, highAttacker) = Pair(level: 220, distance: 1.0f);

            Assert.AreEqual(20u, low.AbsorbMonsterEffectDamage(lowAttacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit),
                "a reflect returns the incoming amount unchanged");
            high.AbsorbMonsterEffectDamage(highAttacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(75u, lowAttacker.Health.Current, "level 100 x 0.25 = 25 reflected");
            Assert.AreEqual(45u, highAttacker.Health.Current, "level 220 x 0.25 = 55 reflected");
        }

        [TestMethod]
        public void Magnitude_IsIndependentOfTheDamageTaken()
        {
            // the old shape reflected a fraction of the hit; the new one must not move with it
            var (a, aAttacker) = Pair(level: 100, distance: 1.0f);
            var (b, bAttacker) = Pair(level: 100, distance: 1.0f);

            a.AbsorbMonsterEffectDamage(aAttacker, DamageType.Slash, 5, IncomingDamageOrigin.DirectHit);
            b.AbsorbMonsterEffectDamage(bAttacker, DamageType.Slash, 400, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(75u, aAttacker.Health.Current);
            Assert.AreEqual(75u, bAttacker.Health.Current);
        }

        [TestMethod]
        public void Magnitude_FollowsTheLiveRateTunable()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_reflect_per_level", 0.5));

            var (defender, attacker) = Pair(level: 100, distance: 1.0f);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(50u, attacker.Health.Current, "level 100 x 0.5 = 50");
        }

        [TestMethod]
        public void Magnitude_MultScalesTheLevelAmount()
        {
            var (defender, attacker) = Pair(level: 100, distance: 1.0f, args: "on=hit mult=2");

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(50u, attacker.Health.Current, "level 100 x 0.25 x mult 2 = 50");
        }

        [TestMethod]
        public void Magnitude_IsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 10));

            var (defender, attacker) = Pair(level: 220, distance: 1.0f);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(90u, attacker.Health.Current, "55 clamped to the cap of 10");
        }

        [TestMethod]
        public void ComputeHitReflect_RoundsOnceAndClamps()
        {
            Assert.AreEqual(55u, ReflectEffect.ComputeHitReflect(220, 0.25, 1.0, 10000));
            Assert.AreEqual(55u, ReflectEffect.ComputeHitReflect(221, 0.25, 1.0, 10000), "55.25 rounds to 55");
            Assert.AreEqual(56u, ReflectEffect.ComputeHitReflect(223, 0.25, 1.0, 10000), "55.75 rounds to 56");
            Assert.AreEqual(10u, ReflectEffect.ComputeHitReflect(220, 0.25, 1.0, 10), "clamped to the cap");
            Assert.AreEqual(0u, ReflectEffect.ComputeHitReflect(0, 0.25, 1.0, 10000), "a level-less monster reflects nothing");
        }

        // ---- (b) the close-range gate -------------------------------------------------------------------------

        [TestMethod]
        public void Range_MeleeShapedHit_WithinReflects_BeyondDoesNot()
        {
            // a melee strike reaches here as Monster_Melee / Player.DamageTarget -> Creature.TakeDamage(...,
            // DirectHit); driven through that same TakeDamage entry point
            var (near, nearAttacker) = Pair(level: 100, distance: 1.0f);
            var (far, farAttacker) = Pair(level: 100, distance: 8.0f);

            near.TakeDamage(nearAttacker, DamageType.Slash, 20, false, IncomingDamageOrigin.DirectHit);
            far.TakeDamage(farAttacker, DamageType.Slash, 20, false, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(75u, nearAttacker.Health.Current, "1 m is inside the 5 m default");
            Assert.AreEqual(100u, farAttacker.Health.Current, "8 m is outside it");
            Assert.AreEqual(480u, far.Health.Current, "the out-of-range hit still landed in full - only the reflect is gated");
        }

        [TestMethod]
        public void Range_MissileHit_FromDistanceDoesNotReflect_ButPointBlankDoes()
        {
            // a monster/pet missile impact reaches here as ProjectileCollisionHelper ->
            // Creature.TakeDamage(..., false, DirectHit): the same origin as melee, so the RANGE is the only
            // thing standing between an archer at 30 m and a reflect
            var (sniped, archer) = Pair(level: 100, distance: 30.0f);
            var (pointBlank, closeArcher) = Pair(level: 100, distance: 4.0f);

            sniped.TakeDamage(archer, DamageType.Pierce, 20, false, IncomingDamageOrigin.DirectHit);
            pointBlank.TakeDamage(closeArcher, DamageType.Pierce, 20, false, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u, archer.Health.Current, "30 m: no reflect");
            Assert.AreEqual(75u, closeArcher.Health.Current, "4 m: reflect");
        }

        [TestMethod]
        public void Range_SpellProjectile_OrdinaryCastIsADirectHit_AndIsRangeGated()
        {
            // SpellProjectile.DamageTarget passes the projectile's own IncomingDamageOrigin to the dispatch;
            // an ordinary cast's projectile must classify as DirectHit, and then only the range decides
            var bolt = BareProjectile();
            Assert.AreEqual(IncomingDamageOrigin.DirectHit, bolt.IncomingDamageOrigin);

            var (near, nearCaster) = Pair(level: 100, distance: 3.0f);
            var (far, farCaster) = Pair(level: 100, distance: 25.0f);

            near.AbsorbMonsterEffectDamage(nearCaster, DamageType.Fire, 20, bolt.IncomingDamageOrigin);
            far.AbsorbMonsterEffectDamage(farCaster, DamageType.Fire, 20, bolt.IncomingDamageOrigin);

            Assert.AreEqual(75u, nearCaster.Health.Current, "a war bolt from 3 m reflects");
            Assert.AreEqual(100u, farCaster.Health.Current, "a war bolt from 25 m does not");
        }

        [TestMethod]
        public void Range_MonsterRiposteCounter_IsADirectHit_NearReflects_FarDoesNot()
        {
            // A riposte counter-swing is a real melee strike on its target. Driven through the REAL strike
            // body, RiposteEffect.DealCounterDamage, against a Creature target (a combat pet or faction mob)
            // carrying reflect on=hit; the only difference between the two halves is the distance.
            var (nearTarget, nearRiposter) = RipostePair(distance: 1.0f);
            var (farTarget, farRiposter) = RipostePair(distance: 8.0f);

            var nearStart = nearRiposter.Health.Current;
            var farStart = farRiposter.Health.Current;

            RiposteEffect.DealCounterDamage(nearRiposter, nearTarget, null, 1.0);
            RiposteEffect.DealCounterDamage(farRiposter, farTarget, null, 1.0);

            Assert.IsTrue(nearTarget.Health.Current < 500, "control: the near counter landed");
            Assert.IsTrue(farTarget.Health.Current < 500, "control: the far counter landed too - only the reflect is gated");

            Assert.AreEqual(nearStart - 25, nearRiposter.Health.Current, "a counter from 1 m reflects level 100 x 0.25 = 25");
            Assert.AreEqual(farStart, farRiposter.Health.Current, "a counter from 8 m does not reflect");
        }

        /// <summary>
        /// A riposting monster (overpowering, so its counter always lands) and a level 100 reflect on=hit
        /// target standing <paramref name="distance"/> metres away.
        /// </summary>
        private static (Creature target, Creature riposter) RipostePair(float distance)
        {
            var riposter = TestCreatures.CreateAttacker(maxHealth: 500);
            var target = TestCreatures.CreateDefender(maxHealth: 500);

            riposter.Health.Current = riposter.Health.MaxValue;
            target.Health.Current = 500;
            target.Level = 100;

            TestCreatures.AttachBarePhysics(target, 50.0f);
            TestCreatures.AttachBarePhysics(riposter, 50.0f + distance);

            target.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("on=hit"))));

            return (target, riposter);
        }

        [TestMethod]
        public void Range_FollowsTheLiveMaxRangeTunable()
        {
            var (defender, attacker) = Pair(level: 100, distance: 8.0f);

            // CONTROL: 8 m against the 5 m default
            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(100u, attacker.Health.Current);

            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_reflect_max_range", 10.0));

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.AreEqual(75u, attacker.Health.Current, "the same 8 m hit reflects once the range is 10 m");
        }

        [TestMethod]
        public void Range_NoPhysicsObjFailsClosed()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;
            defender.Level = 100;
            TestCreatures.AttachBarePhysics(defender, 50.0f);   // the attacker is NOT placed

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("on=hit"))));

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u, attacker.Health.Current);
        }

        // ---- (c) no reflect on damage over time or any other secondary damage --------------------------------

        [TestMethod]
        public void DoT_AMonsterDotTickDoesNotReflect_WhileTheSameHitAsADirectHitDoes()
        {
            // The DoT half runs the REAL tick body, DotEffect.DealTick - the exact code the scheduled
            // ActionChain runs - so the Secondary origin under test is the one the production tick states,
            // not one this test hands in.
            var (dotted, dotter) = Pair(level: 100, distance: 1.0f);

            DotEffect.DealTick(dotter, dotted, DamageType.Fire, 20);

            Assert.AreEqual(480u, dotted.Health.Current, "the tick reached the monster and landed");
            Assert.AreEqual(100u, dotter.Health.Current, "but a DoT tick never reflects");

            // CONTROL: identical level, distance, damage and attacker shape - only the origin differs
            var (struck, striker) = Pair(level: 100, distance: 1.0f);

            struck.TakeDamage(striker, DamageType.Fire, 20, false, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(75u, striker.Health.Current, "the same 20 as an initial hit reflects 25");
        }

        [TestMethod]
        public void DoT_EnchantmentTickPathDoesNotReflect()
        {
            // EnchantmentManager's DoT/NetherDoT tick lands through Creature.TakeDamageOverTime
            var (dotted, dotter) = Pair(level: 100, distance: 1.0f);

            dotted.TakeDamageOverTime(20, DamageType.Nether);

            Assert.AreEqual(480u, dotted.Health.Current);
            Assert.AreEqual(100u, dotter.Health.Current);
        }

        [TestMethod]
        public void DoT_TakeDamageWithoutAnOriginIsSecondary()
        {
            // The default every unmarked caller gets (Acid Proc ticks, Poison Weapon, thorns, player reflect,
            // hotspots, smite): a caller that says nothing must never reflect
            var (defender, attacker) = Pair(level: 100, distance: 1.0f);

            defender.TakeDamage(attacker, DamageType.Base, 20);

            Assert.AreEqual(480u, defender.Health.Current);
            Assert.AreEqual(100u, attacker.Health.Current);
        }

        [TestMethod]
        public void Secondary_DerivedSpellProjectilesAreNotDirectHits()
        {
            // each derived-projectile flag alone must be enough to make the projectile Secondary
            var control = BareProjectile();
            Assert.AreEqual(IncomingDamageOrigin.DirectHit, control.IncomingDamageOrigin, "control: an ordinary cast");

            var proc = BareProjectile(); proc.FromProc = true;
            var classProc = BareProjectile(); classProc.IsClassAbilityProc = true;
            var cascade = BareProjectile(); cascade.ClassAbilityGeneration = 1;
            var aoeChild = BareProjectile(); aoeChild.IsClassAbilityAoeChild = true;
            var spawned = BareProjectile(); spawned.IsClassAbilitySpawned = true;
            var echo = BareProjectile(); echo.IsEchoCopy = true;

            Assert.AreEqual(IncomingDamageOrigin.Secondary, proc.IncomingDamageOrigin, "item / cloak proc");
            Assert.AreEqual(IncomingDamageOrigin.Secondary, classProc.IncomingDamageOrigin, "Spellsword proc");
            Assert.AreEqual(IncomingDamageOrigin.Secondary, cascade.IncomingDamageOrigin, "Cascade hop");
            Assert.AreEqual(IncomingDamageOrigin.Secondary, aoeChild.IncomingDamageOrigin, "Spell AOE splash");
            Assert.AreEqual(IncomingDamageOrigin.Secondary, spawned.IncomingDamageOrigin, "class-ability spawned copy");
            Assert.AreEqual(IncomingDamageOrigin.Secondary, echo.IncomingDamageOrigin, "Echo Cast recast");

            // and Secondary really does stop the reflect at point blank
            var (defender, attacker) = Pair(level: 100, distance: 1.0f);
            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Fire, 20, aoeChild.IncomingDamageOrigin);
            Assert.AreEqual(100u, attacker.Health.Current);
        }

        // ---- cooldown ----------------------------------------------------------------------------------------

        [TestMethod]
        public void IsReflectReady_PureCases()
        {
            Assert.IsTrue(ReflectEffect.IsReflectReady(now: 100.0, lastTime: 0.0, cooldownSeconds: 5.0),
                "a fresh (zero) LastTime always lets the first reflect through");
            Assert.IsFalse(ReflectEffect.IsReflectReady(now: 103.0, lastTime: 100.0, cooldownSeconds: 5.0),
                "3 of 5 seconds elapsed: still cooling down");
            Assert.IsTrue(ReflectEffect.IsReflectReady(now: 105.0, lastTime: 100.0, cooldownSeconds: 5.0),
                "exactly the cooldown has elapsed");
            Assert.IsTrue(ReflectEffect.IsReflectReady(now: 200.0, lastTime: 100.0, cooldownSeconds: 5.0),
                "well past the cooldown");
            Assert.IsTrue(ReflectEffect.IsReflectReady(now: 100.001, lastTime: 100.0, cooldownSeconds: 0.0),
                "cooldownSeconds <= 0 disables the cooldown entirely");
            Assert.IsTrue(ReflectEffect.IsReflectReady(now: 100.0, lastTime: 100.0, cooldownSeconds: -1.0),
                "a negative cooldown also disables it");
        }

        [TestMethod]
        public void Cooldown_TwoQualifyingHitsBackToBack_OnlyTheFirstReflects()
        {
            var (defender, attacker) = Pair(level: 100, distance: 1.0f);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(75u, attacker.Health.Current, "the second hit landed within the 5s default cooldown, so it must not reflect again");
        }

        [TestMethod]
        public void Cooldown_DisabledAtZero_BothHitsReflect()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_reflect_cooldown", 0.0));

            var (defender, attacker) = Pair(level: 100, distance: 1.0f);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(50u, attacker.Health.Current, "cooldown disabled (0): both hits reflect 25 each, proving the default cooldown above actually discriminates");
        }

        [TestMethod]
        public void Cooldown_NotStampedWhenTheHitDoesNotReflect()
        {
            // out of range: the hit lands but never reaches the reflect, so LastTime must stay at its default
            var (defender, attacker) = Pair(level: 100, distance: 8.0f);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u, attacker.Health.Current, "control: out of range, no reflect");
            Assert.AreEqual(0.0, defender.MonsterEffectStates[0].LastTime, "an unreflected hit must not start the cooldown clock");

            // a Secondary-origin hit at point-blank range also never reaches the reflect
            var (defender2, attacker2) = Pair(level: 100, distance: 1.0f);

            defender2.AbsorbMonsterEffectDamage(attacker2, DamageType.Slash, 20, IncomingDamageOrigin.Secondary);

            Assert.AreEqual(100u, attacker2.Health.Current, "control: Secondary origin, no reflect");
            Assert.AreEqual(0.0, defender2.MonsterEffectStates[0].LastTime, "a non-DirectHit must not start the cooldown clock either");
        }

        // ---- switches and guards -----------------------------------------------------------------------------

        [TestMethod]
        public void Disabled_TunableStopsTheReflect()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var (defender, attacker) = Pair(level: 100, distance: 1.0f);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u, attacker.Health.Current, "the master switch must stop the reflect entirely");
        }

        [TestMethod]
        public void OnAvoidOnly_RecordDoesNotReflectALandedHit()
        {
            var (defender, attacker) = Pair(level: 100, distance: 1.0f, args: "on=avoid flat=15");

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(100u, attacker.Health.Current);
        }

        // ---- on=avoid (unchanged) ----------------------------------------------------------------------------

        /// <summary>
        /// on=avoid's flat magnitude, called directly (no landed-hit damage figure exists on an avoided
        /// attack - see the class doc comment). Not routed through AbsorbMonsterEffectDamage, since on=avoid
        /// is not dispatched there at all.
        /// </summary>
        [TestMethod]
        public void OnAvoided_DealsAFlatAmountBackAtTheAttacker()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;

            var spec = Spec("on=avoid flat=15");
            var state = new MonsterEffectState();

            Effect.OnAvoided(defender, attacker, spec, ref state);

            Assert.AreEqual(85u, attacker.Health.Current, "15 flat damage reflected back at the attacker");
        }

        /// <summary>
        /// The cap tunable for on=avoid's flat magnitude is monster_effect_damage_rider_cap.
        /// </summary>
        [TestMethod]
        public void OnAvoided_FlatIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 5));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;

            var spec = Spec("on=avoid flat=15");
            var state = new MonsterEffectState();

            Effect.OnAvoided(defender, attacker, spec, ref state);

            Assert.AreEqual(95u, attacker.Health.Current, "flat=15 clamped to the cap of 5");
        }

        /// <summary>
        /// A SpellProjectile with every property at its default, built without running a constructor:
        /// IncomingDamageOrigin reads only auto-properties, so nothing else needs to exist.
        /// </summary>
        private static SpellProjectile BareProjectile() =>
            (SpellProjectile)RuntimeHelpers.GetUninitializedObject(typeof(SpellProjectile));
    }
}

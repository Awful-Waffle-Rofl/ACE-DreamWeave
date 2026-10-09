using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ImmuneEffect: an externally toggled damage immunity, filtering every point of incoming damage to zero
    /// while the carrying creature's ephemeral <see cref="Creature.P_DigsiteImmune"/> flag is set.
    ///
    /// Exercised through the REAL dispatch site (Creature.AbsorbMonsterEffectDamage), the same way
    /// MonsterEffectReflectTests and MonsterEffectManaBarrierTests exercise theirs - an immunity asserted only
    /// against a direct OnIncomingDamage call would not prove the record is reached at all.
    ///
    /// The point of the whole effect is that PropertyBool.Invincible does NOT cover spell damage on a monster
    /// (both spell-side checks gate on the target being a Player), so the digsite's immune phase rides this
    /// hook instead. What is testable here is the FILTER; that the hook is wired at all three damage sinks
    /// (Creature.TakeDamage, SpellProjectile.DamageTarget, WorldObject.HandleCastSpell_Boost) is
    /// AbsorbMonsterEffectDamage's own contract, stated in its doc comment.
    /// </summary>
    [TestClass]
    public class MonsterEffectImmuneTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

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

            // read by the reflect record the ordering tests carry
            foreach (var key in new[] { "monster_effect_reflect_per_level", "monster_effect_reflect_max_range", "monster_effect_damage_rider_cap" })
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        private static readonly ImmuneEffect Effect = new ImmuneEffect();

        private static readonly ReflectEffect Reflect = new ReflectEffect();

        private static MonsterEffectSpec Spec(string args = "")
        {
            MonsterEffectParser.Parse(("immune " + args).Trim(), out var specs, out var errors);

            Assert.AreEqual(1, specs.Count, string.Join(" | ", errors));

            return specs[0];
        }

        // ---- authoring -----------------------------------------------------------------------------------

        [TestMethod]
        public void A_bare_immune_record_is_valid_and_needs_no_args()
        {
            // An immune record on a creature nothing ever toggles is permanently inert, which is exactly what
            // it should be: it ships attached to the boss and is switched on in place.
            Assert.IsTrue(Effect.Validate(Spec(), out var error), error);
            Assert.IsTrue(Effect.Validate(Spec("silent=true"), out _));
            Assert.IsTrue(Effect.Validate(Spec("silent=false"), out _));
            Assert.IsTrue(Effect.Validate(Spec("silent=1"), out _));
            Assert.IsTrue(Effect.Validate(Spec("silent=no"), out _));
        }

        [TestMethod]
        public void A_mis_typed_silent_is_rejected_rather_than_read_as_its_default()
        {
            // GetBool is total, so "silent=ture" would silently read as whatever the caller's default is and
            // leave an author certain they had silenced something they had not.
            Assert.IsFalse(Effect.Validate(Spec("silent=ture"), out var error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
            StringAssert.Contains(error, "silent=");

            Assert.IsFalse(Effect.Validate(Spec("silent=maybe"), out _));
        }

        [TestMethod]
        public void The_registry_resolves_the_authored_kind_to_this_handler()
        {
            Assert.IsTrue(MonsterEffectRegistry.IsKnownKind("immune"));
            Assert.IsTrue(MonsterEffectRegistry.TryGetHandler("immune", out var handler));
            Assert.IsInstanceOfType(handler, typeof(ImmuneEffect));
        }

        [TestMethod]
        public void It_runs_ahead_of_every_reader_on_its_hook()
        {
            // A reflect or a leech sizes itself from the damage figure this effect changes, so "immune" has to
            // mean immune rather than "immune, but the reflect still fires".
            Assert.AreEqual(MonsterEffectDispatchOrder.DamageMutator, Effect.DispatchOrder);
            Assert.IsTrue(Effect.DispatchOrder < MonsterEffectDispatchOrder.DamageReader);
        }

        // ---- the filter ----------------------------------------------------------------------------------

        [TestMethod]
        public void A_fresh_creature_is_not_immune()
        {
            Assert.IsFalse(TestCreatures.CreateDefender(maxHealth: 500).P_DigsiteImmune,
                "the flag is ephemeral and starts clear; nothing but the driver ever sets it");
        }

        [TestMethod]
        public void With_the_flag_CLEAR_the_damage_is_passed_through_untouched()
        {
            // THE COMMON PATH. A boss carries this record for the whole fight and is immune for a few seconds
            // of it; every other hit must cost exactly one bool read and arrive unchanged.
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));

            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
            Assert.AreEqual(7u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Fire, 7, IncomingDamageOrigin.DirectHit));
        }

        [TestMethod]
        public void With_the_flag_SET_every_point_is_filtered_to_zero_whatever_the_damage_type()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));

            defender.P_DigsiteImmune = true;

            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Fire, 9999, IncomingDamageOrigin.DirectHit),
                "the whole point: this is the hook spell damage reaches, which PropertyBool.Invincible does not filter on a monster");
            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Health, 1, IncomingDamageOrigin.DirectHit));
        }

        [TestMethod]
        public void Clearing_the_flag_makes_the_boss_killable_again_on_the_very_next_hit()
        {
            // The phase ends when the last add dies, or on the failsafe timeout. Either way the shell has to
            // come off immediately - a boss that stayed immune after "the shell cracks" would read as the
            // fight being broken.
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));

            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));

            defender.P_DigsiteImmune = true;
            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));

            defender.P_DigsiteImmune = false;
            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
        }

        [TestMethod]
        public void A_zero_damage_hit_stays_zero_and_is_not_announced_as_an_immunity()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            var set = MonsterEffectSet.BuildFromHandlers((Effect, Spec()));

            defender.AttachMonsterEffectsForTest(set);
            defender.P_DigsiteImmune = true;

            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 0, IncomingDamageOrigin.DirectHit));

            var slot = TestCreatures.MonsterEffectSlot(defender, Effect);

            Assert.IsTrue(slot >= 0, "the record must actually be attached for this assertion to mean anything");
            Assert.IsFalse(defender.MonsterEffectStates[slot].Announced,
                "an attack that was already doing nothing must not spend the one announcement");
        }

        [TestMethod]
        public void The_immunity_is_announced_at_most_once()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));
            defender.P_DigsiteImmune = true;

            var slot = TestCreatures.MonsterEffectSlot(defender, Effect);

            Assert.IsFalse(defender.MonsterEffectStates[slot].Announced);

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.IsTrue(defender.MonsterEffectStates[slot].Announced, "the first filtered hit latches it");

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);
            Assert.IsTrue(defender.MonsterEffectStates[slot].Announced, "and it stays latched");
        }

        [TestMethod]
        public void silent_true_spends_no_announcement_at_all()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("silent=true"))));
            defender.P_DigsiteImmune = true;

            var slot = TestCreatures.MonsterEffectSlot(defender, Effect);

            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit), "still immune");
            Assert.IsFalse(defender.MonsterEffectStates[slot].Announced);
        }

        // ---- ordering, with a control run ------------------------------------------------------------------

        [TestMethod]
        public void A_reader_on_the_same_hook_sees_the_zeroed_figure_not_the_thrown_one()
        {
            // The CONTROL half of this test is the first block: the same boss, the same reflect record, the
            // flag clear - and the attacker demonstrably takes the reflected damage. Without it, the second
            // block would pass just as happily against an immune effect that did nothing, because a reflect
            // that never fired for some unrelated reason looks identical.
            var control = TestCreatures.CreateDefender(maxHealth: 500);
            var controlAttacker = TestCreatures.CreateDefender(maxHealth: 500);

            controlAttacker.Health.Current = 100;
            control.Health.Current = 500;

            PlaceForReflect(control, controlAttacker);

            control.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec()), (Reflect, ReflectSpec())));

            Assert.AreEqual(20u, control.AbsorbMonsterEffectDamage(controlAttacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
            Assert.AreEqual(90u, controlAttacker.Health.Current, "not immune: level 40 x 0.25 = 10 comes back");

            // the same set, the flag set
            var immune = TestCreatures.CreateDefender(maxHealth: 500);
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;
            immune.Health.Current = 500;

            PlaceForReflect(immune, attacker);

            immune.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Effect, Spec()), (Reflect, ReflectSpec())));

            immune.P_DigsiteImmune = true;

            Assert.AreEqual(0u, immune.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
            Assert.AreEqual(100u, attacker.Health.Current,
                "the reflect must size itself from the zeroed figure - immune means immune, not 'immune but the reflect still fires'");
        }

        [TestMethod]
        public void The_authored_order_does_not_change_the_outcome_because_the_set_sorts_by_dispatch_order()
        {
            // Authoring "reflect; immune" rather than "immune; reflect" must not hand the reflect the
            // pre-immunity figure.
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;

            PlaceForReflect(defender, attacker);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (Reflect, ReflectSpec()), (Effect, Spec())));

            // CONTROL: flag clear, the same placement and records - the reflect fires
            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
            Assert.AreEqual(90u, attacker.Health.Current);

            defender.P_DigsiteImmune = true;

            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
            Assert.AreEqual(90u, attacker.Health.Current);
        }

        // ---- the overlay the digsite boss actually carries ---------------------------------------------------

        [TestMethod]
        public void The_composed_Boss_Rush_overlay_carries_a_record_that_really_filters()
        {
            // THE END-TO-END HALF. Every other test here attaches the handler directly; this one starts from
            // the string MlDigsiteSpawner composes for a boss whose rolled set runs an immune mechanic, runs
            // it through the REAL parser and the REAL registry, and only then drives the dispatch site. That
            // is what catches the defect the direct tests cannot see: the flag being set by the driver with
            // no record on the boss to read it, so the "shell" is a chat line over a boss taking full damage.
            var set = new MlDigsiteMechanicSet(3, MlDigsiteMechanic.ImmunePhases, MlDigsiteMechanic.Drums, 0.4, 1.1);

            var overlay = MlDigsiteBossMechanicRules.ComposeBossOverlay(MlDigsiteRules.DefaultBossRushMechanic, set);

            MonsterEffectParser.Parse(overlay, out var specs, out var parseErrors);

            Assert.AreEqual(0, parseErrors.Count, string.Join(" | ", parseErrors));
            Assert.AreEqual(2, specs.Count, "the authored ward survives and the immune record joins it");

            var buildErrors = new List<string>();
            var effects = MonsterEffectSet.Build(specs, buildErrors);

            Assert.IsNotNull(effects, string.Join(" | ", buildErrors));
            Assert.AreEqual(0, buildErrors.Count, string.Join(" | ", buildErrors));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(effects);
            defender.Health.Current = defender.Health.MaxValue;

            // CONTROL: the same boss, the same composed overlay, the flag clear. Without this the assertion
            // below would pass just as happily against an overlay that filtered everything unconditionally.
            Assert.AreEqual(37u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 37, IncomingDamageOrigin.DirectHit),
                "with the phase off the boss takes its damage in full");

            defender.P_DigsiteImmune = true;

            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 37, IncomingDamageOrigin.DirectHit),
                "and the record composed onto the boss is what makes the immune phase mean anything");
            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Fire, 9999, IncomingDamageOrigin.DirectHit));

            defender.P_DigsiteImmune = false;

            Assert.AreEqual(37u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 37, IncomingDamageOrigin.DirectHit),
                "and the shell comes off again on the very next hit");
        }

        [TestMethod]
        public void A_set_with_no_immune_mechanic_composes_no_record_and_its_boss_is_never_immune()
        {
            // The composition is conditional, so a volatile or drum set's boss carries exactly the effects it
            // carried before the mechanics landed. A stray P_DigsiteImmune on one of those - which nothing
            // sets, but which is one line away from being possible - must not silently make it immortal.
            var set = new MlDigsiteMechanicSet(1, MlDigsiteMechanic.Volatile, MlDigsiteMechanic.None, 0.25, 1.3);

            var overlay = MlDigsiteBossMechanicRules.ComposeBossOverlay(MlDigsiteRules.DefaultBossRushMechanic, set);

            Assert.IsFalse(overlay.Contains("immune"), overlay);

            MonsterEffectParser.Parse(overlay, out var specs, out _);

            var effects = MonsterEffectSet.Build(specs, null);

            Assert.IsNotNull(effects);

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(effects);
            defender.Health.Current = defender.Health.MaxValue;
            defender.P_DigsiteImmune = true;

            Assert.AreEqual(37u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 37, IncomingDamageOrigin.DirectHit),
                "no record, no filter - the flag alone does nothing");
        }

        // ---- the master switch -----------------------------------------------------------------------------

        [TestMethod]
        public void The_monster_effects_master_switch_also_turns_the_immunity_off()
        {
            // Pinned deliberately rather than left to be discovered: monster_effects_enabled=false is the
            // framework's whole-system kill switch, so an operator who throws it during a Boss Rush makes the
            // boss killable mid-phase. That is the correct behaviour for a kill switch and it is the one
            // interaction that would otherwise look like the immunity failing.
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));
            defender.P_DigsiteImmune = true;

            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit),
                "the master switch stops every effect, this one included");
        }

        private static MonsterEffectSpec ReflectSpec()
        {
            MonsterEffectParser.Parse("reflect on=hit", out var specs, out var errors);

            Assert.AreEqual(1, specs.Count, string.Join(" | ", errors));

            return specs[0];
        }

        /// <summary>
        /// reflect on=hit only fires for an attacker within monster_effect_reflect_max_range of a monster with
        /// a level, so the ordering tests give the defender level 40 (40 x 0.25 = 10 reflected at the seeded
        /// rate) and stand the attacker 1 m away. Without this every reflect assertion here would pass for the
        /// wrong reason - the range gate failing closed.
        /// </summary>
        private static void PlaceForReflect(Creature defender, Creature attacker)
        {
            defender.Level = 40;

            TestCreatures.AttachBarePhysics(defender, 50.0f);
            TestCreatures.AttachBarePhysics(attacker, 51.0f);
        }

        // ---- IsDigsiteImmuneActive (the attacker-notification predicate) --------------------------------
        //
        // Creature.IsDigsiteImmuneActive is what Player.DamageTarget reads, BEFORE TakeDamage runs, to decide
        // whether the attacker notification reports 0 damage instead of the pre-filter figure. It must agree
        // exactly with what AbsorbMonsterEffectDamage would actually do to the hit - these tests pin the three
        // ways it could disagree, each one a case the RoZ playtest feedback (normal damage numbers shown
        // through three immune phases) would reproduce if this predicate got it wrong.

        private static readonly WardEffect Ward = new WardEffect();

        private static MonsterEffectSpec WardSpec()
        {
            MonsterEffectParser.Parse("ward amount=50", out var specs, out var errors);

            Assert.AreEqual(1, specs.Count, string.Join(" | ", errors));

            return specs[0];
        }

        [TestMethod]
        public void IsDigsiteImmuneActive_is_true_only_with_the_flag_set_and_the_effect_carried()
        {
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));

            Assert.IsFalse(defender.IsDigsiteImmuneActive(), "carried but not yet toggled - a boss between phases");

            defender.P_DigsiteImmune = true;
            Assert.IsTrue(defender.IsDigsiteImmuneActive());

            defender.P_DigsiteImmune = false;
            Assert.IsFalse(defender.IsDigsiteImmuneActive(), "the shell comes off on the very next hit");
        }

        [TestMethod]
        public void IsDigsiteImmuneActive_is_false_when_the_flag_is_set_but_no_effect_is_carried()
        {
            // A stray P_DigsiteImmune with nothing attached to read it - the predicate must not report a hit
            // as zero for a creature ImmuneEffect.OnIncomingDamage never touches.
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.P_DigsiteImmune = true;

            Assert.IsFalse(defender.IsDigsiteImmuneActive());
        }

        [TestMethod]
        public void IsDigsiteImmuneActive_is_false_for_a_ward_only_carrier_even_with_the_flag_set()
        {
            // Scoped to the immune effect only - a ward (or any other incoming-damage filter) must never trip
            // this predicate, even if P_DigsiteImmune happens to be set on the same creature. Every other
            // filter keeps reporting the pre-filter figure, unchanged (owner ruling).
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Ward, WardSpec())));
            defender.P_DigsiteImmune = true;

            Assert.IsFalse(defender.IsDigsiteImmuneActive());
        }

        [TestMethod]
        public void IsDigsiteImmuneActive_is_false_when_the_master_switch_is_off()
        {
            // AbsorbMonsterEffectDamage itself short-circuits with monster_effects_enabled=false and hands the
            // full amount through - this predicate has to agree, or the notification would report 0 for a hit
            // that actually landed in full.
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));
            defender.P_DigsiteImmune = true;

            Assert.IsFalse(defender.IsDigsiteImmuneActive());
        }

        [TestMethod]
        public void IsDigsiteImmuneActive_agrees_with_AbsorbMonsterEffectDamage_across_every_case()
        {
            // The predicate is only useful if it always agrees with the real filter it is standing in for.
            // One creature, driven through flag-clear / flag-set / flag-cleared-again, asserting both the
            // predicate and the actual filtered damage at each step.
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec())));

            Assert.IsFalse(defender.IsDigsiteImmuneActive());
            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));

            defender.P_DigsiteImmune = true;
            Assert.IsTrue(defender.IsDigsiteImmuneActive());
            Assert.AreEqual(0u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));

            defender.P_DigsiteImmune = false;
            Assert.IsFalse(defender.IsDigsiteImmuneActive());
            Assert.AreEqual(20u, defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit));
        }

        [TestMethod]
        public void Player_DamageTarget_suppresses_the_splatter_for_a_digsite_immune_hit_but_not_for_others()
        {
            // Follow-up owner ruling: a hit notification that says 0 damage must not still play a hit sound,
            // a pain sound and a blood splatter. Player.DamageTarget needs a live Session to exercise end to
            // end (ACE.Server.Tests cannot construct one - the same limit MlDigsiteRozR17Tests documents), so
            // this is a source-text pin: the splatter block is gated on the SAME digsiteImmuneBlocked flag the
            // attacker notification reads, and the gate is scoped to the immune effect only.
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs");

            var method = PooledLootSourceText.MethodBody(src,
                "public DamageEvent DamageTarget(Creature target, WorldObject damageSource, float damageMultiplier = 1.0f, bool primaryTarget = true, bool multiShotArrow = false)");

            StringAssert.Contains(method, "var digsiteImmuneBlocked = targetPlayer == null && damageEvent.HasDamage && target.IsDigsiteImmuneActive();",
                "the same predicate drives both the notification and the splatter suppression");
            // The notification reads the same flag through Player.AttackerNotificationDamage (it also reports what an Attack/Defend
            // crystal actually took, Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"); an immune hit still reports 0 whatever the rest.
            StringAssert.Contains(method, "var intDamage = AttackerNotificationDamage(digsiteImmuneBlocked, objectiveDealt, damageEvent.Damage);",
                "the notification is unchanged by this follow-up: it still reads digsiteImmuneBlocked");
            Assert.AreEqual(0u, Player.AttackerNotificationDamage(true, null, 20f), "an immune hit reports 0");
            Assert.AreEqual(20u, Player.AttackerNotificationDamage(false, null, 20f), "any other hit reports the rolled damage");
            StringAssert.Contains(method, "if (targetPlayer == null && !digsiteImmuneBlocked)",
                "splatter is suppressed for an immune hit and ONLY for an immune hit - a ward/barrier/reflect hit keeps splatter, since digsiteImmuneBlocked is scoped to the immune effect alone (IsDigsiteImmuneActive)");
        }
    }
}

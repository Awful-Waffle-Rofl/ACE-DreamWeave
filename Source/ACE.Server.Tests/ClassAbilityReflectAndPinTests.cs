using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Reflect (Vanguard T3) and Pinning Shot (Archer T2) - the two 2026-09-12 overhaul entries that needed a
    /// dispatch site of their own rather than an existing hook.
    ///
    /// EXERCISED THROUGH THE PURE STATIC HELPERS, never through a live Player: Player's static initializer
    /// cannot run under this test host (the same constraint SpellswordReadoutTests and
    /// ClassAbilityAffinityCapReadoutInvariantTests work around the same way). The two runtime rules that
    /// cannot be reduced to arithmetic - "a melee swing is never reflected" and "two reflecting actors
    /// terminate" - are therefore pinned two different ways: as pure decisions
    /// (IsReflectableCombatType / AppliesTo, which the runtime paths actually call) and, for the melee rule,
    /// additionally as a source-level scan of where the dispatch is wired.
    /// </summary>
    [TestClass]
    public class ClassAbilityReflectAndPinTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // The live 2026-08-04 measured saturating rider, expressed as a multiplier: 1 + 2.09/base reproduces
        // a raw added amount of +2.09 (209 percentage points) whatever the base is, guaranteeing the cap
        // tests below actually drive the cap. Same constant and same reasoning as
        // ClassAbilityAffinityCapReadoutInvariantTests.
        private const double SaturatingDelta = 2.09;

        private static double Saturating(double rankBonus) => 1.0 + SaturatingDelta / rankBonus;

        private static double ReflectBase => PropertyManager.GetDouble("class_ability_reflectmagic_chance_base").Item;
        private static double ReflectStep => PropertyManager.GetDouble("class_ability_reflectmagic_chance_step").Item;
        private static double PinBase => PropertyManager.GetDouble("class_ability_pinningshot_chance_base").Item;
        private static double PinStep => PropertyManager.GetDouble("class_ability_pinningshot_chance_step").Item;
        private static double AffinityCap => PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

        // ---- Reflect: the rank ladder -------------------------------------------------------------------

        [TestMethod]
        public void Reflect_RankLadder_Is6_12_18_AtNeutralAffinity()
        {
            Assert.AreEqual(0.06, ReflectBase, 1e-9, "registered default moved - the 6/12/18 ladder is the signed-off spec");
            Assert.AreEqual(0.06, ReflectStep, 1e-9, "registered default moved - the 6/12/18 ladder is the signed-off spec");

            Assert.AreEqual(0.06, ReflectMagicAbility.ReflectChance(1, ReflectBase, ReflectStep, 1.0), 1e-9);
            Assert.AreEqual(0.12, ReflectMagicAbility.ReflectChance(2, ReflectBase, ReflectStep, 1.0), 1e-9);
            Assert.AreEqual(0.18, ReflectMagicAbility.ReflectChance(3, ReflectBase, ReflectStep, 1.0), 1e-9);
        }

        [TestMethod]
        public void Reflect_RankZero_ReflectsNothing()
        {
            Assert.AreEqual(0.0, ReflectMagicAbility.ReflectChance(0, ReflectBase, ReflectStep, 1.0), 0.0);
            Assert.AreEqual(0.0, ReflectMagicAbility.ReflectChance(-1, ReflectBase, ReflectStep, 5.0), 0.0);
        }

        [TestMethod]
        public void Reflect_ZeroEffectiveAffinity_IsBitIdenticalToRankOnly()
        {
            for (var rank = 1; rank <= 3; rank++)
            {
                var rankOnly = ReflectBase + (rank - 1) * ReflectStep;

                // 1.0 is the neutral factor GetClassAbilityAffinityMultiplier returns at zero effective skill
                Assert.AreEqual(rankOnly, ReflectMagicAbility.ReflectChance(rank, ReflectBase, ReflectStep, 1.0), 0.0,
                    $"rank {rank}: a neutral multiplier must leave the chance bit-identical to rank alone");

                // 0.0 is the neutral value of the OLD additive primitive - an easy mistake, and it must
                // degrade to rank-only rather than multiply the whole bonus away
                Assert.AreEqual(rankOnly, ReflectMagicAbility.ReflectChance(rank, ReflectBase, ReflectStep, 0.0), 0.0,
                    $"rank {rank}: a 0.0 multiplier must floor to 1.0, not delete the rank bonus");
            }
        }

        [TestMethod]
        public void Reflect_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const int rank = 3;

            var rankBonus = ReflectBase + (rank - 1) * ReflectStep;
            var multiplier = Saturating(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > AffinityCap, "control: the rider must actually exceed the cap for this test to mean anything");

            var clampedAdded = Math.Min(rawAdded, AffinityCap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            const double gear = 0.0;  // Reflect carries no equipment mod
            var effective = ReflectMagicAbility.ReflectChance(rank, ReflectBase, ReflectStep, multiplier, AffinityCap) * 100.0;

            Assert.AreEqual(effective, skill + affinity + gear, 1e-4,
                "Reflect: Skill + Affinity + Gear must equal Effective exactly");

            // control: reporting the RAW pre-clamp amount would break the sum once the cap bites
            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gear, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }

        // ---- Reflect: projectiles only ------------------------------------------------------------------

        [TestMethod]
        public void Reflect_ReflectsProjectilesOnly_AndNeverAMeleeSwing()
        {
            Assert.IsTrue(ReflectMagicAbility.IsReflectableCombatType(CombatType.Missile),
                "arrows, bolts and thrown weapons are the headline case");
            Assert.IsTrue(ReflectMagicAbility.IsReflectableCombatType(CombatType.Magic),
                "war bolts and void projectiles reach the player as spell PROJECTILES");

            // the hard boundary: a melee swing carries no projectile and must never be reflected, no matter
            // which site calls in
            Assert.IsFalse(ReflectMagicAbility.IsReflectableCombatType(CombatType.Melee),
                "a melee swing is never reflected - this is a hard boundary, not an omission to widen later");
        }

        [TestMethod]
        public void Reflect_IsDispatchedOnlyFromProjectileCollisionSites()
        {
            var serverRoot = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server");

            Assert.IsTrue(Directory.Exists(serverRoot), $"could not locate {serverRoot}");

            // The trailing "(" is load bearing: it matches the DECLARATION and real INVOCATIONS while
            // ignoring prose mentions of the method in doc comments (ReflectMagicAbility's own summary names
            // Player.TryReflectProjectile when it explains where the roll is dispatched from). Without it
            // this test tracks which files talk about the dispatch rather than which files perform it.
            var callers = new DirectoryInfo(serverRoot)
                .GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f.FullName).Contains("TryReflectProjectile("))
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            // the declaration itself plus exactly the two sites a projectile can reach a player
            var expected = new[]
            {
                "Player_ClassAbilityReflectAndPin.cs",  // the dispatch method itself
                "ProjectileCollisionHelper.cs",         // arrows, crossbow bolts, thrown weapons
                "SpellProjectile.cs",                   // war bolts, void projectiles
            }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expected, callers,
                "Reflect is wired to exactly the two projectile collision sites. A new caller must be a " +
                "projectile path - IsReflectableCombatType would refuse a melee one at runtime, and this " +
                "test is the second layer that makes the addition visible.");

            // stated separately so the failure names the rule rather than a set difference
            foreach (var meleePath in new[] { "Monster_Melee.cs", "Player_Combat.cs", "Creature_Melee.cs" })
            {
                var file = Path.Combine(serverRoot, "WorldObjects", meleePath);

                if (!File.Exists(file))
                    continue;

                Assert.IsFalse(File.ReadAllText(file).Contains("TryReflectProjectile"),
                    $"{meleePath} dispatches a projectile reflect - a melee swing must never be reflected");
            }
        }

        // ---- Reflect: the latch ------------------------------------------------------------------------

        [TestMethod]
        public void ReflectLatch_RefusesReentry_SoTwoReflectingActorsTerminate()
        {
            // Models the real loop: actor A reflects, which damages actor B, whose own reflect answers by
            // damaging A, and so on. Every reflect entry point claims the same depth-1 [ThreadStatic] latch,
            // so the SECOND actor's reflect is refused outright and the chain stops.
            var reflects = 0;
            var refused = 0;

            void Bounce(int depth)
            {
                Assert.IsTrue(depth <= 64, "the reflect latch did not terminate the bounce chain");

                if (!Creature.TryEnterReflect())
                {
                    refused++;
                    return;
                }

                try
                {
                    reflects++;
                    Bounce(depth + 1);
                }
                finally
                {
                    Creature.ExitReflect();
                }
            }

            Bounce(0);

            Assert.AreEqual(1, reflects, "exactly ONE reflect may resolve on a thread - a reflect of a reflect is never wanted");
            Assert.AreEqual(1, refused, "the first re-entrant attempt must be refused outright");

            Assert.IsTrue(Creature.TryEnterReflect(), "the latch must be released once the chain unwinds");
            Creature.ExitReflect();
        }

        [TestMethod]
        public void ReflectLatch_IsReleasedEvenWhenTheReflectThrows()
        {
            // Player.TryReflectProjectile releases in a finally; if it ever stopped doing so, the latch would
            // stay claimed and silently disable every later reflect on this thread.
            try
            {
                if (Creature.TryEnterReflect())
                {
                    try
                    {
                        throw new InvalidOperationException("simulated failure inside a reflect");
                    }
                    finally
                    {
                        Creature.ExitReflect();
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // expected
            }

            Assert.IsTrue(Creature.TryEnterReflect(), "a throwing reflect must not strand the latch");
            Creature.ExitReflect();
        }

        // ---- Pinning Shot: the rank ladder --------------------------------------------------------------

        [TestMethod]
        public void PinningShot_RankLadder_Is10_20_30_AtNeutralAffinity()
        {
            Assert.AreEqual(0.10, PinBase, 1e-9, "registered default moved - the 10/20/30 ladder is the signed-off spec");
            Assert.AreEqual(0.10, PinStep, 1e-9, "registered default moved - the 10/20/30 ladder is the signed-off spec");

            Assert.AreEqual(0.10, PinningShotAbility.Chance(1, PinBase, PinStep, 1.0), 1e-9);
            Assert.AreEqual(0.20, PinningShotAbility.Chance(2, PinBase, PinStep, 1.0), 1e-9);
            Assert.AreEqual(0.30, PinningShotAbility.Chance(3, PinBase, PinStep, 1.0), 1e-9);
        }

        [TestMethod]
        public void PinningShot_RankZero_PinsNothing()
        {
            Assert.AreEqual(0.0, PinningShotAbility.Chance(0, PinBase, PinStep, 1.0), 0.0);
            Assert.AreEqual(0.0, PinningShotAbility.Chance(-1, PinBase, PinStep, 5.0), 0.0);
        }

        [TestMethod]
        public void PinningShot_ZeroEffectiveAffinity_IsBitIdenticalToRankOnly()
        {
            for (var rank = 1; rank <= 3; rank++)
            {
                var rankOnly = PinBase + (rank - 1) * PinStep;

                Assert.AreEqual(rankOnly, PinningShotAbility.Chance(rank, PinBase, PinStep, 1.0), 0.0,
                    $"rank {rank}: a neutral multiplier must leave the chance bit-identical to rank alone");

                Assert.AreEqual(rankOnly, PinningShotAbility.Chance(rank, PinBase, PinStep, 0.0), 0.0,
                    $"rank {rank}: a 0.0 multiplier must floor to 1.0, not delete the rank bonus");
            }
        }

        [TestMethod]
        public void PinningShot_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const int rank = 3;

            var rankBonus = PinBase + (rank - 1) * PinStep;
            var multiplier = Saturating(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > AffinityCap, "control: the rider must actually exceed the cap for this test to mean anything");

            var clampedAdded = Math.Min(rawAdded, AffinityCap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            const double gear = 0.0;  // Pinning Shot carries no equipment mod
            var effective = PinningShotAbility.Chance(rank, PinBase, PinStep, multiplier, AffinityCap) * 100.0;

            Assert.AreEqual(effective, skill + affinity + gear, 1e-4,
                "Pinning Shot: Skill + Affinity + Gear must equal Effective exactly");

            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gear, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }

        [TestMethod]
        public void PinningShot_FiresOnMissileHitsOnly()
        {
            Assert.IsTrue(PinningShotAbility.AppliesTo(CombatType.Missile));

            // IOutgoingDamageAbility covers melee AND missile (and multi-shot extra hits), so without this
            // gate every melee swing would roll a pin
            Assert.IsFalse(PinningShotAbility.AppliesTo(CombatType.Melee),
                "Pinning Shot is a MISSILE rider - a melee swing must never pin");
            Assert.IsFalse(PinningShotAbility.AppliesTo(CombatType.Magic),
                "spells do not route through the outgoing-damage hook and must not pin");
        }

        [TestMethod]
        public void PinningShot_DurationAndImmunity_MatchTheSignedOffSpec()
        {
            Assert.AreEqual(5.0, PropertyManager.GetDouble("class_ability_pinningshot_duration_seconds").Item, 1e-9);
            Assert.AreEqual(15.0, PropertyManager.GetDouble("class_ability_pinningshot_immunity_seconds").Item, 1e-9);

            // the immunity must outlast the pin, or the effect chain-locks
            Assert.IsTrue(PropertyManager.GetDouble("class_ability_pinningshot_immunity_seconds").Item
                        > PropertyManager.GetDouble("class_ability_pinningshot_duration_seconds").Item,
                "the immunity window must be longer than the pin, or a second pin can land the instant the first ends");
        }

        // ---- Pinning Shot: the post-pin movement slow ----------------------------------------------------

        private static double SlowSeconds => PropertyManager.GetDouble("class_ability_pinningshot_slow_seconds").Item;
        private static float SlowFactor => (float)PropertyManager.GetDouble("class_ability_pinningshot_slow_factor").Item;

        /// <summary>
        /// A live creature that can be pinned, with MonsterState forced to Return.
        ///
        /// THE STATE IS NOT INCIDENTAL: TryApplyClassAbilityPin calls Creature.CancelMoveTo() in every state
        /// EXCEPT Return, and that dereferences PhysicsObj.MovementManager.MoveToManager - which a fixture
        /// creature built without the dats does not have. Return is the one state whose documented carve-out
        /// skips the cancel, so it is the only state in which the pin can be applied here at all. Nothing
        /// under test reads MonsterState except TickClassAbilityMoveSlow's re-issue guard, which these tests
        /// do not exercise (it needs a physics object by construction).
        /// </summary>
        private static Creature PinnableTarget()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 50);

            target.MonsterState = Creature.State.Return;

            return target;
        }

        [TestMethod]
        public void PinningShotSlow_WindowIsDerivedFromThePinEnd_NotRecomputedFromNow()
        {
            var target = PinnableTarget();

            Assert.IsTrue(target.TryApplyClassAbilityPin(5.0, 15.0), "the fixture target must be pinnable");
            Assert.IsTrue(target.TryApplyClassAbilityMoveSlow(10.0, 0.2f));

            // the ONLY legal derivation: pin end + slowSeconds. A recomputed Timers.RunningTime + slowSeconds
            // would land a full pin duration early and drift further apart the moment either side gains a clamp.
            Assert.AreEqual(target.ClassAbilityPinnedUntil + 10.0, target.ClassAbilityMoveSlowUntil, 1e-9,
                "the slow window must start when the PIN ENDS, derived from ClassAbilityPinnedUntil");

            Assert.AreEqual(0.2f, target.ClassAbilityMoveSlowFactor, 1e-6f);
        }

        [TestMethod]
        public void PinningShotSlow_FactorIsNeutralBefore_TheStoredValueDuring_AndNeutralAfter()
        {
            var target = PinnableTarget();

            Assert.AreEqual(1.0f, target.CurrentMovementSlowFactor, 0.0f,
                "an untouched creature must read exactly 1.0 - every move issuance multiplies by this");

            Assert.IsTrue(target.TryApplyClassAbilityPin(5.0, 15.0));
            Assert.IsTrue(target.TryApplyClassAbilityMoveSlow(10.0, 0.2f));

            Assert.AreEqual(0.2f, target.CurrentMovementSlowFactor, 1e-6f,
                "inside the window the stored factor is what both the wire and the server must see");

            // expire the window by hand rather than waiting 15 seconds. Deliberately NOT set to 0.0, which is
            // the separate "no slow at all" case: this exercises the RunningTime >= until branch specifically.
            target.ClassAbilityMoveSlowUntil = Timers.RunningTime - 0.001;

            Assert.AreEqual(1.0f, target.CurrentMovementSlowFactor, 0.0f,
                "past the window the factor must be neutral again, not the stored 0.2");
        }

        [TestMethod]
        public void PinningShotSlow_FactorOutsideTheOpenInterval_IsRefusedAndReadsNeutral()
        {
            // 0.0 and a negative would stop or reverse the creature; 1.0 and above are not a slow at all
            foreach (var bad in new[] { 0.0f, -0.5f, 1.0f, 2.5f })
            {
                var target = PinnableTarget();

                Assert.IsTrue(target.TryApplyClassAbilityPin(5.0, 15.0));

                Assert.IsFalse(target.TryApplyClassAbilityMoveSlow(10.0, bad),
                    $"a factor of {bad} is outside (0.0, 1.0) and must be refused outright");

                Assert.AreEqual(0.0, target.ClassAbilityMoveSlowUntil, 0.0,
                    $"a refused factor of {bad} must leave no window armed");

                Assert.AreEqual(1.0f, target.CurrentMovementSlowFactor, 0.0f,
                    $"a refused factor of {bad} must read as a neutral 1.0, never as a nonsense speed");
            }
        }

        [TestMethod]
        public void PinningShotSlow_StoredFactorOutsideTheOpenInterval_StillReadsNeutral()
        {
            // the GETTER's guard, independent of the setter's: a value written by any future path (or a
            // half-initialised field) must not reach the movement layer as a speed
            foreach (var bad in new[] { 0.0f, -0.5f, 1.5f })
            {
                var target = PinnableTarget();

                target.ClassAbilityMoveSlowUntil = Timers.RunningTime + 30.0;
                target.ClassAbilityMoveSlowFactor = bad;

                Assert.AreEqual(1.0f, target.CurrentMovementSlowFactor, 0.0f,
                    $"a stored factor of {bad} inside a live window must still read as a neutral 1.0");
            }
        }

        [TestMethod]
        public void PinningShotSlow_ZeroSecondsRefusesTheSlowOutright()
        {
            var target = PinnableTarget();

            Assert.IsTrue(target.TryApplyClassAbilityPin(5.0, 15.0));

            Assert.IsFalse(target.TryApplyClassAbilityMoveSlow(0.0, 0.2f),
                "class_ability_pinningshot_slow_seconds = 0 is the feature's off switch");
            Assert.IsFalse(target.TryApplyClassAbilityMoveSlow(-1.0, 0.2f));

            Assert.AreEqual(0.0, target.ClassAbilityMoveSlowUntil, 0.0);
            Assert.AreEqual(1.0f, target.CurrentMovementSlowFactor, 0.0f);
        }

        [TestMethod]
        public void PinningShotSlow_RequiresALivePin()
        {
            var target = PinnableTarget();

            // no pin applied: the slow is a rider ON a pin, and its window has nothing to derive from
            Assert.IsFalse(target.TryApplyClassAbilityMoveSlow(10.0, 0.2f),
                "a slow with no live pin has no window to derive from and must be refused");
            Assert.AreEqual(0.0, target.ClassAbilityMoveSlowUntil, 0.0);
        }

        [TestMethod]
        public void PinningShotSlow_TunableDefaultsMatchTheSignedOffSpec()
        {
            Assert.AreEqual(10.0, SlowSeconds, 1e-9);
            Assert.AreEqual(0.2, SlowFactor, 1e-6f);
        }

        [TestMethod]
        public void PinningShotSlow_WindowEndsNoLaterThanThePinImmunity()
        {
            var duration = PropertyManager.GetDouble("class_ability_pinningshot_duration_seconds").Item;
            var immunity = PropertyManager.GetDouble("class_ability_pinningshot_immunity_seconds").Item;

            // both windows are measured from the same instant - the pin's end - so comparing the two
            // durations compares the two end times. Mirrors the immunity > duration assertion above.
            Assert.IsTrue(SlowSeconds <= immunity,
                $"the slow ({SlowSeconds}s from the pin end) must not outlast the immunity ({immunity}s from the " +
                "same instant), or a re-proc can land while the previous slow is still running and the two windows overlap");

            Assert.IsTrue(duration > 0.0, "a slow armed from a zero-length pin would start in the past");
        }

        [TestMethod]
        public void PinningShotSlow_StuckCounterDoesNotAccumulateWhileSlowed()
        {
            // what MoveToManager.UseTime does to a slowed creature chasing a fleeing target: CheckProgressMade
            // fails on closing rate every time, so FailProgressCount is incremented on every physics tick.
            // The leg in flight was issued AT the slow factor, which is the case suppression is scoped to.
            var count = 0;

            for (var tick = 0; tick < 8; tick++)
            {
                count++;
                count = Creature.SuppressStuckProgress(count, inFlightSpeed: 0.2f, slowFactor: 0.2f);

                Assert.AreEqual(0, count, $"tick {tick}: a slowed creature's stuck counter must not accumulate");
            }

            // neither consumer's cancel condition can be reached from zero
            Assert.IsFalse(count > 0, "Monster_Navigation.Movement cancels the move when FailProgressCount > 0");
            Assert.IsFalse(count >= 5, "Creature_Navigation.AddMoveToTick cancels the move at FailProgressCount 5");
        }

        [TestMethod]
        public void PinningShotSlow_StuckCounterStillAccumulatesAndCancelsWithNoSlow()
        {
            // THE CONTROL for the test above. A suppression test that passes with the suppression removed is
            // worthless, and so is one that passes because the counter never moves for anybody.
            var count = 0;

            count++;
            count = Creature.SuppressStuckProgress(count, inFlightSpeed: 1.0f, slowFactor: 1.0f);

            Assert.AreEqual(1, count, "an unslowed creature's stuck counter must still accumulate");
            Assert.IsTrue(count > 0, "Monster_Navigation.Movement's cancel condition must stay reachable");

            for (var tick = 0; tick < 4; tick++)
            {
                count++;
                count = Creature.SuppressStuckProgress(count, inFlightSpeed: 1.0f, slowFactor: 1.0f);
            }

            Assert.AreEqual(5, count);
            Assert.IsTrue(count >= 5, "Creature_Navigation.AddMoveToTick's cancel threshold must stay reachable");
        }

        [TestMethod]
        public void PinningShotSlow_StuckSuppressionIsScopedToTheLegIssuedAtTheSlowRate()
        {
            // the suppressed case: a slow is live AND this leg was issued at the slow factor
            Assert.AreEqual(0, Creature.SuppressStuckProgress(4, inFlightSpeed: 0.2f, slowFactor: 0.2f),
                "the slowed leg is the one case stuck detection must be blind to");

            // an EMOTE move. Creature_Navigation.AddMoveToTick is reached only from
            // MoveTo(Position, ..., setLoc: true, ...), whose callers are Vendor.MoveTo(Home) and the three
            // EmoteManager sites passing emote.Extent. Those moves run at their own speed and must keep their
            // stuck detection even while a slow window happens to be open on the same creature.
            Assert.AreEqual(4, Creature.SuppressStuckProgress(4, inFlightSpeed: 0.5f, slowFactor: 0.2f),
                "an emote move at its own speed must keep stuck detection while a slow window is open");
            Assert.AreEqual(4, Creature.SuppressStuckProgress(4, inFlightSpeed: 1.0f, slowFactor: 0.2f),
                "a full-speed leg must keep stuck detection while a slow window is open");

            // a PRE-SLOW leg: TryApplyClassAbilityPin deliberately skips its cancel in State.Return, so a
            // return-home move issued before the slow was armed is still running at 1.0 and really is fast
            Assert.AreEqual(2, Creature.SuppressStuckProgress(2, inFlightSpeed: 1.0f, slowFactor: 0.2f));

            // no slow at all: CurrentMovementSlowFactor is exactly 1.0f outside a live window, and a leg that
            // happens to be slow for some unrelated reason must NOT be suppressed on that account
            Assert.AreEqual(3, Creature.SuppressStuckProgress(3, inFlightSpeed: 0.2f, slowFactor: 1.0f),
                "with no slow live, nothing may be suppressed whatever speed the leg carries");
            Assert.AreEqual(3, Creature.SuppressStuckProgress(3, inFlightSpeed: 1.0f, slowFactor: 1.0f));
        }

        [TestMethod]
        public void PinningShotSlow_AChessPieceCanNeverBePinned()
        {
            // TestCreatures' static ctor initialises the formula tables, and hands out a wcid no other fixture
            // will reuse (the body-part table cache is global and wcid-keyed)
            var wcid = TestCreatures.NextWcid();

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.GamePiece,
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    // ALIVE on purpose: CanBeClassAbilityPinned also refuses a dead creature, so a zero-health
                    // piece would return false without proving anything about the GamePiece exclusion
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 50 } },
                },
            };

            var piece = new GamePiece(weenie, new ObjectGuid(0x7D000000 + wcid));

            Assert.IsFalse(piece.IsDead, "the fixture piece must be alive, or the assertion below proves nothing");

            Assert.IsFalse(piece.CanBeClassAbilityPinned(),
                "GamePiece must be refused at the state boundary, the same way Player and Pet are. That is what " +
                "makes GetMovementParameters' claim that a chess piece is never slowed true by construction " +
                "rather than an argument from no damage path reaching one");

            Assert.IsFalse(piece.TryApplyClassAbilityMoveSlow(10.0, 0.2f),
                "and with no pin possible, no slow can be armed on one either");

            Assert.AreEqual(1.0f, piece.CurrentMovementSlowFactor, 0.0f,
                "so the factor GetMovementParameters reads for a chess piece is always exactly neutral");
        }

        [TestMethod]
        public void PinningShotSlow_IsWiredAtEveryCallSiteItClaims()
        {
            var worldObjects = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects");

            Assert.IsTrue(Directory.Exists(worldObjects), $"could not locate {worldObjects}");

            var monsterNav = File.ReadAllText(Path.Combine(worldObjects, "Monster_Navigation.cs"));
            var creatureNav = File.ReadAllText(Path.Combine(worldObjects, "Creature_Navigation.cs"));
            var monsterTick = File.ReadAllText(Path.Combine(worldObjects, "Monster_Tick.cs"));

            // ---- THE CHASE: server half and wire half must carry the same expression, or the client's dead
            // reckoning and the server's physics disagree about where a slowed creature is.
            Assert.IsTrue(monsterNav.Contains("mvp.Speed = CurrentMovementSlowFactor;"),
                "Monster_Navigation.GetMovementParameters must set the SERVER-side speed from the slow factor");
            Assert.IsTrue(creatureNav.Contains("motion.MoveToParameters.Speed = CurrentMovementSlowFactor;"),
                "Creature_Navigation.GetMoveToMotion must set the WIRE-side speed from the same factor");

            // ---- THE RANGED / CASTING / IMMOBILE TURN. StartTurn's turnTo branch pairs
            // Creature_Navigation.TurnTo(WorldObject, bool) on the wire with PhysicsObj.TurnToObject on the
            // server, and mvp.Speed scales turning OMEGA as well as translation. Missing this was a real
            // divergence: the client tracked at full rate while the server turned at a fifth.
            Assert.IsTrue(creatureNav.Contains("turnToMotion.MoveToParameters.Speed = CurrentMovementSlowFactor;"),
                "Creature_Navigation.TurnTo(WorldObject, bool) must carry the slow factor - it is the WIRE half " +
                "of StartTurn's turnTo branch, whose server half is PhysicsObj.TurnToObject(..., GetMovementParameters())");
            Assert.IsTrue(monsterNav.Contains("PhysicsObj.TurnToObject(AttackTarget.PhysicsObj, mvp);"),
                "and that server half must still take its mvp from GetMovementParameters, or the pair above is " +
                "no longer a pair");

            // ---- THE RETURN-HOME LEG, both halves. The literal 1.0f in each call is walkRunThreshold, NOT speed.
            Assert.IsTrue(monsterNav.Contains("MoveTo(home, RunRate, false, 1.0f, CurrentMovementSlowFactor);"),
                "MoveToHome must pass the slow factor as MoveTo's speed argument (the fifth), not leave it null");
            Assert.IsTrue(creatureNav.Contains("GetMoveToPosition(home, RunRate, 1.0f, CurrentMovementSlowFactor);"),
                "BroadcastMoveTo's return-home branch must carry the slow factor too, or a player entering range " +
                "mid-slow is told the wrong rate");

            // ---- THE DELIBERATE NON-CHANGES. Rotate()/TurnToObject(WorldObject, bool) and TurnTo(Position) are
            // the EMOTE turn helpers, and they have NO server physics turn: each broadcasts, then waits an
            // unscaled GetRotateDelay and snaps Location.Rotation. Slowing only their wire value would CREATE a
            // divergence. Both must therefore keep their Player-only TurnToSpeed assignment and nothing more.
            var turnToSpeedSites = creatureNav.Split(new[] { "turnToMotion.MoveToParameters.Speed = player.TurnToSpeed;" }, StringSplitOptions.None).Length - 1;

            Assert.AreEqual(2, turnToSpeedSites,
                "the two emote turn helpers (TurnToObject and TurnTo(Position)) must each still set Speed for a " +
                "Player only. Adding the slow factor to either would desync the client from the server's " +
                "unscaled rotate-delay snap; removing one means this test no longer guards what it claims");

            // ---- THE RESTORE, and the guard that keeps it off the hot path for everything else
            Assert.IsTrue(monsterTick.Contains("if (ClassAbilityMoveSlowUntil != 0.0)")
                       && monsterTick.Contains("TickClassAbilityMoveSlow();"),
                "Monster_Tick must call TickClassAbilityMoveSlow behind a bare timer compare");

            // ---- THE STUCK-DETECTION SUPPRESSION, at BOTH consumers of MoveToManager.FailProgressCount, and at
            // both keyed on the SPEED OF THE LEG IN FLIGHT rather than on a creature-level flag
            Assert.IsTrue(monsterNav.Contains("SuppressStuckProgress(moveToManager.FailProgressCount, moveToManager.MovementParams?.Speed ?? 1.0f, CurrentMovementSlowFactor)"),
                "Monster_Navigation.Movement is one of the two FailProgressCount consumers and must suppress per leg");
            Assert.IsTrue(creatureNav.Contains("SuppressStuckProgress(failCountManager.FailProgressCount, failCountManager.MovementParams?.Speed ?? 1.0f, CurrentMovementSlowFactor)"),
                "Creature_Navigation.AddMoveToTick is the other FailProgressCount consumer and must suppress per leg");

            // ---- THE GamePiece EXCLUSION, which is what makes GetMovementParameters' chess-piece comment true
            // by construction rather than an argument from absence
            var pinState = File.ReadAllText(Path.Combine(worldObjects, "Creature_ClassAbilityPin.cs"));

            Assert.IsTrue(pinState.Contains("if (this is Player || this is Pet || this is GamePiece)"),
                "CanBeClassAbilityPinned must refuse GamePiece at the state boundary, alongside Player and Pet");
        }

        // ---- Registry wiring ---------------------------------------------------------------------------

        [TestMethod]
        public void Reflect_IsImplemented_AndCarriesNoDamageHook()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.ReflectMagic);

            Assert.IsTrue(handler.Definition.Implemented, "Reflect's mechanic has landed and it should be learnable");
            Assert.IsInstanceOfType(handler, typeof(IPassiveStatAbility),
                "Reflect is dispatched by hand from two projectile sites, so it carries the bespoke marker (as Parry / Shield Block / Pocket Sand do)");

            // it must reach no damage hook at all - every one of these is melee-reachable, and the whole
            // point of the entry is that it only ever sees projectiles
            Assert.IsFalse(handler is IIncomingDamageAbility, "an incoming-damage hook would fire on melee hits");
            Assert.IsFalse(handler is IPreWriteDamageAbility, "a pre-write hook would fire on melee hits and DoT ticks");
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is ISpellHitAbility);
        }

        [TestMethod]
        public void PinningShot_IsImplemented_AndRidesTheOutgoingDamageHook()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.PinningShot);

            Assert.IsTrue(handler.Definition.Implemented, "Pinning Shot's mechanic has landed and it should be learnable");
            Assert.IsInstanceOfType(handler, typeof(IOutgoingDamageAbility),
                "Pinning Shot rides the existing landed-weapon-hit hook rather than a new one");
            Assert.IsTrue(ClassAbilityRegistry.OutgoingDamageAbilities.Contains((IOutgoingDamageAbility)handler),
                "Pinning Shot must be in the OutgoingDamageAbilities bucket");

            Assert.IsFalse(handler is IPassiveStatAbility,
                "a handler that hooks something must not also claim the no-hook marker");
        }

        [TestMethod]
        public void BothEntries_DeclareTheAffinityTheirCodeApplies()
        {
            Assert.AreEqual(Skill.MissileDefense, ClassAbilityRegistry.Get(ClassAbilityId.ReflectMagic).AffinitySkill);
            Assert.AreEqual(Skill.AssessCreature, ClassAbilityRegistry.Get(ClassAbilityId.PinningShot).AffinitySkill);
        }

        [TestMethod]
        public void ReflectMagic_KeepsItsDeliberateTokenDisplayAsymmetry()
        {
            var definition = ClassAbilityRegistry.Get(ClassAbilityId.ReflectMagic);

            // the enum member and token are ReflectMagic / reflect_magic while it DISPLAYS as "Reflect".
            // That asymmetry comes from the design table and must not be normalised.
            // NOT StringAssert.Equals - that resolves to object.Equals(object, object) and asserts nothing.
            // Assert.AreEqual on two const strings folds to MSTEST0032, so compare explicitly.
            Assert.IsTrue(definition.Name == "reflect_magic", $"canonical token must stay reflect_magic, found {definition.Name}");
            Assert.IsTrue(definition.DisplayName == "Reflect", $"display name must stay Reflect, found {definition.DisplayName}");
            Assert.AreEqual(ClassAbilityClass.Vanguard, definition.AbilityClass);
            Assert.AreEqual(3, definition.Tier);
            Assert.AreEqual(3, definition.MaxRank);
        }

        [TestMethod]
        public void PinningShot_KeepsItsSignedOffPlacement()
        {
            var definition = ClassAbilityRegistry.Get(ClassAbilityId.PinningShot);

            Assert.AreEqual(ClassAbilityClass.Archer, definition.AbilityClass);
            Assert.AreEqual(2, definition.Tier, "Pinning Shot takes the Archer T2 slot Heavy Draw vacated");
            Assert.AreEqual(3, definition.MaxRank);
        }
    }
}

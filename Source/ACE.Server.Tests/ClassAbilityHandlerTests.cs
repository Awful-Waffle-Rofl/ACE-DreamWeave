using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Behavior tests for the class ability handlers' math (the registry tests cover wiring).
    /// Covers the parts computable without a live Player/world: pure damage formulas (Multishot,
    /// PoisonWeapon, BattleHardened, Thorns) and the balance-critical tunable invariants (SpellAoe).
    /// The parts that genuinely need landblock/equipment state - Thorns' shield lookup, SpellAoe's
    /// neighbor targeting, Taunt's radius+line-of-sight sweep - are exercised in-game.
    /// </summary>
    [TestClass]
    public class ClassAbilityHandlerTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void Multishot_AppendsOneExtraShotPerRank_WithoutTouchingExistingShots()
        {
            var handler = (IMissileVolleyAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.Multishot);
            var skillMultiplier = (float)PropertyManager.GetDouble("class_ability_multishot_damage_mult").Item;

            // gear-synergy contract: the weapon's own extra arrows are already in the list and
            // the skill must stack on top of them, not replace them
            var multipliers = new List<float> { 1.0f, 0.5f };

            handler.AddExtraShots(null, rank: 2, multipliers);

            Assert.AreEqual(4, multipliers.Count);
            Assert.AreEqual(1.0f, multipliers[0]);
            Assert.AreEqual(0.5f, multipliers[1]);
            Assert.AreEqual(skillMultiplier, multipliers[2]);
            Assert.AreEqual(skillMultiplier, multipliers[3]);

            // extra arrows must deal reduced damage, never more than the primary shot
            Assert.IsTrue(skillMultiplier > 0f && skillMultiplier <= 1f, $"multishot multiplier {skillMultiplier} outside (0, 1]");
        }

        [TestMethod]
        public void Multishot_RankZero_AddsNothing()
        {
            var handler = (IMissileVolleyAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.Multishot);

            var multipliers = new List<float> { 1.0f };
            handler.AddExtraShots(null, rank: 0, multipliers);

            Assert.AreEqual(1, multipliers.Count);
        }

        /// <summary>
        /// Call site: MultishotAbility.PerShotMultiplier (the clamp AddExtraShots and GetReadout both
        /// route through). 2026-10-02 owner ruling: base cut to 0.60 and a new hard cap at 1.00.
        /// </summary>
        [TestMethod]
        public void Multishot_NewDefaults_BaseIsSixtyPercentAndCapIsOneHundred()
        {
            var rankBonus = PropertyManager.GetDouble("class_ability_multishot_damage_mult").Item;
            var cap = PropertyManager.GetDouble("class_ability_multishot_damage_mult_cap").Item;

            Assert.AreEqual(0.6, rankBonus, 1e-9);
            Assert.AreEqual(1.0, cap, 1e-9);
        }

        /// <summary>
        /// Call site: MultishotAbility.PerShotMultiplier. A combined affinity+gear value that would land
        /// ABOVE the cap is clamped down to it - the discriminating number here is the cap itself (1.00),
        /// not whatever the uncapped sum would have been.
        /// </summary>
        [TestMethod]
        public void Multishot_PerShotMultiplier_ClampsAboveCap()
        {
            var perShot = MultishotAbility.PerShotMultiplier(rankBonus: 0.6, affinityMultiplier: 2.0, gearMod: 0.5, cap: 1.0);

            // uncapped would be 0.6*2.0 + 0.5 = 1.7 - well above the cap
            Assert.AreEqual(1.0f, perShot, 1e-6f);
        }

        /// <summary>
        /// Call site: MultishotAbility.PerShotMultiplier. The discriminating control for the cap test
        /// above: a value strictly between the base (0.60) and the cap (1.00) must pass through
        /// UNCLAMPED, landing on its own distinct number rather than either boundary.
        /// </summary>
        [TestMethod]
        public void Multishot_PerShotMultiplier_PassesThroughUnclampedBetweenBaseAndCap()
        {
            var perShot = MultishotAbility.PerShotMultiplier(rankBonus: 0.6, affinityMultiplier: 1.2, gearMod: 0.05, cap: 1.0);

            // 0.6*1.2 + 0.05 = 0.77 - strictly between 0.6 and 1.0, and distinct from both the base
            // (0.6) and the cap (1.0) boundary values exercised by the neighboring tests.
            Assert.AreEqual(0.77f, perShot, 1e-6f);
        }

        /// <summary>
        /// Call site: MultishotAbility.PerShotMultiplier. The FLOOR invariant: no affinity/gear
        /// combination - including ones that would otherwise SUBTRACT - can push a per-shot value below
        /// the flat base. A sub-1.0 affinity multiplier and a negative gear mod both still floor at
        /// rankBonus exactly.
        /// </summary>
        [TestMethod]
        public void Multishot_PerShotMultiplier_FloorsAtBase_NeverBelowIt()
        {
            var perShotLowAffinity = MultishotAbility.PerShotMultiplier(rankBonus: 0.6, affinityMultiplier: 0.5, gearMod: 0.0, cap: 1.0);
            var perShotNegativeGear = MultishotAbility.PerShotMultiplier(rankBonus: 0.6, affinityMultiplier: 1.0, gearMod: -0.2, cap: 1.0);

            Assert.AreEqual(0.6f, perShotLowAffinity, 1e-6f);
            Assert.AreEqual(0.6f, perShotNegativeGear, 1e-6f);
        }

        /// <summary>
        /// Call site: MultishotAbility.PerShotMultiplier. REGRESSION (code review on cda2fba7c):
        /// class_ability_multishot_damage_mult and class_ability_multishot_damage_mult_cap are two
        /// independently settable live tunables with no cross-validation, so an operator can invert them
        /// (cap below base). Math.Clamp(value, min, max) throws ArgumentException when min > max, and
        /// min here is rankBonus - an inverted cap must NOT throw; the base wins (degrades to "no cap"
        /// rather than clamping everything down to the now-lower cap).
        /// </summary>
        [TestMethod]
        public void Multishot_PerShotMultiplier_InvertedCapDoesNotThrow_BaseWins()
        {
            var perShot = MultishotAbility.PerShotMultiplier(rankBonus: 0.6, affinityMultiplier: 1.0, gearMod: 0.0, cap: 0.5);

            Assert.AreEqual(0.6f, perShot, 1e-6f);
        }

        /// <summary>
        /// Call site: MultishotAbility.PerShotMultiplier. Same inverted-cap case as above, but with a
        /// non-neutral affinity so the result is NOT just the base: it must still be the unclamped
        /// 0.6*1.5 = 0.9 (the inverted cap degrading to "no cap", not "clamp to 0.5" and not a throw).
        /// </summary>
        [TestMethod]
        public void Multishot_PerShotMultiplier_InvertedCapDoesNotThrow_UnclampedRiderPassesThrough()
        {
            var perShot = MultishotAbility.PerShotMultiplier(rankBonus: 0.6, affinityMultiplier: 1.5, gearMod: 0.0, cap: 0.5);

            Assert.AreEqual(0.9f, perShot, 1e-6f);
        }

        /// <summary>
        /// Call site: MultishotAbility.AddExtraShots with a null attacker (affinity 1.0, gear 0.0) - at
        /// zero effective Assess Creature and no gear, the per-shot multiplier must be bit-identical to
        /// the flat base (rankBonus) alone, unaffected by the new cap.
        /// </summary>
        [TestMethod]
        public void Multishot_AddExtraShots_NullAttacker_IsBitIdenticalToBase()
        {
            var handler = (IMissileVolleyAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.Multishot);
            var rankBonus = (float)PropertyManager.GetDouble("class_ability_multishot_damage_mult").Item;

            var multipliers = new List<float>();
            handler.AddExtraShots(null, rank: 1, multipliers);

            Assert.AreEqual(1, multipliers.Count);
            Assert.AreEqual(rankBonus, multipliers[0]);
        }

        /// <summary>
        /// Call site: MultishotAbility.GetReadout(null, rank), mirroring AcidProcAbility.GetReadout's
        /// null-player testability. With null-safe affinity (1.0) and gear (0.0), Effective must equal
        /// the live AddExtraShots per-shot value for the same (null) inputs, and CapNote must be unset
        /// since nothing clamped.
        /// </summary>
        [TestMethod]
        public void Multishot_GetReadout_NullPlayer_EffectiveMatchesAddExtraShots_NoCapNote()
        {
            var ability = new MultishotAbility();
            var handler = (IMissileVolleyAbility)ability;

            var multipliers = new List<float>();
            handler.AddExtraShots(null, rank: 1, multipliers);

            var readout = ability.GetReadout(null, 1);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(multipliers[0] * 100.0, readout.Effective, 1e-6);
            Assert.IsNull(readout.CapNote);
            Assert.IsFalse(readout.Capped);
            Assert.AreEqual("extra arrow", readout.Label);
        }

        [TestMethod]
        public void PoisonWeapon_FlatBonus_ScalesWithRank_BaseOnlyWithoutAlchemy()
        {
            // Poison is now dealt as a separate proc (its own combat line + damage instance), so the
            // deliverable that's pure/testable is the flat-bonus math; the proc itself needs a live target.
            var perRank = PropertyManager.GetDouble("class_ability_poisonweapon_damage_per_rank").Item;

            // null player -> only the per-rank base (no Alchemy rider)
            Assert.AreEqual(0.0, PoisonWeaponAbility.FlatBonus(null, 0), 1e-9);
            Assert.AreEqual(perRank, PoisonWeaponAbility.FlatBonus(null, 1), 1e-9);
            Assert.AreEqual(3 * perRank, PoisonWeaponAbility.FlatBonus(null, 3), 1e-9);
        }

        /// <summary>
        /// DamageEvent.CriticalDamageMultiplier: 1.0 (no change) whenever IsCritical is false, regardless of
        /// whatever CriticalDamageMod happens to hold (it has no initializer and is only meaningful on a
        /// crit) - so a freshly-constructed non-crit event, which never touches CriticalDamageMod, reads 1.0.
        /// </summary>
        [TestMethod]
        public void DamageEvent_CriticalDamageMultiplier_IsOneWhenNotCritical()
        {
            var nonCrit = new DamageEvent { IsCritical = false };
            Assert.AreEqual(1.0, nonCrit.CriticalDamageMultiplier, 1e-9);
        }

        /// <summary>
        /// On a critical hit, CriticalDamageMultiplier reads CriticalDamageMod directly (DamageEvent.cs sets
        /// it to 1 + GetWeaponCritDamageMod, times the Execution weapon mod - both always >= 1.0 in practice,
        /// but this property does not assume that: it clamps to at least 1.0 itself).
        /// </summary>
        [TestMethod]
        public void DamageEvent_CriticalDamageMultiplier_ReadsCriticalDamageModWhenCritical()
        {
            var crit = new DamageEvent { IsCritical = true, CriticalDamageMod = 1.75f };
            Assert.AreEqual(1.75, crit.CriticalDamageMultiplier, 1e-6);
        }

        /// <summary>
        /// Defensive clamp: a critical event whose CriticalDamageMod somehow sits below 1.0 (should not
        /// happen given how DoCalculateDamage builds it, but this property does not trust the invariant of a
        /// field it does not itself compute) never REDUCES the proc it feeds.
        /// </summary>
        [TestMethod]
        public void DamageEvent_CriticalDamageMultiplier_ClampedToAtLeastOne()
        {
            var crit = new DamageEvent { IsCritical = true, CriticalDamageMod = 0.4f };
            Assert.AreEqual(1.0, crit.CriticalDamageMultiplier, 1e-9);
        }

        /// <summary>
        /// ComputePoisonDamage's critMultiplier parameter defaults to 1.0 and must reproduce every existing
        /// caller's result bit-for-bit, INCLUDING the no-gear branch's default (banker's) rounding - this is
        /// the regression guard the crit feature must not break. Pins the same numbers
        /// EquipmentModHookTests already pins for the two-argument overload, calling the three-argument form
        /// with an explicit 1.0 to prove the default and an explicit neutral value agree.
        /// </summary>
        [TestMethod]
        public void ComputePoisonDamage_CritMultiplierDefault_IsBitIdenticalToBefore()
        {
            // no gear, exact .5 midpoint -> banker's rounding (rounds to even: 15.5 -> 16, 16.5 -> 16)
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.5, 0.0));
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.5, 0.0, 1.0));
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(16.5, 0.0));
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(16.5, 0.0, 1.0));

            // with gear, half-up rounding - unaffected by the new parameter at its default
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.0, 0.5));
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.0, 0.5, 1.0));
        }

        /// <summary>
        /// A crit multiplier > 1.0 is applied to the WHOLE proc (ability + gear) BEFORE the single rounding,
        /// in both the no-gear and gear branches.
        /// </summary>
        [TestMethod]
        public void ComputePoisonDamage_CritMultiplier_AppliedBeforeRounding()
        {
            // no gear: 10 * 1.5 = 15 exactly
            Assert.AreEqual(15u, PoisonWeaponAbility.ComputePoisonDamage(10.0, 0.0, 1.5));

            // no gear, non-integral result: 7 * 1.5 = 10.5 -> banker's rounding to 10 (even)
            Assert.AreEqual(10u, PoisonWeaponAbility.ComputePoisonDamage(7.0, 0.0, 1.5));

            // with gear: (10 + 2) * 1.5 = 18 exactly
            Assert.AreEqual(18u, PoisonWeaponAbility.ComputePoisonDamage(10.0, 2.0, 1.5));

            // with gear, half-up midpoint after the multiply: (10 + 1) * 1.5 = 16.5 -> AwayFromZero -> 17
            Assert.AreEqual(17u, PoisonWeaponAbility.ComputePoisonDamage(10.0, 1.0, 1.5));
        }

        /// <summary>
        /// The strike multiplier is now floored at 0, not 1: since 2026-09-21 it carries the target's damage
        /// resistance rating, so a sub-1.0 value is the intended reduction rather than a bad caller. This
        /// test used to pin the old floor of 1.0 (10 and 12 below); the owner ruling that procs mirror the
        /// strike's rating math is what moved it. A negative input still cannot wrap the uint.
        /// </summary>
        [TestMethod]
        public void ComputePoisonDamage_StrikeMultiplierBelowOne_ReducesTheProc_FlooredAtZero()
        {
            // 10 * 0.5 = 5 exactly (no gear, banker's branch)
            Assert.AreEqual(5u, PoisonWeaponAbility.ComputePoisonDamage(10.0, 0.0, 0.5));

            // a zero multiplier zeroes the proc, gear branch included
            Assert.AreEqual(0u, PoisonWeaponAbility.ComputePoisonDamage(10.0, 2.0, 0.0));

            // a negative multiplier is floored at 0 rather than wrapping to a huge uint
            Assert.AreEqual(0u, PoisonWeaponAbility.ComputePoisonDamage(10.0, 2.0, -1.0));
        }

        /// <summary>
        /// AcidProcAbility.ComputeTickAmount at its default (1.0) critMultiplier must reproduce the formula
        /// ModifyOutgoingDamage used before this parameter existed: Round(flat * tickFraction * (1 +
        /// caustic)).
        /// </summary>
        [TestMethod]
        public void AcidProc_ComputeTickAmount_CritMultiplierDefault_IsBitIdenticalToBefore()
        {
            // 100 * 0.25 * 1.0 = 25 exactly, no caustic
            Assert.AreEqual(25u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.0));
            Assert.AreEqual(25u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.0, 1.0));

            // with a caustic gear term: 100 * 0.25 * 1.2 = 30 exactly
            Assert.AreEqual(30u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.2));
        }

        /// <summary>
        /// A crit multiplier scales the tick amount before its single rounding, same shape as
        /// ComputePoisonDamage.
        /// </summary>
        [TestMethod]
        public void AcidProc_ComputeTickAmount_CritMultiplier_AppliedBeforeRounding()
        {
            // 100 * 0.25 * 1.0 * 1.5 = 37.5 -> Math.Round default (banker's, to even) -> 38
            Assert.AreEqual(38u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.0, 1.5));

            // 100 * 0.25 * 1.2 * 1.5 = 45 exactly
            Assert.AreEqual(45u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.2, 1.5));
        }

        /// <summary>
        /// ComputeTickAmount's strike multiplier is floored at 0, not 1, mirroring ComputePoisonDamage: a
        /// resistant target's reduction now reaches the acid wound. This test used to pin the old floor of
        /// 1.0 (25 for both rows below).
        /// </summary>
        [TestMethod]
        public void AcidProc_ComputeTickAmount_StrikeMultiplierBelowOne_ReducesTheTick_FlooredAtZero()
        {
            // 100 * 0.25 * 0.5 = 12.5 -> Math.Round default (banker's, to even) -> 12
            Assert.AreEqual(12u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.0, 0.5));
            Assert.AreEqual(0u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.0, 0.0));
            Assert.AreEqual(0u, AcidProcAbility.ComputeTickAmount(100.0, 0.25, 0.0, -2.0));
        }

        // ---- DamageEvent.ProcDamageMultiplier: procs mirror the strike's rating math (2026-09-21) ----
        //
        // The rating mods below are built with the SAME Creature helpers DamageEvent.DoCalculateDamage uses
        // (GetPositiveRatingMod / GetNegativeRatingMod / AdditiveCombine), so each fixture holds the value
        // that method would have left on the event. The crit-only gating of the target's crit damage
        // resistance lives in DoCalculateDamage (it combines that term into DamageResistanceRatingMod only
        // under IsCritical); the proc reads the finished field, so these tests pin that the proc does not
        // read the separate crit-resist field on its own.

        private const double ProcFlat = 100.0;

        /// <summary>
        /// Non-crit strike with +20 damage rating: the proc takes the same 1.20x. A stale CriticalDamageMod on
        /// a non-crit event is ignored (CriticalDamageMultiplier gates on IsCritical).
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_NonCrit_TakesTheStrikesDamageRating()
        {
            var e = new DamageEvent
            {
                IsCritical = false,
                CriticalDamageMod = 2.0f,                                  // stale, must be ignored
                DamageRatingMod = Creature.GetPositiveRatingMod(20),       // 1.20
                DamageResistanceRatingMod = Creature.GetNegativeRatingMod(0), // 1.00, no target DRR
            };

            Assert.AreEqual(1.2, e.ProcDamageMultiplier, 1e-6);
            Assert.AreEqual(120u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, e.ProcDamageMultiplier));
        }

        /// <summary>
        /// Crit with +20 damage rating and +30 crit damage rating: the two ratings are ADDED into one pool
        /// (+50 -> 1.50x), exactly as DoCalculateDamage's crit branch rebuilds DamageRatingMod, then times the
        /// 2.0x weapon crit multiplier = 3.00x -> 300. Multiplying the ratings instead (1.2 x 1.3 = 1.56) would
        /// give 312, which is the wrong reading this test rules out.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_Crit_CritDamageRatingIsAdditiveWithDamageRating()
        {
            var e = new DamageEvent
            {
                IsCritical = true,
                CriticalDamageMod = 2.0f,
                DamageRatingMod = Creature.AdditiveCombine(Creature.GetPositiveRatingMod(20), Creature.GetPositiveRatingMod(30), 1.0f, 1.0f),
                DamageResistanceRatingMod = 1.0f,
            };

            Assert.AreEqual(1.5, e.DamageRatingMod, 1e-6);
            Assert.AreEqual(3.0, e.ProcDamageMultiplier, 1e-6);

            var poison = PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, e.ProcDamageMultiplier);
            Assert.AreEqual(300u, poison);
            Assert.AreNotEqual(312u, poison);
        }

        /// <summary>
        /// The target's damage resistance rating reaches the proc: +25 DRR is 100/125 = 0.80x, so a plain
        /// strike's 100 flat lands as 80, and with the attacker's +20 damage rating as 1.2 x 0.8 = 0.96 -> 96.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_TargetDamageResistanceRating_ReducesTheProc()
        {
            var plain = new DamageEvent
            {
                IsCritical = false,
                DamageRatingMod = 1.0f,
                DamageResistanceRatingMod = Creature.GetNegativeRatingMod(25),   // 0.80
            };

            Assert.AreEqual(0.8, plain.ProcDamageMultiplier, 1e-6);
            Assert.AreEqual(80u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, plain.ProcDamageMultiplier));

            var rated = new DamageEvent
            {
                IsCritical = false,
                DamageRatingMod = Creature.GetPositiveRatingMod(20),             // 1.20
                DamageResistanceRatingMod = Creature.GetNegativeRatingMod(25),   // 0.80
            };

            Assert.AreEqual(96u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, rated.ProcDamageMultiplier));
        }

        /// <summary>
        /// The target's crit damage resistance applies to the proc on a crit only. On a crit, DoCalculateDamage
        /// combines +25 DRR and +25 crit DRR additively (-50 -> 100/150 = 0.667x); with a 2.0x crit that is
        /// 200 x 0.667 = 133. On a NON-crit the same target leaves DamageResistanceRatingMod at the base 0.80
        /// and the proc lands 80, even if CriticalDamageResistanceRatingMod holds a value - the proc reads
        /// only the finished DamageResistanceRatingMod.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_TargetCritDamageResistance_AppliesOnCritOnly()
        {
            var baseDrr = Creature.GetNegativeRatingMod(25);   // 0.80
            var critDrr = Creature.GetNegativeRatingMod(25);   // 0.80

            var crit = new DamageEvent
            {
                IsCritical = true,
                CriticalDamageMod = 2.0f,
                DamageRatingMod = 1.0f,
                DamageResistanceRatingBaseMod = baseDrr,
                CriticalDamageResistanceRatingMod = critDrr,
                DamageResistanceRatingMod = Creature.AdditiveCombine(baseDrr, critDrr),
            };

            Assert.AreEqual(100.0 / 150.0, crit.ProcDamageMultiplier / 2.0, 1e-6);
            Assert.AreEqual(133u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, crit.ProcDamageMultiplier));

            var nonCrit = new DamageEvent
            {
                IsCritical = false,
                DamageRatingMod = 1.0f,
                DamageResistanceRatingBaseMod = baseDrr,
                CriticalDamageResistanceRatingMod = critDrr,   // present, must NOT be read
                DamageResistanceRatingMod = baseDrr,
            };

            Assert.AreEqual(80u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, nonCrit.ProcDamageMultiplier));
        }

        /// <summary>
        /// ProcDamageMultiplier is exactly CriticalDamageMultiplier x DamageRatingMod x
        /// DamageResistanceRatingMod whenever those were computed - the three values DamageEvent already holds,
        /// nothing re-derived.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_IsTheProductOfTheStrikesOwnFields()
        {
            var e = new DamageEvent
            {
                IsCritical = true,
                CriticalDamageMod = 1.75f,
                DamageRatingMod = 1.36f,
                DamageResistanceRatingMod = 0.9f,
            };

            Assert.AreEqual(e.CriticalDamageMultiplier * e.DamageRatingMod * e.DamageResistanceRatingMod, e.ProcDamageMultiplier, 1e-12);
        }

        /// <summary>
        /// Clamp and edge cases:
        ///  - a fresh event (no ratings computed: an Invincible defender or general failure returns before the
        ///    ratings block) reads the neutral 1.0, so the proc is exactly what it was before this change;
        ///  - an unset rating factor alone reads neutral while the other still applies;
        ///  - the crit part keeps its own floor of 1.0 (a crit never shrinks the proc by itself);
        ///  - a weakened attacker (negative damage rating, 100/110 = 0.909x) reduces the proc, as it does the
        ///    strike.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_ClampAndEdgeCases()
        {
            var fresh = new DamageEvent();
            Assert.AreEqual(1.0, fresh.ProcDamageMultiplier, 1e-12);
            Assert.AreEqual(100u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, fresh.ProcDamageMultiplier));

            var ratingUnset = new DamageEvent { DamageResistanceRatingMod = 0.8f };
            Assert.AreEqual(0.8, ratingUnset.ProcDamageMultiplier, 1e-6);

            var subOneCrit = new DamageEvent { IsCritical = true, CriticalDamageMod = 0.4f, DamageRatingMod = 1.0f, DamageResistanceRatingMod = 1.0f };
            Assert.AreEqual(1.0, subOneCrit.ProcDamageMultiplier, 1e-9);

            var weakened = new DamageEvent { DamageRatingMod = Creature.GetPositiveRatingMod(-10), DamageResistanceRatingMod = 1.0f };
            Assert.AreEqual(100.0 / 110.0, weakened.ProcDamageMultiplier, 1e-6);
            Assert.AreEqual(91u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, weakened.ProcDamageMultiplier));
        }

        /// <summary>
        /// ComputePoisonDamage with the combined multiplier, both branches: the ability-plus-Venom sum is
        /// multiplied once and rounded once. (100 + 2) x 3.0 = 306; and the Venom-only proc (the rank-0
        /// standalone mod path) at its 0.5 minimum lands +1 at a neutral multiplier but rounds to 0 against a
        /// 0.80x target (0.4, half-up), which is the documented consequence of the floor moving to 0.
        /// </summary>
        [TestMethod]
        public void ComputePoisonDamage_CombinedStrikeMultiplier_GearAndVenomOnly()
        {
            Assert.AreEqual(306u, PoisonWeaponAbility.ComputePoisonDamage(100.0, 2.0, 3.0));

            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.5, 1.0));
            Assert.AreEqual(0u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.5, Creature.GetNegativeRatingMod(25)));
        }

        /// <summary>
        /// The acid tick with the combined multiplier, snapshotted when the wound opens (the computed value is
        /// what ApplyAcidProcDot stores as TickAmount). Crit 2.0x with +20/+30 additive ratings = 3.0x ->
        /// 100 x 1.0 x 3.0 = 300; a non-crit +20 rating strike on a +25 DRR target with a 0.2 Caustic term =
        /// 100 x 1.2 x 1.2 x 0.8 = 115.2 -> 115.
        /// </summary>
        [TestMethod]
        public void AcidProc_ComputeTickAmount_CombinedStrikeMultiplier()
        {
            var crit = new DamageEvent
            {
                IsCritical = true,
                CriticalDamageMod = 2.0f,
                DamageRatingMod = Creature.AdditiveCombine(Creature.GetPositiveRatingMod(20), Creature.GetPositiveRatingMod(30), 1.0f, 1.0f),
                DamageResistanceRatingMod = 1.0f,
            };

            Assert.AreEqual(300u, AcidProcAbility.ComputeTickAmount(100.0, 1.0, 0.0, crit.ProcDamageMultiplier));

            var resisted = new DamageEvent
            {
                IsCritical = false,
                DamageRatingMod = Creature.GetPositiveRatingMod(20),
                DamageResistanceRatingMod = Creature.GetNegativeRatingMod(25),
            };

            Assert.AreEqual(115u, AcidProcAbility.ComputeTickAmount(100.0, 1.0, 0.2, resisted.ProcDamageMultiplier));
        }

        // ---- Round 2 (2026-09-22): Killer Instinct and Multishot reach the procs ----

        /// <summary>
        /// Killer Instinct's crit-damage bonus reaches the proc on a crit: 2.0 crit x 1.30 KI = 2.60 -> 260.
        /// The stash is ignored on a non-crit (Killer Instinct only writes it on a crit, but the property does
        /// not trust that).
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_KillerInstinctBonus_AppliesOnCritOnly()
        {
            var crit = new DamageEvent
            {
                IsCritical = true,
                CriticalDamageMod = 2.0f,
                DamageRatingMod = 1.0f,
                DamageResistanceRatingMod = 1.0f,
                ClassAbilityCritDamageBonus = 0.30,
            };

            Assert.AreEqual(2.6, crit.ProcDamageMultiplier, 1e-6);
            Assert.AreEqual(260u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, crit.ProcDamageMultiplier));

            var nonCrit = new DamageEvent { DamageRatingMod = 1.0f, DamageResistanceRatingMod = 1.0f, ClassAbilityCritDamageBonus = 0.30 };
            Assert.AreEqual(100u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, nonCrit.ProcDamageMultiplier));
        }

        /// <summary>
        /// A multi-shot extra arrow's fraction reaches the proc: 0.5 -> 50. A fresh event (the primary arrow,
        /// melee, a Riposte counter - anything DamageTarget did not flag as an extra arrow) reads 1.0.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_MultiShotExtraArrow_ScalesTheProc()
        {
            Assert.AreEqual(1.0f, new DamageEvent().MultiShotProcMultiplier);

            var extra = new DamageEvent { DamageRatingMod = 1.0f, DamageResistanceRatingMod = 1.0f, MultiShotProcMultiplier = 0.5f };
            Assert.AreEqual(50u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, extra.ProcDamageMultiplier));
        }

        /// <summary>
        /// Every factor at once, each applied exactly once: crit 2.0 x rating 1.5 x DRR 0.8 x Killer Instinct
        /// 1.3 x extra arrow 0.5 = 1.56 -> 156 poison; the acid tick at fraction 1.0 lands the same 156.
        /// </summary>
        [TestMethod]
        public void ProcDamageMultiplier_AllFactors_EachAppliedOnce()
        {
            var e = new DamageEvent
            {
                IsCritical = true,
                CriticalDamageMod = 2.0f,
                DamageRatingMod = Creature.AdditiveCombine(Creature.GetPositiveRatingMod(20), Creature.GetPositiveRatingMod(30), 1.0f, 1.0f),
                DamageResistanceRatingMod = Creature.GetNegativeRatingMod(25),
                ClassAbilityCritDamageBonus = 0.30,
                MultiShotProcMultiplier = 0.5f,
            };

            Assert.AreEqual(1.56, e.ProcDamageMultiplier, 1e-6);
            Assert.AreEqual(156u, PoisonWeaponAbility.ComputePoisonDamage(ProcFlat, 0.0, e.ProcDamageMultiplier));
            Assert.AreEqual(156u, AcidProcAbility.ComputeTickAmount(100.0, 1.0, 0.0, e.ProcDamageMultiplier));
        }

        /// <summary>
        /// Pins the dispatch reorder: Killer Instinct is the only CritDamage-band handler and runs FIRST in the
        /// outgoing-damage bucket, so Poison Weapon and Acid Proc (Modify band) read its stashed bonus. Every
        /// other handler keeps its relative registration order (stable sort), with the war procs still last.
        /// </summary>
        [TestMethod]
        public void OutgoingDispatch_KillerInstinctRunsBeforePoisonAndAcid()
        {
            var bucket = ClassAbilityRegistry.OutgoingDamageAbilities.ToList();

            Assert.AreEqual(ClassAbilityId.KillerInstinct, bucket[0].Definition.Id, "Killer Instinct must dispatch first");
            Assert.AreEqual(1, bucket.Count(h => h.DispatchOrder == OutgoingDamageDispatchOrder.CritDamage),
                "only Killer Instinct belongs in the CritDamage band");

            var ki = bucket.FindIndex(h => h.Definition.Id == ClassAbilityId.KillerInstinct);
            var poison = bucket.FindIndex(h => h.Definition.Id == ClassAbilityId.PoisonWeapon);
            var acid = bucket.FindIndex(h => h.Definition.Id == ClassAbilityId.AcidProc);

            Assert.IsTrue(poison > ki && acid > ki, $"KI {ki}, Poison {poison}, Acid {acid}");

            // everything except Killer Instinct: the Modify band in registration order, then the war procs
            var registered = ClassAbilityRegistry.Handlers.OfType<IOutgoingDamageAbility>()
                .Where(h => h.Definition.Id != ClassAbilityId.KillerInstinct).ToList();
            var expected = registered.Where(h => h.DispatchOrder == OutgoingDamageDispatchOrder.Modify)
                .Concat(registered.Where(h => h.DispatchOrder == OutgoingDamageDispatchOrder.SpellCastingProc))
                .Select(h => h.Definition.Id).ToList();

            CollectionAssert.AreEqual(expected, bucket.Skip(1).Select(h => h.Definition.Id).ToList());
        }

        // ---- Round 2: life-projectile echoes ----

        /// <summary>
        /// The Blood Charge grant is one per CAST: only the aimed projectile of the original cast grants it,
        /// never a ring side-projectile, and never an Echo Cast recast (which copies a cast whose aimed bolt
        /// already earned it).
        /// </summary>
        [TestMethod]
        public void LifeProjectile_BloodChargeGrant_OnePerCast_NeverFromAnEcho()
        {
            Assert.IsTrue(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: true, isEchoCopy: false));
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: false, isEchoCopy: false));
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: true, isEchoCopy: true));
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: false, isEchoCopy: true));
        }

        /// <summary>
        /// An echo inherits the parent cast's life-projectile damage basis (it used to land for 0), its
        /// Spellweave capture, and the IsEchoCopy flag - and does NOT take IsClassAbilitySpawned, so Spell AOE
        /// still radiates off it.
        /// </summary>
        [TestMethod]
        public void EchoCast_StampEchoCopy_InheritsTheParentsPerCastValues()
        {
            var parent = NewProjectile();
            parent.LifeProjectileDamage = 420;
            parent.SpellweaveDamageMod = 1.25f;

            var echo = NewProjectile();
            Assert.AreEqual(0u, echo.LifeProjectileDamage, "precondition: a fresh projectile carries no basis");

            EchoCastAbility.StampEchoCopy(echo, parent);

            Assert.AreEqual(420u, echo.LifeProjectileDamage);
            Assert.AreEqual(1.25f, echo.SpellweaveDamageMod);
            Assert.IsTrue(echo.IsEchoCopy);
            Assert.IsFalse(echo.IsClassAbilitySpawned);
        }

        /// <summary>
        /// The shared helper every child-spawn site (Echo Cast, Spell AOE, Cascade) calls to inherit the
        /// parent's per-cast stamps. Without it, a Spell AOE splash child or a Cascade child carries the
        /// projectile default (LifeProjectileDamage = 0) and a life-projectile spell like Martyr's Hecatomb
        /// lands for 0 damage on every splash/chain hit.
        /// </summary>
        [TestMethod]
        public void CopyPerCastStamps_InheritsTheParentsPerCastValues()
        {
            var parent = NewProjectile();
            parent.LifeProjectileDamage = 420;
            parent.SpellweaveDamageMod = 1.25f;
            parent.LifeProjectileStamp = new LifeProjectileCastStamp(1.2f, 1.21f, BloodChargeCastOutcome.Accrue);

            var child = NewProjectile();
            Assert.AreEqual(0u, child.LifeProjectileDamage, "precondition: a fresh projectile carries no basis");

            SpellProjectile.CopyPerCastStamps(child, parent);

            Assert.AreEqual(420u, child.LifeProjectileDamage);
            Assert.AreEqual(1.25f, child.SpellweaveDamageMod);
            Assert.AreEqual(parent.LifeProjectileStamp.DamageMultiplier, child.LifeProjectileStamp.DamageMultiplier);
            Assert.AreEqual(parent.LifeProjectileStamp.Bursts, child.LifeProjectileStamp.Bursts);
            Assert.AreEqual(parent.LifeProjectileStamp.GrantsCharge, child.LifeProjectileStamp.GrantsCharge);
            Assert.IsFalse(child.IsEchoCopy, "CopyPerCastStamps must not set the Echo-only flag");
        }

        // ---- Round 3: the Blood Mage per-cast stamp rides the projectile (PR #1271 review) ----

        /// <summary>
        /// Two life projectiles in flight from two different casts - an accruing cast (Blood Price 1.2 x ramp
        /// 1.21) and a later exsanguinating one (Blood Price 1.2 x burst 2.05) - each resolve against their
        /// OWN launch-time stamp, whichever lands first. Before the snapshot both read the player's live
        /// fields, so the earlier bolt landing after the later cast took the burst and the resistance-ignore.
        /// </summary>
        [TestMethod]
        public void LifeProjectileStamp_TwoProjectilesInFlight_EachUsesItsOwnStamp_InEitherOrder()
        {
            var accrue = new LifeProjectileCastStamp(1.2f, 1.21f, BloodChargeCastOutcome.Accrue);
            var burst = new LifeProjectileCastStamp(1.2f, 2.05f, BloodChargeCastOutcome.Burst);

            foreach (var accrueLandsFirst in new[] { true, false })
            {
                var first = NewProjectile();
                first.LifeProjectileStamp = accrue;
                var second = NewProjectile();
                second.LifeProjectileStamp = burst;

                var order = accrueLandsFirst ? new[] { first, second } : new[] { second, first };
                var seen = order.ToDictionary(p => p, p => (p.LifeProjectileStamp.DamageMultiplier, p.LifeProjectileStamp.Bursts, p.LifeProjectileStamp.GrantsCharge));

                Assert.AreEqual(1.2f * 1.21f, seen[first].DamageMultiplier, 1e-5f, $"accrue bolt (accrueLandsFirst={accrueLandsFirst})");
                Assert.IsFalse(seen[first].Bursts);
                Assert.IsTrue(seen[first].GrantsCharge);

                Assert.AreEqual(1.2f * 2.05f, seen[second].DamageMultiplier, 1e-5f, $"burst bolt (accrueLandsFirst={accrueLandsFirst})");
                Assert.IsTrue(seen[second].Bursts);
                Assert.IsFalse(seen[second].GrantsCharge);
            }
        }

        /// <summary>
        /// An echo carries its PARENT's stamp, not whatever CreateSpellProjectiles captured from the live player
        /// fields when the echo launched (which may already belong to a later Exsanguinate).
        /// </summary>
        [TestMethod]
        public void EchoCast_StampEchoCopy_TakesTheParentsLifeProjectileStamp_NotALaterCasts()
        {
            var parent = NewProjectile();
            parent.LifeProjectileStamp = new LifeProjectileCastStamp(1.1f, 1.14f, BloodChargeCastOutcome.Accrue);

            var echo = NewProjectile();
            // What LaunchSpellProjectiles would have captured had a burst cast landed in between.
            echo.LifeProjectileStamp = new LifeProjectileCastStamp(1.3f, 2.05f, BloodChargeCastOutcome.Burst);

            EchoCastAbility.StampEchoCopy(echo, parent);

            Assert.AreEqual(1.1f, echo.LifeProjectileStamp.BloodPriceMod);
            Assert.AreEqual(1.14f, echo.LifeProjectileStamp.ChargeMod);
            Assert.AreEqual(BloodChargeCastOutcome.Accrue, echo.LifeProjectileStamp.Outcome);
            Assert.IsFalse(echo.LifeProjectileStamp.Bursts, "the echo must not inherit a later cast's burst");
            Assert.IsFalse(SpellProjectile.GrantsLifeProjectileCharge(hasProjectileTarget: true, echo.IsEchoCopy),
                "the grant rule is unchanged: an echo never grants, even with an accruing stamp");
        }

        /// <summary>
        /// An unstamped projectile (a non-player caster, or any path that never snapshots) is neutral: no
        /// multiplier, no burst - and the default struct, whose multipliers are 0, reads as 1.0, not as 0.
        /// </summary>
        [TestMethod]
        public void LifeProjectileStamp_NoneAndDefault_AreNeutral()
        {
            Assert.AreEqual(1.0f, LifeProjectileCastStamp.None.DamageMultiplier);
            Assert.IsFalse(LifeProjectileCastStamp.None.Bursts);
            Assert.AreEqual(1.0f, default(LifeProjectileCastStamp).DamageMultiplier);
            Assert.IsFalse(default(LifeProjectileCastStamp).Bursts);
            Assert.AreEqual(1.0f, NewProjectile().LifeProjectileStamp.DamageMultiplier, "a fresh projectile defaults to None");
        }

        private static uint nextProjectileWcid = 993000;

        private static SpellProjectile NewProjectile()
        {
            var weenie = new Weenie { WeenieClassId = nextProjectileWcid++, WeenieType = WeenieType.ProjectileSpell };
            return new SpellProjectile(weenie, new ACE.Entity.ObjectGuid(0x7F100000 + nextProjectileWcid));
        }

        // ---- Echo Cast full recast (2026-09-21): recursion guards as pure functions ----
        //
        // The flag states below are the ones the live code stamps: a normal cast carries neither flag; an
        // echo is stamped IsEchoCopy only (EchoCastAbility.OnSpellHit); a Spell AOE child is stamped
        // IsClassAbilitySpawned and inherits the parent's IsEchoCopy (SpellProjectile.SpawnClassAbilityAoeChild).

        [TestMethod]
        public void EchoCast_NormalCast_IsEligibleForEchoAndAoe()
        {
            Assert.IsTrue(EchoCastAbility.CanEcho(isClassAbilitySpawned: false, isEchoCopy: false));
            Assert.IsTrue(SpellAoeAbility.CanRadiate(isClassAbilitySpawned: false, ProjectileSpellType.Arc));
        }

        /// <summary>
        /// The owner ruling: an echo is a full second cast, so Spell AOE radiates off an echoed Arc - but an
        /// echo never echoes again.
        /// </summary>
        [TestMethod]
        public void EchoCast_EchoCopy_IsEligibleForAoe_NotForEcho()
        {
            Assert.IsTrue(SpellAoeAbility.CanRadiate(isClassAbilitySpawned: false, ProjectileSpellType.Arc),
                "an echoed Arc must radiate Spell AOE like a normal cast");
            Assert.IsFalse(EchoCastAbility.CanEcho(isClassAbilitySpawned: false, isEchoCopy: true),
                "an echo must never echo again");
        }

        /// <summary>
        /// An AOE child radiated off an echo (IsClassAbilitySpawned + inherited IsEchoCopy) neither echoes nor
        /// radiates further. Both guard fields refuse the echo independently.
        /// </summary>
        [TestMethod]
        public void EchoCast_AoeChildOfEcho_IsEligibleForNeither()
        {
            Assert.IsFalse(EchoCastAbility.CanEcho(isClassAbilitySpawned: true, isEchoCopy: true));
            Assert.IsFalse(SpellAoeAbility.CanRadiate(isClassAbilitySpawned: true, ProjectileSpellType.Arc));

            // either field alone is enough to stop the echo
            Assert.IsFalse(EchoCastAbility.CanEcho(isClassAbilitySpawned: true, isEchoCopy: false));
        }

        /// <summary>
        /// Unchanged invariants: an AOE child of a normal cast still neither echoes nor radiates; Spell AOE is
        /// still Arc-only on every projectile; and Cascade still never fires off an echo, because an echo is
        /// built through the normal cast path and is not a class-ability proc.
        /// </summary>
        [TestMethod]
        public void EchoCast_ExistingNoCascadeInvariants_Unchanged()
        {
            Assert.IsFalse(EchoCastAbility.CanEcho(isClassAbilitySpawned: true, isEchoCopy: false));
            Assert.IsFalse(SpellAoeAbility.CanRadiate(isClassAbilitySpawned: true, ProjectileSpellType.Arc));

            foreach (var type in new[] { ProjectileSpellType.Bolt, ProjectileSpellType.Blast, ProjectileSpellType.Volley,
                ProjectileSpellType.Streak, ProjectileSpellType.Ring, ProjectileSpellType.Wall, ProjectileSpellType.Strike })
            {
                Assert.IsFalse(SpellAoeAbility.CanRadiate(isClassAbilitySpawned: false, type), $"{type} must not radiate");
            }

            Assert.IsFalse(CascadeAbility.CanCascade(isClassAbilityProc: false, generation: 0, maxGeneration: 1),
                "an echo is not a class-ability proc, so it can never cascade");
        }

        [TestMethod]
        public void BattleHardened_ReducesByPointZeroFivePercentPerStrength_AndCaps()
        {
            var perPoint = PropertyManager.GetDouble("class_ability_battlehardened_reduction_per_strength").Item;
            var maxReduction = PropertyManager.GetDouble("class_ability_battlehardened_max_reduction").Item;

            // spec: 0.05% reduction per point of Strength
            Assert.AreEqual(0.0005, perPoint, 1e-9);

            // 0 Strength -> no reduction
            Assert.AreEqual(1.0f, BattleHardenedAbility.GetDamageResistMod(0, perPoint, maxReduction), 1e-6f);

            // 200 Strength -> 200 * 0.0005 = 0.10 reduction -> 0.90 damage multiplier
            Assert.AreEqual(0.90f, BattleHardenedAbility.GetDamageResistMod(200, perPoint, maxReduction), 1e-5f);

            // a lower multiplier means less damage taken as Strength rises
            var atFifty = BattleHardenedAbility.GetDamageResistMod(50, perPoint, maxReduction);
            var atThreeHundred = BattleHardenedAbility.GetDamageResistMod(300, perPoint, maxReduction);
            Assert.IsTrue(atThreeHundred < atFifty && atFifty < 1.0f);

            // reduction is capped regardless of extreme Strength (never inverts to zero/negative damage)
            var capped = BattleHardenedAbility.GetDamageResistMod(100000, perPoint, maxReduction);
            Assert.AreEqual((float)(1.0 - maxReduction), capped, 1e-5f);
            Assert.IsTrue(capped > 0f);
        }

        [TestMethod]
        public void Frenzy_StackCap_Is3_6_10_ByRank()
        {
            Assert.AreEqual(0, FrenzyAbility.StackCap(0));
            Assert.AreEqual(3, FrenzyAbility.StackCap(1));
            Assert.AreEqual(6, FrenzyAbility.StackCap(2));
            Assert.AreEqual(10, FrenzyAbility.StackCap(3));
        }

        [TestMethod]
        public void Frenzy_AddsFivePercentAttackSpeedPerStack()
        {
            var perStack = PropertyManager.GetDouble("class_ability_frenzy_percent_per_stack").Item;

            // spec (Round 6): +5% per stack - nominal +50% at the rank-3 cap, which no longer
            // saturates the raised 3.5x anim ceiling
            Assert.AreEqual(0.05, perStack, 1e-9);
            Assert.AreEqual(1.0f, FrenzyAbility.AttackSpeedMultiplier(0, perStack), 1e-6f);
            Assert.AreEqual(1.15f, FrenzyAbility.AttackSpeedMultiplier(3, perStack), 1e-5f);   // rank-1 cap
            Assert.AreEqual(1.50f, FrenzyAbility.AttackSpeedMultiplier(10, perStack), 1e-5f);  // rank-3 cap = +50%
        }

        /// <summary>
        /// FINDINGS-LEDGER L6-73 (user decision 2026-07-25): Frenzy is a two-handed / dual-wield melee
        /// ability. The stance predicate is the pure half of the gate; the two call sites that consume it
        /// (Player.OnFrenzyLandedHit for the trigger, Player.GetFrenzyAttackSpeedMod for the application)
        /// need live combat state and are verified in-game.
        /// </summary>
        [TestMethod]
        public void Frenzy_QualifyingStances_AreTwoHandedAndDualWieldMeleeOnly()
        {
            Assert.IsTrue(FrenzyAbility.IsQualifyingStance(MotionStance.TwoHandedSwordCombat));
            Assert.IsTrue(FrenzyAbility.IsQualifyingStance(MotionStance.TwoHandedStaffCombat));
            Assert.IsTrue(FrenzyAbility.IsQualifyingStance(MotionStance.DualWieldCombat));
        }

        [TestMethod]
        public void Frenzy_NonQualifyingStances_AreRejected()
        {
            // the observed bug: bow volleys built and consumed stacks
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.BowCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.BowNoAmmo));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.CrossbowCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.CrossBowNoAmmo));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.AtlatlCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.SlingCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.ThrownWeaponCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.ThrownShieldCombat));

            // magic
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.Magic));

            // unarmed, and a single one-hander with or without a shield (no offhand weapon = no dual wield)
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.HandCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.SwordCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.SwordShieldCombat));

            // out of combat, and an unset stance (CurrentMotionState may be null)
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.NonCombat));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(MotionStance.Invalid));
            Assert.IsFalse(FrenzyAbility.IsQualifyingStance(null));
        }

        /// <summary>
        /// Guards the predicate against enum drift: exactly three of the MotionStance values may qualify.
        /// A new stance added to the enum defaults to non-qualifying, and this fails if that changes.
        /// </summary>
        [TestMethod]
        public void Frenzy_ExactlyThreeStancesQualify()
        {
            var qualifying = Enum.GetValues(typeof(MotionStance))
                .Cast<MotionStance>()
                .Where(s => FrenzyAbility.IsQualifyingStance(s))
                .ToList();

            Assert.AreEqual(3, qualifying.Count, $"qualifying stances: {string.Join(", ", qualifying)}");
        }

        [TestMethod]
        public void Frenzy_RecklessRider_AddsToPerStackRate()
        {
            var perStack = PropertyManager.GetDouble("class_ability_frenzy_percent_per_stack").Item;
            Assert.AreEqual(0.05, perStack, 1e-9);

            // rider = 0 reproduces today's value exactly (5%/stack, +50% at 10 stacks)
            Assert.AreEqual(1.50f, FrenzyAbility.AttackSpeedMultiplier(10, perStack + 0.0), 1e-5f);

            // a +1% Recklessness rider adds to the per-stack rate: 6%/stack, +60% at 10 stacks
            Assert.AreEqual(1.60f, FrenzyAbility.AttackSpeedMultiplier(10, perStack + 0.01), 1e-5f);
        }

        [TestMethod]
        public void NetherRush_CastSpeed_RampsAndScalesWithRank_AndCapsAt5Stacks()
        {
            var perRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;

            // spec: +5% / +10% / +15% per stack by rank
            Assert.AreEqual(0.05, perRank, 1e-9);

            // first void cast (0 stacks accumulated) gets no bonus at any rank
            Assert.AreEqual(1.0f, NetherRushAbility.CastSpeedMultiplier(0, 1, perRank), 1e-6f);
            Assert.AreEqual(1.0f, NetherRushAbility.CastSpeedMultiplier(0, 3, perRank), 1e-6f);

            // per-stack strength scales with rank
            Assert.AreEqual(1.05f, NetherRushAbility.CastSpeedMultiplier(1, 1, perRank), 1e-5f);
            Assert.AreEqual(1.15f, NetherRushAbility.CastSpeedMultiplier(1, 3, perRank), 1e-5f);

            // caps at MaxStacks (5): rank 3 -> 1 + 5 * 0.15 = 1.75, and more stacks don't exceed it
            Assert.AreEqual(1.75f, NetherRushAbility.CastSpeedMultiplier(5, 3, perRank), 1e-5f);
            Assert.AreEqual(1.75f, NetherRushAbility.CastSpeedMultiplier(99, 3, perRank), 1e-5f);
        }

        /// <summary>
        /// The fourth argument is the AMOUNT affinity adds to the per-stack rate, not a skill-specific
        /// rider. The skill behind it became Loyalty in the 2026-09-12 overhaul (it was Arcane Lore),
        /// but this function has never known which skill produced the number and still does not - which
        /// is precisely why the affinity migration did not have to change it.
        /// </summary>
        [TestMethod]
        public void NetherRush_AffinityAddedAmount_AddsToPerStackRate()
        {
            var perRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;

            // an added amount of 0 reproduces the rank-only value exactly
            Assert.AreEqual(1.15f, NetherRushAbility.CastSpeedMultiplier(1, 3, perRank, 0.0), 1e-5f);

            // a +2% (0.02) affinity contribution adds to the per-stack rate: (0.15 + 0.02) per stack
            Assert.AreEqual(1.17f, NetherRushAbility.CastSpeedMultiplier(1, 3, perRank, 0.02), 1e-5f);

            // still respects MaxStacks(5) with the affinity amount applied
            Assert.AreEqual((float)(1.0 + 5 * (3 * perRank + 0.02)), NetherRushAbility.CastSpeedMultiplier(5, 3, perRank, 0.02), 1e-5f);
        }

        [TestMethod]
        public void Thorns_ReflectsPercentOfShieldArmorPerRank_ClampingNegativeArmor()
        {
            var percentPerRank = PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item;

            // spec: 5% of the shield's effective armor level per rank (halved from 10% on 2026-08-05)
            Assert.AreEqual(0.05, percentPerRank, 1e-9);

            // rank 1 vs a 500-AL shield: 5% of 500 = 25 reflected
            Assert.AreEqual(25u, ThornsAbility.ComputeReflectDamage(500, 1, percentPerRank));

            // scales with rank: rank 3 = 15% of 500 = 75
            Assert.AreEqual(75u, ThornsAbility.ComputeReflectDamage(500, 3, percentPerRank));

            // rounds to nearest (33 * 0.15 = 4.95 -> 5)
            Assert.AreEqual(5u, ThornsAbility.ComputeReflectDamage(33, 3, percentPerRank));

            // a brittlemail'd shield (negative effective AL) clamps to 0 - Thorns can never heal the attacker
            Assert.AreEqual(0u, ThornsAbility.ComputeReflectDamage(-200, 3, percentPerRank));
        }

        [TestMethod]
        public void Thorns_ShieldSkillRider_AddsOntoThePerRankReflectFraction()
        {
            var percentPerRank = PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item;

            // rank 1 (5%) + a 0.05 (=+5%) Shield-skill rider = 10% of a 500-AL shield = 50
            Assert.AreEqual(50u, ThornsAbility.ComputeReflectDamage(500, 1, percentPerRank, 0.05));

            // zero rider reproduces the un-scaled value (backwards compatible with the default overload)
            Assert.AreEqual(ThornsAbility.ComputeReflectDamage(500, 2, percentPerRank),
                            ThornsAbility.ComputeReflectDamage(500, 2, percentPerRank, 0.0));
        }

        [TestMethod]
        public void Avoidance_PooledRoll_BlockThenParry_UnderCap()
        {
            // block 20%, parry 15%, pooled 35% under a 50% cap
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Block, ClassAbilityAvoidance.Resolve(0.20, 0.15, 0.50, 0.10));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Block, ClassAbilityAvoidance.Resolve(0.20, 0.15, 0.50, 0.19));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Parry, ClassAbilityAvoidance.Resolve(0.20, 0.15, 0.50, 0.25));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Parry, ClassAbilityAvoidance.Resolve(0.20, 0.15, 0.50, 0.34));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.None, ClassAbilityAvoidance.Resolve(0.20, 0.15, 0.50, 0.35));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.None, ClassAbilityAvoidance.Resolve(0.20, 0.15, 0.50, 0.99));
        }

        [TestMethod]
        public void Avoidance_OverCap_ScalesBothProportionally_PreservingRatio()
        {
            // block 40% + parry 40% = 80% raw, capped at 50% -> each scales to 25%
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Block, ClassAbilityAvoidance.Resolve(0.40, 0.40, 0.50, 0.24));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Parry, ClassAbilityAvoidance.Resolve(0.40, 0.40, 0.50, 0.26));
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.Parry, ClassAbilityAvoidance.Resolve(0.40, 0.40, 0.50, 0.49));
            // total avoidance never exceeds the cap
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.None, ClassAbilityAvoidance.Resolve(0.40, 0.40, 0.50, 0.50));

            // no skills -> never avoids
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.None, ClassAbilityAvoidance.Resolve(0.0, 0.0, 0.50, 0.0));
        }

        [TestMethod]
        public void Avoidance_ChanceHelpers_MatchDocumentedProgressions()
        {
            // Parry 5/10/15% by rank. Both of these moved to the MULTIPLICATIVE affinity primitive on
            // 2026-09-12, so the third argument is now a FACTOR whose neutral value is 1.0, not a flat
            // rider whose neutral value was 0.0.
            Assert.AreEqual(0.05, ParryAbility.ParryChance(1, 0.05, 1.0), 1e-9);
            Assert.AreEqual(0.15, ParryAbility.ParryChance(3, 0.05, 1.0), 1e-9);
            Assert.AreEqual(0.0, ParryAbility.ParryChance(0, 0.05, 1.5), 1e-9);

            // Shield Block 8/14/20% by rank (base 0.08 step 0.06)
            Assert.AreEqual(0.08, ShieldBlockAbility.BlockChance(1, 0.08, 0.06, 1.0), 1e-9);
            Assert.AreEqual(0.14, ShieldBlockAbility.BlockChance(2, 0.08, 0.06, 1.0), 1e-9);
            Assert.AreEqual(0.20, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, 1.0), 1e-9);

            // Riposte 40/70/100% by rank (base 0.4 step 0.3). Shield Check shared this curve until the
            // 2026-09-12 class ability overhaul retired it.
            Assert.AreEqual(0.70, RiposteAbility.CounterFraction(2, 0.40, 0.30), 1e-9);
        }

        [TestMethod]
        public void ClassAbilityScaling_DualRatio_TighterDivisorWhenSpecialized()
        {
            // "+1 per 12 pts (trained) / per 9 pts (spec) of effective Alchemy above 100"
            // 220 effective -> 120 above threshold -> 120/12 = 10 (trained), 120/9 = 13.33 (spec)
            Assert.AreEqual(10.0, ClassAbilityScaling.Compute(220, false, 12, 9, 100), 1e-9);
            Assert.AreEqual(120.0 / 9.0, ClassAbilityScaling.Compute(220, true, 12, 9, 100), 1e-9);

            // specialized is always >= trained at the same investment (tighter divisor = bigger bonus)
            Assert.IsTrue(ClassAbilityScaling.Compute(400, true, 20, 15) > ClassAbilityScaling.Compute(400, false, 20, 15));
        }

        [TestMethod]
        public void AttackSpeed_ConstantMultiplier_PlusLockpickRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_attackspeed_percent_per_rank").Item;
            Assert.AreEqual(0.06, perRank, 1e-9);

            // rank 3, no rider -> +18%
            Assert.AreEqual(1.18f, AttackSpeedAbility.AttackSpeedMultiplier(3, perRank, 0.0), 1e-5f);
            // rank 3 + a 0.06 (=+6%) Lockpick rider -> +24%
            Assert.AreEqual(1.24f, AttackSpeedAbility.AttackSpeedMultiplier(3, perRank, 0.06), 1e-5f);
            // unlearned -> no buff
            Assert.AreEqual(1.0f, AttackSpeedAbility.AttackSpeedMultiplier(0, perRank, 0.5), 1e-5f);
        }

        [TestMethod]
        public void VoidDamage_MultiplierPerRank()
        {
            var perRank = PropertyManager.GetDouble("class_ability_voiddamage_percent_per_rank").Item;
            Assert.AreEqual(1.08f, VoidDamageAbility.DamageMultiplier(1, perRank), 1e-5f);
            Assert.AreEqual(1.24f, VoidDamageAbility.DamageMultiplier(3, perRank), 1e-5f);
            Assert.AreEqual(1.0f, VoidDamageAbility.DamageMultiplier(0, perRank), 1e-5f);
        }

        [TestMethod]
        public void FlatCastSpeed_ConstantMultiplierPerRank()
        {
            var perRank = PropertyManager.GetDouble("class_ability_flatcastspeed_percent_per_rank").Item;
            Assert.AreEqual(1.10f, FlatCastSpeedAbility.CastSpeedMultiplier(1, perRank), 1e-5f);
            Assert.AreEqual(1.30f, FlatCastSpeedAbility.CastSpeedMultiplier(3, perRank), 1e-5f);
            Assert.AreEqual(1.0f, FlatCastSpeedAbility.CastSpeedMultiplier(0, perRank), 1e-5f);
        }

        [TestMethod]
        public void QuickenedCasting_ConstantMultiplierPerRank()
        {
            var perRank = PropertyManager.GetDouble("class_ability_quickenedcasting_percent_per_rank").Item;

            // spec: +20% per rank, so +100% at rank 5
            Assert.AreEqual(0.20, perRank, 1e-9);

            Assert.AreEqual(1.20f, QuickenedCastingAbility.CastSpeedMultiplier(1, perRank), 1e-5f);
            Assert.AreEqual(2.00f, QuickenedCastingAbility.CastSpeedMultiplier(5, perRank), 1e-5f);
            Assert.AreEqual(1.0f, QuickenedCastingAbility.CastSpeedMultiplier(0, perRank), 1e-5f);
        }

        /// <summary>
        /// The assertion most worth having for Quickened Casting: it must classify a damaging spell as
        /// unaffected regardless of school, and a buff/debuff (Enchantment/FellowEnchantment) as affected -
        /// UNLESS that "buff/debuff" is actually a damage- or heal-over-time spell dressed up as a stat mod,
        /// which stays excluded too. A war bolt is MetaSpellType.Projectile, so it never even reaches the
        /// MetaSpellType gate - this pins the classification the composed cast-speed product depends on to
        /// keep a war bolt bit-identical at rank 5.
        /// </summary>
        [TestMethod]
        public void QuickenedCasting_AffectsSpell_OnlyClassifiesNonDamagingBuffsAndDebuffs()
        {
            // damaging spells of every relevant MetaSpellType are unaffected, DoT flag or not
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.Projectile, false));
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.LifeProjectile, false));
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.Boost, false));
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.Transfer, false));

            // a plain buff/debuff (protection, bane, imperil, vulnerability) of either Enchantment shape
            // qualifies
            Assert.IsTrue(QuickenedCastingAbility.AffectsSpell(SpellType.Enchantment, false));
            Assert.IsTrue(QuickenedCastingAbility.AffectsSpell(SpellType.FellowEnchantment, false));

            // a damage/heal-over-time spell dressed up as an Enchantment is excluded - "damaging spells are
            // completely unaffected" must hold even for this shape
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.Enchantment, true));
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.FellowEnchantment, true));

            // everything else (portal spells, dispels) is unaffected too
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.Dispel, false));
            Assert.IsFalse(QuickenedCastingAbility.AffectsSpell(SpellType.PortalRecall, false));
        }

        [TestMethod]
        public void Spellweave_DamageMultiplier_RankPlusAffinityAddedAmount()
        {
            var perRank = PropertyManager.GetDouble("class_ability_spellweave_percent_per_rank").Item;

            // spec: +3% per rank, so +15% at rank 5
            Assert.AreEqual(0.03, perRank, 1e-9);

            Assert.AreEqual(1.03f, SpellweaveAbility.DamageMultiplier(1, perRank), 1e-5f);
            Assert.AreEqual(1.15f, SpellweaveAbility.DamageMultiplier(5, perRank), 1e-5f);

            // unlearned (or no charge pending, at the call site) -> no bonus
            Assert.AreEqual(1.0f, SpellweaveAbility.DamageMultiplier(0, perRank), 1e-5f);

            // an added amount of 0 reproduces the rank-only value exactly
            Assert.AreEqual(1.15f, SpellweaveAbility.DamageMultiplier(5, perRank, 0.0), 1e-5f);

            // a +2% (0.02) Item Enchantment affinity contribution adds to the rank bonus
            Assert.AreEqual(1.17f, SpellweaveAbility.DamageMultiplier(5, perRank, 0.02), 1e-5f);
        }

        [TestMethod]
        public void Overchannel_WarDamageMultiplier_PerRankPlusArcaneLoreRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_overchannel_percent_per_rank").Item;
            Assert.AreEqual(0.095, perRank, 1e-9);

            // rank 3, no rider -> +28.5%
            Assert.AreEqual(1.285f, OverchannelAbility.DamageMultiplier(3, perRank, 0.0), 1e-5f);
            // rank 1 + a 0.06 (=+6%) Arcane Lore rider -> +15.5%
            Assert.AreEqual(1.155f, OverchannelAbility.DamageMultiplier(1, perRank, 0.06), 1e-5f);
            // unlearned -> no bonus even with a rider
            Assert.AreEqual(1.0f, OverchannelAbility.DamageMultiplier(0, perRank, 0.5), 1e-5f);
        }

        [TestMethod]
        public void SpellAoe_ManaSurcharge_DefaultsToPlusOneHundredPercentEach()
        {
            // Overchannel and Spell AOE surcharges each default to +100%, stacking to +200%
            Assert.AreEqual(1.0, PropertyManager.GetDouble("class_ability_overchannel_mana_surcharge").Item, 1e-9);
            Assert.AreEqual(1.0, PropertyManager.GetDouble("class_ability_spellaoe_mana_surcharge").Item, 1e-9);
        }

        /// <summary>
        /// Reads a file out of the server source tree, for the call-graph guards below. Same shape as
        /// BloodMageMechanicsTests.ReadServerSource - see the note there on why a structural invariant that
        /// needs a live Player is pinned at the source level instead.
        /// </summary>
        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path);
        }

        /// <summary>
        /// REGRESSION (2026-09-13). Widening Overchannel from War Magic to every school took the school test
        /// off GetClassAbilityManaSurcharge and put nothing in its place, so an Overchannel owner paid +100%
        /// mana on EVERY cast - every armor buff, every Strength cast, every Imperil - for a damage bonus
        /// only a damaging projectile can ever receive.
        ///
        /// The rule is not "which school" but "which spell shape", and Life Magic is the case that proves
        /// it: it holds both heals and damaging spells, so no school test can be right here. The predicate
        /// is derived from the damage bonus's own call path - see OverchannelAbility.DamageBonusApplies.
        /// </summary>
        [TestMethod]
        public void Overchannel_ManaSurcharge_AppliesOnlyWhereItsDamageBonusCan()
        {
            // the payoff set: a damaging projectile, whatever school it belongs to. A war bolt and a void
            // bolt carry the same enum value, which is why the widening itself was correct.
            Assert.IsTrue(OverchannelAbility.DamageBonusApplies(SpellType.Projectile),
                "a damaging projectile of any school must still be surcharged - that widening is the ruling being implemented");

            // the regression: non-damaging casts, which no Overchannel bonus can ever reach
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.Enchantment),
                "an armor buff or an Imperil has no damage roll for the bonus to multiply, so it must not be surcharged");
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.FellowEnchantment));
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.EnchantmentProjectile),
                "reaches CalculateDamage, but OnCollideObject discards the damage number and applies the enchantment instead");

            // damaging, but resolved at damage sites that carry no Overchannel term at all
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.LifeProjectile),
                "Hecatomb and Raven Fury take CalculateDamage's other branch (ApplyLifeProjectileClassAbilityDamage)");
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.Boost),
                "Harm resolves outside SpellProjectile entirely (ApplyHarmClassAbilityDamage)");
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.FellowBoost));

            // and everything with no damage roll whatsoever
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.Transfer));
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.Dispel));
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.FellowDispel));
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.PortalSending));
            Assert.IsFalse(OverchannelAbility.DamageBonusApplies(SpellType.Undef));
        }

        /// <summary>
        /// The pure predicate above is only worth anything if the surcharge site actually consults it, and
        /// that wiring needs a live Player to execute - so it is pinned at the source level instead, in the
        /// same source-guard style BloodMageMechanicsTests uses for its call-graph invariants.
        ///
        /// This is the assertion that fails if the guard is ever "simplified" away again, which is exactly
        /// what happened when the War Magic school test came off.
        /// </summary>
        [TestMethod]
        public void Overchannel_ManaSurchargeSite_StillConsultsTheDamageBonusPredicate()
        {
            var buffs = ReadServerSource("ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");

            StringAssert.Contains(buffs, "OverchannelAbility.DamageBonusApplies(spell.MetaSpellType)",
                "GetClassAbilityManaSurcharge must gate Overchannel's surcharge on the same condition its damage bonus uses, or a caster pays +100% mana on buffs and debuffs that can never take the bonus");

            // the Spell AOE half of the same method keeps its own payoff's condition
            StringAssert.Contains(buffs, "SpellProjectile.GetProjectileSpellType(spell.Id) == ProjectileSpellType.Arc",
                "Spell AOE's surcharge must stay gated on the Arc casts it actually radiates from");
        }

        [TestMethod]
        public void SpellAoe_RadiatedDamageFraction_RankLadder_CappedAtThreeQuarters()
        {
            var r1 = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r1").Item;
            var r2 = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r2").Item;
            var r3 = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r3").Item;
            var cap = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_cap").Item;

            // spec: the 3-rank ladder radiates 15 / 30 / 50% of the triggering hit
            Assert.AreEqual(0.15, r1, 1e-9);
            Assert.AreEqual(0.30, r2, 1e-9);
            Assert.AreEqual(0.50, r3, 1e-9);
            Assert.AreEqual(0.75, cap, 1e-9);

            Assert.AreEqual(r1, SpellAoeAbility.BaseDamageMult(1, r1, r2, r3), 1e-9);
            Assert.AreEqual(r2, SpellAoeAbility.BaseDamageMult(2, r1, r2, r3), 1e-9);
            Assert.AreEqual(r3, SpellAoeAbility.BaseDamageMult(3, r1, r2, r3), 1e-9);

            // rank is clamped, never rejected: above MaxRank behaves as max, 0/negative floors at rank 1
            Assert.AreEqual(r3, SpellAoeAbility.BaseDamageMult(4, r1, r2, r3), 1e-9);
            Assert.AreEqual(r1, SpellAoeAbility.BaseDamageMult(0, r1, r2, r3), 1e-9);
            Assert.AreEqual(r1, SpellAoeAbility.BaseDamageMult(-1, r1, r2, r3), 1e-9);

            // no rider -> the rank's fraction; a big rider clamps to the shared cap; the rank base is the floor
            Assert.AreEqual(r1, Math.Clamp(r1 + 0.0, r1, cap), 1e-9);
            Assert.AreEqual(0.75, Math.Clamp(r3 + 0.40, r3, cap), 1e-9);
            Assert.AreEqual(r3, Math.Clamp(r3 - 0.10, r3, cap), 1e-9);
        }

        // SpellAoe_ManaConversionRider_DivisorsAreDoubledAgainstTheRankLadder removed: Spell AOE's Mana
        // Conversion rider moved from an additive dual-ratio quotient to the shared MULTIPLICATIVE affinity
        // model (2026-09-12 overhaul), and its two divisor tunables (class_ability_spellaoe_manaconv_per_trained
        // / _per_spec) were deleted along with it - this test asserted their now-deleted defaults.

        [TestMethod]
        public void EchoCast_ChancePerRank_PlusMagicItemTinkerRider()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;
            Assert.AreEqual(0.08, chanceBase, 1e-9);
            Assert.AreEqual(0.08, chanceStep, 1e-9);

            // ranks 1-3 with no rider -> 8/16/24%
            Assert.AreEqual(0.08f, EchoCastAbility.EchoChance(1, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.16f, EchoCastAbility.EchoChance(2, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.24f, EchoCastAbility.EchoChance(3, chanceBase, chanceStep, 0.0), 1e-5f);
            // rank 3 + a 0.05 (=+5%) tinker rider -> 29%
            Assert.AreEqual(0.29f, EchoCastAbility.EchoChance(3, chanceBase, chanceStep, 0.05), 1e-5f);
            // unlearned -> no chance
            Assert.AreEqual(0.0f, EchoCastAbility.EchoChance(0, chanceBase, chanceStep, 0.5), 1e-5f);
        }

        [TestMethod]
        public void ElementalRend_ChancePerRank_PlusLifeMagicRider()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_elementalrend_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_elementalrend_chance_step").Item;
            Assert.AreEqual(0.10, chanceBase, 1e-9);
            Assert.AreEqual(0.08, chanceStep, 1e-9);

            // ranks 1-3 with no rider -> 10/18/26%
            Assert.AreEqual(0.10f, ElementalRendAbility.RendChance(1, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.18f, ElementalRendAbility.RendChance(2, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.26f, ElementalRendAbility.RendChance(3, chanceBase, chanceStep, 0.0), 1e-5f);
            // unlearned -> no chance
            Assert.AreEqual(0.0f, ElementalRendAbility.RendChance(0, chanceBase, chanceStep, 0.5), 1e-5f);
        }

        [TestMethod]
        public void ElementalRend_VulnMapping_MatchesElement_AndClampsLevel()
        {
            // each war damage type maps to its own element's "...Other" vuln at the spell's level
            Assert.AreEqual(SpellId.FireVulnerabilityOther3, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Fire, 3));
            Assert.AreEqual(SpellId.ColdVulnerabilityOther1, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Cold, 1));
            Assert.AreEqual(SpellId.LightningVulnerabilityOther6, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Electric, 6));
            Assert.AreEqual(SpellId.BladeVulnerabilityOther2, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Slash, 2));

            // level 7 is the correct mapping now (was wrongly clamped to 6 before the fix)
            Assert.AreEqual(SpellId.AcidVulnerabilityOther7, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Acid, 7));
            // a level 0 spell clamps up to level 1
            Assert.AreEqual(SpellId.BludgeonVulnerabilityOther1, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Bludgeon, 0));

            // non-elemental / unmapped damage types have no single-element vuln
            Assert.IsNull(ElementalRendAbility.GetVulnerabilitySpell(DamageType.Nether, 3));
            Assert.IsNull(ElementalRendAbility.GetVulnerabilitySpell(DamageType.Health, 3));
        }

        [TestMethod]
        public void ElementalRend_VulnMapping_Level7ReturnsGiftFamily()
        {
            // level 7 is the "X's Gift" family (no roman-numeral name) - assert via the numeric id, since
            // the display name has no "VII" to grep for.
            Assert.AreEqual((uint)2164, (uint)ElementalRendAbility.GetVulnerabilitySpell(DamageType.Slash, 7).Value);
            Assert.AreEqual((uint)2170, (uint)ElementalRendAbility.GetVulnerabilitySpell(DamageType.Fire, 7).Value);
            Assert.AreEqual((uint)2172, (uint)ElementalRendAbility.GetVulnerabilitySpell(DamageType.Electric, 7).Value);
        }

        [TestMethod]
        public void ElementalRend_VulnMapping_Level8ReturnsIncantationFamily()
        {
            // level 8 is "Incantation of X Vulnerability Other"
            Assert.AreEqual((uint)4475, (uint)ElementalRendAbility.GetVulnerabilitySpell(DamageType.Slash, 8).Value);
            Assert.AreEqual((uint)4481, (uint)ElementalRendAbility.GetVulnerabilitySpell(DamageType.Fire, 8).Value);
            Assert.AreEqual((uint)4483, (uint)ElementalRendAbility.GetVulnerabilitySpell(DamageType.Electric, 8).Value);
        }

        [TestMethod]
        public void ElementalRend_VulnMapping_AboveLevel8ClampsToLevel8()
        {
            // regression guard for the old bug: a spell above the top of the ladder must clamp to the
            // ACTUAL top (level 8), not the old wrongly-hardcoded level 6.
            Assert.AreEqual(SpellId.BladeVulnerabilityOther8, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Slash, 10));
            Assert.AreEqual(SpellId.FireVulnerabilityOther8, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Fire, 99));
            Assert.AreEqual(SpellId.ColdVulnerabilityOther8, ElementalRendAbility.GetVulnerabilitySpell(DamageType.Cold, 10));

            // an unmapped damage type still returns null regardless of level
            Assert.IsNull(ElementalRendAbility.GetVulnerabilitySpell(DamageType.Health, 10));
        }

        /// <summary>
        /// FastAimAbility.LowEndBonus is a bonus (0.0-1.0), not a "1 + ..." multiplier like the old Eagle
        /// Eye shape - see the type's doc comment. This pins the FOUR required properties named in the
        /// 2026-09-29 rework spec.
        /// </summary>
        [TestMethod]
        public void FastAim_LowEndBonus_PerRank()
        {
            var perRank = PropertyManager.GetDouble("class_ability_fastaim_low_end_per_rank").Item;
            Assert.AreEqual(0.10, perRank, 1e-9);

            // rank 0 / untrained Run (neutral multiplier 1.0) / no gear -> bonus is exactly 0, so
            // GetAccuracyMod's low = 0.6 + 0 = 0.6, bit-identical to the pre-Fast-Aim AccuracyLevel + 0.6f.
            Assert.AreEqual(0.0f, FastAimAbility.LowEndBonus(0, perRank), 1e-6f);

            Assert.AreEqual(0.10f, FastAimAbility.LowEndBonus(1, perRank), 1e-6f);
            Assert.AreEqual(0.20f, FastAimAbility.LowEndBonus(2, perRank), 1e-6f);
            Assert.AreEqual(0.30f, FastAimAbility.LowEndBonus(3, perRank), 1e-6f);
        }

        [TestMethod]
        public void FastAim_AtZeroBonus_AccuracyModReducesToAccuracyLevelPlusPointSix()
        {
            // The formula: low = 0.6 + bonus; accuracyMod = low + AccuracyLevel * (1.6 - low).
            // At bonus == 0, low == 0.6, so accuracyMod == 0.6 + AccuracyLevel * 1.0 == AccuracyLevel + 0.6 -
            // the untrained case must be bit-identical to the pre-rework expression.
            foreach (var accuracyLevel in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
            {
                const float bonus = 0.0f;
                var low = 0.6f + bonus;
                var accuracyMod = low + accuracyLevel * (1.6f - low);

                Assert.AreEqual(accuracyLevel + 0.6f, accuracyMod, 1e-6f);
            }
        }

        [TestMethod]
        public void FastAim_AtHalfBonus_LowEndLiftsButHighEndDoesNot()
        {
            // At fastAimBonus == 0.5: low = 1.1. Slider 0.0 -> 1.1, slider 1.0 -> still exactly 1.6, the
            // unmodified ceiling - the ability must never move the high end.
            const float bonus = 0.5f;
            var low = 0.6f + bonus;

            var atSliderZero = low + 0.0f * (1.6f - low);
            var atSliderOne = low + 1.0f * (1.6f - low);

            Assert.AreEqual(1.1f, atSliderZero, 1e-6f);
            Assert.AreEqual(1.6f, atSliderOne, 1e-6f);
        }

        /// <summary>
        /// The clamp at 1.0 is load-bearing: without it, a large enough affinity multiplier pushes
        /// fastAimBonus above 1.0, low above 1.6, and the curve INVERTS - fast, sloppy fire would out-roll
        /// careful aim. This proves a huge multiplier cannot escape the clamp.
        /// </summary>
        [TestMethod]
        public void FastAim_HugeAffinityMultiplier_CannotInvertTheCurve()
        {
            const double perRank = 0.10;
            const double saturatingMultiplier = 1000.0; // a wildly saturating Run rider

            var bonus = FastAimAbility.LowEndBonus(3, perRank, saturatingMultiplier);

            Assert.AreEqual(1.0f, bonus, 1e-6f, "the clamp must cap the bonus at 1.0 however large the affinity multiplier is");

            var low = 0.6f + bonus;
            Assert.AreEqual(1.6f, low, 1e-6f);

            // at the clamp, low == 1.6 == the high end: the curve is flat, not inverted. A genuine
            // inversion (low > 1.6) would make accuracyMod DECREASE as AccuracyLevel rises - assert that
            // does not happen across the slider.
            var atSliderZero = low + 0.0f * (1.6f - low);
            var atSliderOne = low + 1.0f * (1.6f - low);
            Assert.IsTrue(atSliderOne >= atSliderZero, "accuracyMod must never decrease as AccuracyLevel rises");
        }

        [TestMethod]
        public void FastAim_RankZero_ContributesNothing()
        {
            Assert.AreEqual(0.0f, FastAimAbility.LowEndBonus(0, 0.10, 1.0, 0.0), 1e-6f);
            // even with a strong affinity multiplier, rank 0 has no rank bonus to multiply
            Assert.AreEqual(0.0f, FastAimAbility.LowEndBonus(0, 0.10, 5.0, 0.0), 1e-6f);
        }

        [TestMethod]
        public void DoubleVolley_ChancePerRank_IsSixTwelveEighteen()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item;
            Assert.AreEqual(0.06, chanceBase, 1e-9);
            Assert.AreEqual(0.06, chanceStep, 1e-9);

            // affinityMultiplier = 1.0 (neutral, untrained Run) reproduces the rank-only ladder exactly
            Assert.AreEqual(0.06f, DoubleVolleyAbility.Chance(1, chanceBase, chanceStep, 1.0), 1e-5f);
            Assert.AreEqual(0.12f, DoubleVolleyAbility.Chance(2, chanceBase, chanceStep, 1.0), 1e-5f);
            Assert.AreEqual(0.18f, DoubleVolleyAbility.Chance(3, chanceBase, chanceStep, 1.0), 1e-5f);
            Assert.AreEqual(0.0f, DoubleVolleyAbility.Chance(0, chanceBase, chanceStep, 1.0), 1e-5f);
        }

        [TestMethod]
        public void DoubleVolley_RunAffinity_MultipliesTheOwnRankBonus_Uncapped()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item;

            // rank 3: rankBonus = 0.06 + 2*0.06 = 0.18. A 1.5x multiplier -> 0.27, and passing 0.0 for the
            // cap must leave it UNCAPPED (never clamped against class_ability_affinity_chance_cap).
            var chance = DoubleVolleyAbility.Chance(3, chanceBase, chanceStep, 1.5, 0.0);

            Assert.AreEqual(0.27f, chance, 1e-5f);
        }

        [TestMethod]
        public void Withering_VoidDotMultiplier_PerRankPlusCreatureEnchantRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_withering_percent_per_rank").Item;
            Assert.AreEqual(0.095, perRank, 1e-9);

            Assert.AreEqual(1.285f, WitheringAbility.DotMultiplier(3, perRank, 0.0), 1e-5f);
            Assert.AreEqual(1.155f, WitheringAbility.DotMultiplier(1, perRank, 0.06), 1e-5f);
            Assert.AreEqual(1.0f, WitheringAbility.DotMultiplier(0, perRank, 0.5), 1e-5f);
        }

        [TestMethod]
        public void Hemomancy_TickMultiplier_PerRankPlusHealingRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_hemomancy_tick_percent_per_rank").Item;
            Assert.AreEqual(0.02, perRank, 1e-9);

            // rank 5, no affinity: 1 + 5*0.02 = 1.10 (+10%, the signed-off max)
            Assert.AreEqual(1.10f, HemomancyAbility.TickMultiplier(5, perRank, 0.0), 1e-5f);

            // rank 1 with an affinity rider: 1 + 0.02 + 0.01
            Assert.AreEqual(1.03f, HemomancyAbility.TickMultiplier(1, perRank, 0.01), 1e-5f);

            // rank 0: bit-identical to no ability at all, regardless of any (impossible) rider
            Assert.AreEqual(1.0f, HemomancyAbility.TickMultiplier(0, perRank, 0.5), 1e-5f);
        }

        [TestMethod]
        public void Hemomancy_DrainMultiplier_PerRankPlusHealingRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_hemomancy_drain_percent_per_rank").Item;
            Assert.AreEqual(0.02, perRank, 1e-9);

            Assert.AreEqual(1.10f, HemomancyAbility.DrainMultiplier(5, perRank, 0.0), 1e-5f);
            Assert.AreEqual(1.03f, HemomancyAbility.DrainMultiplier(1, perRank, 0.01), 1e-5f);
            Assert.AreEqual(1.0f, HemomancyAbility.DrainMultiplier(0, perRank, 0.5), 1e-5f);
        }

        /// <summary>
        /// The heal fraction stays FLAT across every rank (never per-rank, unlike the tick/Drain bonuses
        /// above) and is zero only at rank 0 - pinning exactly the asymmetry HemomancyAbility's doc comment
        /// describes.
        /// </summary>
        [TestMethod]
        public void Hemomancy_HealFraction_IsFlatAcrossRanksAndZeroAtRankZero()
        {
            var healFraction = PropertyManager.GetDouble("class_ability_hemomancy_heal_fraction").Item;
            Assert.AreEqual(0.01, healFraction, 1e-9);

            Assert.AreEqual(0.01, HemomancyAbility.HealFraction(1, healFraction), 1e-9);
            Assert.AreEqual(0.01, HemomancyAbility.HealFraction(5, healFraction), 1e-9);
            Assert.AreEqual(0.0, HemomancyAbility.HealFraction(0, healFraction), 1e-9);
        }

        // Heavy Draw's Run-rider stamina-surcharge test lived here until 2026-09-12, when the class ability
        // overhaul retired the ability and deleted HeavyDrawAbility. What survives it is the
        // RetiredClassAbilities["heavydraw"] refund coverage below. The class_ability_heavydraw_* tunables
        // stay registered but unused, so an existing stage or prod config row does not orphan.

        [TestMethod]
        public void Taunt_LoyaltyRider_ExtendsDuration_ClampedToCap()
        {
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;
            var cap = PropertyManager.GetDouble("class_ability_taunt_loyalty_bonus_cap_seconds").Item;
            Assert.AreEqual(11.0, baseDuration, 1e-9);
            Assert.AreEqual(10.0, cap, 1e-9);

            // rider = 0 reproduces today's duration exactly
            Assert.AreEqual(11.0, TauntAbility.EffectiveDuration(baseDuration, 0.0, cap), 1e-9);

            // a +4 second rider extends the hold duration
            Assert.AreEqual(15.0, TauntAbility.EffectiveDuration(baseDuration, 4.0, cap), 1e-9);

            // clamps at the cap: a +25 second rider still only adds 10 seconds, never past the cap
            Assert.AreEqual(21.0, TauntAbility.EffectiveDuration(baseDuration, 25.0, cap), 1e-9);
        }

        /// <summary>
        /// REWORKED 2026-08-17 (Berserker/Rogue balance pass): the chance is now flat and rank-invariant,
        /// with no affinity rider at all - rank instead raises PoisonDamageBonus (see the tests below).
        /// </summary>
        [TestMethod]
        public void AcidProc_Chance_FlatAndRankInvariant()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_acidproc_chance_base").Item;
            Assert.AreEqual(0.30, chanceBase, 1e-9);

            // ranks 1-3 are all the same flat 30% - rank does not raise the chance any more
            Assert.AreEqual(0.30f, AcidProcAbility.Chance(1, chanceBase), 1e-5f);
            Assert.AreEqual(0.30f, AcidProcAbility.Chance(2, chanceBase), 1e-5f);
            Assert.AreEqual(0.30f, AcidProcAbility.Chance(3, chanceBase), 1e-5f);

            // unlearned -> no chance
            Assert.AreEqual(0.0f, AcidProcAbility.Chance(0, chanceBase), 1e-5f);

            // the equipment-mod term still rides on top (EquipmentModId.AcidProc is unchanged)
            Assert.AreEqual(0.35f, AcidProcAbility.Chance(2, chanceBase, 0.05), 1e-5f);
        }

        /// <summary>
        /// REGRESSION, live bug 2026-08-04, exercised against PoisonDamageBonus since the 2026-08-17 rework
        /// moved the affinity there. MIGRATED 2026-09-12: the affinity parameter is now a multiplier (neutral
        /// 1.0, and the skill moved from Item Tinkering to Alchemy), not a raw additive quotient (neutral
        /// 0.0). GetClassAbilityAffinityMultiplier has no bound of its own, so an uncapped multiply is still
        /// linear in a skill value the server does not constrain - the cap now bounds the ADDED amount
        /// (rankBonus * multiplier - rankBonus) instead of a raw quotient.
        /// </summary>
        [TestMethod]
        public void AcidProc_PoisonDamageBonus_PerRankPlusClampedAlchemyRider()
        {
            // ranks 0-3 with a neutral (1.0) multiplier -> 0/25/50/75%
            Assert.AreEqual(0.0, AcidProcAbility.PoisonDamageBonus(0, 0.25, 0.25, 1.0), 1e-9);
            Assert.AreEqual(0.25, AcidProcAbility.PoisonDamageBonus(1, 0.25, 0.25, 1.0), 1e-9);
            Assert.AreEqual(0.50, AcidProcAbility.PoisonDamageBonus(2, 0.25, 0.25, 1.0), 1e-9);
            Assert.AreEqual(0.75, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, 1.0), 1e-9);

            // rank 3's rank-scaled base is 0.75; 1 + 2.09/0.75 adds the same absurd +2.09 the live
            // 2026-08-04 measurement produced, uncapped
            var hugeMultiplier = 1.0 + 2.09 / 0.75;
            Assert.AreEqual(2.09 + 0.75, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, hugeMultiplier), 1e-9);

            // capped at 0.20: base+step 0.75 + capped added amount 0.20 = 0.95
            Assert.AreEqual(0.95, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, hugeMultiplier, 0.20), 1e-9);

            // a legitimate rider under the cap is unchanged by it: 1 + 0.16/0.75 adds exactly 0.16
            var legitimateMultiplier = 1.0 + 0.16 / 0.75;
            Assert.AreEqual(0.91, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, legitimateMultiplier, 0.20), 1e-9);

            // cap 0 means uncapped
            Assert.AreEqual(2.84, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, hugeMultiplier, 0.0), 1e-9);
        }

        /// <summary>
        /// PoisonWeaponAbility.FlatBonus stays bit-identical for a player with zero Acid Proc ranks -
        /// REGRESSION guard for the 2026-08-17 rework, which folded the Acid Proc bonus into this shared
        /// method. A null attacker (used by the pure-math test above) can never own Acid Proc, so it must
        /// take the unmultiplied path.
        /// </summary>
        [TestMethod]
        public void PoisonWeapon_FlatBonus_UnaffectedWithoutAcidProc()
        {
            var perRank = PropertyManager.GetDouble("class_ability_poisonweapon_damage_per_rank").Item;

            Assert.AreEqual(perRank, PoisonWeaponAbility.FlatBonus(null, 1), 1e-9);
            Assert.AreEqual(3 * perRank, PoisonWeaponAbility.FlatBonus(null, 3), 1e-9);
        }

        [TestMethod]
        public void BreakArmor_Chance_FlatAndRankInvariant_PlusRider()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_breakarmor_chance").Item;
            Assert.AreEqual(0.15, chanceBase, 1e-9);

            // The third argument is the affinity MULTIPLIER as of the 2026-09-12 overhaul, NOT the raw
            // rider fraction it used to be. 1.0 means "no affinity" and must be bit-identical to rank
            // alone - that identity is the whole safety property of the migration.

            // ranks 1-3 are all the same flat 15% - rank buys the Imperil rung, not the proc odds
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(1, chanceBase, 1.0), 1e-5f);
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(2, chanceBase, 1.0), 1e-5f);
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(3, chanceBase, 1.0), 1e-5f);

            // unlearned -> no chance
            Assert.AreEqual(0.0f, BreakArmorAbility.Chance(0, chanceBase, 1.0), 1e-5f);

            // a Weapon Tinkering multiplier scales this ability's OWN bonus: 2.0 doubles the 15% base,
            // adding 0.15 on top of it
            Assert.AreEqual(0.30f, BreakArmorAbility.Chance(1, chanceBase, 2.0), 1e-5f);

            // a multiplier below 1.0 can never SUBTRACT - the added amount floors at zero
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(1, chanceBase, 0.0), 1e-5f);
        }

        [TestMethod]
        public void BreakArmorChance_AffinityCap_BoundsAnAbsurdRider_ButNotTheGearMod()
        {
            // control: an absurd affinity MULTIPLIER, uncapped, saturates the proc to certainty.
            // 15.0 adds 14x the 15% base, i.e. 2.10, on top of it.
            Assert.IsTrue(BreakArmorAbility.Chance(1, 0.15, 15.0) >= 1.0f);

            // capped at 0.20: base 0.15 + capped ADDED amount 0.20 = 0.35. Under the multiplicative
            // model the cap bounds what affinity ADDS - never the multiplier, never the base.
            Assert.AreEqual(0.35f, BreakArmorAbility.Chance(1, 0.15, 15.0, 0.20), 1e-5f);

            // the gear mod rides on top of the cap rather than inside it
            Assert.AreEqual(0.40f, BreakArmorAbility.Chance(1, 0.15, 15.0, 0.20, 0.05), 1e-5f);

            // cap 0 means uncapped: 0.15 + 2.10
            Assert.AreEqual(2.25f, BreakArmorAbility.Chance(1, 0.15, 15.0, 0.0), 1e-5f);
        }

        [TestMethod]
        public void BreakArmor_ImperilFor_MapsRankToTheRightRung()
        {
            Assert.AreEqual(SpellId.ImperilOther3, BreakArmorAbility.ImperilFor(1));
            Assert.AreEqual(SpellId.ImperilOther5, BreakArmorAbility.ImperilFor(2));
            Assert.AreEqual(SpellId.ImperilOther7, BreakArmorAbility.ImperilFor(3));
            Assert.AreEqual(SpellId.Undef, BreakArmorAbility.ImperilFor(0));
            Assert.AreEqual(SpellId.Undef, BreakArmorAbility.ImperilFor(4));
        }

        /// <summary>
        /// KillerInstinctAbility.PerOpeningCritDamage is the one arithmetic slice of that ability that is
        /// unit-testable without a live Player (the Opening pool and the crit-chance read both need one).
        /// Pins the shipped tunables (4/7/10% per rank 1-3) and the rank &lt;= 0 early-out.
        /// </summary>
        [TestMethod]
        public void KillerInstinct_PerOpeningCritDamage_MatchesTheShippedRankLadder()
        {
            var baseRate = PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_base").Item;
            var step = PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_step").Item;

            Assert.AreEqual(0.04, baseRate, 1e-9);
            Assert.AreEqual(0.03, step, 1e-9);

            Assert.AreEqual(0.04, KillerInstinctAbility.PerOpeningCritDamage(1, baseRate, step), 1e-9);
            Assert.AreEqual(0.07, KillerInstinctAbility.PerOpeningCritDamage(2, baseRate, step), 1e-9);
            Assert.AreEqual(0.10, KillerInstinctAbility.PerOpeningCritDamage(3, baseRate, step), 1e-9);

            // unlearned -> no per-Opening rate
            Assert.AreEqual(0.0, KillerInstinctAbility.PerOpeningCritDamage(0, baseRate, step), 1e-9);
            Assert.AreEqual(0.0, KillerInstinctAbility.PerOpeningCritDamage(-1, baseRate, step), 1e-9);
        }

        /// <summary>
        /// Three stacked Openings at rank 3 (the shipped max on both axes) is 30% crit damage before any
        /// Sneak Attack affinity - the number the ability's own Description text quotes.
        /// </summary>
        [TestMethod]
        public void KillerInstinct_PerOpeningCritDamage_TimesMaxStacks_MatchesDescribedCeiling()
        {
            var baseRate = PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_base").Item;
            var step = PropertyManager.GetDouble("class_ability_killerinstinct_critdamage_step").Item;
            var maxStacks = PropertyManager.GetLong("class_ability_killerinstinct_max_stacks").Item;

            Assert.AreEqual(3, maxStacks);

            var perOpeningAtRank3 = KillerInstinctAbility.PerOpeningCritDamage(3, baseRate, step);

            Assert.AreEqual(0.30, perOpeningAtRank3 * maxStacks, 1e-9);
        }

        /// <summary>
        /// REGRESSION, live bug 2026-08-04 - the Archmage half of the same defect. See
        /// AcidProcChance_AffinityCap_BoundsAnAbsurdRider_ButNotTheGearMod.
        /// </summary>
        [TestMethod]
        public void ElementalRendChance_AffinityCap_BoundsAnAbsurdRider_ButNotTheGearMod()
        {
            Assert.IsTrue(ElementalRendAbility.RendChance(3, 0.08, 0.06, 2.09) >= 1.0f);

            Assert.AreEqual(0.40f, ElementalRendAbility.RendChance(3, 0.08, 0.06, 2.09, 0.0, 0.20), 1e-5f);
            Assert.AreEqual(0.45f, ElementalRendAbility.RendChance(3, 0.08, 0.06, 2.09, 0.05, 0.20), 1e-5f);
            Assert.AreEqual(0.36f, ElementalRendAbility.RendChance(3, 0.08, 0.06, 0.16, 0.0, 0.20), 1e-5f);
            Assert.AreEqual(2.29f, ElementalRendAbility.RendChance(3, 0.08, 0.06, 2.09, 0.0, 0.0), 1e-5f);
        }

        [TestMethod]
        public void EmpoweredSummons_StatMultiplier_PerRankPlusLeadershipRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_empoweredsummons_percent_per_rank").Item;
            Assert.AreEqual(0.0833, perRank, 1e-9);

            // rank 3, no rider -> +24.99% (presents as +25%)
            Assert.AreEqual(1.2499f, EmpoweredSummonsAbility.StatMultiplier(3, perRank, 0.0), 1e-5f);
            // rank 1 + a 0.05 (=+5%) Leadership rider -> +13.33%
            Assert.AreEqual(1.1333f, EmpoweredSummonsAbility.StatMultiplier(1, perRank, 0.05), 1e-5f);
            // unlearned -> no bonus
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.StatMultiplier(0, perRank, 0.5), 1e-5f);
        }

        [TestMethod]
        public void EmpoweredSummonsLeech_PerRankPlusLeadershipRider_CreditsTheOwner()
        {
            const double percentPerRank = 0.03;

            // rank 3, no rider -> +9% (3/6/9% schedule at MaxRank 3)
            Assert.AreEqual(0.09, EmpoweredSummonsAbility.LeechFraction(3, percentPerRank, 0.0), 1e-9);
            // rank 1 + a 0.02 (=+2%) Leadership rider -> +5%
            Assert.AreEqual(0.05, EmpoweredSummonsAbility.LeechFraction(1, percentPerRank, 0.02), 1e-9);
            // unlearned -> no leech, however large the rider
            Assert.AreEqual(0.0, EmpoweredSummonsAbility.LeechFraction(0, percentPerRank, 0.5), 1e-9);
            // rank 2 -> +6% exactly (control: the 3/6/9 schedule, no rounding drift)
            Assert.AreEqual(0.06, EmpoweredSummonsAbility.LeechFraction(2, percentPerRank, 0.0), 1e-9);
        }

        /// <summary>
        /// REGRESSION, code review 2026-09-12: a combat pet's elemental resist floats must never reach or
        /// cross zero. bonusFraction (statMod - 1.0) is unbounded because the Leadership affinity rider has
        /// no cap, so at rank 3 with a large enough affinity multiplier the raw (1.0 - bonusFraction) formula
        /// goes negative, which would turn incoming elemental damage into healing - a sign inversion, not
        /// merely "too strong". EmpoweredSummonsAbility.ResistMultiplier is the clamp CombatPet.Init actually
        /// calls; this pins it directly, using a 10x+ affinity multiplier so the raw formula would genuinely
        /// have gone negative without the floor.
        /// </summary>
        [TestMethod]
        public void EmpoweredSummonsResistMultiplier_ExtremeAffinity_NeverReachesOrCrossesZero()
        {
            var perRank = PropertyManager.GetDouble("class_ability_empoweredsummons_percent_per_rank").Item;
            Assert.AreEqual(0.0833, perRank, 1e-9);

            const int rank = 3;
            const double extremeMultiplier = 10.0; // a 10x+ affinity multiplier - well within the observed range (PropertyManager.cs:1200's 5226 effective skill)
            const double floor = 0.05;

            var rankBonus = rank * perRank; // 0.2499
            var leadershipRider = rankBonus * extremeMultiplier - rankBonus; // the AMOUNT the multiplier adds

            var statMod = EmpoweredSummonsAbility.StatMultiplier(rank, perRank, leadershipRider);
            var bonusFraction = statMod - 1.0;

            // control: prove the raw, unclamped formula really would go negative here - otherwise this test
            // would not actually exercise the floor
            var rawResistMod = 1.0 - bonusFraction;
            Assert.IsTrue(rawResistMod < 0.0, "control: the raw formula must actually go negative for this test to mean anything");

            var clamped = EmpoweredSummonsAbility.ResistMultiplier(bonusFraction, floor);

            Assert.AreEqual(floor, clamped, 1e-9);
            Assert.IsTrue(clamped > 0.0, "a combat pet's resist multiplier must never reach or cross zero");
        }

        [TestMethod]
        public void UmbralSiphonLeech_PerRankPlusLoyaltyRider_HealsThePetOnly()
        {
            var perRank = PropertyManager.GetDouble("class_ability_umbralsiphon_percent_per_rank").Item;
            Assert.AreEqual(0.02, perRank, 1e-9);

            // rank 5, no rider -> +10% (2/4/6/8/10% schedule at MaxRank 5)
            Assert.AreEqual(0.10, UmbralSiphonAbility.LeechFraction(5, perRank, 0.0), 1e-9);
            // rank 1 + a 0.01 (=+1%) Loyalty rider -> +3%
            Assert.AreEqual(0.03, UmbralSiphonAbility.LeechFraction(1, perRank, 0.01), 1e-9);
            // unlearned -> no leech, however large the rider
            Assert.AreEqual(0.0, UmbralSiphonAbility.LeechFraction(0, perRank, 0.5), 1e-9);
            // rank 3 -> +6% exactly (control: the 2/4/6/8/10 schedule, no rounding drift)
            Assert.AreEqual(0.06, UmbralSiphonAbility.LeechFraction(3, perRank, 0.0), 1e-9);
        }

        [TestMethod]
        public void RatingSkill_BonusPerRank_IsThreeSixTen_AndClamped()
        {
            Assert.AreEqual(0, RatingAbility.BonusForRank(0));
            Assert.AreEqual(3, RatingAbility.BonusForRank(1));
            Assert.AreEqual(6, RatingAbility.BonusForRank(2));
            Assert.AreEqual(10, RatingAbility.BonusForRank(3));
            Assert.AreEqual(10, RatingAbility.BonusForRank(99));
            Assert.AreEqual(0, RatingAbility.BonusForRank(-5));
        }

        [TestMethod]
        public void Bundles_CoverTheirDocumentedSkills_AndAreTiered()
        {
            var bundles = System.Linq.Enumerable.ToList(BundleStatAbility.GenerateAll());

            // Phase 1 skill redistribution (2026-08-03): Advanced Weaponry and Questionable Tactics are
            // retired, leaving seven Training bundles - the original six classes' plus Blood Mage Training
            // (joined the same day, this branch). Spellsword Training (8th class, 2026-08-03) makes eight.
            Assert.AreEqual(8, bundles.Count);

            var rogue = bundles.Single(b => b.Definition.Id == ClassAbilityId.RogueTraining);
            // Rogue Training now bundles Sneak Attack and Deception directly (Questionable Tactics is retired)
            CollectionAssert.Contains(rogue.BundledSkills.ToArray(), Skill.Alchemy);
            CollectionAssert.Contains(rogue.BundledSkills.ToArray(), Skill.Lockpick);
            CollectionAssert.Contains(rogue.BundledSkills.ToArray(), Skill.SneakAttack);
            CollectionAssert.Contains(rogue.BundledSkills.ToArray(), Skill.Deception);
            Assert.AreEqual(ClassAbilityClass.Rogue, rogue.Definition.AbilityClass);
            Assert.AreEqual(1, rogue.Definition.Tier);

            var archer = bundles.Single(b => b.Definition.Id == ClassAbilityId.ArcherTraining);
            CollectionAssert.Contains(archer.BundledSkills.ToArray(), Skill.MissileDefense);
            CollectionAssert.DoesNotContain(archer.BundledSkills.ToArray(), Skill.Jump);

            var vanguard = bundles.Single(b => b.Definition.Id == ClassAbilityId.VanguardTraining);
            CollectionAssert.Contains(vanguard.BundledSkills.ToArray(), Skill.AssessPerson);
            CollectionAssert.Contains(vanguard.BundledSkills.ToArray(), Skill.ArmorTinkering);
            CollectionAssert.Contains(vanguard.BundledSkills.ToArray(), Skill.Shield);
            // Skill.Healing handed off to Blood Mage 2026-08-03 (a parallel build); not bundled here anymore
            CollectionAssert.DoesNotContain(vanguard.BundledSkills.ToArray(), Skill.Healing);
            Assert.AreEqual(3, vanguard.BundledSkills.Count);

            var voidTraining = bundles.Single(b => b.Definition.Id == ClassAbilityId.VoidTraining);
            CollectionAssert.Contains(voidTraining.BundledSkills.ToArray(), Skill.Loyalty);
            CollectionAssert.Contains(voidTraining.BundledSkills.ToArray(), Skill.Leadership);
            // Skill.CreatureEnchantment handed off to Blood Mage 2026-08-03 (a parallel build); not bundled here anymore
            CollectionAssert.DoesNotContain(voidTraining.BundledSkills.ToArray(), Skill.CreatureEnchantment);
            Assert.AreEqual(2, voidTraining.BundledSkills.Count);
        }

        [TestMethod]
        public void Registry_ContainsBundlesAndRatings_WithClassAndTierTags()
        {
            foreach (var id in new[] { ClassAbilityId.ArcherTraining, ClassAbilityId.CritRating, ClassAbilityId.DamageResistRating })
            {
                var def = ClassAbilityRegistry.Get(id);
                Assert.IsTrue(def.Implemented, $"{id} not implemented");
                Assert.AreNotEqual(ClassAbilityClass.None, def.AbilityClass, $"{id} has no class");
                Assert.IsTrue(def.Tier >= 1 && def.Tier <= 3, $"{id} tier {def.Tier} out of range");
            }
        }

        [TestMethod]
        public void LongDraw_DistanceRamp_ZeroClose_FullFar_LinearBetween()
        {
            // rank 3, +10%/rank peak, 15m..50m window -> peak +30% at/beyond 50m
            Assert.AreEqual(0.0, LongDrawAbility.DistanceBonus(10, 3, 0.10, 15, 50), 1e-9);   // below min
            Assert.AreEqual(0.0, LongDrawAbility.DistanceBonus(15, 3, 0.10, 15, 50), 1e-9);   // at min
            Assert.AreEqual(0.30, LongDrawAbility.DistanceBonus(50, 3, 0.10, 15, 50), 1e-9);  // at max
            Assert.AreEqual(0.30, LongDrawAbility.DistanceBonus(99, 3, 0.10, 15, 50), 1e-9);  // beyond max
            Assert.AreEqual(0.15, LongDrawAbility.DistanceBonus(32.5, 3, 0.10, 15, 50), 1e-9); // midpoint

            // degenerate range or rank 0 -> no bonus, no divide-by-zero
            Assert.AreEqual(0.0, LongDrawAbility.DistanceBonus(40, 3, 0.10, 50, 50), 1e-9);
            Assert.AreEqual(0.0, LongDrawAbility.DistanceBonus(40, 0, 0.10, 15, 50), 1e-9);
        }

        // Blood Fury's low-health ramp test lived here until 2026-08-17. The ability was retired in the
        // Berserker/Rogue balance pass and its handler deleted; what survives it is the
        // RetiredClassAbilities["bloodfury"] refund coverage below.

        [TestMethod]
        public void ClassAbilityScaling_BelowThreshold_ContributesNothing()
        {
            Assert.AreEqual(0.0, ClassAbilityScaling.Compute(100, false, 12, 9, 100), 1e-9);
            Assert.AreEqual(0.0, ClassAbilityScaling.Compute(50, false, 12, 9, 100), 1e-9);

            // a non-positive divisor is a disabled hook, not a divide-by-zero
            Assert.AreEqual(0.0, ClassAbilityScaling.Compute(400, false, 0, 0), 1e-9);
        }

        [TestMethod]
        public void SpellAoe_SecondaryBlast_ClimbsByRankAndNeverAmplifies()
        {
            var r1 = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r1").Item;
            var r2 = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r2").Item;
            var r3 = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r3").Item;

            // spec: radiated secondary blasts land at 15 / 30 / 50% of normal spell damage by rank
            Assert.AreEqual(0.15, r1, 1e-9);
            Assert.AreEqual(0.30, r2, 1e-9);
            Assert.AreEqual(0.50, r3, 1e-9);

            // the ladder must be strictly increasing, and every rung must deal positive damage
            // without ever exceeding the primary hit
            Assert.IsTrue(r1 < r2 && r2 < r3, $"SpellAoe rank ladder {r1}/{r2}/{r3} must be strictly increasing");
            foreach (var mult in new[] { r1, r2, r3 })
                Assert.IsTrue(mult > 0.0 && mult <= 1.0, $"SpellAoe damage multiplier {mult} must be in (0, 1]");
        }

        [TestMethod]
        public void PinningShot_RepricedBackToOnePerRank()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.PinningShot);

            Assert.AreEqual(2, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank,
                "Pinning Shot's 2026-09-13 premium reprice (2/rank) was reversed 2026-09-29 back to 1/rank");
        }

        [TestMethod]
        public void SpellAoe_IsAThreeRankTierOneGameChanger_CostingOnePerRank()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.SpellAoe);

            Assert.AreEqual(1, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank,
                "Spell AOE was repriced off the 1/2/3 escalator onto flat 1/rank by the 2026-09-12 class ability overhaul");
        }

        [TestMethod]
        public void SpellAoe_Radius_IsPositive()
        {
            var radius = PropertyManager.GetDouble("class_ability_spellaoe_radius").Item;

            // spec: 5m reach; must be > 0 or the skill can never find a neighbor to strike
            Assert.AreEqual(5.0, radius, 1e-9);
            Assert.IsTrue(radius > 0.0);
        }

        private static WorldObject CreateBareWorldObject()
        {
            var biota = new Biota
            {
                Id = 0x80000042,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        [TestMethod]
        public void RetiredClassAbilities_KnownKey_RefundsCumulativeCostPerRank_AndClampsRank()
        {
            // advanced_weaponry: CostPerRank {1,1,1}, MaxRank 3
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("advanced_weaponry", 1, out var points1, out var name1));
            Assert.AreEqual(1, points1);
            Assert.AreEqual("Advanced Weaponry", name1);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("advanced_weaponry", 2, out var points2, out _));
            Assert.AreEqual(2, points2);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("advanced_weaponry", 3, out var points3, out _));
            Assert.AreEqual(3, points3);

            // rank above MaxRank clamps rather than over-refunding
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("advanced_weaponry", 99, out var pointsClamped, out _));
            Assert.AreEqual(3, pointsClamped);

            // questionable_tactics: same shape, different name
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("questionable_tactics", 3, out var qtPoints, out var qtName));
            Assert.AreEqual(3, qtPoints);
            Assert.AreEqual("Questionable Tactics", qtName);

            // streaktoarc: a flat one-time unlock, CostPerRank {3}, MaxRank 1 - rank clamps to 1 even if
            // a stale row somehow held a higher NumTimesCompleted
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("streaktoarc", 1, out var streakPoints, out var streakName));
            Assert.AreEqual(3, streakPoints);
            Assert.AreEqual("Streak-to-Arc", streakName);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("streaktoarc", 5, out var streakClamped, out _));
            Assert.AreEqual(3, streakClamped);

            // bloodfury: retired 2026-08-17 at CostPerRank {3,3,3} / MaxRank 3, so a held rank 2 is worth 6
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("bloodfury", 1, out var bfPoints1, out var bfName));
            Assert.AreEqual(3, bfPoints1);
            Assert.AreEqual("Blood Fury", bfName);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("bloodfury", 2, out var bfPoints2, out _));
            Assert.AreEqual(6, bfPoints2);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("bloodfury", 3, out var bfPoints3, out _));
            Assert.AreEqual(9, bfPoints3);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("bloodfury", 99, out var bfClamped, out _));
            Assert.AreEqual(9, bfClamped);

            // enhanced_missiledefense: retired 2026-09-16 (Missile Defense ownership fix), CostPerRank
            // {1,1,1} / MaxRank 3, the flat Enhanced-skill rate - a held rank 3 cumulatively refunds 3
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("enhanced_missiledefense", 3, out var emdPoints, out var emdName));
            Assert.AreEqual(3, emdPoints);
            Assert.AreEqual("Enhanced Missile Defense", emdName);
        }

        /// <summary>
        /// The id-keyed overload prices a single prepaid TOKEN, not a held rank, so it returns ONE rank's cost
        /// where the string overload returns the cumulative cost. Player.RefundUnusedVoucher depends on that
        /// difference: a token is one rank that was paid for once.
        /// </summary>
        [TestMethod]
        public void RetiredClassAbilities_ById_RefundsOneRanksCost_NotTheCumulative()
        {
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.BloodFury, 1, out var tier1, out var name));
            Assert.AreEqual(3, tier1);
            Assert.AreEqual("Blood Fury", name);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.BloodFury, 3, out var tier3, out _));
            Assert.AreEqual(3, tier3);   // NOT 9 - the string overload's rank-3 answer

            // every retired id with a reserved token catalog slot must be refundable, or its unused tokens
            // strand prepaid points forever
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.AdvancedWeaponry, 1, out var awPoints, out _));
            Assert.AreEqual(1, awPoints);
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.QuestionableTactics, 3, out var qtPoints, out _));
            Assert.AreEqual(1, qtPoints);
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.StreakToArc, 1, out var staPoints, out _));
            Assert.AreEqual(3, staPoints);

            // Enhanced Missile Defense: retired 2026-09-16, synthetic id via ClassIdForSkill - an unused
            // tier-1 token in a pack refunds ONE rank's cost (1), not the cumulative string-overload answer
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(
                EnhancedStatAbility.ClassIdForSkill(Skill.MissileDefense), 1, out var emdTier1, out var emdIdName));
            Assert.AreEqual(1, emdTier1);
            Assert.AreEqual("Enhanced Missile Defense", emdIdName);
        }

        [TestMethod]
        public void RetiredClassAbilities_ById_RejectsLiveIdsAndOutOfRangeTiers()
        {
            // a LIVE ability must not resolve here - it is refunded from the live registry instead
            Assert.IsFalse(RetiredClassAbilities.TryGetRefund(ClassAbilityId.Executioner, 1, out var livePoints, out var liveName));
            Assert.AreEqual(0, livePoints);
            Assert.IsNull(liveName);

            // Streak-to-Arc was a one-rank unlock, so a tier-2 token for it never existed and is not priced
            Assert.IsFalse(RetiredClassAbilities.TryGetRefund(ClassAbilityId.StreakToArc, 2, out var overTier, out _));
            Assert.AreEqual(0, overTier);

            Assert.IsFalse(RetiredClassAbilities.TryGetRefund(ClassAbilityId.BloodFury, 0, out var zeroTier, out _));
            Assert.AreEqual(0, zeroTier);

            Assert.IsFalse(RetiredClassAbilities.TryGetRefund(ClassAbilityId.BloodFury, 4, out var pastMax, out _));
            Assert.AreEqual(0, pastMax);
        }

        /// <summary>
        /// The three ids reserved in Phase 0 of the Berserker/Rogue balance pass. Their handlers land in
        /// Phase 1; what must hold NOW is that the numeric values two parallel worktrees were told to build
        /// against are exactly these, and that Blood Fury's id stays reserved rather than being reused.
        /// </summary>
        [TestMethod]
        public void BalancePassIds_AreReservedAtTheirAgreedValues()
        {
            Assert.AreEqual(71, (int)ClassAbilityId.BreakArmor);
            Assert.AreEqual(72, (int)ClassAbilityId.Surefooted);
            Assert.AreEqual(73, (int)ClassAbilityId.PocketSand);

            Assert.AreEqual(35, (int)ClassAbilityId.BloodFury);
            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.BloodFury),
                "Blood Fury is retired - its id stays reserved but must not be registered");
        }

        [TestMethod]
        public void RetiredClassAbilities_UnknownKey_ReturnsFalse()
        {
            Assert.IsFalse(RetiredClassAbilities.TryGetRefund("not_a_real_ability", 1, out var points, out var name));
            Assert.AreEqual(0, points);
            Assert.IsNull(name);

            Assert.IsFalse(RetiredClassAbilities.TryGetRefund(null, 1, out var pointsNull, out var nameNull));
            Assert.AreEqual(0, pointsNull);
            Assert.IsNull(nameNull);
        }

        [TestMethod]
        public void RetiredClassAbilities_ZeroRank_RefundsZeroPoints()
        {
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("advanced_weaponry", 0, out var points, out var name));
            Assert.AreEqual(0, points);
            Assert.AreEqual("Advanced Weaponry", name);
        }

        [TestMethod]
        public void HuntersMark_MarkPercent_ScalesByRankAndAffinity()
        {
            // rank alone: 1% per rank, no affinity
            Assert.AreEqual(0.01, HuntersMarkAbility.MarkPercent(1, 0.01, 1.0), 1e-9);
            Assert.AreEqual(0.05, HuntersMarkAbility.MarkPercent(5, 0.01, 1.0), 1e-9);

            // unlearned -> nothing
            Assert.AreEqual(0.0, HuntersMarkAbility.MarkPercent(0, 0.01, 1.0), 1e-9);

            // affinity MULTIPLIES the rank bonus, not a gear term: rank 5 (5%) at a 2.0x affinity
            // multiplier is 10%, not 5% + 2%
            Assert.AreEqual(0.10, HuntersMarkAbility.MarkPercent(5, 0.01, 2.0), 1e-9);

            // zero effective affinity (multiplier exactly 1.0) is bit-identical to rank-only
            Assert.AreEqual(HuntersMarkAbility.MarkPercent(3, 0.01, 1.0), HuntersMarkAbility.MarkPercent(3, 0.01, 1.0), 1e-9);
        }

        [TestMethod]
        public void HuntersMark_MarkStatModVal_IsOnePlusThePercent()
        {
            Assert.AreEqual(1.0f, HuntersMarkAbility.MarkStatModVal(0.0), 1e-6f);
            Assert.AreEqual(1.05f, HuntersMarkAbility.MarkStatModVal(0.05), 1e-6f);

            // never below 1.0 (no "anti-vulnerability" from a negative input)
            Assert.AreEqual(1.0f, HuntersMarkAbility.MarkStatModVal(-0.5), 1e-6f);
        }

        [TestMethod]
        public void RallyingPresence_ReductionFraction_ScalesByRankAndAffinity()
        {
            Assert.AreEqual(0.01, RallyingPresenceMath.ReductionFraction(1, 0.01, 1.0), 1e-9);
            Assert.AreEqual(0.05, RallyingPresenceMath.ReductionFraction(5, 0.01, 1.0), 1e-9);
            Assert.AreEqual(0.0, RallyingPresenceMath.ReductionFraction(0, 0.01, 1.0), 1e-9);

            // affinity MULTIPLIES the rank bonus
            Assert.AreEqual(0.10, RallyingPresenceMath.ReductionFraction(5, 0.01, 2.0), 1e-9);
        }

        [TestMethod]
        public void RallyingPresence_SelfShare_IsExactlyHalfOfTheAllyReduction()
        {
            var allyReduction = RallyingPresenceMath.ReductionFraction(5, 0.01, 1.0); // 5%
            var selfReduction = RallyingPresenceMath.SelfShareFraction(allyReduction, 0.50);

            Assert.AreEqual(0.025, selfReduction, 1e-9);
            Assert.AreEqual(allyReduction / 2.0, selfReduction, 1e-9);
        }

        [TestMethod]
        public void RallyingPresence_DamageMultiplier_ClampsAtZeroAndOne()
        {
            Assert.AreEqual(1.0f, RallyingPresenceMath.DamageMultiplier(0.0), 1e-6f);
            Assert.AreEqual(0.95f, RallyingPresenceMath.DamageMultiplier(0.05), 1e-6f);

            // an absurd, uncapped reduction floors at 0 rather than inverting into bonus damage
            Assert.AreEqual(0.0f, RallyingPresenceMath.DamageMultiplier(1.5), 1e-6f);
        }
    }
}

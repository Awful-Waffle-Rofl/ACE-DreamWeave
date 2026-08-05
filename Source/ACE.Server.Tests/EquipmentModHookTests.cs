using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Phase 3 tests for the equipment-mod combat hooks: the axis-composition rule, the rank-0 degenerate
    /// case, and the machinery-vs-standalone gating, all exercised through the pure ability math each hook
    /// composes into.
    ///
    /// THE RULE UNDER TEST. A gear term is added INSIDE its ability's own parenthesis -
    /// (1 + rankBonus + rider + gearMod) - never as a separate multiplier. Standalone mods are not
    /// rank-gated, so at rank 0 that degenerates to (1 + gearMod). Machinery mods sit behind the same rank
    /// check as the ability, so at rank 0 they contribute nothing at all. Every helper defaults its gear
    /// parameter to 0, so a build with no equipment mods reproduces the previous numbers exactly - the
    /// "gear-free equals master" assertions below pin that.
    ///
    /// DEFERRED TO THE PHASE 4 LIVE LOOP - these need a live Player with equipped items, a session and a
    /// landblock, so they cannot be reached from a unit test:
    ///   - The no-double-apply invariant end to end: that Player.GetStandaloneEquipmentModValue returns 0
    ///     while the linked ability is learned, so an owning player's handler is the ONLY path that applies
    ///     the mod, and an unowning player's Player_EquipmentMods mirror is. Both halves are proven here as
    ///     math; what needs a live character is that the gate actually flips on learn/unlearn.
    ///   - Player.ApplyEquipmentModOutgoingDamage / ApplyEquipmentModIncomingDamage themselves (they read
    ///     EquippedObjects, Location, Health and schedule action chains).
    ///   - That a rank-0 Venom proc fires on multishot extra arrows and on Riposte counters (both route
    ///     through DamageTarget, which is where the call sits).
    ///   - Thorns: that a blocked hit never reaches TakeDamage, so the avoidance path and the rank-0 path
    ///     cannot both reflect for one attack.
    ///   - Attack speed: that the Attack Speed mod and the Frenzied Pace per-stack mod both land inside the
    ///     class_ability_attack_speed_ceiling clamp in ApplyClassAbilityAttackSpeed.
    ///   - Lingering Fury actually extending a live frenzy window on the heartbeat.
    /// </summary>
    [TestClass]
    public class EquipmentModHookTests
    {
        private const double Epsilon = 1e-6;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---------------- standalone: additive inside the parenthesis, reachable at rank 0 ----------------

        [TestMethod]
        public void Standalone_GearTermIsAdditiveOnTheAbilityAxis()
        {
            // (1 + rank*perRank + rider + gear), NOT (1 + rank*perRank + rider) * (1 + gear).
            // At rank 3 with the 0.08 default and a 0.02 rider: 1 + 0.24 + 0.02 + 0.02 = 1.28.
            // A separate multiplier would give 1.26 * 1.02 = 1.2852, which this pins against.
            Assert.AreEqual(1.28, OverchannelAbility.DamageMultiplier(3, 0.08, 0.02, 0.02), Epsilon);
            Assert.AreEqual(1.26, OverchannelAbility.DamageMultiplier(3, 0.08, 0.02), Epsilon);

            Assert.AreEqual(1.26, VoidDamageAbility.DamageMultiplier(3, 0.08, 0.02), Epsilon);
            Assert.AreEqual(1.28, WitheringAbility.DotMultiplier(3, 0.08, 0.02, 0.02), Epsilon);
            Assert.AreEqual(1.14, EagleEyeAbility.AccuracyMultiplier(3, 0.04, 0.02), Epsilon);
            Assert.AreEqual(1.17, AttackSpeedAbility.AttackSpeedMultiplier(3, 0.05, 0.0, 0.02), Epsilon);
            Assert.AreEqual(1.35, EmpoweredSummonsAbility.StatMultiplier(3, 0.10, 0.0, 0.05), Epsilon);
        }

        [TestMethod]
        public void Standalone_DegeneratesToOnePlusGearAtRankZero()
        {
            // rank 0: the ability's per-rank base AND its skill rider are both zeroed, leaving the mod alone
            Assert.AreEqual(1.02, OverchannelAbility.DamageMultiplier(0, 0.08, 0.99, 0.02), Epsilon);
            Assert.AreEqual(1.02, VoidDamageAbility.DamageMultiplier(0, 0.08, 0.02), Epsilon);
            Assert.AreEqual(1.02, WitheringAbility.DotMultiplier(0, 0.08, 0.99, 0.02), Epsilon);
            Assert.AreEqual(1.02, EagleEyeAbility.AccuracyMultiplier(0, 0.04, 0.02), Epsilon);
            Assert.AreEqual(1.005, AttackSpeedAbility.AttackSpeedMultiplier(0, 0.05, 0.99, 0.005), Epsilon);
            Assert.AreEqual(1.05, EmpoweredSummonsAbility.StatMultiplier(0, 0.10, 0.99, 0.05), Epsilon);
        }

        [TestMethod]
        public void Standalone_WithoutGearIsBitIdenticalToTheOldBehavior()
        {
            // every gear parameter defaults to 0, so an unmodded player's numbers are untouched
            Assert.AreEqual(1.0f, OverchannelAbility.DamageMultiplier(0, 0.08, 0.02));
            Assert.AreEqual(1.0f, VoidDamageAbility.DamageMultiplier(0, 0.08));
            Assert.AreEqual(1.0f, WitheringAbility.DotMultiplier(0, 0.08, 0.02));
            Assert.AreEqual(1.0f, EagleEyeAbility.AccuracyMultiplier(0, 0.04));
            Assert.AreEqual(1.0f, AttackSpeedAbility.AttackSpeedMultiplier(0, 0.05, 0.02));
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.StatMultiplier(0, 0.10, 0.02));

            Assert.AreEqual(1.24f, VoidDamageAbility.DamageMultiplier(3, 0.08));
            Assert.AreEqual(1.15f, AttackSpeedAbility.AttackSpeedMultiplier(3, 0.05, 0.0));
        }

        [TestMethod]
        public void Standalone_NegativeGearCannotWeakenAnAbility()
        {
            // a contaminated or hand-edited value can only ever be ignored, never subtract
            Assert.AreEqual(1.24, VoidDamageAbility.DamageMultiplier(3, 0.08, -5.0), Epsilon);
            Assert.AreEqual(1.0, VoidDamageAbility.DamageMultiplier(0, 0.08, -5.0), Epsilon);
            Assert.AreEqual(1.0, EagleEyeAbility.AccuracyMultiplier(0, 0.04, -1.0), Epsilon);
        }

        // ---------------- Mana Barrier: gear share additive, capped WITH the ability's own share ----------

        [TestMethod]
        public void ManaBarrier_GearShareIsAdditiveAndCappedWithTheAbility()
        {
            // rank 3 at the 0.10 base/0.075 step defaults plus a 0.02 rider = 0.10 + 0.15 + 0.02 = 0.27,
            // +0.03 gear = 0.30, all well under the 0.60 cap
            Assert.AreEqual(0.30, ManaBarrierAbility.DivertShare(3, 0.10, 0.075, 0.02, 0.60, 0.03), Epsilon);

            // no mod = the original expression
            Assert.AreEqual(0.27, ManaBarrierAbility.DivertShare(3, 0.10, 0.075, 0.02, 0.60), Epsilon);
        }

        [TestMethod]
        public void ManaBarrier_ReachesRankZeroFromTheModAlone()
        {
            // unowned (rank 0): the ability contributes nothing, so the gear share stands alone
            Assert.AreEqual(0.03, ManaBarrierAbility.DivertShare(0, 0.10, 0.075, 0.0, 0.60, 0.03), Epsilon);

            // and nothing at all with neither ability nor mod
            Assert.AreEqual(0.0, ManaBarrierAbility.DivertShare(0, 0.10, 0.075, 0.0, 0.60), Epsilon);
        }

        [TestMethod]
        public void ManaBarrier_GearCannotPushSharePastTheAbilityCap()
        {
            // rank 3 already sits near the cap; a large gear roll cannot push the combined share past it
            Assert.AreEqual(0.60, ManaBarrierAbility.DivertShare(3, 0.10, 0.075, 0.02, 0.60, 0.99), Epsilon);

            // and the cap applies to the gear-alone (rank 0) case too
            Assert.AreEqual(0.60, ManaBarrierAbility.DivertShare(0, 0.10, 0.075, 0.0, 0.60, 0.99), Epsilon);
        }

        // ---------------- Soul Tether: gear reduction additive, capped WITH the ability's own reduction ----

        [TestMethod]
        public void SoulTether_GearReductionIsAdditiveAndCappedWithTheAbility()
        {
            // rank 3 at the 0.10 base/0.075 step defaults plus a 0.02 rider = 0.10 + 0.15 + 0.02 = 0.27,
            // +0.03 gear = 0.30 reduction -> a 0.70 damage multiplier, under the 0.50 reduction cap
            Assert.AreEqual(0.70f, SoulTetherAbility.DamageMultiplier(3, 0.10, 0.075, 0.02, 0.50, 0.03), 1e-6f);

            // no mod = the original expression
            Assert.AreEqual(0.73f, SoulTetherAbility.DamageMultiplier(3, 0.10, 0.075, 0.02, 0.50), 1e-6f);
        }

        [TestMethod]
        public void SoulTether_ReachesRankZeroFromTheModAlone()
        {
            // unowned (rank 0): the ability contributes nothing, so the gear reduction stands alone
            Assert.AreEqual(0.97f, SoulTetherAbility.DamageMultiplier(0, 0.10, 0.075, 0.0, 0.50, 0.03), 1e-6f);

            // and no reduction at all with neither ability nor mod
            Assert.AreEqual(1.0f, SoulTetherAbility.DamageMultiplier(0, 0.10, 0.075, 0.0, 0.50));
        }

        [TestMethod]
        public void SoulTether_GearCannotPushReductionPastTheAbilityCap()
        {
            // rank 3 already sits near the cap; a large gear roll cannot push the combined reduction past it
            Assert.AreEqual(0.50f, SoulTetherAbility.DamageMultiplier(3, 0.10, 0.075, 0.02, 0.50, 0.99), 1e-6f);

            // and the cap applies to the gear-alone (rank 0) case too
            Assert.AreEqual(0.50f, SoulTetherAbility.DamageMultiplier(0, 0.10, 0.075, 0.0, 0.50, 0.99), 1e-6f);
        }

        // ---------------- Nether Bloom: MACHINERY - the gear roll only ever adds ONE jump ------------------

        [TestMethod]
        public void NetherBloom_GearRollAddsExactlyOneJumpWhenItSucceeds()
        {
            // roll strictly under the gear probability -> succeeds, +1 jump
            Assert.AreEqual(3, NetherBloomAbility.ApplyGearBloomChance(2, 0.25, 0.10));

            // roll at or above the gear probability -> fails, jumps unchanged
            Assert.AreEqual(2, NetherBloomAbility.ApplyGearBloomChance(2, 0.25, 0.25));
            Assert.AreEqual(2, NetherBloomAbility.ApplyGearBloomChance(2, 0.25, 0.99));
        }

        [TestMethod]
        public void NetherBloom_IsInertAtGearValueZero()
        {
            // no mod equipped (or the value resolves to 0/negative) -> jumps are never touched, regardless
            // of the roll
            Assert.AreEqual(2, NetherBloomAbility.ApplyGearBloomChance(2, 0.0, 0.0));
            Assert.AreEqual(0, NetherBloomAbility.ApplyGearBloomChance(0, 0.0, 0.0));
            Assert.AreEqual(2, NetherBloomAbility.ApplyGearBloomChance(2, -0.5, 0.0));
        }

        [TestMethod]
        public void NetherBloom_IsMachineryAndDoesNothingWithoutTheBaseAbility()
        {
            // Nether Bloom is registered Standalone = false: OnCreatureKilled is an ICreatureDeathAbility
            // handler, and ClassAbilityRegistry only dispatches those to players who have actually learned
            // the ability (see ClassAbilityRegistry.CreatureDeathAbilities / Player's kill-dispatch loop) -
            // an unlearned player's kill never reaches this class at all, so the gear roll never runs.
            var definition = EquipmentModRegistry.Get(EquipmentModId.NetherBloom);

            Assert.IsFalse(definition.Standalone, "Nether Bloom must stay machinery-only - it is inert without the base ability");
            Assert.AreEqual(EquipmentModHookKind.AbilityMachinery, definition.HookKind);
        }

        // ---------------- Bulwark: summed with Battle Hardened, clamped together ----------------

        [TestMethod]
        public void Bulwark_IsSummedWithBattleHardenedBeforeTheSharedClamp()
        {
            // 100 Strength at the 0.0005 default = 5% reduction; +1% Bulwark = 6% total -> factor 0.94.
            // Two separate factors would give 0.95 * 0.99 = 0.9405, which this pins against.
            Assert.AreEqual(0.94f, BattleHardenedAbility.GetDamageResistMod(100, 0.0005, 0.5, 0.01), 1e-6f);

            // rank 0: the caller passes a per-Strength rate of 0, so the mod stands alone
            Assert.AreEqual(0.99f, BattleHardenedAbility.GetDamageResistMod(100, 0.0, 0.5, 0.01), 1e-6f);

            // no mod = the original expression
            Assert.AreEqual(0.95f, BattleHardenedAbility.GetDamageResistMod(100, 0.0005, 0.5), 1e-6f);
        }

        [TestMethod]
        public void Bulwark_CannotPushReductionPastTheAbilityCeiling()
        {
            // the pair is clamped TOGETHER: 2000 Strength alone already saturates the 50% ceiling, and the
            // mod cannot add on top of it
            Assert.AreEqual(0.5f, BattleHardenedAbility.GetDamageResistMod(2000, 0.0005, 0.5), 1e-6f);
            Assert.AreEqual(0.5f, BattleHardenedAbility.GetDamageResistMod(2000, 0.0005, 0.5, 0.01), 1e-6f);

            // and just under the ceiling, the mod fills the remaining headroom but no further
            Assert.AreEqual(0.5f, BattleHardenedAbility.GetDamageResistMod(990, 0.0005, 0.5, 0.05), 1e-6f);
        }

        // ---------------- Thorns: percent composition and the shield condition ----------------

        [TestMethod]
        public void Thorns_PercentIsAbilityPlusRiderPlusGear()
        {
            // 500 shield AL, rank 2 at 10%/rank, a 2% Shield-skill rider, and a 1% mod = 23% of 500 = 115
            Assert.AreEqual(115u, ThornsAbility.ComputeReflectDamage(500, 2, 0.10, 0.02, 0.01));

            // no mod = the original expression
            Assert.AreEqual(110u, ThornsAbility.ComputeReflectDamage(500, 2, 0.10, 0.02));
        }

        [TestMethod]
        public void Thorns_ReflectsAtRankZeroFromTheModAlone()
        {
            // rank 0 with the caller passing a zeroed Shield rider: 1% of 500 = 5
            Assert.AreEqual(5u, ThornsAbility.ComputeReflectDamage(500, 0, 0.10, 0.0, 0.01));

            // and nothing at all with neither ability nor mod
            Assert.AreEqual(0u, ThornsAbility.ComputeReflectDamage(500, 0, 0.10));

            // a brittlemail-style negative effective AL still cannot heal the attacker
            Assert.AreEqual(0u, ThornsAbility.ComputeReflectDamage(-500, 0, 0.10, 0.0, 0.01));
        }

        // ---------------- conditional standalone mods re-use the ability's own curve ----------------

        [TestMethod]
        public void LongDraw_ModRidesTheAbilitysDistanceRamp()
        {
            const double min = 15.0;
            const double max = 50.0;

            // the rank-0 path calls DistanceBonus(distance, rank: 1, peak: modValue, ...), so a 3% mod pays
            // its full 3% only at maximum range and nothing at all up close
            Assert.AreEqual(0.0, LongDrawAbility.DistanceBonus(10.0, 1, 0.03, min, max), Epsilon);
            Assert.AreEqual(0.0, LongDrawAbility.DistanceBonus(15.0, 1, 0.03, min, max), Epsilon);
            Assert.AreEqual(0.015, LongDrawAbility.DistanceBonus(32.5, 1, 0.03, min, max), Epsilon);
            Assert.AreEqual(0.03, LongDrawAbility.DistanceBonus(50.0, 1, 0.03, min, max), Epsilon);
            Assert.AreEqual(0.03, LongDrawAbility.DistanceBonus(500.0, 1, 0.03, min, max), Epsilon);

            // owned: the handler adds the mod to the SAME peak, so rank 2 (0.10/rank) plus a 0.03 mod peaks
            // at 0.23 - additive on one axis, not 1.20 * 1.03
            Assert.AreEqual(0.23, LongDrawAbility.DistanceBonus(50.0, 1, 2 * 0.10 + 0.03, min, max), Epsilon);
        }

        [TestMethod]
        public void BloodFury_ModRidesTheAbilitysHealthRamp()
        {
            const double start = 0.75;
            const double peak = 0.25;

            // the rank-0 path passes the mod value as peakBonus, so a 3% mod pays nothing at full health
            Assert.AreEqual(0.0, BloodFuryAbility.LowHealthBonus(1.00, 0.03, start, peak), Epsilon);
            Assert.AreEqual(0.0, BloodFuryAbility.LowHealthBonus(0.75, 0.03, start, peak), Epsilon);
            Assert.AreEqual(0.015, BloodFuryAbility.LowHealthBonus(0.50, 0.03, start, peak), Epsilon);
            Assert.AreEqual(0.03, BloodFuryAbility.LowHealthBonus(0.25, 0.03, start, peak), Epsilon);
            Assert.AreEqual(0.03, BloodFuryAbility.LowHealthBonus(0.01, 0.03, start, peak), Epsilon);

            // owned: rank 2 (0.10/rank) plus a 0.03 mod is one peak of 0.23
            Assert.AreEqual(0.23, BloodFuryAbility.LowHealthBonus(0.25, 2 * 0.10 + 0.03, start, peak), Epsilon);
        }

        [TestMethod]
        public void Venom_FlatBonusIsTheAbilityAloneAndTheModIsAddedAtApplication()
        {
            // FlatBonus carries NO gear term: it is also what Acid Proc derives its DoT tick from, so a
            // single Venom roll must not pay out into two separate damage streams
            Assert.AreEqual(0.0, PoisonWeaponAbility.FlatBonus(null, 0), Epsilon);
            Assert.AreEqual(15.0, PoisonWeaponAbility.FlatBonus(null, 3), Epsilon);

            // the gear term joins at the application point, additively, never multiplicatively
            Assert.AreEqual(17u, PoisonWeaponAbility.ComputePoisonDamage(PoisonWeaponAbility.FlatBonus(null, 3), 2.0));

            // rank 0: the mod value alone
            Assert.AreEqual(2u, PoisonWeaponAbility.ComputePoisonDamage(PoisonWeaponAbility.FlatBonus(null, 0), 2.0));
        }

        // ---------------- Phase 3.1: potency floors and half-up rounding for quantized effects ----------------

        [TestMethod]
        public void PoisonRounding_SumsFractionsThenRoundsHalfUpOnce()
        {
            // the ruling's worked examples (user 2026-07-25)
            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.5), "gear 0.5 alone rounds up to +1");
            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.4 + 0.4), "two 0.4 rolls sum to 0.8 and pay out as +1");
            Assert.AreEqual(15u, PoisonWeaponAbility.ComputePoisonDamage(15.0, 0.4), "15.4 rounds down to 15");
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.0, 0.8), "15.8 rounds up to 16");

            // fractions are additive BEFORE rounding: two 0.4 rolls must not each vanish
            Assert.AreEqual(0u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.4), "one 0.4 roll alone still rounds away");
            Assert.AreEqual(2u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.5 + 0.5 + 0.5 + 0.4), "1.9 rounds to 2");

            // exact midpoints round UP once a gear term is present
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.0, 0.5));
            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(0.5, 0.5));
        }

        [TestMethod]
        public void PoisonRounding_AbilityOnlyBehaviorIsUnchanged()
        {
            // with no gear the original expression runs untouched, including .NET's default rounding, so an
            // unmodded player's poison damage is bit-identical to before equipment mods existed
            Assert.AreEqual(15u, PoisonWeaponAbility.ComputePoisonDamage(15.0, 0.0));
            Assert.AreEqual(15u, PoisonWeaponAbility.ComputePoisonDamage(15.4, 0.0));
            Assert.AreEqual(16u, PoisonWeaponAbility.ComputePoisonDamage(15.6, 0.0));
            Assert.AreEqual(0u, PoisonWeaponAbility.ComputePoisonDamage(0.0, 0.0));

            // negative contamination on either side is ignored, never subtracted
            Assert.AreEqual(15u, PoisonWeaponAbility.ComputePoisonDamage(15.0, -5.0));
            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(-5.0, 0.5));
        }

        [TestMethod]
        public void VenomFloor_GuaranteesASingleRollIsWorthAtLeastOnePoint()
        {
            var venom = EquipmentModRegistry.Get(EquipmentModId.Venom);

            Assert.AreEqual(0.25, venom.MinPotency, Epsilon, "Venom's declared floor");

            // the floor's whole purpose: the weakest legal Venom still deals damage. At MaxMagnitude 4.1 the
            // declared floor (0.25) is now MORE generous than quantization strictly needs (0.5/4.1 = 0.122
            // would suffice), so the weakest legal roll clears the half-point threshold with margin.
            var weakest = EquipmentModValue.Resolve(venom, venom.MinPotency);
            Assert.AreEqual(1.025, weakest, Epsilon);
            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(0.0, weakest));

            // a potency well below what quantization needs is exactly the dead roll the floor exists to exclude
            Assert.AreEqual(0u, PoisonWeaponAbility.ComputePoisonDamage(0.0, EquipmentModValue.Resolve(venom, 0.05)));
        }

        [TestMethod]
        public void CausticFloor_GuaranteesANonzeroTickAtTheDesignAnchor()
        {
            var caustic = EquipmentModRegistry.Get(EquipmentModId.Caustic);

            Assert.AreEqual(0.25, caustic.MinPotency, Epsilon);

            var weakest = EquipmentModValue.Resolve(caustic, caustic.MinPotency);
            var strongest = EquipmentModValue.Resolve(caustic, 1.0);

            // the anchor the +15% was priced against: Poison Weapon rank 3, flat 15, tick fraction 1.0.
            // The floor exists so that even the weakest legal roll moves that tick by at least half a point.
            Assert.IsTrue(15.0 * weakest >= 0.5, $"the weakest legal Caustic ({weakest}) must move a 15-point tick by at least half a point");

            // DOCUMENTED RESIDUAL (see the PR body's quantization audit): the floor is derived from the
            // rank-3 base of 15 and does NOT cover lower Poison Weapon ranks, whose tick is smaller. At the
            // rank-1 base of 5 the weakest legal roll is still dead, and full coverage there would need a
            // floor of 0.5/5 / 0.15 = 0.667 - a far larger change to the roll distribution than this fix.
            Assert.IsTrue(5.0 * weakest < 0.5, "at the rank-1 base the floor is knowingly insufficient");
            Assert.IsTrue(5.0 * strongest >= 0.5, "a perfect Caustic does still register at the rank-1 base");
        }

        [TestMethod]
        public void ThornsFloor_GuaranteesANonzeroReflectAtTheDesignAnchor()
        {
            var thorns = EquipmentModRegistry.Get(EquipmentModId.Thorns);

            Assert.AreEqual(0.10, thorns.MinPotency, Epsilon);

            // the power-assessment endgame anchor: 550 effective shield armor level, ability rank 0 so the
            // mod is the whole reflect
            const double anchorShieldAl = 550.0;

            var weakest = EquipmentModValue.Resolve(thorns, thorns.MinPotency);
            Assert.IsTrue(ThornsAbility.ComputeReflectDamage(anchorShieldAl, 0, 0.10, 0.0, weakest) >= 1u,
                "the weakest legal Thorns must still reflect at least one point off an anchor shield");

            // the potency just below the floor is exactly the dead roll it excludes
            Assert.AreEqual(0u, ThornsAbility.ComputeReflectDamage(anchorShieldAl, 0, 0.10, 0.0, EquipmentModValue.Resolve(thorns, 0.05)));
        }

        [TestMethod]
        public void BloodlustAccumulation_PaysOutSubPointHealsInsteadOfLosingThem()
        {
            // 125 damage at a fraction of 0.0024 = 0.3 health per hit
            const double perHit = 0.3;

            // the premise: under per-hit rounding this mod pays literally nothing, ever
            Assert.AreEqual(0, (int)Math.Round(perHit), "a 0.3 heal rounds away on every single hit");

            var carry = 0.0;
            var heals = new List<int>();

            for (var hit = 0; hit < 6; hit++)
                heals.Add(BloodlustAbility.AccrueHeal(carry, perHit, out carry));

            // the running total is 0.3 / 0.6 / 0.9 / 1.2 / 1.5 / 1.8 and the payout tracks its nearest
            // integer. WHICH hit pays is deliberately not asserted: at an exact .5 boundary the answer turns
            // on accumulated floating-point error, so only the amounts are meaningful.
            Assert.AreEqual(2, heals.Sum(), "six hits at 0.3 must have paid out 2 points, not 0");

            // and the mod starts paying early rather than banking silently for a long time
            Assert.IsTrue(heals.Take(3).Sum() >= 1, "the first point should land within the first three hits");
            Assert.IsTrue(heals.All(h => h >= 0), "a hit can never remove health");
        }

        [TestMethod]
        public void BloodlustAccumulation_NeverDrifts()
        {
            // the invariant that makes accumulation safe: it changes WHEN the value arrives, never HOW MUCH.
            // After any number of hits the total healed stays within half a point of the exact total.
            foreach (var perHit in new[] { 0.05, 0.3, 0.499, 0.5, 0.7, 1.4, 3.25 })
            {
                var carry = 0.0;
                var paid = 0;

                for (var hit = 1; hit <= 500; hit++)
                {
                    paid += BloodlustAbility.AccrueHeal(carry, perHit, out carry);

                    var exact = hit * perHit;

                    Assert.IsTrue(Math.Abs(paid - exact) <= 0.5 + Epsilon,
                        $"at {perHit}/hit, after {hit} hits paid {paid} against an exact {exact} - drifted");
                    Assert.IsTrue(Math.Abs(carry) <= 0.5 + Epsilon, $"carry {carry} escaped [-0.5, 0.5]");
                }
            }
        }

        [TestMethod]
        public void BloodlustAccumulation_PaysImmediatelyWhenAHitAlreadyEarnsAPoint()
        {
            // a big hit is not delayed by the accumulator - it pays at once and carries the remainder
            var carry = 0.0;

            Assert.AreEqual(3, BloodlustAbility.AccrueHeal(carry, 3.4, out carry));
            Assert.AreEqual(0.4, carry, Epsilon);

            // and the carried 0.4 tips the next hit up
            Assert.AreEqual(4, BloodlustAbility.AccrueHeal(carry, 3.2, out carry));
            Assert.AreEqual(-0.4, carry, Epsilon, "paying slightly ahead is expected and self-correcting");
        }

        [TestMethod]
        public void BloodlustAccumulation_IgnoresNothingAndSanitizesGarbage()
        {
            var carry = 0.25;

            // a zero or negative contribution must not disturb the carry
            Assert.AreEqual(0, BloodlustAbility.AccrueHeal(carry, 0.0, out var after));
            Assert.AreEqual(0.25, after, Epsilon);

            Assert.AreEqual(0, BloodlustAbility.AccrueHeal(carry, -5.0, out after));
            Assert.AreEqual(0.25, after, Epsilon);

            // contaminated inputs sanitize rather than poisoning the accumulator forever
            Assert.AreEqual(0, BloodlustAbility.AccrueHeal(carry, double.NaN, out after));
            Assert.AreEqual(0.25, after, Epsilon);

            Assert.AreEqual(1, BloodlustAbility.AccrueHeal(double.NaN, 0.6, out after));
            Assert.AreEqual(-0.4, after, Epsilon);
        }

        [TestMethod]
        public void BloodlustAccumulation_StartsFromZeroSoAFreshPlayerCarriesNothing()
        {
            // the carry is an instance field on Player, so a rebuilt Player (logout/login) starts at 0 -
            // this pins the behavior a fresh accumulator must have. It SURVIVES a landblock transfer, which
            // moves the existing Player rather than rebuilding it.
            var carry = 0.0;

            Assert.AreEqual(0, BloodlustAbility.AccrueHeal(carry, 0.3, out carry), "a fresh carry earns nothing from one 0.3 hit");
            Assert.AreEqual(0.3, carry, Epsilon);
        }

        [TestMethod]
        public void RollPotency_NeverLandsBelowAModsFloor()
        {
            var venom = EquipmentModRegistry.Get(EquipmentModId.Venom);
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            var sawLow = false;
            var sawHigh = false;

            for (var i = 0; i < 20000; i++)
            {
                var floored = EquipmentModRoller.RollPotency(venom);
                Assert.IsTrue(floored >= venom.MinPotency - Epsilon && floored <= 1.0, $"floored roll {floored} escaped [{venom.MinPotency}, 1]");

                if (floored < venom.MinPotency + 0.05)
                    sawLow = true;
                if (floored > 0.95)
                    sawHigh = true;

                // a mod that declares no floor of its own is not unfloored - it rolls over
                // [DefaultMinPotency, 1]
                var open = EquipmentModRoller.RollPotency(deadeye);
                Assert.IsTrue(open >= EquipmentModRoller.DefaultMinPotency - Epsilon && open <= 1.0,
                    $"undeclared-floor roll {open} escaped [{EquipmentModRoller.DefaultMinPotency}, 1]");
            }

            // the floor rescales rather than truncates, so the whole [floor, 1] band stays reachable
            Assert.IsTrue(sawLow, "floored rolls never landed near the floor");
            Assert.IsTrue(sawHigh, "floored rolls never landed near the maximum");
        }

        [TestMethod]
        public void LowTierApply_TakesTheGreaterOfTheTunableAndTheFloor()
        {
            var venom = EquipmentModRegistry.Get(EquipmentModId.Venom);
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            // Venom's floor (0.25) beats the 0.2 tunable, so a TigerEye Venom is worth 1.025 flat, not 0.82
            Assert.AreEqual(0.25, EquipmentModRoller.LowTierPotency(venom), Epsilon);
            Assert.AreEqual(1.025, EquipmentModValue.Resolve(venom, EquipmentModRoller.LowTierPotency(venom)), Epsilon);
            Assert.AreEqual(1u, PoisonWeaponAbility.ComputePoisonDamage(0.0, EquipmentModValue.Resolve(venom, EquipmentModRoller.LowTierPotency(venom))));

            // a mod on the catalog default floor (0.10) is untouched: the 0.2 tunable is already above it, so
            // raising the default floor did not change any low-tier payout
            Assert.AreEqual(0.2, EquipmentModRoller.LowTierPotency(deadeye), Epsilon);
            Assert.IsTrue(EquipmentModRoller.DefaultMinPotency < PropertyManager.GetDouble("equipment_mod_lowtier_potency").Item,
                "a default floor above the low-tier tunable would silently buff every TigerEye application");
        }

        [TestMethod]
        public void MinPotencyIsSaneForEveryMod()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                Assert.IsTrue(mod.MinPotency >= 0.0 && mod.MinPotency < 1.0,
                    $"{mod.Id}: MinPotency {mod.MinPotency} must be in [0, 1) - a floor of 1 would make the mod deterministic");

                // a floor must never exceed what a perfect roll grants
                Assert.IsTrue(EquipmentModValue.Resolve(mod, mod.MinPotency) <= EquipmentModValue.Resolve(mod, 1.0) + Epsilon);
            }

            // Only the integer-quantized mods DECLARE a floor of their own (see the PR body's quantization
            // audit): a mod whose ENTIRE effect is rounded to an integer needs a floor high enough to survive
            // that rounding, which is well above the catalog default. Every other row takes the default -
            // being on a continuous pipeline buys a mod a LOWER floor, never no floor.
            var declared = EquipmentModRegistry.AllMods.Where(m => m.MinPotency > 0.0).Select(m => m.Id).ToList();

            CollectionAssert.AreEquivalent(new[] { EquipmentModId.Venom, EquipmentModId.Caustic, EquipmentModId.Thorns }, declared,
                "the set of mods needing a quantization floor above the catalog default changed - re-run the audit");

            // Bloodlust is integer-quantized too but cannot carry a quantization floor (it would have to be
            // ~0.8 at the damage anchor and would gut the roll gamble); accumulation across hits covers that
            // instead (Phase 3.2). It still takes the catalog default, which is what keeps its appraisal line
            // off "+0%" - the two mechanisms are orthogonal.
            Assert.AreEqual(0.0, EquipmentModRegistry.Get(EquipmentModId.Bloodlust).MinPotency, Epsilon,
                "Bloodlust must declare no quantization floor of its own - accumulation is what makes its low rolls pay out");

            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(EquipmentModRegistry.Get(EquipmentModId.Bloodlust)), Epsilon,
                "Bloodlust is not exempt from the catalog default floor");
        }

        [TestMethod]
        public void MinPotencyHelperSanitizesDegenerateFloors()
        {
            // an unset, zero or degenerate declaration falls back to the catalog default, NOT to 0 - there is
            // deliberately no value a registry row can write to opt out of the floor
            Assert.AreEqual(0.10, EquipmentModRoller.MinPotency(new EquipmentModDefinition()), Epsilon,
                "an undeclared floor resolves to the catalog default of 0.10");
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(new EquipmentModDefinition { MinPotency = 0.0 }), Epsilon);
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(new EquipmentModDefinition { MinPotency = -1.0 }), Epsilon);
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(new EquipmentModDefinition { MinPotency = double.NaN }), Epsilon);

            // a floor of 1 or more would collapse the roll range entirely, so it too falls back to the default
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(new EquipmentModDefinition { MinPotency = 1.0 }), Epsilon);
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(new EquipmentModDefinition { MinPotency = 5.0 }), Epsilon);

            // a declared floor above the default is honored as declared
            Assert.AreEqual(0.25, EquipmentModRoller.MinPotency(new EquipmentModDefinition { MinPotency = 0.25 }), Epsilon);

            // no definition at all is not a mod, and stays at 0
            Assert.AreEqual(0.0, EquipmentModRoller.MinPotency(null), Epsilon);
        }

        // ---------------- machinery: rank-gated, inert without the ability ----------------

        [TestMethod]
        public void Machinery_ContributesNothingAtRankZero()
        {
            // the defining machinery property: no ability, no effect, however large the mod
            Assert.AreEqual(0.0f, DoubleVolleyAbility.Chance(0, 0.06, 0.06, 0.99));
            Assert.AreEqual(0.0f, AcidProcAbility.Chance(0, 0.08, 0.06, 0.0, 0.99));
            Assert.AreEqual(0.0f, EchoCastAbility.EchoChance(0, 0.06, 0.06, 0.0, 0.99));
            Assert.AreEqual(0.0f, ElementalRendAbility.RendChance(0, 0.08, 0.06, 0.0, 0.99));
            Assert.AreEqual(0.0, ShieldCheckAbility.ReflectStrength(0, 0.40, 0.30, 0.99), Epsilon);
            Assert.AreEqual(0.0, RiposteAbility.CounterFraction(0, 0.40, 0.30, 0.99), Epsilon);
        }

        [TestMethod]
        public void Machinery_AddsPercentagePointsInsideTheAbilitysOwnRoll()
        {
            // rank 1 base 6% + a 1.5pp mod = 7.5%
            Assert.AreEqual(0.075f, DoubleVolleyAbility.Chance(1, 0.06, 0.06, 0.015), 1e-6f);
            // rank 3 = 6 + 2*6 = 18%, + 1.5pp = 19.5%
            Assert.AreEqual(0.195f, DoubleVolleyAbility.Chance(3, 0.06, 0.06, 0.015), 1e-6f);

            // acid proc: base 8% at rank 1, a 1% skill rider, and a 2pp mod = 11%
            Assert.AreEqual(0.11f, AcidProcAbility.Chance(1, 0.08, 0.06, 0.01, 0.02), 1e-6f);

            Assert.AreEqual(0.075f, EchoCastAbility.EchoChance(1, 0.06, 0.06, 0.0, 0.015), 1e-6f);
            Assert.AreEqual(0.10f, ElementalRendAbility.RendChance(1, 0.08, 0.06, 0.0, 0.02), 1e-6f);

            // riposte counter fraction: 40% at rank 1 + 10pp = 50%
            Assert.AreEqual(0.50, RiposteAbility.CounterFraction(1, 0.40, 0.30, 0.10), Epsilon);
        }

        [TestMethod]
        public void Machinery_WithoutGearIsBitIdenticalToTheOldBehavior()
        {
            Assert.AreEqual(0.06f, DoubleVolleyAbility.Chance(1, 0.06, 0.06));
            Assert.AreEqual(0.18f, DoubleVolleyAbility.Chance(3, 0.06, 0.06));
            Assert.AreEqual(0.08f, AcidProcAbility.Chance(1, 0.08, 0.06, 0.0));
            Assert.AreEqual(0.06f, EchoCastAbility.EchoChance(1, 0.06, 0.06, 0.0));
            Assert.AreEqual(0.08f, ElementalRendAbility.RendChance(1, 0.08, 0.06, 0.0));
            Assert.AreEqual(0.40, RiposteAbility.CounterFraction(1, 0.40, 0.30), Epsilon);
            Assert.AreEqual(1.00, ShieldCheckAbility.ReflectStrength(3, 0.40, 0.30), Epsilon);
        }

        [TestMethod]
        public void ShieldCheck_ModHonorsTheHundredPercentCeilingAndIsDeadAtMaxRank()
        {
            // rank 1: 40% + 10pp = 50%
            Assert.AreEqual(0.50, ShieldCheckAbility.ReflectStrength(1, 0.40, 0.30, 0.10), Epsilon);
            // rank 2: 70% + 10pp = 80%
            Assert.AreEqual(0.80, ShieldCheckAbility.ReflectStrength(2, 0.40, 0.30, 0.10), Epsilon);
            // rank 3 already converts at 100%, so the mod is dead weight - priced and accepted as trade fodder
            Assert.AreEqual(1.00, ShieldCheckAbility.ReflectStrength(3, 0.40, 0.30, 0.10), Epsilon);

            // an operator who tunes the ability itself above 1.0 is not retroactively clamped by a change
            // that only meant to bound the mod
            Assert.AreEqual(1.50, ShieldCheckAbility.ReflectStrength(1, 1.50, 0.30), Epsilon);
            Assert.AreEqual(1.50, ShieldCheckAbility.ReflectStrength(1, 1.50, 0.30, 0.10), Epsilon);
        }

        [TestMethod]
        public void FrenziedPace_ScalesWithStacksAndIsWorthlessWithoutThem()
        {
            // the mod adds to the PER-STACK rate: 0.05 + 0.0015 = 0.0515 per stack
            Assert.AreEqual(1.515f, FrenzyAbility.AttackSpeedMultiplier(10, 0.05 + 0.0015), 1e-5f);
            Assert.AreEqual(1.50f, FrenzyAbility.AttackSpeedMultiplier(10, 0.05), 1e-5f);

            // machinery in the plainest sense: no stacks, no benefit at all
            Assert.AreEqual(1.0f, FrenzyAbility.AttackSpeedMultiplier(0, 0.05 + 0.0015), 1e-6f);
        }

        // ---------------- registry-to-hook wiring ----------------

        [TestMethod]
        public void EveryMachineryModsLinkedAbilityIsRealAndEveryStandaloneHasAHookKind()
        {
            // the machinery gate keys off LinkedAbility, so a mod naming a retired or wrong ability would be
            // silently inert (machinery) or silently double-applied (standalone)
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                Assert.IsTrue(ACE.Server.ClassAbilities.ClassAbilityRegistry.Abilities.ContainsKey(mod.LinkedAbility),
                    $"{mod.Id}: LinkedAbility {mod.LinkedAbility} is not a registered class ability");

                if (mod.Standalone)
                    Assert.AreNotEqual(EquipmentModHookKind.AbilityMachinery, mod.HookKind, $"{mod.Id}: standalone mods need a rank-0-reachable hook kind");
                else
                    Assert.AreEqual(EquipmentModHookKind.AbilityMachinery, mod.HookKind, $"{mod.Id}: machinery mods must read inside their ability handler");
            }
        }

        [TestMethod]
        public void TheSevenRankZeroOutgoingModsAreExactlyTheOutgoingDamageStandalones()
        {
            // Player_EquipmentMods.ApplyEquipmentModOutgoingDamage is the rank-0 mirror for precisely the
            // standalone mods whose ability is an IOutgoingDamageAbility - every other standalone mod folds
            // into a getter the core site already calls and needs no mirror. If this set changes, that
            // method must change with it.
            var expected = new[]
            {
                EquipmentModId.Deadeye,
                EquipmentModId.LongDraw,
                EquipmentModId.SavageBlows,
                EquipmentModId.BloodFury,
                EquipmentModId.Executioner,
                EquipmentModId.Bloodlust,
                EquipmentModId.Venom,
            };

            var outgoingHandlerIds = ACE.Server.ClassAbilities.ClassAbilityRegistry.OutgoingDamageAbilities
                .Select(h => h.Definition.Id)
                .ToHashSet();

            var actual = EquipmentModRegistry.AllMods
                .Where(m => m.Standalone && outgoingHandlerIds.Contains(m.LinkedAbility))
                .Select(m => m.Id)
                .ToList();

            CollectionAssert.AreEquivalent(expected, actual,
                "the set of standalone mods needing a rank-0 outgoing-damage mirror changed");
        }

        [TestMethod]
        public void ExecuteRangeConditionMatchesTheAbility()
        {
            // the rank-0 Executioner path re-uses this exact predicate, so the mod applies in the same window
            // the ability does. A null or zero-max-health target is never in range.
            Assert.IsFalse(ExecutionerAbility.IsExecuteRange(null, 0.25));
        }
    }
}

using System;
using System.Collections.Generic;
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

        [TestMethod]
        public void NetherRush_ArcaneLoreRider_AddsToPerStackRate()
        {
            var perRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;

            // rider = 0 reproduces today's value exactly
            Assert.AreEqual(1.15f, NetherRushAbility.CastSpeedMultiplier(1, 3, perRank, 0.0), 1e-5f);

            // a +2% (0.02) Arcane Lore rider adds to the per-stack rate: (0.15 + 0.02) per stack
            Assert.AreEqual(1.17f, NetherRushAbility.CastSpeedMultiplier(1, 3, perRank, 0.02), 1e-5f);

            // still respects MaxStacks(5) with the rider applied
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
            // Parry 5/10/15% by rank (no rider)
            Assert.AreEqual(0.05, ParryAbility.ParryChance(1, 0.05, 0.0), 1e-9);
            Assert.AreEqual(0.15, ParryAbility.ParryChance(3, 0.05, 0.0), 1e-9);
            Assert.AreEqual(0.0, ParryAbility.ParryChance(0, 0.05, 0.5), 1e-9);

            // Shield Block 8/14/20% by rank (base 0.08 step 0.06)
            Assert.AreEqual(0.08, ShieldBlockAbility.BlockChance(1, 0.08, 0.06, 0.0), 1e-9);
            Assert.AreEqual(0.14, ShieldBlockAbility.BlockChance(2, 0.08, 0.06, 0.0), 1e-9);
            Assert.AreEqual(0.20, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, 0.0), 1e-9);

            // Shield Check + Riposte 40/70/100% by rank (base 0.4 step 0.3)
            Assert.AreEqual(0.40, ShieldCheckAbility.ReflectStrength(1, 0.40, 0.30), 1e-9);
            Assert.AreEqual(1.00, ShieldCheckAbility.ReflectStrength(3, 0.40, 0.30), 1e-9);
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
            Assert.AreEqual(0.05, perRank, 1e-9);

            // rank 3, no rider -> +15%
            Assert.AreEqual(1.15f, AttackSpeedAbility.AttackSpeedMultiplier(3, perRank, 0.0), 1e-5f);
            // rank 3 + a 0.06 (=+6%) Lockpick rider -> +21%
            Assert.AreEqual(1.21f, AttackSpeedAbility.AttackSpeedMultiplier(3, perRank, 0.06), 1e-5f);
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
            Assert.AreEqual(1.05f, FlatCastSpeedAbility.CastSpeedMultiplier(1, perRank), 1e-5f);
            Assert.AreEqual(1.15f, FlatCastSpeedAbility.CastSpeedMultiplier(3, perRank), 1e-5f);
            Assert.AreEqual(1.0f, FlatCastSpeedAbility.CastSpeedMultiplier(0, perRank), 1e-5f);
        }

        [TestMethod]
        public void Overchannel_WarDamageMultiplier_PerRankPlusArcaneLoreRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_overchannel_percent_per_rank").Item;
            Assert.AreEqual(0.08, perRank, 1e-9);

            // rank 3, no rider -> +24%
            Assert.AreEqual(1.24f, OverchannelAbility.DamageMultiplier(3, perRank, 0.0), 1e-5f);
            // rank 1 + a 0.06 (=+6%) Arcane Lore rider -> +14%
            Assert.AreEqual(1.14f, OverchannelAbility.DamageMultiplier(1, perRank, 0.06), 1e-5f);
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

        [TestMethod]
        public void SpellAoe_ManaConversionRider_DivisorsAreDoubledAgainstTheRankLadder()
        {
            var perTrained = PropertyManager.GetDouble("class_ability_spellaoe_manaconv_per_trained").Item;
            var perSpec = PropertyManager.GetDouble("class_ability_spellaoe_manaconv_per_spec").Item;

            // spec (user, 2026-08-26): +1% radiated damage per 40 Mana Conversion, per 30 if specialized
            Assert.AreEqual(40.0, perTrained, 1e-9);
            Assert.AreEqual(30.0, perSpec, 1e-9);

            // a specialized source is always the tighter (stronger) divisor
            Assert.IsTrue(perSpec < perTrained);

            // 400 effective Mana Conversion -> +10% trained, +13.3% specialized (on top of the rank base)
            Assert.AreEqual(0.10, ClassAbilityScaling.Compute(400, false, perTrained, perSpec) * 0.01, 1e-9);
            Assert.AreEqual(0.1333333, ClassAbilityScaling.Compute(400, true, perTrained, perSpec) * 0.01, 1e-6);
        }

        [TestMethod]
        public void EchoCast_ChancePerRank_PlusMagicItemTinkerRider()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;
            Assert.AreEqual(0.06, chanceBase, 1e-9);
            Assert.AreEqual(0.06, chanceStep, 1e-9);

            // ranks 1-3 with no rider -> 6/12/18%
            Assert.AreEqual(0.06f, EchoCastAbility.EchoChance(1, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.12f, EchoCastAbility.EchoChance(2, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.18f, EchoCastAbility.EchoChance(3, chanceBase, chanceStep, 0.0), 1e-5f);
            // rank 3 + a 0.05 (=+5%) tinker rider -> 23%
            Assert.AreEqual(0.23f, EchoCastAbility.EchoChance(3, chanceBase, chanceStep, 0.05), 1e-5f);
            // unlearned -> no chance
            Assert.AreEqual(0.0f, EchoCastAbility.EchoChance(0, chanceBase, chanceStep, 0.5), 1e-5f);
        }

        [TestMethod]
        public void ElementalRend_ChancePerRank_PlusLifeMagicRider()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_elementalrend_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_elementalrend_chance_step").Item;
            Assert.AreEqual(0.08, chanceBase, 1e-9);
            Assert.AreEqual(0.06, chanceStep, 1e-9);

            // ranks 1-3 with no rider -> 8/14/20%
            Assert.AreEqual(0.08f, ElementalRendAbility.RendChance(1, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.14f, ElementalRendAbility.RendChance(2, chanceBase, chanceStep, 0.0), 1e-5f);
            Assert.AreEqual(0.20f, ElementalRendAbility.RendChance(3, chanceBase, chanceStep, 0.0), 1e-5f);
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

        [TestMethod]
        public void EagleEye_AccuracyMultiplier_PerRank()
        {
            var perRank = PropertyManager.GetDouble("class_ability_eagleeye_percent_per_rank").Item;
            Assert.AreEqual(0.04, perRank, 1e-9);

            Assert.AreEqual(1.04f, EagleEyeAbility.AccuracyMultiplier(1, perRank), 1e-5f);
            Assert.AreEqual(1.08f, EagleEyeAbility.AccuracyMultiplier(2, perRank), 1e-5f);
            Assert.AreEqual(1.12f, EagleEyeAbility.AccuracyMultiplier(3, perRank), 1e-5f);
            Assert.AreEqual(1.0f, EagleEyeAbility.AccuracyMultiplier(0, perRank), 1e-5f);
        }

        [TestMethod]
        public void DoubleVolley_ChancePerRank_IsSixTwelveEighteen()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item;
            Assert.AreEqual(0.06, chanceBase, 1e-9);
            Assert.AreEqual(0.06, chanceStep, 1e-9);

            Assert.AreEqual(0.06f, DoubleVolleyAbility.Chance(1, chanceBase, chanceStep), 1e-5f);
            Assert.AreEqual(0.12f, DoubleVolleyAbility.Chance(2, chanceBase, chanceStep), 1e-5f);
            Assert.AreEqual(0.18f, DoubleVolleyAbility.Chance(3, chanceBase, chanceStep), 1e-5f);
            Assert.AreEqual(0.0f, DoubleVolleyAbility.Chance(0, chanceBase, chanceStep), 1e-5f);
        }

        [TestMethod]
        public void Withering_VoidDotMultiplier_PerRankPlusCreatureEnchantRider()
        {
            var perRank = PropertyManager.GetDouble("class_ability_withering_percent_per_rank").Item;
            Assert.AreEqual(0.08, perRank, 1e-9);

            Assert.AreEqual(1.24f, WitheringAbility.DotMultiplier(3, perRank, 0.0), 1e-5f);
            Assert.AreEqual(1.14f, WitheringAbility.DotMultiplier(1, perRank, 0.06), 1e-5f);
            Assert.AreEqual(1.0f, WitheringAbility.DotMultiplier(0, perRank, 0.5), 1e-5f);
        }

        [TestMethod]
        public void HeavyDraw_RunRider_ReducesStaminaSurcharge_ClampedToCap()
        {
            var baseCost = (int)PropertyManager.GetDouble("class_ability_heavydraw_stamina_cost").Item;
            var cap = PropertyManager.GetDouble("class_ability_heavydraw_run_reduction_cap").Item;
            Assert.AreEqual(10, baseCost);
            Assert.AreEqual(0.50, cap, 1e-9);

            // rider = 0 reproduces today's cost exactly
            Assert.AreEqual(10, HeavyDrawAbility.EffectiveStaminaCost(baseCost, 0.0, cap));

            // a 20% reduction knocks 10 stamina down to 8
            Assert.AreEqual(8, HeavyDrawAbility.EffectiveStaminaCost(baseCost, 0.20, cap));

            // clamps at the 50% cap: a 90% reduction still only reduces to 5, never below the floor
            Assert.AreEqual(5, HeavyDrawAbility.EffectiveStaminaCost(baseCost, 0.90, cap));
        }

        [TestMethod]
        public void Taunt_LoyaltyRider_ExtendsDuration_ClampedToCap()
        {
            var baseDuration = PropertyManager.GetDouble("class_ability_taunt_duration").Item;
            var cap = PropertyManager.GetDouble("class_ability_taunt_loyalty_bonus_cap_seconds").Item;
            Assert.AreEqual(10.0, baseDuration, 1e-9);
            Assert.AreEqual(10.0, cap, 1e-9);

            // rider = 0 reproduces today's duration exactly
            Assert.AreEqual(10.0, TauntAbility.EffectiveDuration(baseDuration, 0.0, cap), 1e-9);

            // a +4 second rider extends the hold duration
            Assert.AreEqual(14.0, TauntAbility.EffectiveDuration(baseDuration, 4.0, cap), 1e-9);

            // clamps at the cap: a +25 second rider still only adds 10 seconds, never past the cap
            Assert.AreEqual(20.0, TauntAbility.EffectiveDuration(baseDuration, 25.0, cap), 1e-9);
        }

        /// <summary>
        /// REWORKED 2026-08-17 (Berserker/Rogue balance pass): the chance is now flat and rank-invariant,
        /// with no affinity rider at all - rank instead raises PoisonDamageBonus (see the tests below).
        /// </summary>
        [TestMethod]
        public void AcidProc_Chance_FlatAndRankInvariant()
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_acidproc_chance_base").Item;
            Assert.AreEqual(0.25, chanceBase, 1e-9);

            // ranks 1-3 are all the same flat 25% - rank does not raise the chance any more
            Assert.AreEqual(0.25f, AcidProcAbility.Chance(1, chanceBase), 1e-5f);
            Assert.AreEqual(0.25f, AcidProcAbility.Chance(2, chanceBase), 1e-5f);
            Assert.AreEqual(0.25f, AcidProcAbility.Chance(3, chanceBase), 1e-5f);

            // unlearned -> no chance
            Assert.AreEqual(0.0f, AcidProcAbility.Chance(0, chanceBase), 1e-5f);

            // the equipment-mod term still rides on top (EquipmentModId.AcidProc is unchanged)
            Assert.AreEqual(0.30f, AcidProcAbility.Chance(2, chanceBase, 0.05), 1e-5f);
        }

        /// <summary>
        /// REGRESSION, live bug 2026-08-04, now exercised against PoisonDamageBonus rather than Chance since
        /// the 2026-08-17 rework moved the Item Tinkering affinity there. GetClassAbilityScaling returns a
        /// raw quotient (skill / divisor) with no bound of its own, so an uncapped rider is linear in a skill
        /// value the server does not constrain.
        /// </summary>
        [TestMethod]
        public void AcidProc_PoisonDamageBonus_PerRankPlusClampedItemTinkerRider()
        {
            // ranks 0-3 with no rider -> 0/25/50/75%
            Assert.AreEqual(0.0, AcidProcAbility.PoisonDamageBonus(0, 0.25, 0.25, 0.0), 1e-9);
            Assert.AreEqual(0.25, AcidProcAbility.PoisonDamageBonus(1, 0.25, 0.25, 0.0), 1e-9);
            Assert.AreEqual(0.50, AcidProcAbility.PoisonDamageBonus(2, 0.25, 0.25, 0.0), 1e-9);
            Assert.AreEqual(0.75, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, 0.0), 1e-9);

            // control: an absurd rider (mirrors the live 2026-08-04 measurement), uncapped
            Assert.AreEqual(2.09 + 0.75, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, 2.09), 1e-9);

            // capped at 0.20: base+step 0.75 + capped rider 0.20 = 0.95
            Assert.AreEqual(0.95, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, 2.09, 0.20), 1e-9);

            // a legitimate rider under the cap is unchanged by it
            Assert.AreEqual(0.91, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, 0.16, 0.20), 1e-9);

            // cap 0 means uncapped
            Assert.AreEqual(2.84, AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, 2.09, 0.0), 1e-9);
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

            // ranks 1-3 are all the same flat 15% - rank buys the Imperil rung, not the proc odds
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(1, chanceBase, 0.0), 1e-5f);
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(2, chanceBase, 0.0), 1e-5f);
            Assert.AreEqual(0.15f, BreakArmorAbility.Chance(3, chanceBase, 0.0), 1e-5f);

            // unlearned -> no chance
            Assert.AreEqual(0.0f, BreakArmorAbility.Chance(0, chanceBase, 0.0), 1e-5f);

            // a Weapon Tinkering rider adds on top
            Assert.AreEqual(0.20f, BreakArmorAbility.Chance(1, chanceBase, 0.05), 1e-5f);
        }

        [TestMethod]
        public void BreakArmorChance_AffinityCap_BoundsAnAbsurdRider_ButNotTheGearMod()
        {
            // control: an absurd rider, uncapped, saturates the proc to certainty
            Assert.IsTrue(BreakArmorAbility.Chance(1, 0.15, 2.09) >= 1.0f);

            // capped at 0.20: base 0.15 + capped rider 0.20 = 0.35
            Assert.AreEqual(0.35f, BreakArmorAbility.Chance(1, 0.15, 2.09, 0.20), 1e-5f);

            // the gear mod rides on top of the cap rather than inside it
            Assert.AreEqual(0.40f, BreakArmorAbility.Chance(1, 0.15, 2.09, 0.20, 0.05), 1e-5f);

            // cap 0 means uncapped
            Assert.AreEqual(2.24f, BreakArmorAbility.Chance(1, 0.15, 2.09, 0.0), 1e-5f);
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
            Assert.AreEqual(0.10, perRank, 1e-9);

            // rank 3, no rider -> +30%
            Assert.AreEqual(1.30f, EmpoweredSummonsAbility.StatMultiplier(3, perRank, 0.0), 1e-5f);
            // rank 1 + a 0.05 (=+5%) Leadership rider -> +15%
            Assert.AreEqual(1.15f, EmpoweredSummonsAbility.StatMultiplier(1, perRank, 0.05), 1e-5f);
            // unlearned -> no bonus
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.StatMultiplier(0, perRank, 0.5), 1e-5f);
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
        public void SpellAoe_IsAThreeRankTierOneGameChanger_CostingOneTwoThree()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.SpellAoe);

            Assert.AreEqual(1, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, def.CostPerRank,
                "Spell AOE is on the standard tier-1 game-changer 1/2/3 cost scale");
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
    }
}

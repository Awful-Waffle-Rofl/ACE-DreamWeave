using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T2 (2026-09-29 rework, ability id 24 redefined in place - was Eagle Eye): lifts the LOW end of
    /// the archer's missile accuracy range without touching the high end. Player.GetAccuracyMod returns
    /// AccuracyLevel + 0.6 for a ranged weapon today (AccuracyLevel is the client's 0.0-1.0 missile slider),
    /// so the multiplier on effective attack skill runs 0.6 (fastest fire) to 1.6 (most precise aim). Fast
    /// Aim raises the 0.6 floor toward the 1.6 ceiling by <c>fastAimBonus</c>, linear in between:
    ///
    ///   low         = 0.6 + fastAimBonus
    ///   accuracyMod = low + AccuracyLevel * (1.6 - low)
    ///
    /// At AccuracyLevel == 1.0 this is always 1.6 regardless of fastAimBonus - the high end never moves. At
    /// AccuracyLevel == 0.0 it is exactly `low`. The whole effect lives in Player.GetAccuracyMod (see that
    /// method's own comment); GetEffectiveAttackSkill no longer applies a separate multiplier, so the
    /// ability is applied in exactly one place.
    ///
    /// fastAimBonus MUST be clamped to [0.0, 1.0]. Above 1.0, low would exceed 1.6 and the curve INVERTS -
    /// fast, sloppy fire would out-accuracy careful aim, which is the opposite of what a Fast Aim archer
    /// should feel like. See LowEndBonus below.
    /// </summary>
    public class FastAimAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EagleEye,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 2,
            Name = "fastaim",
            DisplayName = "Fast Aim",
            Description = "Raises the low end of your missile accuracy range by 10/20/30 percentage " +
                          "points (by rank), narrowing the gap between snap shots and careful aim without " +
                          "changing your accuracy at full precision. Higher Run multiplies the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Run,
        };

        /// <summary>
        /// The low-end lift (0.0-1.0, always clamped): the ability's own rank bonus (rank * perRank),
        /// MULTIPLIED by the Run affinity factor, PLUS the equipment-mod term, with the SUM clamped to
        /// [0.0, 1.0]. Pure for testability.
        ///
        /// <paramref name="affinityMultiplier"/> is what Player.GetClassAbilityAffinityMultiplier(Skill.Run)
        /// returns: a factor &gt;= 1.0, exactly 1.0 at zero effective Run, which is why the default here is
        /// 1.0 (neutral) rather than 0.0 - a caller that omits it must reproduce the untrained case exactly.
        ///
        /// <paramref name="gearModFraction"/> is the Fast Aim equipment mod (repurposed EagleEye gear mod,
        /// same PropertyFloat/EquipmentModId - see EquipmentModRegistry), a STANDALONE mod: added OUTSIDE
        /// the affinity multiply, reachable at rank 0. Defaults to 0.
        ///
        /// UNCAPPED AFFINITY, CAPPED RESULT. There is no separate ceiling on the affinity term the way
        /// PinningShotAbility clamps its "added" amount - here the ENTIRE bonus (rank term * affinity, plus
        /// gear) is clamped to 1.0 together, because that is the value that must never let `low` exceed
        /// `1.6` (see the type doc). A huge affinity multiplier is thus contained by the same clamp that
        /// bounds gear, not a second one.
        /// </summary>
        public static float LowEndBonus(int rank, double perRank, double affinityMultiplier = 1.0, double gearModFraction = 0.0)
        {
            var rankBonus = rank <= 0 ? 0.0 : rank * perRank;
            var multiplier = Math.Max(0.0, affinityMultiplier);
            var gearBonus = Math.Max(0.0, gearModFraction);

            var bonus = rankBonus * multiplier + gearBonus;

            return (float)Math.Max(0.0, Math.Min(1.0, bonus));
        }

        /// <summary>
        /// Mirrors the terms Player.GetFastAimLowEndBonus (Player_ClassAbilityBuffs.cs) feeds into
        /// LowEndBonus above - the two must stay in step. Reports the CLAMPED affinity contribution, not the
        /// raw one: Skill + Affinity + Gear must sum to Effective exactly even when the overall [0,1] clamp
        /// bites, so any amount the clamp removes is taken out of Affinity (the rider), never out of Skill
        /// or Gear.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_fastaim_low_end_per_rank").Item;
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.Run) ?? 1.0;
            var gear = Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.EagleEye) ?? 0.0);

            var rankBonus = rank <= 0 ? 0.0 : rank * perRank;
            var rawTotal = rankBonus * Math.Max(0.0, multiplier) + gear;
            var clampedTotal = Math.Max(0.0, Math.Min(1.0, rawTotal));

            // the clamp is on the SUM, not on any one term - so any amount it removes comes out of the
            // affinity rider, the same way a capped "added" amount does on the other affinity abilities
            var affinity = clampedTotal - rankBonus - gear;

            var skill = rankBonus * 100.0;
            var affinityPct = affinity * 100.0;
            var gearPct = gear * 100.0;
            var effective = clampedTotal * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinityPct,
                Gear = gearPct,
                Effective = effective,
                Unit = "pp",
                Label = "accuracy low-end lift",
                Per = null,
                CapNote = rawTotal > 1.0 ? "low-end clamp" : null,
            };
        }
    }
}

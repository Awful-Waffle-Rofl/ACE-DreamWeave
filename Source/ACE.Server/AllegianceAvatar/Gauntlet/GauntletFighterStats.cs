using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// What one stamp writes on the Avatar fighter: the run-start snapshot (the fighter's power never changes after it). Built by <see cref="GauntletFighterStats.Compute"/>; applied by
/// GauntletAvatarFighter.ApplyStamp. A skill or attribute target of 0 means "leave it authored", the normalizer's own
/// convention (an empty band standard, or no median for that axis).
/// </summary>
public sealed record GauntletFighterStamp
{
    /// <summary>The level shown: the Avatar's own level A (never the wave level). Compute leaves the wave's level here; the live hooks overwrite it with A.</summary>
    public int Level { get; init; }

    /// <summary>The Avatar level A the stamp was computed with.</summary>
    public int AvatarLevel { get; init; }

    public uint MaxHealth { get; init; }

    /// <summary>fighter_offense_base + fighter_offense_per_level x A: the ratio every skill and attribute target uses.</summary>
    public double OffenseRatio { get; init; }

    /// <summary>fighter_damage_base + fighter_damage_per_level x A.</summary>
    public double DamageRatio { get; init; }

    /// <summary>The maximum hit its body parts and weapons are set to (0: leave authored).</summary>
    public uint DamageTarget { get; init; }

    /// <summary>
    /// The body-part base armor its parts are set to: the start plan's band standard MaxBaseArmor at
    /// <see cref="GauntletFighterStats.ArmorRatio"/> (owner ruling 2026-11-25, real damage with band armor). 0: leave authored.
    /// </summary>
    public uint ArmorTarget { get; init; }

    public IReadOnlyDictionary<Skill, uint> Skills { get; init; } = new Dictionary<Skill, uint>();

    public IReadOnlyDictionary<PropertyAttribute, uint> Attributes { get; init; } = new Dictionary<PropertyAttribute, uint>();

    /// <summary>The damage intake cap: max health over this many seconds at the fastest (fighter_min_death_seconds).</summary>
    public double MinDeathSeconds { get; init; }

    /// <summary>Seconds of the cap's rate the bucket holds as burst (fighter_burst_seconds).</summary>
    public double BurstSeconds { get; init; }
}

/// <summary>
/// The Avatar fighter's power (pure; spec "The Avatar fights", Power), computed ONCE at the run's start from wave 1's plan and
/// the Avatar's level A at that moment (never recomputed by later waves). Every number comes from that plan and A, through <see cref="GauntletTuning"/>:
/// <list type="bullet">
/// <item>max health = plan.HealthCurveTarget x (fighter_health_base + fighter_health_per_level x A), held to
/// allegiance_gauntlet_max_creature_health and to [1, int.MaxValue];</item>
/// <item>attack, defense and magic skills, and the six attributes, are the band standard's targets
/// (DungeonCombatNormalizer.AttackSkillTarget, DefenseSkillTarget, AttributeTarget) at the offense ratio;</item>
/// <item>the maximum hit is plan.PerHitMedian x the damage ratio (the band standard's MaxBodyDamage when the plan has no
/// per-hit median, which is the damage ceiling switched off).</item>
/// <item>the body-part base armor is the band standard's MaxBaseArmor at <see cref="ArmorRatio"/> (1.0), the
/// SetBossBodyParts armor axis, so the fighter takes real hits as a monster of the start wave's band would.</item>
/// </list>
/// The current health fraction is kept across a re-apply (<see cref="KeptHealth"/>).
/// </summary>
public static class GauntletFighterStats
{
    public static double OffenseRatio(int avatarLevel, GauntletTuning t) =>
        t.FighterOffenseBase + t.FighterOffensePerLevel * Math.Max(0, avatarLevel);

    public static double DamageRatio(int avatarLevel, GauntletTuning t) =>
        t.FighterDamageBase + t.FighterDamagePerLevel * Math.Max(0, avatarLevel);

    public static double HealthFactor(int avatarLevel, GauntletTuning t) =>
        t.FighterHealthBase + t.FighterHealthPerLevel * Math.Max(0, avatarLevel);

    /// <summary>The band armor ratio (owner ruling 2026-11-25: ratio 1.0, the band's own armor). Not a tunable.</summary>
    public const double ArmorRatio = 1.0;

    /// <summary>The body-part base armor: the standard's MaxBaseArmor x <see cref="ArmorRatio"/>; 0 (leave authored) for no or an empty standard.</summary>
    public static uint ArmorTarget(DungeonBandStandard standard)
    {
        if (standard == null || standard.IsEmpty || standard.MaxBaseArmor == 0)
            return 0;

        return (uint)Math.Clamp(Math.Round(standard.MaxBaseArmor * ArmorRatio, MidpointRounding.AwayFromZero), 1.0, int.MaxValue);
    }

    /// <summary>plan.HealthCurveTarget x the health factor, clamped. 0 when the curve gave no target (leave authored).</summary>
    public static uint MaxHealth(double healthCurveTarget, int avatarLevel, GauntletTuning t)
    {
        if (!double.IsFinite(healthCurveTarget) || !(healthCurveTarget > 0))
            return 0;

        var factor = HealthFactor(avatarLevel, t);

        if (!double.IsFinite(factor) || !(factor > 0))
            return 0;

        var cap = Math.Clamp(t.MaxCreatureHealth, 1L, int.MaxValue);
        var raw = Math.Round(healthCurveTarget * factor, MidpointRounding.AwayFromZero);

        return (uint)Math.Clamp(raw, 1.0, cap);
    }

    /// <summary>The maximum hit: the per-hit median (else the standard's body maximum) x the damage ratio; 0 leaves it authored.</summary>
    public static uint DamageTarget(double perHitMedian, DungeonBandStandard standard, int avatarLevel, GauntletTuning t)
    {
        var basis = double.IsFinite(perHitMedian) && perHitMedian > 0 ? perHitMedian : (double)(standard?.MaxBodyDamage ?? 0);
        var ratio = DamageRatio(avatarLevel, t);

        if (!(basis > 0) || !double.IsFinite(ratio) || !(ratio > 0))
            return 0;

        return (uint)Math.Clamp(Math.Round(basis * ratio, MidpointRounding.AwayFromZero), 1.0, int.MaxValue);
    }

    /// <summary>The stamp for one plan (the run-start snapshot, from wave 1's). <paramref name="plan"/> null (a refused wave) gives null: the fighter keeps its stats.</summary>
    internal static GauntletFighterStamp Compute(GauntletWavePlan plan, int avatarLevel, GauntletTuning t)
    {
        if (plan == null)
            return null;

        return Compute(plan.Level, plan.Plan?.HealthCurveTarget ?? 0, plan.Plan?.BandStandard, plan.Plan?.PerHitMedian ?? 0, avatarLevel, t);
    }

    /// <summary>The stamp from its inputs (the plan's level, curve target, band standard and per-hit median, and A).</summary>
    public static GauntletFighterStamp Compute(int level, double healthCurveTarget, DungeonBandStandard standard, double perHitMedian, int avatarLevel, GauntletTuning t)
    {
        t ??= GauntletTuning.Defaults;

        var offense = OffenseRatio(avatarLevel, t);
        var skills = new Dictionary<Skill, uint>();
        var attributes = new Dictionary<PropertyAttribute, uint>();

        if (standard != null && !standard.IsEmpty)
        {
            foreach (var skill in DungeonCombatNormalizer.AttackSkills)
            {
                var v = DungeonCombatNormalizer.AttackSkillTarget(standard, skill, offense);
                if (v > 0)
                    skills[skill] = v;
            }

            foreach (var skill in DungeonCombatNormalizer.DefenseSkills)
            {
                var v = DungeonCombatNormalizer.DefenseSkillTarget(standard, skill, offense);
                if (v > 0)
                    skills[skill] = v;
            }

            foreach (var attribute in DungeonCombatNormalizer.OffenseAttributes)
            {
                var v = DungeonCombatNormalizer.AttributeTarget(standard, attribute, offense, offense);
                if (v > 0)
                    attributes[attribute] = v;
            }

            foreach (var attribute in DungeonCombatNormalizer.DefenseAttributes)
            {
                var v = DungeonCombatNormalizer.AttributeTarget(standard, attribute, offense, offense);
                if (v > 0)
                    attributes[attribute] = v;
            }
        }

        return new GauntletFighterStamp
        {
            Level = Math.Max(1, level),
            AvatarLevel = Math.Max(0, avatarLevel),
            MaxHealth = MaxHealth(healthCurveTarget, avatarLevel, t),
            OffenseRatio = offense,
            DamageRatio = DamageRatio(avatarLevel, t),
            DamageTarget = DamageTarget(perHitMedian, standard, avatarLevel, t),
            ArmorTarget = ArmorTarget(standard),
            Skills = skills,
            Attributes = attributes,
            MinDeathSeconds = t.FighterMinDeathSeconds,
            BurstSeconds = t.FighterBurstSeconds,
        };
    }

    /// <summary>
    /// The current health after a restamp: the same fraction of the new maximum, never 0 for a living fighter (a fraction
    /// above 0 keeps at least 1), and full when the old maximum is unknown.
    /// </summary>
    public static uint KeptHealth(uint current, uint oldMax, uint newMax)
    {
        if (newMax == 0)
            return 0;

        if (oldMax == 0)
            return newMax;

        var fraction = Math.Clamp(current / (double)oldMax, 0.0, 1.0);

        if (fraction <= 0)
            return 0;

        return (uint)Math.Clamp(Math.Round(fraction * newMax, MidpointRounding.AwayFromZero), 1.0, newMax);
    }
}

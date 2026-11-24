using System;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// Every Allegiance Gauntlet tunable as one immutable value. The pure rule classes take this value instead of reading
/// PropertyManager, so they stay testable without seeding statics. Property names are the PascalCase of the
/// allegiance_gauntlet_ key minus the prefix. Defaults must equal the PropertyManager default dictionaries.
/// The daily account crate cap is a table (allegiance_gauntlet_claim), not a tunable.
/// </summary>
public sealed record GauntletTuning
{
    public bool Enabled { get; init; }
    public long Price { get; init; }
    public long MinAvatarLevel { get; init; }
    public long CooldownHours { get; init; }
    public long MaxConcurrent { get; init; }
    public long StagingSeconds { get; init; }
    public long IntermissionSeconds { get; init; }
    public long StartLevel { get; init; }
    public long LevelStep { get; init; }
    public long BossEvery { get; init; }
    public long WaveSeconds { get; init; }
    public long BossWaveBonusSeconds { get; init; }
    public long WaveBaseCount { get; init; }
    public long MaxAlive { get; init; }
    public long HeadcountCap { get; init; }
    public long PortalsMin { get; init; }
    public long PortalsMax { get; init; }
    public long StragglerCount { get; init; }
    public long StragglerSeconds { get; init; }
    public long HealthCurveTopLevel { get; init; }
    public long MaxCreatureHealth { get; init; }
    public long DeathHoldSeconds { get; init; }
    public long FighterRespawnSeconds { get; init; }
    public long MinContactWaves { get; init; }
    public long CrateMaxTier { get; init; }
    public double WaveCountPerPlayer { get; init; }
    public double BossWaveTrashFraction { get; init; }
    public double PortalMinDistance { get; init; }
    public double PortalMaxDistance { get; init; }
    public double StaggerMinSeconds { get; init; }
    public double StaggerMaxSeconds { get; init; }
    public double KillXpScale { get; init; }
    public double PenMinDistance { get; init; }
    public double FighterHealthBase { get; init; }
    public double FighterHealthPerLevel { get; init; }
    public double FighterOffenseBase { get; init; }
    public double FighterOffensePerLevel { get; init; }
    public double FighterDamageBase { get; init; }
    public double FighterDamagePerLevel { get; init; }
    public double FighterMinDeathSeconds { get; init; }
    public double FighterBurstSeconds { get; init; }
    public string Sites { get; init; } = "";

    /// <summary>The highest crate tier: rewards.json ships gauntlet_t0 to gauntlet_t6 (Task 12).</summary>
    public const int CrateMaxTierCeiling = 6;

    /// <summary>The most trash a wave may ask for. GroupScaling caps live monsters at 180, so this is only a guard against garbage.</summary>
    public const long MaxAliveCeiling = 10000;

    /// <summary>The shipped defaults. Must equal the PropertyManager default dictionaries.</summary>
    public static GauntletTuning Defaults { get; } = new()
    {
        Enabled = true,
        Price = 3000,
        MinAvatarLevel = 75,
        CooldownHours = 24,
        MaxConcurrent = 2,
        StagingSeconds = 120,
        IntermissionSeconds = 20,
        StartLevel = 200,
        LevelStep = 25,
        BossEvery = 5,
        WaveSeconds = 240,
        BossWaveBonusSeconds = 120,
        WaveBaseCount = 8,
        MaxAlive = 120,
        HeadcountCap = 100,
        PortalsMin = 3,
        PortalsMax = 5,
        StragglerCount = 3,
        StragglerSeconds = 45,
        HealthCurveTopLevel = 1000,
        MaxCreatureHealth = 2000000000,
        DeathHoldSeconds = 60,
        FighterRespawnSeconds = 60,
        MinContactWaves = 3,
        CrateMaxTier = 6,
        WaveCountPerPlayer = 2.0,
        BossWaveTrashFraction = 0.5,
        PortalMinDistance = 20.0,
        PortalMaxDistance = 55.0,
        StaggerMinSeconds = 0.5,
        StaggerMaxSeconds = 2.0,
        KillXpScale = 1.0,
        PenMinDistance = 100.0,
        FighterHealthBase = 10.0,
        FighterHealthPerLevel = 0.04,
        FighterOffenseBase = 1.0,
        FighterOffensePerLevel = 0.0004,
        FighterDamageBase = 1.0,
        FighterDamagePerLevel = 0.004,
        FighterMinDeathSeconds = 20.0,
        FighterBurstSeconds = 5.0,
        Sites = "",
    };

    /// <summary>
    /// Reads the live values through PropertyManager (which must be initialized; defaults are seeded into the cache at boot)
    /// and sanitizes them, see <see cref="Sanitized"/>.
    /// </summary>
    public static GauntletTuning FromPropertyManager() => Sanitized(new()
    {
        Enabled = PropertyManager.GetBool("allegiance_gauntlet_enabled").Item,
        Price = PropertyManager.GetLong("allegiance_gauntlet_price").Item,
        MinAvatarLevel = PropertyManager.GetLong("allegiance_gauntlet_min_avatar_level").Item,
        CooldownHours = PropertyManager.GetLong("allegiance_gauntlet_cooldown_hours").Item,
        MaxConcurrent = PropertyManager.GetLong("allegiance_gauntlet_max_concurrent").Item,
        StagingSeconds = PropertyManager.GetLong("allegiance_gauntlet_staging_seconds").Item,
        IntermissionSeconds = PropertyManager.GetLong("allegiance_gauntlet_intermission_seconds").Item,
        StartLevel = PropertyManager.GetLong("allegiance_gauntlet_start_level").Item,
        LevelStep = PropertyManager.GetLong("allegiance_gauntlet_level_step").Item,
        BossEvery = PropertyManager.GetLong("allegiance_gauntlet_boss_every").Item,
        WaveSeconds = PropertyManager.GetLong("allegiance_gauntlet_wave_seconds").Item,
        BossWaveBonusSeconds = PropertyManager.GetLong("allegiance_gauntlet_boss_wave_bonus_seconds").Item,
        WaveBaseCount = PropertyManager.GetLong("allegiance_gauntlet_wave_base_count").Item,
        MaxAlive = PropertyManager.GetLong("allegiance_gauntlet_max_alive").Item,
        HeadcountCap = PropertyManager.GetLong("allegiance_gauntlet_headcount_cap").Item,
        PortalsMin = PropertyManager.GetLong("allegiance_gauntlet_portals_min").Item,
        PortalsMax = PropertyManager.GetLong("allegiance_gauntlet_portals_max").Item,
        StragglerCount = PropertyManager.GetLong("allegiance_gauntlet_straggler_count").Item,
        StragglerSeconds = PropertyManager.GetLong("allegiance_gauntlet_straggler_seconds").Item,
        HealthCurveTopLevel = PropertyManager.GetLong("allegiance_gauntlet_health_curve_top_level").Item,
        MaxCreatureHealth = PropertyManager.GetLong("allegiance_gauntlet_max_creature_health").Item,
        DeathHoldSeconds = PropertyManager.GetLong("allegiance_gauntlet_death_hold_seconds").Item,
        FighterRespawnSeconds = PropertyManager.GetLong("allegiance_gauntlet_fighter_respawn_seconds").Item,
        MinContactWaves = PropertyManager.GetLong("allegiance_gauntlet_min_contact_waves").Item,
        CrateMaxTier = PropertyManager.GetLong("allegiance_gauntlet_crate_max_tier").Item,
        WaveCountPerPlayer = PropertyManager.GetDouble("allegiance_gauntlet_wave_count_per_player").Item,
        BossWaveTrashFraction = PropertyManager.GetDouble("allegiance_gauntlet_boss_wave_trash_fraction").Item,
        PortalMinDistance = PropertyManager.GetDouble("allegiance_gauntlet_portal_min_distance").Item,
        PortalMaxDistance = PropertyManager.GetDouble("allegiance_gauntlet_portal_max_distance").Item,
        StaggerMinSeconds = PropertyManager.GetDouble("allegiance_gauntlet_stagger_min_seconds").Item,
        StaggerMaxSeconds = PropertyManager.GetDouble("allegiance_gauntlet_stagger_max_seconds").Item,
        KillXpScale = PropertyManager.GetDouble("allegiance_gauntlet_kill_xp_scale").Item,
        PenMinDistance = PropertyManager.GetDouble("allegiance_gauntlet_pen_min_distance").Item,
        FighterHealthBase = PropertyManager.GetDouble("allegiance_gauntlet_fighter_health_base").Item,
        FighterHealthPerLevel = PropertyManager.GetDouble("allegiance_gauntlet_fighter_health_per_level").Item,
        FighterOffenseBase = PropertyManager.GetDouble("allegiance_gauntlet_fighter_offense_base").Item,
        FighterOffensePerLevel = PropertyManager.GetDouble("allegiance_gauntlet_fighter_offense_per_level").Item,
        FighterDamageBase = PropertyManager.GetDouble("allegiance_gauntlet_fighter_damage_base").Item,
        FighterDamagePerLevel = PropertyManager.GetDouble("allegiance_gauntlet_fighter_damage_per_level").Item,
        FighterMinDeathSeconds = PropertyManager.GetDouble("allegiance_gauntlet_fighter_min_death_seconds").Item,
        FighterBurstSeconds = PropertyManager.GetDouble("allegiance_gauntlet_fighter_burst_seconds").Item,
        Sites = PropertyManager.GetString("allegiance_gauntlet_sites").Item,
    });

    /// <summary>
    /// Clamps a garbled value into the range the rules can use. Counts and durations are at least 0 (a few at least 1, where
    /// 0 would divide by zero or make a wave end before it starts), the headcount cap is held to
    /// [1, GroupScaling.MaxRosterSize], the start level and level step to [1 or 0, int.MaxValue] (so the level arithmetic cannot wrap), max_alive to [1, <see cref="MaxAliveCeiling"/>], the max creature health to [1, int.MaxValue], the crate tier to
    /// [0, <see cref="CrateMaxTierCeiling"/>], a trash fraction to [0, 1], and every paired min/max is ordered. A non-finite
    /// double falls back to its default.
    /// </summary>
    public static GauntletTuning Sanitized(GauntletTuning t)
    {
        var d = Defaults;

        double Finite(double v, double fallback, double min = 0.0) => double.IsFinite(v) ? Math.Max(min, v) : fallback;

        var portalsMin = Math.Max(1L, t.PortalsMin);
        var portalMinDistance = Finite(t.PortalMinDistance, d.PortalMinDistance);
        var staggerMin = Finite(t.StaggerMinSeconds, d.StaggerMinSeconds);

        return t with
        {
            Price = Math.Max(0L, t.Price),
            MinAvatarLevel = Math.Max(0L, t.MinAvatarLevel),
            CooldownHours = Math.Max(0L, t.CooldownHours),
            MaxConcurrent = Math.Max(0L, t.MaxConcurrent),
            StagingSeconds = Math.Max(0L, t.StagingSeconds),
            IntermissionSeconds = Math.Max(0L, t.IntermissionSeconds),
            StartLevel = Math.Clamp(t.StartLevel, 1L, int.MaxValue),
            LevelStep = Math.Clamp(t.LevelStep, 0L, int.MaxValue),
            BossEvery = Math.Max(1L, t.BossEvery),
            WaveSeconds = Math.Max(1L, t.WaveSeconds),
            BossWaveBonusSeconds = Math.Max(0L, t.BossWaveBonusSeconds),
            WaveBaseCount = Math.Max(0L, t.WaveBaseCount),
            MaxAlive = Math.Clamp(t.MaxAlive, 1L, MaxAliveCeiling),
            HeadcountCap = Math.Clamp(t.HeadcountCap, 1L, GroupScaling.MaxRosterSize),
            PortalsMin = portalsMin,
            PortalsMax = Math.Max(portalsMin, t.PortalsMax),
            StragglerCount = Math.Max(0L, t.StragglerCount),
            StragglerSeconds = Math.Max(0L, t.StragglerSeconds),
            HealthCurveTopLevel = Math.Max(1L, t.HealthCurveTopLevel),
            MaxCreatureHealth = Math.Clamp(t.MaxCreatureHealth, 1L, int.MaxValue),
            DeathHoldSeconds = Math.Max(0L, t.DeathHoldSeconds),
            FighterRespawnSeconds = Math.Max(0L, t.FighterRespawnSeconds),
            MinContactWaves = Math.Max(1L, t.MinContactWaves),
            CrateMaxTier = Math.Clamp(t.CrateMaxTier, 0L, CrateMaxTierCeiling),

            WaveCountPerPlayer = Finite(t.WaveCountPerPlayer, d.WaveCountPerPlayer),
            BossWaveTrashFraction = double.IsFinite(t.BossWaveTrashFraction) ? Math.Clamp(t.BossWaveTrashFraction, 0.0, 1.0) : d.BossWaveTrashFraction,
            PortalMinDistance = portalMinDistance,
            PortalMaxDistance = Math.Max(portalMinDistance, Finite(t.PortalMaxDistance, d.PortalMaxDistance)),
            StaggerMinSeconds = staggerMin,
            StaggerMaxSeconds = Math.Max(staggerMin, Finite(t.StaggerMaxSeconds, d.StaggerMaxSeconds)),
            KillXpScale = Finite(t.KillXpScale, d.KillXpScale),
            PenMinDistance = Finite(t.PenMinDistance, d.PenMinDistance),
            FighterHealthBase = Finite(t.FighterHealthBase, d.FighterHealthBase),
            FighterHealthPerLevel = Finite(t.FighterHealthPerLevel, d.FighterHealthPerLevel),
            FighterOffenseBase = Finite(t.FighterOffenseBase, d.FighterOffenseBase),
            FighterOffensePerLevel = Finite(t.FighterOffensePerLevel, d.FighterOffensePerLevel),
            FighterDamageBase = Finite(t.FighterDamageBase, d.FighterDamageBase),
            FighterDamagePerLevel = Finite(t.FighterDamagePerLevel, d.FighterDamagePerLevel),
            FighterMinDeathSeconds = Finite(t.FighterMinDeathSeconds, d.FighterMinDeathSeconds, min: 1.0),
            FighterBurstSeconds = Finite(t.FighterBurstSeconds, d.FighterBurstSeconds),

            Sites = t.Sites ?? "",
        };
    }
}

using System;

using ACE.Server.Managers;

namespace ACE.Server.Avatars;

/// <summary>
/// Every Allegiance Avatar tunable as one immutable value. The pure math and rules take this
/// value instead of reading PropertyManager, so they stay testable without seeding statics.
/// Property names are the PascalCase of the allegiance_avatar_ key minus the prefix. Units:
/// contribution points = raw received XP x base_rate x source multiplier x roster scale; one
/// point = 1,000,000 raw XP at the Base source.
/// </summary>
public sealed record AvatarTuning
{
    public bool Enabled { get; init; }
    public long MinActiveAccounts { get; init; }
    public long ActiveWindowDays { get; init; }
    public long KeeperIdleDays { get; init; }
    public long ReseatCooldownDays { get; init; }
    public long SummonSeconds { get; init; }
    public long DressCooldownSeconds { get; init; }
    public long FavorCap { get; init; }
    public long FavorDecayGraceDays { get; init; }
    public long AccountDailySoftCap { get; init; }
    public long TopContributorsDays { get; init; }
    public long FlushSeconds { get; init; }
    public long EventPriceQuiet { get; init; }
    public long EventPriceAnnounced { get; init; }
    public long EventMinLevel { get; init; }
    public long EventScheduleBufferMinutes { get; init; }
    public double BaseRate { get; init; }
    public double ThreadMultiplier { get; init; }
    public double WorldEventMultiplier { get; init; }
    public double FellowshipMultiplier { get; init; }
    public double AccountDailyFalloff { get; init; }
    public double AccountDailyHardCapMultiple { get; init; }
    public double RosterThresholdFraction { get; init; }
    public double RosterExcessExponent { get; init; }
    public double LevelXpFraction { get; init; }
    public double FavorRate { get; init; }
    public double FavorDecayDailyFraction { get; init; }
    public double AbsorbFraction { get; init; }

    /// <summary>The size multiplier on the Avatar's body scale (1.0 = player size). A member of the record so every allegiance_avatar_* key is covered; AvatarLookApplier reads the key itself.</summary>
    public double Scale { get; init; }

    /// <summary>The fighter visibility probe log (Gauntlet.AvatarFighterProbe). A member of the record so every allegiance_avatar_* key is covered; the probe reads the key itself.</summary>
    public bool FighterProbeLog { get; init; }

    /// <summary>Seconds between the fighter's periodic lines (0 = off). A member of the record so every allegiance_avatar_* key is covered; AvatarFighterVoice reads the key itself.</summary>
    public long FighterVoiceSeconds { get; init; }

    /// <summary>The shipped defaults. Must equal the PropertyManager default dictionaries.</summary>
    public static AvatarTuning Defaults { get; } = new()
    {
        Enabled = true,
        MinActiveAccounts = 2,
        ActiveWindowDays = 7,
        KeeperIdleDays = 30,
        ReseatCooldownDays = 7,
        SummonSeconds = 600,
        DressCooldownSeconds = 3600,
        FavorCap = 20000,
        FavorDecayGraceDays = 3,
        AccountDailySoftCap = 500,
        TopContributorsDays = 30,
        FlushSeconds = 60,
        EventPriceQuiet = 2000,
        EventPriceAnnounced = 3000,
        EventMinLevel = 40,
        EventScheduleBufferMinutes = 15,
        BaseRate = 0.000001,
        ThreadMultiplier = 4.0,
        WorldEventMultiplier = 6.0,
        FellowshipMultiplier = 8.0,
        AccountDailyFalloff = 0.25,
        AccountDailyHardCapMultiple = 3.0,
        RosterThresholdFraction = 0.20,
        RosterExcessExponent = 0.5,
        LevelXpFraction = 0.01,
        FavorRate = 1.0,
        FavorDecayDailyFraction = 0.10,
        AbsorbFraction = 0.25,
        Scale = 2.0,
        FighterProbeLog = false,
        FighterVoiceSeconds = 120,
    };

    /// <summary>
    /// Reads the live values through PropertyManager. Requires PropertyManager.Initialize; defaults are seeded into the cache
    /// at boot. A NaN LevelXpFraction falls back to the default, and the fraction is clamped to [0, 1].
    /// </summary>
    public static AvatarTuning FromPropertyManager() => Sanitized(new()
    {
        Enabled = PropertyManager.GetBool("allegiance_avatar_enabled").Item,
        MinActiveAccounts = PropertyManager.GetLong("allegiance_avatar_min_active_accounts").Item,
        ActiveWindowDays = PropertyManager.GetLong("allegiance_avatar_active_window_days").Item,
        KeeperIdleDays = PropertyManager.GetLong("allegiance_avatar_keeper_idle_days").Item,
        ReseatCooldownDays = PropertyManager.GetLong("allegiance_avatar_reseat_cooldown_days").Item,
        SummonSeconds = PropertyManager.GetLong("allegiance_avatar_summon_seconds").Item,
        DressCooldownSeconds = PropertyManager.GetLong("allegiance_avatar_dress_cooldown_seconds").Item,
        FavorCap = PropertyManager.GetLong("allegiance_avatar_favor_cap").Item,
        FavorDecayGraceDays = PropertyManager.GetLong("allegiance_avatar_favor_decay_grace_days").Item,
        AccountDailySoftCap = PropertyManager.GetLong("allegiance_avatar_account_daily_soft_cap").Item,
        TopContributorsDays = PropertyManager.GetLong("allegiance_avatar_top_contributors_days").Item,
        FlushSeconds = PropertyManager.GetLong("allegiance_avatar_flush_seconds").Item,
        EventPriceQuiet = PropertyManager.GetLong("allegiance_avatar_event_price_quiet").Item,
        EventPriceAnnounced = PropertyManager.GetLong("allegiance_avatar_event_price_announced").Item,
        EventMinLevel = PropertyManager.GetLong("allegiance_avatar_event_min_level").Item,
        EventScheduleBufferMinutes = PropertyManager.GetLong("allegiance_avatar_event_schedule_buffer_minutes").Item,
        BaseRate = PropertyManager.GetDouble("allegiance_avatar_base_rate").Item,
        ThreadMultiplier = PropertyManager.GetDouble("allegiance_avatar_thread_multiplier").Item,
        WorldEventMultiplier = PropertyManager.GetDouble("allegiance_avatar_world_event_multiplier").Item,
        FellowshipMultiplier = PropertyManager.GetDouble("allegiance_avatar_fellowship_multiplier").Item,
        AccountDailyFalloff = PropertyManager.GetDouble("allegiance_avatar_account_daily_falloff").Item,
        AccountDailyHardCapMultiple = PropertyManager.GetDouble("allegiance_avatar_account_daily_hard_cap_multiple").Item,
        RosterThresholdFraction = PropertyManager.GetDouble("allegiance_avatar_roster_threshold_fraction").Item,
        RosterExcessExponent = PropertyManager.GetDouble("allegiance_avatar_roster_excess_exponent").Item,
        LevelXpFraction = PropertyManager.GetDouble("allegiance_avatar_level_xp_fraction").Item,
        FavorRate = PropertyManager.GetDouble("allegiance_avatar_favor_rate").Item,
        FavorDecayDailyFraction = PropertyManager.GetDouble("allegiance_avatar_favor_decay_daily_fraction").Item,
        AbsorbFraction = PropertyManager.GetDouble("allegiance_avatar_absorb_fraction").Item,
        Scale = PropertyManager.GetDouble("allegiance_avatar_scale").Item,
        FighterProbeLog = PropertyManager.GetBool("allegiance_avatar_fighter_probe_log").Item,
        FighterVoiceSeconds = PropertyManager.GetLong("allegiance_avatar_fighter_voice_seconds").Item,
    });

    internal static AvatarTuning Sanitized(AvatarTuning t) => t with
    {
        LevelXpFraction = Math.Clamp(double.IsNaN(t.LevelXpFraction) ? Defaults.LevelXpFraction : t.LevelXpFraction, 0.0, 1.0),
    };
}

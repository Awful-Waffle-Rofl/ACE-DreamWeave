using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Managers;

using log4net;

namespace ACE.Server.Frays
{
    public enum FraysSettingKind { Number, Integer, Toggle, Text }

    /// <summary>One admin-tunable Frays value: a STRING server property (frays_*) whose "" means not set.</summary>
    public sealed class FraysSetting
    {
        public string Key { get; init; }
        public string PropertyName => FraysSettings.Prefix + Key;
        public string Group { get; init; }
        public FraysSettingKind Kind { get; init; }
        public double Default { get; init; }
        public string TextDefault { get; init; } = "";
        public double Min { get; init; }
        public double Max { get; init; }
        public string Description { get; init; }

        /// <summary>True when a fire may override it (frays_fire_overrides).</summary>
        public bool PerFire { get; init; }

        public string RangeText => Kind == FraysSettingKind.Toggle ? "on/off" : Kind == FraysSettingKind.Text ? "text"
            : $"{FraysSettings.Format(Min)}..{FraysSettings.Format(Max)}";

        public string DefaultText => Kind == FraysSettingKind.Toggle ? (Default != 0 ? "on" : "off") : Kind == FraysSettingKind.Text ? TextDefault : FraysSettings.Format(Default);
    }

    /// <summary>
    /// The Frays settings, built on the World Events Hold pattern (HoldSettings): one string server property per
    /// value, "" = not set (the code default applies), clamped to its range, read live on every use, so an admin's
    /// change is picked up within a tick. Leash values can also be set per fire through frays_fire_overrides, a text
    /// property of "fireId:key=value,key=value;fireId:..." that "/frays admin fire" writes.
    ///
    /// Reads go through <see cref="StringSource"/>, which only looks at the property cache (every registered key is cached
    /// from boot); a unit test has no cache, so every read there is the code default unless the test sets a value.
    /// </summary>
    public static class FraysSettings
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysSettings));

        public const string Prefix = "frays_";

        public const string Enabled = "enabled";
        public const string Landblocks = "landblocks";

        // leash
        public const string LeashMargin = "leash_margin";
        public const string LeashMaxChase = "leash_max_chase";
        public const string LeashGraceSeconds = "leash_grace_seconds";
        public const string LeashReturnSpeed = "leash_return_speed";
        public const string LeashReturnHeal = "leash_return_heal";

        // heat (each player's own Heat at a fire)
        public const string HeatCap = "heat_cap";
        public const string HeatPointsBase = "heat_points_base";
        public const string HeatPointsPerLevel = "heat_points_per_level";
        public const string HeatDecaySeconds = "heat_decay_seconds";
        public const string HeatDecayGraceSeconds = "heat_decay_grace_seconds";
        public const string HeatDeathDropLevels = "heat_death_drop_levels";
        public const string HeatDeathClearsPack = "heat_death_clears_pack";
        public const string HeatSettleDeaths = "heat_settle_deaths";
        public const string HeatSettleWindowMinutes = "heat_settle_window_minutes";
        public const string HeatSettleDrop = "heat_settle_drop";
        public const string HeatSettleHoldMinutes = "heat_settle_hold_minutes";
        public const string HeatSettleHoldMaxMinutes = "heat_settle_hold_max_minutes";
        public const string HeatKillPoints = "heat_kill_points";
        public const string HeatStart = "heat_start";
        public const string HeatStartBelowRating = "heat_start_below_rating";
        public const string HeatEarlyClimbUntil = "heat_early_climb_until";
        public const string HeatEarlyClimbGain = "heat_early_climb_gain";

        // spawning
        public const string SpawnIntervalSeconds = "spawn_interval_seconds";
        public const string SpawnIntervalPerLevel = "spawn_interval_per_level";
        public const string SpawnIntervalFloor = "spawn_interval_floor";
        public const string SpawnPointsPerPlayer = "spawn_points_per_player";
        public const string SpawnPackEvery = "spawn_pack_every";
        public const string SpawnMaxAlive = "spawn_max_alive";

        // scaling

        // static XP
        public const string ActivitySeconds = "activity_seconds";

        // loot and corpses
        public const string LootTierFirstHeat = "loot_tier_first_heat";
        public const string LootTierSecondHeat = "loot_tier_second_heat";
        public const string LootTierByLevel = "loot_tier_by_level";
        public const string LootQualityPerStep = "loot_quality_per_step";
        public const string LootQualityBase = "loot_quality_base";
        public const string LootQualityPerHeat = "loot_quality_per_heat";
        public const string LootQualityCap = "loot_quality_cap";
        public const string LootCorpseCap = "loot_corpse_cap";
        public const string LootOverflowPerKey = "loot_overflow_per_key";
        public const string SnarlKeys = "snarl_keys";
        public const string SkySealsPerSnarl = "sky_seals_per_snarl";
        public const string SkySealsGreatSnarl = "sky_seals_great_snarl";
        public const string SkySealsHeat = "sky_seals_heat";
        public const string SkySealsAtHeat = "sky_seals_at_heat";
        public const string SkySealsWeeklyTask = "sky_seals_weekly_task";
        public const string SkyBundleKeys = "sky_bundle_keys";
        public const string SkyBundleKindling = "sky_bundle_kindling";
        public const string SkyBundleShards = "sky_bundle_shards";
        public const string SkyBannerMinutes = "sky_banner_minutes";
        public const string SkyBannerEmbers = "sky_banner_embers";
        public const string SkyBannerKeys = "sky_banner_keys";
        public const string MonumentSetup = "monument_setup";
        public const string MonumentStoneSetup = "monument_stone_setup";
        public const string MonumentStones = "monument_stones";
        public const string MonumentRingRadius = "monument_ring_radius";
        public const string MonumentChampion = "monument_champion";
        public const string MonumentChampionSize = "monument_champion_size";
        public const string MonumentChampionAngle = "monument_champion_angle";
        public const string KeyBarWork = "key_bar_work";
        public const string KeyBarKnee = "key_bar_knee";
        public const string KeyBarPastKnee = "key_bar_past_knee";
        public const string EarnKnee = "earn_knee";
        public const string EarnPastKnee = "earn_past_knee";
        public const string EarnBankMinutes = "earn_bank_minutes";
        public const string CorpseRotSeconds = "corpse_rot_seconds";
        public const string CorpseCap = "corpse_cap";
        public const string FireScale = "fire_scale";

        // ownership
        public const string AwaySeconds = "away_seconds";
        public const string FireAwaySeconds = "fire_away_seconds";

        // rating
        public const string RatingHoldMinutes = "rating_hold_minutes";
        public const string RatingMinCleared = "rating_min_cleared";

        // who may light a fire
        public const string MinLevel = "min_level";
        public const string OnePerIp = "one_per_ip";

        // embers
        public const string EmbersNormal = "embers_kill_normal";
        public const string EmbersPerLevel = "embers_per_level";
        public const string EmbersCampPerMinute = "embers_camp_per_minute";
        public const string EmbersCampRadius = "embers_camp_radius";

        // placement
        // patterns, the Snarl, the week
        public const string WeekResetDay = "week_reset_day";
        public const string WeekResetHour = "week_reset_hour";
        public const string WeekOffset = "week_offset";
        public const string AffixShift = "affix_shift";
        public const string HeatShiftsEnabled = "heat_shifts_enabled";
        public const string HeatShiftFirst = "heat_shift_first";
        public const string SurgeEverySeconds = "surge_every_seconds";
        public const string SnarlBarNeed = "snarl_bar_need";
        public const string SnarlHealthNormal = "snarl_health_normal";
        public const string SnarlHealthByHeat = "snarl_health_by_heat";
        public const string SnarlDamageNormal = "snarl_damage_normal";
        public const string SnarlAdds = "snarl_adds";
        public const string SnarlAddsCount = "snarl_adds_count";
        public const string SnarlAddsHealth = "snarl_adds_health";
        public const string SnarlAddsBarMult = "snarl_adds_bar_mult";
        public const string CacheItemsNormal = "cache_items_normal";
        public const string EmberWeeklyCap = "ember_weekly_cap";
        public const string EmberOverCapRate = "ember_over_cap_rate";

        public const string PlacementGap = "placement_gap";

        // families
        public const string FamiliesEnabled = "families_enabled";
        public const string FamilySource = "family_source";
        public const string FamilyCasterWeight = "family_caster_weight";
        public const string FamilyArcherWeight = "family_archer_weight";
        public const string CasterMinHeat = "caster_min_heat";
        public const string FamilyExclude = "family_exclude";
        public const string SpawnTargetPerHour = "spawn_target_per_hour";
        public const string ZoneRadius = "zone_radius";

        // group camps (the Sky Fray)
        public const string GroupMinSize = "group_min_size";
        public const string LfgAnswerSeconds = "lfg_answer_seconds";
        public const string LfgQueueMinutes = "lfg_queue_minutes";
        public const string GroupMinSizeMiddle = "group_min_size_middle";
        public const string GroupHealthPerMember = "group_health_per_member";
        public const string GroupPackPerMember = "group_pack_per_member";
        public const string GroupRatePerMember = "group_rate_per_member";
        public const string GroupDeathDrop = "group_death_drop";
        public const string WispSpacing = "wisp_spacing";
        public const string FrayOrder = "fray_order";
        public const string FamilyPin = "family_pin";
        public const string SwarmHealthShare = "swarm_health_share";
        public const string SwarmPackShare = "swarm_pack_share";
        public const string LootItemsPerStandard = "loot_items_per_standard";
        public const string LootOrdinaryPerStandard = "loot_ordinary_per_standard";
        public const string LootMundanePerStandard = "loot_mundane_per_standard";

        // long-term systems
        public const string BaneSlayerBonus = "bane_slayer_bonus";
        public const string RerollWeekChance = "reroll_week_chance";
        public const string VaultBonusEnabled = "vault_bonus_enabled";
        public const string VaultEmbersMin = "vault_embers_min";
        public const string VaultEmbersMax = "vault_embers_max";
        public const string VaultSalvageChance = "vault_salvage_chance";
        public const string VaultScarabChance = "vault_scarab_chance";
        public const string VaultMoteChance = "vault_mote_chance";
        public const string VaultMotesMax = "vault_motes_max";
        public const string FireTierMax = "fire_tier_max";
        public const string FireTierCost = "fire_tier_cost";
        public const string AscensionMax = "ascension_max";
        public const string AscensionCost = "ascension_cost";
        public const string AscensionHeat = "ascension_heat";
        public const string AscensionPerWeek = "ascension_per_week";
        public const string BountyEmbers = "bounty_embers";
        public const string WeeklyTaskEmbers = "weekly_task_embers";
        public const string HuntEmbers = "hunt_embers";
        public const string TaskXpPercent = "task_xp_percent";
        public const string TaskXpMin = "task_xp_min";
        public const string TaskXpMax = "task_xp_max";
        public const string TaskLuminance = "task_luminance";
        public const string RecallCooldown = "recall_cooldown_seconds";
        public const string DeathAtFire = "death_respawn_at_fire";
        public const string SnarlTrophyChance = "snarl_trophy_chance";
        public const string SnarlBaneChance = "snarl_bane_chance";
        public const string SnarlKindlingChance = "snarl_kindling_chance";
        public const string SnarlScarabChance = "snarl_scarab_chance";
        public const string SnarlMmdChance = "snarl_mmd_chance";
        public const string SnarlMmdCount = "snarl_mmd_count";
        public const string SnarlEmberChance = "snarl_ember_chance";
        public const string SnarlEmberCount = "snarl_ember_count";
        public const string SnarlShardChance = "snarl_shard_chance";
        public const string SnarlAugChance = "snarl_aug_chance";
        public const string SnarlAugWcids = "snarl_aug_wcids";
        public const string GreatSnarlAugChance = "great_snarl_aug_chance";
        public const string TrophyWallCount = "trophy_wall_count";
        public const string TrophyWallState = "trophy_wall_state";
        public const string TrophyCountsState = "trophy_counts_state";
        public const string ScoreHeatPoints = "score_heat_points";
        public const string ScoreKillPoints = "score_kill_points";
        public const string ScoreSnarlPoints = "score_snarl_points";
        public const string ScoreGildedPoints = "score_gilded_points";
        public const string ScoreDeathPoints = "score_death_points";
        public const string ScoreTimePoints = "score_time_points";
        public const string ScoreParMinutes = "score_par_minutes";
        public const string ScoreBronze = "score_bronze";
        public const string ScoreSilver = "score_silver";
        public const string ScoreGold = "score_gold";
        public const string ScoreBronzeKeys = "score_bronze_keys";
        public const string ScoreSilverKeys = "score_silver_keys";
        public const string ScoreGoldKeys = "score_gold_keys";
        public const string ScoreTitles = "score_titles";
        public const string LucidReward = "lucid_reward";
        public const string LucidGreatSnarls = "lucid_great_snarls";
        public const string LucidHours = "lucid_hours";
        public const string LucidState = "lucid_state";
        public const string LucidDamage = "lucid_damage";
        public const string LucidEmbers = "lucid_embers";
        public const string LucidCacheKeys = "lucid_cache_keys";
        public const string GroupLongTerm = "longterm";

        // version 1: level-matched creatures and pay
        public const string LevelScaling = "level_scaling";
        public const string ThreadsShape = "threads_shape";
        public const string ThreadsShapeHeatLevels = "threads_shape_heat_levels";
        public const string ThreadsShapeMaxLevel = "threads_shape_max_level";
        public const string HeatLevelsPerHeat = "heat_levels_per_heat";
        public const string DefenseCap = "defense_cap";
        public const string DefenseCapPerLevel = "defense_cap_per_level";
        public const string AttackVsDefense = "attack_vs_defense";
        public const string AttackVsDefensePerHeat = "attack_vs_defense_per_heat";
        public const string HealthCamperLevelCap = "health_camper_level_cap";
        public const string XpByLevel = "xp_by_level";
        public const string HeatCurveLevels = "heat_curve_levels";
        public const string HeatCurveHealth = "heat_curve_health";
        public const string HeatCurveDamage = "heat_curve_damage";
        public const string HeatCurveHitCap = "heat_curve_hit_cap";
        public const string HeatCurveHitShare = "heat_curve_hit_share";
        public const string HitByHealth = "hit_by_health";
        public const string HitArmorMaxCut = "hit_armor_max_cut";
        public const string SpellHitShare = "spell_hit_share";
        public const string DamageReductionCap = "damage_reduction_cap";
        public const string DamageBonusCap = "damage_bonus_cap";
        public const string BoonBonusCap = "boon_bonus_cap";
        public const string BloodSplashDamage = "blood_splash_damage";
        public const string HecatombReachDamage = "hecatomb_reach_damage";
        public const string BloodBoonRadius = "blood_boon_radius";
        public const string HeatCurveIncome = "heat_curve_income";
        public const string HeatPayCurve = "heat_pay_curve";
        public const string CenterText = "center_text";
        public const string BoonPopup = "boon_popup";
        public const string HeatUpEffect = "heat_up_effect";
        public const string VillageLayout = "village_layout";
        public const string GroupRuns = "group_runs";
        public const string HouseCovers = "house_covers";
        public const string BoonEvery = "boon_every";
        public const string BoonAutoSeconds = "boon_auto_seconds";
        public const string BoonVoteSeconds = "boon_vote_seconds";
        public const string BoonVoteActiveSeconds = "boon_vote_active_seconds";
        public const string RareBoonHeat = "rare_boon_heat";
        public const string EliteAffixes = "elite_affixes";
        public const string EffigySnarlChance = "effigy_snarl_chance";
        public const string EmberblessedMinutes = "emberblessed_minutes";
        public const string HeatEffects = "heat_effects";
        public const string GreatSnarlFeederShare = "great_snarl_feeder_share";
        public const string LumByLevel = "lum_by_level";
        public const string XpKillShare = "xp_kill_share";
        public const string XpKillPace = "xp_kill_pace";
        public const string XpKillCap = "xp_kill_cap";
        public const string XpKillCapByHeat = "xp_kill_cap_by_heat";
        public const string KillPayByHealth = "kill_pay_by_health";
        public const string LootByHeatHealth = "loot_by_heat_health";
        public const string PackLink = "pack_link";

        public const string SpawnNearRadius = "spawn_near_radius";
        public const string SpawnRegrowSeconds = "spawn_regrow_seconds";
        public const string NestPackBonus = "nest_pack_bonus";
        public const string NestRestSeconds = "nest_rest_seconds";
        public const string QuickRestSeconds = "quick_rest_seconds";
        public const string HotSpotMinutes = "hot_spot_minutes";
        public const string AggroRange = "aggro_range";

        // solo camp layouts (FraysLayout)
        public const string FixedLayout = "fixed_layout";
        public const string LayoutGapAggro = "layout_gap_aggro";
        public const string LayoutPoints = "layout_points";
        public const string LayoutPackBonus = "layout_pack_bonus";
        public const string LayoutEdges = "layout_edges";
        public const string LayoutRestMult = "layout_rest_mult";
        public const string RichPoints = "rich_points";
        public const string RichMult = "rich_mult";
        public const string RichKillPayMult = "rich_kill_pay_mult";
        public const string RichLootMult = "rich_loot_mult";
        public const string BellowsPackShare = "bellows_pack_share";
        public const string PackmasterPackShare = "packmaster_pack_share";
        public const string PackTypes = "pack_types";
        public const string SwarmPackCounts = "swarm_pack_counts";
        public const string BrutePackCounts = "brute_pack_counts";
        public const string BruteScale = "brute_scale";
        public const string SwarmHitShare = "swarm_hit_share";
        public const string BruteHitByShare = "brute_hit_by_share";
        public const string BruteHitByPack = "brute_hit_by_pack";
        public const string PackTether = "pack_tether";
        public const string PackTypeSupply = "pack_type_supply";
        public const string PackTypeFollow = "pack_type_follow";
        public const string PackTypeFollowSeconds = "pack_type_follow_seconds";
        public const string PackTypeFollowExtra = "pack_type_follow_extra";
        public const string RichRestSeconds = "rich_rest_seconds";
        public const string RichRestFightMult = "rich_rest_fight_mult";
        public const string SpawnTargetByHeat = "spawn_target_by_heat";
        public const string SpawnFollowExtra = "spawn_follow_extra";
        public const string HeatUpkeepByHeat = "heat_upkeep_by_heat";
        public const string SpawnFollowSeconds = "spawn_follow_seconds";
        public const string PayPerCreatureByHeat = "pay_per_creature_by_heat";
        public const string RichWarnSeconds = "rich_warn_seconds";
        public const string ShrineEnabled = "shrine_enabled";
        public const string ShrineBuffSeconds = "shrine_buff_seconds";
        public const string ShrineCooldownSeconds = "shrine_cooldown_seconds";
        public const string ShrineRadius = "shrine_radius";
        public const string ShrineSetup = "shrine_setup";
        public const string RetakeCooldownSeconds = "retake_cooldown_seconds";
        public const string SnarlBarGildedMult = "snarl_bar_gilded_mult";
        public const string SnarlSpeedCap = "snarl_speed_cap";
        public const string FillOnTake = "fill_on_take";
        public const string SessionLog = "session_log";
        public const string ShrineScale = "shrine_scale";
        public const string ShrineFury = "shrine_fury";
        public const string ShrineWard = "shrine_ward";
        public const string ShrineStoke = "shrine_stoke";
        public const string ShrineMending = "shrine_mending";
        public const string ShrineRotate = "shrine_rotate";
        public const string RadarFreeColor = "radar_free_color";
        public const string RadarLitColor = "radar_lit_color";
        public const string BeaconMode = "beacon_mode";
        public const string BeaconSetup = "beacon_setup";
        public const string BeaconScale = "beacon_scale";
        public const string WispsEnabled = "wisps_enabled";
        public const string FrayRealm = "fray_realm";
        public const string WispSetup = "wisp_setup";
        public const string WispScale = "wisp_scale";
        public const string WispHeight = "wisp_height";
        public const string GreatSnarlWork = "great_snarl_work";
        public const string GreatSnarlHealth = "great_snarl_health";
        public const string GreatSnarlScale = "great_snarl_scale";
        public const string GreatPortalSeconds = "great_portal_seconds";
        public const string GreatPortalWcid = "great_portal_wcid";
        public const string GreatSnarlCacheItems = "great_snarl_cache_items";
        public const string GreatSnarlWorkPerCamper = "great_snarl_work_per_camper";
        public const string GreatSnarlWarnSeconds = "great_snarl_warn_seconds";
        public const string GreatSnarlGapHours = "great_snarl_gap_hours";
        public const string GreatHoldMinutes = "great_hold_minutes";
        public const string GreatTimeoutMinutes = "great_timeout_minutes";
        public const string GreatLeaveKeep = "great_leave_keep";
        public const string GreatLeaveHoldMinutes = "great_leave_hold_minutes";
        public const string HeatProgressStep = "heat_progress_step";
        public const string FrayGoalWork = "fray_goal_work";
        public const string FrayGoalKeys = "fray_goal_keys";
        public const string FrayGoalCacheItems = "fray_goal_cache_items";
        public const string FrayGoalTaskReward = "fray_goal_task_reward";
        public const string FrayGoalTaskMult = "fray_goal_task_mult";
        public const string FrayGoalSlayerStones = "fray_goal_slayer_stones";
        public const string FrayGoalKindling = "fray_goal_kindling";
        public const string FrayState = "fray_state";

        public const string FireOverrides = "fire_overrides";

        public const string GroupGeneral = "general";
        public const string GroupLeash = "leash";
        public const string GroupHeat = "heat";
        public const string GroupSpawn = "spawn";
        public const string GroupScaling = "scaling";
        public const string GroupOwnership = "ownership";
        public const string GroupRating = "rating";
        public const string GroupEmbers = "embers";
        public const string GroupXp = "xp";
        public const string GroupLoot = "loot";
        public const string GroupSnarl = "snarl";
        public const string GroupWeek = "week";

        private static FraysSetting N(string g, string k, double d, double min, double max, string desc, bool perFire = false) =>
            new FraysSetting { Group = g, Key = k, Kind = FraysSettingKind.Number, Default = d, Min = min, Max = max, Description = desc, PerFire = perFire };

        private static FraysSetting I(string g, string k, int d, int min, int max, string desc) =>
            new FraysSetting { Group = g, Key = k, Kind = FraysSettingKind.Integer, Default = d, Min = min, Max = max, Description = desc };

        private static FraysSetting T(string g, string k, bool d, string desc) =>
            new FraysSetting { Group = g, Key = k, Kind = FraysSettingKind.Toggle, Default = d ? 1 : 0, Min = 0, Max = 1, Description = desc };

        private static FraysSetting X(string g, string k, string d, string desc) =>
            new FraysSetting { Group = g, Key = k, Kind = FraysSettingKind.Text, TextDefault = d, Description = desc };

        public static readonly IReadOnlyList<FraysSetting> All = new List<FraysSetting>
        {
            T(GroupGeneral, Enabled, false, "Master switch. On: the Frays landblocks load without retail spawns and get campfires (applies when a landblock next loads, so restart or let it unload). Off: retail as always."),
            X(GroupGeneral, Landblocks, "", "Comma-separated hex landblocks run as Frays. Empty (the default) = every landblock holding a fire of an active Fray (fray_order)."),

            N(GroupLeash, LeashMargin, 10, 0, 30, "Metres the leash zone reaches past each doorway of the fire's room.", perFire: true),
            N(GroupLeash, LeashMaxChase, 50, 0, 200, "Hard limit, in metres from the fire, on where its monsters may go and whom they may target (0 = no limit).", perFire: true),
            N(GroupLeash, LeashGraceSeconds, 2, 0, 30, "Seconds a monster may be outside its zone before it turns back.", perFire: true),
            N(GroupLeash, LeashReturnSpeed, 1.5, 1, 5, "Speed of a monster walking home, times its run speed.", perFire: true),
            N(GroupLeash, LeashReturnHeal, 1, 0, 1, "Share of max health a monster gets back when it reaches its spawn point.", perFire: true),

            I(GroupHeat, HeatCap, 20, 0, 50, "Highest Heat a player can reach."),
            I(GroupHeat, HeatPointsBase, 10, 1, 1000, "Heat points level 0 needs to reach level 1 (level N needs base + per_level x N)."),
            I(GroupHeat, HeatPointsPerLevel, 1, 0, 1000, "Extra Heat points each further level needs (with kills paying Heat by their real health: about 8 minutes a level at Heat 6, 14 at Heat 19)."),
            X(GroupHeat, HeatUpkeepByHeat, "15:0,16:5.6,17:11.5,18:17.6,19:25.2,20:31.5", "The top Heats burn down, so Heat 18-20 is very hard to reach and hold: Heat points lost a minute at each Heat (heat:points, linear between), all the time, killing or not, Hold steady too, Still Night too. Kills must outpace it to stay. A camper gains about 5.6 points a minute at pace 500, 11 at 1000, 17 at 1500, so those hold about Heat 16, 17 and 18; only the fastest reach 19-20. A group camp burns this times the members present. Empty = none."),
            N(GroupHeat, HeatDecaySeconds, 30, 0, 600, "A player's Heat loses 1 point every this many seconds once decay runs (0 = no decay). Hold steady stops it."),
            N(GroupHeat, HeatDecayGraceSeconds, 150, 0, 600, "Decay only runs after this many seconds without a kill, so a camp that keeps killing (walking between packs outdoors, a full rebuff included) never cools."),
            I(GroupHeat, HeatDeathDropLevels, 2, 0, 50, "Heat levels a player loses when they die (theirs only)."),
            I(GroupHeat, HeatSettleDeaths, 2, 0, 20, "Climbing (the default): a solo camper who dies this many times inside heat_settle_window_minutes settles: their Heat holds heat_settle_drop below the lowest Heat they died at, then climbs again, so a camper who dies too often settles at a Heat they can hold. 0 = off."),
            N(GroupHeat, HeatSettleWindowMinutes, 60, 1, 240, "The window heat_settle_deaths counts deaths in (60: test campers died 15 to 50 minutes apart, and 20 missed most of them)."),
            I(GroupHeat, HeatSettleDrop, 2, 0, 20, "How far below the lowest Heat died at a settled camper holds."),
            N(GroupHeat, HeatSettleHoldMinutes, 30, 1, 600, "How long the first settle holds; each further settle at the same camp doubles it, up to heat_settle_hold_max_minutes."),
            N(GroupHeat, HeatSettleHoldMaxMinutes, 120, 1, 1440, "The longest a settle holds."),
            T(GroupHeat, HeatDeathClearsPack, true, "On: when a camper dies, the fire's living creatures above the Heat it cooled to fade away (the camper's own; a group camp's all), so the camp refills at the new Heat. Snarls stay. Without it, a pack built at Heat 13 kept killing its camper after their Heat fell. Off: they stay as they were."),
            I(GroupHeat, HeatKillPoints, 1, 0, 100, "Heat points for killing the room's reference creature; others give more or less by their health, shared by damage done."),
            I(GroupHeat, HeatStart, 0, 0, 50, "Testing aid: the lowest Heat anyone starts at (players can also start up to their rating with /frays start)."),
            I(GroupHeat, HeatStartBelowRating, 2, 0, 50, "A camp starts this many Heat under your rating unless you picked a start with /frays start, so lighting the fire starts you near your rating. /frays start auto goes back to this."),
            I(GroupHeat, HeatEarlyClimbUntil, 5, 0, 50, "Players with no Heat rating yet climb faster below this Heat (heat_early_climb_gain)."),
            N(GroupHeat, HeatEarlyClimbGain, 2.5, 1, 10, "Heat points per kill, times this, for a player with no rating yet below heat_early_climb_until (the first Heats carry little danger, so they pass quickly)."),

            N(GroupSpawn, SpawnIntervalSeconds, 8, 0.5, 120, "Seconds between spawns at each spawn point at Heat 0."),
            N(GroupSpawn, SpawnIntervalPerLevel, 0.04, 0, 0.5, "The spawn interval shrinks by this share per Heat level (compounding: 8 s is 5.3 s at 10, 3.5 s at 20)."),
            N(GroupSpawn, SpawnIntervalFloor, 1, 0.2, 60, "The spawn interval never drops below this many seconds."),
            I(GroupSpawn, SpawnPointsPerPlayer, 2, 1, 8, "Spawn points each player's own stream uses (their spawns rotate across the room's points)."),
            I(GroupSpawn, SpawnPackEvery, 5, 0, 100, "Each spawn is 1 creature, plus 1 every this many Heat levels (0 = always 1)."),
            I(GroupSpawn, SpawnMaxAlive, 15, 1, 200, "Most monsters alive per player at a fire; that player's spawns pause at this number."),


            N(GroupXp, ActivitySeconds, 60, 5, 600, "A player counts as active when they hit one of their own fire monsters or were hit by one within this many seconds."),

            I(GroupLoot, LootTierFirstHeat, 8, 0, 100, "From this Heat, corpses roll one treasure tier higher (past tier 8 it becomes better loot quality). 99 = off (loot then does not improve with Heat)."),
            I(GroupLoot, LootTierSecondHeat, 16, 0, 100, "From this Heat, two tiers higher (past tier 8 it becomes better loot quality). 99 = off."),
            X(GroupLoot, LootTierByLevel, "0:8", "The lowest treasure tier a Frays corpse rolls, by the creature's level after scaling (level:tier, the highest step at or under it). The default is always tier 8, so a low-level creature lifted to a high-level camper (a level 115 Monouga at level 440) does not roll its own tier 4 loot. Empty = each creature's own tier."),
            N(GroupLoot, LootQualityPerStep, 0.15, 0, 1, "Loot quality added for each tier step past tier 8."),
            N(GroupLoot, LootQualityBase, 0, 0, 1, "Loot quality every Frays corpse starts with (0 = retail). Heat 0 loot matches the tier 8 spots Neftet, Toutou, Hoshino and Snow Valley: their profiles (2000, 2101, 2105) add 0 to 0.1, so 0 (was 0.15)."),
            N(GroupLoot, LootQualityPerHeat, 0.015, 0, 0.2, "Loot quality added for each Heat (0.015: +0.15 at Heat 10, +0.3 at Heat 20)."),
            N(GroupLoot, LootQualityCap, 0.5, 0, 1, "The most loot quality a Frays corpse rolls (the Ember Vault is 0.6; Threads caps at 0.5)."),
            N(GroupLoot, LootCorpseCap, 25, 1, 120, "The most items one Frays corpse holds (a corpse holds 120). A corpse whose magic, ordinary and mundane items would pass it keeps their mix, cut down to fit; the items past it go to its owner's key bar (loot_overflow_per_key), as a key to a village chest. 40 items on a corpse is too many (was 110)."),
            N(GroupLoot, LootOverflowPerKey, 17, 0, 1000, "Items past loot_corpse_cap that make one Vault Key on the corpse owner's key bar (0 = they are lost). 17: an Ember Vault's 10 magic items in the corpse mix (15 magic of 26 items), so a brute pack is worth a swarm pack however its loot splits."),
            I(GroupLoot, SnarlKeys, 1, 0, 10, "Vault Keys each Snarl kill gives its killer (0 = none)."),
            I(GroupLoot, SkySealsPerSnarl, 1, 0, 20, "Sky Seals every member of a group camp gets when its Snarl falls (0 = none). Seals buy looks, bundles and the Sky Banner from the Sky Steward."),
            I(GroupLoot, SkySealsGreatSnarl, 2, 0, 20, "Sky Seals for each camper the Great Snarl pays who holds a group camp (0 = none)."),
            I(GroupLoot, SkySealsHeat, 20, 0, 25, "The Heat a group camp reaches for sky_seals_at_heat (first time in a camp, once a day per member; 0 = off)."),
            I(GroupLoot, SkySealsAtHeat, 5, 0, 50, "Sky Seals each member present gets when a group camp reaches sky_seals_heat (0 = none)."),
            I(GroupLoot, SkySealsWeeklyTask, 5, 0, 50, "Sky Seals for the weekly task Camp 2 hours in a group camp (0 = the task pays nothing)."),
            I(GroupLoot, SkyBundleKeys, 5, 0, 100, "Vault Keys in a Sky Key Bundle (the Sky Steward)."),
            I(GroupLoot, SkyBundleKindling, 2, 0, 100, "Kindling in a Sky Kindling Bundle (the Sky Steward)."),
            I(GroupLoot, SkyBundleShards, 5, 0, 100, "Sigil Shards in a Sky Shard Bundle (the Sky Steward)."),
            N(GroupLoot, SkyBannerMinutes, 60, 0, 600, "Minutes a Sky Banner flies over a group camp (0 = banners do nothing). Banners do not stack."),
            N(GroupLoot, SkyBannerEmbers, 0.10, 0, 1, "A Sky Banner's extra Embers for every member of the camp (0.10 = +10%)."),
            N(GroupLoot, SkyBannerKeys, 0.10, 0, 1, "A Sky Banner's faster key bar for every member of the camp (0.10 = +10%)."),
            N(GroupLoot, KeyBarKnee, 600, 0, 100000, "The key and Snarl bars' shared soft ceiling: Heat-weighted work an hour (standard creatures, a Gilded one counted once) that fills both bars at the full rate; up to 10 minutes of it banks for a burst. Past it a kill fills them key_bar_past_knee as much, so speed still pays, but less. 600 leaves a middling camper (pace 500) untouched and puts the fastest at about 1.3 to 1.5x a Threads hour of chest items at Heat 20. 0 = no knee."),
            N(GroupLoot, KeyBarPastKnee, 0.25, 0, 1, "The key and Snarl bars past key_bar_knee: the share of the normal fill a kill gives (0.25 = a quarter, was 0.333, so the fastest campers stay under Threads on keys)."),
            N(GroupLoot, EarnKnee, 400, 0, 100000, "Embers and corpse loot's soft ceiling, so single-target and AoE builds earn the same and the ceiling is barely noticed: standard creatures cleared an hour (not Heat-weighted) that pay full Embers and full corpse loot; up to earn_bank_minutes of it banks for a burst. Past it a kill pays earn_past_knee as much. 400 sits above the fastest builds tested (about 356 an hour), so only a pace past every player is thinned. 0 = no ceiling."),
            N(GroupLoot, EarnPastKnee, 0.4, 0, 1, "Embers and corpse loot past earn_knee: the share of a kill's normal pay (0.4, so killing faster still pays: 1,000 an hour earns about 1.6 times a camper at the ceiling)."),
            N(GroupLoot, EarnBankMinutes, 5, 0.5, 60, "Minutes of earn_knee that bank for a burst: a burst pays in full until they run out, and they refill as you slow down (was 10)."),
            X(GroupLoot, PayPerCreatureByHeat, "0:0.86,10:0.92,20:1", "Each creature's Embers, key bar, Snarl bar and corpse loot, times this by Heat (heat:mult, linear between); XP and lum are not changed. Lowered where the Gilded share rose (spawn_target_by_heat, rich_rest_fight_mult) so a pace-500 camper's hour pays what it did."),
            X(GroupLoot, KeyBarWork, "0:228,10:124,20:82", "The key bar: Heat-weighted health removed (in standard creatures) for a Vault Key, by Heat (heat:work, log-linear between), never the hourly kill budget, so a faster killer earns more keys. Sized so a middling camper (pace 500, with Gilded) gets about 4.8 / 9 / 14 Vault Keys an hour at Heat 0 / 10 / 20, counting each Snarl's key: with the Snarl caches about 68 / 120 / 180 chest items, 60% / 90% of a Threads hour (about 200 items) at Heat 10 / 20. Empty = no key bar."),
            N(GroupLoot, CorpseRotSeconds, 60, 5, 600, "Seconds a fire monster's corpse lasts with loot in it (empty corpses go in 15 s)."),
            I(GroupLoot, CorpseCap, 40, 5, 1000, "Most corpses per fire; past it the oldest rots in 15 s."),
            N(GroupLoot, FireScale, 2, 0.5, 5, "Size of the campfire, times the retail model (applies when the dungeon next loads)."),

            N(GroupOwnership, AwaySeconds, 600, 10, 7200, "The fire goes out when no owner has been in the dungeon (or online) for this many seconds; your own Heat waits as long after you leave the room."),
            I(GroupOwnership, FireAwaySeconds, 900, 0, 7200, "The fire also goes out when no owner has stood in its zone for this many seconds, even while they are elsewhere in the Fray (0 = off). The Campfire Hearthstone brings an owner back after a death (without this, a dead owner could hold a fire for hours from across the island)."),

            N(GroupRating, RatingHoldMinutes, 15, 1, 240, "Minutes of fighting at a Heat, without dying, for it to become your Heat rating. Only fighting minutes count (see rating_min_cleared)."),
            N(GroupRating, RatingMinCleared, 0.7, 0, 1, "A minute counts toward the rating only when you removed at least this share of the health your own stream spawned that minute, were alive at the fire, and were not in hold mode."),

            I(GroupOwnership, MinLevel, 300, 1, 999, "Lowest character level that may light a campfire."),
            T(GroupOwnership, OnePerIp, true, "Only one character per IP address may hold a campfire at a time."),

            N(GroupEmbers, EmbersNormal, 0.5, 0, 1000, "Embers for killing the room's reference creature, before the Heat multiplier; others give more or less by their health, shared by damage done."),
            N(GroupEmbers, EmbersPerLevel, 0.03, 0, 1, "Embers per kill per Heat level (0.03 = +3%)."),
            N(GroupEmbers, EmbersCampPerMinute, 2, 0, 1000, "Embers per minute to each owner near their lit fire."),
            N(GroupEmbers, EmbersCampRadius, 30, 1, 200, "Metres from the fire an owner must be for the camp Embers."),


            I(GroupWeek, WeekResetDay, 2, 0, 6, "Day of the Frays' weekly reset, UTC (0 Sunday, 1 Monday, 2 Tuesday ... 6 Saturday)."),
            I(GroupWeek, WeekResetHour, 0, 0, 23, "Hour of the weekly reset, UTC."),
            I(GroupWeek, WeekOffset, 0, -1000, 1000, "Testing aid: shifts the week number (and so the affixes and families) by this many weeks."),
            X(GroupWeek, AffixShift, "", "Pins this week's affix (for example HeavyThread or Raging); empty = the rotation."),
            T(GroupWeek, HeatShiftsEnabled, true, "The mechanic affixes (Raging, Grievous, Venomous, Vampiric, Mana Burn, Linked, Quaking): in a week whose affix is one, camps get it from heat_shift_first. Off: those weeks have no affix."),
            I(GroupWeek, HeatShiftFirst, 5, 0, 100, "Heat from which a mechanic affix is on in its week."),
            N(GroupWeek, SurgeEverySeconds, 240, 30, 3600, "Dream Surges: seconds between boons."),
            N(GroupEmbers, EmberWeeklyCap, 0, 0, 10000000, "Embers a character earns at the full rate each week (0 = no cap, the default)."),
            N(GroupEmbers, EmberOverCapRate, 0.25, 0, 1, "Share of the normal Ember rate paid past the weekly cap."),

            X(GroupSnarl, SnarlBarNeed, "0:308,10:242,20:198", "The Snarl bar: Heat-weighted health removed (as the key bar counts it) for one Snarl, by Heat (heat:work, log-linear between). Sized so a middling camper (pace 500) calls 2 / 3 / 4 Snarls an hour at Heat 0 / 10 / 20; a faster killer calls proportionally more. Never the hourly kill budget. Replaces snarl_bar_work (plain work)."),
            N(GroupSnarl, SnarlBarGildedMult, 3, 0, 20, "A Gilded creature fills the Snarl bar this many times as fast as a normal one of the same health (was 2; the bar is not weighted by Heat)."),
            N(GroupSnarl, SnarlSpeedCap, 0.5, -1, 10, "The most every Snarl speed source together adds to the Snarl bar (Snarl Lure, the Snarl boons, Ember Stoke), as a share (+50%, so Snarl's Bane's +35% counts in full; was +25%). Below 0: no cap."),
            N(GroupSnarl, SnarlHealthNormal, 12, 1, 200, "The Snarl's health, times a normal creature's at the same Heat (Snarl Pact normal). Used only when snarl_health_by_heat is empty."),
            X(GroupSnarl, SnarlHealthByHeat, "0:12,14:12,15:16,16:12,17:9.5,18:8,19:6,20:4.8", "The Snarl's health by Heat (heat:mult, linear between), times a normal creature's at the same Heat; solo and group camps alike. A Snarl takes about 2 minutes at Heat 15 to 20 (with heat_curve_health's softer top); before, Snarls took 3.5 to 5 minutes at Heat 16-17, about a third of camp time. Its cache is the same; its kill pays its health as any kill. Empty = snarl_health_normal at every Heat."),
            T(GroupSnarl, SnarlAdds, true, "Snarl adds: on typed solo camps, swarm pack kills fill a second bar; when it is full the Snarl's adds walk out (snarl_adds_count creatures, one of them the Snarl Brood holding a Snarl cache). Clearing them all pays as a Snarl kill. Brute and untyped packs fill the Snarl bar. Off: every kill fills the Snarl bar."),
            I(GroupSnarl, SnarlAddsCount, 6, 2, 20, "How many Snarl adds walk out when the adds bar is full."),
            N(GroupSnarl, SnarlAddsHealth, 1, 0.1, 10, "All the Snarl adds' health together, times one Snarl's at the same Heat (split evenly between them)."),
            N(GroupSnarl, SnarlAddsBarMult, 1, 0, 10, "The adds bar fills this many times as fast as the Snarl bar would from the same kill (the same snarl_bar_need)."),
            N(GroupSnarl, SnarlDamageNormal, 1.1, 1, 10,"The Snarl's damage, times a normal creature's (normal; was 1.3)."),
            I(GroupSnarl, CacheItemsNormal, 10, 1, 20, "Items in a normal Snarl cache (10 is a Threads chest; was 3)."),

            T(GroupSpawn, FamiliesEnabled, true, "On: each Fray spawns the week's creature family (swarm, standard, elite, Snarl). Off: each fire's fixed retail mix."),
            X(GroupSpawn, FrayOrder, "C8E2,D5DF,D5DA", "The active Frays in rotation order, as hex Fray keys: C8E2 Vissidal Coast, D5DF Vissidal Heights, D5DA the Sky Fray (the group camps on the temple's floating plateau; 002B Egg Orchard and 5F47 Elemental Caverns are kept but off). The first gets family 2w, the second 2w+1, the third 2w+2."),
            X(GroupSpawn, FamilyPin, "", "Testing aid: pins every Fray to one family by key (lugian, olthoi, drudge... see /frays week); empty = the weekly rotation."),
            X(GroupSpawn, FamilySource, "threads", "Where the weekly families come from: threads (every Threads species family, retail creatures) or custom (the five original DreamWeave families)."),
            N(GroupSpawn, FamilyCasterWeight, 0.15, 0, 1, "How often a caster comes up next to a melee creature (1), and how much a caster-heavy family counts in the weekly draw."),
            N(GroupSpawn, FamilyArcherWeight, 0.05, 0, 1, "How often an archer (bow, crossbow, thrown) comes up next to a melee creature (1); archers do not chase, so this stays low."),
            I(GroupSpawn, CasterMinHeat, 17, 0, 100, "The lowest Heat a caster comes up or anything casts. Below it caster kinds are left out of the picks and every creature's spell book is emptied at spawn (retail maggots carry void spells). 0 = casters at every Heat."),
            X(GroupSpawn, FamilyExclude, "olthoi_larvae", "Families left out of the weekly rotation at every Heat, comma separated: Threads family keys (olthoi_larvae) or creature types (OlthoiLarvae). The maggots and grubs are left out by default; one family a week stays the rule. The week draw runs over the families left. Empty = every family."),
            X(GroupSpawn, SpawnTargetByHeat, "0:305,10:380,20:455", "Outdoor zones: Heat-weighted work an hour (a creature counts its Heat health, as the key bar) a camper's plain points field at least, by Heat (heat:work, linear between). About 1.3x what a pace-500 camper clears at each Heat, so the route matters at every Heat; solo camps field more for a faster camper (spawn_follow_extra). Replaces spawn_target_per_hour; empty = spawn_target_per_hour at every Heat."),
            N(GroupSpawn, SpawnFollowExtra, 0.3, 0, 5, "Solo outdoor camps: the plain points field what this camper clears of them plus this share of everything it clears (Gilded too), so there are always about 30% more spawns than it can kill and a faster build always has more to kill. Never under spawn_target_by_heat. 0 = spawn_target_by_heat only."),
            N(GroupSpawn, SpawnFollowSeconds, 300, 60, 3600, "Seconds the clear rate spawn_follow_extra reads is averaged over (a running average: a short burst or a slow pull moves it a little). 300 (was 600), so a fast camper's supply catches up in minutes, not an hour."),
            N(GroupSpawn, SpawnTargetPerHour, 600, 0, 5000, "Outdoor zones: standard creatures' worth an hour a camper's zone fields if they clear everything as it comes up (a swarm creature counts swarm_health_share, an elite elite_health_mult), so heavy, medium and light pay the same. Point rests stretch or shrink to hit it at every Heat and shape. 0 = the rests as set."),
            N(GroupSpawn, SwarmHealthShare, 0.4, 0.05, 1, "A swarm creature's health (and Heat, Ember and loot worth) as a share of a standard one's."),
            N(GroupSpawn, SwarmPackShare, 0.5, 0, 1, "Share of pack creatures that are the swarm kind (the rest are standard)."),
            N(GroupLoot, LootItemsPerStandard, 15, 0, 100, "Magic items a standard creature drops on average; swarm creatures in proportion to their health, so one brute drops what a whole swarm pack does. Better than every tier 8 spot, more items than retail mobs (a retail tier 8 mob drops 4 to 5 at most; a swarm creature here about 5 and a standard one 15)."),
            N(GroupLoot, LootOrdinaryPerStandard, 6, 0, 100, "Ordinary (non-magic, salvage) items a standard creature drops on average, sized like the magic ones (a retail tier 8 mob drops 2 at most)."),
            N(GroupLoot, LootMundanePerStandard, 5, 0, 100, "Mundane items (gems, scrolls, food and the like) a standard creature drops on average, sized like the magic ones (a retail tier 8 mob drops 4 at most)."),

            N(GroupLongTerm, BaneSlayerBonus, 1.5, 1, 5, "Slayer damage a Slayer Stone gives (the weapon keeps a higher one if it has it)."),
            N(GroupLongTerm, RerollWeekChance, 0.25, 0, 1, "Chance a Reroll Stone lands on this week's family (when the weapon does not slay it already); otherwise any other family at random."),
            T(GroupLongTerm, VaultBonusEnabled, true, "Ember Vault bonus items (Embers, salvage, a Diamond Scarab, Pyreal Motes) on top of its gear."),
            I(GroupLongTerm, VaultEmbersMin, 5, 0, 1000000, "Ember Vault: fewest Embers a fill (was 50)."),
            I(GroupLongTerm, VaultEmbersMax, 15, 0, 1000000, "Ember Vault: most Embers a fill: about 10 a fill (was 150, when vault Embers were about 80% of Ember income from Heat 10)."),
            N(GroupLongTerm, VaultSalvageChance, 1, 0, 1, "Ember Vault: chance of a full salvage bag of a random material (1 = every fill, as a Threads boss cache)."),
            N(GroupLongTerm, VaultScarabChance, 0, 0, 1, "Ember Vault: chance of a Diamond Scarab (0: a Threads cache has none)."),
            N(GroupLongTerm, VaultMoteChance, 0, 0, 1, "Ember Vault: chance of Pyreal Motes (retail 6353, 1 to vault_motes_max of them). 0: a Threads cache has none."),
            I(GroupLongTerm, VaultMotesMax, 1, 1, 10, "Ember Vault: the most Pyreal Motes one fill adds."),
            I(GroupLongTerm, FireTierMax, 10, 0, 20, "Highest personal campfire tier (each tier is one campfire perk point)."),
            I(GroupLongTerm, FireTierCost, 500, 0, 1000000, "Embers for campfire tier 1; tier N costs N times this."),
            I(GroupLongTerm, AscensionMax, 5, 0, 50, "Most Ascensions a character can make (+1% damage, inside the Frays damage pool, and +1% Embers each)."),
            I(GroupLongTerm, AscensionCost, 5000, 0, 10000000, "Embers for Ascension N: N times this (5,000: 5k, 10k ... 25k, 75k for all five; about 1.7 hours of a Heat 20 camper's Embers for the first). Paid from the pack, then the bank. 0 = free."),
            I(GroupLongTerm, AscensionHeat, 20, 0, 100, "Ascension needs an active camp at this Heat, plus the Ember cost (not just a Heat rating of 20). One a week (ascension_per_week)."),
            I(GroupLongTerm, AscensionPerWeek, 1, 0, 50, "Ascensions a character may make each Frays week (week_reset_day and week_reset_hour). 0 = no limit (with no limit, a strong build finished all five in about 4 hours)."),
            I(GroupLongTerm, BountyEmbers, 300, 0, 100000, "Embers each daily task pays."),
            I(GroupLongTerm, WeeklyTaskEmbers, 1500, 0, 1000000, "Embers each weekly task pays."),
            I(GroupLongTerm, HuntEmbers, 200, 0, 100000, "Embers each daily hunt pays (the Fray Huntmaster's kill tasks)."),
            N(GroupLongTerm, TaskXpPercent, 0.03, 0, 1, "XP each finished task pays on top of its Embers, as a share of the level (the same as the Marae Lassel bounties: 3%, 20M to 100M)."),
            I(GroupLongTerm, TaskXpMin, 20_000_000, 0, 2_000_000_000, "Least XP a finished task pays."),
            I(GroupLongTerm, TaskXpMax, 100_000_000, 0, 2_000_000_000, "Most XP a finished task pays."),
            I(GroupLongTerm, TaskLuminance, 25_000, 0, 10_000_000, "Luminance each finished task pays (the Marae Lassel bounties pay 25,000)."),
            N(GroupLongTerm, RecallCooldown, 120, 0, 3600, "Seconds between recalls to your own fire (/frays recall)."),
            T(GroupLongTerm, DeathAtFire, true, "On: a camper who dies while holding a lit solo campfire respawns beside it instead of at their lifestone, so they do not run back on foot and get stuck. Off: the lifestone."),
            N(GroupLongTerm, SnarlTrophyChance, 0.01, 0, 1, "Chance a Snarl's cache also holds a Snarl Trophy (mansion decor; 1%, was 2%). Each one is listed on the Trophy Wall."),
            N(GroupLongTerm, SnarlBaneChance, 0.25, 0, 1, "Chance a Snarl's cache also holds a Slayer Stone."),
            N(GroupLongTerm, SnarlKindlingChance, 0.1, 0, 1, "Chance a Snarl's cache also holds a Kindling (Kindling is a rare Snarl drop)."),
            N(GroupLongTerm, SnarlScarabChance, 0.05, 0, 1, "Chance a Snarl's cache also holds a Diamond Scarab (retail 7299)."),
            N(GroupLongTerm, SnarlMmdChance, 0.15, 0, 1, "Chance a Snarl's cache also holds MMDs: snarl_mmd_count Trade Notes (250,000) (retail 20630), the same at every Heat (2 at 15%)."),
            I(GroupLongTerm, SnarlMmdCount, 2, 0, 100, "How many Trade Notes (250,000) a Snarl's MMD drop holds."),
            N(GroupLongTerm, SnarlEmberChance, 0.5, 0, 1, "Chance a Snarl's cache also holds snarl_ember_count Embers."),
            I(GroupLongTerm, SnarlEmberCount, 25, 0, 10000, "How many Embers a Snarl's Ember drop holds."),
            N(GroupLongTerm, SnarlShardChance, 0.25, 0, 1, "Chance a Snarl's cache also holds a Sigil Shard (2000193; 10 join into a Lesser Sigil)."),
            N(GroupLongTerm, SnarlAugChance, 0, 0, 1, "Chance a Snarl's cache also holds one item from snarl_aug_wcids (an augmentation gem or stipend item). 0 by default: they drop only from the Great Snarl (great_snarl_aug_chance)."),
            N(GroupSnarl, GreatSnarlAugChance, 0.05, 0, 1, "Chance each player the Great Snarl pays also gets one item from snarl_aug_wcids (a Token of the Augmentation Gem or a Stipend), rolled for each player apart."),
            X(GroupLongTerm, SnarlAugWcids, "44105,46423", "The items snarl_aug_chance picks from, comma separated wcids (default: retail Token of the Augmentation Gem 44105 and Stipend 46423)."),
            I(GroupLongTerm, TrophyWallCount, 10, 0, 50, "How many of the latest Snarl Trophies the Trophy Wall in the Great Hall lists."),
            X(GroupLongTerm, TrophyWallState, "", "The Trophy Wall's saved list (a #total of trophies ever found, then date|finder|family entries, newest first), written by the server; leave it alone. The Snarl Monument reads it too."),
            X(GroupLongTerm, TrophyCountsState, "", "Each player's all-time Snarl Trophy count (name|count|when they reached it), written by the server; leave it alone. Empty: counted from the Trophy Wall's saved list, which keeps only the latest 50. The Champion's stone reads it."),
            X(GroupLongTerm, MonumentSetup, "0x02000642", "The Snarl Monument's model, a hex setup id (0x02000642 the retail Town Statue on its pedestal). Takes effect when it is put up again."),
            X(GroupLongTerm, MonumentStoneSetup, "0x02000290", "The model of each finder's stone round the Snarl Monument, a hex setup id (0x02000290 the retail Historical Marker)."),
            I(GroupLongTerm, MonumentStones, 12, 0, 50, "Stones round the Snarl Monument, each named for one of the latest Snarl Trophy finders. A new trophy takes the stone of the oldest one shown."),
            N(GroupLongTerm, MonumentRingRadius, 4, 1, 30, "Metres from the Snarl Monument to its ring of stones (times the monument piece's size)."),
            T(GroupLongTerm, MonumentChampion, true, "The Champion's stone: one more stone in the Snarl Monument's ring for the player with the most Snarl Trophies ever found (a tie stays with the one who reached the count first). Off: the ring has only the finders' stones."),
            N(GroupLongTerm, MonumentChampionSize, 1.5, 1, 5, "The Champion's stone's size, times the other stones."),
            N(GroupLongTerm, MonumentChampionAngle, 0, -180, 180, "Degrees from the Snarl Monument's facing to the Champion's stone (0 = straight in front of the statue; as /rotate, 90 turns it west of that)."),
            N(GroupLongTerm, ScoreHeatPoints, 10, 0, 1000, "Climb score: points for the peak Heat, times the peak squared (Heat 20 = 4,000 at 10)."),
            N(GroupLongTerm, ScoreKillPoints, 1, 0, 1000, "Climb score: points a kill."),
            N(GroupLongTerm, ScoreSnarlPoints, 50, 0, 10000, "Climb score: points a Snarl killed."),
            N(GroupLongTerm, ScoreGildedPoints, 5, 0, 10000, "Climb score: points a Gilded creature killed."),
            N(GroupLongTerm, ScoreDeathPoints, 25, 0, 10000, "Climb score: points a death costs."),
            N(GroupLongTerm, ScoreTimePoints, 100, 0, 10000, "Climb score: time points for each new Heat reached: all of them for an instant step, half at score_par_minutes, none at twice that."),
            N(GroupLongTerm, ScoreParMinutes, 4, 0.1, 120, "Climb score: the par minutes a Heat step."),
            N(GroupLongTerm, ScoreBronze, 2500, 0, 1000000, "Weekly climb reward: the best climb score of the week for Bronze (about a Heat 13 camp)."),
            N(GroupLongTerm, ScoreSilver, 4500, 0, 1000000, "Weekly climb reward: the score for Silver (reaching Heat 20)."),
            N(GroupLongTerm, ScoreGold, 6000, 0, 1000000, "Weekly climb reward: the score for Gold (Heat 20 reached quickly, with a good haul of Snarls and Gilded)."),
            I(GroupLongTerm, ScoreBronzeKeys, 2, 0, 100, "Vault Keys for Bronze, paid once a week (no Embers)."),
            I(GroupLongTerm, ScoreSilverKeys, 4, 0, 100, "Vault Keys for Silver in all (reaching it after Bronze pays the difference)."),
            I(GroupLongTerm, ScoreGoldKeys, 6, 0, 100, "Vault Keys for Gold in all."),
            X(GroupLongTerm, ScoreTitles, "the Ember Champion,the Ember Vanguard,the Ember Valiant", "The weekly Epithets for the top 3 of the weekly Fray score board (/top fray score week), first to third, comma separated. Held for the next week."),
            X(GroupLongTerm, LucidReward, "snarl", "Lucid Night: on (any value but off) or off. Only on the final night, the last lucid_hours before the weekly reset, and only when lucid_great_snarls Great Snarls fell that week. While it lasts: every Snarl cache holds 2 more items, every camper gets the lucid_damage and lucid_embers buff, and each camper's first kill gives them lucid_cache_keys Vault Keys (bonded)."),
            N(GroupLongTerm, LucidDamage, 0.05, 0, 1, "Lucid Night buff: extra damage for every camper while it lasts (0.05 = +5%)."),
            N(GroupLongTerm, LucidEmbers, 0.10, 0, 5, "Lucid Night buff: extra Embers for every camper while it lasts (0.10 = +10%)."),
            I(GroupLongTerm, LucidCacheKeys, 1, 0, 10, "Lucid Night cache: Vault Keys each camper gets once a week, on their first kill while it lasts (0 = none)."),
            I(GroupLongTerm, LucidGreatSnarls, 25, 1, 10000, "Lucid Night: Great Snarls slain across the server in a week to bring it (counted from Great Snarl kills, not from all health removed). 25 is about the old 50000 bar: a Great Snarl needs about 1850 of work at 7 campers."),
            N(GroupLongTerm, LucidHours, 24, 1, 168, "Lucid Night lasts this many hours before the weekly reset, when the week's Great Snarls reached lucid_great_snarls (24: the last 24 hours before the Tuesday reset)."),
            X(GroupLongTerm, LucidState, "", "Lucid Night's saved count (week|Great Snarls|active|gs), written by the server; leave it alone."),

            T(GroupScaling, LevelScaling, true, "On: every creature is lifted to its camper's level plus the Heat curve's levels (Threads' stat and health curves). Off: creatures keep their own level."),
            T(GroupScaling, ThreadsShape, true, "On: a creature from a Threads family is picked and built the way a Thread builds its own at the camper's level plus the Heat's levels (band and archetype standards for attack, defense, damage, ratings and resists; the damage ceiling); Frays health, Heat, elite and Snarl numbers go on top. Off, or a family Threads cannot field at that level: the stat curve lift only (which leaves Frays creatures far weaker than Thread creatures)."),
            T(GroupScaling, ThreadsShapeHeatLevels, false, "Off: a Threads-shaped creature is built at the camper's own level (a Thread you would run yourself), and Heat makes it tougher through heat_curve_health and heat_curve_damage. On: built at the camper's level plus the Heat's levels (heat_curve_levels) with no Heat damage curve, so Heat 13 at level 440 fields L477 Thread creatures."),
            I(GroupScaling, ThreadsShapeMaxLevel, 410, 1, 500, "The highest level a Threads-shaped creature is built at: 410, the highest Thread a player can run (a 375 gem pressed with a Mana Scarab). Above it Threads' per-hit damage climbs steeply (x4.9 from 300 to 410, x14.7 by 500), so a level 440 camper fights 410 Thread creatures."),
            N(GroupScaling, HeatLevelsPerHeat, 3, 0, 50, "Creature levels added above the camper's level for each Heat level."),
            N(GroupScaling, DefenseCap, 200, 0, 5000, "Highest melee, missile and magic defense (effective skill) a Frays creature has at level 300, so every family can be hit and an unattended camp can kill what it meets. 0 = no cap (each creature keeps its own). 200 (was 450), so they do not evade or resist."),
            N(GroupScaling, DefenseCapPerLevel, 1.2, 0, 20, "How much defense_cap rises (or falls) for each creature level above (or below) 300."),
            N(GroupScaling, AttackVsDefense, 0, -200, 400, "A creature swinging at its camper attacks at no less than the camper's own defense plus this (0 = about half its swings land; without it, high-defense builds dodged 80-90% of Threads-level attacks, then took rare big hits). Off: below -199."),
            N(GroupScaling, AttackVsDefensePerHeat, 3, 0, 50, "Added to attack_vs_defense for each Heat the hit counts (3 a Heat: about 86% of swings land at Heat 20)."),
            I(GroupScaling, HealthCamperLevelCap, 375, 0, 1000, "Creature health reads the health curve at the camper's level up to this one, plus the Heat's added levels (0 = no cap). 375 is the top of the curve's measured data; past it the curve was only extrapolated (uncapped, a level 440 camper met 33,000-health creatures at Heat 0)."),
            X(GroupScaling, HeatCurveLevels, "0:0,5:10,10:25,15:45,20:70", "Creature levels added above the camper's level, by Heat (heat:levels pairs, straight lines between, the last stretch carried on). Empty = heat_levels_per_heat x Heat."),
            X(GroupScaling, HeatCurveHealth, "0:0.7,5:0.9,10:1.2,12:1.25,15:1.3,18:2.05,20:3", "Extra creature health multiplier by Heat, heat:value pairs, on top of Threads' health curve. More, softer creatures at high Heat, x3 at Heat 20 (was x6), x2.05 at 18 (4), x1.3 at 15 (2.4), so kills an hour about double at Heat 15 to 20. Every kill pays by its Heat health (kill_pay_by_health, loot_by_heat_health) and supply and Heat gain are Heat-weighted, so XP, Embers, keys, Snarls and loot an hour stay the same. Below 1 at low Heat, so Heat 0 is easier than a Thread you would run yourself (a camp is half-attended, with several creatures on you at once); back to Thread strength by about Heat 8 and as hard as before at 20. Empty = none."),
            X(GroupScaling, HeatCurveDamage, "0:0.6,5:0.8,10:1.0,12:1.1,15:1.4,18:1.9,20:2.4", "Extra creature damage multiplier by Heat, heat:value pairs, on top of Threads' stat curve: 0.6 at Heat 0, a Thread's own damage at Heat 10, x2.4 at 20 as before, so a decent camper can hold Heat 10-12. Empty = none."),
            X(GroupScaling, HeatCurveHitCap, "0:0.15,10:0.25,20:0.4", "The most one creature hit can take, as a share of the camper's max health, by Heat (heat:share pairs). Without it, a level 440 with about 450 health could lose most of it to one 410 hit at Heat 0, before a heal mattered. 0.15 = no hit above 15% of max health at Heat 0, 25% at 10, 40% at 20. Several hits can still land between heals. Empty = no cap."),
            T(GroupScaling, HitByHealth, true, "On: a creature's weapon hit on a camper is a share of the camper's max health (heat_curve_hit_share), so it matters at any level and armor (otherwise a level 444's armor let through 0.4% of a hit, so there was no danger). Off: the hit is the creature's own, after armor, times heat_curve_damage."),
            X(GroupScaling, HeatCurveHitShare, "0:0.03,5:0.05,10:0.08,15:0.12,20:0.18", "A creature weapon hit as a share of the camper's max health, by Heat (heat:share pairs), before armor (hit_armor_max_cut), elite and Snarl boosts and the camper's cuts; +/-20% each hit. Used with hit_by_health."),
            N(GroupScaling, HitArmorMaxCut, 0.5, 0, 1, "With hit_by_health: the most armor and resists can cut from a creature hit (0.5 = the best armor halves it; 0 = armor does nothing)."),
            N(GroupScaling, SpellHitShare, 1.0, 0, 5, "With hit_by_health: the most a creature spell or drain can take, as this times heat_curve_hit_share of the camper's max health (+/-20% each hit), before elite and Snarl boosts, the hit cap and the camper's cuts. A weak spell keeps its own smaller hit. Without it, lightning bolts took 30 to 49% of health while weapon hits took 4%, and caused most deaths. 0 = off (spells hit up to heat_curve_hit_cap)."),
            N(GroupScaling, DamageReductionCap, 0.35, 0, 0.95, "The most the Frays reduction pool can cut from a hit on a camper. The pool adds: Ash Guard (up to 10%), the warder companion (10%), the shrine's Ward (5%) and boons (Ember Skin, Iron Ember: boon_bonus_cap together), so all of them maxed is exactly 35%. Proof sets and Bulwark are outside it."),
            N(GroupScaling, DamageBonusCap, 0.5, 0, 5, "The most the Frays damage pool adds to a camper's hit. The pool adds: Searing (up to 10%), Ascension (5%), the striker companion (10%), the shrine's Fury (5%), Emberblessed (5%), Lucid Night (5%) and boons (boon_bonus_cap against any one kind of target), so all of them maxed is exactly +50%. The Slayer Stone, class abilities, ratings and gear are outside it."),
            N(GroupScaling, BloodSplashDamage, 0.6, 0, 2, "Crimson Splash (a Blood Mage boon): a splashed Bloodstone Bolt or Hecatomb hit, times the aimed hit's damage. A splashed drain is a full drain, as Crimson Harvest's."),
            N(GroupScaling, HecatombReachDamage, 0.75, 0, 2, "Hecatomb's Reach (a Blood Mage boon): each extra creature a Martyr's Hecatomb or Curse of Raven Fury strikes, times the aimed hit's damage."),
            N(GroupScaling, BloodBoonRadius, 8, 0, 30, "Crimson Splash and Hecatomb's Reach: how far from the struck creature, in metres, the extra creatures can be."),
            N(GroupScaling, BoonBonusCap, 0.10, 0, 1,"The most boons add to one hit, inside the pools: boon damage against any one kind of target (packs, Gilded, the Snarl), and boon reduction."),
            X(GroupXp, HeatCurveIncome, "0:0.435,10:0.5375,20:0.64", "XP and lum bonus by Heat (1.0 = +100%). A camper at a steady pace (pace 500, Gilded or not) earns 70% / 75% / 80% of a solo Threads hour at Heat 0 / 10 / 20 at every level band (was 70 / 80 / 90, too close to Threads), with xp_by_level (20% of Threads), rich_kill_pay_mult 1 and xp_kill_cap_by_heat. Used only with heat_pay_curve table."),
            X(GroupXp, HeatPayCurve, "table", "How Heat raises XP and lum (the default, table, is a fixed curve): threads (Threads' XP ladder at the creature level the Heat sets, over the ladder at the camper's level) or table (heat_curve_income)."),
            I(GroupHeat, BoonEvery, 3, 0, 10, "Ember Boons: a choice of three run-only boons every this many Heat levels (0 = off). 3: Heat 3, 6, 9 ... 18, six a climb to 20 (was 2)."),
            I(GroupHeat, BoonAutoSeconds, 30, 5, 600, "Ember Boons: seconds a player has to pick (/fp 1, 2 or 3) before the server picks from their boon plan."),
            I(GroupHeat, BoonVoteSeconds, 20, 5, 600, "Group camps: seconds the members have to vote on an Ember Boon (/fp 1, 2 or 3). A tie or no votes: the keeper's boon plan picks."),
            I(GroupHeat, BoonVoteActiveSeconds, 120, 10, 3600, "Group camps: only members who fought this recently (seconds) vote on a boon, so an away member never holds it up."),
            I(GroupHeat, RareBoonHeat, 10, 0, 50, "Ember Boons: rare boons are offered from this Heat."),
            T(GroupHeat, EliteAffixes, true, "On: every Gilded creature carries the week's affix: it resists one damage type and is weak to its opposite (Fireward, Frostward, Acidward, Stormward, Bladeward, Spikeward, Maceward): a test of the gear brought."),
            N(GroupSnarl, EffigySnarlChance, 0.05, 0, 1, "Chance a Snarl leaves a Fray Effigy of the week's family."),
            N(GroupSnarl, EmberblessedMinutes, 30, 0, 240, "Minutes of Emberblessed (+10% Embers, +5% damage) for every camper the Great Snarl pays; 0 = off."),
            T(GroupHeat, CenterText, true, "On: Heat levels, the Snarl and the Great Snarl also show as center-screen text (chat lines scroll away under combat)."),
            T(GroupHeat, BoonPopup, true, "On: an Ember Boon offer also opens a window listing the three boons. It closes itself when the boon is taken (/fp, the boon plan or the vote) or after 30 seconds; if another yes/no box is already up it falls back to a plain popup that stays till dismissed."),
            T(GroupHeat, HeatEffects, true, "On: a flame swirl on the player at each Heat level (a bigger flash every 5, the campfire flaring), a down-swirl when Heat drops."),
            I(GroupHeat, HeatUpEffect, 3, 1, 7, "The look played on a player when their Heat goes up: 1 old orange swirl, 2 retail level-up burst, 3 aetheria level-up glow, 4 gout of flame, 5 orange shield flare, 6 destruction surge, 7 level-up burst and flame. /frays admin heatfx <n> previews one."),
            N(GroupSnarl, GreatSnarlFeederShare, 0.05, 0, 1, "Share of the Great Snarl's bar a camper must have fed to get its cache without striking it (0 = only hitters). The top 3 feeders get one more item."),
            X(GroupXp, XpByLevel, "210:0.358e9,230:0.526e9,250:0.766e9,270:1.02e9,290:1.476e9,310:1.632e9,330:2.9e9,350:2.98e9,370:3.74e9,390:5.48e9,410:6.18e9", "Heat-0 XP an hour by character level, level:xp pairs (straight lines between them on a log scale, flat past both ends). Each point is 20% of the prod solo Threads XP per member-hour for its 20-level gem band, at the band middle (Threads solo about 7.77 billion at level 300, 30.9 billion at 410 and up; was 25%). With heat_curve_income and the kill pay a middling camper earns 70% / 75% / 80% of Threads at Heat 0 / 10 / 20. Empty = xp_per_hour and the XP curve."),
            X(GroupXp, LumByLevel, "210:49.4e3,230:77.4e3,250:106.4e3,270:142.2e3,290:212e3,310:290e3,330:500e3,350:500e3,370:806e3,390:1.156e6,410:1.388e6", "Heat-0 luminance an hour by character level, like xp_by_level: 20% of prod solo Threads lum per member-hour by gem band (was 25%), so lum lands the same shares as XP. Empty = lum_per_hour."),
            N(GroupXp, XpKillShare, 0.7, 0, 1, "Share of the XP and lum an hour paid per kill instead of per active minute. A kill pays by the creature's health in standard creatures, shared by damage done, with the Heat multiplier (a Gilded creature rich_mult times). 0 = all per minute, as before."),
            N(GroupXp, XpKillPace, 164, 1, 10000, "Standard creatures an hour the per-kill XP and lum are sized to: a camper killing this many an hour earns what the per-minute rate alone paid (164)."),
            X(GroupXp, XpKillCapByHeat, "0:4.03,10:3.74,20:3.48", "xp_kill_cap by Heat (heat:cap, log-linear between), in hours of xp_kill_share. Sized so a fast camper (pace 1000 and up) tops out at about 93% of a solo Threads hour at every Heat (was 98-99%), falling as heat_curve_income climbs. Empty = xp_kill_cap at every Heat."),
            N(GroupXp, XpKillCap, 1.25, 0, 20, "Most per-kill XP and lum a camper earns an hour, in hours of xp_kill_share at their level and Heat, so Frays rates stay under Threads (uncapped, one camper killed 14 times the pace at Heat 17 and earned about 145 billion an hour). Up to 10 minutes of it banks for a burst. 0 = no cap."),
            T(GroupLoot, LootByHeatHealth, true, "On: corpse loot is sized by Heat-weighted work, as the key bar, so a creature at higher Heat (more health) drops more and items an hour rise with Heat. Off: by plain work (items fell from about 2,600 to 600 an hour from Heat 0 to 20)."),
            T(GroupXp, KillPayByHealth, true, "On: kill pay (XP and lum per kill, Embers per kill, the key bar; not the Snarl bar) counts health removed in Heat 0 standard creatures, so a creature with 8 times the health at Heat 20 pays 8 times and a steady damage rate pays more at higher Heat. Off: every creature counts as its kind, whatever the Heat (the old behaviour)."),

            N(GroupSnarl, GreatSnarlWork, 300, 1, 100000000, "Health removed by a whole Fray together (in reference creatures) to call the Great Snarl: this, plus great_snarl_work_per_camper for each camper in the Fray now (was 100, so the bar is about 3x)."),
            N(GroupSnarl, GreatSnarlWorkPerCamper, 750, 0, 1000000, "Extra health (in reference creatures) the Great Snarl's bar needs for each camper in the Fray now, so a small group fills it about as fast as a big one (was 250; at an average 164 an hour that is one Great Snarl about every 4.6 hours, about 5 a day)."),
            N(GroupSnarl, GreatSnarlGapHours, 4, 0, 168, "The fewest hours between two Great Snarls. A bar that fills sooner waits, full, until the gap is over (0 = no gap)."),
            N(GroupSnarl, GreatHoldMinutes, 20, 0, 240, "Minutes camps are still held after the Great Snarl dies, for the walk back (was away_seconds, 10). While it is due or up they are always held."),
            N(GroupSnarl, GreatTimeoutMinutes, 15, 0, 240, "Minutes the Great Snarl stays once it walks in. Not dead by then, it leaves: no one is paid, its bar keeps great_leave_keep of its progress and the camps are released (0 = it never leaves)."),
            N(GroupSnarl, GreatLeaveKeep, 0.5, 0, 1, "Share of a full bar the Great Snarl's bar keeps when it leaves unbeaten (0.5 = half full)."),
            N(GroupSnarl, GreatLeaveHoldMinutes, 0, 0, 240, "Minutes camps are still held after the Great Snarl leaves unbeaten (0 = released at once)."),
            N(GroupSnarl, GreatSnarlWarnSeconds, 60, 0, 600, "Seconds between the Great Snarl's bar filling (the Fray is told) and it walking in."),
            I(GroupHeat, HeatProgressStep, 25, 0, 100, "A player is shown their Heat bar each time it fills this many percent more (0 = only on a new Heat level)."),
            N(GroupSnarl, GreatSnarlHealth, 60, 1, 10000, "The Great Snarl's health, times a standard creature's at the campers' level."),
            N(GroupSnarl, GreatSnarlScale, 2.5, 0.5, 10, "The Great Snarl's size, times its creature's (was 2)."),
            N(GroupSnarl, GreatPortalSeconds, 120, 0, 3600, "Seconds the portal back to town stands where the Great Snarl fell (0 = no portal)."),
            I(GroupSnarl, GreatPortalWcid, 2000154, 0, 99999999, "The portal that opens where the Great Snarl falls (default: the Vissidal Fray return portal to the temple)."),
            I(GroupSnarl, GreatSnarlCacheItems, 3, 1, 20, "T8 items each Great Snarl hitter gets (the best one attuned)."),
            N(GroupSnarl, FrayGoalWork, 40000, 1, 100000000, "The Frays' weekly goal: health removed by every camp together (in reference creatures); met, every camper of the week is paid (fray_goal_keys, fray_goal_cache_items, fray_goal_task_reward)."),
            I(GroupSnarl, FrayGoalKeys, 15, 0, 50, "Vault Keys each camper of the week gets when the Frays' weekly goal is met (15: about an hour and a half of Heat 10 key bar)."),
            I(GroupSnarl, FrayGoalCacheItems, 10, 0, 20, "T8 items each camper of the week gets when the weekly goal is met (the best one attuned, as a Snarl cache; 0 = none)."),
            I(GroupSnarl, FrayGoalSlayerStones, 1, 0, 10, "Slayer Stones each camper of the week gets when the weekly goal is met."),
            I(GroupSnarl, FrayGoalKindling, 1, 0, 10, "Kindling each camper of the week gets when the weekly goal is met."),
            T(GroupSnarl, FrayGoalTaskReward, true, "The weekly goal also pays a task-giver's XP and luminance (task_xp_percent, task_luminance), fray_goal_task_mult times over."),
            N(GroupSnarl, FrayGoalTaskMult, 3, 0, 20, "The weekly goal's task XP and luminance, times one task's (its 20M to 100M range too)."),
            X(GroupSnarl, FrayState, "", "The Frays' saved shared bars (written by the server; leave it alone)."),

            N(GroupSpawn, SpawnNearRadius, 0, 0, 300, "Outdoor zones: packs wait at rested points within this many metres of their camper (0 = anywhere in the zone, the default: you route between them and pull)."),
            N(GroupSpawn, SpawnRegrowSeconds, 180, 0, 3600, "Outdoor zones: seconds a cleared spawn point rests before it spawns for that camper again, so standing still dries up."),
            I(GroupSpawn, NestPackBonus, 2, 0, 20, "Outdoor zones: extra creatures in a nest's pack (every 5th spawn point is a nest)."),
            N(GroupSpawn, NestRestSeconds, 360, 0, 3600, "Outdoor zones: seconds a cleared nest rests."),
            N(GroupSpawn, QuickRestSeconds, 90, 0, 3600, "Outdoor zones: seconds a cleared quick point (every 3rd point, one creature) rests."),
            N(GroupSpawn, HotSpotMinutes, 10, 0, 240, "Outdoor zones: every this many minutes a different quarter of each zone flares (its points rest half as long and field one more creature); 0 = off."),
            N(GroupSpawn, AggroRange, 10, 5, 100, "Outdoor zones: metres at which every Fray creature notices its owner, the same for every family so a pull plans the same each week. Keep it well under the gap between packs (40 m on solo camps) so players choose their pulls (at 25 m, walking up to one pack pulled its neighbours too)."),
            T(GroupSpawn, FixedLayout, true, "Solo outdoor camps: every spawn point keeps one role for good (nests, packs, quick points, 2 rich points, the shrine), the same every visit and every week, so one recorded route keeps working. Off: the old weekly turn of the layout."),
            N(GroupSpawn, LayoutGapAggro, 4, 1, 10, "Solo outdoor camps: the fewest metres between any two spawn points, in aggro ranges (4 x aggro_range 10 = 40 m, a 20 m lane between two packs' aggro; was 2.5), so the lanes between packs stay clear and a pull never wakes a neighbour. A point too close moves to the nearest free spot, or is switched off."),
            I(GroupSpawn, LayoutPoints, 10, 0, 40, "Solo outdoor camps: spawn points kept (fewer points, bigger packs). The widest spread ones stay, the rest are switched off; rests still scale to spawn_target_per_hour, so pay an hour holds. 0 = every point that fits."),
            I(GroupSpawn, LayoutPackBonus, 2, 0, 10, "Solo outdoor camps: extra creatures in every pack and nest (not stragglers), on top of Heat growth, so fewer points still fill an hour. Its rests grow to match."),
            N(GroupSpawn, LayoutRestMult, 0.85, 0.2, 2, "Solo outdoor camps: point rests times this on the fixed layout (not Gilded points). With fewer points each one waits longer for you to come back, so 0.85 keeps a fast camper's kills an hour where the old 20-point layout had them."),
            T(GroupSpawn, LayoutEdges, true, "Solo outdoor camps: the 2 Gilded points and the Ember Shrine stand on the zone's rim, the Gilded 120 degrees apart and the shrine 90 degrees from each, so a route has to choose. Off: they may stand anywhere."),
            T(GroupSpawn, PackLink, true, "Outdoor zones: hit or wake one creature of a pack and its whole pack comes, never a neighbouring pack, so each pull is one predictable group. Snarls keep their own rules."),
            I(GroupSpawn, RichPoints, 4, 0, 5, "Solo outdoor camps: rich points per camp, alternating brute and swarm (two of each along the route; was 2, one of each). Their packs are Gilded (gold glow, \"Gilded\" in the name) and pay rich_mult times the Embers and key bar."),
            N(GroupSpawn, RichMult, 3, 1, 20, "A Gilded creature's Embers, key bar and Snarl-free extras, times a normal one's (not its XP and lum: rich_kill_pay_mult)."),
            N(GroupXp, RichKillPayMult, 1.0, 0, 20, "A Gilded creature's per-kill XP and lum, times a normal one's, and what it spends from the hourly kill budget (lowered from rich_mult's x3 so XP climbs with Heat by heat_curve_income for everyone, Gilded or not)."),
            T(GroupSpawn, PackTypes, true, "Solo outdoor camps: every pack point is a brute point (brute_pack_counts tough creatures, for single-target builds) or a swarm point (swarm_pack_counts swarm creatures, for AoE builds), for good, alternating round the camp; one Gilded point of each. A brute pack holds the same total health as a swarm pack at the same Heat, so both pay the same. No layout_pack_bonus, no nests or quick points. Off: the mixed packs."),
            X(GroupSpawn, SwarmPackCounts, "0:6,10:7,20:8", "Swarm points: swarm creatures a pack, by Heat (heat:count, the highest step at or under it). A pack's total health is this times swarm_health_share; a brute pack holds the same."),
            X(GroupSpawn, BrutePackCounts, "0:1,10:2", "Brute points: tough creatures a pack, by Heat (heat:count). Each holds the swarm pack's total health over this count."),
            N(GroupSpawn, BruteScale, 1.3, 1, 2, "A brute point's creatures stand this much bigger than their kind (slightly bigger, so a brute is seen at a glance; 1.2 did not show)."),
            N(GroupSpawn, SwarmHitShare, 0.4, 0.05, 1, "A swarm creature's weapon hit, times a normal one (as its 0.4 health), so a big swarm pull costs about the same damage as a brute pull."),
            T(GroupSpawn, BruteHitByShare, true, "On: a brute's weapon hit is times its health share (a 2.4 share brute hits 2.4 times), so a brute pull costs the same damage as a swarm pull of the same health. Off: brutes hit as a standard creature."),
            T(GroupSpawn, BruteHitByPack, true, "On, so brutes and swarms take equal time to kill: a brute hits swarm_hit_share x (swarm count + 1) / (brute count + 1), so a brute pull killed one at a time costs the same damage as a swarm pull of the same health (about 1.07 a brute at Heat 10, 1.4 a lone brute at Heat 0). Over brute_hit_by_share. Off: brute_hit_by_share."),
            N(GroupSpawn, PackTypeSupply, 0.5, 0.05, 5, "Typed packs: the share of spawn_target_per_hour one type's points field on their own (brute or swarm). 0.5 = both types together field the target. Higher: each build's own loop has enough creatures that it never waits on rests."),
            T(GroupSpawn, PackTypeFollow, true, "Typed packs: on, brute points keep up with how fast this camper clears brutes and swarm points with how fast it clears swarms, each never under pack_type_supply of the Heat's target. Off: one supply for both, so a camper clearing only one type gets half the target."),
            N(GroupSpawn, PackTypeFollowSeconds, 90, 10, 3600, "pack_type_follow: seconds each type's clear rate is averaged over. Shorter than spawn_follow_seconds, so the points catch up within a loop or two."),
            N(GroupSpawn, PackTypeFollowExtra, 0.6, 0, 5, "pack_type_follow: each type's points field what this camper clears of that type plus this share (0.6 = 60% more), so a point is back before the camper comes round its loop."),
            N(GroupSpawn, PackTether, 15, 0, 60, "Solo outdoor camps: metres a pack's creatures may go from their own spawn point; pulled further they walk home and heal, as the fire leash. With 10 m aggro and 40 m spacing a fight never reaches a neighbour's aggro. 0 = no tether."),
            N(GroupSpawn, PackmasterPackShare, 0.10, 0, 1, "The Packmaster boon: the share more creatures an hour it gives, as Bellows (chance this times a pack's size of one more creature), not in the rest scaling."),
            N(GroupSpawn, BellowsPackShare, 0.10, 0, 1, "Bellows: the share more creatures an hour it gives, at any Heat. Each pack gets one more creature with chance this times its size; not in the rest scaling, Gilded packs never."),
            N(GroupSpawn, RichLootMult, 2, 1, 10, "A Gilded creature's loot (magic, ordinary and mundane item chances), times a normal one's."),
            N(GroupSpawn, RichRestSeconds, 210, 10, 3600, "Seconds a cleared rich point rests at least (the floor under rich_rest_fight_mult; not scaled by spawn_target_per_hour or hot spots)."),
            N(GroupSpawn, RichRestFightMult, 6, 0, 50, "A cleared Gilded pack's point rests this times how long the pack took to die (its first hit to its last kill), never under rich_rest_seconds. Gilded take about a third of a fast camper's time at every Heat; with 2 Gilded points each rests about 6 of its fights. 0 = rich_rest_seconds flat."),
            N(GroupSpawn, RichWarnSeconds, 15, 0, 120, "Seconds before a rich point comes back that a gold glow shows at its spot (0 = no warning)."),
            T(GroupSpawn, ShrineEnabled, true, "Solo outdoor camps: an Ember Shrine stands at one fixed point (seen by the camp's owner only). Walk in or use it for its buff (Ember Fury, or the week's with shrine_rotate)."),
            N(GroupSpawn, ShrineBuffSeconds, 180, 10, 3600, "Seconds the Ember Shrine's buff lasts (180, was 120: as long as shrine_cooldown_seconds, so one trip each cooldown keeps it up; at 120 s and +5% a trip cost more than it gave)."),
            T(GroupSpawn, ShrineRotate, false, "On: the shrine's buff turns weekly through Ember Fury, Ward, Stoke and Mending. Off: Ember Fury every week, since a less-damage-taken or healing week is worth nothing to a build that is not dying."),
            N(GroupSpawn, ShrineCooldownSeconds, 180, 10, 3600, "Seconds before an Ember Shrine works again for its camper (counted from when it was taken)."),
            N(GroupSpawn, ShrineRadius, 3, 1, 20, "Metres from the Ember Shrine that count as walking into it."),
            X(GroupSpawn, ShrineSetup, "0x02001025", "The Ember Shrine's model, a hex setup id (0x02001025 the retail Smoldering Brazier)."),
            T(GroupSpawn, FillOnTake, true, "On: when a camp is taken, every point fields its pack at once (Gilded rich points too), through the spawn timer and the alive cap. After that the points rest and come back as usual."),
            N(GroupSpawn, RetakeCooldownSeconds, 300, 0, 3600, "Seconds after a camp ends (you gave up the fire, left its fellowship or were away too long) before you can take or join a campfire again, so leaving is no escape from a hard camp (0 = none). Not counted when staff put the fire out or the dungeon unloaded."),
            X(GroupGeneral, SessionLog, "frays-sessions.jsonl", "Each finished camp, one JSON line: seconds fighting, healing or buffing, looting, walking, idle and dead, damage dealt, kills, Heat and a build snapshot. Relative to the server folder. Empty: off."),
            N(GroupSpawn, ShrineScale, 1.5, 0.1, 20, "The Ember Shrine's size, times the model's own."),
            N(GroupSpawn, ShrineFury, 0.10, 0, 5, "Ember Fury: extra damage in the Frays damage pool, as a share (0.10, with the buff lasting as long as its cooldown; was 0.05, its share of the +50% pool)."),
            N(GroupSpawn, ShrineWard, 0.05, 0, 0.9, "Ember Ward (a shrine week): less damage taken in the Frays reduction pool, as a share (0.05, its share of the 35% pool)."),
            N(GroupSpawn, ShrineStoke, 0.25, 0, 10, "Ember Stoke (a shrine week; replaces Ember Bounty): faster Snarl bar for the shrine buff's time, as a share. Counts toward the shared Snarl speed cap."),
            N(GroupSpawn, ShrineMending, 0.01, 0, 0.5, "Ember Mending (a shrine week): share of max health healed every 10 seconds (was 0.03)."),
            I(GroupGeneral, RadarFreeColor, 3, 0, 16, "Radar blip colour of a free campfire (3 white; 1 blue, 2 gold, 4 purple, 5 red, 6 pink, 7 green, 8 yellow, 9 cyan, 16 bright green)."),
            I(GroupGeneral, RadarLitColor, 5, 0, 16, "Radar blip colour of a lit campfire (5 red)."),
            X(GroupGeneral, BeaconMode, "free", "Which campfires show a beacon: off, free (unlit, so open camps are easy to find), lit, or all. Needs beacon_setup."),
            X(GroupGeneral, BeaconSetup, "", "The beacon's model, a hex setup id (0x02......); empty = no beacon. Try them first with /frays admin beacon."),
            N(GroupGeneral, BeaconScale, 4, 0.1, 50, "The beacon's size, times the model's own."),
            I(GroupGeneral, FrayRealm, 1, 0, 32767, "The realm the Frays run in (1: the Drift copy of the world, like Drift Marae Lassel). Its copy of each Fray landblock gets the campfires; the base world stays retail. Applies when a landblock next loads."),
            T(GroupGeneral, WispsEnabled, true, "Outdoor zones: a ring of fire wisps marks the edge of a lit zone (zone_radius), seen only by its owner."),
            X(GroupGeneral, WispSetup, "0x02000899", "The wisps' model, a hex setup id (0x02000899 is the retail Fire Wisp; 0x0200059A the Ethereal Wisp)."),
            N(GroupGeneral, WispScale, 1.6, 0.1, 20, "The wisps' size, times the model's own."),
            N(GroupGeneral, WispHeight, 2, 0, 30, "Metres above the ground the wisps float."),
            N(GroupGeneral, WispSpacing, 6, 2, 50, "Metres between the wisps of a zone's ring (6: about 90 wisps on a 90 m ring, close enough to read as one line)."),
            N(GroupGeneral, ZoneRadius, 90, 30, 94, "Outdoor zones: the zone is a circle of this many metres round the landblock's centre. Its creatures stay inside, only campers inside it count as present, spawn points sit at least 18 m inside, and the wisp ring marks it."),
            I(GroupOwnership, GroupMinSize, 3, 1, 9, "Group camps (the Sky Fray plateau): the fewest fellowship members, online and on different connections, that may light a group fire. Under it for away_seconds, the fire goes out."),
            N(GroupOwnership, LfgAnswerSeconds, 60, 10, 600, "Group finder (/fray lfg): seconds players have to answer when a group is ready."),
            N(GroupOwnership, LfgQueueMinutes, 60, 1, 1440, "Group finder: minutes a player waits in the queue before it drops them."),
            I(GroupOwnership, GroupMinSizeMiddle, 5, 1, 9, "The Sky Fray's middle camp (Sky Hearth): the fewest fellowship members that may light it (the wings use group_min_size)."),
            N(GroupScaling, GroupHealthPerMember, 0.5, 0, 5, "Group camps: extra creature health for each member present past the first (0.5: a 5-member group fights creatures with three times the health)."),
            N(GroupScaling, GroupPackPerMember, 1, 0, 5, "Group camps: extra creatures in each pack for each member present past the first (rounded down)."),
            N(GroupScaling, GroupRatePerMember, 0.5, 0, 5, "Group camps: spawn points refill faster, and more creatures may stand at once, by this for each member present past the first."),
            I(GroupHeat, GroupDeathDrop, 2, 0, 10, "Group camps: levels of the group's shared Heat a member's death costs (2, the same as a solo death)."),
            X(GroupGeneral, VillageLayout, "", "The Fray village and arrival NPCs as arranged in game with /frays admin piece (JSON, written by that tool). Empty or [] = the built-in layout."),
            X(GroupGeneral, GroupRuns, "", "The group run board (JSON, written by the server): one entry per group camp run with its members, peak Heat and score. /top fray groupruns."),
            X(GroupGeneral, HouseCovers, "", "Hand fixes to the town houses' door and window covers, made in game with /frays admin cover (JSON, written by that tool; each applies to every copy of that house). Empty = the built-in fixes."),


            N(GroupGeneral, PlacementGap, 0, 0, 500, "Smallest allowed gap between two fires' leash zones for /frays admin check (0 = the roster's largest detection range)."),
            X(GroupGeneral, FireOverrides, "", "Per-fire leash overrides: fireId:key=value,key=value;fireId:... (keys leash_margin, leash_max_chase, leash_grace_seconds, leash_return_speed, leash_return_heal). Written by /frays admin fire."),
        };

        private static readonly Dictionary<string, FraysSetting> byKey = All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> Groups => All.Select(s => s.Group).Distinct();

        /// <summary>Test seam: the raw text of a string property, or null when it is not cached.</summary>
        internal static Func<string, string> StringSource = CachedText;

        /// <summary>
        /// The live value of a setting as text, from its TYPED server property: a toggle is a bool property, an integer a
        /// long, a number a double and text a string, so the admin Settings page shows each with its own field. Only the
        /// property cache is read (every registered key is cached from boot), never the database.
        /// </summary>
        private static string CachedText(string property)
        {
            var key = property != null && property.StartsWith(Prefix, StringComparison.Ordinal) ? property.Substring(Prefix.Length) : property;

            if (key == null || !byKey.TryGetValue(key, out var s))
                return PropertyManager.TryGetCachedString(property, out var other) ? other : null;

            switch (s.Kind)
            {
                case FraysSettingKind.Toggle:
                    return PropertyManager.TryGetCachedBool(property, out var b) ? (b ? "1" : "0") : null;
                case FraysSettingKind.Integer:
                    return PropertyManager.TryGetCachedLong(property, out var l) ? l.ToString(CultureInfo.InvariantCulture) : null;
                case FraysSettingKind.Number:
                    return PropertyManager.TryGetCachedDouble(property, out var d) ? d.ToString("R", CultureInfo.InvariantCulture) : null;
                default:
                    return PropertyManager.TryGetCachedString(property, out var t) ? t : null;
            }
        }

        /// <summary>The server property type a setting is stored as (PropertyAdminService.TypeBool and so on).</summary>
        public static string PropertyType(FraysSetting s) => s.Kind switch
        {
            FraysSettingKind.Toggle => PropertyAdminService.TypeBool,
            FraysSettingKind.Integer => PropertyAdminService.TypeLong,
            FraysSettingKind.Number => PropertyAdminService.TypeDouble,
            _ => PropertyAdminService.TypeString,
        };

        /// <summary>A value for a setting as its typed property expects it (true/false, a number, or the text).</summary>
        public static string Encode(FraysSetting s, double value) =>
            s.Kind == FraysSettingKind.Toggle ? (value != 0 ? "true" : "false") : Format(value);

        /// <summary>A setting's code default, encoded for its typed property.</summary>
        public static string EncodedDefault(FraysSetting s) => s.Kind == FraysSettingKind.Text ? s.TextDefault : Encode(s, s.Default);

        /// <summary>
        /// The warning for a value an admin wrote to a frays_* property (from the web Settings page or /modify*):
        /// out of range (it is clamped) or unreadable (the default applies). Null when fine or not a Frays setting.
        /// </summary>
        public static string WarningForProperty(string property, string value)
        {
            if (!TryFind(property, out var s) || s.Kind == FraysSettingKind.Text)
                return null;

            TryParse(s, value, out _, out var warning);
            return warning;
        }

        public static bool TryFind(string name, out FraysSetting setting)
        {
            setting = null;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            name = name.Trim();

            if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(Prefix.Length);

            return byKey.TryGetValue(name, out setting);
        }

        private static string RegisteredDescription(FraysSetting s) =>
            $"(non-retail function) The Frays ({s.Group}): {s.Description} Default {(s.DefaultText.Length == 0 ? "none" : s.DefaultText)}. Range {s.RangeText}. See /frays admin settings";

        /// <summary>The toggle settings, for DefaultPropertyManager's bool defaults.</summary>
        internal static IEnumerable<(string Key, bool Default, string Description)> RegisteredBoolDefaults() =>
            All.Where(s => s.Kind == FraysSettingKind.Toggle).Select(s => (s.PropertyName, s.Default != 0, RegisteredDescription(s)));

        /// <summary>The integer settings, for DefaultPropertyManager's long defaults.</summary>
        internal static IEnumerable<(string Key, long Default, string Description)> RegisteredLongDefaults() =>
            All.Where(s => s.Kind == FraysSettingKind.Integer).Select(s => (s.PropertyName, (long)Math.Round(s.Default), RegisteredDescription(s)));

        /// <summary>The number settings, for DefaultPropertyManager's double defaults.</summary>
        internal static IEnumerable<(string Key, double Default, string Description)> RegisteredDoubleDefaults() =>
            All.Where(s => s.Kind == FraysSettingKind.Number).Select(s => (s.PropertyName, s.Default, RegisteredDescription(s)));

        /// <summary>The text settings, for DefaultPropertyManager's string defaults (the code default text).</summary>
        internal static IEnumerable<(string Key, string Default, string Description)> RegisteredStringDefaults() =>
            All.Where(s => s.Kind == FraysSettingKind.Text).Select(s => (s.PropertyName, s.TextDefault, RegisteredDescription(s)));

        // ---- reading -------------------------------------------------------------------------------------------

        private readonly struct Parsed
        {
            public readonly string Raw;
            public readonly bool Ok;
            public readonly double Number;
            public readonly string Warning;

            public Parsed(string raw, bool ok, double number, string warning)
            {
                Raw = raw;
                Ok = ok;
                Number = number;
                Warning = warning;
            }
        }

        private static readonly ConcurrentDictionary<string, Parsed> parseCache = new ConcurrentDictionary<string, Parsed>(StringComparer.Ordinal);

        public static string RawValue(FraysSetting s)
        {
            try
            {
                return StringSource(s.PropertyName) ?? "";
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] could not read {s.PropertyName}; treating it as not set", ex);
                return "";
            }
        }

        /// <summary>Parses a value for a setting: ok, the clamped number, and a warning when it was clamped or refused.</summary>
        public static bool TryParse(FraysSetting s, string raw, out double value, out string warning)
        {
            warning = null;
            value = s.Default;
            var text = (raw ?? "").Trim();

            if (s.Kind == FraysSettingKind.Toggle)
            {
                switch (text.ToLowerInvariant())
                {
                    case "on": case "true": case "yes": case "1": value = 1; return true;
                    case "off": case "false": case "no": case "0": value = 0; return true;
                    default: warning = $"{s.Key}='{raw}' is not on or off"; return false;
                }
            }

            if (s.Kind == FraysSettingKind.Text)
                return true;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            {
                warning = $"{s.Key}='{raw}' is not a number";
                return false;
            }

            if (s.Kind == FraysSettingKind.Integer)
                v = Math.Round(v, MidpointRounding.AwayFromZero);

            if (v < s.Min || v > s.Max)
            {
                var c = Math.Clamp(v, s.Min, s.Max);
                warning = $"{s.Key}={Format(v)} is out of range {s.RangeText}; using {Format(c)}";
                v = c;
            }

            value = v;
            return true;
        }

        private static Parsed Read(FraysSetting s)
        {
            var raw = RawValue(s);

            if (raw.Trim().Length == 0)
                return default;

            // by value too: a number setting's text is built afresh on every read (CachedText), so by reference alone every
            // read parsed again and rewrote the shared cache under its lock
            if (parseCache.TryGetValue(s.PropertyName, out var cached) && (ReferenceEquals(cached.Raw, raw) || string.Equals(cached.Raw, raw, StringComparison.Ordinal)))
                return cached;

            var ok = TryParse(s, raw, out var v, out var warning);
            var parsed = new Parsed(raw, ok, v, warning);
            parseCache[s.PropertyName] = parsed;

            if (warning != null)
                log.Warn($"[FRAYS] {warning}");

            return parsed;
        }

        /// <summary>The live value of a number, integer or toggle setting (toggle: 1 or 0).</summary>
        public static double Number(string key)
        {
            if (!byKey.TryGetValue(key, out var s))
                throw new ArgumentException($"no Frays setting '{key}'", nameof(key));

            var p = Read(s);
            return p.Raw != null && p.Ok ? p.Number : s.Default;
        }

        /// <summary>A setting for one fire: its override in frays_fire_overrides when it has one, else the global value.</summary>
        public static double Number(string key, string fireId)
        {
            if (fireId != null && byKey.TryGetValue(key, out var s) && s.PerFire && FireOverride(fireId, key) is double v)
                return v;

            return Number(key);
        }

        public static int Int(string key) => (int)Math.Round(Number(key));

        public static bool Toggle(string key) => Number(key) != 0;

        public static string Text(string key)
        {
            var s = byKey[key];
            var raw = RawValue(s);
            return raw.Trim().Length == 0 ? s.TextDefault : raw.Trim();
        }

        /// <summary>True when the setting has a value of its own (not the code default).</summary>
        public static bool IsSet(FraysSetting s) => s.Kind == FraysSettingKind.Text
            ? RawValue(s).Trim().Length > 0 && RawValue(s).Trim() != s.TextDefault
            : Math.Abs(Number(s.Key) - s.Default) > 1e-12;

        /// <summary>The value an admin sees: the live value, formatted.</summary>
        public static string Display(FraysSetting s) => s.Kind == FraysSettingKind.Text ? Text(s.Key)
            : s.Kind == FraysSettingKind.Toggle ? (Number(s.Key) != 0 ? "on" : "off") : Format(Number(s.Key));

        // ---- per-fire overrides --------------------------------------------------------------------------------

        private static string overridesRaw;
        private static Dictionary<string, Dictionary<string, double>> overridesParsed = new Dictionary<string, Dictionary<string, double>>();

        /// <summary>Parses "fireId:key=value,key=value;fireId:...". Bad pieces are skipped.</summary>
        public static Dictionary<string, Dictionary<string, double>> ParseOverrides(string raw)
        {
            var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

            foreach (var fire in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = fire.IndexOf(':');

                if (colon <= 0)
                    continue;

                var id = fire.Substring(0, colon).Trim();
                var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (var pair in fire.Substring(colon + 1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');

                    if (eq <= 0)
                        continue;

                    var key = pair.Substring(0, eq).Trim();

                    if (!byKey.TryGetValue(key, out var s) || !s.PerFire)
                        continue;

                    if (TryParse(s, pair.Substring(eq + 1), out var v, out _))
                        map[s.Key] = v;
                }

                if (map.Count > 0)
                    result[id] = map;
            }

            return result;
        }

        /// <summary>Writes the overrides back to text, sorted so the property is stable.</summary>
        public static string FormatOverrides(Dictionary<string, Dictionary<string, double>> overrides) =>
            string.Join(";", overrides.Where(f => f.Value.Count > 0).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Key + ":" + string.Join(",", f.Value.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={Format(k.Value)}"))));

        public static Dictionary<string, Dictionary<string, double>> CurrentOverrides()
        {
            var raw = Text(FireOverrides);

            lock (byKey)
            {
                if (!ReferenceEquals(raw, overridesRaw) && raw != overridesRaw)
                {
                    overridesParsed = ParseOverrides(raw);
                    overridesRaw = raw;
                }

                return overridesParsed;
            }
        }

        public static double? FireOverride(string fireId, string key)
        {
            var all = CurrentOverrides();
            return all.TryGetValue(fireId, out var map) && map.TryGetValue(key, out var v) ? v : null;
        }

        public static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // ---- old defaults ------------------------------------------------------------------------------------------

        /// <summary>
        /// Old code defaults. A setting's default is written to the shard the first boot it exists, and from
        /// then on the shard's row wins, so a later change to the default never reached a running shard: heat_curve_income,
        /// xp_by_level, key_bar_work, the corpse loot rates and the Vault Embers all stayed at their first values. At boot a
        /// setting still holding one of its old defaults moves to the current default (SyncOldDefaults); any other value
        /// was chosen by an admin and is kept. When a default changes, add the value it had here.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> OldDefaults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [HeatCurveIncome] = new[] { "0:0.2,5:0.4,10:0.6,15:0.8,20:1.0", "0:0.14,10:0.15,20:0.18", "0:0.435,10:0.64,20:0.845" },
            [XpKillCapByHeat] = new[] { "0:5.59,10:5.53,20:5.39", "0:4.3,10:3.72,20:3.28" },
            [XpByLevel] = new[] { "210:0.4475e9,230:0.6575e9,250:0.9575e9,270:1.275e9,290:1.845e9,310:2.04e9,330:3.625e9,350:3.725e9,370:4.675e9,390:6.85e9,410:7.725e9" },
            [LumByLevel] = new[] { "210:61.75e3,230:96.75e3,250:133e3,270:177.75e3,290:265e3,310:362.5e3,330:625e3,350:625e3,370:1.0075e6,390:1.445e6,410:1.735e6" },
            [KeyBarWork] = new[] { "0:75,10:14,20:8.8" },
            [HeatCurveHealth] = new[] { "0:0.7,5:0.9,10:1.2,12:1.5,15:2.4,18:4,20:6" },
            [HeatUpkeepByHeat] = new[] { "15:0,16:5.6,17:11.2,18:16.8,19:24,20:30" },
            [GreatSnarlWork] = new[] { "100" },
            [GreatSnarlWorkPerCamper] = new[] { "250" },
            [LayoutGapAggro] = new[] { "2.5" },
            [LootItemsPerStandard] = new[] { "2", "0.05" },
            [LootOrdinaryPerStandard] = new[] { "1", "0.15" },
            [LootMundanePerStandard] = new[] { "1.5", "0.25" },
            [LootTierByLevel] = new[] { "0:1,30:2,60:3,90:4,120:5,160:6,200:7,275:8" },
            [RichPoints] = new[] { "2" },
            [LootCorpseCap] = new[] { "110" },
            [BruteScale] = new[] { "1.2" },
            [SnarlTrophyChance] = new[] { "0.02" },
            [SnarlHealthNormal] = new[] { "7", "6", "10" },   // set by hand before snarl_health_by_heat existed: back to the fallback default
            [SnarlAugChance] = new[] { "0.02" },
            [VaultEmbersMin] = new[] { "50" },
            [VaultEmbersMax] = new[] { "150" },
        };

        /// <summary>Pure: whether <paramref name="raw"/> (a setting's stored text) is one of its old defaults and not the current one.</summary>
        public static bool IsOldDefault(FraysSetting s, string raw)
        {
            if (s == null || !OldDefaults.TryGetValue(s.Key, out var olds) || string.IsNullOrWhiteSpace(raw))
                return false;

            bool Same(string a, string b)
            {
                if (s.Kind == FraysSettingKind.Text)
                    return string.Equals(Regex.Replace(a ?? "", @"\s", ""), Regex.Replace(b ?? "", @"\s", ""), StringComparison.OrdinalIgnoreCase);

                return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));
            }

            var current = s.Kind == FraysSettingKind.Text ? s.TextDefault : Format(s.Default);
            return !Same(raw, current) && olds.Any(o => o.Length > 0 && Same(raw, o));
        }

        /// <summary>
        /// Boot (Program.cs): moves every setting that still holds an old default to its current default through
        /// <paramref name="write"/> (property name, encoded value; null when written, else why not). Returns a line per setting.
        /// </summary>
        public static List<string> SyncOldDefaults(Func<string, string, string> write)
        {
            var lines = new List<string>();

            foreach (var s in All)
            {
                var raw = RawValue(s).Trim();

                if (!IsOldDefault(s, raw))
                    continue;

                var error = write(s.PropertyName, EncodedDefault(s));
                lines.Add(error == null ? $"{s.Key}: old default {raw} moved to {Display(s)}" : $"{s.Key}: old default {raw} kept ({error})");
            }

            return lines;
        }

        /// <summary>Test seam: forget parsed values.</summary>
        internal static void ResetCacheForTest()
        {
            parseCache.Clear();

            lock (byKey)
            {
                overridesRaw = null;
                overridesParsed = new Dictionary<string, Dictionary<string, double>>();
            }
        }
    }
}

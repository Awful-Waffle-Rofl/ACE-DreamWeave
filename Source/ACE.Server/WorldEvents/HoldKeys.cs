namespace ACE.Server.WorldEvents
{
    /// <summary>The short names of the Hold settings (HoldSettings), one constant each so a typo is a compile error.</summary>
    public static class HoldKeys
    {
        // ---- knights ----
        public const string KnightCount = "knight_count";
        public const string KnightMaxHealth = "knight_max_health";
        public const string KnightMaxHealthByWcid = "knight_max_health_by_wcid";
        public const string KnightDamageTakenMult = "knight_damage_taken_mult";
        public const string UndefendedSeconds = "undefended_seconds";
        public const string UndefendedSecondsPerParticipant = "undefended_seconds_per_participant";
        public const string UndefendedSecondsFloor = "undefended_seconds_floor";
        public const string MinDeathSeconds = "min_death_seconds";
        public const string MinSurvivors = "min_survivors";
        public const string DefendPose = "defend_pose";
        public const string DefendSwingSeconds = "defend_swing_seconds";
        public const string DefendSwingMaxSeconds = "defend_swing_max_seconds";
        public const string HealthMilestones = "health_milestones";

        // ---- healing ----
        public const string Shrine = "shrine";
        public const string ShrineHeal = "shrine_heal";
        public const string ShrineCooldownSeconds = "shrine_cooldown_seconds";
        public const string ShrineCount = "shrine_count";
        public const string ShrineUseRadius = "shrine_use_radius";
        public const string ShrineHealsAll = "shrine_heals_all";
        public const string ShrinePlayerCooldownSeconds = "shrine_player_cooldown_seconds";
        public const string ShrineBindRange = "shrine_bind_range";
        public const string CorruptedKillHeal = "corrupted_kill_heal";
        public const string RescueHeal = "rescue_heal";
        public const string RescueCapPerWave = "rescue_cap_per_wave";
        public const string RegenPerSecond = "regen_per_second";
        public const string RegenClearRadius = "regen_clear_radius";
        public const string RegenIntervalSeconds = "regen_interval_seconds";
        public const string PlayerHeal = "player_heal";
        public const string PlayerHealCap = "player_heal_cap";
        public const string PlayerHealMult = "player_heal_mult";
        public const string HealReductionAfterHit = "heal_reduction_after_hit";
        public const string HealReductionSeconds = "heal_reduction_seconds";
        public const string HealBetweenWaves = "heal_between_waves";
        public const string HealBudgetPerWave = "heal_budget_per_wave";

        // ---- monsters ----
        public const string ShareBreach = "share_breach";
        public const string ShareRunners = "share_runners";
        public const string ShareRanged = "share_ranged";
        public const string ShareHunters = "share_hunters";
        public const string ShareSeekers = "share_seekers";
        public const string ShareCorrupted = "share_corrupted";
        public const string MinBreach = "min_breach";
        public const string MinRanged = "min_ranged";
        public const string MinCorrupted = "min_corrupted";
        public const string MaxCorrupted = "max_corrupted";
        public const string RunnerHealthMult = "runner_health_mult";
        public const string RunnerSpeedMult = "runner_speed_mult";
        public const string RunnerAttackSpeedMult = "runner_attack_speed_mult";
        public const string RangedKeepDistance = "ranged_keep_distance";
        public const string CorruptedHealthMult = "corrupted_health_mult";
        public const string CorruptedSelfMult = "corrupted_self_mult";
        public const string CorruptedEmpowerMult = "corrupted_empower_mult";
        public const string CorruptedAssaultEveryWaves = "corrupted_assault_every_waves";
        public const string CorruptedAssaultFromWave = "corrupted_assault_from_wave";
        public const string CorruptedAssaultCount = "corrupted_assault_count";
        public const string CorruptedTint = "corrupted_tint";
        public const string CasterShareOfRanged = "caster_share_of_ranged";

        // ---- waves ----
        public const string WaveCountBase = "wave_count_base";
        public const string WaveCountPerPlayer = "wave_count_per_player";
        public const string WaveCountCap = "wave_count_cap";
        public const string MaxWaves = "max_waves";
        public const string WaveSizeBase = "wave_size_base";
        public const string WaveSizePerPlayer = "wave_size_per_player";
        public const string WaveSizeCap = "wave_size_cap";
        public const string WaveSizeBasePlayers = "wave_size_base_players";
        public const string WaveSizeMaxPlayers = "wave_size_max_players";
        public const string FinalWaveBonus = "final_wave_bonus";
        public const string FirstWaveDelaySeconds = "first_wave_delay_seconds";
        public const string WaveIntervalSeconds = "wave_interval_seconds";
        public const string BreatherSeconds = "breather_seconds";
        public const string MaxWaveIntervalSeconds = "max_wave_interval_seconds";
        public const string NextWaveAliveShare = "next_wave_alive_share";
        public const string SpawnRadius = "spawn_radius";
        public const string PortalCount = "portal_count";
        public const string PortalRadius = "portal_radius";
        public const string HoldSeconds = "hold_seconds";
        public const string MaxSeconds = "max_seconds";
        public const string AbandonSeconds = "abandon_seconds";

        // ---- internals (constants exposed) ----
        public const string HitIntervalSeconds = "hit_interval_seconds";
        public const string HitIntervalMaxSeconds = "hit_interval_max_seconds";
        public const string CapBurstSeconds = "cap_burst_seconds";
        public const string SpawnClearance = "spawn_clearance";
        public const string RangedSlack = "ranged_slack";
        public const string HealerMemorySeconds = "healer_memory_seconds";
        public const string WaveResolveGraceSeconds = "wave_resolve_grace_seconds";

        // ---- bosses: the stage 1 finale ----
        public const string FinaleHealthMult = "finale_health_mult";
        public const string FinaleHealth = "finale_health";
        public const string FinaleScale = "finale_scale";
        public const string FinaleMinKillSeconds = "finale_min_kill_seconds";
        public const string FinaleLeadSeconds = "finale_lead_seconds";
        public const string FinaleTarget = "finale_target";
        public const string FinaleSwapSeconds = "finale_swap_seconds";
        public const string FinaleSwapMaxSeconds = "finale_swap_max_seconds";
        public const string FinalePullShare = "finale_pull_share";
        public const string FinalePullWindowSeconds = "finale_pull_window_seconds";
        public const string FinalePullSeconds = "finale_pull_seconds";
        public const string FinalePlayerDamageMult = "finale_player_damage_mult";
        public const string FinaleLevelOffset = "finale_level_offset";
        public const string FinaleAddCount = "finale_add_count";
        public const string FinaleAddIntervalSeconds = "finale_add_interval_seconds";
        public const string FinaleEnrageSeconds = "finale_enrage_seconds";
        public const string FinaleEnrageMult = "finale_enrage_mult";
        public const string FinaleBossWcid = "finale_boss_wcid";

        // ---- bosses: the stage 2 champion ----
        public const string ChampionHealthMult = "chain_champion_health_mult";
        public const string ChampionHealth = "chain_champion_health";
        public const string ChampionPlayerDamageMult = "chain_champion_player_damage_mult";
        public const string ChampionLevelOffset = "chain_champion_level_offset";
        public const string ChampionAddCount = "chain_champion_add_count";
        public const string ChampionAddIntervalSeconds = "chain_champion_add_interval_seconds";
        public const string ChampionEnrageSeconds = "chain_champion_enrage_seconds";
        public const string ChampionEnrageMult = "chain_champion_enrage_mult";
        public const string ChampionArrivalSeconds = "chain_champion_arrival_seconds";

        // ---- internals: bosses ----
        public const string FinaleFallbackScale = "finale_fallback_scale";
        public const string FinaleHeightScaleMin = "finale_height_scale_min";
        public const string FinaleHeightScaleMax = "finale_height_scale_max";
        public const string BossDamageKeepSeconds = "boss_damage_keep_seconds";

        // ---- chain ----
        public const string Chain = "chain";
        public const string ChainChance = "chain_chance";
        public const string ChainMaxLength = "chain_max_length";
        public const string ChainNextSource = "chain_next_source";
        public const string ChainNextGoal = "chain_next_goal";
        public const string ChainNextBoss = "chain_next_boss";
        public const string ChainNextFamily = "chain_next_family";
        public const string ChainTownNetwork = "chain_town_network";
        public const string ChainTownMinDistance = "chain_town_min_distance";
        public const string ChainTownMaxDistance = "chain_town_max_distance";
        public const string ChainTownChoices = "chain_town_choices";
        public const string ChainSpotMin = "chain_spot_min";
        public const string ChainSpotMax = "chain_spot_max";
        public const string TownReach = "town_reach";
        public const string ChainCountdownSeconds = "chain_countdown_seconds";
        public const string ChainArrivalSeconds = "chain_arrival_seconds";
        public const string ChainThanksSeconds = "chain_thanks_seconds";
        public const string ChainStorySeconds = "chain_story_seconds";
        public const string ChainAnnounceSeconds = "chain_announce_seconds";
        public const string ChainTieBackSeconds = "chain_tie_back_seconds";
        public const string ChainClosingSeconds = "chain_closing_seconds";
        public const string ChainAsheronSeconds = "chain_asheron_seconds";
        public const string ChainAnnounceLeadSeconds = "chain_announce_lead_seconds";

        // ---- rewards ----
        public const string ChainRewardStage1 = "chain_reward_stage1";
        public const string ChainRewardStage2 = "chain_reward_stage2";
        public const string ChainRewardFinal = "chain_reward_final";
        public const string ParticipantsPerCache = "participants_per_cache";
        public const string MinContribution = "min_contribution";
        public const string KeepStageRewardsOnFail = "keep_stage_rewards_on_fail";
        public const string ChainFullBonus = "chain_full_bonus";
        public const string FailureReward = "failure_reward";

        // ---- participation ----
        public const string ParticipantRadius = "participant_radius";
        public const string MinPresenceSeconds = "min_presence_seconds";
        public const string AfkSeconds = "afk_seconds";
        public const string AdminsCount = "admins_count";

        // ---- messages ----
        public const string Broadcast = "broadcast";
        public const string Story = "story";
        public const string MilestonePercents = "milestone_percents";
    }
}

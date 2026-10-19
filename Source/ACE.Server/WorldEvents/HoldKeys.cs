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
    }
}

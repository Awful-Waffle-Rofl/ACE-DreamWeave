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
    }
}

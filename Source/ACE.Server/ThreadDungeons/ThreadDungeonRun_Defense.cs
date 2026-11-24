using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Defense encounter's four tunables (Docs/Threads/ENCOUNTERS.md "Defense"), read once by TryStart and stamped on
    /// the run before publication, the dynamic_dungeons_shrine_boon_cap pattern. A run nobody stamped (tests) uses the
    /// shipped defaults. Each clamp is pure; NaN reads as the default.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        public const double DefaultDefenseUndefendedSeconds = 240.0;
        public const double DefenseUndefendedSecondsMin = 5.0;
        public const double DefenseUndefendedSecondsMax = 1800.0;

        public const double DefaultDefenseMinDeathSeconds = 45.0;
        public const double DefenseMinDeathSecondsMin = 0.0;
        public const double DefenseMinDeathSecondsMax = 600.0;

        public const double DefaultDefenseSwitchSeconds = 20.0;
        public const double DefenseSwitchSecondsMin = 0.0;
        public const double DefenseSwitchSecondsMax = 600.0;

        public const long DefaultDefenseMaxSeconds = 300;
        public const long DefenseMaxSecondsMin = 60;
        public const long DefenseMaxSecondsMax = 1800;

        /// <summary>dynamic_dungeons_defense_undefended_seconds as TryStart stamps it: NaN reads as the default, then clamped to [5, 1800]. Pure.</summary>
        public static double ClampDefenseUndefendedSeconds(double configured)
            => double.IsNaN(configured) ? DefaultDefenseUndefendedSeconds : Math.Clamp(configured, DefenseUndefendedSecondsMin, DefenseUndefendedSecondsMax);

        /// <summary>dynamic_dungeons_defense_min_death_seconds as TryStart stamps it: NaN reads as the default, then clamped to [0, 600]. Pure.</summary>
        public static double ClampDefenseMinDeathSeconds(double configured)
            => double.IsNaN(configured) ? DefaultDefenseMinDeathSeconds : Math.Clamp(configured, DefenseMinDeathSecondsMin, DefenseMinDeathSecondsMax);

        /// <summary>dynamic_dungeons_defense_switch_seconds as TryStart stamps it: NaN reads as the default, then clamped to [0, 600]. Pure.</summary>
        public static double ClampDefenseSwitchSeconds(double configured)
            => double.IsNaN(configured) ? DefaultDefenseSwitchSeconds : Math.Clamp(configured, DefenseSwitchSecondsMin, DefenseSwitchSecondsMax);

        /// <summary>dynamic_dungeons_defense_max_seconds as TryStart stamps it: clamped to [60, 1800]. Pure.</summary>
        public static long ClampDefenseMaxSeconds(long configured)
            => Math.Clamp(configured, DefenseMaxSecondsMin, DefenseMaxSecondsMax);

        /// <summary>dynamic_dungeons_defense_undefended_seconds: how long one enemy alone takes to bring the pillar down.</summary>
        public double DefenseUndefendedSeconds { get; set; } = DefaultDefenseUndefendedSeconds;

        /// <summary>dynamic_dungeons_defense_min_death_seconds: the fastest any number of enemies together can bring it down (0 = no floor).</summary>
        public double DefenseMinDeathSeconds { get; set; } = DefaultDefenseMinDeathSeconds;

        /// <summary>dynamic_dungeons_defense_switch_seconds: how long an enemy fights players before it turns on the pillar.</summary>
        public double DefenseSwitchSeconds { get; set; } = DefaultDefenseSwitchSeconds;

        /// <summary>dynamic_dungeons_defense_max_seconds: a defense still standing this long after the use succeeds.</summary>
        public long DefenseMaxSeconds { get; set; } = DefaultDefenseMaxSeconds;
    }
}

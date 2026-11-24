using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Craftwork Golem encounter's three tunables (Docs/Threads/ENCOUNTERS.md "Craftwork Golem"), read once by TryStart
    /// and stamped on the run before publication, the Defense pattern. A run nobody stamped (tests) uses the shipped
    /// defaults. Each clamp is pure; NaN reads as the default.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        public const double DefaultGolemPickScale = 1.0;
        public const double GolemPickScaleMin = 0.0;
        public const double GolemPickScaleMax = 3.0;

        public const double DefaultGolemHealthMult = 1.5;
        public const double GolemHealthMultMin = 1.0;
        public const double GolemHealthMultMax = 10.0;

        public const double DefaultGolemAuraSeconds = 4.0;
        public const double GolemAuraSecondsMin = 1.0;
        public const double GolemAuraSecondsMax = 60.0;

        /// <summary>dynamic_dungeons_golem_pick_scale as TryStart stamps it: NaN reads as the default, then clamped to [0, 3]. Pure.</summary>
        public static double ClampGolemPickScale(double configured)
            => double.IsNaN(configured) ? DefaultGolemPickScale : Math.Clamp(configured, GolemPickScaleMin, GolemPickScaleMax);

        /// <summary>dynamic_dungeons_golem_health_mult as TryStart stamps it: NaN reads as the default, then clamped to [1, 10]. Pure.</summary>
        public static double ClampGolemHealthMult(double configured)
            => double.IsNaN(configured) ? DefaultGolemHealthMult : Math.Clamp(configured, GolemHealthMultMin, GolemHealthMultMax);

        /// <summary>dynamic_dungeons_golem_aura_seconds as TryStart stamps it: NaN reads as the default, then clamped to [1, 60]. Pure.</summary>
        public static double ClampGolemAuraSeconds(double configured)
            => double.IsNaN(configured) ? DefaultGolemAuraSeconds : Math.Clamp(configured, GolemAuraSecondsMin, GolemAuraSecondsMax);

        /// <summary>dynamic_dungeons_golem_pick_scale: every wisp buff's size per pick (1 = the bank's values, 0 = no effect).</summary>
        public double GolemPickScale { get; set; } = DefaultGolemPickScale;

        /// <summary>dynamic_dungeons_golem_health_mult: the awakened golem's health floor, as a multiple of the run's largest non-boss health.</summary>
        public double GolemHealthMult { get; set; } = DefaultGolemHealthMult;

        /// <summary>dynamic_dungeons_golem_aura_seconds: how often the awakened golem replays its picked colours.</summary>
        public double GolemAuraSeconds { get; set; } = DefaultGolemAuraSeconds;
    }
}

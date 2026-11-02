using System;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// Pure level scaling for the Pinnacle hazards' damage. The BENCHMARK is the ball hit (authored 0.60 of max
    /// health): 0.25 at Knot level 200, rising 0.0025 per level (0.75 at level 400), clamped to [min, max] where min and
    /// max are the live settings pinnacle_hazard_pct_min / _max (see <see cref="PinnacleTunables.HazardPctClamp"/>).
    /// Every other hazard keeps its authored ratio to the ball: <see cref="Scaled"/> = benchmark x authored / 0.60.
    /// No settings read here, so it tests without a config store.
    /// </summary>
    public static class HazardLevelScale
    {
        /// <summary>The benchmark at <see cref="AnchorLevel"/>.</summary>
        public const double AnchorPct = 0.25;

        public const double AnchorLevel = 200.0;

        /// <summary>Benchmark growth per level.</summary>
        public const double PerLevel = 0.0025;

        /// <summary>The authored damage of the benchmark hazard (a ball hit, HazardDefaults.DamagePct): the denominator of every ratio.</summary>
        public const double BenchmarkAuthored = 0.60;

        /// <summary>clamp(0.25 + 0.0025 x (level - 200), min, max). A min above max yields max (Math.Min wins, no throw).</summary>
        public static double BenchmarkPct(int level, double min, double max)
        {
            var raw = AnchorPct + PerLevel * (level - AnchorLevel);
            return Math.Min(Math.Max(raw, min), max);
        }

        /// <summary>The pct for a hazard authored at <paramref name="authoredPct"/> (the ball's 0.60 gives the benchmark itself).</summary>
        public static double Scaled(int level, double authoredPct, double min, double max)
            => BenchmarkPct(level, min, max) * authoredPct / BenchmarkAuthored;
    }
}

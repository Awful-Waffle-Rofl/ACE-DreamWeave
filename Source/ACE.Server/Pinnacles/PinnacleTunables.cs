using System;

using ACE.Server.Managers;
using ACE.Server.Pinnacles.Grael;
using ACE.Server.Pinnacles.Matron;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The ONLY reader of the pinnacle settings. Each read has an explicit fallback equal to the shipped default: a
    /// PropertyManager read that cannot reach the config store (it throws in unit tests with no database) returns the
    /// fallback rather than propagating, so a fight tick can never fault on a settings read.
    /// </summary>
    public static class PinnacleTunables
    {
        public const string EnabledKey = "pinnacle_encounters_enabled";
        public const string HoldSecondsKey = "pinnacle_hold_seconds";
        public const string ResumeGraceSecondsKey = "pinnacle_resume_grace_seconds";

        public const bool DefaultEnabled = true;
        public const long DefaultHoldSeconds = 600;
        public const long DefaultResumeGraceSeconds = 10;

        /// <summary>The longest hold or grace accepted, in seconds (a day); a garbled huge value cannot pin a copy forever.</summary>
        public const long MaxSeconds = 86400;

        /// <summary>Kill switch: false refuses every new Pinnacle entry. A fight already running is not touched.</summary>
        public static bool Enabled => ReadBool(EnabledKey, DefaultEnabled);

        public static long HoldSeconds => Sanitize(ReadLong(HoldSecondsKey, DefaultHoldSeconds), DefaultHoldSeconds, 1);

        public static long ResumeGraceSeconds => Sanitize(ReadLong(ResumeGraceSecondsKey, DefaultResumeGraceSeconds), DefaultResumeGraceSeconds, 0);

        /// <summary>The fight config from the live settings.</summary>
        public static PinnacleFightConfig FightConfig()
            => new PinnacleFightConfig { HoldSeconds = HoldSeconds, ResumeGraceSeconds = ResumeGraceSeconds };

        public const string GraelP2WispsKey = "pinnacle_grael_p2_wisps";
        public const string GraelP3MemoryKey = "pinnacle_grael_p3_memory";
        public const string GraelP3DispelCountKey = "pinnacle_grael_p3_dispel_count";
        public const string GraelP4BallsKey = "pinnacle_grael_p4_balls";
        public const string GraelP4StreamKey = "pinnacle_grael_p4_stream";
        public const string GraelP4RowsKey = "pinnacle_grael_p4_rows";
        public const string GraelP4CastSecondsKey = "pinnacle_grael_p4_cast_seconds";
        public const string GraelP5WispsKey = "pinnacle_grael_p5_wisps";
        public const string GraelP5RespawnSecondsKey = "pinnacle_grael_p5_respawn_seconds";
        public const string GraelP5CastSecondsKey = "pinnacle_grael_p5_cast_seconds";

        /// <summary>
        /// Grael's tunables from the live settings rows. Any read failure (no config store) yields
        /// <see cref="GraelSettings.Defaults"/>; <see cref="GraelSettings.From"/> turns a blank string into its default.
        /// </summary>
        public static GraelSettings Grael()
        {
            // read once per fight (the factory runs this at each fight's creation); HazardPctClamp never throws
            var (pctMin, pctMax) = HazardPctClamp();

            return GraelStrings() with { HazardPctMin = pctMin, HazardPctMax = pctMax };
        }

        /// <summary>
        /// The Matron's tunables: the code defaults (plan key decision 7, no settings rows of her own) with the live
        /// hazard-pct clamp, read once per fight at its creation. HazardPctClamp never throws.
        /// </summary>
        public static MatronSettings Matron()
        {
            var (pctMin, pctMax) = HazardPctClamp();

            return MatronSettings.Defaults with { HazardPctMin = pctMin, HazardPctMax = pctMax };
        }

        private static GraelSettings GraelStrings()
        {
            try
            {
                return GraelSettings.From(
                    PropertyManager.GetString(GraelP2WispsKey, GraelSettings.DefaultP2Wisps).Item,
                    PropertyManager.GetString(GraelP3MemoryKey, GraelSettings.DefaultP3Memory).Item,
                    PropertyManager.GetLong(GraelP3DispelCountKey, GraelSettings.DefaultP3DispelCount).Item,
                    PropertyManager.GetString(GraelP4BallsKey, GraelSettings.DefaultP4Balls).Item,
                    PropertyManager.GetString(GraelP4StreamKey, GraelSettings.DefaultP4Stream).Item,
                    PropertyManager.GetLong(GraelP4CastSecondsKey, GraelSettings.DefaultP4CastSeconds).Item,
                    PropertyManager.GetString(GraelP5WispsKey, GraelSettings.DefaultP5Wisps).Item,
                    PropertyManager.GetLong(GraelP5RespawnSecondsKey, GraelSettings.DefaultP5RespawnSeconds).Item,
                    PropertyManager.GetLong(GraelP5CastSecondsKey, GraelSettings.DefaultP5CastSeconds).Item,
                    PropertyManager.GetString(GraelP4RowsKey, GraelSettings.DefaultP4Rows).Item);
            }
            catch (Exception)
            {
                return GraelSettings.Defaults;
            }
        }

        // The Threads health-curve dials the Pinnacle tiers scale on (PinnacleScaling.TierHealth). Read here, not in
        // PinnacleScaling, so this class stays the only Pinnacle reader of PropertyManager.
        public const string HealthCurveAnchorLowKey = "dynamic_dungeons_health_curve_anchor_low";
        public const string HealthCurveAnchorHighKey = "dynamic_dungeons_health_curve_anchor_high";
        public const string HealthCurveTopLevelKey = "dynamic_dungeons_health_curve_top_level";

        /// <summary>
        /// The live health-curve anchors and top level, or the compiled Threads defaults when the store cannot be read.
        /// Unsanitized: DungeonHealthCurve.Target sanitizes all three itself. The Threads master switch
        /// (dynamic_dungeons_health_curve) is NOT read: owner ruling 2026-10-20, Pinnacle tiers always use the curve.
        /// </summary>
        public static (double Low, double High, long Top) HealthCurveDials()
        {
            try
            {
                return (PropertyManager.GetDouble(HealthCurveAnchorLowKey, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorLow).Item,
                        PropertyManager.GetDouble(HealthCurveAnchorHighKey, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorHigh).Item,
                        PropertyManager.GetLong(HealthCurveTopLevelKey, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveTopLevel).Item);
            }
            catch (Exception)
            {
                return (ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorLow, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorHigh,
                        ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveTopLevel);
            }
        }

        public const string HazardPctMinKey = "pinnacle_hazard_pct_min";
        public const string HazardPctMaxKey = "pinnacle_hazard_pct_max";

        public const double DefaultHazardPctMin = 0.25;
        public const double DefaultHazardPctMax = 0.75;

        /// <summary>The most either hazard-pct clamp bound may be (a fraction of max health, as the hazards' own pct clamp).</summary>
        public const double MaxHazardPct = 5.0;

        /// <summary>
        /// The live (min, max) clamp for <see cref="HazardLevelScale"/>, each bound clamped to 0..5; a non-finite value
        /// reads as its default, and a min above max falls back to BOTH defaults. Any read failure yields the defaults.
        /// </summary>
        public static (double Min, double Max) HazardPctClamp()
            => SanitizeHazardPct(ReadDouble(HazardPctMinKey, DefaultHazardPctMin), ReadDouble(HazardPctMaxKey, DefaultHazardPctMax));

        /// <summary>Pure: the clamp rules of <see cref="HazardPctClamp"/>.</summary>
        internal static (double Min, double Max) SanitizeHazardPct(double min, double max)
        {
            min = double.IsFinite(min) ? Math.Clamp(min, 0.0, MaxHazardPct) : DefaultHazardPctMin;
            max = double.IsFinite(max) ? Math.Clamp(max, 0.0, MaxHazardPct) : DefaultHazardPctMax;

            if (min > max)
                return (DefaultHazardPctMin, DefaultHazardPctMax);

            return (min, max);
        }

        private static double ReadDouble(string key, double fallback)
        {
            try { return PropertyManager.GetDouble(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }

        /// <summary>Pure: a value below <paramref name="min"/> reads as the default, a value above <see cref="MaxSeconds"/> as the cap.</summary>
        internal static long Sanitize(long value, long fallback, long min)
        {
            if (value < min)
                return fallback;

            return Math.Min(value, MaxSeconds);
        }

        private static bool ReadBool(string key, bool fallback)
        {
            try { return PropertyManager.GetBool(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }

        private static long ReadLong(string key, long fallback)
        {
            try { return PropertyManager.GetLong(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }
    }
}

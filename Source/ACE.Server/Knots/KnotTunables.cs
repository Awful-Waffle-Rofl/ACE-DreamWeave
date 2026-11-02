using System;

using ACE.Server.Managers;

namespace ACE.Server.Knots
{
    /// <summary>
    /// Typed readers for the five Knots tunables, each with an explicit fallback equal to the shipped default.
    /// A read that cannot reach the config store (PropertyManager reads throw in unit tests with no database)
    /// returns the fallback rather than propagating, so a drop roll can never fault a Thread clear.
    /// Chances are clamped to [0, 1] on read.
    /// </summary>
    public static class KnotTunables
    {
        public const string FiberEnabledKey = "dynamic_dungeons_fiber_enabled";
        public const string FiberPerSeatKey = "dynamic_dungeons_fiber_per_seat";
        public const string FiberChanceKey = "dynamic_dungeons_fiber_chance";
        public const string FiberPityStepKey = "dynamic_dungeons_fiber_pity_step";
        public const string PatternChanceKey = "dynamic_dungeons_pattern_chance";

        public const bool DefaultFiberEnabled = false;
        public const bool DefaultFiberPerSeat = true;
        public const double DefaultFiberChance = 0.20;
        public const double DefaultFiberPityStep = 0.05;
        public const double DefaultPatternChance = 0.07;

        public static bool FiberEnabled => ReadBool(FiberEnabledKey, DefaultFiberEnabled);

        /// <summary>Kill switch (owner ruling 2026-10-21): true rolls every eligible seat, false rolls the run owner only.</summary>
        public static bool FiberPerSeat => ReadBool(FiberPerSeatKey, DefaultFiberPerSeat);

        public static double FiberChance => Clamp01(ReadDouble(FiberChanceKey, DefaultFiberChance));

        public static double FiberPityStep => Clamp01(ReadDouble(FiberPityStepKey, DefaultFiberPityStep));

        public static double PatternChance => Clamp01(ReadDouble(PatternChanceKey, DefaultPatternChance));

        internal static double Clamp01(double v) => double.IsNaN(v) ? 0.0 : Math.Min(1.0, Math.Max(0.0, v));

        private static bool ReadBool(string key, bool fallback)
        {
            try { return PropertyManager.GetBool(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }

        private static double ReadDouble(string key, double fallback)
        {
            try { return PropertyManager.GetDouble(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }
    }
}

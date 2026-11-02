using System;

using ACE.Server.Managers;

namespace ACE.Server.KnotLoot
{
    /// <summary>The Knot level window of one loot table: start (the low end of the roll) moves linearly from LowStart at LowLevel to HighStart at HighLevel.</summary>
    public readonly record struct KnotWindowSettings(long LowLevel, long HighLevel, double LowStart, double HighStart, double Width)
    {
        public static readonly KnotWindowSettings Default = new(
            KnotLootDefaults.GraelWindowLowLevel,
            KnotLootDefaults.GraelWindowHighLevel,
            KnotLootDefaults.GraelWindowLowStart,
            KnotLootDefaults.GraelWindowHighStart,
            KnotLootDefaults.GraelWindowWidth);
    }

    /// <summary>A min-max magnitude range (positive values).</summary>
    public readonly record struct KnotTraitRange(double Min, double Max);

    /// <summary>One trait live settings: the mechanic switch and up to two ranges (the second is unused by single-range traits).</summary>
    public readonly record struct KnotTraitSettings(bool Enabled, KnotTraitRange Primary, KnotTraitRange Secondary);

    /// <summary>
    /// The ONLY reader of the Knot loot settings (knot_loot_* and knot_trait_*). Every read is live and falls back
    /// to the supplied default when the store is unreachable or a value is unusable.
    /// </summary>
    public static class KnotLootTunables
    {
        public static string TableKey(string table, string suffix) => $"knot_loot_{table}_{suffix}";

        public static string TraitKey(string prefix, string suffix) => $"knot_trait_{prefix}_{suffix}";

        public static bool TableEnabled(string table) => ReadBool(TableKey(table, "enabled"), true);

        public static long Weight(string table, string entryKey, long fallback)
            => Math.Max(0, ReadLong(TableKey(table, "weight_" + entryKey), fallback));

        public static double Chance(string table, string groupKey, double fallback)
            => Unit(ReadDouble(TableKey(table, "chance_" + groupKey), fallback), fallback);

        public static (long Min, long Max) Currency(string table, string currencyKey, (long Min, long Max) fallback)
            => SanitizeCount(
                (ReadLong(TableKey(table, currencyKey + "_min"), fallback.Min), ReadLong(TableKey(table, currencyKey + "_max"), fallback.Max)),
                fallback);

        public static KnotWindowSettings Window(string table)
        {
            var d = KnotWindowSettings.Default;

            return SanitizeWindow(new KnotWindowSettings(
                ReadLong(TableKey(table, "window_low_level"), d.LowLevel),
                ReadLong(TableKey(table, "window_high_level"), d.HighLevel),
                ReadDouble(TableKey(table, "window_low_start"), d.LowStart),
                ReadDouble(TableKey(table, "window_high_start"), d.HighStart),
                ReadDouble(TableKey(table, "window_width"), d.Width)));
        }

        /// <summary>
        /// Reads a trait. Range key names per prefix: cast_speed, frugal_hand and overpowering_intent use min/max (Primary);
        /// shield_trade uses armor_* (Primary) and health_* (Secondary); wrist_trade uses gain_* (Primary) and cost_*
        /// (Secondary). An unknown prefix reads only the switch; the ranges are the fallback.
        /// </summary>
        public static KnotTraitSettings Trait(string prefix, KnotTraitSettings fallback)
        {
            var enabled = ReadBool(TraitKey(prefix, "enabled"), fallback.Enabled);

            switch (prefix)
            {
                case "cast_speed":
                case "frugal_hand":
                case "overpowering_intent":
                    return new KnotTraitSettings(enabled, ReadRange(prefix, "min", "max", fallback.Primary), fallback.Secondary);

                case "shield_trade":
                    return new KnotTraitSettings(enabled, ReadRange(prefix, "armor_min", "armor_max", fallback.Primary), ReadRange(prefix, "health_min", "health_max", fallback.Secondary));

                case "wrist_trade":
                    return new KnotTraitSettings(enabled, ReadRange(prefix, "gain_min", "gain_max", fallback.Primary), ReadRange(prefix, "cost_min", "cost_max", fallback.Secondary));

                default:
                    return new KnotTraitSettings(enabled, fallback.Primary, fallback.Secondary);
            }
        }

        private static KnotTraitRange ReadRange(string prefix, string minSuffix, string maxSuffix, KnotTraitRange fallback)
            => SanitizeRange(
                new KnotTraitRange(ReadDouble(TraitKey(prefix, minSuffix), fallback.Min), ReadDouble(TraitKey(prefix, maxSuffix), fallback.Max)),
                fallback);

        /// <summary>Pure: a non-finite double reads as its default; starts and width clamp to [0,1]; high level at or below low level returns both levels to the defaults.</summary>
        internal static KnotWindowSettings SanitizeWindow(KnotWindowSettings raw)
        {
            var d = KnotWindowSettings.Default;

            var low = raw.LowLevel;
            var high = raw.HighLevel;

            if (high <= low)
            {
                low = d.LowLevel;
                high = d.HighLevel;
            }

            return new KnotWindowSettings(
                low,
                high,
                Unit(raw.LowStart, d.LowStart),
                Unit(raw.HighStart, d.HighStart),
                Unit(raw.Width, d.Width));
        }

        /// <summary>The largest trait magnitude a setting may hold; above it a range falls back, so no rounded trait value can overflow an int.</summary>
        internal const double MaxTraitMagnitude = 1_000_000;

        /// <summary>Pure: a range with a non-finite or negative bound, a max above MaxTraitMagnitude, or min above max, returns the fallback.</summary>
        internal static KnotTraitRange SanitizeRange(KnotTraitRange raw, KnotTraitRange fallback)
        {
            if (!double.IsFinite(raw.Min) || !double.IsFinite(raw.Max) || raw.Min < 0 || raw.Max < 0 || raw.Min > raw.Max || raw.Max > MaxTraitMagnitude)
                return fallback;

            return raw;
        }

        /// <summary>Pure: a count range with min above max or min below zero returns the fallback.</summary>
        internal static (long Min, long Max) SanitizeCount((long Min, long Max) raw, (long Min, long Max) fallback)
        {
            if (raw.Min < 0 || raw.Min > raw.Max)
                return fallback;

            return raw;
        }

        private static double Unit(double value, double fallback)
            => double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : fallback;

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

        private static double ReadDouble(string key, double fallback)
        {
            try { return PropertyManager.GetDouble(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }
    }
}

using System;
using System.Collections.Generic;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The ten pvp_team_hue_* tunables (Docs/Pvp/TEMPLATES.md "Team hue"), each behind a static Func seam with a try/catch that
    /// falls back to the compiled default: PropertyManager reads throw in ACE.Server.Tests, which has no shard config (the
    /// PvpTemplateSettings pattern). Tests swap a seam and put it back in a finally.
    ///
    /// <see cref="Snapshot"/> reads every key ONCE and returns an immutable <see cref="PvpTeamHueDials"/>, which a match takes
    /// at dispatch so a setting change mid-dispatch never gives two seats different rules.
    /// </summary>
    public static class PvpTeamHueSettings
    {
        public static readonly ILog Log = LogManager.GetLogger(typeof(PvpTeamHueSettings));

        public const bool DefaultEnabled = true;

        // Placeholders for later tuning (each is listed by every distinct suit ClothingBase; PvpTeamHueSuitCoverageTests guards that).
        public const long DefaultRedPalette = 85;
        public const long DefaultBluePalette = 88;
        public const long DefaultNeutralPalette = 90;
        public const double DefaultShade = 0.0;
        public const string DefaultOverrides = "";

        /// <summary>The Tugak Brawl pool (pvp_team_hue_ffa_palettes): each seat is dressed in one distinct palette from it.</summary>
        public const string DefaultFfaPalettes = "2,13,14,84,85,86,88,89,90";
        public const double DefaultFfaShade = 0.0;

        public static Func<bool> EnabledSource = () => ReadBool("pvp_team_hue_enabled", DefaultEnabled);

        public static Func<long> RedPaletteSource = () => ReadLong("pvp_team_hue_red_palette", DefaultRedPalette);
        public static Func<double> RedShadeSource = () => ReadDouble("pvp_team_hue_red_shade", DefaultShade);

        public static Func<long> BluePaletteSource = () => ReadLong("pvp_team_hue_blue_palette", DefaultBluePalette);
        public static Func<double> BlueShadeSource = () => ReadDouble("pvp_team_hue_blue_shade", DefaultShade);

        public static Func<long> NeutralPaletteSource = () => ReadLong("pvp_team_hue_neutral_palette", DefaultNeutralPalette);
        public static Func<double> NeutralShadeSource = () => ReadDouble("pvp_team_hue_neutral_shade", DefaultShade);

        public static Func<string> OverridesSource = () => ReadString("pvp_team_hue_overrides", DefaultOverrides);

        public static Func<string> FfaPalettesSource = () => ReadString("pvp_team_hue_ffa_palettes", DefaultFfaPalettes);
        public static Func<double> FfaShadeSource = () => ReadDouble("pvp_team_hue_ffa_shade", DefaultFfaShade);

        /// <summary>Receives each override-parse warning as well as the log. Null in production; a test seam.</summary>
        internal static Action<string> WarnSink;

        /// <summary>
        /// One read of every team hue setting. A palette outside 0 to int.MaxValue reads as 0 (keep own look); a shade outside 0 to 1,
        /// or not a number, is clamped (NaN reads as 0). A bad override entry is logged and skipped.
        /// </summary>
        public static PvpTeamHueDials Snapshot()
        {
            var enabled = Safe(EnabledSource, DefaultEnabled);

            var overrides = PvpTeamHue.ParseOverrides(Safe(OverridesSource, DefaultOverrides), line =>
            {
                Log.Warn($"[PVPTEMPLATE] {line}");
                WarnSink?.Invoke(line);
            });

            var ffaPalettes = PvpTeamHue.ParseFfaPalettes(Safe(FfaPalettesSource, DefaultFfaPalettes), line =>
            {
                Log.Warn($"[PVPTEMPLATE] {line}");
                WarnSink?.Invoke(line);
            });

            return new PvpTeamHueDials(
                enabled,
                Look(Safe(RedPaletteSource, DefaultRedPalette), Safe(RedShadeSource, DefaultShade)),
                Look(Safe(BluePaletteSource, DefaultBluePalette), Safe(BlueShadeSource, DefaultShade)),
                Look(Safe(NeutralPaletteSource, DefaultNeutralPalette), Safe(NeutralShadeSource, DefaultShade)),
                overrides,
                ffaPalettes,
                Look(1, Safe(FfaShadeSource, DefaultFfaShade)).Shade);
        }

        /// <summary>A palette and shade setting as a look: a palette outside 0 to int.MaxValue is 0, a shade is clamped to [0,1] (NaN is 0).</summary>
        public static PvpTeamHueLook Look(long palette, double shade)
        {
            var p = palette < 0 || palette > int.MaxValue ? 0 : (int)palette;
            var s = double.IsNaN(shade) ? 0.0 : Math.Max(0.0, Math.Min(1.0, shade));

            return new PvpTeamHueLook(p, s);
        }

        private static T Safe<T>(Func<T> source, T fallback)
        {
            try { return source == null ? fallback : source(); }
            catch (Exception) { return fallback; }
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

        private static double ReadDouble(string key, double fallback)
        {
            try { return PropertyManager.GetDouble(key, fallback).Item; }
            catch (Exception) { return fallback; }
        }

        private static string ReadString(string key, string fallback)
        {
            try { return PropertyManager.GetString(key, fallback).Item ?? fallback; }
            catch (Exception) { return fallback; }
        }
    }
}

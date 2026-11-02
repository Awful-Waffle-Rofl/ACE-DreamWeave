using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The pure value parsers of the Hold settings rows that carry a text shape (a list, a palette, an alias). Each is used on the
// write side (the row's ParseValue hook, through the Check* wrappers) and is public for the read side of later slices, so the
// shape that is accepted when an admin types it is the shape that is read back. No clock, no world, no settings.
namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        /// <summary>The range of one knight's max health, as the knight_max_health rows state it.</summary>
        public const int KnightHealthMin = 1000, KnightHealthMax = 100000;

        /// <summary>The write-side aliases of min_survivors: all knights must fall to fail (1), any one fall fails the run (3).</summary>
        public const string MinSurvivorsAll = "all", MinSurvivorsAny = "any";

        // ---- knight_max_health_by_wcid ---------------------------------------------------------------------------

        /// <summary>
        /// "wcid=health,wcid=health" (e.g. "1007650=12000,1007652=8000"): whole numbers, health KnightHealthMin to KnightHealthMax,
        /// each wcid at most once. Any malformed, out-of-range or repeated entry refuses the whole value (the reply names it).
        /// </summary>
        public static bool TryParseKnightHealthByWcid(string raw, out IReadOnlyDictionary<uint, int> byWcid, out string error)
        {
            byWcid = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "give wcid=health pairs, e.g. 1007650=12000,1007652=8000";
                return false;
            }

            var map = new Dictionary<uint, int>();

            foreach (var part in raw.Split(','))
            {
                var entry = part.Trim();

                if (entry.Length == 0)
                {
                    error = "an empty entry (stray comma); use wcid=health,wcid=health";
                    return false;
                }

                var eq = entry.IndexOf('=');

                if (eq <= 0 || eq == entry.Length - 1)
                {
                    error = $"'{entry}' is not wcid=health";
                    return false;
                }

                var wcidText = entry.Substring(0, eq).Trim();
                var healthText = entry.Substring(eq + 1).Trim();

                if (wcidText.Length > 0 && wcidText.All(char.IsAsciiDigit) && !uint.TryParse(wcidText, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    error = $"wcid {wcidText} in '{entry}' is out of range (too large)";
                    return false;
                }

                if (!uint.TryParse(wcidText, NumberStyles.None, CultureInfo.InvariantCulture, out var wcid) || wcid == 0)
                {
                    error = $"'{wcidText}' in '{entry}' is not a wcid (a whole number above 0)";
                    return false;
                }

                if (healthText.Length > 0 && healthText.All(char.IsAsciiDigit) && !long.TryParse(healthText, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    error = $"health {healthText} in '{entry}' is outside {KnightHealthMin}..{KnightHealthMax}";
                    return false;
                }

                if (!long.TryParse(healthText, NumberStyles.None, CultureInfo.InvariantCulture, out var health))
                {
                    error = $"'{healthText}' in '{entry}' is not a whole-number health";
                    return false;
                }

                if (health < KnightHealthMin || health > KnightHealthMax)
                {
                    error = $"{wcid}={health} is outside {KnightHealthMin}..{KnightHealthMax}";
                    return false;
                }

                if (map.ContainsKey(wcid))
                {
                    error = $"wcid {wcid} is listed twice";
                    return false;
                }

                map[wcid] = (int)health;
            }

            byWcid = map;
            return true;
        }

        /// <summary>The knight_max_health_by_wcid row's write-side check; stores the canonical text, sorted by wcid: "1007650=12000,1007652=8000".</summary>
        public static (string Normalized, string Error) CheckKnightHealthByWcid(string raw) =>
            TryParseKnightHealthByWcid(raw, out var map, out var error)
                ? (string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture) + "=" + kv.Value.ToString(CultureInfo.InvariantCulture))), null)
                : (null, error);
        // ---- milestone_percents ----------------------------------------------------------------------------------

        /// <summary>
        /// "80,60,40": whole percents 1 to 99, comma separated, duplicates dropped, highest first. "none" (any case) is no milestone
        /// lines at all: an empty list. Anything else refuses the whole value.
        /// </summary>
        public static bool TryParseMilestonePercents(string raw, out IReadOnlyList<int> percents, out string error)
        {
            percents = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "give percents like 80,60,40 (1 to 99), or none";
                return false;
            }

            if (raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                percents = new List<int>();
                return true;
            }

            var found = new List<int>();

            foreach (var part in raw.Split(','))
            {
                var entry = part.Trim();

                if (entry.Length == 0)
                {
                    error = "an empty entry (stray comma); use percents like 80,60,40";
                    return false;
                }

                if (!int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var pct) || pct < 1 || pct > 99)
                {
                    error = $"'{entry}' is not a whole percent from 1 to 99";
                    return false;
                }

                found.Add(pct);
            }

            percents = found.Distinct().OrderByDescending(p => p).ToList();
            return true;
        }

        /// <summary>The milestone_percents row's write-side check; stores "80,60,40" (deduplicated, descending) or "none".</summary>
        public static (string Normalized, string Error) CheckMilestonePercents(string raw) =>
            TryParseMilestonePercents(raw, out var percents, out var error)
                ? (percents.Count == 0 ? "none" : string.Join(",", percents.Select(p => p.ToString(CultureInfo.InvariantCulture))), null)
                : (null, error);

        // ---- corrupted_tint --------------------------------------------------------------------------------------

        /// <summary>A client palette id (0x04000000 to 0x0400FFFF, hex or decimal) or "none"; 0 is no tint. Uses <see cref="WorldEventTint.TryParsePalette"/>.</summary>
        public static bool TryParseCorruptedTint(string raw, out uint palette, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                palette = 0;
                error = "give a palette like 0x04001381, or none";
                return false;
            }

            if (WorldEventTint.TryParsePalette(raw, out palette))
                return true;

            error = $"'{raw.Trim()}' is not a palette id (0x{WorldEventTint.PaletteMin:X8} to 0x{WorldEventTint.PaletteMax:X8}), or none";
            return false;
        }

        /// <summary>The corrupted_tint row's write-side check; stores "0x04001381" or "none".</summary>
        public static (string Normalized, string Error) CheckCorruptedTint(string raw) =>
            TryParseCorruptedTint(raw, out var palette, out var error)
                ? (palette == 0 ? "none" : "0x" + palette.ToString("X8", CultureInfo.InvariantCulture), null)
                : (null, error);

        // ---- min_survivors ---------------------------------------------------------------------------------------

        /// <summary>The number min_survivors stores for the alias <paramref name="raw"/> ("all" = 1, "any" = 3), or null when it is not an alias.</summary>
        public static string ResolveMinSurvivorsAlias(string raw)
        {
            var t = (raw ?? "").Trim();

            if (t.Equals(MinSurvivorsAll, StringComparison.OrdinalIgnoreCase))
                return "1";

            if (t.Equals(MinSurvivorsAny, StringComparison.OrdinalIgnoreCase))
                return "3";

            return null;
        }

        /// <summary>The min_survivors row's write-side check: an alias becomes its number; anything else passes through to the ordinary 1..3 whole-number check.</summary>
        public static (string Normalized, string Error) CheckMinSurvivors(string raw) => (ResolveMinSurvivorsAlias(raw) ?? raw, null);
    }
}

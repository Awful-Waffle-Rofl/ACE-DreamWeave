using System;

// World Events Hold H0: the pure line helpers HoldObjective's progress text needs (D6 - no clock, no world, no settings).
// Each Hold slice adds WorldEventHoldRules helpers only in its own partial file; later slices reuse these, never redefine them.
namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        /// <summary>What {family} reads as when a run has no family display name.</summary>
        public const string DefaultFamilyText = "the attackers";

        /// <summary>
        /// Fills a protect line's tokens (see ProtectLinesDef). {Family} is <paramref name="family"/> with its first letter
        /// capitalized; a missing family falls back to <see cref="DefaultFamilyText"/>, so a line still reads when a run has no
        /// family display name. Unknown tokens are left as written, and so are {anchor} and {pct} when <paramref name="anchor"/>
        /// / <paramref name="pct"/> is null.
        /// </summary>
        public static string FillLine(string template, string family, string noun, string nounPlural, string anchor,
            int alive, int total, int? pct = null)
        {
            if (string.IsNullOrEmpty(template))
                return "";

            var familyText = string.IsNullOrWhiteSpace(family) ? DefaultFamilyText : family.Trim();

            var result = template
                .Replace("{Family}", CapitalizeFirst(familyText))
                .Replace("{family}", familyText)
                .Replace("{nounPlural}", nounPlural ?? "")
                .Replace("{noun}", noun ?? "")
                .Replace("{alive}", alive.ToString())
                .Replace("{total}", total.ToString());

            if (anchor != null)
                result = result.Replace("{anchor}", anchor);

            if (pct != null)
                result = result.Replace("{pct}", pct.Value.ToString());

            return result;
        }

        /// <summary>"the Emberwrought" to "The Emberwrought". Only the first character changes.</summary>
        public static string CapitalizeFirst(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";

            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>"Wave 3 of 7"; "Wave 0 of 7" before the first. Never a number above the total.</summary>
        public static string WaveProgress(int started, int target)
        {
            // Never "Wave 6 of 5": a wave count past the total (which the cap should make impossible) reads as the last wave.
            target = Math.Max(0, target);
            started = Math.Max(0, started);

            if (target > 0 && started > target)
                started = target;

            return $"Wave {started} of {target}";
        }
    }
}

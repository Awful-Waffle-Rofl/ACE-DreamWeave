using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// One-line text of a rolled Knot drop list for the /knotloot debug command. Pure: the window and trait settings
    /// are handed in, so it never reads the property store. Trait drops use the same text as appraisal.
    /// </summary>
    public static class KnotLootSummary
    {
        /// <summary>AC chat lines are cut at 255 characters; stay under it.</summary>
        public const int MaxLineLength = 250;

        private const string Ellipsis = "...";
        private const int MinTraitText = 12;

        public static string FormatLine(
            string tableKey,
            int level,
            int rollNumber,
            IReadOnlyList<KnotLootDrop> drops,
            KnotWindowSettings window,
            Func<KnotTraitDefinition, KnotTraitSettings> traitSettings)
        {
            var prefix = string.Format(CultureInfo.InvariantCulture, "Knot {0} L{1} #{2}: ", tableKey, level, rollNumber);

            if (drops == null || drops.Count == 0)
                return prefix + "(no drops)";

            var wcids = new List<string>(drops.Count);
            var texts = new List<string>(drops.Count);
            var isTrait = new List<bool>(drops.Count);

            foreach (var drop in drops)
            {
                var wcid = drop.Wcid.ToString(CultureInfo.InvariantCulture);
                string text = null;

                if (drop.Trait != null && KnotTraitRegistry.TryGet(drop.Trait.Value, out var definition))
                {
                    var effect = KnotTraitValue.Resolve(drop.U, level, window, traitSettings(definition), false);
                    text = KnotTraitDisplay.Describe(definition, effect);
                }

                wcids.Add(wcid);
                isTrait.Add(text != null);
                texts.Add(text ?? "x" + drop.StackSize.ToString(CultureInfo.InvariantCulture));
            }

            // Never split a roll across lines: shorten the longest trait text until the line fits.
            while (Build(prefix, wcids, texts).Length > MaxLineLength)
            {
                var longest = -1;

                for (var i = 0; i < texts.Count; i++)
                {
                    if (isTrait[i] && texts[i].Length > MinTraitText + Ellipsis.Length && (longest < 0 || texts[i].Length > texts[longest].Length))
                        longest = i;
                }

                if (longest < 0)
                    break;

                var over = Build(prefix, wcids, texts).Length - MaxLineLength;
                var keep = Math.Max(MinTraitText, texts[longest].Length - Ellipsis.Length - over);

                texts[longest] = texts[longest].Substring(0, keep) + Ellipsis;
            }

            var line = Build(prefix, wcids, texts);

            return line.Length > MaxLineLength ? line.Substring(0, MaxLineLength) : line;
        }

        private static string Build(string prefix, List<string> wcids, List<string> texts)
        {
            var sb = new StringBuilder(prefix);

            for (var i = 0; i < wcids.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");

                sb.Append(wcids[i]).Append(' ').Append(texts[i]);
            }

            return sb.ToString();
        }
    }
}

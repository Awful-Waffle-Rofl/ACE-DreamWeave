using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Managers;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Snarl Trophy Wall in the Great Hall: each Snarl Trophy with its killer, family and
    /// date. A readable sign (dreamweave-content 2000194) placed with /frays admin piece add 2000194; its pages are written
    /// on each read from the latest trophies, kept in frays_trophy_wall_state.
    /// </summary>
    public static class FraysTrophyWall
    {
        public const uint Wcid = 2000194;

        /// <summary>How many trophies the saved state keeps (the wall shows trophy_wall_count of them).</summary>
        public const int Kept = 50;

        public sealed class Entry
        {
            public DateTime Date;
            public string Finder = "";
            public string Family = "";
        }

        private static readonly object gate = new object();

        public static bool Is(ACE.Server.WorldObjects.WorldObject wo) => wo?.WeenieClassId == Wcid;

        /// <summary>Pure: the saved text (date|finder|family entries split by ';', newest first) read back.</summary>
        public static List<Entry> Parse(string text)
        {
            var list = new List<Entry>();

            foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('|');

                if (f.Length >= 3 && DateTime.TryParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    list.Add(new Entry { Date = d, Finder = f[1], Family = f[2] });
            }

            return list;
        }

        /// <summary>Pure: entries to the saved text, newest first, at most <see cref="Kept"/>.</summary>
        public static string Format(IEnumerable<Entry> entries) =>
            string.Join(";", entries.Take(Kept).Select(e => $"{e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{Clean(e.Finder)}|{Clean(e.Family)}"));

        /// <summary>Pure: the same, led by "#total" (how many trophies were ever found; the Snarl Monument's stones use it).</summary>
        public static string Format(IEnumerable<Entry> entries, int total) => total > 0 ? $"#{total};{Format(entries)}" : Format(entries);

        /// <summary>Pure: how many trophies were ever found (the leading "#total"; older saves count their entries).</summary>
        public static int ParseTotal(string text)
        {
            var first = (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return first.StartsWith("#") && int.TryParse(first.Substring(1), out var n) ? Math.Max(n, Parse(text).Count) : Parse(text).Count;
        }

        private static string Clean(string s) => (s ?? "").Replace("|", " ").Replace(";", " ").Trim();

        /// <summary>A Snarl Trophy dropped: it goes on the wall.</summary>
        public static void Add(string finder, string family)
        {
            lock (gate)
            {
                var text = FraysSettings.Text(FraysSettings.TrophyWallState);
                var counts = Counts();   // before the wall changes: a first save seeds from the wall as it was
                var list = Parse(text);
                list.Insert(0, new Entry { Date = DateTime.UtcNow.Date, Finder = finder ?? "", Family = family ?? "" });
                PropertyManager.ModifyString(FraysSettings.Prefix + FraysSettings.TrophyWallState, Format(list, ParseTotal(text) + 1));
                PropertyManager.ModifyString(FraysSettings.Prefix + FraysSettings.TrophyCountsState, FormatCounts(Counted(counts, finder, DateTime.UtcNow)));
            }

            // The Snarl Monument's stones take the new name (the oldest shown gives up its stone).
            FraysVillage.RefreshMonuments();
        }


        // ---- all-time counts (the Champion's stone) ----------------------------------------------------------------

        /// <summary>One player's all-time Snarl Trophy count, and when they reached it (ties: the earlier keeps the lead).</summary>
        public sealed class Tally
        {
            public string Name = "";
            public int Count;
            public DateTime Reached;
        }

        /// <summary>Pure: the saved counts (name|count|reached entries split by ';') read back.</summary>
        public static List<Tally> ParseCounts(string text)
        {
            var list = new List<Tally>();

            foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('|');

                if (f.Length >= 3 && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                    && DateTime.TryParseExact(f[2], "yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d))
                    list.Add(new Tally { Name = f[0], Count = n, Reached = d });
            }

            return list;
        }

        /// <summary>Pure: counts to the saved text.</summary>
        public static string FormatCounts(IEnumerable<Tally> counts) =>
            string.Join(";", counts.Where(t => t.Count > 0).Select(t => $"{Clean(t.Name)}|{t.Count.ToString(CultureInfo.InvariantCulture)}|{t.Reached.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)}"));

        /// <summary>
        /// Pure: counts built from the wall's entries (newest first), for a server that has no counts saved yet. The wall
        /// keeps only the latest <see cref="Kept"/>, so older trophies are missing if there were more. Entries on the same
        /// day keep their order: the older one reached its count first.
        /// </summary>
        public static List<Tally> SeedCounts(IReadOnlyList<Entry> newestFirst)
        {
            var counts = new List<Tally>();
            var oldestFirst = (newestFirst ?? new List<Entry>()).Reverse().ToList();

            for (var i = 0; i < oldestFirst.Count; i++)
                counts = Counted(counts, oldestFirst[i].Finder, oldestFirst[i].Date.AddTicks(i));

            return counts;
        }

        /// <summary>Pure: <paramref name="finder"/> found one more at <paramref name="when"/>.</summary>
        public static List<Tally> Counted(List<Tally> counts, string finder, DateTime when)
        {
            var name = Clean(finder);

            if (name.Length == 0)
                return counts;

            var t = counts.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (t == null)
                counts.Add(t = new Tally { Name = name });

            t.Count++;
            t.Reached = when;
            return counts;
        }

        /// <summary>Pure: the champion: the most trophies; on a tie, the one who reached that count first. Null with none.</summary>
        public static Tally Champion(IEnumerable<Tally> counts) =>
            (counts ?? Enumerable.Empty<Tally>()).Where(t => t.Count > 0).OrderByDescending(t => t.Count).ThenBy(t => t.Reached).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        /// <summary>The all-time counts: the saved ones, else seeded from the wall (saved with the next trophy).</summary>
        public static List<Tally> Counts()
        {
            var saved = FraysSettings.Text(FraysSettings.TrophyCountsState);
            return string.IsNullOrWhiteSpace(saved) ? SeedCounts(Parse(FraysSettings.Text(FraysSettings.TrophyWallState))) : ParseCounts(saved);
        }

        /// <summary>Pure: the wall's pages for these entries, the latest <paramref name="count"/>, about 1000 characters a page.</summary>
        public static List<string> Pages(IReadOnlyList<Entry> entries, int count)
        {
            var shown = entries.Take(Math.Max(0, count)).ToList();

            if (shown.Count == 0)
                return new List<string> { "SNARL TROPHY WALL\n\nNo Snarl Trophy has been found yet. A Snarl's cache holds one now and then." };

            var pages = new List<string>();
            var page = "SNARL TROPHY WALL\n\nThe latest Snarl Trophies found in the Frays:\n";

            foreach (var e in shown)
            {
                var line = $"\n{e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} - {e.Finder}, {(e.Family.Length > 0 ? e.Family : "Fray")} Snarl";

                if (page.Length + line.Length > 950)
                {
                    pages.Add(page);
                    page = "SNARL TROPHY WALL (continued)\n";
                }

                page += line;
            }

            pages.Add(page);
            return pages;
        }

        /// <summary>The pages as the wall shows them now.</summary>
        public static List<string> Pages() =>
            Pages(Parse(FraysSettings.Text(FraysSettings.TrophyWallState)), FraysSettings.Int(FraysSettings.TrophyWallCount));
    }
}

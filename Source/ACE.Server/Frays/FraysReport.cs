using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Frays
{
    /// <summary>The camp report (/frays report): built when a camp ends, saved on the character, ASCII only.</summary>
    public static class FraysReport
    {
        public static string Build(string room, FraysCamper c, double now, double hours, string reason)
        {
            var minutes = (now - c.StartedAt) / 60.0;
            var lines = new List<string>
            {
                $"Camp report - {room}, {minutes:0} minutes ({reason}).",
                $"Heat: started {c.StartLevel}, peak {c.PeakLevel}, ended {c.Level}.",
                "Heat over time: " + HeatLine(c.HeatLog),
                $"Kills: {c.Kills} ({c.Kills / hours:0} an hour). Health removed: {c.HealthRemoved:0} reference creatures.",
                $"Embers: {Math.Floor(c.EmbersEarned):0} ({c.EmbersEarned / hours:0} an hour). XP {c.XpPaid:N0}, lum {c.LumPaid:N0}.",
                LootLine(c.LootDropped),
                $"Deaths: {c.Deaths}" + (c.DeathCauses.Count > 0 ? " - " + string.Join(", ", c.DeathCauses.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}")) : "") + ".",
                "Damage taken: " + (c.DamageTakenBy.Count == 0 ? "none" : string.Join(", ", c.DamageTakenBy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}"))) + ".",
                $"The Snarl: {c.SnarlKills} killed" + (c.SnarlTimes.Count > 0 ? $", fastest {c.SnarlTimes.Min():0} s, average {c.SnarlTimes.Average():0} s" : "") + $"; caches {c.CachesEarned}.",
                $"Vault Keys from your key bar: {c.KeyCaches}.",
            };

            return string.Join("\n", lines);
        }

        /// <summary>A plain name for a dropped item's kind, for the report's loot line.</summary>
        public static string LootKind(ACE.Entity.Enum.ItemType type)
        {
            if ((type & (ACE.Entity.Enum.ItemType.MeleeWeapon | ACE.Entity.Enum.ItemType.MissileWeapon | ACE.Entity.Enum.ItemType.Caster)) != 0)
                return "weapons";
            if ((type & (ACE.Entity.Enum.ItemType.Armor | ACE.Entity.Enum.ItemType.Clothing)) != 0)
                return "armor";
            if ((type & ACE.Entity.Enum.ItemType.Jewelry) != 0)
                return "jewelry";
            if ((type & ACE.Entity.Enum.ItemType.Gem) != 0)
                return "gems";
            if ((type & ACE.Entity.Enum.ItemType.Writable) != 0)
                return "scrolls";
            return "other";
        }

        /// <summary>Pure: "Loot dropped: 2,412 items - weapons 600, armor 500, ..." (what dropped, not just what was kept).</summary>
        public static string LootLine(IReadOnlyDictionary<string, int> dropped)
        {
            var total = dropped?.Values.Sum() ?? 0;

            if (total == 0)
                return "Loot dropped: nothing.";

            return $"Loot dropped: {total:N0} items - " + string.Join(", ", dropped.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}"))
                + ".";
        }

        /// <summary>
        /// The run lines: Ember Boons, the run score against the best before it, and a
        /// small Heat chart - what an unattended player reads when they come back.
        /// </summary>
        public static string RunLines(FraysCamper c, long score, long bestBefore)
        {
            var lines = new List<string>
            {
                $"Run score: {score:N0}" + (bestBefore <= 0 ? " (your first)." : score > bestBefore ? $" - a new best (was {bestBefore:N0})!" : $" (your best: {bestBefore:N0})."),
                $"Ember Boons: {(c.Boons.Count == 0 ? "none" : string.Join(", ", c.Boons.Select(k => FraysBoons.Find(k)?.Name)))}.",
            };

            lines.Add("Heat chart: " + HeatChart(c.HeatLog, c.PeakLevel));
            return string.Join("\n", lines);
        }

        /// <summary>Pure: up to 24 columns of the Heat over the camp as ASCII bar heights 0-9 ("0123455678899"), scaled to the peak.</summary>
        public static string HeatChart(IReadOnlyList<(double Minute, int Level)> log, int peak)
        {
            if (log == null || log.Count == 0 || peak <= 0)
                return "none";

            var cols = Math.Min(24, log.Count);
            var chars = new char[cols];

            for (var i = 0; i < cols; i++)
            {
                var at = log[(int)Math.Round((double)i * (log.Count - 1) / Math.Max(1, cols - 1))].Level;
                chars[i] = (char)('0' + (int)Math.Round(9.0 * Math.Clamp(at, 0, peak) / peak, MidpointRounding.AwayFromZero));
            }

            return $"{new string(chars)} (9 = Heat {peak}, over {log[^1].Minute:0} minutes)";
        }

        /// <summary>The Heat at every 10 minutes (at most 12 points): "0m 3, 10m 7, 20m 9".</summary>
        public static string HeatLine(IReadOnlyList<(double Minute, int Level)> log)
        {
            if (log == null || log.Count == 0)
                return "none";

            var step = Math.Max(10, Math.Ceiling(log[^1].Minute / 12 / 10) * 10);
            var points = new List<string>();
            var next = 0.0;

            foreach (var (minute, level) in log)
            {
                if (minute + 1e-9 >= next)
                {
                    points.Add($"{minute:0}m {level}");
                    next = minute + step;
                }
            }

            if (!points[^1].StartsWith($"{log[^1].Minute:0}m", StringComparison.Ordinal))
                points.Add($"{log[^1].Minute:0}m {log[^1].Level}");

            return string.Join(", ", points);
        }
    }
}

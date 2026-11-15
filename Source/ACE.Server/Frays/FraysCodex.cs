using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>One achievement: what earns it, and what it gives.</summary>
    public sealed class FraysAchievement
    {
        public string Key;
        public string Name;
        public string How;
        public Func<FraysPlayerRecord, bool> Done;
        /// <summary>An Epithet it gives (null: none).</summary>
        public string Epithet;
        /// <summary>Any other reward, given once (null: none). Returns the reward text.</summary>
        public Func<Player, FraysPlayerRecord, string> Reward;
    }

    /// <summary>
    /// The Codex, achievements and Fray Epithets: a collection to complete, with exclusive titles. The Codex fills in as you play - every creature family fought, boon taken and elite affix
    /// met; families also count as collected when their Fray Effigy is used. Achievements give Epithets (server-written
    /// names shown on your campfire, in the Fray feed and on boards: no .dat), perk points and Kindling. The Campfire
    /// Hearthstone comes with your first campfire, not an achievement.
    /// </summary>
    public static class FraysCodex
    {
        public const string Family = "family:", Rare = "rare:", Boon = "boon:", Affix = "affix:", Effigy = "effigy:";

        public static int Count(FraysPlayerRecord rec, string kind) => rec?.Codex?.Count(e => e.StartsWith(kind, StringComparison.Ordinal)) ?? 0;

        public static int FamiliesTotal => FraysFamilies.All.Count;

        public static readonly IReadOnlyList<FraysAchievement> Achievements = new List<FraysAchievement>
        {
            new FraysAchievement { Key = "heat5", Name = "First Light", How = "reach Heat 5", Done = r => Math.Max(r.BestHeat, r.BestGroupHeat) >= 5 },
            new FraysAchievement { Key = "heat10", Name = "Kindler", How = "reach Heat 10", Done = r => Math.Max(r.BestHeat, r.BestGroupHeat) >= 10, Epithet = "the Kindler" },
            new FraysAchievement { Key = "heat20", Name = "Ashwalker", How = "reach Heat 20", Done = r => Math.Max(r.BestHeat, r.BestGroupHeat) >= 20, Epithet = "the Ashen" },
            new FraysAchievement { Key = "snarl25", Name = "Snarlbane", How = "slay 25 Snarls", Done = r => r.SnarlKills >= 25, Epithet = "Snarlbane" },
            new FraysAchievement { Key = "codex10", Name = "Field Notes", How = "fight 10 creature families", Done = r => Count(r, Family) >= 10,
                Reward = (p, r) => { FraysLongTerm.Give(p, FraysLongTerm.Kindling, 3); return "3 Kindling"; } },
            new FraysAchievement { Key = "codex25", Name = "Naturalist", How = "fight 25 creature families", Done = r => Count(r, Family) >= 25,
                Reward = (p, r) => { r.BonusPerkPoints++; return "a perk point"; } },
            new FraysAchievement { Key = "codex50", Name = "Loremaster", How = "fight 50 creature families", Done = r => Count(r, Family) >= 50, Epithet = "the Loremaster" },
            new FraysAchievement { Key = "collector", Name = "The Collector", How = "collect a Fray Effigy of every family", Done = r => FamiliesTotal > 0 && Count(r, Effigy) >= FamiliesTotal, Epithet = "the Collector" },
            new FraysAchievement { Key = "score10k", Name = "Inferno Run", How = "a run score of 10,000", Done = r => r.BestRunScore >= 10_000, Epithet = "the Inferno" },
        };

        /// <summary>Adds a Codex entry and counts it once more. True when it is new (the caller tells the player).</summary>
        public static bool Note(FraysPlayerRecord rec, string entry)
        {
            if (entry == null)
                return false;

            rec.Codex ??= new HashSet<string>();
            rec.CodexCounts ??= new Dictionary<string, int>();
            rec.CodexCounts[entry] = Times(rec, entry) + 1;
            return rec.Codex.Add(entry);
        }

        /// <summary>How many times a Codex entry happened (kills, or boons taken). An entry from before the counts began reads as 1.</summary>
        public static int Times(FraysPlayerRecord rec, string entry) =>
            rec?.CodexCounts != null && rec.CodexCounts.TryGetValue(entry, out var n) ? n : rec?.Codex?.Contains(entry) == true ? 1 : 0;

        private static string Killed(FraysPlayerRecord rec, string entry, string word = "killed")
        {
            var n = Times(rec, entry);
            return n > 0 ? $"{word} {n:N0}" : null;
        }

        /// <summary>A collected form's companion line: its style and level, and whether it is the active one.</summary>
        private static string CompanionNote(FraysPlayerRecord rec, string key)
        {
            if (rec.Companions == null || !rec.Companions.TryGetValue(key, out var form) || string.IsNullOrEmpty(form.Role))
                return $"no style yet (/frays companion {key} warder, mender or striker)";

            return $"{form.Role} level {FraysCompanions.Level(form.Xp)}{(rec.ActiveCompanion == key ? " - active" : "")}";
        }

        /// <summary>Awards every achievement newly done. Returns what to tell the player (empty: none).</summary>
        public static List<string> Check(Player player, FraysPlayerRecord rec)
        {
            var said = new List<string>();
            rec.Achievements ??= new HashSet<string>();
            rec.Epithets ??= new List<string>();

            foreach (var a in Achievements)
            {
                if (rec.Achievements.Contains(a.Key) || !a.Done(rec))
                    continue;

                rec.Achievements.Add(a.Key);
                var gifts = new List<string>();

                if (a.Epithet != null && !rec.Epithets.Contains(a.Epithet))
                {
                    rec.Epithets.Add(a.Epithet);
                    rec.Epithet = a.Epithet;   // the newest is shown unless the player picks
                    gifts.Add($"the Epithet \"{a.Epithet}\"");
                }

                if (a.Reward != null && player != null)
                    gifts.Add(a.Reward(player, rec));

                said.Add($"Achievement: {a.Name} ({a.How}){(gifts.Count > 0 ? " - you earn " + string.Join(" and ", gifts) : "")}.");
            }

            return said;
        }

        /// <summary>/fray codex.</summary>
        public static string Text(FraysPlayerRecord rec)
        {
            var seen = FraysFamilies.All.Where(f => rec.Codex.Contains(Family + f.Key)).Select(f => f.Name).ToList();
            var missing = FraysFamilies.All.Where(f => !rec.Codex.Contains(Family + f.Key)).Select(f => f.Name).Take(12).ToList();
            var lines = new List<string>
            {
                $"Your Fray Codex: families fought {seen.Count} of {FamiliesTotal}, collected {Count(rec, Effigy)} of {FamiliesTotal}; boons {Count(rec, Boon)} of {FraysBoons.All.Count}; weekly affixes {Count(rec, Affix)} of {FraysWards.All.Count}.",
                missing.Count > 0 ? $"Not yet fought: {string.Join(", ", missing)}{(FamiliesTotal - seen.Count > missing.Count ? ", ..." : "")}." : "Every family fought.",
                $"Achievements {rec.Achievements.Count} of {Achievements.Count}: " + string.Join("; ", Achievements.Select(a => $"{a.Name} ({a.How}){(rec.Achievements.Contains(a.Key) ? " - done" : "")}")) + ".",
                rec.Epithets.Count > 0 ? $"Epithets: {string.Join(", ", rec.Epithets)}; shown: {(string.IsNullOrEmpty(rec.Epithet) ? "none" : rec.Epithet)} (/fray epithet <name> | none)." : "No Epithets yet: achievements give them.",
            };

            return string.Join("\n", lines);
        }

        /// <summary>
        /// /fray codex [section]: laid out like /xpaugs and /lumaugs - a title with the count done,
        /// then Complete and Incomplete lists. No section: one line a section, then the achievements in full.
        /// Sections: families, forms, companions, boons, affixes, achievements.
        /// </summary>
        public static string Readout(FraysPlayerRecord rec, string section)
        {
            rec.Codex ??= new HashSet<string>();
            rec.Achievements ??= new HashSet<string>();
            var families = FraysFamilies.All;

            var sections = new List<(string Key, string Title, List<(string Name, bool Done, string Note)> Rows)>
            {
                // How many times you killed each, and a companions list
                ("families", "Families fought", families.Select(f => (f.Name, rec.Codex.Contains(Family + f.Key), Killed(rec, Family + f.Key))).ToList()),
                ("forms", "Forms collected (Fray Effigies)", families.Select(f => (f.Name, rec.Codex.Contains(Effigy + f.Key), (string)"/fray pet " + f.Key)).ToList()),
                ("companions", "Companions", families.Where(f => rec.Codex.Contains(Effigy + f.Key))
                    .Select(f => (f.Name, rec.Companions != null && rec.Companions.TryGetValue(f.Key, out var form) && !string.IsNullOrEmpty(form.Role), CompanionNote(rec, f.Key))).ToList()),
                ("boons", "Ember Boons taken", FraysBoons.All.Select(b => (b.Name, rec.Codex.Contains(Boon + b.Key), Times(rec, Boon + b.Key) > 0 ? $"taken {Times(rec, Boon + b.Key):N0} - {b.Text}" : b.Text)).ToList()),
                ("affixes", "Weekly affixes met", FraysWards.All.Select(a => (a.Name, rec.Codex.Contains(Affix + a.Name), Killed(rec, Affix + a.Name) is string k ? $"{k} - {a.Text}" : a.Text)).ToList()),
                ("achievements", "Achievements", Achievements.Select(a => (a.Name, rec.Achievements.Contains(a.Key), (string)(a.How + (a.Epithet != null ? $" (Epithet: {a.Epithet})" : "")))).ToList()),
            };

            var lines = new List<string>();
            var pick = sections.FirstOrDefault(s => s.Key.StartsWith((section ?? "").Trim().ToLowerInvariant(), StringComparison.Ordinal) && (section ?? "").Trim().Length > 0);

            if (pick.Rows == null)
            {
                lines.Add($"--- Fray Codex --- {sections.Sum(s => s.Rows.Count(r => r.Done))} of {sections.Sum(s => s.Rows.Count)} complete");

                foreach (var s in sections)
                    lines.Add($"  {s.Title} ({s.Rows.Count(r => r.Done)}/{s.Rows.Count}) - /fray codex {s.Key}");

                pick = sections.Last();
            }

            var complete = pick.Rows.Where(r => r.Done).ToList();
            var incomplete = pick.Rows.Where(r => !r.Done).ToList();
            lines.Add($"--- {pick.Title} --- {complete.Count} of {pick.Rows.Count} complete");

            void Section(string heading, List<(string Name, bool Done, string Note)> rows)
            {
                if (rows.Count == 0)
                    return;

                lines.Add(heading);
                lines.AddRange(rows.Select(r => string.IsNullOrEmpty(r.Note) ? $"  {r.Name}" : $"  {r.Name} - {r.Note}"));
            }

            Section("Complete:", complete);
            Section("Incomplete:", incomplete);

            if (pick.Key == "achievements")
                lines.Add(rec.Epithets.Count > 0 ? $"Epithets: {string.Join(", ", rec.Epithets)}; shown: {(string.IsNullOrEmpty(rec.Epithet) ? "none" : rec.Epithet)} (/fray epithet <name> | none)." : "No Epithets yet: achievements give them.");

            return string.Join("\n", lines);
        }

        // ---- the campfire's look ----------------------------------------------------------------------------

        public static readonly IReadOnlyDictionary<string, PlayScript> SparkColours = new Dictionary<string, PlayScript>
        {
            ["red"] = PlayScript.EnchantUpRed, ["orange"] = PlayScript.EnchantUpOrange, ["gold"] = PlayScript.EnchantUpYellow,
            ["green"] = PlayScript.EnchantUpGreen, ["blue"] = PlayScript.EnchantUpBlue, ["purple"] = PlayScript.EnchantUpPurple,
            // Bought with Sky Seals (FraysSky.SparksUnlock).
            [FraysSky.SkySpark] = PlayScript.EnchantUpWhite,
        };

        private static readonly string[] Blocked = { "fuck", "shit", "cunt", "nigg", "fag", "rape", "retard", "whore", "slut", "kike", "spic", "chink", "nazi", "hitler" };

        /// <summary>Pure: a campfire name a player may set (24 characters, letters, spaces, hyphens and apostrophes, no blocked word), or null.</summary>
        public static string CleanFireName(string text)
        {
            var t = Regex.Replace((text ?? "").Trim(), @"\s+", " ");

            if (t.Length < 3 || t.Length > 24 || !Regex.IsMatch(t, @"^[A-Za-z][A-Za-z '\-]*$"))
                return null;

            var flat = Regex.Replace(t.ToLowerInvariant(), @"[^a-z]", "");
            return Blocked.Any(b => flat.Contains(b)) ? null : t;
        }

        /// <summary>A lit fire's name: the player's own name for it, or the room's, then the owner and their Epithet.</summary>
        public static string LitName(string room, string owner, FraysPlayerRecord rec)
        {
            var fire = string.IsNullOrEmpty(rec?.FireName) ? $"Frays Campfire - {room}" : $"{rec.FireName} - {room}";
            var who = string.IsNullOrEmpty(rec?.Epithet) ? owner : $"{owner} {rec.Epithet}";
            return $"{fire} ({who})";
        }
    }
}

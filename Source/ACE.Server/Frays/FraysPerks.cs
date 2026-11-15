using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>One campfire perk: a branch, how many ranks it takes, and what a rank does.</summary>
    public sealed class FraysPerkDef
    {
        public string Key;
        public string Name;
        public string Branch;
        public int MaxRank;
        public Func<int, string> Text;
    }

    /// <summary>
    /// The campfire's perk tree (perks, more rewards, an upgradeable campfire). Each campfire
    /// tier (/frays fire upgrade, Embers) is one perk point, spent with /frays perk; one free reset a week. No perk
    /// makes Heat come faster (the time to a Heat level is no reward): Fire raises how high Heat goes, and
    /// Heat pays on the Threads XP ladder (<see cref="FraysCurve.Income(int, int)"/>), so a higher cap pays.
    /// </summary>
    public static class FraysPerks
    {
        public const string Fire = "Fire", Hoard = "Hoard", Pyre = "Pyre";

        public static readonly IReadOnlyList<FraysPerkDef> All = new List<FraysPerkDef>
        {
            new FraysPerkDef { Key = "higherflame", Name = "Higher Flame", Branch = Fire, MaxRank = 5, Text = r => $"+{r} Heat cap" },
            new FraysPerkDef { Key = "emberhearth", Name = "Ember Hearth", Branch = Hoard, MaxRank = 5, Text = r => $"+{5 * r}% Embers" },
            new FraysPerkDef { Key = "snarllure", Name = "Snarl Lure", Branch = Hoard, MaxRank = 3, Text = r => $"your Snarl's bar fills {5 * r}% faster (all Snarl speed together is capped at +{FraysSettings.Number(FraysSettings.SnarlSpeedCap) * 100:0.#}%)" },
            new FraysPerkDef { Key = "bellows", Name = "Bellows", Branch = Pyre, MaxRank = 1, Text = r => $"about {Math.Round(100 * FraysSettings.Number(FraysSettings.BellowsPackShare))}% more creatures an hour, at any Heat" },
            new FraysPerkDef { Key = "searing", Name = "Searing", Branch = Pyre, MaxRank = 5, Text = r => $"+{2 * r}% damage" },
            new FraysPerkDef { Key = "ashguard", Name = "Ash Guard", Branch = Pyre, MaxRank = 5, Text = r => $"{2 * r}% less damage taken" },
        };

        /// <summary>Perks taken out of the tree. A point spent on one comes back to spend again (Spent counts only the tree);
        /// <see cref="Apply"/> drops the old rank from the record.</summary>
        public static readonly string[] Retired = { "richcoals" };

        /// <summary>Drops retired perks from a record, so their points are free again. Returns true when one was dropped.</summary>
        public static bool DropRetired(FraysPlayerRecord rec)
        {
            var dropped = false;

            foreach (var key in Retired)
                dropped |= rec?.Perks != null && rec.Perks.Remove(key);

            return dropped;
        }

        public static FraysPerkDef Find(string key)
        {
            var k = (key ?? "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
            return All.FirstOrDefault(p => p.Key == k) ?? All.FirstOrDefault(p => p.Key.StartsWith(k, StringComparison.Ordinal) && k.Length >= 3);
        }

        public static int Rank(FraysPlayerRecord rec, string key) =>
            rec?.Perks != null && rec.Perks.TryGetValue(key, out var r) ? Math.Clamp(r, 0, Find(key)?.MaxRank ?? 0) : 0;

        /// <summary>Perk points: one a campfire tier.</summary>
        public static int Points(FraysPlayerRecord rec) => Math.Max(0, (rec?.FireTier ?? 0) + (rec?.BonusPerkPoints ?? 0));

        public static int Spent(FraysPlayerRecord rec) => All.Sum(p => Rank(rec, p.Key));

        public static int Unspent(FraysPlayerRecord rec) => Math.Max(0, Points(rec) - Spent(rec));

        /// <summary>Heat levels Higher Flame adds above heat_cap.</summary>
        public static int CapBonus(FraysPlayerRecord rec) => Rank(rec, "higherflame");

        /// <summary>A player's Heat cap: heat_cap plus Higher Flame.</summary>
        public static int CapFor(FraysPlayerRecord rec) => FraysSettings.Int(FraysSettings.HeatCap) + CapBonus(rec);

        /// <summary>The perks on a camper's modifiers (called from <see cref="FraysLongTerm.Apply"/> every camp tick).</summary>
        public static void Apply(FraysPlayerRecord rec, FraysCampMods m)
        {
            DropRetired(rec);
            m.Embers *= 1 + 0.05 * Rank(rec, "emberhearth");
            m.SnarlBarSpeed *= 1 + 0.05 * Rank(rec, "snarllure");   // 5% a rank (was 15%)
            m.PackShare += Rank(rec, "bellows") * FraysSettings.Number(FraysSettings.BellowsPackShare);   // about +10% creatures, outside the rest scaling
            // The Frays pools: Searing and Ash Guard are 2% a rank, 10% at their 5 ranks.
            m.BonusDamage += 0.02 * Rank(rec, "searing");
            m.BonusReduction += 0.02 * Rank(rec, "ashguard");

        }

        /// <summary>Spends one point on <paramref name="key"/>. Returns what to tell the player.</summary>
        public static string Spend(FraysPlayerRecord rec, string key)
        {
            var def = Find(key);

            if (def == null)
                return $"No perk called {key}. /frays perk lists them.";

            var rank = Rank(rec, def.Key);

            if (rank >= def.MaxRank)
                return $"{def.Name} is at its highest rank ({def.MaxRank}).";

            if (Unspent(rec) <= 0)
                return $"No perk points left. Each campfire tier gives one: /frays fire upgrade.";

            rec.Perks ??= new Dictionary<string, int>();
            rec.Perks[def.Key] = rank + 1;
            return $"{def.Name} rank {rank + 1}: {def.Text(rank + 1)}. It applies from your next camp tick. {Unspent(rec)} point{(Unspent(rec) == 1 ? "" : "s")} left.";
        }

        /// <summary>Takes every point back, free once a week.</summary>
        public static string Reset(FraysPlayerRecord rec, int week)
        {
            if (Spent(rec) == 0)
                return "You have no perks to reset.";

            if (rec.PerkResetWeek == week)
                return "You have used this week's free perk reset. It comes back at the weekly reset.";

            rec.Perks?.Clear();
            rec.PerkResetWeek = week;
            return $"Your perks are reset: {Points(rec)} point{(Points(rec) == 1 ? "" : "s")} to spend again (/frays perk <name>). Your next free reset is next week.";
        }

        /// <summary>/frays perk and /frays fire: the tier, the points and every perk.</summary>
        public static string Text(FraysPlayerRecord rec, int week)
        {
            var max = FraysSettings.Int(FraysSettings.FireTierMax);
            var next = rec.FireTier >= max ? "highest tier" : $"tier {rec.FireTier + 1} costs {FraysSettings.Int(FraysSettings.FireTierCost) * (rec.FireTier + 1)} Embers (/frays fire upgrade)";
            var lines = new List<string>
            {
                $"Campfire tier {rec.FireTier} of {max} ({next}). Perk points: {Unspent(rec)} to spend of {Points(rec)}. Heat cap {CapFor(rec)}. Reset: {(rec.PerkResetWeek == week ? "used this week" : "free this week")} (/frays perk reset).",
            };

            foreach (var branch in All.GroupBy(p => p.Branch))
            {
                lines.Add($"{branch.Key}: " + string.Join("; ", branch.Select(p =>
                {
                    var r = Rank(rec, p.Key);
                    return $"{p.Name} ({p.Key}) {r}/{p.MaxRank}" + (r > 0 ? $" - {p.Text(r)}" : $" - next: {p.Text(1)}");
                })) + ".");
            }

            lines.Add("Spend a point: /frays perk <name>, e.g. /frays perk searing.");
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Perk presets: new perk points spend themselves in this order unless the preset is "off", so an
        /// unattended player's campfire still grows. Each list is filled one rank at a time, round and round.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
        {
            ["balanced"] = new[] { "higherflame", "searing", "ashguard", "emberhearth", "bellows", "snarllure" },
            ["aoe"] = new[] { "bellows", "searing", "ashguard", "higherflame", "emberhearth", "snarllure" },
            ["single"] = new[] { "searing", "higherflame", "ashguard", "emberhearth", "snarllure", "bellows" },
            ["embers"] = new[] { "emberhearth", "higherflame", "searing", "snarllure", "ashguard", "bellows" },
        };

        /// <summary>Spends every unspent point by the record's preset. Returns the perks bought (empty when off or none).</summary>
        public static List<string> AutoSpend(FraysPlayerRecord rec)
        {
            var bought = new List<string>();

            if (rec == null || !Presets.TryGetValue(rec.PerkPreset ?? "", out var order))
                return bought;

            var progress = true;

            while (Unspent(rec) > 0 && progress)
            {
                progress = false;

                foreach (var key in order)
                {
                    var def = Find(key);

                    if (Unspent(rec) <= 0 || def == null || Rank(rec, key) >= def.MaxRank)
                        continue;

                    rec.Perks ??= new Dictionary<string, int>();
                    rec.Perks[key] = Rank(rec, key) + 1;
                    bought.Add(def.Name);
                    progress = true;
                }
            }

            return bought;
        }

        /// <summary>The flame's size for the owner's tier: 6% bigger a tier.</summary>
        public static double FlameScale(int tier) => 1 + 0.06 * Math.Max(0, tier);

        /// <summary>The flame scale for whoever lit a fire (1 when unknown or offline).</summary>
        public static double FlameScaleFor(uint ownerGuid) =>
            Managers.PlayerManager.GetOnlinePlayer(ownerGuid) is Player p ? FlameScale(FraysManager.Record(p).FireTier) : 1;
    }
}

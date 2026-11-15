using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The boon collection (the long-term build).
    /// A boon joins a player's collection the first time they pick it in a camp (the Codex already notes it). Sigils, sold
    /// by the Boonwright for Embers, raise a collected boon a rank: a Lesser Sigil to rank 2, a Greater to 3, a Grand to 4.
    /// Each rank adds half the rank 1 value (1x, 1.5x, 2x, 2.5x). Rare boons stop at rank 2; Phoenix Coal and Undying Flame
    /// are on or off, so they stay at rank 1. A camp's boons always apply at the picker's rank.
    /// </summary>
    public static class FraysCollection
    {
        public const uint LesserSigil = 2000170;
        public const uint GreaterSigil = 2000171;
        public const uint GrandSigil = 2000172;

        public static readonly uint[] Sigils = { LesserSigil, GreaterSigil, GrandSigil };

        public static bool Collected(FraysPlayerRecord rec, string key) => rec?.Codex != null && rec.Codex.Contains(FraysCodex.Boon + key);

        /// <summary>The player's rank in a boon: 1 until a Sigil raises it (an uncollected boon picked in a camp is rank 1 too).</summary>
        public static int Rank(FraysPlayerRecord rec, string key)
        {
            var b = FraysBoons.Find(key);

            if (b == null || rec?.BoonRanks == null || !rec.BoonRanks.TryGetValue(key, out var r))
                return 1;

            return Math.Clamp(r, 1, b.MaxRank);
        }

        /// <summary>The Sigil that raises a boon from <paramref name="rank"/> (0 at its top rank).</summary>
        public static uint SigilFor(FraysBoonDef b, int rank) => b == null || b.IsClass || rank >= b.MaxRank || rank < 1 || rank > 3 ? 0 : Sigils[rank - 1];

        public static string SigilName(uint wcid) => wcid switch
        {
            LesserSigil => "Lesser Sigil",
            GreaterSigil => "Greater Sigil",
            GrandSigil => "Grand Sigil",
            _ => "Sigil",
        };

        /// <summary>A boon by key or by (part of) its name: "wildfire", "Wildfire Heart", "snarl's bane".</summary>
        public static FraysBoonDef Match(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();

            if (t.Length == 0)
                return null;

            string Flat(string s) => new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());
            var f = Flat(t);

            return FraysBoons.All.FirstOrDefault(b => b.Key == t || Flat(b.Name) == f)
                ?? FraysBoons.All.FirstOrDefault(b => f.Length >= 3 && (Flat(b.Name).StartsWith(f) || b.Key.StartsWith(f)));
        }

        /// <summary>Pure: raises <paramref name="b"/> one rank on the record. Null when done, else why not.</summary>
        public static string RankUp(FraysPlayerRecord rec, FraysBoonDef b)
        {
            if (b == null)
                return "There is no such boon. /fray collection lists them.";

            if (!Collected(rec, b.Key))
                return $"{b.Name} is not in your collection yet. Pick it once in a camp first.";

            if (b.IsClass)
                return $"{b.Name} is a class boon: it grows with your rank in {FraysBoons.AbilityName(b)}, not with Sigils.";

            var r = Rank(rec, b.Key);

            if (r >= b.MaxRank)
                return b.MaxRank == 1 ? $"{b.Name} has no ranks; it is the same at any rank." : $"{b.Name} is already at its top rank ({b.MaxRank}).";

            rec.BoonRanks ??= new Dictionary<string, int>();
            rec.BoonRanks[b.Key] = r + 1;
            return null;
        }

        /// <summary>/fray sigil &lt;boon&gt;: spends the Sigil the next rank needs and raises the boon.</summary>
        public static string UseSigil(Player player, string text)
        {
            var rec = FraysManager.Record(player);
            var b = Match(text);

            if (b == null)
                return "Name a boon: /fray sigil <boon>, for example /fray sigil wildfire. /fray collection lists yours.";

            var r = Rank(rec, b.Key);
            var sigil = SigilFor(b, r);

            // Check every reason first so a Sigil is never spent for nothing.
            var test = new FraysPlayerRecord { Codex = rec.Codex, BoonRanks = new Dictionary<string, int>(rec.BoonRanks ?? new Dictionary<string, int>()) };
            var why = RankUp(test, b);

            if (why != null)
                return why;

            if (!player.TryConsumeFromInventoryWithNetworking(sigil, 1))
                return $"Raising {b.Name} to rank {r + 1} takes a {SigilName(sigil)}. The Boonwright sells them for Embers.";

            RankUp(rec, b);
            FraysManager.SaveRecord(player, rec);
            FraysManager.Effect(player, ACE.Entity.Enum.PlayScript.EnchantUpOrange);
            return $"{b.Name} rises to rank {r + 1}: {b.TextAt(r + 1)}.";
        }

        /// <summary>The collection page.</summary>
        public static string Text(FraysPlayerRecord rec)
        {
            var have = FraysBoons.All.Where(b => Collected(rec, b.Key)).ToList();
            var lines = new List<string>
            {
                "YOUR BOON COLLECTION",
                "",
                $"{have.Count} of {FraysBoons.All.Count} boons found. A boon joins the first time you pick it in a camp; in camps it works at your rank.",
                "Sigils raise a rank: Lesser to 2, Greater to 3, Grand to 4 (rares stop at 2). The Boonwright sells them.",
                "",
            };

            foreach (var b in FraysBoons.All)
            {
                if (!Collected(rec, b.Key))
                {
                    lines.Add($"- {b.Name}{(b.Rare ? " (rare)" : "")}: not found yet");
                    continue;
                }

                var r = Rank(rec, b.Key);
                var next = SigilFor(b, r);
                if (b.IsClass)
                {
                    lines.Add($"- {b.Name} (class: {FraysBoons.AbilityName(b)}): {b.Text} (less at lower ranks in {FraysBoons.AbilityName(b)})");
                    continue;
                }

                var more = next == 0 ? (b.MaxRank == 1 ? "" : " (top rank)") : $" (next: {SigilName(next)}, {b.TextAt(r + 1)})";
                lines.Add($"- {b.Name}{(b.Rare ? " (rare)" : "")} rank {r} of {b.MaxRank}: {b.TextAt(r)}{more}");
            }

            lines.Add("");
            lines.Add("/fray sigil <boon> spends a Sigil on one, for example /fray sigil wildfire.");
            return string.Join("\n", lines);
        }

        public static void Show(Player player) =>
            player.Session?.Network.EnqueueSend(new GameEventPopupString(player.Session, Text(FraysManager.Record(player))));
    }
}

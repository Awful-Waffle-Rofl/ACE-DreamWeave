using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Rolls a fresh, unbound DungeonGemSpec. Pure: the caller supplies the Random, so /dd give and any
    /// future gem source (PLAN 3.5) roll identically and tests are deterministic.
    /// </summary>
    public static class DungeonGemRoller
    {
        public static double RarityWeight(string rarity)
        {
            switch (rarity)
            {
                case "common": return 1.0;
                case "uncommon": return 0.35;
                case "rare": return 0.1;
                default: return 0.0;
            }
        }

        public static DungeonGemSpec Roll(int level, int tier, int modifierCount,
            IReadOnlyDictionary<string, ModifierDef> modifiers, IReadOnlyDictionary<string, DungeonEntryDef> dungeons,
            string dungeonId, string family, Random rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            level = Math.Clamp(level, 1, DungeonGemSpec.MaxLevel);
            tier = Math.Clamp(tier, 1, DungeonGemSpec.MaxTier);

            var dg = string.IsNullOrEmpty(dungeonId) ? DungeonGemSpec.Any : dungeonId;
            if (dg != DungeonGemSpec.Any)
            {
                if (!DungeonGemSpec.IsIdent(dg))
                    throw new ArgumentException($"dungeon '{dg}' must be 'any' or lowercase [a-z0-9_]+", nameof(dungeonId));
                if (!dungeons.TryGetValue(dg, out var entry))
                    throw new ArgumentException($"unknown dungeon '{dg}'", nameof(dungeonId));
                if (!entry.Enabled)
                    throw new ArgumentException($"dungeon '{dg}' is disabled", nameof(dungeonId));
            }

            var fam = string.IsNullOrEmpty(family) ? DungeonGemSpec.Any : family;
            if (fam != DungeonGemSpec.Any && !DungeonGemSpec.IsIdent(fam))
                throw new ArgumentException($"family '{fam}' must be 'any' or lowercase [a-z0-9_]+", nameof(family));

            // weighted draw without replacement over the non-boss pool
            var pool = modifiers.Values.Where(m => m.Target != "boss" && RarityWeight(m.Rarity) > 0).ToList();
            var picked = new List<(string, double)>();

            while (picked.Count < modifierCount && pool.Count > 0)
            {
                var total = pool.Sum(m => RarityWeight(m.Rarity));
                var roll = rng.NextDouble() * total;
                ModifierDef chosen = pool[pool.Count - 1];
                foreach (var m in pool)
                {
                    roll -= RarityWeight(m.Rarity);
                    if (roll <= 0) { chosen = m; break; }
                }
                pool.Remove(chosen);

                var mag = chosen.MinMagnitude + rng.NextDouble() * (chosen.MaxMagnitude - chosen.MinMagnitude);
                mag = Math.Round(mag, 2);
                mag = Math.Clamp(mag, chosen.MinMagnitude, chosen.MaxMagnitude);
                picked.Add((chosen.Id, mag));
            }

            var seed = rng.Next(1, int.MaxValue);

            return new DungeonGemSpec(dg, level, tier, fam, seed, picked, 0, 0);
        }
    }
}

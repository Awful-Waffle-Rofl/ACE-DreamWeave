using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One species family that stretches at a gem level: its chosen trash members, plus at most one elite, each
    /// mapped by rank onto the run's trash band [LowEdge, HighEdge].
    /// </summary>
    public sealed class StretchedFamily
    {
        public SpeciesTableDef Family { get; }
        public int LowEdge { get; }
        public int HighEdge { get; }
        public double Overscale { get; }
        public IReadOnlyList<uint> TrashPool { get; }
        public IReadOnlyList<uint> ElitePool { get; }
        public IReadOnlyDictionary<uint, int> TrashLevels { get; }
        public IReadOnlyDictionary<uint, int> EliteLevels { get; }
        public int MappedLow { get; }
        public int MappedHigh { get; }

        internal StretchedFamily(SpeciesTableDef family, int lowEdge, int highEdge, IReadOnlyList<uint> trashPool,
            IReadOnlyList<uint> elitePool, IReadOnlyDictionary<uint, int> trashLevels, IReadOnlyDictionary<uint, int> eliteLevels)
        {
            Family = family;
            LowEdge = lowEdge;
            HighEdge = highEdge;
            Overscale = family.Overscale;
            TrashPool = trashPool;
            ElitePool = elitePool;
            TrashLevels = trashLevels;
            EliteLevels = eliteLevels;

            var all = trashLevels.Values.Concat(eliteLevels.Values).ToList();
            MappedLow = all.Count == 0 ? 0 : all.Min();
            MappedHigh = all.Count == 0 ? 0 : all.Max();
        }

        /// <summary>The mapped level of a member in a role, 0 when absent. An elite falls back to its trash mapping.</summary>
        public int MappedLevelOf(uint wcid, DungeonRole role)
        {
            if (role == DungeonRole.Elite && EliteLevels.TryGetValue(wcid, out var e))
                return e;

            return TrashLevels.TryGetValue(wcid, out var t) ? t : 0;
        }
    }

    /// <summary>
    /// Thread family stretch: decides which species families stretch at a gem level (their strongest trash sits
    /// below the band) and maps each one's top trash members by rank onto the band. Pure.
    /// </summary>
    internal static class FamilyStretch
    {
        internal static bool Active(DungeonPopulationLimits limits) => limits.FamilyStretch && !limits.WidenForForcedFamily;

        internal static bool ShouldStretch(SpeciesTableDef raw, int level, Func<uint, int> projLevelOf, DungeonRosterBand band)
        {
            if (raw == null || !raw.Drawable || !raw.Stretches || raw.Members == null)
                return false;

            var max = 0;

            foreach (var m in raw.Members)
            {
                if (m == null || m.Role != 0)
                    continue;

                var l = projLevelOf(m.Wcid);

                if (l > max)
                    max = l;
            }

            return max > 0 && max < DungeonRosterSelector.TrashBand(level, band).Low;
        }

        internal static IReadOnlyList<(uint Wcid, int Level)> TopTrash(SpeciesTableDef family, Func<uint, int> levelOf, int n)
        {
            var result = new List<(uint Wcid, int Level)>();

            if (family?.Members == null || n <= 0)
                return result;

            var seen = new HashSet<uint>();

            foreach (var m in family.Members)
            {
                if (m == null || m.Role != 0)
                    continue;

                var l = levelOf(m.Wcid);

                if (l > 0 && seen.Add(m.Wcid))
                    result.Add((m.Wcid, l));
            }

            return result.OrderByDescending(x => x.Level).ThenBy(x => x.Wcid).Take(n).ToList();
        }

        internal static IReadOnlyDictionary<uint, int> MapLevels(IReadOnlyList<(uint Wcid, int Level)> chosen, int lowEdge, int highEdge)
        {
            var map = new Dictionary<uint, int>();

            if (chosen == null || chosen.Count == 0)
                return map;

            var hi = chosen.Max(c => c.Level);
            var lo = chosen.Min(c => c.Level);

            foreach (var (wcid, lvl) in chosen)
            {
                map[wcid] = hi == lo
                    ? lowEdge + (int)Math.Round((highEdge - lowEdge) / 2.0, MidpointRounding.AwayFromZero)
                    : lowEdge + (int)Math.Round((double)(lvl - lo) * (highEdge - lowEdge) / (hi - lo), MidpointRounding.AwayFromZero);
            }

            return map;
        }

        internal static StretchedFamily Resolve(SpeciesTableDef raw, SpeciesTableDef selection, int level, Func<uint, int> levelOf,
            Func<uint, int> projLevelOf, DungeonRosterBand band, int minFamilyPool)
        {
            if (selection == null || !ShouldStretch(raw, level, projLevelOf, band))
                return null;

            var chosen = TopTrash(selection, levelOf, Math.Max(1, minFamilyPool));

            if (chosen.Count == 0)
                return null;

            var edges = DungeonRosterSelector.TrashBand(level, band);
            var trashLevels = MapLevels(chosen, edges.Low, edges.High);
            var chosenSet = new HashSet<uint>(chosen.Select(c => c.Wcid));

            var pool = new List<uint>();

            foreach (var m in selection.Members)
            {
                if (m != null && m.Role == 0 && chosenSet.Contains(m.Wcid) && !pool.Contains(m.Wcid))
                    pool.Add(m.Wcid);
            }

            var elites = new List<uint>();
            var eliteLevels = new Dictionary<uint, int>();

            var elite = selection.Members
                .Where(m => m != null && (m.Role == 1 || m.Role == 2) && levelOf(m.Wcid) > 0 && levelOf(m.Wcid) <= edges.High)
                .Select(m => (m.Wcid, Level: levelOf(m.Wcid)))
                .OrderByDescending(x => x.Level).ThenBy(x => x.Wcid)
                .Cast<(uint Wcid, int Level)?>()
                .FirstOrDefault();

            if (elite != null)
            {
                elites.Add(elite.Value.Wcid);
                eliteLevels[elite.Value.Wcid] = edges.High;
            }

            return new StretchedFamily(raw, edges.Low, edges.High, pool, elites, trashLevels, eliteLevels);
        }

        internal static IReadOnlyList<StretchedFamily> ResolveAll(IReadOnlyDictionary<string, SpeciesTableDef> raw,
            IReadOnlyDictionary<string, SpeciesTableDef> selection, int level, Func<uint, int> levelOf, Func<uint, int> projLevelOf,
            DungeonRosterBand band, int minFamilyPool)
        {
            var result = new List<StretchedFamily>();

            if (raw == null || selection == null)
                return result;

            foreach (var id in raw.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!selection.TryGetValue(id, out var sel))
                    continue;

                var f = Resolve(raw[id], sel, level, levelOf, projLevelOf, band, minFamilyPool);

                if (f != null)
                    result.Add(f);
            }

            return result;
        }
    }
}

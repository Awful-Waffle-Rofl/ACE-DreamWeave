using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The trash draw every encounter that adds creatures shares (the Shrine's guardians, the Defense enemies): creatures
    /// drawn from the run plan's TRASH entries, their uplift and stamp levels carried over so they are shaped exactly as the
    /// run's own trash. Pure over the caller's Random.
    /// </summary>
    public static class ThreadEncounterTrash
    {
        /// <summary>The plan's trash entries (null entries and every other role skipped). Never null.</summary>
        public static List<DungeonSpawnPlanEntry> Pool(IReadOnlyList<DungeonSpawnPlanEntry> planEntries)
            => (planEntries ?? Array.Empty<DungeonSpawnPlanEntry>()).Where(e => e != null && e.Role == DungeonRole.Trash).ToList();

        /// <summary>One draw from <paramref name="pool"/> standing at <paramref name="point"/>. Null for an empty pool. One rng draw.</summary>
        public static DungeonSpawnPlanEntry Draw(IReadOnlyList<DungeonSpawnPlanEntry> pool, DungeonSpawnPointDef point, Random rng)
        {
            if (pool == null || pool.Count == 0 || rng == null)
                return null;

            var source = pool[rng.Next(pool.Count)];
            return new DungeonSpawnPlanEntry(source.Wcid, DungeonRole.Trash, point, source.UpliftLevel, source.StampLevel, source.Stretched);
        }
    }
}

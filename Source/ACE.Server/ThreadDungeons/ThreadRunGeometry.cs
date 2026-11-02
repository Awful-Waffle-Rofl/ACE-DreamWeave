using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Which landblocks a run's copy owns (Open World Threads R23). A dungeon run owns exactly one: its dungeon's
    /// landblock. An outdoor run owns every landblock of its site's footprint (OutdoorSiteDef.Footprint, which
    /// includes the primary), and nothing else: the buffer ring around it (loaded up front by adjacency loading, or on
    /// demand under dynamic_dungeons_outdoor_lazy_buffer_ring) is bare terrain under the same instance id, and is never
    /// "on the run".
    ///
    /// <para/>
    /// This is the ONE place that answers "is this position on the run's landblock(s)". Every presence, clearing,
    /// eviction, cache-placement and reward-placement check in ThreadDungeons goes through
    /// <see cref="IsOnRunLandblock"/>, so the dungeon and outdoor answers cannot drift apart. A source-scan
    /// tripwire (ThreadRunGeometryTests) refuses any raw <c>run.Dungeon.Landblock</c> equality left elsewhere in
    /// the folder.
    ///
    /// <para/>
    /// Everything here is pure (no LandblockManager, no Player), so it is unit tested directly. The instance id is
    /// NOT checked here: callers that ask "is this player in the run's copy" check the instance alongside, exactly
    /// as they did before (the run's copy and the public world share landblock ids).
    /// </summary>
    public static class ThreadRunGeometry
    {
        /// <summary>
        /// True when <paramref name="position"/> lies on a landblock the run's copy owns: the dungeon's landblock for
        /// a dungeon run, any footprint landblock for an outdoor run. False for a null run, dungeon or position.
        /// </summary>
        public static bool IsOnRunLandblock(ThreadDungeonRun run, Position position)
            => run?.Dungeon != null && position != null && IsRunLandblock(run.Dungeon, position.LandblockId.Landblock);

        /// <summary>The landblock form of <see cref="IsOnRunLandblock"/>: is <paramref name="landblock"/> (0xXXYY) one the copy owns?</summary>
        public static bool IsRunLandblock(DungeonEntryDef dungeon, ushort landblock)
        {
            if (dungeon == null)
                return false;

            var footprint = dungeon.IsOutdoor ? dungeon.Outdoor?.Footprint : null;

            // A dungeon row, or an outdoor row that somehow carries no footprint, owns its one landblock.
            if (footprint == null || footprint.Count == 0)
                return landblock == dungeon.Landblock;

            return landblock == dungeon.Landblock || footprint.Contains(landblock);
        }

        /// <summary>
        /// Every landblock the run's copy owns, the primary (dungeon.Landblock) FIRST and then each other footprint
        /// landblock in footprint order, with no repeats. A dungeon run's list is its one landblock. Empty for a null
        /// dungeon. This is the set the population gate waits on and the presence scans walk.
        /// </summary>
        public static IReadOnlyList<ushort> RunLandblocks(DungeonEntryDef dungeon)
        {
            var result = new List<ushort>();

            if (dungeon == null)
                return result;

            result.Add(dungeon.Landblock);

            var footprint = dungeon.IsOutdoor ? dungeon.Outdoor?.Footprint : null;

            if (footprint != null)
            {
                foreach (var landblock in footprint)
                {
                    if (!result.Contains(landblock))
                        result.Add(landblock);
                }
            }

            return result;
        }

        /// <summary>
        /// The population gate (R22): true only when EVERY landblock in <paramref name="runLandblocks"/> is loaded and
        /// has finished CreateWorldObjects. <paramref name="createCompleted"/> answers per landblock: null when that
        /// landblock is not loaded under the run's instance, else its CreateWorldObjectsCompleted. An empty list never
        /// passes (there is nothing to populate). For a dungeon the list is the one landblock, so this is exactly the
        /// old single-landblock check.
        /// </summary>
        public static bool AllCreateCompleted(IReadOnlyList<ushort> runLandblocks, Func<ushort, bool?> createCompleted)
        {
            if (runLandblocks == null || runLandblocks.Count == 0 || createCompleted == null)
                return false;

            foreach (var landblock in runLandblocks)
            {
                if (createCompleted(landblock) != true)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The run-end eviction set: which loaded landblocks EndRun must take players off. A dungeon run evicts from
        /// its primary only, exactly as before. An outdoor run evicts from the primary AND from every other landblock
        /// loaded under the run's instance - its footprint members and also any buffer ring or further bare copy a
        /// player has walked onto, so no player is left standing under an instance that is about to unregister. The
        /// primary comes first (when present), then the rest in <paramref name="loaded"/> order; no repeats.
        /// Generic so it is tested over fakes; EndRun passes Landblocks.
        /// </summary>
        public static List<T> EvictionSet<T>(bool isOutdoor, T primary, IEnumerable<T> loaded, uint instance, Func<T, uint> instanceOf) where T : class
        {
            var result = new List<T>();

            if (primary != null)
                result.Add(primary);

            if (!isOutdoor || loaded == null || instanceOf == null)
                return result;

            foreach (var item in loaded)
            {
                if (item == null || instanceOf(item) != instance)
                    continue;

                var seen = false;
                foreach (var r in result)
                {
                    if (ReferenceEquals(r, item)) { seen = true; break; }
                }

                if (!seen)
                    result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// The kind check TryStart applies to the copy it just loaded (R23 / P1: the IsDungeon refusal made kind-aware).
        /// A dungeon row must load a dungeon landblock (no traversable overworld: a private slice of the open world is
        /// never instanced by accident); an outdoor row must load an outdoor one (a dungeon id in an outdoor footprint
        /// is a content error).
        /// </summary>
        public static bool LoadedKindMatches(bool rowIsOutdoor, bool landblockIsDungeon) => rowIsOutdoor ? !landblockIsDungeon : landblockIsDungeon;
    }
}

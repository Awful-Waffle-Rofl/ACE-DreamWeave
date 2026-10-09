using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

using DatEnvironment = ACE.DatLoader.FileTypes.Environment;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Builds a dungeon's <see cref="DungeonWalkGraph"/> from the client cell dat and caches it per landblock id.
    ///
    /// SOURCE. The dat, not the run copy's loaded physics cells: a Thread run's copy is an instance of the same
    /// landblock, and every instance shares the dat's static geometry, so the graph is the same for every copy and
    /// for every run. Reading the dat needs no live landblock (the arming kill may land while the copy is busy) and no
    /// instance-aware cell lookup. The walk mirrors the offline tool's (ACE.Content.Tools DungeonReachCommand.
    /// CollectCellGraph over CellsCommand.GetPortalFaces): LandblockInfo.NumCells cells from 0x0100, each cell's
    /// CellPortals, and each portal's polygon from its Environment's CellStruct, transformed by the cell's frame into
    /// landblock-local coordinates. DatDatabase.ReadFromDat caches every file it reads, and the landblock's own
    /// physics load has already read these EnvCells, so a first build re-reads little.
    ///
    /// CACHE. Only built graphs are cached; a failure (no cell dat, a landblock with no cells) is retried next time.
    /// Static geometry, so a cached graph never goes stale.
    /// </summary>
    public static class DungeonWalkGraphSource
    {
        private static readonly ConcurrentDictionary<ushort, DungeonWalkGraph> Cache = new ConcurrentDictionary<ushort, DungeonWalkGraph>();

        /// <summary>A failed build per landblock: when, and why. Retried only after <see cref="FailureRetry"/>.</summary>
        private static readonly ConcurrentDictionary<ushort, (DateTime Utc, string Error)> Failures = new ConcurrentDictionary<ushort, (DateTime, string)>();

        /// <summary>
        /// How long a failed build is remembered. Long enough that one arming (the placer, then the notice) and the
        /// populate-time prebuild never pay for the same failure twice; short enough that a transient dat read error
        /// heals on its own.
        /// </summary>
        public static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(5);

        /// <summary>Test seam: the builder Get uses. Production reads DatManager's dats.</summary>
        internal static Func<ushort, (DungeonWalkGraph Graph, string Error)> Builder = landblock =>
        {
            var graph = Build(landblock, DatManager.CellDat, DatManager.PortalDat, out var error);
            return (graph, error);
        };

        /// <summary>
        /// The cached graph for <paramref name="landblock"/>, built on first use. Null with a reason; a failure is
        /// remembered for <see cref="FailureRetry"/> and answered from memory until then. ThreadPuzzlePass.Run calls
        /// this at populate for a run with a pending reward seal, so the arming kill only reads the cache.
        /// </summary>
        public static DungeonWalkGraph Get(ushort landblock, out string error)
        {
            if (Cache.TryGetValue(landblock, out var cached))
            {
                error = null;
                return cached;
            }

            if (Failures.TryGetValue(landblock, out var failed) && DateTime.UtcNow - failed.Utc < FailureRetry)
            {
                error = failed.Error;
                return null;
            }

            var (built, buildError) = Builder(landblock);
            error = buildError;

            if (built == null)
            {
                Failures[landblock] = (DateTime.UtcNow, buildError ?? "no graph");
                return null;
            }

            Failures.TryRemove(landblock, out _);
            return Cache.GetOrAdd(landblock, built);
        }

        /// <summary>Is a built graph cached for this landblock?</summary>
        public static bool IsCached(ushort landblock) => Cache.ContainsKey(landblock);

        /// <summary>Test seam: forget every cached graph and remembered failure.</summary>
        internal static void ClearCacheForTest()
        {
            Cache.Clear();
            Failures.Clear();
        }

        /// <summary>Builds the graph from the given dats. Never throws: null with the reason instead.</summary>
        public static DungeonWalkGraph Build(ushort landblock, CellDatDatabase cellDat, PortalDatDatabase portalDat, out string error)
        {
            error = null;

            if (cellDat == null || portalDat == null)
            {
                error = "the cell or portal dat is not loaded";
                return null;
            }

            try
            {
                var info = cellDat.ReadFromDat<LandblockInfo>(((uint)landblock << 16) | 0xFFFE);
                var count = (int)(info?.NumCells ?? 0);

                if (count <= 0)
                {
                    error = $"landblock 0x{landblock:X4} has no cells";
                    return null;
                }

                var cellIds = new List<uint>(count);
                var links = new List<DungeonWalkGraph.Portal>();

                for (var i = 0; i < count; i++)
                {
                    var cellId = ((uint)landblock << 16) | (uint)(0x100 + i);
                    cellIds.Add(cellId);

                    var cell = cellDat.ReadFromDat<EnvCell>(cellId);

                    if (cell == null || cell.CellPortals.Count == 0)
                        continue;

                    var env = portalDat.ReadFromDat<DatEnvironment>(cell.EnvironmentId);

                    if (env == null || !env.Cells.TryGetValue(cell.CellStructure, out var structure))
                        continue;

                    foreach (var portal in cell.CellPortals)
                    {
                        // 0xFFFF is the outside; anything else outside this landblock's cell range names no cell here.
                        if (portal.OtherCellId == ushort.MaxValue || portal.OtherCellId < 0x100 || portal.OtherCellId >= 0x100 + count)
                            continue;

                        if (!structure.Polygons.TryGetValue(portal.PolygonId, out var polygon) || polygon.VertexIds.Count == 0)
                            continue;

                        var sum = Vector3.Zero;
                        var n = 0;

                        foreach (var v in polygon.VertexIds)
                        {
                            if (!structure.VertexArray.Vertices.TryGetValue((ushort)v, out var vertex))
                                continue;

                            sum += cell.Position.Origin + Vector3.Transform(vertex.Origin, cell.Position.Orientation);
                            n++;
                        }

                        if (n == 0)
                            continue;

                        links.Add(new DungeonWalkGraph.Portal(cellId, ((uint)landblock << 16) | portal.OtherCellId, sum / n));
                    }
                }

                return new DungeonWalkGraph(cellIds, links);
            }
            catch (Exception ex)
            {
                error = $"reading landblock 0x{landblock:X4} from the dat threw {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
    }
}

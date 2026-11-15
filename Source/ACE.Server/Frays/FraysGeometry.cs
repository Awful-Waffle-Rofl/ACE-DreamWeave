using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

using DatEnvironment = ACE.DatLoader.FileTypes.Environment;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The boxes of a dungeon's EnvCells (landblock-local, from the cell dat's vertices), for the placement check.
    /// Static geometry, cached per landblock.
    /// </summary>
    public static class FraysGeometry
    {
        private static readonly ConcurrentDictionary<ushort, Dictionary<ushort, (Vector3 Min, Vector3 Max)>> cache =
            new ConcurrentDictionary<ushort, Dictionary<ushort, (Vector3, Vector3)>>();

        public static Dictionary<ushort, (Vector3 Min, Vector3 Max)> CellBoxes(ushort landblock, out string error)
        {
            error = null;

            if (cache.TryGetValue(landblock, out var cached))
                return cached;

            var cellDat = DatManager.CellDat;
            var portalDat = DatManager.PortalDat;

            if (cellDat == null || portalDat == null)
            {
                error = "the cell or portal dat is not loaded";
                return null;
            }

            try
            {
                var info = cellDat.ReadFromDat<LandblockInfo>(((uint)landblock << 16) | 0xFFFE);
                var boxes = new Dictionary<ushort, (Vector3, Vector3)>();

                for (var i = 0; i < (int)(info?.NumCells ?? 0); i++)
                {
                    var cellId = (ushort)(0x100 + i);
                    var cell = cellDat.ReadFromDat<EnvCell>(((uint)landblock << 16) | cellId);

                    if (cell == null)
                        continue;

                    var env = portalDat.ReadFromDat<DatEnvironment>(cell.EnvironmentId);

                    if (env == null || !env.Cells.TryGetValue(cell.CellStructure, out var structure) || structure.VertexArray.Vertices.Count == 0)
                        continue;

                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);

                    foreach (var v in structure.VertexArray.Vertices.Values)
                    {
                        var p = cell.Position.Origin + Vector3.Transform(v.Origin, cell.Position.Orientation);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }

                    boxes[cellId] = (min, max);
                }

                return cache.GetOrAdd(landblock, boxes);
            }
            catch (Exception ex)
            {
                error = $"reading landblock 0x{landblock:X4} threw {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>The boxes of a fire's room cells.</summary>
        public static List<(Vector3 Min, Vector3 Max)> RoomBoxes(FraysFireDef def, Dictionary<ushort, (Vector3 Min, Vector3 Max)> boxes) =>
            def.RoomCells.Where(boxes.ContainsKey).Select(c => boxes[c]).ToList();
    }
}

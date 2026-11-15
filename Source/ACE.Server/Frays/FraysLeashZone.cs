using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.Frays
{
    /// <summary>A doorway out of a fire's room: the portal between a room cell and a cell outside it.</summary>
    public readonly struct FraysDoorway
    {
        /// <summary>The room cell (low 16 bits of the cell id).</summary>
        public readonly ushort Inside;

        /// <summary>The cell beyond the doorway.</summary>
        public readonly ushort Outside;

        /// <summary>The portal polygon's centre, landblock-local.</summary>
        public readonly Vector3 Centre;

        public FraysDoorway(ushort inside, ushort outside, Vector3 centre)
        {
            Inside = inside;
            Outside = outside;
            Centre = centre;
        }
    }

    /// <summary>
    /// A fire's leash zone: the room's own EnvCells, plus a margin past each doorway, and never further than the max
    /// chase distance from the fire. Pure (cell ids and landblock-local positions), so it is unit tested; the live
    /// margin and chase distance come in per call, which is what lets an admin change them live.
    /// </summary>
    public sealed class FraysLeashZone
    {
        private readonly HashSet<ushort> cells;

        public IReadOnlyCollection<ushort> Cells => cells;

        public IReadOnlyList<FraysDoorway> Doorways { get; }

        /// <summary>The fire, landblock-local.</summary>
        public Vector3 Fire { get; }

        public FraysLeashZone(IEnumerable<ushort> roomCells, Vector3 fire, IEnumerable<(ushort From, ushort To, Vector3 Centre)> portals)
        {
            cells = new HashSet<ushort>(roomCells ?? Enumerable.Empty<ushort>());
            Fire = fire;

            var doors = new List<FraysDoorway>();

            foreach (var p in portals ?? Enumerable.Empty<(ushort, ushort, Vector3)>())
            {
                if (cells.Contains(p.From) && !cells.Contains(p.To))
                    doors.Add(new FraysDoorway(p.From, p.To, p.Centre));
            }

            Doorways = doors;
        }

        /// <summary>An outdoor zone: the whole landblock is the zone (the caller checks the landblock), with no chase limit.</summary>
        public bool WholeLandblock { get; init; }

        public bool HasCell(ushort cell) => cells.Contains(cell);

        /// <summary>
        /// True when a point in <paramref name="cell"/> at <paramref name="pos"/> (landblock-local) is inside the zone:
        /// in a room cell, or within <paramref name="margin"/> metres of one of the room's doorways, and in both cases
        /// within <paramref name="maxChase"/> metres of the fire (0 or less = no limit).
        /// </summary>
        public bool Contains(ushort cell, Vector3 pos, double margin, double maxChase)
        {
            if (WholeLandblock)
                return true;

            if (maxChase > 0 && Vector3.DistanceSquared(pos, Fire) > maxChase * maxChase)
                return false;

            if (cells.Contains(cell))
                return true;

            return NearDoorway(pos, margin);
        }

        /// <summary>True within <paramref name="margin"/> metres of a doorway's centre.</summary>
        public bool NearDoorway(Vector3 pos, double margin)
        {
            if (margin <= 0)
                return false;

            var m2 = (float)(margin * margin);

            foreach (var d in Doorways)
            {
                if (Vector3.DistanceSquared(pos, d.Centre) <= m2)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The smallest gap between two zones, from their cells' boxes (landblock-local, each grown by
        /// <paramref name="margin"/> for the doorway reach). 0 when they touch or overlap.
        /// </summary>
        public static double Gap(IEnumerable<(Vector3 Min, Vector3 Max)> a, IEnumerable<(Vector3 Min, Vector3 Max)> b, double margin)
        {
            var best = double.MaxValue;
            var bl = b.ToList();
            var grow = new Vector3((float)margin);

            foreach (var x in a)
            {
                var xMin = x.Min - grow;
                var xMax = x.Max + grow;

                foreach (var y in bl)
                {
                    var yMin = y.Min - grow;
                    var yMax = y.Max + grow;

                    var dx = Math.Max(0, Math.Max(yMin.X - xMax.X, xMin.X - yMax.X));
                    var dy = Math.Max(0, Math.Max(yMin.Y - xMax.Y, xMin.Y - yMax.Y));
                    var dz = Math.Max(0, Math.Max(yMin.Z - xMax.Z, xMin.Z - yMax.Z));

                    var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    if (d < best)
                        best = d;
                }
            }

            return best == double.MaxValue ? double.PositiveInfinity : best;
        }
    }
}

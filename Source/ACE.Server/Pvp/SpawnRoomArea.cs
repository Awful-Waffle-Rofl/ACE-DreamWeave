using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;

namespace ACE.Server.Pvp
{
    /// <summary>An axis-aligned rectangle in landblock-local x/y metres (edges inclusive).</summary>
    public sealed record SpawnRoomRect(float MinX, float MaxX, float MinY, float MaxY);

    /// <summary>
    /// One team's spawn-room footprint: the rectangles its floor cells cover and the z band a standing player's feet are in
    /// (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection", "Trusting the position").
    /// </summary>
    public sealed record SpawnRoomFootprint(IReadOnlyList<SpawnRoomRect> Rects, float ZMin, float ZMax);

    /// <summary>
    /// A team's spawn room as the spawn-protection rule sees it, immutable and shared by every binding of the team. A position is IN the room
    /// only when its reported cell is one of <see cref="Cells"/> AND, when a footprint is given, its x/y lie inside one of the rectangles and
    /// its z inside the band. The cell id and the coordinates are both client-reported (the server commits the client's position), so the cell
    /// alone would let a modified client report a room cell while standing elsewhere and stay protected for good; the footprint bounds the lie
    /// to the room itself. One function, used at every read: the gate, the damage-over-time hook and the exit check.
    /// </summary>
    public sealed class SpawnRoomArea
    {
        public IReadOnlySet<uint> Cells { get; }

        public IReadOnlyList<SpawnRoomRect> Rects { get; }

        public float ZMin { get; }

        public float ZMax { get; }

        public SpawnRoomArea(IReadOnlySet<uint> cells, IReadOnlyList<SpawnRoomRect> rects = null, float zMin = float.NegativeInfinity, float zMax = float.PositiveInfinity)
        {
            Cells = cells ?? new HashSet<uint>();
            Rects = rects ?? Array.Empty<SpawnRoomRect>();
            ZMin = zMin;
            ZMax = zMax;
        }

        /// <summary>True when <paramref name="at"/> is inside this room. Null is outside. Reads the position's fields once each.</summary>
        public bool Contains(Position at)
        {
            if (at == null || !Cells.Contains(at.Cell))
                return false;

            if (Rects.Count == 0)
                return true;

            var x = at.PositionX;
            var y = at.PositionY;
            var z = at.PositionZ;

            if (!(z >= ZMin && z <= ZMax))
                return false;

            for (var i = 0; i < Rects.Count; i++)
            {
                var r = Rects[i];

                if (x >= r.MinX && x <= r.MaxX && y >= r.MinY && y <= r.MaxY)
                    return true;
            }

            return false;
        }
    }
}
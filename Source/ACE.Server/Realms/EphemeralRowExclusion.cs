using System;
using System.Collections.Generic;

using ACE.Database.Models.World;

namespace ACE.Server.Realms
{
    /// <summary>
    /// A per-copy box of landblock_instance rows an ephemeral copy does NOT load: every row on <see cref="Landblock"/>
    /// whose origin lies inside the inclusive box [MinX..MaxX] x [MinY..MaxY] x [MinZ..MaxZ] (landblock-local
    /// coordinates, the row's origin_X/Y/Z). Carried on <see cref="EphemeralRealm.ExcludedRows"/> and applied by
    /// Landblock.CreateWorldObjects before the factory runs, so an excluded row never becomes an object and is never
    /// resolved as a link child of a parent that is kept (link resolution reads the same filtered list). Used by the
    /// Matron Knot copy of 0x586B to drop the retail queen room.
    /// </summary>
    public sealed record EphemeralRowExclusion(ushort Landblock, float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ)
    {
        /// <summary>True when the row is on this landblock and its origin is inside the inclusive box.</summary>
        public bool Contains(LandblockInstance row)
        {
            if (row == null)
                return false;

            if ((row.ObjCellId >> 16) != Landblock)
                return false;

            return row.OriginX >= MinX && row.OriginX <= MaxX
                && row.OriginY >= MinY && row.OriginY <= MaxY
                && row.OriginZ >= MinZ && row.OriginZ <= MaxZ;
        }

        /// <summary>
        /// Returns a NEW list holding every row the box does not contain, in the input order, and the number dropped.
        /// Never mutates <paramref name="rows"/>: the world db's cached instance list for a landblock is shared by every
        /// copy of it (DatabaseManager.World.GetCachedInstancesByLandblock).
        /// </summary>
        public static List<LandblockInstance> Filter(List<LandblockInstance> rows, EphemeralRowExclusion ex, out int dropped)
        {
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentNullException.ThrowIfNull(ex);

            var kept = new List<LandblockInstance>(rows.Count);
            dropped = 0;

            foreach (var row in rows)
            {
                if (ex.Contains(row))
                    dropped++;
                else
                    kept.Add(row);
            }

            return kept;
        }
    }
}

using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Models;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// One creature a wave roster asks for: a wcid at an absolute landblock-frame point. Pure data - the
    /// instance is supplied only when the spawner turns it into a <see cref="Position"/>.
    /// </summary>
    public readonly struct WaveRosterEntry
    {
        public WaveRosterEntry(uint wcid, uint objCellId, float x, float y, float z, float rx, float ry, float rz, float rw)
        {
            Wcid = wcid;
            ObjCellId = objCellId;
            X = x;
            Y = y;
            Z = z;
            RotationX = rx;
            RotationY = ry;
            RotationZ = rz;
            RotationW = rw;
        }

        public uint Wcid { get; }
        public uint ObjCellId { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float RotationX { get; }
        public float RotationY { get; }
        public float RotationZ { get; }
        public float RotationW { get; }

        /// <summary>The landblock half of the cell id (the high 16 bits).</summary>
        public ushort Landblock => (ushort)(ObjCellId >> 16);

        /// <summary>The spawn point in <paramref name="instance"/>, same argument order Player_WaveChallenge uses.</summary>
        public Position ToPosition(uint instance) => new Position(ObjCellId, X, Y, Z, RotationX, RotationY, RotationZ, RotationW, instance);
    }

    /// <summary>
    /// Reads a wave roster weenie's generator rows into spawn entries. LIFTED from the roster read in
    /// Player_WaveChallenge.SpawnWave (the Proving Grounds wave run, which is left untouched): the roster
    /// weenie is pure data - never placed, never a working generator - and each generator row is one
    /// creature at an absolute obj_Cell_Id/origin/angles (where_Create 4, Specific).
    ///
    /// PURE: no world, no database, no logging. The rows handed in come straight out of the world weenie
    /// cache and are SHARED by every reader of that weenie, so this only ever reads them.
    ///
    /// Two rejections, both reported rather than silently dropped so the caller can log the content bug:
    ///   - wcid 0 (an empty row);
    ///   - a row whose cell is on another landblock than the anchor's. Spawn_Specific makes the same check;
    ///     a creature placed there would stand outside the encounter entirely (and the presence scan and the
    ///     cleanup would never find it).
    /// </summary>
    public static class WaveRoster
    {
        public static List<WaveRosterEntry> Read(IEnumerable<PropertiesGenerator> rows, ushort anchorLandblock, List<string> rejections = null)
        {
            var entries = new List<WaveRosterEntry>();

            if (rows == null)
                return entries;

            var index = 0;

            foreach (var row in rows)
            {
                var rowIndex = index++;

                if (row == null)
                    continue;

                if (row.WeenieClassId == 0)
                {
                    rejections?.Add($"row {rowIndex}: wcid 0");
                    continue;
                }

                var cell = row.ObjCellId ?? 0;

                if ((ushort)(cell >> 16) != anchorLandblock)
                {
                    rejections?.Add($"row {rowIndex}: wcid {row.WeenieClassId} cell 0x{cell:X8} is not on the anchor landblock 0x{anchorLandblock:X4}");
                    continue;
                }

                entries.Add(new WaveRosterEntry(row.WeenieClassId, cell,
                    row.OriginX ?? 0, row.OriginY ?? 0, row.OriginZ ?? 0,
                    row.AnglesX ?? 0, row.AnglesY ?? 0, row.AnglesZ ?? 0, row.AnglesW ?? 1));
            }

            return entries;
        }

        /// <summary>
        /// The roster weenie for <paramref name="wave"/> (1-based): base + wave - 1, the same wcid arithmetic
        /// the Proving Grounds uses. 0 for a non-positive wave or a missing base.
        /// </summary>
        public static uint RosterWcid(uint rosterBaseWcid, int wave)
            => rosterBaseWcid == 0 || wave < 1 ? 0 : rosterBaseWcid + (uint)(wave - 1);
    }
}

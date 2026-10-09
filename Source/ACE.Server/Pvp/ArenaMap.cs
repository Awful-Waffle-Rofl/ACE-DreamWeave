using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One authored spawn location inside an <see cref="ArenaMap"/>, in the shape an ACE Position needs.
    ///   - <see cref="CellLow"/> is the LOW 16 bits of the cell id (an EnvCell, 0x0100 and up). The high 16 bits
    ///     come from the map's landblock, so one point serves every map that shares a geometry.
    ///   - X, Y, Z are landblock-local coordinates, exactly as /loc prints them.
    ///   - The rotation is a pure yaw: a quaternion about Z with X = Y = 0, carried as (W, Z).
    /// Build the Position with <see cref="ArenaSpawnPosition.Build"/>, which also stamps the instance id.
    /// </summary>
    public sealed record PvpSpawnPoint(string Label, ushort CellLow, float X, float Y, float Z, float RotationW, float RotationZ);

    /// <summary>
    /// A candidate match space: which landblock and realm to instance, and its authored spawn sets.
    ///   - <see cref="LandblockId"/> is the 16-bit landblock number (0x0066), not a cell id.
    ///   - <see cref="SpawnSets"/> is keyed by mode key ("1v1", "2v2", "ffa"), in the order the points are
    ///     authored. ArenaMapCatalog owns the real maps and the seating rules.
    /// </summary>
    public sealed record ArenaMap(string MapKey, uint LandblockId, ushort RealmId, IReadOnlyDictionary<string, IReadOnlyList<PvpSpawnPoint>> SpawnSets)
    {
        /// <summary>The player-facing name ("Arena I"). Falls back to <see cref="MapKey"/> when not set.</summary>
        public string DisplayName
        {
            get => displayName ?? MapKey;
            init => displayName = value;
        }

        private readonly string displayName;

        /// <summary>The spawn set for a mode, or an empty list if this map has none for it. Never null.</summary>
        public IReadOnlyList<PvpSpawnPoint> SpawnPointsFor(string modeKey)
        {
            if (SpawnSets != null && modeKey != null && SpawnSets.TryGetValue(modeKey, out var set) && set != null)
                return set;

            return Array.Empty<PvpSpawnPoint>();
        }
    }
}

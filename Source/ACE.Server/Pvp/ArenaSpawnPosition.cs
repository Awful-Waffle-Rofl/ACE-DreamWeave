using System;

using ACE.Entity;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Builds the ACE <see cref="Position"/> a participant is teleported to, inside a match's ephemeral instance.
    ///
    /// <para/>
    /// The shape matches how the existing ephemeral-instance callers carry the instance: Portal's instanced
    /// branch copies the destination into the new instance with <c>new Position(portalDest, landblock.Instance)</c>
    /// (Portal.cs:572), and Threads builds a zero-instance entry position (ThreadDungeonManager.TryParseEntry) and
    /// then stamps <c>entry.Instance = instance</c>. Here the instance goes straight into the constructor, whose
    /// argument order is (cell, x, y, z, qx, qy, qz, qw, instance) - the rotation W is LAST
    /// (ACE.Entity/Position.cs:246). Because the cell's low 16 bits are never 0 for an EnvCell, the constructor
    /// does not re-derive the cell from the coordinates (it only does that for an outdoor cell of 0).
    ///
    /// <para/>
    /// Player.Teleport adds its own 0.005 * scale to Z on arrival (Player_Location.cs:865), on top of the
    /// authored 0.005 - the same as every retail arena destination it already serves.
    /// </summary>
    public static class ArenaSpawnPosition
    {
        /// <summary>
        /// The position for <paramref name="point"/> on <paramref name="map"/> inside ephemeral instance
        /// <paramref name="instance"/>. Pure. Throws <see cref="ArgumentException"/> for a caller bug - an instance
        /// that is not ephemeral or not in the map's realm, a landblock number wider than 16 bits, or a cell below
        /// the EnvCell range - because a wrong instance here is exactly the silent reroute into the shared-world
        /// copy that InstanceRouting warns about, and it must fail loudly in tests rather than at the teleport.
        /// </summary>
        public static Position Build(ArenaMap map, PvpSpawnPoint point, uint instance)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));

            if (point == null)
                throw new ArgumentNullException(nameof(point));

            if (map.LandblockId == 0 || map.LandblockId > 0xFFFF)
                throw new ArgumentException($"map {map.MapKey}: LandblockId 0x{map.LandblockId:X} is not a 16-bit landblock number", nameof(map));

            if (point.CellLow < 0x0100)
                throw new ArgumentException($"spawn {point.Label}: cell 0x{point.CellLow:X4} is not an EnvCell (0x0100 and up)", nameof(point));

            Position.ParseInstanceID(instance, out var isEphemeral, out var realmId, out _);

            if (!isEphemeral)
                throw new ArgumentException($"instance 0x{instance:X8} is not an ephemeral instance", nameof(instance));

            if (realmId != map.RealmId)
                throw new ArgumentException($"instance 0x{instance:X8} is in realm {realmId}, map {map.MapKey} is realm {map.RealmId}", nameof(instance));

            var cell = (map.LandblockId << 16) | point.CellLow;

            return new Position(cell, point.X, point.Y, point.Z, 0f, 0f, point.RotationZ, point.RotationW, instance);
        }
    }
}

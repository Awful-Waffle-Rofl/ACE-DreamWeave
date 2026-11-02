using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Where the client sees an object, for the client object ledger (teleport_flush_scope): its own Location, or
    /// its wielder's when it is wielded (a wielded item's Location is cleared when it is equipped). A contained item
    /// has neither, and the ledger never files it - the legacy teleport flush never deleted contents either.
    /// </summary>
    public static class ClientObjectPlacement
    {
        public static bool TryGet(WorldObject worldObject, out ushort landblock, out uint instance)
        {
            landblock = 0;
            instance = 0;

            if (worldObject == null)
                return false;

            var position = worldObject.Wielder != null ? worldObject.Wielder.Location : worldObject.Location;

            if (position == null)
                return false;

            landblock = (ushort)(position.Cell >> 16);
            instance = position.Instance;
            return true;
        }
    }
}

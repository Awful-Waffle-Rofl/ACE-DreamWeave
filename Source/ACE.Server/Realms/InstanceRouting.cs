using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Realms
{
    /// <summary>
    /// How a destination position (which carries no instance of its own, e.g. a
    /// portal destination from the world database) picks the instance it lands in
    /// </summary>
    public enum PlayerInstanceSelectMode
    {
        /// <summary>The default instance of the player's home realm</summary>
        HomeRealmDefault,

        /// <summary>The instance the player is currently in</summary>
        Same,
    }

    public static class InstanceRouting
    {
        /// <summary>
        /// Returns a copy of this position routed into the instance selected by mode.
        /// </summary>
        public static Position AsInstancedPosition(this Position pos, Player player, PlayerInstanceSelectMode mode)
        {
            uint instance;

            switch (mode)
            {
                case PlayerInstanceSelectMode.Same:
                    instance = player.Location.Instance;
                    break;

                case PlayerInstanceSelectMode.HomeRealmDefault:
                default:
                    var realm = RealmManager.GetRealm(player.HomeRealm) ?? RealmManager.BaseRealm;
                    instance = realm.DefaultInstanceID;
                    break;
            }

            return new Position(pos, instance);
        }

        /// <summary>
        /// Validates that a player may arrive at this position's instance: the realm
        /// must be registered, and an ephemeral instance must be live and accept the
        /// player. Returns the position rerouted to the home realm's default instance
        /// (same coordinates) if not.
        /// </summary>
        public static Position ValidateInstanceDestination(this Position pos, Player player)
        {
            Position.ParseInstanceID(pos.Instance, out var isEphemeral, out var realmId, out _);

            if (isEphemeral)
            {
                var landblock = LandblockManager.GetEphemeralLandblockUnsafe(pos.Instance);
                if (landblock != null && landblock.Id.Landblock == (pos.Cell >> 16) && (landblock.InnerRealmInfo?.Accepts(player) ?? false))
                    return pos;

                return pos.AsInstancedPosition(player, PlayerInstanceSelectMode.HomeRealmDefault);
            }

            if (realmId != 0 && RealmManager.GetRealm(realmId) == null)
                return pos.AsInstancedPosition(player, PlayerInstanceSelectMode.HomeRealmDefault);

            return pos;
        }
    }
}

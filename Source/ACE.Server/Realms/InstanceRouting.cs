using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

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

    /// <summary>
    /// Why <see cref="InstanceRouting.ValidateInstanceDestination(Position, Player, out InstanceRejection)"/>
    /// refused a destination's instance. Every value other than <see cref="None"/> means the returned
    /// position is NOT the one that was passed in - it has been rerouted to the player's home realm default
    /// instance, i.e. the same coordinates in a different (usually shared-world) copy of the landblock.
    /// </summary>
    public enum InstanceRejection
    {
        /// <summary>The destination was accepted unchanged.</summary>
        None = 0,

        /// <summary>A non-ephemeral instance naming a realm id that is not in the realm registry.</summary>
        RealmNotRegistered,

        /// <summary>An ephemeral instance with no loaded landblock - it expired, unloaded, or never existed.</summary>
        EphemeralInstanceNotLive,

        /// <summary>
        /// The ephemeral instance is live but hosts a DIFFERENT landblock than the destination cell names.
        /// An ephemeral instance is a private copy of exactly one landblock, so this is a routing bug in the
        /// caller, not an expiry.
        /// </summary>
        EphemeralLandblockMismatch,

        /// <summary>
        /// The ephemeral instance is live and hosts the right landblock, but its
        /// <see cref="EphemeralRealm.Accepts(Player)"/> refused this player (not the owner, not admitted,
        /// not fellowed in, or the instance is past its expiry), or it carries no realm info at all.
        /// </summary>
        EphemeralAccessDenied,
    }

    public static class InstanceRouting
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

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
        /// Pure: TRUE when a move from <paramref name="from"/> to <paramref name="to"/> stays inside ONE
        /// landblock id but changes instance. Such a hop renders as a blend of both copies' content, because the
        /// physics object maintenance (Physics/Common/ObjectMaint.cs) has no concept of Instance and the client
        /// sees no landblock change - the Town Network and the Drift hub both being landblock 0x0007 is where it
        /// was first seen (Content/realms/driftnetwork_entry.sql). Any instance kinds; see
        /// <see cref="IsPersistentSameLandblockRealmHop"/> for the persistent-only form. Null on either side is
        /// never a hop.
        /// </summary>
        public static bool IsSameLandblockCrossInstanceHop(Position from, Position to)
        {
            if (from == null || to == null)
                return false;

            return from.LandblockShort == to.LandblockShort && from.Instance != to.Instance;
        }

        /// <summary>
        /// Pure: the persistent-to-persistent form of <see cref="IsSameLandblockCrossInstanceHop"/> - TRUE only
        /// when BOTH ends are non-ephemeral instances (a realm's default copy, e.g. realm 0's Aerfalle Keep and
        /// realm 1's Marketplace, both landblock 0x01F5). An ephemeral end is left alone on purpose: instanced
        /// portals legitimately hop from the hub into a private copy of the landblock the player is standing in,
        /// and the ephemeral-realm machinery (exit stamps, instance validation) owns those moves.
        ///
        /// Used to refuse the recalls and portals that could otherwise hop a player between realm copies of one
        /// landblock (Marketplace recall, lifestone recall, portal recall, Portal.ActOnUse). Death respawn is
        /// deliberately NOT refused on it - a dead player must always go somewhere - it only logs.
        /// </summary>
        public static bool IsPersistentSameLandblockRealmHop(Position from, Position to)
        {
            if (!IsSameLandblockCrossInstanceHop(from, to))
                return false;

            return !from.IsEphemeralRealm && !to.IsEphemeralRealm;
        }

        /// <summary>The player-facing refusal for <see cref="IsPersistentSameLandblockRealmHop"/>.</summary>
        public const string SameLandblockRealmHopRefusal = "You cannot travel there from here: you are standing in another reflection of the same place. Move away from here first.";

        /// <summary>
        /// Validates that a player may arrive at this position's instance: the realm
        /// must be registered, and an ephemeral instance must be live and accept the
        /// player. Returns the position rerouted to the home realm's default instance
        /// (same coordinates) if not.
        /// </summary>
        public static Position ValidateInstanceDestination(this Position pos, Player player)
        {
            return pos.ValidateInstanceDestination(player, out _);
        }

        /// <summary>
        /// As <see cref="ValidateInstanceDestination(Position, Player)"/>, but also reports WHY the
        /// destination was refused, so a caller can decide that a reroute is not an acceptable outcome for
        /// its path.
        ///
        /// <para/>
        /// The reroute itself is not a neutral fallback: it keeps the coordinates and swaps the instance, so
        /// a refused private-instance destination becomes the SHARED-WORLD copy of that landblock. For a
        /// landblock whose content lives entirely in a realm overlay - every Proving Grounds arena, the
        /// Loom, a Thread - the shared-world copy has no monsters, no NPCs and no exit portal, and
        /// a player dropped there has to /die to get out. That is why every reroute is logged at WARN with
        /// the failing condition named: the same silent reroute cost hours of diagnosis on prod, because
        /// nothing anywhere recorded that it had happened.
        ///
        /// <para/>
        /// The reroute is nevertheless the right answer on the LOGIN path
        /// (WorldManager.DoPlayerEnterWorld), which is the reason this method still performs it rather than
        /// simply failing: a character who logged out inside an ephemeral instance has a saved location that
        /// is guaranteed invalid after a restart, and refusing there would strand them permanently. The
        /// teleport path has no such constraint - a refused teleport just leaves the player where they
        /// already were - so <see cref="Player.Teleport"/> refuses instead of accepting the reroute.
        /// </summary>
        public static Position ValidateInstanceDestination(this Position pos, Player player, out InstanceRejection rejection)
        {
            rejection = InstanceRejection.None;

            Position.ParseInstanceID(pos.Instance, out var isEphemeral, out var realmId, out _);

            if (isEphemeral)
            {
                var landblock = LandblockManager.GetEphemeralLandblock(pos.Instance);

                if (landblock == null)
                    rejection = InstanceRejection.EphemeralInstanceNotLive;
                else if (landblock.Id.Landblock != (pos.Cell >> 16))
                    rejection = InstanceRejection.EphemeralLandblockMismatch;
                else if (!(landblock.InnerRealmInfo?.Accepts(player) ?? false))
                    rejection = InstanceRejection.EphemeralAccessDenied;

                if (rejection == InstanceRejection.None)
                    return pos;

                return Reroute(pos, player, rejection);
            }

            if (realmId != 0 && RealmManager.GetRealm(realmId) == null)
            {
                rejection = InstanceRejection.RealmNotRegistered;
                return Reroute(pos, player, rejection);
            }

            return pos;
        }

        /// <summary>
        /// Builds the home-realm-default reroute and records it. Every caller of this is a case where the
        /// player does not end up where the caller asked for, so it is never silent.
        /// </summary>
        private static Position Reroute(Position pos, Player player, InstanceRejection rejection)
        {
            var rerouted = pos.AsInstancedPosition(player, PlayerInstanceSelectMode.HomeRealmDefault);

            log.Warn($"InstanceRouting: refusing instance 0x{pos.Instance:X8} for {player?.Name ?? "(null player)"} " +
                     $"(0x{(player?.Guid.Full ?? 0):X8}) at landblock 0x{(pos.Cell >> 16):X4} - {rejection}. " +
                     $"Rerouting to instance 0x{rerouted.Instance:X8} (home realm {player?.HomeRealm ?? 0}), " +
                     $"which is the same coordinates in a different copy of that landblock and may have no content.");

            return rerouted;
        }
    }
}

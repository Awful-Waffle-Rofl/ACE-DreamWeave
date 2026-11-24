using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The Salvage Roller station's open flow (Docs/SalvageRoller/DESIGN.md 5.1, 5.4 step 1). The placed Marketplace
    /// object (wcid 1007800, PropertyBool.SalvageRollerStation) IS the vendor: WorldObjectFactory builds it as a
    /// <see cref="SalvageRollerVendor"/>, whose ActOnUse calls <see cref="Open(Player, SalvageRollerVendor)"/>. Every
    /// player gets their own view on that one object; nothing is spawned. A Give is intercepted in
    /// Player.GiveObjectToNPC.
    /// </summary>
    public static class SalvageRollerStation
    {
        /// <summary>Told by the station when something is handed to it. Provisional text: the owner reviews it.</summary>
        public const string GiveRefusedMessage = "I don't take things by hand. Use me instead, then sell me your item and its Hammers.";

        /// <summary>
        /// A Give onto the station: refused with a tell. The caller has already sent the ServerSaveFailed that
        /// bounces the item back into the pack, so nothing is taken. <paramref name="tell"/> defaults to the
        /// station's own Tell and is a seam for tests.
        /// </summary>
        public static void HandleGive(Player player, WorldObject station, WorldObject item, Action<WorldObject, Player, string> tell = null)
        {
            if (player == null || station == null)
                return;

            (tell ?? Tell)(station, player, GiveRefusedMessage);
        }

        /// <summary>Opens the player's view on <paramref name="station"/> with the production fill and panel steps.</summary>
        public static void Open(Player player, SalvageRollerVendor station)
        {
            Open(player, station, SalvageRollerManager.OnStationOpened, (p, s) => s.ApproachVendor(p, VendorType.Open));
        }

        /// <summary>
        /// The open flow with the outside steps injected, in this order: sweep the station's views; drop any view the
        /// player holds on ANOTHER station; create or touch the player's view here; point the player's slot at this
        /// station; run the manager seam <paramref name="onOpened"/> (reconcile and row fill); and finally
        /// <paramref name="approach"/> (the vendor panel). Nothing is ever spawned, so a second Use refills the same
        /// view. False only when there is no player or no live station.
        /// </summary>
        internal static bool Open(Player player, SalvageRollerVendor station,
            Action<Player, SalvageRollerVendor> onOpened,
            Action<Player, SalvageRollerVendor> approach)
        {
            if (player == null || station == null || station.IsDestroyed)
                return false;

            station.SweepViews(Time.GetUnixTime());

            var previous = player.CurrentSalvageRollerStation;

            if (previous != null && !ReferenceEquals(previous, station))
                previous.DropView(player);

            station.OpenView(player.Guid.Full);

            player.CurrentSalvageRollerStation = station;

            onOpened(player, station);
            approach(player, station);

            return true;
        }

        /// <summary>
        /// True when the station's landblock is neither the viewer's landblock nor one adjacent to it. Unknown (null)
        /// on either side is not stale: a station not yet in the world, or a viewer mid-move, is left alone.
        /// </summary>
        internal static bool IsStale(Landblock stationLandblock, Landblock viewerLandblock)
        {
            if (stationLandblock == null || viewerLandblock == null)
                return false;

            if (ReferenceEquals(stationLandblock, viewerLandblock))
                return false;

            return viewerLandblock.Adjacents == null || !viewerLandblock.Adjacents.Contains(stationLandblock);
        }

        /// <summary>The exact shape EmoteType.Tell sends, as the sibling stations' Tell does.</summary>
        private static void Tell(WorldObject npc, Player player, string message)
        {
            if (player?.Session == null || message == null)
                return;

            player.Session.Network.EnqueueSend(new GameEventTell(npc, message, player, ChatMessageType.Tell));
        }
    }
}

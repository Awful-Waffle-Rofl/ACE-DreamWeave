using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class VendorStressCommands
    {
        /// <summary>
        /// Mule Vendor, live verification 17.1: measures the CLIENT's vendor-panel ceiling, which is
        /// unmeasured and is the one open question that can still change the mule vendor design (a low
        /// ceiling makes paging mandatory, flipping a stated non-goal). The server imposes no cap -
        /// GameEventApproachVendor's numItems is an unbounded int over a buffer that grows on demand -
        /// so any vendor exercises the identical path and no mule code is needed to get the number.
        ///
        /// Diagnostic only. The injected items are ordinary generic stock: they live in
        /// DefaultItemsForSale, are owned by the vendor, and are destroyed with it. Injections
        /// ACCUMULATE on the vendor object - nothing about walking away resets DefaultItemsForSale,
        /// so repeated runs stack rather than replace. To clear a vendor between measurements, use
        /// /reload-landblock (or a server restart), which destroys and recreates the Vendor object.
        /// </summary>
        [CommandHandler("vendorstress", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Injects N synthetic items into the vendor you last opened and re-sends its panel.",
            "<count> [wcid]\nCount is the number of DISTINCT lines to add, 1 to 20000. Default wcid is 273, an Iron Key.")]
        public static void HandleVendorStress(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (!int.TryParse(parameters[0], out var count) || count < 1 || count > 20000)
            {
                ChatPacket.SendServerMessage(session, "Count must be between 1 and 20000.", ChatMessageType.Broadcast);
                return;
            }

            var wcid = 273u;

            if (parameters.Length > 1 && !uint.TryParse(parameters[1], out wcid))
            {
                ChatPacket.SendServerMessage(session, "Bad wcid.", ChatMessageType.Broadcast);
                return;
            }

            var vendor = player.CurrentLandblock?.GetObject(player.LastOpenedContainerId) as Vendor;

            if (vendor == null)
            {
                ChatPacket.SendServerMessage(session, "Open a vendor panel first, then run this while standing at it.", ChatMessageType.Broadcast);
                return;
            }

            var added = 0;

            for (var i = 0; i < count; i++)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

                if (wo == null)
                    break;

                // A distinct name per line, so the client is rendering N different rows rather than
                // collapsing identical ones. The distinct case is the one the panel actually has to
                // handle for a vault, where every stored biota is its own line.
                wo.Name = $"{wo.Name} {i:D5}";
                wo.ContainerId = vendor.Guid.Full;
                wo.CalculateObjDesc();

                vendor.DefaultItemsForSale.Add(wo.Guid, wo);
                added++;
            }

            ChatPacket.SendServerMessage(session, $"Injected {added}. Panel now has {vendor.DefaultItemsForSale.Count + vendor.UniqueItemsForSale.Count} lines.", ChatMessageType.Broadcast);

            vendor.ApproachVendor(player, VendorType.Undef);
        }
    }
}

using System.Collections.Generic;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;

namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionSellItems
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [GameAction(GameActionType.Sell)]
        public static void Handle(ClientMessage message, Session session)
        {
            var vendorGuid = message.Payload.ReadUInt32();
            var numItems = message.Payload.ReadUInt32();

            if (numItems > ItemProfile.MaxProfilesPerTransaction)
            {
                log.Warn($"[VENDOR] {session.Player?.Name ?? "<no player>"} (account {session.AccountId}) sent {numItems} item profiles, above the {ItemProfile.MaxProfilesPerTransaction} ceiling. Dropping the message.");

                // Answer the client rather than dropping the action on the floor. Every other refusal on
                // this path sends this same pair before returning (Player_Commerce.cs:25-46), and
                // GameEventUseDone is the protocol's "this use action is finished" signal - without it the
                // vendor panel waits on a response that never comes. Cheap and de-amplifying: an oversized
                // packet in, two small messages out.
                var player = session.Player;

                if (player != null)
                {
                    session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(session, player.Guid.Full));
                    player.SendUseDoneEvent(WeenieError.ActionCancelled);
                }

                return;
            }

            var items = new List<ItemProfile>();

            for (var i = 0; i < numItems; i++)
            {
                var amount = message.Payload.ReadInt32();
                //amount &= 0xFFFFFF;

                var objectGuid = message.Payload.ReadUInt32();

                items.Add(new ItemProfile(amount, objectGuid));
            }

            // currency id is set to 0 by default
            // if non-zero, use as alternate currency wcid

            //var altCurrencyWcid = message.Payload.ReadUInt32();

            session.Player.HandleActionSellItem(vendorGuid, items);
        }
    }
}

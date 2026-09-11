using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// Called every ~5 seconds for Creatures
        /// </summary>
        public override void Heartbeat(double currentUnixTime)
        {
            var expireItems = new List<WorldObject>();

            // added where clause
            foreach (var wo in EquippedObjects.Values.Where(i => i.EnchantmentManager.HasEnchantments || i.Lifespan.HasValue))
            {
                // FIXME: wo.NextHeartbeatTime is double.MaxValue here
                //if (wo.NextHeartbeatTime <= currentUnixTime)
                    //wo.Heartbeat(currentUnixTime);

                // just go by parent heartbeats, only for enchantments?
                // TODO: handle players dropping / picking up items
                wo.EnchantmentManager.HeartBeat(CachedHeartbeatInterval);

                if (wo.IsLifespanSpent)
                    expireItems.Add(wo);
            }

            VitalHeartBeat();

            EmoteManager.HeartBeat();

            DamageHistory.TryPrune();

            // monster combat effects: the periodic clock every timed effect rides (dot ticks, ward refresh,
            // lazy expiry of anything the combat hooks left behind). Here rather than on the 0.2s AI tick,
            // which does not run while a monster is idle, or the emote heartbeat, which returns early once
            // it has aggro - a timed effect on either would stop at exactly the wrong moment.
            MonsterEffectHeartbeat();

            // delete items when RemainingLifespan <= 0
            foreach (var expireItem in expireItems)
            {
                expireItem.DeleteObject(this);

                if (this is Player player)
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Its lifespan finished, your {expireItem.Name} crumbles to dust.", ChatMessageType.Broadcast));
            }

            base.Heartbeat(currentUnixTime);
        }
    }
}

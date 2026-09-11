using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Kill-fill vessels: a vessel in this player's pack gains one charge when THIS player lands the
        /// killing blow on a creature the vessel accepts. Called from Creature.OnDeath beside
        /// <see cref="ApplyCreatureDeathClassAbilities"/> and <see cref="ApplyWeaponModCreatureDeath"/>,
        /// which are the two precedent dispatches at that site; OnDeath's onDeathEntered guard makes this
        /// fire exactly once per death, so no second guard is needed here.
        ///
        /// ONLY THE PLAYER WHO LANDED THE KILLING BLOW IS CREDITED, AND THAT IS THE MECHANIC. The dispatch
        /// site resolves its player from DamageHistory.LastDamager - the last entry in the victim's damage
        /// log - so a fellowship-mate who did most of the damage, a player standing beside the kill, and a
        /// player whose pet landed the blow all get nothing. This is a deliberate exclusion that defines the
        /// feature, not a v1 shortcut awaiting fellowship sharing; see ACE.Server.Entity.KillFillVessel.
        ///
        /// Monster victims only - players (PvP), the player's own pets and self-kills are filtered here, the
        /// same exclusions the two neighbouring death dispatches own.
        ///
        /// The write goes through <see cref="UpdateProperty(WorldObject, PropertyInt, int?, bool)"/>, which
        /// sets the property AND enqueues a GameMessagePublicUpdatePropertyInt. Both halves are required:
        /// SetProperty on its own writes the biota and sends nothing, so the client's uses bar would never
        /// move and the feature would look broken while working correctly. Nothing is written to the shard
        /// here - the charge rides the ordinary periodic player save, because forcing a save per kill buys a
        /// few seconds of durability for real write amplification.
        /// </summary>
        public void ApplyKillFillVesselCreatureDeath(Creature victim)
        {
            if (victim == null || victim == this || victim is Player || victim is Pet)
                return;

            // this runs on every creature death the player lands, so the per-item test is ordered to fail on
            // its first property lookup for an ordinary item: HasRoom reads KillFillCreatureType first, which
            // no ordinary item carries
            var vessel = KillFillVessel.FindFillable(this, victim);

            if (vessel == null)
                return;

            var capacity = KillFillVessel.GetCapacity(vessel);
            var charges = KillFillVessel.NextCharges(KillFillVessel.GetCharges(vessel), capacity);

            UpdateProperty(vessel, PropertyInt.Structure, charges);

            if (charges < capacity)
                return;

            // reaching capacity is a one-way transition: HasRoom is false from here, so this branch cannot
            // run twice for the same vessel and the stamp and the message each land exactly once
            QuestManager.Stamp(KillFillVessel.FullQuestName);

            Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your {vessel.Name} has taken all it will hold.", ChatMessageType.Broadcast));
        }
    }
}

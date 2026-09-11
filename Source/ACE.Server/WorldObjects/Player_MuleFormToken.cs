using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Mule form tokens: an attuned token in this player's pack records one kill when THIS player
        /// lands the killing blow on a creature of the wcid it is attuned to. Called from
        /// Creature.OnDeath beside <see cref="ApplyKillFillVesselCreatureDeath"/>, whose shape and
        /// exclusions this deliberately mirrors; OnDeath's onDeathEntered guard makes it fire exactly
        /// once per death, so there is no second guard here.
        ///
        /// ONLY THE PLAYER WHO LANDED THE KILLING BLOW IS CREDITED. The dispatch site resolves its
        /// player from DamageHistory.LastDamager - the last entry in the victim's damage log - so a
        /// fellowship-mate who did most of the damage, a bystander, and a player whose pet landed the
        /// blow all get nothing. That is the same rule the kill-fill vessel uses, unchanged.
        ///
        /// The match is on WeenieClassId, not CreatureType, so a Tusker Guard and a Tusker are
        /// different attunements. Monster victims only: players (PvP), pets and self-kills are filtered
        /// here, the same exclusions the neighbouring death dispatches own.
        ///
        /// EVERY WRITE GOES THROUGH <see cref="UpdateProperty(WorldObject, PropertyInt, int?, bool)"/>
        /// AND ITS STRING SIBLING, never SetProperty. UpdateProperty sets the property AND enqueues the
        /// client update; SetProperty alone writes the biota and sends nothing, so the fill bar would
        /// stay frozen and the description stale while the server was working perfectly. Nothing is
        /// saved to the shard here - the count rides the ordinary periodic player save, because forcing
        /// a save per kill buys a few seconds of durability for real write amplification.
        ///
        /// This method is not unit-tested and cannot be: UpdateProperty reaches SendNetwork, which
        /// dereferences Session with no null check, and constructing a Player at all pulls in the world
        /// database. It is kept as thin as it can be for exactly that reason, with every decision it
        /// makes owned by the pure statics in <see cref="MuleFormToken"/>; the behaviour is covered by
        /// the live rows in Docs/VERIFY-QUEUE.md.
        /// </summary>
        public void ApplyMuleFormTokenCreatureDeath(Creature victim)
        {
            if (victim == null || victim == this || victim is Player || victim is Pet)
                return;

            var token = MuleFormToken.FindFillableToken(this, victim.WeenieClassId);

            if (token == null)
                return;

            var maxStructure = MuleFormToken.GetMaxStructure(token);
            var structure = MuleFormToken.NextStructure(MuleFormToken.GetStructure(token), maxStructure);

            var donorName = victim.Name;

            UpdateProperty(token, PropertyInt.Structure, structure);
            UpdateProperty(token, PropertyString.LongDesc, MuleFormToken.ComposeLongDesc(donorName, structure, maxStructure));

            if (structure < maxStructure)
                return;

            // Reaching capacity is a one-way transition - FindFillableToken skips a full token from
            // here on - so the rename and the message each land exactly once for this token.
            var baseName = MuleFormToken.GetBaseName(token);

            UpdateProperty(token, PropertyString.Name, MuleFormToken.ComposeName(baseName, donorName, complete: true));

            Session?.Network.EnqueueSend(new GameMessageSystemChat(MuleFormToken.ComposeFullMessage(baseName), ChatMessageType.Broadcast));
        }
    }
}

using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// /telesummon (WaffleACE, 2026-11-07): brings the player's active summons - CurrentActivePet (combat or
    /// passive) and SecondaryActivePet (Summon 2x) - to them, with the same placement the follow teleport uses
    /// (Pet.PlaceBesideOwner).
    ///
    /// Threading: the command only reads. Each pet is moved by an action enqueued on that pet
    /// (WorldObject.EnqueueAction, i.e. its landblock's queue), so the physics teleport runs on the pet's own
    /// landblock tick thread, and that action re-checks every condition (Pet.CheckTeleSummonReach, including
    /// PetFollowRules.CanTeleportToOwner / SharesTickThread) before it touches anything. The up-front check here
    /// only words the reply and decides whether the cooldown starts.
    /// </summary>
    partial class Player
    {
        /// <summary>Timers.RunningTime of the last /telesummon that sent at least one pet. Not persisted.</summary>
        private double? lastTeleSummonTime;

        public void HandleTeleSummon()
        {
            void Msg(string text) => Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            var pets = new List<Pet>(2);

            if (CurrentActivePet != null && !CurrentActivePet.IsDestroyed)
                pets.Add(CurrentActivePet);

            if (SecondaryActivePet != null && !SecondaryActivePet.IsDestroyed && !ReferenceEquals(SecondaryActivePet, CurrentActivePet))
                pets.Add(SecondaryActivePet);

            if (pets.Count == 0)
            {
                Msg("You have no active summons.");
                return;
            }

            if (IsDead)
            {
                Msg("You cannot call your summons while dead.");
                return;
            }

            if (Teleporting)
            {
                Msg("You cannot call your summons while teleporting.");
                return;
            }

            var now = Timers.RunningTime;
            var remaining = PetFollowRules.TeleSummonCooldownRemaining(lastTeleSummonTime, now, PetFollowSettings.ReadTeleSummonCooldownSeconds());

            if (remaining > 0.0)
            {
                Msg($"You can use /telesummon again in {Math.Ceiling(remaining):0} seconds.");
                return;
            }

            var sent = 0;

            foreach (var pet in pets)
            {
                var refusal = pet.CheckTeleSummonReach(this);

                if (refusal != null)
                {
                    Msg(Pet.FormatTeleSummonOutcome(pet.Name, refusal.Value));
                    continue;
                }

                var owner = this;

                // runs on the pet's landblock thread; re-checks everything before moving it
                pet.EnqueueAction(new ActionEventDelegate(() =>
                {
                    var outcome = pet.TeleportToOwnerOnCommand(owner);
                    owner.Session?.Network.EnqueueSend(new GameMessageSystemChat(Pet.FormatTeleSummonOutcome(pet.Name, outcome), ChatMessageType.Broadcast));
                }));

                sent++;
            }

            if (sent > 0)
                lastTeleSummonTime = now;
        }
    }
}

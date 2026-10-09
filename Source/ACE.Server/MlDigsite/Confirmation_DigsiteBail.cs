using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// The Yes/No behind /digsite bail: "end your digsite encounter now, and take what you have reached?".
    /// Modelled on <see cref="Confirmation_ThreadGroupStart"/>, and for the same reasons it is not
    /// Confirmation_Custom: it carries state (which encounter, when it was asked), it hops onto the player's
    /// own action queue before acting, and it honours the same timeout rule - ConfirmationManager's 30 s
    /// abort only sends a message and the client's automatic reply then arrives as a plain No, so an answer
    /// flagged as a timeout or arriving late does nothing (<see cref="Confirmation_ThreadGroupStart.IsLate"/>,
    /// reused rather than copied).
    ///
    /// Nothing happens when the dialog is sent. A Yes only REQUESTS the end
    /// (MlDigsiteManager.RequestBail -> MlDigsiteEncounter.TryRequestEnd); the next world-thread tick
    /// finishes it and pays the tier reached, like every other end. Ownership is re-checked on the answer,
    /// not only when the dialog was sent.
    /// </summary>
    public class Confirmation_DigsiteBail : Confirmation
    {
        public uint EncounterId { get; }

        public DateTime SentUtc { get; }

        public Confirmation_DigsiteBail(ObjectGuid playerGuid, uint encounterId, DateTime sentUtc)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            EncounterId = encounterId;
            SentUtc = sentUtc;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response)
                return;

            var player = Player;

            if (player == null)
                return;

            if (Confirmation_ThreadGroupStart.IsLate(SentUtc, DateTime.UtcNow, timeout))
                return;

            var encounterId = EncounterId;

            player.EnqueueAction(new ActionEventDelegate(() =>
            {
                var message = MlDigsiteManager.RequestBail(player, encounterId);

                if (message != null)
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
            }));
        }
    }
}

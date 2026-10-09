using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The arena accept popup (DESIGN "Accept popup"). A Yes_No confirmation, because Confirmation_Custom has no
    /// decline callback: here a No is a decline and goes to the coordinator.
    ///
    /// The coordinator owns the accept deadline (pvp_arena_accept_seconds, clamped to 30 s or less) and aborts any
    /// popup still open when it passes; ConfirmationManager's own 30 s Yes_No timeout never calls back. After an
    /// abort the client's automatic reply arrives as a plain No, and a popup can also outlive its match (canceled
    /// by someone else's decline). Both are harmless: the answer carries <see cref="MatchId"/>, and the coordinator
    /// ignores any answer for a match that is no longer waiting on this player.
    /// </summary>
    public sealed class Confirmation_PvpArenaAccept : Confirmation
    {
        public Guid MatchId { get; }

        public Confirmation_PvpArenaAccept(ObjectGuid playerGuid, Guid matchId)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            MatchId = matchId;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (timeout)
                return;

            var characterId = PlayerGuid.Full;
            var matchId = MatchId;

            // Replies already arrive on the world thread (NetworkManager.InboundMessageQueue). Hopping through the
            // world action queue keeps that true for any other caller too; the coordinator is world-thread only.
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    PvpMatchManager.AnswerAcceptPopup(characterId, matchId, response);
                }
                catch (Exception ex)
                {
                    log4net.LogManager.GetLogger(typeof(Confirmation_PvpArenaAccept)).Error($"[PVP] accept popup answer for match {matchId} threw", ex);
                }
            }));
        }
    }
}

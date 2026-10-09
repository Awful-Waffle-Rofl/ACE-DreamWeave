using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The "/arena leave" in-match confirmation popup (Docs/Pvp/DESIGN.md "Commands", PvpArenaText.LeaveConfirmPopup).
    /// A Yes/No dialog, same shape and reasoning as <see cref="Confirmation_PvpArenaAccept"/>: a No is a silent
    /// chicken-out (no message, matching Confirmation_Custom's own "if (!response) return;" convention), and a Yes
    /// forfeits through the same world-thread hop.
    ///
    /// Shares ConfirmationType.Yes_No with the accept popup, so ConfirmationManager (one pending confirmation per
    /// type) refuses to show this one while an accept popup for some OTHER match is already open. The command
    /// layer's fallback text tells the player to type "/arena leave confirm" in that case, which calls
    /// PvpMatchManager.Forfeit directly with no popup at all.
    /// </summary>
    public sealed class Confirmation_PvpArenaLeave : Confirmation
    {
        /// <summary>
        /// The match the popup was opened in. The Yes forfeits only that match: a popup left open after the player
        /// exited another way and re-queued must not decline or forfeit the next one.
        /// </summary>
        public Guid MatchId { get; }

        public Confirmation_PvpArenaLeave(ObjectGuid playerGuid, Guid matchId)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            MatchId = matchId;
        }

        /// <summary>
        /// The leave dialog is opened from inside a templated match, so its Yes must pass the templated Confirmation
        /// gate (ConfirmationManager.HandleResponse); otherwise /arena leave refuses with PersonalItemLocked and the
        /// player can never forfeit. The Yes only forfeits, which is an exit path, never a personal-item use.
        /// </summary>
        public override bool PermittedWhilePvpTemplated => true;

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response || timeout)
                return;

            var characterId = PlayerGuid.Full;
            var matchId = MatchId;

            // Replies already arrive on the world thread (NetworkManager.InboundMessageQueue). Hopping through the
            // world action queue keeps that true for any other caller too; the coordinator is world-thread only.
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    var player = PlayerManager.GetOnlinePlayer(new ObjectGuid(characterId));

                    if (player != null)
                        PvpMatchManager.Forfeit(player, matchId);
                }
                catch (Exception ex)
                {
                    log4net.LogManager.GetLogger(typeof(Confirmation_PvpArenaLeave)).Error($"[PVP] leave confirm forfeit for 0x{characterId:X8} threw", ex);
                }
            }));
        }
    }
}

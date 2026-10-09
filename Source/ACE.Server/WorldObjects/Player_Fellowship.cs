using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        ///  The fellowship that this player belongs to
        /// </summary>
        public Fellowship Fellowship;

        public bool FellowVitalUpdate;

        /// <summary>
        /// Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): the player-side gate. TRUE when this player holds a
        /// battleground binding whose match builds team fellowships (PvpPlayerRules.RefusesFellowshipChange), in which
        /// case every player-driven fellowship change is refused and the player is told why. The same shape as
        /// <see cref="MuleBlocked"/>. The system paths (team formation, release, dispose, logout) never call it.
        /// </summary>
        public bool PvpTeamFellowshipBlocked(bool notify = true)
        {
            if (!PvpPlayerRules.RefusesFellowshipChange(PvpBinding))
                return false;

            if (notify && Session != null)
                Session.Network.EnqueueSend(new GameMessageSystemChat(BattlegroundText.TeamFellowshipFixed, ChatMessageType.Broadcast));

            return true;
        }

        // todo: Figure out if this is the best place to do this, and whether there are concurrency issues associated with it.
        public void FellowshipCreate(string fellowshipName, bool shareXP)
        {
            if (PvpTeamFellowshipBlocked())
                return;

            // Mule (WaffleACE): a mule is storage and trade only - it cannot lead a fellowship.
            if (MuleBlocked(MuleAction.JoinFellowship))
                return;

            // An Olthoi player cannot create a fellowship
            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCannotJoinFellowship));
                return;
            }

            Fellowship = new Fellowship(this, fellowshipName, shareXP);
            Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(Session));
            Session.Network.EnqueueSend(new GameEventFellowshipFellowUpdateDone(Session));
        }

        public void HandleActionFellowshipChangeOpenness(bool openness)
        {
            if (PvpTeamFellowshipBlocked())
                return;

            if (Fellowship != null)
            {
                if (Guid.Full != Fellowship.FellowshipLeaderGuid)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustBeLeaderOfFellowship));
                    return;
                }

                if (!Fellowship.IsLocked)
                    Fellowship.UpdateOpenness(openness);
                else
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.FellowshipIsLocked));
            }
        }

        public void HandleActionFellowshipChangeLock(bool lockState, string lockName)
        {
            if (PvpTeamFellowshipBlocked())
                return;

            if (Fellowship != null)
                Fellowship.UpdateLock(lockState, lockName);
        }

        /// <param name="system">
        /// TRUE only for a system path (LogOut_Inner): it bypasses the battleground team fellowship gate, because a player
        /// logging out of a match must still leave its team fellowship. Every player-driven quit passes false.
        /// </param>
        public void FellowshipQuit(bool disband, bool system = false)
        {
            if (!system && PvpTeamFellowshipBlocked())
                return;

            if (Fellowship != null)
                Fellowship.QuitFellowship(this, disband);
        }

        public void FellowshipDismissPlayer(uint dismissGuid)
        {
            if (PvpTeamFellowshipBlocked())
                return;

            if (Fellowship == null) return;

            if (Guid.Full != Fellowship.FellowshipLeaderGuid)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustBeLeaderOfFellowship));
                return;
            }

            if (Guid.Full == dismissGuid)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("You can't dismiss yourself from the fellowship", ChatMessageType.Broadcast));
                return;
            }

            var fellowToDismiss = PlayerManager.GetOnlinePlayer(dismissGuid);

            if (fellowToDismiss == null)
                return;

            Fellowship.RemoveFellowshipMember(fellowToDismiss, this);
        }

        public void FellowshipRecruit(Player newPlayer)
        {
            if (PvpTeamFellowshipBlocked())
                return;

            if (newPlayer == null) return;

            // The joining side of the same rule: a player in a team-fellowship battleground is recruited by nobody. Such a
            // player is normally already in a fellowship (their team's, or a locked one), so this only backstops that;
            // the retail "already a fellowship member" code answers the recruiter and releases their client's recruit
            // state, with no new chat text.
            if (newPlayer.PvpTeamFellowshipBlocked(notify: false))
            {
                SendWeenieError(WeenieError.FellowshipMember);
                return;
            }

            // Mule (WaffleACE): the joining side of the same rule. Guarded on newPlayer, not on the recruiter -
            // the recruit is the one joining. Mirrors the Olthoi branch below by also telling the recruiter and
            // releasing their UI, since they get no other feedback.
            if (newPlayer.MuleBlocked(MuleAction.JoinFellowship))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{newPlayer.Name} is a mule and cannot join a fellowship.", ChatMessageType.Fellowship));
                SendWeenieError(WeenieError.None);
                return;
            }

            // An Olthoi player cannot join a fellowship
            if (newPlayer.IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("The Olthoi's hunger for destruction is too great to understand a request for fellowship.", ChatMessageType.Broadcast));
                SendWeenieError(WeenieError.None);
                return;
            }

            if (newPlayer.GetCharacterOption(CharacterOption.IgnoreFellowshipRequests))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{newPlayer.Name} is not accepting fellowship requests.", ChatMessageType.Fellowship));                
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.FellowshipIgnoringRequests));
            }
            else if (Fellowship != null)
            {
                if (Guid.Full == Fellowship.FellowshipLeaderGuid || Fellowship.Open)
                    Fellowship.AddFellowshipMember(this, newPlayer);
                else
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustBeLeaderOfFellowship));
            }
        }

        public void FellowshipNewLeader(uint newLeaderGuid)
        {
            if (PvpTeamFellowshipBlocked())
                return;

            if (Fellowship == null || Guid.Full == newLeaderGuid)
                return;

            if (Guid.Full != Fellowship.FellowshipLeaderGuid)
            {
                log.Warn($"{Name} tried to assign new fellowship leader from {Fellowship.FellowshipLeaderGuid:X8} to {newLeaderGuid:X8}");
                return;
            }

            var newLeader = PlayerManager.GetOnlinePlayer(newLeaderGuid);

            if (newLeader == null)
                return;

            if (newLeader.Fellowship != Fellowship)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{newLeader.Name} is not a member of the fellowship!", ChatMessageType.Broadcast));
                return;
            }

            Fellowship.AssignNewLeader(this, newLeader);
        }

        public bool FellowshipPanelOpen { get; set; }

        /// <summary>
        /// Called when player opens / closes the fellowship panel
        /// </summary>
        public void HandleFellowshipUpdateRequest(bool panelOpen)
        {
            FellowshipPanelOpen = panelOpen;

            if (Fellowship != null && FellowshipPanelOpen)
                Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(Session));
        }
    }
}

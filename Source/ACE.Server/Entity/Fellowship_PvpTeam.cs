using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Battleground team fellowships (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): the server-built fellowship each
    /// battleground team is put in at Countdown, so teammates show as fellows on radar and in the fellowship panel.
    ///
    /// <para/>
    /// Every method here is a SYSTEM path, called only by the live team-fellowship gateway on the world thread. None of
    /// them consults the player-side gate (PvpPlayerRules.RefusesFellowshipChange), which refuses the player-driven
    /// paths; no player path reaches these.
    ///
    /// <para/>
    /// A team fellowship always has ShareXP off, ShareLoot off, Open off, no password and leech management off. It is
    /// NOT IsLocked: locking broadcasts the quest-lock text and records departed members, both wrong here. Adds by any
    /// other path are refused by <see cref="RefusePvpTeamAdd"/> in AddFellowshipMember and AddConfirmedMember.
    /// </summary>
    public partial class Fellowship
    {
        /// <summary>The battleground match this fellowship is the team fellowship of, or null for every ordinary fellowship.</summary>
        public Guid? PvpTeamMatchId { get; private set; }

        /// <summary>True for a server-built battleground team fellowship.</summary>
        public bool IsPvpTeam => PvpTeamMatchId.HasValue;

        /// <summary>
        /// Builds a team fellowship led by <paramref name="leader"/> (who must not be in a fellowship), with the fixed team
        /// settings, and sends the leader the same panel events FellowshipCreate does.
        /// </summary>
        public static Fellowship CreatePvpTeam(Player leader, string name, Guid matchId)
        {
            if (leader == null || leader.Fellowship != null)
                return null;

            var fellowship = new Fellowship(leader, name, false)
            {
                PvpTeamMatchId = matchId
            };

            fellowship.ApplyPvpTeamSettings();

            leader.Fellowship = fellowship;
            leader.Session?.Network.EnqueueSend(new GameEventFellowshipFullUpdate(leader.Session));
            leader.Session?.Network.EnqueueSend(new GameEventFellowshipFellowUpdateDone(leader.Session));

            return fellowship;
        }

        /// <summary>The fixed team settings: no XP or loot sharing, closed, no password, no leech management.</summary>
        private void ApplyPvpTeamSettings()
        {
            DesiredShareXP = false;
            ShareXP = false;
            ShareLoot = false;
            Open = false;
            LeechManagementEnabled = false;
            ClearPassword();
        }

        /// <summary>
        /// Adds a member to a team fellowship. Mirrors AddConfirmedMember's state writes and update events, without the
        /// loot-permission lines (loot is never shared) and without the inviter's bow. False when the player is already
        /// in a fellowship, this is not a team fellowship, or the roster is at the cap.
        /// </summary>
        public bool AddPvpTeamMember(Player player)
        {
            if (player == null || !IsPvpTeam || player.Fellowship != null)
                return false;

            if (FellowshipMembers.Count >= MaxFellows)
                return false;

            FellowshipMembers.TryAdd(player.Guid.Full, new WeakReference<Player>(player));
            player.Fellowship = this;

            LeechActivity[player.Guid.Full] = Time.GetUnixTime();

            ApplyPvpTeamSettings();
            CalculateXPSharing();

            var fellowshipMembers = GetFellowshipMembers();

            foreach (var member in fellowshipMembers.Values)
            {
                if (member.Guid != player.Guid)
                    member.Session.Network.EnqueueSend(new GameEventFellowshipUpdateFellow(member.Session, player, ShareXP));
            }

            UpdateAllMembers();

            return true;
        }

        /// <summary>
        /// Takes one member out of a team fellowship: the non-disband quit's events, without the loot lines and without a
        /// departed-member record. A leader who leaves hands the lead on as a normal quit does. The fellowship is
        /// unregistered once it is empty.
        /// </summary>
        public void RemovePvpTeamMember(Player player)
        {
            if (player == null)
                return;

            var guid = player.Guid.Full;

            if (!FellowshipMembers.ContainsKey(guid))
            {
                if (player.Fellowship == this)
                    player.Fellowship = null;

                return;
            }

            FellowshipMembers.Remove(guid);
            player.Fellowship = null;

            player.Session?.Network.EnqueueSend(new GameEventFellowshipQuit(player.Session, guid));

            var remaining = GetFellowshipMembers();

            foreach (var member in remaining.Values)
                member.Session.Network.EnqueueSend(new GameEventFellowshipQuit(member.Session, guid));

            if (remaining.Count == 0)
            {
                FellowshipManager.Unregister(this);
                return;
            }

            if (guid == FellowshipLeaderGuid)
                AssignNewLeader(null, null);

            CalculateXPSharing();
        }

        /// <summary>Dissolves a team fellowship: every live member gets the disband event and leaves; the fellowship is unregistered.</summary>
        public void DisbandPvpTeam()
        {
            foreach (var member in GetFellowshipMembers().Values)
            {
                member.Session.Network.EnqueueSend(new GameEventFellowshipDisband(member.Session));

                if (member.Fellowship == this)
                    member.Fellowship = null;
            }

            FellowshipMembers.Clear();
            FellowshipManager.Unregister(this);
        }

        /// <summary>
        /// The restore's re-formed premade: an ordinary fellowship (not a team fellowship) led by <paramref name="leader"/>
        /// under its old name and XP-share flag, built the way FellowshipCreate builds one but without its gate (a system
        /// path: the leader still holds their battleground binding while the release runs).
        /// </summary>
        public static Fellowship CreateRestored(Player leader, string name, bool shareXP)
        {
            if (leader == null || leader.Fellowship != null)
                return null;

            var fellowship = new Fellowship(leader, name, shareXP);

            leader.Fellowship = fellowship;
            leader.Session?.Network.EnqueueSend(new GameEventFellowshipFullUpdate(leader.Session));
            leader.Session?.Network.EnqueueSend(new GameEventFellowshipFellowUpdateDone(leader.Session));

            return fellowship;
        }

        /// <summary>
        /// The refusal at the head of AddFellowshipMember and AddConfirmedMember: nobody joins a team fellowship by those
        /// paths (an invite, a confirmation popup, /fship join, the "xp" tell, a mass add). True when refused; the inviter
        /// is told why and their client's recruit state is released.
        /// </summary>
        private bool RefusePvpTeamAdd(Player inviter, Player newMember)
        {
            if (!IsPvpTeam)
                return false;

            SendRecruitRejection(inviter, newMember, BattlegroundText.TeamFellowshipFixed, ChatMessageType.Broadcast, WeenieError.None);
            return true;
        }
    }
}

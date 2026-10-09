using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// One team handed to <see cref="IPvpTeamFellowshipGateway.Form"/>: its index, the fellowship name, and the still-active
    /// members in team order (the order the leader is chosen in).
    /// </summary>
    public sealed record PvpTeamFellowshipTeam(int TeamIndex, string Name, IReadOnlyList<uint> MemberIds);

    /// <summary>
    /// The team fellowship seam (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"). Optional, like
    /// <see cref="IBattlegroundPlayerGateway"/>: the coordinator enables the feature only when its player gateway also
    /// implements this interface, so every existing gateway and test fake is untouched and leaves fellowships alone.
    ///
    /// <para/>
    /// THREADING: every method is called on the WORLD thread and the live implementation runs SYNCHRONOUSLY there,
    /// not queued on each player's action queue (a deliberate departure from IPvpPlayerGateway's note). A fellowship
    /// spans several players, each with their own queue, and the client-panel fellowship actions it must not interleave
    /// with (create, recruit, quit, dismiss) are dispatched on the world thread too, so the world thread is the one
    /// place every member's fellowship state can be changed in a single consistent step.
    /// </summary>
    public interface IPvpTeamFellowshipGateway
    {
        /// <summary>The fellowship member cap (fellowship_max_members) as the live server reads it.</summary>
        int FellowshipCap { get; }

        /// <summary>
        /// At Countdown: every listed member leaves their current fellowship (recorded for the restore) unless it is
        /// locked, then each team is put in its own team fellowship led by its first eligible member.
        /// </summary>
        void Form(Guid matchId, IReadOnlyList<PvpTeamFellowshipTeam> teams);

        /// <summary>
        /// A participant leaves the match: out of the team fellowship, and, with <paramref name="restore"/>, back into
        /// the fellowship they arrived in when that is possible. Idempotent per (match, character).
        /// </summary>
        void Release(Guid matchId, uint characterId, bool restore);

        /// <summary>The match is Closed or Canceled: any team fellowship still standing is dissolved and the bookkeeping dropped.</summary>
        void Dispose(Guid matchId);

        /// <summary>Dissolves any team fellowship whose match is not in <paramref name="activeMatchIds"/> (a backstop for a missed Dispose).</summary>
        void Sweep(IReadOnlyCollection<Guid> activeMatchIds);
    }

    /// <summary>Whether a battleground match builds team fellowships, and if not, why.</summary>
    public enum TeamFellowshipPlanDecision
    {
        /// <summary>Both teams are formed.</summary>
        Form,

        /// <summary>The mode's policy is not <see cref="TeamFellowshipPolicy.TeamFellowship"/> (every arena mode).</summary>
        SkipPolicy,

        /// <summary>pvp_bg_team_fellowship is off, or the gateway does not implement the seam.</summary>
        SkipDisabled,

        /// <summary>A team is larger than the fellowship cap: BOTH teams are skipped (both teams are treated the same).</summary>
        SkipOverCap
    }

    /// <summary>The restore outcome for one released participant.</summary>
    public enum TeamFellowshipRestoreAction
    {
        None,

        /// <summary>Back into the outside fellowship they left at Countdown (the same Fellowship object).</summary>
        Rejoin,

        /// <summary>Into the copy of their premade another member already re-formed.</summary>
        JoinRebuilt,

        /// <summary>Re-form the premade under its old name, as its leader.</summary>
        Rebuild
    }

    /// <summary>What the player's fellowship was when they arrived, as recorded at Countdown.</summary>
    public enum TeamFellowshipRecordKind
    {
        /// <summary>Nothing recorded: no fellowship, a locked one (left alone), or never formed.</summary>
        None,

        /// <summary>A fellowship with members outside the match: the player is put back into that same fellowship.</summary>
        Outside,

        /// <summary>A fellowship whose every member was in the match: it is re-formed after the match.</summary>
        Premade
    }

    /// <summary>Everything <see cref="TeamFellowshipRestore.Decide"/> needs, snapshotted by the live gateway.</summary>
    public readonly record struct TeamFellowshipRestoreFacts(
        bool RestoreEnabled,
        bool Online,
        bool LoggingOut,
        bool PkLogout,
        bool AlreadyInFellowship,
        TeamFellowshipRecordKind Kind,
        bool OutsideAlive = false,
        bool OutsideLocked = false,
        bool OutsideFull = false,
        bool OutsideLeechLocked = false,
        bool RebuiltCopyAlive = false,
        bool RebuiltCopyLocked = false,
        bool RebuiltCopyFull = false,
        bool RebuiltCopyLeechLocked = false);

    /// <summary>The restore action, and whether the player is told their previous fellowship is no longer available.</summary>
    public readonly record struct TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction Action, bool TellEnded);

    /// <summary>
    /// One participant as Form finds them, snapshotted by the live gateway. <see cref="FellowshipKey"/> is an opaque id
    /// for the fellowship they are in (0 = none); the same fellowship has the same key for every member.
    /// </summary>
    public readonly record struct TeamFellowshipArrival(uint CharacterId, bool Available, int FellowshipKey = 0, bool FellowshipLocked = false, bool FellowshipIsTeam = false);

    /// <summary>What a participant's arrival fellowship was recorded as, and which fellowship (by key).</summary>
    public readonly record struct TeamFellowshipArrivalRecord(TeamFellowshipRecordKind Kind, int FellowshipKey);

    /// <summary>One team's planned fellowship: its leader (0 = no fellowship for this team) and the other members, in team order.</summary>
    public sealed record TeamFellowshipPlannedTeam(int TeamIndex, string Name, uint LeaderId, IReadOnlyList<uint> MemberIds);

    /// <summary>
    /// The whole Form plan: who is told they stay in their locked fellowship, who leaves a leftover team fellowship of
    /// another match, who quits their arrival fellowship (with its record), and each team's leader and members.
    /// </summary>
    public sealed record TeamFellowshipFormPlan(
        IReadOnlyList<uint> LockedSkips,
        IReadOnlyList<uint> LeaveStaleTeam,
        IReadOnlyDictionary<uint, TeamFellowshipArrivalRecord> Records,
        IReadOnlyList<TeamFellowshipPlannedTeam> Teams);

    /// <summary>
    /// The pure formation rules behind battleground team fellowships (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships").
    /// Nothing here reads a live server type: Fellowship has no unit-testable constructor (PropertyManager reads throw
    /// under the test host), so every decision the live gateway makes is one of these or <see cref="TeamFellowshipRestore"/>.
    /// </summary>
    public static class TeamFellowshipPlan
    {
        /// <summary>
        /// Whether a match builds team fellowships. Decided ONCE per match, at formation, from the largest team: a team
        /// larger than the fellowship cap skips BOTH teams (both teams are treated the same).
        /// </summary>
        public static TeamFellowshipPlanDecision Decide(TeamFellowshipPolicy policy, bool enabled, int largestTeamSize, int cap)
        {
            if (policy != TeamFellowshipPolicy.TeamFellowship)
                return TeamFellowshipPlanDecision.SkipPolicy;

            if (!enabled)
                return TeamFellowshipPlanDecision.SkipDisabled;

            if (largestTeamSize > cap)
                return TeamFellowshipPlanDecision.SkipOverCap;

            return TeamFellowshipPlanDecision.Form;
        }

        /// <summary>The team fellowship's leader: the first member in team order that is <paramref name="eligible"/>, or 0 when none is.</summary>
        public static uint ChooseLeader(IReadOnlyList<uint> teamOrder, Func<uint, bool> eligible)
        {
            if (teamOrder == null)
                return 0;

            foreach (var id in teamOrder)
            {
                if (eligible == null || eligible(id))
                    return id;
            }

            return 0;
        }

        /// <summary>
        /// The Form plan, pure. Every participant in team order is classified:
        ///   - not <see cref="TeamFellowshipArrival.Available"/> (offline, logging out): untouched and not a member;
        ///   - in a LOCKED fellowship: left in it, told, and not a member;
        ///   - in a leftover team fellowship of another match: leaves it, then is a member, nothing recorded;
        ///   - in an ordinary fellowship: recorded (Premade when every live member of that fellowship, per
        ///     <paramref name="liveMembersByKey"/>, is a participant of this match; Outside otherwise), quits it, and is
        ///     a member;
        ///   - in no fellowship: a member.
        /// Each team's leader is its first member in team order (<see cref="ChooseLeader"/>); a team with no member gets
        /// leader 0 and no fellowship. The roster cap is the live add's to enforce (AddPvpTeamMember).
        /// </summary>
        public static TeamFellowshipFormPlan PlanForm(IReadOnlyList<PvpTeamFellowshipTeam> teams, Func<uint, TeamFellowshipArrival> arrival, IReadOnlyDictionary<int, IReadOnlyList<uint>> liveMembersByKey)
        {
            var participants = (teams ?? Array.Empty<PvpTeamFellowshipTeam>()).SelectMany(team => team.MemberIds).ToHashSet();
            var lockedSkips = new List<uint>();
            var stale = new List<uint>();
            var records = new Dictionary<uint, TeamFellowshipArrivalRecord>();
            var members = new HashSet<uint>();

            foreach (var id in participants)
            {
                var a = arrival(id);

                if (!a.Available)
                    continue;

                if (a.FellowshipKey != 0)
                {
                    if (a.FellowshipIsTeam)
                        stale.Add(id);
                    else if (a.FellowshipLocked)
                    {
                        lockedSkips.Add(id);
                        continue;
                    }
                    else
                    {
                        var live = liveMembersByKey != null && liveMembersByKey.TryGetValue(a.FellowshipKey, out var l) ? l : new[] { id };
                        records[id] = new TeamFellowshipArrivalRecord(TeamFellowshipRestore.Classify(live, participants), a.FellowshipKey);
                    }
                }

                members.Add(id);
            }

            var planned = new List<TeamFellowshipPlannedTeam>();

            foreach (var team in teams ?? Array.Empty<PvpTeamFellowshipTeam>())
            {
                var leader = ChooseLeader(team.MemberIds, members.Contains);
                var rest = leader == 0 ? new List<uint>() : team.MemberIds.Where(id => id != leader && members.Contains(id)).ToList();

                planned.Add(new TeamFellowshipPlannedTeam(team.TeamIndex, team.Name, leader, rest));
            }

            return new TeamFellowshipFormPlan(lockedSkips, stale, records, planned);
        }

        /// <summary>The team fellowships to dissolve in a sweep: those whose match id is not active.</summary>
        public static IReadOnlyList<Guid> Orphans(IEnumerable<Guid> teamFellowshipMatchIds, IReadOnlyCollection<Guid> activeMatchIds)
        {
            var active = activeMatchIds as ISet<Guid> ?? new HashSet<Guid>(activeMatchIds ?? Array.Empty<Guid>());

            return (teamFellowshipMatchIds ?? Array.Empty<Guid>()).Where(id => !active.Contains(id)).Distinct().ToList();
        }
    }

    /// <summary>
    /// The pure restore rules (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): what a player's arrival fellowship was, and
    /// what happens to it when they leave the match.
    /// </summary>
    public static class TeamFellowshipRestore
    {
        /// <summary>
        /// The fellowship a player arrived in is a premade (re-formed after the match) when every one of its live members
        /// is a participant of this match; any member outside the match makes it an outside fellowship (rejoined).
        /// </summary>
        public static TeamFellowshipRecordKind Classify(IEnumerable<uint> liveMemberIds, ISet<uint> matchParticipantIds)
        {
            var members = liveMemberIds?.ToList() ?? new List<uint>();

            if (members.Count == 0)
                return TeamFellowshipRecordKind.None;

            return members.All(matchParticipantIds.Contains) ? TeamFellowshipRecordKind.Premade : TeamFellowshipRecordKind.Outside;
        }

        /// <summary>A fellowship with <paramref name="liveCount"/> live members has no room under <paramref name="cap"/>.</summary>
        public static bool IsFull(int liveCount, int cap) => liveCount >= cap;

        /// <summary>
        /// The restore facts from the counts the live gateway reads: a fellowship is alive with at least one live member,
        /// and full at the cap (<see cref="IsFull"/>). A null count means there is no such fellowship.
        /// </summary>
        public static TeamFellowshipRestoreFacts BuildFacts(bool restoreEnabled, bool online, bool loggingOut, bool pkLogout, bool alreadyInFellowship, TeamFellowshipRecordKind kind,
            int? outsideLiveCount, bool outsideLocked, bool outsideLeechLocked, int? copyLiveCount, bool copyLocked, bool copyLeechLocked, int cap) =>
            new TeamFellowshipRestoreFacts(
                RestoreEnabled: restoreEnabled,
                Online: online,
                LoggingOut: loggingOut,
                PkLogout: pkLogout,
                AlreadyInFellowship: alreadyInFellowship,
                Kind: kind,
                OutsideAlive: outsideLiveCount > 0,
                OutsideLocked: outsideLocked,
                OutsideFull: outsideLiveCount.HasValue && IsFull(outsideLiveCount.Value, cap),
                OutsideLeechLocked: outsideLeechLocked,
                RebuiltCopyAlive: copyLiveCount > 0,
                RebuiltCopyLocked: copyLocked,
                RebuiltCopyFull: copyLiveCount.HasValue && IsFull(copyLiveCount.Value, cap),
                RebuiltCopyLeechLocked: copyLeechLocked);

        /// <summary>
        /// The restore decision for one released participant. None (and nothing said) when the restore is off, nothing
        /// was recorded, or the player is offline, logging out (a PK logoff included) or already in a fellowship. An
        /// outside fellowship is rejoined only while it still has a live member and is not locked, full or leech-locked
        /// against them; otherwise they are told it ended. A premade is re-formed by the first member home, and later
        /// members join that copy while it stands and admits them.
        /// </summary>
        public static TeamFellowshipRestoreDecision Decide(TeamFellowshipRestoreFacts f)
        {
            var none = new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.None, false);
            var ended = new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.None, true);

            if (!f.RestoreEnabled || f.Kind == TeamFellowshipRecordKind.None)
                return none;

            if (!f.Online || f.LoggingOut || f.PkLogout || f.AlreadyInFellowship)
                return none;

            if (f.Kind == TeamFellowshipRecordKind.Outside)
            {
                if (!f.OutsideAlive || f.OutsideLocked || f.OutsideFull || f.OutsideLeechLocked)
                    return ended;

                return new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.Rejoin, false);
            }

            if (f.RebuiltCopyAlive)
            {
                if (f.RebuiltCopyLocked || f.RebuiltCopyFull || f.RebuiltCopyLeechLocked)
                    return ended;

                return new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.JoinRebuilt, false);
            }

            return new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.Rebuild, false);
        }
    }
}

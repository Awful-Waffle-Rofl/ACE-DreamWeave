using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The live <see cref="IPvpTeamFellowshipGateway"/> (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"). The coordinator
    /// enables it by the same cast it uses for the battleground seams.
    ///
    /// <para/>
    /// THREADING: called by the coordinator on the WORLD thread and run SYNCHRONOUSLY there (see the interface for why).
    /// Every method is a system path: none goes through the player-side gate, which refuses only the player-driven ones.
    ///
    /// <para/>
    /// Bookkeeping is per match and in memory only, like fellowships themselves: each team's fellowship, and for each
    /// participant the fellowship they arrived in (an outside fellowship by reference, or a premade by name, XP-share
    /// flag and member ids, plus the copy re-formed after the match). Dispose drops it; the sweep drops any it missed.
    /// </summary>
    internal sealed partial class LivePvpPlayerGateway : IPvpTeamFellowshipGateway
    {
        /// <summary>A premade the match broke up: re-formed by the first member home, joined by the rest.</summary>
        private sealed class PremadeGroup
        {
            public string Name;
            public bool ShareXP;
            public List<uint> MemberIds;
            public Fellowship Rebuilt;
        }

        /// <summary>The fellowship one participant arrived in.</summary>
        private sealed class ArrivalRecord
        {
            public TeamFellowshipRecordKind Kind;
            public string Name;
            public Fellowship Outside;
            public PremadeGroup Group;
        }

        private sealed class TeamBook
        {
            public readonly Dictionary<int, Fellowship> Teams = new Dictionary<int, Fellowship>();
            public readonly Dictionary<uint, ArrivalRecord> Records = new Dictionary<uint, ArrivalRecord>();
            public readonly HashSet<uint> Released = new HashSet<uint>();
        }

        private readonly Dictionary<Guid, TeamBook> teamBooks = new Dictionary<Guid, TeamBook>();

        public int FellowshipCap => Fellowship.MaxFellows;

        public void Form(Guid matchId, IReadOnlyList<PvpTeamFellowshipTeam> teams)
        {
            if (teams == null || teams.Count == 0)
                return;

            var book = new TeamBook();
            teamBooks[matchId] = book;

            // Snapshot each participant and each fellowship they are in, then plan purely (TeamFellowshipPlan.PlanForm).
            var keys = new Dictionary<Fellowship, int>();
            var byKey = new Dictionary<int, Fellowship>();
            var liveByKey = new Dictionary<int, IReadOnlyList<uint>>();
            var arrivals = new Dictionary<uint, TeamFellowshipArrival>();

            foreach (var id in teams.SelectMany(team => team.MemberIds).Distinct())
            {
                var p = Online(id);

                if (p == null || p.IsLoggingOut)
                {
                    arrivals[id] = new TeamFellowshipArrival(id, false);
                    continue;
                }

                var current = p.Fellowship;
                var key = 0;

                if (current != null && !keys.TryGetValue(current, out key))
                {
                    key = keys.Count + 1;
                    keys[current] = key;
                    byKey[key] = current;
                    liveByKey[key] = LiveMembers(current).Keys.ToList();
                }

                arrivals[id] = new TeamFellowshipArrival(id, true, key, current?.IsLocked ?? false, current?.IsPvpTeam ?? false);
            }

            var plan = TeamFellowshipPlan.PlanForm(teams, id => arrivals[id], liveByKey);

            // 1. A locked (quest) fellowship is left alone, and the player is told why.
            foreach (var id in plan.LockedSkips)
            {
                var p = Online(id);
                p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(BattlegroundText.TeamFellowshipLockedSkip, ChatMessageType.Broadcast));
                log.Info($"[PVP] match {matchId}: {p?.Name ?? $"0x{id:X8}"} is in a LOCKED fellowship; left in it, no team fellowship");
            }

            // 2. A leftover team fellowship of another match (the sweep has not reached it yet): just leave it.
            foreach (var id in plan.LeaveStaleTeam)
            {
                var p = Online(id);
                var stale = byKey[arrivals[id].FellowshipKey];

                log.Warn($"[PVP] match {matchId}: {p.Name} was still in the team fellowship of match {stale.PvpTeamMatchId}; removing them from it");
                stale.RemovePvpTeamMember(p);
            }

            // 3. Everyone else leaves the fellowship they came in with a normal quit (never a disband), recorded.
            var premades = new Dictionary<int, PremadeGroup>();

            foreach (var (id, planned) in plan.Records)
            {
                var p = Online(id);
                var current = byKey[planned.FellowshipKey];
                var record = new ArrivalRecord { Kind = planned.Kind, Name = current.FellowshipName };

                if (planned.Kind == TeamFellowshipRecordKind.Premade)
                {
                    if (!premades.TryGetValue(planned.FellowshipKey, out var group))
                    {
                        group = new PremadeGroup { Name = current.FellowshipName, ShareXP = current.DesiredShareXP, MemberIds = liveByKey[planned.FellowshipKey].ToList() };
                        premades[planned.FellowshipKey] = group;
                    }

                    record.Group = group;
                }
                else
                    record.Outside = current;

                book.Records[id] = record;

                if (p.Fellowship == current)
                    current.QuitFellowship(p, false);

                log.Info($"[PVP] match {matchId}: {p.Name} left \"{record.Name}\" ({record.Kind}) for the team fellowship");
            }

            // 4. Each team's fellowship, led by its planned leader. Both teams the same way.
            foreach (var team in plan.Teams)
            {
                var leader = team.LeaderId != 0 ? Online(team.LeaderId) : null;

                if (leader == null)
                {
                    log.Warn($"[PVP] match {matchId}: no eligible member to lead {team.Name}; that team has no team fellowship");
                    continue;
                }

                var fellowship = Fellowship.CreatePvpTeam(leader, team.Name, matchId);

                if (fellowship == null)
                    continue;

                book.Teams[team.TeamIndex] = fellowship;

                foreach (var id in team.MemberIds)
                {
                    var p = Online(id);

                    if (p != null && !fellowship.AddPvpTeamMember(p))
                        log.Warn($"[PVP] match {matchId}: {p.Name} could not be added to {team.Name}");
                }
            }
        }
        public void Release(Guid matchId, uint characterId, bool restore)
        {
            if (!teamBooks.TryGetValue(matchId, out var book) || !book.Released.Add(characterId))
                return;

            var p = Online(characterId);

            // 1. Out of the team fellowship.
            var current = p?.Fellowship;

            if (current != null && current.IsPvpTeam && current.PvpTeamMatchId == matchId)
                current.RemovePvpTeamMember(p);

            // 2. Back into the fellowship they arrived in, when that is possible.
            if (!book.Records.TryGetValue(characterId, out var record))
                return;

            var outside = record.Outside;
            var copy = record.Group?.Rebuilt;
            var outsideMembers = outside != null ? LiveMembers(outside) : null;
            var copyMembers = copy != null ? LiveMembers(copy) : null;
            var facts = TeamFellowshipRestore.BuildFacts(
                restoreEnabled: restore,
                online: p != null,
                loggingOut: p?.IsLoggingOut ?? false,
                pkLogout: p?.PKLogout ?? false,
                alreadyInFellowship: p?.Fellowship != null,
                kind: record.Kind,
                outsideLiveCount: outsideMembers?.Count,
                outsideLocked: outside?.IsLocked ?? false,
                outsideLeechLocked: outside != null && p != null && outside.GetLeechLockoutRemaining(p) > 0,
                copyLiveCount: copyMembers?.Count,
                copyLocked: copy?.IsLocked ?? false,
                copyLeechLocked: copy != null && p != null && copy.GetLeechLockoutRemaining(p) > 0,
                cap: Fellowship.MaxFellows);

            var decision = TeamFellowshipRestore.Decide(facts);

            switch (decision.Action)
            {
                case TeamFellowshipRestoreAction.Rejoin:
                    Join(p, outside, outsideMembers, record.Name, BattlegroundText.TeamFellowshipRejoined(outside.FellowshipName), matchId);
                    break;

                case TeamFellowshipRestoreAction.JoinRebuilt:
                    Join(p, copy, copyMembers, record.Name, BattlegroundText.TeamFellowshipReformed(copy.FellowshipName), matchId);
                    break;

                case TeamFellowshipRestoreAction.Rebuild:
                    var rebuilt = Fellowship.CreateRestored(p, record.Group.Name, record.Group.ShareXP);

                    if (rebuilt != null)
                    {
                        record.Group.Rebuilt = rebuilt;
                        Tell(p, BattlegroundText.TeamFellowshipReformed(rebuilt.FellowshipName));
                        log.Info($"[PVP] match {matchId}: {p.Name} re-formed the premade \"{rebuilt.FellowshipName}\"");
                    }
                    break;

                default:
                    if (decision.TellEnded)
                        Tell(p, BattlegroundText.TeamFellowshipEnded(record.Name));

                    log.Info($"[PVP] match {matchId}: 0x{characterId:X8} not restored to \"{record.Name}\" ({record.Kind}; restore {(restore ? "on" : "off")}, online {p != null}, told {decision.TellEnded})");
                    break;
            }
        }

        /// <summary>The add-confirmed path, through a live member of <paramref name="target"/> as the inviter; told the outcome.</summary>
        private static void Join(Player p, Fellowship target, Dictionary<uint, Player> members, string recordedName, string joinedLine, Guid matchId)
        {
            var inviter = members.TryGetValue(target.FellowshipLeaderGuid, out var leader) ? leader : members.Values.FirstOrDefault();

            if (inviter != null)
                target.AddConfirmedMember(inviter, p, true);

            if (p.Fellowship == target)
            {
                Tell(p, joinedLine);
                log.Info($"[PVP] match {matchId}: {p.Name} restored into \"{target.FellowshipName}\"");
            }
            else
            {
                Tell(p, BattlegroundText.TeamFellowshipEnded(recordedName));
                log.Info($"[PVP] match {matchId}: {p.Name} could not be restored into \"{target.FellowshipName}\"");
            }
        }

        public void Dispose(Guid matchId)
        {
            if (teamBooks.Remove(matchId, out var book))
            {
                foreach (var fellowship in book.Teams.Values)
                    fellowship.DisbandPvpTeam();
            }

            // Any team fellowship of this match the book did not hold.
            foreach (var fellowship in FellowshipManager.GetAllFellowships().Where(f => f.PvpTeamMatchId == matchId))
                fellowship.DisbandPvpTeam();
        }

        public void Sweep(IReadOnlyCollection<Guid> activeMatchIds)
        {
            var teamsNow = FellowshipManager.GetAllFellowships().Where(f => f.IsPvpTeam).ToList();
            var orphans = TeamFellowshipPlan.Orphans(teamsNow.Select(f => f.PvpTeamMatchId.Value), activeMatchIds).ToHashSet();

            foreach (var fellowship in teamsNow.Where(f => orphans.Contains(f.PvpTeamMatchId.Value)))
            {
                log.Warn($"[PVP] team fellowship \"{fellowship.FellowshipName}\" of match {fellowship.PvpTeamMatchId} outlived its match; dissolving it");
                fellowship.DisbandPvpTeam();
            }

            foreach (var stale in TeamFellowshipPlan.Orphans(teamBooks.Keys.ToList(), activeMatchIds))
                teamBooks.Remove(stale);
        }

        /// <summary>The live members still in <paramref name="fellowship"/> itself (a member disbanded from it and now elsewhere is not one).</summary>
        private static Dictionary<uint, Player> LiveMembers(Fellowship fellowship) =>
            fellowship.GetFellowshipMembers().Where(kvp => kvp.Value.Fellowship == fellowship).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        private static void Tell(Player p, string text) => p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }
}

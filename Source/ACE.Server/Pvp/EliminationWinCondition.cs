using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The single win condition shared by every v1 arena mode (Docs/Pvp/DESIGN.md "Win condition"):
    /// finishes when one team is left. A team is eliminated only once every one of its members has exited
    /// (died or forfeited) - a 2v2 team keeps fighting shorthanded after one member is out.
    ///
    /// Placement: "the number of teams still alive when they were eliminated, plus 1" - computed here as
    /// (teams still alive right after this team is removed) + 1, so the last team standing gets 1 and each
    /// earlier elimination gets a strictly worse number. Deaths/forfeits that complete a team's elimination
    /// within the same tick (the same run of OnParticipantOut calls between two Evaluate calls) are batched
    /// and tie. A team whose elimination involved ANY forfeit is placed last of all (ties with every other
    /// forfeited team), ranked strictly below every team eliminated purely by combat, regardless of tick
    /// order - this is this implementation's reading of "a forfeiter is always placed last": ambiguous in
    /// the design for a team that is only partially forfeited (e.g. 2v2, one forfeiter, live teammate later
    /// loses in combat), reported as an open question rather than guessed silently.
    /// </summary>
    public sealed class EliminationWinCondition : IWinCondition
    {
        /// <summary>
        /// Each combat-eliminated team's placement, fixed when its batch is finalized: the number of teams still
        /// alive right after that batch (forfeited teams already count as gone), plus 1. Evaluate and ResolveTimeout
        /// both read this, so a timeout never re-ranks an early elimination.
        /// </summary>
        private readonly Dictionary<int, int> _combatPlacements = new();
        private readonly List<int> _forfeitedTeams = new();
        private readonly HashSet<int> _eliminatedTeamIndexes = new();

        private List<int> _pendingCombatBatch = new();

        public void OnParticipantOut(IMatchView m, PvpParticipant p, ParticipantExit how)
        {
            p.ExitReason = how;

            var team = m.Teams.FirstOrDefault(t => t.Members.Contains(p));

            if (team == null || _eliminatedTeamIndexes.Contains(team.TeamIndex))
                return;

            if (!team.Members.All(member => member.ExitReason.HasValue))
                return;

            _eliminatedTeamIndexes.Add(team.TeamIndex);

            var anyForfeit = team.Members.Any(member => member.ExitReason.Value != ParticipantExit.Died);

            if (anyForfeit)
                _forfeitedTeams.Add(team.TeamIndex);
            else
                _pendingCombatBatch.Add(team.TeamIndex);
        }

        public MatchOutcome Evaluate(IMatchView m, DateTime utcNow)
        {
            FinalizePendingBatch(m.Teams.Count);

            var totalTeams = m.Teams.Count;
            var aliveTeams = totalTeams - _eliminatedTeamIndexes.Count;

            if (aliveTeams > 1)
                return null;

            var winningTeams = new HashSet<int>();

            if (aliveTeams == 1)
                winningTeams.Add(Enumerable.Range(0, totalTeams).Except(_eliminatedTeamIndexes).Single());

            var placements = BuildEliminatedPlacements(totalTeams);

            if (aliveTeams == 1)
                placements[winningTeams.Single()] = 1;

            // AllForfeited only when EVERY eliminated team was forfeit-tainted - a simultaneous double (or
            // multi) combat KO with zero forfeits is a plain Elimination, just with no outright winner.
            var reason = _forfeitedTeams.Count == totalTeams ? EndReason.AllForfeited : EndReason.Elimination;

            return new MatchOutcome(winningTeams, placements, IsDraw: false, Rated: true, Reason: reason);
        }

        /// <summary>
        /// The pure time-limit resolver (Docs/Pvp/DESIGN.md "Modes" table's Timeout column, added per
        /// code review on PR #1396). Returns null before the limit. At or past it: 1v1/2v2
        /// (survivorsShareFirst=false) always resolves to an unrated draw, whoever (if anyone) is still
        /// alive; FFA (survivorsShareFirst=true) has every still-alive team share 1st, with already
        /// (fully-)eliminated teams keeping the placement <see cref="Evaluate"/> would have given them,
        /// rated per the mode's own TimeoutRated.
        /// </summary>
        public MatchOutcome ResolveTimeout(IMatchView m, DateTime utcNow, int timeLimitSeconds, bool survivorsShareFirst, bool rated)
        {
            if (m.LiveSinceUtc == null)
                return null;

            if ((utcNow - m.LiveSinceUtc.Value).TotalSeconds < timeLimitSeconds)
                return null;

            FinalizePendingBatch(m.Teams.Count);

            var totalTeams = m.Teams.Count;

            if (!survivorsShareFirst)
            {
                var drawPlacements = Enumerable.Repeat(1, totalTeams).ToArray();
                return new MatchOutcome(new HashSet<int>(), drawPlacements, IsDraw: true, Rated: false, Reason: EndReason.Timeout);
            }

            var aliveTeamIndexes = Enumerable.Range(0, totalTeams).Except(_eliminatedTeamIndexes).ToList();
            var placements = BuildEliminatedPlacements(totalTeams);

            foreach (var teamIndex in aliveTeamIndexes)
                placements[teamIndex] = 1;

            var winningTeams = new HashSet<int>(aliveTeamIndexes);

            return new MatchOutcome(winningTeams, placements, IsDraw: false, Rated: rated, Reason: EndReason.Timeout);
        }

        private void FinalizePendingBatch(int totalTeams)
        {
            if (_pendingCombatBatch.Count == 0)
                return;

            // "The number of teams still alive when they were eliminated, plus 1" (DESIGN "Win condition"). Every team
            // in the batch is already in _eliminatedTeamIndexes, so this counts the teams left AFTER the batch, and the
            // whole batch shares the one placement.
            var aliveAfter = totalTeams - _eliminatedTeamIndexes.Count;

            foreach (var teamIndex in _pendingCombatBatch)
                _combatPlacements[teamIndex] = aliveAfter + 1;

            _pendingCombatBatch = new List<int>();
        }

        private int[] BuildEliminatedPlacements(int totalTeams)
        {
            var placements = new int[totalTeams];

            foreach (var (teamIndex, placement) in _combatPlacements)
                placements[teamIndex] = placement;

            foreach (var teamIndex in _forfeitedTeams)
                placements[teamIndex] = totalTeams;

            return placements;
        }
    }
}

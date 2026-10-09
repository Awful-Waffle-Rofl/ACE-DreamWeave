using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The objective-mode win condition (Docs/Pvp/BATTLEGROUNDS.md "Winning"). Target and time limit are
    /// snapshotted by the caller at match creation. Checked in order each tick: (1) an inner
    /// <see cref="EliminationWinCondition"/> (a team with no active members loses, forfeit placement unchanged);
    /// (2) a team at or above the target wins with <see cref="EndReason.Score"/>; (3) at LiveSinceUtc + limit, more
    /// points wins, tied then more team kills, tied again a draw, <see cref="EndReason.Timeout"/>. Every battleground
    /// outcome is rated, including the draw. Before the match is Live only (1) applies. Pure: reads no clock.
    /// </summary>
    public sealed class ScoreWinCondition : IWinCondition
    {
        private readonly EliminationWinCondition _elimination = new();
        private readonly int _scoreTarget;
        private readonly int _timeLimitSeconds;

        public ScoreWinCondition(int scoreTarget, int timeLimitSeconds)
        {
            _scoreTarget = scoreTarget;
            _timeLimitSeconds = timeLimitSeconds;
        }

        public void OnParticipantOut(IMatchView m, PvpParticipant p, ParticipantExit how)
        {
            _elimination.OnParticipantOut(m, p, how);
        }

        public MatchOutcome Evaluate(IMatchView m, DateTime utcNow)
        {
            var eliminated = _elimination.Evaluate(m, utcNow);

            if (eliminated != null)
                return eliminated;

            if (m.LiveSinceUtc == null)
                return null;

            var reached = m.Teams.Where(t => ScoreOf(m, t.TeamIndex) >= _scoreTarget).Select(t => t.TeamIndex).ToList();

            if (reached.Count > 0)
                return Rank(m, reached, EndReason.Score);

            if ((utcNow - m.LiveSinceUtc.Value).TotalSeconds >= _timeLimitSeconds)
                return Rank(m, m.Teams.Select(t => t.TeamIndex).ToList(), EndReason.Timeout);

            return null;
        }

        /// <summary>
        /// Ranks every team by (score, kills), best first; ties share a placement (1 + teams strictly better). The
        /// winners are the contenders sharing the best (score, kills) key: exactly one is a win, several are a draw.
        /// </summary>
        private static MatchOutcome Rank(IMatchView m, List<int> contenders, EndReason reason)
        {
            var teamIndexes = m.Teams.Select(t => t.TeamIndex).ToList();

            var placements = new int[teamIndexes.Count];

            for (var i = 0; i < teamIndexes.Count; i++)
                placements[i] = 1 + teamIndexes.Count(other => Key(m, other).CompareTo(Key(m, teamIndexes[i])) > 0);

            var best = contenders.Select(i => Key(m, i)).Max();
            var leaders = contenders.Where(i => Key(m, i).CompareTo(best) == 0).ToList();

            if (leaders.Count == 1)
                return new MatchOutcome(new HashSet<int>(leaders), placements, IsDraw: false, Rated: true, Reason: reason);

            return new MatchOutcome(new HashSet<int>(), placements, IsDraw: true, Rated: true, Reason: reason);
        }

        private static (int Score, int Kills) Key(IMatchView m, int teamIndex) => (ScoreOf(m, teamIndex), KillsOf(m, teamIndex));

        private static int ScoreOf(IMatchView m, int teamIndex)
        {
            return m is IScoredMatchView s && s.ScoreBoard.TryGetValue(teamIndex, out var v) ? v : 0;
        }

        private static int KillsOf(IMatchView m, int teamIndex)
        {
            return m is IScoredMatchView s && s.TeamKills.TryGetValue(teamIndex, out var v) ? v : 0;
        }
    }
}
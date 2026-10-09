using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Rating
{
    /// <summary>
    /// 2v2 rating (Docs/Pvp/DESIGN.md "Rating" > "Elo"): "2v2 compares the two teams' average ratings to
    /// get one expected score. Each member then gets delta_i = K_i * (S - E), using their own K." A
    /// forfeiter's RatedPlayer.ForceZeroScore overrides S to 0 for that player only, "even when the team wins".
    /// </summary>
    public sealed class TeamEloModel : IRatingModel
    {
        public IReadOnlyDictionary<uint, int> Deltas(IReadOnlyList<RatedTeam> teams, MatchOutcome outcome)
        {
            if (teams.Count != 2)
                throw new ArgumentException("TeamEloModel supports exactly two teams.", nameof(teams));

            var teamA = teams[0];
            var teamB = teams[1];

            var avgA = teamA.Players.Average(p => (double)p.Rating);
            var avgB = teamB.Players.Average(p => (double)p.Rating);

            var expectedA = EloRating.ExpectedScore(avgA, avgB);
            var expectedB = 1.0 - expectedA;

            var result = new Dictionary<uint, int>();

            ApplyTeam(teamA, expectedA, outcome, result);
            ApplyTeam(teamB, expectedB, outcome, result);

            return result;
        }

        private static void ApplyTeam(RatedTeam team, double expected, MatchOutcome outcome, Dictionary<uint, int> result)
        {
            foreach (var player in team.Players)
            {
                double score;

                if (player.ForceZeroScore)
                    score = 0.0;
                else if (outcome.IsDraw)
                    score = 0.5;
                else
                    score = outcome.WinningTeams.Contains(team.TeamIndex) ? 1.0 : 0.0;

                result[player.CharacterId] = EloRating.Delta(player.K, score, expected);
            }
        }
    }
}

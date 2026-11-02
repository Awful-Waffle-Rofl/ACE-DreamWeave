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
        /// <summary>
        /// Each player's delta rounded once, halves away from zero: exactly <see cref="EloRating.Delta"/> applied to
        /// <see cref="UnroundedDeltas"/>, so the result is the same integer it always was.
        /// </summary>
        public IReadOnlyDictionary<uint, int> Deltas(IReadOnlyList<RatedTeam> teams, MatchOutcome outcome)
        {
            var result = new Dictionary<uint, int>();

            foreach (var kvp in UnroundedDeltas(teams, outcome))
                result[kvp.Key] = Round(kvp.Value);

            return result;
        }

        /// <summary>
        /// Each player's K_i * (S - E) before any rounding (the base the battleground performance modifier scales,
        /// <see cref="PerformanceTeamEloModel"/>). Pure.
        /// </summary>
        public IReadOnlyDictionary<uint, double> UnroundedDeltas(IReadOnlyList<RatedTeam> teams, MatchOutcome outcome)
        {
            if (teams.Count != 2)
                throw new ArgumentException("TeamEloModel supports exactly two teams.", nameof(teams));

            var teamA = teams[0];
            var teamB = teams[1];

            var avgA = teamA.Players.Average(p => (double)p.Rating);
            var avgB = teamB.Players.Average(p => (double)p.Rating);

            var expectedA = EloRating.ExpectedScore(avgA, avgB);
            var expectedB = 1.0 - expectedA;

            var result = new Dictionary<uint, double>();

            ApplyTeam(teamA, expectedA, outcome, result);
            ApplyTeam(teamB, expectedB, outcome, result);

            return result;
        }

        /// <summary>The one rounding rule every Elo delta uses: nearest integer, halves away from zero (as <see cref="EloRating.Delta"/>).</summary>
        public static int Round(double delta) => (int)Math.Round(delta, MidpointRounding.AwayFromZero);

        private static void ApplyTeam(RatedTeam team, double expected, MatchOutcome outcome, Dictionary<uint, double> result)
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

                result[player.CharacterId] = player.K * (score - expected);
            }
        }
    }
}

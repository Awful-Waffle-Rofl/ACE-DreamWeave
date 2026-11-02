using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Rating
{
    /// <summary>The deltas of one rated match, and each player's performance modifier as finally applied (after the zero-sum pass).</summary>
    public sealed record PerformanceDeltas(IReadOnlyDictionary<uint, int> Deltas, IReadOnlyDictionary<uint, double> AppliedModifiers);

    /// <summary>
    /// The battleground rating model (Docs/Pvp/BATTLEGROUNDS.md "Rating and performance"): the team-Elo base B_i, unrounded
    /// (<see cref="TeamEloModel.UnroundedDeltas"/>), scaled by each player's performance modifier and rounded ONCE:
    /// delta_i = round(B_i x (1 + sign(B_i) x m_i)), halves away from zero. A positive m makes a gain bigger and a loss smaller.
    ///
    /// <para/>
    /// Before scaling, each team's m's go through <see cref="BattlegroundPerformanceModifier.BalanceZeroSum"/> weighted by |B_i|, so the team's total
    /// change is the team-Elo total (to within rounding). A forfeiter (<see cref="RatedPlayer.ForceZeroScore"/>) keeps its own m and is left
    /// out of that pass. With every <see cref="RatedPlayer.PerformanceModifier"/> 0 the deltas are exactly <see cref="TeamEloModel"/>'s.
    /// </summary>
    public sealed class PerformanceTeamEloModel : IRatingModel
    {
        private readonly TeamEloModel _teamElo = new TeamEloModel();

        public IReadOnlyDictionary<uint, int> Deltas(IReadOnlyList<RatedTeam> teams, MatchOutcome outcome) => Compute(teams, outcome).Deltas;

        public PerformanceDeltas Compute(IReadOnlyList<RatedTeam> teams, MatchOutcome outcome)
        {
            var baseDeltas = _teamElo.UnroundedDeltas(teams, outcome);
            var applied = new Dictionary<uint, double>();

            foreach (var team in teams)
            {
                foreach (var p in team.Players.Where(p => p.ForceZeroScore))
                    applied[p.CharacterId] = Sane(p.PerformanceModifier);

                var pool = team.Players.Where(p => !p.ForceZeroScore).ToList();
                var balanced = BattlegroundPerformanceModifier.BalanceZeroSum(pool.Select(p => (Math.Abs(baseDeltas[p.CharacterId]), Sane(p.PerformanceModifier))).ToList());

                for (var i = 0; i < pool.Count; i++)
                    applied[pool[i].CharacterId] = balanced[i];
            }

            var deltas = new Dictionary<uint, int>();

            foreach (var kvp in baseDeltas)
            {
                var b = kvp.Value;
                var m = applied.TryGetValue(kvp.Key, out var v) ? v : 0.0;

                deltas[kvp.Key] = TeamEloModel.Round(b * (1.0 + Math.Sign(b) * m));
            }

            return new PerformanceDeltas(deltas, applied);
        }

        /// <summary>A non-finite modifier (NaN or infinite) counts as 0, so it can never poison the zero-sum pass or a delta.</summary>
        private static double Sane(double modifier) => PerformanceDials.Finite(modifier) ? modifier : 0.0;
    }
}

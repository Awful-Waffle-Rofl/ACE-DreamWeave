using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Rating
{
    /// <summary>
    /// FFA pairwise-placement rating (Docs/Pvp/DESIGN.md "Rating" > "FFA"):
    /// delta_i = (K_ffa / M_i) * sum over j (S_ij - E_ij), S_ij is 1/0.5/0 by placement compare, M_i is the
    /// opponent count after same-IP exclusions. Zero-sum whenever every K and every M_i are equal, because
    /// S_ij - E_ij is antisymmetric across the pair (i, j).
    /// </summary>
    public sealed class PairwisePlacementEloModel : IRatingModel
    {
        private readonly int _kFfa;

        public PairwisePlacementEloModel(int kFfa)
        {
            _kFfa = kFfa;
        }

        public IReadOnlyDictionary<uint, int> Deltas(IReadOnlyList<RatedTeam> teams, MatchOutcome outcome)
        {
            // FFA is one player per team - PvpModes.Ffa() enforces TeamSize == 1.
            var entrants = teams.Select(t => (t.TeamIndex, Player: t.Players.Single())).ToList();

            var result = new Dictionary<uint, int>();

            foreach (var (indexI, playerI) in entrants)
            {
                double sum = 0;
                var m = 0;

                foreach (var (indexJ, playerJ) in entrants)
                {
                    if (indexJ == indexI)
                        continue;

                    if (playerI.IpKey != null && playerI.IpKey == playerJ.IpKey)
                        continue;

                    var placementI = outcome.Placements[indexI];
                    var placementJ = outcome.Placements[indexJ];

                    double sIj;
                    if (placementI < placementJ)
                        sIj = 1.0;
                    else if (placementI == placementJ)
                        sIj = 0.5;
                    else
                        sIj = 0.0;

                    var eIj = EloRating.ExpectedScore(playerI.Rating, playerJ.Rating);

                    sum += sIj - eIj;
                    m++;
                }

                result[playerI.CharacterId] = m == 0 ? 0 : (int)System.Math.Round((_kFfa / (double)m) * sum, System.MidpointRounding.AwayFromZero);
            }

            return result;
        }
    }
}

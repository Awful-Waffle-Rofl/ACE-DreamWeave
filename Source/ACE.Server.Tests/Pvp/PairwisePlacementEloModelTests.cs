using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// FFA pairwise placement Elo (Docs/Pvp/DESIGN.md "Rating" > "FFA"): zero-sum with equal ratings/K,
    /// ties score 0.5, and same-IP exclusion changes M_i (and therefore the delta).
    /// </summary>
    [TestClass]
    public class PairwisePlacementEloModelTests
    {
        private static RatedTeam Solo(int index, uint characterId, int rating, string ipKey = null) =>
            new RatedTeam(index, new[] { new RatedPlayer(characterId, rating, 32, ipKey) });

        [TestMethod]
        public void EqualRatings_IsExactlyZeroSum()
        {
            var teams = new[]
            {
                Solo(0, 1, 1500),
                Solo(1, 2, 1500),
                Solo(2, 3, 1500),
                Solo(3, 4, 1500)
            };

            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2, 3, 4 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new PairwisePlacementEloModel(32).Deltas(teams, outcome);

            Assert.AreEqual(0, deltas.Values.Sum());
        }

        [TestMethod]
        public void EqualRatings_BetterPlacementIsPositive()
        {
            var teams = new[]
            {
                Solo(0, 1, 1500),
                Solo(1, 2, 1500),
                Solo(2, 3, 1500),
                Solo(3, 4, 1500)
            };

            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2, 3, 4 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new PairwisePlacementEloModel(32).Deltas(teams, outcome);

            Assert.IsTrue(deltas[1] > deltas[2]);
            Assert.IsTrue(deltas[2] > deltas[3]);
            Assert.IsTrue(deltas[3] > deltas[4]);
        }

        [TestMethod]
        public void TiedPlacement_ScoresHalf()
        {
            // Two players tie for 1st (placement 1), equal ratings -> S_ij between them is 0.5,
            // so neither gains or loses versus the other, but both still beat placement-3.
            var teams = new[]
            {
                Solo(0, 1, 1500),
                Solo(1, 2, 1500),
                Solo(2, 3, 1500)
            };

            var outcome = new MatchOutcome(new HashSet<int> { 0, 1 }, new[] { 1, 1, 3 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new PairwisePlacementEloModel(32).Deltas(teams, outcome);

            Assert.AreEqual(deltas[1], deltas[2]);
            Assert.IsTrue(deltas[1] > deltas[3]);
        }

        [TestMethod]
        public void SameIpExclusion_ChangesMAndDelta()
        {
            // Player 1 and player 2 share an IP; player 2 is rated differently from player 3, so the
            // per-opponent (S-E) terms are NOT uniform - excluding player 2 from M_i therefore changes
            // both M_i and the resulting delta for player 1, not just the count.
            var withSharedIp = new[]
            {
                Solo(0, 1, 1500, ipKey: "shared"),
                Solo(1, 2, 1400, ipKey: "shared"),
                Solo(2, 3, 1500)
            };

            var noSharedIp = new[]
            {
                Solo(0, 1, 1500),
                Solo(1, 2, 1400),
                Solo(2, 3, 1500)
            };

            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2, 3 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltasExcluded = new PairwisePlacementEloModel(32).Deltas(withSharedIp, outcome);
            var deltasIncluded = new PairwisePlacementEloModel(32).Deltas(noSharedIp, outcome);

            Assert.AreNotEqual(deltasIncluded[1], deltasExcluded[1]);
        }

        [TestMethod]
        public void OnlyOpponentsShareIp_MiIsZero_DeltaIsZero()
        {
            // Both entrants share an IP and there is no one else in the match, so every opponent is
            // excluded and M_i = 0 for both - the model must not divide by zero, and a delta with no
            // counted opponents is defined as 0.
            var teams = new[]
            {
                Solo(0, 1, 1500, ipKey: "shared"),
                Solo(1, 2, 1600, ipKey: "shared")
            };

            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new PairwisePlacementEloModel(32).Deltas(teams, outcome);

            Assert.AreEqual(0, deltas[1]);
            Assert.AreEqual(0, deltas[2]);
        }
    }
}

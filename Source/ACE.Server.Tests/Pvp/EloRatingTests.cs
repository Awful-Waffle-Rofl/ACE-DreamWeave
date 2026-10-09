using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>Pure Elo math: expected score symmetry, delta symmetry, and the K provisional/established switch.</summary>
    [TestClass]
    public class EloRatingTests
    {
        [TestMethod]
        public void ExpectedScore_IsSymmetric()
        {
            var eAB = EloRating.ExpectedScore(1600, 1500);
            var eBA = EloRating.ExpectedScore(1500, 1600);

            Assert.AreEqual(1.0, eAB + eBA, 1e-9);
        }

        [TestMethod]
        public void ExpectedScore_EqualRatings_IsHalf()
        {
            Assert.AreEqual(0.5, EloRating.ExpectedScore(1500, 1500), 1e-9);
        }

        [TestMethod]
        public void Delta_IsZeroSumForEqualK()
        {
            var e = EloRating.ExpectedScore(1500, 1500);

            var deltaWinner = EloRating.Delta(32, 1.0, e);
            var deltaLoser = EloRating.Delta(32, 0.0, 1.0 - e);

            Assert.AreEqual(0, deltaWinner + deltaLoser);
        }

        [TestMethod]
        public void SelectK_BelowProvisionalBoundary_UsesProvisionalK()
        {
            Assert.AreEqual(40, EloRating.SelectK(gamesPlayed: 9, provisionalGames: 10, kProvisional: 40, kEstablished: 24));
        }

        [TestMethod]
        public void SelectK_AtProvisionalBoundary_UsesEstablishedK()
        {
            Assert.AreEqual(24, EloRating.SelectK(gamesPlayed: 10, provisionalGames: 10, kProvisional: 40, kEstablished: 24));
        }

        [TestMethod]
        public void SelectK_ZeroGames_UsesProvisionalK()
        {
            Assert.AreEqual(40, EloRating.SelectK(gamesPlayed: 0, provisionalGames: 10, kProvisional: 40, kEstablished: 24));
        }
    }
}

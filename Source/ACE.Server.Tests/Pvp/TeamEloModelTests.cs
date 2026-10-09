using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// 2v2 team Elo: team-average expected score, each member's own K, and the forfeiter S=0 override
    /// "even when the team wins" (Docs/Pvp/DESIGN.md "Rating" > "Elo").
    /// </summary>
    [TestClass]
    public class TeamEloModelTests
    {
        private static RatedTeam Team(int index, params RatedPlayer[] players) => new RatedTeam(index, players);

        [TestMethod]
        public void UsesTeamAverageRatings_NotIndividualRatings()
        {
            // Team A averages 1500 (1400 + 1600), team B is a flat 1500 - expected score should be 0.5 each,
            // not skewed by the 1400/1600 split within team A.
            var teamA = Team(0, new RatedPlayer(1, 1400, 24), new RatedPlayer(2, 1600, 24));
            var teamB = Team(1, new RatedPlayer(3, 1500, 24), new RatedPlayer(4, 1500, 24));

            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new TeamEloModel().Deltas(new[] { teamA, teamB }, outcome);

            // S=1, E=0.5 -> delta = K * 0.5 = 12 for both winners
            Assert.AreEqual(12, deltas[1]);
            Assert.AreEqual(12, deltas[2]);
        }

        [TestMethod]
        public void EachMemberUsesTheirOwnK()
        {
            var teamA = Team(0, new RatedPlayer(1, 1500, 40), new RatedPlayer(2, 1500, 24));
            var teamB = Team(1, new RatedPlayer(3, 1500, 24), new RatedPlayer(4, 1500, 24));

            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new TeamEloModel().Deltas(new[] { teamA, teamB }, outcome);

            // E = 0.5 for both teams (equal averages); S=1 for team A -> delta = K * 0.5
            Assert.AreEqual(20, deltas[1]); // K=40
            Assert.AreEqual(12, deltas[2]); // K=24
        }

        [TestMethod]
        public void Forfeiter_AlwaysTakesZeroScore_EvenWhenTeamWins()
        {
            var teamA = Team(0,
                new RatedPlayer(1, 1500, 24, ForceZeroScore: true), // forfeited
                new RatedPlayer(2, 1500, 24));
            var teamB = Team(1, new RatedPlayer(3, 1500, 24), new RatedPlayer(4, 1500, 24));

            // Team A is recorded as the winning team even though player 1 forfeited.
            var outcome = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, IsDraw: false, Rated: true, Reason: EndReason.Elimination);

            var deltas = new TeamEloModel().Deltas(new[] { teamA, teamB }, outcome);

            // Forfeiter: S=0, E=0.5 -> delta = 24 * (0 - 0.5) = -12
            Assert.AreEqual(-12, deltas[1]);
            // Non-forfeiting winning teammate: S=1, E=0.5 -> delta = 12
            Assert.AreEqual(12, deltas[2]);
        }

        [TestMethod]
        public void Draw_GivesHalfScoreToEveryNonForfeiter()
        {
            var teamA = Team(0, new RatedPlayer(1, 1500, 24));
            var teamB = Team(1, new RatedPlayer(2, 1500, 24));

            var outcome = new MatchOutcome(new HashSet<int>(), new[] { 1, 1 }, IsDraw: true, Rated: false, Reason: EndReason.Timeout);

            var deltas = new TeamEloModel().Deltas(new[] { teamA, teamB }, outcome);

            Assert.AreEqual(0, deltas[1]);
            Assert.AreEqual(0, deltas[2]);
        }

        /// <summary>
        /// A RATED draw between unequal teams scores S = 0.5 for both sides, so the favourite loses a little and the
        /// underdog gains the same. Scoring a draw as 0 would give -18 and -6 instead. No v1 mode produces a rated
        /// 1v1/2v2 draw today (timeouts and double knockouts are unrated); this pins the model for when one does.
        /// </summary>
        [TestMethod]
        public void RatedDraw_ScoresHalf_UnequalRatings()
        {
            var teamA = Team(0, new RatedPlayer(1, 1600, 24));
            var teamB = Team(1, new RatedPlayer(2, 1400, 24));

            var outcome = new MatchOutcome(new HashSet<int>(), new[] { 1, 1 }, IsDraw: true, Rated: true, Reason: EndReason.Elimination);

            var deltas = new TeamEloModel().Deltas(new[] { teamA, teamB }, outcome);

            Assert.AreEqual(-6, deltas[1]);
            Assert.AreEqual(6, deltas[2]);
        }
    }
}

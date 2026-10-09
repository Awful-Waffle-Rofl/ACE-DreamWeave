using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// EliminationWinCondition.ResolveTimeout (Docs/Pvp/DESIGN.md "Modes" table's Timeout column, added
    /// per code review on PR #1396): null before the time limit; 1v1/2v2 always resolve to an unrated
    /// draw at the limit; FFA has survivors share 1st, with already-eliminated teams keeping their placement,
    /// rated per the mode's own TimeoutRated.
    /// </summary>
    [TestClass]
    public class EliminationWinConditionTimeoutTests
    {
        private static readonly DateTime LiveSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch BuildSoloMatch(int teamCount)
        {
            var teams = new List<PvpTeam>();
            for (var i = 0; i < teamCount; i++)
                teams.Add(new PvpTeam(i, new List<PvpParticipant> { new PvpParticipant((uint)(i + 1), 1500) }));

            var match = new PvpMatch(Guid.NewGuid(), "test", teams, LiveSince);
            match.GoLive(LiveSince);
            return match;
        }

        [TestMethod]
        public void BeforeTheLimit_ReturnsNull_1v1Style()
        {
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(599), timeLimitSeconds: 600, survivorsShareFirst: false, rated: false);

            Assert.IsNull(outcome);
        }

        [TestMethod]
        public void BeforeTheLimit_ReturnsNull_FfaStyle()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(899), timeLimitSeconds: 900, survivorsShareFirst: true, rated: true);

            Assert.IsNull(outcome);
        }

        [TestMethod]
        public void AtTheLimit_1v1Style_IsAnUnratedDraw()
        {
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(600), timeLimitSeconds: 600, survivorsShareFirst: false, rated: false);

            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.IsDraw);
            Assert.IsFalse(outcome.Rated);
            Assert.AreEqual(EndReason.Timeout, outcome.Reason);
            Assert.AreEqual(0, outcome.WinningTeams.Count);
        }

        [TestMethod]
        public void AtTheLimit_FfaStyle_SurvivorsShareFirst_EliminatedKeepPlacement()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            // Two teams already eliminated by combat before the time limit hits.
            wc.OnParticipantOut(match, match.Teams[4].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(1));

            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(2));

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, survivorsShareFirst: true, rated: true);

            Assert.IsNotNull(outcome);
            Assert.IsFalse(outcome.IsDraw);
            Assert.IsTrue(outcome.Rated);
            Assert.AreEqual(EndReason.Timeout, outcome.Reason);

            // The three still-alive teams (0, 1, 2) share 1st.
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, outcome.WinningTeams.ToList());
            Assert.AreEqual(1, outcome.Placements[0]);
            Assert.AreEqual(1, outcome.Placements[1]);
            Assert.AreEqual(1, outcome.Placements[2]);

            // DESIGN "Win condition": an eliminated team's placement is the number of teams still alive when it
            // was eliminated, plus 1 - the same rule at a timeout as in Evaluate. Team 4 went out first with 4
            // teams left (5th); team 3 went out with 3 left (4th). Survivors sharing 1st does not pull them up.
            Assert.AreEqual(4, outcome.Placements[3]);
            Assert.AreEqual(5, outcome.Placements[4]);
        }

        [TestMethod]
        public void RespectsTimeoutRated_FalseForFfaWouldStillBeUnrated()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, survivorsShareFirst: true, rated: false);

            Assert.IsNotNull(outcome);
            Assert.IsFalse(outcome.Rated);
        }
    }
}

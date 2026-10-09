using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    [TestClass]
    public class ScoreWinConditionTests
    {
        private const int Target = 100;
        private const int Limit = 600;

        private static readonly DateTime Start = FakeBattlegroundContext.Start;

        private static ScoreWinCondition Wc() => new ScoreWinCondition(Target, Limit);

        [TestMethod]
        public void BelowTargetBeforeLimit_Continues()
        {
            var m = new FakeBattlegroundContext();
            m.ScoreBoard[0] = Target - 1;
            Assert.IsNull(Wc().Evaluate(m, Start.AddSeconds(Limit - 1)));
        }

        [TestMethod]
        public void TeamAtTarget_WinsWithScoreReason_Rated()
        {
            var m = new FakeBattlegroundContext();
            m.ScoreBoard[1] = Target;
            m.ScoreBoard[0] = 40;

            var o = Wc().Evaluate(m, Start.AddSeconds(10));

            Assert.IsNotNull(o);
            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList());
            Assert.AreEqual(EndReason.Score, o.Reason);
            Assert.IsTrue(o.Rated);
            Assert.IsFalse(o.IsDraw);
            Assert.AreEqual(1, o.Placements[1]);
            Assert.AreEqual(2, o.Placements[0]);
        }

        [TestMethod]
        public void Timeout_MorePointsWins()
        {
            var m = new FakeBattlegroundContext();
            m.ScoreBoard[0] = 30;
            m.ScoreBoard[1] = 50;
            m.TeamKills[0] = 9;

            var o = Wc().Evaluate(m, Start.AddSeconds(Limit));

            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList());
            Assert.AreEqual(EndReason.Timeout, o.Reason);
            Assert.IsTrue(o.Rated);
        }

        [TestMethod]
        public void Timeout_TiedPoints_MoreKillsWins()
        {
            var m = new FakeBattlegroundContext();
            m.ScoreBoard[0] = 50;
            m.ScoreBoard[1] = 50;
            m.TeamKills[0] = 3;
            m.TeamKills[1] = 5;

            var o = Wc().Evaluate(m, Start.AddSeconds(Limit));

            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList());
            Assert.IsFalse(o.IsDraw);
        }

        [TestMethod]
        public void Timeout_TiedPointsAndKills_IsARatedDraw()
        {
            var m = new FakeBattlegroundContext();
            m.ScoreBoard[0] = 50;
            m.ScoreBoard[1] = 50;
            m.TeamKills[0] = 4;
            m.TeamKills[1] = 4;

            var o = Wc().Evaluate(m, Start.AddSeconds(Limit));

            Assert.IsTrue(o.IsDraw);
            Assert.IsTrue(o.Rated);
            Assert.AreEqual(0, o.WinningTeams.Count);
            Assert.AreEqual(EndReason.Timeout, o.Reason);
            Assert.AreEqual(1, o.Placements[0]);
            Assert.AreEqual(1, o.Placements[1]);
        }

        [TestMethod]
        public void NotLive_OnlyEliminationApplies()
        {
            var m = new FakeBattlegroundContext { LiveSinceUtc = null };
            m.ScoreBoard[0] = Target + 50;

            Assert.IsNull(Wc().Evaluate(m, Start.AddSeconds(Limit * 2)));
        }

        [TestMethod]
        public void NonScoredView_ScoresAsZeros_AndDrawsAtTimeout()
        {
            var match = new PvpMatch(Guid.NewGuid(), "x", new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            }, Start);
            match.GoLive(Start);

            var o = Wc().Evaluate(new UnscoredView(match), Start.AddSeconds(Limit));

            Assert.IsTrue(o.IsDraw);
        }

        [TestMethod]
        public void EmptyTeam_StillWinsByElimination_AheadOfScore()
        {
            var m = new FakeBattlegroundContext(perTeam: 2);
            var wc = Wc();

            // team 0 is ahead on points, but every member of it forfeits.
            m.ScoreBoard[0] = Target + 10;
            foreach (var p in m.TeamList[0].Members)
                wc.OnParticipantOut(m, p, ParticipantExit.ForfeitCommand);

            var o = wc.Evaluate(m, Start.AddSeconds(5));

            Assert.IsNotNull(o);
            Assert.AreEqual(EndReason.Elimination, o.Reason);
            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList());
            Assert.AreEqual(2, o.Placements[0]);
        }

        [TestMethod]
        public void PartialTeamOut_DoesNotEliminate()
        {
            var m = new FakeBattlegroundContext(perTeam: 2);
            var wc = Wc();

            wc.OnParticipantOut(m, m.Member(0, 0), ParticipantExit.ForfeitCommand);

            Assert.IsNull(wc.Evaluate(m, Start.AddSeconds(5)));
        }

        private sealed class UnscoredView : IMatchView
        {
            private readonly PvpMatch _m;
            public UnscoredView(PvpMatch m) { _m = m; }
            public Guid MatchId => _m.MatchId;
            public string ModeKey => _m.ModeKey;
            public IReadOnlyList<PvpTeam> Teams => _m.Teams;
            public PvpMatchState State => _m.State;
            public DateTime? LiveSinceUtc => _m.LiveSinceUtc;
        }
    }
}
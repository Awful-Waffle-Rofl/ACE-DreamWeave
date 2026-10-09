using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>The Attack/Defend win condition: elimination, then every crystal destroyed, then the clock (a RATED defender win).</summary>
    [TestClass]
    public class CrystalWinConditionTests
    {
        private const int Crystals = 3;
        private const int Limit = 600;

        private static readonly DateTime Start = FakeObjectiveContext.Start;

        private static CrystalWinCondition Wc() => new CrystalWinCondition(Crystals, Limit);

        [TestMethod]
        public void MidMatch_Continues()
        {
            var m = new FakeObjectiveContext();
            m.ScoreBoard[0] = Crystals - 1;

            Assert.IsNull(Wc().Evaluate(m, Start.AddSeconds(Limit - 1)));
        }

        [TestMethod]
        public void AllCrystalsDestroyed_AttackersWin_WithScoreReason_Rated()
        {
            var m = new FakeObjectiveContext();
            m.ScoreBoard[0] = Crystals;

            var o = Wc().Evaluate(m, Start.AddSeconds(10));

            Assert.IsNotNull(o);
            CollectionAssert.AreEquivalent(new[] { 0 }, o.WinningTeams.ToList());
            Assert.AreEqual(EndReason.Score, o.Reason);
            Assert.IsTrue(o.Rated);
            Assert.IsFalse(o.IsDraw);
            Assert.AreEqual(1, o.Placements[0]);
            Assert.AreEqual(2, o.Placements[1]);
        }

        [TestMethod]
        public void PartialAtTimeout_DefendersWin_Rated_NeverADraw()
        {
            var m = new FakeObjectiveContext();
            m.ScoreBoard[0] = Crystals - 1;

            var o = Wc().Evaluate(m, Start.AddSeconds(Limit));

            Assert.IsNotNull(o);
            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList());
            Assert.AreEqual(EndReason.Timeout, o.Reason);
            Assert.IsTrue(o.Rated, "a timeout is a rated defender win");
            Assert.IsFalse(o.IsDraw);
            Assert.AreEqual(1, o.Placements[1]);
            Assert.AreEqual(2, o.Placements[0]);
        }

        [TestMethod]
        public void NothingDestroyedAtTimeout_DefendersStillWin()
        {
            var m = new FakeObjectiveContext();

            var o = Wc().Evaluate(m, Start.AddSeconds(Limit + 30));

            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList());
            Assert.AreEqual(EndReason.Timeout, o.Reason);
        }

        /// <summary>The last crystal falling on the very tick the clock runs out is an attacker win: the score check runs before the clock.</summary>
        [TestMethod]
        public void DestroyedOnTheTimeoutTick_AttackersWin()
        {
            var m = new FakeObjectiveContext();
            m.ScoreBoard[0] = Crystals;

            var o = Wc().Evaluate(m, Start.AddSeconds(Limit));

            CollectionAssert.AreEquivalent(new[] { 0 }, o.WinningTeams.ToList());
            Assert.AreEqual(EndReason.Score, o.Reason);
        }

        [TestMethod]
        public void OneSecondBeforeTheLimit_Continues_AtTheLimit_Ends()
        {
            var m = new FakeObjectiveContext();

            Assert.IsNull(Wc().Evaluate(m, Start.AddSeconds(Limit - 1)));
            Assert.IsNotNull(Wc().Evaluate(m, Start.AddSeconds(Limit)));
        }

        /// <summary>Elimination is checked first: a team with no active members loses, whatever the score and the clock say.</summary>
        [TestMethod]
        public void Elimination_IsCheckedBeforeScoreAndClock()
        {
            var m = new FakeObjectiveContext();
            var wc = Wc();

            // The attackers have every crystal AND the clock is up, but the attacking team has been wiped by forfeits.
            m.ScoreBoard[0] = Crystals;

            foreach (var p in m.Teams[0].Members)
                wc.OnParticipantOut(m, p, ParticipantExit.ForfeitCommand);

            var o = wc.Evaluate(m, Start.AddSeconds(Limit));

            Assert.IsNotNull(o);
            CollectionAssert.AreEquivalent(new[] { 1 }, o.WinningTeams.ToList(), "the surviving defenders win, not the attackers");
            Assert.AreNotEqual(EndReason.Score, o.Reason);
            Assert.AreNotEqual(EndReason.Timeout, o.Reason);
        }

        [TestMethod]
        public void BeforeLive_OnlyEliminationApplies()
        {
            var m = new FakeObjectiveContext { LiveSinceUtc = null };
            m.ScoreBoard[0] = Crystals;

            Assert.IsNull(Wc().Evaluate(m, Start.AddSeconds(Limit * 2)), "not Live: no score win and no timeout");
        }

        [TestMethod]
        public void ZeroCrystals_IsNeverAnInstantAttackerWin()
        {
            var m = new FakeObjectiveContext();

            Assert.IsNull(new CrystalWinCondition(0, Limit).Evaluate(m, Start.AddSeconds(1)));
        }

        /// <summary>A plain PvpMatch (no tick context) works too: it is the view the coordinator holds.</summary>
        [TestMethod]
        public void WorksOnAPlainPvpMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };
            var match = new PvpMatch(Guid.NewGuid(), BattlegroundModes.AttackDefendModeKey, teams, Start);
            match.GoLive(Start);
            match.ScoreBoard[0] = Crystals;

            var o = Wc().Evaluate(match, Start.AddSeconds(5));

            CollectionAssert.AreEquivalent(new[] { 0 }, o.WinningTeams.ToList());
        }
    }
}

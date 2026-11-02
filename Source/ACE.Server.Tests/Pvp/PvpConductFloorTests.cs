using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The contribution floor decision (Docs/Pvp/BATTLEGROUNDS.md "Contribution floor"). Pure; the dials are the compiled defaults
    /// (floor on, share 0.25, min engaged 30 s).
    /// </summary>
    [TestClass]
    public class PvpConductFloorTests
    {
        private static readonly PerformanceDials Perf = PerformanceDials.From(BattlegroundTunables.Defaults);
        private static readonly PvpConductDials On = PvpConductTunables.Defaults;

        private static SeatPerformanceSnapshot Stats(uint id, int team, long dmg = 0, long kills = 0, long hill = 0, long healing = 0, long engaged = 0, long crystal = 0)
            => new SeatPerformanceSnapshot(id, team, dmg, crystal, 0, 0, kills, 0, 0, 0, hill, healing, engaged);

        private static PerformanceSeat Seat(SeatPerformanceSnapshot s, bool forfeited = false, bool dispatched = true)
            => new PerformanceSeat(s.CharacterId, s.TeamIndex, s, $"10.0.0.{s.CharacterId}", forfeited, dispatched);

        [TestMethod]
        public void HealerOnlySeat_PassesTheFloor()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, healing: 900)),
                Seat(Stats(2, 0, dmg: 600, kills: 2, hill: 30, engaged: 100)),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf);

            Assert.IsFalse(d[1].BelowFloor, "healing alone is a contribution (C = 2.0 via healing, and healing > 0 clears the absolute test)");
            Assert.IsFalse(d[2].BelowFloor);
        }

        [TestMethod]
        public void SlackerNextToATeammate_IsBelowTheRelativeFloor()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 10, engaged: 40)),
                Seat(Stats(2, 0, dmg: 990, kills: 3, hill: 300, engaged: 960)),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf);

            Assert.IsTrue(d[1].BelowFloor);
            Assert.AreEqual("relative", d[1].Reason, "engaged 40 >= 30 and damage > 0, so only the relative test fails");
            Assert.IsFalse(d[2].BelowFloor);
        }

        [TestMethod]
        public void ZeroAverageDimension_IsSkipped_NotDividedBy()
        {
            // Nobody on the team heals: the healing dimension (average 0) is skipped, the seat is judged on the rest.
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 500, engaged: 100)),
                Seat(Stats(2, 0, dmg: 500, engaged: 100)),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf);

            Assert.IsFalse(d[1].BelowFloor);
            Assert.IsFalse(d[2].BelowFloor);
            Assert.AreEqual(1.0, d[1].Contribution.Value, 1e-9);
        }

        [TestMethod]
        public void AllAfkTeam_IsCaughtByTheAbsoluteFloor()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0)),
                Seat(Stats(2, 0)),
                Seat(Stats(3, 1, dmg: 400, engaged: 100)),
                Seat(Stats(4, 1, dmg: 400, engaged: 100)),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf);

            Assert.IsNull(d[1].Contribution, "every average is 0, so there is no C");
            Assert.IsTrue(d[1].BelowFloor);
            Assert.AreEqual("absolute", d[1].Reason);
            Assert.IsTrue(d[2].BelowFloor);
            Assert.IsFalse(d[3].BelowFloor);
            Assert.IsFalse(d[4].BelowFloor);
        }

        [TestMethod]
        public void SingleEligibleSeat_UsesTheAbsoluteFloor()
        {
            // The relative test is meaningless alone (C is 1.0 against itself); the absolute test still bites.
            var afk = new[] { Seat(Stats(1, 0)), Seat(Stats(2, 0), forfeited: true) };
            var active = new[] { Seat(Stats(1, 0, dmg: 50, engaged: 5)), Seat(Stats(2, 0), forfeited: true) };

            Assert.IsTrue(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, afk, On, Perf)[1].BelowFloor);
            Assert.IsFalse(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, active, On, Perf)[1].BelowFloor, "damage dealt clears the absolute test");
        }

        [TestMethod]
        public void CrystalAttacker_WithFewEngagedSeconds_IsNotBelowTheFloor()
        {
            // Crystal damage is a contribution of its own: 5 engaged seconds and no player damage must not trip the absolute test.
            var seats = new[]
            {
                Seat(Stats(1, 0, engaged: 5, crystal: 500)),
                Seat(Stats(2, 0, dmg: 300, crystal: 500, engaged: 100)),
                Seat(Stats(3, 1, dmg: 300, engaged: 100)),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.AttackDefendModeKey, seats, On, Perf);

            Assert.IsFalse(d[1].BelowFloor);
            Assert.IsNull(d[1].Reason);
        }

        [TestMethod]
        public void KillshotsOnlyOrHillOnly_AreExemptFromTheAbsoluteTest()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, kills: 1)),
                Seat(Stats(2, 0, hill: 20)),
                Seat(Stats(3, 0, dmg: 500, engaged: 100)),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf);

            Assert.IsFalse(d[1].Reason != null && d[1].Reason.Contains("absolute"));
            Assert.IsFalse(d[2].Reason != null && d[2].Reason.Contains("absolute"));
        }

        [TestMethod]
        public void ShortMatch_ScalesTheAbsoluteThresholdToHalfTheLiveSeconds()
        {
            // 25 engaged seconds is under the 30 s dial, so on a long match this idle seat is below the floor ...
            var seats = new[] { Seat(Stats(1, 0, engaged: 25)), Seat(Stats(2, 0), forfeited: true) };

            Assert.IsTrue(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf, liveSeconds: 600)[1].BelowFloor, "long match: threshold is the 30 s dial");
            Assert.IsTrue(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf)[1].BelowFloor, "no live duration given: the dial alone");

            // ... but in a 40 s match the bar is 20 s, and 25 clears it.
            Assert.IsFalse(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf, liveSeconds: 40)[1].BelowFloor, "40 s match: threshold min(30, 20) = 20");

            // A seat that was never engaged and did nothing, in a 10 s match: threshold 5, so 0 engaged is still below.
            var idle = new[] { Seat(Stats(1, 0)), Seat(Stats(2, 0), forfeited: true) };
            Assert.IsTrue(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, idle, On, Perf, liveSeconds: 10)[1].BelowFloor);

            // A match that was never Live (0 s) or has a nonsense duration cannot withhold anyone by the absolute test.
            Assert.IsFalse(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, idle, On, Perf, liveSeconds: 0)[1].BelowFloor);
            Assert.IsFalse(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, idle, On, Perf, liveSeconds: -5)[1].BelowFloor);
            Assert.IsFalse(PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, idle, On, Perf, liveSeconds: double.NaN)[1].BelowFloor);
        }

        [TestMethod]
        public void Disabled_NeverBelowFloor()        {
            var seats = new[] { Seat(Stats(1, 0)), Seat(Stats(2, 0)) };

            Assert.AreEqual(0, PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On with { FloorEnabled = false }, Perf).Count);
            Assert.AreEqual(0, PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, null, Perf).Count);
        }

        [TestMethod]
        public void ForfeiterAndUndispatchedSeats_AreNotJudged()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 500, engaged: 100)),
                Seat(Stats(2, 0), forfeited: true),
                Seat(Stats(3, 0), dispatched: false),
            };

            var d = PvpConductFloor.Evaluate(BattlegroundModes.KothModeKey, seats, On, Perf);

            CollectionAssert.AreEquivalent(new uint[] { 1 }, d.Keys.ToArray());
        }

        [TestMethod]
        public void Tugak_JudgesAgainstTheWholeField()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 900, kills: 3, engaged: 100)),
                Seat(Stats(2, 1, dmg: 300, kills: 1, engaged: 100)),
                Seat(Stats(3, 2, dmg: 0, kills: 0, engaged: 10)),
            };

            var d = PvpConductFloor.Evaluate(ArenaMapCatalog.FfaKey, seats, On, Perf);

            Assert.IsFalse(d[1].BelowFloor);
            Assert.IsFalse(d[2].BelowFloor);
            Assert.IsTrue(d[3].BelowFloor);
        }

        [TestMethod]
        public void OneVOne_HasNoDimensions_AndOnlyTheAbsoluteTestApplies()
        {
            var seats = new[] { Seat(Stats(1, 0)), Seat(Stats(2, 1, dmg: 5)) };

            var d = PvpConductFloor.Evaluate(ArenaMapCatalog.OneVOneKey, seats, On, Perf);

            Assert.IsNull(d[1].Contribution);
            Assert.IsTrue(d[1].BelowFloor, "(the coordinator never asks: a 1v1 has no ledger)");
            Assert.IsFalse(d[2].BelowFloor);
        }
    }
}
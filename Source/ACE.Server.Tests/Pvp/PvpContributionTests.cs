using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The contribution share C (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): the highest ratio across dimensions to the eligible reference
    /// average. Pure; reads no setting and no PropertyManager key (the dials are built from the compiled defaults).
    /// </summary>
    [TestClass]
    public class PvpContributionTests
    {
        private static readonly PerformanceDials Dials = PerformanceDials.From(BattlegroundTunables.Defaults);

        private static SeatPerformanceSnapshot Stats(uint id, int team, long dmg = 0, long kills = 0, long hill = 0, long healing = 0, long engaged = 0, long crystal = 0)
            => new SeatPerformanceSnapshot(id, team, dmg, crystal, 0, 0, kills, 0, 0, 0, hill, healing, engaged);

        private static PerformanceSeat Seat(SeatPerformanceSnapshot s, bool forfeited = false, bool dispatched = true)
            => new PerformanceSeat(s.CharacterId, s.TeamIndex, s, $"10.0.0.{s.CharacterId}", forfeited, dispatched);

        /// <summary>
        /// A healer who deals no damage, holds no hill and kills nobody still gets a high C through HealingDone: on a team where only they
        /// heal, their healing is 3x the team average (one of three seats), so C = 3, from the healing dimension.
        /// </summary>
        [TestMethod]
        public void HealerOnly_GetsAHighC_ThroughHealing()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, healing: 900)),
                Seat(Stats(2, 0, dmg: 600, kills: 2, hill: 30, engaged: 100)),
                Seat(Stats(3, 0, dmg: 300, kills: 1, hill: 60, engaged: 100)),
            };

            var c = PvpContribution.Compute(BattlegroundModes.KothModeKey, seats, Dials);

            Assert.AreEqual(3.0, c[1].Contribution.Value, 1e-9);
            Assert.AreEqual(PvpContribution.DimensionHealing, c[1].Dimension);
        }

        /// <summary>Negative control for the healer case: with HealingDone left out the same seat would be at 0 (every other stat is 0).</summary>
        [TestMethod]
        public void WithoutHealing_TheSameSeatWouldBeZero()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0)),
                Seat(Stats(2, 0, dmg: 600, kills: 2, hill: 30, engaged: 100)),
                Seat(Stats(3, 0, dmg: 300, kills: 1, hill: 60, engaged: 100)),
            };

            Assert.AreEqual(0.0, PvpContribution.Compute(BattlegroundModes.KothModeKey, seats, Dials)[1].Contribution.Value, 1e-9);
        }

        /// <summary>
        /// A dimension nobody touched (average 0) is skipped rather than counted as a ratio of 1: with no healing on the team, a seat with
        /// nothing else gets C = 0, not 1. And when EVERY dimension averages 0, C is null.
        /// </summary>
        [TestMethod]
        public void ZeroAverageDimension_IsSkipped()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 100)),
                Seat(Stats(2, 0)),
            };

            var c = PvpContribution.Compute(BattlegroundModes.KothModeKey, seats, Dials);

            Assert.AreEqual(2.0, c[1].Contribution.Value, 1e-9, "100 against an average of 50");
            Assert.AreEqual(0.0, c[2].Contribution.Value, 1e-9, "0 damage; healing, hill, kills and engaged all average 0 and are skipped");

            var empty = PvpContribution.Compute(BattlegroundModes.KothModeKey, new[] { Seat(Stats(1, 0)), Seat(Stats(2, 0)) }, Dials);
            Assert.IsNull(empty[1].Contribution, "nothing to compare against");
            Assert.IsNull(empty[1].Dimension);
        }

        /// <summary>
        /// Forfeiters are left out of the reference average. A team of a 600-damage forfeiter and two players at 100 and 200: the average is
        /// 150 (not 300), so the 200 player's C is 4/3. Counting the forfeiter would make it 2/3. The forfeiter still gets a C of its own.
        /// </summary>
        [TestMethod]
        public void Forfeiters_AreExcludedFromTheAverage()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 600), forfeited: true),
                Seat(Stats(2, 0, dmg: 100)),
                Seat(Stats(3, 0, dmg: 200)),
            };

            var c = PvpContribution.Compute(BattlegroundModes.KothModeKey, seats, Dials);

            Assert.AreEqual(200.0 / 150.0, c[3].Contribution.Value, 1e-9);
            Assert.AreEqual(4.0, c[1].Contribution.Value, 1e-9, "the forfeiter measured against the eligible average");
        }

        [TestMethod]
        public void Undispatched_HasNoC_AndIsNotInTheAverage()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 900), dispatched: false),
                Seat(Stats(2, 0, dmg: 100)),
                Seat(Stats(3, 0, dmg: 300)),
            };

            var c = PvpContribution.Compute(BattlegroundModes.KothModeKey, seats, Dials);

            Assert.IsNull(c[1].Contribution);
            Assert.AreEqual(1.5, c[3].Contribution.Value, 1e-9, "300 against (100 + 300) / 2");
        }

        /// <summary>A battleground's reference is the seat's OWN team: a big hitter on the other team does not lower this team's ratios.</summary>
        [TestMethod]
        public void Battleground_ReferenceIsTheOwnTeam()
        {
            var seats = new[]
            {
                Seat(Stats(1, 0, dmg: 100)),
                Seat(Stats(2, 0, dmg: 100)),
                Seat(Stats(3, 1, dmg: 10000)),
                Seat(Stats(4, 1, dmg: 0)),
            };

            var c = PvpContribution.Compute(BattlegroundModes.KothModeKey, seats, Dials);

            Assert.AreEqual(1.0, c[1].Contribution.Value, 1e-9);
            Assert.AreEqual(2.0, c[3].Contribution.Value, 1e-9);
        }

        /// <summary>Tugak Brawl: the whole field is the reference, over damage, killshots and engaged seconds.</summary>
        [TestMethod]
        public void Tugak_ReferenceIsTheWholeField_OverDamageKillsAndEngaged()
        {
            var seats = Enumerable.Range(1, 4).Select(i => Seat(Stats((uint)i, i - 1, dmg: i == 1 ? 400 : 0, engaged: i == 2 ? 80 : 10))).ToArray();

            var c = PvpContribution.Compute(ArenaMapCatalog.FfaKey, seats, Dials);

            Assert.AreEqual(4.0, c[1].Contribution.Value, 1e-9, "400 against a field average of 100");
            Assert.AreEqual(PvpContribution.DimensionDamage, c[1].Dimension);
            Assert.AreEqual(80.0 / 27.5, c[2].Contribution.Value, 1e-9, "engaged 80 against (10 + 80 + 10 + 10) / 4");
            Assert.AreEqual(PvpContribution.DimensionEngaged, c[2].Dimension);
        }

        [TestMethod]
        public void Tugak_DoesNotReadHealingOrHill()
        {
            var seats = new[] { Seat(Stats(1, 0, healing: 999, hill: 999, dmg: 10)), Seat(Stats(2, 1, dmg: 10)) };

            Assert.AreEqual(1.0, PvpContribution.Compute(ArenaMapCatalog.FfaKey, seats, Dials)[1].Contribution.Value, 1e-9);
        }

        /// <summary>Attack/Defend attackers are measured on their role stats (crystal damage among them) from the modifier's own list.</summary>
        [TestMethod]
        public void AttackDefend_UsesTheRoleStatsOfTheModifier()
        {
            var seats = new[]
            {
                Seat(Stats(1, CrystalWinCondition.AttackerTeam, crystal: 3000)),
                Seat(Stats(2, CrystalWinCondition.AttackerTeam, crystal: 1000)),
            };

            var c = PvpContribution.Compute(BattlegroundModes.AttackDefendModeKey, seats, Dials);

            Assert.AreEqual(1.5, c[1].Contribution.Value, 1e-9);
            Assert.AreEqual("crystal_dmg", c[1].Dimension);

            var noCrystalWeight = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfWeightAdAtkCrystalDmg = 0 });
            Assert.IsNull(PvpContribution.Compute(BattlegroundModes.AttackDefendModeKey, seats, noCrystalWeight)[1].Contribution, "a role stat weighted 0 is not a dimension (same list as the modifier)");
        }

        [TestMethod]
        public void OtherModes_HaveNoC()
        {
            var seats = new[] { Seat(Stats(1, 0, dmg: 100)), Seat(Stats(2, 1, dmg: 50)) };

            Assert.IsNull(PvpContribution.Compute(ArenaMapCatalog.OneVOneKey, seats, Dials)[1].Contribution);
            Assert.AreEqual(0, PvpContribution.Compute(BattlegroundModes.KothModeKey, null, Dials).Count);
        }

        [TestMethod]
        public void NullDials_UseTheShippedDefaults()
        {
            var seats = new[] { Seat(Stats(1, 0, hill: 30)), Seat(Stats(2, 0, hill: 10)) };

            Assert.AreEqual(1.5, PvpContribution.Compute(BattlegroundModes.KothModeKey, seats)[1].Contribution.Value, 1e-9);
        }
    }
}

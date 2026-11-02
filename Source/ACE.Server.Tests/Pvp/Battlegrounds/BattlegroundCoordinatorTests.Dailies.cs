using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// PvP dailies through the battleground coordinator rig: the strict on-hill killshot field (Usurper) and the King of the Hill facts.
    /// Method names start with Dailies_ so they can be run on their own.
    /// </summary>
    public partial class BattlegroundCoordinatorTests
    {
        /// <summary>
        /// Drives: two cross-team kills in a Live King of the Hill match. A death 0.5 m OUTSIDE the zone radius counts for the margin field
        /// (KillshotsNearHill, radius + 6 m) but NOT for the strict one (KillshotsOnHill); a death inside the zone counts for both. Making the
        /// strict test use the margin, or dropping it, changes KillshotsOnHill.
        /// </summary>
        [TestMethod]
        public void Dailies_StrictOnHill_HalfAMetreOutside_IsNearButNotOn()
        {
            var rig = LiveKoth(out var match);
            var west = rig.Team(match, 0);
            var east = rig.Team(match, 1);
            var layout = BattlegroundMapCatalog.Bg016c;
            var radius = (float)rig.Bg.KothZoneRadius;

            Assert.IsTrue(rig.Bg.KothPerfNearMargin >= 1.0, "fixture: the margin is wider than half a metre");

            // KillAt(rig, match, victim, killer, where): east[0] kills west[0] just outside the zone, then west[1] just inside it.
            KillAt(rig, match, west[0], east[0], InMatch(rig, match, layout.ZoneX + radius + 0.5f, layout.ZoneY));
            Assert.AreEqual(1, Perf(match, east[0]).KillshotsNearHill, "0.5 m outside the zone is inside the margin");
            Assert.AreEqual(0, Perf(match, east[0]).KillshotsOnHill, "but it is not ON the hill");

            KillAt(rig, match, west[1], east[0], InMatch(rig, match, layout.ZoneX + radius - 1f, layout.ZoneY));

            var killer = Perf(match, east[0]);
            Assert.AreEqual(2, killer.Killshots, "fixture: both kills counted");
            Assert.AreEqual(2, killer.KillshotsNearHill, "both deaths are within the radius plus the margin");
            Assert.AreEqual(1, killer.KillshotsOnHill, "only the death inside the zone itself is on the hill");
        }

        /// <summary>
        /// Drives: a Live King of the Hill match with hill seconds, crystal damage and an on-hill kill credited, then a team 0 score win. Every
        /// seat gets exactly one facts record carrying ITS ledger counters, mode KOTH, its team and its result; no opponent account outside 1v1.
        /// Extra ticks and the post-match close dispatch nothing more.
        /// </summary>
        [TestMethod]
        public void Dailies_Koth_FactsCarryTheLedger_OncePerSeat()
        {
            var rig = LiveKoth(out var match);
            var west = rig.Team(match, 0);
            var east = rig.Team(match, 1);
            var layout = BattlegroundMapCatalog.Bg016c;

            Credit(match, west[0], hill: 75);
            match.Performance.CreditCrystalDamage(west[1], 1234);
            KillAt(rig, match, east[0], west[0], InMatch(rig, match, layout.ZoneX, layout.ZoneY));

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            rig.Tick();
            rig.Advance(rig.Dials.PostMatchSeconds + 1);
            rig.Tick();

            var facts = rig.Gateway.DailyFacts;
            Assert.AreEqual(4, facts.Count, "one record per seat, once");
            CollectionAssert.AreEquivalent(west.Concat(east).ToArray(), facts.Select(f => f.CharacterId).ToArray());
            Assert.IsTrue(facts.All(f => f.Mode == PvpDailyMode.Koth && f.MatchId == match.MatchId && f.OpponentAccount == null));

            var w0 = facts.Single(f => f.CharacterId == west[0]);
            Assert.AreEqual(75, w0.HillSeconds);
            Assert.AreEqual(1, w0.KillshotsOnHill, "the kill on the point");
            Assert.AreEqual(1, w0.Kills);
            Assert.AreEqual(PvpBloodResult.Win, w0.Result);
            Assert.AreEqual(0, w0.TeamIndex);

            Assert.AreEqual(1234, facts.Single(f => f.CharacterId == west[1]).CrystalDamage);
            Assert.IsTrue(east.All(id => facts.Single(f => f.CharacterId == id).Result == PvpBloodResult.Loss && facts.Single(f => f.CharacterId == id).TeamIndex == 1));
            Assert.IsTrue(facts.All(f => f.PeakRating > 0), "the peak is read from the rating store after this match's own rating was applied");
        }

        /// <summary>
        /// Drives: the contribution floor ON, west seat 1 does nothing, a west win (FloorScenario). The withheld seat gets NO facts while its
        /// three peers do; the control run with the floor off gives all four. Also proves the floor is evaluated once: one decision line per seat.
        /// </summary>
        [TestMethod]
        public void Dailies_Koth_FloorWithheldSeat_GetsNoFacts()
        {
            var on = FloorScenario(floorEnabled: true);
            var off = FloorScenario(floorEnabled: false);
            var idle = on.Rig.Team(on.Match, 0)[1];

            Assert.AreEqual(4, off.Rig.Gateway.DailyFacts.Count, "control: the floor off gives every seat its facts");
            Assert.AreEqual(3, on.Rig.Gateway.DailyFacts.Count);
            Assert.IsFalse(on.Rig.Gateway.DailyFacts.Any(f => f.CharacterId == idle), "the withheld seat gets none");
            Assert.AreEqual(4, on.Lines.Count(l => l.Contains(" conduct_floor: ")), "the floor is decided once, shared by the payout and the facts");
        }

        /// <summary>Drives: a battleground forfeiter, then a team 0 score win. The forfeiter gets no facts; the other three do.</summary>
        [TestMethod]
        public void Dailies_Koth_ForfeiterGetsNoFacts()
        {
            var rig = LiveKoth(out var match);
            var forfeiter = rig.Team(match, 1)[0];

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(forfeiter).Outcome);
            rig.Tick();
            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(3, rig.Gateway.DailyFacts.Count);
            Assert.IsFalse(rig.Gateway.DailyFacts.Any(f => f.CharacterId == forfeiter));
        }

        /// <summary>Drives: same-IP opposing teams (pvp_arena_block_same_ip off), a score win. Nobody gets facts.</summary>
        [TestMethod]
        public void Dailies_Koth_SameIpOpposingTeams_GetNoFacts()
        {
            var rig = LiveKoth(out var match, r => r.Dials = r.Dials with { BlockSameIp = false }, sharedIp: "10.9.9.9");

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(0, rig.Gateway.DailyFacts.Count);
        }
    }
}

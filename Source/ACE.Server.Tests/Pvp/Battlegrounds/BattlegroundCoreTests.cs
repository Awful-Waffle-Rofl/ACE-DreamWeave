using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>Step 2-core: the shared predicates, the BG constants, and that every new parameter defaults to arena behaviour.</summary>
    [TestClass]
    public class BattlegroundCoreTests
    {
        [TestMethod]
        public void IsActive_IsExitReasonNull()
        {
            var p = new PvpParticipant(1, 1500);
            Assert.IsTrue(BattlegroundSeats.IsActive(p));
            p.ExitReason = ParticipantExit.Died;
            Assert.IsFalse(BattlegroundSeats.IsActive(p));
        }

        [TestMethod]
        public void CountsOnZone_IsActiveAndNotRespawning()
        {
            var p = new PvpParticipant(1, 1500);
            Assert.IsTrue(BattlegroundSeats.CountsOnZone(new BattlegroundSeat(p, 0, false)));
            Assert.IsFalse(BattlegroundSeats.CountsOnZone(new BattlegroundSeat(p, 0, true)));
            Assert.IsTrue(BattlegroundSeats.IsActive(new BattlegroundSeat(p, 0, true)));
            p.ExitReason = ParticipantExit.ForfeitCommand;
            Assert.IsFalse(BattlegroundSeats.CountsOnZone(new BattlegroundSeat(p, 0, false)));
        }

        [TestMethod]
        public void Constants_AreTheSpecKeys()
        {
            Assert.AreEqual("bg_koth", BattlegroundModes.KothModeKey);
            Assert.AreEqual("bg", BattlegroundModes.RoomKey);
            Assert.AreEqual("battleground", BattlegroundModes.LadderKey);
        }

        [TestMethod]
        public void ArenaModes_KeepArenaDefaults()
        {
            foreach (var m in PvpModes.All(PvpTunables.Defaults))
            {
                Assert.AreEqual(m.ModeKey, m.RoomKey);
                Assert.IsFalse(m.IsObjective);
                Assert.IsTrue(m.UsesOvertime);
                Assert.IsTrue(m.PaysBlood);
            }
        }

        [TestMethod]
        public void NewTrailingParameters_DefaultToUnchangedBehaviour()
        {
            var match = new PvpMatch(System.Guid.NewGuid(), "1v1", new List<PvpTeam>(), System.DateTime.UtcNow);
            Assert.AreEqual(0, match.TeamKills.Count);

            var b = new PvpPlayerBinding(match, 0, PvpMatchState.Live, true, true, true, true, true);
            Assert.IsNull(b.RespawnPen);
            Assert.IsFalse(b.Respawning);

            var e = new QueueEntrant(System.Guid.NewGuid(), new uint[] { 1 }, new[] { 1500 }, "ip", System.DateTime.UtcNow);
            Assert.IsNull(e.MonarchIds);
            Assert.IsNull(new MatchmakingContext(System.DateTime.UtcNow, 1, 1, 1, 1, 1, 1, 1, 1, true).Bg);
        }

        [TestMethod]
        public void BattlegroundModesAll_IsNotPartOfPvpModesAll()
        {
            foreach (var m in PvpModes.All(PvpTunables.Defaults))
                Assert.AreNotEqual(BattlegroundModes.KothModeKey, m.ModeKey);
        }
    }
}
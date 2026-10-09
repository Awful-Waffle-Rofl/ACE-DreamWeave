using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    [TestClass]
    public class KothTickHandlerTests
    {
        private static readonly DateTime Start = FakeBattlegroundContext.Start;

        // Every dial is explicit and deliberately unlike the shipped default, so no test passes by coincidence.
        private static readonly BattlegroundDials Dials = BattlegroundTunables.Defaults with
        {
            KothTickSeconds = 4,
            KothHoldPoints = 7,
            KothDecayPoints = 3,
            KothMinHolders = 1,
            KothDedupeIp = true,
            KothDrainHealth = 11,
            KothDrainStamina = 12,
            KothDrainMana = 13,
            KothDrainLethal = true,
            KothScoreAnnounceSeconds = 40
        };

        private static readonly KothZone Zone = new KothZone(10, 20, 1, Radius: 5, Height: 2);

        private static KothTickHandler Handler(BattlegroundDials dials = null) => new KothTickHandler(dials ?? Dials, Zone);

        private static DateTime Tick(int n) => Start.AddSeconds(Dials.KothTickSeconds * n);

        private static (FakeBattlegroundContext M, KothTickHandler H) Live(BattlegroundDials dials = null)
        {
            var m = new FakeBattlegroundContext();
            var h = Handler(dials);
            h.OnLive(m);
            return (m, h);
        }

        [TestMethod]
        public void OnLive_SchedulesFirstScoreOneIntervalOut_AndNothingBefore()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1);

            Assert.AreEqual(Start.AddSeconds(Dials.KothTickSeconds), h.NextScoreUtc);

            h.OnTick(m, Tick(1).AddSeconds(-1));

            Assert.AreEqual(0, m.ScoreBoard.Count);
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void NonBattlegroundContext_IsIgnored()
        {
            var h = Handler();
            var plain = new PvpMatch(Guid.NewGuid(), "x", new(), Start);
            plain.GoLive(Start);
            h.OnLive(plain);

            h.OnTick(plain, Start.AddHours(1));

            Assert.AreEqual(0, plain.ScoreBoard.Count);
        }

        [TestMethod]
        public void UncontestedHolder_Scores_OtherTeamDecays()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 20;
            m.ScoreBoard[1] = 10;
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(20 + Dials.KothHoldPoints, m.ScoreBoard[0]);
            Assert.AreEqual(10 - Dials.KothDecayPoints, m.ScoreBoard[1]);
        }

        [TestMethod]
        public void Decay_FloorsAtZero()
        {
            var (m, h) = Live();
            m.ScoreBoard[1] = 1;
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(0, m.ScoreBoard[1]);
        }

        [TestMethod]
        public void Contested_NoScoreChange_ButBothDrained()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 20;
            m.ScoreBoard[1] = 10;
            m.Sample(m.Member(0, 0), 10, 20, 1);
            m.Sample(m.Member(1, 0), 11, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(20, m.ScoreBoard[0]);
            Assert.AreEqual(10, m.ScoreBoard[1]);
            Assert.AreEqual(2, m.Drains.Count);
        }

        [TestMethod]
        public void EmptyZone_NoChange_NoDrain()
        {
            var (m, h) = Live();
            m.ScoreBoard[0] = 20;

            h.OnTick(m, Tick(1));

            Assert.AreEqual(20, m.ScoreBoard[0]);
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void BelowMinHolders_DoesNotHold()
        {
            var (m, h) = Live(Dials with { KothMinHolders = 2 });
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.IsFalse(m.ScoreBoard.TryGetValue(0, out var v) && v > 0);
        }

        [TestMethod]
        public void MinHolders_MetByTwoPlayers_Holds()
        {
            var (m, h) = Live(Dials with { KothMinHolders = 2 });
            m.Sample(m.Member(0, 0), 10, 20, 1);
            m.Sample(m.Member(0, 1), 10, 21, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(Dials.KothHoldPoints, m.ScoreBoard[0]);
        }

        [TestMethod]
        public void RespawningPlayer_NotCounted()
        {
            var (m, h) = Live();
            m.SetRespawning(m.Member(0, 0), true);
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(0, m.ScoreBoard.GetValueOrDefault(0));
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void ExitedSeat_NotCounted()
        {
            var (m, h) = Live();
            m.Member(0, 0).ExitReason = ParticipantExit.ForfeitCommand;
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(0, m.ScoreBoard.GetValueOrDefault(0));
        }

        [TestMethod]
        public void OutsideRadius_NotCounted()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10 + Zone.Radius + 0.5, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(0, m.ScoreBoard.GetValueOrDefault(0));
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void RadiusIsHorizontalDistance_NotPerAxis()
        {
            var (m, h) = Live();
            // 4 + 4 on each axis: inside a per-axis box of 5, outside the circle (distance ~5.66).
            m.Sample(m.Member(0, 0), 14, 24, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void OutsideHeight_NotCounted_AboveAndBelow()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1 + Zone.Height + 0.5);
            m.Sample(m.Member(1, 0), 10, 20, 1 - Zone.Height - 0.5);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void Dead_NotCounted()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1, dead: true);
            h.OnTick(m, Tick(1));
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void Teleporting_NotCounted()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1, teleporting: true);
            h.OnTick(m, Tick(1));
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void NotInInstance_NotCounted()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1, inInstance: false);
            h.OnTick(m, Tick(1));
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void SampleWithNoSeat_NotCounted()
        {
            var (m, h) = Live();
            m.Samples.Add(new BattlegroundZoneSample(999, 0, "x", true, false, false, 10, 20, 1));
            h.OnTick(m, Tick(1));
            Assert.AreEqual(0, m.Drains.Count);
        }

        [TestMethod]
        public void DedupeIp_CountsOncePerTeam()
        {
            var (m, h) = Live(Dials with { KothMinHolders = 2 });
            m.Sample(m.Member(0, 0), 10, 20, 1, ip: "same");
            m.Sample(m.Member(0, 1), 10, 21, 1, ip: "same");

            h.OnTick(m, Tick(1));

            // one counted player < min holders 2: no hold, but dedupe never spares a drain.
            Assert.AreEqual(0, m.ScoreBoard.GetValueOrDefault(0));
            Assert.AreEqual(2, m.Drains.Count);
        }

        [TestMethod]
        public void DedupeIp_SameIpTeammates_CountOnceForTheHold_BothDrained()
        {
            var (m, h) = Live();
            var a = m.Member(0, 0);
            var b = m.Member(0, 1);
            m.Sample(a, 10, 20, 1, ip: "same");
            m.Sample(b, 10, 21, 1, ip: "same");

            h.OnTick(m, Tick(1));

            Assert.AreEqual(Dials.KothHoldPoints, m.ScoreBoard[0]);
            CollectionAssert.AreEquivalent(new[] { a.CharacterId, b.CharacterId }, m.Drains.Select(d => d.Id).ToList());
        }

        [TestMethod]
        public void DedupeIp_SameIpOnOppositeTeams_BothCount()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1, ip: "same");
            m.Sample(m.Member(1, 0), 10, 21, 1, ip: "same");

            h.OnTick(m, Tick(1));

            Assert.AreEqual(2, m.Drains.Count);
            Assert.AreEqual(0, m.ScoreBoard.GetValueOrDefault(0));
        }

        [TestMethod]
        public void DedupeOff_SameIpCountsTwice()
        {
            var (m, h) = Live(Dials with { KothDedupeIp = false, KothMinHolders = 2 });
            m.Sample(m.Member(0, 0), 10, 20, 1, ip: "same");
            m.Sample(m.Member(0, 1), 10, 21, 1, ip: "same");

            h.OnTick(m, Tick(1));

            Assert.AreEqual(Dials.KothHoldPoints, m.ScoreBoard[0]);
            Assert.AreEqual(2, m.Drains.Count);
        }

        [TestMethod]
        public void Drain_OnlyForCountedPlayers_WithDialsAndLethalFlag()
        {
            var (m, h) = Live();
            var inside = m.Member(0, 0);
            var outside = m.Member(0, 1);
            m.Sample(inside, 10, 20, 1);
            m.Sample(outside, 100, 100, 1);

            h.OnTick(m, Tick(1));

            Assert.AreEqual(1, m.Drains.Count);
            Assert.AreEqual((inside.CharacterId, Dials.KothDrainHealth, Dials.KothDrainStamina, Dials.KothDrainMana, true), m.Drains[0]);
        }

        [TestMethod]
        public void Drain_LethalFlagFollowsTheDial()
        {
            var (m, h) = Live(Dials with { KothDrainLethal = false });
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));

            Assert.IsFalse(m.Drains.Single().Lethal);
        }

        [TestMethod]
        public void LongStall_PaysExactlyOneScore_ThenResumesOnTheNormalInterval()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1);

            var late = Start.AddSeconds(Dials.KothTickSeconds * 50);
            h.OnTick(m, late);

            // 50 intervals were missed; one score is paid, and the next is a full interval away.
            Assert.AreEqual(Dials.KothHoldPoints, m.ScoreBoard[0]);
            Assert.AreEqual(late.AddSeconds(Dials.KothTickSeconds), h.NextScoreUtc);

            // Consecutive 250 ms ticks inside that interval pay nothing more.
            for (var ms = 250; ms < Dials.KothTickSeconds * 1000; ms += 250)
                h.OnTick(m, late.AddMilliseconds(ms));

            Assert.AreEqual(Dials.KothHoldPoints, m.ScoreBoard[0]);

            h.OnTick(m, late.AddSeconds(Dials.KothTickSeconds));
            Assert.AreEqual(Dials.KothHoldPoints * 2, m.ScoreBoard[0]);
        }
        [TestMethod]
        public void HoldStateChange_AnnouncesAtOnce_NotWhenUnchanged()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1);

            h.OnTick(m, Tick(1));
            Assert.AreEqual(1, m.Announcements.Count);
            Assert.AreEqual(BattlegroundText.ZoneHeld(0), m.Announcements[0]);

            h.OnTick(m, Tick(2));
            Assert.AreEqual(1, m.Announcements.Count);

            m.Sample(m.Member(1, 0), 10, 21, 1);
            h.OnTick(m, Tick(3));
            Assert.AreEqual(BattlegroundText.ZoneContested(), m.Announcements.Last());

            m.Samples.Clear();
            h.OnTick(m, Tick(4));
            Assert.AreEqual(BattlegroundText.ZoneNeutral(), m.Announcements.Last());
        }

        [TestMethod]
        public void HolderSwitch_AnnouncesNewHolder()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1);
            h.OnTick(m, Tick(1));

            m.Samples.Clear();
            m.Sample(m.Member(1, 0), 10, 20, 1);
            h.OnTick(m, Tick(2));

            Assert.AreEqual(BattlegroundText.ZoneHeld(1), m.Announcements.Last());
        }

        [TestMethod]
        public void ScoreAnnouncement_EveryAnnounceSeconds_WithBothScores()
        {
            var (m, h) = Live();
            m.Sample(m.Member(0, 0), 10, 20, 1);

            // Ticks every 4 s; the announce interval is 40 s. Tick 10 lands exactly on it.
            for (var n = 1; n <= 9; n++)
                h.OnTick(m, Tick(n));

            Assert.IsFalse(m.Announcements.Any(a => a.StartsWith("[Battleground] Score")));

            h.OnTick(m, Tick(10));

            Assert.AreEqual(BattlegroundText.Scores(Dials.KothHoldPoints * 10, 0), m.Announcements.Last());
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The Double Marks windows reach every arena mode: 1v1 (owner request 2026-11-25), 2v2 and Tugak Brawl (owner ruling 2026-11-26); Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"). These drive
    /// the coordinator's real payout path and assert on the grants it hands the gateway (amount and the DoubleMarks flag), so a helper that the
    /// coordinator never calls cannot pass them. 2026-09-26 is PDT (UTC-7): 06:30 local is 13:30Z. Method names start with DoubleMarks_.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private static DateTime InMorningWindow => new DateTime(2026, 9, 26, 13, 30, 0, DateTimeKind.Utc);

        private static DateTime OutsideWindow => new DateTime(2026, 9, 26, 15, 30, 0, DateTimeKind.Utc);

        private static void AssertLiveInside(PvpMatch match, bool inside)
        {
            var live = match.LiveSinceUtc.Value;
            var window = live >= new DateTime(2026, 9, 26, 13, 0, 0, DateTimeKind.Utc) && live < new DateTime(2026, 9, 26, 14, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(inside, window, "fixture: where the match went Live relative to the 06:00-07:00 Pacific window");
        }

        /// <summary>A 1v1 between A and B that goes Live at <paramref name="liveAt"/>; A always kills B.</summary>
        private static (Rig Rig, PvpMatch Match) ResolvedOneVOne(DateTime liveAt, Action<Rig> configure = null)
        {
            var rig = new Rig();
            rig.Clock.Now = liveAt.AddSeconds(-rig.Dials.CountdownSeconds);
            configure?.Invoke(rig);
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");

            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(B, "1v1", false).Joined);
            rig.Tick();
            rig.AcceptAll(A, B);

            var match = rig.DriveAcceptedToLive(A);

            Assert.AreEqual(liveAt, match.LiveSinceUtc, "fixture: Live at the requested instant");

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            return (rig, match);
        }

        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_InsideTheWindow_WinPays4_LossPays2_AndFlagsTheGrants()
        {
            var (rig, match) = ResolvedOneVOne(InMorningWindow);

            AssertLiveInside(match, true);
            Assert.AreEqual((4, 1), rig.Gateway.Blood(A), "the 1v1 win is 2 x 2");
            Assert.AreEqual((2, 1), rig.Gateway.Blood(B), "the 1v1 loss is 1 x 2");
            Assert.AreEqual(2, rig.Gateway.BloodGrants.Count, "one grant per seat: the bonus never adds a grant, so the daily cap still counts one per paid match");
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.DoubleMarks), "the pickup line reads (Double Marks)");
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.Ledger == PvpBloodLedgerKind.Arena), "still the arena ledger and cap");
        }

        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_OutsideTheWindow_IsUnchanged()
        {
            var (rig, match) = ResolvedOneVOne(OutsideWindow);

            AssertLiveInside(match, false);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B));
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks));
        }

        /// <summary>The window is read at LIVE time: Live at 06:58 and resolved after 07:00 still pays double.</summary>
        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_IsDecidedByLiveTime_NotResolveTime()
        {
            var live = new DateTime(2026, 9, 26, 13, 58, 0, DateTimeKind.Utc);
            var rig = new Rig();
            rig.Clock.Now = live.AddSeconds(-rig.Dials.CountdownSeconds);
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);

            Assert.AreEqual(live, match.LiveSinceUtc);
            rig.Clock.Advance(8 * 60);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual((4, 1), rig.Gateway.Blood(A));
        }

        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_MasterSwitchOff_IsUnchanged()
        {
            var (rig, match) = ResolvedOneVOne(InMorningWindow, r => r.BgSource = () => BattlegroundTunables.Defaults with { BonusEnabled = false });

            AssertLiveInside(match, true);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B));
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks));
        }

        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_UsesTheBonusMultiplierDial_AndSanitizesIt()
        {
            var (rig, _) = ResolvedOneVOne(InMorningWindow, r => r.BgSource = () => BattlegroundTunables.Defaults with { BonusMultiplier = 3.0 });

            Assert.AreEqual((6, 1), rig.Gateway.Blood(A), "2 x 3");
            Assert.AreEqual((3, 1), rig.Gateway.Blood(B), "1 x 3");

            var (low, _) = ResolvedOneVOne(InMorningWindow, r => r.BgSource = () => BattlegroundTunables.Defaults with { BonusMultiplier = 0.5 });

            Assert.AreEqual((2, 1), low.Gateway.Blood(A), "below 1 reads as 1.0: the bonus never reduces pay");
            Assert.IsTrue(low.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks), "and is not announced as Double Marks");
        }

        /// <summary>A forfeiter pays 0 inside a window, and the winner's amount is still doubled.</summary>
        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_ForfeiterStaysZero()
        {
            var rig = new Rig();
            rig.Clock.Now = InMorningWindow.AddSeconds(-rig.Dials.CountdownSeconds);
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);

            rig.Logout(B, match.MatchId);
            rig.Tick();

            Assert.AreEqual((0, 0), rig.Gateway.Blood(B), "a forfeiter is paid nothing, doubled or not");
            Assert.AreEqual((4, 1), rig.Gateway.Blood(A));
        }

        /// <summary>The third match between the same two accounts is an unpaid rematch: it pays 0 and makes no grant even inside a window.</summary>
        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_UnpaidRematchStaysZero()
        {
            var rig = new Rig();
            rig.Clock.Now = InMorningWindow.AddSeconds(-rig.Dials.CountdownSeconds);
            rig.Build();

            var r1 = PlayRound(rig, 1);
            var r2 = PlayRound(rig, 2);

            Assert.AreEqual((4, 1), rig.Gateway.Blood(r1.One), "control: a paid round inside the window is doubled");
            Assert.AreEqual((4, 1), rig.Gateway.Blood(r2.One), "control: round 2 is still within the pair cap");
            AssertLiveInside(r2.Match, true);

            var grantsBefore = rig.Gateway.BloodGrants.Count;
            var r3 = PlayRound(rig, 3);

            AssertLiveInside(r3.Match, true);
            Assert.AreEqual(grantsBefore, rig.Gateway.BloodGrants.Count, "an unpaid rematch makes no grant call");
            Assert.AreEqual((0, 0), rig.Gateway.Blood(r3.One));
            Assert.AreEqual((0, 0), rig.Gateway.Blood(r3.Two));
        }

        /// <summary>A 2v2 (A + B duo vs C + D) that goes Live at <paramref name="liveAt"/>; A's side wins by killing C and D.</summary>
        private (Rig Rig, PvpMatch Match) ResolvedTwoVTwo(DateTime liveAt, Action<Rig> configure = null)
        {
            var rig = new Rig();
            rig.Clock.Now = liveAt.AddSeconds(-rig.Dials.CountdownSeconds - rig.Dials.DuoVsSoloAfterSeconds);
            configure?.Invoke(rig);
            rig.Build();
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");

            rig.Coordinator.Join(A, "2v2", true);
            rig.Coordinator.Join(C, "2v2", false);
            rig.Coordinator.Join(D, "2v2", false);
            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(C, match.MatchId, killer: A);
            rig.Death(D, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            return (rig, match);
        }

        /// <summary>A five-seat Tugak Brawl that goes Live at <paramref name="liveAt"/>; A outlasts everyone.</summary>
        private (Rig Rig, PvpMatch Match, uint[] Ids) ResolvedTugak(DateTime liveAt, Action<Rig> configure = null)
        {
            var rig = FfaRig(5, out var ids, r =>
            {
                r.Dials = r.Dials with { FfaTargetPlayers = 5, OvertimeEnabled = false };
                r.Clock.Now = liveAt.AddSeconds(-r.Dials.CountdownSeconds);
                configure?.Invoke(r);
            });

            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);

            rig.Tick();
            rig.AcceptAll(ids);
            var match = rig.DriveAcceptedToLive(A);

            foreach (var victim in ids.Skip(1))
                rig.Death(victim, match.MatchId, killer: A);

            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            return (rig, match, ids.ToArray());
        }

        [TestMethod]
        public void DoubleMarks_ArenaTwoVTwo_InsideTheWindow_WinPays4_LossPays2_AndFlagsTheGrants()
        {
            var (rig, match) = ResolvedTwoVTwo(InMorningWindow);

            AssertLiveInside(match, true);
            Assert.AreEqual((4, 1), rig.Gateway.Blood(A), "2v2 win is 2 x 2");
            Assert.AreEqual((2, 1), rig.Gateway.Blood(C), "2v2 loss is 1 x 2");
            Assert.AreEqual((4, 1), rig.Gateway.Blood(B), "the duo partner shares the win: 2 x 2");
            Assert.AreEqual((2, 1), rig.Gateway.Blood(D), "the other loser: 1 x 2");
            Assert.IsTrue(rig.Gateway.BloodGrants.Count > 0 && rig.Gateway.BloodGrants.All(g => g.Grant.DoubleMarks && g.Grant.Ledger == PvpBloodLedgerKind.Arena));
        }

        [TestMethod]
        public void DoubleMarks_ArenaTwoVTwo_OutsideTheWindow_IsUnchanged()
        {
            var (rig, match) = ResolvedTwoVTwo(OutsideWindow);

            AssertLiveInside(match, false);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(C));
            Assert.AreEqual((2, 1), rig.Gateway.Blood(B));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(D));
            Assert.IsTrue(rig.Gateway.BloodGrants.Count > 0 && rig.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks));
        }

        [TestMethod]
        public void DoubleMarks_ArenaTwoVTwo_MasterSwitchOff_IsUnchanged()
        {
            var (rig, match) = ResolvedTwoVTwo(InMorningWindow, r => r.BgSource = () => BattlegroundTunables.Defaults with { BonusEnabled = false });

            AssertLiveInside(match, true);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(C));
            Assert.AreEqual((2, 1), rig.Gateway.Blood(B));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(D));
            Assert.IsTrue(rig.Gateway.BloodGrants.Count > 0 && rig.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks));
        }

        [TestMethod]
        public void DoubleMarks_ArenaTwoVTwo_ZeroBaseAmountStaysZero()
        {
            var (rig, _) = ResolvedTwoVTwo(InMorningWindow, r => r.Dials = r.Dials with { BloodWin = 0 });

            Assert.AreEqual((0, 0), rig.Gateway.Blood(A), "a 0 base amount stays 0 under the bonus");
            Assert.AreEqual((1 * 2, 1), rig.Gateway.Blood(C), "the nonzero loss still doubles");
        }

        [TestMethod]
        public void DoubleMarks_ArenaTugak_InsideTheWindow_WinnerPays4_AndFlagsTheGrants()
        {
            var (rig, match, ids) = ResolvedTugak(InMorningWindow);

            AssertLiveInside(match, true);
            Assert.AreEqual((4, 1), rig.Gateway.Blood(A), "the Tugak winner is 2 x 2");
            Assert.AreEqual((2, 1), rig.Gateway.Blood(ids[1]), "the first seat eliminated (placed last) pays a loss: 1 x 2");
            Assert.IsTrue(rig.Gateway.BloodGrants.Count > 0 && rig.Gateway.BloodGrants.All(g => g.Grant.DoubleMarks && g.Grant.Ledger == PvpBloodLedgerKind.Arena));
        }

        [TestMethod]
        public void DoubleMarks_ArenaTugak_OutsideTheWindow_IsUnchanged()
        {
            var (rig, match, _) = ResolvedTugak(OutsideWindow);

            AssertLiveInside(match, false);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.IsTrue(rig.Gateway.BloodGrants.Count > 0 && rig.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks && g.Amount <= 2));
        }

        [TestMethod]
        public void DoubleMarks_ArenaTugak_MasterSwitchOff_IsUnchanged()
        {
            var (rig, match, _) = ResolvedTugak(InMorningWindow, r => r.BgSource = () => BattlegroundTunables.Defaults with { BonusEnabled = false });

            AssertLiveInside(match, true);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.IsTrue(rig.Gateway.BloodGrants.Count > 0 && rig.Gateway.BloodGrants.All(g => !g.Grant.DoubleMarks && g.Amount <= 2));
        }

        // ---- the Crier gate: the arena-only rig has no battleground room, so only the arena mode terms can open it ----

        /// <summary>Ticks an arena-only rig (no BG room) across the 06:00 Pacific window opening; returns every Double Marks line sent.</summary>
        private static List<string> DoubleMarksCrierLines(Func<PvpArenaDials, PvpArenaDials> dials)
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Announcer = crier };
            rig.Dials = dials(rig.Dials with { CrierEnabled = false });
            rig.Clock.Now = new DateTime(2026, 9, 26, 12, 58, 0, DateTimeKind.Utc);
            rig.Build();

            for (var i = 0; i < 6; i++)
            {
                rig.Tick();
                rig.Clock.Advance(30);
            }

            return crier.Sent.Select(x => x.Line).Where(l => l.StartsWith("Double Marks")).ToList();
        }

        [TestMethod]
        public void DoubleMarks_Crier_TwoVTwoOnlyEnabled_AnnouncesTheOpen()
        {
            var lines = DoubleMarksCrierLines(d => d with { OneVOneEnabled = false, TwoVTwoEnabled = true, FfaEnabled = false });

            Assert.AreEqual(1, lines.Count(l => l.StartsWith("Double Marks is now live")), string.Join(" | ", lines));
        }

        [TestMethod]
        public void DoubleMarks_Crier_TugakOnlyEnabled_AnnouncesTheOpen()
        {
            var lines = DoubleMarksCrierLines(d => d with { OneVOneEnabled = false, TwoVTwoEnabled = false, FfaEnabled = true });

            Assert.AreEqual(1, lines.Count(l => l.StartsWith("Double Marks is now live")), string.Join(" | ", lines));
        }

        [TestMethod]
        public void DoubleMarks_Crier_EveryArenaModeOff_AnnouncesNothing()
        {
            var lines = DoubleMarksCrierLines(d => d with { OneVOneEnabled = false, TwoVTwoEnabled = false, FfaEnabled = false });

            Assert.AreEqual(0, lines.Count, string.Join(" | ", lines));
        }

        // ---- the pure amounts (BloodFor), for the rounding and zero rules the coordinator relies on ----

        [TestMethod]
        public void DoubleMarks_BloodFor_RoundsHalvesAwayFromZero_AndKeepsZerosZero()
        {
            var dials = PvpTunables.Defaults with { BloodEnabled = true, BloodWin = 2, BloodLoss = 1, BloodDraw = 1 };

            Assert.AreEqual(4, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials, bonusMultiplier: 2.0));
            Assert.AreEqual(2, PvpArenaRewards.BloodFor(PvpBloodResult.Loss, false, true, false, dials, bonusMultiplier: 2.0));
            Assert.AreEqual(2, PvpArenaRewards.BloodFor(PvpBloodResult.Loss, false, true, false, dials, bonusMultiplier: 1.5), "1.5 rounds away from zero");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, true, true, false, dials, bonusMultiplier: 2.0), "forfeit");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, false, false, dials, bonusMultiplier: 2.0), "never Live");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, true, dials, bonusMultiplier: 2.0), "same IP");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials, withheld: true, bonusMultiplier: 2.0), "unpaid rematch");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials with { BloodWin = 0 }, bonusMultiplier: 2.0));
            Assert.AreEqual(2, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials, bonusMultiplier: double.NaN), "NaN reads as 1.0");
            Assert.AreEqual(int.MaxValue, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials, bonusMultiplier: 1e12), "saturates");
        }

        [TestMethod]
        public void DoubleMarks_ReceivedLine_PicksTheLedgerAndTheSuffix()
        {
            Assert.AreEqual(PvpArenaText.BloodReceived, PvpArenaText.ReceivedLine(false, false));
            Assert.AreEqual(PvpArenaText.BloodReceivedDouble, PvpArenaText.ReceivedLine(false, true));
            Assert.AreEqual(PvpArenaText.BgMarksReceived, PvpArenaText.ReceivedLine(true, false));
            Assert.AreEqual(PvpArenaText.BgMarksReceivedDouble, PvpArenaText.ReceivedLine(true, true));
            Assert.IsTrue(PvpArenaText.ReceivedLine(false, true).Contains("[Arena]") && PvpArenaText.ReceivedLine(false, true).EndsWith("(Double Marks)."));
            Assert.IsTrue(PvpArenaText.ReceivedLine(true, true).Contains("[Battleground]") && PvpArenaText.ReceivedLine(true, true).EndsWith("(Double Marks)."));
        }

        [TestMethod]
        public void DoubleMarks_ArenaText_ReceivedLineCarriesTheSuffixBeforeTheFullStop()
        {
            Assert.AreEqual("[Arena] You receive 4 Marks of the Hopeslayer (Double Marks).", PvpArenaText.Fill(PvpArenaText.BloodReceivedDouble, ("amount", 4), ("marks", "Marks of the Hopeslayer")));
        }
    }
}

using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Double Marks windows through the battleground coordinator rig (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): the payout
    /// is decided by when the match went LIVE, and the Crier lines are sent whether or not pvp_arena_crier_enabled is on. The rig's
    /// dials are the shipped ones (marks 2 / 1 / 1, scale 1.0, bonus 2.0, windows 06:00-07:00 and 18:00-19:00 Pacific). Method names
    /// start with DoubleMarks_ so they can be run on their own. 2026-10-05 is PDT (UTC-7): 06:58 local is 13:58Z.
    /// </summary>
    public partial class BattlegroundCoordinatorTests
    {
        private static DateTime PacificLocal(int h, int m, int s = 0) => new DateTime(2026, 10, 5, h, m, s, DateTimeKind.Utc).AddHours(7);

        /// <summary>A rig whose match goes Live at exactly <paramref name="liveAt"/> (the clock starts the countdown earlier).</summary>
        private static Rig LiveKothAt(DateTime liveAt, out PvpMatch match, Action<Rig> configure = null)
        {
            DateTime? start = null;

            var rig = LiveKoth(out match, r =>
            {
                r.Now = liveAt.AddSeconds(-r.Dials.CountdownSeconds);
                start = r.Now;
                configure?.Invoke(r);
            });

            Assert.IsNotNull(start);
            Assert.AreEqual(liveAt, match.LiveSinceUtc, "fixture: the match went Live at the requested instant");
            return rig;
        }

        private static void ResolveWithTeamZeroWin(Rig rig, PvpMatch match)
        {
            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
        }

        [TestMethod]
        public void DoubleMarks_LiveAt0658_WinPays4_LossPays2()
        {
            var rig = LiveKothAt(PacificLocal(6, 58), out var match);

            ResolveWithTeamZeroWin(rig, match);

            Assert.AreEqual(4, rig.Gateway.BloodGrants.Count);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(4, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "winner: 2 x 2");

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual(2, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "loser: 1 x 2");

            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.DoubleMarks), "every grant carries the Double Marks flag for the pickup line");
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.Ledger == PvpBloodLedgerKind.Battleground));
        }

        [TestMethod]
        public void DoubleMarks_LiveAt0701_WinPays2_LossPays1_NoFlag()
        {
            var rig = LiveKothAt(PacificLocal(7, 1), out var match);

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(2, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount);

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual(1, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount);

            Assert.IsFalse(rig.Gateway.BloodGrants.Any(g => g.Grant.DoubleMarks));
        }

        /// <summary>The window is read at LIVE time: a match that goes Live at 06:58 and resolves at 07:06 still pays double.</summary>
        [TestMethod]
        public void DoubleMarks_LiveAt0658_ResolvedAt0706_StillPays4()
        {
            var rig = LiveKothAt(PacificLocal(6, 58), out var match);

            rig.Advance(8 * 60);
            Assert.AreEqual(PacificLocal(7, 6), rig.Now, "fixture: the match resolves at 07:06 local");

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(4, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount);
        }

        /// <summary>The mirror: Live at 05:58 (before the window), resolved at 06:06 (inside it), pays the ordinary amount.</summary>
        [TestMethod]
        public void DoubleMarks_LiveAt0558_ResolvedInsideTheWindow_Pays2()
        {
            var rig = LiveKothAt(PacificLocal(5, 58), out var match);

            rig.Advance(8 * 60);

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(2, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount);

            Assert.IsFalse(rig.Gateway.BloodGrants.Any(g => g.Grant.DoubleMarks));
        }

        [TestMethod]
        public void DoubleMarks_Draw_IsDoubled()
        {
            var rig = LiveKothAt(PacificLocal(18, 30), out var match);

            rig.Advance(rig.Bg.TimeLimitSecondsKoth);
            rig.Tick();

            Assert.AreEqual("draw", rig.Sink.Saves.Single().Match.Outcome);
            Assert.AreEqual(4, rig.Gateway.BloodGrants.Count);
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Amount == 2), "draw 1 x 2");
        }

        [TestMethod]
        public void DoubleMarks_ScaleAndBonusMultiply_ThenRound()
        {
            var rig = LiveKothAt(PacificLocal(6, 30), out var match, r => r.Bg = r.Bg with { MarksScale = 1.5 });

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(6, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "round(2 x 1.5 x 2.0)");

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual(3, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "round(1 x 1.5 x 2.0)");
        }

        [TestMethod]
        public void DoubleMarks_ABonusMultiplierBelowOne_NeverReducesPay_AndRaisesNoFlag()
        {
            var rig = LiveKothAt(PacificLocal(6, 30), out var match, r => r.Bg = r.Bg with { BonusMultiplier = 0.25 });

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(2, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount);

            Assert.IsFalse(rig.Gateway.BloodGrants.Any(g => g.Grant.DoubleMarks), "a multiplier that sanitizes to 1.0 is not a Double Marks payout");
        }

        [TestMethod]
        public void DoubleMarks_Disabled_PaysTheOrdinaryAmount_InsideTheWindow()
        {
            var rig = LiveKothAt(PacificLocal(6, 30), out var match, r => r.Bg = r.Bg with { BonusEnabled = false });

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(2, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount);

            Assert.IsFalse(rig.Gateway.BloodGrants.Any(g => g.Grant.DoubleMarks));
        }

        /// <summary>The bonus does not reach around the other payout rules: a forfeiter is still paid nothing, and the cap dial still travels with the grant.</summary>
        [TestMethod]
        public void DoubleMarks_ForfeiterStillPaidNothing_AndTheCapStillTravelsWithTheGrant()
        {
            var rig = LiveKothAt(PacificLocal(6, 30), out var match, r => r.Bg = r.Bg with { MarksDailyCap = 5 });
            var forfeiter = rig.Team(match, 1)[0];

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(forfeiter).Outcome);
            rig.Tick();
            ResolveWithTeamZeroWin(rig, match);

            Assert.IsFalse(rig.Gateway.BloodGrants.Any(g => g.Id == forfeiter));
            Assert.AreEqual(3, rig.Gateway.BloodGrants.Count);
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.DailyCap == 5), "the bonus marks count against the same cap");
        }

        /// <summary>The window is read in the configured zone: a UTC window at the same instants pays the ordinary amount outside it.</summary>
        [TestMethod]
        public void DoubleMarks_UsesTheConfiguredWindowsAndZone()
        {
            var rig = LiveKothAt(PacificLocal(6, 30), out var match, r => r.Bg = r.Bg with { BonusTimezone = "UTC", BonusWindows = "20:00-21:00" });

            ResolveWithTeamZeroWin(rig, match);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(2, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "13:30Z is outside 20:00-21:00 UTC");
        }

        // ---------------- the Crier lines through the coordinator ----------------

        /// <summary>
        /// The heads-up, the open and the end are sent through the announcer with pvp_arena_crier_enabled OFF (the rig's default), once each.
        /// </summary>
        [TestMethod]
        public void DoubleMarks_Crier_SendsTheThreeLines_EvenWithTheArenaCrierOff()
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier, Now = PacificLocal(5, 40) }.Build();

            Assert.IsFalse(rig.Dials.CrierEnabled, "fixture: the arena Crier is off");

            for (var i = 0; i < 90 * 2; i++)
            {
                rig.Tick();
                rig.Advance(30);
            }

            CollectionAssert.AreEqual(
                new[] { BgBonusCrier.WarnLine(15), BgBonusCrier.OpenLine("1h 0m"), BgBonusCrier.EndLine("11h 0m") },
                crier.Lines.Where(l => l.StartsWith("Battleground Double Marks")).ToArray());
        }

        /// <summary>
        /// The arena master switch (pvp_arena_enabled) sits above the Double Marks Crier: with it off, a window in progress gets no line;
        /// switching it back on mid-window gives exactly one Open, and nothing after.
        /// </summary>
        [TestMethod]
        public void DoubleMarks_Crier_HonorsTheArenaMasterSwitch()
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier, Now = PacificLocal(6, 10) };
            rig.Dials = rig.Dials with { Enabled = false };
            rig.Build();

            for (var i = 0; i < 10; i++)
            {
                rig.Tick();
                rig.Advance(30);
            }

            Assert.AreEqual(0, crier.Lines.Count(l => l.StartsWith("Battleground Double Marks")), "the arena is off mid-window: no announcement");

            rig.Dials = rig.Dials with { Enabled = true };

            for (var i = 0; i < 10; i++)
            {
                rig.Tick();
                rig.Advance(30);
            }

            var lines = crier.Lines.Where(l => l.StartsWith("Battleground Double Marks")).ToList();

            Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
            StringAssert.Contains(lines[0], "is now live for the next");
        }

        [TestMethod]
        public void DoubleMarks_Crier_IsSilentWhenTheSwitchIsOff()
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier, Now = PacificLocal(5, 40), Bg = BattlegroundTunables.Defaults with { BonusEnabled = false } }.Build();

            for (var i = 0; i < 90 * 2; i++)
            {
                rig.Tick();
                rig.Advance(30);
            }

            Assert.AreEqual(0, crier.Lines.Count(l => l.StartsWith("Battleground Double Marks")));
        }
    }
}

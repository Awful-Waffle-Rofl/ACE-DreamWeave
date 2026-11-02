using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The Arena Crier's per-match result line on /lfg (Docs/Pvp/DESIGN.md "Arena Crier") and the shipped arena Marks amounts (2 win, 1 loss, 1 draw,
    /// 0 for a forfeiter). The coordinator is driven through its seams; no PropertyManager key is read here (every dial is injected).
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private sealed class RecordingCrier : IPvpArenaCrierAnnouncer
        {
            public readonly List<(string Line, string Sender)> Sent = new();

            public Exception Throw;

            public void Announce(IReadOnlyList<string> lines, string senderName)
            {
                if (Throw != null)
                    throw Throw;

                foreach (var line in lines)
                    Sent.Add((line, senderName));
            }
        }

        /// <summary>A 1v1 on the given dials (periodic Crier off so only result lines can appear), driven to Live.</summary>
        private static Rig CrierOneVOneLive(RecordingCrier crier, out PvpMatch match, Func<PvpArenaDials, PvpArenaDials> dials = null)
        {
            return OneVOneLive(out match, r =>
            {
                r.Announcer = crier;
                r.Dials = (dials ?? (d => d))(r.Dials with { CrierEnabled = false });
            });
        }

        /// <summary>Drives: a 1v1 to Live, Alpha kills Bravo, Tick. Exactly one line goes to /lfg, from the Crier, naming characters only.</summary>
        [TestMethod]
        public void CrierResult_OneVOneWin_SendsExactlyOneLfgLine()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(1, crier.Sent.Count);
            Assert.AreEqual("1v1: Alpha defeated Bravo.", crier.Sent[0].Line);
            Assert.AreEqual(rig.Dials.CrierSenderName, crier.Sent[0].Sender);
        }

        /// <summary>Drives: a 1v1 timeout draw (overtime off). One line, the two names as bare sides.</summary>
        [TestMethod]
        public void CrierResult_OneVOneDraw_SendsTheDrawLine()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match, d => d with { OvertimeEnabled = false });

            rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1);
            rig.Tick();

            Assert.AreEqual(1, crier.Sent.Count);
            Assert.AreEqual("1v1 ended in a draw: Alpha vs Bravo.", crier.Sent[0].Line);
        }

        /// <summary>Drives: a 1v1 to Live, Bravo leaves the instance (forfeit). The forfeiter is on the losing side, so the winner still "defeated" them.</summary>
        [TestMethod]
        public void CrierResult_Forfeit_ForfeiterIsOnTheLosingSide()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(1, crier.Sent.Count);
            Assert.AreEqual("1v1: Alpha defeated Bravo.", crier.Sent[0].Line);
        }

        /// <summary>The toggle off: the match resolves and pays as usual, and nothing goes to /lfg.</summary>
        [TestMethod]
        public void CrierResult_ToggleOff_SendsNone()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match, d => d with { CrierResultsEnabled = false });

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(0, crier.Sent.Count);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
        }

        /// <summary>The arena master switch (pvp_arena_enabled) off at resolve time: no result line.</summary>
        [TestMethod]
        public void CrierResult_ArenaDisabled_SendsNone()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Dials = rig.Dials with { Enabled = false };
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State, "fixture: the match still resolved with the switch off");
            Assert.AreEqual(0, crier.Sent.Count);
        }

        /// <summary>pvp_arena_crier_enabled (the periodic queue lines) is off by the fixture; the result line is NOT behind it.</summary>
        [TestMethod]
        public void CrierResult_CrierEnabledFalse_StillSends()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);
            Assert.IsFalse(rig.Dials.CrierEnabled, "control: the periodic Crier is off");

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(1, crier.Sent.Count);
        }

        /// <summary>An admin cancel never reaches Resolve, so the Crier stays silent.</summary>
        [TestMethod]
        public void CrierResult_AdminCancel_SendsNone()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(match.MatchId).Outcome);
            rig.Tick();

            Assert.AreEqual(0, crier.Sent.Count);
        }

        /// <summary>A throwing announcer is caught and logged: the save still happens and the payout is still made.</summary>
        [TestMethod]
        public void CrierResult_AnnouncerThrows_SaveAndPayAreUnaffected()
        {
            var crier = new RecordingCrier { Throw = new InvalidOperationException("simulated crier failure") };
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(1, rig.Sink.Saves.Count);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B));
        }

        // ---- team, placement, forfeit and Tugak cases ----

        /// <summary>A 2v2 (the duo Alpha + Bravo, Red, against the solos Charlie + Delta, Blue) driven to Live with the periodic Crier off.</summary>
        private static Rig CrierTwoVTwoLive(RecordingCrier crier, out PvpMatch match)
        {
            var rig = new Rig { Announcer = crier };
            rig.Dials = rig.Dials with { CrierEnabled = false };
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
            match = rig.DriveAcceptedToLive(A);
            return rig;
        }

        /// <summary>A Tugak Brawl of <paramref name="players"/> (5 to 7) driven to Live; overtime off so the time limit ends it.</summary>
        private static Rig CrierFfaLive(RecordingCrier crier, int players, out PvpMatch match)
        {
            var rig = FfaRig(players, out var ids, r =>
            {
                r.Announcer = crier;
                r.Dials = r.Dials with { CrierEnabled = false, FfaTargetPlayers = players, OvertimeEnabled = false };
            });

            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);

            rig.Tick();
            rig.AcceptAll(ids);
            match = rig.DriveAcceptedToLive(A);
            return rig;
        }

        /// <summary>Drives: a 2v2 to Live, both solos die. A normal team win: both sides labelled by their colour, members listed.</summary>
        [TestMethod]
        public void CrierResult_TwoVTwoWin_NamesBothTeams()
        {
            var crier = new RecordingCrier();
            var rig = CrierTwoVTwoLive(crier, out var match);

            rig.Death(C, match.MatchId, killer: A);
            rig.Death(D, match.MatchId, killer: B);
            rig.Tick();

            Assert.AreEqual(1, crier.Sent.Count);
            Assert.AreEqual("2v2: Red team (Alpha, Bravo) defeated Blue team (Charlie, Delta).", crier.Sent[0].Line);
        }

        /// <summary>Drives: a 2v2 to Live, a WINNING-team member logs out, both solos die. The forfeiter is on neither side of the line (review F1).</summary>
        [TestMethod]
        public void CrierResult_TwoVTwo_WinningTeamForfeiter_AppearsNowhere()
        {
            var crier = new RecordingCrier();
            var rig = CrierTwoVTwoLive(crier, out var match);

            rig.Logout(B, match.MatchId);
            rig.Tick();
            rig.Death(C, match.MatchId, killer: A);
            rig.Death(D, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual("loss", rig.Part(B).Result, "control: the forfeiter is scored as a loss");
            Assert.AreEqual(1, crier.Sent.Count);
            Assert.AreEqual("2v2: Red team (Alpha) defeated Blue team (Charlie, Delta).", crier.Sent[0].Line);
            Assert.IsFalse(crier.Sent[0].Line.Contains("Bravo"));
        }

        /// <summary>Drives: a 2v2 to Live, every fighter dies in the same tick. A double knockout converted to an unrated draw: the two-team draw line.</summary>
        [TestMethod]
        public void CrierResult_TwoVTwoDoubleKnockout_IsTheDrawLine()
        {
            var crier = new RecordingCrier();
            var rig = CrierTwoVTwoLive(crier, out var match);

            foreach (var id in new[] { A, B, C, D })
                rig.Death(id, match.MatchId, killer: id == A ? C : A);

            rig.Tick();

            Assert.AreEqual("draw", rig.Part(A).Result, "control: the conversion to a draw happened");
            Assert.AreEqual(1, crier.Sent.Count);
            Assert.AreEqual("2v2 ended in a draw: Red team (Alpha, Bravo) vs Blue team (Charlie, Delta).", crier.Sent[0].Line);
        }

        /// <summary>Drives: a 1v1 to Live, both Death intents in one tick. The same conversion in 1v1: bare names.</summary>
        [TestMethod]
        public void CrierResult_OneVOneDoubleKnockout_IsTheDrawLine()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Death(A, match.MatchId, killer: B);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual("draw", rig.Part(A).Result);
            Assert.AreEqual("1v1 ended in a draw: Alpha vs Bravo.", crier.Sent.Single().Line);
        }

        /// <summary>
        /// Drives: a 1v1 to Live, Alpha dies and Bravo logs out in the same tick. No team is left standing but the placements differ, so the
        /// better-placed side wins (the placement-adjust path in Resolve); the line follows the adjusted outcome, as the saved result does.
        /// </summary>
        [TestMethod]
        public void CrierResult_PlacementAdjust_FollowsTheAdjustedWinner()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Death(A, match.MatchId, killer: B);
            rig.Logout(B, match.MatchId);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            var winner = rig.Part(A).Result == "win" ? "Alpha" : "Bravo";
            var loser = winner == "Alpha" ? "Bravo" : "Alpha";
            Assert.AreEqual("win", rig.Part(winner == "Alpha" ? A : B).Result, "control: the saved result has a winner");
            Assert.AreEqual($"1v1: {winner} defeated {loser}.", crier.Sent.Single().Line);
        }

        /// <summary>Drives: a 1v1 to Live, both players log out in the same tick (AllForfeited). Nobody is named a winner.</summary>
        [TestMethod]
        public void CrierResult_AllForfeited_EndedWithNoWinner()
        {
            var crier = new RecordingCrier();
            var rig = CrierOneVOneLive(crier, out var match);

            rig.Logout(A, match.MatchId);
            rig.Logout(B, match.MatchId);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("1v1 ended with no winner.", crier.Sent.Single().Line);
        }

        /// <summary>Drives: 6 Tugak fighters, four die, the time limit ends it. Two survivors share first: "A and B won against 4 other fighters."</summary>
        [TestMethod]
        public void CrierResult_Ffa_TiedFirst_JoinsWinnersAndCountsTheRest()
        {
            var crier = new RecordingCrier();
            var rig = CrierFfaLive(crier, 6, out var match);

            foreach (var id in new[] { C, D, E, F })
                rig.Death(id, match.MatchId, killer: A);

            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "fixture: two survivors do not end it");
            Assert.AreEqual(0, crier.Sent.Count);

            rig.Clock.Advance(rig.Dials.TimeLimitSecondsFfa);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("Tugak Brawl: Alpha and Bravo won against 4 other fighters.", crier.Sent.Single().Line);
        }

        /// <summary>Drives: 5 Tugak fighters, one logs out, two die, the time limit ends it. The forfeiter is counted among the other fighters.</summary>
        [TestMethod]
        public void CrierResult_Ffa_ForfeiterIsCountedAmongTheOthers()
        {
            var crier = new RecordingCrier();
            var rig = CrierFfaLive(crier, 5, out var match);

            rig.Logout(E, match.MatchId);
            rig.Tick();
            rig.Death(C, match.MatchId, killer: A);
            rig.Death(D, match.MatchId, killer: A);
            rig.Tick();

            rig.Clock.Advance(rig.Dials.TimeLimitSecondsFfa);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("Tugak Brawl: Alpha and Bravo won against 3 other fighters.", crier.Sent.Single().Line);
        }

        /// <summary>Drives: 1v1 to Countdown, Bravo walks out (resolved, but it never went Live). Nothing is posted publicly (review F3).</summary>
        [TestMethod]
        public void CrierResult_WalkoutBeforeLive_SendsNone()
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Announcer = crier };
            rig.Dials = rig.Dials with { CrierEnabled = false };
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("win", rig.Part(A).Result, "control: the match did resolve with a winner");
            Assert.AreEqual(0, crier.Sent.Count);
        }
        /// <summary>Drives: a 1v1 on the SHIPPED dials (not the rig's pinned amounts), Alpha kills Bravo. Winner 2, loser 1.</summary>
        [TestMethod]
        public void ShippedArenaMarks_OneVOneWin_PaysTwoAndLoserOne()
        {
            var rig = OneVOneLive(out var match, r => r.Dials = PvpTunables.Defaults with { CrierEnabled = false });
            Assert.AreEqual((2, 1, 1), (rig.Dials.BloodWin, rig.Dials.BloodLoss, rig.Dials.BloodDraw), "control: the rig runs the shipped amounts");

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B));
        }

        /// <summary>Drives: a 1v1 timeout draw on the shipped dials. Each seat gets 1.</summary>
        [TestMethod]
        public void ShippedArenaMarks_OneVOneDraw_PaysOneEach()
        {
            var rig = OneVOneLive(out var match, r => r.Dials = PvpTunables.Defaults with { CrierEnabled = false, OvertimeEnabled = false });

            rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1);
            rig.Tick();

            Assert.AreEqual((1, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B));
        }

        /// <summary>Drives: a 1v1 on the shipped dials, Bravo forfeits. The forfeiter is never granted; the opponent gets the win amount.</summary>
        [TestMethod]
        public void ShippedArenaMarks_Forfeiter_PaysZero()
        {
            var rig = OneVOneLive(out var match, r => r.Dials = PvpTunables.Defaults with { CrierEnabled = false });

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((0, 0), rig.Gateway.Blood(B));
        }
    }
}
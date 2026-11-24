using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;

using S = ACE.Server.Avatars.Gauntlet.GauntletState;
using E = ACE.Server.Avatars.Gauntlet.GauntletEvent;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>The run state machine, one test per row of the spec's state table.</summary>
    [TestClass]
    public class GauntletStateMachineTests
    {
        // ---- the table, row by row --------------------------------------------------------------------------------

        [TestMethod]
        public void Purchased_Loaded_GoesToStaging() => Assert.AreEqual(S.Staging, GauntletStateMachine.Next(S.Purchased, E.Loaded));

        [TestMethod]
        public void Purchased_LoadFailed_Aborts() => Assert.AreEqual(S.Aborted, GauntletStateMachine.Next(S.Purchased, E.LoadFailed));

        [TestMethod]
        public void Staging_CountdownOverWithMembers_GoesActive() => Assert.AreEqual(S.Active, GauntletStateMachine.Next(S.Staging, E.StagingOverWithMembers));

        [TestMethod]
        public void Staging_CountdownOverEmpty_Aborts() => Assert.AreEqual(S.Aborted, GauntletStateMachine.Next(S.Staging, E.StagingOverEmpty));

        [TestMethod]
        public void Active_WaveCleared_GoesToIntermission() => Assert.AreEqual(S.Intermission, GauntletStateMachine.Next(S.Active, E.WaveCleared));

        [TestMethod]
        public void Active_TimerExpired_GoesToEnding() => Assert.AreEqual(S.Ending, GauntletStateMachine.Next(S.Active, E.TimerExpired));

        [TestMethod]
        public void Intermission_Over_GoesActive() => Assert.AreEqual(S.Active, GauntletStateMachine.Next(S.Intermission, E.IntermissionOver));

        [TestMethod]
        public void Ending_Done_GoesToRewarding() => Assert.AreEqual(S.Rewarding, GauntletStateMachine.Next(S.Ending, E.EndingDone));

        [TestMethod]
        public void Rewarding_WindowOver_Closes() => Assert.AreEqual(S.Closed, GauntletStateMachine.Next(S.Rewarding, E.ClaimWindowOver));

        // ---- instance loss and the admin abort ---------------------------------------------------------------------

        [TestMethod]
        public void InstanceLost_BeforeWave1_Aborts()
        {
            Assert.AreEqual(S.Aborted, GauntletStateMachine.Next(S.Purchased, E.InstanceLost));
            Assert.AreEqual(S.Aborted, GauntletStateMachine.Next(S.Staging, E.InstanceLost));
        }

        [TestMethod]
        public void InstanceLost_MidRun_GoesToEnding()
        {
            // The cleared waves are still scored and eligible members still paid, so it ends like a timer expiry.
            Assert.AreEqual(S.Ending, GauntletStateMachine.Next(S.Active, E.InstanceLost));
            Assert.AreEqual(S.Ending, GauntletStateMachine.Next(S.Intermission, E.InstanceLost));
        }

        [TestMethod]
        public void InstanceLost_WhileRewarding_Closes() => Assert.AreEqual(S.Closed, GauntletStateMachine.Next(S.Rewarding, E.InstanceLost));

        [TestMethod]
        public void AdminAbort_FollowsTheSameShapeAsInstanceLoss()
        {
            Assert.AreEqual(S.Aborted, GauntletStateMachine.Next(S.Purchased, E.AdminAbort));
            Assert.AreEqual(S.Aborted, GauntletStateMachine.Next(S.Staging, E.AdminAbort));
            Assert.AreEqual(S.Ending, GauntletStateMachine.Next(S.Active, E.AdminAbort), "an abort from Active ends like a timer expiry");
            Assert.AreEqual(S.Ending, GauntletStateMachine.Next(S.Intermission, E.AdminAbort));
            Assert.AreEqual(S.Closed, GauntletStateMachine.Next(S.Rewarding, E.AdminAbort));
        }

        // ---- the invariants -----------------------------------------------------------------------------------------

        [TestMethod]
        public void TimerIsTheOnlyActiveExit()
        {
            // From Active, an event that does not start a run's end must never reach Ending, Rewarding, Closed or Aborted.
            // The only events allowed to end an active run are the timer, an instance loss and the admin abort. Every event
            // in the enum is classified here, so a new one has to be decided consciously.
            var endsARun = new[] { E.TimerExpired, E.InstanceLost, E.AdminAbort };

            foreach (var e in Enum.GetValues<E>())
            {
                var next = GauntletStateMachine.Next(S.Active, e);
                var ended = next == S.Ending || next == S.Rewarding || next == S.Closed || next == S.Aborted;
                Assert.AreEqual(endsARun.Contains(e), ended, $"Active + {e} -> {next}");
            }

            // The owner ruling: an empty arena, a wipe and the Avatar's death never end a run, in any live state.
            foreach (var s in new[] { S.Active, S.Intermission })
                foreach (var e in new[] { E.ArenaEmpty, E.MembersWiped, E.FighterDied })
                    Assert.AreEqual(s, GauntletStateMachine.Next(s, e), $"{s} + {e} must change nothing");
        }

        [TestMethod]
        public void TerminalStates_AcceptNothing()
        {
            foreach (var s in new[] { S.Closed, S.Aborted })
            {
                Assert.IsTrue(GauntletStateMachine.IsTerminal(s));
                foreach (var e in Enum.GetValues<E>())
                    Assert.AreEqual(s, GauntletStateMachine.Next(s, e), $"{s} + {e}");
            }

            foreach (var s in Enum.GetValues<S>().Where(s => s != S.Closed && s != S.Aborted))
                Assert.IsFalse(GauntletStateMachine.IsTerminal(s), s.ToString());
        }

        [TestMethod]
        public void UnlistedEvent_LeavesTheStateAlone_AndIsReportedAsNotApplicable()
        {
            Assert.AreEqual(S.Staging, GauntletStateMachine.Next(S.Staging, E.WaveCleared));
            Assert.IsFalse(GauntletStateMachine.TryNext(S.Staging, E.WaveCleared, out _));
            Assert.IsTrue(GauntletStateMachine.TryNext(S.Staging, E.StagingOverWithMembers, out var next));
            Assert.AreEqual(S.Active, next);
        }

        [TestMethod]
        public void EveryRun_ReachesATerminalState_FromEveryState()
        {
            // Liveness: following the "forward" events from any state always ends in Closed or Aborted.
            var forward = new[] { E.Loaded, E.StagingOverWithMembers, E.IntermissionOver, E.TimerExpired, E.EndingDone, E.ClaimWindowOver };

            foreach (var start in Enum.GetValues<S>())
            {
                var s = start;
                for (var i = 0; i < 20 && !GauntletStateMachine.IsTerminal(s); i++)
                    foreach (var e in forward)
                        if (GauntletStateMachine.TryNext(s, e, out var n)) { s = n; break; }

                Assert.IsTrue(GauntletStateMachine.IsTerminal(s), $"{start} stalls at {s}");
            }
        }

        [TestMethod]
        public void Intermission_Loop_ReturnsToActive()
        {
            var s = S.Active;
            for (var wave = 0; wave < 50; wave++)
            {
                s = GauntletStateMachine.Next(s, E.WaveCleared);
                Assert.AreEqual(S.Intermission, s);
                s = GauntletStateMachine.Next(s, E.IntermissionOver);
                Assert.AreEqual(S.Active, s);
            }
        }
    }
}

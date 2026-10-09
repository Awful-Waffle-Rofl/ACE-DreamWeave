using System;
using System.Collections.Generic;

using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The empty grace for live Thread copies (owner rulings 2026-09-17): a Starting or Active run's copy
    /// survives 10 minutes with nobody inside, the owner is warned at 5m, 2m, 1m and 30s, and a Cleared run
    /// keeps its old behaviour. Everything here is a pure function or a run object built without a world -
    /// no landblock, player or PropertyManager read.
    /// </summary>
    [TestClass]
    public class ThreadRunEmptyGraceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

        private static ThreadDungeonRun NewRun()
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos's Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        #region ResolveUnloadInterval / UnloadIntervalOverride

        [TestMethod]
        public void ResolveUnloadInterval_StartingAndActive_UseTheGrace()
        {
            Assert.AreEqual(Grace, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Starting, Grace));
            Assert.AreEqual(Grace, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Active, Grace));
        }

        [TestMethod]
        public void ResolveUnloadInterval_ClearedAndEnded_AreNull()
        {
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace));
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Ended, Grace));
        }

        [TestMethod]
        public void EmptyGrace_DefaultsToTenMinutes_AndTheTunableClampMatchesTheRuling()
        {
            Assert.AreEqual(TimeSpan.FromMinutes(10), NewRun().EmptyGrace);
            Assert.AreEqual(10L, ThreadDungeonManager.EmptyGraceMinutesDefault);
            Assert.AreEqual(5L, ThreadDungeonManager.EmptyGraceMinutesMin);
            Assert.AreEqual(60L, ThreadDungeonManager.EmptyGraceMinutesMax);
        }

        /// <summary>
        /// The pull model end to end on a real run object: the override follows the run's state as it moves,
        /// so nothing has to clear it when the run clears or ends.
        /// </summary>
        [TestMethod]
        public void UnloadIntervalOverride_FollowsTheRunState()
        {
            var run = NewRun();
            run.EmptyGrace = TimeSpan.FromMinutes(12);
            var realm = new EphemeralRealm(null) { Run = run, OpenToFellowship = false };

            Assert.AreEqual(TimeSpan.FromMinutes(12), realm.UnloadIntervalOverride, "Starting");

            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.AreEqual(TimeSpan.FromMinutes(12), realm.UnloadIntervalOverride, "Active");

            run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsNull(realm.UnloadIntervalOverride, "Cleared");

            run.MarkEnded("test");
            Assert.IsNull(realm.UnloadIntervalOverride, "Ended");
        }

        [TestMethod]
        public void UnloadIntervalOverride_RealmWithNoRun_IsNull()
        {
            Assert.IsNull(new EphemeralRealm(null).UnloadIntervalOverride);
        }

        #endregion

        #region GraceWarningDue

        /// <summary>
        /// Walks a full empty grace down in one-second ticks, as ThreadDungeonManager.Tick does, and records
        /// what would be sent. Each threshold fires exactly once, in order, worded from the remaining time.
        /// </summary>
        [TestMethod]
        public void GraceWarningDue_FiresEachThresholdOnce()
        {
            var fired = 0;
            var sent = new List<string>();

            for (var remaining = Grace - TimeSpan.FromMilliseconds(400); remaining > TimeSpan.FromSeconds(-3); remaining -= TimeSpan.FromSeconds(1))
            {
                if (ThreadDungeonRun.GraceWarningDue(remaining, fired, out var newFired))
                    sent.Add(ThreadDungeonRun.FormatRemaining(remaining));

                fired = newFired;
            }

            CollectionAssert.AreEqual(new[] { "5 minutes", "2 minutes", "1 minute", "30 seconds" }, sent);
            Assert.AreEqual(4, fired);
        }

        [TestMethod]
        public void GraceWarningDue_CrossingSeveralAtOnce_SendsOnlyOne()
        {
            // An owner who logs back in with 45 seconds left: three thresholds are behind them.
            Assert.IsTrue(ThreadDungeonRun.GraceWarningDue(TimeSpan.FromSeconds(45), 0, out var fired));
            Assert.AreEqual(3, fired);

            Assert.IsFalse(ThreadDungeonRun.GraceWarningDue(TimeSpan.FromSeconds(44), fired, out fired), "no repeat for the same crossing");
            Assert.AreEqual(3, fired);

            Assert.IsTrue(ThreadDungeonRun.GraceWarningDue(TimeSpan.FromSeconds(30), fired, out fired), "the 30s warning still comes");
            Assert.AreEqual(4, fired);
        }

        [TestMethod]
        public void GraceWarningDue_AboveFiveMinutes_ResetsAndSendsNothing()
        {
            Assert.IsFalse(ThreadDungeonRun.GraceWarningDue(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1), 3, out var fired));
            Assert.AreEqual(0, fired);
        }

        [TestMethod]
        public void GraceWarningDue_NonPositive_SendsNothing()
        {
            Assert.IsFalse(ThreadDungeonRun.GraceWarningDue(TimeSpan.Zero, 0, out var fired));
            Assert.AreEqual(0, fired, "a latch is not advanced by a copy that is already going");

            Assert.IsFalse(ThreadDungeonRun.GraceWarningDue(TimeSpan.FromSeconds(-5), 2, out fired));
            Assert.AreEqual(2, fired);
        }

        /// <summary>
        /// Re-entry restarts the ladder: after warnings down to 1m, the owner steps back in (refreshing the
        /// idle clock), leaves again, and the first look sees a full grace. The 5-minute warning comes again.
        /// </summary>
        [TestMethod]
        public void TryAdvanceGraceWarning_ReEntryRestartsTheLadder()
        {
            var run = NewRun();

            Assert.IsTrue(run.TryAdvanceGraceWarning(TimeSpan.FromMinutes(5)));
            Assert.IsTrue(run.TryAdvanceGraceWarning(TimeSpan.FromMinutes(2)));
            Assert.IsTrue(run.TryAdvanceGraceWarning(TimeSpan.FromSeconds(59)));
            Assert.IsFalse(run.TryAdvanceGraceWarning(TimeSpan.FromSeconds(58)));

            Assert.IsFalse(run.TryAdvanceGraceWarning(Grace - TimeSpan.FromSeconds(3)), "back out with a full grace: nothing to say yet");

            Assert.IsTrue(run.TryAdvanceGraceWarning(TimeSpan.FromMinutes(5)), "the ladder restarted at 5m");
            Assert.IsFalse(run.TryAdvanceGraceWarning(TimeSpan.FromMinutes(4)));
        }

        #endregion

        #region GraceRemaining

        [TestMethod]
        public void GraceRemaining_UsesTheIdleDeadline_WhenItIsSooner()
        {
            var lastActive = Now - TimeSpan.FromMinutes(3);

            Assert.AreEqual(TimeSpan.FromMinutes(7), ThreadDungeonRun.GraceRemaining(lastActive, Grace, Now.AddHours(2), Now));
        }

        [TestMethod]
        public void GraceRemaining_UsesTheTtl_WhenItIsSooner()
        {
            var lastActive = Now - TimeSpan.FromMinutes(1);
            var expires = Now + TimeSpan.FromMinutes(2);

            Assert.AreEqual(TimeSpan.FromMinutes(2), ThreadDungeonRun.GraceRemaining(lastActive, Grace, expires, Now));
        }

        #endregion

        #region wording

        [TestMethod]
        public void FormatRemaining_CeilsSeconds_FloorsMinutes_NeverOverstates()
        {
            Assert.AreEqual("5 minutes", ThreadDungeonRun.FormatRemaining(TimeSpan.FromMinutes(5)));
            Assert.AreEqual("5 minutes", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(299.6)));
            Assert.AreEqual("5 minutes", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(299.4)));
            Assert.AreEqual("4 minutes", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(240.1)), "241 rounded seconds is 4 whole minutes, never 5");
            Assert.AreEqual("2 minutes", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(119.2)));
            Assert.AreEqual("2 minutes", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(119.4)));
            Assert.AreEqual("1 minute", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(119)));
            Assert.AreEqual("1 minute", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(65)), "an owner back online with 65s left must not read '2 minutes'");
            Assert.AreEqual("1 minute", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(60.5)));
            Assert.AreEqual("1 minute", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(60)));
            Assert.AreEqual("1 minute", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(59.5)), "the 1m warning fires just under 60s and must not read '60 seconds'");
            Assert.AreEqual("59 seconds", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(59)));
            Assert.AreEqual("30 seconds", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(30)));
            Assert.AreEqual("30 seconds", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(29.1)));
            Assert.AreEqual("1 second", ThreadDungeonRun.FormatRemaining(TimeSpan.FromSeconds(1)));
            Assert.AreEqual("1 second", ThreadDungeonRun.FormatRemaining(TimeSpan.FromMilliseconds(200)));
        }

        [TestMethod]
        public void BuildGraceWarning_ExactWording()
        {
            Assert.AreEqual("Your Thread in Filos's Doom collapses in 5 minutes. Use your Thread Gem to step back in before then.",
                ThreadDungeonRun.BuildGraceWarning("Filos's Doom", TimeSpan.FromMinutes(5), canReEnter: true));

            Assert.AreEqual("Your Thread in Filos's Doom collapses in 30 seconds.",
                ThreadDungeonRun.BuildGraceWarning("Filos's Doom", TimeSpan.FromSeconds(30), canReEnter: false));
        }

        [TestMethod]
        public void BuildStillOpenLoginMessage_ExactWording()
        {
            Assert.AreEqual("Your Thread in Filos's Doom is still open. Use your Thread Gem to step back in before it collapses.",
                ThreadDungeonRun.BuildStillOpenLoginMessage("Filos's Doom", canReEnter: true));

            Assert.AreEqual("Your Thread in Filos's Doom is still open.",
                ThreadDungeonRun.BuildStillOpenLoginMessage("Filos's Doom", canReEnter: false));
        }

        [TestMethod]
        public void GemCanReEnter_OnlyAFoundGemWithNoEntriesDropsTheSentence()
        {
            Assert.IsTrue(ThreadDungeonRun.GemCanReEnter(gemFound: true, structure: 2));
            Assert.IsFalse(ThreadDungeonRun.GemCanReEnter(gemFound: true, structure: 0));
            Assert.IsTrue(ThreadDungeonRun.GemCanReEnter(gemFound: false, structure: 0));
        }

        #endregion
    }
}

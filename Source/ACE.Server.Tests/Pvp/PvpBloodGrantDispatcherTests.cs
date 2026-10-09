using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpBloodGrantDispatcher: every Blood grant is settled exactly once even when the player's action queue runs
    /// late or never (a player mid death-teleport has no landblock to tick their queue, and a disconnect then
    /// finalizes the logout at once). The fake's player queue and world-thread schedule only run when a test says so.
    /// </summary>
    [TestClass]
    public class PvpBloodGrantDispatcherTests
    {
        private const uint A = 0x50000001;

        private sealed class FakeTargets : IPvpBloodGrantTargets
        {
            public bool Online = true;
            public bool LoggingOut;
            public bool HasOfflineRecord;

            public readonly List<Action> PlayerQueue = new();
            public readonly List<(double Seconds, Action Action)> World = new();
            public readonly List<(string Path, int Amount)> Paid = new();

            public bool IsOnline(uint characterId) => Online;

            public bool TryEnqueueOnPlayer(uint characterId, Action action)
            {
                if (!Online || LoggingOut)
                    return false;

                PlayerQueue.Add(action);
                return true;
            }

            public bool GrantOnline(uint characterId, int amount, PvpBloodGrant grant)
            {
                if (!Online || LoggingOut)
                    return false;

                Paid.Add(("online", amount));
                return true;
            }

            public bool GrantOffline(uint characterId, int amount, PvpBloodGrant grant)
            {
                if (!HasOfflineRecord)
                    return false;

                Paid.Add(("offline", amount));
                return true;
            }

            public void ScheduleOnWorld(double seconds, Action action) => World.Add((seconds, action));

            /// <summary>Runs (and drains) everything queued on the player so far.</summary>
            public void RunPlayerQueue()
            {
                var run = PlayerQueue.ToList();
                PlayerQueue.Clear();
                run.ForEach(a => a());
            }

            /// <summary>Runs (and drains) every world-thread fallback scheduled so far.</summary>
            public void RunWorld()
            {
                var run = World.ToList();
                World.Clear();
                run.ForEach(w => w.Action());
            }
        }

        private static PvpBloodGrant Grant(Guid? match = null) => new PvpBloodGrant(match ?? Guid.NewGuid(), 10, "UTC", 0);

        /// <summary>
        /// The F3 case: online at payout, the player's queue never runs, the player disconnects. The world-thread
        /// fallback pays on the offline record exactly once; nothing is left pending.
        /// </summary>
        [TestMethod]
        public void QueueNeverRuns_FallbackPaysOffline_ExactlyOnce()
        {
            var t = new FakeTargets();
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 2, Grant());
            Assert.AreEqual(1, t.PlayerQueue.Count, "queued on the player");
            Assert.AreEqual(1, t.World.Count, "and the fallback armed");
            Assert.AreEqual(PvpBloodGrantDispatcher.FallbackSeconds, t.World[0].Seconds);

            t.Online = false;
            t.HasOfflineRecord = true;
            t.RunWorld();

            CollectionAssert.AreEqual(new[] { ("offline", 2) }, t.Paid);
            Assert.AreEqual(0, d.PendingCount);
        }

        /// <summary>The fallback fired first; the player's queue then runs late. The late delegate finds nothing to claim.</summary>
        [TestMethod]
        public void QueueRunsLateAfterTheFallback_DoesNotPayTwice()
        {
            var t = new FakeTargets();
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 2, Grant());

            t.Online = false;
            t.HasOfflineRecord = true;
            t.RunWorld();

            t.Online = true; // the old queue finally runs (or the player came back)
            t.RunPlayerQueue();

            Assert.AreEqual(1, t.Paid.Count, "paid once, by the fallback");
            Assert.AreEqual("offline", t.Paid[0].Path);
        }

        /// <summary>The normal case: the player's queue runs and pays; the fallback later finds nothing.</summary>
        [TestMethod]
        public void QueueRunsFirst_PaysOnline_FallbackFindsNothing()
        {
            var t = new FakeTargets();
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 7, Grant());
            t.RunPlayerQueue();
            t.HasOfflineRecord = true;
            t.RunWorld();

            CollectionAssert.AreEqual(new[] { ("online", 7) }, t.Paid);
            Assert.AreEqual(0, d.PendingCount);
        }

        /// <summary>Offline at payout: paid on the stored character at once; nothing queued, nothing scheduled.</summary>
        [TestMethod]
        public void OfflineAtPayout_PaysOfflineAtOnce()
        {
            var t = new FakeTargets { Online = false, HasOfflineRecord = true };
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 1, Grant());

            CollectionAssert.AreEqual(new[] { ("offline", 1) }, t.Paid);
            Assert.AreEqual(0, t.PlayerQueue.Count);
            Assert.AreEqual(0, t.World.Count);
        }

        /// <summary>
        /// Logging out at payout: nothing goes on the dying queue. The first fallback lands mid-logout (no offline record
        /// yet) and re-arms; the next one, after the logout finalized, pays exactly once.
        /// </summary>
        [TestMethod]
        public void LoggingOutAtPayout_NothingQueued_FallbackRetriesUntilTheOfflineRecordExists()
        {
            var t = new FakeTargets { LoggingOut = true };
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 2, Grant());
            Assert.AreEqual(0, t.PlayerQueue.Count, "never queued on a logging-out player");

            t.RunWorld();
            Assert.AreEqual(0, t.Paid.Count, "no offline record yet: not paid, not lost");
            Assert.AreEqual(1, d.PendingCount);
            Assert.AreEqual(1, t.World.Count, "re-armed");

            t.Online = false;
            t.LoggingOut = false;
            t.HasOfflineRecord = true;
            t.RunWorld();

            CollectionAssert.AreEqual(new[] { ("offline", 2) }, t.Paid);
            Assert.AreEqual(0, d.PendingCount);
        }

        /// <summary>The queue runs while the player is logging out: the claimed grant is re-pended, then paid offline once.</summary>
        [TestMethod]
        public void QueueRunsDuringLogout_RePends_ThenPaysOfflineOnce()
        {
            var t = new FakeTargets();
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 2, Grant());
            t.LoggingOut = true;
            t.RunPlayerQueue();
            Assert.AreEqual(0, t.Paid.Count);
            Assert.AreEqual(1, d.PendingCount, "re-pended, not dropped");

            t.Online = false;
            t.HasOfflineRecord = true;
            t.RunWorld(); // the original fallback and the re-armed one; only one can claim

            CollectionAssert.AreEqual(new[] { ("offline", 2) }, t.Paid);
        }

        [TestMethod]
        public void SameMatchSameCharacter_WhilePending_IsNotGrantedTwice()
        {
            var t = new FakeTargets();
            var d = new PvpBloodGrantDispatcher(t);
            var g = Grant();

            d.Grant(A, 2, g);
            d.Grant(A, 2, g);
            t.RunPlayerQueue();
            t.RunPlayerQueue();

            Assert.AreEqual(1, t.Paid.Count);
        }

        /// <summary>
        /// Two fallbacks outstanding at once (the original, plus the one a retry armed after the player's queue claimed
        /// mid-logout) consume only ONE attempt between them: the older one sees a newer generation and stands down.
        /// </summary>
        [TestMethod]
        public void TwoOutstandingFallbacks_ConsumeOnlyOneAttempt()
        {
            var t = new FakeTargets();
            var d = new PvpBloodGrantDispatcher(t);
            var g = Grant();

            d.Grant(A, 2, g);
            t.LoggingOut = true;
            t.RunPlayerQueue(); // claims generation 1, cannot pay, re-pends as generation 2 with its own fallback

            Assert.AreEqual(2, d.AttemptOf(g.MatchId, A));
            Assert.AreEqual(2, t.World.Count, "two fallbacks outstanding: generation 1's and generation 2's");

            t.RunWorld(); // still no offline record

            Assert.AreEqual(3, d.AttemptOf(g.MatchId, A), "one attempt consumed, not two");
            Assert.AreEqual(1, t.World.Count, "exactly one fallback outstanding again");
            Assert.AreEqual(0, t.Paid.Count);
        }

        /// <summary>With no offline record ever, the grant gets exactly MaxAttempts world-thread rounds (about MaxAttempts x 30 s) before giving up.</summary>
        [TestMethod]
        public void NoOfflineRecordEver_RunsExactlyMaxAttemptRounds()
        {
            var t = new FakeTargets { LoggingOut = true };
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 2, Grant());

            var rounds = 0;

            while (t.World.Count > 0 && rounds < 100)
            {
                t.RunWorld();
                rounds++;
            }

            Assert.AreEqual(PvpBloodGrantDispatcher.MaxAttempts, rounds);
        }

        [TestMethod]
        public void NoOfflineRecordEver_GivesUpAfterMaxAttempts()
        {
            var t = new FakeTargets { LoggingOut = true };
            var d = new PvpBloodGrantDispatcher(t);

            d.Grant(A, 2, Grant());

            for (var i = 0; i < PvpBloodGrantDispatcher.MaxAttempts + 5 && t.World.Count > 0; i++)
                t.RunWorld();

            Assert.AreEqual(0, t.World.Count, "stops re-arming");
            Assert.AreEqual(0, d.PendingCount);
            Assert.AreEqual(0, t.Paid.Count);
        }
    }
}

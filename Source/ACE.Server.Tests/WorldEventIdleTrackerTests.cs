using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using ACE.Entity;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventIdleTracker"/> with a hand-driven clock, sampled once a second
    /// like the production sampler (so the stale-record rule does not fire unless a test means it to). No
    /// PropertyManager read.
    /// </summary>
    [TestClass]
    public class WorldEventIdleTrackerTests
    {
        private const uint A = 1;
        private const uint B = 2;

        private static Position At(float x, float y, uint instance = 0) => new Position(0xA9B40005, x, y, 50f, 0f, 0f, 0f, 1f, instance);

        /// <summary>
        /// Observes <paramref name="guid"/> once a second from <paramref name="from"/> + 1 through
        /// <paramref name="to"/> inclusive at an unchanged position and damage, returning the last answer.
        /// </summary>
        private static bool Hold(WorldEventIdleTracker t, uint guid, int from, int to, Position pos, float damage = 0f, bool afk = false)
        {
            var idle = false;

            for (var s = from + 1; s <= to; s++)
                idle = t.IsIdle(guid, s, pos, damage, afk);

            return idle;
        }

        [TestMethod]
        public void AfkZero_IsNeverIdle_AndTracksNothing()
        {
            foreach (var afk in new[] { 0.0, -5.0, double.NaN })
            {
                var t = new WorldEventIdleTracker(afk);

                Assert.IsFalse(t.Enabled, $"afk {afk} is off");

                // Standing still for an hour, dealing no damage, even flagged afk: never idle.
                for (var now = 0; now <= 3600; now++)
                {
                    Assert.IsFalse(t.IsIdle(A, now, At(10f, 10f), 0f, flaggedAfk: false), $"afk {afk} t={now}");
                    Assert.IsFalse(t.IsIdle(B, now, At(20f, 20f), 0f, flaggedAfk: true), $"afk {afk} flagged t={now}");
                    Assert.IsFalse(t.IsIdle(B, now, null, 0f, flaggedAfk: true), $"afk {afk} null position t={now}");
                }

                Assert.AreEqual(0, t.TrackedCount, $"afk {afk} tracks nothing");
            }
        }

        [TestMethod]
        public void FirstSighting_IsActive_ThenStillnessGoesIdleAtTheThreshold()
        {
            var t = new WorldEventIdleTracker(30);
            var here = At(10f, 10f);

            Assert.IsFalse(t.IsIdle(A, 100, here, 0f, false), "first sighting is activity");
            Assert.IsFalse(Hold(t, A, 100, 129, here), "29 s still: under the window");
            Assert.IsTrue(t.IsIdle(A, 130, here, 0f, false), "exactly the window is idle (>=)");
            Assert.IsTrue(Hold(t, A, 130, 500, here), "stays idle");
            Assert.AreEqual(1, t.TrackedCount);
        }

        [TestMethod]
        public void Movement_ResetsTheIdleClock_AndSmallJitterDoesNot()
        {
            var t = new WorldEventIdleTracker(30);

            t.IsIdle(A, 0, At(10f, 10f), 0f, false);

            // Jitter at or under the threshold is not movement.
            Assert.IsFalse(Hold(t, A, 0, 20, At(10.5f, 10f)));
            Assert.IsTrue(Hold(t, A, 20, 30, At(10.5f, 10f)), "0.5 m is not beyond the threshold");

            // A real step resets the clock.
            Assert.IsFalse(t.IsIdle(A, 31, At(12f, 10f), 0f, false), "moved 1.5 m: active");
            Assert.IsFalse(Hold(t, A, 31, 60, At(12f, 10f)), "29 s since the step");
            Assert.IsTrue(t.IsIdle(A, 61, At(12f, 10f), 0f, false), "30 s since the step");
        }

        [TestMethod]
        public void InstanceChange_CountsAsMovement()
        {
            var t = new WorldEventIdleTracker(30);

            Hold(t, A, -1, 40, At(10f, 10f, 0));

            Assert.IsTrue(t.IsIdle(A, 40, At(10f, 10f, 0), 0f, false), "still for 40 s");
            Assert.IsFalse(t.IsIdle(A, 41, At(10f, 10f, 1), 0f, false), "same spot, other realm: moved");
            Assert.IsTrue(Hold(t, A, 41, 71, At(10f, 10f, 1)));
        }

        [TestMethod]
        public void NewDamage_ResetsTheIdleClock_AndAnUnchangedTotalDoesNot()
        {
            var t = new WorldEventIdleTracker(30);
            var here = At(10f, 10f);

            Hold(t, A, -1, 24, here, damage: 100f);

            Assert.IsFalse(t.IsIdle(A, 25, here, 150f, false), "damage rose: active at 25");
            Assert.IsFalse(Hold(t, A, 25, 54, here, damage: 150f), "29 s since the damage");
            Assert.IsTrue(t.IsIdle(A, 55, here, 150f, false), "30 s since the damage, total unchanged");
            Assert.IsTrue(t.IsIdle(A, 56, here, 140f, false), "a lower total is not new damage");
        }

        [TestMethod]
        public void FlaggedAfk_IsIdleImmediately_AndClearingItRestoresTheActivityRule()
        {
            var t = new WorldEventIdleTracker(30);

            Assert.IsTrue(t.IsIdle(A, 0, At(10f, 10f), 0f, true), "flagged on the first sighting");
            Assert.IsTrue(t.IsIdle(A, 1, At(50f, 50f), 999f, true), "moving and fighting while flagged is still idle");
            Assert.IsFalse(t.IsIdle(A, 2, At(50f, 50f), 999f, false), "unflagged: last activity was at 1");
        }

        [TestMethod]
        public void Guids_AreTrackedIndependently()
        {
            var t = new WorldEventIdleTracker(10);

            for (var s = 0; s < 10; s++)
            {
                t.IsIdle(A, s, At(10f, 10f), 0f, false);
                t.IsIdle(B, s, At(20f, 20f), 0f, false);
            }

            Assert.IsFalse(t.IsIdle(B, 10, At(25f, 20f), 0f, false), "B moved");
            Assert.IsTrue(t.IsIdle(A, 10, At(10f, 10f), 0f, false), "A did not");
        }

        [TestMethod]
        public void NullPosition_ComparesAsMoved()
        {
            var t = new WorldEventIdleTracker(10);

            Assert.IsFalse(Hold(t, A, -1, 20, null), "no position: never idle on the movement rule");
            Assert.IsFalse(t.IsIdle(A, 21, At(1f, 1f), 0f, false), "first real position after null is movement");
            Assert.IsTrue(Hold(t, A, 21, 31, At(1f, 1f)));
        }

        [TestMethod]
        public void TheCallersPosition_IsCopied_NotAliased()
        {
            var t = new WorldEventIdleTracker(10);
            var pos = At(10f, 10f);

            Hold(t, A, -1, 9, pos);

            // Mutating the caller's live object in place must not move the tracker's stored copy.
            pos.PositionX = 40f;

            Assert.IsFalse(t.IsIdle(A, 10, pos, 0f, false), "the live object moved 30 m from the stored copy");
        }

        [TestMethod]
        public void StaleRecord_AfterAMissedSample_IsAFirstSightingAgain()
        {
            var t = new WorldEventIdleTracker(30);
            var here = At(10f, 10f);

            Assert.AreEqual(2.0, t.StaleAfterSeconds, "2 x the default 1 s sample period");

            Assert.IsTrue(Hold(t, A, -1, 40, here), "idle after 40 s of stillness");

            // Absent for one sample (logged out at 41, back at 42 in the same spot): a fresh sighting.
            Assert.IsFalse(t.IsIdle(A, 42, here, 0f, false), "a 2 s gap resets the idle clock");
            Assert.IsFalse(Hold(t, A, 42, 71, here), "29 s since the return");
            Assert.IsTrue(t.IsIdle(A, 72, here, 0f, false), "30 s since the return");

            // A long absence (relog minutes later), still the same spot.
            Assert.IsFalse(t.IsIdle(A, 600, here, 0f, false), "a relog never inherits the old idle clock");
        }

        [TestMethod]
        public void StaleRecord_JustUnderTwoPeriods_IsNotStale()
        {
            var t = new WorldEventIdleTracker(5);
            var here = At(10f, 10f);

            t.IsIdle(A, 0, here, 0f, false);

            // Gaps of 1.9 s keep the record live, so stillness still accumulates.
            Assert.IsFalse(t.IsIdle(A, 1.9, here, 0f, false));
            Assert.IsFalse(t.IsIdle(A, 3.8, here, 0f, false));
            Assert.IsTrue(t.IsIdle(A, 5.7, here, 0f, false), "5.7 s still across sub-stale gaps");
        }

        [TestMethod]
        public void SamplePeriod_ScalesTheStaleThreshold_AndANonPositivePeriodUsesTheDefault()
        {
            Assert.AreEqual(10.0, new WorldEventIdleTracker(30, 5.0).StaleAfterSeconds);
            Assert.AreEqual(2.0, new WorldEventIdleTracker(30, 0.0).StaleAfterSeconds);
            Assert.AreEqual(2.0, new WorldEventIdleTracker(30, -1.0).StaleAfterSeconds);
        }

        [TestMethod]
        public void ConcurrentCalls_OverDistinctGuids_NeitherThrowNorLoseRecords()
        {
            const int Threads = 4;
            const int GuidsPerThread = 250;
            const int Iterations = 10000;

            var t = new WorldEventIdleTracker(30);
            var errors = new ConcurrentQueue<Exception>();

            Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
            {
                try
                {
                    var pos = At(10f + worker, 10f);

                    for (var i = 0; i < Iterations; i++)
                    {
                        var guid = (uint)(1000 * (worker + 1) + i % GuidsPerThread);

                        t.IsIdle(guid, i / GuidsPerThread, pos, i % 7, i % 11 == 0);

                        if (i % 100 == 0)
                            _ = t.TrackedCount;
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });

            Assert.IsTrue(errors.IsEmpty, errors.TryPeek(out var first) ? first.ToString() : "");
            Assert.AreEqual(Threads * GuidsPerThread, t.TrackedCount);
        }
    }
}

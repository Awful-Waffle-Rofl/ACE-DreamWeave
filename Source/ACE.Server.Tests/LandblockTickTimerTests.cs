using System.Diagnostics;

using ACE.Common.Performance;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: LandblockTickTimer, the start/stop decision behind a landblock's
    /// Monitor5m/Monitor1h event (and so behind SLOW_TICK lbN_ms and @landblockstats), and the per-section
    /// breakdown behind lbN_s1/lbN_s2.
    ///
    /// Driven through the same call sequence Landblock makes - TickPhysics (skipped while dormant), then
    /// TickMultiThreadedWork, then TickSingleThreadedWork - with a timed spin standing in for each method's work,
    /// because constructing a real Landblock needs client dat files.
    /// </summary>
    [TestClass]
    public class LandblockTickTimerTests
    {
        private static void Spin(double ms)
        {
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed.TotalMilliseconds < ms)
            {
            }
        }

        private static double LastEventMs(RateMonitor monitor) => monitor.EventHistory.LastEvent * 1000;

        /// <summary>
        /// The Part 1 bug. A dormant landblock skips TickPhysics, which used to be the only place the
        /// "event is open" flag was cleared - so TickSingleThreadedWork Restarted the monitors again and the
        /// recorded event was single-threaded work only, silently dropping the multi-threaded work (action queue,
        /// generators, heartbeat, SaveDB).
        /// </summary>
        [TestMethod]
        public void DormantTickEventSpansMultiThreadedAndSingleThreadedWork()
        {
            var timer = new LandblockTickTimer();

            timer.BeginMultiThreaded(1);
            Spin(40);
            timer.EndMultiThreaded();

            timer.BeginSingleThreaded(1);
            Spin(5);
            timer.EndSingleThreaded();

            Assert.IsTrue(LastEventMs(timer.Monitor5m) >= 45, $"Monitor5m recorded {LastEventMs(timer.Monitor5m):F2} ms; the dormant tick did 40 ms multi + 5 ms single");
            Assert.IsTrue(LastEventMs(timer.Monitor1h) >= 45, $"Monitor1h recorded {LastEventMs(timer.Monitor1h):F2} ms; the dormant tick did 40 ms multi + 5 ms single");
        }

        [TestMethod]
        public void ActiveTickEventSpansPhysicsMultiAndSingle()
        {
            var timer = new LandblockTickTimer();

            timer.BeginPhysics(1);
            Spin(10);
            timer.EndPhysics();

            timer.BeginMultiThreaded(1);
            Spin(10);
            timer.EndMultiThreaded();

            timer.BeginSingleThreaded(1);
            Spin(10);
            timer.EndSingleThreaded();

            Assert.IsTrue(LastEventMs(timer.Monitor5m) >= 30, $"recorded {LastEventMs(timer.Monitor5m):F2} ms of 30 ms of work");
            Assert.IsTrue(timer.LastSectionMs[(int)LandblockTickSection.Physics] >= 10, "physics is timed by the timer itself");
        }

        /// <summary>
        /// Control for the two tests above: they are not passing merely because the event runs from first open
        /// to close in wall time. The gaps between the three calls - where OTHER landblocks tick - are excluded.
        /// </summary>
        [TestMethod]
        public void GapsBetweenTheThreeCallsAreNotCounted()
        {
            var timer = new LandblockTickTimer();

            timer.BeginMultiThreaded(1);
            Spin(5);
            timer.EndMultiThreaded();

            Spin(60); // other landblocks' work

            timer.BeginSingleThreaded(1);
            Spin(5);
            timer.EndSingleThreaded();

            var ms = LastEventMs(timer.Monitor5m);

            Assert.IsTrue(ms >= 10 && ms < 60, $"recorded {ms:F2} ms: must cover both 5 ms spans and not the 60 ms gap");
        }

        [TestMethod]
        public void SectionsReportedAreExactlyThoseOfTheStampedIteration()
        {
            var timer = new LandblockTickTimer();

            // iteration 1: active
            timer.BeginPhysics(1);
            Spin(2);
            timer.EndPhysics();
            timer.BeginMultiThreaded(1);
            timer.AddSection(LandblockTickSection.RunActions, 0.003);
            timer.AddSection(LandblockTickSection.Monster, 0.009);
            timer.EndMultiThreaded();
            timer.BeginSingleThreaded(1);
            timer.AddSection(LandblockTickSection.PlayerTick, 0.001);
            timer.EndSingleThreaded();

            Assert.AreEqual(9.0, timer.LastSectionMs[(int)LandblockTickSection.Monster], 1e-9);
            Assert.IsTrue(timer.LastSectionMs[(int)LandblockTickSection.Physics] >= 2);

            // iteration 2: dormant - no physics, generator update only
            timer.BeginMultiThreaded(2);
            timer.AddSection(LandblockTickSection.GenUpdate, 0.080);
            timer.AddSection(LandblockTickSection.GenUpdate, 0.001); // accumulates within one tick
            timer.EndMultiThreaded();
            timer.BeginSingleThreaded(2);
            timer.AddSection(LandblockTickSection.WoHeartbeat, 0.002);
            timer.EndSingleThreaded();

            Assert.AreEqual(81.0, timer.LastSectionMs[(int)LandblockTickSection.GenUpdate], 1e-9);
            Assert.AreEqual(2.0, timer.LastSectionMs[(int)LandblockTickSection.WoHeartbeat], 1e-9, "a section added in TickSingleThreadedWork belongs to the same event");
            Assert.AreEqual(0.0, timer.LastSectionMs[(int)LandblockTickSection.Physics], "no carry-over of iteration 1's physics into a dormant tick");
            Assert.AreEqual(0.0, timer.LastSectionMs[(int)LandblockTickSection.Monster], "no carry-over of iteration 1's monster time");
            Assert.AreEqual(0.0, timer.LastSectionMs[(int)LandblockTickSection.RunActions]);
            Assert.AreEqual(0.0, timer.LastSectionMs[(int)LandblockTickSection.PlayerTick]);
        }

        /// <summary>
        /// A tick that threw before TickSingleThreadedWork closed its event (LandblockManager catches per
        /// landblock) leaves it open. The next iteration must start a fresh event, not resume the stale one.
        /// </summary>
        [TestMethod]
        public void AnEventLeftOpenByAThrowIsNotResumedByTheNextIteration()
        {
            var timer = new LandblockTickTimer();

            timer.BeginPhysics(1);
            Spin(30);
            timer.EndPhysics();
            timer.BeginMultiThreaded(1);
            timer.AddSection(LandblockTickSection.Monster, 0.5);
            timer.EndMultiThreaded();
            // ...TickSingleThreadedWork threw: no EndSingleThreaded for iteration 1

            timer.BeginMultiThreaded(2); // dormant now
            timer.AddSection(LandblockTickSection.DbSave, 0.004);
            timer.EndMultiThreaded();
            timer.BeginSingleThreaded(2);
            timer.EndSingleThreaded();

            Assert.IsTrue(LastEventMs(timer.Monitor5m) < 30, $"recorded {LastEventMs(timer.Monitor5m):F2} ms: iteration 1's 30 ms of physics leaked into iteration 2");
            Assert.AreEqual(0.0, timer.LastSectionMs[(int)LandblockTickSection.Monster]);
            Assert.AreEqual(0.0, timer.LastSectionMs[(int)LandblockTickSection.Physics]);
            Assert.AreEqual(4.0, timer.LastSectionMs[(int)LandblockTickSection.DbSave], 1e-9);
        }

        [TestMethod]
        public void TopTwoPicksTheTwoLargestReportableSections()
        {
            var ms = new double[LandblockTickSections.Count];
            ms[(int)LandblockTickSection.Physics] = 3;
            ms[(int)LandblockTickSection.GenUpdate] = 80;
            ms[(int)LandblockTickSection.LbHeartbeat] = 12;
            ms[(int)LandblockTickSection.WoHeartbeat] = 0.004; // below MinReportableMs

            LandblockTickSections.TopTwo(ms, out var s1, out var s1Ms, out var s2, out var s2Ms);

            Assert.AreEqual((int)LandblockTickSection.GenUpdate, s1);
            Assert.AreEqual(80.0, s1Ms);
            Assert.AreEqual((int)LandblockTickSection.LbHeartbeat, s2);
            Assert.AreEqual(12.0, s2Ms);

            // the second slot must also be filled when the largest comes LAST
            var rising = new double[LandblockTickSections.Count];
            rising[0] = 1;
            rising[8] = 5;

            LandblockTickSections.TopTwo(rising, out s1, out _, out s2, out _);

            Assert.AreEqual(8, s1);
            Assert.AreEqual(0, s2);
        }

        [TestMethod]
        public void TopTwoLeavesEmptySlotsAtMinusOne()
        {
            var ms = new double[LandblockTickSections.Count];

            LandblockTickSections.TopTwo(ms, out var s1, out _, out var s2, out _);
            Assert.AreEqual(-1, s1);
            Assert.AreEqual(-1, s2);

            ms[(int)LandblockTickSection.DbSave] = 0.5;
            ms[(int)LandblockTickSection.Monster] = 0.001;

            LandblockTickSections.TopTwo(ms, out s1, out _, out s2, out _);
            Assert.AreEqual((int)LandblockTickSection.DbSave, s1);
            Assert.AreEqual(-1, s2, "a section that would print as 0 is not reported");
        }

        [TestMethod]
        public void SectionNamesAreFixedAndOnePerSection()
        {
            CollectionAssert.AreEqual(
                new[] { "physics", "run_actions", "monster", "gen_update", "gen_regen", "lb_heartbeat", "db_save", "player_tick", "wo_heartbeat" },
                LandblockTickSections.Names);
            Assert.AreEqual(LandblockTickSections.Count, System.Enum.GetValues(typeof(LandblockTickSection)).Length);
        }
    }
}

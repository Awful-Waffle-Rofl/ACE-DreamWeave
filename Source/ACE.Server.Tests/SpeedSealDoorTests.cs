using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>The Seal door lock: the pure rule, the run's use of it against a fake world, and the store's validation.</summary>
    [TestClass]
    public class SpeedSealDoorTests
    {
        private const uint BookWcid = 1007500;
        private const uint Owner = 0x50000042;

        // ---- the rule ----

        [TestMethod]
        public void Rule_ShutsOnlyWhenLatchedBossAliveAndRunnerInside()
        {
            Assert.AreEqual(SpeedSealDoorDecision.Shut, SpeedCourseRules.DecideSealDoor(true, true, false, true, false));
            Assert.AreEqual(SpeedSealDoorDecision.NoChange, SpeedCourseRules.DecideSealDoor(true, true, false, false, false), "runner outside: never shut");
            Assert.AreEqual(SpeedSealDoorDecision.NoChange, SpeedCourseRules.DecideSealDoor(false, true, false, true, false), "Seal not latched: nothing");
            Assert.AreEqual(SpeedSealDoorDecision.NoChange, SpeedCourseRules.DecideSealDoor(true, false, false, true, false), "boss not spawned: nothing");
        }

        [TestMethod]
        public void Rule_AlreadyShut_DoesNotShutAgain_EvenIfTheRunnerIsOutside()
        {
            Assert.AreEqual(SpeedSealDoorDecision.NoChange, SpeedCourseRules.DecideSealDoor(true, true, false, true, true));
            Assert.AreEqual(SpeedSealDoorDecision.NoChange, SpeedCourseRules.DecideSealDoor(true, true, false, false, true));
        }

        [TestMethod]
        public void Rule_BossDead_OpensAShutDoorOnly()
        {
            Assert.AreEqual(SpeedSealDoorDecision.Open, SpeedCourseRules.DecideSealDoor(true, true, true, true, true));
            Assert.AreEqual(SpeedSealDoorDecision.Open, SpeedCourseRules.DecideSealDoor(true, true, true, false, true));
            Assert.AreEqual(SpeedSealDoorDecision.NoChange, SpeedCourseRules.DecideSealDoor(true, true, true, true, false), "a dead boss never shuts the door");
        }

        // ---- the run ----

        private static (SpeedCourseRun Run, SpeedCourseFakeWorld World) NewRun(bool withLock = true)
        {
            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();

            var course = SpeedCourseFixtures.Course();

            if (withLock)
                course.SealLock = new SpeedSealLockDef { Region = new SpeedHazardRegionDef { Shape = "rect", MinX = 199, MaxX = 225, MinY = -225, MaxY = -195, GroundZ = 0 } };

            var run = new SpeedCourseRun(course, 3, 60, BookWcid, 120, Owner, 12345, world, now: 0.0);
            run.AttachHazardDriver(new RecordingHazardDriver());
            return (run, world);
        }

        private static void Place(SpeedCourseFakeWorld world, float x, float y)
            => world.RunnerView = new SpeedRunnerView { Online = true, BoundHere = true, HasPosition = true, X = x, Y = y, Z = 0f };

        private static void SpawnBoss(SpeedCourseRun run, SpeedCourseFakeWorld world)
        {
            run.Tick(0.5);
            world.Drain();
            world.Seals["bb_seal"] = true;
            Place(world, 190f, -210f);
            run.Tick(1.0);
            Assert.IsFalse(run.Boss.IsNone, "the boss spawned");
        }

        [TestMethod]
        public void Run_RunnerOutsideTheRegion_NeverShuts()
        {
            var (run, world) = NewRun();
            SpawnBoss(run, world);

            run.Tick(2.0);
            run.Tick(3.0);

            Assert.AreEqual(0, world.SealDoorCalls.Count);
        }

        [TestMethod]
        public void Run_RunnerInsideWithBossAlive_ShutsExactlyOnce()
        {
            var (run, world) = NewRun();
            SpawnBoss(run, world);

            Place(world, 210f, -210f);
            run.Tick(2.0);
            run.Tick(3.0);
            run.Tick(4.0);

            CollectionAssert.AreEqual(new[] { ("bb_seal", true) }, world.SealDoorCalls.ToArray());
        }

        [TestMethod]
        public void Run_BossDies_OpensTheDoorOnce()
        {
            var (run, world) = NewRun();
            SpawnBoss(run, world);
            Place(world, 210f, -210f);
            run.Tick(2.0);

            world.Dead.Add(run.Boss.Guid);
            run.Tick(3.0);
            run.Tick(4.0);

            CollectionAssert.AreEqual(new[] { ("bb_seal", true), ("bb_seal", false) }, world.SealDoorCalls.ToArray());
        }

        [TestMethod]
        public void Run_EndWhileShut_PostsAnOpen()
        {
            var (run, world) = NewRun();
            SpawnBoss(run, world);
            Place(world, 210f, -210f);
            run.Tick(2.0);
            Assert.AreEqual(1, world.SealDoorCalls.Count);

            run.End("forfeit");
            Assert.AreEqual(1, world.SealDoorCalls.Count, "the open is posted, not run inline");

            world.Drain();
            CollectionAssert.AreEqual(new[] { ("bb_seal", true), ("bb_seal", false) }, world.SealDoorCalls.ToArray());

            run.End("again");
            world.Drain();
            Assert.AreEqual(2, world.SealDoorCalls.Count, "End is idempotent");
        }

        // ---- the End/Tick race: End runs off-thread (SpeedCourseManager.Register) in the middle of a tick ----

        [TestMethod]
        public void Race_EndFromInsideTheShut_LeavesTheDoorOpen()
        {
            var (run, world) = NewRun();
            SpawnBoss(run, world);
            Place(world, 210f, -210f);

            world.OnSetSealDoor = (key, shut) =>
            {
                if (shut)
                    run.End("runner entered a new instance");
            };

            run.Tick(2.0);
            world.Drain();

            Assert.AreEqual(("bb_seal", false), world.SealDoorCalls.Last(), "the last door call opens it: " + string.Join(", ", world.SealDoorCalls));
            Assert.AreEqual(("bb_seal", true), world.SealDoorCalls.First());
        }

        [TestMethod]
        public void Race_EndAfterTheTicksEndedCheck_ButBeforeTheShut_StillOpensTheDoor()
        {
            // The window the review found: Tick has passed its IsEnded check, End then reads sealDoorShut == false and posts
            // no open, and only then does Tick shut the door. SealLatched is read inside TickSealDoor before the shut decision.
            var (run, world) = NewRun();
            SpawnBoss(run, world);
            Place(world, 210f, -210f);

            world.OnSealLatched = key => run.End("instance re-registered");

            run.Tick(2.0);
            world.Drain();

            Assert.IsTrue(run.IsEnded);
            Assert.IsTrue(world.SealDoorCalls.Count > 0 && world.SealDoorCalls.Last() == ("bb_seal", false),
                "the door must not stay shut after End: " + string.Join(", ", world.SealDoorCalls));
        }

        [TestMethod]
        public void Run_EndWhileOpen_PostsNothing()
        {
            var (run, world) = NewRun();
            SpawnBoss(run, world);

            run.End("fail");
            world.Drain();

            Assert.AreEqual(0, world.SealDoorCalls.Count);
        }

        [TestMethod]
        public void Run_NotLatched_NothingHappens()
        {
            var (run, world) = NewRun();
            run.Tick(0.5);
            world.Drain();
            Place(world, 210f, -210f);
            run.Tick(1.0);
            run.Tick(2.0);

            Assert.AreEqual(0, world.SealDoorCalls.Count);
        }

        [TestMethod]
        public void Run_CourseWithoutSealLock_NeverTouchesTheDoor()
        {
            var (run, world) = NewRun(withLock: false);
            SpawnBoss(run, world);
            Place(world, 210f, -210f);
            run.Tick(2.0);
            run.End("finish");
            world.Drain();

            Assert.AreEqual(0, world.SealDoorCalls.Count);
        }

        // ---- the store ----

        [TestMethod]
        public void Store_SealLockWithoutRegion_IsRejected()
        {
            var course = SpeedCourseFixtures.Course();
            course.SealLock = new SpeedSealLockDef();

            var problems = SpeedCourseStore.Validate(course, new SpeedCourseFixtures.FakeLookup());
            Assert.IsTrue(problems.Any(p => p.Contains("sealLock")), string.Join("; ", problems));
        }

        [TestMethod]
        public void Store_SealLockWithBadRegion_IsRejected()
        {
            var course = SpeedCourseFixtures.Course();
            course.SealLock = new SpeedSealLockDef { Region = new SpeedHazardRegionDef { Shape = "rect", MinX = 5, MaxX = 5, MinY = 0, MaxY = 9 } };

            var problems = SpeedCourseStore.Validate(course, new SpeedCourseFixtures.FakeLookup());
            Assert.IsTrue(problems.Any(p => p.Contains("sealLock") && p.Contains("zero width")), string.Join("; ", problems));
        }
    }
}

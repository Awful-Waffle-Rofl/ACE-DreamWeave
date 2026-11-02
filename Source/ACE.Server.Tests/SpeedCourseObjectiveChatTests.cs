using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SpeedCourseManager.OwnsObjectiveChat: the generic objective-lock line is suppressed only for the live course run's
    /// own Seal key, so every other gate in a course instance (the start lever's stub door) keeps its line. The run
    /// registry is process-static; every test removes what it registers.
    /// </summary>
    [TestClass]
    public class SpeedCourseObjectiveChatTests
    {
        private const uint Instance = 0x80089777u;
        private const uint OtherInstance = 0x80089778u;

        private static SpeedCourseRun NewRun()
            => new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, 1007500, 120, 0x50000042, 1, new SpeedCourseFakeWorld { Instance = Instance }, now: 0.0);

        [TestMethod]
        public void OwnsTheSealKey_ButNotAnotherKey_NorAnotherInstance_NorAnEndedRun()
        {
            var run = NewRun();
            SpeedCourseManager.SetRunForTests(Instance, run);

            try
            {
                Assert.AreEqual("bb_seal", run.Course.SealKey);
                Assert.IsTrue(SpeedCourseManager.OwnsObjectiveChat(Instance, "bb_seal"), "the live run owns its Seal's chat");

                Assert.IsFalse(SpeedCourseManager.OwnsObjectiveChat(Instance, "bb_stub"), "another key in the instance keeps its line");
                Assert.IsFalse(SpeedCourseManager.OwnsObjectiveChat(Instance, null));
                Assert.IsFalse(SpeedCourseManager.OwnsObjectiveChat(OtherInstance, "bb_seal"), "a non-speed instance");
                Assert.IsFalse(SpeedCourseManager.OwnsObjectiveChat(0u, "bb_seal"), "the base world");

                run.End("test");
                Assert.IsFalse(SpeedCourseManager.OwnsObjectiveChat(Instance, "bb_seal"), "an ended run owns nothing");
            }
            finally
            {
                SpeedCourseManager.SetRunForTests(Instance, null);
            }
        }

        [TestMethod]
        public void TheLandblockOverload_IsFalseForNoLandblock()
        {
            Assert.IsFalse(SpeedCourseManager.OwnsObjectiveChat((ACE.Server.Entity.Landblock)null, "bb_seal"));
        }
    }
}

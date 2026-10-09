using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers the one part of ThreadDungeonSpawner that can be exercised without a world: the per-run
    /// in-flight latch. Everything else in that class needs a loaded landblock, the world weenie cache and a
    /// live PropertyManager (whose reads throw under the test harness).
    ///
    /// Why the latch matters enough to test. It is keyed on the run id, which IS the ephemeral instance id,
    /// and LandblockManager re-issues those. A latch left behind by a populate chain that never finished -
    /// Landblock.Unload clears the landblock's action queue outright, taking any pending delegate with it -
    /// is permanent, and a later run handed the same id would be refused a populate and sit empty forever.
    /// ThreadDungeonManager.EndRun calls ForgetRun for exactly that case.
    ///
    /// The internals are visible via InternalsVisibleTo in ACE.Server.csproj:15.
    /// </summary>
    [TestClass]
    public class ThreadDungeonSpawnerLatchTests
    {
        /// <summary>Ids private to this class, so a parallel test can never collide on the static latch.</summary>
        private const uint RunA = 0x7F00A001u;
        private const uint RunB = 0x7F00A002u;

        [TestCleanup]
        public void Cleanup()
        {
            ThreadDungeonSpawner.ForgetRun(RunA);
            ThreadDungeonSpawner.ForgetRun(RunB);
        }

        [TestMethod]
        public void ForgetRun_releases_the_latch()
        {
            Assert.IsFalse(ThreadDungeonSpawner.IsPopulating(RunA));

            Assert.IsTrue(ThreadDungeonSpawner.TryBeginPopulate(RunA));
            Assert.IsTrue(ThreadDungeonSpawner.IsPopulating(RunA));

            ThreadDungeonSpawner.ForgetRun(RunA);

            Assert.IsFalse(ThreadDungeonSpawner.IsPopulating(RunA), "EndRun must be able to release a chain that never finished");
            Assert.IsTrue(ThreadDungeonSpawner.TryBeginPopulate(RunA), "a re-issued instance id can populate again");
        }

        [TestMethod]
        public void A_second_populate_of_the_same_run_is_refused_while_one_is_in_flight()
        {
            Assert.IsTrue(ThreadDungeonSpawner.TryBeginPopulate(RunA));
            Assert.IsFalse(ThreadDungeonSpawner.TryBeginPopulate(RunA), "the world thread ticks again while the chain is queued");
        }

        [TestMethod]
        public void ForgetRun_is_idempotent_and_safe_for_an_unlatched_run()
        {
            ThreadDungeonSpawner.ForgetRun(RunB);
            ThreadDungeonSpawner.ForgetRun(RunB);
            Assert.IsFalse(ThreadDungeonSpawner.IsPopulating(RunB));
        }

        [TestMethod]
        public void The_latch_is_per_run()
        {
            Assert.IsTrue(ThreadDungeonSpawner.TryBeginPopulate(RunA));
            Assert.IsTrue(ThreadDungeonSpawner.TryBeginPopulate(RunB));

            ThreadDungeonSpawner.ForgetRun(RunA);

            Assert.IsFalse(ThreadDungeonSpawner.IsPopulating(RunA));
            Assert.IsTrue(ThreadDungeonSpawner.IsPopulating(RunB));
        }
    }
}

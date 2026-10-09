using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins that WorldEventManager builds its axis store and family catalog WITHOUT holding the manager
    /// lock. Program.cs calls WorldEventManager.Initialize after WorldManager.Initialize has started the
    /// world thread, and the first WorldEventManager.Tick takes the same lock; while the ~16 s catalog
    /// build held it, world-loop iteration 1 stalled for the whole build (stage SLOW_TICK iter=1
    /// total_ms=17274 and 18034, 2026-09-21, all of it in ph_world_managers).
    ///
    /// The loader is replaced through the InitializeWith seam, so no database or PropertyManager is
    /// touched. Tick with no running event reads only the clock and the lock.
    /// </summary>
    [TestClass]
    public class WorldEventManagerInitializeLockTests
    {
        private static void ForceTickDue()
        {
            // Tick self-throttles to one pass per second; zeroing the schedule guarantees the call under
            // test actually reaches the lock instead of returning early, which would pass vacuously.
            var field = typeof(WorldEventManager).GetField("nextTickTime", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "WorldEventManager.nextTickTime not found; update this test's reflection");
            field.SetValue(null, 0.0);
        }

        [TestMethod]
        public void Tick_DoesNotWaitForAnInitializeBuildInProgress()
        {
            var previousStore = WorldEventManager.Store;
            var previousCatalog = WorldEventManager.Catalog;

            var probeStore = WorldEventAxisStore.Parse(null, null, null, null, null, resolvedFolder: "first-tick-lock-probe");
            var probeCatalog = new WorldEventCatalog(new Dictionary<string, IReadOnlyList<FamilyMember>>(), new List<string>());

            using var buildEntered = new ManualResetEventSlim(false);
            using var releaseBuild = new ManualResetEventSlim(false);

            Task init = null;

            try
            {
                init = Task.Run(() => WorldEventManager.InitializeWith(() =>
                {
                    buildEntered.Set();
                    releaseBuild.Wait(TimeSpan.FromSeconds(30));
                    return (probeStore, probeCatalog);
                }));

                Assert.IsTrue(buildEntered.Wait(TimeSpan.FromSeconds(10)), "the stub build never started");

                ForceTickDue();

                var tick = Task.Run(WorldEventManager.Tick);

                Assert.IsTrue(tick.Wait(TimeSpan.FromSeconds(5)),
                    "WorldEventManager.Tick blocked while an Initialize build was in progress: the build is running under the manager lock");

                // Nothing is published until the build finishes.
                Assert.AreNotSame(probeStore, WorldEventManager.Store);

                releaseBuild.Set();

                Assert.IsTrue(init.Wait(TimeSpan.FromSeconds(10)), "InitializeWith did not return after the build was released");

                Assert.AreSame(probeStore, WorldEventManager.Store);
                Assert.AreSame(probeCatalog, WorldEventManager.Catalog);
            }
            finally
            {
                releaseBuild.Set();
                init?.Wait(TimeSpan.FromSeconds(10));
                WorldEventManager.InitializeWith(() => (previousStore, previousCatalog));
            }
        }

        [TestMethod]
        public void InitializeWith_LoaderThrows_KeepsThePreviousStoreAndDoesNotThrow()
        {
            var previousStore = WorldEventManager.Store;
            var previousCatalog = WorldEventManager.Catalog;

            WorldEventManager.InitializeWith(() => throw new InvalidOperationException("stub axis folder is broken"));

            Assert.AreSame(previousStore, WorldEventManager.Store);
            Assert.AreSame(previousCatalog, WorldEventManager.Catalog);
        }
    }
}

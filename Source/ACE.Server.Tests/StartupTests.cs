using System;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Common;
using ACE.Database;
using ACE.Server.Managers;

[assembly: DoNotParallelize]

namespace ACE.Server.Tests
{
    [TestClass]
    public class StartupTests
    {
        private static bool databaseManagerInitialized;

        private static void EnsureDatabaseManagerInitialized()
        {
            // DatabaseManager.Initialize retries unreachable databases forever, so probe first
            // and skip (rather than hang) when no MySQL instance is available
            TestEnvironment.RequireDatabases();

            if (databaseManagerInitialized)
                return;

            DatabaseManager.Initialize();
            databaseManagerInitialized = true;
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void DatabaseManager_Initialize()
        {
            // this triggers all the prepared statement validation
            EnsureDatabaseManagerInitialized();
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void WorldManager_Initialize()
        {
            // WorldManager requires an initialized DatabaseManager, so don't rely on test ordering
            EnsureDatabaseManagerInitialized();

            // The world tick calls into these three, and Program.cs initializes all of them before
            // WorldManager.Initialize(). HouseManager in particular leaves its RentQueue null until
            // then, so HouseManager.Tick() throws on the very first world tick without it. Same order
            // as Program.cs.
            RealmManager.Initialize();
            PlayerManager.Initialize();
            HouseManager.Initialize();

            // Landblock preloading constructs real Landblock objects, and the Landblock constructor
            // reads the client's dat files through DatManager.CellDat. DatManager is never initialized
            // in this project (and the dat files are not present in CI at all), so leaving preloading
            // on makes the world thread throw a NullReferenceException. What this test covers is the
            // startup wiring - the world thread starts, reaches its update loop, and stops on request -
            // so run it with preloading off rather than booting a world that needs client assets.
            var landblockPreloading = ConfigManager.Config.Server.LandblockPreloading;
            ConfigManager.Config.Server.LandblockPreloading = false;

            // WorldManager now catches anything escaping the world thread and logs it rather than
            // killing the process, so capture that log to report why the thread died instead of
            // leaving a bare "did not start" failure.
            var appender = new MemoryAppender();
            var hierarchy = (Hierarchy)LogManager.GetRepository(typeof(WorldManager).Assembly);
            var root = hierarchy.Root;
            var priorLevel = root.Level;
            var priorConfigured = hierarchy.Configured;

            var started = false;
            var stopped = false;

            try
            {
                root.AddAppender(appender);
                root.Level = Level.All;
                hierarchy.Configured = true;

                WorldManager.Initialize();

                started = WaitFor(() => WorldManager.WorldActive);
            }
            finally
            {
                WorldManager.StopWorld();

                // Do not return while the world thread is still live: it would keep ticking underneath
                // every test that runs after this one.
                stopped = WaitFor(() => !WorldManager.WorldActive);

                root.RemoveAppender(appender);
                root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;

                ConfigManager.Config.Server.LandblockPreloading = landblockPreloading;
            }

            var fatal = appender.GetEvents().Where(e => e.Level >= Level.Error).ToList();
            var fatalDetail = fatal.Count == 0
                ? "no error was logged"
                : string.Join(" | ", fatal.Select(e => $"{e.RenderedMessage} {e.ExceptionObject}"));

            Assert.IsTrue(started, $"The world thread did not reach its update loop: {fatalDetail}");
            Assert.IsTrue(stopped, $"The world thread did not stop after StopWorld(): {fatalDetail}");
            Assert.AreEqual(0, fatal.Count, $"the world thread logged {fatal.Count} error(s): {fatalDetail}");
        }

        /// <summary>
        /// Polls condition until it holds or the timeout expires. WorldManager exposes no handle on its
        /// thread, so this mirrors how ServerManager itself waits on WorldActive during shutdown.
        /// </summary>
        private static bool WaitFor(Func<bool> condition, int timeoutSeconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;

                Thread.Sleep(10);
            }

            return condition();
        }
    }
}

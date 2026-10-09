using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Locates a Config.js for tests, and gates database-dependent tests so they skip
    /// (Assert.Inconclusive) instead of failing when no MySQL instance is reachable.
    /// </summary>
    internal static class TestEnvironment
    {
        private static bool configInitialized;

        /// <summary>
        /// Initializes ConfigManager from the first Config.js found: the test output directory,
        /// then a real (gitignored) Source\ACE.Server\Config.js, then Config.js.example.
        /// </summary>
        public static void InitializeConfig()
        {
            if (configInitialized)
                return;

            var configPath = Path.Combine(AppContext.BaseDirectory, "Config.js");

            if (!File.Exists(configPath))
            {
                var source = FindInSourceTree(Path.Combine("ACE.Server", "Config.js"))
                    ?? FindInSourceTree(Path.Combine("ACE.Server", "Config.js.example"));

                if (source == null)
                    Assert.Inconclusive("Skipped: no Config.js or Config.js.example found in the source tree.");

                File.Copy(source, configPath, true);
            }

            ConfigManager.Initialize(configPath);
            configInitialized = true;
        }

        /// <summary>
        /// Skips the calling test unless the authentication database is reachable with the configured Config.js.
        /// </summary>
        public static void RequireAuthDatabase(AuthenticationDatabase authDb)
        {
            InitializeConfig();
            RequireDatabase("authentication", () => authDb.Exists(false));
        }

        /// <summary>
        /// Skips the calling test unless the world database is reachable with the configured Config.js.
        /// </summary>
        public static void RequireWorldDatabase(WorldDatabase worldDb)
        {
            InitializeConfig();
            RequireDatabase("world", () => worldDb.Exists(false));
        }

        /// <summary>
        /// Set to 1 by CI jobs that load a world database (build-test.yml, content-apply-ci.yml). With it set,
        /// <see cref="RequireWorldDatabaseOrFailInCi"/> FAILS instead of skipping, so a broken connection can never
        /// turn a blocking test into a quiet Inconclusive.
        /// </summary>
        public const string RequireWorldDatabaseVariable = "ACE_REQUIRE_WORLD_DB";

        public static bool WorldDatabaseRequired => Environment.GetEnvironmentVariable(RequireWorldDatabaseVariable) == "1";

        /// <summary>
        /// <see cref="RequireWorldDatabase"/>, except that when <see cref="RequireWorldDatabaseVariable"/> is 1 an
        /// unreachable database (or a missing Config.js) fails the test rather than skipping it.
        /// </summary>
        public static void RequireWorldDatabaseOrFailInCi(WorldDatabase worldDb)
        {
            try
            {
                RequireWorldDatabase(worldDb);
            }
            catch (AssertInconclusiveException ex) when (WorldDatabaseRequired)
            {
                Assert.Fail($"{RequireWorldDatabaseVariable}=1 but the world database is unavailable: {ex.Message}");
            }
        }

        private static void RequireDatabase(string name, Func<bool> probe)
        {
            bool reachable;

            try
            {
                reachable = probe();
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive($"Skipped: the {name} database is not reachable with the configured Config.js.");
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works
        /// regardless of the bin\Debug vs bin\x64\Debug output layout.
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}

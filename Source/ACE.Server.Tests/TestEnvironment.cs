using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Locates a Config.js for tests, and gates database-dependent tests so they skip
    /// (Assert.Inconclusive) instead of failing or hanging when no MySQL instance is reachable.
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
                    ?? Path.Combine(AppContext.BaseDirectory, "Config.js.example");

                File.Copy(source, configPath, true);
            }

            ConfigManager.Initialize(configPath);
            configInitialized = true;
        }

        /// <summary>
        /// Skips the calling test unless all three databases are reachable with the configured Config.js.
        /// DatabaseManager.Initialize retries unreachable databases forever, so callers must gate on this
        /// before touching it.
        /// </summary>
        public static void RequireDatabases()
        {
            InitializeConfig();

            RequireDatabase("authentication", () => new AuthenticationDatabase().Exists(false));
            RequireDatabase("shard", () => new ShardDatabase().Exists(false));
            RequireDatabase("world", () => new WorldDatabase().Exists(false));
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

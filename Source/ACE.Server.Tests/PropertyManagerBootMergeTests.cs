using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PR #1451 gave Initialize's one-time boot load the SAME merge DoWork's live reload uses, including
    /// its ShouldSkipReload guard. That guard is correct for the live reload (a still-Modified entry there
    /// is a local change the DB does not have yet) and wrong at boot, where every entry is Modified only
    /// because LoadDefaultProperties just called Modify* for every code default - so a persisted
    /// config_properties_* row was skipped and discarded for every key that has a code default, and the
    /// code default is what the server ran.
    ///
    /// These tests pin the two paths apart: the boot merge APPLIES a DB row over a Modified (default-loaded)
    /// entry, the live reload still SKIPS one. The source-text guard at the bottom pins the call sites, so a
    /// future edit that passes the live-reload flag from Initialize (which is exactly the regression, and is
    /// invisible to the behavioural tests because they call the helper directly) fails loudly.
    /// </summary>
    [TestClass]
    public class PropertyManagerBootMergeTests
    {
        private static ConcurrentDictionary<string, ConfigurationEntry<double>> NewCache()
        {
            return new ConcurrentDictionary<string, ConfigurationEntry<double>>();
        }

        /// <summary>
        /// The exact boot state PR #1451 got wrong: LoadDefaultProperties has just run ModifyDouble for
        /// this key, so the cache holds the CODE DEFAULT and the entry is Modified for that reason alone,
        /// and the shard has a persisted row an operator tuned to something else. The row must win.
        /// </summary>
        [TestMethod]
        public void BootMerge_AppliesThePersistedRowOverACodeDefault()
        {
            var cache = NewCache();
            var entry = new ConfigurationEntry<double>(true, 0.115, "code default");
            cache["class_ability_empoweredsummons_percent_per_rank"] = entry;

            var reloaded = new List<(string Key, double Value, string Description)>
            {
                ("class_ability_empoweredsummons_percent_per_rank", 0.0833, "from-db")
            };

            PropertyManager.MergeReloadedInPlace(cache, reloaded, applyOverModified: true);

            Assert.AreEqual(0.0833, cache["class_ability_empoweredsummons_percent_per_rank"].Item,
                "the persisted config_properties_double row must win over the code default at boot.");
            Assert.AreEqual("from-db", cache["class_ability_empoweredsummons_percent_per_rank"].Description);
            Assert.AreSame(entry, cache["class_ability_empoweredsummons_percent_per_rank"],
                "the entry object must still be mutated in place, never replaced.");
        }

        /// <summary>
        /// Companion to the test above, and the reason the boot merge needed its own flag rather than a
        /// loosening of the shared helper: DoWork's LIVE reload must still skip a Modified entry, because
        /// there Modified means an admin's /modifydouble that has not reached the DB yet.
        /// </summary>
        [TestMethod]
        public void LiveReloadMerge_StillSkipsAModifiedEntry()
        {
            var cache = NewCache();
            cache["class_ability_empoweredsummons_percent_per_rank"] = new ConfigurationEntry<double>(false, 0.115, "d0");

            // An admin just changed it; the write has not happened yet.
            cache["class_ability_empoweredsummons_percent_per_rank"].Modify(0.25);

            var reloaded = new List<(string Key, double Value, string Description)>
            {
                ("class_ability_empoweredsummons_percent_per_rank", 0.0833, "from-db")
            };

            PropertyManager.MergeReloadedInPlace(cache, reloaded);

            Assert.AreEqual(0.25, cache["class_ability_empoweredsummons_percent_per_rank"].Item,
                "the live reload must never apply a DB row over an unwritten local change.");
            Assert.IsTrue(cache["class_ability_empoweredsummons_percent_per_rank"].Modified,
                "still pending - the live reload must leave it to be retried.");
        }

        /// <summary>
        /// After the boot merge takes a row, that key is no longer pending, so DoWork's first flush (five
        /// minutes in) must not pick it up and write the code default back over the operator's row. This is
        /// the data-loss half of the regression, not just the wrong-value-at-runtime half.
        /// </summary>
        [TestMethod]
        public void BootMerge_ClearsModified_SoTheFirstFlushDoesNotRewriteTheRow()
        {
            var cache = NewCache();
            cache["k"] = new ConfigurationEntry<double>(true, 0.115, "code default");

            var reloaded = new List<(string Key, double Value, string Description)> { ("k", 0.0833, "from-db") };
            PropertyManager.MergeReloadedInPlace(cache, reloaded, applyOverModified: true);

            Assert.IsFalse(cache["k"].Modified, "a key whose row was just applied is not pending any more.");

            var snapshot = PropertyManager.SnapshotModified(cache);
            Assert.AreEqual(0, snapshot.Count,
                "DoWork's first flush must not re-write this key; it would write the code default over the operator's row.");
        }

        /// <summary>
        /// The other half of pre-#1451 behaviour, which the release-check doctrine and all the committed
        /// configdefaults migrations assume: a code default with NO row stays Modified, so the first flush
        /// seeds it as a row. "No row" is the only case where a new code default applies by itself.
        /// </summary>
        [TestMethod]
        public void BootMerge_LeavesADefaultWithNoRowPending_SoItGetsSeeded()
        {
            var cache = NewCache();
            cache["key_with_no_row"] = new ConfigurationEntry<double>(true, 0.115, "code default");

            // The DB returned rows, just not one for this key.
            var reloaded = new List<(string Key, double Value, string Description)> { ("some_other_key", 1.0, "from-db") };
            PropertyManager.MergeReloadedInPlace(cache, reloaded, applyOverModified: true);

            Assert.AreEqual(0.115, cache["key_with_no_row"].Item, "with no row, the code default stands.");
            Assert.IsTrue(cache["key_with_no_row"].Modified, "with no row, the default must stay pending so the first flush seeds it.");

            var snapshot = PropertyManager.SnapshotModified(cache);
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual("key_with_no_row", snapshot[0].Key);
        }

        /// <summary>
        /// Same repo-root walk PropertyManagerConfigSyncSourceInvariantTests uses - never redirect test
        /// output out of the repo with --artifacts-path, or this walk fails.
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

        /// <summary>
        /// The brace-matched body of the method whose declaration text starts with <paramref name="signatureStart"/>.
        /// </summary>
        private static string ExtractMethodBody(string source, string signatureStart)
        {
            var signatureIndex = source.IndexOf(signatureStart, StringComparison.Ordinal);
            Assert.IsTrue(signatureIndex >= 0, $"Could not find '{signatureStart}' in PropertyManager.cs - has it been renamed?");

            var openBraceIndex = source.IndexOf('{', signatureIndex + signatureStart.Length);
            Assert.IsTrue(openBraceIndex >= 0, $"'{signatureStart}' has no following open brace - malformed source.");

            var depth = 0;

            for (var i = openBraceIndex; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
                }
            }

            Assert.Fail($"'{signatureStart}' never reaches a matching closing brace - malformed source.");
            return null;
        }

        private static List<string> MergeCallLines(string methodBody)
        {
            return methodBody
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Contains("MergeReloadedInPlace(") && !line.StartsWith("//"))
                .ToList();
        }

        [TestMethod]
        public void Initialize_MergesWithTheBootFlag_AndDoWork_DoesNot()
        {
            var sourcePath = FindInSourceTree("Source/ACE.Server/Managers/PropertyManager.cs");
            Assert.IsNotNull(sourcePath, "Could not find Source/ACE.Server/Managers/PropertyManager.cs by walking up from " + AppContext.BaseDirectory);

            var source = File.ReadAllText(sourcePath);

            var initializeBody = ExtractMethodBody(source, "public static void Initialize(bool loadDefaultValues");
            var doWorkBody = ExtractMethodBody(source, "private static void DoWork(Object source");

            var initializeCalls = MergeCallLines(initializeBody);
            var doWorkCalls = MergeCallLines(doWorkBody);

            Assert.AreEqual(4, initializeCalls.Count, "Initialize should merge exactly the four caches (bool/long/double/string). Found:\n" + string.Join("\n", initializeCalls));
            Assert.AreEqual(4, doWorkCalls.Count, "DoWork should merge exactly the four caches (bool/long/double/string). Found:\n" + string.Join("\n", doWorkCalls));

            foreach (var call in initializeCalls)
            {
                Assert.IsTrue(call.Contains("applyOverModified: true"),
                    "Initialize's boot merge must pass applyOverModified: true, or a persisted config_properties_* row " +
                    "is skipped for every key LoadDefaultProperties just marked Modified (that is PR #1451's precedence " +
                    "regression). Offending call: " + call);
            }

            foreach (var call in doWorkCalls)
            {
                Assert.IsFalse(call.Contains("applyOverModified: true"),
                    "DoWork's live reload must NOT pass applyOverModified: true - a still-Modified entry there is a local " +
                    "value the DB does not have yet, and applying the DB row over it would silently drop that write with " +
                    "nothing left to retry it. Offending call: " + call);
            }
        }
    }
}

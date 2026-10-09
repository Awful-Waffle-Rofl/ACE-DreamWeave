using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A fourth code-review round on PR #1451 found Initialize's own DB load still running under
    /// ConfigSync (a boot-order argument, not the invariant), after DoWork was already fixed to keep all
    /// DB I/O out of the lock. This is a cheap, DB-free guard against that shape recurring anywhere else in
    /// the file: it scans PropertyManager.cs's own source text for every "lock (ConfigSync)" block and
    /// fails if any of them reaches DatabaseManager.ShardConfig - the one call surface every DB read/write
    /// in this file goes through. It cannot prove no I/O happens under the lock in general (a future
    /// helper method called from inside a lock block, that itself calls ShardConfig two levels down, would
    /// not be caught by this text scan), but it does directly catch the exact regression both round three
    /// and round four found: DB I/O written directly inside a "lock (ConfigSync) { ... }" body.
    /// </summary>
    [TestClass]
    public class PropertyManagerConfigSyncSourceInvariantTests
    {
        [TestMethod]
        public void NoConfigSyncLockBlock_CallsShardConfigDirectly()
        {
            var sourcePath = FindInSourceTree("Source/ACE.Server/Managers/PropertyManager.cs");
            Assert.IsNotNull(sourcePath, "Could not find Source/ACE.Server/Managers/PropertyManager.cs by walking up from " + AppContext.BaseDirectory);

            var source = File.ReadAllText(sourcePath);
            var lockBlocks = ExtractLockConfigSyncBlockBodies(source);

            Assert.IsTrue(lockBlocks.Count > 0, "Found no 'lock (ConfigSync)' blocks at all - has ConfigSync been renamed or removed?");

            foreach (var (blockIndex, body) in lockBlocks)
            {
                Assert.IsFalse(body.Contains("DatabaseManager.ShardConfig"),
                    $"lock (ConfigSync) block #{blockIndex} in PropertyManager.cs calls DatabaseManager.ShardConfig directly - " +
                    $"this violates the no-DB-I/O-under-ConfigSync invariant documented on the field itself. Body:\n{body}");
            }
        }

        /// <summary>
        /// Finds every "lock (ConfigSync)" in the source text and returns each one's brace-matched block
        /// body (the text between the '{' immediately following it and its matching '}').
        /// </summary>
        private static List<(int Index, string Body)> ExtractLockConfigSyncBlockBodies(string source)
        {
            const string needle = "lock (ConfigSync)";
            var results = new List<(int Index, string Body)>();

            var searchFrom = 0;
            var blockIndex = 0;

            while (true)
            {
                var needleIndex = source.IndexOf(needle, searchFrom, StringComparison.Ordinal);
                if (needleIndex < 0)
                    break;

                blockIndex++;

                var openBraceIndex = source.IndexOf('{', needleIndex + needle.Length);
                Assert.IsTrue(openBraceIndex >= 0, $"lock (ConfigSync) at offset {needleIndex} has no following '{{' - malformed source or the pattern changed.");

                var depth = 0;
                var closeBraceIndex = -1;

                for (var i = openBraceIndex; i < source.Length; i++)
                {
                    if (source[i] == '{')
                        depth++;
                    else if (source[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBraceIndex = i;
                            break;
                        }
                    }
                }

                Assert.IsTrue(closeBraceIndex >= 0, $"lock (ConfigSync) at offset {needleIndex} never reaches a matching closing brace - malformed source.");

                var body = source.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);
                results.Add((blockIndex, body));

                searchFrom = closeBraceIndex + 1;
            }

            return results;
        }

        /// <summary>
        /// Same repo-root walk TestEnvironment.cs uses internally (not reused directly - that helper is
        /// private) - never redirect test output out of the repo with --artifacts-path, or this walk fails.
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

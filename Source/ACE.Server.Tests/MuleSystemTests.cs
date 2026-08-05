using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule system (WaffleACE) guards.
    ///
    /// The load-bearing test here is <see cref="EveryMuleActionHasAGuardSite"/>. A MuleAction member with no
    /// guard call site is not a compile error and not a runtime error - it is a restriction that silently
    /// does not exist. Since a mule is irreversible, an unenforced restriction cannot be repaired by
    /// un-muling the character, so the gate has to be a test.
    ///
    /// The eligibility tests exercise Player.IsEligibleForMule, the pure primitives-in / reason-out form the
    /// rules were extracted into precisely so they can be tested without constructing a Player (whose static
    /// type initializer needs a live world database). Same extraction rationale as Player.CalcPayoutStackSizes
    /// in BankTests. No database, no world.
    /// </summary>
    [TestClass]
    public class MuleSystemTests
    {
        private const string ServerProjectRelativePath = "Source/ACE.Server";

        /// <summary>
        /// Files that define the system itself, and so do not count as guard sites.
        /// </summary>
        private static readonly HashSet<string> ExcludedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Player_Mule.cs",
            "MuleAction.cs",
        };

        [TestMethod]
        public void EveryMuleActionHasAGuardSite()
        {
            var serverDir = FindDirectoryInSourceTree(ServerProjectRelativePath);

            Assert.IsNotNull(serverDir, $"Could not find {ServerProjectRelativePath} by walking up from {AppContext.BaseDirectory}.");

            var sources = Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !ExcludedFileNames.Contains(Path.GetFileName(f)))
                .ToList();

            Assert.IsTrue(sources.Count > 100, $"Only found {sources.Count} .cs files under {serverDir} - the scan would pass vacuously.");

            var text = string.Join("\n", sources.Select(File.ReadAllText));

            var unguarded = new List<string>();

            foreach (var action in Enum.GetNames(typeof(MuleAction)))
            {
                if (!text.Contains("MuleAction." + action, StringComparison.Ordinal))
                    unguarded.Add(action);
            }

            Assert.AreEqual(0, unguarded.Count,
                $"MuleAction member(s) with no guard site anywhere in {ServerProjectRelativePath} outside " +
                $"{string.Join(" / ", ExcludedFileNames)}: {string.Join(", ", unguarded)}. " +
                "Every member must be referenced by at least one 'if (MuleBlocked(MuleAction.X)) return;' guard, " +
                "or the restriction it names is not enforced.");
        }

        [TestMethod]
        public void Eligibility_HappyPathPasses()
        {
            var eligible = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: false, level: 8, maxConvertLevel: 50,
                existingAccountMules: 0, maxPerAccount: 2,
                out var reason);

            Assert.IsTrue(eligible, reason);
            Assert.IsNull(reason);
        }

        /// <summary>
        /// Regression guard for the eligibility rules that were removed. A character is level 8+ the moment it
        /// leaves the training hall, has already earned experience, and already holds quest registry rows (its
        /// own training-hall stamp, plus the LedgerSeededMarker that Player_QuestStamps writes on first login
        /// for EVERY character). Earlier revisions gated on all three, which made conversion unreachable for
        /// any character that had ever logged in. Only level may gate.
        /// </summary>
        [TestMethod]
        public void Eligibility_TrainingHallGraduateIsStillEligible()
        {
            for (var level = 1; level <= 50; level++)
            {
                var eligible = Player.IsEligibleForMule(
                    systemEnabled: true, alreadyMule: false, level: level, maxConvertLevel: 50,
                    existingAccountMules: 0, maxPerAccount: 2,
                    out var reason);

                Assert.IsTrue(eligible, $"level {level} is inside the cap and must be eligible, got: {reason}");
            }
        }

        [TestMethod]
        public void Eligibility_RefusedWhenSystemDisabled()
        {
            var eligible = Player.IsEligibleForMule(
                systemEnabled: false, alreadyMule: false, level: 8, maxConvertLevel: 50,
                existingAccountMules: 0, maxPerAccount: 2,
                out var reason);

            Assert.IsFalse(eligible);
            Assert.IsNotNull(reason);
        }

        [TestMethod]
        public void Eligibility_RefusedWhenAlreadyAMule()
        {
            var eligible = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: true, level: 8, maxConvertLevel: 50,
                existingAccountMules: 0, maxPerAccount: 2,
                out var reason);

            Assert.IsFalse(eligible);
            StringAssert.Contains(reason, "already a mule");
        }

        [TestMethod]
        public void Eligibility_LevelCapIsInclusiveAndRefusesOneAbove()
        {
            var atCap = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: false, level: 50, maxConvertLevel: 50,
                existingAccountMules: 0, maxPerAccount: 2,
                out var atCapReason);

            Assert.IsTrue(atCap, $"level 50 against a cap of 50 must be eligible, got: {atCapReason}");

            var aboveCap = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: false, level: 51, maxConvertLevel: 50,
                existingAccountMules: 0, maxPerAccount: 2,
                out var aboveCapReason);

            Assert.IsFalse(aboveCap);
            StringAssert.Contains(aboveCapReason, "level 50 or below");
            StringAssert.Contains(aboveCapReason, "level 51");
        }

        [TestMethod]
        public void Eligibility_LevelCapFollowsConfig()
        {
            var eligible = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: false, level: 120, maxConvertLevel: 150,
                existingAccountMules: 0, maxPerAccount: 2,
                out var reason);

            Assert.IsTrue(eligible, $"the cap is config-driven, not the hardcoded 50, got: {reason}");
        }

        [TestMethod]
        public void Eligibility_RefusedAtTheAccountCap()
        {
            var atCap = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: false, level: 8, maxConvertLevel: 50,
                existingAccountMules: 2, maxPerAccount: 2,
                out var atCapReason);

            Assert.IsFalse(atCap);
            StringAssert.Contains(atCapReason, "limit is 2");

            var underCap = Player.IsEligibleForMule(
                systemEnabled: true, alreadyMule: false, level: 8, maxConvertLevel: 50,
                existingAccountMules: 1, maxPerAccount: 2,
                out _);

            Assert.IsTrue(underCap);
        }

        [TestMethod]
        public void Eligibility_RefusedWhenAccountCapIsZeroOrNegative()
        {
            foreach (var cap in new long[] { 0, -1 })
            {
                var eligible = Player.IsEligibleForMule(
                    systemEnabled: true, alreadyMule: false, level: 8, maxConvertLevel: 50,
                    existingAccountMules: 0, maxPerAccount: cap,
                    out var reason);

                Assert.IsFalse(eligible, $"maxPerAccount {cap} must refuse conversion outright.");
                Assert.IsNotNull(reason);
            }
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath as a DIRECTORY, so the lookup works
        /// regardless of the bin\Debug vs bin\x64\Debug output layout. Same idiom as
        /// PropertyRegistryTests.FindInSourceTree, which looks for a file.
        /// </summary>
        private static string FindDirectoryInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}

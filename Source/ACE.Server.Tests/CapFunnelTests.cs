using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// THE CAP FUNNEL GUARD (Class Ability Point audit ledger, round 2).
    ///
    /// Two prod characters hold fewer AvailableClassAbilityPoints than the ledger identity allows and
    /// the losing event is unrecoverable, because before round 2 every CAP write site poked the two
    /// biota properties directly and nothing recorded the change. The fix makes both properties
    /// GET-ONLY so every mutation has to go through Player.AdjustClassAbilityPoints, which appends a
    /// `character_cap_ledger` row.
    ///
    /// A clean build is the primary enforcement of that - a re-added assignment does not compile. This
    /// test covers the case a compiler cannot: someone re-adding a setter (or a second raw setter) to
    /// the properties and then assigning to them from elsewhere, which would compile perfectly and
    /// silently reopen the hole. So the assertion is stated over the SOURCE TEXT of the whole server
    /// project: an assignment to either counter may appear ONLY in Player_ClassAbilities.cs, the file
    /// that owns the funnel.
    ///
    /// The two `=>` expression-bodied getters in that file match the regex and are expected to; they
    /// are inside the permitted file, and pinning the exact expected count there would just make this
    /// test brittle against a comment reflow.
    /// </summary>
    [TestClass]
    public class CapFunnelTests
    {
        /// <summary>The one file allowed to contain an assignment to either counter.</summary>
        private const string FunnelFile = "Player_ClassAbilities.cs";

        /// <summary>
        /// `=(?!=)` rather than a bare `=`, following AccountVaultDaoShapeTests' read-modify-write
        /// regex: a bare `=` also matches the `==` in every "you have N point(s)" pluralisation
        /// comparison (there are several, in ClassAbilityTrainer.cs and Player_ClassAbilityTokens.cs),
        /// which are reads, not writes. `>=`, `<=` and `!=` never match, because the character
        /// immediately after the identifier's trailing whitespace is the `>`, `&lt;` or `!`.
        /// </summary>
        private static readonly string[] AssignmentPatterns =
        {
            @"AvailableClassAbilityPoints\s*(=(?!=)|\+=|-=)",
            @"TotalClassAbilityPointsEarned\s*(=(?!=)|\+=|-=)",
        };

        /// <summary>
        /// Same parent-directory walk-up as CapLedgerSchemaTests.RepoRoot, for the same reason: the
        /// test assembly's location is the only anchor available and the repo root is found by looking
        /// for a directory that must exist in it. Never run these with --artifacts-path; moving the
        /// assembly out of the tree breaks the walk.
        /// </summary>
        private static string ServerSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server");
        }

        /// <summary>
        /// Every hand-written .cs in the server project. bin/ and obj/ are excluded: obj/ holds
        /// generated sources (and, after a build, a copy of nothing relevant), and a generated file
        /// tripping this assertion would be noise.
        /// </summary>
        private static List<string> ServerSourceFiles()
        {
            var root = ServerSourceRoot();

            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .ToList();

            Assert.IsTrue(files.Count > 100, $"only {files.Count} source files found under {root} - the sweep is not actually reading the server project");

            return files;
        }

        [TestMethod]
        public void EveryCapAssignment_LivesOnlyInTheFunnelFile()
        {
            var offenders = new List<string>();

            foreach (var path in ServerSourceFiles())
            {
                if (string.Equals(Path.GetFileName(path), FunnelFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Normalised to \n: most of the tree is CRLF while the index is LF, and a test that
                // depended on which one it got would pass or fail by checkout setting.
                var text = File.ReadAllText(path).Replace("\r\n", "\n");
                var lines = text.Split('\n');

                foreach (var pattern in AssignmentPatterns)
                {
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (Regex.IsMatch(lines[i], pattern))
                            offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }

            Assert.AreEqual(0, offenders.Count,
                "AvailableClassAbilityPoints / TotalClassAbilityPointsEarned may only be assigned inside " +
                $"{FunnelFile}, through AdjustClassAbilityPoints, so the change reaches character_cap_ledger. " +
                $"Found {offenders.Count} assignment(s) elsewhere:{Environment.NewLine}" +
                string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// The funnel file must actually still contain the funnel. Without this, deleting
        /// AdjustClassAbilityPoints outright would leave the test above passing over a codebase that
        /// records nothing - a green light for the exact state round 2 exists to end.
        /// </summary>
        [TestMethod]
        public void FunnelFile_StillDeclaresTheMutatorAndReadOnlyProperties()
        {
            var path = Path.Combine(ServerSourceRoot(), "WorldObjects", FunnelFile);

            Assert.IsTrue(File.Exists(path), $"{path} is missing");

            var src = File.ReadAllText(path).Replace("\r\n", "\n");

            StringAssert.Contains(src, "private void AdjustClassAbilityPoints(",
                "the one mutator every CAP write site funnels through is gone");

            StringAssert.Contains(src, "DatabaseManager.Shard.AddCapLedgerRow(",
                "AdjustClassAbilityPoints no longer enqueues a ledger row, so the funnel records nothing");

            StringAssert.Contains(src, "public int AvailableClassAbilityPoints => ",
                "AvailableClassAbilityPoints must stay an expression-bodied get-only property - a setter reopens the hole");

            StringAssert.Contains(src, "public int TotalClassAbilityPointsEarned => ",
                "TotalClassAbilityPointsEarned must stay an expression-bodied get-only property - a setter reopens the hole");
        }
    }
}

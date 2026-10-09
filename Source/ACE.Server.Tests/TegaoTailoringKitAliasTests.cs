using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers Tailoring.ResolveKitWcid / Tailoring.IsTailoringKit for the Tegao Wavecounter (wcid
    /// 1004130) cosmetic clones of the retail tailoring kits. Without the alias resolution these
    /// clones dispatch to nothing in Tailoring.DoTailoring and silently do nothing when used.
    ///
    /// Only the pure/static parts are exercised - ACE.Server.Tests cannot construct a live Player.
    /// </summary>
    [TestClass]
    public class TegaoTailoringKitAliasTests
    {
        // Clone wcid -> expected retail kit wcid, mirrored from Tailoring.KitAliases.
        private static readonly Dictionary<uint, uint> ExpectedAliases = new Dictionary<uint, uint>
        {
            { 1004312, Tailoring.ArmorLayeringToolTop },
            { 1004313, Tailoring.ArmorLayeringToolBottom },
            { 1004314, Tailoring.WeaponTailoringKit },
            { 1004315, Tailoring.ArmorTailoringKit },
            { 1004316, Tailoring.ArmorMainReductionTool },
            { 1004317, Tailoring.ArmorMiddleReductionTool },
            { 1004318, Tailoring.ArmorLowerReductionTool },
        };

        [TestMethod]
        public void ResolveKitWcid_ResolvesEachCloneToItsRetailKit()
        {
            foreach (var (cloneWcid, retailWcid) in ExpectedAliases)
            {
                Assert.AreEqual(retailWcid, Tailoring.ResolveKitWcid(cloneWcid),
                    $"clone wcid {cloneWcid} should resolve to retail kit wcid {retailWcid}");
            }
        }

        [TestMethod]
        public void IsTailoringKit_ReturnsTrueForEveryCloneWcid()
        {
            foreach (var cloneWcid in ExpectedAliases.Keys)
            {
                Assert.IsTrue(Tailoring.IsTailoringKit(cloneWcid),
                    $"clone wcid {cloneWcid} should be recognized as a tailoring kit");
            }
        }

        [TestMethod]
        public void ResolveKitWcid_And_IsTailoringKit_LeaveNonKitWcidUnaffected()
        {
            const uint nonKitWcid = 0;

            Assert.AreEqual(nonKitWcid, Tailoring.ResolveKitWcid(nonKitWcid));
            Assert.IsFalse(Tailoring.IsTailoringKit(nonKitWcid));

            const uint arbitraryNonKitWcid = 123456789;

            Assert.AreEqual(arbitraryNonKitWcid, Tailoring.ResolveKitWcid(arbitraryNonKitWcid));
            Assert.IsFalse(Tailoring.IsTailoringKit(arbitraryNonKitWcid));
        }

        [TestMethod]
        public void CloneSqlFiles_HeaderCloneOfWcidMatchesAliasTable()
        {
            var contentDir = FindRepoContentDir();
            if (contentDir == null)
            {
                Assert.Inconclusive("Could not locate the repo's Content/ directory by walking up from the test assembly -- skipping live-tree check.");
                return;
            }

            var weeniesDir = Path.Combine(contentDir, "sql", "weenies");
            if (!Directory.Exists(weeniesDir))
            {
                Assert.Inconclusive($"Found Content/ at '{contentDir}' but it held no sql/weenies directory -- skipping.");
                return;
            }

            foreach (var (cloneWcid, retailWcid) in ExpectedAliases)
            {
                var matches = Directory.EnumerateFiles(weeniesDir, $"{cloneWcid} *.sql", SearchOption.TopDirectoryOnly).ToList();

                if (matches.Count == 0)
                {
                    Assert.Inconclusive($"No sql file found for clone wcid {cloneWcid} under '{weeniesDir}' -- skipping.");
                    return;
                }

                var headerWcid = ReadCloneOfWcid(matches[0]);

                Assert.IsNotNull(headerWcid,
                    $"'{matches[0]}' has no 'clone of wcid N' header line to verify against the alias table.");

                Assert.AreEqual(retailWcid, headerWcid.Value,
                    $"'{matches[0]}' header says clone of wcid {headerWcid.Value}, but Tailoring.KitAliases maps {cloneWcid} -> {retailWcid}");
            }
        }

        /// <summary>
        /// Pulls the retail wcid out of a "* clone of wcid N (...)" header comment line.
        /// </summary>
        private static uint? ReadCloneOfWcid(string filePath)
        {
            const string marker = "clone of wcid ";

            foreach (var line in File.ReadLines(filePath))
            {
                var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;

                var rest = line.Substring(idx + marker.Length);

                var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());

                if (digits.Length > 0 && uint.TryParse(digits, out var wcid))
                    return wcid;
            }

            return null;
        }

        /// <summary>
        /// Walks up from the test assembly location looking for a sibling "Content" directory that actually
        /// holds .sql files. Robust to the bin/x64/Debug/netX nesting and to running inside a git worktree.
        /// </summary>
        private static string FindRepoContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content");
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "*.sql", SearchOption.AllDirectories).Any())
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}

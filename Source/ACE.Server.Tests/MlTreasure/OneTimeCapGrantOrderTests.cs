using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// A source-text guard over Gem.UseGem's class-ability-point branch, pinning the ONE thing about the
    /// one-time claim that no unit test over pure functions can reach: the ORDER of the three calls.
    ///
    /// The claim check must run before the points are paid, and the claim must be stamped only after they
    /// have been. Stamping first would burn a character's single lifetime claim on a grant that then
    /// failed (Player.GrantClassAbilityPoints returns false for a mule), leaving them with the ledger row
    /// and no point and no way to ever get one. Checking after would pay the point before deciding whether
    /// the character was allowed to have it.
    ///
    /// Comments are stripped before the search, following CapAuditTests: a source-order test that reads
    /// method names would otherwise be satisfied by a comment mentioning them in the right order.
    /// </summary>
    [TestClass]
    public class OneTimeCapGrantOrderTests
    {
        private static string GemSourcePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Gem.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Gem.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Gem.cs");
        }

        private static string StripComments(string source)
        {
            var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlockComments, @"//[^\n]*", "");
        }

        [TestMethod]
        public void UseGem_ChecksTheClaimBeforeGrantingAndStampsAfter()
        {
            var src = StripComments(File.ReadAllText(GemSourcePath()));

            var begin = src.IndexOf("TryBeginOneTimeClassAbilityGrant", StringComparison.Ordinal);
            var grant = src.IndexOf("GrantClassAbilityPoints(", StringComparison.Ordinal);
            var complete = src.IndexOf("CompleteOneTimeClassAbilityGrant", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, begin, "Gem.UseGem must gate a one-time CAP grant through Player.TryBeginOneTimeClassAbilityGrant");
            Assert.AreNotEqual(-1, grant, "Gem.UseGem must pay class ability points through Player.GrantClassAbilityPoints");
            Assert.AreNotEqual(-1, complete, "Gem.UseGem must record the claim through Player.CompleteOneTimeClassAbilityGrant");

            // Each appears exactly once, so the ordering below cannot be satisfied by a second occurrence
            // somewhere else in the file.
            Assert.AreEqual(1, Regex.Matches(src, "TryBeginOneTimeClassAbilityGrant").Count);
            Assert.AreEqual(1, Regex.Matches(src, Regex.Escape("GrantClassAbilityPoints(")).Count);
            Assert.AreEqual(1, Regex.Matches(src, "CompleteOneTimeClassAbilityGrant").Count);

            Assert.IsTrue(begin < grant,
                "the claim check must run BEFORE GrantClassAbilityPoints - otherwise the point is paid before deciding whether this character may have it");

            Assert.IsTrue(grant < complete,
                "the claim must be stamped AFTER GrantClassAbilityPoints returns true - otherwise a refused grant burns the character's single lifetime claim");
        }

        /// <summary>
        /// The one-time gate is OPT-IN by PropertyString.ClassAbilityGrantQuest. Every CAP consumable
        /// shipped before it - the Proving Grounds Commendation (1000304) and the Meridian Commendation
        /// (1001407) - carries no such property and must stay repeatable, so the guard may never be
        /// reached without that read.
        /// </summary>
        [TestMethod]
        public void UseGem_OneTimeGateIsOptInByProperty()
        {
            var src = StripComments(File.ReadAllText(GemSourcePath()));

            var propertyRead = src.IndexOf("PropertyString.ClassAbilityGrantQuest", StringComparison.Ordinal);
            var begin = src.IndexOf("TryBeginOneTimeClassAbilityGrant", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, propertyRead, "the one-time gate must be keyed on PropertyString.ClassAbilityGrantQuest, not on a wcid");
            Assert.IsTrue(propertyRead < begin, "the property read must precede the guard it opts into");
        }
    }
}

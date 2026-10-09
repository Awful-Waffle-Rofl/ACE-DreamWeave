using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A source-text guard over Gem.UseGem's UseCreateItem branch, pinning the ONE thing about the
    /// one-time payout gate that no unit test over pure functions can reach: the ORDER of the three
    /// calls. Modeled directly on ACE.Server.Tests.MlTreasure.OneTimeCapGrantOrderTests, which pins the
    /// sibling CAP grant's ordering the same way.
    ///
    /// The claim check must run before HandleUseCreateItem creates the payout, and the claim must be
    /// stamped only after HandleUseCreateItem returns true. Stamping first would burn a character's one
    /// payout on a creation that then failed (full pack, out of inventory/container slots), leaving them
    /// with the ledger row and no notes and no way to ever get them. Checking after would create the
    /// notes before deciding whether the character was allowed to have them.
    ///
    /// Comments are stripped before the search, following OneTimeCapGrantOrderTests: a source-order test
    /// that reads method names would otherwise be satisfied by a comment mentioning them in the right
    /// order.
    /// </summary>
    [TestClass]
    public class OneTimeItemGrantOrderTests
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
        public void UseGem_ChecksTheClaimBeforeCreatingAndStampsAfter()
        {
            var src = StripComments(File.ReadAllText(GemSourcePath()));

            var begin = src.IndexOf("TryBeginOneTimeItemGrant", StringComparison.Ordinal);
            // The call site, not the method's own definition (which reads "HandleUseCreateItem(Player player)").
            var create = src.IndexOf("HandleUseCreateItem(player)", StringComparison.Ordinal);
            var complete = src.IndexOf("CompleteOneTimeItemGrant", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, begin, "Gem.UseGem must gate the one-time payout through Player.TryBeginOneTimeItemGrant");
            Assert.AreNotEqual(-1, create, "Gem.UseGem must call HandleUseCreateItem(player) to create the payout");
            Assert.AreNotEqual(-1, complete, "Gem.UseGem must record the claim through Player.CompleteOneTimeItemGrant");

            // Each appears exactly once, so the ordering below cannot be satisfied by a second occurrence
            // somewhere else in the file.
            Assert.AreEqual(1, Regex.Matches(src, "TryBeginOneTimeItemGrant").Count);
            Assert.AreEqual(1, Regex.Matches(src, Regex.Escape("HandleUseCreateItem(player)")).Count);
            Assert.AreEqual(1, Regex.Matches(src, "CompleteOneTimeItemGrant").Count);

            Assert.IsTrue(begin < create,
                "the claim check must run BEFORE HandleUseCreateItem - otherwise the notes are created before deciding whether this character may have them");

            Assert.IsTrue(create < complete,
                "the claim must be stamped AFTER HandleUseCreateItem succeeds - otherwise a refused creation (full pack, out of slots) burns the character's one payout");
        }

        /// <summary>
        /// The one-time gate is OPT-IN by PropertyString.UseCreateGrantQuest. A UseCreateItem gem that
        /// carries no such property (e.g. 1004132 Box of 20 MMD) must stay repeatable, so the guard may
        /// never be reached without that read.
        /// </summary>
        [TestMethod]
        public void UseGem_OneTimeGateIsOptInByProperty()
        {
            var src = StripComments(File.ReadAllText(GemSourcePath()));

            var propertyRead = src.IndexOf("PropertyString.UseCreateGrantQuest", StringComparison.Ordinal);
            var begin = src.IndexOf("TryBeginOneTimeItemGrant", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, propertyRead, "the one-time gate must be keyed on PropertyString.UseCreateGrantQuest, not on a wcid");
            Assert.IsTrue(propertyRead < begin, "the property read must precede the guard it opts into");
        }

        /// <summary>
        /// A refusal from the gate must return before HandleUseCreateItem runs - not just be positioned
        /// earlier in the file. Pins the literal `return;` immediately following the TryBeginOneTimeItemGrant
        /// refusal branch, between it and the HandleUseCreateItem call, so a future edit cannot leave the
        /// check in place but fall through into item creation regardless of its result.
        /// </summary>
        [TestMethod]
        public void UseGem_RefusalReturnsBeforeCreatingTheItem()
        {
            var src = StripComments(File.ReadAllText(GemSourcePath()));

            var begin = src.IndexOf("TryBeginOneTimeItemGrant", StringComparison.Ordinal);
            var create = src.IndexOf("HandleUseCreateItem(player)", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, begin);
            Assert.AreNotEqual(-1, create);

            var between = src.Substring(begin, create - begin);

            StringAssert.Contains(between, "return;",
                "the refusal branch guarding TryBeginOneTimeItemGrant must return before HandleUseCreateItem is ever reached");
        }
    }
}

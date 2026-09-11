using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CAP audit ledger, round 3: a source-text guard over ClassAbilityCommands.cs (/caaudit,
    /// /capadjust). Neither admin command may ever write TotalClassAbilityPointsEarned - Total is
    /// what MeetsClassAbilityTierUnlock reads to gate Tier 2/3 class-ability access
    /// (Player_ClassAbilities.cs's `TotalClassAbilityPointsEarned &lt; cspRequired` check), so a
    /// correction that raised it would silently hand out tier access along with the point.
    ///
    /// Comments are stripped before the token is searched for, following CapFunnelTests/CLAUDE.md's
    /// own warning: a source-text test that forbids a token also forbids explaining why in a comment,
    /// and this file's own doc comments (and ClassAbilityCommands.cs's) mention the property name in
    /// prose deliberately.
    /// </summary>
    [TestClass]
    public class CapAuditTests
    {
        private static string CommandsSourcePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "Command", "Handlers", "ClassAbilityCommands.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ClassAbilityCommands.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server", "Command", "Handlers", "ClassAbilityCommands.cs");
        }

        /// <summary>
        /// Strips // line comments and /* */ block comments (including XML doc comments, which are
        /// just triple-slash line comments), which is what lets this test mention the forbidden
        /// property in its own prose without the stripped source ever containing it.
        /// </summary>
        private static string StripComments(string source)
        {
            // Block comments first, then line comments. Not a general-purpose C# tokenizer - it does
            // not need to be, since ClassAbilityCommands.cs has no string literal containing "/*", "*/"
            // or "//".
            var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var noLineComments = Regex.Replace(noBlockComments, @"//[^\n]*", "");
            return noLineComments;
        }

        [TestMethod]
        public void ClassAbilityCommands_NeverWritesTotalClassAbilityPointsEarned()
        {
            var src = StripComments(File.ReadAllText(CommandsSourcePath()));

            // Matches an assignment ("Foo.TotalClassAbilityPointsEarned = ...") but not a read or an
            // equality check ("== ", or the property appearing as a bare read in an interpolation).
            var assignmentPattern = new Regex(@"TotalClassAbilityPointsEarned\s*=(?!=)");

            Assert.IsFalse(assignmentPattern.IsMatch(src),
                "ClassAbilityCommands.cs must never assign TotalClassAbilityPointsEarned - see the class header on Player.TryAdminAdjustClassAbilityPoints for why (it gates Tier 2/3 unlock).");
        }

        [TestMethod]
        public void ClassAbilityCommands_CapAdjustNeverCallsGrantClassAbilityPoints()
        {
            // GrantClassAbilityPoints raises BOTH counters - it is the wrong vehicle for a
            // points-only correction. Confirms /capadjust routes through the dedicated
            // TryAdminAdjustClassAbilityPoints method instead.
            var src = StripComments(File.ReadAllText(CommandsSourcePath()));

            var capAdjustStart = src.IndexOf("HandleCapAdjust", StringComparison.Ordinal);
            Assert.AreNotEqual(-1, capAdjustStart, "HandleCapAdjust method not found");

            var nextMethodStart = src.IndexOf("public static void", capAdjustStart + 1, StringComparison.Ordinal);
            var capAdjustBody = nextMethodStart > 0
                ? src.Substring(capAdjustStart, nextMethodStart - capAdjustStart)
                : src.Substring(capAdjustStart);

            Assert.IsFalse(capAdjustBody.Contains(".GrantClassAbilityPoints("),
                "HandleCapAdjust must not call GrantClassAbilityPoints - it raises Total alongside Available");
            StringAssert.Contains(capAdjustBody, "TryAdminAdjustClassAbilityPoints",
                "HandleCapAdjust must route through Player.TryAdminAdjustClassAbilityPoints");
        }
    }
}

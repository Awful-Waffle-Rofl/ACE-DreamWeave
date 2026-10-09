using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pin for the Lucky White Rabbit's Foot (wcid 1004136, a clone of retail 32937 -
    /// Content/sql/weenies/1004136 Lucky White Rabbit's Foot.sql) recipe dispatch fix in
    /// RecipeManager_New.GetNewRecipe.
    ///
    /// GetNewRecipe cannot be exercised directly here: it calls DatabaseManager.World.GetCachedRecipe,
    /// which is unavailable in this unit test project (no live world database). Instead this pins that
    /// the pre-switch raw-wcid guard for 1004136 exists, applies the same gate as retail's
    /// W_LUCKYRABBITSFOOT_CLASS case (MeleeWeapon + Workmanship != null), and dispatches through the
    /// same SourceToRecipe[W_LUCKYRABBITSFOOT_CLASS] lookup used by the switch - i.e. that 1004136 is
    /// wired identically to how 32937 would behave if it could reach the switch at all.
    ///
    /// Before this fix, `(WeenieClassName)source.WeenieClassId` on the switch silently truncated wcid
    /// 1004136 (it does not fit in the ushort-backed WeenieClassName enum), so no case ever matched and
    /// GetNewRecipe fell through and returned null for every target - the rabbit's foot could not be
    /// used on anything.
    ///
    /// COMMENTS CANNOT SATISFY THIS PIN. The source file is stripped of // and /* */ comments
    /// (string-literal aware) before the guard is located and searched, matching the pattern in
    /// WeaponModSuppressionWiringTests.StripComments.
    /// </summary>
    [TestClass]
    public class RabbitsFootRecipeDispatchTests
    {
        private const string RecipeManagerNew = "Source/ACE.Server/Managers/RecipeManager_New.cs";

        [TestMethod]
        public void GetNewRecipe_HasAPreSwitchGuardForTheRabbitsFootWcid()
        {
            var source = StripComments(File.ReadAllText(FindInSourceTree(RecipeManagerNew)));

            var guardAt = source.IndexOf("source.WeenieClassId == LuckyRabbitsFootWcid", StringComparison.Ordinal);
            Assert.IsTrue(guardAt >= 0,
                "RecipeManager_New.GetNewRecipe must gate on wcid 1004136 (LuckyRabbitsFootWcid) ahead of "
                + "the WeenieClassName switch, the same way it already does for 1002700 (Foolproof White "
                + "Quartz) - that switch casts the raw wcid to the ushort-backed WeenieClassName enum, "
                + "which silently truncates any fork wcid instead of matching it");

            // the guard must define LuckyRabbitsFootWcid as 1004136
            Assert.IsTrue(Squash(source).Contains(Squash("LuckyRabbitsFootWcid = 1004136")),
                "LuckyRabbitsFootWcid must be defined as wcid 1004136");

            var guardBody = BraceBodyAt(source, guardAt);

            AssertContains(guardBody, "target.WeenieType != WeenieType.MeleeWeapon", "the 1004136 guard");
            AssertContains(guardBody, "target.Workmanship == null", "the 1004136 guard");
            AssertContains(guardBody, "SourceToRecipe[WeenieClassName.W_LUCKYRABBITSFOOT_CLASS]", "the 1004136 guard");
        }

        // ================= helpers (pattern: WeaponModSuppressionWiringTests) =================

        private static void AssertContains(string body, string expected, string where)
        {
            Assert.IsTrue(Squash(body).Contains(Squash(expected), StringComparison.Ordinal),
                $"{where}: expected live code (comments stripped) containing:\n  {expected}");
        }

        /// <summary>
        /// The brace-matched block that contains the given index - walks backward from the index to the
        /// nearest preceding '{' at the same nesting level as its matching close, then returns that block.
        /// Used here to find the `if (...) { ... }` body that follows the guard's condition text.
        /// </summary>
        private static string BraceBodyAt(string source, int nearIndex)
        {
            var open = source.IndexOf('{', nearIndex);
            Assert.IsTrue(open >= 0, "no '{' found after the guard condition");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail("unbalanced braces after the guard condition");
            return null;
        }

        /// <summary>
        /// Removes // and /* */ comments, keeping string and char literals intact (regular, verbatim @"..."
        /// and interpolated strings). Comment text is replaced by a single space so tokens do not fuse.
        /// </summary>
        internal static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            var i = 0;

            while (i < source.Length)
            {
                var c = source[i];
                var next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                        i++;

                    sb.Append(' ');
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + 2;
                    sb.Append(' ');
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && (source[i - 1] == '@' || (source[i - 1] == '$' && i > 1 && source[i - 2] == '@'));
                    sb.Append(c);
                    i++;

                    while (i < source.Length)
                    {
                        var s = source[i];
                        sb.Append(s);
                        i++;

                        if (!verbatim && s == '\\' && i < source.Length)
                        {
                            sb.Append(source[i]);
                            i++;
                            continue;
                        }

                        if (s == c)
                        {
                            if (verbatim && i < source.Length && source[i] == '"')
                            {
                                sb.Append('"');
                                i++;
                                continue;
                            }

                            break;
                        }

                        if (!verbatim && s == '\n')
                            break;
                    }

                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}

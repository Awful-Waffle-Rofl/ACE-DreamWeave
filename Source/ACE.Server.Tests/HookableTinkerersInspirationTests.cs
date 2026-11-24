using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the hookable Tinkerer's Inspiration (wcid 1002770): the weenie must stay a type-64 Hooker with
    /// PropertyBool 9037 and a HookType, and no Stuck row, and Hooker.ActOnUse must keep dispatching to
    /// TinkerersInspiration.TryHandleUse. Pure file parsing - no database, no world, no Player construction.
    /// A behavioral test was skipped on purpose: building a Hooker plus a Player in the harness is fragile.
    /// </summary>
    [TestClass]
    public class HookableTinkerersInspirationTests
    {
        private static string FindRepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {string.Join("/", relative)} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string ReadSql()
        {
            var file = FindRepoFile("Content", "sql", "weenies", "1002770 Tinkerer's Inspiration (Hookable).sql");
            // strip the block comment and line comments so prose cannot satisfy a pin
            var text = File.ReadAllText(file);
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"--[^\r\n]*", " ");
            return text;
        }

        private static string TableBlock(string sql, string table)
        {
            var m = Regex.Match(sql, @"INSERT INTO `" + table + @"`.*?;", RegexOptions.Singleline);
            return m.Success ? m.Value : string.Empty;
        }

        [TestMethod]
        public void Weenie_IsHookerType64()
        {
            var sql = ReadSql();
            Assert.IsTrue(Regex.IsMatch(sql, @"VALUES\s*\(\s*1002770\s*,\s*'[^']+'\s*,\s*64\s*,"), "weenie.type must be 64 (Hooker).");
        }

        [TestMethod]
        public void Weenie_HasTinkerersInspirationBool_AndNoStuck()
        {
            var bools = TableBlock(ReadSql(), "weenie_properties_bool");
            Assert.IsTrue(bools.Length > 0, "no weenie_properties_bool INSERT found.");
            Assert.IsTrue(Regex.IsMatch(bools, @"\(\s*1002770\s*,\s*9037\s*,\s*True\s*\)"), "bool 9037 TinkerersInspiration must be True.");
            Assert.IsFalse(Regex.IsMatch(bools, @"\(\s*1002770\s*,\s*1\s*,"), "bool type 1 (Stuck) must not be present: a Stuck item cannot be hooked.");
        }

        [TestMethod]
        public void Weenie_HasHookTypeInt()
        {
            var ints = TableBlock(ReadSql(), "weenie_properties_int");
            Assert.IsTrue(Regex.IsMatch(ints, @"\(\s*1002770\s*,\s*151\s*,\s*[1-9]\d*\s*\)"), "int 151 HookType must be present and non-zero.");
        }

        [TestMethod]
        public void HookerActOnUse_DispatchesToTinkerersInspiration()
        {
            var file = FindRepoFile("Source", "ACE.Server", "WorldObjects", "Hooker.cs");
            var text = File.ReadAllText(file);
            var sig = Regex.Match(text, @"override void ActOnUse\(WorldObject activator\)\s*\{");
            Assert.IsTrue(sig.Success, "Hooker.ActOnUse not found.");

            // brace-depth count from the opening brace of the method, robust to reformatting
            var start = sig.Index + sig.Length;
            var depth = 1;
            var end = start;
            while (end < text.Length && depth > 0)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}') depth--;
                end++;
            }
            Assert.AreEqual(0, depth, "Hooker.ActOnUse braces do not balance.");

            var body = Regex.Replace(text.Substring(start, end - start - 1), @"//[^\r\n]*", " ");
            Assert.IsTrue(Regex.IsMatch(body, @"TinkerersInspiration\.TryHandleUse\(\s*this\s*,\s*player\s*\)"),
                "Hooker.ActOnUse must call TinkerersInspiration.TryHandleUse(this, player).");
        }
    }
}

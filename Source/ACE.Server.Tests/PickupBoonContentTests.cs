using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content-shape gate for pickup-speed boon gems: any weenie carrying PropertyString 9013
    /// (PickupBoonKey) is a permanent per-character pickup-speed boon voucher, and every future one must
    /// satisfy the same invariants the first instance does - Content/sql/weenies/1003400 Quickhand Gem.sql
    /// and its quest row Content/sql/quests/PickupBoon_Firecut.sql:
    ///  - WeenieType is Gem (38)
    ///  - Attuned (int 114) and Bonded (int 33) are set, so the voucher can't be traded or dropped
    ///  - Value (int 19) equals StackUnitValue (int 15), and MaxStackSize (int 11) is 1
    ///  - the PickupBoonKey matches ^[A-Za-z0-9_]+$
    ///  - a Content/sql/quests/PickupBoon_&lt;Key&gt;.sql file exists with that EXACT casing, its quest
    ///    `name` matches PickupBoon_&lt;Key&gt; verbatim, and its max_Solves is 1 (a boon claimed more
    ///    than once would double-grant, and the quest name is what Gem.UseGem stamps)
    /// </summary>
    [TestClass]
    public class PickupBoonContentTests
    {
        [TestMethod]
        public void CommittedPickupBoonGems_MatchAllInvariants()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            if (dir == null)
                Assert.Inconclusive("Could not locate the repo's Content/sql/weenies directory by walking up from the test assembly -- skipping.");

            var weeniesDir = Path.Combine(dir.FullName, "Content", "sql", "weenies");
            var questsDir = Path.Combine(dir.FullName, "Content", "sql", "quests");
            Assert.IsTrue(Directory.Exists(questsDir), $"expected {questsDir} to exist");

            var problems = new List<string>();
            var gemCount = 0;

            foreach (var f in Directory.GetFiles(weeniesDir, "*.sql"))
            {
                var text = File.ReadAllText(f);
                var strings = ParseWeenieStrings(text);

                if (!strings.TryGetValue(9013, out var key))
                    continue;

                gemCount++;
                var name = Path.GetFileName(f);
                var ints = ParseWeenieInts(text);

                if (!ints.TryGetValue(114, out var attuned) || attuned != (int)AttunedStatus.Attuned)
                    problems.Add($"{name}: Attuned (int 114) must be {(int)AttunedStatus.Attuned} (Attuned), found " +
                        (ints.ContainsKey(114) ? ints[114].ToString() : "missing"));

                if (!ints.TryGetValue(33, out var bonded) || bonded != (int)BondedStatus.Bonded)
                    problems.Add($"{name}: Bonded (int 33) must be {(int)BondedStatus.Bonded} (Bonded), found " +
                        (ints.ContainsKey(33) ? ints[33].ToString() : "missing"));

                var type = ParseWeenieType(text, name);
                if (type != (int)WeenieType.Gem)
                    problems.Add($"{name}: WeenieType must be {(int)WeenieType.Gem} (Gem), found {type}");

                var hasValue = ints.TryGetValue(19, out var value);
                var hasStackUnitValue = ints.TryGetValue(15, out var stackUnitValue);
                if (!hasValue)
                    problems.Add($"{name}: no Value (int 19) row");
                if (!hasStackUnitValue)
                    problems.Add($"{name}: no StackUnitValue (int 15) row");
                if (hasValue && hasStackUnitValue && value != stackUnitValue)
                    problems.Add($"{name}: Value (int 19, {value}) must equal StackUnitValue (int 15, {stackUnitValue})");

                if (!ints.TryGetValue(11, out var maxStackSize) || maxStackSize != 1)
                    problems.Add($"{name}: MaxStackSize (int 11) must be 1, found " +
                        (ints.ContainsKey(11) ? maxStackSize.ToString() : "missing"));

                if (!Regex.IsMatch(key, "^[A-Za-z0-9_]+$"))
                {
                    problems.Add($"{name}: PickupBoonKey (string 9013) '{key}' does not match ^[A-Za-z0-9_]+$ - skipping quest cross-check");
                    continue;
                }

                var expectedQuestFileName = $"PickupBoon_{key}.sql";
                var actualQuestFileNames = Directory.GetFiles(questsDir, "*.sql").Select(Path.GetFileName).ToList();

                if (!actualQuestFileNames.Contains(expectedQuestFileName, StringComparer.Ordinal))
                {
                    problems.Add($"{name}: no {expectedQuestFileName} (exact case) in Content/sql/quests for PickupBoonKey '{key}'");
                    continue;
                }

                var questFile = Path.Combine(questsDir, expectedQuestFileName);
                var questText = File.ReadAllText(questFile);
                var expectedQuestName = $"PickupBoon_{key}";

                if (!TryParseQuestRow(questText, out var questName, out var maxSolves, out var parseError))
                {
                    problems.Add($"{expectedQuestFileName}: {parseError}");
                    continue;
                }

                if (!string.Equals(questName, expectedQuestName, StringComparison.Ordinal))
                    problems.Add($"{expectedQuestFileName}: quest name is '{questName}', expected '{expectedQuestName}' verbatim (same casing as the gem's PickupBoonKey)");

                if (maxSolves != 1)
                    problems.Add($"{expectedQuestFileName}: max_Solves must be 1, found {maxSolves}");
            }

            Assert.AreNotEqual(0, gemCount, "no pickup-boon gems (PropertyString 9013) found under Content/sql/weenies - the scan likely rotted");

            Assert.IsTrue(problems.Count == 0,
                "pickup-boon content invariant violations:\n" + string.Join("\n", problems));
        }

        /// <summary>
        /// Reads the `type` column off the weenie's own INSERT INTO `weenie` row (class_Id, class_Name,
        /// type, last_Modified), not the weenie_properties_int table - WeenieType is a column on `weenie`
        /// itself, not a property row.
        /// </summary>
        private static int ParseWeenieType(string sql, string fileNameForError)
        {
            var m = Regex.Match(sql,
                @"INSERT INTO `weenie`\s*\([^)]*\)\s*VALUES\s*\(\s*\d+\s*,\s*'(?:[^'\\]|'')*'\s*,\s*(\d+)\s*,",
                RegexOptions.Singleline);
            Assert.IsTrue(m.Success, $"{fileNameForError}: could not find an INSERT INTO `weenie` row to read the type column from");
            return int.Parse(m.Groups[1].Value);
        }

        /// <summary>
        /// Property ids are namespaced per property table - int 15 is StackUnitValue but string 15 is
        /// ShortDesc - so this scopes the scan to the weenie_properties_int statement instead of matching
        /// (wcid, id, value) tuples across the whole file. Comments are stripped first, because a semicolon
        /// inside a block comment would otherwise end the statement early.
        /// </summary>
        private static Dictionary<int, int> ParseWeenieInts(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

            var start = sql.IndexOf("`weenie_properties_int`", StringComparison.Ordinal);
            if (start < 0)
                return new Dictionary<int, int>();

            var end = sql.IndexOf(';', start);
            var block = end < 0 ? sql.Substring(start) : sql.Substring(start, end - start);

            var result = new Dictionary<int, int>();
            foreach (Match m in Regex.Matches(block, @"\(\s*\d+\s*,\s*(\d+)\s*,\s*(-?\d+)\s*\)"))
                result[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);

            return result;
        }

        /// <summary>
        /// Same idea as ParseWeenieInts but for weenie_properties_string, whose value column is a quoted
        /// SQL string that can itself contain commas and escaped ('') apostrophes - so this walks the block
        /// with a small quote-aware tokenizer rather than one regex.
        /// </summary>
        private static Dictionary<int, string> ParseWeenieStrings(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

            var start = sql.IndexOf("`weenie_properties_string`", StringComparison.Ordinal);
            if (start < 0)
                return new Dictionary<int, string>();

            var end = sql.IndexOf(';', start);
            var block = end < 0 ? sql.Substring(start) : sql.Substring(start, end - start);

            var result = new Dictionary<int, string>();
            var i = 0;
            while (i < block.Length)
            {
                var open = block.IndexOf('(', i);
                if (open < 0)
                    break;
                var tuple = ExtractParenTuple(block, open, out var next);
                var tokens = ParseSqlTuple(tuple);
                if (tokens.Count >= 3 && int.TryParse(tokens[1].Trim(), out var propId))
                    result[propId] = tokens[2];
                i = next;
            }

            return result;
        }

        /// <summary>
        /// Extracts the content between a matched pair of parens starting at <paramref name="openIndex"/>
        /// (which must point at the '(' character), respecting SQL '' escaped quotes so a ')' or ',' inside a
        /// quoted string is never mistaken for structure. <paramref name="nextIndex"/> is set to just past the
        /// closing paren, for the caller to resume scanning from.
        /// </summary>
        private static string ExtractParenTuple(string s, int openIndex, out int nextIndex)
        {
            var i = openIndex + 1;
            var depth = 1;
            var inString = false;

            while (i < s.Length && depth > 0)
            {
                var c = s[i];
                if (inString)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }
                        inString = false;
                    }
                }
                else
                {
                    if (c == '\'')
                        inString = true;
                    else if (c == '(')
                        depth++;
                    else if (c == ')')
                        depth--;
                }
                i++;
            }

            nextIndex = i;
            return s.Substring(openIndex + 1, Math.Max(0, i - openIndex - 2));
        }

        /// <summary>
        /// Splits a paren tuple's inner content on top-level commas, honoring quoted strings (with ''
        /// escaping) so a comma or paren inside a message string never splits a token early.
        /// </summary>
        private static List<string> ParseSqlTuple(string s)
        {
            var tokens = new List<string>();
            var i = 0;

            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
                if (i >= s.Length)
                    break;

                if (s[i] == '\'')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < s.Length)
                    {
                        if (s[i] == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                        }
                        else if (s[i] == '\'')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            sb.Append(s[i]);
                            i++;
                        }
                    }
                    tokens.Add(sb.ToString());
                }
                else
                {
                    var start = i;
                    while (i < s.Length && s[i] != ',')
                        i++;
                    tokens.Add(s.Substring(start, i - start).Trim());
                }

                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
                if (i < s.Length && s[i] == ',')
                    i++;
            }

            return tokens;
        }

        /// <summary>
        /// Reads the `quest` table's own INSERT (name, min_Delta, max_Solves, message, last_Modified) row by
        /// its actual header column order rather than assuming a fixed position, so a reordered column list
        /// doesn't silently misread the wrong value as max_Solves.
        /// </summary>
        private static bool TryParseQuestRow(string sql, out string questName, out int maxSolves, out string error)
        {
            questName = null;
            maxSolves = 0;
            error = null;

            var headerMatch = Regex.Match(sql, @"INSERT INTO `quest`\s*\(([^)]*)\)", RegexOptions.Singleline);
            if (!headerMatch.Success)
            {
                error = "could not find an INSERT INTO `quest` (...) column header";
                return false;
            }

            var columns = headerMatch.Groups[1].Value
                .Split(',')
                .Select(c => c.Trim().Trim('`'))
                .ToList();

            var nameIndex = columns.IndexOf("name");
            var maxSolvesIndex = columns.IndexOf("max_Solves");
            if (nameIndex < 0 || maxSolvesIndex < 0)
            {
                error = $"quest column header is missing name and/or max_Solves: [{string.Join(", ", columns)}]";
                return false;
            }

            var valuesIdx = sql.IndexOf("VALUES", headerMatch.Index, StringComparison.Ordinal);
            if (valuesIdx < 0)
            {
                error = "no VALUES clause found after the column header";
                return false;
            }

            var openParen = sql.IndexOf('(', valuesIdx);
            if (openParen < 0)
            {
                error = "no opening paren found after VALUES";
                return false;
            }

            var tuple = ExtractParenTuple(sql, openParen, out _);
            var tokens = ParseSqlTuple(tuple);

            if (nameIndex >= tokens.Count || maxSolvesIndex >= tokens.Count)
            {
                error = $"VALUES tuple has fewer tokens ({tokens.Count}) than the column header ({columns.Count})";
                return false;
            }

            questName = tokens[nameIndex];
            if (!int.TryParse(tokens[maxSolvesIndex], out maxSolves))
            {
                error = $"max_Solves token '{tokens[maxSolvesIndex]}' is not an integer";
                return false;
            }

            return true;
        }
    }
}

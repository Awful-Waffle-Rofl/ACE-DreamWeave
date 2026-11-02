using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Every single-quoted literal in the Black Book's SQL (the 1007500-block weenies and the realm unit) is closed, with
    /// any apostrophe doubled, and ends where a value ends (before , ) or ;). An unescaped apostrophe ('Keeper's Door')
    /// is a MySQL 1064 at apply time that content-lint does not catch. Files only.
    /// </summary>
    [TestClass]
    public class SpeedBlackBookSqlLiteralTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.sln by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }

        /// <summary>
        /// One pass over <paramref name="sql"/> outside comments (/* */ and -- to end of line): every '...' literal must
        /// close ('' is an escaped quote inside it) and be followed, past whitespace and comments, by , ) or ;.
        /// Returns one line per problem.
        /// </summary>
        public static List<string> LiteralProblems(string sql)
        {
            var problems = new List<string>();
            var i = 0;
            var line = 1;

            void Advance(int n)
            {
                for (var k = 0; k < n && i < sql.Length; k++, i++)
                    if (sql[i] == '\n')
                        line++;
            }

            bool SkipComment()
            {
                if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    Advance(end < 0 ? sql.Length - i : end + 2 - i);
                    return true;
                }

                if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
                {
                    var end = sql.IndexOf('\n', i);
                    Advance(end < 0 ? sql.Length - i : end - i);
                    return true;
                }

                return false;
            }

            while (i < sql.Length)
            {
                if (SkipComment())
                    continue;

                if (sql[i] != '\'')
                {
                    Advance(1);
                    continue;
                }

                var startLine = line;
                var start = i;
                Advance(1);
                var closed = false;

                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        Advance(2);
                        continue;
                    }

                    if (sql[i] == '\'')
                    {
                        Advance(1);
                        closed = true;
                        break;
                    }

                    if (sql[i] == '\n')
                        break; // no literal in this content spans lines; a newline inside one is a runaway quote

                    Advance(1);
                }

                var text = sql.Substring(start, Math.Min(i - start, 60));

                if (!closed)
                {
                    problems.Add($"line {startLine}: literal never closed: {text}");
                    continue;
                }

                while (i < sql.Length && (char.IsWhiteSpace(sql[i]) || SkipComment()))
                    if (i < sql.Length && char.IsWhiteSpace(sql[i]))
                        Advance(1);

                if (i >= sql.Length || (sql[i] != ',' && sql[i] != ')' && sql[i] != ';'))
                    problems.Add($"line {startLine}: literal {text} is followed by '{(i < sql.Length ? sql[i].ToString() : "EOF")}', not , ) or ; (an unescaped apostrophe?)");
            }

            return problems;
        }

        [TestMethod]
        public void TheScanner_CatchesAnUnescapedApostrophe_AndPassesADoubledOne()
        {
            // Positive control, the shipped defect: the literal ends at "Keeper'" and is followed by 's' (and the stray quote
            // then opens a runaway literal, a second problem).
            var shipped = LiteralProblems("VALUES (1007516,   1, 'Keeper's Door') /* Name */;");
            Assert.IsTrue(shipped.Any(p => p.Contains("followed by 's'")), string.Join(" | ", shipped));
            Assert.AreEqual(0, LiteralProblems("VALUES (1007516,   1, 'Keeper''s Door') /* Name */;").Count);
            Assert.AreEqual(0, LiteralProblems("/* the room's door */ VALUES (1, 'a', 'b') -- it's fine\n;").Count, "apostrophes in comments are not literals");
            Assert.AreEqual(0, LiteralProblems("VALUES (1, 'a /* not a comment */ b')\n     , (2, 'c');").Count);
            Assert.AreEqual(1, LiteralProblems("VALUES (1, 'never closed);\n").Count);
        }

        [TestMethod]
        public void EveryBlackBookSqlLiteral_IsClosedAndEscaped()
        {
            var root = RepoRoot();
            var weenies = Path.Combine(root, "Content", "sql", "weenies");

            // Filter by name in code: a wildcard pattern can also match Windows 8.3 short names.
            var files = Directory.EnumerateFiles(weenies, "*.sql", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetFileName(p).StartsWith("10075", StringComparison.Ordinal))
                .Append(Path.Combine(root, "Content", "realms", "speedrun_black_book.sql"))
                .ToList();

            Assert.IsTrue(files.Count >= 27, $"expected the 1007500-1007525 weenies and the realm unit, found {files.Count}");

            var problems = new List<string>();

            foreach (var path in files)
                foreach (var p in LiteralProblems(File.ReadAllText(path)))
                    problems.Add($"{Path.GetFileName(path)} {p}");

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }
    }
}

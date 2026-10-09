using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Group Threads (Docs/Threads/GROUP-THREADS-DESIGN.md), Task 3: pure source-text checks over
    /// ace_analytics.sql and AnalyticsDatabase.cs for the new dungeon_run_participant / dungeon_run_group
    /// tables, in the same spirit as AnalyticsKindLengthTests - no live MySQL connection and no Player
    /// are available in ACE.Server.Tests, so the schema and the writer's column lists are checked as
    /// text instead of by executing SQL.
    ///
    /// Checks: both new CREATE TABLE statements exist with IF NOT EXISTS (the file has no ALTER path,
    /// see the header comment in ace_analytics.sql); every column named in the new INSERTs in
    /// AnalyticsDatabase.cs exists in the matching CREATE TABLE; and the file contains no ALTER TABLE
    /// anywhere, which would violate that same rule for every existing table too.
    /// </summary>
    [TestClass]
    public class DungeonRunChildTablesSchemaTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Managers", "Analytics", "AnalyticsManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Managers/Analytics/AnalyticsManager.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string SchemaSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "ace_analytics.sql"));

        private static string DatabaseSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "AnalyticsDatabase.cs"));

        /// <summary>Extracts the body of `CREATE TABLE IF NOT EXISTS `name` ( ... ) ENGINE=...` for one table.</summary>
        private static string CreateTableBody(string sql, string tableName)
        {
            var match = Regex.Match(
                sql,
                $@"CREATE TABLE IF NOT EXISTS `{Regex.Escape(tableName)}` \((?<body>(?:(?!\)\s*ENGINE).)*)\)\s*ENGINE",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, $"could not find 'CREATE TABLE IF NOT EXISTS `{tableName}`' in ace_analytics.sql - did the schema move?");

            return match.Groups["body"].Value;
        }

        /// <summary>Every backtick-quoted column name declared inside a CREATE TABLE body (skips KEY/PRIMARY KEY lines).</summary>
        private static HashSet<string> DeclaredColumns(string createTableBody)
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawLine in createTableBody.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd(',');
                if (line.Length == 0)
                    continue;
                if (line.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("KEY ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("UNIQUE KEY", StringComparison.OrdinalIgnoreCase))
                    continue;

                var m = Regex.Match(line, @"^`([^`]+)`");
                if (m.Success)
                    columns.Add(m.Groups[1].Value);
            }

            return columns;
        }

        /// <summary>Every backtick-quoted column name inside one `INSERT INTO `table` (...) VALUES` column list.</summary>
        private static string[] InsertColumns(string csharpSource, string tableName)
        {
            var match = Regex.Match(
                csharpSource,
                $@"INSERT INTO `{Regex.Escape(tableName)}` \((?<cols>[^)]*)\)\s*""?\s*\+?\s*""?\s*VALUES",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, $"could not find an 'INSERT INTO `{tableName}`' column list in AnalyticsDatabase.cs - did the writer move?");

            return Regex.Matches(match.Groups["cols"].Value, @"`([^`]+)`")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();
        }

        [TestMethod]
        public void DungeonRunParticipant_CreateTableExists_WithIfNotExists()
        {
            var sql = SchemaSource();
            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `dungeon_run_participant`");
        }

        [TestMethod]
        public void DungeonRunGroup_CreateTableExists_WithIfNotExists()
        {
            var sql = SchemaSource();
            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `dungeon_run_group`");
        }

        [TestMethod]
        public void DungeonRunParticipant_InsertColumns_AllExistInCreateTable()
        {
            var sql = SchemaSource();
            var cs = DatabaseSource();

            var declared = DeclaredColumns(CreateTableBody(sql, "dungeon_run_participant"));
            var inserted = InsertColumns(cs, "dungeon_run_participant");

            Assert.IsTrue(inserted.Length > 0, "expected at least one column in the dungeon_run_participant INSERT");

            foreach (var col in inserted)
                Assert.IsTrue(declared.Contains(col), $"dungeon_run_participant INSERT writes column '{col}', which is not declared in the CREATE TABLE");
        }

        [TestMethod]
        public void DungeonRunGroup_InsertColumns_AllExistInCreateTable()
        {
            var sql = SchemaSource();
            var cs = DatabaseSource();

            var declared = DeclaredColumns(CreateTableBody(sql, "dungeon_run_group"));
            var inserted = InsertColumns(cs, "dungeon_run_group");

            Assert.IsTrue(inserted.Length > 0, "expected at least one column in the dungeon_run_group INSERT");

            foreach (var col in inserted)
                Assert.IsTrue(declared.Contains(col), $"dungeon_run_group INSERT writes column '{col}', which is not declared in the CREATE TABLE");
        }

        [TestMethod]
        public void Schema_HasNoAlterTablePath()
        {
            // Strip `-- ...` line comments first: the file's own header PROSE discusses "ALTER TABLE"
            // (explaining why the file never uses one), which is not a live statement and must not trip
            // this check. Only an ALTER TABLE outside a comment - an actual statement - is a violation.
            var sql = SchemaSource();
            var withoutComments = string.Join('\n', sql.Split('\n').Select(line =>
            {
                var idx = line.IndexOf("--", StringComparison.Ordinal);
                return idx >= 0 ? line.Substring(0, idx) : line;
            }));

            Assert.IsFalse(Regex.IsMatch(withoutComments, @"ALTER\s+TABLE", RegexOptions.IgnoreCase),
                "ace_analytics.sql must have no live ALTER TABLE statement - every change is a new CREATE TABLE IF NOT EXISTS column added speculatively up front");
        }
    }
}

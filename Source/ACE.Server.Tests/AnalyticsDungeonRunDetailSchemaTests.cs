using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Drift guard between the dungeon_run_detail CREATE TABLE column set in ace_analytics.sql and the
    /// INSERT INTO `dungeon_run_detail` column list AnalyticsDatabase.WriteDungeonRuns actually writes.
    /// A pure source-text check, on the same footing as AnalyticsKindLengthTests: AnalyticsDatabase needs
    /// a live MySQL connection, which is not available in ACE.Server.Tests, so the two column lists are
    /// compared as text instead of by executing anything.
    ///
    /// This table is 1:1 with dungeon_run and every column except run_fk/source/plan_built is nullable
    /// (see ace_analytics.sql), so a column added to one side and forgotten on the other would silently
    /// either never get written or throw at insert time - either way, invisible until it happened in
    /// production. This test fails the day that happens instead.
    /// </summary>
    [TestClass]
    public class AnalyticsDungeonRunDetailSchemaTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Managers", "Analytics", "AnalyticsManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Managers/Analytics/AnalyticsManager.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string AnalyticsDatabaseSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "AnalyticsDatabase.cs"));

        private static string AnalyticsSchemaSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "ace_analytics.sql"));

        /// <summary>Every backtick-quoted column name inside the dungeon_run_detail CREATE TABLE block.</summary>
        private static string[] SchemaColumns()
        {
            var sql = AnalyticsSchemaSource();
            var table = Regex.Match(sql, @"CREATE TABLE IF NOT EXISTS `dungeon_run_detail` \((?<body>.*?)\n\) ENGINE=InnoDB", RegexOptions.Singleline);

            Assert.IsTrue(table.Success, "expected a CREATE TABLE IF NOT EXISTS `dungeon_run_detail` block in ace_analytics.sql - did it move or get renamed?");

            var body = table.Groups["body"].Value;

            // Column definitions only - skip PRIMARY KEY / KEY lines, which also start with a backtick-quoted
            // name but name an index, not a column.
            return body.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("`") && !line.StartsWith("PRIMARY KEY") && !line.StartsWith("KEY"))
                .Select(line => Regex.Match(line, @"^`([a-z0-9_]+)`").Groups[1].Value)
                .Where(name => name.Length > 0)
                .ToArray();
        }

        /// <summary>Every backtick-quoted column name inside the INSERT INTO `dungeon_run_detail` (...) column list.</summary>
        private static string[] InsertColumns()
        {
            var source = AnalyticsDatabaseSource();
            var insert = Regex.Match(source, @"INSERT INTO `dungeon_run_detail` \((?<cols>[^)]+)\)", RegexOptions.Singleline);

            Assert.IsTrue(insert.Success, "expected an INSERT INTO `dungeon_run_detail` (...) column list in AnalyticsDatabase.cs - did it move?");

            return Regex.Matches(insert.Groups["cols"].Value, @"`([a-z0-9_]+)`")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();
        }

        [TestMethod]
        public void Schema_and_insert_column_sets_are_identical()
        {
            var schemaColumns = SchemaColumns();
            var insertColumns = InsertColumns();

            Assert.IsTrue(schemaColumns.Length > 5, "expected the dungeon_run_detail column extraction to find a real column list - did the regex break?");
            Assert.IsTrue(insertColumns.Length > 5, "expected the INSERT column extraction to find a real column list - did the regex break?");

            var schemaSet = new HashSet<string>(schemaColumns);
            var insertSet = new HashSet<string>(insertColumns);

            var inSchemaOnly = schemaSet.Except(insertSet).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var inInsertOnly = insertSet.Except(schemaSet).OrderBy(s => s, StringComparer.Ordinal).ToArray();

            Assert.AreEqual(0, inSchemaOnly.Length, $"columns in ace_analytics.sql but never written: {string.Join(", ", inSchemaOnly)}");
            Assert.AreEqual(0, inInsertOnly.Length, $"columns written but not in ace_analytics.sql: {string.Join(", ", inInsertOnly)}");

            // The INSERT also duplicates every column name in its VALUES (...) tuple's own comment-free
            // parameter placeholders only, never a second copy of the column names, so no dedup is needed
            // here - but pin the exact count too, as a second guard against a column silently renamed to an
            // already-used name (which would pass the set comparison above by accident).
            Assert.AreEqual(schemaColumns.Length, insertColumns.Length,
                $"schema has {schemaColumns.Length} columns, INSERT writes {insertColumns.Length} - a rename that collided with an existing name would still pass the set check above");
        }
    }
}

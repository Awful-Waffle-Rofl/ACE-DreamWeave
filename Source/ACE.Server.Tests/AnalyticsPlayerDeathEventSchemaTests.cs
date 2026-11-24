using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers.Analytics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Drift guards for player_death_event, the same footing as AnalyticsKnotEventSchemaTests and AnalyticsKindLengthTests:
    /// pure source-text checks, because AnalyticsDatabase needs a live MySQL connection that ACE.Server.Tests does not have.
    ///
    ///   * the CREATE TABLE column set in ace_analytics.sql and the INSERT column list in WritePlayerDeaths are identical;
    ///   * every VARCHAR/CHAR width in the DDL matches the AnalyticsDatabase constant the writer clamps to;
    ///   * every closed-vocabulary value the producer can write (attack_kind, element) fits its column.
    ///
    /// The table freezes on first deploy (no ALTER path), so a column added to one side only would either never be
    /// written or fail every insert, invisibly until production. These fail the day that happens instead.
    /// </summary>
    [TestClass]
    public class AnalyticsPlayerDeathEventSchemaTests
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

        private static string TableBody()
        {
            var table = Regex.Match(AnalyticsSchemaSource(), @"CREATE TABLE IF NOT EXISTS `player_death_event` \((?<body>.*?)\n\) ENGINE=InnoDB", RegexOptions.Singleline);

            Assert.IsTrue(table.Success, "expected a CREATE TABLE IF NOT EXISTS `player_death_event` block in ace_analytics.sql - did it move or get renamed?");

            return table.Groups["body"].Value;
        }

        /// <summary>Column name -> its type text, for every column definition (index lines and the id key skipped).</summary>
        private static Dictionary<string, string> SchemaColumnTypes()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var raw in TableBody().Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^`([a-z0-9_]+)`\s+([A-Z]+(?:\(\d+\))?)");
                if (!m.Success || m.Groups[1].Value == "id")
                    continue;

                Assert.IsFalse(result.ContainsKey(m.Groups[1].Value), $"duplicate column {m.Groups[1].Value} in the DDL");
                result.Add(m.Groups[1].Value, m.Groups[2].Value);
            }

            return result;
        }

        private static string[] InsertColumns()
        {
            var insert = Regex.Match(AnalyticsDatabaseSource(), @"INSERT INTO `player_death_event` \((?<cols>[^)]+)\)", RegexOptions.Singleline);

            Assert.IsTrue(insert.Success, "expected an INSERT INTO `player_death_event` (...) column list in AnalyticsDatabase.cs - did it move?");

            return Regex.Matches(insert.Groups["cols"].Value, @"`([a-z0-9_]+)`")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();
        }

        [TestMethod]
        public void Schema_and_insert_column_sets_are_identical()
        {
            var schema = SchemaColumnTypes().Keys.ToArray();
            var insert = InsertColumns();

            Assert.IsTrue(schema.Length > 5, "expected the DDL column extraction to find a real column list - did the regex break?");
            Assert.IsTrue(insert.Length > 5, "expected the INSERT column extraction to find a real column list - did the regex break?");

            var inSchemaOnly = schema.Except(insert).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var inInsertOnly = insert.Except(schema).OrderBy(s => s, StringComparer.Ordinal).ToArray();

            Assert.AreEqual(0, inSchemaOnly.Length, $"columns in ace_analytics.sql but never written: {string.Join(", ", inSchemaOnly)}");
            Assert.AreEqual(0, inInsertOnly.Length, $"columns written but not in ace_analytics.sql: {string.Join(", ", inInsertOnly)}");

            Assert.AreEqual(35, insert.Length, "player_death_event has 35 non-id columns");
            Assert.AreEqual(insert.Length, insert.Distinct().Count(), "the INSERT names a column twice");
            Assert.AreEqual(schema.Length, insert.Length);
        }

        [TestMethod]
        public void Every_string_column_width_matches_the_writer_clamp()
        {
            var types = SchemaColumnTypes();

            var expected = new Dictionary<string, string>
            {
                ["name"] = $"VARCHAR({AnalyticsDatabase.DeathNameMaxLength})",
                ["killer_name"] = $"VARCHAR({AnalyticsDatabase.DeathNameMaxLength})",
                ["attack_kind"] = $"VARCHAR({AnalyticsDatabase.DeathAttackKindMaxLength})",
                ["damage_type"] = $"VARCHAR({AnalyticsDatabase.DeathDamageTypeMaxLength})",
                ["element"] = $"VARCHAR({AnalyticsDatabase.DeathElementMaxLength})",
                ["thread_start_group"] = $"CHAR({AnalyticsDatabase.DeathStartGroupMaxLength})",
                ["thread_dungeon_id"] = $"VARCHAR({AnalyticsDatabase.DeathDungeonIdMaxLength})",
                ["message"] = $"VARCHAR({AnalyticsDatabase.DeathMessageMaxLength})",
            };

            foreach (var kv in expected)
                Assert.AreEqual(kv.Value, types[kv.Key], $"player_death_event.{kv.Key}");

            // every string column is covered above, so a new one cannot arrive without a clamp constant
            var stringColumns = types.Where(t => t.Value.StartsWith("VARCHAR", StringComparison.Ordinal) || t.Value.StartsWith("CHAR", StringComparison.Ordinal))
                .Select(t => t.Key).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expected.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray(), stringColumns);
        }

        [TestMethod]
        public void Every_attack_kind_fits_its_column_and_the_set_is_pinned()
        {
            var wire = Enum.GetValues(typeof(DamageAttackKind)).Cast<DamageAttackKind>().Select(DamageAttackKindNames.Wire).ToArray();

            foreach (var w in wire)
                Assert.IsTrue(w.Length <= AnalyticsDatabase.DeathAttackKindMaxLength, $"attack_kind '{w}' overflows VARCHAR({AnalyticsDatabase.DeathAttackKindMaxLength})");

            CollectionAssert.AreEqual(new[] { "effect", "hazard", "magic", "melee", "missile", "other" },
                wire.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray(), "a new kind is a reviewed change to this pin and the DDL comment");

            StringAssert.Contains(AnalyticsSchemaSource(), "-- melee | missile | magic | hazard | effect | other");
        }

        [TestMethod]
        public void Every_element_name_fits_its_column()
        {
            foreach (DamageType dt in Enum.GetValues(typeof(DamageType)))
            {
                var name = DeathTelemetry.ElementName(DeathTelemetry.ResistElement(dt));
                Assert.IsTrue(name.Length <= AnalyticsDatabase.DeathElementMaxLength, $"element '{name}' overflows VARCHAR({AnalyticsDatabase.DeathElementMaxLength})");
            }
        }

        [TestMethod]
        public void Every_single_damage_type_name_fits_its_column()
        {
            foreach (DamageType dt in Enum.GetValues(typeof(DamageType)))
            {
                var name = DeathTelemetry.DamageTypeName(dt);
                Assert.IsTrue(name.Length <= AnalyticsDatabase.DeathDamageTypeMaxLength, $"damage_type '{name}' overflows VARCHAR({AnalyticsDatabase.DeathDamageTypeMaxLength})");
            }
        }
    }
}

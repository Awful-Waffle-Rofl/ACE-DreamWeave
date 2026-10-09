using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvP template storage shape: Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql against the EF
    /// mapping in PvpTemplatePartial.cs, plus the DAO's pure parts. Same split as PvpArenaSchemaTests: no
    /// database here, so these catch an entity/migration mismatch at build time.
    /// </summary>
    [TestClass]
    public class PvpTemplateSchemaTests
    {
        private const string MigrationFile = "2026-10-03-00-Add-Pvp-Templates.sql";

        private static readonly Dictionary<string, string> ExpectedTemplateColumns = new Dictionary<string, string>
        {
            { "TemplateKey",         "template_Key" },
            { "DisplayName",         "display_Name" },
            { "SourceCharacterId",   "source_Character_Id" },
            { "SourceCharacterName", "source_Character_Name" },
            { "Version",             "version" },
            { "Enabled",             "enabled" },
            { "Modes",               "modes" },
            { "DefinitionJson",      "definition_Json" },
            { "SnapshotAt",          "snapshot_At" },
            { "SnapshotBy",          "snapshot_By" },
        };

        private static readonly Dictionary<string, string> ExpectedHistoryColumns = new Dictionary<string, string>
        {
            { "TemplateKey",    "template_Key" },
            { "Version",        "version" },
            { "DefinitionJson", "definition_Json" },
            { "SnapshotAt",     "snapshot_At" },
            { "SnapshotBy",     "snapshot_By" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Statements()
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile));
            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(sql, @"(?m)--.*$", string.Empty);
        }

        private static string TableBody(string table)
        {
            var sql = Statements();
            var start = sql.IndexOf($"CREATE TABLE IF NOT EXISTS `{table}`", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"no CREATE TABLE IF NOT EXISTS `{table}`");
            var end = sql.IndexOf(';', start);
            Assert.IsTrue(end > start, $"`{table}` statement is not terminated");
            return sql.Substring(start, end - start);
        }

        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none", new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        // ---- migration ----

        [TestMethod]
        public void Migration_CreatesExactlyTheTwoTables_Idempotently()
        {
            var statements = Statements();

            Assert.AreEqual(2, Regex.Matches(statements, @"CREATE TABLE IF NOT EXISTS").Count);
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(Regex.IsMatch(statements, @"COMMENT\s*=?\s*'[^']*;"),
                "no semicolon inside a COMMENT string: TableBody (and any statement-splitting reader) finds a statement's end by its semicolon");
        }

        [TestMethod]
        public void Migration_HasNoForeignKeyOrDangerousConstructs()
        {
            var statements = Statements();

            Assert.IsFalse(statements.Contains("FOREIGN KEY"));
            Assert.IsFalse(Regex.IsMatch(statements, @"\bDROP\b"), "a shard migration must never DROP");
            Assert.IsFalse(statements.Contains("DELIMITER"), "DELIMITER is a mysql client command, not SQL");
            Assert.IsFalse(statements.Contains("CURRENT_TIMESTAMP"), "no datetime default - it would stamp the server's LOCAL time");
            Assert.IsFalse(Regex.IsMatch(statements, @"ADD (COLUMN|INDEX)\s+IF NOT EXISTS", RegexOptions.IgnoreCase), "MariaDB-only syntax");
            Assert.IsFalse(Regex.IsMatch(statements, @"\bTRUNCATE\b", RegexOptions.IgnoreCase));
        }

        [TestMethod]
        public void Migration_DeclaresEveryMappedColumnAndTheKeys()
        {
            foreach (var pair in ExpectedTemplateColumns)
                StringAssert.Contains(TableBody("pvp_template"), $"`{pair.Value}`", $"pvp_template is missing {pair.Value}");

            foreach (var pair in ExpectedHistoryColumns)
                StringAssert.Contains(TableBody("pvp_template_history"), $"`{pair.Value}`", $"pvp_template_history is missing {pair.Value}");

            StringAssert.Contains(TableBody("pvp_template"), "PRIMARY KEY (`template_Key`)");
            StringAssert.Contains(TableBody("pvp_template_history"), "PRIMARY KEY (`template_Key`, `version`)");
            Assert.IsTrue(Regex.IsMatch(TableBody("pvp_template"), @"`template_Key`\s+varchar\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(TableBody("pvp_template"), @"`definition_Json`\s+mediumtext\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(TableBody("pvp_template"), @"`enabled`\s+bit\(1\)\s+NOT NULL"));
        }

        /// <summary>
        /// The one-time rating reset is keyed on the ABSENCE of template_Key and runs BEFORE the ALTER that adds it,
        /// so a replay (empty applied-updates ledger on a fresh bin) deletes nothing. It only ever touches the three
        /// arena ladders, and never the match history.
        /// </summary>
        [TestMethod]
        public void Migration_RatingReset_IsKeyedOnTheColumnAndRunsBeforeIt()
        {
            var statements = Statements();

            var reset = statements.IndexOf("DELETE FROM `character_pvp_rating`", StringComparison.Ordinal);
            var alter = statements.IndexOf("ADD COLUMN `template_Key`", StringComparison.Ordinal);
            var probe = statements.IndexOf("COLUMN_NAME = 'template_Key'", StringComparison.Ordinal);

            Assert.IsTrue(reset > 0 && alter > 0 && probe > 0);
            Assert.IsTrue(probe < reset && reset < alter, "probe the column, then reset, then add the column");

            var resetLine = statements.Substring(reset, statements.IndexOf('\n', reset) - reset);
            StringAssert.Contains(resetLine, "WHERE `ladder` IN (''arena_1v1'', ''arena_2v2'', ''arena_ffa'')");

            Assert.AreEqual(1, Regex.Matches(statements, @"\bDELETE\b").Count, "exactly one DELETE");
            Assert.IsFalse(Regex.IsMatch(statements, @"DELETE FROM `pvp_match"), "match history is kept");
            Assert.IsTrue(Regex.IsMatch(statements, @"IF\(@pvpt_has_key > 0 OR @pvpt_has_ratings = 0, 'DO 1',\s*'DELETE"), "the reset is skipped once the column exists");
        }

        [TestMethod]
        public void Migration_ParticipantColumns_AreGuardedAndNullable()
        {
            var statements = Statements();

            Assert.IsTrue(Regex.IsMatch(statements, @"ADD COLUMN `template_Key` varchar\(32\) CHARACTER SET ascii COLLATE ascii_bin NULL"));
            Assert.IsTrue(Regex.IsMatch(statements, @"ADD COLUMN `template_Version` int unsigned NULL"));
            Assert.IsTrue(Regex.IsMatch(statements, @"IF\(@pvpt_has_key > 0 OR @pvpt_has_participant = 0, 'DO 1',\s*'ALTER TABLE `pvp_match_participant` ADD COLUMN `template_Key`"));
            Assert.IsTrue(Regex.IsMatch(statements, @"IF\(@pvpt_has_version > 0 OR @pvpt_has_participant = 0, 'DO 1',\s*'ALTER TABLE `pvp_match_participant` ADD COLUMN `template_Version`"));
            Assert.AreEqual(3, Regex.Matches(statements, @"\bEXECUTE\b").Count);
            Assert.AreEqual(3, Regex.Matches(statements, @"DEALLOCATE PREPARE").Count);
        }

        // ---- EF model ----

        private static void AssertColumns(Type clrType, string table, Dictionary<string, string> expected)
        {
            using var ctx = CreateContext();
            var e = ctx.Model.FindEntityType(clrType);

            Assert.IsNotNull(e, $"{clrType.Name} is not in the ShardDbContext model");
            Assert.AreEqual(table, e.GetTableName());
            CollectionAssert.AreEquivalent(expected.Keys.ToList(), e.GetProperties().Select(p => p.Name).ToList());

            foreach (var pair in expected)
                Assert.AreEqual(pair.Value, e.FindProperty(pair.Key).GetColumnName(), $"{clrType.Name}.{pair.Key}");
        }

        [TestMethod]
        public void EfModel_MapsEveryColumnAndTheKeys()
        {
            AssertColumns(typeof(PvpTemplate), "pvp_template", ExpectedTemplateColumns);
            AssertColumns(typeof(PvpTemplateHistory), "pvp_template_history", ExpectedHistoryColumns);

            using var ctx = CreateContext();
            CollectionAssert.AreEqual(new[] { "TemplateKey" }, ctx.Model.FindEntityType(typeof(PvpTemplate)).FindPrimaryKey().Properties.Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "TemplateKey", "Version" }, ctx.Model.FindEntityType(typeof(PvpTemplateHistory)).FindPrimaryKey().Properties.Select(p => p.Name).ToArray());
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, ctx.Model.FindEntityType(typeof(PvpTemplateHistory)).FindProperty("Version").ValueGenerated);
        }

        /// <summary>
        /// The participant stamp columns are NOT mapped: mapping them would put them in every participant INSERT,
        /// and an unmigrated shard would then fail to save any match.
        /// </summary>
        [TestMethod]
        public void EfModel_DoesNotMapTheParticipantStampColumns()
        {
            using var ctx = CreateContext();
            var participant = ctx.Model.FindEntityType(typeof(PvpMatchParticipant));

            Assert.IsFalse(participant.GetProperties().Any(p => p.GetColumnName() == "template_Key" || p.GetColumnName() == "template_Version"));
        }

        [TestMethod]
        public void ShardDbContext_DeclaresBothDbSets()
        {
            foreach (var name in new[] { "PvpTemplate", "PvpTemplateHistory" })
                Assert.IsNotNull(typeof(ShardDbContext).GetProperty(name, BindingFlags.Public | BindingFlags.Instance), $"ShardDbContext.{name} DbSet is missing");
        }

        // ---- the DAO's pure parts ----

        private static PvpTemplateRecord Snapshot(string key = "mage") => new PvpTemplateRecord { TemplateKey = key, DisplayName = "Mage", DefinitionJson = "{}", Modes = "" };

        [TestMethod]
        public void ValidateSnapshot_RefusesBadShapes()
        {
            Assert.IsNull(ShardDatabase.ValidatePvpTemplateSnapshot(Snapshot()));
            Assert.IsNotNull(ShardDatabase.ValidatePvpTemplateSnapshot(null));
            Assert.IsNotNull(ShardDatabase.ValidatePvpTemplateSnapshot(Snapshot("")));
            Assert.IsNotNull(ShardDatabase.ValidatePvpTemplateSnapshot(Snapshot(new string('a', 33))));
            Assert.IsNotNull(ShardDatabase.ValidatePvpTemplateSnapshot(Snapshot("mé")));

            var noBody = Snapshot();
            noBody.DefinitionJson = "";
            Assert.IsNotNull(ShardDatabase.ValidatePvpTemplateSnapshot(noBody));

            var longName = Snapshot();
            longName.DisplayName = new string('n', 65);
            Assert.IsNotNull(ShardDatabase.ValidatePvpTemplateSnapshot(longName));
        }

        [TestMethod]
        public void ToRecord_ReadsBackAsUtc_AndNeverNullModes()
        {
            var when = new DateTime(2026, 10, 3, 18, 30, 0, DateTimeKind.Unspecified);
            var record = ShardDatabase.ToRecord(new PvpTemplate { TemplateKey = "k", Version = 4, Enabled = true, Modes = null, SnapshotAt = when, DefinitionJson = "{}" });

            Assert.AreEqual(DateTimeKind.Utc, record.SnapshotAt.Kind);
            Assert.AreEqual(when.Ticks, record.SnapshotAt.Ticks);
            Assert.AreEqual(string.Empty, record.Modes);
            Assert.AreEqual(4u, record.Version);
            Assert.IsTrue(record.Enabled);
        }

        [TestMethod]
        public void UnknownColumnDetection_IgnoresUnrelatedExceptions()
        {
            Assert.IsFalse(ShardDatabase.IsUnknownColumnError(null));
            Assert.IsFalse(ShardDatabase.IsUnknownColumnError(new InvalidOperationException("x", new TimeoutException())));
        }
    }
}

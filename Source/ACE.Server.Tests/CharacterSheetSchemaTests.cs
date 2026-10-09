using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>Character Sheet link table shape - catches an entity/migration mismatch at build time.</summary>
    [TestClass]
    public class CharacterSheetSchemaTests
    {
        private const string MigrationFile = "2026-09-15-00-Add-Character-Sheet-Link.sql";

        // Copied from MarketSchemaTests.RepoRoot: walk up from the test output to the repo root.
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Migration()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile));

        /// <summary>The migration with comments stripped, the same rule MarketSchemaTests.Statements applies.</summary>
        private static string Statements()
        {
            var sql = Migration();

            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"(?m)--.*$", string.Empty);

            return sql;
        }

        /// <summary>
        /// The real shard model on the real provider with no connection behind it, exactly as
        /// BiotaUpdaterCollectionMutationTests builds it. The parameterless ShardDbContext would read
        /// Config.js and auto-detect the server version over a live connection.
        /// </summary>
        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        [TestMethod]
        public void Migration_IsIdempotentAndCarriesTheUniqueSlugKey()
        {
            var sql = Migration();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `character_sheet_link`");
            StringAssert.Contains(sql, "PRIMARY KEY (`character_Id`)");
            StringAssert.Contains(sql, "UNIQUE KEY `character_sheet_link_slug_uidx` (`slug`)");

            // Base62 is case-significant. The server default utf8mb4_0900_ai_ci folds case, which would
            // make `AbC..` resolve the sheet whose slug is `aBc..` and shrink the guess space to 36^10.
            Assert.IsTrue(Regex.IsMatch(sql, @"`slug`\s+varchar\(16\)\s+(CHARACTER SET ascii\s+)?COLLATE ascii_bin\s+NOT NULL"),
                "the slug column must carry COLLATE ascii_bin so lookups and the UNIQUE key are case-sensitive");

            var statements = Statements();

            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.IsFalse(statements.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
            Assert.IsFalse(statements.Contains("FOREIGN KEY"), "the read path refuses deleted characters itself");
        }

        [TestMethod]
        public void EfModel_MapsTheTableAndColumns()
        {
            using var ctx = CreateContext();

            var e = ctx.Model.FindEntityType(typeof(CharacterSheetLink));

            Assert.IsNotNull(e);
            Assert.AreEqual("character_sheet_link", e.GetTableName());
            Assert.AreEqual("character_Id", e.FindProperty("CharacterId").GetColumnName());
            Assert.AreEqual("slug", e.FindProperty("Slug").GetColumnName());
            Assert.AreEqual("created_At", e.FindProperty("CreatedAt").GetColumnName());

            Assert.AreEqual("CharacterId", e.FindPrimaryKey().Properties.Single().Name);

            var slugIndex = e.GetIndexes().Single(i => i.Properties.Single().Name == "Slug");
            Assert.IsTrue(slugIndex.IsUnique, "the slug key is what turns a collision into a 1062");
            Assert.AreEqual("character_sheet_link_slug_uidx", slugIndex.GetDatabaseName());
        }

        /// <summary>
        /// UpsertCharacterSheetLink reports a slug collision by MySqlErrorCode.DuplicateKeyEntry. The
        /// MySqlConnector docs name the member (ER_DUP_ENTRY) but not its value; this pins it to 1062.
        /// </summary>
        [TestMethod]
        public void DuplicateKeyEntry_IsMySqlError1062()
        {
            // Convert rather than a cast: a cast is a compile-time constant, which MSTEST0032 flags as always true.
            Assert.AreEqual(1062, Convert.ToInt32(MySqlConnector.MySqlErrorCode.DuplicateKeyEntry));
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>thread_puzzle_ip_ledger table shape - catches an entity/migration mismatch at build time. Mirrors RewardClaimSchemaTests.</summary>
    [TestClass]
    public class ThreadPuzzleIpLedgerSchemaTests
    {
        private const string MigrationFile = "2026-10-06-00-Add-Thread-Puzzle-Ip-Ledger.sql";

        // Copied from RewardClaimSchemaTests.RepoRoot: walk up from the test output to the repo root.
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

        private static string Statements()
        {
            var sql = Migration();

            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"(?m)--.*$", string.Empty);

            return sql;
        }

        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        [TestMethod]
        public void Migration_IsIdempotentAndCarriesTheLookupIndex()
        {
            var sql = Migration();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `thread_puzzle_ip_ledger`");
            StringAssert.Contains(sql, "PRIMARY KEY (`id`)");
            StringAssert.Contains(sql, "KEY `thread_puzzle_ip_ledger_key_kind_at_idx` (`ip_Key`, `kind`, `at_Utc`)");

            Assert.IsTrue(Regex.IsMatch(sql, @"`id`\s+bigint unsigned\s+NOT NULL AUTO_INCREMENT"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`ip_Key`\s+varchar\(45\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NOT NULL"),
                "ip_Key must be ascii_bin NOT NULL so the table compares keys ordinally, as the in-memory dictionary does");
            Assert.IsTrue(Regex.IsMatch(sql, @"`kind`\s+tinyint unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`at_Utc`\s+datetime\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`until_Utc`\s+datetime\s+NULL\b"), "until_Utc is NULL on fail rows");
            Assert.IsTrue(Regex.IsMatch(sql, @"`account_Id`\s+int unsigned\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`character_Id`\s+int unsigned\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`run_Id`\s+int unsigned\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`run_Start_Group`\s+char\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`ip_Address`\s+varchar\(45\)\s+NULL\b"));

            var statements = Statements();

            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
            Assert.IsFalse(statements.Contains("FOREIGN KEY"));
            Assert.AreEqual(1, statements.Count(c => c == ';'), "exactly one statement");
        }

        [TestMethod]
        public void EfModel_MapsTheTableColumnsAndIndex()
        {
            using var ctx = CreateContext();

            var e = ctx.Model.FindEntityType(typeof(ThreadPuzzleIpLedgerRow));

            Assert.IsNotNull(e);
            Assert.AreEqual("thread_puzzle_ip_ledger", e.GetTableName());
            Assert.AreEqual("id", e.FindProperty("Id").GetColumnName());
            Assert.AreEqual("ip_Key", e.FindProperty("IpKey").GetColumnName());
            Assert.AreEqual("kind", e.FindProperty("Kind").GetColumnName());
            Assert.AreEqual("at_Utc", e.FindProperty("AtUtc").GetColumnName());
            Assert.AreEqual("until_Utc", e.FindProperty("UntilUtc").GetColumnName());
            Assert.AreEqual("account_Id", e.FindProperty("AccountId").GetColumnName());
            Assert.AreEqual("character_Id", e.FindProperty("CharacterId").GetColumnName());
            Assert.AreEqual("run_Id", e.FindProperty("RunId").GetColumnName());
            Assert.AreEqual("run_Start_Group", e.FindProperty("RunStartGroup").GetColumnName());
            Assert.AreEqual("ip_Address", e.FindProperty("IpAddress").GetColumnName());

            CollectionAssert.AreEqual(new[] { "Id" }, e.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());

            var index = e.GetIndexes().Single(i => i.GetDatabaseName() == "thread_puzzle_ip_ledger_key_kind_at_idx");
            Assert.IsFalse(index.IsUnique);
            CollectionAssert.AreEqual(new[] { "IpKey", "Kind", "AtUtc" }, index.Properties.Select(p => p.Name).ToArray());

            Assert.IsFalse(e.FindProperty("IpKey").IsNullable);
            Assert.IsTrue(e.FindProperty("UntilUtc").IsNullable, "until_Utc is NULL on fail rows");
            Assert.AreEqual(typeof(byte), e.FindProperty("Kind").GetProviderClrType(), "kind is stored as the tinyint value");
            // The prune SQL hardcodes kind = 1 / kind = 2, so pin the enum values it relies on.
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, Enum.GetValues<ThreadPuzzleIpLedgerKind>().Select(k => (byte)k).ToArray());
        }

        [TestMethod]
        public void LoadAndPrune_TranslateToSql()
        {
            // Proves the LINQ in LoadActiveThreadPuzzleIpLedger translates (no client evaluation) without a database.
            using var ctx = CreateContext();

            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var cutoff = now.AddHours(-1);

            var sql = ctx.ThreadPuzzleIpLedger
                .Where(r => (r.Kind == ThreadPuzzleIpLedgerKind.Fail && r.AtUtc > cutoff)
                         || (r.Kind == ThreadPuzzleIpLedgerKind.Lockout && r.UntilUtc > now))
                .OrderBy(r => r.Id)
                .ToQueryString();

            StringAssert.Contains(sql, "`thread_puzzle_ip_ledger`");
            StringAssert.Contains(sql, "`until_Utc`");
        }
    }
}

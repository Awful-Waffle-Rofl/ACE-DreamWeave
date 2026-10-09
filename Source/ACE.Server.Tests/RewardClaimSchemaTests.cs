using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>reward_claim table shape - catches an entity/migration mismatch at build time.</summary>
    [TestClass]
    public class RewardClaimSchemaTests
    {
        private const string MigrationFile = "2026-09-15-01-Add-Reward-Claim.sql";

        // Copied from CharacterSheetSchemaTests.RepoRoot: walk up from the test output to the repo root.
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
        public void Migration_IsIdempotentAndCarriesBothClaimKeys()
        {
            var sql = Migration();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `reward_claim`");
            StringAssert.Contains(sql, "PRIMARY KEY (`claim_Key`, `account_Id`)");
            StringAssert.Contains(sql, "UNIQUE KEY `reward_claim_ip_uidx` (`claim_Key`, `ip_Key`)");

            Assert.IsTrue(Regex.IsMatch(sql, @"`claim_Key`\s+varchar\(64\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NOT NULL"),
                "claim_Key must be ascii_bin NOT NULL so the allowlist's ordinal comparison matches the key");
            Assert.IsTrue(Regex.IsMatch(sql, @"`ip_Key`\s+varchar\(45\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NULL\b"),
                "ip_Key must be NULLable: an exempt claim stores NULL, which the UNIQUE key never compares equal");
            Assert.IsTrue(Regex.IsMatch(sql, @"`claim_Token`\s+char\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`account_Id`\s+int unsigned\s+NOT NULL"));

            var statements = Statements();

            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
            Assert.IsFalse(statements.Contains("FOREIGN KEY"));
            Assert.AreEqual(1, statements.Count(c => c == ';'), "exactly one statement");
        }

        [TestMethod]
        public void EfModel_MapsTheTableColumnsAndKeys()
        {
            using var ctx = CreateContext();

            var e = ctx.Model.FindEntityType(typeof(RewardClaim));

            Assert.IsNotNull(e);
            Assert.AreEqual("reward_claim", e.GetTableName());
            Assert.AreEqual("claim_Key", e.FindProperty("ClaimKey").GetColumnName());
            Assert.AreEqual("account_Id", e.FindProperty("AccountId").GetColumnName());
            Assert.AreEqual("ip_Key", e.FindProperty("IpKey").GetColumnName());
            Assert.AreEqual("ip_Address", e.FindProperty("IpAddress").GetColumnName());
            Assert.AreEqual("character_Id", e.FindProperty("CharacterId").GetColumnName());
            Assert.AreEqual("npc_Wcid", e.FindProperty("NpcWcid").GetColumnName());
            Assert.AreEqual("claim_Token", e.FindProperty("ClaimToken").GetColumnName());
            Assert.AreEqual("claimed_At", e.FindProperty("ClaimedAt").GetColumnName());

            CollectionAssert.AreEqual(new[] { "ClaimKey", "AccountId" }, e.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());

            var ipIndex = e.GetIndexes().Single(i => i.GetDatabaseName() == "reward_claim_ip_uidx");
            Assert.IsTrue(ipIndex.IsUnique);
            CollectionAssert.AreEqual(new[] { "ClaimKey", "IpKey" }, ipIndex.Properties.Select(p => p.Name).ToArray());

            Assert.IsTrue(e.FindProperty("IpKey").IsNullable, "ip_Key is NULL for an exempt claim");
        }
    }
}

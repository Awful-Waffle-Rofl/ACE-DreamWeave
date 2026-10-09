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

namespace ACE.Server.Tests
{
    /// <summary>
    /// Account capacity upgrade schema shape - the two tables created by
    /// Database/Updates/Shard/2026-09-17-00-Add-Account-Capacity-Upgrade.sql. Modelled on
    /// AccountMuleFormSchemaTests (text) and RewardClaimSchemaTests (EF model). Catches an
    /// entity/migration mismatch at build time; that the SQL applies twice against a real MySQL stays a
    /// manual step, because ACE.Server.Tests has no database.
    /// </summary>
    [TestClass]
    public class AccountCapacityUpgradeSchemaTests
    {
        private const string MigrationFile = "2026-09-17-00-Add-Account-Capacity-Upgrade.sql";

        private static readonly Dictionary<string, string> UpgradeColumns = new Dictionary<string, string>
        {
            { "AccountId",    "account_Id" },
            { "UpgradeKind",  "upgrade_Kind" },
            { "UpgradeCount", "upgrade_Count" },
            { "UpdatedAt",    "updated_At" },
        };

        private static readonly Dictionary<string, string> PurchaseColumns = new Dictionary<string, string>
        {
            { "PurchaseToken", "purchase_Token" },
            { "AccountId",     "account_Id" },
            { "UpgradeKind",   "upgrade_Kind" },
            { "UpgradeNumber", "upgrade_Number" },
            { "CostMmd",       "cost_Mmd" },
            { "CostPyreals",   "cost_Pyreals" },
            { "CharacterGuid", "character_Guid" },
            { "PurchasedAt",   "purchased_At" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Migration()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile)).Replace("\r\n", "\n");

        /// <summary>The migration with comments stripped, so a word in a comment cannot satisfy or fail a statement check.</summary>
        private static string Statements()
        {
            var sql = Regex.Replace(Migration(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(sql, @"(?m)--.*$", string.Empty);
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
        public void Migration_CreatesBothTables_Idempotently()
        {
            var statements = Statements();

            StringAssert.Contains(statements, "CREATE TABLE IF NOT EXISTS `account_capacity_upgrade` (");
            StringAssert.Contains(statements, "CREATE TABLE IF NOT EXISTS `account_capacity_upgrade_purchase` (");

            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.AreEqual(2, statements.Count(c => c == ';'), "exactly two statements");
        }

        [TestMethod]
        public void Migration_DeclaresNoForeignKey()
        {
            // Upgrades belong to the account and must survive the buying character being deleted.
            Assert.IsFalse(Statements().Contains("FOREIGN KEY"));
        }

        [TestMethod]
        public void Migration_ColumnTypesAndKeys()
        {
            var sql = Statements();

            StringAssert.Contains(sql, "PRIMARY KEY (`account_Id`, `upgrade_Kind`)");
            StringAssert.Contains(sql, "PRIMARY KEY (`purchase_Token`)");
            StringAssert.Contains(sql, "KEY `account_capacity_upgrade_purchase_account_idx` (`account_Id`)");

            Assert.IsTrue(Regex.IsMatch(sql, @"`upgrade_Kind`\s+tinyint unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`upgrade_Count`\s+int unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`purchase_Token`\s+char\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`upgrade_Number`\s+int unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`cost_Mmd`\s+bigint\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`cost_Pyreals`\s+bigint\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`character_Guid`\s+int unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`updated_At`\s+datetime\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`purchased_At`\s+datetime\s+NOT NULL"));
            Assert.IsFalse(sql.Contains("AUTO_INCREMENT"), "no key here is store-generated");
        }

        [TestMethod]
        public void Entities_PropertiesMatchTheColumnMaps()
        {
            var sql = Statements();

            AssertColumns(typeof(AccountCapacityUpgrade), UpgradeColumns, sql);
            AssertColumns(typeof(AccountCapacityUpgradePurchase), PurchaseColumns, sql);

            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("AccountCapacityUpgrade", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("AccountCapacityUpgradePurchase", BindingFlags.Public | BindingFlags.Instance));
        }

        private static void AssertColumns(Type entity, Dictionary<string, string> columns, string sql)
        {
            var clrProperties = entity.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();

            CollectionAssert.AreEquivalent(columns.Keys.ToList(), clrProperties, $"{entity.Name}'s CLR properties and the expected column map have diverged");

            foreach (var pair in columns)
                StringAssert.Contains(sql, $"`{pair.Value}`", $"migration does not declare {pair.Value} for {entity.Name}.{pair.Key}");
        }

        [TestMethod]
        public void EfModel_MapsTablesColumnsAndKeys()
        {
            using var ctx = CreateContext();

            var upgrade = ctx.Model.FindEntityType(typeof(AccountCapacityUpgrade));
            Assert.IsNotNull(upgrade, "AccountCapacityUpgrade is not in the model - ConfigureAccountCapacityUpgrade is not registered");
            Assert.AreEqual("account_capacity_upgrade", upgrade.GetTableName());

            foreach (var pair in UpgradeColumns)
                Assert.AreEqual(pair.Value, upgrade.FindProperty(pair.Key).GetColumnName());

            CollectionAssert.AreEqual(new[] { "AccountId", "UpgradeKind" }, upgrade.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, upgrade.FindProperty("AccountId").ValueGenerated);
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, upgrade.FindProperty("UpgradeKind").ValueGenerated);

            var purchase = ctx.Model.FindEntityType(typeof(AccountCapacityUpgradePurchase));
            Assert.IsNotNull(purchase);
            Assert.AreEqual("account_capacity_upgrade_purchase", purchase.GetTableName());

            foreach (var pair in PurchaseColumns)
                Assert.AreEqual(pair.Value, purchase.FindProperty(pair.Key).GetColumnName());

            CollectionAssert.AreEqual(new[] { "PurchaseToken" }, purchase.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, purchase.FindProperty("PurchaseToken").ValueGenerated);

            var accountIndex = purchase.GetIndexes().Single(i => i.GetDatabaseName() == "account_capacity_upgrade_purchase_account_idx");
            CollectionAssert.AreEqual(new[] { "AccountId" }, accountIndex.Properties.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void Configure_IsRegisteredInOnModelCreatingPartial()
        {
            var partial = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Database", "Models", "Shard", "BiotaPropertiesPositionPartial.cs"));

            StringAssert.Contains(partial, "ConfigureAccountCapacityUpgrade(modelBuilder)");
        }

        [TestMethod]
        public void Kind_ValuesArePersistedAndNeverZero()
        {
            // The byte values are stored in upgrade_Kind; renumbering would silently reassign every row.
            Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(CapacityUpgradeKind)));
            Assert.AreEqual((byte)1, (byte)Enum.Parse(typeof(CapacityUpgradeKind), "MuleVault"));
            Assert.AreEqual((byte)2, (byte)Enum.Parse(typeof(CapacityUpgradeKind), "MarketListings"));
            CollectionAssert.DoesNotContain(Enum.GetValues(typeof(CapacityUpgradeKind)).Cast<byte>().ToList(), (byte)0);
            Assert.AreEqual(2, Enum.GetValues(typeof(CapacityUpgradeKind)).Length);
        }
    }
}

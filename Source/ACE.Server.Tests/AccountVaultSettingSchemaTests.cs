using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Account vault settings schema shape - the account_vault_setting table created by
    /// Database/Updates/Shard/2026-10-09-00-Add-Account-Vault-Setting.sql.
    ///
    /// Same split as AccountMuleFormSchemaTests: these assertions catch an entity/migration mismatch at
    /// build time, and the expensive half (that the SQL applies twice in a row against a real MySQL)
    /// stays a manual step, because ACE.Server.Tests deliberately has no database.
    /// </summary>
    [TestClass]
    public class AccountVaultSettingSchemaTests
    {
        private const string MigrationFile = "2026-10-09-00-Add-Account-Vault-Setting.sql";

        /// <summary>
        /// CLR property name -> the quoted column name the migration must declare. Kept explicit
        /// rather than derived, so adding a property to the entity without adding its column here
        /// fails the equivalence assertion below instead of passing silently.
        /// </summary>
        private static readonly Dictionary<string, string> ExpectedColumns = new Dictionary<string, string>
        {
            { "AccountId",          "`account_Id`" },
            { "SalvageCombine",     "`salvage_Combine`" },
            { "SetByCharacterGuid", "`set_By_Character_Guid`" },
            { "UpdatedAt",          "`updated_At`" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string MigrationText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile));

        [TestMethod]
        public void Migration_CreatesTheTable_Idempotently()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `account_vault_setting`", "missing idempotent create for account_vault_setting");

            // A shard migration is tracked by FILENAME only (there is no ledger table), so it must be
            // safe to re-run. A bare CREATE TABLE or a DROP would break that.
            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(sql, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
        }

        [TestMethod]
        public void Migration_DeclaresNoForeignKey()
        {
            // Same decision as account_mule_form and the account_vault* tables: a setting must survive
            // the character that set it being deleted.
            Assert.IsFalse(MigrationText().Contains("FOREIGN KEY"),
                "account_vault_setting takes no FK - a deleted character must not cascade away an account's settings");
        }

        [TestMethod]
        public void Migration_KeysOnAccountId_AndDefaultsSalvageCombineOn()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "PRIMARY KEY (`account_Id`)", "account_vault_setting must be keyed by account_Id alone");

            // The column default must agree with the store-layer default for a missing row (ON), so a
            // row inserted without the column means the same thing as no row at all.
            Assert.IsTrue(Regex.IsMatch(sql, @"`salvage_Combine`\s+bit\(1\)\s+NOT NULL\s+DEFAULT b'1'"),
                "salvage_Combine must be bit(1) NOT NULL DEFAULT b'1'");
        }

        [TestMethod]
        public void Configure_MapsSalvageCombineAsBit1()
        {
            // Same column type the other fork bool columns use (account_vault_grant.can_Withdraw,
            // pvp_template.enabled). A mapping that disagrees with the migration only fails at runtime.
            var partial = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Database", "Models", "Shard", "AccountVaultSettingPartial.cs"));

            Assert.IsTrue(Regex.IsMatch(partial, @"HasColumnName\(""salvage_Combine""\)\s*\.HasColumnType\(""bit\(1\)""\)"),
                "AccountVaultSetting.SalvageCombine must be mapped with HasColumnType(\"bit(1)\")");
        }

        [TestMethod]
        public void Entity_ColumnNamesMatchTheMigration()
        {
            var sql = MigrationText();

            var clrProperties = typeof(AccountVaultSetting)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            CollectionAssert.AreEquivalent(ExpectedColumns.Keys.ToList(), clrProperties,
                "AccountVaultSetting's CLR properties and the expected column map have diverged");

            foreach (var pair in ExpectedColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for {pair.Key}");

            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("AccountVaultSetting", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.AccountVaultSetting DbSet is missing");
        }

        [TestMethod]
        public void Configure_IsRegisteredInOnModelCreatingPartial()
        {
            // Without this registration the DbSet still compiles and every query against it throws at
            // runtime. Nothing else in this project can catch that, because ACE.Server.Tests never
            // builds a model, so the guard is a read of the registration site's source text.
            var partial = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Database", "Models", "Shard", "BiotaPropertiesPositionPartial.cs"));

            StringAssert.Contains(partial, "ConfigureAccountVaultSetting(modelBuilder)",
                "ConfigureAccountVaultSetting is not called from ShardDbContext.OnModelCreatingPartial");
        }
    }
}

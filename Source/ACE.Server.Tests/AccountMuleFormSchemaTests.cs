using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Form Token schema shape - the account_mule_form table created by
    /// Database/Updates/Shard/2026-09-01-00-Add-Account-Mule-Form.sql.
    ///
    /// Same split as AccountVaultSchemaTests: these assertions catch an entity/migration mismatch at
    /// build time, and the expensive half (that the SQL applies twice in a row against a real MySQL)
    /// stays a manual step, because ACE.Server.Tests deliberately has no database.
    /// </summary>
    [TestClass]
    public class AccountMuleFormSchemaTests
    {
        /// <summary>
        /// CLR property name -> the quoted column name the migration must declare. Kept explicit
        /// rather than derived, so adding a property to the entity without adding its column here
        /// fails the equivalence assertion below instead of passing silently.
        /// </summary>
        private static readonly Dictionary<string, string> ExpectedColumns = new Dictionary<string, string>
        {
            { "AccountId",          "`account_Id`" },
            { "FormWcid",           "`form_Wcid`" },
            { "FormName",           "`form_Name`" },
            { "SetByCharacterGuid", "`set_By_Character_Guid`" },
            { "SetUnixTime",        "`set_Unix_Time`" },
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
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-09-01-00-Add-Account-Mule-Form.sql"));

        [TestMethod]
        public void Migration_CreatesTheTable_Idempotently()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `account_mule_form`", "missing idempotent create for account_mule_form");

            // A shard migration is tracked by FILENAME only (there is no ledger table), so it must be
            // safe to re-run. A bare CREATE TABLE or a DROP would break that.
            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"CREATE TABLE\s+`"),
                "every CREATE TABLE must be IF NOT EXISTS");
        }

        [TestMethod]
        public void Migration_DeclaresNoForeignKey()
        {
            // Deliberate, and the same decision the four account_vault* tables took: the look must
            // survive the character that set it being deleted, which is also why form_Name and
            // set_By_Character_Guid are snapshots rather than live joins.
            Assert.IsFalse(MigrationText().Contains("FOREIGN KEY"),
                "account_mule_form takes no FK - a deleted character must not cascade away an account's saved look");
        }

        [TestMethod]
        public void Entity_ColumnNamesMatchTheMigration()
        {
            var sql = MigrationText();

            var clrProperties = typeof(AccountMuleForm)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            CollectionAssert.AreEquivalent(ExpectedColumns.Keys.ToList(), clrProperties,
                "AccountMuleForm's CLR properties and the expected column map have diverged");

            foreach (var pair in ExpectedColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for {pair.Key}");

            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("AccountMuleForm", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.AccountMuleForm DbSet is missing");
        }

        [TestMethod]
        public void Configure_IsRegisteredInOnModelCreatingPartial()
        {
            // Without this registration the DbSet still compiles and every query against it throws at
            // runtime. Nothing else in this project can catch that, because ACE.Server.Tests never
            // builds a model, so the guard is a read of the registration site's source text.
            var partial = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Database", "Models", "Shard", "BiotaPropertiesPositionPartial.cs"));

            StringAssert.Contains(partial, "ConfigureAccountMuleForm(modelBuilder)",
                "ConfigureAccountMuleForm is not called from ShardDbContext.OnModelCreatingPartial");
        }
    }
}

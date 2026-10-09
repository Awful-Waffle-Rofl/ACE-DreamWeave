using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The counted item-CLASS ledger's schema shape, in the family of AccountVaultSchemaTests. Same
    /// split of responsibilities: these assertions catch an entity/migration mismatch at build time,
    /// and the expensive half - that the SQL applies twice in a row against a live MySQL - is manual,
    /// because ACE.Server.Tests deliberately has no database.
    ///
    /// Note which type is under test. ACE.Database.Models.Shard.AccountVaultClass is the ROW. The
    /// runtime descriptor that decides whether two items belong to the same class is
    /// ACE.Server.Entity.AccountVault.VaultItemClass, which has no persistence at all. They are not
    /// interchangeable and nothing converts between them; the row carries VaultItemClass's canonical
    /// form as a string.
    /// </summary>
    [TestClass]
    public class AccountVaultClassSchemaTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string MigrationText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-09-24-00-Add-Account-Vault-Class.sql"));

        [TestMethod]
        public void Migration_CreatesTheTableIdempotently_AndNeverDrops()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `account_vault_class`",
                "the class table create must be idempotent - a shard migration is tracked by FILENAME only");

            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(sql, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(sql.Contains("FOREIGN KEY"),
                "vault tables take no FK - a class row must survive character deletion, and it has no biota to point at");
        }

        /// <summary>
        /// The unique key is what makes a deposit O(1): the DAO's guarded UPDATE and its
        /// INSERT ... ON DUPLICATE KEY UPDATE both resolve the row by (account, class key) alone, so a
        /// deposit never scans the account's other holdings. Losing this index would not fail a test
        /// that only checks behaviour - it would silently turn every deposit into a table scan.
        /// </summary>
        [TestMethod]
        public void Migration_UniqueKey_IsAccountPlusClassKey()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "UNIQUE KEY `account_vault_class_uidx` (`account_Id`, `class_Key`)");
            StringAssert.Contains(sql, "KEY `account_vault_class_account_idx` (`account_Id`)");
        }

        /// <summary>
        /// account_vault_stack is deliberately NOT widened. The two ledgers count different things -
        /// stack rows count UNITS of a fungible stackable, class rows count indivisible ITEMS - and
        /// sharing a table would have forced one of those meanings onto the other.
        /// </summary>
        [TestMethod]
        public void Migration_DoesNotTouchTheStackLedger()
        {
            // Comments stripped first: the file's header explains at length WHY the stack ledger is not
            // widened, so a bare Contains would fail on the prose that documents the rule it checks.
            var sql = Regex.Replace(MigrationText(), @"/\*.*?\*/", " ", RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"--[^\r\n]*", " ");

            Assert.IsFalse(sql.Contains("account_vault_stack"),
                "the class tier is a new table, never a widening of the stack ledger");

            Assert.IsFalse(Regex.IsMatch(sql, @"\bALTER\s+TABLE\b", RegexOptions.IgnoreCase),
                "this migration only creates its own table");
        }

        [TestMethod]
        public void Entity_ExposesEveryColumnTheStoreNeeds()
        {
            foreach (var name in new[] { "Id", "AccountId", "ClassKey", "Wcid", "Count", "TotalValue", "ValueBandPct", "CanonicalForm" })
                Assert.IsNotNull(typeof(AccountVaultClass).GetProperty(name), $"AccountVaultClass.{name} is missing");
        }

        /// <summary>
        /// Both counters are long, and for the same reason AccountVaultStack.Count is: the cap on a
        /// hoard should be a number someone chose, not int overflow. total_Value especially - it is a
        /// SUM over every item in the class, so it overruns int long before the item count does.
        /// </summary>
        [TestMethod]
        public void CountAndTotalValue_AreLong()
        {
            Assert.AreEqual(typeof(long), typeof(AccountVaultClass).GetProperty("Count").PropertyType);
            Assert.AreEqual(typeof(long), typeof(AccountVaultClass).GetProperty("TotalValue").PropertyType);
        }

        [TestMethod]
        public void ShardDbContext_ExposesTheClassDbSet()
        {
            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("AccountVaultClass", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.AccountVaultClass DbSet is missing");
        }

        private static string LogMigrationText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-09-24-01-Account-Vault-Log-Value-And-Class.sql"));

        /// <summary>
        /// The audit trail's two new columns are ADDED, never baked into the shipped 2026-08-28 script.
        /// Editing that script would be invisible to every shard that has already applied it, which is
        /// every shard there is: the runner tracks scripts by FILENAME.
        /// </summary>
        [TestMethod]
        public void LogMigration_IsANewScript_AndDoesNotEditTheShippedOne()
        {
            var original = File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-08-28-00-Add-Account-Vault.sql"));

            Assert.IsFalse(original.Contains("class_Key"),
                "the shipped 2026-08-28 script must not be edited - a shard that already applied it would never see the change");

            var sql = LogMigrationText();

            StringAssert.Contains(sql, "ADD COLUMN `value` bigint NULL");
            StringAssert.Contains(sql, "ADD COLUMN `class_Key` char(32) NULL");
        }

        /// <summary>
        /// Both column adds are GUARDED. MySQL 8.0 has no ADD COLUMN IF NOT EXISTS, and the boot
        /// patcher breaks its loop on a throwing script, so an unguarded ALTER here fails on every boot
        /// after the first AND takes every later migration in the directory with it.
        /// </summary>
        [TestMethod]
        public void LogMigration_GuardsBothColumnAdds_AndIsRerunnable()
        {
            var sql = LogMigrationText();

            foreach (var column in new[] { "value", "class_Key" })
            {
                StringAssert.Contains(sql, $"COLUMN_NAME = '{column}'", $"the {column} add must check information_schema first");
            }

            StringAssert.Contains(sql, "information_schema.COLUMNS");
            Assert.AreEqual(2, Regex.Matches(sql, @"PREPARE\s+\w+\s+FROM").Count, "one SET/PREPARE/EXECUTE guard per column add");
            Assert.AreEqual(2, Regex.Matches(sql, @"DEALLOCATE PREPARE").Count, "every prepared statement must be deallocated");

            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(sql.Contains("FOREIGN KEY"), "vault tables take no FK");
        }

        /// <summary>
        /// Both columns NULLABLE, and that is a correctness requirement rather than tidiness: every row
        /// already written carries neither, and on a non-class action neither has a meaning. A default
        /// of 0 on `value` would make "this action moves no pooled value" indistinguishable from "this
        /// action moved a value of zero", which a worthless item really can carry.
        /// </summary>
        [TestMethod]
        public void LogEntity_ExposesBothNewColumns_AsNullable()
        {
            Assert.AreEqual(typeof(long?), typeof(AccountVaultLog).GetProperty("Value")?.PropertyType,
                "AccountVaultLog.Value must be a nullable long - nullable so NULL can mean 'not a class action', long because a withdraw row carries a SUM");

            Assert.AreEqual(typeof(string), typeof(AccountVaultLog).GetProperty("ClassKey")?.PropertyType,
                "AccountVaultLog.ClassKey is missing; wcid alone cannot say which of an account's pools a row touched");

            StringAssert.Contains(LogMigrationText(), "`value` bigint NULL");
            StringAssert.Contains(LogMigrationText(), "`class_Key` char(32) NULL");
        }

        /// <summary>
        /// The fold gets its own action value, appended rather than inserted, because the enum is
        /// persisted as a plain int and values are frozen once written.
        /// </summary>
        [TestMethod]
        public void FoldHasItsOwnAuditAction_Appended()
        {
            Assert.AreEqual(7, (int)ACE.Entity.Enum.AccountVaultAction.Fold,
                "Fold must be 7, appended after Restore - renumbering an existing member would relabel rows already written");

            // The six that came before it are frozen. A test that only checked Fold would not catch a
            // renumber of the others, which is the change that silently rewrites history.
            Assert.AreEqual(0, (int)ACE.Entity.Enum.AccountVaultAction.Deposit);
            Assert.AreEqual(1, (int)ACE.Entity.Enum.AccountVaultAction.Withdraw);
            Assert.AreEqual(2, (int)ACE.Entity.Enum.AccountVaultAction.Grant);
            Assert.AreEqual(3, (int)ACE.Entity.Enum.AccountVaultAction.Revoke);
            Assert.AreEqual(4, (int)ACE.Entity.Enum.AccountVaultAction.Return);
            Assert.AreEqual(5, (int)ACE.Entity.Enum.AccountVaultAction.Barrel);
            Assert.AreEqual(6, (int)ACE.Entity.Enum.AccountVaultAction.Restore);
        }

        /// <summary>
        /// The fork's real OnModelCreatingPartial lives in BiotaPropertiesPositionPartial.cs, not in
        /// ShardDbContext.cs. An entity whose Configure call is never reached still compiles, still has
        /// a DbSet, and fails only at the first query against it - so the wiring is pinned by source
        /// text here rather than left to a live-database test nobody runs.
        /// </summary>
        [TestMethod]
        public void ModelBuilder_ActuallyCallsTheClassConfiguration()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database", "Models", "Shard", "BiotaPropertiesPositionPartial.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find BiotaPropertiesPositionPartial.cs by walking up from {AppContext.BaseDirectory}");

            var src = File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Database", "Models", "Shard", "BiotaPropertiesPositionPartial.cs"));

            StringAssert.Contains(src, "ConfigureAccountVaultClass(modelBuilder);",
                "OnModelCreatingPartial never configures AccountVaultClass, so the entity is unmapped and every query against it throws");
        }
    }
}

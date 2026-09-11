using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor schema shape. These assertions are the cheap half of the migration's correctness:
    /// they catch an entity/migration mismatch at build time instead of at first write against a live
    /// shard. The expensive half - that the SQL applies twice in a row without error - is a manual
    /// step in the task, because it needs a real MySQL instance and ACE.Server.Tests deliberately has
    /// none.
    /// </summary>
    [TestClass]
    public class AccountVaultSchemaTests
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
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-08-28-00-Add-Account-Vault.sql"));

        private static string BarrelMigrationText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-09-04-00-Add-Account-Vault-Barrel.sql"));

        [TestMethod]
        public void Migration_CreatesAllFourTables_Idempotently()
        {
            var sql = MigrationText();

            foreach (var table in new[] { "account_vault", "account_vault_stack", "account_vault_grant", "account_vault_log" })
                StringAssert.Contains(sql, $"CREATE TABLE IF NOT EXISTS `{table}`", $"missing idempotent create for {table}");

            // A shard migration is tracked by FILENAME only (there is no ledger table), so it must be
            // safe to re-run. A bare CREATE TABLE or a DROP would break that.
            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"CREATE TABLE\s+`"),
                "every CREATE TABLE must be IF NOT EXISTS");
        }

        [TestMethod]
        public void Migration_DeclaresNoForeignKeyToCharacter()
        {
            // Deliberate: rows must survive character deletion, which is also why the name columns
            // are snapshots rather than live joins.
            Assert.IsFalse(MigrationText().Contains("FOREIGN KEY"),
                "account vault tables take no FK - a deleted character must not cascade away a vault or an audit row");
        }

        [TestMethod]
        public void Migration_UniqueKeys_MatchTheEntityContract()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "UNIQUE KEY `account_vault_container_uidx` (`container_Guid`)");
            StringAssert.Contains(sql, "UNIQUE KEY `account_vault_stack_uidx` (`account_Id`, `wcid`)");
            StringAssert.Contains(sql, "UNIQUE KEY `account_vault_grant_uidx` (`owner_Account_Id`, `grantee_Character_Guid`)");
        }

        [TestMethod]
        public void Entities_ExposeEveryColumnTheStoreNeeds()
        {
            AssertHasProperties(typeof(AccountVault), "Id", "AccountId", "ContainerGuid", "CreatedAt");
            AssertHasProperties(typeof(AccountVaultStack), "Id", "AccountId", "Wcid", "Count");
            AssertHasProperties(typeof(AccountVaultGrant), "Id", "OwnerAccountId", "GranteeCharacterGuid", "GranteeCharacterName", "CanWithdraw", "GrantedAt");
            AssertHasProperties(typeof(AccountVaultLog), "Id", "OwnerAccountId", "ActorCharacterGuid", "ActorCharacterName", "Action", "Wcid", "ItemGuid", "ItemName", "Count", "Timestamp");
        }

        [TestMethod]
        public void ShardDbContext_ExposesAllFourDbSets()
        {
            foreach (var name in new[] { "AccountVault", "AccountVaultStack", "AccountVaultGrant", "AccountVaultLog" })
                Assert.IsNotNull(typeof(ShardDbContext).GetProperty(name, BindingFlags.Public | BindingFlags.Instance),
                    $"ShardDbContext.{name} DbSet is missing");
        }

        [TestMethod]
        public void StackCount_IsLong_SoAHoardedStackCannotOverflow()
        {
            // The ledger holds total UNITS, independent of MaxStackSize (DESIGN 6.2). An int would
            // cap a hoard at ~2.1 billion units, which is reachable by a script and is not a limit
            // anyone chose.
            Assert.AreEqual(typeof(long), typeof(AccountVaultStack).GetProperty("Count").PropertyType);
        }

        [TestMethod]
        public void BarrelMigration_CreatesTheTableIdempotentlyAndGuardsTheColumnAdd()
        {
            var sql = BarrelMigrationText();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `account_vault_barrel`",
                "the barrel table create must be idempotent");

            // The column add is the half a bare ALTER would break: a shard migration is tracked by
            // FILENAME only, so a second run must be a no-op rather than an error that aborts the
            // rest of the patch pass. The guard is an information_schema count plus a prepared
            // statement, because MySQL has no ADD COLUMN IF NOT EXISTS.
            StringAssert.Contains(sql, "information_schema.COLUMNS");
            StringAssert.Contains(sql, "COLUMN_NAME = 'kind'");
            StringAssert.Contains(sql, "ADD COLUMN `kind`");

            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"CREATE TABLE\s+`"),
                "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(sql.Contains("FOREIGN KEY"),
                "a barrel row must survive character deletion, so it takes no FK - the name columns are snapshots");
        }

        [TestMethod]
        public void BarrelMigration_IndexesTheOpenRowScanTheReaperRuns()
        {
            var sql = BarrelMigrationText();

            StringAssert.Contains(sql, "KEY `account_vault_barrel_account_idx` (`account_Id`)");

            // The retention sweep asks for rows that are neither restored nor purged and are older
            // than a cutoff. Without this index that is a full scan of a table which, by design,
            // never has rows deleted from it.
            StringAssert.Contains(sql, "KEY `account_vault_barrel_open_idx` (`restored_At`, `purged_At`, `barreled_At`)");
        }

        [TestMethod]
        public void BarrelEntities_ExposeEveryColumnTheStoreNeeds()
        {
            AssertHasProperties(typeof(AccountVaultBarrel), "Id", "AccountId", "Wcid", "ItemGuid", "Count", "ItemName",
                "BarreledAt", "ActorCharacterGuid", "ActorCharacterName", "RestoredAt", "PurgedAt");

            // Kind is what keeps the barrel container out of the vault list. A missing property here
            // would silently load every barrel as an ordinary vault.
            AssertHasProperties(typeof(AccountVault), "Kind");
            Assert.AreEqual(typeof(int), typeof(AccountVault).GetProperty("Kind").PropertyType);
        }

        [TestMethod]
        public void BarrelRow_HasNullableItemGuidAndTerminalStamps()
        {
            // A ledger barreling has no biota, so it has no guid. A non-nullable column here would
            // force a 0, which is a real guid shape and would make a ledger row look like an item row.
            Assert.AreEqual(typeof(uint?), typeof(AccountVaultBarrel).GetProperty("ItemGuid").PropertyType);

            // Both terminal stamps are nullable, and NULL in both is what "still holds an item" means.
            Assert.AreEqual(typeof(DateTime?), typeof(AccountVaultBarrel).GetProperty("RestoredAt").PropertyType);
            Assert.AreEqual(typeof(DateTime?), typeof(AccountVaultBarrel).GetProperty("PurgedAt").PropertyType);

            // Count is long for the same reason AccountVaultStack.Count is: a ledger hoard is units,
            // not stacks, and an int would cap one barreling at ~2.1 billion units.
            Assert.AreEqual(typeof(long), typeof(AccountVaultBarrel).GetProperty("Count").PropertyType);
        }

        [TestMethod]
        public void ShardDbContext_ExposesTheBarrelDbSet()
        {
            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("AccountVaultBarrel", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.AccountVaultBarrel DbSet is missing");
        }

        private static void AssertHasProperties(Type type, params string[] names)
        {
            foreach (var name in names)
                Assert.IsNotNull(type.GetProperty(name), $"{type.Name}.{name} is missing");
        }
    }
}

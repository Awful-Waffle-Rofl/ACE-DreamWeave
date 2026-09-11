using System;
using System.IO;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Account-wide banked pyreals: schema shape. These assertions are the cheap half of the
    /// migration's correctness - they catch an entity/migration mismatch at build time instead of at
    /// first write against a live shard. The expensive half, that the SQL applies twice in a row
    /// without error, is a manual step, because it needs a real MySQL instance and ACE.Server.Tests
    /// deliberately has none.
    ///
    /// Placed beside AccountVaultSchemaTests.cs, which it is modelled on, rather than in
    /// ACE.Database.Tests: the vault's equivalent lives here precisely BECAUSE it needs no database,
    /// and ACE.Database.Tests refuses to run without one.
    /// </summary>
    [TestClass]
    public class AccountBankSchemaTests
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
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", "2026-09-01-00-Add-Account-Bank.sql"));

        [TestMethod]
        public void Migration_CreatesBothTables_Idempotently()
        {
            var sql = MigrationText();

            foreach (var table in new[] { "account_bank", "account_bank_fold" })
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
            // Deliberate: a fold row must survive character deletion, and the bulk fold deliberately
            // folds deleted characters' balances into their account's pool.
            Assert.IsFalse(MigrationText().Contains("FOREIGN KEY"),
                "account bank tables take no FK - a deleted character must not cascade away a pool or a fold record");
        }

        [TestMethod]
        public void Migration_KeysTheBankByAccountAndTheFoldByCharacter()
        {
            var sql = MigrationText();

            // The identity of each table is the whole point of its design. account_bank is per
            // ACCOUNT, which is what makes the pool shared; account_bank_fold is per CHARACTER, which
            // is what makes a fold happen exactly once however the bulk SQL and the lazy per-login
            // fold interleave. Swapping either key silently breaks the property it exists to hold.
            StringAssert.Contains(sql, "PRIMARY KEY (`account_Id`)");
            StringAssert.Contains(sql, "PRIMARY KEY (`character_Guid`)");
        }

        [TestMethod]
        public void Migration_GuardsTheFoldWithAMarker()
        {
            var sql = MigrationText();

            // The fold is NOT a no-op to re-run, and the boot patcher re-runs a file whenever
            // applied_updates.txt is absent - a container without a persisted Config volume. So the
            // guard has to live in the shard database, not in the ledger file.
            StringAssert.Contains(sql, "ace_shard_migration_marker");
            StringAssert.Contains(sql, "'fold-banked-pyreals-to-account'");
            StringAssert.Contains(sql, "START TRANSACTION");
            StringAssert.Contains(sql, "COMMIT");
        }

        [TestMethod]
        public void Migration_CreditsOnlyCharactersWithNoFoldRowYet()
        {
            var sql = MigrationText();

            // THE defect this guards. The obvious spelling of the fold credits SUM(amount) over the
            // whole fold table, which pays a second time for every character the per-login lazy path
            // already folded - and since that path exists precisely because this script may silently
            // never run, "already folded" is the expected state, not a corner case. The credit must
            // take its amounts from live 9004 rows belonging to characters that have no claim row yet.
            StringAssert.Contains(sql, "NOT EXISTS (SELECT 1 FROM `account_bank_fold`",
                "the bulk credit must exclude characters the lazy per-login fold has already paid for");

            Assert.IsFalse(sql.Contains("SUM(`amount`)"),
                "summing account_bank_fold.amount credits every already-folded character a second time");
        }

        [TestMethod]
        public void Entities_ExposeEveryColumnTheManagerNeeds()
        {
            AssertHasProperties(typeof(AccountBank), "AccountId", "BankedPyreals", "UpdatedAt");
            AssertHasProperties(typeof(AccountBankFold), "CharacterGuid", "AccountId", "Amount", "FoldedAt");
        }

        [TestMethod]
        public void ShardDbContext_ExposesBothDbSets()
        {
            foreach (var name in new[] { "AccountBank", "AccountBankFold" })
                Assert.IsNotNull(typeof(ShardDbContext).GetProperty(name, BindingFlags.Public | BindingFlags.Instance),
                    $"ShardDbContext.{name} DbSet is missing");
        }

        [TestMethod]
        public void BankedPyreals_IsLong_SoAHoardedBalanceCannotOverflow()
        {
            // The pool replaces PropertyInt64.BankedPyreals, which is a signed 64-bit property. An
            // int here would silently cap every account at ~2.1 billion pyreals, which normal play
            // reaches, and the guarded UPDATE's overflow bound assumes the full long range.
            Assert.AreEqual(typeof(long), typeof(AccountBank).GetProperty("BankedPyreals").PropertyType);
            Assert.AreEqual(typeof(long), typeof(AccountBankFold).GetProperty("Amount").PropertyType);
        }

        private static void AssertHasProperties(Type type, params string[] names)
        {
            foreach (var name in names)
                Assert.IsNotNull(type.GetProperty(name), $"{type.Name}.{name} is missing");
        }
    }
}

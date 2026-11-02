using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Round trip of the account vault settings DAO (ShardDatabase_AccountVaultSetting.cs) against a
    /// real shard database: the EF read through ConfigureAccountVaultSetting's mapping and the raw
    /// INSERT ... ON DUPLICATE KEY UPDATE.
    ///
    /// THIS WRITES ROWS. It upserts into account_vault_setting and deletes its own row afterwards. It
    /// therefore runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/> is set to 1, which is
    /// a promise that Config.js's Shard block points at a DISPOSABLE database with
    /// Database/Updates/Shard/2026-10-09-00-Add-Account-Vault-Setting.sql applied. Without it every test
    /// here reports Inconclusive.
    ///
    /// The account id is drawn from a random high range per run so leftover rows cannot collide.
    /// </summary>
    [TestClass]
    public class AccountVaultSettingRoundTripTests
    {
        private static ShardDatabase RequireDisposableShard()
        {
            if (Environment.GetEnvironmentVariable(PvpArenaRoundTripTests.OptInVariable) != "1")
                Assert.Inconclusive($"Skipped: set {PvpArenaRoundTripTests.OptInVariable}=1 only when Config.js points at a disposable shard database. This test writes rows.");

            TestEnvironment.InitializeConfig();

            var shardDb = new ShardDatabase();

            bool reachable;

            try
            {
                reachable = shardDb.Exists(false);
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive("Skipped: the shard database is not reachable with the configured Config.js.");

            return shardDb;
        }

        private static void DeleteRow(uint accountId)
        {
            using var context = new ShardDbContext();

            context.AccountVaultSetting.Where(s => s.AccountId == accountId).ExecuteDelete();
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Upsert_InsertsThenOverwrites_AndGetReadsItBack()
        {
            var shardDb = RequireDisposableShard();

            var accountId = 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);

            try
            {
                var (ok, row) = shardDb.GetAccountVaultSetting(accountId);
                Assert.IsTrue(ok, "a read of an account with no row must succeed");
                Assert.IsNull(row, "a fresh account must have no row");

                // INSERT side of the upsert.
                Assert.IsTrue(shardDb.UpsertAccountVaultSetting(new AccountVaultSetting { AccountId = accountId, SalvageCombine = false, SetByCharacterGuid = 0x50000001 }));

                (ok, row) = shardDb.GetAccountVaultSetting(accountId);
                Assert.IsTrue(ok);
                Assert.IsNotNull(row);
                Assert.IsFalse(row.SalvageCombine);
                Assert.AreEqual(0x50000001u, row.SetByCharacterGuid);
                Assert.AreNotEqual(default(DateTime), row.UpdatedAt, "updated_At must be stamped by the writer");

                // UPDATE side of the upsert.
                Assert.IsTrue(shardDb.UpsertAccountVaultSetting(new AccountVaultSetting { AccountId = accountId, SalvageCombine = true, SetByCharacterGuid = 0x50000002 }));

                (ok, row) = shardDb.GetAccountVaultSetting(accountId);
                Assert.IsTrue(ok);
                Assert.IsTrue(row.SalvageCombine);
                Assert.AreEqual(0x50000002u, row.SetByCharacterGuid);

                using (var context = new ShardDbContext())
                    Assert.AreEqual(1, context.AccountVaultSetting.Count(s => s.AccountId == accountId), "the upsert must not add a second row");
            }
            finally
            {
                DeleteRow(accountId);
            }
        }
    }
}

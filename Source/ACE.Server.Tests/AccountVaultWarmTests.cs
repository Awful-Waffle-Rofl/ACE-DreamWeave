using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /mule search all's warm-up path: AccountVaultStore.IsWarm must answer from state alone and never
    /// start a load, and AccountVaultManager's warm queue must deduplicate, cap, and rate-limit itself.
    /// </summary>
    [TestClass]
    public class AccountVaultWarmTests
    {
        private const uint AccountA = 6101;
        private const uint AccountB = 6102;
        private const uint AccountC = 6103;

        [TestInitialize]
        public void Setup()
        {
            // Store construction and loading read these; seeded so the class passes run alone. NOT read
            // first to remember a previous value: an unseeded PropertyManager read falls through to a live
            // shard database, which is exactly the trap the seeding exists to avoid. Cleanup restores the
            // shipped defaults instead.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();

            AccountVaultManager.ResetWarmQueueForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AccountVaultManager.ResetWarmQueueForTest();

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyLong("account_vault_landblock", DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item);
        }

        [TestMethod]
        public void IsWarm_OnAColdStore_IsFalse_AndReadsNothing()
        {
            var backend = new FakeVaultBackend();
            var store = new AccountVaultStore(AccountA, backend, new FakeVaultWorld());

            Assert.IsFalse(store.IsWarm);
            Assert.IsFalse(store.IsWarm);

            Assert.AreEqual(0, backend.VaultReads, "IsWarm must never start the index read - that is IsLoaded's job");
            Assert.AreEqual(0, backend.StackReads, "IsWarm must never start the ledger read");
        }

        [TestMethod]
        public void IsWarm_AfterALoad_IsTrue()
        {
            var backend = new FakeVaultBackend();
            backend.Stacks.Add(new AccountVaultStack { AccountId = AccountA, Wcid = 91001, Count = 5 });

            var store = new AccountVaultStore(AccountA, backend, new FakeVaultWorld());

            Assert.IsTrue(store.IsLoaded, "an account with no containers loads synchronously");
            Assert.IsTrue(store.IsWarm);
        }

        [TestMethod]
        public void RequestWarm_Deduplicates()
        {
            Assert.IsTrue(AccountVaultManager.RequestWarm(AccountA));
            Assert.IsTrue(AccountVaultManager.RequestWarm(AccountA));
            Assert.IsTrue(AccountVaultManager.RequestWarm(AccountB));

            Assert.AreEqual(2, AccountVaultManager.WarmQueueCount);
        }

        [TestMethod]
        public void RequestWarm_IsCapped_AndRefusesAccountZero()
        {
            Assert.IsFalse(AccountVaultManager.RequestWarm(0));

            for (uint i = 0; i < AccountVaultManager.WarmQueueCapacity; i++)
                Assert.IsTrue(AccountVaultManager.RequestWarm(70000 + i));

            Assert.IsFalse(AccountVaultManager.RequestWarm(79999), "a request past the cap is dropped, not queued");
            Assert.AreEqual(AccountVaultManager.WarmQueueCapacity, AccountVaultManager.WarmQueueCount);
        }

        /// <summary>
        /// One store per WarmIntervalSeconds, server-wide, with time injected: at t=100 the first
        /// account warms, at t=100.1 nothing does, at t=100.25 the second does.
        /// </summary>
        [TestMethod]
        public void DrainWarmQueue_WarmsOneStorePerInterval_AndReallyLoadsIt()
        {
            var backends = new Dictionary<uint, FakeVaultBackend>
            {
                { AccountA, new FakeVaultBackend() },
                { AccountB, new FakeVaultBackend() },
            };

            var stores = new Dictionary<uint, AccountVaultStore>();

            AccountVaultStore StoreFor(uint id)
            {
                if (!stores.TryGetValue(id, out var s))
                    stores[id] = s = new AccountVaultStore(id, backends[id], new FakeVaultWorld());

                return s;
            }

            AccountVaultManager.RequestWarm(AccountA);
            AccountVaultManager.RequestWarm(AccountB);

            Assert.AreEqual(AccountA, AccountVaultManager.DrainWarmQueue(100.0, StoreFor));
            Assert.AreEqual(1, backends[AccountA].VaultReads, "the warm-up must actually run the load");
            Assert.IsTrue(stores[AccountA].IsWarm);

            Assert.AreEqual(0u, AccountVaultManager.DrainWarmQueue(100.1, StoreFor), "inside the interval nothing warms");
            Assert.AreEqual(0, backends[AccountB].VaultReads);

            Assert.AreEqual(AccountB, AccountVaultManager.DrainWarmQueue(100.25, StoreFor));
            Assert.AreEqual(1, backends[AccountB].VaultReads);

            Assert.AreEqual(0, AccountVaultManager.WarmQueueCount);
        }

        [TestMethod]
        public void DrainWarmQueue_SwallowsAThrowingWarmUp_AndKeepsDraining()
        {
            AccountVaultManager.RequestWarm(AccountC);
            AccountVaultManager.RequestWarm(AccountA);

            Assert.AreEqual(AccountC, AccountVaultManager.DrainWarmQueue(10.0, id => throw new System.InvalidOperationException("boom")));

            var backend = new FakeVaultBackend();

            Assert.AreEqual(AccountA, AccountVaultManager.DrainWarmQueue(11.0, id => new AccountVaultStore(id, backend, new FakeVaultWorld())));
            Assert.AreEqual(1, backend.VaultReads);
        }

        [TestMethod]
        public void TryGetExistingStore_NeverCreatesOne()
        {
            var before = AccountVaultManager.StoreCount;

            Assert.IsFalse(AccountVaultManager.TryGetExistingStore(987654321, out var store));
            Assert.IsNull(store);
            Assert.AreEqual(before, AccountVaultManager.StoreCount);
        }
    }
}

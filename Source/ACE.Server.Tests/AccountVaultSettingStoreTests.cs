using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The store's cached salvage-combine setting. TryGetSalvageCombineEnabled must run on the mutation
    /// queue, so every read here goes through store.Enqueue.
    /// </summary>
    [TestClass]
    public class AccountVaultSettingStoreTests
    {
        private const uint OwnerAccount = 4101;
        private const uint OwnerCharacter = 0x50000011;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            VaultClassTestConfig.Seed();
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        private static bool? Read(AccountVaultStore store)
        {
            bool? result = null;

            Assert.IsTrue(store.Enqueue(() => result = store.TryGetSalvageCombineEnabled()));

            return result;
        }

        [TestMethod]
        public void MissingRow_MeansOn()
        {
            Assert.AreEqual(true, Read(NewStore()));
        }

        [TestMethod]
        public void SetFalse_PersistsToAFreshStoreOverTheSameBackend()
        {
            var store = NewStore();

            Assert.IsTrue(store.TrySetSalvageCombine(false, Owner, out var error), error);
            Assert.AreEqual(false, Read(store));

            // a fresh store has no cache, so this reads the persisted row rather than memory
            Assert.AreEqual(false, Read(NewStore()));
            Assert.AreEqual(OwnerCharacter, backend.VaultSettings[OwnerAccount].SetByCharacterGuid);
        }

        [TestMethod]
        public void SetTrue_RoundTrips()
        {
            var store = NewStore();

            Assert.IsTrue(store.TrySetSalvageCombine(false, Owner, out _));
            Assert.IsTrue(store.TrySetSalvageCombine(true, Owner, out _));

            Assert.AreEqual(true, Read(store));
            Assert.AreEqual(true, Read(NewStore()));
        }

        [TestMethod]
        public void FailedRead_IsNull_AndNotCached()
        {
            backend.FailVaultSettingRead = true;

            var store = NewStore();

            Assert.IsNull(Read(store));
            var afterFirst = backend.VaultSettingReads;

            Assert.IsNull(Read(store));
            Assert.AreEqual(afterFirst + 1, backend.VaultSettingReads, "a failed read must be retried, not cached");

            backend.FailVaultSettingRead = false;
            Assert.AreEqual(true, Read(store), "recovers once the backend does");
        }

        [TestMethod]
        public void FailedUpsert_ReturnsFalse_AndLeavesTheCacheAlone()
        {
            var store = NewStore();

            Assert.IsTrue(store.TrySetSalvageCombine(false, Owner, out _));

            backend.FailVaultSettingUpsert = true;

            Assert.IsFalse(store.TrySetSalvageCombine(true, Owner, out var error));
            Assert.IsNotNull(error);
            Assert.AreEqual(false, Read(store), "the cache must keep the last value that actually persisted");
        }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The store's cached mule form.
    ///
    /// The load-bearing property of every test here is that the form is INDEPENDENT of the vault
    /// index. A look is a cosmetic; a vault is items. The vault index refuses the store when its read
    /// fails, because serving a short vault list loses items, and the form must never inherit that
    /// refusal: a failed form read has to degrade to "no look", never to a refused summon
    /// (design section 6 step 3 and section 8).
    /// </summary>
    [TestClass]
    public class AccountMuleFormStoreTests
    {
        private const uint OwnerAccount = 4001;
        private const uint OwnerCharacter = 0x50000001;

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

        private AccountVaultStore NewStore()
        {
            return new AccountVaultStore(OwnerAccount, backend, world);
        }

        [TestMethod]
        public void GetMuleForm_ReadsTheBackendOnce_AndCaches()
        {
            backend.MuleForms[OwnerAccount] = new ACE.Database.Models.Shard.AccountMuleForm
            {
                AccountId = OwnerAccount,
                FormWcid = 1234,
                FormName = "Tusker Guard",
            };

            var store = NewStore();

            var first = store.GetMuleForm();
            var second = store.GetMuleForm();

            Assert.IsNotNull(first);
            Assert.AreEqual(1234u, first.Value.Wcid);
            Assert.AreEqual("Tusker Guard", first.Value.Name);
            Assert.AreEqual(first.Value.Wcid, second.Value.Wcid);
            Assert.AreEqual(1, backend.MuleFormReads, "the form must be read once per store, not once per summon");
        }

        [TestMethod]
        public void GetMuleForm_WithNoRow_IsNoForm_AndIsStillCached()
        {
            var store = NewStore();

            Assert.IsNull(store.GetMuleForm());
            Assert.IsNull(store.GetMuleForm());
            Assert.AreEqual(1, backend.MuleFormReads);
        }

        [TestMethod]
        public void GetMuleForm_WhenTheReadFails_IsNoForm_AndNeverThrows()
        {
            backend.FailMuleFormRead = true;
            backend.MuleForms[OwnerAccount] = new ACE.Database.Models.Shard.AccountMuleForm
            {
                AccountId = OwnerAccount,
                FormWcid = 1234,
                FormName = "Tusker Guard",
            };

            var store = NewStore();

            // Degrades to the default look for this store's lifetime, and is cached as such so a
            // flapping shard cannot put a database read on every summon.
            Assert.IsNull(store.GetMuleForm());
            Assert.IsNull(store.GetMuleForm());
            Assert.AreEqual(1, backend.MuleFormReads);
        }

        [TestMethod]
        public void GetMuleForm_DoesNotDependOnTheVaultIndexLoad()
        {
            // The two loads are independent on purpose. A vault index read failure refuses the store,
            // because a short vault list loses items; a cosmetic must not be collected by that.
            backend.FailVaultRead = true;
            backend.MuleForms[OwnerAccount] = new ACE.Database.Models.Shard.AccountMuleForm
            {
                AccountId = OwnerAccount,
                FormWcid = 1234,
                FormName = "Tusker Guard",
            };

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded, "precondition: the vault index read is failing");

            var form = store.GetMuleForm();

            Assert.IsNotNull(form, "a failed vault index read must not hide the saved look");
            Assert.AreEqual(1234u, form.Value.Wcid);
        }

        [TestMethod]
        public void TrySetMuleForm_WritesThrough_AndReplacesTheCache()
        {
            var store = NewStore();

            Assert.IsNull(store.GetMuleForm());

            Assert.IsTrue(store.TrySetMuleForm(1234, "Tusker Guard", Owner, out var failReason), failReason);
            Assert.IsNull(failReason);

            var form = store.GetMuleForm();

            Assert.IsNotNull(form);
            Assert.AreEqual(1234u, form.Value.Wcid);
            Assert.AreEqual("Tusker Guard", form.Value.Name);
            Assert.AreEqual(1, backend.UpsertMuleFormCalls);

            var stored = backend.MuleForms[OwnerAccount];
            Assert.AreEqual(OwnerCharacter, stored.SetByCharacterGuid, "the setting character is snapshotted for audit");
            Assert.IsTrue(stored.SetUnixTime > 0, "the write must stamp a time; the column carries no default");
        }

        [TestMethod]
        public void TrySetMuleForm_WhenTheUpsertFails_LeavesTheCacheAlone_AndReportsUnavailable()
        {
            var store = NewStore();

            Assert.IsTrue(store.TrySetMuleForm(1234, "Tusker Guard", Owner, out _));

            backend.FailMuleFormUpsert = true;

            Assert.IsFalse(store.TrySetMuleForm(5678, "Olthoi Soldier", Owner, out var failReason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, failReason);

            var form = store.GetMuleForm();

            Assert.AreEqual(1234u, form.Value.Wcid, "a failed write must not have replaced the cached look");
        }

        [TestMethod]
        public void TrySetMuleForm_DoesNotBumpTheVersionStamp()
        {
            // The version stamp gates PersonalVendor's item-panel rebuild. A look change is not an
            // inventory change, and bumping it would rebuild every open vendor window for a cosmetic.
            var store = NewStore();

            var before = store.Version;

            Assert.IsTrue(store.TrySetMuleForm(1234, "Tusker Guard", Owner, out _));

            Assert.AreEqual(before, store.Version);
        }
    }
}

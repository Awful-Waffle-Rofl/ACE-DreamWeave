using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// VaultPackDelivery: the withdraw-then-deliver-to-pack core extracted from the facet restore.
    /// Driven through the store's two seams (FakeVaultBackend / FakeVaultWorld) and the core's
    /// deliverToPack parameter, exactly as AccountVaultStoreTests drives the store, so no live Player,
    /// database or network layer is needed. The Player-bound WithdrawToPack wrapper is one line over
    /// this core and is NOT reached here (a Player cannot be constructed in this harness).
    /// </summary>
    [TestClass]
    public class VaultPackDeliveryTests
    {
        private const uint OwnerAccount = 4101;
        private const uint StrangerAccount = 4102;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, 0x50000101, "Packowner");
        private static readonly VaultActor Stranger = new VaultActor(StrangerAccount, 0x50000102, "Notowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        private Container SeedVaultWithOneItem()
        {
            var container = FakeVaultWorld.MakeContainer(4);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9101,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeStack(8000, 1, 100)), "could not seed a vault item");

            return container;
        }

        private static VaultEntry StoredEntry(AccountVaultStore store) =>
            store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

        private int ReturnRows() => backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Return);

        [TestMethod]
        public void Delivered_ToPack_WhenThePackTakesIt()
        {
            var vault = SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            WorldObject handed = null;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, null, item => { handed = item; return true; });

            Assert.AreEqual(VaultPackDeliveryStatus.Delivered, result.Status);
            Assert.IsTrue(result.DeliveredToPack);
            Assert.IsNotNull(handed, "the pack delivery must have been offered the object");
            Assert.AreSame(handed, result.Item);
            Assert.IsNull(result.ReturnOutcome, "a delivered object is never returned");
            Assert.AreEqual(0, vault.Inventory.Count, "the item left the vault");
            Assert.AreEqual(0, ReturnRows());
        }

        [TestMethod]
        public void Delivered_ByTryDeliverFirst_SkipsThePack()
        {
            SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            var packCalls = 0;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, item => true, item => { packCalls++; return true; });

            Assert.AreEqual(VaultPackDeliveryStatus.Delivered, result.Status);
            Assert.IsFalse(result.DeliveredToPack);
            Assert.AreEqual(0, packCalls, "a tryDeliverFirst that took the object must skip the pack delivery");
        }

        /// <summary>
        /// The behavioural half of FacetVaultRestoreOrderingTests.VaultRestore_AttemptsTheEquipBeforeItEverAnnouncesAPackDelivery:
        /// the facet's equip is its tryDeliverFirst, and it must run before the pack delivery announces the object.
        /// </summary>
        [TestMethod]
        public void TryDeliverFirst_RunsBeforeThePack_OnTheSameObject()
        {
            SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            var order = new System.Collections.Generic.List<string>();
            WorldObject offeredFirst = null, offeredPack = null;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner,
                item => { order.Add("first"); offeredFirst = item; return false; },
                item => { order.Add("pack"); offeredPack = item; return true; });

            CollectionAssert.AreEqual(new[] { "first", "pack" }, order);
            Assert.AreSame(offeredFirst, offeredPack);
            Assert.AreEqual(VaultPackDeliveryStatus.Delivered, result.Status);
            Assert.IsTrue(result.DeliveredToPack);
        }

        [TestMethod]
        public void Returned_WhenThePackRefuses_TheItemGoesBackToTheVault()
        {
            var vault = SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            // tryDeliverFirst declining must fall through to the pack, which then also declines.
            var firstCalls = 0;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, item => { firstCalls++; return false; }, item => false);

            Assert.AreEqual(1, firstCalls);
            Assert.AreEqual(VaultPackDeliveryStatus.Returned, result.Status);
            Assert.AreEqual(VaultReturnOutcome.Returned, result.ReturnOutcome);
            Assert.IsNull(result.WithdrawException);
            Assert.IsNull(result.ReturnException);
            Assert.AreEqual(1, vault.Inventory.Count);
            Assert.IsTrue(vault.Inventory.ContainsKey(result.Item.Guid), "the same object must be back in the vault");
            Assert.AreEqual(1, ReturnRows(), "the return must write its audit row");
        }

        /// <summary>
        /// The ledger arm of the plain failed-delivery path (no throw anywhere): isLedger true must route the
        /// object back through TryReturnWithdrawnToLedger, so the unit lands on the ledger again rather than
        /// being filed as a stored biota (AccountVaultStoreTests.LedgerWithdrawalRolledBack_... for why).
        /// </summary>
        [TestMethod]
        public void Returned_ToTheLedger_WhenALedgerWithdrawalsPackDeliveryFails()
        {
            const uint wcid = 1234;

            backend.Stacks.Add(new AccountVaultStack { Id = 7102, AccountId = OwnerAccount, Wcid = wcid, Count = 3 });

            var store = NewStore();
            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger);

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, true, Owner, null, item => false);

            Assert.AreEqual(VaultPackDeliveryStatus.Returned, result.Status);
            Assert.AreEqual(VaultReturnOutcome.Returned, result.ReturnOutcome);
            Assert.IsNull(result.WithdrawException);
            Assert.IsNull(result.ReturnException);

            var rows = store.GetEntries(0, -1);
            Assert.AreEqual(1, rows.Count, "the return must go back onto the ledger row, not become a stored-biota entry");
            Assert.AreEqual(VaultEntryKind.Ledger, rows.Single().Kind);
            Assert.AreEqual(3L, rows.Single().Count, "every unit must be back on the ledger");

            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw));
            Assert.AreEqual(1, ReturnRows(), "exactly one Return audit row pairs with the withdraw");
        }

        [TestMethod]
        public void Refused_WhenTryWithdrawRefuses_NothingIsDelivered()
        {
            var vault = SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            var packCalls = 0;

            // A stranger has no access to this vault: TryWithdraw refuses cleanly.
            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Stranger, null, item => { packCalls++; return true; });

            Assert.AreEqual(VaultPackDeliveryStatus.Refused, result.Status);
            Assert.IsFalse(result.StoreUnavailable);
            Assert.IsFalse(string.IsNullOrEmpty(result.FailReason), "a clean refusal carries the store's reason");
            Assert.IsNull(result.Item);
            Assert.AreEqual(0, packCalls);
            Assert.AreEqual(1, vault.Inventory.Count, "a refused withdraw moves nothing");
        }

        [TestMethod]
        public void Refused_StoreUnavailable_WhenTheStoreWasRetired()
        {
            SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            Assert.IsTrue(store.TryEvict(store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, null, item => true);

            Assert.AreEqual(VaultPackDeliveryStatus.Refused, result.Status);
            Assert.IsTrue(result.StoreUnavailable);
            Assert.IsNull(result.FailReason);
        }

        [TestMethod]
        public void Stranded_WhenThePackRefusesAndTheVaultRefusesTheReturn()
        {
            var vault = SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            // The vault stops being ready between the withdraw and the return, so TryReturnWithdrawn
            // refuses cleanly (AccountVaultStoreTests.ReturnWithdrawn_WhenRefused_WritesNoAuditRow, refusal 1).
            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, null, item =>
            {
                FakeVaultWorld.SetInventoryLoaded(vault, false);
                return false;
            });

            Assert.AreEqual(VaultPackDeliveryStatus.Stranded, result.Status);
            Assert.AreEqual(VaultReturnOutcome.Refused, result.ReturnOutcome);
            Assert.IsNull(result.ReturnException);
            Assert.IsNotNull(result.Item, "the stranded object must be handed back so the caller can name it");
            Assert.AreEqual(0, ReturnRows());
        }

        [TestMethod]
        public void Threw_WhenTheReturnThrows()
        {
            SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            // The non-ledger return saves the item after re-adding it; making that save throw is the
            // runtime/DB split TryReturnWithdrawn's remarks describe.
            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, null, item =>
            {
                world.ThrowFromSaveBiotaCount = 1;
                return false;
            });

            Assert.AreEqual(VaultPackDeliveryStatus.Threw, result.Status);
            Assert.AreEqual(VaultReturnOutcome.Threw, result.ReturnOutcome);
            Assert.IsNotNull(result.ReturnException, "the return's exception must reach the caller to be logged");
            Assert.IsNull(result.WithdrawException, "the withdraw itself did not throw");
        }

        [TestMethod]
        public void Threw_WhenTryWithdrawThrowsWithNothingRecovered()
        {
            SeedVaultWithOneItem();
            var store = NewStore();
            var entry = StoredEntry(store);

            var packCalls = 0;

            // WithdrawItem saves the vault container BEFORE adding the item to `withdrawn`, so a throw
            // there leaves the caller nothing to return.
            world.ThrowFromSaveBiotaCount = 1;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, false, Owner, null, item => { packCalls++; return true; });

            Assert.AreEqual(VaultPackDeliveryStatus.Threw, result.Status);
            Assert.IsNotNull(result.WithdrawException);
            Assert.IsNull(result.ReturnOutcome, "nothing was recovered, so no return was attempted");
            Assert.IsNull(result.Item);
            Assert.AreEqual(0, packCalls);
        }

        [TestMethod]
        public void Returned_WhenTryWithdrawThrowsAfterProducingALedgerObject()
        {
            const uint wcid = 1234;

            backend.Stacks.Add(new AccountVaultStack { Id = 7101, AccountId = OwnerAccount, Wcid = wcid, Count = 1 });

            var store = NewStore();
            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger);

            // WithdrawFromLedger debits the ledger and builds the object, THEN reaps the empty row; a
            // throw from the reap leaves a real object in `withdrawn` with TryWithdraw's result unset.
            backend.ThrowFromDeleteEmptyStacksCount = 1;

            var packCalls = 0;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 1, true, Owner, null, item => { packCalls++; return true; });

            Assert.IsNotNull(result.WithdrawException, "the fixture must reach the throw-after-produce path, or this test proves nothing");
            Assert.AreEqual(VaultReturnOutcome.Returned, result.ReturnOutcome, "the recovered object must go back through the ledger return");
            Assert.AreEqual(VaultPackDeliveryStatus.Returned, result.Status);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual(0, packCalls, "a throwing withdraw must never deliver");
            Assert.AreEqual(1L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger).Count, "the unit must be back on the ledger");
        }

        /// <summary>
        /// A withdraw that produces MORE than one object (a ledger amount above one stack) delivers only the
        /// first, and must send every other one straight back rather than leave it unreferenced. Before the
        /// extras path existed the two extra stacks here were out of the vault and in nobody's hands.
        /// </summary>
        [TestMethod]
        public void Extras_BeyondTheFirstObject_AreReturnedToTheLedger_AndReported()
        {
            const uint wcid = 1235;

            backend.Stacks.Add(new AccountVaultStack { Id = 7103, AccountId = OwnerAccount, Wcid = wcid, Count = 5 });
            world.ItemMaxStackSize = 1;

            var store = NewStore();
            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger);

            var packCalls = 0;

            var result = VaultPackDelivery.WithdrawAndDeliver(store, entry, 3, true, Owner, null, item => { packCalls++; return true; });

            Assert.AreEqual(VaultPackDeliveryStatus.Delivered, result.Status);
            Assert.AreEqual(1, packCalls, "only the first object is ever offered for delivery");
            Assert.AreEqual(2, result.ExtraReturns.Count, "both extra stacks must be reported");
            Assert.IsTrue(result.ExtraReturns.All(e => e.Outcome == VaultReturnOutcome.Returned));
            Assert.IsTrue(result.ExtraReturns.All(e => !ReferenceEquals(e.Item, result.Item)));
            Assert.AreEqual(4L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger).Count,
                "5 - 3 withdrawn + 2 returned: exactly the one delivered unit left the ledger");
            Assert.AreEqual(2, ReturnRows(), "one Return audit row per extra");
        }

        [TestMethod]
        public void Extras_AreEmpty_ForASingleObjectWithdraw()
        {
            SeedVaultWithOneItem();
            var store = NewStore();

            var result = VaultPackDelivery.WithdrawAndDeliver(store, StoredEntry(store), 1, false, Owner, null, item => true);

            Assert.AreEqual(VaultPackDeliveryStatus.Delivered, result.Status);
            Assert.AreEqual(0, result.ExtraReturns.Count);
        }

        /// <summary>
        /// A CLASS line's undeliverable object goes back through TryReturnWithdrawnToClass: the class row is
        /// whole again and no stored biota appears. Without the class dispatch (isLedger false routes it to
        /// TryReturnWithdrawn) the item lands as a separate stored entry beside a class row one short.
        /// </summary>
        [TestMethod]
        public void Returned_ToTheClassRow_WhenAClassWithdrawalsPackDeliveryFails()
        {
            SeedVaultWithNoItems();
            world.PristineResult = false;
            world.ClassifyResult = true;

            var store = NewStore();

            for (var i = 0; i < 3; i++)
                DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(21014, 100, 77, 12, 640));

            var classEntry = store.GetEntries(0, -1).Single();
            Assert.AreEqual(VaultEntryKind.Class, classEntry.Kind, "precondition: the bags folded into one class row");
            Assert.AreEqual(3L, classEntry.Count);

            var result = VaultPackDelivery.WithdrawAndDeliver(store, classEntry, 1, false, Owner, null, item => false);

            Assert.AreEqual(VaultPackDeliveryStatus.Returned, result.Status);
            Assert.AreEqual(VaultReturnOutcome.Returned, result.ReturnOutcome);

            var rows = store.GetEntries(0, -1);
            Assert.AreEqual(1, rows.Count, "the return must go back onto the class row, not become a stored biota");
            Assert.AreEqual(VaultEntryKind.Class, rows.Single().Kind);
            Assert.AreEqual(3L, rows.Single().Count, "the class row must be whole again");
        }

        private void SeedVaultWithNoItems()
        {
            var container = FakeVaultWorld.MakeContainer(16);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9102,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });
        }

        private static void DepositOnQueue(AccountVaultStore store, WorldObject item)
        {
            var ok = false;
            string failReason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out failReason));

            Assert.IsTrue(ok, $"seed deposit refused: {failReason}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Salvage Roller's escrow containers (Docs/SalvageRoller/DESIGN.md 5.8): account_vault rows of
    /// kind 2 that hold a batch's target and Hammers.
    ///
    /// Two families of test. The EXCLUSIONS mirror AccountVaultBarrelTests: an escrowed item must never
    /// be listed, counted against the cap, withdrawn, folded or reaped, and none of those exclusions
    /// fails to compile if it is missed. The OPERATIONS pin the save rules copied from the vault -
    /// into escrow saves the item, out of escrow does not.
    ///
    /// Driven through FakeVaultBackend and FakeVaultWorld, so none of this needs a database.
    /// </summary>
    [TestClass]
    public class AccountVaultEscrowTests
    {
        private const uint OwnerAccount = 4201;

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();




            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000),
                "account_vault_entry_cap is missing from DefaultLongProperties");

            // The escrow's lazy container creation reads this key through ReservedVaultLocation; seeded
            // so PropertyManager answers from its cache rather than opening a ShardDbContext.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();
        }

        [TestCleanup]
        public void Teardown()
        {
            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyLong("account_vault_landblock", DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item);
        }

        // ---- helpers ----

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        private uint nextSeedOrder = 1;

        private Container SeedContainer(int kind, int capacity = AccountVaultStore.VaultItemCapacity)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9000 + nextSeedOrder,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextSeedOrder),
                Kind = kind,
            });

            nextSeedOrder++;

            return container;
        }

        private void SeedUnloadableContainer(int kind)
        {
            var guid = FakeVaultWorld.NextGuid();

            world.FailLoadContainerGuids.Add(guid);

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9000 + nextSeedOrder,
                AccountId = OwnerAccount,
                ContainerGuid = guid,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextSeedOrder),
                Kind = kind,
            });

            nextSeedOrder++;
        }

        /// <summary>A loose, unparented object, the shape the sell path hands the escrow after its detach.</summary>
        private static WorldObject LooseItem(uint wcid = 9001) => FakeVaultWorld.MakeStack(wcid, 1, 100);

        private static bool Escrow(AccountVaultStore store, WorldObject item, out uint containerGuid, out string failReason)
        {
            var ok = false;
            uint guid = 0;
            string reason = null;

            store.Enqueue(() => ok = store.TryEscrow(item, out guid, out reason));

            containerGuid = guid;
            failReason = reason;
            return ok;
        }

        private static bool Release(AccountVaultStore store, uint itemGuid, out WorldObject item, out string failReason)
        {
            var ok = false;
            WorldObject released = null;
            string reason = null;

            store.Enqueue(() => ok = store.TryReleaseEscrow(itemGuid, out released, out reason));

            item = released;
            failReason = reason;
            return ok;
        }

        private static bool ReturnToEscrow(AccountVaultStore store, WorldObject item, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryReturnToEscrow(item, out reason));

            failReason = reason;
            return ok;
        }

        private static bool DestroyEscrowed(AccountVaultStore store, uint itemGuid, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDestroyEscrowed(itemGuid, out reason));

            failReason = reason;
            return ok;
        }

        /// <summary>A store with one vault container and one escrow container already holding <paramref name="items"/> items.</summary>
        private AccountVaultStore StoreWithEscrowHolding(out List<WorldObject> escrowed, int items = 1)
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);

            var escrowContainer = SeedContainer(AccountVaultStore.EscrowContainerKind);

            escrowed = new List<WorldObject>();

            for (var i = 0; i < items; i++)
            {
                var item = LooseItem();

                Assert.IsTrue(escrowContainer.TryAddToInventory(item), "could not seed an escrowed item");
                escrowed.Add(item);
            }

            return NewStore();
        }

        // ---- the exclusions ----

        [TestMethod]
        public void Load_PutsAKindTwoContainerInTheEscrowAndNotInTheVaults()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            SeedContainer(AccountVaultStore.EscrowContainerKind);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);
            Assert.AreEqual(1, store.VaultContainerCountForTest, "an escrow container must not be counted as a vault");
            Assert.AreEqual(1, store.EscrowContainerCountForTest, "the kind 2 row must have loaded as escrow");
            Assert.IsFalse(store.HasBarrelForTest, "kind 2 is not the barrel either");
            Assert.IsTrue(store.IsEscrowReady(out _));
        }

        [TestMethod]
        public void EscrowContents_DoNotAppearInGetEntries()
        {
            var store = StoreWithEscrowHolding(out var escrowed, items: 3);

            var entries = store.GetEntries(0, -1);

            Assert.AreEqual(0, entries.Count, "the vault container is empty and escrow is not part of the vault listing");
            Assert.IsNotNull(store.FindEscrowed(escrowed[0].Guid.Full), "control: the item really is in escrow");
        }

        [TestMethod]
        public void EscrowContents_DoNotCountAgainstTheVaultCap()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = StoreWithEscrowHolding(out _, items: 5);

            Assert.AreEqual(0, store.EntryCount, "escrowed items are not vault entries");
            Assert.IsTrue(store.HasRoomFor(1), "a full escrow must never make the vault look full");
        }

        [TestMethod]
        public void EscrowContents_CannotBeFoldedIntoTheLedger()
        {
            // Positive control in the same store: a classifiable VAULT item folds into the class ledger,
            // so a fold pass really ran. The escrowed item, equally classifiable, must not be touched.
            // The fold is the class-tier migration, so it needs the class predicate ON and the pristine
            // check OFF (AccountVaultClassStorageTests' setup).
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var escrowContainer = SeedContainer(AccountVaultStore.EscrowContainerKind);

            var stored = FakeVaultWorld.MakeSalvageBag(21013, 100, 7, 10, 101, "Salvage (100)");
            var escrowedItem = FakeVaultWorld.MakeSalvageBag(21013, 100, 7, 10, 102, "Salvage (100)");

            Assert.IsTrue(vault.TryAddToInventory(stored));
            Assert.IsTrue(escrowContainer.TryAddToInventory(escrowedItem));

            world.PristineResult = false;
            world.ClassifyResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);

            var folded = 0;
            store.Enqueue(() => folded = store.FoldSomeStoredItemsOnQueue(1000.0, 100, out _));

            Assert.AreEqual(1, folded, "control: the classifiable vault item must fold, or this test proves nothing");
            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == escrowedItem.Guid.Full), "an escrowed item must never be folded away");
            Assert.IsNotNull(store.FindEscrowed(escrowedItem.Guid.Full));
        }

        [TestMethod]
        public void Reap_NeverTakesAnEscrowContainer()
        {
            var escrowContainer = SeedContainer(AccountVaultStore.EscrowContainerKind);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);
            Assert.IsFalse(store.TryReapEmptyVault(escrowContainer), "an empty escrow container is not an empty vault");
            Assert.AreEqual(1, store.EscrowContainerCountForTest);
            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == escrowContainer.Guid.Full));
        }

        [TestMethod]
        public void Load_AnEscrowThatWillNotLoad_DegradesTheEscrowAndNotTheVault()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            SeedContainer(AccountVaultStore.EscrowContainerKind);
            SeedUnloadableContainer(AccountVaultStore.EscrowContainerKind);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the vault must still work when only an escrow container is unreadable");
            Assert.IsTrue(store.EscrowDegradedForTest);
            Assert.IsFalse(store.IsEscrowReady(out var reason), "the whole escrow refuses: a batch's items may be in the unreadable container");
            Assert.AreEqual(AccountVaultStore.EscrowDegradedMessage, reason);

            Assert.IsFalse(Escrow(store, LooseItem(), out _, out reason));
            Assert.AreEqual(AccountVaultStore.EscrowDegradedMessage, reason);
        }

        [TestMethod]
        public void Degraded_HasItsOwnLine_ThatDoesNotInviteARetry()
        {
            // a degraded escrow stays degraded until someone repairs it, so its line must not read as transient
            Assert.AreNotEqual(AccountVaultStore.EscrowUnavailableMessage, AccountVaultStore.EscrowDegradedMessage);
            Assert.IsFalse(AccountVaultStore.EscrowDegradedMessage.IndexOf("try again", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.IsFalse(AccountVaultStore.EscrowDegradedMessage.IndexOf("in a moment", StringComparison.OrdinalIgnoreCase) >= 0);

            // control: the transient line still does
            StringAssert.Contains(AccountVaultStore.EscrowUnavailableMessage, "Try again in a moment");
        }

        [TestMethod]
        public void Load_AVaultThatWillNotLoad_StillRefusesTheWholeStore()
        {
            // Control for the test above: the escrow's lenient arm must not have made the loop lenient.
            SeedContainer(AccountVaultStore.EscrowContainerKind);
            SeedUnloadableContainer(AccountVaultStore.VaultContainerKind);

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded);
            Assert.IsFalse(store.IsEscrowReady(out _), "an unloadable store cannot serve escrow either");
        }

        [TestMethod]
        public void Escrow_AnItemInEscrowCannotBeWithdrawnThroughAVaultEntry()
        {
            var store = StoreWithEscrowHolding(out var escrowed);

            // The vault listing is the only way to name something to withdraw, and it is empty.
            Assert.IsFalse(store.GetEntries(0, -1).Any(e => e.Kind == VaultEntryKind.StoredItem && e.Guid.Full == escrowed[0].Guid.Full));
        }

        // ---- the operations ----

        [TestMethod]
        public void TryEscrow_CreatesAKindTwoContainerAndSavesTheItem()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            var item = LooseItem();

            Assert.IsTrue(Escrow(store, item, out var containerGuid, out var reason), reason);

            Assert.AreEqual(1, backend.Vaults.Count(v => v.AccountId == OwnerAccount && v.Kind == AccountVaultStore.EscrowContainerKind),
                "the first escrow creates exactly one kind 2 index row");
            Assert.AreEqual(containerGuid, backend.Vaults.Single(v => v.Kind == AccountVaultStore.EscrowContainerKind).ContainerGuid);
            Assert.AreEqual(containerGuid, item.ContainerId, "the item is parented to the escrow container");
            Assert.IsTrue(world.Saved.Contains(item), "the item's own save records its new home");
            Assert.AreEqual(0, store.EntryCount, "and it is still not a vault entry");
            Assert.AreEqual(1, store.VaultContainerCountForTest, "no vault container was created for it");
        }

        [TestMethod]
        public void TryEscrow_AParentedItem_IsRefusedAndNothingMoves()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var item = LooseItem();
            Assert.IsTrue(vault.TryAddToInventory(item));

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            Assert.IsFalse(Escrow(store, item, out _, out _));
            Assert.AreEqual(vault.Guid.Full, item.ContainerId);
            Assert.AreEqual(0, store.EscrowContainerCountForTest, "a refusal creates no escrow container");
        }

        [TestMethod]
        public void TryEscrow_AFullEscrowContainer_OpensASecondOne()
        {
            SeedContainer(AccountVaultStore.EscrowContainerKind, capacity: 1);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            Assert.IsTrue(Escrow(store, LooseItem(), out var first, out _));
            Assert.IsTrue(Escrow(store, LooseItem(), out var second, out _));

            Assert.AreNotEqual(first, second);
            Assert.AreEqual(2, store.EscrowContainerCountForTest);
            Assert.AreEqual(2, backend.Vaults.Count(v => v.Kind == AccountVaultStore.EscrowContainerKind));
        }

        [TestMethod]
        public void TryEscrow_AFailedItemSave_IsRetriedOnTheNextDrain()
        {
            SeedContainer(AccountVaultStore.EscrowContainerKind);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            var item = LooseItem();
            world.FailSaveBiotaCounts[item.Guid.Full] = 1;

            Assert.IsTrue(Escrow(store, item, out _, out _));
            Assert.AreEqual(1, world.SaveBiotaCalls(item));
            Assert.IsFalse(world.Saved.Contains(item));

            store.Enqueue(() => { });

            Assert.AreEqual(2, world.SaveBiotaCalls(item), "the next drain retries the escrow save");
            Assert.IsTrue(world.Saved.Contains(item));
        }

        [TestMethod]
        public void TryReleaseEscrow_HandsTheItemBackUnparentedAndDoesNotResaveIt()
        {
            var store = StoreWithEscrowHolding(out var escrowed);
            Assert.IsTrue(store.IsLoaded);

            var item = escrowed[0];
            var savesBefore = world.SaveBiotaCalls(item);

            Assert.IsTrue(Release(store, item.Guid.Full, out var released, out var reason), reason);

            Assert.AreSame(item, released);
            Assert.IsNull(released.ContainerId);
            Assert.AreEqual(savesBefore, world.SaveBiotaCalls(item),
                "the withdraw rule: the released item is not re-saved, so a crash leaves its row pointing at escrow");
            Assert.IsNull(store.FindEscrowed(item.Guid.Full));
        }

        [TestMethod]
        public void TryReleaseEscrow_AGuidTheEscrowDoesNotHold_IsRefused()
        {
            var store = StoreWithEscrowHolding(out _);
            Assert.IsTrue(store.IsLoaded);

            Assert.IsFalse(Release(store, FakeVaultWorld.NextGuid(), out var item, out var reason));
            Assert.IsNull(item);
            Assert.AreEqual(AccountVaultStore.EscrowItemGoneMessage, reason);
        }

        [TestMethod]
        public void TryReturnToEscrow_PutsAReleasedItemBackAndSavesIt()
        {
            var store = StoreWithEscrowHolding(out var escrowed);
            Assert.IsTrue(store.IsLoaded);

            var item = escrowed[0];

            Assert.IsTrue(Release(store, item.Guid.Full, out var released, out _));

            var savesBefore = world.SaveBiotaCalls(item);

            Assert.IsTrue(ReturnToEscrow(store, released, out var reason), reason);

            Assert.IsNotNull(store.FindEscrowed(item.Guid.Full));
            Assert.AreEqual(savesBefore + 1, world.SaveBiotaCalls(item), "a return saves the item, like an escrow");
        }

        [TestMethod]
        public void TryDestroyEscrowed_DestroysTheItemAndDropsAPendingRetry()
        {
            SeedContainer(AccountVaultStore.EscrowContainerKind);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            var hammer = LooseItem(9200);

            // TWO failures: the escrow save, and the retry that the destroy's own drain runs AHEAD of
            // the destroy. The item is then still pending when the destroy runs, which is the case the
            // drop exists for. With one failure the retry would have succeeded first and this test
            // would pass whether or not the drop is there.
            world.FailSaveBiotaCounts[hammer.Guid.Full] = 2;

            Assert.IsTrue(Escrow(store, hammer, out _, out _));
            Assert.IsTrue(DestroyEscrowed(store, hammer.Guid.Full, out var reason), reason);

            var callsAfterDestroy = world.SaveBiotaCalls(hammer);
            Assert.AreEqual(2, callsAfterDestroy, "precondition: the escrow save and one retry both ran and failed");

            store.Enqueue(() => { });

            Assert.IsTrue(world.Destroyed.Contains(hammer));
            Assert.IsNull(store.FindEscrowed(hammer.Guid.Full));
            Assert.AreEqual(callsAfterDestroy, world.SaveBiotaCalls(hammer), "a destroyed Hammer's failed escrow save must never be retried");
        }

        [TestMethod]
        public void EscrowOperations_OffTheMutationQueue_Throw()
        {
            var store = StoreWithEscrowHolding(out var escrowed);
            Assert.IsTrue(store.IsLoaded);

            Assert.ThrowsExactly<InvalidOperationException>(() => store.TryReleaseEscrow(escrowed[0].Guid.Full, out _, out _));
        }
    }
}

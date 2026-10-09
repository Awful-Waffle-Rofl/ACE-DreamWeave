using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor: the account vault store (DESIGN sections 5, 7, 7.1, 7.3, 10; risks R2, R3, R4,
    /// R7, R8).
    ///
    /// Every test here is named for the thing it protects rather than for the method it calls, because
    /// each one stands in for a failure that is silent in production: a vault that loses 211 slots to
    /// a byte wrap, a withdraw that transacts against a vault the store cannot see yet, a reap that
    /// destroys a container whose contents merely have not arrived, a transient shard read that gets
    /// mistaken for an empty vault.
    ///
    /// The store is driven through its two seams - IAccountVaultBackend and IAccountVaultWorldSource -
    /// so none of this needs a database or a network layer.
    /// </summary>
    [TestClass]
    public class AccountVaultStoreTests
    {
        /// <summary>
        /// Terse access read for assertions. The production-side collapsing GetAccess overload was
        /// DELETED by Task 12's reconciliation pass - three of five production sites had taken it and
        /// reported "you do not have permission" to players during a database outage - so this local
        /// helper exists to keep the assertions one-liners WITHOUT putting a bool-discarding wrapper
        /// back where production code could reach it. Any test that cares about the read-failure case
        /// must call TryGetAccess directly and assert on failReason; this helper cannot see it.
        /// </summary>
        private static VaultAccess AccessOf(AccountVaultStore store, Player player)
        {
            return store.TryGetAccess(VaultActor.From(player), out var access, out _) ? access : VaultAccess.None;
        }

        /// <summary>Same as the Player overload, for the sites that already hold a VaultActor.</summary>
        private static VaultAccess AccessOf(AccountVaultStore store, VaultActor actor)
        {
            return store.TryGetAccess(actor, out var access, out _) ? access : VaultAccess.None;
        }

        private const uint OwnerAccount = 4001;
        private const uint StrangerAccount = 4002;

        private const uint OwnerCharacter = 0x50000001;
        private const uint StrangerCharacter = 0x50000002;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");
        private static readonly VaultActor Stranger = new VaultActor(StrangerAccount, StrangerCharacter, "Somebodyelse");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // Also asserts that Step 1 actually registered the key: ModifyLong returns false for a key
            // that is not in DefaultLongProperties.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000),
                "account_vault_entry_cap is missing from DefaultLongProperties");

            // Seeded so PropertyManager's cache answers it. AccountVaultStore.ReservedVaultLocation
            // reads this key on the CreateVault path, and an uncached PropertyManager.GetLong falls
            // through to DatabaseManager.ShardConfig, which opens a ShardDbContext - so without this an
            // ordinary unit test needs a live MySQL, and only if this class happens to run after the one
            // class that seeds the key.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();
        }

        private AccountVaultStore NewStore()
        {
            return new AccountVaultStore(OwnerAccount, backend, world);
        }

        private Container SeedVault(int capacity, int itemCount, int order)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = (uint)(9000 + order),
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
            });

            for (var i = 0; i < itemCount; i++)
                Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeStack(8000, 1, 100)), "could not seed a vault item");

            return container;
        }

        private void SeedLedger(uint wcid, long count)
        {
            backend.Stacks.Add(new AccountVaultStack { Id = (uint)(7000 + backend.Stacks.Count), AccountId = OwnerAccount, Wcid = wcid, Count = count });
        }

        private static bool Deposit(AccountVaultStore store, WorldObject item, VaultActor actor, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, actor, out reason));

            failReason = reason;
            return ok;
        }

        private static bool Withdraw(AccountVaultStore store, VaultEntry entry, int amount, VaultActor actor, out List<WorldObject> withdrawn, out string failReason)
        {
            return Withdraw(store, entry, amount, actor, GroupTakeOrder.Front, out withdrawn, out failReason);
        }

        private static bool Withdraw(AccountVaultStore store, VaultEntry entry, int amount, VaultActor actor,
                                     GroupTakeOrder takeOrder, out List<WorldObject> withdrawn, out string failReason)
        {
            var ok = false;
            List<WorldObject> got = null;
            string reason = null;

            store.Enqueue(() => ok = store.TryWithdraw(entry, amount, actor, out got, out reason, takeOrder));

            withdrawn = got ?? new List<WorldObject>();
            failReason = reason;
            return ok;
        }

        // ---------------------------------------------------------------- R7

        [TestMethod]
        public void VaultCapacity_IsPinnedTo255_IgnoringWeenieData()
        {
            world.SeedVaultItemsCapacity = 300;

            // Control: prove the wrap this test exists to defeat is really live. ItemCapacity is a
            // (byte?) cast over PropertyInt.ItemsCapacity, so an unpinned 300 reads back as 44.
            var unpinned = world.CreateNewWorldObject(AccountVaultStore.VaultContainerWcid);
            Assert.AreEqual(300, unpinned.GetProperty(PropertyInt.ItemsCapacity).Value);
            Assert.AreEqual((byte)44, unpinned.ItemCapacity.Value, "the one-byte wrap must be reachable, or this test proves nothing");

            world.CreatedContainers.Clear();

            var store = NewStore();

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason), reason);

            Assert.AreEqual(1, world.CreatedContainers.Count);

            var vault = world.CreatedContainers[0];

            Assert.AreEqual(AccountVaultStore.VaultItemCapacity, vault.GetProperty(PropertyInt.ItemsCapacity).Value,
                "vault capacity must be pinned in code, never read from the weenie");
            Assert.AreEqual((byte)255, vault.ItemCapacity.Value);
        }

        // ---------------------------------------------------------------- DESIGN 7.1

        [TestMethod]
        public void Insertion_UsesFirstVaultWithAFreeSlot_NotTheNewest()
        {
            var oldest = SeedVault(capacity: 2, itemCount: 1, order: 1);
            var newest = SeedVault(capacity: 2, itemCount: 0, order: 2);

            var store = NewStore();

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason), reason);

            Assert.AreEqual(2, oldest.Inventory.Count, "the oldest vault with a free slot must take the item");
            Assert.AreEqual(0, newest.Inventory.Count, "the newest vault must not be preferred");
            Assert.AreEqual(0, backend.AddedVaults, "no new vault was needed");
        }

        [TestMethod]
        public void Insertion_AfterFragmentation_RefillsTheHole()
        {
            var oldest = SeedVault(capacity: 2, itemCount: 2, order: 1);
            var newest = SeedVault(capacity: 2, itemCount: 1, order: 2);

            var store = NewStore();

            var targetGuid = oldest.Inventory.Keys.First().Full;
            var entry = store.GetEntries(0, int.MaxValue).First(e => e.Kind == VaultEntryKind.StoredItem && e.Guid.Full == targetGuid);

            Assert.IsTrue(Withdraw(store, entry, 1, Owner, out var withdrawn, out var reason), reason);
            Assert.AreEqual(1, withdrawn.Count);
            Assert.AreEqual(1, oldest.Inventory.Count, "the withdraw left a hole in the oldest vault");

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out reason), reason);

            Assert.AreEqual(2, oldest.Inventory.Count, "the next deposit must refill the hole");
            Assert.AreEqual(1, newest.Inventory.Count);
            Assert.AreEqual(0, backend.AddedVaults);
        }

        [TestMethod]
        public void Insertion_CreatesANewVault_OnlyWhenAllAreFull()
        {
            var first = SeedVault(capacity: 1, itemCount: 1, order: 1);
            var second = SeedVault(capacity: 1, itemCount: 1, order: 2);

            var store = NewStore();

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason), reason);

            Assert.AreEqual(1, backend.AddedVaults, "a third vault is created only because both existing ones are full");
            Assert.AreEqual(3, backend.Vaults.Count);
            Assert.AreEqual(1, first.Inventory.Count);
            Assert.AreEqual(1, second.Inventory.Count);

            var created = world.CreatedContainers.Single();
            Assert.AreEqual(1, created.Inventory.Count);
            Assert.AreEqual((byte)255, created.ItemCapacity.Value);
        }

        // ---------------------------------------------------------------- R2

        [TestMethod]
        public void Reap_LeavesNonEmptyVault()
        {
            var vault = SeedVault(capacity: 4, itemCount: 1, order: 1);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);
            Assert.IsFalse(store.TryReapEmptyVault(vault), "a vault holding an item must never be destroyed");

            Assert.AreEqual(1, backend.Vaults.Count);
            CollectionAssert.DoesNotContain(world.Destroyed, vault);
        }

        [TestMethod]
        public void Reap_LeavesUnloadedEmptyVault()
        {
            var vault = SeedVault(capacity: 4, itemCount: 0, order: 1);

            var store = NewStore();

            // Control first: an empty vault that HAS loaded is reapable, so the refusal below is
            // caused by the unloaded state and not by the reap being inert.
            Assert.IsTrue(vault.InventoryLoaded);
            Assert.IsTrue(store.TryReapEmptyVault(vault));
            Assert.AreEqual(0, backend.Vaults.Count);

            // Now the real case. An unloaded container is indistinguishable from an empty one:
            // Inventory.Count is 0 for both, and its contents may simply not have arrived yet.
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            var unloaded = SeedVault(capacity: 4, itemCount: 0, order: 1);
            FakeVaultWorld.SetInventoryLoaded(unloaded, false);

            var store2 = NewStore();

            Assert.IsFalse(store2.TryReapEmptyVault(unloaded), "an unloaded container looks empty and must never be reaped");
            Assert.AreEqual(1, backend.Vaults.Count);
            CollectionAssert.DoesNotContain(world.Destroyed, unloaded);
        }

        // ---------------------------------------------------------------- R4

        [TestMethod]
        public void IsLoaded_IsFalse_WhileAnyOneVaultIsUnloaded()
        {
            SeedVault(capacity: 4, itemCount: 1, order: 1);
            var middle = SeedVault(capacity: 4, itemCount: 1, order: 2);
            SeedVault(capacity: 4, itemCount: 1, order: 3);

            FakeVaultWorld.SetInventoryLoaded(middle, false);

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded, "IsLoaded is an AND across every vault, not just the first");

            FakeVaultWorld.SetInventoryLoaded(middle, true);

            Assert.IsTrue(store.IsLoaded);
        }

        [TestMethod]
        public void Mutation_IsRefused_WhileNotLoaded()
        {
            var vault = SeedVault(capacity: 4, itemCount: 0, order: 1);
            FakeVaultWorld.SetInventoryLoaded(vault, false);

            var store = NewStore();

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            Assert.AreEqual(AccountVaultStore.StillLoadingMessage, reason);

            Assert.AreEqual(0, vault.Inventory.Count);
            Assert.AreEqual(0, backend.AddedVaults, "a not-yet-loaded store must not create a second vault");
            Assert.AreEqual(0, backend.Stacks.Count);
        }

        [TestMethod]
        public void GetEntries_IsRefused_WhileNotLoaded()
        {
            SeedVault(capacity: 4, itemCount: 2, order: 1);
            var second = SeedVault(capacity: 4, itemCount: 0, order: 2);

            FakeVaultWorld.SetInventoryLoaded(second, false);

            var store = NewStore();

            Assert.AreEqual(0, store.GetEntries(0, int.MaxValue).Count,
                "a partial list is worse than none: it hides items the player owns");

            FakeVaultWorld.SetInventoryLoaded(second, true);

            Assert.AreEqual(2, store.GetEntries(0, int.MaxValue).Count);
        }

        // ---------------------------------------------------------------- DESIGN 7.3

        [TestMethod]
        public void EntryCount_CountsLedgerRowsAsOne_RegardlessOfUnits()
        {
            SeedVault(capacity: 8, itemCount: 2, order: 1);
            SeedLedger(1111, 10000);
            SeedLedger(2222, 1);

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount,
                "two stored biotas plus two ledger rows; 10,000 units of one wcid are ONE entry");
        }

        [TestMethod]
        public void Deposit_AtCap_IsRefused_WithCurrentAndLimitInTheMessage()
        {
            SeedVault(capacity: 8, itemCount: 2, order: 1);
            SeedLedger(1111, 500);
            SeedLedger(2222, 500);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 4));

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount);

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            StringAssert.Contains(reason, "4 of 4", $"the refusal must name the current count and the limit; got: {reason}");

            Assert.AreEqual(0, backend.AddedVaults);
        }

        [TestMethod]
        public void HasRoomFor_AgreesWithTryDepositAtTheBoundary()
        {
            // The shared cap helper (AccountVaultStore.HasRoomFor) that TryDeposit's own cap check now
            // calls, and that VaultMarketItemStore.CanReceive calls too for the market purchase
            // preflight - this proves the preflight and the real deposit cannot drift apart at the
            // exact boundary.
            SeedVault(capacity: 8, itemCount: 3, order: 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 4));

            var store = NewStore();

            Assert.AreEqual(3, store.EntryCount, "precondition: one entry of headroom");

            Assert.IsTrue(store.HasRoomFor(1), "one entry of headroom fits one new entry");
            Assert.IsFalse(store.HasRoomFor(2), "one entry of headroom does not fit two");

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason), reason);
            Assert.AreEqual(4, store.EntryCount, "now exactly at the cap");

            Assert.IsFalse(store.HasRoomFor(1), "at the cap, no room for even one more entry");

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(5678, 1, 100), Owner, out reason));
            Assert.IsTrue(AccountVaultStore.IsFullMessage(reason),
                $"TryDeposit must refuse with the same cap message HasRoomFor(1) predicted; got: {reason}");
        }

        [TestMethod]
        public void HasRoomFor_ZeroNewEntries_IsAlwaysTrue_MatchingALedgerTopUpThatConsumesNoEntry()
        {
            // A ledger deposit that tops up a row the account already holds adds no entry (DESIGN
            // 7.3), so the market preflight for that case asks HasRoomFor(0) - which must stay true
            // even sitting exactly at the cap, or a hoarding-case purchase that was always going to be
            // free would be wrongly refused.
            SeedVault(capacity: 8, itemCount: 4, order: 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 4));

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "precondition: the vault is exactly at the cap");

            Assert.IsFalse(store.HasRoomFor(1), "control: the cap genuinely has no room for a new entry");
            Assert.IsTrue(store.HasRoomFor(0), "zero new entries always fits, even at the cap");
        }

        [TestMethod]
        public void IsFullMessage_MatchesTheRealCapRefusal_AndNothingElse()
        {
            // The classifier VaultMarketItemStore.TryGiveToBuyer relies on to tell a full vault apart
            // from every other deposit failure. It must match TryDeposit's actual cap-refusal message
            // (numbers and all) and must not false-positive on the other two stock vault messages.
            SeedVault(capacity: 8, itemCount: 2, order: 1);
            SeedLedger(1111, 500);
            SeedLedger(2222, 500);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 4));

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "precondition: the vault is exactly at the cap");

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            Assert.IsTrue(AccountVaultStore.IsFullMessage(reason), $"expected the cap refusal to be classified as full; got: {reason}");

            Assert.IsFalse(AccountVaultStore.IsFullMessage(AccountVaultStore.UnavailableMessage));
            Assert.IsFalse(AccountVaultStore.IsFullMessage(AccountVaultStore.StillLoadingMessage));
            Assert.IsFalse(AccountVaultStore.IsFullMessage(null));
            Assert.IsFalse(AccountVaultStore.IsFullMessage("That is no longer in your vault."));
        }

        [TestMethod]
        public void VaultMarketItemStore_ClassifyDepositFailure_ReadsVaultFullFromARealCappedStore()
        {
            // VaultMarketItemStore.TryGiveToBuyer resolves its store through
            // AccountVaultManager.GetStore, which hardcodes the production ShardAccountVaultBackend
            // and offers no seam to point it at a test double for one account id - so this cannot
            // drive TryGiveToBuyer itself against a fake-backed store. Instead this exercises the
            // exact classification VaultMarketItemStore uses (ClassifyDepositFailure, extracted from
            // its TryGiveToBuyer ternary) against a failReason produced by a REAL AccountVaultStore
            // hitting its REAL cap check, so the ternary is proven against the real message shape and
            // not just the fake store's stand-in error.
            SeedVault(capacity: 8, itemCount: 2, order: 1);
            SeedLedger(1111, 500);
            SeedLedger(2222, 500);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 4));

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "precondition: the vault is exactly at the cap");

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));

            Assert.AreEqual(MarketError.VaultFull, VaultMarketItemStore.ClassifyDepositFailure(reason));

            // Control: a message that is not the cap refusal classifies as the transient error, so the
            // assertion above is not a tautology of ClassifyDepositFailure always returning VaultFull.
            Assert.AreEqual(MarketError.VaultUnavailable, VaultMarketItemStore.ClassifyDepositFailure(AccountVaultStore.UnavailableMessage));
        }

        [TestMethod]
        public void Deposit_OverCap_AfterCapLowered_StillAllowsWithdraw()
        {
            var vault = SeedVault(capacity: 8, itemCount: 2, order: 1);
            SeedLedger(1111, 40);
            SeedLedger(2222, 5);

            // Lowered below what the account already holds. Never retroactive: further deposits are
            // refused, everything already stored stays withdrawable.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount);

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            StringAssert.Contains(reason, "4 of 2", $"got: {reason}");

            var entries = store.GetEntries(0, int.MaxValue);

            var storedItem = entries.First(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(Withdraw(store, storedItem, 1, Owner, out var got, out reason), reason);
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual(1, vault.Inventory.Count);

            var ledgerEntry = entries.First(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == 1111);
            Assert.IsTrue(Withdraw(store, ledgerEntry, 15, Owner, out got, out reason), reason);
            Assert.AreEqual(15, got.Sum(w => w.StackSize ?? 1));
            Assert.AreEqual(25, backend.Stacks.Single(s => s.Wcid == 1111).Count);
        }

        // ---------------------------------------------------------------- R8 insurance

        [TestMethod]
        public void GetEntries_Paged_ReturnsTheRequestedWindow()
        {
            SeedVault(capacity: 8, itemCount: 5, order: 1);
            SeedLedger(1111, 3);
            SeedLedger(2222, 4);

            var store = NewStore();

            var all = store.GetEntries(0, int.MaxValue);
            Assert.AreEqual(7, all.Count);

            var window = store.GetEntries(2, 3);
            Assert.AreEqual(3, window.Count);

            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(all[i + 2].Kind, window[i].Kind);
                Assert.AreEqual(all[i + 2].Wcid, window[i].Wcid);
                Assert.AreEqual(all[i + 2].Guid.Full, window[i].Guid.Full);
            }

            Assert.AreEqual(1, store.GetEntries(6, 10).Count, "a window running off the end returns what is left");
            Assert.AreEqual(0, store.GetEntries(7, 10).Count, "an offset past the end returns nothing");
            Assert.AreEqual(0, store.GetEntries(0, 0).Count);
        }

        // ---------------------------------------------------------------- DESIGN section 10

        [TestMethod]
        public void GetAccess_Owner_IsDepositWithdraw()
        {
            var store = NewStore();

            Assert.AreEqual(VaultAccess.DepositWithdraw, AccessOf(store, Owner));
        }

        [TestMethod]
        public void GetAccess_UngrantedStranger_IsNone()
        {
            var store = NewStore();

            Assert.AreEqual(VaultAccess.None, AccessOf(store, Stranger));
        }

        [TestMethod]
        public void GetAccess_DepositOnlyGrantee_IsDeposit()
        {
            backend.Grants.Add(new AccountVaultGrant
            {
                Id = 1,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = StrangerCharacter,
                GranteeCharacterName = "Somebodyelse",
                CanWithdraw = false,
                GrantedAt = DateTime.UtcNow,
            });

            var store = NewStore();

            Assert.AreEqual(VaultAccess.Deposit, AccessOf(store, Stranger));

            // And the gate that matters: a deposit-only grantee cannot withdraw.
            var vault = SeedVault(capacity: 4, itemCount: 1, order: 1);
            var store2 = new AccountVaultStore(OwnerAccount, backend, world);

            var entry = store2.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

            Assert.IsFalse(Withdraw(store2, entry, 1, Stranger, out _, out var reason));
            Assert.AreEqual(1, vault.Inventory.Count);

            // Fix round A, A7: the store collapsed VaultAccess.None and VaultAccess.Deposit into one
            // message while PersonalVendor.TryAuthorizeWithdraw distinguished them. Both refuse, so
            // this is cosmetic - but a same-assembly caller reaching TryWithdraw without going through
            // the vendor sees THIS string, and a deposit-only grantee told "you do not have permission
            // to use this vault" cannot tell a revoked grant from a grant that never included
            // withdraw. The two sites must say the same thing for the same access value.
            Assert.AreEqual("You do not have permission to withdraw from this vault.", reason,
                "a grantee who HAS access but not withdraw access must be told which half is missing");
        }

        /// <summary>
        /// Fix round A, A7, the other side of the same ternary: an actor with no grant at all gets the
        /// "use this vault" wording, not the withdraw wording. Paired with the assertion in
        /// GetAccess_DepositOnlyGrantee_IsDeposit above - either one alone passes with the two branches
        /// collapsed back into a single string.
        /// </summary>
        [TestMethod]
        public void Withdraw_WithNoGrantAtAll_IsRefusedWithTheUseWording()
        {
            SeedVault(capacity: 4, itemCount: 1, order: 1);

            var store = new AccountVaultStore(OwnerAccount, backend, world);

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

            Assert.IsFalse(Withdraw(store, entry, 1, Stranger, out _, out var reason));

            Assert.AreEqual("You do not have permission to use this vault.", reason,
                "no grant at all is a different situation from a deposit-only grant and must not collapse into the withdraw wording");
        }

        [TestMethod]
        public void GetAccess_IsReResolved_AfterRevoke_WithNoSessionInvalidation()
        {
            backend.Grants.Add(new AccountVaultGrant
            {
                Id = 1,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = StrangerCharacter,
                GranteeCharacterName = "Somebodyelse",
                CanWithdraw = true,
                GrantedAt = DateTime.UtcNow,
            });

            var store = NewStore();

            Assert.AreEqual(VaultAccess.DepositWithdraw, AccessOf(store, Stranger));

            var readsAfterFirst = backend.GrantReads;

            backend.Grants.Clear();

            Assert.AreEqual(VaultAccess.None, AccessOf(store, Stranger),
                "a revoke must take effect on the very next call, with no session invalidation");

            Assert.IsTrue(backend.GrantReads > readsAfterFirst, "the grant list must be re-read every call, never cached");
        }

        // ---------------------------------------------------------------- failed read vs empty read

        [TestMethod]
        public void VaultIndexReadFailure_RefusesAndCreatesNoVault()
        {
            SeedVault(capacity: 4, itemCount: 1, order: 1);

            backend.FailVaultRead = true;

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded);
            Assert.AreEqual(0, store.GetEntries(0, int.MaxValue).Count);

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);

            Assert.AreEqual(0, backend.AddedVaults,
                "a failed read is not an empty account: creating a vault here would hide the player's items");

            // And it heals: the next access after the shard comes back loads normally.
            backend.FailVaultRead = false;

            Assert.IsTrue(store.IsLoaded);
            Assert.AreEqual(1, store.GetEntries(0, int.MaxValue).Count);
        }

        [TestMethod]
        public void LedgerReadFailure_RefusesRatherThanTreatingItAsEmpty()
        {
            SeedVault(capacity: 4, itemCount: 1, order: 1);
            SeedLedger(1111, 7);

            backend.FailStackRead = true;

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded);
            Assert.AreEqual(0, store.EntryCount);

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);
            Assert.AreEqual(0, backend.AddedVaults);
        }

        [TestMethod]
        public void GrantReadFailure_DeniesAccessRatherThanGrantingIt()
        {
            backend.Grants.Add(new AccountVaultGrant
            {
                Id = 1,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = StrangerCharacter,
                GranteeCharacterName = "Somebodyelse",
                CanWithdraw = true,
                GrantedAt = DateTime.UtcNow,
            });

            backend.FailGrantRead = true;

            var store = NewStore();

            Assert.AreEqual(VaultAccess.None, AccessOf(store, Stranger));

            // The owner still gets in: ownership is decided from the account id before the grant list
            // is ever read, so a grant-table outage cannot lock a player out of their own vault.
            Assert.AreEqual(VaultAccess.DepositWithdraw, AccessOf(store, Owner));
        }

        // ---------------------------------------------------------------- collapse routing and audit

        [TestMethod]
        public void PristineDeposit_MovesTheLedgerBeforeItDestroysTheItem()
        {
            world.PristineResult = true;

            var store = NewStore();
            var item = FakeVaultWorld.MakeStack(1234, 25, 100);

            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);

            Assert.AreEqual(25, backend.Stacks.Single(s => s.Wcid == 1234).Count);
            CollectionAssert.Contains(world.Destroyed, (WorldObject)item);
            Assert.AreEqual(0, backend.AddedVaults, "a collapsed deposit needs no vault at all");
        }

        [TestMethod]
        public void PristineDeposit_KeepsTheItem_WhenTheLedgerRefuses()
        {
            world.PristineResult = true;

            // The ledger write fails outright. Ledger first, destroy second is exactly what makes this
            // recoverable: the player still has the item, and nothing was silently swallowed.
            backend.FailLedgerAdjustWcids.Add(1234);

            var store = NewStore();
            var item = FakeVaultWorld.MakeStack(1234, 25, 100);

            Assert.IsFalse(Deposit(store, item, Owner, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);
            CollectionAssert.DoesNotContain(world.Destroyed, (WorldObject)item);
            Assert.AreEqual(0, backend.Logs.Count, "a refused deposit writes no audit row");
        }

        [TestMethod]
        public void LedgerWithdraw_SplitsIntoStacks_RespectingMaxStackSize()
        {
            SeedLedger(1234, 250);

            world.ItemMaxStackSize = 100;

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger);

            Assert.IsTrue(Withdraw(store, entry, 250, Owner, out var got, out var reason), reason);

            CollectionAssert.AreEqual(new[] { 100, 100, 50 }, got.Select(w => w.StackSize ?? 1).ToArray());
            Assert.AreEqual(0, backend.Stacks.Count(s => s.Wcid == 1234 && s.Count > 0));
            Assert.IsTrue(backend.DeletedEmptyStackCalls > 0, "the zero row is tidied on the store's own queue");
        }

        [TestMethod]
        public void LedgerWithdraw_OverAvailable_IsRefusedAndLeavesTheLedgerAlone()
        {
            SeedLedger(1234, 10);

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger);

            Assert.IsFalse(Withdraw(store, entry, 25, Owner, out var got, out _));
            Assert.AreEqual(0, got.Count);
            Assert.AreEqual(10, backend.Stacks.Single(s => s.Wcid == 1234).Count);
        }

        [TestMethod]
        public void Deposit_OffTheMutationQueue_Throws()
        {
            var store = NewStore();

            // The queue is the in-memory half of R3. Calling a mutation off it is a wiring bug that
            // must be loud, because the alternative is an unserialized biota path that looks fine
            // until two windows race.
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                store.TryDeposit(FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out _);
            });
        }

        [TestMethod]
        public void Deposit_RefusesAnItemStillParentedToItsOwner()
        {
            var holder = FakeVaultWorld.MakeContainer(4);
            var item = FakeVaultWorld.MakeStack(1234, 1, 100);

            Assert.IsTrue(holder.TryAddToInventory(item));

            var store = NewStore();

            Assert.IsFalse(Deposit(store, item, Owner, out _),
                "an item still in another container would end up in two at once");
            Assert.AreEqual(1, holder.Inventory.Count);
        }

        [TestMethod]
        public void Deposit_RefusesAPack_WithAReasonRatherThanAGenericFailure()
        {
            var pack = FakeVaultWorld.MakeContainer(itemsCapacity: 24);

            Assert.IsTrue(pack.UseBackpackSlot, "the fixture must actually be a pack, or this test proves nothing");

            var store = NewStore();

            Assert.IsFalse(Deposit(store, pack, Owner, out var packReason));
            StringAssert.Contains(packReason, "pack", $"got: {packReason}");
            Assert.AreNotEqual(AccountVaultStore.UnavailableMessage, packReason);
            Assert.AreEqual(0, backend.AddedVaults);

            // UseBackpackSlot is WeenieType == Container OR RequiresPackSlot
            // (WorldObject_Properties.cs:2070, :1813), and a Focusing Stone is the second kind. Telling
            // its owner to "store what is inside it instead" is advice they cannot act on, so the two
            // causes must not share one message.
            var focus = FakeVaultWorld.MakeStack(1234, 1, 1);
            focus.RequiresPackSlot = true;

            Assert.IsTrue(focus.UseBackpackSlot);
            Assert.AreNotEqual(WeenieType.Container, focus.WeenieType, "the second fixture must not be a container, or it proves nothing");

            Assert.IsFalse(Deposit(store, focus, Owner, out var focusReason));
            Assert.AreNotEqual(packReason, focusReason, "a pack-slot item and a pack are refused for different reasons");
            Assert.AreNotEqual(AccountVaultStore.UnavailableMessage, focusReason);
            StringAssert.Contains(focusReason, "pack slot", $"got: {focusReason}");
            Assert.AreEqual(0, backend.AddedVaults);
        }

        // ---------------------------------------------------------------- W1: the withdraw contract

        [TestMethod]
        public void Withdraw_DoesNotForceSaveTheItem()
        {
            var vault = SeedVault(capacity: 4, itemCount: 1, order: 1);

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

            world.Saved.Clear();

            Assert.IsTrue(Withdraw(store, entry, 1, Owner, out var got, out var reason), reason);

            var item = got.Single();

            CollectionAssert.Contains(world.Saved, (WorldObject)vault,
                "the container save copies Storage.OnRemoveItem and is not optional");

            // The regression guard for a deliberate ordering choice, which is exactly the kind a later
            // maintainer 'fixes'. TryRemoveFromInventory has already cleared the item's ContainerId in
            // memory (Container.cs:651-654) and only then honours forceSave (:665-666), so saving here
            // would persist a row with no container, no wielder and no location - the orphan the purge
            // is entitled to delete (R1). Leaving the row pointing at the vault means a crash in this
            // window returns the item to the vault on restart, which is the recoverable direction.
            CollectionAssert.DoesNotContain(world.Saved, item,
                "force-saving the withdrawn item reintroduces the R1 orphan window");
        }

        [TestMethod]
        public void ReturnWithdrawn_PutsAnUndeliverableItemBackInAFreeSlot()
        {
            var vault = SeedVault(capacity: 4, itemCount: 1, order: 1);

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

            Assert.IsTrue(Withdraw(store, entry, 1, Owner, out var got, out var reason), reason);

            var item = got.Single();
            Assert.AreEqual(0, vault.Inventory.Count);

            world.Saved.Clear();

            // The caller could not deliver it - a full pack, a failed networking add. Destroy() would
            // lose it from the vault and from the player at once, so there has to be a way back in.
            var returned = false;
            store.Enqueue(() => returned = store.TryReturnWithdrawn(item, Owner));

            Assert.IsTrue(returned);
            Assert.AreEqual(1, vault.Inventory.Count);
            Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid));

            Assert.AreEqual(1, store.EntryCount);
            Assert.AreEqual(0, backend.AddedVaults, "a free slot existed, so no new vault was needed");

            CollectionAssert.Contains(world.Saved, item,
                "the returned item's row must point at the vault again before this returns");
            CollectionAssert.Contains(world.Saved, (WorldObject)vault);
        }

        [TestMethod]
        public void ReturnWithdrawn_IgnoresTheCap_BecauseTheItemWasAlreadyCounted()
        {
            var vault = SeedVault(capacity: 4, itemCount: 2, order: 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).First(e => e.Kind == VaultEntryKind.StoredItem);

            Assert.IsTrue(Withdraw(store, entry, 1, Owner, out var got, out var reason), reason);

            var item = got.Single();

            // Control: an ordinary deposit is refused at this count, so the return below is not merely
            // slipping in under a cap that happens to have room.
            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out _),
                "the fixture must be at or over the cap, or this test proves nothing");

            var returned = false;
            store.Enqueue(() => returned = store.TryReturnWithdrawn(item, Owner));

            Assert.IsTrue(returned,
                "the item was already counted against the cap; refusing it would strand it in the caller's hands with nowhere legal to put it");
            Assert.AreEqual(2, vault.Inventory.Count);
        }

        [TestMethod]
        public void ReturnWithdrawn_WritesExactlyOneReturnAuditRow()
        {
            var vault = SeedVault(capacity: 4, itemCount: 0, order: 1);

            Assert.IsTrue(vault.TryAddToInventory(FakeVaultWorld.MakeStack(1234, 17, 100)));

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

            Assert.IsTrue(Withdraw(store, entry, 17, Owner, out var got, out var reason), reason);

            var item = got.Single();

            var returned = false;
            store.Enqueue(() => returned = store.TryReturnWithdrawn(item, Owner));

            Assert.IsTrue(returned);

            // Without this row the log shows an item leaving the vault while the item is in fact
            // sitting back inside it, which is the shape no dupe or theft investigation can resolve.
            var returnRow = backend.Logs.Single(l => l.Action == (int)AccountVaultAction.Return);

            Assert.AreEqual(OwnerAccount, returnRow.OwnerAccountId);
            Assert.AreEqual(OwnerCharacter, returnRow.ActorCharacterGuid, "the audit row must name the actor who withdrew it");
            Assert.AreEqual("Vaultowner", returnRow.ActorCharacterName);
            Assert.AreEqual(1234u, returnRow.Wcid);
            Assert.IsNotNull(returnRow.ItemGuid, "a returned biota has a guid, unlike a collapsed ledger row");
            Assert.AreEqual(item.Guid.Full, returnRow.ItemGuid.Value);
            Assert.AreEqual(17L, returnRow.Count, "the quantity actually returned");

            // It pairs with the withdraw, rather than replacing or duplicating it.
            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw));
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit),
                "a return is not a deposit: a Deposit row would read as a second transaction");
        }

        [TestMethod]
        public void ReturnWithdrawn_WhenRefused_WritesNoAuditRow()
        {
            var vault = SeedVault(capacity: 4, itemCount: 1, order: 1);

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);

            Assert.IsTrue(Withdraw(store, entry, 1, Owner, out var got, out var reason), reason);

            var item = got.Single();

            var logsAfterWithdraw = backend.Logs.Count;

            // Refusal 1: the store is not ready, so it cannot pick a free slot it can see (R4).
            FakeVaultWorld.SetInventoryLoaded(vault, false);

            var returned = true;
            store.Enqueue(() => returned = store.TryReturnWithdrawn(item, Owner));

            Assert.IsFalse(returned);

            // Refusal 2: the item is already parented somewhere, so it is not a returnable item.
            FakeVaultWorld.SetInventoryLoaded(vault, true);

            var holder = FakeVaultWorld.MakeContainer(4);
            Assert.IsTrue(holder.TryAddToInventory(item));

            store.Enqueue(() => returned = store.TryReturnWithdrawn(item, Owner));

            Assert.IsFalse(returned);

            // A Return row for an item still stuck in the caller's hands would be worse than no row:
            // it would assert the item is back in the vault when it is not.
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Return));
            Assert.AreEqual(logsAfterWithdraw, backend.Logs.Count, "a refused return writes nothing at all");
        }

        /// <summary>
        /// F5, half two: the undo of a LEDGER withdrawal is not the undo of a stored-biota withdrawal.
        /// TryReturnWithdrawn deliberately skips the cap check because a stored biota was counted
        /// against the cap while it sat in the vault; a ledger-derived object never was, because one
        /// ledger row is one entry no matter how many units it holds (DESIGN 7.3). Routing a rolled-back
        /// ledger withdrawal through TryReturnWithdrawn therefore converts one entry into as many
        /// entries as there were units, past the cap and with no bound on the vault containers created.
        /// </summary>
        [TestMethod]
        public void LedgerWithdrawalRolledBack_ReturnsToTheLedgerAndDoesNotConsumeACapEntry()
        {
            const uint wcid = 1234;

            // One collapsed ledger row of 3 units of a stackable wcid, and nothing else.
            SeedLedger(wcid, 3);

            var store = NewStore();

            Assert.AreEqual(1, store.EntryCount, "precondition: one ledger row is exactly one entry against the cap.");

            var ok = Withdraw(store, VaultEntry.ForLedger(wcid, 2), 2, Owner, out var withdrawn, out _);
            Assert.IsTrue(ok);

            // Simulate the delivery failing and the transaction unwinding.
            foreach (var item in withdrawn)
                Assert.IsTrue(store.Enqueue(() => store.TryReturnWithdrawnToLedger(item, wcid, item.StackSize ?? 1, Owner)));

            Assert.AreEqual(1, store.EntryCount,
                "a rolled-back LEDGER withdrawal must go back to the ledger. Filing it as a stored biota turns one entry into two and is uncapped, because TryReturnWithdrawn deliberately skips the cap check on the (correct, for a stored biota) reasoning that the item was already counted.");
            Assert.AreEqual(3, store.GetEntries(0, -1).Single().Count, "every unit must be back on the ledger.");
        }

        // ---------------------------------------------------------------- A1

        [TestMethod]
        public void Withdraw_OfAStoredStack_RefusesAPartialAmount()
        {
            var vault = FakeVaultWorld.MakeContainer(8);

            world.Containers[vault.Guid.Full] = vault;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9500,
                AccountId = OwnerAccount,
                ContainerGuid = vault.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            Assert.IsTrue(vault.TryAddToInventory(FakeVaultWorld.MakeStack(1234, 200, 200)));

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.AreEqual(200L, entry.Count, "the fixture must be a stored stack of 200, or this test proves nothing");

            // The defect: the amount was accepted and silently ignored, so the player got all 200 and
            // the audit row recorded 200 against a panel that had shown them asking for 50.
            Assert.IsFalse(Withdraw(store, entry, 50, Owner, out var got, out var reason));
            StringAssert.Contains(reason, "whole", $"got: {reason}");
            Assert.AreEqual(0, got.Count);
            Assert.AreEqual(1, vault.Inventory.Count);
            Assert.AreEqual(0, backend.Logs.Count, "a refused withdraw writes no audit row");

            // Control: the same entry taken whole succeeds, so the refusal is about the amount and not
            // about the entry.
            Assert.IsTrue(Withdraw(store, entry, 200, Owner, out got, out reason), reason);
            Assert.AreEqual(200, got.Single().StackSize);
        }

        // ---------------------------------------------------------------- C2

        [TestMethod]
        public void Deposit_AtCap_StillTopsUpAnExistingLedgerStack()
        {
            SeedVault(capacity: 8, itemCount: 2, order: 1);
            SeedLedger(1111, 9000);
            SeedLedger(2222, 500);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 4));

            world.PristineResult = true;

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "the account is exactly at the cap");

            // DESIGN 7.3: the cap counts ENTRIES, and 10,000 healing kits are ONE entry. Ten more onto
            // a row that already exists create no row and no entry, so there is nothing for the cap to
            // refuse - and refusing anyway is the cap doing the opposite of what 7.3 says it is for.
            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1111, 10, 100), Owner, out var reason), reason);

            Assert.AreEqual(9010, backend.Stacks.Single(s => s.Wcid == 1111).Count);
            Assert.AreEqual(4, store.EntryCount, "topping up a ledger row must not add an entry");

            // Control: a pristine deposit of a NEW wcid does add an entry, and is still refused.
            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(3333, 1, 100), Owner, out reason));
            StringAssert.Contains(reason, "4 of 4", $"got: {reason}");
        }

        // ---------------------------------------------------------------- E1

        [TestMethod]
        public void EntryCount_IsZero_WhileAnyOneVaultIsUnloaded()
        {
            SeedVault(capacity: 4, itemCount: 2, order: 1);
            var middle = SeedVault(capacity: 4, itemCount: 3, order: 2);
            SeedVault(capacity: 4, itemCount: 1, order: 3);

            FakeVaultWorld.SetInventoryLoaded(middle, false);

            var store = NewStore();

            // A SHORT count is indistinguishable from a true one at every call site, and this getter's
            // own doc says a zero means "unknown". GetEntries already refuses in this state.
            Assert.AreEqual(0, store.EntryCount, "EntryCount must use the same readiness gate as GetEntries");

            FakeVaultWorld.SetInventoryLoaded(middle, true);

            Assert.AreEqual(6, store.EntryCount);
        }

        // ---------------------------------------------------------------- K1

        [TestMethod]
        public void Grant_OffTheMutationQueue_Throws()
        {
            world.Characters["Somebodyelse"] = (StrangerCharacter, "Somebodyelse", StrangerAccount);

            var store = NewStore();

            // TryGetAccess re-reads the grant list inside every deposit and withdraw, so a grant
            // written off the queue races an in-flight transaction's authorization decision.
            Assert.ThrowsExactly<InvalidOperationException>(() => store.TryGrantCore(Owner, "Somebodyelse", true, out _, out _, out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.TryRevokeCore(Owner, "Somebodyelse", out _));

            Assert.AreEqual(0, backend.Grants.Count);

            // Control: the same call ON the queue succeeds, so the throw is about the queue and not
            // about the grant being invalid.
            var ok = false;
            store.Enqueue(() => ok = store.TryGrantCore(Owner, "Somebodyelse", true, out _, out _, out _));

            Assert.IsTrue(ok);
            Assert.AreEqual(1, backend.Grants.Count);
        }

        // ---------------------------------------------------------------- V1

        [TestMethod]
        public void Load_WithAMissingContainerBiota_DropsThatIndexRowAndLoadsTheRest()
        {
            var dangling = SeedVault(capacity: 4, itemCount: 0, order: 1);
            var healthy = SeedVault(capacity: 4, itemCount: 2, order: 2);

            // The crash window: the account_vault row was written and the container biota never
            // landed. Before the fix this refused the WHOLE store forever, taking every other vault on
            // the account with it, with no admin command and no self-heal.
            world.Containers.Remove(dangling.Guid.Full);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "one dangling index row must not brick the account's other vaults");
            Assert.AreEqual(2, store.GetEntries(0, int.MaxValue).Count);
            Assert.AreEqual(2, store.EntryCount);

            Assert.AreEqual(1, backend.Vaults.Count, "the dangling row is deleted rather than left to fail every future load");
            Assert.AreEqual(healthy.Guid.Full, backend.Vaults.Single().ContainerGuid);
        }

        [TestMethod]
        public void Load_WithAnUnreadableContainerBiota_StillRefusesTheWholeStore()
        {
            var unreadable = SeedVault(capacity: 4, itemCount: 0, order: 1);
            SeedVault(capacity: 4, itemCount: 2, order: 2);

            // The distinction that is the whole point: a read that FAILED says nothing about whether
            // the biota exists, so it must keep today's fail-closed behaviour exactly.
            world.FailLoadContainerGuids.Add(unreadable.Guid.Full);

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded);
            Assert.AreEqual(0, store.GetEntries(0, int.MaxValue).Count);
            Assert.AreEqual(2, backend.Vaults.Count, "a failed read must never delete an index row");

            // And it heals once the shard comes back.
            world.FailLoadContainerGuids.Clear();

            Assert.IsTrue(store.IsLoaded);
            Assert.AreEqual(2, store.GetEntries(0, int.MaxValue).Count);
            Assert.AreEqual(2, backend.Vaults.Count);
        }

        [TestMethod]
        public void CreateVault_DoesNotRegisterTheIndexRow_UntilTheBiotaSaveSucceeds()
        {
            world.FailSaveBiota = true;

            var store = NewStore();

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);

            // The index row is what makes the container findable again. Written before the biota has
            // landed, a crash in between leaves a row pointing at a biota that never existed.
            Assert.AreEqual(0, backend.AddedVaults);
            Assert.AreEqual(0, backend.Vaults.Count);

            var container = world.CreatedContainers.Single();
            CollectionAssert.Contains(world.Destroyed, (WorldObject)container,
                "the unregistered container is destroyed while it is provably empty - the one moment that is safe");

            // Control: the identical deposit succeeds once the save lands, so the refusal above is
            // caused by the save failing rather than by the deposit path being broken.
            world.FailSaveBiota = false;

            var store2 = NewStore();

            Assert.IsTrue(Deposit(store2, FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out reason), reason);
            Assert.AreEqual(1, backend.AddedVaults);
        }

        [TestMethod]
        public void DepositSaveFailure_IsRetriedOnTheNextStoreMutation()
        {
            var store = NewStore();

            var item = FakeVaultWorld.MakeStack(1234, 1, 100);
            var otherItem = FakeVaultWorld.MakeStack(1235, 1, 100);

            // One object's FIRST save fails and every later one lands. Keyed by guid rather than the
            // blanket FailSaveBiota switch on purpose: that switch also fails the vault container's own
            // save, which refuses the deposit outright (see the test above) and never reaches the path
            // under test.
            world.FailSaveBiotaCounts[item.Guid.Full] = 1;

            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);

            Assert.AreEqual(1, world.SaveBiotaCalls(item), "precondition: the first save was attempted and failed.");
            CollectionAssert.DoesNotContain(world.Saved, (WorldObject)item, "precondition: a failed save persists nothing.");

            // Any subsequent mutation drains the retry list.
            Assert.IsTrue(Deposit(store, otherItem, Owner, out reason), reason);

            Assert.AreEqual(2, world.SaveBiotaCalls(item),
                "a failed deposit save must be retried. The sell path has already flushed ContainerId = null to the shard by this point, so the pre-save on-disk state is an orphan - a dropped write means the item is gone from the vault on restart, with nothing to re-save it.");

            CollectionAssert.Contains(world.Saved, (WorldObject)item, "the retry must actually persist the item, not merely re-attempt it");

            // A retry that succeeded must clear itself, or every later mutation re-saves the same item
            // forever. A third mutation adds no further attempt.
            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1236, 1, 100), Owner, out reason), reason);

            Assert.AreEqual(2, world.SaveBiotaCalls(item), "a confirmed retry must be dropped from the pending list");
        }

        /// <summary>
        /// Important 2 from the whole-branch review. RetryPendingDepositSaves only ran from Drain, i.e.
        /// from the NEXT enqueued mutation - and the case F4 was written for is a player selling one
        /// item into the vault as the LAST act of a session. The window closes, OpenWindows drops to
        /// zero, and the idle sweep retired the store with the retry never attempted and no "gave up"
        /// line either, because that only fires from inside the retry. The ERROR line's promise of a
        /// retry was false in exactly the case it was written for.
        /// </summary>
        [TestMethod]
        public void DepositSaveFailure_DeclinesEvictionWhileTheSaveIsUnconfirmed()
        {
            var store = NewStore();

            var item = FakeVaultWorld.MakeStack(1234, 1, 100);

            // EVERY save of this object fails, unlike the retry test above: TryEvict now drains the
            // pending list itself, so a single-failure item would be confirmed by that drain and the
            // decision under test would never be reached.
            world.FailSaveBiotaCounts[item.Guid.Full] = 99;

            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);
            Assert.AreEqual(1, world.SaveBiotaCalls(item), "precondition: the first save was attempted and failed.");

            var pastIdleThreshold = ACE.Common.Time.GetUnixTime() + AccountVaultStore.IdleEvictionSeconds + 10.0;

            Assert.IsFalse(store.TryEvict(pastIdleThreshold),
                "a store holding an unconfirmed deposit save must not be retired. The sell path has already flushed ContainerId = null to the shard, so the on-disk row is an orphan until that save lands and nothing else ever re-saves a vault item - dropping the store here loses the item on the next restart.");

            Assert.IsFalse(store.IsEvicted);

            // Control: an identically-built store with nothing pending evicts on the SAME threshold, so
            // the refusal above is caused by the unconfirmed save and not by some other guard.
            var clean = NewStore();
            Assert.IsTrue(Deposit(clean, FakeVaultWorld.MakeStack(1235, 1, 100), Owner, out reason), reason);
            Assert.IsTrue(clean.TryEvict(pastIdleThreshold),
                "control: a store with no unconfirmed save must still evict at the same idle threshold");
        }

        /// <summary>
        /// The other half of Important 2: the guard above must not be able to pin a store forever. It
        /// cannot, because TryEvict DRAINS the pending list rather than merely declining against it, and
        /// RetryPendingDepositSaves gives up after MaxDepositSaveAttempts - so the list empties within a
        /// bounded number of sweeps whether the saves ever land or not.
        /// </summary>
        [TestMethod]
        public void DepositSaveFailure_EvictionProceedsOnceTheRetryAttemptsAreExhausted()
        {
            var store = NewStore();

            var item = FakeVaultWorld.MakeStack(1234, 1, 100);
            world.FailSaveBiotaCounts[item.Guid.Full] = 99;

            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);

            var pastIdleThreshold = ACE.Common.Time.GetUnixTime() + AccountVaultStore.IdleEvictionSeconds + 10.0;

            var declinedSweeps = 0;

            while (!store.TryEvict(pastIdleThreshold))
            {
                declinedSweeps++;

                Assert.IsTrue(declinedSweeps <= 10,
                    "eviction must not be pinned forever by a save that never lands. The retry list is drained from inside TryEvict, so its own MaxDepositSaveAttempts give-up is what clears the guard - a guard that merely declined would hold this store in memory for the life of the process.");
            }

            Assert.IsTrue(store.IsEvicted);

            Assert.AreEqual(4, world.SaveBiotaCalls(item),
                "the deposit's own save plus MaxDepositSaveAttempts (3) real retries. The store is never retired without the retry F4 promises having actually been spent.");
        }

        /// <summary>
        /// Minor 4 from the whole-branch review. TryWithdraw bumps Version on its FAILURE path too,
        /// because WithdrawFromLedger can leave the in-memory ledger debited while returning false. The
        /// symmetric case on deposit was uncovered: the ledger branch commits the credit and THEN
        /// calls world.DestroyItem, so a throw from that tail unwound out of TryDeposit past a bump
        /// placed on the success return. Drain catches it and does not rethrow, so a window kept showing
        /// pre-deposit contents with no later mutation to correct it.
        /// </summary>
        [TestMethod]
        public void LedgerDeposit_BumpsVersionEvenWhenItsTailThrows()
        {
            var store = NewStore();

            world.PristineResult = true;

            // Forces the first-touch load before Version is sampled, so the assertion below measures the
            // deposit rather than any load-time bookkeeping.
            Assert.AreEqual(0, store.GetEntries(0, int.MaxValue).Count);

            var before = store.Version;

            // Throws from the TEARDOWN half of the ledger deposit - after the credit has committed,
            // which is the only ordering that can leave the ledger ahead of the window.
            world.ThrowFromDestroyItemCount = 1;

            Assert.IsFalse(Deposit(store, FakeVaultWorld.MakeStack(1234, 7, 100), Owner, out _),
                "precondition: the throw must unwind out of TryDeposit so the caller sees a failure - Drain catches it and does not rethrow.");

            Assert.AreEqual(7L, store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger).Count,
                "precondition: the ledger credit committed before the throw, so the vault really does hold these units.");

            Assert.IsTrue(store.Version > before,
                "a deposit whose tail threw has still changed what GetEntries returns, so it must bump Version. Without the bump a window keeps its cached pre-deposit view, and since nothing is going to mutate this store afterwards it shows the wrong contents indefinitely.");
        }

        // ------------------------------------------- read-time grouping (2026-08-31 grouping design)

        /// <summary>
        /// Seeds one vault holding exactly these objects.
        ///
        /// NOTE the order they end up in. Container.TryAddToInventory defaults placementPosition to 0
        /// and bumps every existing item up (Container.cs:564-570), so items are inserted at the FRONT
        /// and the vault enumerates them in the REVERSE of the order passed here. No assertion below
        /// depends on which one wins the representative slot; they depend only on how many rows there
        /// are and what is in them.
        /// </summary>
        private Container SeedVaultHolding(params WorldObject[] items)
        {
            var container = SeedVault(capacity: 255, itemCount: 0, order: 1);

            foreach (var item in items)
                Assert.IsTrue(container.TryAddToInventory(item), "could not seed a vault item");

            return container;
        }

        /// <summary>A real bag's shape: 100 units, workmanship 77 over 12 items, which reads 6.42.</summary>
        private static WorldObject Bag(int structure = 100, int? itemWorkmanship = 77, int? numItemsInMaterial = 12, int value = 640, string name = null)
        {
            return FakeVaultWorld.MakeSalvageBag(20980, structure, itemWorkmanship, numItemsInMaterial, value, name);
        }

        private List<VaultEntry> StoredRows(AccountVaultStore store)
        {
            return store.GetEntries(0, -1).Where(e => e.Kind == VaultEntryKind.StoredItem).ToList();
        }

        [TestMethod]
        public void Grouping_TwoEquivalentBags_PresentAsOneRowOfTwo()
        {
            var a = Bag();
            var b = Bag();

            SeedVaultHolding(a, b);

            var rows = StoredRows(NewStore());

            Assert.AreEqual(1, rows.Count, "two equivalent bags are one panel row");
            Assert.IsTrue(rows[0].IsGroup);
            Assert.AreEqual(2L, rows[0].Count, "a group's Count is its member count");
            Assert.AreSame(rows[0].Members[0], rows[0].WorldObject, "the representative is the group's first member");
            Assert.AreEqual(rows[0].Members[0].Guid, rows[0].Guid, "and the row's guid is the representative's");
            CollectionAssert.AreEquivalent(new[] { a, b }, rows[0].Members.ToList(), "both bags are members of the one row");
        }

        /// <summary>
        /// Value is the reason grouping is restricted to ItemType.TinkeringMaterial: it is a weighted
        /// sum of what was salvaged, so two otherwise-identical bags routinely differ on it and would
        /// otherwise never group at all.
        /// </summary>
        [TestMethod]
        public void Grouping_BagsDifferingOnlyInValue_StillGroup()
        {
            SeedVaultHolding(Bag(value: 640), Bag(value: 1207));

            var rows = StoredRows(NewStore());

            Assert.AreEqual(1, rows.Count, "Value is a tolerated difference");
            Assert.AreEqual(2L, rows[0].Count);
        }

        /// <summary>
        /// Both bags are given the SAME name on purpose, so this test isolates Structure rather than
        /// passing on the derived name difference a real pair would also have.
        /// </summary>
        [TestMethod]
        public void Grouping_BagsDifferingInStructure_DoNotGroup()
        {
            SeedVaultHolding(Bag(structure: 100, name: "Salvage"), Bag(structure: 73, name: "Salvage"));

            var rows = StoredRows(NewStore());

            Assert.AreEqual(2, rows.Count, "Structure is part of the bucket key and must split the rows");
            Assert.IsFalse(rows[0].IsGroup);
            Assert.IsFalse(rows[1].IsGroup);
        }

        [TestMethod]
        public void Grouping_BagsDifferingInRoundedWorkmanship_DoNotGroup()
        {
            // 77/12 = 6.42 and 90/12 = 7.50. Everything else about the two is identical.
            SeedVaultHolding(Bag(itemWorkmanship: 77), Bag(itemWorkmanship: 90));

            var rows = StoredRows(NewStore());

            Assert.AreEqual(2, rows.Count, "the number the client renders is part of the bucket key");
        }

        /// <summary>
        /// The worked example from the design: two bags whose raw (ItemWorkmanship,
        /// NumItemsInMaterial) pairs differ but which both read 6.42 to the player. Rounding the key to
        /// what the client shows is what makes these one row instead of two.
        /// </summary>
        [TestMethod]
        public void Grouping_EquivalentWorkmanshipFromDifferentRawPairs_StillGroup()
        {
            var a = Bag(itemWorkmanship: 77, numItemsInMaterial: 12);
            var b = Bag(itemWorkmanship: 154, numItemsInMaterial: 24);

            Assert.AreEqual(a.Workmanship.Value, b.Workmanship.Value, 0.0001f,
                "precondition: these two really do render the same number, or the test proves nothing");

            SeedVaultHolding(a, b);

            var rows = StoredRows(NewStore());

            Assert.AreEqual(1, rows.Count, "(77, 12) and (154, 24) both read 6.42 and must group");
            Assert.AreEqual(2L, rows[0].Count);
        }

        /// <summary>
        /// Name is deliberately NOT in the tolerated-difference set. A bag's name is derived from its
        /// Structure, so an equal bucket key already implies an equal name for an untouched bag - which
        /// means a name difference can only be a deliberate rename, and a renamed bag is exactly the
        /// one the player needs to be able to pick out of the row.
        /// </summary>
        [TestMethod]
        public void Grouping_AHandRenamedBag_DoesNotGroup()
        {
            SeedVaultHolding(Bag(), Bag(name: "Keep this one"));

            var rows = StoredRows(NewStore());

            Assert.AreEqual(2, rows.Count, "a hand-renamed bag must keep its own row");
        }

        /// <summary>
        /// The negative control, and the one that matters most: the tolerated-difference set includes
        /// Value, so applying this rule outside TinkeringMaterial would merge two differently-priced
        /// weapons into one row with no way to tell them apart.
        /// </summary>
        [TestMethod]
        public void Grouping_NonTinkeringMaterialItems_NeverGroup()
        {
            var a = FakeVaultWorld.MakeStack(8000, 1, 100);
            var b = FakeVaultWorld.MakeStack(8000, 1, 100);

            Assert.AreEqual(ItemType.Misc, a.ItemType, "precondition: these are NOT TinkeringMaterial");

            SeedVaultHolding(a, b);

            var rows = StoredRows(NewStore());

            Assert.AreEqual(2, rows.Count, "two byte-identical Misc items are still two rows");
        }

        [TestMethod]
        public void GroupWithdraw_TakesKOfN_AndLeavesTheRest()
        {
            var vault = SeedVaultHolding(Bag(), Bag(), Bag(), Bag(), Bag());

            var store = NewStore();

            var group = StoredRows(store).Single();
            Assert.AreEqual(5L, group.Count);

            Assert.IsTrue(Withdraw(store, group, 2, Owner, out var withdrawn, out var reason), reason);

            Assert.AreEqual(2, withdrawn.Count, "k members must be handed over");
            Assert.AreEqual(2, withdrawn.Select(w => w.Guid.Full).Distinct().Count(), "and they must be DISTINCT biotas, not the same one twice");
            CollectionAssert.AreEqual(group.Members.Take(2).ToList(), withdrawn, "members are taken from the front, so repeated withdrawals are reproducible");

            Assert.AreEqual(3, vault.Inventory.Count, "N-k must be left in the vault");

            var remaining = StoredRows(store).Single();
            Assert.AreEqual(3L, remaining.Count, "and the row must now read N-k");
        }

        /// <summary>
        /// THE ANCHOR. A group listing pins market_listing.item_Guid to the group's representative -
        /// Members[0] - and nothing rewrites that guid as the listing sells down, because a rewrite
        /// would put a database write in the middle of the money path. What makes that safe is this
        /// take order: the market asks for GroupTakeOrder.Back, so the representative is removed only
        /// by the sale that empties the row, and that sale closes the listing anyway.
        ///
        /// The discriminating control sits in GroupWithdraw_TakesKOfN_AndLeavesTheRest above, which
        /// pins the DEFAULT (Front) to members 1..k. Without that pair this test would only restate
        /// that an enum parameter is read.
        /// </summary>
        [TestMethod]
        public void GroupWithdraw_BackTake_LeavesTheRepresentativeInTheVault()
        {
            var vault = SeedVaultHolding(Bag(), Bag(), Bag(), Bag(), Bag());

            var store = NewStore();

            var group = StoredRows(store).Single();
            Assert.AreEqual(5L, group.Count);

            var anchor = group.Guid;
            var lastTwo = group.Members.Skip(3).ToList();

            Assert.IsTrue(Withdraw(store, group, 2, Owner, GroupTakeOrder.Back, out var withdrawn, out var reason), reason);

            CollectionAssert.AreEqual(lastTwo, withdrawn, "Back takes the LAST k members, still in list order");
            Assert.IsFalse(withdrawn.Any(w => w.Guid == anchor), "the representative must not be among them");

            Assert.AreEqual(3, vault.Inventory.Count, "N-k must be left in the vault");
            Assert.IsTrue(vault.Inventory.ContainsKey(anchor), "and the representative must be one of them");

            var remaining = StoredRows(store).Single();
            Assert.AreEqual(3L, remaining.Count, "the row now reads N-k");
            Assert.AreEqual(anchor, remaining.Guid, "and it is still represented by the same guid a listing would name");
        }

        /// <summary>
        /// The pair that actually decides a group sale's outcome, driven through the REAL
        /// AccountVaultStore: the amount rule and the take order VaultMarketItemStore hands it. The
        /// test above pins what Back does; this pins that a SALE is what asks for it, which is the
        /// half a market fake cannot prove because the fake models the order rather than choosing it.
        ///
        /// Same no-injection-seam reason as the two VaultMarketItemStore_* tests below: the take
        /// resolves its store through AccountVaultManager.GetStore, which hardcodes the production
        /// backend.
        /// </summary>
        [TestMethod]
        public void VaultMarketItemStore_SellingPartOfAGroup_TakesFromTheBackAndKeepsTheAnchor()
        {
            var vault = SeedVaultHolding(Bag(), Bag(), Bag(), Bag(), Bag());

            var store = NewStore();

            var group = StoredRows(store).Single();
            Assert.IsTrue(group.IsGroup, "precondition: five equivalent bags are one group row");

            var anchor = group.Guid;

            Assert.AreEqual(GroupTakeOrder.Back, VaultMarketItemStore.TakeOrderForSale(group),
                "a sale must draw a group from the back, or the listing loses the guid it is pinned to");

            // The control that keeps this from being a restatement of the ternary: a LONE stored biota
            // is not a group and must keep the default order.
            Assert.AreEqual(GroupTakeOrder.Front, VaultMarketItemStore.TakeOrderForSale(VaultEntry.ForItem(Bag())));

            var amount = VaultMarketItemStore.WithdrawAmountForSale(group, 2);
            Assert.AreEqual(2, amount);

            Assert.IsTrue(Withdraw(store, group, amount, Owner, VaultMarketItemStore.TakeOrderForSale(group),
                                   out var withdrawn, out var reason), reason);

            Assert.AreEqual(2, withdrawn.Count, "exactly the listed count moves");
            Assert.IsFalse(withdrawn.Any(w => w.Guid == anchor), "and never the representative while members remain");
            Assert.IsTrue(vault.Inventory.ContainsKey(anchor));
            Assert.AreEqual(anchor, StoredRows(store).Single().Guid, "the row is still named by the guid the listing holds");
        }

        [TestMethod]
        public void GroupWithdraw_MoreThanTheGroupHolds_IsRefused()
        {
            var vault = SeedVaultHolding(Bag(), Bag(), Bag());

            var store = NewStore();

            var group = StoredRows(store).Single();

            Assert.IsFalse(Withdraw(store, group, 4, Owner, out var withdrawn, out var reason),
                "an over-withdraw is refused, not clamped");
            Assert.AreEqual(0, withdrawn.Count, "and NOTHING may leave the vault on the refusal path");
            Assert.AreEqual(3, vault.Inventory.Count);
            StringAssert.Contains(reason ?? "", "3", "the refusal has to tell the player how many are actually there");
        }

        /// <summary>
        /// The existing rule is untouched for a lone stored biota: it did not collapse into the ledger
        /// precisely because it is not a plain identical-to-fresh stack, so it is indivisible.
        /// </summary>
        [TestMethod]
        public void Withdraw_ASingletonBag_StillRefusesAPartialWithdraw()
        {
            SeedVaultHolding(Bag());

            var store = NewStore();

            var row = StoredRows(store).Single();
            Assert.IsFalse(row.IsGroup, "one bag is an ordinary item entry, never a group of one");

            Assert.IsFalse(Withdraw(store, row, 2, Owner, out _, out var reason));
            Assert.AreEqual("A stored item can only be withdrawn whole.", reason);
        }

        /// <summary>
        /// THE OVER-DELIVERY. A group row is several separate whole biotas on one panel line, and at
        /// the time MarketManager.List pinned every stored-item listing to a count of 1 - so a listed
        /// group was priced as ONE bag. The sale take used to read the entry's own Count for anything
        /// that was not a ledger, and a group's Count is its MEMBER count, so the buyer received every
        /// bag on the line for one bag's price. (List now accepts a count up to a group's member
        /// count; the amount rule below is what stops the count from being ignored either way.)
        ///
        /// Driven through the real AccountVaultStore rather than the market fakes, for the same reason
        /// VaultMarketItemStore_ClassifyDepositFailure_ReadsVaultFullFromARealCappedStore is: the take
        /// resolves its store through AccountVaultManager.GetStore, which hardcodes the production
        /// backend and offers no seam. What is exercised here is the pair that decides the outcome -
        /// the amount rule and the withdraw it dispatches.
        /// </summary>
        [TestMethod]
        public void VaultMarketItemStore_SellingOneOfAGroup_TakesOneMemberAndLeavesTheRest()
        {
            var vault = SeedVaultHolding(Bag(), Bag(), Bag(), Bag(), Bag());

            var store = NewStore();

            var group = StoredRows(store).Single();
            Assert.IsTrue(group.IsGroup, "precondition: five equivalent bags are one group row");
            Assert.AreEqual(5L, group.Count, "precondition: a group's Count is its MEMBER count, which is what used to be taken");

            // A group listing may now name any count up to its member count, but 1 is still the case
            // this test is about: it is the smallest one, and it is the one the over-delivery turned
            // into all five.
            var amount = VaultMarketItemStore.WithdrawAmountForSale(group, 1);

            Assert.AreEqual(1, amount, "a group must take the COUNT that was listed and paid for, never its member count");

            Assert.IsTrue(Withdraw(store, group, amount, Owner, out var withdrawn, out var reason), reason);

            Assert.AreEqual(1, withdrawn.Count, "exactly one member is transferred to the buyer");
            Assert.AreEqual(4, vault.Inventory.Count, "N-1 members stay in the seller's vault");
            Assert.AreEqual(4L, StoredRows(store).Single().Count, "and the row now reads N-1");
        }

        /// <summary>
        /// The behaviour that must NOT change while fixing the group case. A lone stored biota is
        /// indivisible - a stored stack of 200 is listed and sold as one thing and comes out whole -
        /// and TryWithdraw refuses any other amount for one, so the listed count cannot be used here.
        /// The control below is that refusal, which is what makes this test discriminating rather than
        /// a restatement of the amount rule.
        /// </summary>
        [TestMethod]
        public void VaultMarketItemStore_SellingAStoredStack_StillTakesItWhole()
        {
            var vault = SeedVaultHolding(FakeVaultWorld.MakeStack(1234, 200, 250));

            var store = NewStore();

            var row = StoredRows(store).Single();
            Assert.IsFalse(row.IsGroup, "precondition: one stack is an ordinary stored item, never a group");
            Assert.AreEqual(200L, row.Count, "precondition: a stored biota's Count is its StackSize");

            var amount = VaultMarketItemStore.WithdrawAmountForSale(row, 1);

            Assert.AreEqual(200, amount, "a single stored biota comes out whole, whatever count was listed");

            Assert.IsTrue(Withdraw(store, row, amount, Owner, out var withdrawn, out var reason), reason);

            Assert.AreEqual(1, withdrawn.Count, "one object leaves the vault");
            Assert.AreEqual(200, withdrawn[0].StackSize ?? 1, "and it carries all 200 units");
            Assert.AreEqual(0, vault.Inventory.Count, "the stack leaves the seller's vault whole");
        }

        /// <summary>
        /// The control for the test above: taking the listed count from a single stored biota is
        /// REFUSED by the vault, so the amount rule cannot simply use the listed count everywhere.
        /// </summary>
        [TestMethod]
        public void Withdraw_AStoredStackAtTheListedCount_IsRefused()
        {
            var vault = SeedVaultHolding(FakeVaultWorld.MakeStack(1234, 200, 250));

            var store = NewStore();

            var row = StoredRows(store).Single();

            Assert.IsFalse(Withdraw(store, row, 1, Owner, out var withdrawn, out var reason));
            Assert.AreEqual("A stored item can only be withdrawn whole.", reason);
            Assert.AreEqual(0, withdrawn.Count);
            Assert.AreEqual(1, vault.Inventory.Count, "nothing leaves the vault on the refusal path");
        }

        /// <summary>
        /// The third kind, also unchanged: a ledger row is divisible, so a partial sale takes exactly
        /// the units that were bought and leaves the rest on the ledger.
        /// </summary>
        [TestMethod]
        public void VaultMarketItemStore_SellingFromALedger_TakesExactlyTheRequestedCount()
        {
            SeedLedger(1234, 100);

            world.ItemMaxStackSize = 100;

            var store = NewStore();

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger);
            Assert.AreEqual(100L, entry.Count, "precondition: the ledger holds 100 units");

            var amount = VaultMarketItemStore.WithdrawAmountForSale(entry, 20);

            Assert.AreEqual(20, amount, "a ledger row honours the count that was bought");

            Assert.IsTrue(Withdraw(store, entry, amount, Owner, out var withdrawn, out var reason), reason);

            Assert.AreEqual(20, withdrawn.Sum(w => w.StackSize ?? 1), "exactly the bought units are handed over");
            Assert.AreEqual(80, backend.Stacks.Single(s => s.Wcid == 1234).Count, "the rest stays on the ledger");
        }

        [TestMethod]
        public void EntryCount_CountsAGroupOfNAsOneRow()
        {
            SeedVaultHolding(Bag(), Bag(), Bag(), Bag(), Bag(), FakeVaultWorld.MakeStack(8000, 1, 100));
            SeedLedger(1234, 40);

            var store = NewStore();

            Assert.AreEqual(3, store.EntryCount,
                "one group row plus one ungroupable stored item plus one ledger row - the cap counts RENDERED ROWS");
        }

        /// <summary>
        /// The deposit-time half of the same rule. Without it the cap would count a row
        /// EntryCountLocked does not, and a vault of equivalent salvage would refuse deposits while its
        /// panel showed a handful of rows.
        /// </summary>
        [TestMethod]
        public void Deposit_IntoAnExistingGroup_ConsumesNoEntry()
        {
            SeedVaultHolding(Bag(), Bag());

            var store = NewStore();

            Assert.IsFalse(store.WouldAddEntry(Bag()), "an equivalent bag joins the row that is already drawn");

            // The controls, both directions. A bag that would open a NEW row costs an entry, and so
            // does an ungroupable item - otherwise the assertion above would pass with the rule
            // inverted to "nothing ever costs an entry".
            Assert.IsTrue(store.WouldAddEntry(Bag(structure: 73)), "a bag that cannot join an existing group costs an entry");
            Assert.IsTrue(store.WouldAddEntry(FakeVaultWorld.MakeStack(8000, 1, 100)), "an ungroupable stored item always costs an entry");
        }

        /// <summary>
        /// End to end against the real cap, because WouldAddEntry above is only half the story: the
        /// deposit path re-derives the same question through AddsEntryLocked and the two have drifted
        /// apart once before.
        /// </summary>
        [TestMethod]
        public void Deposit_AtTheCap_IsStillAcceptedWhenItJoinsAGroup()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var vault = SeedVaultHolding(Bag());

            var store = NewStore();

            Assert.AreEqual(1, store.EntryCount, "precondition: the vault is exactly at the cap");

            Assert.IsTrue(Deposit(store, Bag(), Owner, out var reason), reason);
            Assert.AreEqual(2, vault.Inventory.Count, "an equivalent bag joins the drawn row and is accepted at the cap");

            // Control: the cap is genuinely live, so the acceptance above is the grouping rule and not
            // a cap that stopped working.
            Assert.IsFalse(Deposit(store, Bag(structure: 73), Owner, out reason), "a bag that opens a new row must still be refused at the cap");
            StringAssert.Contains(reason ?? "", "full");
            Assert.AreEqual(2, vault.Inventory.Count);
        }

        /// <summary>
        /// The grouping memo must never be served across a mutation, and the version stamp alone
        /// cannot prove it has not been.
        ///
        /// Every vault mutation applies its change under stateLock, RELEASES the lock, does a database
        /// round-trip, and only then bumps Version. GetEntries and EntryCount are plain locked readers
        /// and do not go through the mutation queue, so a reader landing in that gap sees mutated
        /// vaults with an unchanged version - and a memo keyed on the version alone hands back the
        /// PRE-mutation group list, including WorldObject references to items already removed from
        /// their container.
        ///
        /// The observation runs on the mutation thread itself, from inside FakeVaultWorld.SaveBiota,
        /// so the window is hit deterministically rather than raced for. The version equality assert is
        /// the precondition that proves the observation really landed inside it.
        /// </summary>
        [TestMethod]
        public void Grouping_IsNotServedStaleFromInsideAWithdrawsSaveWindow()
        {
            var vault = SeedVaultHolding(Bag(), Bag(), Bag(), Bag(), Bag());

            var store = NewStore();

            var group = StoredRows(store).Single();
            Assert.AreEqual(5L, group.Count, "precondition: the memo is populated and reads five members");

            var versionBefore = store.Version;

            List<VaultEntry> midFlight = null;
            var versionAtObservation = -1L;

            world.DuringSaveBiota = _ =>
            {
                if (midFlight != null)
                    return;

                versionAtObservation = store.Version;
                midFlight = store.GetEntries(0, -1).ToList();
            };

            Assert.IsTrue(Withdraw(store, group, 2, Owner, out var withdrawn, out var reason), reason);
            Assert.AreEqual(2, withdrawn.Count);

            Assert.IsNotNull(midFlight, "the observation never ran, so this test proves nothing");
            Assert.AreEqual(versionBefore, versionAtObservation,
                "precondition: the observation must land BEFORE the version bump - that gap IS the window under test");

            var members = midFlight.Where(e => e.Kind == VaultEntryKind.StoredItem).SelectMany(e => e.Members).ToList();

            foreach (var member in members)
            {
                Assert.IsTrue(vault.Inventory.ContainsKey(member.Guid),
                    $"a mid-flight read returned 0x{member.Guid.Full:X8}, which had already been removed from the vault");
            }

            Assert.AreEqual(3, members.Count, "the mid-flight read must report the post-withdraw membership");
        }

        /// <summary>
        /// The cap half of the same window. Charging a row whose every member has already left the
        /// vault can refuse a deposit that should have been accepted, and the row count is what makes
        /// this discriminating: EntryCount would read the same either way if the group merely shrank.
        /// </summary>
        [TestMethod]
        public void EntryCount_IsNotServedStaleFromInsideAWithdrawsSaveWindow()
        {
            SeedVaultHolding(Bag(), Bag(), Bag(structure: 73));

            var store = NewStore();

            Assert.AreEqual(2, store.EntryCount, "precondition: one group of two, plus one bag that cannot join it");

            var group = StoredRows(store).Single(r => r.IsGroup);

            var versionBefore = store.Version;
            var observed = -1;
            var versionAtObservation = -1L;

            world.DuringSaveBiota = _ =>
            {
                if (observed >= 0)
                    return;

                versionAtObservation = store.Version;
                observed = store.EntryCount;
            };

            Assert.IsTrue(Withdraw(store, group, 2, Owner, out _, out var reason), reason);

            Assert.AreEqual(versionBefore, versionAtObservation, "precondition: observed inside the pre-bump window");
            Assert.AreEqual(1, observed,
                "the cap must not be charged for a group row whose every member has already left the vault");
        }

        /// <summary>
        /// A read of the vault must not WRITE to a stored item.
        ///
        /// WorldObject.Workmanship is not a getter: when ItemWorkmanship / (NumItemsInMaterial ?? 1)
        /// falls outside [1, 10] it rewrites ItemWorkmanship in place through SetProperty
        /// (WorldObject_Properties.cs:1587-1598). The bucket key used to read it, which put a silent
        /// unaudited write on every GetEntries, EntryCount, deposit and withdraw for any account
        /// holding a TinkeringMaterial item - under stateLock only, with no BiotaDatabaseLock.
        ///
        /// 77 over 1 item is the out-of-range shape. The second half matters as much as the first: the
        /// key must still be STABLE, which it could not be while the first read rewrote the value the
        /// second read keyed on.
        /// </summary>
        [TestMethod]
        public void Grouping_NeverRewritesAStoredItemsWorkmanship()
        {
            var bag = Bag(itemWorkmanship: 77, numItemsInMaterial: 1);
            var twin = Bag(itemWorkmanship: 77, numItemsInMaterial: 1);

            Assert.AreEqual(77, bag.GetProperty(PropertyInt.ItemWorkmanship), "precondition: 77 over 1 item is far outside the 1..10 the client renders");

            SeedVaultHolding(bag, twin);

            var store = NewStore();

            store.GetEntries(0, -1);
            var count = store.EntryCount;
            var rows = StoredRows(store);

            Assert.AreEqual(77, bag.GetProperty(PropertyInt.ItemWorkmanship),
                "reading the vault must never rewrite a stored item's ItemWorkmanship");
            Assert.AreEqual(77, twin.GetProperty(PropertyInt.ItemWorkmanship));
            Assert.AreEqual(1, bag.GetProperty(PropertyInt.NumItemsInMaterial));

            Assert.AreEqual(1, count, "and the two must still group, on a key that is the same on every read");
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(2L, rows[0].Count);
        }

        /// <summary>
        /// The bucket key, not the diff, is the real gate on whether a workmanship difference matters:
        /// VaultCollapse.AreGroupable forgives ItemWorkmanship and NumItemsInMaterial outright once two
        /// items already share a bucket. So a key that loses a distinction MERGES rows, it does not
        /// merely split them.
        ///
        /// These two bags have the same RAW quotient (50000 over 1, and 100000 over 2), so a key built
        /// from the raw quotient alone puts them in one bucket and the diff then waves them through.
        /// The recovery formula separates them, because it recomputes from ItemWorkmanship and
        /// Structure and ignores NumItemsInMaterial entirely: 50000/10000/5 is 1.0 and 100000/10000/5
        /// is 2.0.
        ///
        /// Legacy botched-formula data is what makes this reachable, and the player can tell the two
        /// apart: the client-visible Workmanship still clamps, so they appraise at different tiers once
        /// withdrawn. The scratch pair below pins exactly that, through the real getter, on objects
        /// nothing else in this test depends on.
        /// </summary>
        [TestMethod]
        public void Grouping_BagsWithEqualRawQuotientsButDifferentRecoveredWorkmanship_DoNotMerge()
        {
            Assert.AreEqual((float)50000 / 1, (float)100000 / 2, 0f,
                "precondition: the RAW quotients collide, which is the whole reason a key without the recovery branch would bucket these together");

            var scratchA = Bag(structure: 5, itemWorkmanship: 50000, numItemsInMaterial: 1);
            var scratchB = Bag(structure: 5, itemWorkmanship: 100000, numItemsInMaterial: 2);

            Assert.AreEqual(1.0f, scratchA.Workmanship.Value, 0.001f, "precondition: the client renders these two at DIFFERENT workmanship");
            Assert.AreEqual(2.0f, scratchB.Workmanship.Value, 0.001f);

            SeedVaultHolding(
                Bag(structure: 5, itemWorkmanship: 50000, numItemsInMaterial: 1),
                Bag(structure: 5, itemWorkmanship: 100000, numItemsInMaterial: 2));

            var rows = StoredRows(NewStore());

            Assert.AreEqual(2, rows.Count,
                "two bags the client shows at different workmanship must never share one row - the bucket key has to carry the recovery branch, because the diff forgives ItemWorkmanship and NumItemsInMaterial once the bucket matches");
        }

        /// <summary>
        /// A double-to-int cast in .NET SATURATES rather than throwing: (int)3e9 is int.MaxValue,
        /// (int)double.NegativeInfinity is int.MinValue, (int)double.NaN is 0. int.MinValue is exactly
        /// the bucket key's "no workmanship at all" sentinel, so an unguarded cast lets a corrupted
        /// item bucket with every item that has none - and the diff then forgives the difference,
        /// because ItemWorkmanship present-vs-absent is a tolerated difference.
        ///
        /// ItemWorkmanship has no range validation where it is written (AdminCommands.cs:4676-4688
        /// parses an admin-typed float straight into it), so this needs no zero denominator to reach.
        /// </summary>
        [TestMethod]
        public void Grouping_ACorruptedWorkmanshipNeverBucketsWithAnItemThatHasNone()
        {
            var corrupted = Bag(itemWorkmanship: -30000000, numItemsInMaterial: 1);
            var none = Bag(itemWorkmanship: null, numItemsInMaterial: null);

            Assert.IsNull(none.GetProperty(PropertyInt.ItemWorkmanship), "precondition: this bag really carries no workmanship at all");
            Assert.AreEqual(-30000000, corrupted.GetProperty(PropertyInt.ItemWorkmanship));

            SeedVaultHolding(corrupted, none);

            var rows = StoredRows(NewStore());

            Assert.AreEqual(2, rows.Count,
                "a corrupted workmanship must not saturate onto the no-workmanship sentinel and merge with an item that has none");
        }

        [TestMethod]
        public void Audit_RecordsBothSidesOfARoundTrip()
        {
            var store = NewStore();

            world.PristineResult = true;
            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(1234, 40, 100), Owner, out var reason), reason);

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.Kind == VaultEntryKind.Ledger);
            Assert.IsTrue(Withdraw(store, entry, 15, Owner, out _, out reason), reason);

            var deposit = backend.Logs.Single(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Deposit);
            var withdraw = backend.Logs.Single(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Withdraw);

            Assert.AreEqual(OwnerAccount, deposit.OwnerAccountId);
            Assert.AreEqual(OwnerCharacter, deposit.ActorCharacterGuid);
            Assert.AreEqual(40L, deposit.Count);
            Assert.AreEqual(1234u, deposit.Wcid);
            Assert.IsNull(deposit.ItemGuid, "a collapsed deposit has no biota behind it");

            Assert.AreEqual(15L, withdraw.Count);
            Assert.AreEqual(1234u, withdraw.Wcid);
        }

        [TestMethod]
        public void LedgerWithdraw_WritesItsAuditRowBeforeTheDebit()
        {
            const string relativePath = "Source/ACE.Server/Entity/AccountVault/AccountVaultStore.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("private bool WithdrawFromLedger(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "WithdrawFromLedger was not found.");

            var iDebit = code.IndexOf("TryAdjustAccountVaultStack(AccountId, entry.Wcid, -amount", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iDebit > iMethod, "the ledger debit was not found inside WithdrawFromLedger.");

            var iLog = code.IndexOf("WriteLog(AccountVaultAction.Withdraw", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iLog >= 0 && iLog < iDebit,
                "the Withdraw audit row must be written BEFORE the debit. The debit is an autocommitted UPDATE and the replacement stacks are not persisted until the vendor saves them, so a crash in between destroys the units - and the audit row is the only thing that makes that reconstructable.");
        }

        // ------------------------------- review round, finding 1: the retry must not abort its caller

        /// <summary>
        /// Seeds one entry in the store's private pendingDepositSaves list by driving a REAL deposit
        /// whose item save reports failure - DepositToVault's own callback is what enrols it, so this
        /// reaches the production enrolment path rather than reflecting a value into the field.
        /// </summary>
        private WorldObject SeedPendingDepositSave(AccountVaultStore store)
        {
            world.PristineResult = false;   // the stored-biota path, which is the one that enrols a retry

            var item = FakeVaultWorld.MakeStack(4330, 1, 100);

            // Only THIS object's save fails; the vault container's must succeed or the deposit aborts
            // before the enrolment path is reached.
            world.FailSaveBiotaCounts[item.Guid.Full] = 1;

            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);
            Assert.AreEqual(1, world.SaveBiotaCalls(item), "sanity: the deposit must have attempted the item's save exactly once");

            return item;
        }

        /// <summary>
        /// Review round, finding 1. Drain ran RetryPendingDepositSaves and the dequeued work item
        /// inside the SAME try, so a throw from the retry aborted the iteration BEFORE the work ran.
        ///
        /// That work usually belongs to a DIFFERENT caller, and the damage is to the contract rather
        /// than to the queue: Drain's catch logs and carries on, so Enqueue(work, out thrown) hands
        /// that caller thrown == null with its locals still at their defaults - and thrown == null is
        /// documented as "the work completed". The caller reads a refusal for a mutation that never ran.
        ///
        /// The trigger is not hypothetical: the retry reaches world.SaveBiota ->
        /// SerializedShardDatabase's BlockingCollection.Add, which throws once CompleteAdding() has run
        /// at shutdown.
        ///
        /// The mechanism is that the work RAN - a sentinel the closure overwrites, not a bool that
        /// could be confused with a default - plus the save-call count proving the throwing retry was
        /// actually reached, so the test cannot pass vacuously.
        /// </summary>
        [TestMethod]
        public void Drain_WhenTheDepositSaveRetryThrows_StillRunsTheQueuedWork()
        {
            var store = NewStore();

            var pending = SeedPendingDepositSave(store);

            // The next SaveBiota call throws instead of failing politely - which is the retry, since
            // the retry is the first thing Drain does for the next dequeued item.
            world.ThrowFromSaveBiotaCount = 1;

            var outcome = "the queued work never ran";

            var queued = store.Enqueue(() => outcome = "the queued work ran", out var thrown);

            Assert.IsTrue(queued, "sanity: the store must have accepted the work");

            Assert.AreEqual(2, world.SaveBiotaCalls(pending),
                "sanity: the throwing retry must actually have been reached, or this test proves nothing about it");

            Assert.AreEqual("the queued work ran", outcome,
                "a throw from the deposit-save retry must not abort the dequeued work item that shares its try. The work belongs to a different caller, and skipping it silently hands that caller thrown == null with default locals - which Enqueue's contract says means the work completed.");

            Assert.IsNull(thrown, "the work itself did not throw, so the caller must be told so");
        }

        /// <summary>
        /// The same guard at the other call site, where the consequence is worse. TryEvict runs on the
        /// world heartbeat from AccountVaultManager.Tick, which wraps nothing - so a throw escaping
        /// here takes the whole idle sweep down for that pass, and with it every OTHER account's store.
        ///
        /// The store must decline eviction rather than throw: the retry could not run, so the
        /// unconfirmed save is still unconfirmed, and dropping the store would drop the retry with it.
        /// </summary>
        [TestMethod]
        public void TryEvict_WhenTheDepositSaveRetryThrows_DeclinesRatherThanEscapingIntoTheIdleSweep()
        {
            var store = NewStore();

            var pending = SeedPendingDepositSave(store);

            world.ThrowFromSaveBiotaCount = 1;

            // Well past IdleEvictionSeconds, so every other guard in TryEvict passes and the retry is
            // genuinely reached.
            var evicted = store.TryEvict(store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1.0);

            Assert.AreEqual(2, world.SaveBiotaCalls(pending), "sanity: the throwing retry must actually have been reached");

            Assert.IsFalse(evicted,
                "a store with an unconfirmed deposit save must decline eviction - and it must reach that decision rather than throwing out of the idle sweep, which has no guard of its own");

            Assert.IsFalse(store.IsEvicted);
        }

        // ------------------------------------------------ fix round 2, F3: timed items

        /// <summary>
        /// F3, a live exploit rather than a hypothetical. A Lifespan-bearing stackable that is
        /// otherwise template-identical is PRISTINE by VaultCollapse's rule - the diff deliberately
        /// ignores CreationTimestamp, because that is when the object was made and not what it is - so
        /// it collapsed into a wcid ledger row, its biota was destroyed, and the withdrawal built a
        /// fresh object stamped with a CreationTimestamp of now. WorldObject_Tick computes expiry from
        /// that, so the timer restarted; and a vault container is never ticked, so nothing expired
        /// while it sat there either. Deposit, withdraw, repeat: an unlimited timed item.
        ///
        /// Both halves are asserted. The refusal alone would pass with the whole vault refusing
        /// everything.
        /// </summary>
        [TestMethod]
        public void Deposit_RefusesATimedItem_AndStillAcceptsAnUntimedOne()
        {
            world.PristineResult = true;    // exactly the case the exploit needs: the item DOES collapse

            var store = NewStore();

            var timed = FakeVaultWorld.MakeStack(4320, 1, 100);
            timed.Lifespan = 300;

            Assert.IsFalse(Deposit(store, timed, Owner, out var timedReason), "a Lifespan-bearing item must never enter the vault");
            Assert.AreEqual("A timed item cannot be stored in your vault.", timedReason);
            Assert.IsFalse(timed.IsDestroyed, "a refused deposit must never destroy the item");
            CollectionAssert.DoesNotContain(world.Destroyed, (WorldObject)timed);
            Assert.AreEqual(0, store.GetEntries(0, -1).Count, "nothing may have been credited");

            // The control: the guard must be about Lifespan, not about stackables or about pristine
            // items generally. An identical item with no Lifespan still collapses into the ledger.
            var untimed = FakeVaultWorld.MakeStack(4320, 1, 100);

            Assert.IsTrue(Deposit(store, untimed, Owner, out var untimedReason), untimedReason);
            Assert.AreEqual(1L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger).Count);
        }

        /// <summary>
        /// The guard is on Lifespan itself, not on whether the item has already expired: a timed item
        /// with plenty of time left is exactly the one worth storing, and it is the one the reset
        /// benefits.
        /// </summary>
        [TestMethod]
        public void Deposit_RefusesATimedItemEvenWhenItKeepsItsBiota()
        {
            world.PristineResult = false;   // the stored-biota path, which does not collapse at all

            var store = NewStore();

            var timed = FakeVaultWorld.MakeStack(4321, 1, 100);
            timed.Lifespan = 86400;

            Assert.IsFalse(Deposit(store, timed, Owner, out var reason),
                "the refusal must not depend on the item collapsing - a stored biota in an unticked vault container does not expire either, so its clock is equally suspended");
            Assert.AreEqual("A timed item cannot be stored in your vault.", reason);
        }

        // ------------------------------------------------ fix round 2, F1: ledger outcome taxonomy

        /// <summary>
        /// F1. A ledger CREDIT that committed but could not be read back must be treated as APPLIED:
        /// the item is destroyed, the Deposit row is written, and the store re-reads the ledger rather
        /// than trusting the meaningless newCount it was handed.
        ///
        /// The mechanism, not the end state: <see cref="FakeVaultBackend.StackReads"/> proves the
        /// re-read actually ran. Before the fix, TryAdjustAccountVaultStack collapsed this case into
        /// the same `false` a refusal returns, so the store kept the item, told the player the vault
        /// was unavailable, and left the units credited - a dupe on every retry.
        /// </summary>
        [TestMethod]
        public void LedgerDeposit_WhenTheCreditCommittedButTheCountIsUnknown_StoresTheItemAndRereadsTheLedger()
        {
            const uint wcid = 4310;

            world.PristineResult = true;    // the ledger path, not the stored-biota path
            backend.LedgerAdjustCountUnknownWcids.Add(wcid);

            var store = NewStore();
            var item = FakeVaultWorld.MakeStack(wcid, 7, 100);

            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);

            CollectionAssert.Contains(world.Destroyed, item, "a credit that committed must destroy the item, exactly as an ordinary credit does");
            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Deposit), "the deposit must be audited");

            Assert.AreEqual(2, backend.StackReads,
                "the store must RE-READ the ledger after a credit whose count it could not learn - one read for the initial load, one for the invalidation. Without the re-read the store would carry the meaningless newCount of 0.");

            var ledgerEntry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger);
            Assert.AreEqual(wcid, ledgerEntry.Wcid);
            Assert.AreEqual(7L, ledgerEntry.Count, "the re-read must land on the count the database actually holds, which is the one nobody told the store");
        }

        /// <summary>
        /// F1, the deposit half of the documented asymmetry. An UPDATE that threw means nobody knows
        /// whether the credit landed, so the item is KEPT - a visible dupe an operator can reconcile
        /// beats destroying an item against a credit that may never have happened - and the ledger is
        /// invalidated, because it may have moved.
        ///
        /// The kept item alone is NOT the mechanism: the pre-fix code kept it too, by reading the
        /// failure as a refusal. The re-read is what is new, and what a reverted fix loses.
        /// </summary>
        [TestMethod]
        public void LedgerDeposit_WhenTheCreditOutcomeIsUnknown_KeepsTheItemAndStillInvalidatesTheLedger()
        {
            const uint wcid = 4311;

            world.PristineResult = true;
            backend.LedgerAdjustFailedWcids.Add(wcid);

            var store = NewStore();
            var item = FakeVaultWorld.MakeStack(wcid, 3, 100);

            Assert.IsFalse(Deposit(store, item, Owner, out var reason), "an unknown credit outcome must not report success");
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);

            Assert.IsFalse(item.IsDestroyed, "the item must never be destroyed against a credit that may not have landed");
            CollectionAssert.DoesNotContain(world.Destroyed, item);
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Deposit), "nothing provably happened, so nothing is audited as a deposit");

            Assert.AreEqual(2, backend.StackReads,
                "an unknown outcome may still have moved the ledger, so the store must re-read it. This is the assertion the pre-fix code fails: it read the failure as a refusal and left its in-memory ledger untouched.");
        }

        /// <summary>
        /// F1, the withdraw half, and the finding that motivated the whole taxonomy.
        ///
        /// A debit that committed and then lost its read-back used to return false, which made
        /// WithdrawFromLedger write a balancing Return row and hand over nothing: the units left the
        /// ledger, the player got nothing, and the audit trail shows a Withdraw cancelled by a Return -
        /// a real loss recorded as a non-event. The items must be delivered and NO Return row written.
        /// </summary>
        [TestMethod]
        public void LedgerWithdraw_WhenTheDebitCommittedButTheCountIsUnknown_DeliversAndWritesNoReturnRow()
        {
            const uint wcid = 4312;

            SeedLedger(wcid, 20);
            backend.LedgerAdjustCountUnknownWcids.Add(wcid);

            var store = NewStore();
            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == wcid);

            Assert.IsTrue(Withdraw(store, entry, 5, Owner, out var withdrawn, out var reason), reason);

            Assert.AreEqual(5, withdrawn.Sum(w => w.StackSize ?? 1), "the units must actually be handed over");

            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Return),
                "a debit that committed must NEVER be balanced by a Return row - that is what erases the loss from the audit trail");
            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Withdraw));

            var remaining = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == wcid);
            Assert.AreEqual(15L, remaining.Count, "the re-read must land on the debited count the store was never told");
        }

        /// <summary>
        /// F1, the withdraw half for an outcome that is unknown rather than merely uncounted. Same
        /// answer, and deliberately so: the units may already be gone and the player has nothing, so
        /// handing the items over with a marker in the log beats writing a Return row over a debit that
        /// may have landed.
        /// </summary>
        [TestMethod]
        public void LedgerWithdraw_WhenTheDebitOutcomeIsUnknown_DeliversAndWritesNoReturnRow()
        {
            const uint wcid = 4313;

            SeedLedger(wcid, 20);

            var store = NewStore();
            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == wcid);

            // Armed AFTER the entry is read, so the initial load still sees a normal ledger.
            backend.LedgerAdjustFailedWcids.Add(wcid);

            Assert.IsTrue(Withdraw(store, entry, 5, Owner, out var withdrawn, out var reason), reason);

            Assert.AreEqual(5, withdrawn.Sum(w => w.StackSize ?? 1));
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Return),
                "an unknown debit outcome must not write the balancing Return row either - the debit may have landed");
        }

        /// <summary>
        /// The control for the two tests above: a PROVABLE refusal - the only outcome that still means
        /// the ledger did not move - must still refuse, still write the balancing Return row, and hand
        /// over nothing. Without this, the fix above is indistinguishable from having deleted the
        /// over-withdraw guard.
        /// </summary>
        [TestMethod]
        public void LedgerWithdraw_WhenTheDebitIsRefused_StillWritesTheBalancingReturnRow()
        {
            const uint wcid = 4314;

            SeedLedger(wcid, 20);

            var store = NewStore();
            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == wcid);

            backend.FailLedgerAdjustWcids.Add(wcid);

            Assert.IsFalse(Withdraw(store, entry, 5, Owner, out var withdrawn, out _));

            Assert.AreEqual(0, withdrawn.Count);
            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)ACE.Entity.Enum.AccountVaultAction.Return),
                "a refused debit still has to balance its own Withdraw row");
        }

        /// <summary>
        /// F1's fail-safe. If the invalidating re-read ALSO fails, the store knows its ledger is wrong
        /// for at least one wcid and must refuse rather than serve the stale number - a player acts on
        /// a count, so a wrong one is worse than an outage. It heals on the next successful read.
        /// </summary>
        [TestMethod]
        public void Ledger_WhenTheInvalidatingRereadAlsoFails_TheStoreRefusesRatherThanServeAStaleCount()
        {
            const uint wcid = 4315;

            world.PristineResult = true;
            backend.LedgerAdjustCountUnknownWcids.Add(wcid);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "sanity: the initial load must succeed, or this test would be measuring that instead");

            // Only now does the ledger read start failing, so the invalidation cannot complete.
            backend.FailStackRead = true;

            Assert.IsTrue(Deposit(store, FakeVaultWorld.MakeStack(wcid, 4, 100), Owner, out var reason), reason);

            Assert.IsFalse(store.TryCheckReady(out var failReason), "a store holding a knowingly stale ledger must refuse");
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, failReason);
            Assert.AreEqual(0, store.GetEntries(0, -1).Count, "and it must serve nothing while it is refusing");

            // Heals: the next successful read clears the pending reload.
            backend.FailStackRead = false;

            Assert.IsTrue(store.TryCheckReady(out _));
            Assert.AreEqual(4L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == wcid).Count);
        }

        #region Deposit-path per-item constant (2026-09-23)

        /// <summary>
        /// Deposits <paramref name="items"/> inside ONE queued work item, so they share a single
        /// <c>Drain</c>. The per-item <see cref="Deposit"/> helper enqueues separately and therefore
        /// drains separately, which is the opposite of what the coalescing assertions need to see.
        ///
        /// Failures are collected rather than asserted inline: Drain catches whatever a work item
        /// throws and does not rethrow, so an Assert in there would be swallowed into a log line and
        /// the test would pass on a deposit that never happened.
        /// </summary>
        private static List<string> DepositAllInOneDrain(AccountVaultStore store, VaultActor actor, params WorldObject[] items)
        {
            var failures = new List<string>();

            store.Enqueue(() =>
            {
                foreach (var item in items)
                {
                    if (!store.TryDeposit(item, actor, out var reason))
                        failures.Add(reason ?? "<no reason>");
                }
            });

            return failures;
        }

        /// <summary>
        /// The audit write is one MySQL round trip per deposited item, on the world thread, and a
        /// 512-item mule sale paid 512 of them. AccountVaultAuditWriter queues the rows and writes them
        /// in batches on a background thread instead.
        ///
        /// Both halves are asserted because either alone passes with the other broken: batching that
        /// loses rows would satisfy the call-count assertion, and writing every row one at a time would
        /// satisfy the row-count assertion.
        ///
        /// The writer is started WITHOUT its drain thread and flushed by hand - see
        /// StartQueueingForTest - so there is no sleep and no race with a background thread. The finally
        /// is not optional: the queueing flag is process-static, and leaving it set makes every other
        /// vault test in the run stop seeing its audit rows synchronously.
        /// </summary>
        [TestMethod]
        public void Deposit_AuditWrites_AreBatched()
        {
            const int itemCount = 8;

            SeedVault(capacity: 255, itemCount: 0, order: 1);

            var store = NewStore();

            // Not pristine, so every deposit takes the vault-container path and writes one Deposit row.
            world.PristineResult = false;

            var items = Enumerable.Range(0, itemCount).Select(_ => FakeVaultWorld.MakeStack(8000, 1, 100)).ToArray();

            AccountVaultAuditWriter.StartQueueingForTest();

            try
            {
                var failures = DepositAllInOneDrain(store, Owner, items);
                CollectionAssert.AreEqual(new string[0], failures.ToArray(), "every deposit must have been accepted");

                Assert.AreEqual(0, backend.AddLogCalls,
                    "with the writer running, not one audit row may be written one-at-a-time from the deposit path");
                Assert.AreEqual(0, backend.Logs.Count, "nothing is written until the queue is flushed");

                var written = AccountVaultAuditWriter.Flush(int.MaxValue);

                Assert.AreEqual(itemCount, written, "the flush must report every queued row as written");
                Assert.AreEqual(itemCount, backend.AddLogRowsWritten, "every audit row must reach the backend");
                Assert.AreEqual(itemCount, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit));

                Assert.IsTrue(backend.AddLogBatchCalls > 0, "the rows must go through the BATCH write");
                Assert.IsTrue(backend.AddLogBatchCalls < itemCount,
                    $"{itemCount} audit rows cost {backend.AddLogBatchCalls} batch call(s); batching that needs one call per row is not batching");
                Assert.AreEqual(0, backend.AddLogCalls, "and none of them may also take the single-row path");
            }
            finally
            {
                AccountVaultAuditWriter.StopQueueingForTest();
            }
        }

        /// <summary>
        /// The audit trail is what makes a theft report answerable, but it may never be what makes a
        /// deposit fail: refusing a player's item because the log write failed turns a logging outage
        /// into a storage outage (ShardDatabase_AccountVault.cs, AddAccountVaultLog's own remark).
        ///
        /// Three failure shapes, because they are not the same failure. A polite false is what the DAO
        /// actually returns when MySQL refuses. A THROW is what escapes when it cannot catch, and that
        /// is the one the inline call could not survive - it unwound out of DepositToVault, past the
        /// item's save, into Drain's catch, and the caller read the deposit as refused. And the same
        /// throw from the BATCH write must not reach the flushing thread either.
        /// </summary>
        [TestMethod]
        public void Deposit_AuditWriteFailure_DoesNotFailTheDeposit()
        {
            var vault = SeedVault(capacity: 255, itemCount: 0, order: 1);

            var store = NewStore();

            world.PristineResult = false;

            backend.FailAddLog = true;

            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            Assert.IsTrue(Deposit(store, item, Owner, out var reason), $"a refused audit write must not refuse the deposit: {reason}");
            Assert.AreEqual(1, vault.Inventory.Count, "the item is in the vault");

            backend.FailAddLog = false;
            backend.ThrowFromAddLogCount = 1;

            var thrower = FakeVaultWorld.MakeStack(8000, 1, 100);
            Assert.IsTrue(Deposit(store, thrower, Owner, out reason), $"an audit write that THREW must not refuse the deposit: {reason}");
            Assert.AreEqual(0, backend.ThrowFromAddLogCount, "sanity: the throwing write really was reached");
            Assert.AreEqual(2, vault.Inventory.Count, "the item is in the vault");

            // And the same for the batched path, whose failure lands on the writer's thread rather than
            // on the depositing one.
            AccountVaultAuditWriter.StartQueueingForTest();

            try
            {
                var queued = FakeVaultWorld.MakeStack(8000, 1, 100);
                Assert.IsTrue(Deposit(store, queued, Owner, out reason), reason);

                backend.ThrowFromAddLogCount = 1;

                var written = AccountVaultAuditWriter.Flush(int.MaxValue);

                Assert.AreEqual(0, written, "the batch threw, so nothing was written");
                Assert.AreEqual(0, backend.ThrowFromAddLogCount, "sanity: the throwing batch really was reached");
                Assert.AreEqual(3, vault.Inventory.Count, "and the deposit it described still stands");
            }
            finally
            {
                AccountVaultAuditWriter.StopQueueingForTest();
            }
        }

        /// <summary>
        /// Container.TryAddToInventory's default placement position is 0, and at 0 it renumbers EVERY
        /// existing non-pack item in the container (Container.cs:566-570). Each of those writes is a
        /// persisted property setter that takes that item's BiotaDatabaseLock, so a deposit into a
        /// nearly-full vault paid up to 254 of them, and a 512-item sale paid that repeatedly.
        ///
        /// The deposit appends instead. Every sibling's position is snapshotted and asserted unchanged,
        /// rather than just the count - a renumber leaves the count identical.
        /// </summary>
        [TestMethod]
        public void Deposit_DoesNotRenumberSiblingPlacementPositions()
        {
            var vault = SeedVault(capacity: 255, itemCount: 6, order: 1);

            var before = vault.Inventory.Values.ToDictionary(i => i.Guid, i => i.PlacementPosition);

            Assert.AreEqual(6, before.Count, "precondition: six siblings to disturb");
            CollectionAssert.AreEquivalent(new int?[] { 0, 1, 2, 3, 4, 5 }, before.Values.ToArray(),
                "precondition: the seeded siblings occupy 0..5, so a renumber is visible");

            var store = NewStore();

            world.PristineResult = false;

            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            Assert.IsTrue(Deposit(store, item, Owner, out var reason), reason);

            Assert.AreEqual(7, vault.Inventory.Count, "sanity: the deposit really landed in this container");

            foreach (var kvp in before)
            {
                Assert.AreEqual(kvp.Value, vault.Inventory[kvp.Key].PlacementPosition,
                    $"0x{kvp.Key.Full:X8} was renumbered by a deposit that only had to append");
            }

            Assert.AreEqual(6, item.PlacementPosition ?? -1, "the deposited item takes the next free position");
        }

        /// <summary>
        /// One container save per DRAIN, not per item.
        ///
        /// These are same-id saves, and SerializedShardDatabase explicitly cannot batch those - a
        /// same-id duplicate inside a would-be batch is held over and run on its own - so one per item
        /// queued 512 separate container saves ahead of every other player's shard work.
        ///
        /// The items' own saves are asserted too, in the same test and deliberately: they are what
        /// records each item's new home, and a "coalescing" that folded those away would be item loss.
        /// </summary>
        [TestMethod]
        public void Deposit_ContainerSaves_AreCoalescedPerDrain()
        {
            const int itemCount = 5;

            var vault = SeedVault(capacity: 255, itemCount: 0, order: 1);

            var store = NewStore();

            world.PristineResult = false;

            var savesBefore = world.SaveBiotaCalls(vault);

            var items = Enumerable.Range(0, itemCount).Select(_ => FakeVaultWorld.MakeStack(8000, 1, 100)).ToArray();

            var failures = DepositAllInOneDrain(store, Owner, items);
            CollectionAssert.AreEqual(new string[0], failures.ToArray(), "every deposit must have been accepted");

            Assert.AreEqual(itemCount, vault.Inventory.Count, "sanity: all five really went into this container");

            Assert.AreEqual(1, world.SaveBiotaCalls(vault) - savesBefore,
                $"{itemCount} deposits in one drain must cost ONE container save, not one per item");

            foreach (var item in items)
                Assert.AreEqual(1, world.SaveBiotaCalls(item), "every deposited ITEM must still be saved on its own - that save is what records where it now lives");
        }

        #endregion

        /// <summary>
        /// Same walk-up idiom as PropertyRegistryTests.cs's FindInSourceTree (kept private to that file,
        /// so this is a local copy rather than a cross-test-file dependency).
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}

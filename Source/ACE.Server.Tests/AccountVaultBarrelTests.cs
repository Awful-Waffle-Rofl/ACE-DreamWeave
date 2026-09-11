using System;
using System.Collections.Generic;
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
    /// The Barrel (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6): a player-invoked soft
    /// delete for vault items, recoverable by an administrator for a bounded window.
    ///
    /// The barrel is one more container on an account that already has several, so almost every test
    /// here exists for the same reason: <c>vaults</c> is iterated in the capacity, entries,
    /// load-state and reap paths, and the barrel must be absent from every one of them. None of those
    /// exclusions fails to compile if it is missed, and each one is a separate visible bug - barreled
    /// items showing in the player's panel, a full barrel making the vault look full, a barrel that
    /// will not load taking the whole vault down with it, a reap destroying the container the next
    /// barreling needs.
    ///
    /// Driven through the same two seams as AccountVaultStoreTests (FakeVaultBackend and
    /// FakeVaultWorld in AccountVaultFakes.cs), so none of this needs a database.
    /// </summary>
    [TestClass]
    public class AccountVaultBarrelTests
    {
        private const uint OwnerAccount = 4101;
        private const uint OwnerCharacter = 0x50000401;

        private static readonly VaultActor Actor = new VaultActor(OwnerAccount, OwnerCharacter, "Barrelowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000),
                "account_vault_entry_cap is missing from DefaultLongProperties");

            // Seeded so PropertyManager's cache answers it without falling through to
            // DatabaseManager.ShardConfig, which opens a ShardDbContext. See AccountVaultStoreTests'
            // Setup for the full reasoning; the barrel's lazy container creation reads the same key.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            // Also asserts that the reaper's tunable was actually registered: ModifyLong returns false
            // for a key that is not in DefaultLongProperties.
            Assert.IsTrue(PropertyManager.ModifyLong("vault_barrel_retention_days", 30),
                "vault_barrel_retention_days is missing from DefaultLongProperties");

            AccountVaultStore.IsListedHook = null;
        }

        [TestCleanup]
        public void Teardown()
        {
            // Static, like PreWithdrawHook. A test that seeded a listing must not leave the next class
            // in the run believing everything is listed.
            AccountVaultStore.IsListedHook = null;
        }

        // ---- helpers ----

        private AccountVaultStore NewStore()
        {
            return new AccountVaultStore(OwnerAccount, backend, world);
        }

        private uint nextSeedOrder = 1;

        /// <summary>
        /// One loadable container plus its account_vault index row, at the given kind. 0 is an
        /// ordinary vault container, 1 is the barrel.
        /// </summary>
        private Container SeedVaultContainer(int kind, int capacity = AccountVaultStore.VaultItemCapacity)
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

        /// <summary>
        /// An index row whose container reports a READ FAILURE rather than an absence. Failure, not
        /// absence, is the case that matters here: an absent biota drops the one row, while an
        /// unreadable one is what a vault container refuses the whole store over and a barrel must not.
        /// </summary>
        private uint SeedUnloadableContainer(int kind)
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

            return guid;
        }

        /// <summary>
        /// A store with one empty vault container and a barrel already holding
        /// <paramref name="items"/> stored biotas. Seeded through the index rather than through
        /// TryBarrel, so the exclusion tests do not depend on the barreling path existing yet.
        /// </summary>
        private AccountVaultStore StoreWithBarrelHolding(out List<uint> barreledGuids, int items = 1)
        {
            SeedVaultContainer(kind: 0);

            var barrelContainer = SeedVaultContainer(kind: AccountVaultStore.BarrelContainerKind);

            barreledGuids = new List<uint>();

            for (var i = 0; i < items; i++)
            {
                var item = FakeVaultWorld.MakeStack(9001, 1, 100);

                Assert.IsTrue(barrelContainer.TryAddToInventory(item), "could not seed a barreled item");
                barreledGuids.Add(item.Guid.Full);
            }

            return NewStore();
        }

        private Container vaultContainer;

        /// <summary>The account's one ordinary vault container, created on first use.</summary>
        private Container EnsureVault(int capacity = AccountVaultStore.VaultItemCapacity)
        {
            return vaultContainer ??= SeedVaultContainer(kind: AccountVaultStore.VaultContainerKind, capacity: capacity);
        }

        /// <summary>
        /// One stored biota sitting in the account's vault container. Seeded straight into the
        /// container rather than through TryDeposit, so a barrel test that fails is failing about
        /// barreling.
        /// </summary>
        private WorldObject SeedStoredItem(uint wcid = 9001)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 100);

            Assert.IsTrue(EnsureVault().TryAddToInventory(item), "could not seed a stored item");

            return item;
        }

        private void SeedLedger(uint wcid, long count)
        {
            backend.Stacks.Add(new AccountVaultStack
            {
                Id = 7000 + nextSeedOrder++,
                AccountId = OwnerAccount,
                Wcid = wcid,
                Count = count,
            });
        }

        /// <summary>
        /// Makes the market's listing hook answer "listed" for this item. The hook is the seam
        /// MarketManager sets on startup; a test sets it directly and clears it in Cleanup.
        /// </summary>
        private static void SeedActiveListingFor(uint accountId, uint? itemGuid, uint wcid = 0)
        {
            AccountVaultStore.IsListedHook = (account, guid, listedWcid)
                => account == accountId && (itemGuid != null ? guid == itemGuid : guid == null && listedWcid == wcid);
        }

        /// <summary>A store whose index read FAILS, so it is never ready.</summary>
        private AccountVaultStore UnloadedStore()
        {
            backend.FailVaultRead = true;

            return NewStore();
        }

        private static VaultEntry AnyEntry() => VaultEntry.ForLedger(9001, 1);

        /// <summary>
        /// TryBarrel asserts it is on the store's mutation queue, so every call goes through Enqueue
        /// exactly as the production callers do. Same shape as AccountVaultStoreTests' Deposit and
        /// Withdraw helpers.
        /// </summary>
        private static bool Barrel(AccountVaultStore store, VaultEntry entry, int amount, VaultActor actor, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryBarrel(entry, amount, actor, out reason));

            failReason = reason;
            return ok;
        }

        /// <summary>TryRestoreFromBarrel is a mutation like any other and asserts the queue too.</summary>
        private static bool Restore(AccountVaultStore store, uint rowId, VaultActor actor, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryRestoreFromBarrel(rowId, actor, out reason));

            failReason = reason;
            return ok;
        }

        /// <summary>A row whose item retention has already destroyed. Terminal, and never restorable.</summary>
        private AccountVaultBarrel SeedPurgedBarrelRow()
        {
            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = 9001,
                ItemGuid = FakeVaultWorld.NextGuid(),
                Count = 1,
                ItemName = "Something Long Gone",
                BarreledAt = DateTime.UtcNow.AddDays(-60),
                ActorCharacterGuid = OwnerCharacter,
                ActorCharacterName = "Barrelowner",
                PurgedAt = DateTime.UtcNow.AddDays(-30),
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            return row;
        }

        /// <summary>
        /// Moves a row's barreled_At back, which is how a test reaches past the retention window
        /// without waiting thirty days or reaching into the reaper's own clock.
        /// </summary>
        private void BackdateBarrelRow(uint rowId, DateTime when)
        {
            var row = backend.Barrels.Single(b => b.Id == rowId);

            row.BarreledAt = when;
        }

        /// <summary>
        /// A reaper pointed at this test's store. The purge runs THROUGH the store - see the ctor's
        /// remarks for why - so the destruction lands in this same FakeVaultWorld's Destroyed list.
        /// </summary>
        private AccountVaultBarrelReaper ReaperFor(AccountVaultStore store)
        {
            return new AccountVaultBarrelReaper(backend, _ => store);
        }

        [TestMethod]
        public void Backend_RoundTripsABarrelRow()
        {
            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = 9001,
                ItemGuid = 0x50000001,
                Count = 1,
                ItemName = "Brass Chainmail Gauntlets",
                BarreledAt = DateTime.UtcNow,
                ActorCharacterGuid = 0x51000001,
                ActorCharacterName = "Tester",
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            var read = backend.GetAccountVaultBarrels(OwnerAccount, 10);

            Assert.IsNotNull(read, "null means the read failed and is never an empty list");
            Assert.AreEqual(1, read.Count);
            Assert.AreEqual("Brass Chainmail Gauntlets", read[0].ItemName);
            Assert.IsNull(read[0].RestoredAt);
            Assert.IsNull(read[0].PurgedAt);
        }

        [TestMethod]
        public void Backend_ExpiredQuery_ExcludesRestoredAndPurgedRows()
        {
            var old = DateTime.UtcNow.AddDays(-40);

            backend.AddAccountVaultBarrel(new AccountVaultBarrel { AccountId = OwnerAccount, Wcid = 1, Count = 1, ItemName = "open",     BarreledAt = old });
            backend.AddAccountVaultBarrel(new AccountVaultBarrel { AccountId = OwnerAccount, Wcid = 2, Count = 1, ItemName = "restored", BarreledAt = old, RestoredAt = DateTime.UtcNow });
            backend.AddAccountVaultBarrel(new AccountVaultBarrel { AccountId = OwnerAccount, Wcid = 3, Count = 1, ItemName = "purged",   BarreledAt = old, PurgedAt   = DateTime.UtcNow });
            backend.AddAccountVaultBarrel(new AccountVaultBarrel { AccountId = OwnerAccount, Wcid = 4, Count = 1, ItemName = "recent",   BarreledAt = DateTime.UtcNow });

            var expired = backend.GetExpiredAccountVaultBarrels(DateTime.UtcNow.AddDays(-30), 100);

            Assert.IsNotNull(expired);
            Assert.AreEqual(1, expired.Count, "only a row that still holds an item and is past the cutoff expires");
            Assert.AreEqual("open", expired[0].ItemName);
        }

        [TestMethod]
        public void Backend_AFailedBarrelRead_IsNullAndNotAnEmptyList()
        {
            backend.AddAccountVaultBarrel(new AccountVaultBarrel { AccountId = OwnerAccount, Wcid = 1, Count = 1, ItemName = "open" });

            backend.FailBarrelRead = true;

            Assert.IsNull(backend.GetAccountVaultBarrels(OwnerAccount, 10),
                "a failed list read is null; an empty list means the account has never barreled anything");
            Assert.IsNull(backend.GetExpiredAccountVaultBarrels(DateTime.UtcNow, 10));
            Assert.IsNull(backend.GetAccountVaultBarrel(1));
        }

        // ---- the exclusions (DESIGN 6.2). Each one is a separate visible bug and none breaks the build ----

        [TestMethod]
        public void Load_PutsAKindOneContainerInTheBarrelAndNotInTheVaults()
        {
            SeedVaultContainer(kind: 0);
            SeedVaultContainer(kind: AccountVaultStore.BarrelContainerKind);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);
            Assert.AreEqual(1, store.VaultContainerCountForTest, "the barrel must not be counted as a vault");
            Assert.IsTrue(store.HasBarrelForTest, "the kind 1 row must have loaded as the barrel");
        }

        [TestMethod]
        public void Load_ABarrelThatWillNotLoad_DoesNotMakeTheVaultUnusable()
        {
            SeedVaultContainer(kind: 0);
            SeedUnloadableContainer(kind: AccountVaultStore.BarrelContainerKind);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the player's vault must still work when only the barrel is unreadable");
            Assert.IsFalse(store.HasBarrelForTest);
            Assert.AreEqual(1, store.VaultContainerCountForTest);
        }

        [TestMethod]
        public void Load_AVaultThatWillNotLoad_STILLRefusesTheWholeStore()
        {
            // The control for the test above. The barrel's continue-on-failure arm is an exception to
            // this loop's fail-closed rule, and without this assertion a change that made the WHOLE
            // loop lenient would leave both tests green while silently hiding a player's items.
            SeedVaultContainer(kind: 0);
            SeedUnloadableContainer(kind: 0);

            var store = NewStore();

            Assert.IsFalse(store.IsLoaded, "an unreadable VAULT container must still refuse the whole store");
        }

        [TestMethod]
        public void BarrelContents_DoNotAppearInGetEntries()
        {
            var store = StoreWithBarrelHolding(out var barreled);

            var entries = store.GetEntries(0, -1);

            Assert.IsFalse(entries.Any(e => !e.IsLedger && e.Guid.Full == barreled[0]),
                           "a barreled item is gone from the player's view");
            Assert.AreEqual(0, entries.Count, "the vault container is empty, so the panel has nothing to draw");
        }

        [TestMethod]
        public void BarrelCapacity_DoesNotCountAgainstTheVault()
        {
            // A cap of 1 with five items in the barrel: if the barrel's contents were vault entries
            // the count would be five and HasRoomFor(1) would be false.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = StoreWithBarrelHolding(out _, items: 5);

            Assert.AreEqual(0, store.EntryCount, "the barrel's contents are not vault entries");
            Assert.IsTrue(store.HasRoomFor(1), "a full barrel must never make the vault look full");
        }

        [TestMethod]
        public void Reap_NeverTakesTheBarrel()
        {
            // The reap exists to clean up an empty VAULT container. A barrel that happens to be empty
            // right now is still the account's barrel - the retention sweep empties it routinely - and
            // reaping it would destroy the container the next barreling needs plus the index row that
            // makes it findable.
            var barrelContainer = SeedVaultContainer(kind: AccountVaultStore.BarrelContainerKind);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);
            Assert.IsTrue(store.HasBarrelForTest);

            // TryReapEmptyVault takes the container to reap, so the barrel is out of reach of any
            // caller walking the vault list. Handing it the barrel EXPLICITLY is the stronger test:
            // it must refuse even when named directly.
            Assert.IsFalse(store.TryReapEmptyVault(barrelContainer),
                           "the barrel is not a vault and must never be reaped, even when passed by hand");

            Assert.IsTrue(store.HasBarrelForTest, "an empty barrel is not an empty vault");
            Assert.AreEqual(1, backend.Vaults.Count(v => v.AccountId == OwnerAccount && v.Kind == AccountVaultStore.BarrelContainerKind),
                            "its index row must survive too");
            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == barrelContainer.Guid.Full),
                           "and its biota must survive");
        }

        // ---- TryBarrel ----

        [TestMethod]
        public void TryBarrel_AStoredItem_MovesItOutOfTheVaultAndRecordsARow()
        {
            var item = SeedStoredItem();
            var store = NewStore();

            var entry = store.GetEntries(0, -1).Single();

            Assert.IsTrue(Barrel(store, entry, 1, Actor, out var why), why);

            Assert.AreEqual(0, store.GetEntries(0, -1).Count, "the item is gone from the vault");

            var rows = backend.GetAccountVaultBarrels(OwnerAccount, 10);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(item.Guid.Full, rows[0].ItemGuid);
            Assert.AreEqual(item.Name, rows[0].ItemName, "the name is snapshotted so the row survives the item");
            Assert.IsNull(rows[0].RestoredAt);
            Assert.IsNull(rows[0].PurgedAt);

            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Barrel));

            // The item is NOT destroyed: a barreling is a soft delete, and the biota has to survive
            // for an administrator to be able to hand it back.
            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full),
                           "barreling must not destroy the biota - that is what retention is for");
            Assert.IsTrue(store.HasBarrelForTest, "the barrel container was created lazily on the first barreling");
        }

        [TestMethod]
        public void TryBarrel_ALedgerStack_DecrementsAndRecordsWcidAndCountWithNoItemGuid()
        {
            SeedLedger(9001, 50);

            var store = NewStore();

            var entry = store.GetEntries(0, -1).Single();

            Assert.IsTrue(Barrel(store, entry, 20, Actor, out var why), why);

            var remaining = store.GetEntries(0, -1).Single();
            Assert.AreEqual(30, remaining.Count, "only the barreled units leave the ledger");

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            Assert.IsNull(row.ItemGuid, "a ledger barreling has no biota and therefore no guid");
            Assert.AreEqual(9001u, row.Wcid);
            Assert.AreEqual(20, row.Count);

            Assert.IsFalse(store.HasBarrelForTest, "a ledger barreling never touches a container, so none is created");
        }

        [TestMethod]
        public void TryBarrel_AnItemWithAnActiveListing_IsRefused()
        {
            var item = SeedStoredItem();
            var store = NewStore();

            SeedActiveListingFor(OwnerAccount, item.Guid.Full);

            var entry = store.GetEntries(0, -1).Single();

            Assert.IsFalse(Barrel(store, entry, 1, Actor, out var why));
            StringAssert.Contains(why, "listed");
            Assert.AreEqual(1, store.GetEntries(0, -1).Count, "the item stays put");
            Assert.AreEqual(0, backend.GetAccountVaultBarrels(OwnerAccount, 10).Count, "a refusal records nothing");
        }

        [TestMethod]
        public void TryBarrel_WhenTheStoreIsNotLoaded_IsRefused()
        {
            var store = UnloadedStore();

            Assert.IsFalse(Barrel(store, AnyEntry(), 1, Actor, out var why));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, why);
        }

        [TestMethod]
        public void TryBarrel_MoreThanIsHeld_IsRefused()
        {
            SeedLedger(9001, 5);

            var store = NewStore();

            var entry = store.GetEntries(0, -1).Single();

            Assert.IsFalse(Barrel(store, entry, 6, Actor, out _));
            Assert.AreEqual(5, store.GetEntries(0, -1).Single().Count);
            Assert.AreEqual(0, backend.GetAccountVaultBarrels(OwnerAccount, 10).Count);
        }

        [TestMethod]
        public void TryBarrel_ALedgerDebitTheDatabaseREFUSED_RecordsNothing()
        {
            // Refused is the ONE adjust outcome that proves the ledger did not move, and it is the
            // only one a barreling may treat as "nothing happened". A row written here would be a
            // dupe waiting for the first restore.
            SeedLedger(9001, 50);
            backend.FailLedgerAdjustWcids.Add(9001);

            var store = NewStore();

            Assert.IsFalse(Barrel(store, store.GetEntries(0, -1).Single(), 20, Actor, out var why));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, why);

            Assert.AreEqual(0, backend.GetAccountVaultBarrels(OwnerAccount, 10).Count,
                            "a provably-refused debit must leave no barrel row behind");
            Assert.AreEqual(50, store.GetEntries(0, -1).Single().Count);
        }

        [TestMethod]
        public void TryBarrel_WhenTheAuditRowCannotBeWritten_TheStoredItemDoesNotMove()
        {
            // The stored-item path writes the row BEFORE the move precisely so this case is clean:
            // an item in the barrel that no row names is invisible to /vaultrestore.
            SeedStoredItem();
            backend.FailAddBarrel = true;

            var store = NewStore();

            Assert.IsFalse(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelUnavailableMessage, why);

            Assert.AreEqual(1, store.GetEntries(0, -1).Count, "the item is still in the player's vault");
        }

        // ---- TryRestoreFromBarrel ----

        [TestMethod]
        public void TryRestore_AStoredItem_ReturnsItToTheVaultIntact()
        {
            var item = SeedStoredItem();
            var originalName = item.Name;

            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out var barrelWhy), barrelWhy);

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();

            Assert.IsTrue(Restore(store, row.Id, Actor, out var why), why);

            var restored = store.GetEntries(0, -1).Single();
            Assert.AreEqual(item.Guid.Full, restored.Guid.Full, "the SAME item comes back, not a copy");
            Assert.AreEqual(originalName, restored.WorldObject.Name);
            Assert.AreSame(item, restored.WorldObject, "and the same live object, with its properties untouched");

            Assert.IsNotNull(backend.GetAccountVaultBarrel(row.Id).RestoredAt);
            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Restore));
        }

        [TestMethod]
        public void TryRestore_ALedgerBarreling_ReAddsTheUnits()
        {
            SeedLedger(9001, 50);

            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 20, Actor, out var barrelWhy), barrelWhy);

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();

            Assert.IsTrue(Restore(store, row.Id, Actor, out var why), why);
            Assert.AreEqual(50, store.GetEntries(0, -1).Single().Count);
        }

        [TestMethod]
        public void TryRestore_ARowAlreadyRestored_IsRefused()
        {
            SeedStoredItem();

            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out _));

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            Assert.IsTrue(Restore(store, row.Id, Actor, out _));

            Assert.IsFalse(Restore(store, row.Id, Actor, out var why), "a restore is not repeatable");
            StringAssert.Contains(why, "already");
            Assert.AreEqual(1, store.GetEntries(0, -1).Count, "the item must not be duplicated");
        }

        [TestMethod]
        public void TryRestore_APurgedRow_IsRefused()
        {
            var row = SeedPurgedBarrelRow();

            var store = NewStore();

            Assert.IsFalse(Restore(store, row.Id, Actor, out var why));
            StringAssert.Contains(why, "destroyed");
        }

        [TestMethod]
        public void TryRestore_IntoAFullVault_IsRefusedAndLeavesTheRowOpen()
        {
            // A cap of two: one entry already in the vault, one barreled. Restoring the barreled one
            // would make two, which fits; a cap of one is what makes the restore refuse.
            SeedStoredItem();
            SeedStoredItem();

            var store = NewStore();

            var entries = store.GetEntries(0, -1);
            Assert.AreEqual(2, entries.Count, "the two seeded items must not have grouped into one row");

            Assert.IsTrue(Barrel(store, entries[0], 1, Actor, out var barrelWhy), barrelWhy);

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            Assert.IsFalse(Restore(store, row.Id, Actor, out var why));
            StringAssert.Contains(why, "room");

            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).RestoredAt,
                          "a refused restore must leave the row restorable");
            Assert.AreEqual(1, store.GetEntries(0, -1).Count, "and must not have moved anything");
        }

        [TestMethod]
        public void TryRestore_ARowBelongingToAnotherAccount_IsRefused()
        {
            // The admin command resolves the store from an account name and the row id separately, so
            // nothing upstream guarantees the two agree. Restoring here would hand one player another
            // player's item.
            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount + 1,
                Wcid = 9001,
                Count = 1,
                ItemName = "Somebody Else's",
                BarreledAt = DateTime.UtcNow,
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            var store = NewStore();

            Assert.IsFalse(Restore(store, row.Id, Actor, out var why));
            StringAssert.Contains(why, "belongs to account");
        }

        [TestMethod]
        public void TryRestore_ARowTheBarrelDoesNotHold_IsRefusedAndNothingIsCreated()
        {
            // The shape a barreling leaves behind when its row landed and its move did not. It is
            // deliberately inert: nothing is restored, nothing is created, and the row stays open.
            SeedVaultContainer(kind: AccountVaultStore.BarrelContainerKind);

            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = 9001,
                ItemGuid = FakeVaultWorld.NextGuid(),
                Count = 1,
                ItemName = "Never Moved",
                BarreledAt = DateTime.UtcNow,
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            var store = NewStore();

            Assert.IsFalse(Restore(store, row.Id, Actor, out var why));
            StringAssert.Contains(why, "does not hold");

            Assert.AreEqual(0, store.GetEntries(0, -1).Count, "nothing may be conjured from a row alone");
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).RestoredAt);
        }

        // ---- retention ----

        [TestMethod]
        public void Reaper_DestroysAnExpiredItemAndStampsPurgedAtButKeepsTheRow()
        {
            var item = SeedStoredItem();
            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out var why), why);

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            BackdateBarrelRow(row.Id, DateTime.UtcNow.AddDays(-31));

            var purged = ReaperFor(store).RunOnce(DateTime.UtcNow);

            Assert.AreEqual(1, purged);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full), "the item is really destroyed");

            var after = backend.GetAccountVaultBarrel(row.Id);
            Assert.IsNotNull(after, "the audit row outlives the item");
            Assert.IsNotNull(after.PurgedAt);
            Assert.AreEqual(item.Name, after.ItemName, "and still names what was thrown away");

            Assert.IsTrue(store.HasBarrelForTest, "the barrel container itself is not reaped by retention");
        }

        [TestMethod]
        public void Reaper_LeavesRowsInsideTheRetentionWindowAlone()
        {
            SeedStoredItem();
            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out var why), why);

            var purged = ReaperFor(store).RunOnce(DateTime.UtcNow);

            Assert.AreEqual(0, purged);
            Assert.IsNull(backend.GetAccountVaultBarrels(OwnerAccount, 10).Single().PurgedAt);
            Assert.AreEqual(0, world.Destroyed.Count, "nothing inside the window may be destroyed");
        }

        [TestMethod]
        public void Reaper_ARestoredRowIsNeverPurged()
        {
            var item = SeedStoredItem();
            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out _));

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            Assert.IsTrue(Restore(store, row.Id, Actor, out var why), why);

            BackdateBarrelRow(row.Id, DateTime.UtcNow.AddDays(-31));

            Assert.AreEqual(0, ReaperFor(store).RunOnce(DateTime.UtcNow));
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt,
                          "an item the player got back must never be destroyed by retention");
            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
        }

        [TestMethod]
        public void Reaper_ALedgerRow_IsStampedWithNothingDestroyed()
        {
            // A ledger barreling destroyed its units at barrel time and never had a biota, so the
            // purge is the stamp and nothing else. Without this the sweep would leave every ledger row
            // open forever, retrying it every hour.
            SeedLedger(9001, 50);

            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 20, Actor, out var why), why);

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            BackdateBarrelRow(row.Id, DateTime.UtcNow.AddDays(-31));

            Assert.AreEqual(1, ReaperFor(store).RunOnce(DateTime.UtcNow));
            Assert.IsNotNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
        }

        [TestMethod]
        public void Reaper_ARowTheBarrelDoesNotHold_IsLeftOpenRatherThanStamped()
        {
            // purged_At is an audit statement that retention DESTROYED something. A row whose item is
            // not in the barrel names an item that may still be sitting in the player's vault, so
            // stamping it would put a false statement in the audit trail.
            SeedVaultContainer(kind: AccountVaultStore.BarrelContainerKind);

            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = 9001,
                ItemGuid = FakeVaultWorld.NextGuid(),
                Count = 1,
                ItemName = "Never Moved",
                BarreledAt = DateTime.UtcNow.AddDays(-31),
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            var store = NewStore();

            Assert.AreEqual(0, ReaperFor(store).RunOnce(DateTime.UtcNow));
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
            Assert.AreEqual(0, world.Destroyed.Count);
        }

        [TestMethod]
        public void Reaper_AFailedRead_PurgesNothing()
        {
            // NULL from the expired query is a failed read, never an empty table. Purging nothing is
            // the right answer; the next pass tries again.
            SeedStoredItem();
            var store = NewStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, Actor, out _));

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            BackdateBarrelRow(row.Id, DateTime.UtcNow.AddDays(-31));

            backend.FailBarrelRead = true;

            Assert.AreEqual(0, ReaperFor(store).RunOnce(DateTime.UtcNow));
            Assert.AreEqual(0, world.Destroyed.Count);
        }
    }
}

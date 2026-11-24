using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A full barrel evicts its oldest items instead of refusing (owner decision "Evict oldest when
    /// full"; Discord bug thread 1569220583785234502). The undo window becomes vault_barrel_retention_days,
    /// or less once a full barrel needs the slot.
    ///
    /// The barrel container here is seeded with a SMALL capacity so a test can fill it with a handful
    /// of items; the store reads the container's own ItemCapacity, so the rule under test is the same one
    /// that runs at 255. Driven through FakeVaultBackend and FakeVaultWorld, no database. The fake's row
    /// list reads return DETACHED copies here, as the real NoTracking DAO does, so a stamp written
    /// through a planned row cannot silently change the fake's own row.
    /// </summary>
    [TestClass]
    public class AccountVaultBarrelEvictionTests
    {
        private const uint OwnerAccount = 4181;
        private const uint OwnerCharacter = 0x50000481;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Evictowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        private uint nextSeedOrder = 1;
        private uint nextWcid = 9101;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend { BarrelListsReturnCopies = true, ExpiredReadReturnsCopies = true };
            world = new FakeVaultWorld();

            // Every PropertyManager key these paths read is seeded, and every one is put back in Teardown.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));
            Assert.IsTrue(PropertyManager.ModifyLong(AccountVaultBarrelReaper.RetentionKey, 30));

            VaultClassTestConfig.Seed();

            AccountVaultStore.IsListedHook = null;
        }

        [TestCleanup]
        public void Teardown()
        {
            try
            {
                AccountVaultStore.IsListedHook = null;
            }
            finally
            {
                foreach (var key in new[] { "account_vault_entry_cap", "account_vault_landblock", AccountVaultBarrelReaper.RetentionKey })
                    PropertyManager.ModifyLong(key, DefaultPropertyManager.DefaultLongProperties[key].Item);

                foreach (var key in new[] { "account_vault_class_storage", "account_vault_salvage_combine" })
                    PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);
            }
        }

        // ---- helpers ----

        private Container SeedContainer(int kind, int capacity = AccountVaultStore.VaultItemCapacity)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9700 + nextSeedOrder,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextSeedOrder),
                Kind = kind,
            });

            nextSeedOrder++;

            return container;
        }

        /// <summary>An item already in the barrel, plus the open row naming it, barreled <paramref name="daysAgo"/> days ago.</summary>
        private (WorldObject Item, AccountVaultBarrel Row) SeedBarreled(Container barrelContainer, double daysAgo, bool withRow = true)
        {
            var item = FakeVaultWorld.MakeStack(nextWcid++, 1, 100);

            Assert.IsTrue(barrelContainer.TryAddToInventory(item), "could not seed a barreled item");

            if (!withRow)
                return (item, null);

            return (item, AddItemRow(item.Guid.Full, item.WeenieClassId, daysAgo));
        }

        private AccountVaultBarrel AddItemRow(uint guid, uint wcid, double daysAgo)
        {
            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = wcid,
                ItemGuid = guid,
                Count = 1,
                ItemName = "Seeded",
                BarreledAt = DateTime.UtcNow.AddDays(-daysAgo),
                ActorCharacterGuid = OwnerCharacter,
                ActorCharacterName = "Evictowner",
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            return row;
        }

        private WorldObject SeedStored(Container vault)
        {
            var item = FakeVaultWorld.MakeStack(nextWcid++, 1, 100);

            Assert.IsTrue(vault.TryAddToInventory(item));

            return item;
        }

        private static List<WorldObject> SeedGroup(Container vault, int members)
        {
            var bags = new List<WorldObject>();

            for (var i = 0; i < members; i++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(20980, 100, 77, 12, 640);
                Assert.IsTrue(vault.TryAddToInventory(bag));
                bags.Add(bag);
            }

            return bags;
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        private static VaultEntry EntryFor(AccountVaultStore store, WorldObject item)
        {
            return store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem && (e.WorldObject == item || (e.IsGroup && e.Members.Contains(item))));
        }

        private static bool Barrel(AccountVaultStore store, VaultEntry entry, int amount, out string failReason)
        {
            var ok = false;
            string reason = null;

            Assert.IsTrue(store.Enqueue(() => ok = store.TryBarrel(entry, amount, Owner, out reason), out var thrown));
            Assert.IsNull(thrown, thrown?.ToString());

            failReason = reason;
            return ok;
        }

        private static bool Restore(AccountVaultStore store, uint rowId, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryRestoreFromBarrel(rowId, Owner, out reason));

            failReason = reason;
            return ok;
        }

        private bool IsDestroyed(WorldObject item) => world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full);

        private AccountVaultBarrel RowFor(WorldObject item) =>
            backend.Barrels.Where(b => b.ItemGuid == item.Guid.Full).OrderByDescending(b => b.Id).FirstOrDefault();

        private AccountVaultBarrel Fresh(AccountVaultBarrel row) => backend.Barrels.Single(b => b.Id == row.Id);

        private bool AnyRowStamped(IEnumerable<AccountVaultBarrel> rows) => rows.Any(r => Fresh(r).PurgedAt != null || Fresh(r).RestoredAt != null);

        // ---- the rule ----

        [TestMethod]
        public void Barreling_ThatFits_EvictsNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (old, oldRow) = SeedBarreled(barrelContainer, 50);
            var item = SeedStored(vault);

            var store = NewStore();

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.AreEqual(0, world.Destroyed.Count, "nothing is evicted when it fits");
            Assert.AreEqual(1, backend.OpenBarrelItemReads, "one open-row read, for the stray check; no eviction plan");
            Assert.IsNull(Fresh(oldRow).PurgedAt);
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(old.Guid));
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(item.Guid));
        }

        [TestMethod]
        public void Barreling_OverByN_EvictsExactlyTheNOldest_AndKeepsTheNewest()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            // Seeded out of age order, so insertion order cannot pass for "oldest".
            var (middle, middleRow) = SeedBarreled(barrelContainer, 40);
            var (newest, newestRow) = SeedBarreled(barrelContainer, 30);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);

            var bags = SeedGroup(vault, 2);

            var store = NewStore();
            var entry = EntryFor(store, bags[0]);

            Assert.IsTrue(entry.IsGroup, "precondition: a group of two");

            Assert.IsTrue(Barrel(store, entry, 2, out var why), why);

            Assert.IsTrue(IsDestroyed(oldest) && IsDestroyed(middle), "the two oldest are evicted");
            Assert.IsFalse(IsDestroyed(newest), "the newest is kept");
            Assert.AreEqual(2, world.Destroyed.Count, "exactly as many as needed, no more");

            Assert.IsNotNull(Fresh(oldestRow).PurgedAt, "an evicted row is kept and stamped purged");
            Assert.IsNotNull(Fresh(middleRow).PurgedAt);
            Assert.IsNull(Fresh(newestRow).PurgedAt);

            Assert.IsTrue(bags.All(b => barrelContainer.Inventory.ContainsKey(b.Guid)), "and the barreling went through");
            Assert.AreEqual(3, barrelContainer.Inventory.Count);
            Assert.IsTrue(bags.All(b => RowFor(b)?.PurgedAt == null && RowFor(b)?.RestoredAt == null), "with open rows of its own");
        }

        [TestMethod]
        public void Eviction_SkipsRestoredAndLedgerRows_AndTakesTheOldestOpenItemRow()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            // A ledger row, oldest of all: no item in the container, so it neither counts nor is evicted.
            var ledgerRow = new AccountVaultBarrel { AccountId = OwnerAccount, Wcid = 9001, ItemGuid = null, Count = 5, ItemName = "Ledger", BarreledAt = DateTime.UtcNow.AddDays(-90) };
            Assert.IsTrue(backend.AddAccountVaultBarrel(ledgerRow));

            var (restoredItem, restoredRow) = SeedBarreled(barrelContainer, 60);
            var (b, bRow) = SeedBarreled(barrelContainer, 50);
            var (c, _) = SeedBarreled(barrelContainer, 40);

            var store = NewStore();

            // Restored by an admin: its item goes back to the vault and the row is terminal.
            Assert.IsTrue(Restore(store, restoredRow.Id, out var r), r);

            var d = SeedBarreled(barrelContainer, 30).Item;      // the barrel is full again: b, c, d

            var item = SeedStored(vault);

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.IsTrue(IsDestroyed(b), "the oldest OPEN item row is the one evicted");
            Assert.IsNotNull(Fresh(bRow).PurgedAt);

            Assert.IsFalse(IsDestroyed(restoredItem), "a restored row's item is never evicted");
            Assert.IsNotNull(Fresh(restoredRow).RestoredAt);
            Assert.IsNull(Fresh(restoredRow).PurgedAt);

            Assert.IsNull(Fresh(ledgerRow).PurgedAt, "a ledger row is never evicted");
            Assert.IsFalse(IsDestroyed(c) || IsDestroyed(d));
            Assert.AreEqual(1, world.Destroyed.Count);
        }

        [TestMethod]
        public void Eviction_WhoseOldestItemCannotBeProven_DestroysNothing_AndRefusesAsFull()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            // An item in the barrel that no open row names: it could be older than every candidate.
            var (orphan, _) = SeedBarreled(barrelContainer, 0, withRow: false);
            var (b, bRow) = SeedBarreled(barrelContainer, 50);
            var (c, cRow) = SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var rowsBefore = backend.Barrels.Count;

            var store = NewStore();

            Assert.IsFalse(Barrel(store, EntryFor(store, item), 1, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why, "refused exactly as before");

            Assert.AreEqual(0, world.Destroyed.Count, "nothing destroyed");
            Assert.IsNull(Fresh(bRow).PurgedAt);
            Assert.IsNull(Fresh(cRow).PurgedAt);
            Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid), "the item being barreled stays in the vault");
            Assert.AreEqual(rowsBefore, backend.Barrels.Count, "and no row was written for it");
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(orphan.Guid));
        }

        [TestMethod]
        public void Eviction_TwoOpenRowsNamingOneItem_RefusesWithAWarn_AndDestroysNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);

            var (dup, dupRow) = SeedBarreled(barrelContainer, 50);
            var second = AddItemRow(dup.Guid.Full, dup.WeenieClassId, 45);
            var (other, otherRow) = SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var rowsBefore = backend.Barrels.Count;
            var store = NewStore();

            using (var capture = new MarketLogCapture())
            {
                Assert.IsFalse(Barrel(store, EntryFor(store, item), 1, out var why));
                Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why);

                Assert.AreEqual(1, capture.Events.Count(e => e.Level == log4net.Core.Level.Warn
                    && e.RenderedMessage.Contains($"0x{dup.Guid.Full:X8} is named by 2 open rows")
                    && e.RenderedMessage.Contains($"{dupRow.Id}") && e.RenderedMessage.Contains($"{second.Id}")), "one WARN names the item and both rows");
            }

            Assert.AreEqual(0, world.Destroyed.Count, "nothing destroyed");
            Assert.IsFalse(AnyRowStamped(new[] { dupRow, second, otherRow }), "and nothing stamped");
            Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid));
            Assert.AreEqual(rowsBefore, backend.Barrels.Count, "no row was written for the refused barreling");
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(other.Guid));
        }

        [TestMethod]
        public void Eviction_SkipsAStrayOpenRow_AndNeverDestroysMoreThanNeeded()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            // A stray: the OLDEST open row of all, but its item is not in the barrel. It frees nothing.
            var stray = AddItemRow(FakeVaultWorld.NextGuid(), 9001, 90);

            var (a, aRow) = SeedBarreled(barrelContainer, 50);
            var (b, _) = SeedBarreled(barrelContainer, 40);
            var (c, _) = SeedBarreled(barrelContainer, 30);

            var item = SeedStored(vault);
            var store = NewStore();

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.AreEqual(1, world.Destroyed.Count, "exactly the one slot needed");
            Assert.IsTrue(IsDestroyed(a), "the oldest item that IS in the barrel");
            Assert.IsNotNull(Fresh(aRow).PurgedAt);
            Assert.IsFalse(IsDestroyed(b) || IsDestroyed(c));
            Assert.IsNull(Fresh(stray).PurgedAt, "the stray row is skipped, not stamped");
            Assert.IsNull(Fresh(stray).RestoredAt);
        }

        [TestMethod]
        public void Barreling_BiggerThanTheWholeBarrel_IsRefused_AndDestroysNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 1);
            var (old, oldRow) = SeedBarreled(barrelContainer, 50);

            var bags = SeedGroup(vault, 2);

            var store = NewStore();
            var entry = EntryFor(store, bags[0]);

            Assert.IsTrue(entry.IsGroup);
            Assert.IsFalse(Barrel(store, entry, 2, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why);

            Assert.AreEqual(0, world.Destroyed.Count);
            Assert.IsNull(Fresh(oldRow).PurgedAt);
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(old.Guid));
            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)));
        }

        // ---- Q1: nothing is destroyed for a barreling that is then refused ----

        [TestMethod]
        public void OverCapacity_WhenTheRowInsertFails_DestroysNothing_AndStampsNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            var (next, nextRow) = SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var store = NewStore();
            var entry = EntryFor(store, item);

            backend.FailAddBarrel = true;

            Assert.IsFalse(Barrel(store, entry, 1, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelUnavailableMessage, why, "refused on the row insert");

            Assert.AreEqual(0, world.Destroyed.Count, "nothing evicted for a barreling that was refused");
            Assert.IsFalse(AnyRowStamped(new[] { oldestRow, nextRow }), "and nothing stamped");
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(oldest.Guid) && barrelContainer.Inventory.ContainsKey(next.Guid));
            Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid));
        }

        [TestMethod]
        public void OverCapacity_WhenTheTargetIsInNoVault_DestroysNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (_, oldestRow) = SeedBarreled(barrelContainer, 50);
            var (_, nextRow) = SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var store = NewStore();
            var entry = EntryFor(store, item);

            // Another window took it between the panel read and the barreling.
            Assert.IsTrue(vault.TryRemoveFromInventory(item.Guid, out _));

            var rowsBefore = backend.Barrels.Count;

            Assert.IsFalse(Barrel(store, entry, 1, out var why));
            Assert.AreEqual("That is no longer in your vault.", why, "refused on the target lookup");

            Assert.AreEqual(0, world.Destroyed.Count, "nothing evicted for a barreling that was refused");
            Assert.IsFalse(AnyRowStamped(new[] { oldestRow, nextRow }));
            Assert.AreEqual(rowsBefore, backend.Barrels.Count, "and no row was written");
        }

        // ---- Q2: the plan is validated in full before anything is destroyed ----

        [TestMethod]
        public void Eviction_SecondPlannedRowFailsValidation_DestroysNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            var (middle, middleRow) = SeedBarreled(barrelContainer, 40);
            SeedBarreled(barrelContainer, 30);

            var bags = SeedGroup(vault, 2);

            // The SECOND planned row's re-read fails.
            backend.FailBarrelRowReadIds.Add(middleRow.Id);

            var rowsBefore = backend.Barrels.Count;
            var store = NewStore();

            Assert.IsFalse(Barrel(store, EntryFor(store, bags[0]), 2, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why);

            Assert.AreEqual(0, world.Destroyed.Count, "the first planned row is NOT evicted either");
            Assert.IsFalse(IsDestroyed(oldest) || IsDestroyed(middle));
            Assert.IsFalse(AnyRowStamped(new[] { oldestRow, middleRow }));

            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)), "the items being barreled never left the vault");
            Assert.AreEqual(rowsBefore, backend.Barrels.Count, "and no row was written for them");
        }

        [TestMethod]
        public void Eviction_ADestroyFailingAfterValidation_KeepsWhatLanded_RefusesAndClosesTheNewRows()
        {
            // The one remaining partial case: a validated row's destroy fails mid-execution.
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            var (middle, middleRow) = SeedBarreled(barrelContainer, 40);
            SeedBarreled(barrelContainer, 30);

            var bags = SeedGroup(vault, 2);
            var store = NewStore();
            var entry = EntryFor(store, bags[0]);
            var rowsBefore = backend.Barrels.Count;

            // While the first eviction is being stamped, the second planned item leaves the barrel.
            backend.BeforeUpdateBarrel = updating =>
            {
                if (updating.Id == oldestRow.Id)
                    barrelContainer.TryRemoveFromInventory(middle.Guid, out _);
            };

            var saves = world.SaveBiotaCalls(barrelContainer);

            Assert.IsFalse(Barrel(store, entry, 2, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why);

            Assert.IsTrue(IsDestroyed(oldest), "the eviction that landed stays landed");
            Assert.IsNotNull(Fresh(oldestRow).PurgedAt);
            Assert.IsNull(Fresh(middleRow).PurgedAt, "the failed one is not stamped");
            Assert.AreEqual(1, world.SaveBiotaCalls(barrelContainer) - saves, "the barrel is saved once, for the eviction that landed");

            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)), "the items being barreled never left the vault");

            var written = backend.Barrels.Skip(rowsBefore).ToList();
            Assert.AreEqual(2, written.Count, "precondition: the barreling had written its rows");
            Assert.IsTrue(written.All(r => r.RestoredAt != null && r.PurgedAt == null), "and they are closed as restored, not left open");
        }

        // ---- the restore race, both orders ----

        [TestMethod]
        public void Eviction_RestoreQueuedFirst_Wins_AndTheEvictionTakesTheNextOldest()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            var (next, nextRow) = SeedBarreled(barrelContainer, 40);

            var store = NewStore();

            // The restore is ahead of the barreling on the queue, so it lands before the plan is read.
            Assert.IsTrue(Restore(store, oldestRow.Id, out var r), r);
            SeedBarreled(barrelContainer, 10);

            var item = SeedStored(vault);

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.IsNotNull(Fresh(oldestRow).RestoredAt);
            Assert.IsNull(Fresh(oldestRow).PurgedAt, "the restored row is not also purged");
            Assert.IsFalse(IsDestroyed(oldest));
            Assert.IsTrue(vault.Inventory.ContainsKey(oldest.Guid), "the restored item is in the vault");
            Assert.IsTrue(IsDestroyed(next), "the next oldest open row is evicted instead");
            Assert.IsNotNull(Fresh(nextRow).PurgedAt);
        }

        [TestMethod]
        public void Eviction_RestoreQueuedDuringTheBarreling_IsRefused_AndTheEvictionWins()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var store = NewStore();

            Thread restorer = null;
            var restoreOk = true;
            string restoreReason = null;
            var blockedWhileBarreling = false;

            // While the eviction is stamping, a restore of the SAME row is started on another thread. The
            // hook does not return until that thread is provably parked (WaitSleepJoin) on the store's
            // drain lock, so the restore is queued behind the barreling rather than merely after it.
            backend.BeforeUpdateBarrel = updating =>
            {
                if (restorer != null || updating.Id != oldestRow.Id)
                    return;

                restorer = new Thread(() => restoreOk = Restore(store, oldestRow.Id, out restoreReason)) { IsBackground = true };
                restorer.Start();

                var deadline = DateTime.UtcNow.AddSeconds(10);

                while (DateTime.UtcNow < deadline && (restorer.ThreadState & ThreadState.WaitSleepJoin) == 0 && restorer.IsAlive)
                    Thread.Yield();

                blockedWhileBarreling = restorer.IsAlive && (restorer.ThreadState & ThreadState.WaitSleepJoin) != 0;
            };

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.IsNotNull(restorer, "precondition: the restore was started during the barreling");
            Assert.IsTrue(blockedWhileBarreling, "precondition: the restore was parked behind the barreling, not finished");
            Assert.IsTrue(restorer.Join(TimeSpan.FromSeconds(10)), "the restore runs once the barreling is done");

            Assert.IsFalse(restoreOk, "the restore is refused");
            Assert.AreEqual(AccountVaultStore.PurgedMessage, restoreReason, "because the row is already purged");

            Assert.IsNotNull(Fresh(oldestRow).PurgedAt, "the eviction won");
            Assert.IsNull(Fresh(oldestRow).RestoredAt);
            Assert.IsTrue(IsDestroyed(oldest));
            Assert.IsFalse(vault.Inventory.ContainsKey(oldest.Guid), "and nothing came back to the vault");
        }

        // ---- Q3: the plan reads open item rows only ----

        [TestMethod]
        public void Eviction_FindsTheOldestItems_BehindMoreThanAThousandNewerClosedAndLedgerRows()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            var (middle, _) = SeedBarreled(barrelContainer, 49);
            var (newest, _) = SeedBarreled(barrelContainer, 48);

            // More than the DAO ceiling of NEWER rows that name nothing in the barrel: ledger barrelings,
            // and item barrelings already restored or purged.
            var newer = ACE.Database.ShardDatabase.MaxAccountVaultBarrelRows + 50;

            for (var i = 0; i < newer; i++)
            {
                var row = new AccountVaultBarrel
                {
                    AccountId = OwnerAccount,
                    Wcid = 9001,
                    ItemGuid = i % 3 == 0 ? null : FakeVaultWorld.NextGuid(),
                    Count = 1,
                    ItemName = "History",
                    BarreledAt = DateTime.UtcNow.AddDays(-40).AddMinutes(i),
                    RestoredAt = i % 3 == 1 ? DateTime.UtcNow.AddDays(-1) : null,
                    PurgedAt = i % 3 == 2 ? DateTime.UtcNow.AddDays(-1) : null,
                };

                Assert.IsTrue(backend.AddAccountVaultBarrel(row));
            }

            Assert.IsTrue(backend.GetAccountVaultBarrels(OwnerAccount, ACE.Database.ShardDatabase.MaxAccountVaultBarrelRows).All(r => r.Id != oldestRow.Id),
                "precondition: the all-rows read cannot even see the barrel's oldest row");

            var item = SeedStored(vault);
            var store = NewStore();

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.AreEqual(1, world.Destroyed.Count);
            Assert.IsTrue(IsDestroyed(oldest), "the oldest item is still found and evicted");
            Assert.IsFalse(IsDestroyed(middle) || IsDestroyed(newest));
            Assert.IsNotNull(Fresh(oldestRow).PurgedAt);
        }

        // ---- Q4: cost ----

        [TestMethod]
        public void OverCapacityBarreling_SavesTheBarrelExactlyOnce()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);
            SeedBarreled(barrelContainer, 30);

            var bags = SeedGroup(vault, 2);
            var store = NewStore();
            var entry = EntryFor(store, bags[0]);

            var before = world.SaveBiotaCalls(barrelContainer);

            Assert.IsTrue(Barrel(store, entry, 2, out var why), why);
            Assert.AreEqual(2, world.Destroyed.Count, "precondition: two evictions");

            Assert.AreEqual(1, world.SaveBiotaCalls(barrelContainer) - before, "one barrel save for the whole barreling, not one per eviction");
        }

        [TestMethod]
        public void Barreling_NeedingMoreThanTheEvictionCap_IsRefused_AndDestroysNothing_ButTheCapItselfFits()
        {
            var cap = AccountVaultStore.MaxEvictionsPerBarreling;
            var capacity = cap + 6;

            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: capacity);

            for (var i = 0; i < capacity; i++)
                SeedBarreled(barrelContainer, 60 - i * 0.1);

            var bags = SeedGroup(vault, cap + 1);
            var store = NewStore();

            Assert.IsFalse(Barrel(store, EntryFor(store, bags[0]), cap + 1, out var why), "one slot past the cap");
            Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why);
            Assert.AreEqual(0, world.Destroyed.Count, "nothing destroyed");
            Assert.AreEqual(0, backend.OpenBarrelItemReads, "refused before the eviction read");

            Assert.IsTrue(Barrel(store, EntryFor(store, bags[0]), cap, out why), why);
            Assert.AreEqual(cap, world.Destroyed.Count, "exactly the cap fits");
        }

        // ---- the stamp failing ----

        [TestMethod]
        public void Eviction_DestroyedButUnstamped_StopsAfterThatOne_RefusesAndClosesTheNewRows()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (a, aRow) = SeedBarreled(barrelContainer, 50);
            var (b, bRow) = SeedBarreled(barrelContainer, 40);
            var (c, cRow) = SeedBarreled(barrelContainer, 30);

            var bags = SeedGroup(vault, 3);
            var store = NewStore();
            var entry = EntryFor(store, bags[0]);
            var rowsBefore = backend.Barrels.Count;

            // Three planned rows, and every one's stamp fails: a database that cannot take a stamp.
            backend.FailUpdateBarrelIds.UnionWith(new[] { aRow.Id, bRow.Id, cRow.Id });

            var saves = world.SaveBiotaCalls(barrelContainer);

            using (var capture = new MarketLogCapture())
            {
                Assert.IsFalse(Barrel(store, entry, 3, out var why));
                Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why, "the barreling is refused");

                Assert.AreEqual(1, capture.Events.Count(e => e.Level == log4net.Core.Level.Error && e.RenderedMessage.Contains("could not stamp purged_At")),
                    "one ERROR: one destroyed-but-unstamped item, logged once");
            }

            Assert.AreEqual(1, world.Destroyed.Count, "nothing more is destroyed after the first failed stamp");
            Assert.IsTrue(IsDestroyed(a), "the oldest went");
            Assert.IsFalse(IsDestroyed(b) || IsDestroyed(c));
            Assert.IsNull(Fresh(aRow).PurgedAt, "its row still reads open: the stamp never landed");
            Assert.AreEqual(1, world.SaveBiotaCalls(barrelContainer) - saves, "the barrel is saved for the eviction that landed");

            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)), "the items being barreled never left the vault");

            var written = backend.Barrels.Skip(rowsBefore).ToList();
            Assert.AreEqual(3, written.Count, "precondition: the barreling had written its rows");
            Assert.IsTrue(written.All(r => r.RestoredAt != null && r.PurgedAt == null), "and they are closed");
        }

        // ---- H1: an exception after the rows are written ----

        [TestMethod]
        public void Eviction_DestroyThrowingOnTheSecondCall_ClosesTheNewRows_SavesTheBarrel_AndTheThrowReachesTheCaller()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (a, aRow) = SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);
            SeedBarreled(barrelContainer, 30);

            var bags = SeedGroup(vault, 2);
            var store = NewStore();
            var entry = EntryFor(store, bags[0]);
            var rowsBefore = backend.Barrels.Count;

            var destroyCalls = 0;

            world.BeforeDestroyItem = _ =>
            {
                if (++destroyCalls == 2)
                    throw new InvalidOperationException("fake destroy failure on call 2");
            };

            var saves = world.SaveBiotaCalls(barrelContainer);

            var ok = true;
            Assert.IsTrue(store.Enqueue(() => ok = store.TryBarrel(entry, 2, Owner, out _), out var thrown));

            Assert.IsNotNull(thrown, "the exception is still observable to the caller");
            StringAssert.Contains(thrown.Message, "fake destroy failure on call 2", "and it is the original one, unchanged");

            Assert.IsTrue(IsDestroyed(a), "precondition: the first eviction landed");
            Assert.IsNotNull(Fresh(aRow).PurgedAt);
            Assert.AreEqual(1, world.SaveBiotaCalls(barrelContainer) - saves, "the barrel is saved for the eviction that landed");

            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)), "the items being barreled are still in the vault");

            var written = backend.Barrels.Skip(rowsBefore).ToList();
            Assert.AreEqual(2, written.Count, "precondition: the barreling had written its rows");
            Assert.IsTrue(written.All(r => r.RestoredAt != null && r.PurgedAt == null), "and they are closed, not left open over items in the vault");
        }

        // ---- H3: stray open rows for an item being barreled ----

        [TestMethod]
        public void Barreling_AnItemWithAStrayOpenRow_ClosesTheStray_LeavesOneOpenRow_AndTheReaperDestroysNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            // The item is in the vault, but an old open row names it: a barreling that never completed.
            var item = SeedStored(vault);
            var stray = AddItemRow(item.Guid.Full, item.WeenieClassId, 40);

            var store = NewStore();

            Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

            Assert.IsNotNull(Fresh(stray).RestoredAt, "the stray is closed");
            Assert.IsNull(Fresh(stray).PurgedAt);

            var open = backend.Barrels.Where(r => r.ItemGuid == item.Guid.Full && r.RestoredAt == null && r.PurgedAt == null).ToList();
            Assert.AreEqual(1, open.Count, "exactly one open row names the item: the new one");
            Assert.AreNotEqual(stray.Id, open[0].Id);

            // A retention pass where the stray's 40-day-old date is past the 30-day cutoff.
            var reaper = new AccountVaultBarrelReaper(backend, _ => store, _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow), "the stray is no longer expired-and-open, so nothing purges");
            Assert.IsFalse(IsDestroyed(item), "the re-barreled item survives");
        }

        [TestMethod]
        public void Barreling_WhenTheStrayCannotBeClosed_IsRefused_AndChangesNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            var item = SeedStored(vault);
            var stray = AddItemRow(item.Guid.Full, item.WeenieClassId, 40);

            backend.FailUpdateBarrelIds.Add(stray.Id);

            var store = NewStore();
            var rowsBefore = backend.Barrels.Count;

            using (var capture = new MarketLogCapture())
            {
                Assert.IsFalse(Barrel(store, EntryFor(store, item), 1, out var why));
                Assert.AreEqual(AccountVaultStore.BarrelUnavailableMessage, why);

                Assert.AreEqual(1, capture.Events.Count(e => e.Level == log4net.Core.Level.Error && e.RenderedMessage.Contains($"could not close stray row(s) {stray.Id}")));
            }

            Assert.AreEqual(rowsBefore, backend.Barrels.Count, "nothing written");
            Assert.IsNull(Fresh(stray).RestoredAt, "the stray is still open");
            Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid), "nothing moved");
            Assert.AreEqual(0, barrelContainer.Inventory.Count);
            Assert.AreEqual(0, world.Destroyed.Count, "nothing destroyed");
        }

        // ---- H4: the missing refusal paths ----

        [TestMethod]
        public void Barreling_WhoseSecondRowInsertFails_ClosesExactlyTheWrittenRow_AndDestroysNothing()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var seeded = new[] { SeedBarreled(barrelContainer, 50), SeedBarreled(barrelContainer, 40), SeedBarreled(barrelContainer, 30) };

            var bags = SeedGroup(vault, 2);
            var store = NewStore();
            var entry = EntryFor(store, bags[0]);
            var rowsBefore = backend.Barrels.Count;

            backend.FailAddBarrelOnCall = backend.AddBarrelCalls + 2;

            Assert.IsFalse(Barrel(store, entry, 2, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelUnavailableMessage, why);

            var written = backend.Barrels.Skip(rowsBefore).ToList();
            Assert.AreEqual(1, written.Count, "row 1 was written, row 2 was not");
            Assert.IsNotNull(written[0].RestoredAt, "and exactly that row is closed");

            Assert.AreEqual(0, world.Destroyed.Count, "the inserts come before any eviction");
            Assert.IsFalse(AnyRowStamped(seeded.Select(s => s.Row)));
            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)));
        }

        [TestMethod]
        public void Barreling_WhoseCloseWriteFails_LogsTheError_AndLeavesThatRowOpenOverTheVaultItem()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);
            SeedBarreled(barrelContainer, 30);

            var bags = SeedGroup(vault, 2);
            var store = NewStore();
            var entry = EntryFor(store, bags[0]);
            var rowsBefore = backend.Barrels.Count;

            backend.FailAddBarrelOnCall = backend.AddBarrelCalls + 2;
            backend.FailUpdateBarrel = true;

            using (var capture = new MarketLogCapture())
            {
                Assert.IsFalse(Barrel(store, entry, 2, out var why));
                Assert.AreEqual(AccountVaultStore.BarrelUnavailableMessage, why);

                var written = backend.Barrels.Skip(rowsBefore).Single();

                Assert.AreEqual(1, capture.Events.Count(e => e.Level == log4net.Core.Level.Error && e.RenderedMessage.Contains($"could not close refused-barreling row(s) {written.Id}")),
                    "the failed close is an ERROR naming the row");

                Assert.IsNull(written.RestoredAt, "the row stays open");
                Assert.IsTrue(bags.Any(bag => bag.Guid.Full == written.ItemGuid && vault.Inventory.ContainsKey(bag.Guid)), "over an item that is still in the vault");
            }

            Assert.IsTrue(bags.All(bag => vault.Inventory.ContainsKey(bag.Guid)));
            Assert.AreEqual(0, world.Destroyed.Count);
        }

        [TestMethod]
        public void Barreling_WhoseMoveFailsAfterAnEvictionLanded_KeepsTheEviction_ClosesTheRow_AndSavesTheBarrel()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (a, aRow) = SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var store = NewStore();
            var entry = EntryFor(store, item);
            var rowsBefore = backend.Barrels.Count;

            // While the eviction is stamped, the target leaves the vault, so the move cannot find it.
            backend.BeforeUpdateBarrel = updating =>
            {
                if (updating.Id == aRow.Id)
                    vault.TryRemoveFromInventory(item.Guid, out _);
            };

            var saves = world.SaveBiotaCalls(barrelContainer);

            Assert.IsFalse(Barrel(store, entry, 1, out var why));
            Assert.AreEqual("That is no longer in your vault.", why, "refused on the move");

            Assert.IsTrue(IsDestroyed(a), "the eviction landed and stays landed");
            Assert.IsNotNull(Fresh(aRow).PurgedAt);
            Assert.AreEqual(1, world.SaveBiotaCalls(barrelContainer) - saves, "the barrel is saved once");
            Assert.IsNotNull(backend.Barrels.Skip(rowsBefore).Single().RestoredAt, "the barreling's row is closed");
        }

        [TestMethod]
        public void Barreling_StillTooFullAfterEviction_IsRefused_ClosesTheRow_AndSavesTheBarrel()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (a, aRow) = SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var store = NewStore();
            var entry = EntryFor(store, item);
            var rowsBefore = backend.Barrels.Count;

            // While the eviction is stamped, something else fills the freed slot.
            backend.BeforeUpdateBarrel = updating =>
            {
                if (updating.Id == aRow.Id)
                    Assert.IsTrue(barrelContainer.TryAddToInventory(FakeVaultWorld.MakeStack(nextWcid++, 1, 100)));
            };

            var saves = world.SaveBiotaCalls(barrelContainer);

            Assert.IsFalse(Barrel(store, entry, 1, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelFullMessage, why);

            Assert.IsTrue(IsDestroyed(a), "the eviction landed and stays landed");
            Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid), "nothing moved");
            Assert.AreEqual(1, world.SaveBiotaCalls(barrelContainer) - saves, "the barrel is saved once");
            Assert.IsNotNull(backend.Barrels.Skip(rowsBefore).Single().RestoredAt, "the barreling's row is closed");
        }

        [TestMethod]
        public void Reaper_ARowRestoredAndReBarreledAfterTheSnapshot_IsRefusedThroughTryPurgeAndStamp_AndNotStamped()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (item, row) = SeedBarreled(barrelContainer, 40);

            var store = NewStore();
            var raced = false;

            // After the sweep has read its (detached) snapshot of the row, an admin restores the item and
            // the player barrels it again under a NEW row. The stale row must not destroy it.
            var reaper = new AccountVaultBarrelReaper(backend, _ =>
            {
                if (!raced)
                {
                    raced = true;
                    Assert.IsTrue(Restore(store, row.Id, out var r), r);
                    Assert.IsTrue(vault.Inventory.ContainsKey(item.Guid), "precondition: restored to the vault");
                    Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var w), w);
                }

                return store;
            }, _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));

            Assert.IsTrue(raced);
            Assert.IsFalse(IsDestroyed(item), "the re-barreled item survives the stale row");
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(item.Guid));
            Assert.IsNotNull(Fresh(row).RestoredAt);
            Assert.IsNull(Fresh(row).PurgedAt, "and the stale row is not stamped");
            Assert.AreEqual(1, backend.Barrels.Count(r => r.ItemGuid == item.Guid.Full && r.RestoredAt == null && r.PurgedAt == null), "the new row is open");
        }

        [TestMethod]
        public void TwoHundredSequentialSingleBarrelings_IntoAFullBarrel_AllLand_EachEvictingExactlyTheOldest()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);

            var oldestFirst = new Queue<WorldObject>(new[]
            {
                SeedBarreled(barrelContainer, 50).Item,
                SeedBarreled(barrelContainer, 49).Item,
                SeedBarreled(barrelContainer, 48).Item,
            });

            const int count = 200;

            var items = Enumerable.Range(0, count).Select(_ => SeedStored(vault)).ToList();
            var store = NewStore();

            for (var i = 0; i < count; i++)
            {
                var expected = oldestFirst.Dequeue();

                Assert.IsTrue(Barrel(store, EntryFor(store, items[i]), 1, out var why), $"barreling {i}: {why}");

                Assert.AreEqual(i + 1, world.Destroyed.Count, $"barreling {i} evicts exactly one");
                Assert.AreEqual(expected.Guid.Full, world.Destroyed[i].Guid.Full, $"barreling {i} evicts the oldest");
                Assert.AreEqual(3, barrelContainer.Inventory.Count);

                oldestFirst.Enqueue(items[i]);
            }
        }

        // ---- H5: the DAO query, pinned without a database ----

        /// <summary>The method body of ShardDatabase.GetOpenAccountVaultBarrelItems, one trimmed line per entry.</summary>
        internal static List<string> OpenItemsQueryLines()
        {
            string path = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "ACE.Database", "ShardDatabase_AccountVault.cs");

                if (File.Exists(candidate))
                    path = candidate;
            }

            Assert.IsNotNull(path, $"Could not find ACE.Database/ShardDatabase_AccountVault.cs by walking up from {AppContext.BaseDirectory}");

            var lines = File.ReadAllLines(path);
            var start = Array.FindIndex(lines, l => l.Trim() == "public List<AccountVaultBarrel> GetOpenAccountVaultBarrelItems(uint accountId, int limit)");

            Assert.IsTrue(start >= 0, "the method signature is not where the pin expects it");

            // The method ends at the first line that is exactly its closing brace at method indentation.
            var end = Array.FindIndex(lines, start + 1, l => l == "        }");

            Assert.IsTrue(end > start, "the method's closing brace was not found");

            return lines.Skip(start + 1).Take(end - start - 1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        [TestMethod]
        public void OpenItemsQuery_FiltersAndOrdersExactlyAsTheEvictionPlanNeeds()
        {
            var body = OpenItemsQueryLines();

            // EXACT line equality, in order, so removing or loosening one predicate fails the pin.
            var expected = new[]
            {
                "limit = ClampBarrelLimit(limit);",
                "context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;",
                "return context.AccountVaultBarrel",
                ".Where(b => b.AccountId == accountId && b.ItemGuid != null && b.RestoredAt == null && b.PurgedAt == null)",
                ".OrderByDescending(b => b.BarreledAt)",
                ".ThenByDescending(b => b.Id)",
                ".Take(limit)",
                ".ToList();",
            };

            var at = -1;

            foreach (var line in expected)
            {
                var found = body.FindIndex(at + 1, l => l == line);
                Assert.IsTrue(found > at, $"missing or out of order: {line}");
                at = found;
            }

            Assert.AreEqual(1, body.Count(l => l.StartsWith(".Where(")), "exactly one filter");
            Assert.AreEqual(0, body.Count(l => l.StartsWith(".OrderBy(") || l.StartsWith(".ThenBy(")), "no ascending order slipped in");
        }

        [TestMethod]
        public void RetentionPurge_DestroyedButUnstamped_LogsOneError()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 3);
            var (item, row) = SeedBarreled(barrelContainer, 40);

            var store = NewStore();
            var reaper = new AccountVaultBarrelReaper(backend, _ => store, _ => { });

            backend.FailUpdateBarrel = true;

            using (var capture = new MarketLogCapture())
            {
                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));

                Assert.AreEqual(1, capture.Events.Count(e => e.Level == log4net.Core.Level.Error && e.RenderedMessage.Contains("could not stamp purged_At") && e.RenderedMessage.Contains($"row {row.Id}")),
                    "the store logs it once and the reaper does not repeat it");
            }

            Assert.IsTrue(IsDestroyed(item));
        }

        [TestMethod]
        public void Eviction_LogsOneInfoLinePerEvictedRow()
        {
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind, capacity: 2);
            var (oldest, oldestRow) = SeedBarreled(barrelContainer, 50);
            SeedBarreled(barrelContainer, 40);

            var item = SeedStored(vault);
            var store = NewStore();

            using (var capture = new MarketLogCapture())
            {
                Assert.IsTrue(Barrel(store, EntryFor(store, item), 1, out var why), why);

                var lines = capture.Events
                    .Where(e => e.Level == log4net.Core.Level.Info && e.RenderedMessage.Contains("EVICTED EARLY FOR CAPACITY"))
                    .Select(e => e.RenderedMessage)
                    .ToList();

                Assert.AreEqual(1, lines.Count);
                StringAssert.Contains(lines[0], $"account {OwnerAccount}:");
                StringAssert.Contains(lines[0], $"row {oldestRow.Id}");
                StringAssert.Contains(lines[0], $"0x{oldest.Guid.Full:X8}");
            }
        }
    }
}

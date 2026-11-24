using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Barrel retention against a store nobody has loaded (Discord bug thread 1569220583785234502).
    ///
    /// On prod the hourly sweep left the same rows open every hour with "The barrel is unavailable",
    /// because the store it resolves for an account whose owner has been offline since the last
    /// restart is a fresh one that has never read its index: the barrel field is still null, and the
    /// purge refused on that alone. Every test here starts from that cold store, which is the shape
    /// AccountVaultBarrelTests never reached - each of its reaper tests barrels through the store
    /// first, and the barreling is what loaded it.
    ///
    /// Driven through FakeVaultBackend and FakeVaultWorld, so no database is involved.
    /// </summary>
    [TestClass]
    public class AccountVaultBarrelColdStoreTests
    {
        private const uint OwnerAccount = 4171;

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        private uint nextSeedOrder = 1;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // Every PropertyManager key the store reads on these paths is SEEDED here, so no read falls
            // through to DatabaseManager.ShardConfig (which throws in the unit-test process), and every
            // one is put back to its shipped default in Teardown.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000),
                "account_vault_entry_cap is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));
            Assert.IsTrue(PropertyManager.ModifyLong(AccountVaultBarrelReaper.RetentionKey, 30),
                "vault_barrel_retention_days is missing from DefaultLongProperties");

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
                RestoreLong("account_vault_entry_cap");
                RestoreLong("account_vault_landblock");
                RestoreLong(AccountVaultBarrelReaper.RetentionKey);
                RestoreBool("account_vault_class_storage");
                RestoreBool("account_vault_salvage_combine");
            }
        }

        private static void RestoreLong(string key)
        {
            PropertyManager.ModifyLong(key, DefaultPropertyManager.DefaultLongProperties[key].Item);
        }

        private static void RestoreBool(string key)
        {
            PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);
        }

        // ---- helpers ----

        private AccountVaultStore NewColdStore()
        {
            var store = new AccountVaultStore(OwnerAccount, backend, world);

            // The precondition every test here rests on: nothing has read the index yet.
            Assert.IsFalse(store.IsIndexLoaded, "the store under test must start cold");
            Assert.IsFalse(store.HasBarrelForTest, "a cold store has not seen its barrel yet");

            return store;
        }

        private Container SeedContainer(int kind) => SeedContainer(OwnerAccount, kind);

        private Container SeedContainer(uint accountId, int kind)
        {
            var container = FakeVaultWorld.MakeContainer(AccountVaultStore.VaultItemCapacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9500 + nextSeedOrder,
                AccountId = accountId,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextSeedOrder),
                Kind = kind,
            });

            nextSeedOrder++;

            return container;
        }

        /// <summary>
        /// One item already sitting in the barrel container plus the audit row naming it, barreled at
        /// <paramref name="barreledAt"/>. Seeded straight into the container and the table, exactly
        /// the on-disk shape a restart leaves behind, so the store under test has done nothing yet.
        /// </summary>
        private (WorldObject Item, AccountVaultBarrel Row) SeedBarreledItem(Container barrelContainer, DateTime barreledAt)
            => SeedBarreledItem(OwnerAccount, barrelContainer, barreledAt);

        private (WorldObject Item, AccountVaultBarrel Row) SeedBarreledItem(uint accountId, Container barrelContainer, DateTime barreledAt)
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 100);

            Assert.IsTrue(barrelContainer.TryAddToInventory(item), "could not seed a barreled item");

            var row = new AccountVaultBarrel
            {
                AccountId = accountId,
                Wcid = 9001,
                ItemGuid = item.Guid.Full,
                Count = 1,
                ItemName = item.Name,
                BarreledAt = barreledAt,
                ActorCharacterGuid = 0x50000471,
                ActorCharacterName = "Coldowner",
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            return (item, row);
        }

        private AccountVaultBarrelReaper ReaperFor(AccountVaultStore store)
        {
            return new AccountVaultBarrelReaper(backend, _ => store);
        }

        // ---- the reproduction ----

        [TestMethod]
        public void Reaper_OnAColdStore_PurgesAnExpiredItem_WithoutTheOwnerLoggingIn()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            var store = NewColdStore();

            var purged = ReaperFor(store).RunOnce(DateTime.UtcNow);

            Assert.AreEqual(1, purged, "an expired row on a store nobody has opened must still purge");
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full), "the item is really destroyed");
            Assert.IsFalse(barrelContainer.Inventory.ContainsKey(item.Guid), "and is no longer in the barrel");
            Assert.IsNotNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt, "and the row is stamped");
            Assert.IsTrue(store.IsIndexLoaded, "the purge loaded the store through its own load path");
        }

        // ---- an in-flight inventory load ----

        /// <summary>A reaper whose in-pass wait is recorded and runs <paramref name="onWait"/> instead of sleeping.</summary>
        private AccountVaultBarrelReaper ReaperFor(AccountVaultStore store, List<TimeSpan> waits, Action<int> onWait = null)
        {
            return new AccountVaultBarrelReaper(backend, _ => store, delay =>
            {
                waits.Add(delay);
                onWait?.Invoke(waits.Count);
            });
        }

        private static bool Purge(AccountVaultStore store, AccountVaultBarrel row, out string failReason)
        {
            var ok = false;
            string reason = null;

            Assert.IsTrue(store.Enqueue(() => ok = store.TryPurgeFromBarrel(row, out reason), out var thrown));
            Assert.IsNull(thrown, thrown?.ToString());

            failReason = reason;
            return ok;
        }

        [TestMethod]
        public void Purge_WhileTheBarrelInventoryIsStillLoading_ReturnsTheDistinctReason_AndDestroysNothing()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            // The R4 state in production: the container biota is read, its contents are still on the
            // shard queue. Reached through the real property, which is what the purge reads.
            FakeVaultWorld.SetInventoryLoaded(barrelContainer, false);

            var store = NewColdStore();

            Assert.IsFalse(Purge(store, row, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelStillLoadingMessage, why, "a load in flight is not 'unavailable'");
            Assert.AreNotEqual(AccountVaultStore.BarrelUnavailableMessage, why);

            Assert.IsTrue(store.IsIndexLoaded, "the purge started the load");
            Assert.IsTrue(store.HasBarrelForTest, "and the barrel container is now known");
            Assert.AreEqual(0, world.Destroyed.Count, "nothing is purged against an inventory that has not arrived");
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(item.Guid));
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
        }

        [TestMethod]
        public void Reaper_WhenTheLoadNeverFinishesInThePass_RetriesABoundedNumberOfTimes_AndLeavesTheRowOpen()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            FakeVaultWorld.SetInventoryLoaded(barrelContainer, false);

            var store = NewColdStore();
            var waits = new List<TimeSpan>();

            Assert.AreEqual(0, ReaperFor(store, waits).RunOnce(DateTime.UtcNow));

            Assert.AreEqual(AccountVaultBarrelReaper.StillLoadingRetryAttempts, waits.Count, "the wait is bounded");
            Assert.IsTrue(waits.All(w => w == AccountVaultBarrelReaper.StillLoadingRetryDelay));
            Assert.AreEqual(0, world.Destroyed.Count);
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(item.Guid));
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt, "left open, for the next pass");

            // ...and the next pass, once the load has landed, finishes the job.
            FakeVaultWorld.SetInventoryLoaded(barrelContainer, true);

            waits.Clear();

            Assert.AreEqual(1, ReaperFor(store, waits).RunOnce(DateTime.UtcNow));
            Assert.AreEqual(0, waits.Count);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
        }

        [TestMethod]
        public void Reaper_WhenTheLoadFinishesDuringThePass_PurgesInThatSamePass()
        {
            // The case that matters on prod: an hour later the idle sweep has retired the store, so a
            // purge that only ever succeeded "next pass" would never succeed at all.
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            FakeVaultWorld.SetInventoryLoaded(barrelContainer, false);

            var store = NewColdStore();
            var waits = new List<TimeSpan>();

            // The async inventory load lands while the reaper is waiting for its second retry.
            var reaper = ReaperFor(store, waits, n =>
            {
                if (n == 2)
                    FakeVaultWorld.SetInventoryLoaded(barrelContainer, true);
            });

            Assert.AreEqual(1, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(2, waits.Count, "it stops waiting as soon as nothing is deferred");
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
            Assert.IsNotNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
        }

        // ---- what must not change ----

        [TestMethod]
        public void Reaper_OnAColdStore_ANonExpiredRowSurvives()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (oldItem, oldRow) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));
            var (newItem, newRow) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-1));

            var store = NewColdStore();

            Assert.AreEqual(1, ReaperFor(store).RunOnce(DateTime.UtcNow));

            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == oldItem.Guid.Full));
            Assert.IsNotNull(backend.GetAccountVaultBarrel(oldRow.Id).PurgedAt);

            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == newItem.Guid.Full), "an item inside the window is never destroyed");
            Assert.IsTrue(barrelContainer.Inventory.ContainsKey(newItem.Guid), "and is still restorable");
            Assert.IsNull(backend.GetAccountVaultBarrel(newRow.Id).PurgedAt);
        }

        [TestMethod]
        public void Reaper_OnAnAlreadyLoadedStore_PurgesExactlyAsBefore_WithNoReloadAndNoWait()
        {
            // The control: an online owner with the vault open. The purge's load call must be the
            // no-read fast path, so nothing they see is rebuilt underneath them.
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            var store = NewColdStore();

            Assert.IsTrue(store.IsLoaded);

            var readsBefore = backend.VaultReads;
            var waits = new List<TimeSpan>();

            Assert.AreEqual(1, ReaperFor(store, waits).RunOnce(DateTime.UtcNow));

            Assert.AreEqual(readsBefore, backend.VaultReads, "a loaded store is not re-read by the purge");
            Assert.AreEqual(0, waits.Count);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
            Assert.IsTrue(world.Saved.Contains(barrelContainer), "the barrel is saved after the destroy, as before");
            Assert.IsNotNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
            Assert.AreEqual(1, store.VaultContainerCountForTest, "the barrel still never joins the vault list");
        }

        // ---- fakes that fail part way ----

        [TestMethod]
        public void Reaper_WhenTheIndexReadFails_PurgesNothing_ThenHealsOnTheNextPass()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            var store = NewColdStore();
            var waits = new List<TimeSpan>();

            backend.FailVaultRead = true;

            Assert.IsFalse(Purge(store, row, out var why));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, why, "a failed read is not a load in flight");

            Assert.AreEqual(0, ReaperFor(store, waits).RunOnce(DateTime.UtcNow));
            Assert.AreEqual(0, waits.Count, "a failed read is not waited on within the pass");
            Assert.AreEqual(0, world.Destroyed.Count);
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
            Assert.IsFalse(store.IsIndexLoaded, "and leaves nothing half-loaded behind");

            backend.FailVaultRead = false;

            Assert.AreEqual(1, ReaperFor(store, waits).RunOnce(DateTime.UtcNow));
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
        }

        [TestMethod]
        public void Reaper_WhenOnlyTheBarrelContainerCannotBeRead_PurgesNothing_AndTheVaultStillLoads()
        {
            // Part way: the index, ledger and vault container all read, and the barrel biota does not.
            SeedContainer(AccountVaultStore.VaultContainerKind);

            var barrelGuid = FakeVaultWorld.NextGuid();

            world.FailLoadContainerGuids.Add(barrelGuid);

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9599,
                AccountId = OwnerAccount,
                ContainerGuid = barrelGuid,
                CreatedAt = new DateTime(2026, 1, 2),
                Kind = AccountVaultStore.BarrelContainerKind,
            });

            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = 9001,
                ItemGuid = FakeVaultWorld.NextGuid(),
                Count = 1,
                ItemName = "Behind An Unreadable Barrel",
                BarreledAt = DateTime.UtcNow.AddDays(-31),
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            var store = NewColdStore();
            var waits = new List<TimeSpan>();

            Assert.IsFalse(Purge(store, row, out var why));
            Assert.AreEqual(AccountVaultStore.BarrelUnavailableMessage, why);

            Assert.AreEqual(0, ReaperFor(store, waits).RunOnce(DateTime.UtcNow));
            Assert.AreEqual(0, waits.Count);
            Assert.AreEqual(0, world.Destroyed.Count);
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
            Assert.IsTrue(store.IsLoaded, "a barrel that will not load must not take the vault down with it");
        }

        // ---- racing the owner's own load ----

        [TestMethod]
        public void Reaper_RacingTheOwnersOwnLoad_LoadsTheStoreExactlyOnce()
        {
            // The owner logs in and opens the vault at the moment the sweep reaches their row. Both
            // sides load through TryEnsureLoadedLocked under stateLock, so whichever arrives second
            // takes the fast path: one index read, never two loads interleaved.
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, _) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            var store = NewColdStore();

            // Widens the window: every LoadContainer call sleeps, and records overlap if two run at once.
            world.CallDelayMs = 25;

            using var start = new System.Threading.Barrier(2);

            var reaperRun = System.Threading.Tasks.Task.Run(() =>
            {
                start.SignalAndWait();
                return ReaperFor(store).RunOnce(DateTime.UtcNow);
            });

            var ownerRun = System.Threading.Tasks.Task.Run(() =>
            {
                start.SignalAndWait();
                return store.IsLoaded;
            });

            Assert.IsTrue(System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { reaperRun, ownerRun }, TimeSpan.FromSeconds(30)), "deadlock");

            Assert.AreEqual(1, reaperRun.Result);
            Assert.IsTrue(ownerRun.Result);
            Assert.AreEqual(1, backend.VaultReads, "the store was loaded exactly once");
            Assert.AreEqual(1, world.MaxConcurrentCalls, "no two loads ran at the same time");
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
            Assert.AreEqual(1, store.VaultContainerCountForTest);
        }

        // ---- review round: what the purge reports on a cold store ----

        [TestMethod]
        public void Purge_OnAColdStore_ARowTheBarrelDoesNotHold_ReportsDoesNotHold_NotUnavailable()
        {
            // AccountVaultBarrelTests' version of this cannot tell "does not hold it" from "unavailable":
            // both leave the row open. This one asserts the reason, so it fails if the load is skipped.
            SeedContainer(AccountVaultStore.VaultContainerKind);
            SeedContainer(AccountVaultStore.BarrelContainerKind);

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

            var store = NewColdStore();

            Assert.IsFalse(Purge(store, row, out var why));
            StringAssert.Contains(why, "does not hold");
            Assert.AreNotEqual(AccountVaultStore.BarrelUnavailableMessage, why);
            Assert.AreEqual(0, world.Destroyed.Count);
            Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
        }

        [TestMethod]
        public void Purge_WhenOnlyAVaultContainerIsStillLoading_StillPurgesTheBarrel()
        {
            // The reason the purge uses TryEnsureLoadedLocked rather than CheckReadyLocked: it touches
            // only the barrel, so a vault container still loading must not block it.
            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            FakeVaultWorld.SetInventoryLoaded(vault, false);

            var store = NewColdStore();

            Assert.IsTrue(Purge(store, row, out var why), why);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
            Assert.IsFalse(store.IsLoaded, "precondition: the vault itself really was still loading");
        }

        [TestMethod]
        public void Reaper_OnAColdStore_LogsTheLoadTimeOnce_AndNotForALoadedStore()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));
            SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            var store = NewColdStore();

            using (var capture = new MarketLogCapture())
            {
                Assert.AreEqual(2, ReaperFor(store).RunOnce(DateTime.UtcNow));

                var lines = capture.Events.Select(e => e.RenderedMessage).Where(m => m.Contains("retention loaded a cold store in")).ToList();

                Assert.AreEqual(1, lines.Count, "one real load, one line - the second row took the fast path");
                StringAssert.Contains(lines[0], $"account {OwnerAccount}:");
                StringAssert.Contains(lines[0], "(1 vault containers)");
            }

            SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            using (var capture = new MarketLogCapture())
            {
                Assert.AreEqual(1, ReaperFor(store).RunOnce(DateTime.UtcNow));
                Assert.IsFalse(capture.Events.Any(e => e.RenderedMessage.Contains("retention loaded a cold store in")));
            }
        }

        // ---- review round: a stale snapshot ----

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, 0x50000471, "Coldowner");

        private static bool Barrel(AccountVaultStore store, VaultEntry entry, int amount, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryBarrel(entry, amount, Owner, out reason));

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

        [TestMethod]
        public void Reaper_ARowRestoredAndReBarreledAfterTheSweepReadIt_DestroysNothing()
        {
            // The real DAO hands back detached rows, so the reaper's snapshot does not see a later restore.
            backend.ExpiredReadReturnsCopies = true;

            var vault = SeedContainer(AccountVaultStore.VaultContainerKind);
            var item = FakeVaultWorld.MakeStack(9001, 1, 100);
            Assert.IsTrue(vault.TryAddToInventory(item));

            var store = NewColdStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, out var why), why);

            var oldRow = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            oldRow.BarreledAt = DateTime.UtcNow.AddDays(-31);

            var raced = false;

            // Between the sweep's read of the expired rows and the purge: an admin restores the item and
            // the player barrels it again, so the SAME guid is back in the barrel under a new, young row.
            var reaper = new AccountVaultBarrelReaper(backend, _ =>
            {
                if (!raced)
                {
                    raced = true;

                    Assert.IsTrue(Restore(store, oldRow.Id, out var r1), r1);
                    Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 1, out var r2), r2);
                }

                return store;
            }, _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));

            Assert.IsTrue(raced, "precondition: the race really ran");
            Assert.IsFalse(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full), "the re-barreled item must survive");

            var newRow = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single(r => r.Id != oldRow.Id);

            Assert.AreEqual(item.Guid.Full, newRow.ItemGuid, "precondition: the new row names the same item");
            Assert.IsNull(backend.GetAccountVaultBarrel(oldRow.Id).PurgedAt, "the old row is not stamped");
            Assert.IsNotNull(backend.GetAccountVaultBarrel(oldRow.Id).RestoredAt, "and its restore stamp is intact");
            Assert.IsNull(newRow.PurgedAt);
        }

        // ---- review round: one account at a time, and a cap ----

        private const uint FirstAccount = 4300;

        /// <summary>One cold account with a vault, a barrel and one expired barreled item. Containers start UNLOADED when asked.</summary>
        private (Container Vault, Container Barrel, WorldObject Item, AccountVaultBarrel Row) SeedColdAccount(uint accountId, bool inventoriesLoading, int ageDays = 31)
        {
            var vault = SeedContainer(accountId, AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(accountId, AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(accountId, barrelContainer, DateTime.UtcNow.AddDays(-ageDays));

            if (inventoriesLoading)
            {
                FakeVaultWorld.SetInventoryLoaded(vault, false);
                FakeVaultWorld.SetInventoryLoaded(barrelContainer, false);
            }

            return (vault, barrelContainer, item, row);
        }

        /// <summary>A store per account, rebuilt if retired - what AccountVaultManager.GetStore does.</summary>
        private Func<uint, AccountVaultStore> StoreTable(Dictionary<uint, AccountVaultStore> stores, Action<uint> onResolve = null)
        {
            return accountId =>
            {
                onResolve?.Invoke(accountId);

                if (!stores.TryGetValue(accountId, out var store) || store.IsEvicted)
                {
                    store = new AccountVaultStore(accountId, backend, world);
                    stores[accountId] = store;
                }

                return store;
            };
        }

        [TestMethod]
        public void Reaper_LoadsAtMostTheCapOfColdAccountsPerPass_AndTheRestOnLaterPasses()
        {
            var count = AccountVaultBarrelReaper.MaxColdLoadsPerPass + 5;
            var seeded = new List<(Container Vault, Container Barrel, WorldObject Item, AccountVaultBarrel Row)>();

            for (var i = 0; i < count; i++)
                seeded.Add(SeedColdAccount(FirstAccount + (uint)i, inventoriesLoading: false, ageDays: 60 - i));

            var stores = new Dictionary<uint, AccountVaultStore>();
            var reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores), _ => { });

            Assert.AreEqual(AccountVaultBarrelReaper.MaxColdLoadsPerPass, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(AccountVaultBarrelReaper.MaxColdLoadsPerPass, backend.VaultReads, "one index read per cold account, capped");
            Assert.AreEqual(AccountVaultBarrelReaper.MaxColdLoadsPerPass * 2, world.LoadContainerCalls, "two containers per loaded account");

            // Oldest first: the five youngest are the ones left.
            for (var i = AccountVaultBarrelReaper.MaxColdLoadsPerPass; i < count; i++)
                Assert.IsNull(backend.GetAccountVaultBarrel(seeded[i].Row.Id).PurgedAt);

            Assert.AreEqual(5, reaper.RunOnce(DateTime.UtcNow), "the rest are picked up by the next pass");
            Assert.IsTrue(seeded.All(s => backend.GetAccountVaultBarrel(s.Row.Id).PurgedAt != null));

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow), "converged: purged rows have left the expired query");
        }

        [TestMethod]
        public void Reaper_WhileLoadsSettle_StartsNoLoadUntilItsPreviousLoadHasSettled()
        {
            const int accounts = 4;

            var seeded = new Dictionary<uint, (Container Vault, Container Barrel, WorldObject Item, AccountVaultBarrel Row)>();

            for (var i = 0; i < accounts; i++)
                seeded[FirstAccount + (uint)i] = SeedColdAccount(FirstAccount + (uint)i, inventoriesLoading: true, ageDays: 60 - i);

            var stores = new Dictionary<uint, AccountVaultStore>();
            uint current = 0;
            var unsettledAtLoadStart = 0;
            var maxUnsettled = 0;
            var loadsChecked = 0;

            int CountUnsettled() => stores.Values.Count(s => s.GetRetentionLoadState(out _) == AccountVaultStore.RetentionLoadState.Unsettled);

            // The counting hook: at the moment ANY container load starts, no other store may still be
            // waiting on a load the reaper started. (The store being loaded is still Cold here.)
            world.BeforeLoadContainer = _ =>
            {
                loadsChecked++;
                unsettledAtLoadStart = Math.Max(unsettledAtLoadStart, CountUnsettled());
            };

            // The async loads land one container per wait, barrel first: a reaper that moved on as soon
            // as the BARREL was ready would start the next account with this one's vault still loading.
            var waitsForCurrent = 0;

            var reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores, a => { current = a; waitsForCurrent = 0; }), _ =>
            {
                maxUnsettled = Math.Max(maxUnsettled, CountUnsettled());

                waitsForCurrent++;

                if (waitsForCurrent == 1)
                    FakeVaultWorld.SetInventoryLoaded(seeded[current].Barrel, true);
                else if (waitsForCurrent == 2)
                    FakeVaultWorld.SetInventoryLoaded(seeded[current].Vault, true);
            });

            Assert.AreEqual(accounts, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(accounts * 2, loadsChecked, "precondition: the hook saw every container load");
            Assert.AreEqual(1, maxUnsettled, "precondition: each load really was unsettled while the reaper waited");
            Assert.AreEqual(0, unsettledAtLoadStart, "no load starts while a previous reaper load is unsettled");
        }

        [TestMethod]
        public void Reaper_ALoadThatNeverCompletes_WarnsExactlyOnce_OnTheThirdPass()
        {
            var stuck = SeedColdAccount(OwnerAccount, inventoriesLoading: true);

            var store = NewColdStore();
            var reaper = new AccountVaultBarrelReaper(backend, _ => store, _ => { });

            using (var capture = new MarketLogCapture())
            {
                bool IsEscalation(log4net.Core.LoggingEvent e) =>
                    e.Level == log4net.Core.Level.Warn && e.RenderedMessage.Contains("consecutive passes") && e.RenderedMessage.Contains($"account {OwnerAccount}");

                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
                Assert.AreEqual(0, capture.Events.Count(IsEscalation), "not before the third pass");
                Assert.AreEqual(2, reaper.ConsecutiveDeferredPassesForTest(OwnerAccount));

                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
                Assert.AreEqual(1, capture.Events.Count(IsEscalation), "one WARN on the third pass");

                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
                Assert.AreEqual(1, capture.Events.Count(IsEscalation), "and not again on every pass after");
            }

            Assert.AreEqual(0, world.Destroyed.Count);

            // Once the load finally lands, the account purges and its history is cleared.
            FakeVaultWorld.SetInventoryLoaded(stuck.Vault, true);
            FakeVaultWorld.SetInventoryLoaded(stuck.Barrel, true);

            Assert.AreEqual(1, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(0, reaper.ConsecutiveDeferredPassesForTest(OwnerAccount));
        }

        // ---- second review round ----

        [TestMethod]
        public void Reaper_TwoHundredPermanentlyRefusedRows_CannotHideAPurgeableRowBehindThem()
        {
            // 200 old rows whose items the barrel does not hold: every one is refused on every pass and
            // none ever leaves the expired query, so without the refused set they fill the whole batch.
            SeedContainer(OwnerAccount, AccountVaultStore.VaultContainerKind);
            SeedContainer(OwnerAccount, AccountVaultStore.BarrelContainerKind);

            for (var i = 0; i < AccountVaultBarrelReaper.MaxRowsPerPass; i++)
            {
                Assert.IsTrue(backend.AddAccountVaultBarrel(new AccountVaultBarrel
                {
                    AccountId = OwnerAccount,
                    Wcid = 9001,
                    ItemGuid = FakeVaultWorld.NextGuid(),
                    Count = 1,
                    ItemName = "Long Gone",
                    BarreledAt = DateTime.UtcNow.AddDays(-90).AddMinutes(i),
                }));
            }

            var behind = SeedColdAccount(FirstAccount, inventoriesLoading: false, ageDays: 40);

            var stores = new Dictionary<uint, AccountVaultStore>();
            var reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores), _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow), "pass 1 reads only the 200 refused rows");
            Assert.AreEqual(AccountVaultBarrelReaper.MaxRowsPerPass, reaper.PermanentlyRefusedCountForTest, "and learns every refusal");
            Assert.IsNull(backend.GetAccountVaultBarrel(behind.Row.Id).PurgedAt);

            Assert.AreEqual(1, reaper.RunOnce(DateTime.UtcNow), "pass 2 reaches the purgeable row behind them");
            Assert.IsNotNull(backend.GetAccountVaultBarrel(behind.Row.Id).PurgedAt);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == behind.Item.Guid.Full));
        }

        [TestMethod]
        public void Reaper_AVaultThatNeverLoads_StillPurgesTheBarrel_WarnsOnce_AndReachesTheNextAccount()
        {
            var stuck = SeedColdAccount(FirstAccount, inventoriesLoading: true, ageDays: 60);
            FakeVaultWorld.SetInventoryLoaded(stuck.Barrel, true);

            var next = SeedColdAccount(FirstAccount + 1, inventoriesLoading: false, ageDays: 40);

            var stores = new Dictionary<uint, AccountVaultStore>();
            var reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores), _ => { });

            using (var capture = new MarketLogCapture())
            {
                bool IsUnsettledWarn(log4net.Core.LoggingEvent e) =>
                    e.Level == log4net.Core.Level.Warn && e.RenderedMessage.Contains("did not settle") && e.RenderedMessage.Contains($"account {FirstAccount}'s");

                Assert.AreEqual(2, reaper.RunOnce(DateTime.UtcNow), "the stuck account's barrel purges AND the next account is reached");
                Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == stuck.Item.Guid.Full));
                Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == next.Item.Guid.Full));
                Assert.AreEqual(1, capture.Events.Count(IsUnsettledWarn), "one WARN names the account whose load did not settle");

                // An hour later the store has been retired, so the next pass loads it again - and its vault
                // still never arrives. That repeat does not repeat the WARN.
                Assert.IsTrue(stores[FirstAccount].TryEvict(ACE.Common.Time.GetUnixTime() + 3600));
                SeedBarreledItem(FirstAccount, stuck.Barrel, DateTime.UtcNow.AddDays(-31));

                Assert.AreEqual(1, reaper.RunOnce(DateTime.UtcNow));
                Assert.AreEqual(1, capture.Events.Count(IsUnsettledWarn), "deduplicated");
            }
        }

        [TestMethod]
        public void Reaper_TheStampCannotInterleaveWithARestoreOfTheSameRow()
        {
            // The stamp runs inside the queued closure. A restore enqueued from another thread WHILE the
            // stamp is being written must wait for the closure to finish, so it sees the row purged.
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);
            var (item, row) = SeedBarreledItem(barrelContainer, DateTime.UtcNow.AddDays(-31));

            var store = NewColdStore();

            System.Threading.Tasks.Task<bool> restore = null;
            bool? restoreFinishedDuringStamp = null;

            backend.BeforeUpdateBarrel = updating =>
            {
                if (restore != null || updating.Id != row.Id)
                    return;

                restore = System.Threading.Tasks.Task.Run(() => Restore(store, row.Id, out _));
                restoreFinishedDuringStamp = restore.Wait(200);
            };

            Assert.AreEqual(1, ReaperFor(store, new List<TimeSpan>()).RunOnce(DateTime.UtcNow));

            Assert.IsNotNull(restore, "precondition: the restore was attempted during the stamp");
            Assert.IsTrue(restore.Wait(TimeSpan.FromSeconds(10)), "the restore completes once the closure has");
            Assert.AreEqual(false, restoreFinishedDuringStamp, "the restore cannot run between the destroy and the stamp");
            Assert.IsFalse(restore.Result, "it then refuses: the row is already purged");

            var after = backend.GetAccountVaultBarrel(row.Id);
            Assert.IsNotNull(after.PurgedAt);
            Assert.IsNull(after.RestoredAt);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
        }

        [TestMethod]
        public void Reaper_ALedgerRowRestoredAfterTheSweepReadIt_IsNotStamped()
        {
            // A ledger row has no biota, so the purge is stamp-only - and a stamp written from a stale
            // snapshot would overwrite the restore. The re-read guard runs before the ledger short-cut.
            backend.ExpiredReadReturnsCopies = true;

            SeedContainer(AccountVaultStore.VaultContainerKind);

            backend.Stacks.Add(new AccountVaultStack { Id = 7171, AccountId = OwnerAccount, Wcid = 9001, Count = 50 });

            var store = NewColdStore();

            Assert.IsTrue(Barrel(store, store.GetEntries(0, -1).Single(), 20, out var why), why);

            var row = backend.GetAccountVaultBarrels(OwnerAccount, 10).Single();
            Assert.IsNull(row.ItemGuid, "precondition: a ledger row");
            row.BarreledAt = DateTime.UtcNow.AddDays(-31);

            var raced = false;

            var reaper = new AccountVaultBarrelReaper(backend, _ =>
            {
                if (!raced)
                {
                    raced = true;
                    Assert.IsTrue(Restore(store, row.Id, out var r), r);
                }

                return store;
            }, _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));

            Assert.IsTrue(raced);

            var after = backend.GetAccountVaultBarrel(row.Id);
            Assert.IsNotNull(after.RestoredAt, "the restore stamp survives");
            Assert.IsNull(after.PurgedAt, "and the stale row is not stamped purged");
        }

        [TestMethod]
        public void IsWarm_WhileAnotherThreadHoldsALoadOpen_AnswersNotWarm()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);

            var store = NewColdStore();

            using var entered = new System.Threading.ManualResetEventSlim(false);
            using var release = new System.Threading.ManualResetEventSlim(false);

            // Holds the load open INSIDE stateLock: IsLoaded takes the lock, then reads the containers.
            world.BeforeLoadContainer = _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };

            var loader = System.Threading.Tasks.Task.Run(() => store.IsLoaded);

            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the load is open");

                var warm = store.IsWarm;

                Assert.IsFalse(warm, "a contended store answers not warm");
            }
            finally
            {
                release.Set();
            }

            Assert.IsTrue(loader.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(loader.Result);
            Assert.IsTrue(store.IsWarm, "and warm once the load is done");
        }

        [TestMethod]
        public void Reaper_AnAccountThatKeepsFailing_RotatesBehindAFreshAccountUnderTheCap()
        {
            // As many stuck cold accounts as the cap allows, oldest, plus one fresh account behind them.
            var stuck = new List<uint>();

            for (var i = 0; i < AccountVaultBarrelReaper.MaxColdLoadsPerPass; i++)
            {
                var id = FirstAccount + (uint)i;
                SeedColdAccount(id, inventoriesLoading: true, ageDays: 90 - i);
                stuck.Add(id);
            }

            var fresh = SeedColdAccount(FirstAccount + 100, inventoriesLoading: false, ageDays: 40);

            var stores = new Dictionary<uint, AccountVaultStore>();
            var reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores), _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow), "pass 1 spends the cap on the stuck accounts");
            Assert.IsFalse(stores.ContainsKey(FirstAccount + 100) && stores[FirstAccount + 100].IsIndexLoaded, "the fresh account was not reached");

            // An hour later the idle sweep has retired every stuck store, so they are cold again.
            foreach (var id in stuck)
                Assert.IsTrue(stores[id].TryEvict(ACE.Common.Time.GetUnixTime() + 3600));

            Assert.AreEqual(1, reaper.RunOnce(DateTime.UtcNow), "pass 2 serves the fresh account first");
            Assert.IsNotNull(backend.GetAccountVaultBarrel(fresh.Row.Id).PurgedAt);
        }

        // ---- review round 3: the real load bound, and the refused set ----

        [TestMethod]
        public void Reaper_LoadsThatNeverSettle_AreBoundedPerPass_NotAcrossPasses()
        {
            // Every load is stuck. Pass 1 starts exactly the cap. Pass 2 does not load a stuck store that
            // is still resident again, but it does start loads on the cold accounts pass 1 never reached,
            // so the number left unsettled is bounded per pass and not overall.
            const int extra = 5;
            var count = AccountVaultBarrelReaper.MaxColdLoadsPerPass + extra;

            for (var i = 0; i < count; i++)
                SeedColdAccount(FirstAccount + (uint)i, inventoriesLoading: true, ageDays: 90 - i);

            var stores = new Dictionary<uint, AccountVaultStore>();
            var reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores), _ => { });

            int Unsettled() => stores.Values.Count(s => s.GetRetentionLoadState(out _) == AccountVaultStore.RetentionLoadState.Unsettled);

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(AccountVaultBarrelReaper.MaxColdLoadsPerPass * 2, world.LoadContainerCalls, "pass 1 starts exactly the cap");
            Assert.AreEqual(AccountVaultBarrelReaper.MaxColdLoadsPerPass, Unsettled(), "and leaves every one of them unsettled");

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(count * 2, world.LoadContainerCalls, "pass 2 loads only the accounts pass 1 did not reach; no resident stuck store is loaded twice");
            Assert.AreEqual(count, Unsettled(), "so after two passes more than the cap are unsettled: the bound is per pass");
        }

        [TestMethod]
        public void Reaper_ARefusedRowThatIsRepaired_IsRetriedOnTheClearingPass()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            var barrelContainer = SeedContainer(AccountVaultStore.BarrelContainerKind);

            // The row names an item the barrel does not hold yet: refused for a reason that does not heal.
            var item = FakeVaultWorld.MakeStack(9001, 1, 100);

            var row = new AccountVaultBarrel
            {
                AccountId = OwnerAccount,
                Wcid = 9001,
                ItemGuid = item.Guid.Full,
                Count = 1,
                ItemName = item.Name,
                BarreledAt = DateTime.UtcNow.AddDays(-40),
            };

            Assert.IsTrue(backend.AddAccountVaultBarrel(row));

            var store = NewColdStore();
            var reaper = new AccountVaultBarrelReaper(backend, _ => store, _ => { });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow), "pass 1 refuses the row");
            Assert.AreEqual(1, reaper.PermanentlyRefusedCountForTest, "and remembers it");

            // An operator repairs it: the item is now in the barrel.
            Assert.IsTrue(barrelContainer.TryAddToInventory(item));

            for (var pass = 2; pass < AccountVaultBarrelReaper.RefusedResetPasses; pass++)
            {
                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow), $"pass {pass} still filters the remembered row");
                Assert.IsNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
            }

            Assert.AreEqual(1, reaper.RunOnce(DateTime.UtcNow), "the clearing pass retries it, and the repaired row purges");
            Assert.AreEqual(0, reaper.PermanentlyRefusedCountForTest);
            Assert.IsNotNull(backend.GetAccountVaultBarrel(row.Id).PurgedAt);
            Assert.IsTrue(world.Destroyed.Any(o => o.Guid.Full == item.Guid.Full));
        }

        [TestMethod]
        public void Reaper_TheRefusedSet_StopsGrowingAtItsCap_AndNoRequestPassesTheDaoCeiling()
        {
            SeedContainer(AccountVaultStore.VaultContainerKind);
            SeedContainer(AccountVaultStore.BarrelContainerKind);

            // More refusable rows than the cap plus a pass can ever hold.
            var rows = AccountVaultBarrelReaper.MaxRowsPerRead + 100;

            for (var i = 0; i < rows; i++)
            {
                Assert.IsTrue(backend.AddAccountVaultBarrel(new AccountVaultBarrel
                {
                    AccountId = OwnerAccount,
                    Wcid = 9001,
                    ItemGuid = FakeVaultWorld.NextGuid(),
                    Count = 1,
                    ItemName = "Long Gone",
                    BarreledAt = DateTime.UtcNow.AddDays(-90).AddMinutes(i),
                }));
            }

            var store = NewColdStore();
            var reaper = new AccountVaultBarrelReaper(backend, _ => store, _ => { });

            // Enough passes to reach the cap and go past it, and fewer than the periodic clear.
            var passes = AccountVaultBarrelReaper.MaxRefusedRows / AccountVaultBarrelReaper.MaxRowsPerPass + 2;
            Assert.IsTrue(passes < AccountVaultBarrelReaper.RefusedResetPasses, "precondition: no clear in this run");

            for (var pass = 0; pass < passes; pass++)
                Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));

            Assert.AreEqual(AccountVaultBarrelReaper.MaxRefusedRows, reaper.PermanentlyRefusedCountForTest, "the set stops at its cap");
            Assert.IsTrue(backend.ExpiredReadLimits.All(l => l <= AccountVaultBarrelReaper.MaxRowsPerRead), $"no request passes the DAO ceiling: {string.Join(", ", backend.ExpiredReadLimits)}");
            Assert.AreEqual(AccountVaultBarrelReaper.MaxRowsPerRead, backend.ExpiredReadLimits.Last(), "precondition: the request did reach the ceiling");
            Assert.AreEqual(0, world.Destroyed.Count);
        }

        // ---- review round: shutdown ----

        [TestMethod]
        public void Reaper_StopRequestedDuringAWait_StartsNoFurtherPurge()
        {
            // The barrel is ready but the vault is still loading, so without the stop check the reaper
            // would wait out its budget and then purge.
            var first = SeedColdAccount(FirstAccount, inventoriesLoading: true, ageDays: 60);
            FakeVaultWorld.SetInventoryLoaded(first.Barrel, true);

            var second = SeedColdAccount(FirstAccount + 1, inventoriesLoading: false, ageDays: 40);

            var stores = new Dictionary<uint, AccountVaultStore>();
            var waits = 0;
            AccountVaultBarrelReaper reaper = null;

            reaper = new AccountVaultBarrelReaper(backend, StoreTable(stores), _ =>
            {
                waits++;
                reaper.RequestStop();
            });

            Assert.AreEqual(0, reaper.RunOnce(DateTime.UtcNow));
            Assert.AreEqual(1, waits, "the wait loop stops at the next check");
            Assert.AreEqual(0, world.Destroyed.Count, "no purge starts once stopping");
            Assert.IsFalse(stores.ContainsKey(FirstAccount + 1), "and the next account is never touched");
            Assert.IsNull(backend.GetAccountVaultBarrel(first.Row.Id).PurgedAt);
            Assert.IsNull(backend.GetAccountVaultBarrel(second.Row.Id).PurgedAt);
        }
    }
}

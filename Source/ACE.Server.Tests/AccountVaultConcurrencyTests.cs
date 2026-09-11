using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor risk R3: two vendor windows on ONE store, racing.
    ///
    /// This is the case the sharing feature introduces and that retail never faced, because retail
    /// vendor stock is per-vendor and in-memory. The store's per-store mutation queue is the in-memory
    /// half of the mitigation; the ledger's guarded UPDATE is the database half.
    ///
    /// Each test here is built so that it would FAIL if the queue were removed, not merely observe
    /// that two tasks completed:
    ///
    /// - the fake ledger applies its delta as a NON-atomic read-modify-write with a deliberate sleep
    ///   between the two halves, so an unserialized pair of withdrawals really does overdraw;
    /// - the fake world source records the maximum number of calls in flight at once, so any overlap
    ///   at all shows up as a value above 1.
    /// </summary>
    [TestClass]
    public class AccountVaultConcurrencyTests
    {
        private const uint OwnerAccount = 4101;
        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, 0x50000011, "Vaultowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));

            // Seeded so PropertyManager's cache answers it. AccountVaultStore.ReservedVaultLocation
            // reads this key on the CreateVault path, and an uncached PropertyManager.GetLong falls
            // through to DatabaseManager.ShardConfig, which opens a ShardDbContext - so without this an
            // ordinary unit test needs a live MySQL, and only if this class happens to run after the one
            // class that seeds the key.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));
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
                Id = (uint)(9100 + order),
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
            });

            for (var i = 0; i < itemCount; i++)
                Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeStack(8000, 1, 100)));

            return container;
        }

        /// <summary>
        /// Starts every action on its own thread and releases them together, so they contend rather
        /// than running one after the other by accident.
        /// </summary>
        private static void Race(params Action[] actions)
        {
            using (var gate = new ManualResetEventSlim(false))
            {
                var tasks = actions.Select(a => Task.Run(() => { gate.Wait(); a(); })).ToArray();

                // Give every task a moment to reach the gate before opening it.
                Thread.Sleep(20);
                gate.Set();

                Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), "the racing mutations deadlocked");
            }
        }

        [TestMethod]
        public void TwoWindows_RacingOneWithdraw_ProduceExactlyOneSuccess()
        {
            var vault = SeedVault(capacity: 4, itemCount: 1, order: 1);

            // Widen every world-layer call, so a store that stopped serializing shows overlap.
            world.CallDelayMs = 25;

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => !e.IsLedger);

            var results = new bool[2];
            var got = new List<WorldObject>[2];
            var deposited = false;

            // The third racer is a DEPOSIT, and it is what makes the overlap assertion at the bottom
            // load-bearing. Two withdrawals of the SAME item cannot overlap in the world layer however
            // badly the store behaves: the loser is turned away by the inventory re-resolve before it
            // ever calls out, so only one of them reaches world.SaveBiota and MaxConcurrentCalls stays
            // at 1 even with the queue removed entirely. A deposit runs IsPristine plus two saves on a
            // thread that is genuinely allowed in, so with the queue removed it really does run beside
            // the winning withdraw and the assertion fails - which is the only thing that makes it a
            // test of the mechanism rather than of the guard.
            Race(
                () => store.Enqueue(() => results[0] = store.TryWithdraw(entry, 1, Owner, out got[0], out _)),
                () => store.Enqueue(() => results[1] = store.TryWithdraw(entry, 1, Owner, out got[1], out _)),
                () => store.Enqueue(() => deposited = store.TryDeposit(FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out _)));

            Assert.AreEqual(1, results.Count(r => r), "exactly one of the two windows may take the item");

            var handedOver = (got[0]?.Count ?? 0) + (got[1]?.Count ?? 0);
            Assert.AreEqual(1, handedOver, "the item must be handed over exactly once - two would be a dupe");

            Assert.IsTrue(deposited, "the deposit racer must succeed, or it is not exercising the world layer");

            Assert.AreEqual(1, vault.Inventory.Count, "one item left and one arrived, in either order");
            Assert.AreEqual(1, world.MaxConcurrentCalls, "the store's mutations must never overlap");
        }

        [TestMethod]
        public void TwoWindows_RacingLedgerWithdraws_NeverOverdraw()
        {
            backend.Stacks.Add(new AccountVaultStack { Id = 1, AccountId = OwnerAccount, Wcid = 1234, Count = 10 });

            // The read-modify-write window the queue has to close. Without serialization both threads
            // read 10, both pass the non-negative guard, and both write 2 while 16 units are handed
            // out.
            backend.LedgerAdjustDelayMs = 25;

            var store = NewStore();

            var entry = store.GetEntries(0, int.MaxValue).Single(e => e.IsLedger);

            var results = new bool[2];
            var got = new List<WorldObject>[2];

            Race(
                () => store.Enqueue(() => results[0] = store.TryWithdraw(entry, 8, Owner, out got[0], out _)),
                () => store.Enqueue(() => results[1] = store.TryWithdraw(entry, 8, Owner, out got[1], out _)));

            Assert.AreEqual(1, results.Count(r => r), "10 units cannot satisfy two withdrawals of 8");

            var units = (got[0]?.Sum(w => w.StackSize ?? 1) ?? 0) + (got[1]?.Sum(w => w.StackSize ?? 1) ?? 0);
            Assert.AreEqual(8, units, "exactly one withdrawal's worth of units may leave the store");

            Assert.AreEqual(2, backend.Stacks.Single(s => s.Wcid == 1234).Count);

            Assert.IsFalse(backend.LedgerCountHistory.Any(c => c < 0), "the ledger must never hold a negative count");
        }

        [TestMethod]
        public void Mutations_RunSerially_EvenWhenEnqueuedFromManyThreads()
        {
            var store = NewStore();

            const int threads = 8;
            const int perThread = 16;

            var inside = 0;
            var overlaps = 0;

            // Deliberately NOT interlocked: if the queue serialized nothing, this count would come out
            // short as well as tripping the overlap detector.
            var completed = 0;

            using (var gate = new ManualResetEventSlim(false))
            {
                var tasks = new Task[threads];

                for (var t = 0; t < threads; t++)
                {
                    tasks[t] = Task.Run(() =>
                    {
                        gate.Wait();

                        for (var i = 0; i < perThread; i++)
                        {
                            store.Enqueue(() =>
                            {
                                if (Interlocked.Increment(ref inside) != 1)
                                    Interlocked.Increment(ref overlaps);

                                var seen = completed;
                                Thread.Sleep(1);
                                completed = seen + 1;

                                Interlocked.Decrement(ref inside);
                            });
                        }
                    });
                }

                Thread.Sleep(20);
                gate.Set();

                Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)), "the mutation queue deadlocked");
            }

            Assert.AreEqual(0, overlaps, "two queued mutations must never run at the same time");
            Assert.AreEqual(threads * perThread, completed, "every enqueued mutation must run exactly once");
        }

        [TestMethod]
        public void Enqueue_FromInsideQueuedWork_DoesNotRunTheInnerItemFirst()
        {
            var store = NewStore();

            var order = new List<string>();

            store.Enqueue(() =>
            {
                order.Add("outer-start");

                // The drain lock is a monitor and therefore re-entrant, so before the fix this
                // re-entered Drain and ran the inner item to completion right here - a later mutation
                // finishing in the middle of an earlier one, which is the thing the queue exists to
                // make impossible.
                store.Enqueue(() => order.Add("inner"));

                order.Add("outer-end");
            });

            CollectionAssert.AreEqual(
                new[] { "outer-start", "inner", "outer-end" },
                order,
                "the inner item runs inline, so it may not interleave with anything the outer item has not finished");

            Assert.AreEqual(0, store.QueuedWork);
        }

        [TestMethod]
        public void Enqueue_OnAnEvictedStore_IsRefusedWithoutRunningTheWork()
        {
            var store = NewStore();

            // Control first: a live store accepts work, so the refusal below is caused by the
            // eviction and not by Enqueue being inert.
            var ranBefore = false;
            Assert.IsTrue(store.Enqueue(() => ranBefore = true));
            Assert.IsTrue(ranBefore);

            Assert.IsTrue(store.TryEvict(store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1),
                "an idle store with no windows and no queued work must be evictable");

            Assert.IsTrue(store.IsEvicted);

            var ranAfter = false;

            // A caller holding a store reference from before the sweep. Letting it mutate this store
            // would put a second mutation queue over the same account once GetStore built the
            // replacement, which is R3 reopened.
            Assert.IsFalse(store.Enqueue(() => ranAfter = true), "a retired store must refuse work");
            Assert.IsFalse(ranAfter, "refused work must not have run");
        }

        [TestMethod]
        public void TryEvict_LeavesAStoreThatIsStillInUse()
        {
            var store = NewStore();

            var future = store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1;

            store.AddWindow();
            Assert.IsFalse(store.TryEvict(future), "a store with an open vendor window is still being read from");

            store.RemoveWindow();
            Assert.IsFalse(store.TryEvict(store.LastTouchedUnixTime), "a store touched just now is not idle");

            Assert.IsTrue(store.TryEvict(store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));
        }

        [TestMethod]
        public void GetStore_NeverHandsBackAnEvictedStore()
        {
            const uint account = 41010177;

            var first = AccountVaultManager.GetStore(account);

            Assert.IsTrue(first.TryEvict(first.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));

            var second = AccountVaultManager.GetStore(account);

            Assert.AreNotSame(first, second, "a retired store must be replaced, not handed out again");
            Assert.IsFalse(second.IsEvicted);
        }
    }
}

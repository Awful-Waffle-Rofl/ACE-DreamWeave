using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// VaultPackDeposit: the pack-to-vault deposit the facet switch uses, driven against a REAL
    /// AccountVaultStore over FakeVaultBackend / FakeVaultWorld, with the Player-bound detach, flush and
    /// hand-back steps as recorded delegates (no Player can be built in this harness, so the one-line
    /// DepositFromPack wrapper is pinned by source in FacetVaultDepositSelectTests instead).
    ///
    /// The invariant, in both directions, is the one VaultBatchDepositTests pins for the store: an item is
    /// in the vault or back with the player, never both and never neither - except when the store itself
    /// cannot say (a throw at or after a credit), where the item is WITHHELD rather than handed back.
    /// </summary>
    [TestClass]
    public class VaultPackDepositTests
    {
        private const uint OwnerAccount = 7701;
        private const uint OwnerCharacter = 0x50000701;
        private const uint GranteeAccount = 7702;
        private const uint GranteeCharacter = 0x50000702;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Facetowner");
        private static readonly VaultActor Grantee = new VaultActor(GranteeAccount, GranteeCharacter, "Facetgrantee");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // Every key this class reads is seeded here and restored in Cleanup (PropertyManager's caches are
            // process-static and shared with every other class in the run).
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();

            // Pristine items collapse to ledger rows and are destroyed, so "in the vault" is observable as
            // world.Destroyed plus the ledger count, without a vault container.
            world.PristineResult = true;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyLong("account_vault_landblock", DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
            PropertyManager.ModifyBool("account_vault_salvage_combine", DefaultPropertyManager.DefaultBooleanProperties["account_vault_salvage_combine"].Item);
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        private long LedgerCount(uint wcid) => backend.Stacks.Where(s => s.AccountId == OwnerAccount && s.Wcid == wcid).Sum(s => s.Count);

        private void SeedLedger(uint wcid, long count) =>
            backend.Stacks.Add(new AccountVaultStack { Id = (uint)(9900 + backend.Stacks.Count), AccountId = OwnerAccount, Wcid = wcid, Count = count });

        /// <summary>Records every delegate call, through concurrent collections so a racing test can use it too.</summary>
        private sealed class Recorder
        {
            public readonly ConcurrentQueue<string> Order = new ConcurrentQueue<string>();
            public readonly ConcurrentBag<WorldObject> Detached = new ConcurrentBag<WorldObject>();
            public readonly ConcurrentBag<WorldObject> HandedBack = new ConcurrentBag<WorldObject>();
            public int Flushes;
            public int Refetches;

            public HashSet<uint> FailDetach = new HashSet<uint>();
            public bool FailHandBack;

            /// <summary>Guids whose detach THROWS (review F4).</summary>
            public HashSet<uint> ThrowDetach = new HashSet<uint>();

            /// <summary>Guids whose hand-back is recorded and then THROWS (review F4).</summary>
            public HashSet<uint> ThrowHandBack = new HashSet<uint>();

            public readonly ConcurrentBag<WorldObject> DetachAttempts = new ConcurrentBag<WorldObject>();

            public bool Detach(WorldObject item)
            {
                Order.Enqueue("detach");
                DetachAttempts.Add(item);

                if (ThrowDetach.Contains(item.Guid.Full))
                    throw new InvalidOperationException($"fake detach failure for 0x{item.Guid.Full:X8}");

                if (FailDetach.Contains(item.Guid.Full))
                    return false;

                Detached.Add(item);
                return true;
            }

            public void Flush()
            {
                Order.Enqueue("flush");
                Interlocked.Increment(ref Flushes);
            }

            public bool HandBack(WorldObject item)
            {
                Order.Enqueue("handback");
                HandedBack.Add(item);

                if (ThrowHandBack.Contains(item.Guid.Full))
                    throw new InvalidOperationException($"fake hand-back failure for 0x{item.Guid.Full:X8}");

                return !FailHandBack;
            }
        }

        private VaultPackDepositResult Run(AccountVaultStore store, IReadOnlyList<WorldObject> items, Recorder rec, Func<AccountVaultStore> refetch = null)
        {
            return VaultPackDeposit.DepositAndDispose(store, () => { Interlocked.Increment(ref rec.Refetches); return refetch?.Invoke(); },
                items, Owner, rec.Detach, rec.Flush, rec.HandBack);
        }

        private static List<WorldObject> Items(params uint[] wcids) => wcids.Select(w => (WorldObject)FakeVaultWorld.MakeStack(w, 1, 100)).ToList();

        private static VaultPackDepositStatus StatusOf(VaultPackDepositResult result, WorldObject item) =>
            result.Items.Single(r => ReferenceEquals(r.Item, item)).Status;

        // ------------------------------------------------------------------ the happy path

        [TestMethod]
        public void AllCommitted_NoneHandedBack()
        {
            var store = NewStore();
            var items = Items(7711, 7712, 7713);
            var rec = new Recorder();

            var result = Run(store, items, rec);

            Assert.IsNull(result.Thrown);
            Assert.IsFalse(result.StoreUnavailable);
            Assert.IsTrue(result.Items.All(r => r.Status == VaultPackDepositStatus.Deposited), "every item must deposit");
            Assert.AreEqual(0, rec.HandedBack.Count, "nothing is handed back after a clean deposit");
            Assert.AreEqual(3, world.Destroyed.Count, "each pristine item is credited and destroyed");
            Assert.AreEqual(1L, LedgerCount(7711));
            Assert.AreEqual(1, backend.StackBatchCalls, "ONE batch call for the whole set, as PersonalVendor.DepositItems");
        }

        /// <summary>CATCHES: the flush moved after the batch, which would let a pack save land after the vault's delete.</summary>
        [TestMethod]
        public void DetachSaves_AreFlushedBeforeTheBatchRuns()
        {
            var store = NewStore();
            var rec = new Recorder();

            backend.OnBeforeStackBatch = () => rec.Order.Enqueue("batch");

            Run(store, Items(7714, 7715), rec);

            var order = rec.Order.ToList();

            Assert.AreEqual(1, rec.Flushes, "flushed exactly once");
            Assert.IsTrue(order.IndexOf("flush") >= 0 && order.IndexOf("flush") < order.IndexOf("batch"),
                $"the detach saves must be flushed before the batch: {string.Join(",", order)}");
            Assert.IsTrue(order.LastIndexOf("detach") < order.IndexOf("flush"), "every detach precedes the flush");
        }

        // ------------------------------------------------------------------ refusals

        [TestMethod]
        public void CapRefusesTheThirdItem_WhichIsHandedBackExactlyOnce()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));

            var store = NewStore();
            var items = Items(7721, 7722, 7723);
            var rec = new Recorder();

            var result = Run(store, items, rec);

            Assert.AreEqual(VaultPackDepositStatus.Deposited, StatusOf(result, items[0]));
            Assert.AreEqual(VaultPackDepositStatus.Deposited, StatusOf(result, items[1]));
            Assert.AreEqual(VaultPackDepositStatus.HandedBack, StatusOf(result, items[2]));
            Assert.IsTrue(AccountVaultStore.IsFullMessage(result.Items[2].FailReason), $"the cap reason travels with the item: {result.Items[2].FailReason}");

            Assert.AreEqual(1, rec.HandedBack.Count(i => ReferenceEquals(i, items[2])), "handed back exactly once");
            Assert.AreEqual(1, rec.HandedBack.Count, "and nothing else is");
            Assert.AreEqual(0L, LedgerCount(7723));
            CollectionAssert.DoesNotContain(world.Destroyed, items[2]);
        }

        [TestMethod]
        public void LedgerRefused_IsRefusedAndHandedBack()
        {
            backend.FailLedgerAdjustWcids.Add(7732);

            var store = NewStore();
            var items = Items(7731, 7732);
            var rec = new Recorder();

            var result = Run(store, items, rec);

            Assert.AreEqual(VaultPackDepositStatus.Deposited, StatusOf(result, items[0]));
            Assert.AreEqual(VaultPackDepositStatus.HandedBack, StatusOf(result, items[1]));
            Assert.AreEqual(VaultDepositCommit.None, result.Items[1].Commit, "a provable refusal is None");
            CollectionAssert.Contains(rec.HandedBack.ToList(), items[1]);
            CollectionAssert.DoesNotContain(world.Destroyed, items[1]);
        }

        [TestMethod]
        public void DetachFails_TheItemIsNeverSubmitted()
        {
            var store = NewStore();
            var items = Items(7741, 7742);
            var rec = new Recorder();

            rec.FailDetach.Add(items[1].Guid.Full);

            var result = Run(store, items, rec);

            Assert.AreEqual(VaultPackDepositStatus.Deposited, StatusOf(result, items[0]));
            Assert.AreEqual(VaultPackDepositStatus.NotDetached, StatusOf(result, items[1]));
            Assert.AreEqual(0L, LedgerCount(7742), "an item that never came off the player must never reach the vault");
            Assert.AreEqual(0, rec.HandedBack.Count, "and is not handed back either - it never left");
        }

        [TestMethod]
        public void HandBackFails_IsReportedAsHandBackFailed()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = NewStore();
            var items = Items(7751, 7752);
            var rec = new Recorder { FailHandBack = true };

            var result = Run(store, items, rec);

            Assert.AreEqual(VaultPackDepositStatus.Deposited, StatusOf(result, items[0]));
            Assert.AreEqual(VaultPackDepositStatus.HandBackFailed, StatusOf(result, items[1]));
        }

        /// <summary>CATCHES: the already-parented guard dropped, which would put one object in two containers.</summary>
        [TestMethod]
        public void AnItemStillParented_IsNeverHandedBack()
        {
            var store = NewStore();
            var items = Items(7756);
            var rec = new Recorder();

            // A detach that reported success but left the item parented: the store refuses it (still parented),
            // and the hand-back must then refuse too.
            items[0].ContainerId = 0x50000799;

            var result = Run(store, items, rec);

            Assert.AreEqual(VaultPackDepositStatus.AlreadyParented, StatusOf(result, items[0]));
            Assert.AreEqual(0, rec.HandedBack.Count, "an item that is already somewhere is not placed again");
            Assert.AreEqual(0L, LedgerCount(7756));
        }

        // ------------------------------------------------------------------ throws

        [TestMethod]
        public void CreditThrows_EveryItemIsUnknownAndNoneIsHandedBack()
        {
            backend.ThrowFromStackBatchAfterGroups = 1;

            var store = NewStore();
            var items = Items(7761, 7762, 7763);
            var rec = new Recorder();

            var result = Run(store, items, rec);

            Assert.IsNotNull(result.Thrown, "sanity: the credit really threw");
            Assert.IsTrue(result.Items.All(r => r.Status == VaultPackDepositStatus.Withheld), "a throw out of the credit withholds every item");
            Assert.IsTrue(result.Items.All(r => r.Commit == VaultDepositCommit.Unknown));
            Assert.AreEqual(0, rec.HandedBack.Count, "nobody knows which groups landed, so nothing may be handed back");
        }

        [TestMethod]
        public void DestroyPhaseThrows_CommittedItemsStayWithTheVault()
        {
            var store = NewStore();
            var items = Items(7771, 7771, 7771);
            var rec = new Recorder();

            world.BeforeDestroyItem = item =>
            {
                if (ReferenceEquals(item, items[1]))
                    throw new InvalidOperationException("fake teardown failure in the destroy phase");
            };

            var result = Run(store, items, rec);

            Assert.IsNotNull(result.Thrown, "sanity: the destroy phase really threw");
            Assert.IsTrue(result.Items.All(r => r.Status == VaultPackDepositStatus.Withheld && r.Commit == VaultDepositCommit.Committed),
                "every item of a committed group is the vault's, including the ones the destroy never reached");
            Assert.AreEqual(0, rec.HandedBack.Count, "handing back a credited item is a duplicate");
            Assert.AreEqual(3L, LedgerCount(7771), "the credit did land");
        }

        /// <summary>
        /// THE PER-ITEM THROW RULE: a throw withholds Committed items but hands back a None one. CATCHES both the
        /// over-cautious variant (withhold everything: the None item is then LOST, detached and in no container)
        /// and the reckless one (hand everything back: a duplicate).
        /// </summary>
        [TestMethod]
        public void ThrowAfterAPartialBatch_HandsBackOnlyTheProvablyUntouchedItem()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));

            var store = NewStore();
            var items = Items(7781, 7782, 7783);
            var rec = new Recorder();

            world.BeforeDestroyItem = item =>
            {
                if (ReferenceEquals(item, items[0]))
                    throw new InvalidOperationException("fake teardown failure in the destroy phase");
            };

            var result = Run(store, items, rec);

            Assert.IsNotNull(result.Thrown, "sanity: the destroy phase really threw");
            Assert.AreEqual(VaultPackDepositStatus.Withheld, StatusOf(result, items[0]));
            Assert.AreEqual(VaultPackDepositStatus.Withheld, StatusOf(result, items[1]));
            Assert.AreEqual(VaultPackDepositStatus.HandedBack, StatusOf(result, items[2]), "the cap-refused item is at None and must go home");
            CollectionAssert.AreEquivalent(new[] { items[2] }, rec.HandedBack.ToList());
        }

        // ------------------------------------------------------------------ store availability

        [TestMethod]
        public void RetiredStore_RetriesOnceOnAFreshStore()
        {
            var retired = NewStore();
            Assert.IsTrue(retired.TryEvict(retired.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));

            var items = Items(7791, 7792);
            var rec = new Recorder();

            var result = Run(retired, items, rec, () => NewStore());

            Assert.AreEqual(1, rec.Refetches, "fetched again exactly once");
            Assert.IsFalse(result.StoreUnavailable);
            Assert.IsTrue(result.Items.All(r => r.Status == VaultPackDepositStatus.Deposited), "the retry must run the whole batch");
            Assert.AreEqual(0, rec.HandedBack.Count);
        }

        [TestMethod]
        public void RetiredStore_RetiredAgain_HandsEverythingBack()
        {
            var retired = NewStore();
            Assert.IsTrue(retired.TryEvict(retired.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));

            var items = Items(7793, 7794);
            var rec = new Recorder();

            var result = Run(retired, items, rec, () => retired);

            Assert.AreEqual(1, rec.Refetches, "one retry only");
            Assert.IsTrue(result.StoreUnavailable);
            Assert.IsTrue(result.Items.All(r => r.Status == VaultPackDepositStatus.HandedBack), "nothing ran, so every item is provably the player's");
            Assert.AreEqual(2, rec.HandedBack.Count);
            Assert.AreEqual(0, backend.StackBatchCalls, "the batch never reached the backend");
        }

        [TestMethod]
        public void NoStore_HandsEverythingBack()
        {
            var items = Items(7795);
            var rec = new Recorder();

            var result = Run(null, items, rec);

            Assert.IsTrue(result.StoreUnavailable);
            Assert.AreEqual(VaultPackDepositStatus.HandedBack, StatusOf(result, items[0]));
        }

        // ------------------------------------------------------------------ conservation over every fault

        /// <summary>
        /// One fault per row. For every row: each submitted item has exactly one status; an item is in the vault
        /// (destroyed after a committed credit), handed back, withheld, or never left the player - never two of
        /// those; and no handed-back item was ever destroyed or carried a Committed commit.
        /// </summary>
        [DataTestMethod]
        [DataRow("none")]
        [DataRow("cap")]
        [DataRow("ledgerRefused")]
        [DataRow("ledgerFailed")]
        [DataRow("creditThrows")]
        [DataRow("destroyThrows")]
        [DataRow("destroyThrowsWithCap")]
        [DataRow("detachFails")]
        [DataRow("handBackFails")]
        [DataRow("parented")]
        [DataRow("detachThrows")]
        [DataRow("handBackThrows")]
        [DataRow("retiredThenFresh")]
        [DataRow("retiredTwice")]
        [DataRow("noStore")]
        public void Conservation_EveryItemEndsUpInExactlyOnePlace(string fault)
        {
            var store = NewStore();
            var items = Items(7801, 7802, 7802, 7803, 7804);
            var rec = new Recorder();
            Func<AccountVaultStore> refetch = null;

            switch (fault)
            {
                case "cap":
                    Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));
                    break;
                case "ledgerRefused":
                    backend.FailLedgerAdjustWcids.Add(7802);
                    break;
                case "ledgerFailed":
                    backend.LedgerAdjustFailedWcids.Add(7803);
                    break;
                case "creditThrows":
                    backend.ThrowFromStackBatchAfterGroups = 2;
                    break;
                case "destroyThrows":
                    world.BeforeDestroyItem = i => { if (ReferenceEquals(i, items[2])) throw new InvalidOperationException("fake"); };
                    break;
                case "destroyThrowsWithCap":
                    Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));
                    world.BeforeDestroyItem = i => { if (ReferenceEquals(i, items[0])) throw new InvalidOperationException("fake"); };
                    break;
                case "detachFails":
                    rec.FailDetach.Add(items[1].Guid.Full);
                    break;
                case "handBackFails":
                    Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));
                    rec.FailHandBack = true;
                    break;
                case "parented":
                    items[3].ContainerId = 0x50000799;
                    break;
                case "detachThrows":
                    // The FIRST and a middle item throw: every later item must still be detached and deposited.
                    rec.ThrowDetach.Add(items[0].Guid.Full);
                    rec.ThrowDetach.Add(items[2].Guid.Full);
                    break;
                case "handBackThrows":
                    // Room for one entry, so items 1-4 are refused; the first two hand-backs throw, the rest must still run.
                    Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));
                    rec.ThrowHandBack.Add(items[1].Guid.Full);
                    rec.ThrowHandBack.Add(items[2].Guid.Full);
                    break;
                case "retiredThenFresh":
                    Assert.IsTrue(store.TryEvict(store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));
                    refetch = () => NewStore();
                    break;
                case "retiredTwice":
                    Assert.IsTrue(store.TryEvict(store.LastTouchedUnixTime + AccountVaultStore.IdleEvictionSeconds + 1));
                    refetch = () => store;
                    break;
                case "noStore":
                    store = null;
                    break;
            }

            var result = Run(store, items, rec, refetch);

            Assert.AreEqual(items.Count, result.Items.Count, $"{fault}: one result per item");
            Assert.IsTrue(result.Items.All(r => r != null), $"{fault}: exactly one result per item, none missing");

            switch (fault)
            {
                case "parented":
                    // Review F3: the parented item specifically, not just the totals.
                    Assert.AreEqual(VaultPackDepositStatus.AlreadyParented, result.Items[3].Status, "parented: the still-parented item is AlreadyParented");
                    Assert.IsFalse(rec.HandedBack.Any(h => ReferenceEquals(h, items[3])), "parented: and it was never handed back");
                    break;

                case "detachThrows":
                    Assert.AreEqual(VaultPackDepositStatus.NotDetached, result.Items[0].Status);
                    Assert.AreEqual(VaultPackDepositStatus.NotDetached, result.Items[2].Status);
                    Assert.AreEqual(items.Count, rec.DetachAttempts.Count, "detachThrows: every item's detach was attempted");
                    foreach (var i in new[] { 1, 3, 4 })
                        Assert.AreEqual(VaultPackDepositStatus.Deposited, result.Items[i].Status, $"detachThrows: item {i} after a throw still deposits");
                    break;

                case "handBackThrows":
                    Assert.AreEqual(VaultPackDepositStatus.Deposited, result.Items[0].Status);
                    Assert.AreEqual(VaultPackDepositStatus.HandBackFailed, result.Items[1].Status);
                    Assert.AreEqual(VaultPackDepositStatus.HandBackFailed, result.Items[2].Status);
                    Assert.AreEqual(VaultPackDepositStatus.HandedBack, result.Items[3].Status, "handBackThrows: a hand-back after a throwing one still runs");
                    Assert.AreEqual(VaultPackDepositStatus.HandedBack, result.Items[4].Status);
                    break;
            }

            for (var i = 0; i < items.Count; i++)
                Assert.AreSame(items[i], result.Items[i].Item, $"{fault}: results keep input order");

            var handed = rec.HandedBack.ToList();
            var destroyed = world.Destroyed.ToList();

            Assert.AreEqual(handed.Count, handed.Distinct().Count(), $"{fault}: no item is handed back twice");

            var byStatus = result.Items.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
            int Count(VaultPackDepositStatus s) => byStatus.TryGetValue(s, out var n) ? n : 0;

            Assert.AreEqual(items.Count,
                Count(VaultPackDepositStatus.Deposited) + Count(VaultPackDepositStatus.HandedBack) + Count(VaultPackDepositStatus.Withheld)
                + Count(VaultPackDepositStatus.NotDetached) + Count(VaultPackDepositStatus.HandBackFailed) + Count(VaultPackDepositStatus.AlreadyParented),
                $"{fault}: vault + handed back + withheld + never-left = submitted");

            Assert.AreEqual(Count(VaultPackDepositStatus.HandedBack) + Count(VaultPackDepositStatus.HandBackFailed), handed.Count,
                $"{fault}: the hand-back delegate ran exactly for the items reported handed back (or failed)");

            foreach (var r in result.Items)
            {
                var wasHanded = handed.Any(h => ReferenceEquals(h, r.Item));
                var wasDestroyed = destroyed.Any(d => ReferenceEquals(d, r.Item));

                Assert.IsFalse(wasHanded && wasDestroyed, $"{fault}: 0x{r.Item.Guid.Full:X8} is both in the vault and handed back");

                switch (r.Status)
                {
                    case VaultPackDepositStatus.Deposited:
                        Assert.IsTrue(wasDestroyed, $"{fault}: a deposited pristine item is credited and destroyed");
                        Assert.AreEqual(VaultDepositCommit.Committed, r.Commit);
                        break;

                    case VaultPackDepositStatus.HandedBack:
                    case VaultPackDepositStatus.HandBackFailed:
                        Assert.IsTrue(wasHanded);
                        Assert.AreNotEqual(VaultDepositCommit.Committed, r.Commit, $"{fault}: a committed item must never be handed back");
                        if (result.Thrown != null)
                            Assert.AreEqual(VaultDepositCommit.None, r.Commit, $"{fault}: after a throw only a None item goes home");
                        break;

                    case VaultPackDepositStatus.Withheld:
                        Assert.IsNotNull(result.Thrown, $"{fault}: only a throw withholds");
                        Assert.IsFalse(wasHanded);
                        break;

                    case VaultPackDepositStatus.NotDetached:
                    case VaultPackDepositStatus.AlreadyParented:
                        Assert.IsFalse(wasHanded);
                        Assert.IsFalse(wasDestroyed);
                        break;
                }
            }

            // Ledger units credited equal the items the result says are the vault's (Deposited, or Withheld as
            // Committed), for every row whose credit did not throw midway.
            if (fault != "creditThrows")
            {
                var credited = backend.Stacks.Where(s => s.AccountId == OwnerAccount).Sum(s => s.Count);
                var vaults = result.Items.Count(r => r.Status == VaultPackDepositStatus.Deposited
                                                  || (r.Status == VaultPackDepositStatus.Withheld && r.Commit == VaultDepositCommit.Committed));

                Assert.AreEqual((long)vaults, credited, $"{fault}: ledger units = items the vault owns");
            }
        }

        // ------------------------------------------------------------------ concurrency

        /// <summary>
        /// The facet deposit and a grantee's deposit, released together by a Barrier against a vault ONE entry below
        /// its cap, each bringing a brand-new wcid. The store's queue must admit exactly one: the cap is never
        /// exceeded, and each item ends up in exactly one place (the vault, the facet player's pack, or still the
        /// grantee's). Repeated in-process, and meant to be run 10-20 times alone as well.
        /// </summary>
        [TestMethod]
        public void Race_FacetDepositAndGranteeDeposit_AtOneBelowTheCap_NeverExceedTheCap()
        {
            const int cap = 4;
            const int iterations = 25;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                backend = new FakeVaultBackend();
                world = new FakeVaultWorld { PristineResult = true };

                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", cap));

                for (var s = 0; s < cap - 1; s++)
                    SeedLedger((uint)(7900 + s), 1);

                backend.Grants.Add(new AccountVaultGrant
                {
                    Id = 1,
                    OwnerAccountId = OwnerAccount,
                    GranteeCharacterGuid = GranteeCharacter,
                    GranteeCharacterName = Grantee.Name,
                    CanWithdraw = true,
                    GrantedAt = DateTime.UtcNow,
                });

                // Widen every world-layer call so an unserialized store would overlap.
                world.CallDelayMs = 5;

                var store = NewStore();

                var facetItem = FakeVaultWorld.MakeStack(7950, 1, 100);
                var granteeItem = FakeVaultWorld.MakeStack(7951, 1, 100);

                var rec = new Recorder();
                VaultPackDepositResult facetResult = null;
                var granteeOk = false;
                string granteeReason = null;

                using (var barrier = new Barrier(2))
                {
                    var facet = Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        facetResult = Run(store, new List<WorldObject> { facetItem }, rec);
                    });

                    var grantee = Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        store.Enqueue(() => granteeOk = store.TryDeposit(granteeItem, Grantee, out granteeReason));
                    });

                    Assert.IsTrue(Task.WaitAll(new[] { facet, grantee }, TimeSpan.FromSeconds(30)), $"iteration {iteration}: the race deadlocked");
                }

                var facetDeposited = facetResult.Items[0].Status == VaultPackDepositStatus.Deposited;

                Assert.AreEqual(1, (facetDeposited ? 1 : 0) + (granteeOk ? 1 : 0), $"iteration {iteration}: exactly one of the two fits under the cap (grantee reason: {granteeReason})");

                var lines = backend.Stacks.Count(s => s.AccountId == OwnerAccount && s.Count > 0);
                Assert.AreEqual(cap, lines, $"iteration {iteration}: the vault holds exactly cap lines, never more");

                // The facet item: in the vault xor back in the pack.
                var facetHanded = rec.HandedBack.Any(i => ReferenceEquals(i, facetItem));
                Assert.AreEqual(facetDeposited, world.Destroyed.Contains(facetItem), $"iteration {iteration}: a deposited facet item is the vault's");
                Assert.AreEqual(!facetDeposited, facetHanded, $"iteration {iteration}: a refused facet item is handed back, and only then");
                Assert.AreEqual(facetDeposited ? 1L : 0L, LedgerCount(7950));

                // The grantee item: in the vault xor still the grantee's.
                Assert.AreEqual(granteeOk, world.Destroyed.Contains(granteeItem), $"iteration {iteration}: the grantee item is in exactly one place");
                Assert.AreEqual(granteeOk ? 1L : 0L, LedgerCount(7951));
            }
        }
    }
}

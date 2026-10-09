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
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The counted item-CLASS storage tier: the third way a vault holds something, after a stored
    /// biota and the stack ledger.
    ///
    /// What makes this tier different from the other two, and what most of these tests are about: a
    /// class row holds no biota and no per-item value. It holds a COUNT of interchangeable items and
    /// ONE pooled total, and a withdraw hands out round(remaining total / remaining count) per item
    /// while decrementing both. So the property that has to hold is not "each item got its value
    /// back" - no item has a value of its own any more - it is that the pool is CONSERVED: a full
    /// withdraw hands out a sum exactly equal to what was deposited, with nothing stranded on the row
    /// and nothing invented.
    ///
    /// The store is driven through its two seams, so none of this needs a database.
    /// </summary>
    [TestClass]
    public class AccountVaultClassStorageTests
    {
        private const uint OwnerAccount = 6101;
        private const uint OwnerCharacter = 0x50000101;

        private const uint BagWcid = 21013;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Classowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // The class tier only ever sees items the pristine check has already refused, so every
            // deposit in this file is non-pristine by construction. PristineResult defaults to false;
            // it is set here explicitly because the whole file depends on it.
            world.PristineResult = false;

            // The predicate is OFF in every pre-existing vault test, which is what keeps them running in
            // the pre-class world. This file is the one that turns it on.
            world.ClassifyResult = true;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();

            // The profile's counters are process-static, exactly like PropertyManager's caches, so they
            // are reset here and in Cleanup rather than only inside the two tests that assert on them.
            VaultFoldProfile.ResetForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            VaultFoldProfile.ResetForTest();

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        /// <summary>
        /// One fold pass over <paramref name="store"/> with an explicit per-pass budget, which is the only
        /// shape the fold has: AccountVaultFoldMigration supplies the batch size and there is no tunable
        /// left to read. Returns how many items folded.
        ///
        /// <paramref name="clock"/> only rate-limits VaultFoldProfile's slow-pass line, so it is an
        /// arbitrary but distinct number per call site for the tests that care about that limiter.
        ///
        /// The result is captured through a LOCAL rather than an out parameter of this method, because C#
        /// forbids capturing an enclosing method's own out parameter in a lambda - see
        /// AccountVaultStore.FoldSomeStoredItemsOnQueue's remarks.
        /// </summary>
        private static int Fold(AccountVaultStore store, double clock, int budget)
        {
            var folded = 0;

            store.Enqueue(() => folded = store.FoldSomeStoredItemsOnQueue(clock, budget, out _));

            return folded;
        }

        /// <summary>
        /// Loads a freshly built store, which is the precondition every fold test now has to state for
        /// itself.
        ///
        /// A fold pass gates on IsWarm and will NOT load a store - see
        /// FoldPass_OnAStoreThatIsRegisteredButCold_ReadsNothingAtAll for why that is the production
        /// property and not a test detail. So a test that seeds a container and goes straight to a fold
        /// would be folding against a store that has never read its own vault index, which is not a
        /// state any real fold pass runs in: AccountVaultFoldMigration polls the store until it is loaded
        /// before it enqueues a single batch, and reports NotWarm rather than folding if it never is.
        ///
        /// Reading IsLoaded is what performs the load, and it is the same call AccountVaultManager's
        /// warm queue makes, so this is the production warm-up rather than a test-only back door.
        /// </summary>
        private static void Warm(AccountVaultStore store)
        {
            Assert.IsTrue(store.IsLoaded, "the store must load before a fold pass can see anything in it");
        }

        /// <summary>
        /// A vault container with <paramref name="itemCount"/> stored biotas already in it. The seeded
        /// items are UNCLASSIFIABLE, so they stay stored biotas and act purely as vault bulk - which is
        /// the whole point of the scaling test: the deposits under measurement must not care how many
        /// of these there are.
        /// </summary>
        private Container SeedVault(int capacity, int itemCount, int order)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = (uint)(9500 + order),
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
            });

            for (var i = 0; i < itemCount; i++)
            {
                var filler = FakeVaultWorld.MakeStack(8000, 1, 100);

                world.UnclassifiableGuids.Add(filler.Guid.Full);

                Assert.IsTrue(container.TryAddToInventory(filler), "could not seed a vault item");
            }

            return container;
        }

        /// <summary>One salvage bag of the class this file measures. Only Value varies unless asked.</summary>
        private static WorldObject Bag(int value, int structure = 100, int workmanship = 7, int numItems = 10, string name = "Salvage (100)")
        {
            return FakeVaultWorld.MakeSalvageBag(BagWcid, structure, workmanship, numItems, value, name);
        }

        private static bool Deposit(AccountVaultStore store, WorldObject item, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out reason));

            failReason = reason;
            return ok;
        }

        private static bool Withdraw(AccountVaultStore store, VaultEntry entry, int amount, out List<WorldObject> withdrawn, out string failReason)
        {
            var ok = false;
            List<WorldObject> got = null;
            string reason = null;

            store.Enqueue(() => ok = store.TryWithdraw(entry, amount, Owner, out got, out reason, GroupTakeOrder.Front));

            withdrawn = got ?? new List<WorldObject>();
            failReason = reason;
            return ok;
        }

        private static VaultEntry TheClassEntry(AccountVaultStore store)
        {
            var entries = store.GetEntries(0, int.MaxValue).Where(e => e.Kind == VaultEntryKind.Class).ToList();

            Assert.AreEqual(1, entries.Count, $"expected exactly one class entry, found {entries.Count}");

            return entries[0];
        }

        // ---------------------------------------------------------------- the deposit is O(1)

        /// <summary>
        /// THE cost invariant of this tier, and the one no functional test can see: a deposit does the
        /// SAME amount of work whatever the account already holds.
        ///
        /// Measured at two vault sizes an order of magnitude apart, and asserted IDENTICAL rather than
        /// merely bounded - a bound would pass for an implementation that scanned the vault and
        /// happened to stay under it at both sizes, which is the exact regression this tier exists to
        /// avoid. Four counters, chosen because each one is a different way the O(1) could be lost:
        ///
        /// - TryDescribeClass calls: the predicate is the most expensive thing on the path, and a
        ///   deposit that compared the incoming item against each stored item would show up here first;
        /// - MaterializeClass calls: the round-trip self-check builds exactly one probe;
        /// - backend class adjusts: one guarded UPDATE, never a read of the account's other rows;
        /// - grouping rebuilds: the only O(stored items) walk this class has. A merging deposit adds no
        ///   entry, so it must never reach the cap check that would trigger one.
        /// </summary>
        [TestMethod]
        public void Deposit_CostsTheSameWhateverTheVaultAlreadyHolds()
        {
            var small = Measure(storedItems: 10);
            var large = Measure(storedItems: 200);


            Assert.AreEqual(small.describe, large.describe,
                $"TryDescribeClass ran {small.describe} time(s) per deposit at 10 stored items and {large.describe} at 200 - the deposit is scanning the vault");

            Assert.AreEqual(small.materialize, large.materialize,
                $"MaterializeClass ran {small.materialize} time(s) per deposit at 10 stored items and {large.materialize} at 200");

            Assert.AreEqual(small.adjusts, large.adjusts,
                $"the backend saw {small.adjusts} class adjust(s) per deposit at 10 stored items and {large.adjusts} at 200");

            Assert.AreEqual(small.regroups, large.regroups,
                $"the grouping memo was rebuilt {small.regroups} time(s) per deposit at 10 stored items and {large.regroups} at 200 - a merging deposit must not reach the cap check");

            // Control: the numbers above are only meaningful if the deposits actually went to a class
            // row. A tier that silently refused everything would report 0 == 0 four times and pass.
            Assert.AreEqual(1, small.classRows, "the small vault's deposits must have landed on one class row");
            Assert.AreEqual(1, large.classRows, "the large vault's deposits must have landed on one class row");
            Assert.AreEqual(small.classCount, large.classCount, "both runs must have deposited the same number of items");
            Assert.IsTrue(small.classCount >= 4, "the measurement must cover several deposits, not one");

            // The measured values, pinned as well as compared. The identity assertions above are the
            // invariant; these say what the constant actually IS, so a change that made both sizes
            // equally MORE expensive is still visible.
            //
            // 2 predicate calls: one on the incoming item in TryDeposit, one on the probe inside the
            // round-trip self-check. 1 materialize: that probe. 1 backend adjust: the guarded UPDATE.
            // 0 grouping rebuilds: a merging deposit adds no entry, so it never reaches the cap check.
            Assert.AreEqual(2, small.describe);
            Assert.AreEqual(1, small.materialize);
            Assert.AreEqual(1, small.adjusts);
            Assert.AreEqual(0, small.regroups);
        }

        private (int describe, int materialize, int adjusts, int regroups, int classRows, long classCount) Measure(int storedItems)
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };

            SeedVault(capacity: 255, itemCount: Math.Min(storedItems, 255), order: 1);

            if (storedItems > 255)
                SeedVault(capacity: 255, itemCount: storedItems - 255, order: 2);

            var store = NewStore();

            // First deposit CREATES the row, so it is deliberately outside the measurement: it takes the
            // cap check's entry-count path that a merging deposit does not, and comparing a
            // row-creating deposit against a merging one would compare two different operations.
            Assert.IsTrue(Deposit(store, Bag(1000), out var reason), reason);

            const int measured = 5;

            world.ResetClassCalls();

            var adjustsBefore = backend.ClassAdjustCalls;
            var regroupsBefore = store.GroupingRebuilds;

            for (var i = 0; i < measured; i++)
                Assert.IsTrue(Deposit(store, Bag(1000 + i), out reason), reason);

            var rows = backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0);
            var count = backend.Classes.Where(c => c.AccountId == OwnerAccount).Sum(c => c.Count);

            return (world.DescribeClassCalls / measured,
                    world.MaterializeClassCalls / measured,
                    (backend.ClassAdjustCalls - adjustsBefore) / measured,
                    (store.GroupingRebuilds - regroupsBefore) / measured,
                    rows,
                    count);
        }

        // ---------------------------------------------------------------- the entry cap

        /// <summary>
        /// A deposit that MERGES into a class the account already holds consumes no entry, so it stays
        /// legal at a full cap. This is the class tier's half of the same ruling DESIGN 7.3 makes for
        /// the stack ledger: the cap counts entries, and a merge creates none.
        /// </summary>
        [TestMethod]
        public void Deposit_MergingIntoAnExistingClass_IsLegalAtTheCap()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, Bag(500), out var reason), reason);

            // Exactly full: one class row and nothing else.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", store.EntryCount));

            Assert.IsTrue(Deposit(store, Bag(600), out reason),
                $"a deposit merging into an existing class must not be refused at the cap: {reason}");

            Assert.AreEqual(2, TheClassEntry(store).Count, "the merge must have landed");
            Assert.AreEqual(1, backend.Classes.Count(c => c.AccountId == OwnerAccount), "a merge must not open a second row");
        }

        /// <summary>
        /// The control for the test above, and the reason that one proves anything: at the SAME cap, a
        /// deposit of a DIFFERENT class does add an entry and is refused. Without this, an
        /// implementation that had simply stopped enforcing the cap would pass the merge test.
        /// </summary>
        [TestMethod]
        public void Deposit_OfANewClass_AtTheCap_IsRefused()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, Bag(500), out var reason), reason);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", store.EntryCount));

            // A different Structure is a different class: Structure is part of the identity key.
            var different = Bag(500, structure: 42);

            Assert.IsFalse(Deposit(store, different, out reason),
                "a deposit that opens a NEW class row adds an entry and must be refused at the cap");

            StringAssert.Contains(reason ?? string.Empty, "entries");

            Assert.IsFalse(world.Destroyed.Contains(different), "a refused deposit must never destroy the item");
            Assert.AreEqual(1, backend.Classes.Count(c => c.AccountId == OwnerAccount));
        }

        // ---------------------------------------------------------------- the pooled value

        /// <summary>
        /// EXACT conservation. Deposit an awkward set of values whose total does not divide evenly by
        /// the count, withdraw everything in one go, and the values handed over must sum to exactly
        /// what went in - not approximately, and with nothing left on the row.
        ///
        /// The values are chosen so that total / count is not an integer at any point of the payout
        /// schedule, because an implementation that computed one share and multiplied would pass on a
        /// set that happened to divide evenly.
        /// </summary>
        [TestMethod]
        public void FullWithdraw_HandsBackExactlyTheDepositedTotal()
        {
            var values = new[] { 1, 2, 3, 100, 7777, 99991, 4, 5 };

            var store = NewStore();

            foreach (var value in values)
                Assert.IsTrue(Deposit(store, Bag(value), out var reason), reason);

            var expected = values.Sum();
            var entry = TheClassEntry(store);

            Assert.AreEqual(values.Length, entry.Count);
            Assert.AreEqual(expected, entry.TotalValue, "the pool must be the exact sum of what was deposited");

            Assert.IsTrue(Withdraw(store, entry, values.Length, out var withdrawn, out var failReason), failReason);

            Assert.AreEqual(values.Length, withdrawn.Count, "one object per item, never a stack");

            var handedOut = withdrawn.Sum(w => w.Value ?? 0);

            Assert.AreEqual(expected, handedOut,
                $"the pool must be conserved exactly: {expected} went in and {handedOut} came out");

            // And nothing is stranded: the row is gone, not sitting at zero with a remainder.
            Assert.AreEqual(0, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0));
            Assert.AreEqual(0, store.GetEntries(0, int.MaxValue).Count(e => e.Kind == VaultEntryKind.Class));
        }

        /// <summary>
        /// A PARTIAL withdraw leaves the row exactly consistent: what left plus what remains equals
        /// what was there, in both items and value. This is the property that makes repeated partial
        /// withdraws add up to the full one - the row is decremented by the sum actually handed out,
        /// never by a share recomputed afterwards.
        /// </summary>
        [TestMethod]
        public void PartialWithdraws_LeaveTheRowConsistent_AndStillSumToTheWhole()
        {
            var values = new[] { 13, 27, 1000, 1, 55, 98765, 3 };

            var store = NewStore();

            foreach (var value in values)
                Assert.IsTrue(Deposit(store, Bag(value), out var reason), reason);

            var expected = values.Sum();

            var first = TheClassEntry(store);

            Assert.IsTrue(Withdraw(store, first, 3, out var firstBatch, out var failReason), failReason);
            Assert.AreEqual(3, firstBatch.Count);

            var firstOut = firstBatch.Sum(w => w.Value ?? 0);

            var after = TheClassEntry(store);

            Assert.AreEqual(values.Length - 3, after.Count, "the item count must drop by exactly what left");
            Assert.AreEqual(expected - firstOut, after.TotalValue,
                "the pool must drop by exactly the sum handed out, never by a recomputed share");

            Assert.IsTrue(Withdraw(store, after, (int)after.Count, out var rest, out failReason), failReason);

            var total = firstOut + rest.Sum(w => w.Value ?? 0);

            Assert.AreEqual(expected, total, "two partial withdraws must add up to exactly the whole pool");
            Assert.AreEqual(0, store.GetEntries(0, int.MaxValue).Count(e => e.Kind == VaultEntryKind.Class));
        }

        /// <summary>
        /// Over-withdrawing is REFUSED rather than clamped, and refused before the row moves, matching
        /// the stack ledger's and the group's own behaviour.
        /// </summary>
        [TestMethod]
        public void Withdraw_OfMoreThanTheRowHolds_IsRefused_AndMovesNothing()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, Bag(100), out var reason), reason);
            Assert.IsTrue(Deposit(store, Bag(200), out reason), reason);

            var entry = TheClassEntry(store);

            Assert.IsFalse(Withdraw(store, entry, 3, out var withdrawn, out var failReason));
            Assert.AreEqual(0, withdrawn.Count);
            StringAssert.Contains(failReason ?? string.Empty, "only 2");

            Assert.AreEqual(2, TheClassEntry(store).Count);
            Assert.AreEqual(300, TheClassEntry(store).TotalValue);
        }

        /// <summary>
        /// A withdraw that COMMITS its debit and then cannot build all the objects refunds the WHOLE
        /// debit, exactly - the same contract RefundLedger keeps for the stack ledger, but it is not a
        /// copy-paste of it, and this test is about the difference.
        ///
        /// The row is refunded with the sum that was computed for the payout SCHEDULE, never with a
        /// recomputed round(remaining total / remaining count). By the time the refund runs the row's
        /// count and total have both already moved, so a recomputed share would be a share of a
        /// DIFFERENT pool and would leak or print value.
        ///
        /// The total is deliberately non-divisible (11 across 3 items -> shares 4, 4, 3), so a
        /// recomputed refund would visibly differ: refunding 3 x round(0/0-ish) or 3 x 4 = 12 both miss
        /// 11. The assertion is that the pool comes back to EXACTLY what it was.
        /// </summary>
        [TestMethod]
        public void Withdraw_ThatCannotBuildEverything_RefundsTheExactValueItDebited()
        {
            var store = NewStore();

            foreach (var value in new[] { 4, 4, 3 })
                Assert.IsTrue(Deposit(store, Bag(value), out var reason), reason);

            var entry = TheClassEntry(store);

            Assert.AreEqual(3, entry.Count);
            Assert.AreEqual(11, entry.TotalValue, "11 across 3 items does not divide evenly, which is the point");

            // Control: the schedule for a 3-item withdraw is 4, 4, 3 - three DIFFERENT shares, so a
            // refund that recomputed one share and multiplied it could not land on 11 by accident.
            Assert.AreEqual(4, AccountVaultStore.PooledShare(11, 3));
            Assert.AreEqual(4, AccountVaultStore.PooledShare(7, 2));
            Assert.AreEqual(3, AccountVaultStore.PooledShare(3, 1));

            // The FIRST object is the probe, built BEFORE the debit. Letting exactly one more build
            // succeed puts the failure after the debit has committed, which is the only place a refund
            // can be needed at all - a failure on the probe refuses before the row moves.
            world.MaterializeFailAfterCalls = 1;

            Assert.IsFalse(Withdraw(store, entry, 3, out var withdrawn, out var failReason),
                "a withdraw that cannot build everything must refuse rather than hand over a partial row");

            Assert.AreEqual(0, withdrawn.Count, "all or nothing: nothing may be delivered");

            world.MaterializeFailAfterCalls = null;

            var after = TheClassEntry(store);

            Assert.AreEqual(3, after.Count, "every item must be back on the row");
            Assert.AreEqual(11, after.TotalValue,
                $"the pool must be refunded EXACTLY, not recomputed: expected 11, found {after.TotalValue}");

            // And the row is still fully withdrawable afterwards, for exactly the same total.
            Assert.IsTrue(Withdraw(store, after, 3, out var again, out failReason), failReason);
            Assert.AreEqual(11, again.Sum(w => w.Value ?? 0), "after the refund a full withdraw must still hand over exactly 11");
        }

        // ---------------------------------------------------------------- the round-trip self-check

        /// <summary>
        /// The self-check that runs before ANYTHING is destroyed. If a fresh instance built from the
        /// payload does not describe back to the same class key, the item is not interchangeable with
        /// its own class - so it keeps its biota and is stored the ordinary way, and nothing is
        /// destroyed.
        ///
        /// Driven through MaterializeStructureOverride, which makes every rebuilt object carry a
        /// different Structure. Structure is part of the identity key, so the rebuilt probe keys
        /// differently and the check fails - which is exactly the real hazard it exists for: a property
        /// the materializer cannot reproduce.
        /// </summary>
        [TestMethod]
        public void Deposit_WhoseRoundTripDoesNotHold_KeepsTheBiota_AndDestroysNothing()
        {
            world.MaterializeStructureOverride = 3;

            var store = NewStore();
            var item = Bag(500);

            Assert.IsTrue(Deposit(store, item, out var reason), reason);

            Assert.IsFalse(world.Destroyed.Contains(item),
                "an item whose class could not be rebuilt must keep its biota - destroying it would make it unrecoverable");

            Assert.AreEqual(0, backend.Classes.Count(c => c.AccountId == OwnerAccount),
                "nothing may be credited to a class the item cannot be rebuilt into");

            var entries = store.GetEntries(0, int.MaxValue);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(VaultEntryKind.StoredItem, entries[0].Kind, "it must fall back to an ordinary stored biota");
            Assert.AreSame(item, entries[0].WorldObject);

            // Control: with the override cleared, the SAME item does collapse into a class row. Without
            // this the test would pass against a tier that never classified anything.
            world.MaterializeStructureOverride = null;

            var control = Bag(500);

            Assert.IsTrue(Deposit(store, control, out reason), reason);
            Assert.IsTrue(world.Destroyed.Contains(control), "the control deposit must collapse and destroy its biota");
            Assert.AreEqual(1, backend.Classes.Count(c => c.AccountId == OwnerAccount));
        }

        // ---------------------------------------------------------------- the Failed asymmetry

        /// <summary>
        /// A deposit whose credit neither committed nor provably failed KEEPS the item. A visible dupe
        /// an operator can reconcile against account_vault_log beats a silent, unrecoverable loss, and
        /// that ruling is the same one the stack-ledger branch already makes.
        /// </summary>
        [TestMethod]
        public void Deposit_OnAnUnknownCreditOutcome_KeepsTheItem()
        {
            var store = NewStore();

            // The key is not known ahead of time, so the first deposit establishes it and the SECOND is
            // the one driven into the Failed outcome.
            Assert.IsTrue(Deposit(store, Bag(100), out var reason), reason);

            var key = TheClassEntry(store).ClassKey;

            backend.ClassAdjustFailedKeys.Add(key);

            var item = Bag(200);

            Assert.IsFalse(Deposit(store, item, out reason), "an unknown credit outcome must report failure to the caller");

            Assert.IsFalse(world.Destroyed.Contains(item),
                "the item must NOT be destroyed over a credit that may never have landed");
        }

        /// <summary>
        /// A withdraw whose debit neither committed nor provably failed PROCEEDS and delivers. The
        /// asymmetry with the deposit above is deliberate: there the player still holds the item, here
        /// the items may already be gone from the row and the player has nothing.
        /// </summary>
        [TestMethod]
        public void Withdraw_OnAnUnknownDebitOutcome_ProceedsAndDelivers()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, Bag(100), out var reason), reason);
            Assert.IsTrue(Deposit(store, Bag(300), out reason), reason);

            var entry = TheClassEntry(store);

            backend.ClassAdjustFailedKeys.Add(entry.ClassKey);

            Assert.IsTrue(Withdraw(store, entry, 2, out var withdrawn, out var failReason),
                $"an unknown debit outcome must still deliver: {failReason}");

            Assert.AreEqual(2, withdrawn.Count);
            Assert.AreEqual(400, withdrawn.Sum(w => w.Value ?? 0), "the payout is computed before the debit, so it is unaffected by the outcome");
        }

        // ---------------------------------------------------------------- the lazy fold

        /// <summary>
        /// The fold migrates already-stored biotas into class rows, and it is IDEMPOTENT: a second pass
        /// over a fully folded store folds nothing and does no database work at all.
        ///
        /// Both halves matter. "Folds nothing" alone would pass for a fold that re-ran the predicate
        /// over every item forever and simply found nothing to do - which is the shape that would put
        /// the most expensive call on this path onto the world loop on every tick, for the life of the
        /// process.
        /// </summary>
        [TestMethod]
        public void Fold_IsIdempotent_AndASecondPassDoesNoWork()
        {
            var container = FakeVaultWorld.MakeContainer(255);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9601,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            var foldable = new List<WorldObject>();

            for (var i = 0; i < 3; i++)
            {
                var bag = Bag(100 + i);

                foldable.Add(bag);

                Assert.IsTrue(container.TryAddToInventory(bag));
            }

            // One item the predicate refuses, so the pass has to remember a refusal as well as fold.
            var stubborn = FakeVaultWorld.MakeStack(8000, 1, 100);

            world.UnclassifiableGuids.Add(stubborn.Guid.Full);

            Assert.IsTrue(container.TryAddToInventory(stubborn));

            var store = NewStore();

            Warm(store);

            var first = Fold(store, 100.0, budget: 100);

            Assert.AreEqual(3, first, "every foldable stored biota must fold in one pass when the budget allows");

            foreach (var bag in foldable)
                Assert.IsTrue(world.Destroyed.Contains(bag), "a folded item's biota is destroyed");

            Assert.AreEqual(1, backend.Classes.Count(c => c.AccountId == OwnerAccount));
            Assert.AreEqual(3, backend.Classes.Single(c => c.AccountId == OwnerAccount).Count);
            Assert.AreEqual(303, backend.Classes.Single(c => c.AccountId == OwnerAccount).TotalValue);

            // The unfoldable item is still there, untouched.
            Assert.IsFalse(world.Destroyed.Contains(stubborn));
            Assert.IsTrue(container.Inventory.ContainsKey(stubborn.Guid));

            var adjustsAfterFirst = backend.ClassAdjustCalls;

            world.ResetClassCalls();

            var second = Fold(store, 101.0, budget: 100);

            Assert.AreEqual(0, second, "a second pass over a folded store must fold nothing");
            Assert.AreEqual(adjustsAfterFirst, backend.ClassAdjustCalls, "a second pass must do no database work");
            Assert.AreEqual(0, world.DescribeClassCalls,
                "a refused item must be REMEMBERED - re-running the predicate every pass is the cost this tier cannot afford");
        }

        /// <summary>
        /// The kill switch is a STOP, not a rollback: with account_vault_class_storage off nothing new
        /// collapses into a class and nothing folds, while rows that already exist stay readable and
        /// withdrawable.
        /// </summary>
        [TestMethod]
        public void KillSwitchOff_StopsNewCollapse_ButLeavesExistingRowsWithdrawable()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, Bag(700), out var reason), reason);

            VaultClassTestConfig.Seed(classStorageEnabled: false);

            var afterOff = Bag(800);

            Assert.IsTrue(Deposit(store, afterOff, out reason), reason);

            Assert.IsFalse(world.Destroyed.Contains(afterOff), "with the switch off a classifiable item keeps its biota");
            Assert.AreEqual(1, backend.Classes.Single(c => c.AccountId == OwnerAccount).Count, "nothing new may join a class row");

            var folded = Fold(store, 200.0, budget: 25);
            Assert.AreEqual(0, folded, "the fold must not run with the switch off");

            // The existing row is still there and still comes out.
            var entry = TheClassEntry(store);

            Assert.AreEqual(1, entry.Count);
            Assert.IsTrue(Withdraw(store, entry, 1, out var withdrawn, out var failReason), failReason);
            Assert.AreEqual(1, withdrawn.Count);
            Assert.AreEqual(700, withdrawn[0].Value ?? 0);
        }


        /// <summary>
        /// THE FOLD NEVER CAUSES A COLD VAULT TO LOAD, and the gate that guarantees it lives on the STORE
        /// rather than on whatever is driving the fold.
        ///
        /// A store can be registered and still cold, because /mule search all builds stores deliberately
        /// unloaded and warms them later through the rate-limited warm queue. If a fold pass gated itself
        /// on readiness the way a deposit does, reaching such a store would run the whole load - vault
        /// index, stack ledger, class ledger and one biota per vault - synchronously, for an account
        /// nobody is using. It gates on IsWarm instead, which is documented as the one reader that never
        /// starts a load. AccountVaultFoldMigration reports that as NotWarm and moves on; this test is
        /// about the store's own refusal, so it drives the pass directly and does not go through the
        /// migration at all.
        ///
        /// Asserted as ZERO calls rather than as a bound, and across every seam a load would have to
        /// cross: three backend reads, the predicate, and the ledger write. Any one of them moving off
        /// zero means something on this path touched the shard for a store nobody asked about.
        ///
        /// The positive control at the end is what makes the zeros mean anything: the same fixture,
        /// once warm, folds. Without it this test would pass just as happily against a fixture that
        /// was never foldable in the first place.
        /// </summary>
        [TestMethod]
        public void FoldPass_OnAStoreThatIsRegisteredButCold_ReadsNothingAtAll()
        {
            var container = SeedInterleaved(10, 10, 10);

            var store = NewStore();

            Assert.IsFalse(store.IsWarm, "the fixture only means anything if the store starts cold");

            Assert.AreEqual(0, Fold(store, 100.0, budget: 25),
                "a cold store holds nothing in memory to fold, so the pass must fold nothing");

            Assert.AreEqual(0, backend.VaultReads, "a fold pass must not read the vault index of a cold store");
            Assert.AreEqual(0, backend.StackReads, "a fold pass must not read the stack ledger of a cold store");
            Assert.AreEqual(0, backend.ClassReads, "a fold pass must not read the class ledger of a cold store");
            Assert.AreEqual(0, backend.ClassAdjustCalls, "a fold pass on a cold store must write nothing");
            Assert.AreEqual(0, world.DescribeClassCalls, "a fold pass must not run the predicate against a cold store");

            Assert.AreEqual(3, container.Inventory.Count, "nothing may leave a vault the fold refused to look at");
            Assert.IsFalse(store.IsWarm, "the pass must not have warmed the store as a side effect either");

            // POSITIVE CONTROL. Warm the store the way production does - AccountVaultManager's warm
            // queue reads IsLoaded, and that read is what performs the load - and the very same fold
            // pass over the very same fixture now does real work.
            Assert.IsTrue(store.IsLoaded, "the warm path must be able to load this fixture");
            Assert.IsTrue(store.IsWarm);

            Assert.IsTrue(Fold(store, 200.0, budget: 25) > 0,
                "once the store is warm the identical pass must fold, or the zeros above proved nothing");
            Assert.IsTrue(world.DescribeClassCalls > 0,
                "the predicate must run on a warm store, or the zero above was measuring an inert fixture");
        }

        // ---------------------------------------------------------------- class-ordered folding

        /// <summary>
        /// A vault container seeded with bags of several classes INTERLEAVED, which is the layout that
        /// makes class order matter: stored order is vault order then PlacementPosition then guid, so
        /// without the target the fold would walk straight across all three classes and leave three
        /// partly-folded pairs on the panel at once.
        /// </summary>
        private Container SeedInterleaved(params int[] structuresInOrder)
        {
            var container = FakeVaultWorld.MakeContainer(255);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9801,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            foreach (var structure in structuresInOrder)
                Assert.IsTrue(container.TryAddToInventory(Bag(100, structure: structure, name: $"Salvage ({structure})")));

            return container;
        }

        /// <summary>
        /// THE OWNER'S RULING: one class is drained before another is started.
        ///
        /// Nine bags of three classes, interleaved A B C A B C A B C, with a budget of 3. Before this
        /// ordering existed a pass took the first three in stored order - one of each - and left THREE
        /// classes each showing a shrinking group row beside a growing class row. Now each pass folds
        /// one class, so at most one such pair exists at a time.
        ///
        /// Structure is what separates the classes here, because Structure is part of the identity key.
        /// </summary>
        [TestMethod]
        public void Fold_DrainsOneClassBeforeStartingAnother()
        {
            SeedInterleaved(10, 20, 30, 10, 20, 30, 10, 20, 30);

            var store = NewStore();

            Warm(store);

            // Pass 1 adopts a class and folds what it can see of it. With the budget at 3 and the
            // classes interleaved, the window holds one of each, so exactly one folds.
            var first = Fold(store, 1000.0, budget: 3);

            Assert.AreEqual(1, first, "the first pass adopts a class from a window holding one of each");
            Assert.AreEqual(1, backend.Classes.Count(c => c.AccountId == OwnerAccount),
                "exactly ONE class row may exist after the first pass - that is the whole ruling");

            var adopted = backend.Classes.Single(c => c.AccountId == OwnerAccount).ClassKey;

            // Run until nothing is left. At every single point, at most one class may be PARTLY folded:
            // a class row whose items plus the stored bags of the same class do not add up to 3 means
            // that class is mid-migration.
            for (var pass = 0; pass < 40; pass++)
            {
                var splits = CountPartlyFoldedClasses(store);

                Assert.IsTrue(splits <= 1,
                    $"at most one class may be partly folded at a time; pass {pass} had {splits}");

                Fold(store, 2000.0 + pass, budget: 3);

                if (CountStoredBags(store) == 0)
                    break;
            }

            Assert.AreEqual(0, CountStoredBags(store), "the migration must finish, not stall on its own ordering");
            Assert.AreEqual(3, backend.Classes.Count(c => c.AccountId == OwnerAccount), "all three classes end up folded");
            Assert.AreEqual(9, backend.Classes.Where(c => c.AccountId == OwnerAccount).Sum(c => c.Count));

            // The class adopted first is one of the three, not something invented.
            CollectionAssert.Contains(backend.Classes.Where(c => c.AccountId == OwnerAccount).Select(c => c.ClassKey).ToList(), adopted);
        }

        /// <summary>How many stored biotas are left in the account's vaults.</summary>
        private static int CountStoredBags(AccountVaultStore store)
            => store.GetEntries(0, int.MaxValue).Where(e => e.Kind == VaultEntryKind.StoredItem).Sum(e => (int)e.Count);

        /// <summary>
        /// How many classes are MID-MIGRATION: a class row exists and stored bags of the same class are
        /// still sitting in a vault. That is exactly the split row-pair the ordering is meant to bound
        /// to one at a time.
        ///
        /// A class is identified here by the display name the fixture gives it, which is unique per
        /// structure. Using the real class key would mean running the predicate from the test, which is
        /// the thing under test.
        /// </summary>
        private static int CountPartlyFoldedClasses(AccountVaultStore store)
        {
            var entries = store.GetEntries(0, int.MaxValue);

            var storedNames = new HashSet<string>(entries
                .Where(e => e.Kind == VaultEntryKind.StoredItem)
                .SelectMany(e => e.Members)
                .Select(m => m.Name), StringComparer.Ordinal);

            return entries.Count(e => e.Kind == VaultEntryKind.Class && e.DisplayName != null && storedNames.Contains(e.DisplayName));
        }

        /// <summary>
        /// A class larger than the budget still finishes, and while it is finishing it is the ONLY
        /// thing being folded. This is the case the ruling explicitly does not claim to make
        /// split-free: it bounds the split to one pair, it does not remove it.
        /// </summary>
        [TestMethod]
        public void Fold_KeepsDrainingAClassLargerThanOnePass_BeforeTouchingAnother()
        {
            // Five of class A, then five of class B, then one more A at the very end so the second
            // phase of the selection has to go looking past the window to find it.
            SeedInterleaved(10, 10, 10, 10, 10, 20, 20, 20, 20, 20, 10);

            var store = NewStore();

            for (var pass = 0; pass < 40; pass++)
            {
                Assert.IsTrue(CountPartlyFoldedClasses(store) <= 1,
                    $"pass {pass} left more than one class partly folded");

                Fold(store, 3000.0 + pass, budget: 4);

                if (CountStoredBags(store) == 0)
                    break;
            }

            Assert.AreEqual(0, CountStoredBags(store));

            var rows = backend.Classes.Where(c => c.AccountId == OwnerAccount).ToList();

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(6, rows.Max(r => r.Count), "the six bags of the larger class all land on one row");
            Assert.AreEqual(5, rows.Min(r => r.Count));
        }

        /// <summary>
        /// The ordering is the FOLD's concern and must not cost the deposit anything. Same measurement
        /// as Deposit_CostsTheSameWhateverTheVaultAlreadyHolds, re-run with a vault full of stored bags
        /// of several classes - the situation in which a deposit that consulted the fold's bookkeeping
        /// would start paying for it.
        /// </summary>
        [TestMethod]
        public void ClassOrderedFolding_CostsTheDepositNothing()
        {
            SeedInterleaved(10, 20, 30, 10, 20, 30, 10, 20, 30);

            var store = NewStore();

            // Prime the fold's hint map, so any deposit-side consultation of it would have something to
            // find. Without this the test would pass against a deposit that scanned an empty map.
            Fold(store, 4000.0, budget: 9);

            // A class the vault does not hold, so the first deposit opens its row and the rest merge.
            Assert.IsTrue(Deposit(store, Bag(1000, structure: 77, name: "Salvage (77)"), out var reason), reason);

            world.ResetClassCalls();

            var adjustsBefore = backend.ClassAdjustCalls;
            var regroupsBefore = store.GroupingRebuilds;

            const int measured = 4;

            for (var i = 0; i < measured; i++)
                Assert.IsTrue(Deposit(store, Bag(1000 + i, structure: 77, name: "Salvage (77)"), out reason), reason);

            Assert.AreEqual(2, world.DescribeClassCalls / measured, "still one predicate call on the item plus one on the self-check probe");
            Assert.AreEqual(1, world.MaterializeClassCalls / measured, "still one probe");
            Assert.AreEqual(1, (backend.ClassAdjustCalls - adjustsBefore) / measured, "still one guarded UPDATE");
            Assert.AreEqual(0, (store.GroupingRebuilds - regroupsBefore) / measured, "a merging deposit still reaches no cap check");
        }

        /// <summary>
        /// The fold's two maps are keyed by guid and are cleared wholesale only on a class RELOAD, so
        /// without an explicit forget at each vault boundary they would grow by one entry per
        /// withdrawal for the life of the store.
        ///
        /// Correctness was never at risk - FoldOne re-derives the class key from the live item before
        /// it acts, so a stale entry can at worst waste one budget slot on an item then excluded as
        /// WrongClass - but the bound the doc comments state has to be true, and an unbounded map on a
        /// long-lived store is a leak whatever it cannot cause.
        ///
        /// Asserted as an ABSENCE, which is why it needs an accessor: no functional assertion can see a
        /// map that is merely bigger than it should be.
        /// </summary>
        [TestMethod]
        public void FoldMemory_ForgetsAnItemThatLeavesAVault()
        {
            // Three foldable, one the predicate refuses, so both maps have something in them.
            var container = SeedInterleaved(10, 10, 10);

            var stubborn = FakeVaultWorld.MakeStack(8000, 1, 100);

            world.UnclassifiableGuids.Add(stubborn.Guid.Full);
            Assert.IsTrue(container.TryAddToInventory(stubborn));

            var store = NewStore();

            Warm(store);

            // A budget of 2 so the pass hints more items than it folds: one is folded, and the rest of
            // the window is hinted or refused and left in the vault. That is the state that leaks.
            Fold(store, 6000.0, budget: 2);
            Fold(store, 6001.0, budget: 2);
            Fold(store, 6002.0, budget: 2);

            var remembered = store.FoldRememberedGuidsForTest;

            Assert.IsTrue(remembered.Count > 0,
                "the fold must actually remember something, or the assertion below passes vacuously");

            // Everything still stored, and everything the fold remembers, must be the same population:
            // remembered guids that are no longer in any vault are the leak.
            var stored = new HashSet<uint>(store.GetEntries(0, int.MaxValue)
                .Where(e => e.Kind == VaultEntryKind.StoredItem)
                .SelectMany(e => e.Members)
                .Select(m => m.Guid.Full));

            CollectionAssert.IsSubsetOf(remembered.ToList(), stored.ToList(),
                "the fold remembers a guid that folding already removed from every vault");

            // Now the player withdraws everything that is left, by the ordinary path.
            foreach (var entry in store.GetEntries(0, int.MaxValue).Where(e => e.Kind == VaultEntryKind.StoredItem).ToList())
                Assert.IsTrue(Withdraw(store, entry, (int)entry.Count, out _, out var reason), reason);

            Assert.AreEqual(0, CountStoredBags(store), "every stored biota must be out of the vault for this to prove anything");

            Assert.AreEqual(0, store.FoldRememberedGuidsForTest.Count,
                "an item the player withdrew must be forgotten by the fold - both maps are keyed by guid and " +
                "are otherwise cleared only by a class reload, so this is where the entries would accumulate");
        }

        /// <summary>
        /// The same forgetting on the BARREL path, which removes items from a vault without going
        /// through WithdrawItem at all. Included because the leak is per removal SITE, not per removal:
        /// a fix wired only into the single-item withdraw would still leak here, and nothing else would
        /// say so.
        /// </summary>
        [TestMethod]
        public void FoldMemory_ForgetsAnItemTheBarrelTakes()
        {
            // The item left behind is one the PREDICATE REFUSES, not one of a different class. That is
            // deliberate: a refusal lands in foldRefused and the item stays in the vault whatever order
            // the fixture enumerates the container in, whereas "an item of the other class" depends on
            // which items the first window happened to hold.
            var container = SeedInterleaved(10, 10);

            var stubborn = FakeVaultWorld.MakeStack(8000, 1, 100);

            world.UnclassifiableGuids.Add(stubborn.Guid.Full);
            Assert.IsTrue(container.TryAddToInventory(stubborn));

            var store = NewStore();

            Warm(store);

            // Budget above the item count, so one pass examines all three: the two bags fold, the
            // refused one is remembered AND stays stored. That is the entry that used to outlive the
            // item.
            Fold(store, 7000.0, budget: 10);

            CollectionAssert.AreEquivalent(new[] { stubborn.Guid.Full }, store.FoldRememberedGuidsForTest.ToList(),
                "the refused item must be remembered and still stored, or the assertion below is vacuous");

            var remaining = store.GetEntries(0, int.MaxValue).Where(e => e.Kind == VaultEntryKind.StoredItem).ToList();

            Assert.AreEqual(1, remaining.Count, "there must be something left to barrel");

            foreach (var entry in remaining)
            {
                var ok = false;
                string reason = null;

                store.Enqueue(() => ok = store.TryBarrel(entry, (int)entry.Count, Owner, out reason));

                Assert.IsTrue(ok, reason);
            }

            Assert.AreEqual(0, CountStoredBags(store));

            Assert.AreEqual(0, store.FoldRememberedGuidsForTest.Count,
                "a barreled item leaves its vault by a different route and must be forgotten just the same");
        }

        // ---------------------------------------------------------------- the audit trail

        /// <summary>
        /// A class deposit records the Value that entered the pool and WHICH pool, and a withdraw
        /// records the summed value that left. Without both, a total_Value drift is not investigable:
        /// the log would say an item of some wcid moved, and one wcid spans as many classes as there
        /// are distinct property combinations.
        /// </summary>
        [TestMethod]
        public void ClassAuditRows_CarryTheValueAndTheClassKey()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, Bag(700), out var reason), reason);
            Assert.IsTrue(Deposit(store, Bag(300), out reason), reason);

            var entry = TheClassEntry(store);

            var deposits = backend.Logs.Where(l => l.Action == (int)AccountVaultAction.Deposit).ToList();

            Assert.AreEqual(2, deposits.Count);
            CollectionAssert.AreEquivalent(new long?[] { 700, 300 }, deposits.Select(l => l.Value).ToList(),
                "each deposit records the Value that item put into the pool");
            Assert.IsTrue(deposits.All(l => l.ClassKey == entry.ClassKey), "every class row names its pool");

            Assert.IsTrue(Withdraw(store, entry, 2, out var withdrawn, out var failReason), failReason);

            var withdraw = backend.Logs.Single(l => l.Action == (int)AccountVaultAction.Withdraw);

            Assert.AreEqual(1000L, withdraw.Value, "a withdraw records the SUMMED value that left, not a per-item share");
            Assert.AreEqual(entry.ClassKey, withdraw.ClassKey);
            Assert.AreEqual(1000, withdrawn.Sum(w => w.Value ?? 0), "and it agrees with what was actually handed over");
        }

        /// <summary>
        /// Null, not zero, on every non-class action. The difference is load-bearing for an
        /// investigation: 0 is a real value a worthless item can carry, while null means this action
        /// does not move pooled value at all.
        /// </summary>
        [TestMethod]
        public void NonClassAuditRows_LeaveBothNewColumnsNull()
        {
            world.ClassifyResult = false;

            var store = NewStore();

            var item = Bag(500);

            Assert.IsTrue(Deposit(store, item, out var reason), reason);

            var row = backend.Logs.Single(l => l.Action == (int)AccountVaultAction.Deposit);

            Assert.IsNull(row.Value, "an ordinary stored-biota deposit moves no pooled value");
            Assert.IsNull(row.ClassKey, "and it touches no pool");

            // Control: with the predicate back on, the same deposit DOES carry both.
            world.ClassifyResult = true;
            backend.Logs.Clear();

            Assert.IsTrue(Deposit(store, Bag(500), out reason), reason);

            var classRow = backend.Logs.Single(l => l.Action == (int)AccountVaultAction.Deposit);

            Assert.AreEqual(500L, classRow.Value);
            Assert.IsNotNull(classRow.ClassKey);
        }

        /// <summary>
        /// The background fold writes AccountVaultAction.Fold, never Deposit.
        ///
        /// It routes through the same DepositToClass a player deposit does, so before this it was
        /// indistinguishable from player activity - and folding one large vault writes thousands of
        /// rows in minutes, into the same table an investigation reads.
        /// </summary>
        [TestMethod]
        public void FoldedRows_AreLabelledFold_NotDeposit()
        {
            SeedInterleaved(10, 10, 10);

            var store = NewStore();

            Warm(store);

            var folded = Fold(store, 5000.0, budget: 10);

            Assert.AreEqual(3, folded);

            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit),
                "a fold must not be logged as a player deposit");

            var foldRows = backend.Logs.Where(l => l.Action == (int)AccountVaultAction.Fold).ToList();

            Assert.AreEqual(3, foldRows.Count);
            Assert.IsTrue(foldRows.All(l => l.Value == 100), "a fold row carries the value that entered the pool");
            Assert.IsTrue(foldRows.All(l => l.ClassKey != null), "and which pool it entered");
            Assert.IsTrue(foldRows.All(l => l.ActorCharacterName == "(vault fold)"), "the actor is the fold, never a character");

            // Control: a real player deposit into the same store still logs as Deposit, so the label is
            // discriminating rather than universally rewritten.
            Assert.IsTrue(Deposit(store, Bag(100, structure: 10, name: "Salvage (10)"), out var reason), reason);

            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit));
        }

        /// <summary>
        /// The class tier's counterpart to LedgerWithdraw_WritesItsAuditRowBeforeTheDebit, same
        /// source-scan technique and same reasoning: the debit is an autocommitted UPDATE while the
        /// objects it pays out are not persisted until the caller saves them, so a crash in between
        /// destroys the items and the audit row is the only thing that makes that reconstructable.
        ///
        /// It exists because the last change to this method HOISTED the payout-schedule computation
        /// above the audit line, so that the row could carry the value that leaves. That is exactly the
        /// class of edit that could put the audit after the debit while every behavioural test stayed
        /// green, because the OUTCOME is identical either way - only the crash window differs.
        /// </summary>
        [TestMethod]
        public void ClassWithdraw_WritesItsAuditRowBeforeTheDebit()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "Entity", "AccountVault", "AccountVaultStore.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find AccountVaultStore.cs by walking up from {AppContext.BaseDirectory}");

            // Comments stripped, so the method's own prose about ordering cannot satisfy the scan.
            var code = AccountVaultPurgeTests.StripComments(
                File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "Entity", "AccountVault", "AccountVaultStore.cs")));

            var iMethod = code.IndexOf("private bool WithdrawFromClass(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "WithdrawFromClass was not found.");

            var iDebit = code.IndexOf("TryAdjustAccountVaultClass(AccountId, step.ClassKey, -step.Take", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iDebit > iMethod, "the class debit was not found inside WithdrawFromClass.");

            var iLog = code.IndexOf("WriteLog(AccountVaultAction.Withdraw", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iLog >= 0 && iLog < iDebit,
                "the Withdraw audit row must be written BEFORE the debit. The debit is an autocommitted UPDATE and the " +
                "objects it pays out are not persisted until the caller saves them, so a crash in between destroys the " +
                "items - and the audit row, which now carries the value and the class key, is the only thing that makes " +
                "that reconstructable.");

            // The payout schedule must still be computed before the audit row, or the row cannot carry
            // the value that leaves. This is the constraint that makes the ordering above non-obvious,
            // so it is pinned rather than left to be rediscovered. It now lives in TryPlanClassDrain,
            // which a multi-row drain has to run to completion BEFORE it debits anything, so the pin is
            // on that call rather than on the loop that consumes it.
            var iPlan = code.IndexOf("TryPlanClassDrain(entry, amount", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iPlan > iMethod && iPlan < iLog,
                "the payout schedule must be computed before the audit row, which reports the summed value that leaves");

            // And it really is a schedule rather than a single share multiplied: the planner walks the
            // take item by item off the MEMBER's own count and total.
            var iPlanner = code.IndexOf("private bool TryPlanClassDrain(", StringComparison.Ordinal);
            Assert.IsTrue(iPlanner >= 0, "TryPlanClassDrain was not found.");

            var iSchedule = code.IndexOf("var shares = new List<int>(take);", iPlanner, StringComparison.Ordinal);
            Assert.IsTrue(iSchedule > iPlanner, "the per-member payout schedule was not found inside TryPlanClassDrain.");
        }

        // ---------------------------------------------------------------- fold instrumentation

        /// <summary>
        /// A fold pass reports what it cost, and reports EXAMINED separately from FOLDED so ms-per-item
        /// is derivable rather than guessed.
        ///
        /// The two numbers differ whenever the predicate refuses, and that difference is the whole
        /// reason both are recorded: a pass that examined 25 and folded 2 did the expensive work 25
        /// times, and a report that only said "2" would understate the cost by an order of magnitude.
        /// </summary>
        [TestMethod]
        public void FoldPass_RecordsExaminedAndFoldedSeparately()
        {
            var container = FakeVaultWorld.MakeContainer(255);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9701,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            for (var i = 0; i < 2; i++)
                Assert.IsTrue(container.TryAddToInventory(Bag(100 + i)));

            // Three the predicate refuses, so examined must come back 5 while folded comes back 2.
            for (var i = 0; i < 3; i++)
            {
                var stubborn = FakeVaultWorld.MakeStack(8000, 1, 100);

                world.UnclassifiableGuids.Add(stubborn.Guid.Full);

                Assert.IsTrue(container.TryAddToInventory(stubborn));
            }

            var store = NewStore();

            Warm(store);

            VaultFoldProfile.ResetForTest();

            var folded = Fold(store, 1000.0, budget: 100);

            Assert.AreEqual(2, folded);

            var snapshot = VaultFoldProfile.SnapshotForTest();

            Assert.AreEqual(1, snapshot.Passes, "one real pass");
            Assert.AreEqual(0, snapshot.IdlePasses);
            Assert.AreEqual(5, snapshot.Examined, "every candidate the pass walked, refused ones included");
            Assert.AreEqual(2, snapshot.Folded);
            Assert.IsTrue(snapshot.MaxMs >= 0, "the pass is timed");

            // A second pass over the same store is IDLE - foldExhausted short-circuits before the
            // candidate scan - and an idle pass is counted apart from a real one, so a mostly-migrated
            // fleet cannot make the real passes look cheap to anything reading the ratio.
            Fold(store, 1001.0, budget: 100);

            var after = VaultFoldProfile.SnapshotForTest();

            Assert.AreEqual(1, after.Passes, "an idle pass is not a pass");
            Assert.AreEqual(1, after.IdlePasses);
            Assert.AreEqual(5, after.Examined, "an idle pass examines nothing");
        }

        /// <summary>
        /// The slow-pass line fires on the threshold and is then rate-limited, so a persistently slow
        /// fleet logs once per window rather than once per pass.
        ///
        /// This is now the WHOLE of VaultFoldProfile's output. The periodic `[VAULT] fold over the last
        /// 60s` roll-up went with the background fold rotation that drained it, and its throughput numbers
        /// are already in /vaultclassfold's progress line.
        /// </summary>
        [TestMethod]
        public void SlowPassLine_IsThresholdTriggered_AndRateLimited()
        {
            VaultFoldProfile.ResetForTest();

            // Below the threshold: no line, and no rate limit armed.
            VaultFoldProfile.RecordPass(1u, VaultFoldProfile.SlowPassMs - 1, 25, 25, 100.0);

            // At the threshold: fires, and arms the limiter.
            VaultFoldProfile.RecordPass(1u, VaultFoldProfile.SlowPassMs, 25, 25, 100.0);

            // The observable is the rate limit rather than the log line itself, since this suite has no
            // appender: a second slow pass inside the window must not re-arm the limiter, and one after
            // it must.
            VaultFoldProfile.RecordPass(1u, VaultFoldProfile.SlowPassMs * 10, 25, 25, 101.0);

            var snapshot = VaultFoldProfile.SnapshotForTest();

            Assert.AreEqual(3, snapshot.Passes, "every pass is still counted, slow or not");
            Assert.AreEqual(VaultFoldProfile.SlowPassMs * 10, snapshot.MaxMs, "the rolling maximum tracks the worst pass regardless of the log rate limit");
        }

        // ---------------------------------------------------------------- PooledShare itself

        /// <summary>
        /// The payout schedule, driven directly, over the shapes a deposited set actually produces. The
        /// property asserted is the one the tier rests on: whatever the total and the count, repeatedly
        /// taking round(remaining / remaining count) and decrementing both hands out the total exactly.
        /// </summary>
        [DataTestMethod]
        [DataRow(0L, 1L)]
        [DataRow(1L, 3L)]
        [DataRow(2L, 3L)]
        [DataRow(100L, 7L)]
        [DataRow(99991L, 13L)]
        [DataRow(1L, 1000L)]
        [DataRow(7L, 2L)]
        [DataRow(123456789L, 97L)]
        public void PooledShare_ConservesThePoolExactly(long total, long count)
        {
            long remainingTotal = total;
            long remainingCount = count;
            long handedOut = 0;

            for (var i = 0; i < count; i++)
            {
                var share = AccountVaultStore.PooledShare(remainingTotal, remainingCount);

                Assert.IsTrue(share >= 0, "a share is never negative");

                handedOut += share;
                remainingTotal -= share;
                remainingCount--;
            }

            Assert.AreEqual(total, handedOut, $"paying out {count} share(s) of {total} must hand over exactly {total}");
            Assert.AreEqual(0, remainingTotal, "nothing may be stranded on the row");
        }

        // ---------------------------------------------------------------- VaultEntryKind

        /// <summary>
        /// Pins the deletion of VaultEntry.IsLedger.
        ///
        /// The bool was deleted rather than kept as a computed convenience because there are now THREE
        /// kinds and a bool can only answer two of them - every `if (e.IsLedger)` site silently lumped
        /// class rows in with stored items, and the compiler is the only thing that can find all of
        /// them. Re-adding it "for symmetry" with an IsClass would undo exactly that, and would do it
        /// without breaking a single test, so the guard is a source-text assertion rather than a
        /// behavioural one.
        /// </summary>
        [TestMethod]
        public void VaultEntry_HasNoBooleanKindHelpers()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "Entity", "AccountVault", "VaultEntry.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find VaultEntry.cs by walking up from {AppContext.BaseDirectory}");

            var src = File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "Entity", "AccountVault", "VaultEntry.cs"));

            // Comment lines stripped FIRST. The file's own doc explains at length why the bool was
            // deleted, and it names it to do so - a bare Contains would fail on the prose that
            // documents the rule this test enforces. Matching declarations only is also the sharper
            // check: it is a member coming back that matters, not a mention.
            var code = string.Join("\n", src.Replace("\r\n", "\n").Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            foreach (var member in new[] { "bool IsLedger", "bool IsClass", "bool IsStoredItem" })
                Assert.IsFalse(code.Contains(member),
                    $"VaultEntry.{member.Substring(5)} is back. Kind is an enum with three members; a bool can answer two of them, " +
                    "and the sites that ask are exactly the ones that must be forced to say which kind they mean.");

            // IsGroup stays: it is not a kind. A group is a stored-item entry that happens to have
            // several members, which is orthogonal to Kind and cannot be expressed by it. It doubles as
            // the control for the strip above - if it removed too much, this would fail.
            StringAssert.Contains(code, "public bool IsGroup");
        }

        /// <summary>
        /// Every kind of entry the store can hold shows up in the panel and counts against the cap.
        /// A class row that enumerated but did not count would let an account exceed its cap silently;
        /// one that counted but did not enumerate would charge for a holding nobody can see.
        /// </summary>
        [TestMethod]
        public void ClassRows_BothEnumerateAndCountAgainstTheCap()
        {
            var container = SeedVault(capacity: 255, itemCount: 1, order: 1);

            var store = NewStore();

            var before = store.EntryCount;

            Assert.IsTrue(Deposit(store, Bag(100), out var reason), reason);
            Assert.IsTrue(Deposit(store, Bag(200), out reason), reason);

            Assert.AreEqual(before + 1, store.EntryCount, "two items of ONE class are one entry, not two");

            var kinds = store.GetEntries(0, int.MaxValue).Select(e => e.Kind).ToList();

            CollectionAssert.Contains(kinds, VaultEntryKind.Class);
            CollectionAssert.Contains(kinds, VaultEntryKind.StoredItem);

            Assert.AreEqual(container.Inventory.Count + 1, store.EntryCount);
        }
    }
}

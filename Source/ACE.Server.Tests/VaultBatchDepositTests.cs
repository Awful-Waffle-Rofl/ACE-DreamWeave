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
    /// The BATCHED deposit entry point, AccountVaultStore.TryDepositBatch
    /// (SPEC-vault-batch-deposit.md, step 4).
    ///
    /// What these tests are for, and it is one invariant rather than a feature: an item is credited
    /// exactly once and destroyed exactly once, or neither. Deposits destroy the biota and withdrawals
    /// rebuild from the ledger, so any path that credits twice, or that credits while leaving the biota
    /// alive and reachable, MINTS ITEMS.
    ///
    /// Both directions are tested, and the second is the one that is easy to miss:
    ///
    ///   LOSS - no item is destroyed without its group's credit having committed.
    ///   DUPE - no item whose group's credit committed is ever handed back to the player. A throw out of
    ///          the destroy phase leaves items k..N credited, not destroyed and still detached, and the
    ///          existing per-item disposition rule for "no recorded success" is HandBackOrphan
    ///          (PersonalVendor.cs:680). Applied to a batch unchanged, that is a hand-back of up to 512
    ///          already-credited items.
    ///
    /// The mechanism that closes the dupe direction is that every item of every committed group has
    /// "credited, do not hand back" written into its outcome record BEFORE the destroy phase begins.
    /// <see cref="Batch_WhenTheDestroyPhaseThrows_EveryCreditedItemAlreadySaysDoNotHandBack"/> is the
    /// test that pins it, and it was shown to FAIL against an implementation that writes the outcome as
    /// the destroy loop reaches each item.
    /// </summary>
    [TestClass]
    public class VaultBatchDepositTests
    {
        private const uint OwnerAccount = 7301;
        private const uint OwnerCharacter = 0x50000301;

        /// <summary>The wcid the single-key ledger sale uses. Nothing else in this file deposits it.</summary>
        private const uint LedgerWcid = 7310;

        private const uint BagWcid = 21013;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Batchowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // Every key this class reads is seeded here and restored in Cleanup. PropertyManager's caches
            // are process-static and shared with every other test class in the run, so a class that reads
            // a key it never seeded passes only when some earlier class happened to seed it.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyLong("account_vault_landblock", DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        /// <summary>A vault container plus its index row, for the tests whose items keep their biota.</summary>
        private Container SeedVault(int capacity = AccountVaultStore.VaultItemCapacity)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9700,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            return container;
        }

        private void SeedLedger(uint wcid, long count)
        {
            backend.Stacks.Add(new AccountVaultStack { Id = (uint)(8800 + backend.Stacks.Count), AccountId = OwnerAccount, Wcid = wcid, Count = count });
        }

        /// <summary>
        /// Runs one batch on the store's mutation queue and hands back both the outcomes and whatever the
        /// work item threw. Drain catches a work item's exception without rethrowing, so a test that did
        /// not capture it would read a throw as a silent false.
        /// </summary>
        private static IReadOnlyList<VaultDepositOutcome> Batch(AccountVaultStore store, IReadOnlyList<WorldObject> items,
                                                                out bool ok, out Exception thrown)
        {
            var result = false;
            IReadOnlyList<VaultDepositOutcome> outcomes = null;

            store.Enqueue(() => result = store.TryDepositBatch(items, Owner, out outcomes), out var caught);

            ok = result;
            thrown = caught;

            return outcomes;
        }

        private static IReadOnlyList<VaultDepositOutcome> Batch(AccountVaultStore store, IReadOnlyList<WorldObject> items, out bool ok)
        {
            var outcomes = Batch(store, items, out ok, out var thrown);

            Assert.IsNull(thrown, $"the batch was not expected to throw: {thrown}");

            return outcomes;
        }

        private static bool Deposit(AccountVaultStore store, WorldObject item, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out reason));

            failReason = reason;
            return ok;
        }

        private long LedgerCount(uint wcid)
            => backend.Stacks.Where(s => s.AccountId == OwnerAccount && s.Wcid == wcid).Sum(s => s.Count);

        private int DepositRows(uint wcid)
            => backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit && l.Wcid == wcid);

        private static WorldObject Bag(int value, int workmanship, int numItems)
            => FakeVaultWorld.MakeSalvageBag(BagWcid, 100, workmanship, numItems, value, $"Salvage ({workmanship}/{numItems})");

        // ------------------------------------------------------------------ case 1

        /// <summary>
        /// The measured sale: 53 pristine items of ONE wcid. It must cost exactly ONE stack statement
        /// rather than 53, credit the summed units once, destroy all 53 and audit all 53.
        ///
        /// All four assertions are needed and none implies another. One call with a wrong sum is a
        /// silent under-credit; the right sum written by 53 calls is the cost this change exists to
        /// remove; 53 credits with 52 destroys is a dupe; and 53 destroys with 52 audit rows is an
        /// unreconcilable loss.
        /// </summary>
        [TestMethod]
        public void Batch_FiftyThreeItemsOfOneWcid_CostsOneStatementAndCreditsEveryUnitExactlyOnce()
        {
            world.PristineResult = true;

            var store = NewStore();

            var items = new List<WorldObject>();

            for (var i = 0; i < 53; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 2, 100));

            var outcomes = Batch(store, items, out var ok);

            Assert.IsTrue(ok, "every item of a healthy single-key sale must deposit");

            Assert.AreEqual(1, backend.StackBatchCalls, "53 items of one wcid must cost ONE stack statement, not 53");
            Assert.AreEqual(106L, LedgerCount(LedgerWcid), "the credit must be the SUMMED units of every item in the group");
            Assert.AreEqual(1, backend.StackBatchCreditsApplied[LedgerWcid], "the group's delta must be applied exactly once");

            Assert.AreEqual(53, world.Destroyed.Count, "every credited item is destroyed");
            Assert.AreEqual(53, DepositRows(LedgerWcid), "one audit row per ITEM, never one per group");

            Assert.IsTrue(outcomes.All(o => o.Deposited && o.Commit == VaultDepositCommit.Committed),
                "every item's outcome must say it is the vault's");

            Assert.AreEqual(106L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger).Count,
                "and the store's own count must come from the reload, not from arithmetic on the delta");
        }

        // ------------------------------------------------------------------ case 2

        /// <summary>
        /// A mixed sale: pristine stackables down the LEDGER branch and salvage bags down the CLASS
        /// branch, in one call. One statement per TABLE, and every item credited exactly once.
        ///
        /// The two class keys are distinct on purpose - a freshly salvaged bag almost always carries a
        /// novel (ItemWorkmanship, NumItemsInMaterial) pair - so this also pins that the class plan
        /// carries N distinct keys in ONE call rather than one call per key.
        /// </summary>
        [TestMethod]
        public void Batch_MixedLedgerAndClassSale_CostsOneStatementPerTableAndCreditsEveryItemOnce()
        {
            world.PristineResult = false;
            world.ClassifyResult = true;

            var store = NewStore();

            var ledgerItems = new List<WorldObject>();
            var classItems = new List<WorldObject>();

            // Pristine and non-pristine in one list: IsPristine answers per call, so the switch is flipped
            // by a per-guid set rather than by the blanket flag, which cannot express a mixed sale.
            for (var i = 0; i < 4; i++)
                ledgerItems.Add(FakeVaultWorld.MakeStack(LedgerWcid, 3, 100));

            classItems.Add(Bag(100, 7, 10));
            classItems.Add(Bag(120, 7, 10));
            classItems.Add(Bag(140, 8, 20));

            var pristineGuids = new HashSet<uint>(ledgerItems.Select(i => i.Guid.Full));

            world.PristineGuids = pristineGuids;

            var items = new List<WorldObject> { ledgerItems[0], classItems[0], ledgerItems[1], classItems[1], ledgerItems[2], classItems[2], ledgerItems[3] };

            var outcomes = Batch(store, items, out var ok);

            Assert.IsTrue(ok, "every item of a healthy mixed sale must deposit");

            Assert.AreEqual(1, backend.StackBatchCalls, "one statement for the stack ledger");
            Assert.AreEqual(1, backend.ClassBatchCalls, "one statement for the class ledger, whatever its distinct-key count");

            Assert.AreEqual(12L, LedgerCount(LedgerWcid), "four items of three units each");
            Assert.AreEqual(1, backend.StackBatchCreditsApplied[LedgerWcid]);

            Assert.AreEqual(2, backend.Classes.Count, "two distinct class keys");
            Assert.AreEqual(3L, backend.Classes.Sum(c => c.Count), "three class items, counted once each");
            Assert.AreEqual(360L, backend.Classes.Sum(c => c.TotalValue), "and their pooled value is the sum of the three");
            Assert.IsTrue(backend.ClassBatchCreditsApplied.Values.All(v => v == 1), "no class key may be credited twice");

            // Counted per ITEM rather than as a total: the class round-trip self-check materializes and
            // then destroys a scratch probe per class deposit, so world.Destroyed carries three more
            // objects than this sale had items and a bare count would be asserting the wrong thing.
            foreach (var item in items)
            {
                Assert.AreEqual(1, world.Destroyed.Count(d => ReferenceEquals(d, item)),
                    $"0x{item.Guid.Full:X8} must be destroyed exactly once - never twice, and never not at all after its group committed");
            }

            Assert.AreEqual(7, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit), "one audit row per item");

            Assert.IsTrue(outcomes.All(o => o.Deposited && o.Commit == VaultDepositCommit.Committed));
        }

        // ------------------------------------------------------------------ case 3

        /// <summary>
        /// Group 2 of 3 comes back Failed. Groups 1 and 3 must commit and be destroyed, group 2's items
        /// must be KEPT with an Unknown outcome, and the failure must produce ONE log line naming every
        /// guid in the group.
        ///
        /// Keeping the items is the deposit/withdraw asymmetry applied per group: the items still exist
        /// in the player's hands, so destroying them against a credit that may never have landed is a
        /// certain unrecoverable loss, while keeping them when the credit DID land is a visible dupe an
        /// operator can reconcile. The per-item outcome must be Unknown rather than None, because None
        /// is what tells a caller the item is provably still its own.
        /// </summary>
        [TestMethod]
        public void Batch_WhenTheSecondOfThreeGroupsFails_TheOtherTwoCommitAndTheFailedGroupsItemsAreKept()
        {
            const uint first = 7311;
            const uint failing = 7312;
            const uint third = 7313;

            world.PristineResult = true;

            backend.LedgerAdjustFailedWcids.Add(failing);

            var store = NewStore();

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(first, 1, 100),
                FakeVaultWorld.MakeStack(failing, 1, 100),
                FakeVaultWorld.MakeStack(failing, 1, 100),
                FakeVaultWorld.MakeStack(third, 1, 100),
            };

            var outcomes = Batch(store, items, out var ok);

            Assert.IsFalse(ok, "a batch with a failed group is not a wholly successful batch");

            Assert.AreEqual(1L, LedgerCount(first), "group 1 committed");
            Assert.AreEqual(1L, LedgerCount(third), "group 3 committed, independently of group 2");
            Assert.AreEqual(0L, LedgerCount(failing), "the failed group moved nothing in the fake's world");

            Assert.IsTrue(outcomes[0].Deposited && outcomes[0].Commit == VaultDepositCommit.Committed);
            Assert.IsTrue(outcomes[3].Deposited && outcomes[3].Commit == VaultDepositCommit.Committed);

            foreach (var index in new[] { 1, 2 })
            {
                Assert.IsFalse(outcomes[index].Deposited, "an item whose credit is unknown is not reported as deposited");
                Assert.AreEqual(VaultDepositCommit.Unknown, outcomes[index].Commit,
                    "and it must be UNKNOWN rather than None - None would tell the caller the item is provably still its own, which is exactly what nobody knows");
                Assert.AreEqual(AccountVaultStore.UnavailableMessage, outcomes[index].FailReason);
            }

            // THE LOSS DIRECTION: no item is destroyed without its group's credit having committed.
            CollectionAssert.DoesNotContain(world.Destroyed, items[1]);
            CollectionAssert.DoesNotContain(world.Destroyed, items[2]);
            Assert.AreEqual(2, world.Destroyed.Count, "only the two committed groups' items are destroyed");

            Assert.AreEqual(0, DepositRows(failing), "nothing provably happened for the failed group, so nothing is audited as a deposit");
            Assert.AreEqual(2, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit));
        }

        // ------------------------------------------------------------------ case 4, the dupe direction

        /// <summary>
        /// THE DUPE-DIRECTION TEST, and the reason this step is engineer tier.
        ///
        /// The destroy phase throws at item k of a committed group. Items k..N are then credited, not
        /// destroyed and still detached from the player. The invariant is that their outcome records
        /// ALREADY say "credited, do not hand back", so the caller's disposition loop cannot return a
        /// single one of them - returning them on top of a credit that committed is the duplication the
        /// deposit ordering exists to prevent, arrived at from the other side.
        ///
        /// This test FAILS against an implementation that writes the outcome as the destroy loop reaches
        /// each item: items k..N are then left carrying no recorded success, and the existing per-item
        /// rule for that state is HandBackOrphan. That variant was built and run - see the class remark.
        /// </summary>
        [TestMethod]
        public void Batch_WhenTheDestroyPhaseThrows_EveryCreditedItemAlreadySaysDoNotHandBack()
        {
            const int itemCount = 53;
            const int throwAt = 10;

            world.PristineResult = true;

            var store = NewStore();

            var items = new List<WorldObject>();

            for (var i = 0; i < itemCount; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 1, 100));

            // Throws when the destroy loop reaches item `throwAt`, which is AFTER the group's credit has
            // committed - the only state in which a hand-back would duplicate.
            world.BeforeDestroyItem = item =>
            {
                if (ReferenceEquals(item, items[throwAt]))
                    throw new InvalidOperationException("fake teardown failure in the destroy phase");
            };

            var outcomes = Batch(store, items, out var ok, out var thrown);

            Assert.IsNotNull(thrown, "sanity: the destroy phase really did throw, or this test proves nothing");
            Assert.IsFalse(ok, "the batch did not return, so its bool is at its initialised false");

            Assert.IsNotNull(outcomes, "the outcomes must be readable after a throw - they are assigned before any work runs");

            Assert.AreEqual(throwAt, world.Destroyed.Count, "the loop stopped where it threw");

            for (var i = 0; i < itemCount; i++)
            {
                Assert.IsTrue(outcomes[i].Deposited,
                    $"item {i} belongs to a group whose credit COMMITTED, so its outcome must say the vault's - including the items at and after the throw, which are the ones a hand-back would duplicate");

                Assert.AreEqual(VaultDepositCommit.Committed, outcomes[i].Commit,
                    $"item {i} must not be reported refused or unknown after its group's credit committed");

                Assert.IsNull(outcomes[i].FailReason, $"item {i} carries no refusal");
            }

            // And the items the destroy never reached are still alive, which is what makes handing them
            // back a DUPE rather than a harmless no-op.
            for (var i = throwAt; i < itemCount; i++)
                CollectionAssert.DoesNotContain(world.Destroyed, items[i], $"item {i} was never destroyed");

            Assert.AreEqual((long)itemCount, LedgerCount(LedgerWcid), "and the credit for all 53 did commit");
        }

        /// <summary>
        /// The same shape one phase earlier: the backend call itself throws after committing some of its
        /// groups. Nobody knows which, so every submitted item must be UNKNOWN rather than None - the
        /// in-flight mark written before the statement runs is what makes that true, and it is the batch's
        /// replacement for the single-item path's `depositCommit = Unknown` before its own adjust.
        /// </summary>
        [TestMethod]
        public void Batch_WhenTheCreditItselfThrows_EverySubmittedItemIsUnknownRatherThanRefused()
        {
            world.PristineResult = true;

            backend.ThrowFromStackBatchAfterGroups = 1;

            var store = NewStore();

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(7321, 1, 100),
                FakeVaultWorld.MakeStack(7322, 1, 100),
                FakeVaultWorld.MakeStack(7323, 1, 100),
            };

            var outcomes = Batch(store, items, out _, out var thrown);

            Assert.IsNotNull(thrown, "sanity: the credit really did throw");

            foreach (var outcome in outcomes)
            {
                Assert.AreEqual(VaultDepositCommit.Unknown, outcome.Commit,
                    "a throw out of the credit leaves nobody knowing, and None would tell the caller the item is provably still its own");

                Assert.IsFalse(outcome.Deposited);
            }

            Assert.AreEqual(0, world.Destroyed.Count, "nothing is destroyed on a credit nobody can account for");
        }

        // ------------------------------------------------------------------ case 5

        /// <summary>
        /// The store's in-memory index is only a HINT about which keys already exist. Both ways of being
        /// wrong are exercised, and the assertion is on the resulting COUNTS: whatever the oracle said,
        /// every key is credited exactly once.
        ///
        /// This is the one place a bug double-credits. A key the oracle calls present that is absent
        /// misses the guarded UPDATE, is proved absent by the identifying read and is then upserted - and
        /// an implementation that upserted it WITHOUT that read would also re-apply the delta to every key
        /// the UPDATE already matched.
        /// </summary>
        [TestMethod]
        public void Batch_WhenTheOracleCallsAnAbsentKeyPresent_TheIdentifyingReadRunsAndTheKeyIsCreditedOnce()
        {
            const uint wcid = 7331;

            world.PristineResult = true;

            // The store loads with the row in place, so its in-memory ledger holds the key...
            SeedLedger(wcid, 40);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "sanity: the store must load before its oracle means anything");

            // ...and the row then vanishes from under it, which is exactly a stale oracle.
            backend.Stacks.RemoveAll(s => s.Wcid == wcid);

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(wcid, 5, 100),
                FakeVaultWorld.MakeStack(wcid, 5, 100),
            };

            Batch(store, items, out var ok);

            Assert.IsTrue(ok, "a stale oracle is bounded, never fatal: the deposit must still succeed");

            Assert.AreEqual(1, backend.StackBatchIdentifyingReads, "a key the oracle called present that was absent must force the identifying read");
            Assert.AreEqual(1, backend.StackBatchKeysUpsertedAfterIdentifyingRead, "and only that key may reach the upsert");

            Assert.AreEqual(10L, LedgerCount(wcid), "the two items' units, credited exactly once - 20 would be the double credit");
            Assert.AreEqual(1, backend.StackBatchCreditsApplied[wcid], "one application of the group's delta, never two");
        }

        /// <summary>
        /// The mirror: the oracle calls a key ABSENT that is present. It takes the upsert's duplicate
        /// branch and is credited exactly once on top of what was already there.
        /// </summary>
        [TestMethod]
        public void Batch_WhenTheOracleCallsAPresentKeyAbsent_TheKeyIsStillCreditedExactlyOnce()
        {
            const uint wcid = 7332;

            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store loads holding nothing, so its oracle will call this key absent");

            // Appears under the store after it loaded.
            SeedLedger(wcid, 40);

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(wcid, 5, 100),
                FakeVaultWorld.MakeStack(wcid, 5, 100),
            };

            Batch(store, items, out var ok);

            Assert.IsTrue(ok);

            Assert.AreEqual(0, backend.StackBatchIdentifyingReads, "the oracle was wrong the other way, so no identifying read is forced");
            Assert.AreEqual(50L, LedgerCount(wcid), "40 already there plus the two items' 10 units, credited once");
            Assert.AreEqual(1, backend.StackBatchCreditsApplied[wcid]);
        }

        // ------------------------------------------------------------------ case 6

        /// <summary>
        /// The cap counts ENTRIES. With room for two more and a sale of three items of three DISTINCT new
        /// wcids, the first two deposit and the third is refused with the cap message.
        ///
        /// The batch-local claim is what makes this work at all: phase A mutates nothing, so without it
        /// AddsEntryLocked would answer "a new line" for all three against an entry count that never
        /// moves, and all three would fit.
        /// </summary>
        [TestMethod]
        public void Batch_WithRoomForTwoEntries_DepositsTheFirstTwoNewWcidsAndRefusesTheThird()
        {
            world.PristineResult = true;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));

            var store = NewStore();

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(7341, 1, 100),
                FakeVaultWorld.MakeStack(7342, 1, 100),
                FakeVaultWorld.MakeStack(7343, 1, 100),
            };

            var outcomes = Batch(store, items, out var ok);

            Assert.IsFalse(ok, "a batch that refused an item is not a wholly successful batch");

            Assert.IsTrue(outcomes[0].Deposited, "the first new entry fits");
            Assert.IsTrue(outcomes[1].Deposited, "the second new entry fits");

            Assert.IsFalse(outcomes[2].Deposited, "the third does not, and a batch-local claim is the only thing that can know that");
            Assert.AreEqual(VaultDepositCommit.None, outcomes[2].Commit, "a cap refusal is provable: nothing happened");
            Assert.IsTrue(AccountVaultStore.IsFullMessage(outcomes[2].FailReason),
                $"the cap refusal must be the existing full-vault message, got: {outcomes[2].FailReason}");

            Assert.AreEqual(1L, LedgerCount(7341));
            Assert.AreEqual(1L, LedgerCount(7342));
            Assert.AreEqual(0L, LedgerCount(7343), "the refused item's units must never reach the ledger");

            CollectionAssert.DoesNotContain(world.Destroyed, items[2], "and it must not be destroyed");
        }

        /// <summary>
        /// The other half of the claim rule, and the one that pins WHAT a claim is keyed on. With room
        /// for exactly ONE entry, a sale of 53 items of ONE BRAND NEW wcid must deposit all 53: they all
        /// land on the single line the first of them creates.
        ///
        /// A claim keyed on the ITEM rather than the LINE passes every other test in this file and fails
        /// this one at item 2, because each item would then consume an entry of its own for a line that
        /// already exists in the plan.
        /// </summary>
        [TestMethod]
        public void Batch_WithRoomForOneEntry_FiftyThreeItemsOfOneNewWcidAllDeposit()
        {
            const uint wcid = 7345;

            world.PristineResult = true;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = NewStore();

            Assert.AreEqual(0, store.EntryCount, "sanity: the store starts empty, with room for exactly one entry");

            var items = new List<WorldObject>();

            for (var i = 0; i < 53; i++)
                items.Add(FakeVaultWorld.MakeStack(wcid, 1, 100));

            var outcomes = Batch(store, items, out var ok);

            Assert.IsTrue(ok, "53 items of one new wcid consume ONE entry between them, not 53");
            Assert.IsTrue(outcomes.All(o => o.Deposited), "every one of the 53, not just the first");

            Assert.AreEqual(53L, LedgerCount(wcid));
            Assert.AreEqual(1, store.EntryCount, "and the sale really did consume exactly one entry");
        }

        /// <summary>
        /// The 9,001st healing kit, batched. An account AT its cap sells 53 items of a wcid it ALREADY
        /// holds: every one of them must deposit, because they top up a line the panel is already drawing
        /// and the cap counts lines, not units (DESIGN 7.3).
        ///
        /// This is the case AddsEntryLocked's first arm exists for, and getting it wrong in a batch is not
        /// merely a repeat of the old bug - a batch-local claim that keyed on the ITEM rather than the
        /// LINE would refuse items 2..53 of a wcid the account already holds.
        /// </summary>
        [TestMethod]
        public void Batch_AtTheCapWithAnAlreadyHeldWcid_DepositsEveryOneOfFiftyThreeItems()
        {
            const uint wcid = 7351;

            world.PristineResult = true;

            SeedLedger(wcid, 9000);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = NewStore();

            Assert.AreEqual(1, store.EntryCount, "sanity: the store is AT its cap of one entry");

            var items = new List<WorldObject>();

            for (var i = 0; i < 53; i++)
                items.Add(FakeVaultWorld.MakeStack(wcid, 1, 100));

            var outcomes = Batch(store, items, out var ok);

            Assert.IsTrue(ok, "an account at its cap may still top up a line it is already drawing");
            Assert.IsTrue(outcomes.All(o => o.Deposited), "every one of the 53, not just the first");

            Assert.AreEqual(9053L, LedgerCount(wcid));
            Assert.AreEqual(53, world.Destroyed.Count);
        }

        // ------------------------------------------------------------------ case 7, the control

        /// <summary>
        /// The one-item control. TryDeposit now delegates to the batch, so every value its callers read
        /// must be exactly what the single-item path produced: the bool, the failReason, the audit row,
        /// and LastDepositCommit.
        ///
        /// All three arms are asserted together, because each one alone passes with the others broken -
        /// and the difference between None and Unknown is the difference between an item a caller may
        /// hand back and one it must keep.
        /// </summary>
        [TestMethod]
        public void Deposit_OneItemThroughTheDelegatingPath_ProducesThePreChangeOutcomes()
        {
            const uint applied = 7361;
            const uint refused = 7362;
            const uint failed = 7363;

            world.PristineResult = true;

            backend.FailLedgerAdjustWcids.Add(refused);
            backend.LedgerAdjustFailedWcids.Add(failed);

            var store = NewStore();

            // ---- the credit lands ----
            var good = FakeVaultWorld.MakeStack(applied, 9, 100);

            Assert.IsTrue(Deposit(store, good, out var reason), reason);
            Assert.IsNull(reason, "a successful deposit carries no fail reason");
            Assert.AreEqual(VaultDepositCommit.Committed, store.LastDepositCommit);

            var row = backend.Logs.Single(l => l.Action == (int)AccountVaultAction.Deposit && l.Wcid == applied);

            Assert.AreEqual(OwnerAccount, row.OwnerAccountId);
            Assert.AreEqual(OwnerCharacter, row.ActorCharacterGuid);
            Assert.AreEqual(9L, row.Count, "the audit row records the item's UNITS");
            Assert.IsNull(row.ItemGuid, "a collapsed ledger deposit records no item guid, exactly as before");
            Assert.IsNull(row.ClassKey, "and no class key");
            Assert.IsNull(row.Value, "and no pooled value - null is not zero here");

            CollectionAssert.Contains(world.Destroyed, good);

            // ---- the credit is provably refused ----
            var refusedItem = FakeVaultWorld.MakeStack(refused, 1, 100);

            Assert.IsFalse(Deposit(store, refusedItem, out reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);
            Assert.AreEqual(VaultDepositCommit.None, store.LastDepositCommit, "a provable refusal leaves the item the caller's");
            CollectionAssert.DoesNotContain(world.Destroyed, refusedItem);
            Assert.AreEqual(0, DepositRows(refused));

            // ---- nobody knows ----
            var failedItem = FakeVaultWorld.MakeStack(failed, 1, 100);

            Assert.IsFalse(Deposit(store, failedItem, out reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason);
            Assert.AreEqual(VaultDepositCommit.Unknown, store.LastDepositCommit, "an unknown outcome must never be reported as None");
            CollectionAssert.DoesNotContain(world.Destroyed, failedItem);
            Assert.AreEqual(0, DepositRows(failed));
        }

        /// <summary>
        /// The one-item control for the branch that keeps its biota: a non-pristine, unclassifiable item
        /// goes into a vault container, LastDepositCommit is Committed the moment it is physically there,
        /// and nothing is destroyed.
        /// </summary>
        [TestMethod]
        public void Deposit_OneStoredBiotaThroughTheDelegatingPath_StillLandsInAVaultContainer()
        {
            var vault = SeedVault();

            world.PristineResult = false;
            world.ClassifyResult = false;

            var store = NewStore();

            var item = FakeVaultWorld.MakeStack(7371, 1, 100);

            Assert.IsTrue(Deposit(store, item, out var reason), reason);

            Assert.AreEqual(1, vault.Inventory.Count, "the biota is in the vault container");
            Assert.AreEqual(VaultDepositCommit.Committed, store.LastDepositCommit);
            Assert.AreEqual(0, world.Destroyed.Count, "the vault branch destroys nothing");
            Assert.AreEqual(0, backend.StackBatchCalls, "and it credits no ledger row");
            Assert.AreEqual(1, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit));
        }

        // ------------------------------------------------------------------ case 8

        // ------------------------------------------------------------------ instrumentation wiring

        /// <summary>
        /// Runs one batch exactly the way PersonalVendor.DepositItems does - BeginDeposit, TryDepositBatch,
        /// EndBatchDeposit with the ITEM count - with the profile armed, and leaves the aggregate readable.
        ///
        /// The arming is process-static and shared with every other test class in the run
        /// (VaultDepositPhaseProfileTests owns it for its own duration), so it is restored in a finally.
        /// WorldTickProfile.BeginIteration claims this thread as the world thread, which is what the
        /// profile's own gate checks; AccountVaultStore.Enqueue runs queued work on the CALLING thread, so
        /// the charges land on this one.
        /// </summary>
        private static void ProfiledBatch(AccountVaultStore store, IReadOnlyList<WorldObject> items, Action assertions)
        {
            var wasEnabled = InboundOpcodeProfile.Enabled;

            WorldTickProfile.BeginIteration();
            WorldTickProfile.EndIteration();

            InboundOpcodeProfile.Enabled = true;
            VaultDepositPhaseProfile.BeginPhase();

            try
            {
                var scope = VaultDepositPhaseProfile.BeginDeposit();

                Assert.AreNotEqual(0L, scope,
                    "sanity: the profile must actually arm, or every count below is a zero that proves nothing about the wiring");

                try
                {
                    store.Enqueue(() => store.TryDepositBatch(items, Owner, out _));
                }
                finally
                {
                    VaultDepositPhaseProfile.EndBatchDeposit(scope, items.Count);
                }

                assertions();
            }
            finally
            {
                InboundOpcodeProfile.Enabled = wasEnabled;
                VaultDepositPhaseProfile.BeginPhase();
            }
        }

        /// <summary>
        /// THE DISCRIMINATING INSTRUMENTATION TEST: vd_upsert_n must be the GROUP count.
        ///
        /// Three distinct wcids in one 53-item sale, which is what makes the assertion able to fail in both
        /// directions at once. A scope closed with the plain leaf End would read 1 (one statement); a scope
        /// charged per item would read 53; only EndForGroups with the group count reads 3. A single-key sale
        /// cannot discriminate the first of those, because one group and one statement are the same number -
        /// which is why this test carries three groups and
        /// <see cref="Batch_Instrumentation_ASingleKeySaleReadsAsOneUpsertOverFiftyThreeItems"/> is the floor
        /// case rather than the guard.
        ///
        /// vd_n is asserted alongside it, because the ratio is only readable if the denominator stays an ITEM
        /// count: a vd_n that collapsed to the number of physical batch calls would make vd_upsert_n / vd_n
        /// meaningless in exactly the same way.
        /// </summary>
        [TestMethod]
        public void Batch_Instrumentation_ChargesUpsertOncePerGroupAndNotOncePerStatementOrItem()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "load before arming, so the initial index read is not charged to the sale");

            var items = new List<WorldObject>();

            for (var i = 0; i < 53; i++)
                items.Add(FakeVaultWorld.MakeStack((uint)(7391 + (i % 3)), 1, 100));

            ProfiledBatch(store, items, () =>
            {
                Assert.AreEqual(53, VaultDepositPhaseProfile.DepositCalls,
                    "vd_n must stay an ITEM count - one physical batch call covering 53 items is 53, never 1");

                Assert.AreEqual(3, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert),
                    "vd_upsert_n must be the GROUP count: 1 would mean the scope was closed with the plain leaf End, and 53 would mean it was charged per item");

                Assert.IsTrue(VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Upsert) >= 0.0,
                    "and the statement's own elapsed time is still charged exactly once");
            });
        }

        /// <summary>
        /// The compression floor, and the case the spec names: 53 items of ONE wcid read as one upsert over
        /// 53 deposits. It does NOT discriminate a revert of the EndForGroups wiring on its own - with one
        /// group, End and EndForGroups both produce 1 - so it is here as the ratio's other end rather than as
        /// the guard. See
        /// <see cref="Batch_Instrumentation_ChargesUpsertOncePerGroupAndNotOncePerStatementOrItem"/>.
        /// </summary>
        [TestMethod]
        public void Batch_Instrumentation_ASingleKeySaleReadsAsOneUpsertOverFiftyThreeItems()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);

            var items = new List<WorldObject>();

            for (var i = 0; i < 53; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 1, 100));

            ProfiledBatch(store, items, () =>
            {
                Assert.AreEqual(53, VaultDepositPhaseProfile.DepositCalls);
                Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert),
                    "one distinct key, so one group: vd_upsert_n / vd_n is 1/53, the sale's compression ratio");
            });
        }

        /// <summary>
        /// Phase D's whole-account reload must be CHARGED rather than falling into vd_rest_ms, which is where
        /// it silently sat before it was wired. Charged once per call that reached phase D, whatever the
        /// batch carried.
        ///
        /// The remainder is the control: asserting only that reload is non-zero would also pass if the scope
        /// double-charged, and the sum invariant is what this instrument's whole acceptance criterion rests
        /// on.
        /// </summary>
        [TestMethod]
        public void Batch_Instrumentation_ChargesPhaseDsReloadAndKeepsTheSumInvariant()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);

            var items = new List<WorldObject>();

            for (var i = 0; i < 8; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 1, 100));

            ProfiledBatch(store, items, () =>
            {
                Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Reload),
                    "phase D is one whole-account read per CALL, not per table and not per item");

                var phaseSum = 0.0;

                for (var i = 0; i < VaultDepositPhases.Count; i++)
                    phaseSum += VaultDepositPhaseProfile.TotalMsAt((VaultDepositPhase)i);

                Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, phaseSum + VaultDepositPhaseProfile.RemainderMs, 0.01,
                    "THE INVARIANT: the ten phases plus the remainder are the deposit total, and wiring a new phase must not break it");

                Assert.IsTrue(VaultDepositPhaseProfile.RemainderMs >= -0.01,
                    "and the remainder must not go negative, which is what a double-charged scope looks like");
            });
        }

        /// <summary>
        /// The market's single-item reader. VaultMarketItemStore.TryGiveToBuyer calls TryDeposit inside
        /// its own try/catch and then reads store.LastDepositCommit
        /// (Source\ACE.Server\Managers\Market\VaultMarketItemStore.cs:434) to decide whether the item is
        /// the buyer's, the seller's, or nobody's. This asserts the value it reads at exactly the moment
        /// it reads it - on the mutation queue, immediately after TryDeposit RETURNS OR THROWS.
        ///
        /// The two throwing arms are the ones the delegation could silently break: an `out` parameter is
        /// not copied back on a throw in most people's mental model, and LastDepositCommit after a throw
        /// is the whole reason the field exists.
        /// </summary>
        [TestMethod]
        public void LastDepositCommit_AfterAThrow_StillTellsTheMarketWhichSideOfTheCommitItHappened()
        {
            world.PristineResult = true;

            var store = NewStore();

            // ---- threw BEFORE the credit committed: the item is still the caller's to hand back ----
            backend.ThrowFromStackBatchAfterGroups = 0;

            var early = FakeVaultWorld.MakeStack(7381, 1, 100);
            var earlyCommit = VaultDepositCommit.Committed;

            store.Enqueue(() =>
            {
                try
                {
                    store.TryDeposit(early, Owner, out _);
                }
                finally
                {
                    earlyCommit = store.LastDepositCommit;
                }
            }, out var earlyThrown);

            Assert.IsNotNull(earlyThrown, "sanity: the credit really did throw");
            Assert.AreEqual(VaultDepositCommit.Unknown, earlyCommit,
                "a throw out of the credit is UNKNOWN - the market must keep the item on the buyer's side rather than return it to the seller");

            // ---- threw AFTER the credit committed: the item is the buyer's and must not go back ----
            backend.ThrowFromStackBatchAfterGroups = -1;
            world.ThrowFromDestroyItemCount = 1;

            var late = FakeVaultWorld.MakeStack(7382, 1, 100);
            var lateCommit = VaultDepositCommit.None;

            store.Enqueue(() =>
            {
                try
                {
                    store.TryDeposit(late, Owner, out _);
                }
                finally
                {
                    lateCommit = store.LastDepositCommit;
                }
            }, out var lateThrown);

            Assert.IsNotNull(lateThrown, "sanity: the destroy really did throw");
            Assert.AreEqual(VaultDepositCommit.Committed, lateCommit,
                "the credit landed before the throw, so the market must mark the item DELIVERED - returning it to the seller would duplicate it");

            Assert.AreEqual(1L, LedgerCount(7382), "and the credit really is in the ledger");
        }

        // ------------------------------------------------------------------ phase A containment

        /// <summary>
        /// PHASE A IS CONTAINED PER ITEM, and this is the loss-direction test the batching made necessary.
        ///
        /// Before the batch every item had its own Enqueue and its own TryDeposit call, so a throw while
        /// planning item 3 of 5 left items 4 and 5 untouched and provably still the caller's. One Enqueue
        /// for the whole sale removes that boundary by itself: the throw unwinds out of TryDepositBatch and
        /// items 4 and 5 are never examined - detached, not destroyed, in no vault container, and with no
        /// outcome the caller may act on. That is the one state outside all three of guarantee 1's.
        ///
        /// The throw is driven from FakeVaultWorld.BeforeIsPristine, which is the first thing the per-item
        /// planning loop calls, so this lands squarely inside phase A and before anything at all is
        /// credited. The failing item is asserted None, not Unknown, and that is the contained region's
        /// safety argument rather than a convenience: everything inside the catch reads the item and
        /// writes nothing but batch-local plan state, so a throw there proves no credit was attempted.
        /// DepositToVault, the one step of phase A that commits, sits deliberately OUTSIDE the catch and
        /// still unwinds the whole call - MarketPartialDeliveryTests pins that contract.
        ///
        /// Shown to FAIL against the pre-change implementation: with no catch around the per-item body the
        /// exception escapes, `thrown` is non-null and items 4 and 5 are left at None with nothing credited.
        /// </summary>
        [TestMethod]
        public void Batch_WhenPhaseAPlanningThrowsForOneItem_ThatItemAloneFailsAndTheRestOfTheSaleCompletes()
        {
            const int throwAt = 2;

            world.PristineResult = true;

            var store = NewStore();

            var items = new List<WorldObject>();

            for (var i = 0; i < 5; i++)
                items.Add(FakeVaultWorld.MakeStack((uint)(7401 + i), 1, 100));

            world.BeforeIsPristine = item =>
            {
                if (ReferenceEquals(item, items[throwAt]))
                    throw new InvalidOperationException("fake planning failure in phase A");
            };

            var outcomes = Batch(store, items, out var ok, out var thrown);

            Assert.IsNull(thrown, "THE CONTAINMENT: one item's planning failure must not unwind the whole sale");
            Assert.IsFalse(ok, "but the batch is not wholly successful either");

            Assert.IsFalse(outcomes[throwAt].Deposited, "the item whose planning threw is not deposited");
            Assert.AreEqual(VaultDepositCommit.None, outcomes[throwAt].Commit,
                "and it is None: the contained region writes nothing but batch-local plan state, so a throw inside it proves no credit was attempted and the item may go home");
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, outcomes[throwAt].FailReason);

            for (var i = 0; i < items.Count; i++)
            {
                if (i == throwAt)
                    continue;

                Assert.IsTrue(outcomes[i].Deposited, $"item {i} was never touched by the failure and must deposit normally");
                Assert.AreEqual(VaultDepositCommit.Committed, outcomes[i].Commit, $"item {i} must be committed");
                Assert.AreEqual(1L, LedgerCount((uint)(7401 + i)), $"item {i}'s units must reach the ledger");
                CollectionAssert.Contains(world.Destroyed, items[i], $"item {i} must be destroyed after its credit");
            }

            Assert.AreEqual(0L, LedgerCount((uint)(7401 + throwAt)), "the failed item never joined a group, so nothing was credited for it");
            CollectionAssert.DoesNotContain(world.Destroyed, items[throwAt], "and it must not be destroyed");

            Assert.AreEqual(1, backend.StackBatchCalls, "the surviving four still cost ONE statement between them");
        }

        // ------------------------------------------------------------------ cap coverage, the `d:` claim

        /// <summary>
        /// THE DISPLAY-KEY CLAIM, which the three cap tests above never reach: every one of them deposits
        /// PRISTINE items, so every claim they make is keyed `w:&lt;wcid&gt;`.
        ///
        /// Two salvage bags with the same wcid, Structure and Name but different RAW
        /// (ItemWorkmanship, NumItemsInMaterial) pairs that fall in the same workmanship bucket
        /// (14/10 and 21/15 both scale to 140) are TWO class rows drawn as ONE panel line. With room for
        /// exactly one entry both must deposit, because the cap counts lines.
        ///
        /// A claim keyed on the CLASS key rather than the display key passes every other test in this file
        /// and fails here at the second bag, which is the mutation this test was shown to discriminate.
        /// </summary>
        [TestMethod]
        public void Batch_AtTheCapWithTwoClassKeysOnOneDrawnLine_BothDeposit()
        {
            const string sharedName = "Pile of Iron Salvage";

            world.PristineResult = false;
            world.ClassifyResult = true;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var store = NewStore();

            Assert.AreEqual(0, store.EntryCount, "sanity: the store starts empty, with room for exactly one line");

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 14, 10, 60, sharedName),
                FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 21, 15, 80, sharedName),
            };

            var outcomes = Batch(store, items, out var ok);

            Assert.IsTrue(ok, "two class rows that draw as one line consume ONE entry between them");
            Assert.IsTrue(outcomes.All(o => o.Deposited), "both bags, not just the first");

            Assert.AreEqual(2, backend.Classes.Count,
                "sanity: these really are two DISTINCT class rows - one row would mean the test is proving nothing about the display key");

            Assert.AreEqual(1, backend.ClassBatchCalls, "and both keys are credited by one statement");
            Assert.AreEqual(2L, backend.Classes.Sum(c => c.Count));
            Assert.AreEqual(140L, backend.Classes.Sum(c => c.TotalValue));

            Assert.AreEqual(1, store.EntryCount, "the sale consumed exactly one entry, because the panel draws exactly one line");
        }

        /// <summary>
        /// THE MIXED CAP CASE the 2026-09-28 cap correction was written for: a vault-branch item and
        /// deferred claims in one sale, at the cap.
        ///
        /// An item routed to a vault container is stored COMPLETELY at its own position in phase A, and the
        /// store's own EntryCountLocked already counts its line by the time the next item is checked, so it
        /// must record NO batch-local claim. Recording one counts that line twice and refuses deposits that
        /// fit.
        ///
        /// With a cap of 2 the vault item and one new pristine wcid both fit and a second new wcid does
        /// not. An implementation that let the vault item claim an entry refuses the pristine item instead,
        /// which is the mutation this was shown to discriminate.
        /// </summary>
        [TestMethod]
        public void Batch_AtTheCapWithAVaultBranchItemAndDeferredClaims_CountsEachLineExactlyOnce()
        {
            const uint storedWcid = 7411;
            const uint firstLedger = 7412;
            const uint secondLedger = 7413;

            SeedVault();

            // Nothing is pristine or classifiable by default, so the first item takes the vault branch; the
            // two ledger items are switched on individually.
            world.PristineResult = false;
            world.ClassifyResult = false;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));

            var store = NewStore();

            var stored = FakeVaultWorld.MakeStack(storedWcid, 1, 100);
            var ledgerOne = FakeVaultWorld.MakeStack(firstLedger, 1, 100);
            var ledgerTwo = FakeVaultWorld.MakeStack(secondLedger, 1, 100);

            world.PristineGuids = new HashSet<uint> { ledgerOne.Guid.Full, ledgerTwo.Guid.Full };

            var items = new List<WorldObject> { stored, ledgerOne, ledgerTwo };

            var outcomes = Batch(store, items, out var ok);

            Assert.IsFalse(ok, "the third line does not fit under a cap of two");

            Assert.IsTrue(outcomes[0].Deposited, "the vault-branch item takes the first entry");
            Assert.AreEqual(1, world.Containers.Values.Sum(c => c.Inventory.Count), "and its biota really is in a vault container");

            Assert.IsTrue(outcomes[1].Deposited,
                "THE CASE THIS TEST EXISTS FOR: the vault item's line is already counted by the store, so it must claim nothing and leave room for this one");
            Assert.AreEqual(1L, LedgerCount(firstLedger));

            Assert.IsFalse(outcomes[2].Deposited, "the third line is genuinely over the cap");
            Assert.AreEqual(VaultDepositCommit.None, outcomes[2].Commit, "a cap refusal is provable: nothing happened");
            Assert.IsTrue(AccountVaultStore.IsFullMessage(outcomes[2].FailReason),
                $"the cap refusal must be the existing full-vault message, got: {outcomes[2].FailReason}");

            Assert.AreEqual(0L, LedgerCount(secondLedger), "and its units must never reach the ledger");
            CollectionAssert.DoesNotContain(world.Destroyed, ledgerTwo);
        }

        // ------------------------------------------------- the coalescing deposit window

        /// <summary>
        /// Runs <paramref name="work"/> on the store's mutation queue inside ONE coalescing deposit window,
        /// the shape VaultMarketItemStore.TryGiveToBuyer uses: one serialized region, a per-item TryDeposit
        /// loop, one window around it.
        /// </summary>
        private static Exception InWindow(AccountVaultStore store, Action work)
        {
            store.Enqueue(() =>
            {
                using (store.BeginDepositWindow())
                    work();
            }, out var thrown);

            return thrown;
        }

        /// <summary>
        /// THE REGRESSION THIS WINDOW EXISTS FOR, pinned by COUNTING BACKEND READS rather than by timing
        /// anything: a caller that must loop single-item deposits pays ONE whole-account re-read for the
        /// whole loop, not one per item.
        ///
        /// Before the window, every TryDeposit ran a batch of one and every batch's phase D re-read the
        /// account, so a 12-item market delivery issued 12 whole-account SELECTs on the world thread. The
        /// assertion below reads 12 without the window and 1 with it.
        ///
        /// The count is taken IMMEDIATELY after the region and before anything reads the store, because a
        /// later GetEntries would add a read of its own on a store left refusing, and that would make this
        /// number mean something other than "what the delivery cost".
        /// </summary>
        [TestMethod]
        public void DepositWindow_LoopedSingleItemDeposits_ReReadTheAccountOnceAtTheClose_NotOncePerItem()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the reads under test are counted");

            var items = new List<WorldObject>();

            for (var i = 0; i < 12; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 2, 100));

            // The fixture's own load reads are not the delivery's.
            backend.StackReads = 0;
            backend.ClassReads = 0;

            var refusals = new List<string>();

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                {
                    if (!store.TryDeposit(item, Owner, out var reason))
                        refusals.Add(reason);
                }
            });

            var readsForTheDelivery = backend.StackReads;
            var classReadsForTheDelivery = backend.ClassReads;

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            // Control FIRST: a read count is worth nothing over a delivery that did not deliver.
            CollectionAssert.AreEqual(new List<string>(), refusals, "control: every item must actually deposit");
            Assert.AreEqual(24L, LedgerCount(LedgerWcid), "control: the summed units of all 12 items");
            Assert.AreEqual(12, world.Destroyed.Count, "control: every credited item is destroyed");

            Assert.AreEqual(1, readsForTheDelivery,
                $"{items.Count} looped single-item deposits must cost ONE whole-account stack re-read, at the window's close. Without the window each one re-reads, which is {items.Count}.");

            Assert.AreEqual(0, classReadsForTheDelivery,
                "a ledger-only delivery must not re-read the class table at all, whose rows carry canonical_Form TEXT");

            // THE OTHER HALF OF THE CLAIM, and until now nothing asserted it: the window removes READS, it
            // does not batch WRITES. Each of the 12 deposits is still its own one-group batch statement, and
            // a number below 12 here would mean the window had quietly changed the commit shape - which
            // would change every per-item outcome the market path reads.
            Assert.AreEqual(items.Count, backend.StackBatchCalls,
                "the window must not change how the credits commit: still one batch statement per deposit");

            // And no oracle mismatch: every key after the first is carried as present, so nothing forces the
            // real DAO's extra identifying SELECT, which would put back a read per deposit by another route.
            Assert.AreEqual(0, backend.StackBatchIdentifyingReads,
                "no call may report a key present that was absent, which is what forces the identifying SELECT");

            Assert.AreEqual(0, backend.StackBatchKeysUpsertedAfterIdentifyingRead,
                "and therefore no key reaches the upsert by way of a corrected oracle");

            Assert.AreEqual(24L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger).Count,
                "and the count the store then serves comes from that one re-read, never from arithmetic on the deltas");
        }

        /// <summary>
        /// The class table's counterpart, and the one that motivated the window: every one of these re-reads
        /// drags every class row's canonical_Form TEXT column.
        /// </summary>
        [TestMethod]
        public void DepositWindow_LoopedClassDeposits_ReReadTheClassLedgerOnceAtTheClose_NotOncePerItem()
        {
            world.PristineResult = false;
            world.ClassifyResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the reads under test are counted");

            var items = new List<WorldObject>();

            for (var i = 0; i < 12; i++)
                items.Add(Bag(250, 70, 10));

            backend.StackReads = 0;
            backend.ClassReads = 0;

            var refusals = new List<string>();

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                {
                    if (!store.TryDeposit(item, Owner, out var reason))
                        refusals.Add(reason);
                }
            });

            var classReadsForTheDelivery = backend.ClassReads;
            var stackReadsForTheDelivery = backend.StackReads;

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            CollectionAssert.AreEqual(new List<string>(), refusals, "control: every bag must actually deposit");
            Assert.AreEqual(1, backend.Classes.Count, "control: 12 equivalent bags are ONE class row");
            Assert.AreEqual(12L, backend.Classes.Single().Count, "control: all 12 reached it");

            Assert.AreEqual(1, classReadsForTheDelivery,
                $"{items.Count} looped single-item class deposits must cost ONE whole-account class re-read, at the window's close. Without the window each one re-reads, which is {items.Count}.");

            Assert.AreEqual(0, stackReadsForTheDelivery, "a class-only delivery must not re-read the stack ledger");
        }

        /// <summary>
        /// THE DANGEROUS DIRECTION of deferring the re-read, and the reason line PRESENCE is carried on the
        /// window rather than simply left stale.
        ///
        /// While the re-read is deferred, the in-memory ledger does not yet show the lines this window has
        /// created. Left at that, EntryCountLocked would answer with the pre-window count for every item of
        /// the loop and the cap would ADMIT one new line per item - an account at 4 of 5 entries would take
        /// three new lines and finish at 7. Carrying the claims makes the second and third refusals happen
        /// where they should.
        ///
        /// The REFUSALS and the final entry count are what pin the breach. The message is asserted too, for a
        /// different defect: the figure it prints must include the claims, or a player refused at their cap
        /// is told they hold fewer entries than it - here "4 of 5" while being refused for being at 5, which
        /// reads as a broken cap rather than as a full vault.
        /// </summary>
        [TestMethod]
        public void DepositWindow_DistinctNewLines_StillCannotBreachTheEntryCap()
        {
            const uint firstNew = 7351;
            const uint secondNew = 7352;
            const uint thirdNew = 7353;

            // Four lines already drawn, and room for exactly one more.
            for (uint held = 7340; held < 7344; held++)
                SeedLedger(held, 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 5));

            world.PristineResult = true;

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "control: the account starts at four of its five entries");

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(firstNew, 1, 100),
                FakeVaultWorld.MakeStack(secondNew, 1, 100),
                FakeVaultWorld.MakeStack(thirdNew, 1, 100),
            };

            var accepted = new List<bool>();
            var reasons = new List<string>();

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                {
                    accepted.Add(store.TryDeposit(item, Owner, out var reason));
                    reasons.Add(reason);
                }
            });

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            Assert.IsTrue(accepted[0], "the fifth entry fits");
            Assert.IsFalse(accepted[1], "the sixth does not, and the deferred re-read must not hide that");
            Assert.IsFalse(accepted[2], "nor the seventh");

            // The claims are part of what the account HOLDS: the row for the fifth line is in the database,
            // it is only missing from the in-memory count until the window's re-read. Reporting
            // EntryCountLocked() alone would print "4 of 5" here.
            Assert.IsTrue(AccountVaultStore.IsFullMessage(reasons[1]), $"the refusal must be the full-vault message, got: {reasons[1]}");
            StringAssert.Contains(reasons[1], "5 of 5 entries",
                $"a refused player must be told the count that actually refused them, claims included; got: {reasons[1]}");

            Assert.AreEqual(1L, LedgerCount(firstNew));
            Assert.AreEqual(0L, LedgerCount(secondNew), "a line the cap refused must never reach the ledger");
            Assert.AreEqual(0L, LedgerCount(thirdNew));

            Assert.AreEqual(5, store.EntryCount, "and the account ends AT its cap, never past it");
        }

        /// <summary>
        /// A COALESCED RE-READ THAT FAILS must leave the store refusing, exactly as a per-call phase-D
        /// failure does, and must never escape the window's dispose - it runs from a finally on a delivery
        /// path that is deciding who owns which item.
        ///
        /// The deposits STAND either way: their credits committed and their items are gone. This is
        /// guarantee 4's own failure mode, not a new one.
        /// </summary>
        [TestMethod]
        public void DepositWindow_WhenTheCoalescedReReadFails_TheStoreRefusesRatherThanServingAStaleCount()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "control: the store loaded before the read was broken");

            var items = new List<WorldObject>();

            for (var i = 0; i < 4; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 1, 100));

            backend.FailStackRead = true;

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                    store.TryDeposit(item, Owner, out _);
            });

            Assert.IsNull(thrown, "a failed coalesced re-read must be logged, never thrown out of the dispose");

            Assert.AreEqual(4L, LedgerCount(LedgerWcid), "THE DEPOSITS STAND - the credits committed and the items are gone");
            Assert.AreEqual(4, world.Destroyed.Count);

            Assert.IsFalse(store.TryCheckReady(out var failReason),
                "the store must refuse until a later read succeeds rather than serve a count it knows is stale");

            Assert.AreEqual(AccountVaultStore.UnavailableMessage, failReason);

            // And it heals: the flag stays set, so the next reader re-reads.
            backend.FailStackRead = false;

            Assert.IsTrue(store.TryCheckReady(out _), "a later successful read clears the refusal");
            Assert.AreEqual(4L, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Ledger).Count);
        }

        /// <summary>
        /// The window must not turn a present row back into an absent one for the DAO's oracle. A row the
        /// window itself created is proved present by the credit the database reported applied, so the next
        /// deposit of that key takes the guarded UPDATE branch rather than INSERT..ON DUPLICATE KEY UPDATE.
        ///
        /// Not a correctness bug if it regresses - ODKU lands the same delta - but every such call burns an
        /// AUTO_INCREMENT id for a row that already exists, and account_vault_stack.id is int unsigned.
        /// </summary>
        [TestMethod]
        public void DepositWindow_ARowItCreated_IsStillKnownPresentToTheNextDepositInTheWindow()
        {
            world.PristineResult = true;

            var store = NewStore();

            var items = new List<WorldObject>();

            for (var i = 0; i < 5; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 1, 100));

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                    store.TryDeposit(item, Owner, out _);
            });

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            Assert.AreEqual(5L, LedgerCount(LedgerWcid), "control: every unit landed");

            Assert.AreEqual(1, backend.StackBatchKeysOracleCalledAbsent,
                "only the FIRST deposit may route to the upsert branch; the four after it must find the row already known present");
        }

        /// <summary>
        /// A RE-READ THAT HAPPENS MID-WINDOW MUST TAKE THAT TABLE'S CLAIMS WITH IT, or the account is charged
        /// twice for the same line for the rest of the delivery.
        ///
        /// The claims describe lines the in-memory ledger cannot see yet. The moment the ledger IS re-read
        /// they are in it, and EntryCountLocked() counts them itself - so a surviving claim is added on top
        /// of a count that already includes it. The account is then refused at a cap it has not reached, and
        /// on the market path ClassifyDepositFailure turns that into MarketError.VaultFull and a paid
        /// delivery into a partial sale.
        ///
        /// NO SECOND THREAD IS NEEDED, which is what makes this cheap to pin. EntryCount passes
        /// tolerateDeferredReload: false, so calling it from inside the window - on the very thread running
        /// the delivery - performs the re-read itself.
        ///
        /// THE NUMBERS. 4 lines held, cap 6. Deposit one new wcid (5 lines), call EntryCount (which re-reads
        /// and returns 5), deposit a second distinct new wcid (the 6th line, which fits). Right: accepted,
        /// and the account ends at 6. Claims left behind: EntryCountLocked() is 5 and the stale claim adds 1,
        /// so the check is 5 + 1 + 1 against a cap of 6 and the deposit is refused at "6 of 6" - the account
        /// ends at 5 with a line it had room for turned away.
        /// </summary>
        [TestMethod]
        public void DepositWindow_AReReadMidWindow_DropsThatTablesClaimsSoTheCapIsNotChargedTwice()
        {
            const uint firstNew = 7371;
            const uint secondNew = 7372;

            for (uint held = 7340; held < 7344; held++)
                SeedLedger(held, 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 6));

            world.PristineResult = true;

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "control: the account starts at four of its six entries");

            var first = FakeVaultWorld.MakeStack(firstNew, 1, 100);
            var second = FakeVaultWorld.MakeStack(secondNew, 1, 100);

            var firstAccepted = false;
            var secondAccepted = false;
            string secondReason = null;
            var countMidWindow = -1;

            var thrown = InWindow(store, () =>
            {
                firstAccepted = store.TryDeposit(first, Owner, out _);

                // The re-read, forced from inside the window by an ordinary reader on this same thread.
                countMidWindow = store.EntryCount;

                secondAccepted = store.TryDeposit(second, Owner, out secondReason);
            });

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            Assert.IsTrue(firstAccepted, "control: the fifth line fits");

            Assert.AreEqual(5, countMidWindow,
                "control: reading the count inside the window really did re-read the ledger, or this test proves nothing");

            Assert.IsTrue(secondAccepted,
                $"the sixth line fits and must be accepted: the mid-window re-read already counted the fifth, so its claim must not be counted again. Refused with: {secondReason}");

            Assert.AreEqual(1L, LedgerCount(secondNew), "and its units reached the ledger");

            Assert.AreEqual(6, store.EntryCount, "the account ends at exactly six, its cap, with nothing turned away that fit");
        }

        /// <summary>
        /// THE OTHER SIDE OF THE SAME RE-READ, and the dangerous one. Dropping a table's claims when it
        /// re-reads is right only once those claims' rows are readable. A claim is recorded in phase A and
        /// its row is created in phase B, so a re-read landing BETWEEN the two sees neither the row nor the
        /// claim, the account is charged less than it holds, and the next deposit is admitted PAST its cap.
        /// VaultDepositWindow.ClaimsInFlight is what holds the drop back over that interval.
        ///
        /// WHY THE HOOK. The window between claim and credit is not reachable from a test that only calls
        /// TryDeposit: it opens and closes inside one call. FakeVaultBackend.OnBeforeStackBatch runs at the
        /// top of the batch statement, before any delta is applied, which is exactly inside it. No second
        /// thread is needed - EntryCount passes tolerateDeferredReload: false, so calling it there performs
        /// the re-read on the deposit's own thread, which is how an ordinary panel read reaches this state
        /// in production.
        ///
        /// THE NUMBERS. 4 lines held, cap 6, three distinct new wcids deposited one at a time; the hook is
        /// armed for the SECOND batch only. Item 1 is the 5th line and fits. Item 2 claims the 6th line,
        /// and the hook re-reads before its credit lands, so the fresh ledger shows 5 lines - item 1's, not
        /// item 2's. Right: the claims survive, so item 3 is measured against 5 counted plus 2 claimed plus
        /// its own 1 against a cap of 6, and is REFUSED; the account ends at 6, which is the truth (4 held
        /// plus items 1 and 2). Claims dropped mid-flight: item 3 is measured against 5 plus 0 plus 1,
        /// which fits a cap of 6 exactly, so it is ACCEPTED and the account ends at SEVEN lines against a
        /// cap of six.
        ///
        /// The over-count this trades for is visible right here: item 1's line is counted twice for the
        /// length of item 2's batch, once in the re-read and once in its surviving claim. That can only
        /// refuse a deposit that would have fit, and it does not refuse one here, because item 3 genuinely
        /// does not fit.
        /// </summary>
        [TestMethod]
        public void DepositWindow_AReReadBetweenAClaimAndItsCredit_MustNotDropTheClaimAndLetTheCapBeBreached()
        {
            const uint firstNew = 7381;
            const uint secondNew = 7382;
            const uint thirdNew = 7383;

            for (uint held = 7340; held < 7344; held++)
                SeedLedger(held, 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 6));

            world.PristineResult = true;

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "control: the account starts at four of its six entries");

            var items = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(firstNew, 1, 100),
                FakeVaultWorld.MakeStack(secondNew, 1, 100),
                FakeVaultWorld.MakeStack(thirdNew, 1, 100),
            };

            var batchesSeen = 0;
            var countMidCredit = -1;

            // THE SECOND BATCH ONLY, so the re-read lands after item 2 has claimed its entry and before its
            // credit has run. Arming every batch would re-read before item 1 has claimed anything as well,
            // which is the benign case the neighbouring test already covers.
            backend.OnBeforeStackBatch = () =>
            {
                batchesSeen++;

                if (batchesSeen == 2)
                    countMidCredit = store.EntryCount;
            };

            var accepted = new List<bool>();
            var reasons = new List<string>();

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                {
                    accepted.Add(store.TryDeposit(item, Owner, out var reason));
                    reasons.Add(reason);
                }
            });

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            Assert.AreEqual(5, countMidCredit,
                "control: the hook really did re-read mid-credit, and saw item 1's line but not item 2's. Without that this test proves nothing.");

            Assert.IsTrue(accepted[0], "control: the fifth line fits");
            Assert.IsTrue(accepted[1], $"control: the sixth line fits too; refused with: {reasons[1]}");

            Assert.IsFalse(accepted[2],
                "the seventh line does NOT fit, and a re-read that landed between the sixth line's claim and its credit must not be able to hide that");

            Assert.AreEqual(0L, LedgerCount(thirdNew), "a line the cap refused must never reach the ledger");

            Assert.AreEqual(6, store.EntryCount, "the account ends AT its cap of six, never past it");

            // Item 3 is refused in phase A, so it never reaches a batch statement at all: TWO of the three
            // deposits issue one. Asserted last because it is a corollary of the refusal above rather than
            // the point, and putting it first would hide that refusal behind a count.
            Assert.AreEqual(2, batchesSeen, "the refused item must not have issued a batch statement");
        }

        /// <summary>
        /// THE CARRIED CLAIM KEYS, which nothing else in this file exercises.
        ///
        /// Claims.Keys is what makes N items of ONE new line cost ONE entry between them. Every other window
        /// test either has cap headroom to spare (so nothing is refused whatever the keys do) or uses
        /// DISTINCT wcids (so Keys.Contains never matches), and an implementation that carries the COUNTER
        /// but drops the KEYS passes all of them. Its real defect is this delivery: 12 bags of one new line
        /// into a buyer with room, refused from item 3 onward.
        ///
        /// SPEC-vault-batch-deposit.md section 5 step 4 flags this exact case - the claim is keyed on the
        /// LINE, not the item - as the one the enumerated cap cases do not reach.
        ///
        /// THE NUMBERS. 4 lines held, cap 6, 12 items of one new wcid. Right: all 12 accepted, because item 1
        /// claims the line and items 2..12 find its key, so the account is charged ONE entry and ends at 5.
        /// Keys dropped but Entries carried, which is the mutant this was measured against: item 1 sees
        /// 4 + 0 + 1 = 5 and is accepted, item 2 sees 4 + 1 + 1 = 6 and is still accepted, and item 3 sees
        /// 4 + 2 + 1 = 7 and is refused at "6 of 6" - two accepted, a ledger count of 2, ten refusals, and
        /// the first failing assertion is the accepted[] loop at ITEM 3.
        ///
        /// WHAT THIS TEST DOES NOT DISCRIMINATE, so that nobody reads it as covering more than it does: an
        /// implementation that carries NEITHER the keys nor the counter passes it at any cap, because every
        /// item then sees 4 + 0 + 1 = 5 against a cap of 6. That direction belongs to
        /// DepositWindow_DistinctNewLines_StillCannotBreachTheEntryCap, whose lines are distinct so the keys
        /// never match and only the counter can refuse anything. The cap of 6 here is headroom, not a
        /// discriminator: it keeps the correct implementation clear of its cap while the carried counter is
        /// still able to reach it.
        /// </summary>
        [TestMethod]
        public void DepositWindow_ManyItemsOfOneNewLine_ConsumeExactlyOneEntry()
        {
            const uint newLine = 7361;

            for (uint held = 7340; held < 7344; held++)
                SeedLedger(held, 1);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 6));

            world.PristineResult = true;

            var store = NewStore();

            Assert.AreEqual(4, store.EntryCount, "control: the account starts at four of its six entries");

            var items = new List<WorldObject>();

            for (var i = 0; i < 12; i++)
                items.Add(FakeVaultWorld.MakeStack(newLine, 1, 100));

            var accepted = new List<bool>();
            var reasons = new List<string>();

            var thrown = InWindow(store, () =>
            {
                foreach (var item in items)
                {
                    accepted.Add(store.TryDeposit(item, Owner, out var reason));
                    reasons.Add(reason);
                }
            });

            Assert.IsNull(thrown, $"the window must not throw: {thrown}");

            for (var i = 0; i < items.Count; i++)
                Assert.IsTrue(accepted[i], $"item {i + 1} of one new line must not be charged a second entry for it; refused with: {reasons[i]}");

            Assert.AreEqual(12L, LedgerCount(newLine), "control: every unit of the line reached the ledger");

            Assert.AreEqual(5, store.EntryCount, "12 items of one new line consume exactly ONE entry between them");
        }

        /// <summary>
        /// A NESTED window must be inert. Nothing nests today; the guard exists because an inner close would
        /// run the outer caller's re-read early and silently restore the per-item cost the outer window was
        /// opened to avoid.
        ///
        /// THE NUMBERS. Right: 0 reads when the inner disposable is disposed, 1 for the whole region. A real
        /// inner window instead: 1 read at the inner dispose and 2 in total.
        /// </summary>
        [TestMethod]
        public void DepositWindow_ANestedWindow_IsInertAndTheOuterCloseStillReadsOnce()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the reads under test are counted");

            var items = new List<WorldObject>();

            for (var i = 0; i < 3; i++)
                items.Add(FakeVaultWorld.MakeStack(LedgerWcid, 1, 100));

            backend.StackReads = 0;

            var readsAfterTheInnerDispose = -1;

            store.Enqueue(() =>
            {
                using (store.BeginDepositWindow())
                {
                    store.TryDeposit(items[0], Owner, out _);

                    using (store.BeginDepositWindow())
                        store.TryDeposit(items[1], Owner, out _);

                    readsAfterTheInnerDispose = backend.StackReads;

                    store.TryDeposit(items[2], Owner, out _);
                }
            }, out var thrown);

            Assert.IsNull(thrown, $"the nested window must not throw: {thrown}");

            Assert.AreEqual(3L, LedgerCount(LedgerWcid), "control: all three deposits landed");

            Assert.AreEqual(0, readsAfterTheInnerDispose,
                "disposing an inner window must perform NO re-read - the outer window still owns it");

            Assert.AreEqual(1, backend.StackReads, "and the outer close performs exactly one");
        }

        /// <summary>
        /// A THROW OUT OF THE WINDOW BODY, which is the case most likely to break the close: the re-read runs
        /// from the using's finally while an exception is already unwinding.
        ///
        /// Two things must hold, and they pull in opposite directions. The re-read must still happen exactly
        /// once, because the deposits before the throw committed and the store must not be left serving a
        /// count it knows is stale. And the body's own exception must reach the caller UNALTERED, because on
        /// the market path it is what VaultMarketItemStore classifies to decide who owns the item; an
        /// exception from the close replacing it would replace a custody decision with a vault diagnostic.
        ///
        /// THE NUMBERS. Right: 1 read, and the caller receives the very same exception instance. A close that
        /// throws instead: the caller receives a different exception, and AreSame fails.
        /// </summary>
        [TestMethod]
        public void DepositWindow_WhenTheBodyThrows_ReReadsOnceAndLetsTheOriginalExceptionThrough()
        {
            world.PristineResult = true;

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the reads under test are counted");

            var item = FakeVaultWorld.MakeStack(LedgerWcid, 1, 100);

            var boom = new InvalidOperationException("the delivery loop threw with a window open");

            backend.StackReads = 0;

            store.Enqueue(() =>
            {
                using (store.BeginDepositWindow())
                {
                    store.TryDeposit(item, Owner, out _);

                    throw boom;
                }
            }, out var thrown);

            Assert.AreSame(boom, thrown,
                "the window's close must not replace the body's exception - that exception is the caller's custody signal");

            Assert.AreEqual(1L, LedgerCount(LedgerWcid), "control: the deposit before the throw committed");

            Assert.AreEqual(1, backend.StackReads,
                "and the re-read still runs exactly once, from the using's finally, so the store is not left stale");

            Assert.IsTrue(store.TryCheckReady(out _), "the store is usable again afterwards");
        }

        /// <summary>
        /// A window opened OFF the mutation queue is refused. The window carries state that only the queue
        /// serializes - the claims and the carried presence - so one opened from an arbitrary thread would
        /// hand two callers the same sets.
        ///
        /// THE NUMBERS. Right: throws InvalidOperationException. The assertion removed: returns a disposable
        /// and the test fails with "no exception thrown".
        /// </summary>
        [TestMethod]
        public void DepositWindow_OpenedOffTheMutationQueue_IsRefused()
        {
            var store = NewStore();

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => store.BeginDepositWindow(),
                "a deposit window must only be opened from inside store.Enqueue");

            StringAssert.Contains(ex.Message, "mutation queue", $"and must say so; got: {ex.Message}");
        }
    }
}

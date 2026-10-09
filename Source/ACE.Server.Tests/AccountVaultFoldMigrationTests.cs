using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The OPERATOR-DRIVEN class-storage migration: AccountVaultFoldMigration, and the completion
    /// statement that is the whole reason it exists.
    ///
    /// WHAT THESE TESTS ARE REALLY ABOUT. This is the ONLY way a fold happens - a background rotation on
    /// the world heartbeat used to drift stores into the counted tier, and it was retired once this
    /// command had finished the migration on prod (2026-09-26). It was paced for drift, not for finishing,
    /// so an operator needed a way to drain the fleet in one window and, far more importantly, a way to be
    /// TOLD whether it actually finished. The failure being guarded against has already happened once: a
    /// stage run was called complete because the fold's log roll-ups went quiet, while the two largest
    /// vaults on the shard had folded zero. Quiet is what an idling fold and a finished migration have in
    /// common, and "zero folded" is what a cold store, a kill switch, a zero batch size and a refusing
    /// ledger all report.
    ///
    /// So the headline is derived from two INDEPENDENT facts - every account the index named was visited,
    /// and every one of them reported Exhausted - and several of the tests below exist purely to prove
    /// that each of those clauses can fail on its own.
    ///
    /// Everything is driven through AccountVaultFoldMigration.RunSynchronously over fake-backed stores,
    /// so none of it needs a thread, a world loop or a database.
    /// </summary>
    [TestClass]
    public class AccountVaultFoldMigrationTests
    {
        private const uint BagWcid = 21013;

        /// <summary>
        /// Account ids are per-test rather than shared, because a store built here is never registered
        /// with AccountVaultManager and must not be confusable with one that is.
        /// </summary>
        private static uint nextAccount = 7300;

        private static TimeSpan savedReadyTimeout;
        private static TimeSpan savedReadyPollInterval;
        private static int savedMaxBatches;

        [TestInitialize]
        public void Setup()
        {
            // Every key this class's code path reads, seeded here rather than relied on from another test
            // class: PropertyManager's caches are process-wide, so a class that reads a key it never
            // seeded passes only when some earlier class in the run happened to seed it.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();

            // VaultFoldProfile's counters are process-static, exactly like PropertyManager's caches.
            VaultFoldProfile.ResetForTest();

            // The migration's waits and its safety net are process-static and mutable for exactly this
            // reason. Saved here and restored in Cleanup, so no test in this class or any later one
            // inherits a shortened timeout.
            savedReadyTimeout = AccountVaultFoldMigration.ReadyTimeout;
            savedReadyPollInterval = AccountVaultFoldMigration.ReadyPollInterval;
            savedMaxBatches = AccountVaultFoldMigration.MaxBatchesPerAccount;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AccountVaultFoldMigration.ReadyTimeout = savedReadyTimeout;
            AccountVaultFoldMigration.ReadyPollInterval = savedReadyPollInterval;
            AccountVaultFoldMigration.MaxBatchesPerAccount = savedMaxBatches;

            AccountVaultFoldMigration.EndRun();

            VaultFoldProfile.ResetForTest();

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        // ---------------------------------------------------------------- fixtures

        /// <summary>
        /// One account: its own backend, its own world source, one vault container already holding
        /// stored biotas, and a store over the two. Nothing is deposited - the bags are seeded straight
        /// into the container, which is the state the migration exists to clean up.
        ///
        /// The store starts COLD on purpose. The migration's job includes loading it, and a fixture that
        /// pre-warmed would quietly stop testing that.
        /// </summary>
        private sealed class FoldFixture
        {
            public readonly uint AccountId;
            public readonly FakeVaultBackend Backend = new FakeVaultBackend();
            public readonly FakeVaultWorld World = new FakeVaultWorld();
            public readonly Container Container;
            public readonly AccountVaultStore Store;

            /// <summary>
            /// The classifiable salvage bags, in the order they were seeded.
            ///
            /// SEED ORDER IS THE REVERSE OF FOLD ORDER, so never identify a folded or refused bag by its
            /// index here. The LAST bag seeded is the FIRST one folded: the seeding loop calls
            /// Container.TryAddToInventory with no placement position, which inserts at position 0 and
            /// renumbers every item already in the container (Container.cs:566-570), while the store reads
            /// its candidates in PlacementPosition order (AccountVaultStore.EnumerateStoredItemsLocked).
            ///
            /// This cost a debugging round: a test asserting a put-back on Bags[1] failed because Bags[1]
            /// had already folded on the pass before, and every other assertion in that test passed, so
            /// the failure read as a put-back regression rather than as a wrong index. Identify the bag
            /// you mean from observed state instead - which bag is in World.Destroyed, which guid the
            /// container still holds.
            /// </summary>
            public readonly List<WorldObject> Bags = new List<WorldObject>();

            /// <summary>Items the predicate refuses, so a fixture can hold vault bulk that never folds.</summary>
            public readonly List<WorldObject> Unfoldable = new List<WorldObject>();

            public FoldFixture(params int[] bagStructures)
            {
                AccountId = nextAccount++;

                World.PristineResult = false;
                World.ClassifyResult = true;

                Container = FakeVaultWorld.MakeContainer(255);

                World.Containers[Container.Guid.Full] = Container;

                Backend.Vaults.Add(new ShardAccountVault
                {
                    Id = 9900 + AccountId,
                    AccountId = AccountId,
                    ContainerGuid = Container.Guid.Full,
                    CreatedAt = new DateTime(2026, 1, 1),
                });

                foreach (var structure in bagStructures)
                {
                    var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, structure, 7, 10, 100, $"Salvage ({structure})");

                    Bags.Add(bag);

                    Assert.IsTrue(Container.TryAddToInventory(bag), "could not seed a vault bag");
                }

                Store = new AccountVaultStore(AccountId, Backend, World);
            }

            /// <summary>Adds one item the class predicate refuses. It must keep its biota through every pass.</summary>
            public WorldObject AddUnfoldable()
            {
                var filler = FakeVaultWorld.MakeStack(8000, 1, 100);

                World.UnclassifiableGuids.Add(filler.Guid.Full);

                Assert.IsTrue(Container.TryAddToInventory(filler), "could not seed an unfoldable vault item");

                Unfoldable.Add(filler);

                return filler;
            }

            public long ClassRowCount => Backend.Classes.Where(c => c.AccountId == AccountId).Sum(c => c.Count);
        }

        /// <summary>A storeFor seam over a fixed set of fixtures. Unknown accounts answer null, which is the production answer for an account with no live store.</summary>
        private static Func<uint, AccountVaultStore> StoreFor(params FoldFixture[] fixtures)
        {
            var byAccount = fixtures.ToDictionary(f => f.AccountId, f => f.Store);

            return accountId => byAccount.TryGetValue(accountId, out var store) ? store : null;
        }

        private static VaultFoldAccountResult ResultFor(VaultFoldMigrationReport report, FoldFixture fixture)
        {
            var result = report.Accounts.SingleOrDefault(a => a.AccountId == fixture.AccountId);

            Assert.IsNotNull(result, $"the report has no line for account {fixture.AccountId}");

            return result;
        }

        // ---------------------------------------------------------------- 1. the happy path

        /// <summary>
        /// The base case: one account whose vault holds foldable bags of TWO classes is migrated to
        /// completion in one run.
        ///
        /// Two classes rather than one deliberately. The store drains one class before starting another
        /// (the owner's ruling), so a single-class fixture would finish in one batch and would never
        /// exercise the case the per-account loop exists for - a pass that folds nothing, reports
        /// MoreToDo because it merely released its target class, and must not be read as completion.
        ///
        /// The audit assertion is what pins WHICH path folded these. "Folded == N" alone would also be
        /// true of a run that had somehow deposited them, and a deposit and a fold both end in a class
        /// row; only the audit action distinguishes them, and the Deposit count is asserted at zero so
        /// the Fold count is not merely the larger of two numbers.
        /// </summary>
        [TestMethod]
        public void Migration_FoldsEveryStoredBag_AndReportsTheAccountComplete()
        {
            var fixture = new FoldFixture(10, 20, 10, 20, 10, 20);

            Assert.IsFalse(fixture.Store.IsWarm, "the fixture only means anything if the store starts cold");

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 100,
                                                                   shouldStop: null);

            Assert.AreEqual("COMPLETE", report.Headline, report.Render());
            Assert.IsTrue(report.IsComplete);

            Assert.AreEqual(1, report.AccountsOwningAVault);
            Assert.AreEqual(1, report.AccountsVisited);
            Assert.AreEqual(1, report.AccountsComplete);
            Assert.AreEqual(6, report.Folded);

            var result = ResultFor(report, fixture);

            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, result.Terminal, report.Render());
            Assert.AreEqual(6, result.Folded);
            Assert.IsTrue(result.Batches >= 2, $"two interleaved classes cannot drain in one batch; the run took {result.Batches}");

            // The migration loaded the store itself. That is the point of running off the world loop.
            Assert.IsTrue(fixture.Store.IsWarm, "the migration must load the store it was asked to migrate");
            Assert.IsTrue(fixture.Store.FoldHasNothingLeft, "and the store itself must agree it has nothing left");

            foreach (var bag in fixture.Bags)
            {
                Assert.IsTrue(fixture.World.Destroyed.Contains(bag), "a folded bag's biota is destroyed");
                Assert.IsFalse(fixture.Container.Inventory.ContainsKey(bag.Guid), "and it leaves the vault container");
            }

            Assert.AreEqual(6, fixture.ClassRowCount, "every bag must be accounted for on a class row");
            Assert.AreEqual(2, fixture.Backend.Classes.Count(c => c.AccountId == fixture.AccountId), "two structures are two classes");

            // Which PATH did this: a fold, never a deposit.
            Assert.AreEqual(6, fixture.Backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Fold));
            Assert.AreEqual(0, fixture.Backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Deposit),
                "nothing here is a player deposit, so a Deposit row would mean the count above came from the wrong path");
        }

        // ---------------------------------------------------------------- 2. a store that never loads

        /// <summary>
        /// An account whose store can never load is reported NOT WARM and the fleet headline is
        /// INCOMPLETE. It is never reported as finished, and that is the case that matters: an unloadable
        /// vault holds an unknown number of foldable items, so "nothing folded" says nothing at all.
        ///
        /// WHICH PATH PRODUCED NotWarm IS PINNED, because two can. The migration's own readiness poll
        /// times out and reports NotWarm without ever enqueueing anything; the store's fold pass has its
        /// own IsWarmLocked gate that reports the same word from inside a batch. Batches == 0 and zero
        /// predicate calls are what say this was the poll, not the gate - if the poll had wrongly
        /// proceeded, a batch would have run and been counted.
        ///
        /// The positive control at the end is what makes the zeros mean anything: the SAME fixture, with
        /// the index read repaired, loads and folds. Without it this test would pass just as happily
        /// against a fixture that was never foldable.
        /// </summary>
        [TestMethod]
        public void Migration_OnAStoreThatNeverLoads_ReportsNotWarm_AndTheFleetIsIncomplete()
        {
            AccountVaultFoldMigration.ReadyTimeout = TimeSpan.FromMilliseconds(80);
            AccountVaultFoldMigration.ReadyPollInterval = TimeSpan.FromMilliseconds(10);

            var fixture = new FoldFixture(10, 10, 10);

            // The index read FAILS, which is how a store stays permanently unloadable: the store treats
            // null as a database failure rather than as an empty account, so indexLoaded never flips.
            fixture.Backend.FailVaultRead = true;

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 100,
                                                                   shouldStop: null);

            var result = ResultFor(report, fixture);

            Assert.AreEqual(VaultFoldAccountTerminal.NotWarm, result.Terminal, report.Render());

            Assert.AreEqual(0, result.Batches, "the readiness poll must time out BEFORE anything is enqueued - a batch here would mean the store's own warm gate produced this word instead");
            Assert.AreEqual(0, fixture.World.DescribeClassCalls, "and nothing may run the predicate against a store that never loaded");
            Assert.AreEqual(0, fixture.Backend.ClassAdjustCalls, "nor write to the class ledger");

            Assert.AreEqual("INCOMPLETE", report.Headline, report.Render());
            Assert.IsFalse(report.IsComplete);
            Assert.AreEqual(1, report.AccountsVisited, "the account WAS visited; it simply could not be loaded");
            Assert.AreEqual(0, report.AccountsComplete);
            Assert.AreEqual(0, report.Folded);

            foreach (var bag in fixture.Bags)
                Assert.IsFalse(fixture.World.Destroyed.Contains(bag), "nothing may leave a vault the migration could not read");

            // POSITIVE CONTROL. Same fixture, same bags, same store; only the index read is repaired.
            fixture.Backend.FailVaultRead = false;

            var second = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                    StoreFor(fixture),
                                                                    batch: 100,
                                                                    shouldStop: null);

            Assert.AreEqual("COMPLETE", second.Headline, second.Render());
            Assert.AreEqual(3, second.Folded, "the identical fixture must fold once it can load, or the zeros above proved nothing");
            Assert.IsTrue(ResultFor(second, fixture).Batches > 0);
        }

        // ---------------------------------------------------------------- 3. a stop mid-run

        /// <summary>
        /// A stop request takes effect BETWEEN batches, and the run says so: the account it stopped in is
        /// terminal Stopped, the accounts after it are never visited, and the headline is INCOMPLETE.
        ///
        /// The stop predicate is keyed to OBSERVED PROGRESS rather than to a call count, so the test
        /// cannot quietly start measuring something else if the number of places shouldStop is consulted
        /// ever changes: it returns true as soon as a class row exists, which is exactly "one batch has
        /// folded something".
        ///
        /// The assertion that pins the interruption is that FOUR bags are still in the vault. A run that
        /// had folded everything and then been labelled Stopped would show the same terminal and the same
        /// headline, and nothing else in the report would distinguish the two.
        /// </summary>
        [TestMethod]
        public void Migration_WhenStopped_ReportsStopped_AndLeavesTheRestOfTheFleetUnvisited()
        {
            var first = new FoldFixture(10, 10, 10, 10, 10);
            var second = new FoldFixture(10, 10);

            bool ShouldStop() => first.Backend.Classes.Any(c => c.AccountId == first.AccountId && c.Count >= 1);

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { first.AccountId, second.AccountId },
                                                                   StoreFor(first, second),
                                                                   batch: 1,
                                                                   shouldStop: ShouldStop);

            var result = ResultFor(report, first);

            Assert.AreEqual(VaultFoldAccountTerminal.Stopped, result.Terminal, report.Render());
            Assert.AreEqual(1, result.Batches, "the stop must land after the first batch");
            Assert.AreEqual(1, result.Folded);

            Assert.AreEqual(4, fixtureBagsStillStored(first), "four bags must still be in the vault, or the run was not interrupted at all");

            Assert.AreEqual("INCOMPLETE", report.Headline, report.Render());
            Assert.IsFalse(report.IsComplete);
            Assert.IsTrue(report.StoppedEarly, "the fleet walk was cut short, which is its own reason for INCOMPLETE");

            Assert.AreEqual(2, report.AccountsOwningAVault);
            Assert.AreEqual(1, report.AccountsVisited);
            Assert.AreEqual(0, report.AccountsComplete);

            Assert.IsFalse(report.Accounts.Any(a => a.AccountId == second.AccountId),
                "an account the run never reached must not appear with a terminal of its own");

            Assert.AreEqual(0, second.World.DescribeClassCalls, "and must not have been touched");
            Assert.AreEqual(2, fixtureBagsStillStored(second));
        }

        private static int fixtureBagsStillStored(FoldFixture fixture)
        {
            return fixture.Bags.Count(b => fixture.Container.Inventory.ContainsKey(b.Guid));
        }

        // ---------------------------------------------------------------- 4. idempotence

        /// <summary>
        /// A second run over the same stores folds NOTHING, reports every account Exhausted, and says
        /// COMPLETE.
        ///
        /// Both halves matter, and the second is the expensive one. "Folds nothing" alone would also be
        /// true of a migration that re-ran the class predicate over every stored item on every run and
        /// simply found nothing to do - which is the shape that would put the most expensive call on this
        /// path over the whole fleet every time an operator ran it. The predicate-call assertion is what
        /// separates the two.
        ///
        /// Batches on the second run is asserted NON-zero on purpose: a COMPLETE built out of zero
        /// batches would mean the accounts were never actually asked, and the report cannot tell the
        /// difference from its headline alone.
        /// </summary>
        [TestMethod]
        public void Migration_RunTwice_FoldsNothingTheSecondTime_AndStillReportsComplete()
        {
            var first = new FoldFixture(10, 20, 10);
            var second = new FoldFixture(30, 30);

            var accounts = new[] { first.AccountId, second.AccountId };
            var storeFor = StoreFor(first, second);

            var initial = AccountVaultFoldMigration.RunSynchronously(accounts, storeFor, batch: 100, shouldStop: null);

            Assert.AreEqual("COMPLETE", initial.Headline, initial.Render());
            Assert.AreEqual(5, initial.Folded);

            var adjustsAfterFirst = first.Backend.ClassAdjustCalls + second.Backend.ClassAdjustCalls;

            first.World.ResetClassCalls();
            second.World.ResetClassCalls();

            var again = AccountVaultFoldMigration.RunSynchronously(accounts, storeFor, batch: 100, shouldStop: null);

            Assert.AreEqual("COMPLETE", again.Headline, again.Render());
            Assert.IsTrue(again.IsComplete);
            Assert.AreEqual(2, again.AccountsComplete);
            Assert.AreEqual(0, again.Folded, "a second run over a migrated fleet must fold nothing");

            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, ResultFor(again, first).Terminal);
            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, ResultFor(again, second).Terminal);

            Assert.IsTrue(again.Batches > 0, "the second run must actually have asked each store, or its COMPLETE is vacuous");

            Assert.AreEqual(0, first.World.DescribeClassCalls + second.World.DescribeClassCalls,
                "a migrated store must answer from its own latch - re-running the predicate over the fleet is the cost this tier cannot afford");

            Assert.AreEqual(adjustsAfterFirst, first.Backend.ClassAdjustCalls + second.Backend.ClassAdjustCalls,
                "and it must do no database work at all");
        }

        // ---------------------------------------------------------------- 5. the anti-false-completion guard

        /// <summary>
        /// THE TEST THIS WHOLE CLASS IS FOR. Three accounts are visited and all three report Exhausted,
        /// and the run is still INCOMPLETE - because the index named FOUR accounts and one of them was
        /// never opened.
        ///
        /// Without the fleet-count clause this run would report COMPLETE: every terminal in the report is
        /// Exhausted, every batch succeeded, nothing failed, and the fourth account is invisible to any
        /// test that only looks at what the report contains. That is the exact shape of the stage failure
        /// this guard exists for - a migration declared finished over vaults it had never reached.
        ///
        /// THE DISCRIMINATING CONTROL IS THE SECOND HALF. The same three fixtures, in the same state,
        /// with an index naming three accounts instead of four, report COMPLETE. So the INCOMPLETE above
        /// can only come from the count, and not from any lingering property of the fixtures.
        /// </summary>
        [TestMethod]
        public void Migration_WhenTheIndexNamesMoreAccountsThanWereVisited_IsIncompleteAlthoughEveryVisitedAccountIsExhausted()
        {
            var a = new FoldFixture(10, 10);
            var b = new FoldFixture(20);
            var c = new FoldFixture(30, 30, 30);

            // The fourth id has no store, which is what AccountVaultManager answers for an account whose
            // store could not be built. It is in the index, so it counts towards the fleet.
            var missing = nextAccount++;

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { a.AccountId, b.AccountId, c.AccountId, missing },
                                                                   StoreFor(a, b, c),
                                                                   batch: 100,
                                                                   shouldStop: null);

            Assert.AreEqual(4, report.AccountsOwningAVault);
            Assert.AreEqual(3, report.AccountsVisited);
            Assert.AreEqual(3, report.AccountsComplete);
            Assert.AreEqual(6, report.Folded);

            // Every account that WAS visited finished. There is nothing else in this report that could
            // make it incomplete, which is the point.
            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, ResultFor(report, a).Terminal);
            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, ResultFor(report, b).Terminal);
            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, ResultFor(report, c).Terminal);

            Assert.IsFalse(report.IsComplete);
            Assert.AreEqual("INCOMPLETE", report.Headline);

            var rendered = report.Render();

            Assert.IsTrue(rendered.StartsWith("[VAULT] fold migration INCOMPLETE:", StringComparison.Ordinal),
                $"the grep target must be the first thing on the line; got: {rendered}");

            Assert.IsTrue(rendered.Contains("never visited"), $"the report must say an account was missed; got: {rendered}");

            var missingLine = report.Accounts.Single(x => x.AccountId == missing);

            Assert.AreEqual(VaultFoldAccountTerminal.StoreUnavailable, missingLine.Terminal);
            Assert.AreEqual(0, missingLine.Batches);

            // DISCRIMINATING CONTROL: the same three fixtures, an index of three, and the answer flips.
            var honest = AccountVaultFoldMigration.RunSynchronously(new[] { a.AccountId, b.AccountId, c.AccountId },
                                                                   StoreFor(a, b, c),
                                                                   batch: 100,
                                                                   shouldStop: null);

            Assert.AreEqual("COMPLETE", honest.Headline, honest.Render());
            Assert.AreEqual(3, honest.AccountsVisited);
            Assert.AreEqual(3, honest.AccountsComplete);
            Assert.AreEqual(0, honest.Folded, "there was nothing left to fold, so the flip came from the count and not from new work");
        }

        // ---------------------------------------------------------------- 6. the kill switch

        /// <summary>
        /// With account_vault_class_storage OFF the run is REFUSED by name, touches nothing, and is never
        /// reported complete.
        ///
        /// This is the same failure as test 5 arriving by a different road. With the switch off every
        /// batch examines nothing and folds nothing on every account, so a run with no precondition check
        /// would walk the whole fleet, do no work, and - if completion were read off "nothing folded" -
        /// call the migration finished. The refusal is what turns that into an answer an operator can
        /// act on, and naming the tunable is what stops them having to read source to find out which
        /// switch.
        ///
        /// The positive control is the same fixture with the switch back on. Without it the zeros below
        /// would be equally true of a fixture holding nothing foldable.
        /// </summary>
        [TestMethod]
        public void Migration_WithClassStorageOff_IsRefusedByName_AndNeverReportsComplete()
        {
            var fixture = new FoldFixture(10, 10, 10);

            VaultClassTestConfig.Seed(classStorageEnabled: false);

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 100,
                                                                   shouldStop: null);

            Assert.IsNotNull(report.Refusal, "a run with the tier switched off must refuse rather than report a zero");
            Assert.IsTrue(report.Refusal.Contains("account_vault_class_storage"),
                $"the refusal must name the tunable; got: {report.Refusal}");

            Assert.AreEqual("INCOMPLETE", report.Headline, report.Render());
            Assert.IsFalse(report.IsComplete);

            Assert.AreEqual(0, report.AccountsVisited, "nothing may be visited");
            Assert.AreEqual(0, report.AccountsComplete);
            Assert.AreEqual(1, report.AccountsOwningAVault, "the fleet is still reported, so the headline fails on the count as well as on the refusal");

            Assert.AreEqual(0, fixture.World.DescribeClassCalls);
            Assert.AreEqual(0, fixture.Backend.ClassAdjustCalls);
            Assert.AreEqual(3, fixtureBagsStillStored(fixture));

            Assert.IsTrue(report.Render().Contains("REFUSED"), report.Render());

            // The same refusal reaches an operator through the start gate, which must also not take the
            // one-run-at-a-time flag on its way out.
            Assert.IsFalse(AccountVaultFoldMigration.TryBeginRun(out var refusal));
            Assert.IsTrue(refusal.Contains("account_vault_class_storage"), refusal);
            Assert.IsFalse(AccountVaultFoldMigration.IsRunning, "a refused start must not leave the run flag set");

            // POSITIVE CONTROL: switch on, same fixture, same bags.
            VaultClassTestConfig.Seed();

            var after = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 100,
                                                                   shouldStop: null);

            Assert.IsNull(after.Refusal);
            Assert.AreEqual("COMPLETE", after.Headline, after.Render());
            Assert.AreEqual(3, after.Folded, "the identical fixture must fold with the switch on, or the zeros above proved nothing");
        }

        // ---------------------------------------------------------------- the other two start refusals

        /// <summary>
        /// The remaining two preconditions, both of which exist to stop two things reading and writing
        /// the same stores at once: a second migration, and a /vaultclassdryrun or /vaultclassinspect
        /// scan.
        ///
        /// Each refusal is followed by the SUCCESS of the same call once the blocker is released, because
        /// a guard that refuses unconditionally is indistinguishable from one that works, and would break
        /// the feature outright while passing a refusal-only test.
        /// </summary>
        [TestMethod]
        public void TryBeginRun_RefusesASecondRun_AndRefusesWhileAClassScanIsInFlight()
        {
            Assert.IsFalse(AccountVaultFoldMigration.IsRunning, "another test leaked the run flag");

            // A scan in flight.
            using (var scan = VaultClassCommands.TakeScanFlagForTest())
            {
                Assert.IsNotNull(scan, "the class scan flag was already held; another test leaked it");

                Assert.IsFalse(AccountVaultFoldMigration.TryBeginRun(out var scanRefusal));
                Assert.IsTrue(scanRefusal.Contains("scan"), scanRefusal);
                Assert.IsFalse(AccountVaultFoldMigration.IsRunning, "a refused start must not take the run flag");
            }

            // Released: the identical call now succeeds, which is what makes the refusal above mean
            // something.
            try
            {
                Assert.IsTrue(AccountVaultFoldMigration.TryBeginRun(out var noRefusal), noRefusal);
                Assert.IsNull(noRefusal);
                Assert.IsTrue(AccountVaultFoldMigration.IsRunning);

                // A second run while the first holds the flag.
                Assert.IsFalse(AccountVaultFoldMigration.TryBeginRun(out var busyRefusal));
                Assert.IsTrue(busyRefusal.Contains("already running"), busyRefusal);
            }
            finally
            {
                AccountVaultFoldMigration.EndRun();
            }

            Assert.IsFalse(AccountVaultFoldMigration.IsRunning);
            Assert.IsTrue(AccountVaultFoldMigration.TryBeginRun(out _), "the flag must be releasable");
            AccountVaultFoldMigration.EndRun();
        }

        // ---------------------------------------------------------------- 8. Exhausted is not "folded nothing"

        /// <summary>
        /// THE TERMINAL IS NOT A SYNONYM FOR "FOLDED NOTHING". Exhausted is reported by the pass that
        /// FINDS NOTHING, never by the pass that folds the last item, and never by the pass in between
        /// that merely releases its target class.
        ///
        /// That distinction is the whole basis of the completion statement. If the pass that folded the
        /// last item claimed Exhausted, the per-account loop would stop one pass early and could not have
        /// seen the store's own latch - and if the target-release pass claimed it, an account holding two
        /// classes would be declared finished with its second class untouched.
        ///
        /// Driven directly against the batch seam rather than through the migration, because the three
        /// passes have to be observed one at a time and the migration's job is to run them back to back.
        /// FoldHasNothingLeft is asserted alongside each terminal, so the two cannot drift apart: it is
        /// the store's own latch and the only thing the report is entitled to read completion from.
        /// </summary>
        [TestMethod]
        public void BatchTerminal_IsExhaustedOnlyOnThePassThatFindsNothing()
        {
            var fixture = new FoldFixture(10, 10, 10);

            Assert.IsTrue(fixture.Store.IsLoaded, "the store must load before a batch can see anything in it");

            VaultFoldBatchResult result = null;

            void Batch(double clock)
                => fixture.Store.Enqueue(() => fixture.Store.FoldSomeStoredItemsOnQueue(clock, 100, out result));

            // Pass 1 folds all three - the LAST item of the only class in this vault - and must still
            // report MoreToDo.
            Batch(1000.0);

            Assert.AreEqual(3, result.Folded);
            Assert.AreEqual(3, result.Examined);
            Assert.AreEqual(VaultFoldBatchTerminal.MoreToDo, result.Terminal,
                "the pass that folds the last item has not PROVED there is nothing left; only a pass that looks and finds nothing has");
            Assert.IsFalse(fixture.Store.FoldHasNothingLeft, "and the store's own latch must not be set by a pass that did work");

            // Pass 2 finds no item of the target class and releases it. It folds nothing and examines
            // nothing, and is still not finished.
            Batch(1001.0);

            Assert.AreEqual(0, result.Folded);
            Assert.AreEqual(0, result.Examined);
            Assert.AreEqual(VaultFoldBatchTerminal.MoreToDo, result.Terminal,
                "releasing a drained target class is not completion - an account with a second class would be abandoned here");
            Assert.IsFalse(fixture.Store.FoldHasNothingLeft);

            // Pass 3 looks with no target at all, finds nothing, and latches.
            Batch(1002.0);

            Assert.AreEqual(0, result.Examined);
            Assert.AreEqual(VaultFoldBatchTerminal.Exhausted, result.Terminal);
            Assert.IsTrue(fixture.Store.FoldHasNothingLeft, "the terminal and the store's latch must agree");
        }

        // ---------------------------------------------------------------- the refusal counters discriminate

        /// <summary>
        /// The three refusal counters are not aliases for each other. They were ONE enum member before
        /// the batch seam existed, and a report that summed them would be useless in the one case it is
        /// read for: most of a real vault is refused by the predicate and that is entirely normal, while
        /// a round-trip refusal means content that cannot be rebuilt and is worth waking somebody for.
        ///
        /// Each fixture drives exactly one of the two reachable refusals, and each assertion is paired
        /// with a ZERO on the sibling counter - which is what proves the counters are wired to different
        /// outcomes rather than all to the same one.
        /// </summary>
        [TestMethod]
        public void BatchCounters_AttributeARefusalToThePredicateOrTheRoundTrip_NeverToBoth()
        {
            // Refused by the PREDICATE: not classifiable at all.
            var predicate = new FoldFixture();
            predicate.AddUnfoldable();

            var predicateReport = AccountVaultFoldMigration.RunSynchronously(new[] { predicate.AccountId },
                                                                            StoreFor(predicate),
                                                                            batch: 100,
                                                                            shouldStop: null);

            var predicateResult = ResultFor(predicateReport, predicate);

            Assert.AreEqual(1, predicateResult.RefusedPredicate);
            Assert.AreEqual(0, predicateResult.RefusedRoundTrip, "a predicate refusal must not be counted as a round-trip failure");
            Assert.AreEqual(0, predicateResult.RefusedTakeOut);
            Assert.AreEqual(0, predicateResult.Folded);

            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, predicateResult.Terminal,
                "an account holding only unfoldable items IS migrated - there is nothing left to try");
            Assert.AreEqual(1, predicate.Unfoldable.Count(u => predicate.Container.Inventory.ContainsKey(u.Guid)),
                "and a refused item keeps its biota and its place in the vault");

            // Refused by the ROUND TRIP: classifiable, but the rebuilt item is of a different class, so
            // it is not interchangeable with its own class.
            var roundTrip = new FoldFixture(10);

            roundTrip.World.MaterializeStructureOverride = 99;

            var roundTripReport = AccountVaultFoldMigration.RunSynchronously(new[] { roundTrip.AccountId },
                                                                            StoreFor(roundTrip),
                                                                            batch: 100,
                                                                            shouldStop: null);

            var roundTripResult = ResultFor(roundTripReport, roundTrip);

            Assert.AreEqual(1, roundTripResult.RefusedRoundTrip);
            Assert.AreEqual(0, roundTripResult.RefusedPredicate, "the predicate ACCEPTED this item; only the rebuild refused it");
            Assert.AreEqual(0, roundTripResult.RefusedTakeOut);
            Assert.AreEqual(0, roundTripResult.Folded);

            Assert.IsFalse(roundTrip.World.Destroyed.Contains(roundTrip.Bags[0]),
                "an item that fails the round trip must keep its biota");
            Assert.IsTrue(roundTrip.Container.Inventory.ContainsKey(roundTrip.Bags[0].Guid),
                "and must never leave its vault");

            // THE RECONCILIATION IDENTITY. Every candidate a pass looked at has to land in exactly one
            // bucket, so the buckets must account for Examined with nothing left over. This is the
            // assertion that catches an outcome added to the switch and not to the report - which is
            // precisely what AbortPass was: counted as examined, counted in no bucket, so an operator saw
            // Examined exceed the sum of every other field by one with no field naming the difference.
            Assert.AreEqual(predicateResult.Examined, predicateResult.Accounted,
                $"the outcome buckets must account for every examined candidate; examined {predicateResult.Examined}, accounted {predicateResult.Accounted}");

            Assert.AreEqual(roundTripResult.Examined, roundTripResult.Accounted,
                $"examined {roundTripResult.Examined}, accounted {roundTripResult.Accounted}");
        }

        /// <summary>
        /// The ABORT bucket, which is the outcome the reconciliation identity was silently missing.
        ///
        /// A pass aborts when the class ledger refuses or cannot be reached AFTER the item has been taken
        /// out of its vault: the item is put straight back, the pass stops rather than grinding the rest of
        /// the budget against a refusing ledger, and that one candidate was already counted as examined.
        /// Before it had a bucket of its own, Examined exceeded the sum of the buckets by exactly one and
        /// nothing in the report said why.
        ///
        /// Reaching it needs the real class key, which only exists once something has folded - so the
        /// fixture folds one bag, reads the key off the row it created, and poisons that key for the second
        /// bag. That is also why this is worth having beyond the counter: nothing else in the suite drives
        /// the abort path end to end, and the item being PUT BACK rather than lost is the part that matters
        /// most if it ever regresses.
        /// </summary>
        [TestMethod]
        public void BatchCounters_CountTheAbortingCandidate_AndTheItemIsPutBack()
        {
            var fixture = new FoldFixture(10, 10);

            Assert.IsTrue(fixture.Store.IsLoaded, "the store must load before a batch can see anything in it");

            VaultFoldBatchResult result = null;

            void Batch(double clock, int budget)
                => fixture.Store.Enqueue(() => fixture.Store.FoldSomeStoredItemsOnQueue(clock, budget, out result));

            // Pass 1 folds the first bag, which is what creates the class row this test needs the key of.
            Batch(1000.0, 1);

            Assert.AreEqual(1, result.Folded);
            Assert.AreEqual(result.Examined, result.Accounted, "the ordinary pass must already reconcile");

            var row = fixture.Backend.Classes.Single(c => c.AccountId == fixture.AccountId);

            Assert.IsNotNull(row.ClassKey);

            // Now the ledger refuses that exact class. The second bag is of the same class, so its deposit
            // reaches the refusal AFTER the fold has taken it out of the vault - which is the only state
            // that produces an abort.
            fixture.Backend.FailClassAdjustKeys.Add(row.ClassKey);

            Batch(1001.0, 1);

            Assert.AreEqual(VaultFoldBatchTerminal.Aborted, result.Terminal);
            Assert.AreEqual(1, result.Examined);
            Assert.AreEqual(1, result.Aborted, "the aborting candidate must be counted in its own bucket");
            Assert.AreEqual(0, result.Folded);

            Assert.AreEqual(result.Examined, result.Accounted,
                $"examined {result.Examined} must equal the sum of every bucket, {result.Accounted} - this identity is what the Aborted bucket exists for");

            // And the thing that actually matters if this path regresses: the item came back.
            //
            // The aborting bag is identified from OBSERVED STATE, never by index into Bags, because seed
            // order is the reverse of fold order - see FoldFixture.Bags for the mechanism. The survivor of
            // pass 1 is the bag pass 2 aborted on, by definition.
            // Stated as a count first so a regression that destroys the aborted candidate reads as
            // "expected 1 bag left, found 0" rather than as a LINQ Single() throw with no numbers in it.
            var survivors = fixture.Bags.Where(b => !fixture.World.Destroyed.Contains(b)).ToList();

            Assert.AreEqual(1, survivors.Count,
                            $"pass 1 folded exactly one of the two bags and the aborted pass must have destroyed nothing, so exactly one bag must survive; {survivors.Count} did");

            var aborted = survivors[0];

            Assert.IsTrue(fixture.Container.Inventory.ContainsKey(aborted.Guid),
                          "an aborted candidate must be put back in its vault, not left in limbo");

            // The account-level result carries the bucket too, so the report an operator reads reconciles
            // as well as the batch does.
            fixture.Backend.FailClassAdjustKeys.Clear();

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 100,
                                                                   shouldStop: null);

            var account = ResultFor(report, fixture);

            Assert.AreEqual(account.Examined, account.Accounted,
                $"the account line must reconcile too; examined {account.Examined}, accounted {account.Accounted}");
        }

        // ---------------------------------------------------------------- the open-panel skip

        /// <summary>
        /// An account whose store already has a window open - a player with their own mule panel up - is
        /// SKIPPED, reported WindowOpen, and makes the fleet headline INCOMPLETE. Then the same fixture,
        /// with the window closed and nothing else changed, folds to COMPLETE.
        ///
        /// The control arm is the whole test. WindowOpen is a terminal that reports zero of everything,
        /// which is also what an empty vault, a cold store and a disabled kill switch report, so a skip
        /// asserted alone would pass just as well over a fixture that had nothing to fold in the first
        /// place. Running the second arm on the SAME fixture is what proves those bags were foldable all
        /// along and the window is what stopped them.
        ///
        /// The store is LOADED before either arm, so NotWarm cannot be what produced the skip, and zero
        /// DescribeClassCalls across the first arm is what separates "did not look" from "looked and
        /// folded nothing" - the migration must stand off before it examines anything, not fold the vault
        /// and report a skip.
        /// </summary>
        [TestMethod]
        public void Migration_SkipsAnAccountWhosePanelIsOpen_AndFoldsItOnceTheWindowCloses()
        {
            var fixture = new FoldFixture(10, 10, 20);

            Assert.IsTrue(fixture.Store.IsLoaded, "the store must be loaded so that NotWarm cannot be mistaken for the skip");

            fixture.World.ResetClassCalls();

            // The player's panel. AddWindow is what PersonalVendor takes on approach and what
            // AccountVaultStore.TryEvict reads to decide a store is busy.
            fixture.Store.AddWindow();

            var skipped = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                    StoreFor(fixture),
                                                                    batch: 100,
                                                                    shouldStop: null);

            var skippedResult = ResultFor(skipped, fixture);

            Assert.AreEqual(VaultFoldAccountTerminal.WindowOpen, skippedResult.Terminal, skipped.Render());
            Assert.AreEqual(0, skippedResult.Batches, "a skipped account must not have run a batch");
            Assert.AreEqual(0, skippedResult.Examined);
            Assert.AreEqual(0, skippedResult.Folded);

            Assert.AreEqual(0, fixture.World.DescribeClassCalls,
                            "the skip must happen BEFORE anything examines the vault, so the predicate must never run");

            Assert.AreEqual(0, fixture.World.Destroyed.Count, "and nothing in the vault may be destroyed");

            foreach (var bag in fixture.Bags)
                Assert.IsTrue(fixture.Container.Inventory.ContainsKey(bag.Guid), "every bag must still be in its vault");

            // The fleet verdict: visited, because the store resolved, and NOT complete, so the operator is
            // told to re-run rather than being told the fleet is migrated.
            Assert.AreEqual("INCOMPLETE", skipped.Headline, skipped.Render());
            Assert.IsFalse(skipped.IsComplete);
            Assert.AreEqual(1, skipped.AccountsVisited);
            Assert.AreEqual(0, skipped.AccountsComplete);
            Assert.IsFalse(skippedResult.IsComplete, "WindowOpen must never count towards completion");

            // The report has to NAME the account, or an operator cannot act on the INCOMPLETE.
            StringAssert.Contains(skipped.Render(), $"account {fixture.AccountId}",
                                  "an INCOMPLETE report must name the account that was skipped");

            // ---- the control: the same fixture, the window closed, nothing else changed.
            fixture.Store.RemoveWindow();

            Assert.AreEqual(0, fixture.Store.OpenWindows, "the control arm is only a control if the window is actually closed");

            var folded = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 100,
                                                                   shouldStop: null);

            var foldedResult = ResultFor(folded, fixture);

            Assert.AreEqual(VaultFoldAccountTerminal.Exhausted, foldedResult.Terminal, folded.Render());
            Assert.AreEqual("COMPLETE", folded.Headline, folded.Render());
            Assert.AreEqual(3, foldedResult.Folded, "all three bags were foldable the whole time; the window is what stopped them");

            Assert.IsTrue(fixture.World.DescribeClassCalls > 0, "and this arm did examine the vault");

            foreach (var bag in fixture.Bags)
                Assert.IsTrue(fixture.World.Destroyed.Contains(bag), "every bag folds once the panel is closed");
        }

        // ------------------------------------------- what the retired rotation's tests used to protect

        /// <summary>
        /// ONE ACCOUNT PER UNIT OF WORK, WITH THAT ACCOUNT'S STORE PINNED FOR THE WHOLE OF IT.
        ///
        /// This is the invariant the retired background rotation's pacing test used to hold from the other
        /// side. That test asserted the rotation took exactly one store per interval, round robin, so no
        /// account could starve another and no single world tick carried more than one budget's worth. The
        /// rotation is gone, and the same property now belongs to the migration: it walks the fleet
        /// SERIALLY, and while it is batching one account it holds a window on that account's store and on
        /// no other.
        ///
        /// WHY THE PIN IS HALF THE PROPERTY AND NOT DECORATION. AccountVaultManager's idle sweep can retire
        /// a store between two batches, and a retired store refuses work - Enqueue answers false, which the
        /// migration reports as StoreRetired and which would show up as an account truncated for no stated
        /// reason. AddWindow is what stops that (AccountVaultStore.TryEvict declines while OpenWindows &gt; 0),
        /// and RemoveWindow sits in a finally so a throw cannot leave an account pinned for the life of the
        /// process - which the final assertion here is what checks.
        ///
        /// OBSERVED THROUGH THE PRODUCTION PROGRESS SEAM, not through test-only state. onProgress fires
        /// after every batch and after every account, and it carries CurrentAccountId, so each tick can be
        /// checked against the windows actually open at that instant. Three accounts of two classes each,
        /// with a batch size of one, so there are several batches per account and the walk has somewhere to
        /// go wrong.
        ///
        /// THE TWO KINDS OF TICK ARE BOTH COUNTED, because they differ and the difference is the release.
        /// A per-BATCH tick fires from inside RunAccount, so the account is pinned. A per-ACCOUNT tick
        /// fires from the fleet loop AFTER RunAccount's finally has run, so nothing is pinned at all -
        /// which is why the per-tick assertion is "at most one" and the released half is pinned separately
        /// as one unpinned tick per visited account. An assertion of "exactly one, always" would fail on a
        /// correct build, and an assertion of "at most one" alone would pass on a build that pinned nothing.
        ///
        /// The completion assertion is the control: without it, a migration that refused every account
        /// would satisfy every window check trivially, because an empty run pins nothing.
        /// </summary>
        [TestMethod]
        public void Migration_PinsOneAccountsStoreAtATime_AndReleasesEveryWindowAfterwards()
        {
            var a = new FoldFixture(10, 20, 10, 20);
            var b = new FoldFixture(30, 40, 30, 40);
            var c = new FoldFixture(50, 60, 50, 60);

            var fixtures = new[] { a, b, c };

            var ticks = 0;
            var pinnedTicks = 0;

            void OnProgress(VaultFoldMigrationProgress progress)
            {
                ticks++;

                var pinned = fixtures.Where(f => f.Store.OpenWindows > 0).ToList();

                Assert.IsTrue(pinned.Count <= 1,
                    $"tick {ticks} had {pinned.Count} stores pinned; the migration must hold at most one account at a time");

                if (pinned.Count != 1)
                    return;

                pinnedTicks++;

                Assert.AreEqual(progress.CurrentAccountId, pinned[0].AccountId,
                    "the pinned store must be the account the run says it is working on");

                Assert.AreEqual(1, pinned[0].Store.OpenWindows,
                    "one window, not several - the migration must not re-pin an account it already holds");
            }

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { a.AccountId, b.AccountId, c.AccountId },
                                                                   StoreFor(fixtures),
                                                                   batch: 1,
                                                                   shouldStop: null,
                                                                   scope: null,
                                                                   fleetWide: true,
                                                                   onProgress: OnProgress);

            Assert.IsTrue(pinnedTicks >= 6,
                $"only {pinnedTicks} of {ticks} progress ticks saw a pinned store, so the checks above barely ran");

            Assert.AreEqual(3, ticks - pinnedTicks,
                "exactly one tick per visited account must see NO window at all - that is the per-account tick, after the release");

            // CONTROL. The run really did the work, so the single-window checks above were made against a
            // migration that was folding rather than against one that refused everything.
            Assert.AreEqual("COMPLETE", report.Headline, report.Render());
            Assert.AreEqual(12, report.Folded, "every bag in all three vaults must have folded");
            Assert.AreEqual(3, report.AccountsComplete);

            foreach (var fixture in fixtures)
                Assert.AreEqual(0, fixture.Store.OpenWindows,
                    $"account {fixture.AccountId} is still pinned after the run; RemoveWindow must be unconditional");
        }

        /// <summary>
        /// A STORE THAT IS NOT AVAILABLE IS SKIPPED AND REPORTED - IT IS NEVER CREATED.
        ///
        /// The other half of what the retired rotation's pacing test protected. That test asserted the
        /// rotation resolved stores through a lookup that can answer null rather than through
        /// AccountVaultManager.GetStore, because creating a store would load a vault from the shard for an
        /// account nobody is using. The migration takes the same seam, and this pins that a null answer
        /// stops there: reported StoreUnavailable, not counted as visited, and no store registered in the
        /// process-wide table as a side effect.
        ///
        /// StoreCount IS THE OBSERVABLE FOR "NEVER CREATED", because the absence of a store cannot be seen
        /// from the report: a created-then-unused store would produce the same terminal. It is asserted as
        /// UNCHANGED rather than as zero, since other test classes in the same process leave stores behind
        /// (see AccountVaultManager.ForgetStoreForTest's remarks).
        ///
        /// The second arm is the control. The same run, with the same batch size, over an account whose
        /// store IS available folds to COMPLETE - so the zeros in the first arm are the refusal and not a
        /// fixture that could never have folded.
        /// </summary>
        [TestMethod]
        public void Migration_WhenNoStoreIsAvailable_ReportsStoreUnavailable_AndNeverBuildsOne()
        {
            var absent = nextAccount++;

            var asked = new List<uint>();

            // The production answer for an account with no live store. It can answer null, and that is the
            // load-bearing half: AccountVaultManager.GetStore cannot, which is why the migration takes a
            // seam rather than calling it.
            AccountVaultStore NullStoreFor(uint accountId)
            {
                asked.Add(accountId);

                return null;
            }

            var storesBefore = AccountVaultManager.StoreCount;

            var report = AccountVaultFoldMigration.RunSynchronously(new[] { absent },
                                                                   NullStoreFor,
                                                                   batch: 25,
                                                                   shouldStop: null);

            CollectionAssert.AreEqual(new[] { absent }, asked, "the run must ask the seam exactly once for that account");

            var line = report.Accounts.Single(x => x.AccountId == absent);

            Assert.AreEqual(VaultFoldAccountTerminal.StoreUnavailable, line.Terminal, report.Render());
            Assert.AreEqual(0, line.Batches, "nothing may be enqueued against an account with no store");
            Assert.AreEqual(0, line.Examined);
            Assert.AreEqual(0, line.Folded);

            Assert.AreEqual(1, report.AccountsOwningAVault);
            Assert.AreEqual(0, report.AccountsVisited, "an account whose store never resolved was not visited");
            Assert.AreEqual("INCOMPLETE", report.Headline, report.Render());

            Assert.AreEqual(storesBefore, AccountVaultManager.StoreCount,
                "the migration must not build a store for an account nobody is using - that would load a vault from the shard");

            // CONTROL: an available store, everything else identical, folds.
            var present = new FoldFixture(10, 20);

            var second = AccountVaultFoldMigration.RunSynchronously(new[] { present.AccountId },
                                                                   StoreFor(present),
                                                                   batch: 25,
                                                                   shouldStop: null);

            Assert.AreEqual("COMPLETE", second.Headline, second.Render());
            Assert.AreEqual(2, second.Folded, "the identical run must fold when a store IS available, or the zeros above proved nothing");
        }

        // ---------------------------------------------------------------- the safety net

        /// <summary>
        /// The per-account batch ceiling. It is a safety net against a future edit making the store
        /// answer MoreToDo forever, not an expected state - so what is tested is that hitting it is
        /// REPORTED rather than reported as completion.
        ///
        /// Driven by lowering the ceiling under a vault that genuinely needs more batches than that, with
        /// a batch size of one. The control is the same fixture at the ordinary ceiling, which finishes:
        /// without it a guard that refused every account would pass this test.
        /// </summary>
        [TestMethod]
        public void Migration_WhenAnAccountExceedsTheBatchCeiling_ReportsBatchLimit_NotCompletion()
        {
            var fixture = new FoldFixture(10, 10, 10, 10, 10, 10);

            AccountVaultFoldMigration.MaxBatchesPerAccount = 2;

            var capped = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                   StoreFor(fixture),
                                                                   batch: 1,
                                                                   shouldStop: null);

            var cappedResult = ResultFor(capped, fixture);

            Assert.AreEqual(VaultFoldAccountTerminal.BatchLimit, cappedResult.Terminal, capped.Render());
            Assert.AreEqual(2, cappedResult.Batches);
            Assert.AreEqual(2, cappedResult.Folded, "two batches of one fold two items");

            Assert.AreEqual("INCOMPLETE", capped.Headline, capped.Render());
            Assert.AreEqual(0, capped.AccountsComplete);
            Assert.AreEqual(4, fixtureBagsStillStored(fixture), "four bags are still waiting, which is why this is not completion");

            // CONTROL: the same fixture at the shipped ceiling finishes.
            AccountVaultFoldMigration.MaxBatchesPerAccount = savedMaxBatches;

            var uncapped = AccountVaultFoldMigration.RunSynchronously(new[] { fixture.AccountId },
                                                                     StoreFor(fixture),
                                                                     batch: 1,
                                                                     shouldStop: null);

            Assert.AreEqual("COMPLETE", uncapped.Headline, uncapped.Render());
            Assert.AreEqual(4, uncapped.Folded, "the remaining bags fold once the ceiling is not in the way");
            Assert.AreEqual(0, fixtureBagsStillStored(fixture));
        }
    }
}

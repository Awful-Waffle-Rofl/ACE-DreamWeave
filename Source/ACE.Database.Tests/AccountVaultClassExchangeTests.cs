using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// The atomic multi-row class exchange (ShardDatabase.TryExchangeAccountVaultClasses) against a
    /// real shard database.
    ///
    /// THIS WRITES ROWS into account_vault_class and deletes its own rows again afterwards. Like
    /// PvpArenaRoundTripTests it runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/> is
    /// 1, which is a promise that Config.js's Shard block points at a DISPOSABLE database with
    /// Database/Updates/Shard/2026-09-24-00-Add-Account-Vault-Class.sql applied. Without it every test
    /// here reports Inconclusive.
    ///
    /// Each test draws a random account id from a high range and fresh random class keys, so leftover
    /// or parallel rows cannot collide.
    /// </summary>
    [TestClass]
    public class AccountVaultClassExchangeTests
    {
        private static ShardDatabase RequireDisposableShard()
        {
            if (Environment.GetEnvironmentVariable(PvpArenaRoundTripTests.OptInVariable) != "1")
                Assert.Inconclusive($"Skipped: set {PvpArenaRoundTripTests.OptInVariable}=1 only when Config.js points at a disposable shard database. This test writes rows.");

            TestEnvironment.InitializeConfig();

            var shardDb = new ShardDatabase();

            bool reachable;

            try
            {
                reachable = shardDb.Exists(false);
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive("Skipped: the shard database is not reachable with the configured Config.js.");

            return shardDb;
        }

        private static uint NewAccountId() => 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);

        private static string NewKey() => Guid.NewGuid().ToString("N");

        private static AccountVaultClassSeed Seed(uint wcid) => new AccountVaultClassSeed { Wcid = wcid, ValueBandPct = 0, CanonicalForm = $"test-exchange-form-{wcid}-{Guid.NewGuid():N}" };

        private static void DeleteRows(uint accountId)
        {
            using var context = new ShardDbContext();

            context.AccountVaultClass.Where(c => c.AccountId == accountId).ExecuteDelete();
        }

        /// <summary>Plants a row through the existing single-row adjust, so setup uses only shipped code.</summary>
        private static void Plant(ShardDatabase db, uint accountId, string key, long count, long total)
        {
            var result = db.TryAdjustAccountVaultClass(accountId, key, count, total, Seed(1), out var newCount, out var newTotal);

            Assert.AreEqual(AccountVaultStackAdjustResult.Applied, result, $"setup could not plant {key}");
            Assert.AreEqual(count, newCount);
            Assert.AreEqual(total, newTotal);
        }

        /// <summary>Every row of the account, keyed by class key: (id, count, total).</summary>
        private static Dictionary<string, (uint Id, long Count, long Total)> Snapshot(ShardDatabase db, uint accountId)
        {
            var rows = db.GetAccountVaultClasses(accountId);

            Assert.IsNotNull(rows, "the read-back failed");

            return rows.ToDictionary(r => r.ClassKey, r => (r.Id, r.Count, r.TotalValue));
        }

        private static void AssertSnapshotsEqual(Dictionary<string, (uint Id, long Count, long Total)> expected,
                                                 Dictionary<string, (uint Id, long Count, long Total)> actual, string because)
        {
            Assert.AreEqual(expected.Count, actual.Count, $"row count changed: {because}");

            foreach (var entry in expected)
            {
                Assert.IsTrue(actual.TryGetValue(entry.Key, out var row), $"row {entry.Key} vanished: {because}");
                Assert.AreEqual(entry.Value, row, $"row {entry.Key} changed: {because}");
            }
        }

        // (a) + (b): apply once, then the identical call again.
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_AppliesOnce_AndAnIdenticalRetryIsAppliedWithZeroWrites()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var keyA = NewKey();
            var keyB = NewKey();

            try
            {
                Plant(db, accountId, keyA, 5, 500);
                Plant(db, accountId, keyB, 2, 80);

                var steps = new List<AccountVaultClassExchangeStep>
                {
                    new AccountVaultClassExchangeStep(keyA, 5, 500, -2, -200),
                    new AccountVaultClassExchangeStep(keyB, 2, 80, 1, 200),
                };

                // (a)
                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, db.TryExchangeAccountVaultClasses(accountId, steps));

                var after = Snapshot(db, accountId);

                Assert.AreEqual((3L, 300L), (after[keyA].Count, after[keyA].Total));
                Assert.AreEqual((3L, 280L), (after[keyB].Count, after[keyB].Total));

                // (b) The identical call is exactly what the execution strategy re-runs after a commit
                // whose acknowledgement was lost. It must report Applied and move NOTHING: had it
                // re-applied, A would read 1/100 and B 4/480.
                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, db.TryExchangeAccountVaultClasses(accountId, steps),
                    "a re-run at the post-state must be recognised as already applied");

                AssertSnapshotsEqual(after, Snapshot(db, accountId), "the retry must write nothing");
            }
            finally
            {
                DeleteRows(accountId);
            }
        }

        // (c): the ledger moved underneath the caller.
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_StateMovedUnderneath_IsRefusedAndNoRowChanges()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var keyA = NewKey();
            var keyB = NewKey();
            var keyNew = NewKey();

            try
            {
                Plant(db, accountId, keyA, 5, 500);
                Plant(db, accountId, keyB, 2, 80);

                // Something else moves B after the caller computed its trade.
                Assert.AreEqual(AccountVaultStackAdjustResult.Applied,
                    db.TryAdjustAccountVaultClass(accountId, keyB, 1, 40, null, out _, out _));

                var before = Snapshot(db, accountId);

                var steps = new List<AccountVaultClassExchangeStep>
                {
                    new AccountVaultClassExchangeStep(keyA, 5, 500, -2, -200),   // still matches
                    new AccountVaultClassExchangeStep(keyB, 2, 80, 1, 200),      // stale: B is now 3/120
                    new AccountVaultClassExchangeStep(keyNew, 0, 0, 1, 10, Seed(7)),
                };

                Assert.AreEqual(AccountVaultStackAdjustResult.Refused, db.TryExchangeAccountVaultClasses(accountId, steps));

                AssertSnapshotsEqual(before, Snapshot(db, accountId),
                    "a refused exchange must change no row - including A, which matched, and the row it would have created");
            }
            finally
            {
                DeleteRows(accountId);
            }
        }

        // (d): one step creates a row, another drains a row to zero, in one exchange.
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_CreatesARowAndDrainsARowToZeroInOneTrade()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var keyDrain = NewKey();
            var keyKeep = NewKey();
            var keyNew = NewKey();
            var seed = Seed(29581);

            try
            {
                Plant(db, accountId, keyDrain, 2, 150);
                Plant(db, accountId, keyKeep, 1, 10);

                var steps = new List<AccountVaultClassExchangeStep>
                {
                    new AccountVaultClassExchangeStep(keyDrain, 2, 150, -2, -150),
                    new AccountVaultClassExchangeStep(keyKeep, 1, 10, 1, 40),
                    new AccountVaultClassExchangeStep(keyNew, 0, 0, 1, 110, seed),
                };

                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, db.TryExchangeAccountVaultClasses(accountId, steps));

                var after = db.GetAccountVaultClasses(accountId);
                Assert.IsNotNull(after);

                var drained = after.Single(c => c.ClassKey == keyDrain);
                Assert.AreEqual((0L, 0L), (drained.Count, drained.TotalValue), "a drained row is left at 0/0 for DeleteEmptyAccountVaultClasses");

                var kept = after.Single(c => c.ClassKey == keyKeep);
                Assert.AreEqual((2L, 50L), (kept.Count, kept.TotalValue));

                var created = after.Single(c => c.ClassKey == keyNew);
                Assert.AreEqual((1L, 110L), (created.Count, created.TotalValue));
                Assert.AreEqual(seed.Wcid, created.Wcid);
                Assert.AreEqual(seed.ValueBandPct, created.ValueBandPct);
                Assert.AreEqual(seed.CanonicalForm, created.CanonicalForm);

                // The caller's reap removes the drained row, and a retry afterwards still reads the
                // reaped row as its 0/0 post-state rather than as a moved ledger.
                Assert.AreEqual(1, db.DeleteEmptyAccountVaultClasses(accountId));

                var reaped = Snapshot(db, accountId);
                Assert.IsFalse(reaped.ContainsKey(keyDrain));

                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, db.TryExchangeAccountVaultClasses(accountId, steps),
                    "a retry after the reap must still be recognised as already applied");

                AssertSnapshotsEqual(reaped, Snapshot(db, accountId), "the post-reap retry must write nothing");
            }
            finally
            {
                DeleteRows(accountId);
            }
        }

        // Review F3 (b): a MIXED observation - one row already at its post-state, the other still at
        // its pre-state - is neither "apply" nor "already applied".
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_MixedPreAndPostObservation_IsRefusedWithNoWrites()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var keyA = NewKey();
            var keyB = NewKey();

            try
            {
                Plant(db, accountId, keyA, 3, 300);   // = A's post-state (5/500 - 2/200)
                Plant(db, accountId, keyB, 2, 80);    // = B's pre-state

                var before = Snapshot(db, accountId);

                var steps = new List<AccountVaultClassExchangeStep>
                {
                    new AccountVaultClassExchangeStep(keyA, 5, 500, -2, -200),
                    new AccountVaultClassExchangeStep(keyB, 2, 80, 1, 200),
                };

                Assert.AreEqual(AccountVaultStackAdjustResult.Refused, db.TryExchangeAccountVaultClasses(accountId, steps));

                AssertSnapshotsEqual(before, Snapshot(db, accountId), "a mixed observation must write nothing");
            }
            finally
            {
                DeleteRows(accountId);
            }
        }

        // Review F3 (c): a drained-but-UNREAPED row sits at 0/0. Used as a 0/0 pre-state it must take
        // the guarded UPDATE branch - the same row, same id, its original canonical form - and never
        // the INSERT, which would hit the unique key.
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_UnreapedZeroRowAsPreState_TakesTheUpdateBranch()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var key = NewKey();

            try
            {
                Plant(db, accountId, key, 1, 25);

                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, db.TryAdjustAccountVaultClass(accountId, key, -1, -25, null, out var drainedCount, out var drainedTotal));
                Assert.AreEqual((0L, 0L), (drainedCount, drainedTotal));

                var original = db.GetAccountVaultClasses(accountId).Single(c => c.ClassKey == key);

                var steps = new List<AccountVaultClassExchangeStep>
                {
                    // A DIFFERENT seed: if the INSERT branch ran it would either collide or write this
                    // canonical form; the UPDATE branch leaves the original in place.
                    new AccountVaultClassExchangeStep(key, 0, 0, 2, 20, Seed(99)),
                };

                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, db.TryExchangeAccountVaultClasses(accountId, steps));

                var after = db.GetAccountVaultClasses(accountId);
                Assert.AreEqual(1, after.Count, "no second row may be created");

                var row = after.Single();
                Assert.AreEqual(original.Id, row.Id, "the existing 0/0 row must be updated in place, not replaced");
                Assert.AreEqual(original.CanonicalForm, row.CanonicalForm, "the UPDATE branch must never rewrite the canonical form");
                Assert.AreEqual(original.Wcid, row.Wcid);
                Assert.AreEqual((2L, 20L), (row.Count, row.TotalValue));
            }
            finally
            {
                DeleteRows(accountId);
            }
        }

        /// <summary>
        /// Throws ONE transient-classified exception immediately AFTER a transaction has really
        /// committed, by observing EF Core's own "TransactionCommitted" diagnostic event.
        ///
        /// Why this hook and not a DbTransactionInterceptor: the DAO builds its own ShardDbContext with
        /// <c>new ShardDbContext()</c> and there is no seam to add an interceptor to that context's
        /// options. EF's DiagnosticSource is process-global, and RelationalTransaction.Commit raises
        /// TransactionCommitted only after the underlying DbTransaction.Commit() has returned, so an
        /// exception thrown from the observer escapes Commit() with the data already durable - the
        /// "commit landed, acknowledgement lost" shape exactly.
        ///
        /// TimeoutException is used because Pomelo's MySqlTransientExceptionDetector classifies it as
        /// transient, so MySqlRetryingExecutionStrategy re-runs the delegate. Both of those claims are
        /// UNVERIFIED in this session (recall of EF Core / Pomelo internals); this test is itself the
        /// check, and its assertions on <see cref="Fired"/> and <see cref="Commits"/> say which half
        /// failed if one is wrong.
        /// </summary>
        private sealed class ThrowOnceAfterCommit : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>>, IDisposable
        {
            private const string CommittedEvent = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted";

            private readonly IDisposable allListeners;
            private readonly List<IDisposable> subscriptions = new List<IDisposable>();

            public bool Armed;
            public int Fired;
            public int Commits;

            public ThrowOnceAfterCommit()
            {
                allListeners = DiagnosticListener.AllListeners.Subscribe(this);
            }

            public void OnNext(DiagnosticListener listener)
            {
                if (listener.Name == "Microsoft.EntityFrameworkCore")
                {
                    lock (subscriptions)
                        subscriptions.Add(listener.Subscribe(this));
                }
            }

            public void OnNext(KeyValuePair<string, object> evt)
            {
                if (evt.Key != CommittedEvent || !Armed)
                    return;

                Commits++;

                if (Fired == 0)
                {
                    Fired++;
                    throw new TimeoutException("test: simulated lost acknowledgement AFTER a real commit");
                }
            }

            public void OnCompleted() { }

            public void OnError(Exception error) { }

            public void Dispose()
            {
                allListeners.Dispose();

                lock (subscriptions)
                {
                    foreach (var subscription in subscriptions)
                        subscription.Dispose();
                }
            }
        }

        // Review F3 (a): a REAL execution-strategy re-run after a commit whose acknowledgement was
        // lost. The first run commits the trade and then throws a transient error out of Commit(); the
        // strategy re-runs the delegate, which must find every row at its post-state, write nothing,
        // and report Applied - the trade applied exactly once.
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_StrategyRerunAfterAPostCommitTransientFailure_AppliesExactlyOnce()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var keyA = NewKey();
            var keyB = NewKey();
            var keyNew = NewKey();

            using var hook = new ThrowOnceAfterCommit();

            try
            {
                Plant(db, accountId, keyA, 5, 500);
                Plant(db, accountId, keyB, 2, 80);

                var steps = new List<AccountVaultClassExchangeStep>
                {
                    new AccountVaultClassExchangeStep(keyA, 5, 500, -2, -200),
                    new AccountVaultClassExchangeStep(keyB, 2, 80, 1, 200),
                    new AccountVaultClassExchangeStep(keyNew, 0, 0, 1, 10, Seed(5)),
                };

                hook.Armed = true;

                var result = db.TryExchangeAccountVaultClasses(accountId, steps);

                hook.Armed = false;

                Assert.AreEqual(1, hook.Fired, "the hook never fired: EF did not raise TransactionCommitted through DiagnosticSource, so this test exercised nothing");
                Assert.AreEqual(2, hook.Commits, $"expected the original commit plus exactly one re-run commit, saw {hook.Commits}: if 1, the strategy did not treat TimeoutException as transient and did not retry");
                Assert.AreEqual(AccountVaultStackAdjustResult.Applied, result, "the re-run must recognise the committed trade and report Applied");

                var after = Snapshot(db, accountId);

                Assert.AreEqual(3, after.Count, "the re-run must not create a second new row");
                Assert.AreEqual((3L, 300L), (after[keyA].Count, after[keyA].Total), "applied twice would read 1/100");
                Assert.AreEqual((3L, 280L), (after[keyB].Count, after[keyB].Total), "applied twice would read 4/480");
                Assert.AreEqual((1L, 10L), (after[keyNew].Count, after[keyNew].Total));
            }
            finally
            {
                hook.Armed = false;
                DeleteRows(accountId);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Exchange_MalformedRequest_IsRefusedWithoutTouchingTheLedger()
        {
            var db = RequireDisposableShard();
            var accountId = NewAccountId();
            var keyA = NewKey();

            try
            {
                Plant(db, accountId, keyA, 1, 10);

                var before = Snapshot(db, accountId);

                // Over-withdraw: validation refuses it before any statement.
                Assert.AreEqual(AccountVaultStackAdjustResult.Refused,
                    db.TryExchangeAccountVaultClasses(accountId, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(keyA, 1, 10, -2, -10) }));

                AssertSnapshotsEqual(before, Snapshot(db, accountId), "a malformed request must change nothing");
            }
            finally
            {
                DeleteRows(accountId);
            }
        }
    }
}

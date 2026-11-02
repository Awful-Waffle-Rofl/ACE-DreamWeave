using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The atomic class exchange (TryExchangeAccountVaultClasses): the request validation the real DAO
    /// and the fake share, and the fake's two injected failure modes, which later callers' tests drive.
    /// The live-database behaviour is covered by ACE.Database.Tests' AccountVaultClassExchangeTests.
    /// </summary>
    [TestClass]
    public class AccountVaultClassExchangeTests
    {
        private const uint Account = 4242;

        private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string KeyC = "cccccccccccccccccccccccccccccccc";

        private static AccountVaultClassSeed Seed(uint wcid) => new AccountVaultClassSeed { Wcid = wcid, ValueBandPct = 0, CanonicalForm = $"form-{wcid}" };

        private static FakeVaultBackend Backend()
        {
            var backend = new FakeVaultBackend();

            backend.Classes.Add(new AccountVaultClass { Id = 1, AccountId = Account, ClassKey = KeyA, Wcid = 1, Count = 3, TotalValue = 300, ValueBandPct = 0, CanonicalForm = "form-1" });
            backend.Classes.Add(new AccountVaultClass { Id = 2, AccountId = Account, ClassKey = KeyB, Wcid = 2, Count = 1, TotalValue = 50, ValueBandPct = 0, CanonicalForm = "form-2" });

            return backend;
        }

        /// <summary>Drain B into A, and create C: one debit-to-zero, one credit, one new row.</summary>
        private static List<AccountVaultClassExchangeStep> Trade() => new List<AccountVaultClassExchangeStep>
        {
            new AccountVaultClassExchangeStep(KeyA, 3, 300, 1, 50),
            new AccountVaultClassExchangeStep(KeyB, 1, 50, -1, -50),
            new AccountVaultClassExchangeStep(KeyC, 0, 0, 2, 20, Seed(3)),
        };

        private static (long Count, long Total)? Row(FakeVaultBackend backend, string key)
        {
            var row = backend.Classes.FirstOrDefault(c => c.AccountId == Account && c.ClassKey == key);

            return row == null ? null : (row.Count, row.TotalValue);
        }

        // ---------------- the opt-in re-run test's premise ----------------

        /// <summary>
        /// ACE.Database.Tests' strategy re-run test throws a TimeoutException after a real commit and
        /// expects MySqlRetryingExecutionStrategy to re-run the delegate. That rests on Pomelo
        /// classifying TimeoutException as transient - checked here, with no database, so the opt-in
        /// test's premise is verified even when it cannot be run. Found by NAME rather than by a typed
        /// reference because the detector's namespace is an implementation detail of the package.
        /// </summary>
        [TestMethod]
        public void Pomelo_ClassifiesTimeoutExceptionAsTransient()
        {
            var detector = typeof(Microsoft.EntityFrameworkCore.MySqlDbContextOptionsBuilderExtensions).Assembly
                .GetTypes()
                .SingleOrDefault(t => t.Name == "MySqlTransientExceptionDetector");

            Assert.IsNotNull(detector, "Pomelo no longer ships MySqlTransientExceptionDetector; re-derive the re-run test's premise");

            var shouldRetryOn = detector.GetMethod("ShouldRetryOn", new[] { typeof(System.Exception) });
            Assert.IsNotNull(shouldRetryOn);

            Assert.IsTrue((bool)shouldRetryOn.Invoke(null, new object[] { new System.TimeoutException() }), "TimeoutException must be transient");
            Assert.IsFalse((bool)shouldRetryOn.Invoke(null, new object[] { new System.InvalidOperationException() }), "control: a non-transient exception must not be");
        }

        // ---------------- validation ----------------

        [TestMethod]
        public void Validation_AcceptsAWellFormedTrade()
        {
            Assert.IsTrue(ShardDatabase.IsValidAccountVaultClassExchange(Account, Trade(), out var reason), reason);
        }

        [TestMethod]
        public void Validation_RefusesEveryMalformedShape()
        {
            var cases = new Dictionary<string, (uint Account, List<AccountVaultClassExchangeStep> Steps)>
            {
                ["account 0"] = (0, Trade()),
                ["null steps"] = (Account, null),
                ["no steps"] = (Account, new List<AccountVaultClassExchangeStep>()),
                ["null step"] = (Account, new List<AccountVaultClassExchangeStep> { null }),
                ["short key"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep("abc", 3, 300, 1, 1) }),
                ["uppercase key"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA.ToUpperInvariant(), 3, 300, 1, 1) }),
                ["duplicate key"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 3, 300, 1, 1), new AccountVaultClassExchangeStep(KeyA, 3, 300, -1, -1) }),
                ["no delta"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 3, 300, 0, 0) }),
                ["negative expected"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, -1, 300, 1, 1) }),
                ["count goes negative"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 3, 300, -4, -300) }),
                ["total goes negative"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 3, 300, -1, -301) }),
                ["zero items non-zero total after"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 3, 300, -3, -299) }),
                ["zero items non-zero total before"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 0, 5, 1, 1, Seed(1)) }),
                ["overflow"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, long.MaxValue, 300, 1, 1) }),
                ["create without seed"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyC, 0, 0, 1, 1) }),
                ["create with empty seed"] = (Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyC, 0, 0, 1, 1, new AccountVaultClassSeed { Wcid = 3 }) }),
            };

            foreach (var entry in cases)
                Assert.IsFalse(ShardDatabase.IsValidAccountVaultClassExchange(entry.Value.Account, entry.Value.Steps, out _), $"'{entry.Key}' was accepted");
        }

        [TestMethod]
        public void Fake_RefusesAMalformedRequestWithoutMovingAnything()
        {
            var backend = Backend();
            backend.ClassExchangeFailMode = FakeVaultBackend.ExchangeFailure.BeforeCommit;

            var result = backend.TryExchangeAccountVaultClasses(Account, new List<AccountVaultClassExchangeStep> { new AccountVaultClassExchangeStep(KeyA, 3, 300, -4, -300) });

            Assert.AreEqual(AccountVaultStackAdjustResult.Refused, result, "validation must win over an injected database failure, as in the real DAO");
            Assert.AreEqual((3L, 300L), Row(backend, KeyA));
            Assert.AreEqual(int.MaxValue, backend.ClassExchangeFailRemaining, "the injection must not be consumed by a request that never reached the database");
        }

        // ---------------- the fake's normal behaviour ----------------

        [TestMethod]
        public void Fake_AppliesTheWholeTrade_AndARetryIsAppliedWithNoSecondApplication()
        {
            var backend = Backend();

            Assert.AreEqual(AccountVaultStackAdjustResult.Applied, backend.TryExchangeAccountVaultClasses(Account, Trade()));

            Assert.AreEqual((4L, 350L), Row(backend, KeyA));
            Assert.AreEqual((0L, 0L), Row(backend, KeyB), "a drained row is left at 0/0 for DeleteEmptyAccountVaultClasses");
            Assert.AreEqual((2L, 20L), Row(backend, KeyC));

            Assert.AreEqual(AccountVaultStackAdjustResult.Applied, backend.TryExchangeAccountVaultClasses(Account, Trade()));

            Assert.AreEqual((4L, 350L), Row(backend, KeyA), "the retry must not apply the trade a second time");
            Assert.AreEqual((0L, 0L), Row(backend, KeyB));
            Assert.AreEqual((2L, 20L), Row(backend, KeyC));
            Assert.AreEqual(3, backend.Classes.Count, "the retry must not create another row");
        }

        [TestMethod]
        public void Fake_RefusesWhenTheLedgerMoved_AndMovesNoRowIncludingTheOnesThatMatched()
        {
            var backend = Backend();
            backend.Classes.Single(c => c.ClassKey == KeyB).Count = 2;

            Assert.AreEqual(AccountVaultStackAdjustResult.Refused, backend.TryExchangeAccountVaultClasses(Account, Trade()));

            Assert.AreEqual((3L, 300L), Row(backend, KeyA));
            Assert.AreEqual((2L, 50L), Row(backend, KeyB));
            Assert.IsNull(Row(backend, KeyC));
        }

        // ---------------- injected failures ----------------

        [TestMethod]
        public void Fake_FailureBeforeCommit_ReportsFailedAndChangesNothing()
        {
            var backend = Backend();
            backend.ClassExchangeFailMode = FakeVaultBackend.ExchangeFailure.BeforeCommit;
            backend.ClassExchangeFailRemaining = 1;

            Assert.AreEqual(AccountVaultStackAdjustResult.Failed, backend.TryExchangeAccountVaultClasses(Account, Trade()));

            Assert.AreEqual((3L, 300L), Row(backend, KeyA));
            Assert.AreEqual((1L, 50L), Row(backend, KeyB));
            Assert.IsNull(Row(backend, KeyC));

            // The countdown is spent, so a retry behaves normally and applies the trade once.
            Assert.AreEqual(AccountVaultStackAdjustResult.Applied, backend.TryExchangeAccountVaultClasses(Account, Trade()));
            Assert.AreEqual((4L, 350L), Row(backend, KeyA));
            Assert.AreEqual(2, backend.ClassExchangeCalls);
        }

        [TestMethod]
        public void Fake_FailureAfterCommit_ReportsFailedButTheTradeLanded()
        {
            var backend = Backend();
            backend.ClassExchangeFailMode = FakeVaultBackend.ExchangeFailure.AfterCommit;
            backend.ClassExchangeFailRemaining = 1;

            Assert.AreEqual(AccountVaultStackAdjustResult.Failed, backend.TryExchangeAccountVaultClasses(Account, Trade()));

            Assert.AreEqual((4L, 350L), Row(backend, KeyA), "AfterCommit must leave the trade applied - Failed does not mean nothing moved");
            Assert.AreEqual((0L, 0L), Row(backend, KeyB));
            Assert.AreEqual((2L, 20L), Row(backend, KeyC));

            // The caller's retry finds every row at its post-state: Applied, and nothing moves twice.
            Assert.AreEqual(AccountVaultStackAdjustResult.Applied, backend.TryExchangeAccountVaultClasses(Account, Trade()));
            Assert.AreEqual((4L, 350L), Row(backend, KeyA));
            Assert.AreEqual((2L, 20L), Row(backend, KeyC));
            Assert.AreEqual(3, backend.Classes.Count);
        }

        [TestMethod]
        public void Fake_FailureAfterCommit_DoesNotFireOnARefusal()
        {
            var backend = Backend();
            backend.Classes.Single(c => c.ClassKey == KeyA).TotalValue = 301;
            backend.ClassExchangeFailMode = FakeVaultBackend.ExchangeFailure.AfterCommit;

            Assert.AreEqual(AccountVaultStackAdjustResult.Refused, backend.TryExchangeAccountVaultClasses(Account, Trade()),
                "nothing committed, so there is no acknowledgement to lose");
            Assert.AreEqual(int.MaxValue, backend.ClassExchangeFailRemaining);
        }
    }
}

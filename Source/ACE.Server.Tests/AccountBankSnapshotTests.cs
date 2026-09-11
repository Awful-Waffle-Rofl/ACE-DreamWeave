using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The bulk balance snapshot behind /top bank and the analytics bank rows
    /// (<see cref="AccountBankManager.GetAllBalances"/>).
    ///
    /// Three properties are worth pinning, and all three are about a FAILED read rather than a
    /// successful one, because that is where this cache can do damage. It must not re-query on every
    /// call; it must never cache a null, since a cached failure would serve "everyone is broke" for the
    /// life of the entry; and a failure must not throw away a snapshot already in hand, because a
    /// slightly stale board beats an empty one.
    ///
    /// TTL EXPIRY IS NOT COVERED. Nothing here can advance the clock, and adding a clock seam to
    /// production code to assert a 30s window is not worth it - the caching path itself is covered by
    /// the "second call is served" test, and expiry is one comparison against DateTime.UtcNow.
    /// </summary>
    [TestClass]
    public class AccountBankSnapshotTests
    {
        private FakeAccountBankBackend backend;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeAccountBankBackend();
            AccountBankManager.ResetForTesting(backend);
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Restore the production backend and drop this test's balances, so a later test in the same
            // process cannot inherit the fake or its cached snapshot.
            AccountBankManager.ResetForTesting(null);
        }

        [TestMethod]
        public void GetAllBalances_ReturnsEveryAccountsPool()
        {
            backend.Balances[7] = 5_000_000;
            backend.Balances[9] = 12;

            var balances = AccountBankManager.GetAllBalances();

            Assert.IsNotNull(balances);
            Assert.AreEqual(2, balances.Count);
            Assert.AreEqual(5_000_000L, balances[7]);
            Assert.AreEqual(12L, balances[9]);
        }

        /// <summary>
        /// An empty table is a SUCCESSFUL read of a shard where nobody has banked yet, and must come back
        /// as an empty dictionary rather than null - null is reserved for a failure.
        /// </summary>
        [TestMethod]
        public void GetAllBalances_NoAccountsHaveBanked_IsEmptyNotNull()
        {
            var balances = AccountBankManager.GetAllBalances();

            Assert.IsNotNull(balances);
            Assert.AreEqual(0, balances.Count);
        }

        [TestMethod]
        public void GetAllBalances_SecondCallIsServedFromTheSnapshot()
        {
            backend.Balances[7] = 100;

            var first = AccountBankManager.GetAllBalances();
            var second = AccountBankManager.GetAllBalances();

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreEqual(1, backend.AllBalanceReadCalls, "the second call must not hit the backend again");
        }

        /// <summary>
        /// A failed read with nothing cached yet reports unavailable, and - the point of the test - does
        /// not become a cached empty snapshot: the next call must go back to the backend.
        /// </summary>
        [TestMethod]
        public void GetAllBalances_FailedRead_IsNeverCachedAndTheNextCallRetries()
        {
            backend.FailAllBalancesRead = true;

            var failed = AccountBankManager.GetAllBalances();

            Assert.IsNull(failed, "a failed read with no previous snapshot must be null, never an empty board");
            Assert.AreEqual(1, backend.AllBalanceReadCalls);

            backend.FailAllBalancesRead = false;
            backend.Balances[7] = 42;

            var recovered = AccountBankManager.GetAllBalances();

            Assert.AreEqual(2, backend.AllBalanceReadCalls, "the failure must not have started a TTL window");
            Assert.IsNotNull(recovered);
            Assert.AreEqual(42L, recovered[7]);
        }

        /// <summary>
        /// Once a snapshot has been read, a later failure serves the previous one rather than presenting
        /// as empty. A stale balance the next successful read corrects is strictly better than telling a
        /// player their account is empty.
        /// </summary>
        [TestMethod]
        public void GetAllBalances_FailedRead_KeepsServingThePreviousSnapshot()
        {
            backend.Balances[7] = 5_000_000;

            var first = AccountBankManager.GetAllBalances();
            Assert.IsNotNull(first);
            Assert.AreEqual(1, backend.AllBalanceReadCalls);

            // Age the snapshot out WITHOUT discarding it, so the next call genuinely re-reads and
            // genuinely fails. Without this the TTL would serve the cached value and the broken backend
            // would never be consulted - the test would pass whatever the failure path did.
            backend.FailAllBalancesRead = true;
            AccountBankManager.ExpireBulkSnapshotForTesting();

            var stillThere = AccountBankManager.GetAllBalances();

            Assert.AreEqual(2, backend.AllBalanceReadCalls, "the expired snapshot must have been re-read, and the re-read must have failed");
            Assert.IsNotNull(stillThere, "a failed read must not discard a snapshot already in hand");
            Assert.AreEqual(5_000_000L, stillThere[7]);

            // And the failure must not have started a fresh TTL window over itself.
            backend.FailAllBalancesRead = false;
            backend.Balances[7] = 6_000_000;

            var recovered = AccountBankManager.GetAllBalances();

            Assert.AreEqual(3, backend.AllBalanceReadCalls);
            Assert.AreEqual(6_000_000L, recovered[7]);
        }

        /// <summary>
        /// The snapshot must not be a live view of the ledger. A caller ranking off it takes one
        /// consistent picture; movement after that point belongs to the next snapshot.
        /// </summary>
        [TestMethod]
        public void GetAllBalances_SnapshotDoesNotTrackLaterLedgerMovement()
        {
            backend.Balances[7] = 100;

            var snapshot = AccountBankManager.GetAllBalances();

            AccountBankManager.TryAdjust(7, 900, out var newBalance);

            Assert.AreEqual(1000L, newBalance);
            Assert.AreEqual(100L, snapshot[7], "the snapshot already taken must not move under the caller");
        }
    }
}

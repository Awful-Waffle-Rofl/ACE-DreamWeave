using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for AccountBankManager against a fake IAccountBankBackend (no MySQL instance
    /// needed), per Docs/AccountBank/DESIGN.md's fan-out invariants and step 7's test list.
    ///
    /// Two of the seven design-listed cases are NOT covered here because they live inside Player
    /// methods (same-account transfer refusal, the withdraw create-then-debit-first refund path) and
    /// would need a live Player to exercise - no pure static helper exists to call them without one.
    /// Both are covered by the live checks queued in Docs/VERIFY-QUEUE.md instead.
    /// </summary>
    [TestClass]
    public class AccountBankTests
    {
        private FakeAccountBankBackend fake;

        [TestInitialize]
        public void Setup()
        {
            fake = new FakeAccountBankBackend();
            AccountBankManager.ResetForTesting(fake);
        }

        [TestCleanup]
        public void Teardown()
        {
            AccountBankManager.ResetForTesting(null);
        }

        [TestMethod]
        public void CreditThenDebit_RoundTripsAndCacheAgreesWithBackend()
        {
            const uint accountId = 1001;

            var result = AccountBankManager.TryAdjust(accountId, 10_000, out var afterCredit);
            Assert.AreEqual(AccountBankAdjustResult.Applied, result);
            Assert.AreEqual(10_000, afterCredit);

            result = AccountBankManager.TryAdjust(accountId, -4_000, out var afterDebit);
            Assert.AreEqual(AccountBankAdjustResult.Applied, result);
            Assert.AreEqual(6_000, afterDebit);

            Assert.AreEqual(6_000, AccountBankManager.GetBalance(accountId));
            Assert.AreEqual(6_000, fake.Balances[accountId]);
        }

        [TestMethod]
        public void OverWithdraw_IsRefusedAndBalanceUnmoved()
        {
            const uint accountId = 1002;

            AccountBankManager.TryAdjust(accountId, 500, out _);

            var result = AccountBankManager.TryAdjust(accountId, -1_000, out var newBalance);

            Assert.AreEqual(AccountBankAdjustResult.Refused, result);
            Assert.AreEqual(0, newBalance, "newBalance is meaningful only for Applied");
            Assert.AreEqual(500, fake.Balances[accountId]);
        }

        [TestMethod]
        public void DebitOnMissingAccount_IsRefusedAndCreatesNoRow()
        {
            const uint accountId = 1003;

            var result = AccountBankManager.TryAdjust(accountId, -1, out _);

            Assert.AreEqual(AccountBankAdjustResult.Refused, result);
            Assert.IsFalse(fake.Balances.ContainsKey(accountId), "a refused debit must not create a row");
        }

        [TestMethod]
        public void ZeroDelta_IsAppliedWithTheCurrentBalance_AndCreatesNoRow()
        {
            // The real DAO issues NO statement for a zero delta - it falls straight through to the
            // read-back - so it reports Applied with whatever the pool holds, and creates no row for an
            // account that has none. A zero delta is not a debit, and refusing it here would make the
            // fake disagree with the thing it stands in for on the one call every caller can make
            // without meaning to (an empty deposit sweep, a zero-cost purchase).
            const uint missingAccount = 1010;

            var result = AccountBankManager.TryAdjust(missingAccount, 0, out var missingBalance);

            Assert.AreEqual(AccountBankAdjustResult.Applied, result, "a zero delta against a missing row is a no-op read, not a refusal");
            Assert.AreEqual(0, missingBalance);
            Assert.IsFalse(fake.Balances.ContainsKey(missingAccount), "a no-op must not establish that the account has a pool");

            const uint existingAccount = 1011;

            AccountBankManager.TryAdjust(existingAccount, 7_500, out _);

            result = AccountBankManager.TryAdjust(existingAccount, 0, out var existingBalance);

            Assert.AreEqual(AccountBankAdjustResult.Applied, result);
            Assert.AreEqual(7_500, existingBalance, "a zero delta reports the current balance, not 0");
            Assert.AreEqual(7_500, fake.Balances[existingAccount], "a zero delta must not move the balance");
        }

        [TestMethod]
        public void OverflowingCredit_IsRefusedNotThrown()
        {
            const uint accountId = 1004;

            AccountBankManager.TryAdjust(accountId, long.MaxValue - 1, out _);

            var result = AccountBankManager.TryAdjust(accountId, 2, out var newBalance);

            Assert.AreEqual(AccountBankAdjustResult.Refused, result);
            Assert.AreEqual(0, newBalance);
            Assert.AreEqual(long.MaxValue - 1, fake.Balances[accountId], "the refused overflow must not touch the ledger");
        }

        [TestMethod]
        public void Claim_TrueOnce_ThenFalseForSameCharacter()
        {
            const uint characterGuid = 0x50000001;
            const uint accountId = 1005;

            Assert.IsTrue(AccountBankManager.TryClaimFold(characterGuid, accountId, 2_500));
            Assert.IsFalse(AccountBankManager.TryClaimFold(characterGuid, accountId, 2_500), "a second claim for the same character must be refused");

            Assert.AreEqual(2, fake.ClaimCalls, "both claim attempts must reach the backend");
            Assert.AreEqual(1, fake.Folds.Count, "exactly one fold row for this character");
            Assert.IsTrue(AccountBankManager.HasFold(characterGuid) == true);
        }

        [TestMethod]
        public void FailedBalanceRead_IsNotCachedAsZero_AndRecoversOnNextRead()
        {
            const uint accountId = 1006;

            AccountBankManager.TryAdjust(accountId, 7_777, out _);

            // Drop the cache so the next read has to hit the (now failing) backend.
            AccountBankManager.Invalidate(accountId);
            fake.FailBalanceRead = true;

            var ok = AccountBankManager.TryGetBalance(accountId, out var balance);

            Assert.IsFalse(ok, "a failed read must report unavailable, not a balance");
            Assert.AreEqual(0, balance, "balance is a placeholder, not a reading, when ok is false");

            fake.FailBalanceRead = false;

            ok = AccountBankManager.TryGetBalance(accountId, out balance);

            Assert.IsTrue(ok);
            Assert.AreEqual(7_777, balance, "the real balance must still be there once the backend recovers");
        }

        [TestMethod]
        public void FailedAndCountUnknownAdjusts_InvalidateTheCache_SoNextReadHitsBackend()
        {
            const uint accountId = 1007;

            AccountBankManager.TryAdjust(accountId, 1_000, out _);
            Assert.AreEqual(1_000, AccountBankManager.GetBalance(accountId), "warm the cache");

            // Failed: nobody knows whether it committed. The manager must drop the cache regardless.
            fake.FailAdjust = true;
            var result = AccountBankManager.TryAdjust(accountId, 100, out _);
            Assert.AreEqual(AccountBankAdjustResult.Failed, result);
            fake.FailAdjust = false;

            fake.BalanceReadCalls = 0;
            AccountBankManager.GetBalance(accountId);
            Assert.AreEqual(1, fake.BalanceReadCalls, "a Failed adjust must invalidate the cache entry");

            // AppliedCountUnknown: the delta DID land, but the manager must not trust any number it
            // wasn't handed, so it drops the entry and re-reads rather than guessing.
            fake.NextAdjustCountUnknown = true;
            result = AccountBankManager.TryAdjust(accountId, 500, out var newBalance);
            Assert.AreEqual(AccountBankAdjustResult.AppliedCountUnknown, result);
            Assert.AreEqual(0, newBalance, "newBalance is meaningless for AppliedCountUnknown");

            fake.BalanceReadCalls = 0;
            var reread = AccountBankManager.GetBalance(accountId);
            Assert.AreEqual(1, fake.BalanceReadCalls, "an AppliedCountUnknown adjust must invalidate the cache entry");
            Assert.AreEqual(1_500, reread, "the ledger really did move by 500 even though the adjust call could not report it");
        }

        [TestMethod]
        public void Refused_InvalidatesTheCache()
        {
            const uint accountId = 1008;

            AccountBankManager.TryAdjust(accountId, 200, out _);
            Assert.AreEqual(200, AccountBankManager.GetBalance(accountId), "warm the cache");

            var result = AccountBankManager.TryAdjust(accountId, -10_000, out _);
            Assert.AreEqual(AccountBankAdjustResult.Refused, result);

            fake.BalanceReadCalls = 0;
            AccountBankManager.GetBalance(accountId);
            Assert.AreEqual(1, fake.BalanceReadCalls, "a Refused adjust must invalidate the cache entry too");
        }

        [TestMethod]
        public void Release_DropsTheEntry_SoNextReadGoesToBackend()
        {
            const uint accountId = 1009;

            AccountBankManager.TryAdjust(accountId, 42, out _);
            Assert.AreEqual(42, AccountBankManager.GetBalance(accountId), "warm the cache");

            AccountBankManager.Release(accountId);

            fake.BalanceReadCalls = 0;
            var balance = AccountBankManager.GetBalance(accountId);
            Assert.AreEqual(42, balance);
            Assert.AreEqual(1, fake.BalanceReadCalls, "Release must drop the entry so the next read reloads it");
        }
    }
}

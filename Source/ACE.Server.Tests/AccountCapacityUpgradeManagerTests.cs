using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Server.Entity.CapacityUpgrades;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-memory stand-in for the two account_capacity_upgrade* tables, sharing its bank with a
    /// FakeAccountBankBackend so a purchase moves the same pyreals the bank manager reads. Models the
    /// DAO's transaction: token already recorded -> Applied; debit guarded on balance; increment guarded
    /// on the expected count; both roll back together.
    /// </summary>
    internal class FakeCapacityUpgradeStorage : ICapacityUpgradeStorage
    {
        private readonly FakeAccountBankBackend bank;

        public readonly Dictionary<(uint AccountId, CapacityUpgradeKind Kind), int> Counts = new Dictionary<(uint, CapacityUpgradeKind), int>();

        public readonly Dictionary<string, (uint AccountId, CapacityUpgradeKind Kind, int UpgradeNumber, long CostMmd, long CostPyreals, uint CharacterGuid)> Purchases =
            new Dictionary<string, (uint, CapacityUpgradeKind, int, long, long, uint)>();

        /// <summary>When set, every read reports failure.</summary>
        public bool FailRead;

        /// <summary>When set, the next purchase commits fully and THEN reports Unknown, as a lost commit ack would.</summary>
        public bool NextPurchaseCommitsThenUnknown;

        /// <summary>When set, the next purchase reports Unknown without committing anything.</summary>
        public bool NextPurchaseUnknownNoCommit;

        /// <summary>When set, the next purchase throws instead of answering.</summary>
        public bool NextPurchaseThrows;

        public int ReadCalls;
        public int PurchaseCalls;
        public string LastToken;

        public FakeCapacityUpgradeStorage(FakeAccountBankBackend bank)
        {
            this.bank = bank;
        }

        public (bool Ok, Dictionary<CapacityUpgradeKind, int> Counts) GetAccountCapacityUpgrades(uint accountId)
        {
            ReadCalls++;

            if (FailRead)
                return (false, null);

            var result = new Dictionary<CapacityUpgradeKind, int>();

            foreach (var pair in Counts)
            {
                if (pair.Key.AccountId == accountId)
                    result[pair.Key.Kind] = pair.Value;
            }

            return (true, result);
        }

        public CapacityUpgradePurchaseResult TryPurchaseCapacityUpgrade(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, string token, uint characterGuid, out long newBalance)
        {
            newBalance = 0;
            PurchaseCalls++;
            LastToken = token;

            if (NextPurchaseThrows)
            {
                NextPurchaseThrows = false;
                throw new InvalidOperationException("simulated storage failure");
            }

            if (NextPurchaseUnknownNoCommit)
            {
                NextPurchaseUnknownNoCommit = false;
                return CapacityUpgradePurchaseResult.Unknown;
            }

            if (Purchases.TryGetValue(token, out var existing) && existing.AccountId == accountId && existing.Kind == kind)
            {
                newBalance = bank.Balances.TryGetValue(accountId, out var b) ? b : 0;
                return CapacityUpgradePurchaseResult.Applied;
            }

            if (!bank.Balances.TryGetValue(accountId, out var balance) || balance < costPyreals)
                return CapacityUpgradePurchaseResult.InsufficientFunds;

            var current = Counts.TryGetValue((accountId, kind), out var c) ? c : 0;

            if (current != expectedCount)
                return CapacityUpgradePurchaseResult.PriceChanged;

            bank.Balances[accountId] = balance - costPyreals;
            Counts[(accountId, kind)] = expectedCount + 1;
            Purchases[token] = (accountId, kind, expectedCount + 1, costMmd, costPyreals, characterGuid);

            if (NextPurchaseCommitsThenUnknown)
            {
                NextPurchaseCommitsThenUnknown = false;
                return CapacityUpgradePurchaseResult.Unknown;
            }

            newBalance = balance - costPyreals;
            return CapacityUpgradePurchaseResult.Applied;
        }
    }

    /// <summary>
    /// AccountCapacityUpgradeManager against fake storage and a fake bank backend (no MySQL), covering
    /// every purchase result, the failed-read contract, and the bank cache handling of the purchase
    /// path through AccountBankManager.TryAdjustVia. Reads no PropertyManager keys.
    /// </summary>
    [TestClass]
    public class AccountCapacityUpgradeManagerTests
    {
        private const uint AccountId = 4242;
        private const uint CharacterGuid = 0x50000042;
        private const long MmdCost = 500;
        private const long PyrealCost = 125_000_000;

        private FakeAccountBankBackend bank;
        private FakeCapacityUpgradeStorage storage;

        [TestInitialize]
        public void Setup()
        {
            bank = new FakeAccountBankBackend();
            storage = new FakeCapacityUpgradeStorage(bank);

            AccountBankManager.ResetForTesting(bank);
            AccountCapacityUpgradeManager.ResetForTesting(storage);
        }

        [TestCleanup]
        public void Teardown()
        {
            AccountCapacityUpgradeManager.ResetForTesting(null);
            AccountBankManager.ResetForTesting(null);
        }

        private void Fund(long amount) => bank.Balances[AccountId] = amount;

        [TestMethod]
        public void GetCount_LoadsOnce_AndKindsAreIndependent()
        {
            storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)] = 3;

            Assert.IsTrue(AccountCapacityUpgradeManager.TryGetCount(AccountId, CapacityUpgradeKind.MuleVault, out var mule));
            Assert.AreEqual(3, mule);
            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MarketListings));
            Assert.AreEqual(3, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));

            Assert.AreEqual(1, storage.ReadCalls, "one load serves every later read");
        }

        [TestMethod]
        public void FailedRead_IsReportedAndNotCached()
        {
            storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)] = 5;
            storage.FailRead = true;

            Assert.IsFalse(AccountCapacityUpgradeManager.TryGetCount(AccountId, CapacityUpgradeKind.MuleVault, out var count));
            Assert.AreEqual(0, count);
            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault), "GetCount reports 0 when unavailable");

            storage.FailRead = false;

            Assert.IsTrue(AccountCapacityUpgradeManager.TryGetCount(AccountId, CapacityUpgradeKind.MuleVault, out count), "the failure was not cached");
            Assert.AreEqual(5, count);
            Assert.AreEqual(3, storage.ReadCalls);
        }

        [TestMethod]
        public void Purchase_Applied_DebitsIncrementsAndUpdatesBothCaches()
        {
            Fund(200_000_000);

            Assert.IsTrue(AccountCapacityUpgradeManager.TryGetCount(AccountId, CapacityUpgradeKind.MuleVault, out var owned));
            Assert.AreEqual(0, owned);

            var result = AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, owned, MmdCost, PyrealCost, CharacterGuid, out var newBalance);

            Assert.AreEqual(CapacityUpgradePurchaseResult.Applied, result);
            Assert.AreEqual(75_000_000, newBalance);
            Assert.AreEqual(75_000_000, bank.Balances[AccountId]);
            Assert.AreEqual(1, storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)]);

            var readsBefore = storage.ReadCalls;
            Assert.AreEqual(1, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));
            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MarketListings), "the other kind's counter is untouched");
            Assert.AreEqual(readsBefore, storage.ReadCalls, "the cached count was set from the purchase, not re-read");

            var purchase = storage.Purchases[storage.LastToken];
            Assert.AreEqual(1, purchase.UpgradeNumber);
            Assert.AreEqual(MmdCost, purchase.CostMmd);
            Assert.AreEqual(PyrealCost, purchase.CostPyreals);
            Assert.AreEqual(CharacterGuid, purchase.CharacterGuid);
            Assert.AreEqual(32, storage.LastToken.Length);
        }

        /// <summary>
        /// The bank cache after a purchase: a balance cached BEFORE the purchase must never be served
        /// after it. On Applied the cache holds the post-debit balance with no extra read; on Unknown it
        /// is dropped so the next read goes to the ledger.
        /// </summary>
        [TestMethod]
        public void Purchase_BankCache_HoldsPostDebitBalanceOnApplied_AndIsDroppedOnUnknown()
        {
            Fund(400_000_000);

            Assert.AreEqual(400_000_000, AccountBankManager.GetBalance(AccountId), "prime the bank cache with the pre-debit balance");
            var bankReads = bank.BalanceReadCalls;

            Assert.AreEqual(CapacityUpgradePurchaseResult.Applied,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MarketListings, 0, MmdCost, PyrealCost, CharacterGuid));

            Assert.AreEqual(275_000_000, AccountBankManager.GetBalance(AccountId), "the cache must hold the post-debit balance");
            Assert.AreEqual(bankReads, bank.BalanceReadCalls, "Applied carries the balance; no re-read needed");

            storage.NextPurchaseCommitsThenUnknown = true;

            Assert.AreEqual(CapacityUpgradePurchaseResult.Unknown,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MarketListings, 1, 600, 150_000_000, CharacterGuid));

            Assert.AreEqual(125_000_000, AccountBankManager.GetBalance(AccountId), "after Unknown the bank cache must re-read the ledger, which did move");
            Assert.AreEqual(bankReads + 1, bank.BalanceReadCalls);
        }

        [TestMethod]
        public void Purchase_InsufficientFunds_MovesNothing()
        {
            Fund(PyrealCost - 1);

            var result = AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid, out var newBalance);

            Assert.AreEqual(CapacityUpgradePurchaseResult.InsufficientFunds, result);
            Assert.AreEqual(0, newBalance);
            Assert.AreEqual(PyrealCost - 1, bank.Balances[AccountId]);
            Assert.IsFalse(storage.Counts.ContainsKey((AccountId, CapacityUpgradeKind.MuleVault)));
            Assert.AreEqual(0, storage.Purchases.Count);
        }

        [TestMethod]
        public void Purchase_NoBankRow_IsInsufficientFunds()
        {
            Assert.AreEqual(CapacityUpgradePurchaseResult.InsufficientFunds,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid));
        }

        [TestMethod]
        public void Purchase_PriceChanged_MovesNothing_AndDropsTheCachedCount()
        {
            Fund(1_000_000_000);
            storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)] = 0;

            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));

            // Another server path bought one behind this cache's back.
            storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)] = 1;

            var result = AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid);

            Assert.AreEqual(CapacityUpgradePurchaseResult.PriceChanged, result);
            Assert.AreEqual(1_000_000_000, bank.Balances[AccountId], "the debit rolled back");
            Assert.AreEqual(1, storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)]);

            Assert.IsTrue(AccountCapacityUpgradeManager.TryGetCount(AccountId, CapacityUpgradeKind.MuleVault, out var count));
            Assert.AreEqual(1, count, "the stale count was dropped and re-read");
        }

        [TestMethod]
        public void Purchase_Unknown_DropsCountCache_AndLeavesCommittedStateReadable()
        {
            Fund(1_000_000_000);
            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));

            storage.NextPurchaseCommitsThenUnknown = true;

            var result = AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid, out var newBalance);

            Assert.AreEqual(CapacityUpgradePurchaseResult.Unknown, result);
            Assert.AreEqual(0, newBalance);

            var readsBefore = storage.ReadCalls;
            Assert.AreEqual(1, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault), "the count cache was evicted and re-reads the committed purchase");
            Assert.AreEqual(readsBefore + 1, storage.ReadCalls);
        }

        [TestMethod]
        public void Purchase_Unknown_WithoutCommit_DropsBothCaches()
        {
            Fund(1_000_000_000);
            Assert.AreEqual(1_000_000_000, AccountBankManager.GetBalance(AccountId));
            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));

            var bankReads = bank.BalanceReadCalls;
            var countReads = storage.ReadCalls;

            storage.NextPurchaseUnknownNoCommit = true;

            Assert.AreEqual(CapacityUpgradePurchaseResult.Unknown,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid));

            Assert.AreEqual(1_000_000_000, AccountBankManager.GetBalance(AccountId));
            Assert.AreEqual(0, AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));
            Assert.AreEqual(bankReads + 1, bank.BalanceReadCalls, "bank cache dropped");
            Assert.AreEqual(countReads + 1, storage.ReadCalls, "count cache dropped");
        }

        [TestMethod]
        public void Purchase_StorageThrows_IsUnknown_NeverARefusal()
        {
            Fund(1_000_000_000);
            storage.NextPurchaseThrows = true;

            var result = AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid, out var newBalance);

            Assert.AreEqual(CapacityUpgradePurchaseResult.Unknown, result);
            Assert.AreEqual(0, newBalance);
        }

        [TestMethod]
        public void Purchase_InvalidRequest_NeverReachesStorage()
        {
            Fund(1_000_000_000);

            Assert.AreEqual(CapacityUpgradePurchaseResult.InvalidRequest,
                AccountCapacityUpgradeManager.TryPurchase(0, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid));
            Assert.AreEqual(CapacityUpgradePurchaseResult.InvalidRequest,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, (CapacityUpgradeKind)0, 0, MmdCost, PyrealCost, CharacterGuid));
            Assert.AreEqual(CapacityUpgradePurchaseResult.InvalidRequest,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, -1, MmdCost, PyrealCost, CharacterGuid));
            Assert.AreEqual(CapacityUpgradePurchaseResult.InvalidRequest,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, 0, CharacterGuid));

            Assert.AreEqual(0, storage.PurchaseCalls);
        }

        [TestMethod]
        public void Purchase_RetriedToken_IsAppliedOnceOnly()
        {
            Fund(1_000_000_000);
            AccountCapacityUpgradeManager.TokenSource = () => "0123456789abcdef0123456789abcdef";

            Assert.AreEqual(CapacityUpgradePurchaseResult.Applied,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid));

            // Same token again stands in for the strategy re-running a committed delegate.
            Assert.AreEqual(CapacityUpgradePurchaseResult.Applied,
                AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid, out var balance));

            Assert.AreEqual(875_000_000, balance);
            Assert.AreEqual(875_000_000, bank.Balances[AccountId], "charged once");
            Assert.AreEqual(1, storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)]);
        }

        [TestMethod]
        public void Purchase_EachCallUsesAFreshToken()
        {
            Fund(1_000_000_000);

            AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 0, MmdCost, PyrealCost, CharacterGuid);
            var first = storage.LastToken;

            AccountCapacityUpgradeManager.TryPurchase(AccountId, CapacityUpgradeKind.MuleVault, 1, 600, 150_000_000, CharacterGuid);
            var second = storage.LastToken;

            Assert.AreNotEqual(first, second);
            Assert.AreEqual(2, storage.Counts[(AccountId, CapacityUpgradeKind.MuleVault)]);
            Assert.AreEqual(725_000_000, bank.Balances[AccountId]);
        }

        [TestMethod]
        public void ToBankResult_MapsEveryPurchaseResult()
        {
            Assert.AreEqual(AccountBankAdjustResult.Applied, AccountCapacityUpgradeManager.ToBankResult(CapacityUpgradePurchaseResult.Applied));
            Assert.AreEqual(AccountBankAdjustResult.Refused, AccountCapacityUpgradeManager.ToBankResult(CapacityUpgradePurchaseResult.InsufficientFunds));
            Assert.AreEqual(AccountBankAdjustResult.Refused, AccountCapacityUpgradeManager.ToBankResult(CapacityUpgradePurchaseResult.PriceChanged));
            Assert.AreEqual(AccountBankAdjustResult.Refused, AccountCapacityUpgradeManager.ToBankResult(CapacityUpgradePurchaseResult.InvalidRequest));
            Assert.AreEqual(AccountBankAdjustResult.Failed, AccountCapacityUpgradeManager.ToBankResult(CapacityUpgradePurchaseResult.Unknown));
        }

        /// <summary>TryAdjustVia applies TryAdjust's cache rule to an arbitrary mutation.</summary>
        [TestMethod]
        public void TryAdjustVia_AppliesTheBankCacheRule()
        {
            Fund(1_000);
            Assert.AreEqual(1_000, AccountBankManager.GetBalance(AccountId));
            var reads = bank.BalanceReadCalls;

            var result = AccountBankManager.TryAdjustVia(AccountId, (out long b) => { b = 400; return AccountBankAdjustResult.Applied; }, "test applied", out var newBalance);
            Assert.AreEqual(AccountBankAdjustResult.Applied, result);
            Assert.AreEqual(400, newBalance);
            Assert.AreEqual(400, AccountBankManager.GetBalance(AccountId), "Applied caches the reported balance");
            Assert.AreEqual(reads, bank.BalanceReadCalls);

            result = AccountBankManager.TryAdjustVia(AccountId, (out long b) => { b = 0; return AccountBankAdjustResult.Refused; }, "test refused", out _);
            Assert.AreEqual(AccountBankAdjustResult.Refused, result);
            AccountBankManager.GetBalance(AccountId);
            Assert.AreEqual(reads + 1, bank.BalanceReadCalls, "Refused drops the entry");

            result = AccountBankManager.TryAdjustVia(AccountId, (out long b) => throw new InvalidOperationException("boom"), "test throw", out newBalance);
            Assert.AreEqual(AccountBankAdjustResult.Failed, result);
            Assert.AreEqual(0, newBalance);
            AccountBankManager.GetBalance(AccountId);
            Assert.AreEqual(reads + 2, bank.BalanceReadCalls, "a throw drops the entry");
        }
    }
}

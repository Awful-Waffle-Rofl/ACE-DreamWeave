using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The purchase transaction and every compensation path in DESIGN 5.4. Each test is named for the
    /// invariant it protects (e.g. buyer charged with nothing delivered, seller paid twice), each a silent-in-production failure.
    /// </summary>
    [TestClass]
    public class MarketPurchaseTests
    {
        private const uint SellerAccount = 8001;
        private const uint SellerCharacter = 0x50000201;
        private const uint BuyerAccount = 8002;
        private const uint BuyerCharacter = 0x50000202;

        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Marketbuyer");

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;

        [TestInitialize]
        public void Setup()
        {
            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();

            MarketManagerTests.SeedMarketTunables();

            MarketManager.Initialize(store, wallet, repo);

            wallet.Seed(BuyerCharacter, 100);
            wallet.Seed(SellerCharacter, 0);
        }

        [TestCleanup]
        public void Teardown() => MarketManager.Shutdown();

        private MarketListing ListOneItem(long price = 10, uint wcid = 9001)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, wcid, 1, price, MarketChannel.InGame);
            Assert.IsTrue(result.Ok, $"seeding a listing failed with {result.Error}");
            return result.Value;
        }

        private MarketListing ListLedger(uint wcid = 9500, int held = 100, int listed = 50, long price = 2)
        {
            store.SeedLedger(SellerAccount, wcid, held);

            var result = MarketManager.List(Seller, null, wcid, listed, price, MarketChannel.InGame);
            Assert.IsTrue(result.Ok, $"seeding a ledger listing failed with {result.Error}");
            return result.Value;
        }

        [TestMethod]
        public void Buy_HappyPath_MovesTheItem_ChargesTheBuyer_PaysTheSeller_AndClosesTheListing()
        {
            var listing = ListOneItem(price: 10);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"expected a completed purchase, got {result.Error}");
            Assert.AreEqual(MarketTransactionStatus.Completed, result.Value.Status);
            Assert.AreEqual(10, result.Value.PriceMmdTotal);
            Assert.AreEqual(MarketChannel.Web, result.Value.Channel);

            Assert.AreEqual(90, wallet.GetBalanceMmd(BuyerCharacter), "the buyer pays exactly count * price");
            Assert.AreEqual(10, wallet.GetBalanceMmd(SellerCharacter), "the seller is paid exactly count * price");

            Assert.AreEqual(1, store.Items[BuyerAccount].Count, "the item lands in the buyer's vault");
            Assert.IsFalse(store.Items[SellerAccount].Any(), "the item leaves the seller's vault");

            Assert.AreEqual(MarketListingStatus.Sold, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Buy_AFreeListing_CompletesWithoutMovingAnyMoney()
        {
            var listing = ListOneItem(price: 0);

            var balanceBefore = wallet.GetBalanceMmd(BuyerCharacter);
            var sellerBefore = wallet.GetBalanceMmd(SellerCharacter);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 0, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"a free purchase must complete, got {result.Error}");
            Assert.AreEqual(MarketTransactionStatus.Completed, result.Value.Status,
                            "a zero total must never resolve as Failed or DebitLedgerUnknown");
            Assert.AreEqual(0, result.Value.PriceMmdTotal);
            Assert.AreEqual(balanceBefore, wallet.GetBalanceMmd(BuyerCharacter), "the buyer must not be debited");
            Assert.AreEqual(sellerBefore, wallet.GetBalanceMmd(SellerCharacter), "the seller must not be credited");
            Assert.IsTrue(store.Holds(BuyerAccount, listing.ItemGuid, listing.Wcid, 1), "the item must reach the buyer's vault");
        }

        [TestMethod]
        public void Buy_NeverNestsTwoAccountsSerializedRegions()
        {
            // The deadlock this forbids is real and symmetric: "A buys from B" and "B buys from A"
            // running at once would each hold one vault queue and wait for the other.
            ListOneItem();

            MarketManager.Buy(Buyer, MarketManager.GetActiveListingsForAccount(SellerAccount).Single().Id, 1, 10, MarketChannel.Web);

            Assert.AreEqual(1, store.MaxNestedAccounts,
                "the purchase must take the seller's region, leave it holding the items, and only then take the buyer's");
        }

        [TestMethod]
        public void Buy_PartialStack_DecrementsTheListingAndLeavesItActive()
        {
            var listing = ListLedger(wcid: 9500, held: 100, listed: 50, price: 2);

            var result = MarketManager.Buy(Buyer, listing.Id, 20, 2, MarketChannel.InGame);

            Assert.IsTrue(result.Ok, $"expected a partial buy, got {result.Error}");
            Assert.AreEqual(20, result.Value.Count);
            Assert.AreEqual(40, result.Value.PriceMmdTotal, "total is count * per-unit price");

            var live = MarketManager.GetListing(listing.Id);
            Assert.AreEqual(MarketListingStatus.Active, live.Status);
            Assert.AreEqual(30, live.Count, "the listing decrements by what was sold");

            Assert.AreEqual(60, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(40, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual(80, store.LedgerCount(SellerAccount, 9500), "only the sold units leave the ledger");
        }

        [TestMethod]
        public void Buy_TheLastUnitsOfAStack_ClosesTheListingAsSold()
        {
            var listing = ListLedger(wcid: 9500, held: 100, listed: 50, price: 1);

            Assert.IsTrue(MarketManager.Buy(Buyer, listing.Id, 50, 1, MarketChannel.Web).Ok);

            Assert.AreEqual(MarketListingStatus.Sold, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Buy_MoreThanIsListed_IsRefusedWithCountUnavailable()
        {
            var listing = ListLedger(wcid: 9500, held: 100, listed: 50, price: 1);

            var result = MarketManager.Buy(Buyer, listing.Id, 51, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.CountUnavailable, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter), "nothing was charged");
            Assert.AreEqual(0, repo.Transactions.Count, "no row is written for a validation refusal");
        }

        [TestMethod]
        public void Buy_AtAStalePrice_IsRefusedAndReportsTheLivePrice()
        {
            var listing = ListOneItem(price: 10);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 7, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.PriceChanged, result.Error);
            Assert.AreEqual(10, result.CurrentPriceMmd,
                "the live price must travel back with the rejection so the web app can offer the real number");
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter), "a stale price never silently reprices");
        }

        [TestMethod]
        public void Buy_ByTheSeller_IsRefused()
        {
            var listing = ListOneItem();
            wallet.Seed(SellerCharacter, 100);

            var result = MarketManager.Buy(Seller, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NotOwner, result.Error);
        }

        [TestMethod]
        public void Buy_AClosedListing_IsRefused()
        {
            var listing = ListOneItem();
            MarketManager.Delist(Seller, listing.Id, MarketChannel.InGame);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.ListingNotActive, result.Error);
        }

        [TestMethod]
        public void Buy_WithoutTheFunds_ChargesNothingAndMarksTheRowFailed()
        {
            var listing = ListOneItem(price: 500);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 500, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InsufficientFunds, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(1, store.Items[SellerAccount].Count, "the item never left the seller");

            var row = repo.Transactions.Single();
            Assert.AreEqual((int)MarketTransactionStatus.Failed, row.Status,
                "the Pending row must be resolved, not left dangling for boot recovery to find");
        }

        [TestMethod]
        public void Buy_WhenTheDebitStateIsUnknown_MarksTheRowDistinctlyFromARefusal()
        {
            var listing = ListOneItem(price: 10);
            wallet.LedgerUnknownDebitCharacters.Add(BuyerCharacter);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.LedgerUnknown, result.Error);
            Assert.AreEqual(1, store.Items[SellerAccount].Count, "nothing is delivered on an unresolved payment");

            var row = repo.Transactions.Single();

            Assert.AreEqual((int)MarketTransactionStatus.DebitLedgerUnknown, row.Status,
                "a buyer who may have paid and got nothing must be findable in market_transaction; boot recovery scans Pending only, so this resolved row is the only record");

            Assert.AreNotEqual((int)MarketTransactionStatus.Failed, row.Status,
                "Failed asserts the buyer provably was not charged, which is exactly what nobody knows here");
        }

        [TestMethod]
        public void Buy_WhenTheDebitErrors_MarksTheRowFailedLikeAnyOtherRefusal()
        {
            // The control for the case above: an ordinary debit error IS a proof that nothing moved,
            // so it must NOT claim the ledger-unknown status.
            var listing = ListOneItem(price: 10);
            wallet.FailDebitCharacters.Add(BuyerCharacter);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.ServerError, result.Error);
            Assert.AreEqual((int)MarketTransactionStatus.Failed, repo.Transactions.Single().Status);
        }

        [TestMethod]
        public void Buy_WhenTheWithdrawFails_RefundsTheBuyerAndMarksTheRowRefunded()
        {
            var listing = ListOneItem(price: 10);
            store.FailTakeAccounts.Add(SellerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter), "a buyer whose item never moved must be made whole");
            Assert.AreEqual(0, wallet.GetBalanceMmd(SellerCharacter), "the seller is never paid for a sale that did not happen");
            Assert.AreEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single().Status);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status,
                "a failed purchase leaves the listing sellable");
        }

        [TestMethod]
        public void Buy_WhenTheSellersRegionWillNotRun_RefundsTheBuyerAndMarksTheRowRefunded()
        {
            var listing = ListOneItem(price: 10);
            store.RefuseSerializedAccounts.Add(SellerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(1, store.Items[SellerAccount].Count, "the item never left the seller");
            Assert.AreEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single().Status);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Buy_WhenTheDepositFails_ReturnsTheItemToTheSellerAndRefundsTheBuyer()
        {
            var listing = ListOneItem(price: 10);
            store.FailGiveAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(1, store.ReturnCalls, "the taken item must go back through the return path, never be destroyed");
            Assert.AreEqual(1, store.Items[SellerAccount].Count, "the item is back where it started");
            Assert.AreEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single().Status);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
            Assert.AreEqual(0, wallet.GetBalanceMmd(SellerCharacter), "the seller is never paid for an undelivered sale");
        }

        [TestMethod]
        public void Buy_WhenTheBuyerVaultIsFull_ReturnsVaultFullNotVaultUnavailable()
        {
            // The bug this guards: a full buyer vault was reported as vault_unavailable, which the
            // web app reads as transient and auto-retries - each retry debits, takes the item, fails
            // the deposit, and refunds, without ever telling the buyer the real reason.
            var listing = ListOneItem(price: 10);
            store.FullVaultAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultFull, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(1, store.ReturnCalls, "the taken item must go back through the return path, never be destroyed");
            Assert.AreEqual(1, store.Items[SellerAccount].Count, "the item is back where it started");
            Assert.AreEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single().Status);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
            Assert.AreEqual(0, wallet.GetBalanceMmd(SellerCharacter), "the seller is never paid for an undelivered sale");
        }

        [TestMethod]
        public void Buy_WhenTheDepositFailsOnALedgerListing_ReturnsTheUnitsToTheLedger()
        {
            var listing = ListLedger(wcid: 9500, held: 100, listed: 50, price: 2);
            store.FailGiveAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 20, 2, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(100, store.LedgerCount(SellerAccount, 9500),
                "a ledger listing must be returned as units, not as a stored biota");
            Assert.AreEqual(50, MarketManager.GetListing(listing.Id).Count, "the listing is not decremented by a failed sale");
        }

        [TestMethod]
        public void Buy_WhenTheDepositAndTheReturnBothFail_StillRefundsTheBuyer()
        {
            // The worst case: an item is in nobody's vault. The buyer must STILL be made whole -
            // charging someone for an item that is now in limbo is the one outcome with no
            // defensible reading.
            var listing = ListOneItem(price: 10);
            store.FailGiveAccounts.Add(BuyerAccount);
            store.FailReturnAccounts.Add(SellerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single().Status);
            Assert.AreEqual(0, store.Items[SellerAccount].Count, "the item is in limbo, which is what the LOST ITEM RISK log is for");
            Assert.IsFalse(store.Items.ContainsKey(BuyerAccount) && store.Items[BuyerAccount].Any(),
                "the buyer must not receive an item the deposit refused");
        }

        [TestMethod]
        public void Buy_WhenTheSellerCreditFails_TheBuyerStillKeepsTheItemAndTheRowCompletes()
        {
            // Do NOT unwind here: the buyer already has the item and the seller can be paid by
            // hand from market_transaction. Clawing the item back would be a worse wrong.
            var listing = ListOneItem(price: 10);
            wallet.FailCreditCharacters.Add(SellerCharacter);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsTrue(result.Ok, "a failed seller credit does not fail the purchase");
            Assert.AreEqual(1, store.Items[BuyerAccount].Count);
            Assert.AreEqual((int)MarketTransactionStatus.Completed, repo.Transactions.Single().Status);
            Assert.AreEqual(90, wallet.GetBalanceMmd(BuyerCharacter), "the buyer is not refunded a sale that delivered");
            Assert.AreEqual(MarketListingStatus.Sold, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Buy_WhenTheBuyerVaultIsNotReady_RefusesBeforeChargingAnything()
        {
            var listing = ListOneItem(price: 10);
            store.NotReadyAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(0, repo.Transactions.Count);
        }

        [TestMethod]
        public void Buy_WhenTheSellerVaultIsNotReady_RefusesBeforeChargingAnything()
        {
            var listing = ListOneItem(price: 10);
            store.NotReadyAccounts.Add(SellerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(0, repo.Transactions.Count);
        }

        [TestMethod]
        public void Buy_WhenTheBuyerVaultIsFullAtPreflight_RefusesBeforeAnythingMovesAndNeverTouchesTheSeller()
        {
            // The bug this guards (2026-09-02): a web re-post retried a buy against a full vault five
            // times, and each attempt got PAST step 0 into the debit and the take before the deposit
            // failed at step 4, producing five Pending/Refunded rows for one click. A refusal knowable
            // up front - the buyer's vault is already full - must stop before the Pending row, the
            // debit, or the seller's item ever move. Contrast with
            // Buy_WhenTheBuyerVaultIsFull_ReturnsVaultFullNotVaultUnavailable, which injects the same
            // VaultFull error at the give step and still exercises the take/return unwind.
            var listing = ListOneItem(price: 10);
            store.PreflightFullVaultAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultFull, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter), "no debit for a refusal seen at step 0");
            Assert.AreEqual(0, wallet.DebitCalls);
            Assert.AreEqual(0, repo.Transactions.Count, "no Pending/Refunded row at all - the refusal is quiet");
            Assert.AreEqual(0, store.TakeCalls, "the seller's item must never be taken for a purchase refused at step 0");
            Assert.AreEqual(1, store.Items[SellerAccount].Count, "the seller's item is untouched");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Buy_WhenThePendingRowCannotBeWritten_RefusesBeforeAnythingMoves()
        {
            // Without the Pending row a debit that landed leaves no record that anyone was owed a
            // refund, so the purchase must not proceed past step 1.
            var listing = ListOneItem(price: 10);
            repo.FailAddTransaction = true;

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.ServerError, result.Error);
            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(0, wallet.DebitCalls, "the debit must not be attempted without a Pending row");
            Assert.AreEqual(1, store.Items[SellerAccount].Count);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Buy_WhenTheListingUpdateFails_TheRowStillCompletes()
        {
            // Step 6 is bookkeeping: the buyer has the item and the seller has been paid, so a
            // failed listing write must not unwind. The invalidation pass corrects the listing.
            var listing = ListOneItem(price: 10);
            repo.FailUpdateListing = true;

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"expected a completed purchase, got {result.Error}");
            Assert.AreEqual(1, store.Items[BuyerAccount].Count);
            Assert.AreEqual(90, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(10, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual((int)MarketTransactionStatus.Completed, repo.Transactions.Single().Status);
        }

        [TestMethod]
        public void Buy_AdvancesTheHistoryFeed()
        {
            var listing = ListOneItem(price: 10);
            var before = MarketManager.ChangeSequence;

            MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            var changes = MarketManager.GetHistoryChanges(before, MarketManager.MaxFeedRows, out _);

            Assert.AreEqual(1, changes.Count, "a completed purchase must reach the history feed");
            Assert.AreEqual(MarketTransactionStatus.Completed, changes[0].Status);
        }

        // ---- the MMD ceiling: count * price_mmd must never reach the bound Player.TryCreditBankedMmd refuses ----

        [TestMethod]
        public void MaxPriceMmd_IsTheLargestNoteCountThatCanBeCreditedToABank()
        {
            Assert.AreEqual(long.MaxValue / Player.MmdValue, MarketManager.MaxPriceMmd,
                "the market's ceiling IS Player.TryCreditBankedMmd's bound; any higher and a credit is refused after the goods moved");
        }

        [TestMethod]
        public void List_AboveTheMmdCeiling_IsRefusedAsInvalidPrice()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 1, MarketManager.MaxPriceMmd + 1, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, result.Error);
            Assert.AreEqual(0, repo.Listings.Count, "an uncreditable price must never reach the table");
        }

        [TestMethod]
        public void Buy_WhenCountTimesPriceWouldExceedTheCeiling_RefusesBeforeAnythingMoves()
        {
            // Each unit is creditable on its own; the product is not. Without this check the
            // multiply overflows long and the ladder runs on a wrapped, possibly negative, total.
            var listing = ListLedger(wcid: 9500, held: 100, listed: 50, price: MarketManager.MaxPriceMmd);

            var result = MarketManager.Buy(Buyer, listing.Id, 2, MarketManager.MaxPriceMmd, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, result.Error);
            Assert.AreEqual(0, repo.Transactions.Count, "no row is written for a validation refusal");
            Assert.AreEqual(100, store.LedgerCount(SellerAccount, 9500));
        }

        // ---- boot recovery ----

        [TestMethod]
        public void RecoverPending_RefundsARowWhoseDebitLandedButWhoseCompletionDidNot()
        {
            // The restart case. The row is Pending and older than the request timeout, and the
            // listing is still Active, so the debit is the only thing that could have landed.
            var listing = ListOneItem(price: 10);

            repo.Transactions.Add(new ACE.Database.Models.Shard.MarketTransaction
            {
                Id = 999,
                ListingId = listing.Id,
                BuyerAccountId = BuyerAccount,
                BuyerCharacterGuid = BuyerCharacter,
                BuyerCharacterName = "Marketbuyer",
                SellerAccountId = SellerAccount,
                SellerCharacterGuid = SellerCharacter,
                SellerCharacterName = "Marketseller",
                Wcid = 9001,
                ItemName = "Test Item 9001",
                Count = 1,
                PriceMmdTotal = 10,
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Channel = (int)MarketChannel.Web,
                Status = (int)MarketTransactionStatus.Pending,
            });

            MarketManager.RecoverPendingTransactions(3000);

            Assert.AreEqual(110, wallet.GetBalanceMmd(BuyerCharacter), "a debit with no completion is refunded");
            Assert.AreEqual((int)MarketTransactionStatus.Refunded,
                repo.Transactions.Single(t => t.Id == 999).Status);
        }

        [TestMethod]
        public void RecoverPending_LeavesARecentRowAlone()
        {
            var listing = ListOneItem(price: 10);

            repo.Transactions.Add(new ACE.Database.Models.Shard.MarketTransaction
            {
                Id = 998,
                ListingId = listing.Id,
                BuyerAccountId = BuyerAccount,
                BuyerCharacterGuid = BuyerCharacter,
                BuyerCharacterName = "Marketbuyer",
                SellerAccountId = SellerAccount,
                SellerCharacterGuid = SellerCharacter,
                SellerCharacterName = "Marketseller",
                Wcid = 9001,
                ItemName = "Test Item 9001",
                Count = 1,
                PriceMmdTotal = 10,
                Timestamp = DateTime.UtcNow,
                Channel = (int)MarketChannel.Web,
                Status = (int)MarketTransactionStatus.Pending,
            });

            MarketManager.RecoverPendingTransactions(3000);

            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter),
                "a purchase that may still be in flight must not be refunded out from under itself");
            Assert.AreEqual((int)MarketTransactionStatus.Pending, repo.Transactions.Single(t => t.Id == 998).Status);
        }

        [TestMethod]
        public void RecoverPending_OverASoldListing_CompletesTheRowRatherThanRefundingIt()
        {
            // Only the final UPDATE was lost: the buyer has the item and the seller was paid, so a
            // refund here would pay the buyer for goods they kept.
            var listing = ListOneItem(price: 10);
            Assert.IsTrue(MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web).Ok);

            repo.Transactions.Add(new ACE.Database.Models.Shard.MarketTransaction
            {
                Id = 997,
                ListingId = listing.Id,
                BuyerAccountId = BuyerAccount,
                BuyerCharacterGuid = BuyerCharacter,
                BuyerCharacterName = "Marketbuyer",
                SellerAccountId = SellerAccount,
                SellerCharacterGuid = SellerCharacter,
                SellerCharacterName = "Marketseller",
                Wcid = 9001,
                ItemName = "Test Item 9001",
                Count = 1,
                PriceMmdTotal = 10,
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Channel = (int)MarketChannel.Web,
                Status = (int)MarketTransactionStatus.Pending,
            });

            MarketManager.RecoverPendingTransactions(3000);

            Assert.AreEqual(90, wallet.GetBalanceMmd(BuyerCharacter), "a Sold listing means the goods were delivered");
            Assert.AreEqual((int)MarketTransactionStatus.Completed, repo.Transactions.Single(t => t.Id == 997).Status);
        }

        [TestMethod]
        public void RecoverPending_WhenTheReadFails_DoesNothingRatherThanConcludingThereIsNothing()
        {
            repo.FailPendingRead = true;

            MarketManager.RecoverPendingTransactions(3000);

            Assert.AreEqual(100, wallet.GetBalanceMmd(BuyerCharacter));
        }

        [TestMethod]
        public void RecoverPending_DoesNotRefundAPartialSaleWhoseGoodsAlreadyMoved()
        {
            // A partial sale leaves the listing ACTIVE with a decremented count, never Sold, so
            // recovery cannot read delivery off the listing. The row must already be Completed.
            var listing = ListLedger(wcid: 9500, held: 10, listed: 10, price: 5);
            wallet.ThrowCreditCharacters.Add(SellerCharacter);

            try
            {
                MarketManager.Buy(Buyer, listing.Id, 3, 5, MarketChannel.Web);
            }
            catch (InvalidOperationException)
            {
                // The process died paying the seller, after the goods were delivered.
            }

            Assert.AreEqual(85, wallet.GetBalanceMmd(BuyerCharacter), "the buyer was charged");
            Assert.AreEqual(7, store.LedgerCount(SellerAccount, 9500), "the units left the seller");
            Assert.AreEqual(1, store.Items[BuyerAccount].Count, "the buyer holds the goods");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status,
                "a partial sale never reaches Sold, which is exactly why the listing cannot be the recovery signal");

            var row = repo.Transactions.Single();
            row.Timestamp = DateTime.UtcNow.AddMinutes(-5);

            MarketManager.RecoverPendingTransactions(3000);

            Assert.AreEqual(85, wallet.GetBalanceMmd(BuyerCharacter),
                "refunding a buyer who already holds the goods is free items plus their money back");
            Assert.AreNotEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single().Status,
                "a delivered sale must never be recorded as Refunded");
        }

        [TestMethod]
        public void Buy_PartialLedger_WithTheAutoDelistHookLive_LeavesTheListingActiveWithTheRemainder()
        {
            // The market's own take runs through the same vault withdraw the auto-delist hook watches.
            // Nothing else in the suite wires the two together, which is how the sale-delists-itself
            // defect survived every per-stream review.
            var previous = AccountVaultStore.PreWithdrawHook;

            try
            {
                AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;

                var listing = ListLedger(wcid: 9500, held: 10, listed: 10, price: 2);

                var result = MarketManager.Buy(Buyer, listing.Id, 3, 2, MarketChannel.Web);

                Assert.IsTrue(result.Ok, $"expected a completed purchase, got {result.Error}");

                var after = MarketManager.GetListing(listing.Id);

                Assert.AreEqual(MarketListingStatus.Active, after.Status,
                    "the market delisted the listing it was selling: its own withdraw tripped the auto-delist hook");
                Assert.AreEqual(7, after.Count, "DESIGN 5.3: a partial sale decrements and only closes at zero");
                Assert.AreEqual(1, repo.UpdateListingCalls, "the decrement is the only listing write a partial sale makes");
            }
            finally
            {
                AccountVaultStore.PreWithdrawHook = previous;
            }
        }

        [TestMethod]
        public void Buy_StoredItem_WithTheAutoDelistHookLive_ClosesTheListingAsSoldNotDelisted()
        {
            var previous = AccountVaultStore.PreWithdrawHook;

            try
            {
                AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;

                var listing = ListOneItem(price: 10);

                var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

                Assert.IsTrue(result.Ok, $"expected a completed purchase, got {result.Error}");
                Assert.AreEqual(MarketListingStatus.Sold, MarketManager.GetListing(listing.Id).Status,
                    "a sold listing must not be recorded as Delisted by its own withdraw");

                // The status alone does not discriminate here: Close does not refuse to re-close, so the
                // later Sold overwrites a spurious Delisted. The extra write and feed event are the tell.
                Assert.AreEqual(1, repo.UpdateListingCalls,
                    "the sale closed the listing twice: the auto-delist hook fired on the market's own withdraw first");
            }
            finally
            {
                AccountVaultStore.PreWithdrawHook = previous;
            }
        }

        // ---- group listings: the anchor and the two preflight reads that bound them ----

        /// <summary>
        /// THE ANCHOR, end to end. A group listing pins market_listing.item_Guid to the group's
        /// representative and nothing rewrites it as the listing sells down. A front-taking sale would
        /// hand that member to the first buyer and leave an Active listing naming a guid the seller no
        /// longer holds - the vault view would stop joining the listing to the row, and the next buy
        /// would be refused item_not_found even though four bags were still there.
        ///
        /// The second buy is what makes this discriminating: it proves the listing is still REACHABLE
        /// after the partial sale, not merely still marked Active.
        /// </summary>
        [TestMethod]
        public void Buy_PartOfAGroupListing_LeavesTheAnchorValidAndTheListingBuyable()
        {
            const uint wcid = 20980;

            var group = store.SeedGroup(SellerAccount, wcid, 4);
            var anchor = group[0];

            var created = MarketManager.List(Seller, anchor.Guid.Full, wcid, 4, 10, MarketChannel.Web);
            Assert.IsTrue(created.Ok, $"seeding a group listing failed with {created.Error}");

            var first = MarketManager.Buy(Buyer, created.Value.Id, 2, 10, MarketChannel.Web);
            Assert.IsTrue(first.Ok, $"expected a partial group sale, got {first.Error}");

            var after = MarketManager.GetListing(created.Value.Id);

            Assert.AreEqual(MarketListingStatus.Active, after.Status, "a partial sale only closes at zero");
            Assert.AreEqual(2, after.Count);
            Assert.AreEqual(anchor.Guid.Full, after.ItemGuid, "the listing still names the representative it was created against");

            Assert.AreEqual(2, group.Count, "N-k members stay with the seller");
            Assert.AreSame(anchor, group[0], "and the representative is one of them, still first");

            // The join the seller's own panel does. It matches on the ROW's guid, so it only succeeds
            // while the anchor is still the group's representative.
            Assert.IsTrue(MarketManager.TryGetVaultView(SellerAccount, out var view, out var viewError), $"vault view refused: {viewError}");
            Assert.AreEqual(1, view.Count);

            var row = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                System.Text.Json.JsonSerializer.Serialize(view[0], MarketApiHost.Json));

            Assert.IsTrue(row.GetProperty("is_group").GetBoolean());
            Assert.AreEqual(created.Value.Id, row.GetProperty("listing_id").GetUInt32(),
                "the remaining row must still resolve to its own listing");

            var second = MarketManager.Buy(Buyer, created.Value.Id, 2, 10, MarketChannel.Web);

            Assert.IsTrue(second.Ok, $"the listing must still be buyable after a partial sale, got {second.Error}");
            Assert.AreEqual(MarketListingStatus.Sold, MarketManager.GetListing(created.Value.Id).Status);
            Assert.AreEqual(0, group.Count, "every member has now been sold");
            Assert.AreEqual(4, store.Items[BuyerAccount].Count, "and all four reached the buyer, one per unit paid for");
            Assert.AreEqual(60, wallet.GetBalanceMmd(BuyerCharacter), "4 members at 10 MMD each, never one price for the line");
        }

        /// <summary>
        /// VaultMarketItemStore.Holds is the invalidation read: "do these rows still back this
        /// listing?". A group row is N whole biotas, so a listing of 3 stops being backed the moment
        /// the row falls to 2, even though the representative guid is still sitting there. Reading
        /// only the guid answers yes for a row that can no longer deliver.
        ///
        /// Driven through the entries rather than through a store, because Holds resolves its store
        /// via AccountVaultManager.GetStore, which hardcodes the production backend and offers no
        /// injection seam.
        /// </summary>
        [TestMethod]
        public void Holds_AGroupRowThatHasShrunkBelowTheListedCount_IsFalse()
        {
            const uint wcid = 20980;

            var members = new List<WorldObject>
            {
                FakeVaultWorld.MakeStack(wcid, 1, 1),
                FakeVaultWorld.MakeStack(wcid, 1, 1),
            };

            var anchor = members[0].Guid.Full;
            var entries = new List<VaultEntry> { VaultEntry.ForGroup(members) };

            Assert.IsTrue(VaultMarketItemStore.HoldsCount(entries, anchor, wcid, 2),
                "control: a row of 2 does back a listing of 2, or the false below proves nothing");

            Assert.IsFalse(VaultMarketItemStore.HoldsCount(entries, anchor, wcid, 3),
                "a row of 2 cannot back a listing of 3, however familiar its representative guid is");
        }

        /// <summary>
        /// The buyer's preflight has ONE forbidden direction: undercounting. Its own contract says it
        /// may refuse a purchase that would have been free but must never admit one past the cap, and
        /// a group purchase delivers one entry PER MEMBER - buying 3 deposits three separate objects.
        /// </summary>
        [TestMethod]
        public void CanReceive_AnNMemberPurchase_CostsNEntriesNotOne()
        {
            Assert.AreEqual(3, VaultMarketItemStore.NewEntriesForPurchase(0x50001234u, 3, buyerHoldsLedgerRow: false),
                "three whole biotas need three entries");

            Assert.AreEqual(1, VaultMarketItemStore.NewEntriesForPurchase(0x50001234u, 1, buyerHoldsLedgerRow: false),
                "control: a single stored biota still costs exactly one");

            // The ledger rule is untouched: a deposit onto a row the buyer already holds adds no entry.
            Assert.AreEqual(0, VaultMarketItemStore.NewEntriesForPurchase(null, 200, buyerHoldsLedgerRow: true),
                "a ledger top-up onto a held row is free whatever the unit count");

            Assert.AreEqual(1, VaultMarketItemStore.NewEntriesForPurchase(null, 200, buyerHoldsLedgerRow: false),
                "and a fresh ledger row costs exactly one, never one per unit");
        }
    }
}

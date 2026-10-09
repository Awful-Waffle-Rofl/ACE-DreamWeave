using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Managers.Market;

using MarketRejectedAttempt = ACE.Database.Models.Shard.MarketRejectedAttempt;

namespace ACE.Server.Tests
{
    /// <summary>
    /// One row per refusal branch, with the right reason code, operation and channel. Refusals are
    /// the whole point of the table: a branch that returns a failure without enqueueing is exactly
    /// the gap this feature exists to close, and nothing else in the suite would notice it.
    ///
    /// Every test asserts a COUNT as well as a code. The failure mode that matters most here is not a
    /// wrong code, it is a branch that records twice (once in its own arm and once in a wrapper) or
    /// not at all.
    /// </summary>
    [TestClass]
    public class MarketRejectionRecordingTests
    {
        private const uint SellerAccount = 9101;
        private const uint SellerCharacter = 0x50000301;
        private const uint BuyerAccount = 9102;
        private const uint BuyerCharacter = 0x50000302;

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

            wallet.Seed(BuyerCharacter, 1000);
            wallet.Seed(SellerCharacter, 0);

            // AFTER Initialize: seeding a listing below records nothing, but a previous class's rows
            // could still be queued, and this suite counts rows.
            MarketRejectionLog.ResetForTests();
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();
            MarketRejectionLog.ResetForTests();
        }

        private MarketListing ListOneItem(long price = 10, uint wcid = 9001)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, wcid, 1, price, MarketChannel.InGame);
            Assert.IsTrue(result.Ok, $"seeding a listing failed with {result.Error}");

            // The seeding call must itself have recorded nothing - a success is not an attempt.
            MarketRejectionLog.ResetForTests();

            return result.Value;
        }

        private MarketListing ListLedger(uint wcid = 9500, int held = 100, int listed = 50, long price = 2)
        {
            store.SeedLedger(SellerAccount, wcid, held);

            var result = MarketManager.List(Seller, null, wcid, listed, price, MarketChannel.InGame);
            Assert.IsTrue(result.Ok, $"seeding a ledger listing failed with {result.Error}");

            MarketRejectionLog.ResetForTests();

            return result.Value;
        }

        private static MarketRejectedAttempt SingleRow()
        {
            var rows = MarketRejectionLog.Snapshot();

            Assert.AreEqual(1, rows.Count, $"expected exactly one recorded attempt, got {rows.Count}");

            return rows[0];
        }

        private static void AssertRow(MarketRejectedAttempt row, MarketRejectOperation operation, string reasonCode,
                                      MarketChannel channel)
        {
            Assert.AreEqual((int)operation, row.Operation, "wrong operation");
            Assert.AreEqual(reasonCode, row.ReasonCode, "wrong reason code");
            Assert.AreEqual((int)channel, row.Channel, "wrong channel");
        }

        // ---- List ----

        [TestMethod]
        public void List_UnresolvedActor_RecordsNotOwner()
        {
            var result = MarketManager.List(new MarketActor(0, 0, string.Empty), 1u, 9001, 1, 5, MarketChannel.Web);

            Assert.AreEqual(MarketError.NotOwner, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.List, "not_owner", MarketChannel.Web);
        }

        [TestMethod]
        public void List_NegativePrice_RecordsInvalidPrice()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 1, -1, MarketChannel.InGame);

            Assert.AreEqual(MarketError.InvalidPrice, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.List, "invalid_price", MarketChannel.InGame);
            Assert.AreEqual(SellerAccount, row.AccountId);
            Assert.AreEqual(SellerCharacter, row.CharacterGuid);
            Assert.AreEqual("Marketseller", row.CharacterName);
            Assert.AreEqual(9001u, row.Wcid);
            Assert.AreEqual(-1L, row.PriceMmd);
        }

        [TestMethod]
        public void List_OverTheOperatorSoftCap_RecordsInvalidPriceWithTheCapInDetail()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_price_mmd", 100));

            try
            {
                var item = FakeVaultWorld.MakeStack(9001, 1, 1);
                store.SeedItem(SellerAccount, item);

                var result = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 101, MarketChannel.InGame);

                Assert.AreEqual(MarketError.InvalidPrice, result.Error);

                var row = SingleRow();

                AssertRow(row, MarketRejectOperation.List, "invalid_price", MarketChannel.InGame);
                StringAssert.Contains(row.Detail, "market_max_price_mmd");
            }
            finally
            {
                MarketManagerTests.SeedMarketTunables();
            }
        }

        [TestMethod]
        public void List_CountBelowOne_RecordsCountUnavailable()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 0, 5, MarketChannel.InGame);

            Assert.AreEqual(MarketError.CountUnavailable, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.List, "count_unavailable", MarketChannel.InGame);
        }

        [TestMethod]
        public void List_StoredItemWithCountAboveOne_RecordsCountUnavailable()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 2, 5, MarketChannel.InGame);

            Assert.AreEqual(MarketError.CountUnavailable, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.List, "count_unavailable", MarketChannel.InGame);
            StringAssert.Contains(row.Detail, "split");
        }

        [TestMethod]
        public void List_VaultNotReady_RecordsVaultUnavailable()
        {
            store.NotReadyAccounts.Add(SellerAccount);

            var result = MarketManager.List(Seller, 1u, 9001, 1, 5, MarketChannel.Web);

            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.List, "vault_unavailable", MarketChannel.Web);
        }

        [TestMethod]
        public void List_AtTheAccountListingCap_RecordsListingLimit()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 1));

            try
            {
                ListOneItem(wcid: 9001);

                var second = FakeVaultWorld.MakeStack(9002, 1, 1);
                store.SeedItem(SellerAccount, second);

                var result = MarketManager.List(Seller, second.Guid.Full, 9002, 1, 5, MarketChannel.InGame);

                // Changed from AlreadyListed: the cap refusal now carries its own code, distinct from
                // "this item is already up" - see MarketError.ListingLimit.
                Assert.AreEqual(MarketError.ListingLimit, result.Error);

                var row = SingleRow();

                AssertRow(row, MarketRejectOperation.List, "listing_limit", MarketChannel.InGame);
                StringAssert.Contains(row.Detail, "market_max_listings_per_account");
            }
            finally
            {
                MarketManagerTests.SeedMarketTunables();
            }
        }

        [TestMethod]
        public void List_ItemNotInTheVault_RecordsItemNotFound()
        {
            var result = MarketManager.List(Seller, 0x7FFFFFFF, 9001, 1, 5, MarketChannel.InGame);

            Assert.AreEqual(MarketError.ItemNotFound, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.List, "item_not_found", MarketChannel.InGame);
        }

        [TestMethod]
        public void List_MoreUnitsThanTheVaultHolds_RecordsCountUnavailable()
        {
            store.SeedLedger(SellerAccount, 9500, 10);

            var result = MarketManager.List(Seller, null, 9500, 11, 5, MarketChannel.Web);

            Assert.AreEqual(MarketError.CountUnavailable, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.List, "count_unavailable", MarketChannel.Web);
            StringAssert.Contains(row.Detail, "10");
        }

        [TestMethod]
        public void List_SameItemTwice_RecordsAlreadyListed()
        {
            var listing = ListOneItem(wcid: 9001);
            var itemGuid = listing.ItemGuid;

            Assert.IsNotNull(itemGuid);

            var result = MarketManager.List(Seller, itemGuid, 9001, 1, 9, MarketChannel.InGame);

            Assert.AreEqual(MarketError.AlreadyListed, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.List, "already_listed", MarketChannel.InGame);
        }

        /// <summary>
        /// The ONE synthetic code. The player is told already_listed here because nothing was written
        /// either way, but the audit row must not repeat that guess - an investigator reading
        /// already_listed would conclude the seller double-listed, which is the opposite of the truth.
        /// </summary>
        [TestMethod]
        public void List_WhenTheRepositoryRefuses_RecordsRepositoryRefusedNotAlreadyListed()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            repo.FailAddListing = true;

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 5, MarketChannel.Web);

            Assert.AreEqual(MarketError.AlreadyListed, result.Error, "the player-facing message is unchanged");

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.List, MarketRejectionLog.RepositoryRefusedCode, MarketChannel.Web);
            StringAssert.Contains(row.Detail, "already_listed");
        }

        [TestMethod]
        public void List_Success_RecordsNothing()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 5, MarketChannel.InGame);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count, "a completed listing is not a refused attempt");
        }

        /// <summary>
        /// Invariant 4: the kill switch is operator state, not a player event. One flipped switch would
        /// otherwise write a row per click for every player on the shard.
        /// </summary>
        [TestMethod]
        public void List_WhenTheMarketIsDisabled_RecordsNothing()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));

            try
            {
                var result = MarketManager.List(Seller, 1u, 9001, 1, 5, MarketChannel.InGame);

                Assert.AreEqual(MarketError.Disabled, result.Error);
                Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count);
            }
            finally
            {
                MarketManagerTests.SeedMarketTunables();
            }
        }

        // ---- ListByEntryNumber ----

        [TestMethod]
        public void ListByEntryNumber_OutOfRange_RecordsExactlyOneItemNotFound()
        {
            var result = MarketManager.ListByEntryNumber(Seller, 99, 5, 1, MarketChannel.InGame);

            Assert.AreEqual(MarketError.ItemNotFound, result.Error);

            // Exactly one: this arm must not record and then hand off to List, which would record again.
            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.List, "item_not_found", MarketChannel.InGame);
            StringAssert.Contains(row.Detail, "99");
        }

        // ---- Delist ----

        [TestMethod]
        public void Delist_UnknownListing_RecordsListingNotActive()
        {
            var result = MarketManager.Delist(Seller, 4242, MarketChannel.Web);

            Assert.AreEqual(MarketError.ListingNotActive, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Delist, "listing_not_active", MarketChannel.Web);
            Assert.AreEqual((uint?)4242u, row.ListingId);
        }

        [TestMethod]
        public void Delist_ByAnotherAccount_RecordsNotOwner()
        {
            var listing = ListOneItem();

            var result = MarketManager.Delist(Buyer, listing.Id, MarketChannel.InGame);

            Assert.AreEqual(MarketError.NotOwner, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Delist, "not_owner", MarketChannel.InGame);
            Assert.AreEqual(BuyerAccount, row.AccountId, "the row names the ACTOR, not the owner");
            Assert.AreEqual((uint?)listing.Id, row.ListingId);
        }

        [TestMethod]
        public void Delist_AlreadyClosed_RecordsListingNotActive()
        {
            var listing = ListOneItem();

            Assert.IsTrue(MarketManager.Delist(Seller, listing.Id, MarketChannel.InGame).Ok);
            MarketRejectionLog.ResetForTests();

            var result = MarketManager.Delist(Seller, listing.Id, MarketChannel.InGame);

            Assert.AreEqual(MarketError.ListingNotActive, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Delist, "listing_not_active", MarketChannel.InGame);
            StringAssert.Contains(row.Detail, "delisted");
        }

        [TestMethod]
        public void Delist_WhenTheCloseWriteFails_RecordsServerError()
        {
            var listing = ListOneItem();

            repo.FailUpdateListing = true;

            var result = MarketManager.Delist(Seller, listing.Id, MarketChannel.Web);

            Assert.AreEqual(MarketError.ServerError, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.Delist, "server_error", MarketChannel.Web);
        }

        [TestMethod]
        public void Delist_Success_RecordsNothing()
        {
            var listing = ListOneItem();

            Assert.IsTrue(MarketManager.Delist(Seller, listing.Id, MarketChannel.InGame).Ok);
            Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count);
        }

        /// <summary>
        /// Invariant 4 on the third entry point. List and Buy each have this test; Delist skips Record on
        /// the same disabled path and must keep doing so, for the same reason - one flipped kill switch
        /// would otherwise write a row per click for every player on the shard.
        /// </summary>
        [TestMethod]
        public void Delist_WhenTheMarketIsDisabled_RecordsNothing()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));

            try
            {
                var result = MarketManager.Delist(Seller, 1, MarketChannel.InGame);

                Assert.AreEqual(MarketError.Disabled, result.Error);
                Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count);
            }
            finally
            {
                MarketManagerTests.SeedMarketTunables();
            }
        }

        // ---- Buy step 0 ----

        [TestMethod]
        public void Buy_UnresolvedBuyer_RecordsBadCredentials()
        {
            var listing = ListOneItem();

            var result = MarketManager.Buy(new MarketActor(0, 0, string.Empty), listing.Id, 1, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.BadCredentials, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.Buy, "bad_credentials", MarketChannel.Web);
        }

        [TestMethod]
        public void Buy_CountBelowOne_RecordsCountUnavailable()
        {
            var listing = ListOneItem();

            var result = MarketManager.Buy(Buyer, listing.Id, 0, 10, MarketChannel.InGame);

            Assert.AreEqual(MarketError.CountUnavailable, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.Buy, "count_unavailable", MarketChannel.InGame);
        }

        [TestMethod]
        public void Buy_UnknownListing_RecordsListingNotActive()
        {
            var result = MarketManager.Buy(Buyer, 4242, 1, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.ListingNotActive, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, "listing_not_active", MarketChannel.Web);
            Assert.AreEqual((uint?)4242u, row.ListingId);
        }

        [TestMethod]
        public void Buy_OwnListing_RecordsNotOwner()
        {
            var listing = ListOneItem();

            var result = MarketManager.Buy(Seller, listing.Id, 1, 10, MarketChannel.InGame);

            Assert.AreEqual(MarketError.NotOwner, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, "not_owner", MarketChannel.InGame);
            StringAssert.Contains(row.Detail, "own listing");
        }

        [TestMethod]
        public void Buy_MoreThanTheListingHolds_RecordsCountUnavailable()
        {
            var listing = ListOneItem();

            var result = MarketManager.Buy(Buyer, listing.Id, 5, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.CountUnavailable, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, "count_unavailable", MarketChannel.Web);
            Assert.AreEqual(5, row.Count);
        }

        [TestMethod]
        public void Buy_StalePrice_RecordsPriceChangedWithTheQuotedAndLivePrices()
        {
            var listing = ListOneItem(price: 10);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 7, MarketChannel.Web);

            Assert.AreEqual(MarketError.PriceChanged, result.Error);
            Assert.AreEqual(10, result.CurrentPriceMmd);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, "price_changed", MarketChannel.Web);
            Assert.AreEqual(7L, row.PriceMmd, "the row carries the price the BUYER quoted");
            StringAssert.Contains(row.Detail, "10");
        }

        [TestMethod]
        public void Buy_WhenTheProductWouldOverflow_RecordsInvalidPrice()
        {
            // A LEDGER listing, because the overflow check sits below the count check and a stored
            // item can only ever be listed as one unit - buying two of it would refuse earlier.
            var listing = ListLedger(listed: 2, price: MarketManager.MaxPriceMmd);

            var result = MarketManager.Buy(Buyer, listing.Id, 2, MarketManager.MaxPriceMmd, MarketChannel.Web);

            Assert.AreEqual(MarketError.InvalidPrice, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.Buy, "invalid_price", MarketChannel.Web);
        }

        [TestMethod]
        public void Buy_WhenTheSellerVaultIsNotReady_RecordsVaultUnavailable()
        {
            var listing = ListOneItem();

            store.NotReadyAccounts.Add(SellerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.InGame);

            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, "vault_unavailable", MarketChannel.InGame);
            StringAssert.Contains(row.Detail, "seller");
        }

        [TestMethod]
        public void Buy_WhenTheBuyerVaultIsNotReady_RecordsVaultUnavailable()
        {
            var listing = ListOneItem();

            store.NotReadyAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, "vault_unavailable", MarketChannel.Web);
            StringAssert.Contains(row.Detail, "buyer");
        }

        [TestMethod]
        public void Buy_WhenTheBuyerVaultIsFullAtThePreflight_RecordsVaultFull()
        {
            var listing = ListOneItem();

            store.PreflightFullVaultAccounts.Add(BuyerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.VaultFull, result.Error);
            AssertRow(SingleRow(), MarketRejectOperation.Buy, "vault_full", MarketChannel.Web);

            // Invariant 4 and the 2026-09-02 incident: refusing at step 0 means no Pending row at all.
            Assert.AreEqual(0, repo.Transactions.Count);
        }

        /// <summary>
        /// The Buy half of the synthetic code, and the exception to invariant 4. The Pending row did not
        /// fail to matter, it failed to EXIST: market_transaction holds nothing, and the buyer was told
        /// server_error. Without this row the attempt has no trace anywhere, which is the exact gap the
        /// table exists to close. Contrast with
        /// Buy_FailingPastStepZero_RecordsNothingHereAndLeavesTheTransactionRow below, where the row DID
        /// land and a second account of the same event would only disagree with it.
        /// </summary>
        [TestMethod]
        public void Buy_WhenThePendingRowCannotBeWritten_RecordsRepositoryRefused()
        {
            var listing = ListOneItem(price: 10);

            repo.FailAddTransaction = true;

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.ServerError, result.Error, "the player-facing message is unchanged");

            var row = SingleRow();

            AssertRow(row, MarketRejectOperation.Buy, MarketRejectionLog.RepositoryRefusedCode, MarketChannel.Web);
            Assert.AreEqual((uint?)listing.Id, row.ListingId);
            Assert.AreEqual(listing.Wcid, row.Wcid);
            StringAssert.Contains(row.Detail, "Pending row");

            // The premise of the exception: nothing landed in market_transaction, so this row is the
            // only account of the attempt that exists.
            Assert.AreEqual(0, repo.Transactions.Count);
        }

        /// <summary>
        /// Invariant 4: anything that reaches the Pending row belongs to market_transaction, not here.
        /// An insufficient-funds refusal happens at step 2, AFTER the row is written.
        /// </summary>
        [TestMethod]
        public void Buy_FailingPastStepZero_RecordsNothingHereAndLeavesTheTransactionRow()
        {
            var listing = ListOneItem(price: 10);

            wallet.Seed(BuyerCharacter, 0);

            var result = MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web);

            Assert.AreEqual(MarketError.InsufficientFunds, result.Error);
            Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count,
                "past step 0 the purchase is recorded in market_transaction, and duplicating it here would give two disagreeing records");
            Assert.AreEqual(1, repo.Transactions.Count);
        }

        [TestMethod]
        public void Buy_Success_RecordsNothing()
        {
            var listing = ListOneItem(price: 10);

            Assert.IsTrue(MarketManager.Buy(Buyer, listing.Id, 1, 10, MarketChannel.Web).Ok);
            Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count);
        }

        [TestMethod]
        public void Buy_WhenTheMarketIsDisabled_RecordsNothing()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));

            try
            {
                var result = MarketManager.Buy(Buyer, 1, 1, 10, MarketChannel.Web);

                Assert.AreEqual(MarketError.Disabled, result.Error);
                Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count);
            }
            finally
            {
                MarketManagerTests.SeedMarketTunables();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;

using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The completed-sale chat line: its copy, and which side of the sale hears it on which channel.
    /// The delivery seam is MarketNotifier.Send, replaced here so none of this needs a live Session.
    /// </summary>
    [TestClass]
    public class MarketNotifierTests
    {
        private const uint SellerAccount = 8301;
        private const uint SellerCharacter = 0x50000401;
        private const uint BuyerAccount = 8302;
        private const uint BuyerCharacter = 0x50000402;

        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Marketbuyer");

        private Action<uint, string> realSend;
        private List<(uint guid, string text)> sent;

        [TestInitialize]
        public void Setup()
        {
            realSend = MarketNotifier.Send;
            sent = new List<(uint, string)>();
            MarketNotifier.Send = (guid, text) => sent.Add((guid, text));
        }

        [TestCleanup]
        public void Teardown() => MarketNotifier.Send = realSend;

        private static ShardMarketTransaction Row(int count, MarketChannel channel = MarketChannel.Web,
                                                  string itemName = "Obsidian Hammer", long total = 1500)
            => new ShardMarketTransaction
            {
                Id = 77,
                BuyerAccountId = BuyerAccount,
                BuyerCharacterGuid = BuyerCharacter,
                BuyerCharacterName = "Marketbuyer",
                SellerAccountId = SellerAccount,
                SellerCharacterGuid = SellerCharacter,
                SellerCharacterName = "Marketseller",
                Wcid = 1001912,
                ItemName = itemName,
                Count = count,
                PriceMmdTotal = total,
                Channel = (int)channel,
                Status = (int)MarketTransactionStatus.Completed,
            };

        // ---- copy ----

        [TestMethod]
        public void SellerLine_WithMoreThanOne_CarriesTheCountPrefixAndAGroupedTotal()
        {
            Assert.AreEqual("[Market] Sold 3x Obsidian Hammer to Marketbuyer for 1,500 MMD.",
                MarketNotifier.SellerLine(3, "Obsidian Hammer", "Marketbuyer", 1500));
        }

        [TestMethod]
        public void SellerLine_WithExactlyOne_DropsTheCountPrefix()
        {
            Assert.AreEqual("[Market] Sold Obsidian Hammer to Marketbuyer for 500 MMD.",
                MarketNotifier.SellerLine(1, "Obsidian Hammer", "Marketbuyer", 500),
                "a '1x' prefix reads as machine output in a chat window");
        }

        [TestMethod]
        public void BuyerLine_WithMoreThanOne_CarriesTheCountPrefixAndSaysWhereTheItemWent()
        {
            Assert.AreEqual("[Market] Bought 3x Obsidian Hammer from Marketseller for 1,500 MMD. It is in your vault.",
                MarketNotifier.BuyerLine(3, "Obsidian Hammer", "Marketseller", 1500));
        }

        [TestMethod]
        public void BuyerLine_WithExactlyOne_DropsTheCountPrefix()
        {
            Assert.AreEqual("[Market] Bought Obsidian Hammer from Marketseller for 500 MMD. It is in your vault.",
                MarketNotifier.BuyerLine(1, "Obsidian Hammer", "Marketseller", 500));
        }

        [TestMethod]
        public void TheLineGoesOutInAdvancementTeal()
        {
            Assert.AreEqual(ChatMessageType.Advancement, MarketNotifier.Channel);
            Assert.AreEqual(0x0D, (int)MarketNotifier.Channel,
                "0x0D is the client-verified teal; a different id is a different colour");
        }

        // ---- routing ----

        [TestMethod]
        public void NotifySale_OnTheWebChannel_TellsBothSides()
        {
            MarketNotifier.NotifySale(Row(2));

            Assert.AreEqual(2, sent.Count);

            Assert.AreEqual(SellerCharacter, sent[0].guid);
            Assert.IsTrue(sent[0].text.StartsWith("[Market] Sold 2x"), sent[0].text);

            Assert.AreEqual(BuyerCharacter, sent[1].guid);
            Assert.IsTrue(sent[1].text.StartsWith("[Market] Bought 2x"), sent[1].text);
        }

        [TestMethod]
        public void NotifySale_OnTheInGameChannel_TellsTheSellerOnly()
        {
            MarketNotifier.NotifySale(Row(2, MarketChannel.InGame));

            Assert.AreEqual(1, sent.Count,
                "/market buy already prints the buyer its own confirmation; a second line says it twice");
            Assert.AreEqual(SellerCharacter, sent[0].guid);
            Assert.IsTrue(sent[0].text.StartsWith("[Market] Sold"), sent[0].text);
        }

        [TestMethod]
        public void NotifySale_WhenTheSendThrows_DoesNotEscape_AndStillTriesTheOtherSide()
        {
            MarketNotifier.Send = (guid, text) =>
            {
                sent.Add((guid, text));

                if (guid == SellerCharacter)
                    throw new InvalidOperationException("a released session");
            };

            MarketNotifier.NotifySale(Row(1));

            Assert.AreEqual(2, sent.Count,
                "a throw delivering one line must not cost the other one, and must never reach Buy");
        }

        [TestMethod]
        public void NotifySale_WithNoRow_DoesNothing()
        {
            MarketNotifier.NotifySale(null);
            Assert.AreEqual(0, sent.Count);
        }

        // ---- the hook itself ----

        [TestMethod]
        public void ACompletedPurchase_NotifiesTheSeller()
        {
            var repo = new FakeMarketRepository();
            var store = new FakeMarketItemStore();
            var wallet = new FakeMarketWallet();

            MarketManagerTests.SeedMarketTunables();
            MarketManager.Initialize(store, wallet, repo);

            try
            {
                wallet.Seed(BuyerCharacter, 100);
                wallet.Seed(SellerCharacter, 0);

                var item = FakeVaultWorld.MakeStack(9001, 1, 1);
                store.SeedItem(SellerAccount, item);

                var listing = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 10, MarketChannel.InGame);
                Assert.IsTrue(listing.Ok, $"seeding a listing failed with {listing.Error}");

                sent.Clear();

                var result = MarketManager.Buy(Buyer, listing.Value.Id, 1, 10, MarketChannel.Web);

                Assert.IsTrue(result.Ok, $"expected a completed purchase, got {result.Error}");
                Assert.IsTrue(sent.Any(s => s.guid == SellerCharacter && s.text.StartsWith("[Market] Sold")),
                    "Buy must reach MarketNotifier after the row completes");
            }
            finally
            {
                MarketManager.Shutdown();
            }
        }

        [TestMethod]
        public void BootRecovery_CompletingASaleOverASoldListing_StillNotifiesTheSeller()
        {
            // The sale finished across a restart: only the final UPDATE was lost, so the row completes
            // in RecoverPendingTransactions rather than in Buy. That makes recovery the ONLY place this
            // particular sale can ever be announced.
            var repo = new FakeMarketRepository();
            var store = new FakeMarketItemStore();
            var wallet = new FakeMarketWallet();

            MarketManagerTests.SeedMarketTunables();
            MarketManager.Initialize(store, wallet, repo);

            try
            {
                wallet.Seed(BuyerCharacter, 100);
                wallet.Seed(SellerCharacter, 0);

                var item = FakeVaultWorld.MakeStack(9001, 1, 1);
                store.SeedItem(SellerAccount, item);

                var listing = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 10, MarketChannel.InGame);
                Assert.IsTrue(listing.Ok, $"seeding a listing failed with {listing.Error}");
                Assert.IsTrue(MarketManager.Buy(Buyer, listing.Value.Id, 1, 10, MarketChannel.Web).Ok);

                repo.Transactions.Add(new ShardMarketTransaction
                {
                    Id = 996,
                    ListingId = listing.Value.Id,
                    BuyerAccountId = BuyerAccount,
                    BuyerCharacterGuid = BuyerCharacter,
                    BuyerCharacterName = "Marketbuyer",
                    SellerAccountId = SellerAccount,
                    SellerCharacterGuid = SellerCharacter,
                    SellerCharacterName = "Marketseller",
                    Wcid = 9001,
                    ItemName = "Obsidian Hammer",
                    Count = 2,
                    PriceMmdTotal = 1500,
                    Timestamp = DateTime.UtcNow.AddMinutes(-5),
                    Channel = (int)MarketChannel.Web,
                    Status = (int)MarketTransactionStatus.Pending,
                });

                // Only what recovery itself sends: the Buy above announced its own sale.
                sent.Clear();

                MarketManager.RecoverPendingTransactions(3000);

                Assert.AreEqual((int)MarketTransactionStatus.Completed,
                    repo.Transactions.Single(t => t.Id == 996).Status,
                    "the row must complete rather than refund, or the premise of this test is gone");

                Assert.IsTrue(sent.Any(s => s.guid == SellerCharacter
                                            && s.text == "[Market] Sold 2x Obsidian Hammer to Marketbuyer for 1,500 MMD."),
                    "a sale that completes across a restart must still announce itself; "
                    + $"recovery sent: {string.Join(" | ", sent.Select(s => s.text))}");
            }
            finally
            {
                MarketManager.Shutdown();
            }
        }

        [TestMethod]
        public void BootRecovery_RefundingAnInterruptedPurchase_AnnouncesNoSale()
        {
            // The mirror of the test above, and the reason the notify sits INSIDE the Sold branch:
            // a refunded row is a sale that never happened, and must not be reported as one.
            var repo = new FakeMarketRepository();
            var store = new FakeMarketItemStore();
            var wallet = new FakeMarketWallet();

            MarketManagerTests.SeedMarketTunables();
            MarketManager.Initialize(store, wallet, repo);

            try
            {
                wallet.Seed(BuyerCharacter, 100);
                wallet.Seed(SellerCharacter, 0);

                var item = FakeVaultWorld.MakeStack(9001, 1, 1);
                store.SeedItem(SellerAccount, item);

                var listing = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 10, MarketChannel.InGame);
                Assert.IsTrue(listing.Ok, $"seeding a listing failed with {listing.Error}");

                repo.Transactions.Add(new ShardMarketTransaction
                {
                    Id = 995,
                    ListingId = listing.Value.Id,
                    BuyerAccountId = BuyerAccount,
                    BuyerCharacterGuid = BuyerCharacter,
                    BuyerCharacterName = "Marketbuyer",
                    SellerAccountId = SellerAccount,
                    SellerCharacterGuid = SellerCharacter,
                    SellerCharacterName = "Marketseller",
                    Wcid = 9001,
                    ItemName = "Obsidian Hammer",
                    Count = 1,
                    PriceMmdTotal = 10,
                    Timestamp = DateTime.UtcNow.AddMinutes(-5),
                    Channel = (int)MarketChannel.Web,
                    Status = (int)MarketTransactionStatus.Pending,
                });

                sent.Clear();

                MarketManager.RecoverPendingTransactions(3000);

                Assert.AreEqual((int)MarketTransactionStatus.Refunded,
                    repo.Transactions.Single(t => t.Id == 995).Status);

                Assert.AreEqual(0, sent.Count,
                    $"a refunded row is not a sale; recovery sent: {string.Join(" | ", sent.Select(s => s.text))}");
            }
            finally
            {
                MarketManager.Shutdown();
            }
        }
    }
}

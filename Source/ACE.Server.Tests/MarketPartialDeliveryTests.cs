using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;
using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;
using MarketTransaction = ACE.Server.Managers.Market.MarketTransaction;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A purchase whose delivery fails PART WAY, driven end to end through the REAL custody code:
    /// MarketManager.Buy over VaultMarketItemStore over two real AccountVaultStores (seller and buyer),
    /// each on its own FakeVaultBackend and FakeVaultWorld. Nothing here needs a database.
    ///
    /// THE DEFECT THESE PIN. TryGiveToBuyer deposited items one at a time and stopped at the first
    /// failure, and Buy then returned EVERY taken item to the seller and refunded the buyer in full.
    /// Items deposited before the failure were already the buyer's, so each one ended up duplicated
    /// (a ledger or class carrier destroyed at the buyer and re-credited to the seller) or given away
    /// (a stored biota refused by the seller's return because it is parented to the buyer's vault,
    /// while the buyer kept it AND got the money back). Owner ruling 2026-09-27: a delivery that fails
    /// part way is a PARTIAL SALE - the buyer pays for what arrived, the seller is paid for that, and
    /// only the undelivered items go back.
    ///
    /// Every test forces the failure on item 2 of 3 and asserts both vaults, both balances, the
    /// transaction row and the listing, plus the conservation that makes a duplicate visible: the two
    /// vaults together hold exactly what the seller started with.
    ///
    /// Drives the real store through VaultMarketItemStore's internal resolver seam, which exists for
    /// exactly this: AccountVaultManager.GetStore hardcodes the production backend.
    /// </summary>
    [TestClass]
    public class MarketPartialDeliveryTests
    {
        private const uint SellerAccount = 8601;
        private const uint SellerCharacter = 0x50000861;
        private const uint BuyerAccount = 8602;
        private const uint BuyerCharacter = 0x50000862;

        private const uint LedgerWcid = 9650;
        private const uint BagWcid = 21013;

        private const long Price = 10;
        private const long BuyerStart = 1000;

        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Partialbuyer");

        private FakeVaultBackend sellerBackend;
        private FakeVaultBackend buyerBackend;
        private FakeVaultWorld sellerWorld;
        private FakeVaultWorld buyerWorld;

        private AccountVaultStore sellerStore;
        private AccountVaultStore buyerStore;

        private FakeMarketRepository repo;
        private FakeMarketWallet wallet;

        private Container sellerVault;
        private Container buyerVault;

        private uint nextVaultRow = 1;

        private Action<uint, uint?, uint, int, string> savedPreWithdrawHook;

        [TestInitialize]
        public void Setup()
        {
            MarketManagerTests.SeedMarketTunables();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            // Another class may have left the market's hook installed; a sale here must not delist itself.
            savedPreWithdrawHook = AccountVaultStore.PreWithdrawHook;
            AccountVaultStore.PreWithdrawHook = null;

            sellerBackend = new FakeVaultBackend();
            buyerBackend = new FakeVaultBackend();
            sellerWorld = new FakeVaultWorld();
            buyerWorld = new FakeVaultWorld();

            // One unit per object, so a ledger sale of 3 is a BATCH of 3 - the MaxStackSize-1 shape of
            // real salvage bags and Hammers, and the only ledger shape a partial delivery can hit.
            sellerWorld.ItemMaxStackSize = 1;

            repo = new FakeMarketRepository();
            wallet = new FakeMarketWallet();

            wallet.Seed(BuyerCharacter, BuyerStart);
            wallet.Seed(SellerCharacter, 0);
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();

            AccountVaultStore.PreWithdrawHook = savedPreWithdrawHook;

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        // ---- fixture ----

        private Container SeedContainer(FakeVaultBackend backend, FakeVaultWorld world, uint accountId, int capacity)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9700 + nextVaultRow,
                AccountId = accountId,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextVaultRow),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            nextVaultRow++;

            return container;
        }

        private static WorldObject Bag() => FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 70, 10, 250, "Salvage (100)");

        /// <summary>Builds both stores and warms them, so the fixture's own reads never count as the test's.</summary>
        private void BuildStores()
        {
            sellerStore = new AccountVaultStore(SellerAccount, sellerBackend, sellerWorld);
            buyerStore = new AccountVaultStore(BuyerAccount, buyerBackend, buyerWorld);

            Assert.IsTrue(sellerStore.IsLoaded, "the seller's store must load");
            Assert.IsTrue(buyerStore.IsLoaded, "the buyer's store must load");
        }

        private uint SeedListing(uint? itemGuid, uint wcid, int count)
        {
            var row = new ShardMarketListing
            {
                SellerAccountId = SellerAccount,
                SellerCharacterGuid = SellerCharacter,
                SellerCharacterName = "Partialseller",
                ItemGuid = itemGuid,
                ActiveItemGuid = itemGuid,
                ActiveLedgerWcid = itemGuid == null ? (uint?)wcid : null,
                Wcid = wcid,
                Count = count,
                PriceMmd = Price,
                Status = (int)MarketListingStatus.Active,
            };

            Assert.IsTrue(repo.AddListing(row), "could not seed the listing row");
            return row.Id;
        }

        private void StartMarket()
        {
            var itemStore = new VaultMarketItemStore(account =>
                account == SellerAccount ? sellerStore : account == BuyerAccount ? buyerStore : null);

            MarketManager.Initialize(itemStore, wallet, repo);
        }

        /// <summary>A seller with a LEDGER row of <paramref name="held"/> units and a listing of 3 over it.</summary>
        private uint ArrangeLedgerSale(long held) => ArrangeLedgerSale(held, 3);

        /// <summary>
        /// <see cref="ArrangeLedgerSale(long)"/> with the listed count spelled out, for the read-count test,
        /// which needs a delivery long enough that "once per delivery" and "once per item" cannot be
        /// confused for each other.
        /// </summary>
        private uint ArrangeLedgerSale(long held, int listed)
        {
            SeedContainer(sellerBackend, sellerWorld, SellerAccount, AccountVaultStore.VaultItemCapacity);
            buyerVault = SeedContainer(buyerBackend, buyerWorld, BuyerAccount, AccountVaultStore.VaultItemCapacity);

            sellerBackend.Stacks.Add(new AccountVaultStack { Id = 7001, AccountId = SellerAccount, Wcid = LedgerWcid, Count = held });

            // Fresh ledger carriers are pristine at the buyer: they go to the buyer's ledger and their
            // carrier object is destroyed, which is the arm that duplicated on the old unwind.
            buyerWorld.PristineResult = true;

            BuildStores();

            var listingId = SeedListing(null, LedgerWcid, listed);
            StartMarket();
            return listingId;
        }

        /// <summary>A seller holding a GROUP of 3 equivalent stored bags, listed as 3.</summary>
        private uint ArrangeGroupSale(int buyerCapacity) => ArrangeGroupSale(buyerCapacity, 3);

        /// <summary>
        /// <see cref="ArrangeGroupSale(int)"/> with the group size spelled out, for the read-count test.
        /// </summary>
        private uint ArrangeGroupSale(int buyerCapacity, int members)
        {
            sellerVault = SeedContainer(sellerBackend, sellerWorld, SellerAccount, AccountVaultStore.VaultItemCapacity);
            buyerVault = SeedContainer(buyerBackend, buyerWorld, BuyerAccount, buyerCapacity);

            for (var i = 0; i < members; i++)
                Assert.IsTrue(sellerVault.TryAddToInventory(Bag(), placementPosition: i), "could not seed a stored bag");

            BuildStores();

            var entry = sellerStore.GetEntries(0, -1).Single();

            Assert.IsTrue(entry.IsGroup, "control: equivalent bags must present as ONE group row, or this is not a group sale");
            Assert.AreEqual(members, entry.Count);

            var listingId = SeedListing(entry.Guid.Full, BagWcid, members);
            StartMarket();
            return listingId;
        }

        private long LedgerCount(FakeVaultBackend backend, uint accountId)
            => backend.Stacks.Where(s => s.AccountId == accountId && s.Wcid == LedgerWcid).Sum(s => s.Count);

        private static HashSet<uint> GuidsIn(Container container) => new HashSet<uint>(container.Inventory.Keys.Select(g => g.Full));

        private ACE.Database.Models.Shard.MarketTransaction Row() => repo.Transactions.Single();

        private void AssertPartialSale(uint listingId, int delivered, int listedAfter)
        {
            var row = Row();

            Assert.AreEqual((int)MarketTransactionStatus.Completed, row.Status, "a sale that delivered anything is Completed, never Refunded");
            Assert.AreEqual(delivered, row.Count, "the row records what was DELIVERED, not what was asked for");
            Assert.AreEqual(delivered * Price, row.PriceMmdTotal, "and is priced for what was delivered");

            Assert.AreEqual(BuyerStart - delivered * Price, wallet.GetBalanceMmd(BuyerCharacter), "the buyer pays for exactly what arrived");
            Assert.AreEqual(delivered * Price, wallet.GetBalanceMmd(SellerCharacter), "the seller is paid for exactly what arrived");

            var listing = MarketManager.GetListing(listingId);
            Assert.AreEqual(MarketListingStatus.Active, listing.Status);
            Assert.AreEqual(listedAfter, listing.Count, "the listing is decremented by what was delivered and no more");
        }

        // ---- what a whole delivery COSTS ----

        /// <summary>
        /// THE COST OF ONE DELIVERY, counted in BACKEND READS rather than in wall time, so it is a number a
        /// test can hold against a regression.
        ///
        /// TryGiveToBuyer deposits one item at a time and must keep doing so: its per-item commit
        /// classification is what decides whether each item stays with the buyer or goes back to the seller.
        /// Once TryDeposit began delegating to the batched path, each of those one-item batches ran phase D's
        /// WHOLE-ACCOUNT re-read, so a 12-item delivery issued 12 whole-account SELECTs on the world thread -
        /// and on a class delivery each one drags every class row's canonical_Form TEXT.
        ///
        /// The coalescing deposit window makes it one. This asserts 1 and reads 12 without it.
        /// </summary>
        [TestMethod]
        public void Buy_LedgerDelivery_ReReadsTheBuyersAccountOnce_NotOncePerItem()
        {
            const int items = 12;

            var listingId = ArrangeLedgerSale(held: 40, listed: items);

            // The fixture's own load reads are not the delivery's.
            buyerBackend.StackReads = 0;
            buyerBackend.ClassReads = 0;

            var result = MarketManager.Buy(Buyer, listingId, items, Price, MarketChannel.Web);

            var stackReads = buyerBackend.StackReads;
            var classReads = buyerBackend.ClassReads;

            // Control FIRST: a read count is worth nothing over a delivery that did not deliver.
            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(items, result.Value.Count, "control: the whole listing was delivered");
            Assert.AreEqual(items, LedgerCount(buyerBackend, BuyerAccount), "control: every unit reached the buyer's ledger");

            Assert.AreEqual(1, stackReads,
                $"a {items}-item delivery must re-read the buyer's whole stack ledger ONCE, at the close of the delivery's deposit window - not once per item, which is {items}.");

            Assert.AreEqual(0, classReads, "a ledger delivery touches no class row and must not re-read that table at all");
        }

        /// <summary>
        /// The class table's counterpart, which is the branch the cost actually matters on: every one of
        /// these re-reads carries every class row's canonical_Form TEXT column.
        /// </summary>
        [TestMethod]
        public void Buy_ClassDelivery_ReReadsTheBuyersClassLedgerOnce_NotOncePerItem()
        {
            const int items = 12;

            // The seller's bags are ordinary stored biotas; the BUYER's deposit classifies them, which is
            // what sends this delivery down the class branch.
            buyerWorld.ClassifyResult = true;

            var listingId = ArrangeGroupSale(buyerCapacity: AccountVaultStore.VaultItemCapacity, members: items);

            buyerBackend.StackReads = 0;
            buyerBackend.ClassReads = 0;

            var result = MarketManager.Buy(Buyer, listingId, items, Price, MarketChannel.Web);

            var classReads = buyerBackend.ClassReads;
            var stackReads = buyerBackend.StackReads;

            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(items, result.Value.Count, "control: the whole listing was delivered");

            var classRow = buyerBackend.Classes.Single(c => c.AccountId == BuyerAccount);
            Assert.AreEqual((long)items, classRow.Count, "control: every bag reached the buyer's class row");

            Assert.AreEqual(1, classReads,
                $"a {items}-item class delivery must re-read the buyer's whole class ledger ONCE, at the close of the delivery's deposit window - not once per item, which is {items}.");

            Assert.AreEqual(0, stackReads, "a class delivery touches no stack row and must not re-read that table at all");
        }

        // ---- the four kinds ----

        [TestMethod]
        public void Buy_LedgerBatch_DepositRefusedOnItem2_SellsOne_ReturnsTwo_AndDuplicatesNothing()
        {
            var listingId = ArrangeLedgerSale(held: 5);

            var calls = 0;
            buyerBackend.LedgerAdjustOverride = (account, wcid, delta) => ++calls == 2 ? AccountVaultStackAdjustResult.Refused : (AccountVaultStackAdjustResult?)null;

            var result = MarketManager.Buy(Buyer, listingId, 3, Price, MarketChannel.Web);

            Assert.AreEqual(1, LedgerCount(buyerBackend, BuyerAccount), "one unit reached the buyer's ledger");
            Assert.AreEqual(4, LedgerCount(sellerBackend, SellerAccount), "only the two undelivered units went back to the seller");
            Assert.AreEqual(5, LedgerCount(buyerBackend, BuyerAccount) + LedgerCount(sellerBackend, SellerAccount),
                "CONSERVATION: the two vaults together hold what the seller started with; more is a duplicate");
            Assert.AreEqual(0, buyerVault.Inventory.Count, "nothing ledger-sourced may land as a stored biota");

            // Custody and conservation are asserted FIRST, so a regression reports the duplicate itself rather than a bare refusal.
            Assert.IsTrue(result.Ok, $"a delivery that landed one unit is a partial SALE, got {result.Error}");
            Assert.AreEqual(1, result.Value.Count);

            AssertPartialSale(listingId, delivered: 1, listedAfter: 2);
        }

        [TestMethod]
        public void Buy_Group_DepositRefusedOnItem2_SellsOne_ReturnsTwo_AndNoBiotaIsInBothVaults()
        {
            // The buyer's only vault has one free slot and a second vault cannot be created, so the
            // second member's deposit is refused after the first has landed.
            var listingId = ArrangeGroupSale(buyerCapacity: 1);
            buyerBackend.FailAddVault = true;

            var before = GuidsIn(sellerVault);

            var result = MarketManager.Buy(Buyer, listingId, 3, Price, MarketChannel.Web);

            var buyerGuids = GuidsIn(buyerVault);
            var sellerGuids = GuidsIn(sellerVault);

            Assert.AreEqual(1, buyerGuids.Count, "the delivered member stays in the buyer's vault");
            Assert.AreEqual(2, sellerGuids.Count, "the two undelivered members are back with the seller");
            Assert.IsFalse(buyerGuids.Overlaps(sellerGuids), "no biota may be in both vaults");
            Assert.IsTrue(before.SetEquals(buyerGuids.Concat(sellerGuids)), "CONSERVATION: every member is in exactly one vault");

            foreach (var item in buyerVault.Inventory.Values)
                Assert.AreEqual(buyerVault.Guid.Full, item.ContainerId, "the delivered member is parented to the buyer's vault");

            // Custody and conservation are asserted FIRST, so a regression reports the duplicate itself rather than a bare refusal.
            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(1, result.Value.Count);

            AssertPartialSale(listingId, delivered: 1, listedAfter: 2);
        }

        [TestMethod]
        public void Buy_Group_DepositIntoBuyerClassRow_RefusedOnItem2_SellsOne_AndTheDestroyedCarrierIsNotReinserted()
        {
            // The seller's bags are ordinary stored biotas (the seller's world classifies nothing), but
            // the BUYER's deposit classifies them: the class arm credits the buyer's class row and
            // destroys the carrier. Handing that carrier back to the seller re-inserts a biota the buyer
            // is also holding as a count.
            buyerWorld.ClassifyResult = true;

            var listingId = ArrangeGroupSale(buyerCapacity: AccountVaultStore.VaultItemCapacity);

            var calls = 0;
            buyerBackend.ClassAdjustOverride = (key, delta) => ++calls == 2 ? AccountVaultStackAdjustResult.Refused : (AccountVaultStackAdjustResult?)null;

            var result = MarketManager.Buy(Buyer, listingId, 3, Price, MarketChannel.Web);

            var classRow = buyerBackend.Classes.Single(c => c.AccountId == BuyerAccount);

            Assert.AreEqual(1, classRow.Count, "one bag reached the buyer's class row");
            Assert.AreEqual(250, classRow.TotalValue, "carrying exactly that one bag's value");
            Assert.AreEqual(2, sellerVault.Inventory.Count, "only the two undelivered bags are back with the seller");
            Assert.AreEqual(3, classRow.Count + sellerVault.Inventory.Count, "CONSERVATION: three bags in all, never four");

            // Custody and conservation are asserted FIRST, so a regression reports the duplicate itself rather than a bare refusal.
            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(1, result.Value.Count);

            AssertPartialSale(listingId, delivered: 1, listedAfter: 2);
        }

        [TestMethod]
        public void Buy_LedgerBatch_ThrowAfterTheCreditOnItem2_ChargesForTwo_AndReturnsOnlyTheThird()
        {
            var listingId = ArrangeLedgerSale(held: 5);

            // The second carrier's teardown throws AFTER its credit has landed.
            var destroys = 0;
            buyerWorld.BeforeDestroyItem = item =>
            {
                if (++destroys == 2)
                    throw new InvalidOperationException("simulated teardown failure after the buyer's credit committed");
            };

            var result = MarketManager.Buy(Buyer, listingId, 3, Price, MarketChannel.Web);

            Assert.AreEqual(2, LedgerCount(buyerBackend, BuyerAccount));
            Assert.AreEqual(3, LedgerCount(sellerBackend, SellerAccount), "only the never-attempted third unit went back");
            Assert.AreEqual(5, LedgerCount(buyerBackend, BuyerAccount) + LedgerCount(sellerBackend, SellerAccount), "CONSERVATION");

            // Custody and conservation are asserted FIRST, so a regression reports the duplicate itself rather than a bare refusal.
            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(2, result.Value.Count, "the item that committed and then threw is the buyer's");

            AssertPartialSale(listingId, delivered: 2, listedAfter: 1);
        }

        [TestMethod]
        public void Buy_Group_ThrowAfterTheBiotaLandsOnItem2_ChargesForTwo_AndReturnsOnlyTheThird()
        {
            var listingId = ArrangeGroupSale(buyerCapacity: AccountVaultStore.VaultItemCapacity);

            // The second member's own save throws, after TryAddToInventory already put it in the
            // buyer's vault container - the shutdown shape (BlockingCollection.Add after CompleteAdding).
            var itemSaves = 0;
            buyerWorld.DuringSaveBiota = wo =>
            {
                if (!(wo is Container) && ++itemSaves == 2)
                    throw new InvalidOperationException("simulated shard shutdown while saving a deposited item");
            };

            var before = GuidsIn(sellerVault);

            var result = MarketManager.Buy(Buyer, listingId, 3, Price, MarketChannel.Web);

            var buyerGuids = GuidsIn(buyerVault);
            var sellerGuids = GuidsIn(sellerVault);

            Assert.AreEqual(2, buyerGuids.Count);
            Assert.AreEqual(1, sellerGuids.Count);
            Assert.IsFalse(buyerGuids.Overlaps(sellerGuids), "no biota may be in both vaults");
            Assert.IsTrue(before.SetEquals(buyerGuids.Concat(sellerGuids)), "CONSERVATION");

            // Custody and conservation are asserted FIRST, so a regression reports the duplicate itself rather than a bare refusal.
            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(2, result.Value.Count);

            AssertPartialSale(listingId, delivered: 2, listedAfter: 1);
        }

        [TestMethod]
        public void Buy_LedgerBatch_UnknownCreditOnItem2_IsChargedAndNeverReturned_AndLogsAmbiguousDelivery()
        {
            var listingId = ArrangeLedgerSale(held: 5);

            // Failed: the credit neither committed nor provably failed. The fake does NOT apply it,
            // which is the half of "unknown" a fake can show; either way it must not go back.
            var calls = 0;
            buyerBackend.LedgerAdjustOverride = (account, wcid, delta) => ++calls == 2 ? AccountVaultStackAdjustResult.Failed : (AccountVaultStackAdjustResult?)null;

            List<LoggingEvent> events;
            MarketResult<MarketTransaction> result;

            using (var capture = new MarketLogCapture())
            {
                result = MarketManager.Buy(Buyer, listingId, 3, Price, MarketChannel.Web);
                events = capture.Events;
            }

            Assert.AreEqual(1, LedgerCount(buyerBackend, BuyerAccount));
            Assert.AreEqual(3, LedgerCount(sellerBackend, SellerAccount),
                "the ambiguous unit must NOT be re-credited to the seller: if the buyer's credit did land, that is a duplicate");

            Assert.IsTrue(events.Any(e => e.Level >= Level.Error && e.RenderedMessage.Contains($"AMBIGUOUS DELIVERY on transaction {Row().Id}")
                                          && e.RenderedMessage.Contains($"buyer account {BuyerAccount}")
                                          && e.RenderedMessage.Contains($"seller account {SellerAccount}")),
                "an operator must be able to find the one case where the buyer may be owed a refund");

            // Custody and conservation are asserted FIRST, so a regression reports the duplicate itself rather than a bare refusal.
            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(2, result.Value.Count, "an item whose deposit outcome is unknown is charged for");

            AssertPartialSale(listingId, delivered: 2, listedAfter: 1);
        }

        // ---- the classification rule, directly ----

        [TestMethod]
        public void ClassifyDeposit_ResolvesEveryDoubtTowardNotReturning()
        {
            var fresh = FakeVaultWorld.MakeStack(9001, 1, 1);

            Assert.AreEqual(VaultMarketItemStore.DepositOutcome.NotDelivered,
                VaultMarketItemStore.ClassifyDeposit(false, VaultDepositCommit.None, fresh),
                "control: a refusal before any commit leaves the item the caller's");

            Assert.AreEqual(VaultMarketItemStore.DepositOutcome.Delivered,
                VaultMarketItemStore.ClassifyDeposit(true, VaultDepositCommit.None, fresh));

            Assert.AreEqual(VaultMarketItemStore.DepositOutcome.Delivered,
                VaultMarketItemStore.ClassifyDeposit(false, VaultDepositCommit.Committed, fresh),
                "committed and then threw is delivered");

            Assert.AreEqual(VaultMarketItemStore.DepositOutcome.Ambiguous,
                VaultMarketItemStore.ClassifyDeposit(false, VaultDepositCommit.Unknown, fresh));

            var parented = FakeVaultWorld.MakeStack(9001, 1, 1);
            Assert.IsTrue(FakeVaultWorld.MakeContainer(10).TryAddToInventory(parented));

            Assert.AreEqual(VaultMarketItemStore.DepositOutcome.Delivered,
                VaultMarketItemStore.ClassifyDeposit(false, VaultDepositCommit.None, parented),
                "a ContainerId is observable proof the item landed, whatever the marker says");
        }
    }

    /// <summary>
    /// Captures every log4net event raised while it is open. The control in each using test is the
    /// positive assertion itself: an appender that heard nothing would fail it.
    /// </summary>
    internal sealed class MarketLogCapture : IDisposable
    {
        private readonly MemoryAppender appender = new MemoryAppender();
        private readonly Hierarchy hierarchy;
        private readonly Level priorLevel;
        private readonly bool priorConfigured;

        public MarketLogCapture()
        {
            hierarchy = (Hierarchy)LogManager.GetRepository(typeof(MarketManager).Assembly);
            priorLevel = hierarchy.Root.Level;
            priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.All;
            hierarchy.Configured = true;
        }

        public List<LoggingEvent> Events => appender.GetEvents().ToList();

        public void Dispose()
        {
            hierarchy.Root.RemoveAppender(appender);
            hierarchy.Root.Level = priorLevel;
            hierarchy.Configured = priorConfigured;
        }
    }
}

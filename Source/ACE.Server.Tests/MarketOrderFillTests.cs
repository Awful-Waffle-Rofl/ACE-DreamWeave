using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The order FILL (WANTED-DESIGN 6.2 and 6.6): custody, the escrow payout, every unwind, the
    /// in-flight reservation, and the one change a fill makes to existing purchase recovery.
    ///
    /// THE ORDERINGS ARE THE POINT. Three of them are asserted here and nowhere else:
    /// the seller's and the buyer's vault regions are never nested (MaxNestedAccounts == 1);
    /// a refusal knowable at step 0 writes no Pending row at all (AddTransactionCalls == 0);
    /// and the transaction row completes the INSTANT custody changes, so no failure after that point
    /// unwinds anything.
    ///
    /// SCOPE: these drive FakeMarketItemStore, not VaultMarketItemStore - this assembly has no live
    /// vault. The fake's take accounting is pinned against the real algorithm by
    /// MarketFakeMatchingTakeTests, so a green run here is coverage of the MANAGER, not of the store.
    ///
    /// The Player static-initializer trap and its fix are as in MarketBuyOrderTests; see the comment there.
    /// </summary>
    [TestClass]
    public class MarketOrderFillTests
    {
        // A range of its own, clear of MuleSummonTests (90399+), PortalDestinationGuardTests (90800+),
        // MarketSalvageMaterialsTests / MarketTakeMatchingTests (90901-90911) and MarketBuyOrderTests (91001-91011).
        private static uint nextWcid = 91100;

        private static bool playerStaticFieldsSeeded;

        private static void EnsurePlayerStaticFieldsSeeded()
        {
            if (playerStaticFieldsSeeded)
                return;

            var cacheField = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)cacheField.GetValue(DatabaseManager.World);
            var nameCache = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);

            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                var wcid = ++nextWcid;
                cache[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
                nameCache[name.ToLower()] = wcid;
            }

            playerStaticFieldsSeeded = true;
        }

        private const uint BuyerAccount = 8201;
        private const uint BuyerCharacter = 0x50000501;
        private const uint SellerAccount = 8202;
        private const uint SellerCharacter = 0x50000502;

        /// <summary>A second seller, for the reservation race: two accounts over-committing one order.</summary>
        private const uint RivalAccount = 8302;
        private const uint RivalCharacter = 0x50000602;

        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Marketbuyer");
        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
        private static readonly MarketActor Rival = new MarketActor(RivalAccount, RivalCharacter, "Rivalseller");

        private static readonly int Granite = (int)MaterialType.Granite;

        /// <summary>Any wcid: the predicate keys on MaterialType and Structure, never on wcid.</summary>
        private const uint GraniteBagWcid = 21013;

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;

        private Func<uint, Weenie> savedWeenieLookup;

        private static WorldObject Bag(int structure, int workmanship)
        {
            var bag = FakeVaultWorld.MakeSalvageBag(GraniteBagWcid, structure, workmanship * 10, 10, 100);
            bag.MaterialType = MaterialType.Granite;
            return bag;
        }

        private static WorldObject Tool()
        {
            var tool = Bag(100, 5);
            tool.SetProperty(PropertyInt.SalvageToolCharges, 10);
            return tool;
        }

        private static int FullGraniteHeldBy(FakeMarketItemStore store, uint accountId)
            => store.CountMatching(accountId, wo => MarketSalvageMaterials.IsFullBagOf(wo, Granite));

        [TestInitialize]
        public void Setup()
        {
            EnsurePlayerStaticFieldsSeeded();

            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();

            MarketManagerTests.SeedMarketTunables();

            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;

            MarketManager.Initialize(store, wallet, repo);

            wallet.Seed(BuyerCharacter, 1000);
            wallet.Seed(SellerCharacter, 0);

            // Five full granite bags across two workmanship groups.
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { Bag(100, 4), Bag(100, 4), Bag(100, 4) });
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { Bag(100, 7), Bag(100, 7) });
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();
            MarketManager.WeenieLookup = savedWeenieLookup;
        }

        private MarketBuyOrder Place(int count = 5, long price = 10)
        {
            var result = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, count, price, MarketChannel.Web);
            Assert.IsTrue(result.Ok, $"placing failed with {result.Error}");
            return result.Value;
        }

        // ---- the happy path and the closing fill ----

        [TestMethod]
        public void Fill_HappyPath_MovesBags_PaysSeller_DecrementsEscrowAndRemaining()
        {
            var order = Place(count: 5, price: 10);               // buyer 1000 -> 950
            var result = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");
            Assert.AreEqual(MarketTransactionStatus.Completed, result.Value.Status);
            Assert.AreEqual(30, result.Value.PriceMmdTotal);
            Assert.AreEqual(order.Id, result.Value.BuyOrderId);
            Assert.AreEqual(0u, result.Value.ListingId);
            Assert.AreEqual(BuyerCharacter, result.Value.BuyerCharacterGuid);
            Assert.AreEqual(SellerCharacter, result.Value.SellerCharacterGuid);

            Assert.AreEqual(30, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter), "the buyer paid at placement, not at fill");
            Assert.AreEqual(3, store.CountMatching(BuyerAccount, wo => MarketSalvageMaterials.IsFullBagOf(wo, Granite)));
            Assert.AreEqual(2, store.CountMatching(SellerAccount, wo => MarketSalvageMaterials.IsFullBagOf(wo, Granite)));

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status);
            Assert.AreEqual(2, live.CountRemaining);
            Assert.AreEqual(20, live.EscrowMmd);
            Assert.AreEqual(1, store.MaxNestedAccounts, "seller and buyer regions must never nest");
        }

        /// <summary>
        /// The last bags close the order Filled and clear active_Material, which is what releases the
        /// UNIQUE (buyer account, material) key so the buyer can post again.
        /// </summary>
        [TestMethod]
        public void Fill_LastBags_ClosesFilled_AndClearsActiveMaterial()
        {
            var order = Place(count: 5, price: 10);

            var result = MarketManager.FillOrder(Seller, order.Id, 5, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Filled, live.Status);
            Assert.AreEqual(0, live.CountRemaining);
            Assert.AreEqual(0, live.EscrowMmd, "the whole escrow has been paid out");
            Assert.IsNotNull(live.ClosedAt);

            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Filled, row.Status);
            Assert.IsNull(row.ActiveMaterial, "a closed order must release the uniqueness key");
            Assert.AreEqual(0, row.CountRemaining);
            Assert.AreEqual(0, row.EscrowMmd);

            Assert.AreEqual(50, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual(0, FullGraniteHeldBy(store, SellerAccount));
            Assert.AreEqual(5, FullGraniteHeldBy(store, BuyerAccount));

            // And the key really is free again.
            var rePost = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 1, 10, MarketChannel.Web);
            Assert.IsTrue(rePost.Ok, $"re-posting after a filled order failed with {rePost.Error}");
        }

        // ---- salvage HAMMER orders: the kind is what decides which item moves ----

        /// <summary>Tourmaline has both a bag and a Hammer (SalvageForge.MaterialTable); Granite has only a bag.</summary>
        private static readonly int Tourmaline = (int)MaterialType.Tourmaline;

        private const uint TourmalineBagWcid = 21082;
        private const uint TourmalineHammerWcid = 1001910;

        private static WorldObject TourmalineBag()
        {
            var bag = FakeVaultWorld.MakeSalvageBag(TourmalineBagWcid, 100, 40, 10, 100);
            bag.MaterialType = MaterialType.Tourmaline;
            return bag;
        }

        /// <summary>
        /// A Hammer: the SAME ItemType and MaterialType as its bag, distinguished ONLY by
        /// SalvageToolCharges. MaxStructure is the 10-charge capacity, not the bag's 100.
        /// </summary>
        private static WorldObject TourmalineHammer(int charges)
        {
            var hammer = FakeVaultWorld.MakeSalvageBag(TourmalineHammerWcid, charges, 40, 10, 100);
            hammer.MaterialType = MaterialType.Tourmaline;
            hammer.MaxStructure = 10;
            hammer.SetProperty(PropertyInt.SalvageToolCharges, 10);
            return hammer;
        }

        /// <summary>
        /// THE FIXTURE THE WHOLE DISCRIMINATION RESTS ON. The seller holds, of the SAME material:
        /// one full bag, one full-charge Hammer, and one 7-of-10 Hammer. All three are
        /// ItemType.TinkeringMaterial with MaterialType Tourmaline, so nothing about the material
        /// tells them apart - only the kind and the charge count do. A fixture holding hammers alone
        /// could not tell "matched on kind" from "matched on material".
        /// </summary>
        private void SeedTourmalineMix()
        {
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineBag() });
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineHammer(10) });
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineHammer(7) });
        }

        private MarketBuyOrder PlaceKind(MarketBuyOrderKind kind, int count, long price)
        {
            var result = MarketManager.PlaceOrder(Buyer, Tourmaline, kind, count, price, MarketChannel.Web);
            Assert.IsTrue(result.Ok, $"placing failed with {result.Error}");
            return result.Value;
        }

        private int HeldBy(uint accountId, MarketBuyOrderKind kind)
            => store.CountMatching(accountId, wo => MarketSalvageMaterials.MatchesOrder(wo, Tourmaline, kind));

        /// <summary>
        /// Everything actually left in an account's fake vault, loose rows and group members alike.
        /// Deliberately NOT CountMatching: the part-charge Hammer matches NEITHER predicate, so a
        /// predicate-based count could never prove it is still there.
        /// </summary>
        private static IEnumerable<WorldObject> Vault(FakeMarketItemStore store, uint accountId)
        {
            var loose = store.Items.TryGetValue(accountId, out var items) ? (IEnumerable<WorldObject>)items : new List<WorldObject>();
            var grouped = store.Groups.TryGetValue(accountId, out var groups) ? groups.SelectMany(g => g) : Enumerable.Empty<WorldObject>();
            return loose.Concat(grouped);
        }

        /// <summary>
        /// A BAG order takes the bag and leaves both Hammers, even though a Hammer carries the same
        /// ItemType and MaterialType and would satisfy a material-only predicate. If FillOrder's
        /// predicate lost its kind, the store would have three candidates instead of one and could
        /// hand the buyer a Hammer worth ten bags for the price of one.
        /// </summary>
        [TestMethod]
        public void Fill_BagOrder_TakesTheBag_AndNeverAHammer()
        {
            SeedTourmalineMix();

            var order = PlaceKind(MarketBuyOrderKind.SalvageBag, 1, 10);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");

            Assert.AreEqual(1, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageBag), "the buyer got the bag");
            Assert.AreEqual(0, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageHammer), "and no Hammer");

            Assert.AreEqual(0, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageBag));
            Assert.AreEqual(1, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer), "the seller keeps the full-charge Hammer");

            Assert.AreEqual(TourmalineBagWcid, result.Value.Wcid, "the transaction row names the bag");
        }

        /// <summary>
        /// A bag order over a vault holding ONLY Hammers finds nothing. This is the same rule as the
        /// test above with the bag removed, so the refusal cannot be attributed to the order simply
        /// preferring the bag it happened to find first.
        /// </summary>
        [TestMethod]
        public void Fill_BagOrder_AgainstHammersOnly_IsRefusedNoMatchingItems()
        {
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineHammer(10), TourmalineHammer(10) });

            var order = PlaceKind(MarketBuyOrderKind.SalvageBag, 1, 10);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoMatchingItems, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls, "a step-0 refusal writes no Pending row");
            Assert.AreEqual(2, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer), "and nothing left the seller");
            Assert.AreEqual(10, MarketManager.GetBuyOrder(order.Id).EscrowMmd);
        }

        /// <summary>
        /// A HAMMER order takes the full-charge Hammer and leaves the bag AND the part-charge Hammer.
        /// Both of the things it must not take are present at once, so a pass here cannot be explained
        /// by either discrimination alone.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_TakesTheFullChargeHammer_AndNeitherTheBagNorThePartCharge()
        {
            SeedTourmalineMix();

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 1, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");

            Assert.AreEqual(1, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageHammer), "the buyer got a full-charge Hammer");
            Assert.AreEqual(0, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageBag), "and no bag");

            Assert.AreEqual(0, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer));
            Assert.AreEqual(1, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageBag), "the seller keeps the bag");

            // The 7-of-10 Hammer is still there: it matches neither predicate, so neither count sees it.
            var sellerItems = Vault(store, SellerAccount);
            Assert.AreEqual(1, sellerItems.Count(wo => wo.WeenieClassId == TourmalineHammerWcid),
                "the part-charge Hammer must still be in the seller's vault");

            Assert.AreEqual(TourmalineHammerWcid, result.Value.Wcid, "the transaction row names the HAMMER");
            Assert.AreEqual(300, result.Value.PriceMmdTotal);
        }

        /// <summary>
        /// THE DESIGN DECISION, asserted directly: a hammer order is filled only by a FULL-CHARGE
        /// Hammer. The seller holds a 7-of-10 Hammer of the right material and nothing else, so the
        /// only thing that can refuse this is the charge count.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_AgainstAPartChargeHammer_IsRefusedNoMatchingItems()
        {
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineHammer(7) });

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 1, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoMatchingItems, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls, "a step-0 refusal writes no Pending row");
            Assert.AreEqual(300, MarketManager.GetBuyOrder(order.Id).EscrowMmd, "the escrow is untouched");
            Assert.AreEqual(1, Vault(store, SellerAccount).Count(wo => wo.WeenieClassId == TourmalineHammerWcid));
        }

        // ---- collapsed LEDGER rows: the normal shape for a forge-fresh bag or Hammer ----

        /// <summary>
        /// THE REPORTED BUG, end to end through the manager. A full-charge Hammer straight out of the
        /// forge is provably identical to a fresh instance of its own weenie, so the vault COLLAPSES it
        /// on deposit and the seller's holding is a ledger row with no biota behind it. The fill path
        /// used to skip every such row, which told a seller holding three of exactly the wanted item
        /// that they held none and refused the fill at step 0.
        ///
        /// Nothing else is seeded for Tourmaline here, so the ledger row is the only thing that can
        /// satisfy this order.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_FromACollapsedLedgerRow_DeliversAndDebitsTheRow()
        {
            store.SeedLedger(SellerAccount, TourmalineHammerWcid, 3, () => TourmalineHammer(10));

            Assert.AreEqual(3, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer),
                "a ledger row of the wanted item must be visible to the preflight count");

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 3, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");
            Assert.AreEqual(MarketTransactionStatus.Completed, result.Value.Status);
            Assert.AreEqual(2, result.Value.Count);

            Assert.AreEqual(1, store.LedgerCount(SellerAccount, TourmalineHammerWcid), "two of the three units left the row");
            Assert.AreEqual(2, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageHammer), "and both reached the buyer");
            Assert.AreEqual(600, wallet.GetBalanceMmd(SellerCharacter));

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(1, live.CountRemaining);
            Assert.AreEqual(300, live.EscrowMmd);
        }

        /// <summary>The same for a BAG order, which is the half a seller hits first: bags collapse too.</summary>
        [TestMethod]
        public void Fill_BagOrder_FromACollapsedLedgerRow_DeliversAndDebitsTheRow()
        {
            store.SeedLedger(SellerAccount, TourmalineBagWcid, 4, TourmalineBag);

            var order = PlaceKind(MarketBuyOrderKind.SalvageBag, 4, 10);
            var result = MarketManager.FillOrder(Seller, order.Id, 4, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");
            Assert.AreEqual(0, store.LedgerCount(SellerAccount, TourmalineBagWcid));
            Assert.AreEqual(4, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageBag));
            Assert.AreEqual(MarketBuyOrderStatus.Filled, MarketManager.GetBuyOrder(order.Id).Status);
        }

        /// <summary>
        /// The kind still decides, on the ledger too. The seller's ONLY holding of this material is a
        /// ledger row of full-charge Hammers, and a BAG order must find nothing in it - otherwise the
        /// fix would have bought ledger visibility at the price of handing a buyer paying bag prices a
        /// Hammer worth ten bags.
        /// </summary>
        [TestMethod]
        public void Fill_BagOrder_AgainstALedgerRowOfHammers_IsRefusedNoMatchingItems()
        {
            store.SeedLedger(SellerAccount, TourmalineHammerWcid, 5, () => TourmalineHammer(10));

            var order = PlaceKind(MarketBuyOrderKind.SalvageBag, 1, 10);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoMatchingItems, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls, "a step-0 refusal writes no Pending row");
            Assert.AreEqual(5, store.LedgerCount(SellerAccount, TourmalineHammerWcid), "and nothing left the row");
        }

        /// <summary>
        /// A ledger row of PART-CHARGE Hammers fills nothing, so the ledger path applies the same
        /// full-charge rule the stored-biota path does rather than trusting the row's wcid.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_AgainstALedgerRowOfPartChargeHammers_IsRefusedNoMatchingItems()
        {
            store.SeedLedger(SellerAccount, TourmalineHammerWcid, 5, () => TourmalineHammer(7));

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 1, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoMatchingItems, result.Error);
            Assert.AreEqual(5, store.LedgerCount(SellerAccount, TourmalineHammerWcid));
        }

        /// <summary>
        /// A mixed take: one stored biota plus one ledger unit, for a count of two. It proves the take
        /// walks past a row that cannot fill the whole order instead of settling for the first shape it
        /// finds, and that the two provenances arrive in ONE delivery.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_MixedStoredAndLedger_DrawsOnBoth()
        {
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineHammer(10) });
            store.SeedLedger(SellerAccount, TourmalineHammerWcid, 1, () => TourmalineHammer(10));

            Assert.AreEqual(2, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer));

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 2, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");
            Assert.AreEqual(2, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageHammer));
            Assert.AreEqual(0, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer), "both the stored one and the ledger unit went");
            Assert.AreEqual(0, store.LedgerCount(SellerAccount, TourmalineHammerWcid));
        }

        /// <summary>
        /// A hammer order over a vault holding ONLY full bags finds nothing - the mirror of
        /// Fill_BagOrder_AgainstHammersOnly. Together the two pin the predicate as symmetric rather
        /// than as one kind accidentally being a superset of the other.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_AgainstBagsOnly_IsRefusedNoMatchingItems()
        {
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineBag(), TourmalineBag() });

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 1, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoMatchingItems, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls);
            Assert.AreEqual(2, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageBag), "and no bag left the seller");
        }

        /// <summary>
        /// A PARTIAL fill rewrites the order row through ToRow, which is the only place the kind is
        /// re-persisted after placement. If ToRow dropped it, the row would come back as a BAG order
        /// still holding active_Material - which is the buyer's OTHER order's slot under
        /// UNIQUE (buyer_Account_Id, active_Material, order_Kind). The update would then be refused by
        /// the key and the order would be left reading its pre-fill count and escrow with the bags
        /// already gone. Both orders are open here on purpose, so that collision is reachable.
        /// </summary>
        [TestMethod]
        public void Fill_HammerOrder_PartialFill_PersistsTheKind_AndDoesNotCollideWithTheBagOrder()
        {
            SeedTourmalineMix();
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { TourmalineHammer(10) });

            var bagOrder = PlaceKind(MarketBuyOrderKind.SalvageBag, 2, 10);
            var hammerOrder = PlaceKind(MarketBuyOrderKind.SalvageHammer, 2, 300);

            var result = MarketManager.FillOrder(Seller, hammerOrder.Id, 1, MarketChannel.Web);
            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");

            var row = repo.BuyOrders.Single(o => o.Id == hammerOrder.Id);

            Assert.AreEqual((byte)MarketBuyOrderKind.SalvageHammer, row.OrderKind, "the row must still be a hammer order");
            Assert.AreEqual(1, row.CountRemaining, "which means the step-5 update actually landed");
            Assert.AreEqual(300, row.EscrowMmd);
            Assert.AreEqual(Tourmaline, row.ActiveMaterial, "still Active, still holding its half of the key");

            // The bag order is untouched and still holds the OTHER half.
            var bagRow = repo.BuyOrders.Single(o => o.Id == bagOrder.Id);
            Assert.AreEqual((byte)MarketBuyOrderKind.SalvageBag, bagRow.OrderKind);
            Assert.AreEqual(2, bagRow.CountRemaining);
            Assert.AreEqual(Tourmaline, bagRow.ActiveMaterial);
        }

        // ---- step 0 refusals: nothing moved, and in two cases no row was even written ----

        [TestMethod]
        public void Fill_MoreThanRemaining_IsRefusedCountUnavailable()
        {
            var order = Place(count: 2, price: 10);

            var result = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.CountUnavailable, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls, "the reservation refuses before the Pending row");
            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount), "and no bag left the seller");
            Assert.AreEqual(20, MarketManager.GetBuyOrder(order.Id).EscrowMmd);
        }

        [TestMethod]
        public void Fill_OwnOrder_IsRefusedNotOwner()
        {
            var order = Place(count: 5, price: 10);

            var result = MarketManager.FillOrder(Buyer, order.Id, 1, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NotOwner, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls);
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter), "no self-dealing round trip through escrow");
            Assert.AreEqual(0, wallet.CreditCalls);
        }

        [TestMethod]
        public void Fill_SellerHoldsTooFew_IsRefusedNoMatchingItems_BeforeThePendingRow()
        {
            // Two decoys that a naive "how many salvage rows does this account have" count would
            // include: a partial bag of the right material, and a full salvage Hammer, which is
            // TinkeringMaterial with a material and a full structure and is still not a bag.
            store.SeedItem(SellerAccount, Bag(60, 4));
            store.SeedItem(SellerAccount, Tool());
            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount), "neither decoy is fillable");

            var order = Place(count: 6, price: 10);               // more than the seller's five bags

            var result = MarketManager.FillOrder(Seller, order.Id, 6, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoMatchingItems, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls, "a refusal knowable at step 0 writes no Pending row");
            Assert.AreEqual(0, store.TakeCalls, "and never reaches the store's take");
            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount));
            Assert.AreEqual(60, MarketManager.GetBuyOrder(order.Id).EscrowMmd, "the escrow is untouched");
        }

        /// <summary>
        /// The room preflight runs BEFORE the Pending row, so a full buyer vault costs no row and no
        /// custody move. The order stays Active: the web tells the seller the buyer has no room.
        /// </summary>
        [TestMethod]
        public void Fill_BuyerVaultFull_IsRefusedVaultFull_BeforeThePendingRow()
        {
            var order = Place(count: 5, price: 10);
            store.PreflightFullVaultAccounts.Add(BuyerAccount);

            var result = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultFull, result.Error);
            Assert.AreEqual(0, repo.AddTransactionCalls, "a refusal knowable at step 0 writes no Pending row");
            Assert.AreEqual(0, store.TakeCalls);
            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount));

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status, "the order stays open for a buyer who makes room");
            Assert.AreEqual(5, live.CountRemaining);
            Assert.AreEqual(50, live.EscrowMmd);
        }

        // ---- the two unwinds ----

        /// <summary>
        /// A take that fails after the preflight is a Failed row, NOT a Refunded one: the escrow was
        /// never touched, so there is nothing to give back. Refunding here would pay the buyer money
        /// that is still sitting on their own order.
        /// </summary>
        [TestMethod]
        public void Fill_TakeShortfallAfterPreflight_MarksFailed_AndRefundsNothing()
        {
            var order = Place(count: 5, price: 10);
            store.FailTakeMatchingAccounts.Add(SellerAccount);

            var result = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, repo.AddTransactionCalls, "the Pending row IS written before the take");

            var row = repo.Transactions.Single();
            Assert.AreEqual((int)MarketTransactionStatus.Failed, row.Status);
            Assert.AreNotEqual((int)MarketTransactionStatus.Refunded, row.Status,
                "nothing was debited on this row, so Refunded would invent a payment");

            Assert.AreEqual(0, wallet.CreditCalls, "no money moves on a fill that never took custody");
            Assert.AreEqual(0, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter));

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(50, live.EscrowMmd, "the escrow is unchanged");
            Assert.AreEqual(5, live.CountRemaining);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status);
            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount));
        }

        /// <summary>
        /// A give that fails hands every bag back to the seller inside the SELLER's own region, and the
        /// row is Failed. The seller must be left whole - asserted by counting what they hold, because
        /// the unwind returns items loosely rather than rebuilding the group row they came from.
        /// </summary>
        [TestMethod]
        public void Fill_GiveFails_ReturnsBagsToSeller_MarksFailed()
        {
            var order = Place(count: 5, price: 10);
            store.FullVaultAccounts.Add(BuyerAccount);

            var result = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultFull, result.Error);
            Assert.AreEqual(1, store.ReturnCalls, "the taken bags went back through the return path");
            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount), "the seller is left whole");
            Assert.AreEqual(0, FullGraniteHeldBy(store, BuyerAccount));

            Assert.AreEqual((int)MarketTransactionStatus.Failed, repo.Transactions.Single().Status);
            Assert.AreEqual(0, wallet.CreditCalls, "nobody is paid for a delivery that did not happen");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(50, live.EscrowMmd);
            Assert.AreEqual(5, live.CountRemaining);
            Assert.AreEqual(1, store.MaxNestedAccounts, "the unwind opens the seller's region again, never inside the buyer's");
        }

        /// <summary>
        /// THE LEDGER HALF of the test above, and the reason the manager hands the take's receipt back
        /// instead of choosing a return kind itself.
        ///
        /// The window is real and ordinary: step 0's CanReceive preflight is advisory and nothing
        /// re-checks the buyer's vault under a lock immediately before delivery, so a buyer vault that
        /// fills in between produces exactly this - a take that SUCCEEDED, out of a collapsed ledger
        /// row, that now has to go back.
        ///
        /// If it goes back through TryReturnToSeller with asLedger false, every unit lands via
        /// AccountVaultStore.TryReturnWithdrawn, which does no cap check and no collapse test: one
        /// capped ledger entry becomes one uncapped stored entry PER UNIT, permanently, because
        /// nothing re-collapses a stored biota. Not a loss, and still a regression.
        /// </summary>
        [TestMethod]
        public void Fill_GiveFails_ReturnsLedgerUnitsToTheLedger_NeverAsStoredBiotas()
        {
            store.SeedLedger(SellerAccount, TourmalineHammerWcid, 3, () => TourmalineHammer(10));
            store.FullVaultAccounts.Add(BuyerAccount);

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 2, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultFull, result.Error);
            Assert.AreEqual(1, store.ReturnCalls, "the taken units went back through the return path");

            Assert.AreEqual(3, store.LedgerCount(SellerAccount, TourmalineHammerWcid),
                "the units went back onto the LEDGER row they came out of");
            Assert.AreEqual(0, Vault(store, SellerAccount).Count(wo => wo.WeenieClassId == TourmalineHammerWcid),
                "and not one of them became a stored biota, which would be an uncapped entry that can never re-collapse");

            Assert.AreEqual(3, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer), "the seller is left whole");
            Assert.AreEqual(0, HeldBy(BuyerAccount, MarketBuyOrderKind.SalvageHammer));

            Assert.AreEqual((int)MarketTransactionStatus.Failed, repo.Transactions.Single().Status);
            Assert.AreEqual(0, wallet.CreditCalls, "nobody is paid for a delivery that did not happen");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(600, live.EscrowMmd);
            Assert.AreEqual(2, live.CountRemaining);
            Assert.AreEqual(1, store.MaxNestedAccounts, "the undo opens the seller's region again, never inside the buyer's");
        }

        /// <summary>
        /// A MIXED take unwound by the same path: one stored Hammer and one ledger unit go back to
        /// different places out of one list. This is the case no single asLedger flag can express, so
        /// it is the one that proves the receipt is being read rather than a kind being assumed.
        /// </summary>
        [TestMethod]
        public void Fill_GiveFails_MixedTake_ReturnsEachHalfToItsOwnHome()
        {
            var stored = TourmalineHammer(10);
            store.SeedGroupOf(SellerAccount, new List<WorldObject> { stored });
            store.SeedLedger(SellerAccount, TourmalineHammerWcid, 1, () => TourmalineHammer(10));
            store.FullVaultAccounts.Add(BuyerAccount);

            var order = PlaceKind(MarketBuyOrderKind.SalvageHammer, 2, 300);
            var result = MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultFull, result.Error);

            Assert.AreEqual(1, store.LedgerCount(SellerAccount, TourmalineHammerWcid), "the ledger unit went back to the ledger");
            Assert.AreEqual(1, Vault(store, SellerAccount).Count(wo => ReferenceEquals(wo, stored)),
                "and the stored Hammer went back as itself - the same object, not units");
            Assert.AreEqual(2, HeldBy(SellerAccount, MarketBuyOrderKind.SalvageHammer), "the seller is left whole across both halves");
        }

        /// <summary>
        /// The manager's half of the same case: a take that THROWS after withdrawing from one group and
        /// before finishing the next. The fill must look exactly like any other failed take - Failed
        /// row, nobody paid, escrow untouched - and above all the seller must still hold every bag.
        ///
        /// Without the store's own unwind the throw reaches SafeSerialized, which reports false, and
        /// this method takes its "the seller's region never ran" branch, which does NOT return anything
        /// to the seller because it has never seen the items. The seller silently loses the three bags
        /// the first withdraw took, and the only trace is a generic "a serialized vault region threw"
        /// with no guid in it.
        /// </summary>
        [TestMethod]
        public void Fill_WhenTheTakeThrowsMidLoop_LeavesTheSellerWhole_MarksFailed_AndPaysNobody()
        {
            var order = Place(count: 5, price: 10);               // needs both of the seller's two groups

            store.ThrowOnTakeMatchingWithdraw = 2;

            var result = MarketManager.FillOrder(Seller, order.Id, 5, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);

            Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount),
                "every bag the first withdraw took must be back in the seller's vault");
            Assert.AreEqual(0, FullGraniteHeldBy(store, BuyerAccount), "and none reached the buyer");

            Assert.AreEqual(1, repo.AddTransactionCalls, "the Pending row IS written before the take");
            Assert.AreEqual((int)MarketTransactionStatus.Failed, repo.Transactions.Single().Status);
            Assert.AreEqual(0, wallet.CreditCalls, "nobody is paid for a delivery that did not happen");
            Assert.AreEqual(0, wallet.GetBalanceMmd(SellerCharacter));

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status, "the order stays open");
            Assert.AreEqual(5, live.CountRemaining);
            Assert.AreEqual(50, live.EscrowMmd, "and its escrow is untouched");

            // This is the ONLY path that calls TryReturnToSeller from inside an already-open seller
            // region, so it is the only place a nesting regression there would show. Every test gets
            // its own FakeMarketItemStore, so a sibling test's assertion does not cover this one.
            //
            // BOTH counters, because they prove different things and only one of them can see this
            // regression. MaxNestedAccounts counts DISTINCT accounts, and the unwind's account is the
            // SELLER's while the region already open is also the SELLER's - so a nested region here
            // reads as [seller, seller], a distinct count of 1, and passes. MaxRegionDepth counts
            // actual nesting on the thread and is the one that fails.
            Assert.AreEqual(1, store.MaxRegionDepth, "the unwind must run inside the open region, never open a second one");
            Assert.AreEqual(1, store.MaxNestedAccounts, "and the buyer's region is never nested in the seller's");
        }

        /// <summary>
        /// The correlated failure at the manager's layer: the withdraw throws AND the unwind that
        /// answers it throws too. The bags are genuinely lost once the vault refuses to take them back,
        /// so what this pins is CONTAINMENT - the fill returns an honest refusal, the row is Failed,
        /// and nobody is paid for a delivery that did not happen.
        ///
        /// WHAT DISCRIMINATES IT is SerializedRegionThrows, and the distinction is worth understanding
        /// before that assertion is ever removed as redundant. SafeSerialized normalizes the RETURN
        /// VALUE of an escaping throw into the same false, so every value-shaped observable here -
        /// result.Ok, the error, ReturnCalls, the Failed row, what the seller ends up holding - reads
        /// identically whether or not the unwind's throw escaped. What it does NOT normalize is the
        /// FACT of the throw, which it records (MarketManager_Purchase.cs:273 logs "a serialized vault
        /// region threw"). The counter is the string-free form of that same signal, and it names the
        /// store's contract directly: nothing inside a region may escape it.
        /// </summary>
        [TestMethod]
        public void Fill_WhenTheTakeAndTheUnwindBothThrow_StillRefusesCleanly_MarksFailed_AndPaysNobody()
        {
            var order = Place(count: 5, price: 10);

            store.ThrowOnTakeMatchingWithdraw = 2;
            store.ThrowOnReturnToSeller = true;

            var result = MarketManager.FillOrder(Seller, order.Id, 5, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultUnavailable, result.Error);
            Assert.AreEqual(1, store.ReturnCalls, "the unwind was attempted before it failed");
            Assert.AreEqual(0, store.SerializedRegionThrows, "the unwind's throw must never escape the seller's region");

            Assert.AreEqual((int)MarketTransactionStatus.Failed, repo.Transactions.Single().Status);
            Assert.AreEqual(0, wallet.CreditCalls, "nobody is paid for a delivery that did not happen");
            Assert.AreEqual(0, FullGraniteHeldBy(store, BuyerAccount), "and nothing reached the buyer");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status);
            Assert.AreEqual(5, live.CountRemaining);
            Assert.AreEqual(50, live.EscrowMmd, "the escrow is untouched, so the buyer can still cancel for all of it");
        }

        // ---- the kill switch, and the auto-delist a fill deliberately does not claim ----

        [TestMethod]
        public void Fill_WithTheSwitchOff_IsRefused_WhileCancelStillSucceeds()
        {
            var order = Place(count: 5, price: 10);

            Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", false));

            try
            {
                var filled = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

                Assert.IsFalse(filled.Ok);
                Assert.AreEqual(MarketError.BuyOrdersDisabled, filled.Error);
                Assert.AreEqual(0, repo.AddTransactionCalls);
                Assert.AreEqual(5, FullGraniteHeldBy(store, SellerAccount));

                var cancelled = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

                Assert.IsTrue(cancelled.Ok, $"cancel must survive the kill switch, got {cancelled.Error}");
                Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter), "the escrow came back with the switch off");
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", true));
            }
        }

        /// <summary>
        /// WANTED-DESIGN 3, "Bags currently listed for sale": a fill makes NO BeginSale claim, so the
        /// market's own withdraw trips the vault's auto-delist hook and the sell listing closes. That is
        /// the owner ruling, and the opposite of what Buy needs, so it can only be tested with the hook
        /// actually wired up.
        /// </summary>
        [TestMethod]
        public void Fill_OverAListedBag_DelistsTheListing()
        {
            var previous = AccountVaultStore.PreWithdrawHook;

            try
            {
                AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;

                // List the 3-bag group first, then fill 2 from it: the pre-withdraw hook must close the listing.
                var group = store.Groups[SellerAccount][0];
                var listing = MarketManager.List(Seller, group[0].Guid.Full, group[0].WeenieClassId, 3, 5, MarketChannel.InGame).Value;
                var order = Place(count: 2, price: 10);
                var result = MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web);
                Assert.IsTrue(result.Ok);
                Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status);
            }
            finally
            {
                AccountVaultStore.PreWithdrawHook = previous;
            }
        }

        // ---- the one change a fill makes to existing purchase recovery (WANTED-DESIGN 6.5) ----

        [TestMethod]
        public void RecoverPendingTransactions_SkipsOrderFillRows_AndStillRefundsOrdinaryOnes()
        {
            // one Pending fill row (BuyOrderId set) and one Pending ordinary row (BuyOrderId null), both old
            repo.Transactions.Add(new ShardMarketTransaction { Id = 501, ListingId = 0, BuyOrderId = 9, BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "B",
                SellerAccountId = SellerAccount, SellerCharacterGuid = SellerCharacter, SellerCharacterName = "S", Wcid = 21013, ItemName = "Salvage", Count = 1, PriceMmdTotal = 10,
                Timestamp = DateTime.UtcNow.AddMinutes(-5), Channel = 0, Status = 0 });
            repo.Transactions.Add(new ShardMarketTransaction { Id = 502, ListingId = 3, BuyOrderId = null, BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "B",
                SellerAccountId = SellerAccount, SellerCharacterGuid = SellerCharacter, SellerCharacterName = "S", Wcid = 9001, ItemName = "Sword", Count = 1, PriceMmdTotal = 25,
                Timestamp = DateTime.UtcNow.AddMinutes(-5), Channel = 0, Status = 0 });

            MarketManager.RecoverPendingTransactions(1000);

            Assert.AreEqual((int)MarketTransactionStatus.Failed, repo.Transactions.Single(t => t.Id == 501).Status);
            Assert.AreEqual((int)MarketTransactionStatus.Refunded, repo.Transactions.Single(t => t.Id == 502).Status);
            Assert.AreEqual(1025, wallet.GetBalanceMmd(BuyerCharacter), "only the ordinary row was refunded");
        }

        // ---- boot reconciliation of a fill whose order decrement was lost (WANTED-DESIGN 6.5) ----
        //
        // Each of these suppresses ONLY repository.UpdateBuyOrder across the fill. That is exactly the
        // step-5 write and nothing else: the transaction row goes through UpdateTransaction, so the
        // fill still reaches Completed while the order keeps its pre-fill count and escrow - the state
        // a process death between the two writes leaves behind.

        /// <summary>
        /// A Completed fill over an order still reading its pre-fill numbers is not a stale display, it
        /// is money. The order can be filled its whole count again, or cancelled for the entire original
        /// escrow, so the escrow-conservation invariant is what this asserts: whatever the order pays
        /// out plus whatever it refunds can never exceed what it debited.
        /// </summary>
        [TestMethod]
        public void Reconcile_WhenAFillsOrderDecrementWasLost_CorrectsRemainingAndEscrow_AndTheCancelRefundIsNotInflated()
        {
            var order = Place(count: 5, price: 10);               // buyer 1000 -> 950, escrow 50

            repo.FailUpdateBuyOrder = true;
            var fill = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);
            repo.FailUpdateBuyOrder = false;

            Assert.IsTrue(fill.Ok, $"the fill itself must succeed, got {fill.Error}");
            Assert.AreEqual((int)MarketTransactionStatus.Completed, repo.Transactions.Single().Status,
                "the row completes the instant custody changes, which is what makes it evidence");
            Assert.AreEqual(5, repo.BuyOrders.Single().CountRemaining, "while the order row kept its pre-fill count");
            Assert.AreEqual(50, repo.BuyOrders.Single().EscrowMmd);

            // The restart: the index is rebuilt from the rows on disk, pre-fill numbers and all.
            MarketManager.Initialize(store, wallet, repo);

            Assert.AreEqual(5, MarketManager.GetBuyOrder(order.Id).CountRemaining, "reloaded wrong, as it must be for this to be worth fixing");
            Assert.AreEqual(1, MarketManager.ReconcileFilledOrders());

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status, "a partly filled order stays open");
            Assert.AreEqual(2, live.CountRemaining, "5 wanted, less the 3 bags its Completed fills delivered");
            Assert.AreEqual(20, live.EscrowMmd, "and the escrow those 2 bags can still be paid with");

            var row = repo.BuyOrders.Single();
            Assert.AreEqual(2, row.CountRemaining, "the correction is written down, not only held in memory");
            Assert.AreEqual(20, row.EscrowMmd);

            Assert.IsTrue(MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web).Ok);
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter) + wallet.GetBalanceMmd(SellerCharacter),
                "escrow paid to the seller plus escrow refunded to the buyer must never exceed escrow debited");
        }

        /// <summary>The corrected remaining reaching zero closes the order and releases its uniqueness key.</summary>
        [TestMethod]
        public void Reconcile_WhenEveryBagWasDelivered_ClosesFilled_AndReleasesTheKey()
        {
            var order = Place(count: 5, price: 10);

            repo.FailUpdateBuyOrder = true;
            Assert.IsTrue(MarketManager.FillOrder(Seller, order.Id, 5, MarketChannel.Web).Ok);
            repo.FailUpdateBuyOrder = false;

            MarketManager.Initialize(store, wallet, repo);

            Assert.AreEqual(1, MarketManager.ReconcileFilledOrders());

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Filled, live.Status);
            Assert.AreEqual(0, live.CountRemaining);
            Assert.AreEqual(0, live.EscrowMmd);
            Assert.IsNotNull(live.ClosedAt);
            Assert.IsNull(repo.BuyOrders.Single().ActiveMaterial, "a closed order must release the uniqueness key");

            var rePost = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 1, 10, MarketChannel.Web);
            Assert.IsTrue(rePost.Ok, $"and the buyer can post again, got {rePost.Error}");
        }

        /// <summary>
        /// A healthy order must cost nothing. A reconciliation that rewrote every Active order at boot
        /// would be indistinguishable from one that never worked, so "changes nothing" is the assertion
        /// that gives the test above its meaning.
        /// </summary>
        [TestMethod]
        public void Reconcile_WhenTheOrderAlreadyAgreesWithItsFills_CorrectsNothing_AndWritesNothing()
        {
            var order = Place(count: 5, price: 10);
            Assert.IsTrue(MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web).Ok);

            MarketManager.Initialize(store, wallet, repo);

            var writesBefore = repo.UpdateBuyOrderCalls;

            Assert.AreEqual(0, MarketManager.ReconcileFilledOrders());
            Assert.AreEqual(writesBefore, repo.UpdateBuyOrderCalls, "an order that agrees with its fills costs no write");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(2, live.CountRemaining);
            Assert.AreEqual(20, live.EscrowMmd);
        }

        /// <summary>
        /// A NULL read means the read FAILED, and reconciliation is SKIPPED. Treating null as "no order
        /// has ever been filled" would correct every Active order back up to its full count_Total - the
        /// exact inflation this pass exists to remove, applied to the whole market at once.
        /// </summary>
        [TestMethod]
        public void Reconcile_NullRead_SkipsEverything()
        {
            var order = Place(count: 5, price: 10);

            repo.FailUpdateBuyOrder = true;
            Assert.IsTrue(MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web).Ok);
            repo.FailUpdateBuyOrder = false;

            MarketManager.Initialize(store, wallet, repo);
            repo.FailFillTotalsRead = true;

            Assert.AreEqual(0, MarketManager.ReconcileFilledOrders(), "a failed read must not be read as nothing to correct");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(5, live.CountRemaining, "the order keeps the numbers on its row until a later successful pass");
            Assert.AreEqual(50, live.EscrowMmd);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status);
        }

        /// <summary>
        /// Fills are evidence that custody changed, so they can only ever mean an order owes LESS. An
        /// order reading MORE remaining than its fills account for means a fill row is missing, and
        /// inventing escrow to match it would create money - the opposite of this pass's purpose.
        /// </summary>
        [TestMethod]
        public void Reconcile_NeverCorrectsAnOrderUpwards()
        {
            var order = Place(count: 5, price: 10);
            Assert.IsTrue(MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web).Ok);

            // The fill's own row is lost while the order's decrement survived: the mirror image of the
            // case above, and the one where the order row is the trustworthy half.
            repo.Transactions.Clear();

            MarketManager.Initialize(store, wallet, repo);

            Assert.AreEqual(0, MarketManager.ReconcileFilledOrders());

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(2, live.CountRemaining, "the decremented count stands");
            Assert.AreEqual(20, live.EscrowMmd, "and no escrow is conjured back onto the order");
        }

        /// <summary>
        /// MINOR 6: market_transaction_buy_order_idx exists to answer "show me every fill of order N",
        /// and until MarketTransactionQuery carried the filter nothing could ask. An ordinary sale has a
        /// NULL buy_Order_Id, so the filter can never widen into one.
        /// </summary>
        [TestMethod]
        public void Investigation_FindTransactionsByBuyOrderId_ReturnsThatOrdersFillsAndNothingElse()
        {
            var order = Place(count: 5, price: 10);
            Assert.IsTrue(MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web).Ok);

            // An ordinary listing sale sitting alongside it, with no buy_Order_Id at all.
            repo.Transactions.Add(new ShardMarketTransaction { Id = 900, ListingId = 3, BuyOrderId = null,
                BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "B",
                SellerAccountId = SellerAccount, SellerCharacterGuid = SellerCharacter, SellerCharacterName = "S",
                Wcid = 9001, ItemName = "Sword", Count = 1, PriceMmdTotal = 25,
                Timestamp = DateTime.UtcNow, Channel = 0, Status = (int)MarketTransactionStatus.Completed });

            var fills = repo.FindTransactions(new MarketTransactionQuery { BuyOrderId = order.Id });

            Assert.IsNotNull(fills);
            Assert.AreEqual(1, fills.Count, "exactly the one fill of that order");
            Assert.AreEqual(order.Id, fills[0].BuyOrderId);
            Assert.AreEqual(3, fills[0].Count);

            Assert.AreEqual(0, repo.FindTransactions(new MarketTransactionQuery { BuyOrderId = order.Id + 1000 }).Count,
                "and an order with no fills reads empty rather than unfiltered");
        }

        // ---- concurrency (WANTED-DESIGN 6.6) ----
        //
        // Both tests below park a fill INSIDE the seller's vault region using store.StoreHold and
        // rendezvous with store.DelayEntered. Never a timed sleep: a sleep is a race by construction,
        // because on a starved CI thread pool this method's own continuation can outlast the sleep and
        // the "held" call is already finished when the probe lands. A hold has no window to miss.

        /// <summary>
        /// The reservation (WANTED-DESIGN 6.6) is the only thing standing between two concurrent fills
        /// and an order that has delivered more bags than its escrow can pay for. Without
        /// `CountRemaining - InFlight`, the second fill's step 0 reads the UNDECREMENTED count, passes,
        /// and hands over bags the order can no longer buy.
        /// </summary>
        [TestMethod]
        public async Task Fill_SecondOfTwoOverCommittingFills_IsRefused_ByTheReservation()
        {
            // Order for 3; two sellers each try 2 while the first is HELD inside its seller region.
            var order = Place(count: 3, price: 10);

            wallet.Seed(RivalCharacter, 0);
            store.SeedGroupOf(RivalAccount, new List<WorldObject> { Bag(100, 4), Bag(100, 4) });

            using var hold = new ManualResetEventSlim(false);
            store.StoreHold = hold;

            var first = Task.Run(() => MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web));

            try
            {
                // Rendezvous with the first fill actually being inside the store, which is past its
                // reservation: InFlight is 2 and CountRemaining is still 3.
                await store.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var second = MarketManager.FillOrder(Rival, order.Id, 2, MarketChannel.Web);

                Assert.IsFalse(second.Ok, "3 remaining minus 2 in flight leaves 1, which cannot cover a second fill of 2");
                Assert.AreEqual(MarketError.CountUnavailable, second.Error);
                Assert.AreEqual(1, repo.AddTransactionCalls, "the loser writes no Pending row of its own");
                Assert.AreEqual(2, FullGraniteHeldBy(store, RivalAccount), "and gives up no bags");
            }
            finally
            {
                hold.Set();
                store.StoreHold = null;
            }

            var firstResult = await first;

            Assert.IsTrue(firstResult.Ok, $"the winning fill must still complete, got {firstResult.Error}");
            Assert.AreEqual(20, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual(0, wallet.GetBalanceMmd(RivalCharacter), "the refused seller is paid nothing");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(1, live.CountRemaining);
            Assert.AreEqual(10, live.EscrowMmd, "escrow and remaining stay in step: 1 bag at 10 MMD");
            Assert.AreEqual(0, live.InFlight, "the reservation is released on every path");
        }

        /// <summary>
        /// A cancel racing a fill would refund escrow the fill is about to pay out of, so the buyer gets
        /// their money back AND the seller gets paid for bags the buyer keeps. CloseOrder refuses
        /// OrderBusy while any reservation is held; the web retries a moment later.
        /// </summary>
        [TestMethod]
        public async Task Cancel_DuringAnInFlightFill_RefusesOrderBusy_ThenSucceedsAfterRelease()
        {
            var order = Place(count: 5, price: 10);               // buyer 1000 -> 950, escrow 50

            using var hold = new ManualResetEventSlim(false);
            store.StoreHold = hold;

            var fill = Task.Run(() => MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web));

            try
            {
                await store.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var busy = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

                Assert.IsFalse(busy.Ok);
                Assert.AreEqual(MarketError.OrderBusy, busy.Error);
                Assert.AreEqual(MarketBuyOrderStatus.Active, MarketManager.GetBuyOrder(order.Id).Status,
                    "a refused cancel must not have flipped the status on its way out");
                Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter), "and must refund nothing");
                Assert.AreEqual(0, wallet.CreditCalls);
            }
            finally
            {
                hold.Set();
                store.StoreHold = null;
            }

            var filled = await fill;
            Assert.IsTrue(filled.Ok, $"the in-flight fill must still complete, got {filled.Error}");

            var cancelled = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

            Assert.IsTrue(cancelled.Ok, $"the same cancel must succeed once the reservation is released, got {cancelled.Error}");
            Assert.AreEqual(MarketBuyOrderStatus.Cancelled, cancelled.Value.Status);
            Assert.AreEqual(20, wallet.GetBalanceMmd(SellerCharacter), "the fill was paid from escrow");
            Assert.AreEqual(980, wallet.GetBalanceMmd(BuyerCharacter), "and only the 30 MMD still held came back");
            Assert.AreEqual(0, repo.BuyOrders.Single().EscrowMmd);
        }
    }
}

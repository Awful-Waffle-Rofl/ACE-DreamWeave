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
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardMarketBuyOrder = ACE.Database.Models.Shard.MarketBuyOrder;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Wanted buy order LIFECYCLE (WANTED-DESIGN 6.1, 6.3, 6.4, 6.5): placement and its escrow
    /// debit, cancel, the expiry pass, boot recovery and the orders feed. The fill path is the sibling
    /// class MarketOrderFillTests.
    ///
    /// Every test here is named for the invariant it protects, because each one fails silently in
    /// production: money debited with no order to show for it, an order closed with its escrow
    /// stranded, a refund paid twice across a restart.
    ///
    /// Referencing MarketSalvageMaterials (which PlaceOrder does, to resolve the material) drags in
    /// that class's wcidByMaterial field initializer, which reads Player.MaterialSalvage and so forces
    /// Player's static type initializer - which unconditionally calls
    /// DatabaseManager.World.GetCachedWeenie for "portalmarketplace" and 10 PK-arena portal names
    /// (Player_Location.cs) and NREs with no live world database. MuleSummonTests.cs,
    /// PortalDestinationGuardTests.cs, MarketSalvageMaterialsTests.cs and MarketTakeMatchingTests.cs
    /// all hit the identical trap and fix it the identical way, copied here.
    /// </summary>
    [TestClass]
    public class MarketBuyOrderTests
    {
        // A range of its own, clear of MuleSummonTests (90399+), PortalDestinationGuardTests (90800+),
        // MarketSalvageMaterialsTests and MarketTakeMatchingTests (90901-90911).
        private static uint nextWcid = 91000;

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

        /// <summary>A third party, for the tests that need a second Active order the pass must leave alone.</summary>
        private const uint OtherAccount = 8203;
        private const uint OtherCharacter = 0x50000503;

        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Marketbuyer");
        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
        private static readonly MarketActor Other = new MarketActor(OtherAccount, OtherCharacter, "Other");

        private static readonly int Granite = (int)MaterialType.Granite;

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;

        private Func<uint, Weenie> savedWeenieLookup;

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

        // ---- place (WANTED-DESIGN 6.1) ----

        [TestMethod]
        public void Place_HappyPath_DebitsEscrow_WritesActiveRow_AndBumpsTheSequence()
        {
            var seqBefore = MarketManager.ChangeSequence;
            var order = Place(count: 5, price: 10);

            Assert.AreEqual(MarketBuyOrderStatus.Active, order.Status);
            Assert.AreEqual(50, order.EscrowMmd);
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(1, wallet.DebitCalls);
            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Active, row.Status);
            Assert.AreEqual(Granite, row.ActiveMaterial);
            Assert.AreEqual(50, row.EscrowMmd);
            Assert.IsTrue(order.Seq > seqBefore);
            Assert.IsTrue(order.ExpiresAt > order.CreatedAt.AddDays(29));
        }

        /// <summary>
        /// WANTED-DESIGN 3, "Uniqueness": one Active order per (buyer account, material). Without this
        /// the Wanted page shows one buyer twice at two prices and the escrow of both is live.
        /// </summary>
        [TestMethod]
        public void Place_SecondActiveOrderOnTheSameMaterial_IsRefusedOrderExists()
        {
            Place(count: 5, price: 10);

            var second = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 2, 20, MarketChannel.Web);

            Assert.IsFalse(second.Ok);
            Assert.AreEqual(MarketError.OrderExists, second.Error);
            Assert.AreEqual(1, repo.BuyOrders.Count, "the refusal happens before the Pending row");
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter), "and before any second debit");
            Assert.AreEqual(1, wallet.DebitCalls);
        }

        /// <summary>
        /// The same uniqueness rule as the test above, under CONCURRENCY, which is the only shape a
        /// player can actually produce it in: a double-click on the web place form, or any two
        /// concurrent POST /v1/orders. MarketRateLimiter.TryWrite is a token bucket, not a mutex, so
        /// nothing upstream serializes them.
        ///
        /// The uniqueness claim has to span the WHOLE placement, not just the moment of the check. A
        /// check that takes indexLock, releases it, and only publishes the order after an insert, a
        /// debit and an update lets both placements read exists == false and both debit. The database
        /// cannot backstop it either: the Pending row is inserted with active_Material NULL, and NULLs
        /// are distinct under UNIQUE (buyer_Account_Id, active_Material) - modelled at AddBuyOrder in
        /// MarketFakes, and the reason this is a test rather than a comment.
        ///
        /// The rendezvous is a real one: the first placement parks inside AddBuyOrder, exactly in the
        /// window, and the second runs while it is parked.
        /// </summary>
        [TestMethod]
        public async Task Place_TwoConcurrentPlacements_OnlyOneBecomesActive_AndOnlyOneDebits()
        {
            using var hold = new ManualResetEventSlim(false);
            repo.AddBuyOrderHold = hold;

            var first = Task.Run(() => MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web));
            MarketResult<MarketBuyOrder> second;

            try
            {
                await repo.AddBuyOrderEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                second = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);
            }
            finally
            {
                hold.Set();
            }

            var firstResult = await first;
            repo.AddBuyOrderHold = null;

            var active = MarketManager.GetActiveBuyOrdersForAccount(BuyerAccount)
                                      .Where(o => o.MaterialType == Granite).ToList();

            Assert.AreEqual(1, active.Count,
                $"two concurrent placements produced {active.Count} ACTIVE order(s) for the same account and material");
            Assert.AreEqual(1, wallet.DebitCalls, "and only one of them may debit the buyer");
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter));

            Assert.IsTrue(firstResult.Ok ^ second.Ok, "exactly one of the two placements may succeed");
            Assert.AreEqual(MarketError.OrderExists, (firstResult.Ok ? second : firstResult).Error,
                "and the loser is refused for the reason a player can act on");

            Assert.AreEqual(1, repo.BuyOrders.Count(o => o.Status == (int)MarketBuyOrderStatus.Active),
                "one Active row on disk, holding the uniqueness key alone");
        }

        /// <summary>
        /// The money-creation chain the concurrency above unlocks, asserted end to end so it stays shut
        /// whatever route reaches it. A step-3 persist that fails must NOT publish an Active order.
        ///
        /// Publishing one anyway leaves an order that is Active in memory and Pending on disk. It is
        /// served as fillable; a PARTIAL fill's own step-5 write keeps colliding with whatever holds
        /// active_Material, so the disk row stays Pending with count_Total intact, and
        /// RecoverPendingOrders then refunds price * count_Total on the next boot. The buyer is paid
        /// back in full for bags they kept, and the seller keeps the escrow they were paid: MMD created
        /// from nothing and bags transferred free.
        ///
        /// The assertions are ordered so the money question is answered FIRST - it is the invariant,
        /// and the refusal below it is only the mechanism that currently keeps it.
        /// </summary>
        [TestMethod]
        public void Place_WhenActivationCannotBePersisted_PublishesNoOrder_SoNoFillCanCreateMoney()
        {
            wallet.Seed(BuyerCharacter, 10000);
            wallet.Seed(SellerCharacter, 0);
            const long startingTotal = 10000;

            store.SeedGroupOf(SellerAccount, new List<WorldObject> { Bag(), Bag(), Bag(), Bag(), Bag() });

            // Held across the placement AND the fill: one shard outage, not two.
            repo.FailUpdateBuyOrder = true;

            var placed = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            var orderId = repo.BuyOrders.Count == 1 ? repo.BuyOrders[0].Id : 0u;
            var filled = MarketManager.FillOrder(Seller, orderId, 3, MarketChannel.Web);

            // The shard comes back, and the process restarts: this is the pass that pays the refund.
            repo.FailUpdateBuyOrder = false;
            MarketManager.RecoverPendingOrders(0);

            var buyerBalance = wallet.GetBalanceMmd(BuyerCharacter);
            var sellerBalance = wallet.GetBalanceMmd(SellerCharacter);

            Assert.AreEqual(startingTotal, buyerBalance + sellerBalance,
                $"MMD was created: buyer {buyerBalance} + seller {sellerBalance} against {startingTotal} at the start");
            Assert.AreEqual(0, FullGraniteHeldBy(BuyerAccount), "and the buyer holds bags they were refunded for");
            Assert.AreEqual(5, FullGraniteHeldBy(SellerAccount), "while the seller keeps every bag they still own");

            Assert.IsFalse(placed.Ok, "an order whose activation could not be written down must not report success");
            Assert.AreEqual(MarketError.ServerError, placed.Error);
            Assert.IsNull(MarketManager.GetBuyOrder(orderId), "and no order may reach the index");
            Assert.IsFalse(filled.Ok, "so there is nothing to fill");
            Assert.AreEqual(0, repo.AddTransactionCalls, "and no fill row is written");
        }

        /// <summary>A full granite salvage bag, the only thing a Granite order can be filled with.</summary>
        private static WorldObject Bag()
        {
            var bag = FakeVaultWorld.MakeSalvageBag(21013, 100, 40, 10, 100);
            bag.MaterialType = MaterialType.Granite;
            return bag;
        }

        private int FullGraniteHeldBy(uint accountId)
            => store.CountMatching(accountId, wo => MarketSalvageMaterials.IsFullBagOf(wo, Granite));

        /// <summary>Closing an order clears active_Material, which is what frees the UNIQUE key for a re-post.</summary>
        [TestMethod]
        public void Place_ThenCancel_ThenRePost_Succeeds()
        {
            var first = Place(count: 5, price: 10);

            Assert.IsTrue(MarketManager.CancelOrder(Buyer, first.Id, MarketChannel.Web).Ok);
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));

            var second = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 20, MarketChannel.Web);

            Assert.IsTrue(second.Ok, $"re-posting after a cancel failed with {second.Error}");
            Assert.AreEqual(MarketBuyOrderStatus.Active, second.Value.Status);
            Assert.AreEqual(100, second.Value.EscrowMmd);
            Assert.AreEqual(900, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(2, repo.BuyOrders.Count);
            Assert.IsNull(repo.BuyOrders.Single(o => o.Id == first.Id).ActiveMaterial,
                "the cancelled row must release the key it held");
        }

        /// <summary>A free order is a request for a gift, not a purchase (WANTED-DESIGN 6.1).</summary>
        [TestMethod]
        public void Place_ZeroPrice_IsRefusedInvalidPrice()
        {
            var zero = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 0, MarketChannel.Web);

            Assert.IsFalse(zero.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, zero.Error);

            var negative = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, -10, MarketChannel.Web);

            Assert.IsFalse(negative.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, negative.Error);

            Assert.AreEqual(0, repo.AddBuyOrderCalls, "neither reaches the Pending row");
            Assert.AreEqual(0, wallet.DebitCalls);
        }

        [TestMethod]
        public void Place_CountOverMax_IsRefusedCountUnavailable()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_buy_order_max_count", 3));

            try
            {
                var over = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 4, 10, MarketChannel.Web);

                Assert.IsFalse(over.Ok);
                Assert.AreEqual(MarketError.CountUnavailable, over.Error);
                Assert.AreEqual(0, repo.AddBuyOrderCalls);

                var atTheCap = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 3, 10, MarketChannel.Web);

                Assert.IsTrue(atTheCap.Ok, $"the cap itself must still be placeable, got {atTheCap.Error}");
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyLong("market_buy_order_max_count", 100));
            }
        }

        /// <summary>
        /// The five category headers of Player.MaterialSalvage map to wcid 0 and are not orderable, and
        /// neither is a value that is no MaterialType at all.
        /// </summary>
        [TestMethod]
        public void Place_UnknownMaterial_IsRefusedInvalidMaterial()
        {
            var header = MarketManager.PlaceOrder(Buyer, 3, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            Assert.IsFalse(header.Ok);
            Assert.AreEqual(MarketError.InvalidMaterial, header.Error, "material 3 is the Cloth category header");

            var nonsense = MarketManager.PlaceOrder(Buyer, 999, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            Assert.IsFalse(nonsense.Ok);
            Assert.AreEqual(MarketError.InvalidMaterial, nonsense.Error);

            Assert.AreEqual(0, repo.AddBuyOrderCalls);
            Assert.AreEqual(0, wallet.DebitCalls);
        }

        // ---- salvage HAMMER orders ----

        /// <summary>Tourmaline has both a bag and a Hammer (SalvageForge.MaterialTable); Granite has only a bag.</summary>
        private static readonly int Tourmaline = (int)MaterialType.Tourmaline;

        /// <summary>
        /// UNIQUENESS IS PER KIND, not per material. A buyer wanting Tourmaline bags at 10 and
        /// Tourmaline Hammers at 300 is two different standing offers for two different objects, and
        /// collapsing them would force a player to cancel one to post the other.
        ///
        /// Both orders name the SAME material on purpose: if the existence check or the in-flight claim
        /// still keyed on (account, material) alone, the second placement here would be refused
        /// order_exists and this test fails on its very first assertion.
        /// </summary>
        [TestMethod]
        public void Place_ABagOrderAndAHammerOrderForOneMaterial_BothBecomeActive()
        {
            var bagOrder = MarketManager.PlaceOrder(Buyer, Tourmaline, MarketBuyOrderKind.SalvageBag, 2, 10, MarketChannel.Web);
            Assert.IsTrue(bagOrder.Ok, $"the bag order failed with {bagOrder.Error}");

            var hammerOrder = MarketManager.PlaceOrder(Buyer, Tourmaline, MarketBuyOrderKind.SalvageHammer, 1, 300, MarketChannel.Web);
            Assert.IsTrue(hammerOrder.Ok, $"the hammer order failed with {hammerOrder.Error}");

            Assert.AreEqual(MarketBuyOrderKind.SalvageBag, bagOrder.Value.Kind);
            Assert.AreEqual(MarketBuyOrderKind.SalvageHammer, hammerOrder.Value.Kind);

            // The hammer order names the HAMMER wcid, not the bag's - the transaction row and the
            // Wanted page both read it.
            Assert.AreEqual(1001910u, hammerOrder.Value.Wcid, "the Tourmaline Hammer's wcid");
            Assert.AreNotEqual(hammerOrder.Value.Wcid, bagOrder.Value.Wcid);

            var active = MarketManager.GetActiveBuyOrdersForAccount(BuyerAccount);
            Assert.AreEqual(2, active.Count, "one Active order of each kind");

            // Both rows hold the widened UNIQUE key at once, which the two-column key could not allow.
            Assert.AreEqual(2, repo.BuyOrders.Count(o => o.Status == (int)MarketBuyOrderStatus.Active));
            CollectionAssert.AreEquivalent(new[] { (byte)0, (byte)1 },
                repo.BuyOrders.Where(o => o.Status == (int)MarketBuyOrderStatus.Active).Select(o => o.OrderKind).ToList());

            Assert.AreEqual(1000 - 20 - 300, wallet.GetBalanceMmd(BuyerCharacter), "both escrows were taken");
        }

        /// <summary>
        /// The other half of the same rule: a second Active order of the SAME kind and material is
        /// still refused. Without this, widening the key would have quietly removed the one-order
        /// guarantee rather than made it kind-aware.
        /// </summary>
        [TestMethod]
        public void Place_SecondActiveHammerOrderOnTheSameMaterial_IsRefusedOrderExists()
        {
            var first = MarketManager.PlaceOrder(Buyer, Tourmaline, MarketBuyOrderKind.SalvageHammer, 1, 300, MarketChannel.Web);
            Assert.IsTrue(first.Ok, $"the first hammer order failed with {first.Error}");

            var second = MarketManager.PlaceOrder(Buyer, Tourmaline, MarketBuyOrderKind.SalvageHammer, 1, 400, MarketChannel.Web);

            Assert.IsFalse(second.Ok);
            Assert.AreEqual(MarketError.OrderExists, second.Error);
            Assert.AreEqual(1, repo.BuyOrders.Count, "the refusal happens before the Pending row");
            Assert.AreEqual(700, wallet.GetBalanceMmd(BuyerCharacter), "and before any second debit");
            Assert.AreEqual(1, wallet.DebitCalls);
        }

        /// <summary>
        /// Two concurrent HAMMER placements, the shape a double-click on the web place form produces.
        /// This is the in-flight CLAIM's half of the guarantee, not the database key's: the Pending row
        /// inserts active_Material NULL and NULLs are distinct, so the key cannot fire until step 3, by
        /// which point both buyers have been debited. Mirrors the bag test above it, and fails if the
        /// kind were added to the existence check but not to placementsInFlight.
        /// </summary>
        [TestMethod]
        public async Task Place_TwoConcurrentHammerPlacements_OnlyOneBecomesActive_AndOnlyOneDebits()
        {
            using var hold = new ManualResetEventSlim(false);
            repo.AddBuyOrderHold = hold;

            var first = Task.Run(() => MarketManager.PlaceOrder(Buyer, Tourmaline, MarketBuyOrderKind.SalvageHammer, 1, 300, MarketChannel.Web));
            MarketResult<MarketBuyOrder> second;

            try
            {
                await repo.AddBuyOrderEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                second = MarketManager.PlaceOrder(Buyer, Tourmaline, MarketBuyOrderKind.SalvageHammer, 1, 300, MarketChannel.Web);
            }
            finally
            {
                hold.Set();
            }

            var firstResult = await first;
            repo.AddBuyOrderHold = null;

            var active = MarketManager.GetActiveBuyOrdersForAccount(BuyerAccount)
                                      .Where(o => o.MaterialType == Tourmaline && o.Kind == MarketBuyOrderKind.SalvageHammer).ToList();

            Assert.AreEqual(1, active.Count,
                $"two concurrent placements produced {active.Count} ACTIVE hammer order(s) for the same account and material");
            Assert.AreEqual(1, wallet.DebitCalls, "and only one of them may debit the buyer");
            Assert.IsTrue(firstResult.Ok ^ second.Ok, "exactly one of the two placements may succeed");
            Assert.AreEqual(MarketError.OrderExists, (firstResult.Ok ? second : firstResult).Error);
        }

        /// <summary>
        /// A hammer order for a material with no Hammer is refused AT PLACEMENT, with its own code.
        /// Accepting it would park the buyer's escrow on the Wanted page until it expired, because
        /// nothing in the world could ever match it.
        ///
        /// Granite discriminates: it is a real, orderable BAG material, so the refusal cannot be the
        /// generic invalid_material path - a bag order for the same material succeeds immediately after.
        /// </summary>
        [TestMethod]
        public void Place_HammerOrderForAMaterialWithNoHammer_IsRefused_AndTakesNothing()
        {
            var result = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageHammer, 2, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NoHammerForMaterial, result.Error,
                "not invalid_material: Granite salvage exists, only a Granite Hammer does not");
            Assert.AreEqual(0, repo.AddBuyOrderCalls, "no Pending row");
            Assert.AreEqual(0, wallet.DebitCalls, "and no debit");
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));

            // Same material, bag kind: accepted. This is what makes the refusal above about the KIND.
            var asBags = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 2, 10, MarketChannel.Web);
            Assert.IsTrue(asBags.Ok, $"a Granite BAG order must still be accepted, got {asBags.Error}");

            // And a material with no salvage at all is still invalid_material, not no_hammer.
            var nonsense = MarketManager.PlaceOrder(Buyer, 999, MarketBuyOrderKind.SalvageHammer, 2, 10, MarketChannel.Web);
            Assert.AreEqual(MarketError.InvalidMaterial, nonsense.Error);
        }

        /// <summary>
        /// A kind the enum does not name is refused BEFORE the material lookup and before any money
        /// moves. Without the guard it would reach FillOrder's predicate switch, whose default matches
        /// nothing - an order that took the buyer's escrow and could never be filled.
        /// </summary>
        [TestMethod]
        public void Place_UnknownKind_IsRefused_BeforeAnythingMoves()
        {
            var result = MarketManager.PlaceOrder(Buyer, Granite, (MarketBuyOrderKind)99, 2, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InvalidMaterial, result.Error);
            Assert.AreEqual(0, repo.AddBuyOrderCalls);
            Assert.AreEqual(0, wallet.DebitCalls);
        }

        /// <summary>
        /// A row read back from disk carries its kind, so a restart does not turn every hammer order
        /// into a bag order that a bag can fill. The row is written by hand rather than placed, which
        /// is what an existing shard's rows look like to LoadOrders.
        /// </summary>
        [TestMethod]
        public void LoadOrders_CarriesTheKindOffTheRow()
        {
            MarketManager.Shutdown();

            var now = DateTime.UtcNow;
            repo.BuyOrders.Add(new ShardMarketBuyOrder
            {
                Id = 41, BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "Marketbuyer",
                MaterialType = Tourmaline, OrderKind = (byte)MarketBuyOrderKind.SalvageHammer, Wcid = 1001910,
                PriceMmd = 300, CountTotal = 1, CountRemaining = 1, EscrowMmd = 300,
                Status = (int)MarketBuyOrderStatus.Active, ActiveMaterial = Tourmaline,
                CreatedAt = now, ExpiresAt = now.AddDays(30),
            });

            // A pre-kind row: order_Kind absent on disk reads 0, which is what every one of them was.
            repo.BuyOrders.Add(new ShardMarketBuyOrder
            {
                Id = 42, BuyerAccountId = OtherAccount, BuyerCharacterGuid = OtherCharacter, BuyerCharacterName = "Other",
                MaterialType = Tourmaline, Wcid = 21082,
                PriceMmd = 10, CountTotal = 2, CountRemaining = 2, EscrowMmd = 20,
                Status = (int)MarketBuyOrderStatus.Active, ActiveMaterial = Tourmaline,
                CreatedAt = now, ExpiresAt = now.AddDays(30),
            });

            MarketManager.Initialize(store, wallet, repo);

            Assert.AreEqual(MarketBuyOrderKind.SalvageHammer, MarketManager.GetBuyOrder(41).Kind);
            Assert.AreEqual(1001910u, MarketManager.GetBuyOrder(41).Wcid);
            Assert.AreEqual(MarketBuyOrderKind.SalvageBag, MarketManager.GetBuyOrder(42).Kind,
                "a row written before order_Kind existed is a bag order");
        }

        /// <summary>
        /// order_Kind is a tinyint the database will hold 2 in - a hand edit, or a row written by a
        /// newer server and then rolled back. FromRow must CLAMP it to a defined member rather than
        /// cast it through.
        ///
        /// The damage of not clamping is not the fill, which is already safe (MatchesOrder's default
        /// matches nothing). It is the FEED: Dto hands the enum to JsonStringEnumConverter, which
        /// emits a NUMBER when no name matches, so /v1/orders/changes would answer "kind": 2 where
        /// the schema declares a required string - and a strictly typed client throws away the whole
        /// page, not the one bad row. Asserting the value is a DEFINED member is therefore the test,
        /// not merely asserting it equals SalvageBag.
        /// </summary>
        [TestMethod]
        public void LoadOrders_AnUndefinedKindOnTheRow_IsClampedToADefinedMember()
        {
            MarketManager.Shutdown();

            var now = DateTime.UtcNow;
            repo.BuyOrders.Add(new ShardMarketBuyOrder
            {
                Id = 43, BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "Marketbuyer",
                MaterialType = Tourmaline, OrderKind = 2, Wcid = 21082,
                PriceMmd = 10, CountTotal = 1, CountRemaining = 1, EscrowMmd = 10,
                Status = (int)MarketBuyOrderStatus.Active, ActiveMaterial = Tourmaline,
                CreatedAt = now, ExpiresAt = now.AddDays(30),
            });

            MarketManager.Initialize(store, wallet, repo);

            var order = MarketManager.GetBuyOrder(43);

            Assert.IsNotNull(order, "the row must still load; a bad kind is not a reason to drop an order holding escrow");
            CollectionAssert.Contains(Enum.GetValues(typeof(MarketBuyOrderKind)).Cast<MarketBuyOrderKind>().ToList(), order.Kind,
                $"kind {(int)order.Kind} is not a defined MarketBuyOrderKind, so the feed would serve it as a NUMBER");
            Assert.AreEqual(MarketBuyOrderKind.SalvageBag, order.Kind, "and the clamp lands on the restrictive kind");
        }

        /// <summary>
        /// Failed means the buyer PROVABLY was not charged and nothing is held, so the row must carry
        /// zero escrow and release its key. A row left Pending here would be refunded again at boot.
        /// </summary>
        [TestMethod]
        public void Place_DebitRefused_MarksFailed_AndHoldsNothing()
        {
            wallet.FailDebitCharacters.Add(BuyerCharacter);

            var result = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);

            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Failed, row.Status);
            Assert.AreEqual(5, row.Status, "the persisted value IS the column (WANTED-DESIGN 5.3)");
            Assert.AreEqual(0, row.EscrowMmd);
            Assert.IsNull(row.ActiveMaterial);
            Assert.IsNotNull(row.ClosedAt);

            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter), "nothing left the buyer's pool");
            Assert.IsNull(MarketManager.GetBuyOrder(row.Id), "and no order reached the index");
        }

        /// <summary>
        /// The one status under which a buyer may have paid and holds nothing. NOT Failed, for exactly
        /// the reason MarketTransactionStatus.DebitLedgerUnknown is not: it is reconciled by hand.
        /// </summary>
        [TestMethod]
        public void Place_LedgerUnknown_MarksDebitLedgerUnknown()
        {
            wallet.LedgerUnknownDebitCharacters.Add(BuyerCharacter);

            var result = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.LedgerUnknown, result.Error);

            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.DebitLedgerUnknown, row.Status);
            Assert.AreEqual(6, row.Status);
            Assert.AreNotEqual((int)MarketBuyOrderStatus.Failed, row.Status,
                "recording this as Failed would bury the only rows where a buyer can be out of pocket");
            Assert.AreEqual(0, row.EscrowMmd);
            Assert.IsNull(row.ActiveMaterial);
        }

        /// <summary>
        /// The Pending row is written BEFORE any value moves, so a refused insert must cost the buyer
        /// nothing at all. A debit before the row is money with no record to recover it from.
        /// </summary>
        [TestMethod]
        public void Place_PendingRowWriteFails_RefusesServerError_AndDebitsNothing()
        {
            repo.FailAddBuyOrder = true;

            var result = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.ServerError, result.Error);
            Assert.AreEqual(1, repo.AddBuyOrderCalls, "the insert was attempted");
            Assert.AreEqual(0, repo.BuyOrders.Count, "and wrote nothing");
            Assert.AreEqual(0, wallet.DebitCalls, "no debit may follow a Pending row that does not exist");
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));
        }

        [TestMethod]
        public void Place_WithTheSwitchOff_RefusesBuyOrdersDisabled()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", false));

            try
            {
                var result = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

                Assert.IsFalse(result.Ok);
                Assert.AreEqual(MarketError.BuyOrdersDisabled, result.Error,
                    "the market itself is up, so this is the order switch and not Disabled");
                Assert.AreEqual(0, repo.AddBuyOrderCalls);
                Assert.AreEqual(0, wallet.DebitCalls);
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", true));
            }
        }

        // ---- cancel (WANTED-DESIGN 6.3) ----

        [TestMethod]
        public void Cancel_RefundsExactlyTheRemainingEscrow_AndFlipsStatusFirst()
        {
            var order = Place(count: 5, price: 10);
            repo.FailUpdateBuyOrder = false;
            var result = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(MarketBuyOrderStatus.Cancelled, result.Value.Status);
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));
            var row = repo.BuyOrders.Single();
            Assert.AreEqual(0, row.EscrowMmd);
            Assert.IsNull(row.ActiveMaterial);
        }

        [TestMethod]
        public void Cancel_ByAnotherAccount_IsRefusedNotOwner_AndRefundsNothing()
        {
            var order = Place(count: 5, price: 10);

            var result = MarketManager.CancelOrder(Seller, order.Id, MarketChannel.Web);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NotOwner, result.Error);
            Assert.AreEqual(MarketBuyOrderStatus.Active, MarketManager.GetBuyOrder(order.Id).Status);
            Assert.AreEqual(50, MarketManager.GetBuyOrder(order.Id).EscrowMmd);
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter), "the owner keeps their escrow held");
            Assert.AreEqual(0, wallet.GetBalanceMmd(SellerCharacter), "and nobody else is paid it");
            Assert.AreEqual(0, wallet.CreditCalls);
        }

        /// <summary>
        /// WANTED-DESIGN 6.3: allowed with the kill switch OFF. A player must always be able to get
        /// their escrow back, whatever state the operator has put the market in.
        /// </summary>
        [TestMethod]
        public void Cancel_WithTheSwitchOff_StillSucceeds()
        {
            var order = Place(count: 5, price: 10);

            Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", false));

            try
            {
                var result = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

                Assert.IsTrue(result.Ok, $"cancel must survive the kill switch, got {result.Error}");
                Assert.AreEqual(MarketBuyOrderStatus.Cancelled, result.Value.Status);
                Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", true));
            }
        }

        /// <summary>
        /// The status flips BEFORE the refund, so a lost refund leaves a CLOSED row still carrying its
        /// escrow - one query finds every one of them. The forbidden shape is the opposite: an Active
        /// order whose money has already gone back, which reads as normal and pays out twice.
        /// </summary>
        [TestMethod]
        public void Cancel_WhenTheRefundFails_LeavesEscrowOnTheClosedRowAsTheMarker()
        {
            var order = Place(count: 5, price: 10);
            wallet.FailCreditCharacters.Add(BuyerCharacter);

            var result = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

            Assert.IsTrue(result.Ok, "the close itself succeeded; only the money move failed");

            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Cancelled, row.Status, "the status flipped first");
            Assert.AreEqual(50, row.EscrowMmd, "the un-refunded escrow stays on the row as the by-hand marker");
            Assert.IsNull(row.ActiveMaterial);
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter), "and the money really did not come back");
        }

        /// <summary>
        /// A close whose row cannot be persisted must REFUSE, not refund anyway. Refunding over a row
        /// that is still Active on disk creates money: a restart reloads it as Active, and it can then
        /// be cancelled for a second refund or filled against escrow the buyer already has back. That
        /// is precisely the state WANTED-DESIGN 6.3 flips the status before the credit to prevent, so
        /// the invariant, not the step list, decides what happens when the persist fails.
        ///
        /// The retry half is the discriminating half: it proves the rolled-back order is genuinely
        /// usable again rather than wedged half-closed in memory.
        /// </summary>
        [TestMethod]
        public void Cancel_WhenTheRowCannotBePersisted_RefusesAndRefundsNothing_ThenSucceedsOnRetry()
        {
            var order = Place(count: 5, price: 10);

            repo.FailUpdateBuyOrder = true;

            var refused = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

            Assert.IsFalse(refused.Ok, "a cancel that cannot be written down must not report success");
            Assert.AreEqual(MarketError.ServerError, refused.Error);
            Assert.AreNotEqual(MarketError.OrderBusy, refused.Error, "and must stay distinguishable from a busy order");

            Assert.AreEqual(0, wallet.CreditCalls, "no refund may be paid over a row that is still Active on disk");
            Assert.AreEqual(950, wallet.GetBalanceMmd(BuyerCharacter));

            var stillLive = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, stillLive.Status, "the in-memory flip was rolled back");
            Assert.IsNull(stillLive.ClosedAt, "including the close timestamp");
            Assert.AreEqual(50, stillLive.EscrowMmd, "and the escrow it still holds");

            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Active, row.Status, "the row on disk never moved");
            Assert.AreEqual(Granite, row.ActiveMaterial, "so it still holds its uniqueness key");
            Assert.AreEqual(50, row.EscrowMmd);

            Assert.AreEqual(MarketBuyOrderStatus.Active, MarketManager.GetOrderChanges(0, 10, out _).Single().Status,
                "and the feed must not be left telling consumers it closed");

            // The retry is what proves the rollback left the order usable rather than wedged.
            repo.FailUpdateBuyOrder = false;

            var retried = MarketManager.CancelOrder(Buyer, order.Id, MarketChannel.Web);

            Assert.IsTrue(retried.Ok, $"the retry must succeed, got {retried.Error}");
            Assert.AreEqual(MarketBuyOrderStatus.Cancelled, retried.Value.Status);
            Assert.AreEqual(1, wallet.CreditCalls, "refunded exactly once across both attempts");
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));

            var closedRow = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Cancelled, closedRow.Status);
            Assert.AreEqual(0, closedRow.EscrowMmd);
            Assert.IsNull(closedRow.ActiveMaterial);
        }

        // ---- expiry (WANTED-DESIGN 6.4) ----

        [TestMethod]
        public void Expire_ClosesOnlyPastDueOrders_AndRefundsThem()
        {
            var due = Place(count: 2, price: 10);                                   // expires in 30 days

            // A second order that expires LATER, so the pass has something it must leave alone: a
            // different account and material (uniqueness), placed under a 60-day lifetime.
            wallet.Seed(OtherCharacter, 100);
            Assert.IsTrue(PropertyManager.ModifyLong("market_buy_order_max_days", 60));

            // The restore has to happen before ExpireOrders runs, so only the placement is wrapped;
            // the finally is what keeps a throw in here from leaving 60 behind for the next test.
            MarketBuyOrder later = null;

            try
            {
                later = MarketManager.PlaceOrder(Other, (int)MaterialType.Iron, MarketBuyOrderKind.SalvageBag, 2, 10, MarketChannel.Web).Value;
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyLong("market_buy_order_max_days", 30));
            }

            var expired = MarketManager.ExpireOrders(due.ExpiresAt.AddSeconds(1));

            Assert.AreEqual(1, expired);
            Assert.AreEqual(MarketBuyOrderStatus.Expired, MarketManager.GetBuyOrder(due.Id).Status);
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter), "the expired order's escrow came back");
            Assert.AreEqual(MarketBuyOrderStatus.Active, MarketManager.GetBuyOrder(later.Id).Status, "a later order is untouched");
            Assert.AreEqual(80, wallet.GetBalanceMmd(OtherCharacter), "and its escrow is still held");
        }

        /// <summary>
        /// The shutdown race, which the ledger once called benign and is not.
        ///
        /// BE PRECISE ABOUT WHAT THE GUARD BUYS, because Shutdown cannot leave this state at rest: it
        /// nulls the seams and clears the index in ONE indexLock acquisition, and `enabled` is volatile.
        /// The reachable fault is a TORN READ. ExpireOrders reads `enabled`, `repository` and `wallet`
        /// WITHOUT the lock, so a pass can sample `enabled` as true just before Shutdown takes the lock
        /// and then read `repository` after it has been nulled - and Program.cs makes that reachable by
        /// stopping the expiry pass with a bounded 5 second join against a 30 second tick and then
        /// calling Shutdown regardless. CloseOrder then flips an order to Expired, persists it, and
        /// fails on the refund.
        ///
        /// What makes that worse than a lost refund is that nothing retries it. The row is left Expired
        /// carrying non-zero escrow - the design's REFUND LOST marker, so one query finds it - but
        /// ExpireOrders selects ACTIVE orders only, so no later pass ever looks at it again.
        ///
        /// The test stages the torn read directly, since it cannot be raced deterministically: it nulls
        /// the seams while leaving `enabled` TRUE, which is the half of the read that went stale.
        /// Clearing `enabled` is what the pre-existing guard already catches, so a test that cleared it
        /// would pass without the seam check and prove nothing.
        /// </summary>
        [TestMethod]
        public void Expire_WhenTheMarketSeamsAreAlreadyGone_ExpiresNothing()
        {
            var order = Place(count: 2, price: 10);
            var due = order.ExpiresAt.AddSeconds(1);

            var walletField = typeof(MarketManager).GetField("wallet", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(walletField, "MarketManager.wallet was not found by reflection - has it been renamed?");
            var repoField = typeof(MarketManager).GetField("repository", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(repoField, "MarketManager.repository was not found by reflection - has it been renamed?");

            var savedWallet = walletField.GetValue(null);
            var savedRepo = repoField.GetValue(null);

            try
            {
                walletField.SetValue(null, null);
                repoField.SetValue(null, null);

                Assert.AreEqual(0, MarketManager.ExpireOrders(due), "a pass that outlived Shutdown must expire nothing");
            }
            finally
            {
                walletField.SetValue(null, savedWallet);
                repoField.SetValue(null, savedRepo);
            }

            var row = repo.BuyOrders.Single();
            Assert.AreEqual((int)MarketBuyOrderStatus.Active, row.Status,
                "the row must not be left Expired with escrow on it, because no later pass would retry it");
            Assert.AreEqual(20, row.EscrowMmd);
            Assert.AreEqual(Granite, row.ActiveMaterial);
            Assert.AreEqual(0, wallet.CreditCalls);
            Assert.AreEqual(MarketBuyOrderStatus.Active, MarketManager.GetBuyOrder(order.Id).Status,
                "and the in-memory order is not left half-closed either");
        }

        // ---- boot recovery (WANTED-DESIGN 6.5) ----

        [TestMethod]
        public void Recovery_RefundsPendingOrders_AndMarksThemFailed()
        {
            // Seed a Pending row directly, as a crash between steps 1 and 2 would leave it.
            repo.BuyOrders.Add(new ShardMarketBuyOrder { BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "Marketbuyer",
                MaterialType = Granite, Wcid = 21013, PriceMmd = 10, CountTotal = 5, CountRemaining = 5, EscrowMmd = 0, Status = 0,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5), ExpiresAt = DateTime.UtcNow.AddDays(30) });
            repo.BuyOrders[0].Id = 77;
            MarketManager.Initialize(store, wallet, repo);   // reload the index with the seeded row

            MarketManager.RecoverPendingOrders(1000);

            Assert.AreEqual(1050, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual((int)MarketBuyOrderStatus.Failed, repo.BuyOrders.Single().Status);
        }

        /// <summary>
        /// A NULL read means the read FAILED, and recovery is SKIPPED rather than concluding there is
        /// nothing pending. Treating null as empty would leave every interrupted placement unrefunded
        /// and silently mark the pass a success.
        /// </summary>
        [TestMethod]
        public void Recovery_NullRead_SkipsEverything()
        {
            repo.BuyOrders.Add(new ShardMarketBuyOrder { Id = 78, BuyerAccountId = BuyerAccount, BuyerCharacterGuid = BuyerCharacter, BuyerCharacterName = "Marketbuyer",
                MaterialType = Granite, Wcid = 21013, PriceMmd = 10, CountTotal = 5, CountRemaining = 5, EscrowMmd = 0, Status = 0,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5), ExpiresAt = DateTime.UtcNow.AddDays(30) });

            repo.FailPendingOrderRead = true;

            MarketManager.RecoverPendingOrders(1000);

            Assert.AreEqual(0, wallet.CreditCalls, "a failed read must not be read as nothing to do");
            Assert.AreEqual(1000, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(0, repo.BuyOrders.Single().Status, "and the row stays Pending for a later successful pass");
        }

        /// <summary>
        /// WANTED-DESIGN 6.5: a failed order read does NOT disable the market the way a failed listing
        /// read does. It refuses place and fill instead, because an empty order index cannot be told
        /// from a real one - and that refusal, not the UNIQUE key, is what makes the empty index safe:
        /// the key cannot refuse a duplicate placement before the buyer has been debited (5.1).
        /// </summary>
        [TestMethod]
        public void Boot_FailedOrderRead_LeavesTheMarketUp_ButPlaceRefusesServerError()
        {
            repo.FailBuyOrderRead = true;

            MarketManager.Initialize(store, wallet, repo);

            Assert.IsTrue(MarketManager.Enabled, "the market must stay UP: listings and purchases are unaffected");

            var placed = MarketManager.PlaceOrder(Buyer, Granite, MarketBuyOrderKind.SalvageBag, 5, 10, MarketChannel.Web);

            Assert.IsFalse(placed.Ok);
            Assert.AreEqual(MarketError.ServerError, placed.Error);
            Assert.AreEqual(0, repo.AddBuyOrderCalls);
            Assert.AreEqual(0, wallet.DebitCalls);

            var filled = MarketManager.FillOrder(Seller, 1, 1, MarketChannel.Web);

            Assert.IsFalse(filled.Ok);
            Assert.AreEqual(MarketError.ServerError, filled.Error);

            // The rest of the market is untouched, which is the whole point of not disabling it.
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var listed = MarketManager.List(Seller, item.Guid.Full, 9001, 1, 10, MarketChannel.InGame);

            Assert.IsTrue(listed.Ok, $"listing must still work with the order index down, got {listed.Error}");
        }

        /// <summary>
        /// The orders feed shares GetListingChanges' cursor contract: nextSeq is the LAST ROW RETURNED,
        /// never the global sequence, or a page smaller than the backlog would skip everything after it.
        /// And the feed hands back COPIES with InFlight left at zero - that counter is in-memory
        /// reservation state, never on the wire.
        /// </summary>
        [TestMethod]
        public void Feed_GetOrderChanges_PagesByLastReturnedSeq_AndNeverCopiesInFlight()
        {
            var first = Place(count: 5, price: 10);

            wallet.Seed(OtherCharacter, 100);
            var second = MarketManager.PlaceOrder(Other, (int)MaterialType.Iron, MarketBuyOrderKind.SalvageBag, 2, 10, MarketChannel.Web);
            Assert.IsTrue(second.Ok, $"seeding the second order failed with {second.Error}");

            var pageOne = MarketManager.GetOrderChanges(0, 1, out var nextSeq);

            Assert.AreEqual(1, pageOne.Count);
            Assert.AreEqual(first.Id, pageOne[0].Id);
            Assert.AreEqual(pageOne[0].Seq, nextSeq, "the cursor is the last row returned, not the global sequence");

            var pageTwo = MarketManager.GetOrderChanges(nextSeq, 10, out var afterTwo);

            Assert.AreEqual(1, pageTwo.Count);
            Assert.AreEqual(second.Value.Id, pageTwo[0].Id, "the second page must not skip a row");
            Assert.AreEqual(pageTwo[0].Seq, afterTwo);

            Assert.AreEqual(0, MarketManager.GetOrderChanges(afterTwo, 10, out _).Count, "and then the feed is drained");

            // InFlight lives only on the LIVE index object, which no public accessor exposes. Reaching
            // it directly is the only way to prove the copy leaves it behind rather than merely that it
            // happens to be zero.
            var field = typeof(MarketManager).GetField("buyOrders", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "MarketManager.buyOrders was not found by reflection - has it been renamed?");

            var index = (Dictionary<uint, MarketBuyOrder>)field.GetValue(null);
            index[first.Id].InFlight = 3;

            try
            {
                Assert.AreEqual(3, index[first.Id].InFlight, "the live order really is carrying a reservation");
                Assert.AreEqual(0, MarketManager.GetBuyOrder(first.Id).InFlight, "GetBuyOrder hands back a copy without it");
                Assert.AreEqual(0, MarketManager.GetOrderChanges(0, 10, out _).Single(o => o.Id == first.Id).InFlight,
                    "and so does the feed");
                Assert.AreEqual(0, MarketManager.GetActiveBuyOrdersForAccount(BuyerAccount).Single().InFlight,
                    "and the per-account list");
            }
            finally
            {
                index[first.Id].InFlight = 0;
            }
        }
    }
}

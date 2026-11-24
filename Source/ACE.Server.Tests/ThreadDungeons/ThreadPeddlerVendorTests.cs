using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ThreadPeddlerVendor, the clear-only Wandering Peddler's native vendor window: sells refused, listings are disposable
    /// displays parented to the peddler, the shown price is the charged price, the per-viewer stall (only affordable offers,
    /// only for an open shop), the real-pile balance, buys built only from the listing and handed over at once, the "Finished trading?"
    /// question, every done source (walk-away, Yes, idle, logout, /spawncache, departure) releasing exactly once, and
    /// departure after the last armed member.
    ///
    /// Constructing any Vendor needs GameTables, vendor_shop_uses_generator cached, and the currency weenie in the world
    /// cache (SalvageRollerVendorTests.SeedVendorConstructible, plus the Cache Pledge seeded here). The one PropertyManager
    /// key that seeds is restored to its shipped default in TestCleanup, so the class passes alone. Every other seam is
    /// per peddler instance (Clock, InsideCheck, RangeCheck, PlayerLookup, FinishedAsk, Told, Approach), except
    /// ThreadPeddlerShop.RequestPlacement, which every test that releases swaps and restores in a finally.
    /// </summary>
    [TestClass]
    public class ThreadPeddlerVendorTests
    {
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";
        internal const uint PledgeWcid = 1007982;

        private static uint nextGuid = 0x7EC40000;

        private static uint NextObjectGuid() => ++nextGuid;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<lastPlayerInfo>k__BackingField")]
        private static extern ref WorldObjectInfo LastPlayerInfoField(Vendor vendor);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "confirmations")]
        private static extern ref ConcurrentDictionary<ConfirmationType, Confirmation> ConfirmationsField(ACE.Server.WorldObjects.Managers.ConfirmationManager manager);

        /// <summary>Puts <paramref name="confirmation"/> in the player's pending slot, as EnqueueSend would, without a session.</summary>
        internal static void Pend(Player player, Confirmation confirmation)
        {
            player.ConfirmationManager ??= new ACE.Server.WorldObjects.Managers.ConfirmationManager(player);
            Assert.IsTrue(ConfirmationsField(player.ConfirmationManager).TryAdd(confirmation.ConfirmationType, confirmation), "fixture: pending");
        }

        [ClassInitialize]
        public static void PrimePlayerType(TestContext _)
        {
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Setup()
        {
            SeedConstructible();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        // ======================================================================================
        // fixtures
        // ======================================================================================

        internal static void SeedConstructible()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();
            SeedWorldWeenie(PledgeWcid, WeenieType.Generic);
        }

        internal static void SeedWorldWeenie(uint wcid, WeenieType type)
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);

            if (!dict.ContainsKey(wcid))
                dict[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = type };
        }

        /// <summary>The shipped peddler weenie's vendor shape: type 10, bool 9096, AlternateCurrency 1007982, SellPrice 1.0, buys nothing.</summary>
        internal static Weenie PeddlerWeenie() => new Weenie
        {
            WeenieClassId = 1007981,
            WeenieType = WeenieType.Creature,
            PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.ThreadPeddlerVendor, true } },
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.MerchandiseItemTypes, 0 },
                { PropertyInt.MerchandiseMinValue, 0 },
                { PropertyInt.MerchandiseMaxValue, 0 },
            },
            PropertiesFloat = new Dictionary<PropertyFloat, double>
            {
                { PropertyFloat.BuyPrice, 0 },
                { PropertyFloat.SellPrice, 1.0 },
            },
            PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.AlternateCurrency, PledgeWcid } },
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Wandering Peddler" } },
        };

        internal static ThreadPeddlerVendor NewPeddler()
        {
            var peddler = (ThreadPeddlerVendor)WorldObjectFactory.CreateWorldObject(PeddlerWeenie(), new ObjectGuid(NextObjectGuid()));
            Assert.IsTrue(peddler.OpenForBusiness, "fixture: every vendor row and the pledge weenie are present");
            return peddler;
        }

        /// <summary>A test clock the peddler reads.</summary>
        internal sealed class FakeClock
        {
            public double Now = 1_000_000;
        }

        /// <summary>The three listings every buy test uses: notes (50), plain (240) and a Pattern for <see cref="Pinnacle"/> (320).</summary>
        internal sealed class Stall
        {
            public ThreadPeddlerVendor Peddler;
            public ThreadDungeonRun Run;
            public FakeClock Clock;
            public WorldObject Notes, Plain, Pattern;
            public List<WorldObject> Displays;
            public readonly Dictionary<uint, Player> Players = new Dictionary<uint, Player>();
            public readonly HashSet<uint> InRange = new HashSet<uint>();
            public readonly HashSet<uint> Outside = new HashSet<uint>();
            public readonly List<string> Told = new List<string>();
            public readonly List<string> Asked = new List<string>();
            public readonly List<(uint Guid, VendorType Type)> Panels = new List<(uint, VendorType)>();

            /// <summary>Each buyer's pack (the receiver seam), made on first use: roomy unless a test shrinks it.</summary>
            public readonly ConcurrentDictionary<uint, FakePack> Packs = new ConcurrentDictionary<uint, FakePack>();

            /// <summary>Every object the builder seam built, with the record it was built for.</summary>
            public readonly ConcurrentDictionary<WorldObject, PeddlerPurchase> RecordOf = new ConcurrentDictionary<WorldObject, PeddlerPurchase>();

            /// <summary>The run's owner: the solo buyer (a deal seat).</summary>
            public uint Buyer => Run.OwnerGuid;

            public Player P(uint guid) => Players.TryGetValue(guid, out var p) ? p : Players[guid] = Viewer(guid);

            public FakePack Pack(uint guid) => Packs.GetOrAdd(guid, _ => new FakePack());

            /// <summary>The records whose objects reached this buyer's pack, in hand-over order.</summary>
            public List<PeddlerPurchase> BoughtBy(uint guid) => Pack(guid).GivenSnapshot().Select(o => RecordOf[o]).ToList();
        }

        /// <summary>A buyer's pack for the receiver seam: capacity and an accept rule, recording every object it takes.</summary>
        internal sealed class FakePack
        {
            public int FreeInventorySlots = 10_000;
            public int FreeContainerSlots = 100;
            public long AvailableBurden = long.MaxValue;
            public Func<WorldObject, bool> Accept = _ => true;
            private readonly List<WorldObject> given = new List<WorldObject>();

            public List<WorldObject> GivenSnapshot() { lock (given) return given.ToList(); }

            public ThreadPeddlerVendor.PeddlerReceiver Receiver() => new ThreadPeddlerVendor.PeddlerReceiver
            {
                FreeInventorySlots = FreeInventorySlots,
                FreeContainerSlots = FreeContainerSlots,
                AvailableBurden = AvailableBurden,
                Give = o =>
                {
                    if (!Accept(o))
                        return false;

                    lock (given)
                        given.Add(o);

                    return true;
                },
            };
        }

        /// <summary>The builder seam the stalls use: one Generic object per record, of the listing's wcid.</summary>
        internal static Func<ThreadDungeonRun, PeddlerPurchase, Random, List<WorldObject>> StallBuilder(Stall stall) => (r, p, rng) =>
        {
            var wcid = p.EntryId == NoteEntry.Id ? NoteWcid : p.EntryId == PatternEntry.Id ? ACE.Server.Knots.KnotItems.PatternWcid : PlainWcid;
            var o = Generic(wcid);
            o.Name = p.EntryId;
            stall.RecordOf[o] = p;
            return new List<WorldObject> { o };
        };

        /// <summary>
        /// A bound stall on a run that HAS a peddler (the plan), with every seam wired to the stall's own sets: a player is
        /// online once <see cref="Stall.P"/> made them, inside unless in <see cref="Stall.Outside"/>, in range when in
        /// <see cref="Stall.InRange"/>. Not cleared, nobody armed.
        /// </summary>
        internal static Stall BoundStall(ThreadDungeonRun run = null, double idle = 120, double walkAway = 5)
        {
            var stall = new Stall { Run = run ?? ActiveSoloRun(), Clock = new FakeClock(), Peddler = NewPeddler() };

            if (stall.Run.PeddlerPlan == null && stall.Run.State == ThreadDungeonRunState.Active)
                GivePlan(stall.Run, TestPlan(50, 240, 320));

            stall.Peddler.Clock = () => stall.Clock.Now;
            stall.Peddler.InsideCheck = (r, p) => !stall.Outside.Contains(p.Guid.Full);
            stall.Peddler.RangeCheck = p => stall.InRange.Contains(p.Guid.Full);
            stall.Peddler.PlayerLookup = g => stall.Players.TryGetValue(g, out var p) ? p : null;
            stall.Peddler.Told = (p, t) => stall.Told.Add(t);
            stall.Peddler.FinishedAsk = (p, t) => { stall.Asked.Add(t); return true; };
            stall.Peddler.Approach = (p, t, spent) => stall.Panels.Add((p.Guid.Full, t));
            stall.Peddler.Builder = StallBuilder(stall);
            stall.Peddler.ReceiverOf = p => p == null ? null : stall.Pack(p.Guid.Full).Receiver();

            stall.Notes = Generic(NoteWcid);
            stall.Plain = Generic(PlainWcid);
            stall.Pattern = Generic(ACE.Server.Knots.KnotItems.PatternWcid);
            stall.Notes.Name = "Promissory Note x5";
            stall.Plain.Name = "Rear Guard Mantle";
            stall.Pattern.Name = "Pattern: P One";
            stall.Displays = new List<WorldObject> { stall.Notes, stall.Plain, stall.Pattern };

            var bound = stall.Peddler.Bind(stall.Run, idle, walkAway, new (WorldObject, PeddlerPurchase)[]
            {
                (stall.Notes, new PeddlerPurchase(NoteEntry.Id, 50)),
                (stall.Plain, new PeddlerPurchase(PlainEntry.Id, 240)),
                (stall.Pattern, new PeddlerPurchase(PatternEntry.Id, 320, Pinnacle)),
            });

            Assert.AreEqual(3, bound, "fixture: three listings");
            return stall;
        }

        /// <summary>
        /// A stall at the clear: the run cleared, every <paramref name="members"/> (default: the owner) given
        /// <paramref name="plain"/> ordinary pile items, their shops opened and armed on the peddler, each online and in range.
        /// </summary>
        internal static Stall ArmedStall(int plain = 400, ThreadDungeonRun run = null, params uint[] members)
            => ArmedStallWith(120, 5, plain, run, members);

        internal static Stall ArmedStallWith(double idle, double walkAway, int plain = 400, ThreadDungeonRun run = null, params uint[] members)
        {
            var stall = BoundStall(run, idle, walkAway);
            var guids = members.Length == 0 ? new[] { stall.Run.OwnerGuid } : members;

            ClearWithPiles(stall.Run, plain, guids);

            foreach (var guid in guids)
            {
                OpenShop(stall.Run, guid);
                Assert.IsTrue(stall.Peddler.Arm(guid), "fixture: armed");
                stall.P(guid);
                stall.InRange.Add(guid);
            }

            return stall;
        }

        internal static Player Viewer(uint guid)
        {
            var player = VendorAlternateCurrencyBalanceTests.BareViewer();
            GuidField(player) = new ObjectGuid(guid);
            return player;
        }

        /// <summary>Runs <paramref name="body"/> with ThreadPeddlerShop.RequestPlacement recording each release's placement request (thread-safe); restored after.</summary>
        internal static void WithReleases(Action<ConcurrentQueue<uint>> body, CacheRequestOutcome outcome = CacheRequestOutcome.Queued)
        {
            var requests = new ConcurrentQueue<uint>();
            var original = ThreadPeddlerShop.RequestPlacement;

            try
            {
                ThreadPeddlerShop.RequestPlacement = (r, g) => { requests.Enqueue(g); return outcome; };
                body(requests);
            }
            finally
            {
                ThreadPeddlerShop.RequestPlacement = original;
            }
        }

        private static List<ItemProfile> One(WorldObject display, int amount = 1) => new List<ItemProfile> { new ItemProfile(amount, display.Guid.Full) };

        // ======================================================================================
        // sells
        // ======================================================================================

        [TestMethod]
        public void Sells_AreRefused_OnABoundPeddler()
        {
            var stall = BoundStall();
            var viewer = Viewer(stall.Buyer);
            var item = Generic(PlainWcid);

            Assert.IsFalse(stall.Peddler.CanAccept(item, viewer, out var reason));
            Assert.AreEqual("The Wandering Peddler buys nothing.", reason);
            Assert.IsFalse(stall.Peddler.CanAccept(item, viewer, ACE.Server.Entity.AccountVault.VaultAccess.DepositWithdraw, out reason));
            Assert.AreEqual(ThreadPeddlerVendor.BuysNothingMessage, reason);
            Assert.IsFalse(stall.Peddler.TryValidateSellBasket(viewer, new List<WorldObject> { item }, new List<WorldObject> { item }, out reason));
            Assert.AreEqual(ThreadPeddlerVendor.BuysNothingMessage, reason);
            Assert.AreEqual(0, stall.Peddler.CalculatePayoutCoinAmount(new Dictionary<uint, WorldObject> { { item.Guid.Full, item } }));
            Assert.AreEqual(0, stall.Peddler.UniqueItemsForSale.Count);
        }

        // ======================================================================================
        // listings
        // ======================================================================================

        [TestMethod]
        public void Listings_AreDisposableDisplays_ParentedToThePeddler_WithUnlimitedSupply()
        {
            var stall = BoundStall();

            foreach (var display in stall.Displays)
            {
                Assert.IsFalse(display.BiotaOriginatedFromOrHasBeenSavedToDatabase(), $"{display.Name}: a listing is never a saved object");
                Assert.AreEqual(stall.Peddler.Guid.Full, display.ContainerId, $"{display.Name}: parented to the peddler");
                Assert.AreEqual(-1, display.VendorShopCreateListStackSize, $"{display.Name}: unlimited supply");
            }

            CollectionAssert.AreEquivalent(stall.Peddler.Listings.Keys.ToList(), stall.Peddler.DefaultItemsForSale.Keys.ToList(), "the listing map and the vendor registry hold exactly the same displays");
            CollectionAssert.AreEquivalent(stall.Displays.Select(d => d.Guid).ToList(), stall.Peddler.DefaultItemsForSale.Keys.ToList());
            Assert.AreEqual(0, stall.Peddler.ItemsForSaleCount, "the viewer-less count fails closed");
            Assert.AreEqual(0, stall.Peddler.GetItemsForSaleCount(Viewer(stall.Buyer)), "no open shop, nothing shown");
        }

        [TestMethod]
        public void Bind_RefusesADisplayInsideAnotherContainer_AndLeavesItAlone()
        {
            var peddler = NewPeddler();
            var foreign = Generic(PlainWcid);
            foreign.ContainerId = 0x50001234u;
            var ok = Generic(NoteWcid);

            var bound = peddler.Bind(ActiveSoloRun(), 120, 5, new (WorldObject, PeddlerPurchase)[]
            {
                (foreign, new PeddlerPurchase(PlainEntry.Id, 240)),
                (ok, new PeddlerPurchase(NoteEntry.Id, 50)),
            });

            Assert.AreEqual(1, bound);
            Assert.IsFalse(peddler.DefaultItemsForSale.ContainsKey(foreign.Guid), "a real item somewhere else is never a listing");
            Assert.AreEqual(0x50001234u, foreign.ContainerId, "and is left exactly as it was");
            Assert.IsFalse(foreign.IsDestroyed);
            Assert.AreEqual(0, peddler.Bind(ActiveSoloRun(), 120, 5, new[] { (Generic(), new PeddlerPurchase("x", 1)) }), "a second Bind changes nothing");
        }

        [TestMethod]
        public void AddDefaultItem_IsRefused()
        {
            var stall = BoundStall();
            stall.Peddler.AddDefaultItem(Generic());

            Assert.AreEqual(3, stall.Peddler.DefaultItemsForSale.Count);
        }

        /// <summary>
        /// The client prices a listing as unit Value x SellPrice x quantity; the server's formula (Vendor.CalcSellCost,
        /// max(1, ceil(rate x value - 0.1))) must give back exactly the integer price for every price the shipped config can
        /// reach, at the shipped SellPrice (1.0) and the shell's ItemType (Misc). Control: a PromissoryNote-typed object is
        /// forced to 1.15, which is why such an item is always shown as a shell.
        /// </summary>
        [TestMethod]
        public void DisplayPrice_EqualsServerPrice_AcrossTheWholeReachableRange()
        {
            var config = PeddlerEncounterTypeTests.ShippedConfig(out _);
            var (min, max) = config.ReachablePriceRange();
            var peddler = NewPeddler();
            var rate = peddler.SellPrice ?? -1;

            Assert.AreEqual(1.0, rate, "the peddler's SellPrice is 1.0");
            Assert.IsTrue(min >= 1 && max >= 500, $"sanity: the shipped range is {min}..{max}");

            for (var price = 1; price <= max; price++)
                Assert.AreEqual((uint)price, Vendor.CalcSellCost(rate, price, ItemType.Misc, 1), $"price {price}");

            var parted = Enumerable.Range(min, max - min + 1).Count(p => Vendor.CalcSellCost(rate, p, ItemType.PromissoryNote, 1) != (uint)p);
            Assert.IsTrue(parted > 0, "control: a PromissoryNote-typed display would be charged something other than its price");
        }

        // ======================================================================================
        // the per-viewer stall and the balance
        // ======================================================================================

        [TestMethod]
        public void PerViewerStall_ShowsOnlyAffordableOffers_AndCountAndEnumerationAgree()
        {
            var run = ActiveGroupRun(Member);
            var stall = ArmedStall(100, run, Owner);
            AddPlain(run, Member, 300);
            OpenShop(run, Member);
            Assert.IsTrue(stall.Peddler.Arm(Member));

            var poor = Viewer(Owner);
            var rich = Viewer(Member);

            List<WorldObject> Shown(Player v)
            {
                var rows = new List<WorldObject>();
                stall.Peddler.ForEachItemFor(v, rows.Add);
                Assert.AreEqual(rows.Count, stall.Peddler.GetItemsForSaleCount(v), "the count and the enumeration agree");
                return rows;
            }

            CollectionAssert.AreEqual(new[] { stall.Notes }, Shown(poor), "100 items: only the 50 offer");
            CollectionAssert.AreEqual(new[] { stall.Notes, stall.Plain }, Shown(rich), "300 items: 50 and 240, never 320");

            Assert.IsTrue(stall.Peddler.TryGetItemForSale(rich, stall.Plain.Guid, out var found) && ReferenceEquals(found, stall.Plain));
            Assert.IsFalse(stall.Peddler.TryGetItemForSale(poor, stall.Plain.Guid, out _), "an offer the viewer is not shown does not resolve for them");

            // A buy re-filters: 300 - 240 leaves 60, so only the 50 offer remains.
            WithReleases(_ =>
            {
                Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Plain), rich));
                CollectionAssert.AreEqual(new[] { stall.Notes }, Shown(rich));
            });

            Assert.AreEqual(0, Shown(Viewer(0x59999999u)).Count, "a viewer without a shop sees nothing");

            Assert.IsTrue(run.TryMarkShopDone(Owner));
            Assert.AreEqual(0, Shown(poor).Count, "a done member sees nothing");
        }

        [TestMethod]
        public void TheBalanceShown_IsTheRealPile_ForThePledgeOnly()
        {
            var stall = ArmedStall(120);
            var viewer = Viewer(stall.Buyer);
            Assert.IsTrue(stall.Run.AddToHeldPile(stall.Buyer, new HeldPileItem(Generic(), null, 1, null, 0)), "a boss-bonus item is not currency");

            Assert.AreEqual(120, stall.Peddler.AlternateCurrencyBalanceFor(viewer, PledgeWcid), "the eligible items in the pile");
            Assert.AreEqual(stall.Run.PeddlerBalance(stall.Buyer), stall.Peddler.AlternateCurrencyBalanceFor(viewer, PledgeWcid), "read through the one real-pile function");
            Assert.AreEqual(0, stall.Peddler.AlternateCurrencyBalanceFor(viewer, 273), "any other currency reads 0");
            Assert.AreEqual(0, NewPeddler().AlternateCurrencyBalanceFor(viewer, PledgeWcid), "an unbound peddler shows 0");
            Assert.AreEqual(0, stall.Peddler.AlternateCurrencyBalanceFor(Viewer(0x59999999u), PledgeWcid), "no open shop shows 0");

            WithReleases(_ =>
            {
                Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), viewer));
                Assert.AreEqual(70, stall.Peddler.AlternateCurrencyBalanceFor(viewer, PledgeWcid), "minus the payment the buy took from the pile");
            });
        }

        // ======================================================================================
        // Use
        // ======================================================================================

        [TestMethod]
        public void Use_OpensThePanel_OnlyForAnArmedOpenShop_AndARepeatUseIsNotDone()
        {
            var stall = ArmedStall();
            var buyer = stall.P(stall.Buyer);

            stall.Peddler.ActOnUse(buyer);
            stall.Peddler.ActOnUse(buyer);

            Assert.AreEqual(2, stall.Panels.Count(p => p.Guid == stall.Buyer && p.Type == VendorType.Open), "every Use opens (or reopens) the panel");
            Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "a second Use is never a done signal");
            Assert.IsNull(LastPlayerInfoField(stall.Peddler), "never the retail single-customer path");
            Assert.AreEqual(stall.Clock.Now, stall.Peddler.ShopOf(stall.Buyer).LastUseAt);

            var stranger = stall.P(0x59999999u);
            stall.Peddler.ActOnUse(stranger);
            Assert.AreEqual(ThreadPeddlerVendor.NotTradingMessage, stall.Told.Last(), "a member the peddler was not presented to");

            stall.Outside.Add(0x59999999u);
            stall.Peddler.ActOnUse(stranger);
            Assert.AreEqual(ThreadPeddlerVendor.NonMemberMessage, stall.Told.Last());

            Assert.IsTrue(stall.Run.TryMarkShopDone(stall.Buyer));
            stall.Peddler.ActOnUse(buyer);
            Assert.AreEqual(ThreadPeddlerVendor.NotTradingMessage, stall.Told.Last(), "a done member is not served");
            Assert.AreEqual(2, stall.Panels.Count, "no panel for any refusal");
        }

        [TestMethod]
        public void UnboundPeddler_TradesNothing()
        {
            var peddler = NewPeddler();

            Assert.AreEqual(ThreadPeddlerVendor.UseOutcome.Closed, peddler.TryUse(Owner, true));
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Closed, peddler.TryBuy(Owner, true, new List<ItemProfile> { new ItemProfile(1, 1) }).Outcome);
            Assert.IsFalse(peddler.Arm(Owner), "nothing to arm on an unbound peddler");
        }

        // ======================================================================================
        // buying
        // ======================================================================================

        [TestMethod]
        public void Amount3_Buys3Records_FromTheListing()
        {
            var stall = ArmedStall();
            var before = stall.Run.PeddlerBalance(stall.Buyer);

            var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes, 3));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            Assert.AreEqual(150, result.Total);
            Assert.AreEqual("3 x Promissory Note x5", result.Label);

            var bought = stall.BoughtBy(stall.Buyer);
            Assert.AreEqual(3, bought.Count, "amount 3 is three records");
            Assert.IsTrue(bought.All(p => p.EntryId == NoteEntry.Id && p.Price == 50 && p.Pattern == null));
            Assert.AreEqual(before - 150, stall.Run.PeddlerBalance(stall.Buyer));
            Assert.AreEqual(3, stall.Peddler.DefaultItemsForSale.Count, "stock is never consumed");
        }

        [TestMethod]
        public void PatternListing_BuysItsPinnacle_AndCanBeBoughtAgain()
        {
            var stall = ArmedStall(700);

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Pattern)).Outcome);
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Pattern)).Outcome);

            var bought = stall.BoughtBy(stall.Buyer);
            Assert.AreEqual(2, bought.Count);
            Assert.IsTrue(bought.All(p => p.EntryId == PatternEntry.Id && p.Price == 320 && ReferenceEquals(p.Pattern, Pinnacle)));
        }

        [TestMethod]
        public void UnaffordableBasket_BuysNothing()
        {
            var stall = ArmedStall(100);

            var result = stall.Peddler.TryBuy(stall.Buyer, true, new List<ItemProfile> { new ItemProfile(1, stall.Notes.Guid.Full), new ItemProfile(1, stall.Plain.Guid.Full) });

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Insufficient, result.Outcome);
            Assert.AreEqual("You have not pledged enough of your Thread Cache for that.", ThreadPeddlerVendor.RefusalLine(result.Outcome));
            Assert.AreEqual(0, result.Records.Count, "refused before the records were expanded");
            Assert.AreEqual(0, stall.BoughtBy(stall.Buyer).Count, "the whole basket or nothing");
            Assert.AreEqual(100, stall.Run.PeddlerBalance(stall.Buyer));

            var huge = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes, int.MaxValue));
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Insufficient, huge.Outcome, "a huge amount is refused on the total, not by expanding it");
            Assert.AreEqual(0, huge.Records.Count);
        }

        /// <summary>The client sends a display guid, an amount, and (filled server side elsewhere) wcid/palette/shade: only the guid and amount are read.</summary>
        [TestMethod]
        public void ClientSuppliedWcidAndPalette_AreIgnored_RecordsComeFromTheListing()
        {
            var stall = ArmedStall();

            var forged = new ItemProfile(1, stall.Notes.Guid.Full) { WeenieClassId = ACE.Server.Knots.KnotItems.PatternWcid, Palette = 99, Shade = 0.5 };
            var result = stall.Peddler.TryBuy(stall.Buyer, true, new List<ItemProfile> { forged });

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, result.Outcome);
            var record = stall.BoughtBy(stall.Buyer).Single();
            Assert.AreEqual(NoteEntry.Id, record.EntryId, "the entry is the listing's, not the wcid the profile carried");
            Assert.AreEqual(50, record.Price, "the price is the listing's");

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Invalid, stall.Peddler.TryBuy(stall.Buyer, true, new List<ItemProfile> { new ItemProfile(1, 0x7FFFFFF0u) }).Outcome, "a guid that is no listing buys nothing");
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Invalid, stall.Peddler.TryBuy(stall.Buyer, true, new List<ItemProfile> { new ItemProfile(1, stall.Notes.Guid.Full), new ItemProfile(0, stall.Plain.Guid.Full) }).Outcome, "an amount below 1 refuses the whole basket");

            Assert.AreEqual(1, stall.BoughtBy(stall.Buyer).Count, "the refused baskets bought nothing");
        }

        [TestMethod]
        public void Buys_AreRefused_ForANonMember_ANonShopper_AfterDone_AndAfterDeparture()
        {
            var stall = ArmedStall();

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.NotMember, stall.Peddler.TryBuy(stall.Buyer, false, One(stall.Notes)).Outcome);
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.NotTrading, stall.Peddler.TryBuy(0x59999999u, true, One(stall.Notes)).Outcome, "never presented");
            Assert.AreEqual("The Wandering Peddler is not trading with you.", ThreadPeddlerVendor.RefusalLine(ThreadPeddlerVendor.BuyOutcome.NotTrading));

            Assert.IsTrue(stall.Run.TryMarkShopDone(stall.Buyer));
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.NotTrading, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes)).Outcome, "a buy after done is refused");

            var gone = ArmedStall();
            WithReleases(_ => gone.Peddler.Depart("test"));
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Closed, gone.Peddler.TryBuy(gone.Buyer, true, One(gone.Notes)).Outcome);
            Assert.AreEqual("The Wandering Peddler has packed up.", ThreadPeddlerVendor.RefusalLine(ThreadPeddlerVendor.BuyOutcome.Closed));
            Assert.AreEqual(0, stall.BoughtBy(stall.Buyer).Count + gone.BoughtBy(gone.Buyer).Count);
        }

        /// <summary>
        /// Owner ruling 2026-11-20 (stage run 0x8000643D closed after one buy): the Finished question goes out only once the
        /// buyer has nothing left to buy. 400 items: the 240 buy leaves 160 (the 50 offer still shown), so nothing is asked;
        /// three of the 50 leave 10, nothing affordable, so it is asked once.
        /// </summary>
        [TestMethod]
        public void BuyEntryPoint_Buys_RefreshesThePanel_AndAsksOnlyWhenNothingIsLeft()
        {
            var stall = ArmedStall();
            var buyer = stall.P(stall.Buyer);

            Assert.IsFalse(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Plain, 2), buyer), "480 is more than 400");
            Assert.AreEqual(ThreadPeddlerVendor.InsufficientMessage, stall.Told.Last());
            Assert.AreEqual(0, stall.Asked.Count, "no question after a refusal");

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Plain), buyer));
            CollectionAssert.AreEqual(new[] { PlainEntry.Id }, stall.BoughtBy(stall.Buyer).Select(p => p.EntryId).ToArray(), "the purchase is in the pack at the buy");
            Assert.IsTrue(stall.Run.EncounterOutcomes.Any(l => l.Contains("bought Rear Guard Mantle for 240")), string.Join("\n", stall.Run.EncounterOutcomes));
            Assert.AreEqual((stall.Buyer, VendorType.Buy), stall.Panels.Last(), "the panel is re-sent, filtered to the new balance");
            Assert.AreEqual(0, stall.Asked.Count, "160 left and the 50 offer still shown: no question");
            Assert.AreEqual(stall.Clock.Now, stall.Peddler.ShopOf(stall.Buyer).LastBuyAt);
            Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "a buy is not done");

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes, 3), buyer));
            Assert.AreEqual(0, stall.Peddler.RowsFor(buyer).Count, "fixture: 10 left, nothing affordable");
            CollectionAssert.AreEqual(new[] { ThreadPeddlerVendor.FinishedPromptMessage }, stall.Asked, "the Finished question once nothing is left");
            Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "asking is not done: only the Yes is");
        }

        [TestMethod]
        public void Buys_WithRowsLeft_AskNothing_SoSeveralPurchasesFitInOneVisit()
        {
            var stall = ArmedStall(1000);
            var buyer = stall.P(stall.Buyer);

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), buyer));
            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Plain), buyer));
            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Pattern), buyer));

            Assert.AreEqual(3, stall.BoughtBy(stall.Buyer).Count, "three purchases in one visit");
            Assert.AreEqual(390, stall.Run.PeddlerBalance(stall.Buyer));
            Assert.IsTrue(stall.Peddler.RowsFor(buyer).Count > 0, "fixture: rows remain");
            Assert.AreEqual(0, stall.Asked.Count, "no Finished question while something can still be bought");
            Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "the shop stays open");
        }

        /// <summary>
        /// Review of c5f4253d5 (F1): RowsFor is empty for a closed shop too, so the ask needs the shop still open. The panel
        /// re-send runs between the buy and the ask, so a release there (the poll, a Yes, the run ending) is the race; the
        /// buy that emptied the stall then asks nothing. Mutation-proved: dropping the open check asks.
        /// </summary>
        [TestMethod]
        public void A_shop_that_closes_between_the_buy_and_the_ask_is_asked_nothing()
        {
            foreach (var close in new Action<Stall>[]
            {
                s => Assert.IsTrue(s.Run.TryMarkShopDone(s.Buyer), "fixture: released on the run"),
                s => WithReleases(_ => s.Peddler.Finish(s.Buyer, "test")),
                s => s.Peddler.MarkRunEnded(),
            })
            {
                var stall = ArmedStall(50);
                var buyer = stall.P(stall.Buyer);
                stall.Peddler.Approach = (p, t, spent) => close(stall);

                Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), buyer), "the buy itself went through");
                Assert.AreEqual(0, stall.Peddler.RowsFor(buyer).Count, "fixture: nothing shown");
                Assert.IsFalse(stall.Peddler.ShouldAskFinished(buyer), "a closed shop is never asked");
                Assert.AreEqual(0, stall.Asked.Count, "no stray Finished prompt");
            }

            var open = ArmedStall(50);
            var openBuyer = open.P(open.Buyer);
            Assert.IsTrue(open.Peddler.BuyItems_ValidateTransaction(One(open.Notes), openBuyer));
            Assert.AreEqual(1, open.Asked.Count, "control: the same buy on a shop that stays open asks");
        }

        [TestMethod]
        public void Buy_LeavingNothingBuyable_AsksExactlyOnce()
        {
            var stall = ArmedStall(100);
            var buyer = stall.P(stall.Buyer);

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), buyer));
            Assert.AreEqual(0, stall.Asked.Count, "50 left: the 50 offer is still shown");

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), buyer));
            CollectionAssert.AreEqual(new[] { ThreadPeddlerVendor.FinishedPromptMessage }, stall.Asked, "asked once, when the balance hits 0");

            Assert.IsFalse(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), buyer), "nothing left to pay with");
            Assert.AreEqual(1, stall.Asked.Count, "a refusal asks nothing");
        }

        // ======================================================================================
        // one per player (owner ruling 2026-11-20)
        // ======================================================================================

        /// <summary>An armed solo stall whose plan caps the Pattern listing's entry id ("pattern") at one per player.</summary>
        private static Stall CappedStall(int plain)
        {
            var run = ActiveSoloRun();
            GivePlan(run, CappedPlan(new[] { PatternEntry.Id }, 50, 240, 320));
            return ArmedStall(plain, run);
        }

        [TestMethod]
        public void CappedListing_BuysOnce_ThenLimitReached_AndIsHidden_UncappedStillShown()
        {
            var stall = CappedStall(2000);
            var buyer = stall.P(stall.Buyer);

            CollectionAssert.AreEqual(new[] { stall.Notes, stall.Plain, stall.Pattern }, stall.Peddler.RowsFor(buyer), "fixture: all three shown");

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Pattern), buyer), "the first Pattern is bought");
            CollectionAssert.AreEqual(new[] { stall.Notes, stall.Plain }, stall.Peddler.RowsFor(buyer), "the capped listing is hidden once held; the uncapped ones stay");
            Assert.IsFalse(stall.Peddler.TryGetItemForSale(buyer, stall.Pattern.Guid, out _), "and does not resolve for appraisal");
            Assert.AreEqual(0, stall.Asked.Count, "rows remain: no question");

            var balance = stall.Run.PeddlerBalance(stall.Buyer);
            var second = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Pattern));
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.LimitReached, second.Outcome, "a stale panel's second Pattern");
            Assert.AreEqual("The Wandering Peddler sells only one of that to each customer.", ThreadPeddlerVendor.RefusalLine(second.Outcome));

            Assert.IsFalse(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Pattern), buyer));
            Assert.AreEqual(ThreadPeddlerVendor.LimitReachedMessage, stall.Told.Last());

            Assert.AreEqual(balance, stall.Run.PeddlerBalance(stall.Buyer), "balance unchanged");
            Assert.AreEqual(1, stall.BoughtBy(stall.Buyer).Count, "nothing more bought");
        }

        [TestMethod]
        public void CappedListing_AmountTwoOrMore_IsLimitReached_WhateverTheBalance()
        {
            foreach (var plain in new[] { 2000, 400 })
            {
                var stall = CappedStall(plain);

                foreach (var amount in new[] { 2, 3, int.MaxValue })
                {
                    var result = stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Pattern, amount));
                    Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.LimitReached, result.Outcome, $"pile {plain}, amount {amount}");
                    Assert.AreEqual(0, result.Records.Count, "refused before the records were expanded");
                }

                var split = stall.Peddler.TryBuy(stall.Buyer, true, new List<ItemProfile> { new ItemProfile(1, stall.Pattern.Guid.Full), new ItemProfile(1, stall.Pattern.Guid.Full) });
                Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.LimitReached, split.Outcome, $"pile {plain}: two lines of one each");

                Assert.AreEqual(0, stall.BoughtBy(stall.Buyer).Count, "nothing bought");
                Assert.AreEqual(plain, stall.Run.PeddlerBalance(stall.Buyer));
            }
        }

        [TestMethod]
        public void UncappedListing_BuysRepeatedly_BesideACap()
        {
            var stall = CappedStall(1000);
            var buyer = stall.P(stall.Buyer);

            for (var i = 0; i < 3; i++)
                Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(stall.Notes), buyer), $"buy {i + 1}");

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes, 4)).Outcome, "amount 4 of an uncapped listing");
            Assert.AreEqual(7, stall.BoughtBy(stall.Buyer).Count(p => p.EntryId == NoteEntry.Id));
            Assert.AreEqual(650, stall.Run.PeddlerBalance(stall.Buyer));
        }

        [TestMethod]
        public void FinishedQuestion_SpamGuard_SilentForOurOwnPending_ErrorForAnUnrelatedOne()
        {
            var stall = ArmedStall();
            var buyer = stall.P(stall.Buyer);
            var errors = 0;
            stall.Peddler.AskFailed = p => errors++;

            stall.Peddler.FinishedAsk = (p, t) => false;
            stall.Peddler.AskFinished(buyer);
            Assert.AreEqual(1, errors, "an unrelated pending Yes_No earns ConfirmationInProgress");

            var ours = new Confirmation_PeddlerShop(new ObjectGuid(stall.Buyer), stall.Peddler);
            var other = new Confirmation_PeddlerShop(new ObjectGuid(stall.Buyer), NewPeddler());
            Assert.IsTrue(ThreadPeddlerVendor.AskHandled(true, null, stall.Peddler));
            Assert.IsTrue(ThreadPeddlerVendor.AskHandled(false, ours, stall.Peddler), "our own pending question: a repeat buy is silent");
            Assert.IsFalse(ThreadPeddlerVendor.AskHandled(false, other, stall.Peddler), "another peddler's question is unrelated");
            Assert.IsFalse(ThreadPeddlerVendor.AskHandled(false, null, stall.Peddler), "no pending and not sent");
        }

        // ======================================================================================
        // percent-of-pile pricing (owner ruling 2026-11-20)
        // ======================================================================================

        /// <summary>
        /// A cleared group stall (Owner and Member) on <paramref name="plan"/>, a percent plan: the shared displays are bound as
        /// usual, the production member builder (PeddlerEncounterType.BuildMemberListings) is wired, and each member gets the
        /// pile named, then has their shop opened and is armed (which builds their own displays, under the display seams).
        /// </summary>
        internal static Stall PercentStall(PeddlerPlan plan, int ownerPile, int memberPile)
        {
            var run = ActiveGroupRun(Member);
            GivePlan(run, plan);
            var stall = BoundStall(run);
            stall.Peddler.MemberListings = g => PeddlerEncounterType.BuildMemberListings(run, plan, g);

            Clear(run);
            DrainLedger(run);
            AddPlain(run, Owner, ownerPile);
            AddPlain(run, Member, memberPile);

            PeddlerEncounterTypeTests.WithDisplaySeams(new Dictionary<uint, Weenie>(), _ =>
            {
                foreach (var guid in new[] { Owner, Member })
                {
                    OpenShop(run, guid);
                    Assert.IsTrue(stall.Peddler.Arm(guid), "fixture: armed with own listings");
                    stall.P(guid);
                    stall.InRange.Add(guid);
                }
            });

            return stall;
        }

        private static List<WorldObject> Rows(Stall stall, uint guid) => stall.Peddler.RowsFor(stall.P(guid));

        private static int[] Values(IEnumerable<WorldObject> rows) => rows.Select(r => r.Value ?? -1).ToArray();

        [TestMethod]
        public void PercentPricing_TwoMembers_SeeAndAreChargedTheirOwnPrice_TheValueShownIsTheCharge()
        {
            var stall = PercentStall(PercentPlan(), ownerPile: 400, memberPile: 2000);

            // 400: every percent term is below the floor (25, 120, 320): the flat prices. 2000: 125, 600, 1600.
            CollectionAssert.AreEqual(new[] { 50, 240, 320 }, Values(Rows(stall, Owner)), "a small pile pays exactly the flat prices");
            CollectionAssert.AreEqual(new[] { 125, 600, 1600 }, Values(Rows(stall, Member)), "a big pile pays its share (spawn multiplier 1.6 not applied)");

            var ownerNotes = Rows(stall, Owner)[0];
            var memberNotes = Rows(stall, Member)[0];
            Assert.AreNotEqual(ownerNotes.Guid, memberNotes.Guid, "each member has their own display");
            Assert.IsFalse(Rows(stall, Owner).Concat(Rows(stall, Member)).Any(r => stall.Displays.Contains(r)), "the shared flat displays are never shown under percent pricing");

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(ownerNotes), stall.P(Owner)));
            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(memberNotes, 2), stall.P(Member)));

            Assert.AreEqual(50, stall.BoughtBy(Owner).Single().Price, "the owner pays the 50 they were shown");
            CollectionAssert.AreEqual(new[] { 125, 125 }, stall.BoughtBy(Member).Select(p => p.Price).ToArray(), "the member pays the 125 they were shown");
            Assert.AreEqual(ownerNotes.Value, stall.BoughtBy(Owner).Single().Price, "the panel value is the charged price");
            Assert.AreEqual(memberNotes.Value, stall.BoughtBy(Member)[0].Price);
            Assert.AreEqual(1750, stall.Run.PeddlerBalance(Member));

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Invalid, stall.Peddler.TryBuy(Member, true, One(ownerNotes)).Outcome, "another member's (cheaper) display is not for sale to you");
            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Invalid, stall.Peddler.TryBuy(Member, true, One(stall.Notes)).Outcome, "nor is the shared flat display");
            Assert.AreEqual(2, stall.BoughtBy(Member).Count, "the refused buys bought nothing");
        }

        [TestMethod]
        public void PercentPricing_TheBasisIsFixedForTheVisit()
        {
            var stall = PercentStall(PercentPlan(), ownerPile: 400, memberPile: 2000);

            Assert.AreEqual(2000, stall.Run.PeddlerBasis(Member), "snapshot when the shop opened");
            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(Rows(stall, Member)[1]), stall.P(Member)), "600 bought");
            AddPlain(stall.Run, Member, 500);

            Assert.AreEqual(2000, stall.Run.PeddlerBasis(Member), "neither spending nor a late item moves the basis");
            Assert.AreEqual(1900, stall.Run.PeddlerBalance(Member), "the balance does move");
            CollectionAssert.AreEqual(new[] { 125, 600, 1600 }, Values(Rows(stall, Member)), "so the prices hold");
            Assert.AreEqual(600, stall.Run.PeddlerPriceFor(Member, PlainEntry.Id));

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(Rows(stall, Member)[1]), stall.P(Member)));
            CollectionAssert.AreEqual(new[] { 600, 600 }, stall.BoughtBy(Member).Select(p => p.Price).ToArray(), "the second buy pays the same price");
        }

        /// <summary>Affordability, the Finished question and the one-per-player cap all read the per-viewer price.</summary>
        [TestMethod]
        public void PercentPricing_AffordabilityTheQuestionAndTheCap_UseThePerViewerPrice()
        {
            var stall = PercentStall(PercentPlan(onePerPlayer: new[] { PatternEntry.Id }), ownerPile: 400, memberPile: 2000);
            var member = stall.P(Member);

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(Rows(stall, Member)[2]), member), "the 1600 Pattern");
            CollectionAssert.AreEqual(new[] { 125 }, Values(Rows(stall, Member)), "400 left: 125 shown, 600 not, the capped Pattern hidden");
            Assert.AreEqual(0, stall.Asked.Count);

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(Rows(stall, Member)[0], 3), member), "375 of notes");
            Assert.AreEqual(25, stall.Run.PeddlerBalance(Member));
            Assert.AreEqual(0, Rows(stall, Member).Count, "25 left buys nothing at THIS member's prices");
            CollectionAssert.AreEqual(new[] { ThreadPeddlerVendor.FinishedPromptMessage }, stall.Asked, "so the question is asked");

            CollectionAssert.AreEqual(new[] { 50, 240, 320 }, Values(Rows(stall, Owner)), "the owner's stall is untouched by the member's buys");
        }

        /// <summary>A solo run on <paramref name="plan"/>, cleared with <paramref name="pile"/> items, shop open but NOT armed.</summary>
        private static Stall PercentStallUnarmed(PeddlerPlan plan, int pile)
        {
            var run = ActiveSoloRun();
            GivePlan(run, plan);
            var stall = BoundStall(run);
            ClearWithPiles(run, pile, run.OwnerGuid);
            OpenShop(run, run.OwnerGuid);
            stall.P(run.OwnerGuid);
            return stall;
        }

        private static List<(WorldObject Display, PeddlerPurchase Purchase)> GenericListings(Stall stall, uint member, List<WorldObject> built)
        {
            var listed = new List<(WorldObject, PeddlerPurchase)>();

            foreach (var offer in stall.Run.PeddlerPlan.Offers)
            {
                var price = stall.Run.PeddlerPriceFor(member, offer.EntryId).Value;
                var display = Generic(PlainWcid);
                display.Value = price;
                built.Add(display);
                listed.Add((display, new PeddlerPurchase(offer.EntryId, price, offer.Pick.Pattern)));
            }

            return listed;
        }

        /// <summary>
        /// Review of 05daac285 (F1a): WorldObject.Destroy takes no gate, so a raw Destroy (a landblock unload) can land after
        /// Arm's IsDestroyed check and before its install, destroying a snapshot that predates the member's displays. The
        /// seam runs exactly there. Arm must then destroy what it installed and report not armed. Mutation-proved.
        /// </summary>
        [TestMethod]
        public void PercentPricing_ADestroyRacingTheInstall_LeavesNoDisplayAlive()
        {
            var stall = PercentStallUnarmed(PercentPlan(), 2000);
            var built = new List<WorldObject>();
            stall.Peddler.MemberListings = g => GenericListings(stall, g, built);
            stall.Peddler.BeforeInstallForTest = () => stall.Peddler.Destroy();

            Assert.IsFalse(stall.Peddler.Arm(stall.Buyer), "not armed onto a destroyed peddler");
            Assert.AreEqual(3, built.Count, "fixture: three displays were built");
            Assert.IsTrue(built.All(d => d.IsDestroyed), "no display outlives the peddler");
            Assert.IsTrue(stall.Displays.All(d => d.IsDestroyed), "and the shared ones went with it");
        }

        /// <summary>The coordinator's shape of F1: the builder itself destroys the peddler mid-Arm (before the install).</summary>
        [TestMethod]
        public void PercentPricing_ABuilderThatDestroysThePeddler_ArmsNothing_AndLeavesNoDisplayAlive()
        {
            var stall = PercentStallUnarmed(PercentPlan(), 2000);
            var built = new List<WorldObject>();
            stall.Peddler.MemberListings = g => { var l = GenericListings(stall, g, built); stall.Peddler.Destroy(); return l; };

            Assert.IsFalse(stall.Peddler.Arm(stall.Buyer));
            Assert.IsTrue(built.Count == 3 && built.All(d => d.IsDestroyed));
            Assert.IsNull(stall.Peddler.ShopOf(stall.Buyer));
        }

        /// <summary>F3 (2): the peddler leaves while the member's displays are being built: nothing armed, nothing leaked.</summary>
        [TestMethod]
        public void PercentPricing_ArmAfterTheRunEnded_DestroysWhatItBuilt()
        {
            var stall = PercentStallUnarmed(PercentPlan(), 2000);
            var built = new List<WorldObject>();
            stall.Peddler.MemberListings = g => { var l = GenericListings(stall, g, built); stall.Peddler.MarkRunEnded(); return l; };

            Assert.IsFalse(stall.Peddler.Arm(stall.Buyer));
            Assert.IsTrue(built.Count == 3 && built.All(d => d.IsDestroyed), "every built display destroyed");
            Assert.AreEqual(3, stall.Peddler.DefaultItemsForSale.Count, "none of them listed");
        }

        /// <summary>F3 (1): departure destroys every member's own displays with the shared ones.</summary>
        [TestMethod]
        public void PercentPricing_Departure_DestroysEveryMembersOwnDisplays()
        {
            var stall = PercentStall(PercentPlan(), ownerPile: 400, memberPile: 2000);
            var own = Rows(stall, Owner).Concat(Rows(stall, Member)).ToList();
            var all = stall.Peddler.DefaultItemsForSale.Values.ToList();

            Assert.AreEqual(6, own.Count, "fixture: three own displays per member");
            Assert.AreEqual(9, all.Count, "fixture: plus the three shared");
            CollectionAssert.IsSubsetOf(own.Select(d => d.Guid).ToList(), stall.Peddler.Listings.Keys.ToList(), "every own display is a listing");

            WithReleases(_ => stall.Peddler.Depart("test"));

            Assert.IsTrue(own.All(d => d.IsDestroyed), "every member's own display");
            Assert.IsTrue(all.All(d => d.IsDestroyed), "and every listing at all");
        }

        /// <summary>F3 (4): the shown value is the charged price for a bundle shell row (quantity 5) and a Pattern row.</summary>
        [TestMethod]
        public void PercentPricing_BundleAndPatternRows_ShowWhatTheyCharge()
        {
            var bundleEntry = new ThreadEncounterPrizeEntry("bundle5", new[] { NoteWcid }, 5, 1);
            var offers = new List<PeddlerOffer>
            {
                new PeddlerOffer(new ThreadEncounterPrizePick(bundleEntry, new[] { NoteWcid }), 50, true, 1.1),
                new PeddlerOffer(new ThreadEncounterPrizePick(PatternEntry, new[] { ACE.Server.Knots.KnotItems.PatternWcid }, 0, Pinnacle), 320, false, 0.9),
            };
            var plan = new PeddlerPlan(null, new PeddlerConfig { PricePercent = new Dictionary<string, double> { { "bundle5", 6.25 }, { PatternEntry.Id, 80 } } }, offers, 1.6);
            var stall = PercentStall(plan, ownerPile: 400, memberPile: 2000);

            var rows = Rows(stall, Member);
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(PeddlerConfig.DefaultBundleWcid, rows[0].WeenieClassId, "a quantity-5 row is the bundle shell");
            Assert.AreEqual(ACE.Server.Knots.KnotItems.PatternWcid, rows[1].WeenieClassId, "the Pattern row is the real Pattern");
            CollectionAssert.AreEqual(new[] { 138, 1440 }, Values(rows), "ceil(6.25% x 1.1 x 2000) = 138; ceil(80% x 0.9 x 2000) = 1440");

            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(rows[0]), stall.P(Member)));
            Assert.IsTrue(stall.Peddler.BuyItems_ValidateTransaction(One(rows[1]), stall.P(Member)));

            var bought = stall.BoughtBy(Member);
            CollectionAssert.AreEqual(Values(rows), bought.Select(p => p.Price).ToArray(), "each panel value is the charged price");
            Assert.AreSame(Pinnacle, bought[1].Pattern, "the Pattern keeps its pinnacle");
        }

        [TestMethod]
        public void PercentPricing_ArmFailsClosed_WithoutOwnListings()
        {
            var run = ActiveSoloRun();
            GivePlan(run, PercentPlan());
            var stall = BoundStall(run);
            ClearWithPiles(run, 400, run.OwnerGuid);
            OpenShop(run, run.OwnerGuid);

            Assert.IsFalse(stall.Peddler.Arm(run.OwnerGuid), "no member builder: never armed onto the flat shared rows");
            Assert.IsNull(stall.Peddler.ShopOf(run.OwnerGuid));

            stall.Peddler.MemberListings = _ => new List<(WorldObject, PeddlerPurchase)>();
            Assert.IsFalse(stall.Peddler.Arm(run.OwnerGuid), "a builder that builds nothing: not armed");
        }

        // ======================================================================================
        // done sources
        // ======================================================================================

        [TestMethod]
        public void Dispatch_NoTimeoutAndLate_KeepShopping_OnTimeYesReleases()
        {
            WithReleases(requests =>
            {
                var stall = ArmedStall();
                var player = stall.P(stall.Buyer);
                var queued = new List<Action>();

                foreach (var (response, timeout, late) in new[] { (false, false, false), (true, true, false), (true, false, true) })
                    Confirmation_PeddlerShop.Dispatch(response, timeout, late, player, stall.Peddler, queued.Add);

                Assert.AreEqual(0, queued.Count, "No, a timeout and a late Yes queue nothing");
                Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "and leave the member shopping");

                Confirmation_PeddlerShop.Dispatch(true, false, false, player, stall.Peddler, queued.Add);
                Assert.AreEqual(1, queued.Count, "an on-time Yes queues the release");
                Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "on the player's queue, not inline");

                queued[0]();
                Assert.IsFalse(stall.Run.IsPeddlerShopOpen(stall.Buyer));
                CollectionAssert.AreEqual(new[] { stall.Buyer }, requests.ToArray(), "one placement request");
                Assert.IsTrue(stall.Run.EncounterOutcomes.Any(l => l.Contains("done (answered Yes)")));
            });
        }

        [TestMethod]
        public void WalkAway_FiveSeconds_Releases_ReturningAtFourCancels()
        {
            WithReleases(requests =>
            {
                var stall = ArmedStall();
                stall.Peddler.ActOnUse(stall.P(stall.Buyer));

                stall.InRange.Remove(stall.Buyer);
                stall.Peddler.PollOnce();
                stall.Clock.Now += 4;
                stall.Peddler.PollOnce();
                Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "4 s out of range: still shopping");

                stall.InRange.Add(stall.Buyer);
                stall.Peddler.PollOnce();
                Assert.IsNull(stall.Peddler.ShopOf(stall.Buyer).OutOfRangeSince, "back in range cancels the countdown");

                stall.InRange.Remove(stall.Buyer);
                stall.Clock.Now += 1;
                stall.Peddler.PollOnce();
                stall.Clock.Now += 4;
                stall.Peddler.PollOnce();
                Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "the countdown restarted from the second walk-away");
                Assert.AreEqual(0, requests.Count);

                stall.Clock.Now += 1;
                var released = stall.Peddler.PollOnce();
                CollectionAssert.AreEqual(new[] { (stall.Buyer, "walked away") }, released);
                Assert.IsFalse(stall.Run.IsPeddlerShopOpen(stall.Buyer));
                CollectionAssert.AreEqual(new[] { stall.Buyer }, requests.ToArray());
            });
        }

        [TestMethod]
        public void WalkAway_NeverAppliesBeforeTheFirstUse_SoAMemberCanWalkToThePeddler()
        {
            WithReleases(requests =>
            {
                var stall = ArmedStall();
                stall.InRange.Remove(stall.Buyer);

                for (var i = 0; i < 60; i++)
                {
                    stall.Clock.Now += 1;
                    stall.Peddler.PollOnce();
                }

                Assert.IsTrue(stall.Run.IsPeddlerShopOpen(stall.Buyer), "a minute away from it, never having used it: still shopping");
                Assert.AreEqual(0, requests.Count);
            });
        }

        [TestMethod]
        public void Idle_NoUseFor120s_OrNoBuyFor120sAfterTheLastUse_Releases_CappedAtExpiry()
        {
            WithReleases(requests =>
            {
                var never = ArmedStall();
                never.Clock.Now += 119;
                Assert.AreEqual(0, never.Peddler.PollOnce().Count);
                never.Clock.Now += 1;
                CollectionAssert.AreEqual(new[] { (never.Buyer, "idle") }, never.Peddler.PollOnce(), "never used within 120 s of presentation");

                var standing = ArmedStall();
                standing.Peddler.ActOnUse(standing.P(standing.Buyer));
                standing.Clock.Now += 50;
                Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Bought, standing.Peddler.TryBuy(standing.Buyer, true, One(standing.Notes)).Outcome);
                standing.Clock.Now += 119;
                Assert.AreEqual(0, standing.Peddler.PollOnce().Count, "the buy restarted the clock");
                standing.Clock.Now += 1;
                CollectionAssert.AreEqual(new[] { (standing.Buyer, "idle") }, standing.Peddler.PollOnce());

                var shop = new ThreadPeddlerVendor.MemberShop { PresentedAt = 1000, LastUseAt = 1010, LastBuyAt = 1050 };
                Assert.AreEqual(1170, ThreadPeddlerVendor.IdleDeadline(shop, 120, double.MaxValue));
                Assert.AreEqual(1100, ThreadPeddlerVendor.IdleDeadline(shop, 120, 1100), "capped at the run's expiry minus the margin");
                Assert.AreEqual(1120, ThreadPeddlerVendor.IdleDeadline(new ThreadPeddlerVendor.MemberShop { PresentedAt = 1000 }, 120, double.MaxValue));

                var expiring = ArmedStall();
                expiring.Clock.Now = ThreadPeddlerVendor.ExpiryCapUnix(expiring.Run);
                expiring.Peddler.ActOnUse(expiring.P(expiring.Buyer));
                CollectionAssert.AreEqual(new[] { (expiring.Buyer, "the thread is about to close") }, expiring.Peddler.PollOnce(), "the TTL cap overrides a fresh Use");

                Assert.AreEqual(3, requests.Count);
            });
        }

        [TestMethod]
        public void LogoutOrLeavingTheRun_Releases()
        {
            WithReleases(requests =>
            {
                var gone = ArmedStall();
                gone.Players.Clear();
                CollectionAssert.AreEqual(new[] { (gone.Buyer, "logged out or left the run") }, gone.Peddler.PollOnce());

                var left = ArmedStall();
                left.Outside.Add(left.Buyer);
                CollectionAssert.AreEqual(new[] { (left.Buyer, "logged out or left the run") }, left.Peddler.PollOnce());

                Assert.AreEqual(2, requests.Count);
            });
        }

        /// <summary>Every done source, fired after another one already won, releases nothing more: one placement request per member.</summary>
        [TestMethod]
        public void EachDoneSource_ReleasesExactlyOnce()
        {
            var sources = new (string Name, Action<Stall> Fire)[]
            {
                ("Yes", s => s.Peddler.FinishFromPrompt(s.P(s.Buyer))),
                ("walk-away", s => { s.Peddler.ActOnUse(s.P(s.Buyer)); s.InRange.Remove(s.Buyer); s.Peddler.PollOnce(); s.Clock.Now += 5; s.Peddler.PollOnce(); }),
                ("idle", s => { s.Clock.Now += 1000; s.Peddler.PollOnce(); }),
                ("logout", s => { s.Players.Remove(s.Buyer); s.Peddler.PollOnce(); }),
                ("/spawncache", s => ACE.Server.Command.Handlers.ThreadDungeonCommands.SpawnCacheEndsPeddlerShop(s.Run, s.Buyer)),
                ("departure", s => s.Peddler.Depart("test")),
            };

            foreach (var first in sources)
            {
                foreach (var second in sources)
                {
                    WithReleases(requests =>
                    {
                        var stall = ArmedStall(60);
                        first.Fire(stall);
                        Assert.IsFalse(stall.Run.IsPeddlerShopOpen(stall.Buyer), $"{first.Name} releases");
                        second.Fire(stall);
                        second.Fire(stall);
                        Assert.AreEqual(1, requests.Count, $"{first.Name} then {second.Name} twice: exactly one placement request");
                    });
                }
            }
        }

        [TestMethod]
        public void ABusyPlacement_ReFlagsTheMember()
        {
            WithReleases(_ =>
            {
                var solo = ArmedStall(60);
                Assert.IsFalse(solo.Run.IsPlacementWanted);
                solo.Peddler.FinishFromPrompt(solo.P(solo.Buyer));
                Assert.IsTrue(solo.Run.IsPlacementWanted, "solo: the run's placement-wanted flag");

                var group = ArmedStall(60, ActiveGroupRun(Member), Owner, Member);
                group.Peddler.FinishFromPrompt(group.P(Member));
                Assert.IsTrue(group.Run.IsMemberDeliveryWanted(Member), "group: that member's delivery-wanted flag");
                Assert.IsFalse(group.Run.IsMemberDeliveryWanted(Owner), "and nobody else's");
            }, CacheRequestOutcome.Busy);
        }

        // ======================================================================================
        // departure and destroy
        // ======================================================================================

        [TestMethod]
        public void Destroy_RemovesEveryDisplay()
        {
            var stall = BoundStall();
            stall.Peddler.Destroy();

            Assert.IsTrue(stall.Displays.All(d => d.IsDestroyed), "the vendor branch of Destroy destroys every listing");
        }

        [TestMethod]
        public void Departs_AfterTheLastArmedMember_TellsThem_AndDestroysEverything()
        {
            WithReleases(requests =>
            {
                var stall = ArmedStall(60, ActiveGroupRun(Member), Owner, Member);
                Assert.IsNull(stall.Peddler.DepartureReason(), "two shoppers: stays");

                stall.Peddler.FinishFromPrompt(stall.P(Owner));
                Assert.IsNull(stall.Peddler.DepartureReason(), "one still shopping: stays");
                Assert.IsFalse(stall.Peddler.CheckDeparture());

                stall.Peddler.FinishFromPrompt(stall.P(Member));
                Assert.AreEqual("every member finished", stall.Peddler.DepartureReason());
                Assert.IsTrue(stall.Peddler.CheckDeparture());

                Assert.IsTrue(stall.Peddler.IsDestroyed);
                Assert.IsTrue(stall.Displays.All(d => d.IsDestroyed));
                Assert.AreEqual(2, stall.Told.Count(t => t == ThreadPeddlerVendor.DepartureMessage), "each armed member hears it");
                Assert.IsTrue(stall.Run.PeddlerClosed, "nobody is presented again");
                Assert.AreEqual(2, requests.Count, "the two releases; the departure releases nothing more");
                Assert.IsTrue(stall.Run.EncounterOutcomes.Any(l => l.Contains("peddler: departed (every member finished)")));
            });
        }

        [TestMethod]
        public void Departure_WhenTheRunEnds_AndAnOpenShopIsReleased()
        {
            WithReleases(requests =>
            {
                var stall = ArmedStall(60);
                Assert.IsTrue(stall.Run.MarkEnded("test"));
                StringAssert.Contains(stall.Peddler.DepartureReason(), "Ended");
                Assert.IsTrue(stall.Peddler.CheckDeparture());
                Assert.AreEqual(0, stall.Told.Count(t => t == ThreadPeddlerVendor.DepartureMessage), "an ended run's copy is being torn down: no line");
                Assert.AreEqual(0, requests.Count, "nothing to deliver from an ended run");

                var forced = ArmedStall(60);
                forced.Peddler.Depart("test");
                Assert.IsFalse(forced.Run.IsPeddlerShopOpen(forced.Buyer), "the open shop is released by the close");
                CollectionAssert.AreEqual(new[] { forced.Buyer }, requests.ToArray(), "and its placement requested");
            });
        }

        [TestMethod]
        public void MarkRunEnded_StopsAllTrade()
        {
            var stall = ArmedStall();

            stall.Peddler.MarkRunEnded();

            Assert.AreEqual(ThreadPeddlerVendor.BuyOutcome.Closed, stall.Peddler.TryBuy(stall.Buyer, true, One(stall.Notes)).Outcome);
            Assert.AreEqual(ThreadPeddlerVendor.UseOutcome.Closed, stall.Peddler.TryUse(stall.Buyer, true));
            Assert.IsFalse(stall.Peddler.IsDestroyed, "memory only: EndRun destroys the object");
        }

        /// <summary>
        /// The row's idleSeconds and walkAwaySeconds are what the poll uses, not the defaults (review of #2237: removing the
        /// wiring passed every test). Mutation-proved: PollOnce reading PeddlerConfig's defaults fails this.
        /// </summary>
        [TestMethod]
        public void ConfiguredIdleAndWalkAway_AreWhatThePollUses()
        {
            WithReleases(requests =>
            {
                var idle = ArmedStallWith(30, 2);
                Assert.AreEqual((30.0, 2.0), idle.Peddler.Timings);
                idle.Clock.Now += 29;
                Assert.AreEqual(0, idle.Peddler.PollOnce().Count, "29 s: not yet idle");
                idle.Clock.Now += 1;
                CollectionAssert.AreEqual(new[] { (idle.Buyer, "idle") }, idle.Peddler.PollOnce(), "30 s: idle (the default would wait 120)");

                var walk = ArmedStallWith(30, 2);
                walk.Peddler.ActOnUse(walk.P(walk.Buyer));
                walk.InRange.Remove(walk.Buyer);
                walk.Peddler.PollOnce();
                walk.Clock.Now += 1.9;
                Assert.AreEqual(0, walk.Peddler.PollOnce().Count, "1.9 s out of range: still shopping");
                walk.Clock.Now += 0.1;
                CollectionAssert.AreEqual(new[] { (walk.Buyer, "walked away") }, walk.Peddler.PollOnce(), "2 s: walked away (the default would wait 5)");

                Assert.AreEqual(2, requests.Count);
            });
        }

        /// <summary>
        /// GameEventApproachVendor writes the count field (GetItemsForSaleCount) and then the rows (ForEachItemFor): inside one
        /// panel snapshot the two agree even when the balance changes between them. Control: outside a snapshot they differ.
        /// </summary>
        [TestMethod]
        public void PanelCountAndRows_AgreeWithinOneSnapshot_WhenTheBalanceChangesBetweenThem()
        {
            var stall = ArmedStall(100);
            var viewer = Viewer(stall.Buyer);

            int count = -1, rows = -1;

            stall.Peddler.WithPanelSnapshot(viewer, () =>
            {
                AddPlain(stall.Run, stall.Buyer, 150); // another thread's late kill lands before the count field
                count = stall.Peddler.GetItemsForSaleCount(viewer);
                AddPlain(stall.Run, stall.Buyer, 150); // and another between the count field and the rows
                var written = 0;
                stall.Peddler.ForEachItemFor(viewer, _ => written++);
                rows = written;
            });

            Assert.AreEqual(1, count, "the count field is the snapshot taken when the message began (100 items: one offer)");
            Assert.AreEqual(count, rows, "the rows written equal the count field");

            var live = 0;
            stall.Peddler.ForEachItemFor(viewer, _ => live++);
            Assert.AreEqual(3, live, "control: 400 items now, so outside a snapshot the live read has moved on and two reads could disagree");
        }

        /// <summary>
        /// Every panel message goes through ApproachVendor, so the override must route through the snapshot; a bare
        /// base call passed every behavioural test (re-review of #2237). Mutation-proved with exactly that edit.
        /// </summary>
        [TestMethod]
        public void ApproachVendor_routes_through_the_panel_snapshot()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadPeddlerVendor.cs").Replace("\r\n", "\n");
            StringAssert.Contains(source,
                "public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)\n"
                + "            => WithPanelSnapshot(player, () => base.ApproachVendor(player, action, altCurrencySpent));",
                "the ApproachVendor override takes one row snapshot for the whole panel message");
        }

        [TestMethod]
        public void ANpcNobodyWasArmedOn_LeavesNow()
        {
            var stall = BoundStall();
            Clear(stall.Run);
            Assert.AreEqual("nobody to trade with", stall.Peddler.DepartureReason(), "created, but the open failed: it must not linger");
            Assert.IsTrue(stall.Peddler.CheckDeparture());
            Assert.IsTrue(stall.Peddler.IsDestroyed);
        }

        [TestMethod]
        public void A_release_withdraws_this_peddlers_pending_Finished_question()
        {
            WithReleases(requests =>
            {
                var stall = ArmedStall(60);
                var player = stall.P(stall.Buyer);
                Pend(player, new Confirmation_PeddlerShop(new ObjectGuid(stall.Buyer), stall.Peddler));
                Assert.IsTrue(player.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out _), "fixture");

                Assert.IsTrue(stall.Peddler.Finish(stall.Buyer, "walked away"));
                Assert.IsFalse(player.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out _), "the question is withdrawn");

                var other = ArmedStall(60);
                var p2 = other.P(other.Buyer);
                Pend(p2, new Confirmation_PeddlerShop(new ObjectGuid(other.Buyer), NewPeddler()));
                Assert.IsTrue(other.Peddler.Finish(other.Buyer, "idle"));
                Assert.IsTrue(p2.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out _), "control: another peddler's question is left alone");

                var departing = ArmedStall(60);
                var p3 = departing.P(departing.Buyer);
                Pend(p3, new Confirmation_PeddlerShop(new ObjectGuid(departing.Buyer), departing.Peddler));
                departing.Peddler.Depart("test");
                Assert.IsFalse(p3.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out _), "the departure withdraws it too");
            });
        }

        [TestMethod]
        public void IsAlive_FollowsDestroyDepartureAndAStoppedPoll()
        {
            var stall = ArmedStall(10);
            Assert.IsTrue(stall.Peddler.IsAlive(stall.Clock.Now), "never polled yet: alive");

            stall.Peddler.LastPollAt = stall.Clock.Now;
            Assert.IsTrue(stall.Peddler.IsAlive(stall.Clock.Now + 14));
            Assert.IsFalse(stall.Peddler.IsAlive(stall.Clock.Now + 15), "no poll for 15 s: its poll has stopped");

            var departed = ArmedStall(10);
            departed.Peddler.MarkRunEnded();
            Assert.IsFalse(departed.Peddler.IsAlive(departed.Clock.Now));

            var destroyed = ArmedStall(10);
            destroyed.Peddler.Destroy();
            Assert.IsFalse(destroyed.Peddler.IsAlive(destroyed.Clock.Now));
        }

        // ======================================================================================
        // the race
        // ======================================================================================

        /// <summary>
        /// Two done sources for the same member at the same instant (the Yes on the player's queue and the walk-away poll on
        /// the peddler's landblock), released together by a Barrier, 200 times: exactly one placement request every time.
        /// Mutation-proved: with TryMarkShopDone's latch disabled this fails. Run it 10-20 times alone.
        /// </summary>
        [TestMethod]
        public void TwoDoneSourcesAtOnce_ReleaseExactlyOnce()
        {
            WithReleases(requests =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var stall = ArmedStall(10);
                    var player = stall.P(stall.Buyer);
                    stall.Peddler.ActOnUse(player);
                    stall.InRange.Remove(stall.Buyer);
                    stall.Peddler.PollOnce();
                    stall.Clock.Now += 5;

                    var before = requests.Count;
                    var wins = 0;
                    using var barrier = new System.Threading.Barrier(2);

                    // The Yes, and the poll (which finds the 5 s walk-away due): both reach Finish for the same member.
                    var yes = new System.Threading.Thread(() => { barrier.SignalAndWait(); if (stall.Peddler.Finish(stall.Buyer, "answered Yes")) System.Threading.Interlocked.Increment(ref wins); });
                    var poll = new System.Threading.Thread(() => { barrier.SignalAndWait(); stall.Peddler.PollOnce(); });

                    yes.Start();
                    poll.Start();
                    yes.Join();
                    poll.Join();

                    Assert.AreEqual(before + 1, requests.Count, $"iteration {i}: one placement request");
                    Assert.IsTrue(wins <= 1, $"iteration {i}: at most one winner on the Yes side");
                    Assert.IsFalse(stall.Run.IsPeddlerShopOpen(stall.Buyer));
                }
            });
        }

        // ======================================================================================
        // text and source pins
        // ======================================================================================

        [TestMethod]
        public void PlayerText_IsTheOwnerWording()
        {
            Assert.AreEqual("The Wandering Peddler finds you as the thread clears. Spend items from your Thread Cache on the wares below, as many times as you like. When you are done, walk away and your Thread Cache forms.", ThreadPeddlerVendor.PresentedMessage);
            Assert.AreEqual("The Wandering Peddler sells only one of that to each customer.", ThreadPeddlerVendor.LimitReachedMessage);
            Assert.AreEqual("The Peddler is paid in items taken at random from the ordinary loot of your Thread Cache. Fibers, Patterns, Trade Notes (MMDs), rares and the boss's guaranteed rewards, including its salvage bags, are never taken.", ThreadPeddlerVendor.ExclusionMessage);
            Assert.AreEqual("Finished trading with the Wandering Peddler? Choose Yes to form your Thread Cache now, or No to keep shopping.", ThreadPeddlerVendor.FinishedPromptMessage);
            Assert.AreEqual("The Wandering Peddler has nothing you can afford and slips back into the Weave.", ThreadPeddlerVendor.NothingAffordableMessage);
            Assert.AreEqual("The Wandering Peddler packs up and slips back into the Weave.", ThreadPeddlerVendor.DepartureMessage);
            Assert.AreEqual("Only members of this thread can trade with the Wandering Peddler.", ThreadPeddlerVendor.NonMemberMessage);

            CollectionAssert.AreEqual(new[]
            {
                "The Wandering Peddler hands you Rear Guard Mantle.",
                "The Wandering Peddler hands you 3 of Diamond Scarab x5.",
            }, ThreadPeddlerVendor.PurchaseLines(new[] { ("Rear Guard Mantle", 1), ("Diamond Scarab x5", 3) }).ToList(), "N = 1 is the singular line; N above 1 is the plural line");

            Assert.AreEqual("You are too encumbered to buy that!", ThreadPeddlerVendor.RefusalLine(ThreadPeddlerVendor.BuyOutcome.TooEncumbered), "the retail vendor's pack refusals");
            Assert.AreEqual("You do not have enough pack space to buy that!", ThreadPeddlerVendor.RefusalLine(ThreadPeddlerVendor.BuyOutcome.NoPackSpace));
            Assert.AreEqual("You do not have enough container slots to buy that!", ThreadPeddlerVendor.RefusalLine(ThreadPeddlerVendor.BuyOutcome.NoContainerSlots));

            var all = typeof(ThreadPeddlerVendor).GetFields(BindingFlags.NonPublic | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()).ToList();
            Assert.IsTrue(all.All(s => s.All(c => c < 128)), "ASCII only");
            Assert.IsFalse(all.Any(s => s.Contains("will be waiting in your Thread Cache")), "no set-aside wording survives");
            Assert.IsFalse(all.Any(s => s.Contains("when the thread clears") && !s.StartsWith("The Wandering Peddler finds you", StringComparison.Ordinal)), "no mid-run wording survives");
            Assert.IsNull(typeof(ThreadPeddlerVendor).GetField("FirstUseMessage", BindingFlags.NonPublic | BindingFlags.Static), "the mid-run window text is gone");
            Assert.IsNull(typeof(ThreadPeddlerVendor).GetField("RunClosedMessage", BindingFlags.NonPublic | BindingFlags.Static));
        }

        /// <summary>
        /// Source pins (mutation-proved): the buy and the Use never call base (the retail purchase and single-customer
        /// paths), and the type is not IThreadEncounterUsable (a Use is handled once, by the vendor).
        /// </summary>
        [TestMethod]
        public void Source_BuyAndUse_NeverCallBase()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/ThreadDungeons/ThreadPeddlerVendor.cs")));

            Assert.IsFalse(code.Contains("base.BuyItems_ValidateTransaction"), "the retail buy (FinalizeBuyTransaction, pack delivery) must never run");
            Assert.IsFalse(code.Contains("base.ActOnUse"), "the retail single-customer Use must never run");
            Assert.IsFalse(typeof(IThreadEncounterUsable).IsAssignableFrom(typeof(PeddlerEncounterType)), "a Use is handled once, by the vendor's ActOnUse");
        }
    }
}

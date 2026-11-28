using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The Avatar vendor's panel, balance, Use and buy hook. The vendor is built through the factory from an in-memory weenie,
    /// the players are seeded (no session), and every collaborator that would reach the world is a seam restored in cleanup.
    /// Nothing here reads PropertyManager: the tunables go through AvatarVendor.Tuning.
    /// </summary>
    [TestClass]
    public class AvatarVendorTests
    {
        private const double Start = 1_800_000_000.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly AvatarTuning T = AvatarTuning.Defaults with { EventMinLevel = 1 };

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint VassalGuid = 1001;
        private const uint VassalAcct = 101;
        private const uint PlainGuid = 1002;
        private const uint OutsiderGuid = 2000;

        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        private static uint nextGuid = 0x7EC90000;

        /// <summary>The tuning the fixture runs under; a scaled-price test swaps it before InitActive.</summary>
        private AvatarTuning tuning = T;

        private Store store;
        private AccountActivityIndex activity;
        private List<AvatarRoster> rosters;
        private Dictionary<uint, (uint Monarch, bool Seneschal)> facts;
        private List<(Player To, string Text)> told;
        private List<(Player Buyer, bool Announced)> handed;
        private bool handlerResult;
        private Player monarch, seneschal, plain, outsider;

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            SalvageRollerVendorTests.SeedVendorConstructible();

            store = new Store();
            activity = new AccountActivityIndex();
            rosters = new List<AvatarRoster>();
            told = new List<(Player, string)>();
            handed = new List<(Player, bool)>();
            handlerResult = true;

            facts = new Dictionary<uint, (uint, bool)>
            {
                { Monarch, (Monarch, false) },
                { VassalGuid, (Monarch, true) },
                { PlainGuid, (Monarch, false) },
                { OutsiderGuid, (OutsiderGuid, false) },
            };

            AvatarRights.AllegianceFacts = p => facts[p.Guid.Full];
            AvatarVendor.Tuning = () => tuning;
            AvatarVendor.ListingBuilder = MakeDisplay;
            AvatarVendor.BlessingListingBuilder = MakeBlessingDisplay;
            AvatarBlessing.AllOnline = () => online;
            online = new List<Player>();
            AvatarVendor.BuyHandler = Handler;
            AvatarAnnouncer.Sink = (m, text) => { };
            AvatarStatusText.KeeperName = g => g == Monarch ? "Varric" : null;

            monarch = SalvageRollerVendorTests.MakePlayer(Monarch);
            seneschal = SalvageRollerVendorTests.MakePlayer(VassalGuid);
            plain = SalvageRollerVendorTests.MakePlayer(PlainGuid);
            outsider = SalvageRollerVendorTests.MakePlayer(OutsiderGuid);
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
            AvatarVendor.ResetSeams();
            AvatarBlessing.ResetSeams();
            AvatarRights.ResetSeams();
            AvatarStatusText.ResetSeams();
            AvatarAnnouncer.Sink = null;
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        // ---- fixture --------------------------------------------------------------------------------------------

        private bool Handler(Player buyer, AvatarVendor npc, bool announced, out string line)
        {
            handed.Add((buyer, announced));
            line = "handler refused";
            return handlerResult;
        }

        private List<Player> online = new List<Player>();

        private static WorldObject MakeBlessingDisplay()
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.BlessingListingWcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Blessing" } },
            };

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        private static WorldObject MakeDisplay()
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.EventListingWcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Private World Event" } },
            };

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
        }

        private AvatarVendor MakeVendor(uint avatarId = AvatarId)
        {
            var weenie = new Weenie
            {
                WeenieClassId = AvatarVendor.AvatarWcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.AllegianceAvatarVendor, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 0 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, 0 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.BuyPrice, 0 }, { PropertyFloat.SellPrice, 1 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Allegiance Avatar" } },
            };

            var vendor = (AvatarVendor)WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(++nextGuid));
            vendor.AvatarId = avatarId;
            vendor.Told = (p, text) => told.Add((p, text));
            return vendor;
        }

        /// <summary>Avatar 7 bound to monarch 1000 with one vassal, both accounts active: after the first tick it is Bound and Active.</summary>
        private AvatarState InitActive(ulong favor = 5000, ulong lifetime = 10_000, int r = 2, int s = 2)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = AvatarId, MonarchGuid = Monarch, State = (byte)AvatarBindState.Bound,
                Favor = favor, LifetimeXp = lifetime, LevelXp = lifetime, LastDecayDay = Today,
            });
            rosters.Add(AvatarScaleFixture.Roster(activity, r, s, Monarch, MonarchAcct, VassalGuid, VassalAcct, Start));

            AvatarManager.Initialize(store, () => tuning, () => Start, () => activity, () => rosters, m => rosters.FirstOrDefault(r => r.MonarchGuid == m));
            AvatarManager.Tick();

            var state = AvatarManager.Get(AvatarId);
            Assert.IsNotNull(state);
            Assert.AreEqual(AvatarBindState.Bound, state.State);
            Assert.IsTrue(state.Active, "the fixture must start Active");
            return state;
        }

        private static List<WorldObject> Panel(AvatarVendor vendor, Player viewer)
        {
            var rows = new List<WorldObject>();
            vendor.ForEachItemFor(viewer, rows.Add);
            Assert.AreEqual(rows.Count, vendor.GetItemsForSaleCount(viewer), "the count must equal what is enumerated");
            return rows;
        }

        private static List<ItemProfile> Profile(WorldObject row, int amount = 1) => new List<ItemProfile> { new ItemProfile(amount, vendorWire(row)) };

        private static uint vendorWire(WorldObject row) => row.Guid.Full;

        // ---- panel ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Panel_SeneschalSeesTheEventRowsAndTheBlessing()
        {
            InitActive();
            var vendor = MakeVendor();

            var rows = Panel(vendor, seneschal);

            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { AvatarVendor.QuietName, AvatarVendor.AnnouncedName, AvatarVendor.BlessingName }, rows.Select(r => r.Name).ToArray());
            Assert.AreEqual((int)T.EventPriceQuiet, rows[0].Value);
            Assert.AreEqual((int)T.EventPriceAnnounced, rows[1].Value);
            Assert.AreEqual((int)T.BlessingPriceBase, rows[2].Value, "nobody online: the Blessing row shows its base price");
            Assert.AreEqual(3, Panel(vendor, monarch).Count, "the monarch spends too");
            Assert.IsTrue(vendor.TryGetItemForSale(seneschal, rows[0].Guid, out _));
        }

        [DataTestMethod]
        [DataRow(2, 230, 700, 1000)]
        [DataRow(20, 230, 4000, 6000)]
        [DataRow(73, 230, 10240, 15359)]
        public void Panel_EventRowsShowThisAvatarsScaledPrice(int r, int s, int quiet, int announced)
        {
            tuning = T with { EventPriceQuiet = 700, EventPriceQuietPerAccount = 200, EventPriceAnnounced = 1000, EventPriceAnnouncedPerAccount = 300 };
            InitActive(r: r, s: s);
            var vendor = MakeVendor();

            var rows = Panel(vendor, seneschal);

            Assert.AreEqual(quiet, rows[0].Value, "quiet row");
            Assert.AreEqual(announced, rows[1].Value, "announced row");
        }

        [TestMethod]
        public void Panel_ListingValueFollowsTheRosterAsItChanges()
        {
            tuning = T;
            var state = InitActive(r: 73, s: 230);
            var vendor = MakeVendor();
            Assert.AreEqual(10240, Panel(vendor, seneschal)[0].Value);

            state.ActiveAccounts = 20;

            Assert.AreEqual(4000, Panel(vendor, seneschal)[0].Value, "re-read on every panel, not cached from the first");
        }

        [TestMethod]
        public void Panel_SwitchOff_ShowsNone_UntilBackOn()
        {
            InitActive();
            var vendor = MakeVendor();
            Assert.AreEqual(3, Panel(vendor, monarch).Count, "control: listings exist while the switch is on");

            var off = T with { Enabled = false };
            AvatarVendor.Tuning = () => off;
            Assert.AreEqual(0, Panel(vendor, monarch).Count);
            Assert.AreEqual(0, vendor.GetItemsForSaleCount(monarch));

            AvatarVendor.Tuning = () => T;
            Assert.AreEqual(3, Panel(vendor, monarch).Count);
        }

        [TestMethod]
        public void Panel_PlainMemberSeesNone()
        {
            InitActive();
            var vendor = MakeVendor();
            var rowGuid = Panel(vendor, seneschal)[0].Guid;

            Assert.AreEqual(0, Panel(vendor, plain).Count);
            Assert.AreEqual(0, Panel(vendor, outsider).Count);
            Assert.IsFalse(vendor.TryGetItemForSale(plain, rowGuid, out _), "a row not shown does not resolve");
            Assert.AreEqual(0, vendor.ItemsForSaleCount, "the viewer-less count fails closed");

            var any = false;
            vendor.forEachItem(_ => any = true);
            Assert.IsFalse(any);
        }

        [TestMethod]
        public void Panel_NotActive_ShowsNone()
        {
            var state = InitActive();
            var vendor = MakeVendor();
            state.Active = false;

            Assert.AreEqual(0, Panel(vendor, seneschal).Count);
        }

        [TestMethod]
        public void Panel_UnboundVendor_ShowsNone()
        {
            InitActive();
            Assert.AreEqual(0, Panel(MakeVendor(avatarId: 0), seneschal).Count);
        }

        // ---- balance --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Balance_IsFavorMinusReserved_ForFavorWcidOnly()
        {
            var state = InitActive(favor: 5000);
            state.ReservedFavor = 1200;
            var vendor = MakeVendor();

            Assert.AreEqual(3800L, vendor.AlternateCurrencyBalanceFor(plain, AvatarVendor.FavorWcid), "any member sees the balance");
            Assert.AreEqual(0L, vendor.AlternateCurrencyBalanceFor(plain, AvatarVendor.EventListingWcid), "another wcid is 0");
            Assert.AreEqual(0L, vendor.AlternateCurrencyBalanceFor(plain, 1u));
            Assert.AreEqual(0L, vendor.AlternateCurrencyBalanceFor(outsider, AvatarVendor.FavorWcid), "a non-member sees 0");
        }

        // ---- buy ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Buy_NeverCallsFinalize_NoItemInPack()
        {
            var state = InitActive(favor: 5000);
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);
            var packBefore = seneschal.Inventory.Count;
            var favorBefore = state.Favor;
            handlerResult = true;

            var ok = vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal);

            Assert.IsTrue(ok);
            Assert.AreEqual(packBefore, seneschal.Inventory.Count, "nothing reaches the pack");
            Assert.AreEqual(favorBefore, state.Favor, "the vendor itself debits nothing");
            Assert.AreEqual(0UL, state.ReservedFavor, "the vendor itself reserves nothing");
            Assert.AreEqual(1, handed.Count, "the hand-off ran once");
        }

        [TestMethod]
        public void Panel_BlessingRow_IsPricedForTheMembersOnlineNow()
        {
            InitActive();
            var vendor = MakeVendor();
            online.AddRange(new[] { AvatarBlessingTests.Member(0x7EB00001, 501, Monarch), AvatarBlessingTests.Member(0x7EB00002, 502, Monarch) });

            var row = Panel(vendor, seneschal).Single(r => r.Name == AvatarVendor.BlessingName);

            Assert.AreEqual(450, row.Value, "250 + 100 x 2 online accounts");
        }

        [TestMethod]
        public void Buy_BlessingRow_RoutesToTheBlessingHandler_Only()
        {
            InitActive(favor: 5000);
            var vendor = MakeVendor();
            var blessed = new List<Player>();
            AvatarVendor.BlessingBuyHandler = (Player p, AvatarVendor npc, out string line) => { blessed.Add(p); line = null; return true; };
            var rows = Panel(vendor, seneschal);

            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(Profile(rows.Single(r => r.Name == AvatarVendor.BlessingName)), seneschal));
            CollectionAssert.AreEqual(new[] { seneschal }, blessed);
            Assert.AreEqual(0, handed.Count, "the Blessing row never reaches the event hand-off");

            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal));
            Assert.AreEqual(1, blessed.Count, "an event row never reaches the Blessing hand-off");
        }

        [TestMethod]
        public void Buy_BlessingRow_PlainMemberRefused_BeforeTheHandler()
        {
            InitActive(favor: 5000);
            var vendor = MakeVendor();
            var calls = 0;
            AvatarVendor.BlessingBuyHandler = (Player p, AvatarVendor npc, out string line) => { calls++; line = null; return true; };
            var row = Panel(vendor, seneschal).Single(r => r.Name == AvatarVendor.BlessingName);

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(row), plain));
            Assert.AreEqual(0, calls, "the same spend roles as every other row");
        }

        [TestMethod]
        public void Buy_BodyNeverReachesBaseOrFinalize()
        {
            // The pack and Favor assertions above cannot fail on their own (base only sells what is in DefaultItemsForSale, which is
            // empty here), so the structural guarantee is pinned on the source of the override itself.
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/AvatarVendor.cs"),
                "public override bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)");

            Assert.IsFalse(body.Contains("base.BuyItems_ValidateTransaction"), "the buy must never call base");
            Assert.IsFalse(body.Contains("FinalizeBuyTransaction("), "the buy must never finalize");
            Assert.IsTrue(body.Contains("BuyHandler("), "the buy must hand off to the purchase");
        }

        [TestMethod]
        public void Buy_NonMember_RefusedNothingDebited()
        {
            var state = InitActive(favor: 5000);
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), outsider));

            Assert.AreEqual(0, handed.Count);
            Assert.AreEqual(5000UL, state.Favor);
            Assert.AreEqual(0UL, state.ReservedFavor);
            Assert.AreEqual(AvatarVendor.OtherAllegianceMessage, told.Last().Text);
        }

        [TestMethod]
        public void Buy_PlainMember_RefusedAtMomentOfSale()
        {
            InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), plain), "a guid replayed by a member without rights is refused");

            Assert.AreEqual(0, handed.Count);
            Assert.AreEqual(AvatarVendor.NotAllowedMessage, told.Last().Text);
        }

        [TestMethod]
        public void Buy_NotActiveAnymore_RefusedAtMomentOfSale()
        {
            var state = InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);
            state.Active = false;

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal));
            Assert.AreEqual(0, handed.Count);
            Assert.AreEqual(AvatarVendor.NotActiveMessage, told.Last().Text);
        }

        [TestMethod]
        public void Buy_QuietListing_PassesAnnouncedFalse()
        {
            InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);

            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal));

            Assert.AreEqual(1, handed.Count);
            Assert.AreSame(seneschal, handed[0].Buyer);
            Assert.IsFalse(handed[0].Announced);
        }

        [TestMethod]
        public void Buy_AnnouncedListing_PassesAnnouncedTrue()
        {
            InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);

            Assert.IsTrue(vendor.BuyItems_ValidateTransaction(Profile(rows[1]), seneschal));

            Assert.AreEqual(1, handed.Count);
            Assert.IsTrue(handed[0].Announced);
        }

        [TestMethod]
        public void Buy_HandlerRefusal_SendsLineAndReturnsFalse()
        {
            InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);
            handlerResult = false;

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal));
            Assert.AreEqual("handler refused", told.Last().Text);
        }

        [TestMethod]
        public void Buy_RealTryBuy_NullBuyerOrNpc_RefusesWithoutThrowing()
        {
            Assert.IsFalse(AvatarEventPurchase.TryBuy(null, null, false, out var line));
            Assert.AreEqual(AvatarEventPurchase.LineFor(AvatarEventRefusal.NotPermitted, T), line);
        }

        [TestMethod]
        public void Buy_UnknownGuid_Refused()
        {
            InitActive();
            var vendor = MakeVendor();
            Panel(vendor, seneschal);

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(new List<ItemProfile> { new ItemProfile(1, 0xDEADBEEF) }, seneschal));
            Assert.AreEqual(0, handed.Count);
            Assert.AreEqual(AvatarVendor.NotForSaleMessage, told.Last().Text);
        }

        [TestMethod]
        public void Buy_MoreThanOneRowOrAmount_Refused()
        {
            InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(new List<ItemProfile> { Profile(rows[0])[0], Profile(rows[1])[0] }, seneschal));
            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0], amount: 2), seneschal));
            Assert.AreEqual(0, handed.Count);
        }

        [TestMethod]
        public void Buy_AvatarRebound_RefusedAtMomentOfSale()
        {
            var state = InitActive();
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);

            // The Avatar is absorbed or rebound to another monarch after the panel was shown.
            state.MonarchGuid = 3000;

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal));
            Assert.AreEqual(0, handed.Count);
            Assert.AreEqual(AvatarVendor.OtherAllegianceMessage, told.Last().Text);

            state.MonarchGuid = Monarch;
            state.State = AvatarBindState.Absorbed;

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal), "a non-Bound Avatar sells nothing");
            Assert.AreEqual(0, handed.Count);
        }

        // ---- use and status -------------------------------------------------------------------------------------

        [TestMethod]
        public void Use_MemberGetsStatusLines()
        {
            InitActive(favor: 1234);
            var vendor = MakeVendor();
            var opened = new List<Player>();
            vendor.PanelOpener = opened.Add;

            vendor.ActOnUse(plain);

            var lines = told.Where(t => t.To == plain).Select(t => t.Text).ToList();
            Assert.IsTrue(lines.Any(l => l.StartsWith("Allegiance Avatar - level")), string.Join(" | ", lines));
            Assert.IsTrue(lines.Any(l => l.StartsWith("Favor: 1234 / 20000")), string.Join(" | ", lines));
            Assert.IsTrue(lines.Any(l => l.StartsWith("Keeper: Varric")), string.Join(" | ", lines));
            Assert.AreEqual(1, opened.Count, "the panel opens after the lines");
        }

        [TestMethod]
        public void Use_Seated_GetsStatusLinesAndNoPanel()
        {
            InitActive(favor: 1234);
            var vendor = MakeVendor();
            vendor.IsSeated = true;
            var opened = new List<Player>();
            vendor.PanelOpener = opened.Add;

            vendor.ActOnUse(plain);

            var lines = told.Where(t => t.To == plain).Select(t => t.Text).ToList();
            Assert.IsTrue(lines.Any(l => l.StartsWith("Allegiance Avatar - level")), string.Join(" | ", lines));
            Assert.AreEqual(AvatarVendor.SeatedNoTradeMessage, lines.Last());
            Assert.AreEqual(0, opened.Count, "the seated figure never opens the panel");
        }

        [TestMethod]
        public void Buy_Seated_Refused()
        {
            InitActive(favor: 1234);
            var vendor = MakeVendor();
            var rows = Panel(vendor, seneschal);
            told.Clear();
            vendor.IsSeated = true;

            Assert.IsFalse(vendor.BuyItems_ValidateTransaction(Profile(rows[0]), seneschal));
            Assert.AreEqual(0, handed.Count, "a valid row on the seated figure never reaches the hand-off");
            CollectionAssert.AreEqual(new[] { AvatarVendor.SeatedNoTradeMessage }, told.Select(t => t.Text).ToArray());
        }

        [TestMethod]
        public void Use_NonMember_GetsRefusalAndNoPanel()
        {
            InitActive();
            var vendor = MakeVendor();
            var opened = new List<Player>();
            vendor.PanelOpener = opened.Add;

            vendor.ActOnUse(outsider);

            CollectionAssert.AreEqual(new[] { AvatarVendor.OtherAllegianceMessage }, told.Select(t => t.Text).ToArray());
            Assert.AreEqual(0, opened.Count);
        }

        [TestMethod]
        public void Use_ClosedForBusiness_TellsClosed_NoStatusNoPanel()
        {
            InitActive();
            var vendor = MakeVendor();
            Assert.IsTrue(vendor.OpenForBusiness, "fixture: the vendor validates and opens");
            vendor.OpenForBusiness = false;
            var opened = new List<Player>();
            vendor.PanelOpener = opened.Add;

            vendor.ActOnUse(plain);

            CollectionAssert.AreEqual(new[] { AvatarVendor.ClosedMessage }, told.Select(t => t.Text).ToArray());
            Assert.AreEqual(0, opened.Count);
        }

        [TestMethod]
        public void StatusText_ShowsAvailableFavor_AndWhatIsHeldForAnEvent()
        {
            var state = InitActive(favor: 5000);

            Assert.IsTrue(AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a").Contains("Favor: 5000 / 20000."), "nothing reserved: no held note");
            Assert.IsTrue(AvatarStatusText.Lines(state, T, 30_718UL, id => "a").Contains("Favor: 5000 / 30718."), "the line shows the cap it is given, the Avatar's own");

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 2000));
            var lines = AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a");

            Assert.IsTrue(lines.Contains("Favor: 3000 / 20000 (2000 held for an event)."), string.Join(" | ", lines));
        }

        [TestMethod]
        public void StatusText_TopThreeByAccount()
        {
            store.Contributions.AddRange(new[]
            {
                Contribution(1, 300), Contribution(2, 900), Contribution(3, 100), Contribution(4, 500), Contribution(5, 50),
            });
            var state = InitActive();

            var lines = AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "Acct" + id);

            var top = lines.Where(l => l.StartsWith("  ")).ToList();
            CollectionAssert.AreEqual(new[] { "  1. Acct2 - 900", "  2. Acct4 - 500", "  3. Acct1 - 300" }, top);
        }

        [TestMethod]
        public void StatusText_NextUnlock_FollowsTheGateTunables()
        {
            var state = InitActive(lifetime: 0); // level 1

            AvatarStatusText.GauntletMinLevel = () => 1000;
            var low = AvatarStatusText.Lines(state, T with { EventMinLevel = 99 }, (ulong)T.FavorCap, id => "a");
            Assert.AreEqual("Next unlock: Private World Events at level 99.", low.Last());

            // The nearer gate wins.
            AvatarStatusText.GauntletMinLevel = () => 60;
            var nearer = AvatarStatusText.Lines(state, T with { EventMinLevel = 99 }, (ulong)T.FavorCap, id => "a");
            Assert.AreEqual("Next unlock: Allegiance Gauntlet at level 60.", nearer.Last());

            AvatarStatusText.GauntletMinLevel = () => 75;
            var event40 = AvatarStatusText.Lines(state, T with { EventMinLevel = 40 }, (ulong)T.FavorCap, id => "a");
            Assert.AreEqual("Next unlock: Private World Events at level 40.", event40.Last());

            AvatarStatusText.GauntletMinLevel = () => 0;
            var reached = AvatarStatusText.Lines(state, T with { EventMinLevel = 0 }, (ulong)T.FavorCap, id => "a");
            Assert.AreEqual("Next unlock: More perks are coming.", reached.Last());
        }

        [TestMethod]
        public void StatusText_LevelLine_HasNoCeiling()
        {
            var state = InitActive(lifetime: 0);

            var lines = AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a");

            Assert.AreEqual("Allegiance Avatar - level 1.", lines[0]);
        }

        [TestMethod]
        public void StatusText_ActingKeeperNote_AndAscii()
        {
            var state = InitActive();
            state.KeeperIsActing = true;

            var lines = AvatarStatusText.Lines(state, T, (ulong)T.FavorCap, id => "a");

            Assert.IsTrue(lines.Any(l => l.StartsWith("Keeper:") && l.Contains("acting")));
            Assert.IsTrue(lines.All(l => l.All(c => c < 128)), "in-game text is ASCII");
        }

        [TestMethod]
        public void AccountDisplayName_IsFirstNameIgnoringCase()
        {
            AvatarVendor.AccountNames = id => new[] { "bravo", "Zed", "Alpha", "alpine" };
            Assert.AreEqual("Alpha", AvatarVendor.AccountDisplayName(1));

            AvatarVendor.AccountNames = id => new[] { "beta", "Alpha2", "alpha1" };
            Assert.AreEqual("alpha1", AvatarVendor.AccountDisplayName(1), "lower-case a sorts with A, not after Z");

            AvatarVendor.AccountNames = id => null;
            Assert.IsNull(AvatarVendor.AccountDisplayName(1));
        }

        [TestMethod]
        public void Listings_FailedBuildLeavesNothing_AndRetries()
        {
            InitActive();
            var vendor = MakeVendor();
            var calls = 0;
            AvatarVendor.ListingBuilder = () => ++calls == 2 ? throw new InvalidOperationException("boom") : MakeDisplay();

            var shown = new List<WorldObject>();
            vendor.ForEachItemFor(seneschal, shown.Add);
            Assert.AreEqual(0, shown.Count, "a failed build shows nothing");
            Assert.AreEqual(2, calls, "the second listing was the one that failed");

            AvatarVendor.ListingBuilder = MakeDisplay;
            Assert.AreEqual(3, Panel(vendor, seneschal).Count, "the build is retried, not latched closed");
        }

        [TestMethod]
        public void Inert_NothingIsAccepted()
        {
            InitActive();
            var vendor = MakeVendor();

            Assert.IsFalse(vendor.CanAccept(null, plain, out var reason));
            Assert.AreEqual(AvatarVendor.BuysNothingMessage, reason);
            Assert.AreEqual(0, vendor.CalculatePayoutCoinAmount(new Dictionary<uint, WorldObject>()));
        }

        private static AllegianceAvatarContribution Contribution(uint account, long credited) => new AllegianceAvatarContribution
        {
            AvatarId = AvatarId, AccountId = account, Day = Today, RawXp = (ulong)credited, Credited = (ulong)credited, LastCharacterId = 1,
        };

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatarContribution> Contributions = new List<AllegianceAvatarContribution>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => Contributions.Where(c => c.Day >= dayUtc).ToList();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}

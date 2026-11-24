using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.SalvageRoller;
using ACE.Server.Tests.Pvp;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Salvage Roller shared station (Docs/SalvageRoller/DESIGN.md 4, 5.1, 5.4): the ONE placed vendor that serves
    /// every player their own view. Mirrors the PersonalVendorTests shape for the invariants the station copies (zero
    /// costs, inert overrides, ethereal/unattackable, factory hook, persistence exclusion), then covers what is the
    /// station's own: the per-viewer row model and its registry invariant, viewer isolation (the concurrency pin), the
    /// view check on every transaction, the search filter, the take validation, eviction and the minimal hand-back.
    ///
    /// Constructing any Vendor needs GameTables, vendor_shop_uses_generator cached, and the coin weenie in
    /// the world cache (see PersonalVendorTests' class remarks). Every PropertyManager key this class reads is
    /// seeded in TestInitialize and restored to its shipped default in TestCleanup, so the class passes alone.
    /// </summary>
    [TestClass]
    public class SalvageRollerVendorTests
    {
        private const string RotKey = "salvage_roller_window_rot_seconds";
        private const string MaxCandidatesKey = "salvage_roller_max_candidates";
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        /// <summary>Viewer A is the seeded player's own guid; viewer B is a second character.</summary>
        internal const uint GuidA = PvpTemplateCoreTests.PlayerGuid;
        internal const uint GuidB = 0x50000002;

        private static uint nextGuid = 0x71C00000;

        private static uint NextGuid() => ++nextGuid;

        private Func<Player, SalvageRollerVendor, int, bool> savedTakeHandler;
        private SalvageRollerVendor.SellBasketVerifier savedVerifier;

        [TestInitialize]
        public void Setup()
        {
            SeedVendorConstructible();

            Assert.IsTrue(PropertyManager.ModifyLong(RotKey, 600), $"{RotKey} is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong(MaxCandidatesKey, 100), $"{MaxCandidatesKey} is missing from DefaultLongProperties");

            savedTakeHandler = SalvageRollerVendor.TakeHandler;
            savedVerifier = SalvageRollerVendor.BasketVerifier;
        }

        [TestCleanup]
        public void Cleanup()
        {
            SalvageRollerVendor.TakeHandler = savedTakeHandler;
            SalvageRollerVendor.BasketVerifier = savedVerifier;

            PropertyManager.ModifyLong(RotKey, DefaultPropertyManager.DefaultLongProperties[RotKey].Item);
            PropertyManager.ModifyLong(MaxCandidatesKey, DefaultPropertyManager.DefaultLongProperties[MaxCandidatesKey].Item);
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        // ======================================================================================
        // fixtures
        // ======================================================================================

        /// <summary>The three things Vendor's constructor chain needs; see PersonalVendorTests.EnsureVendorConstructible.</summary>
        internal static void SeedVendorConstructible()
        {
            TestGameTables.EnsureInitialized();

            Assert.IsTrue(PropertyManager.ModifyBool(ShopGeneratorKey, false), $"{ShopGeneratorKey} is missing from DefaultBooleanProperties");

            SeedWorldWeenie((uint)WeenieClassName.W_COINSTACK_CLASS, WeenieType.Coin);
        }

        private static void SeedWorldWeenie(uint wcid, WeenieType type)
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);

            if (!dict.ContainsKey(wcid))
                dict[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = type };
        }

        /// <summary>
        /// The station weenie's shape: type 10 (Creature), as the content ships it, with the vendor property set and, opted
        /// in, bool 9089. <paramref name="missing"/> leaves one int row out, to build a station that is not open for business.
        /// </summary>
        internal static Weenie MakeStationWeenie(bool optIn = true, WeenieType type = WeenieType.Creature, PropertyInt? missing = null)
        {
            var properties = new Dictionary<PropertyBool, bool>();

            if (optIn)
                properties[PropertyBool.SalvageRollerStation] = true;

            var weenie = new Weenie
            {
                WeenieClassId = 1007800,
                WeenieType = type,
                PropertiesBool = properties,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 1073774863 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, int.MaxValue },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.BuyPrice, 0 },
                    { PropertyFloat.SellPrice, 0 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Salvage Roller" },
                },
            };

            if (missing != null)
                weenie.PropertiesInt.Remove(missing.Value);

            return weenie;
        }

        /// <summary>
        /// The station with its two outside lookups replaced: a viewer is online only when registered in
        /// <see cref="Online"/>, and out of range only when listed in <see cref="OutOfRange"/>. Every approach is
        /// recorded with what the viewer's view held at that moment.
        /// </summary>
        internal class TestStation : SalvageRollerVendor
        {
            public readonly Dictionary<uint, Player> Online = new Dictionary<uint, Player>();

            public readonly HashSet<uint> OutOfRange = new HashSet<uint>();

            public readonly List<(Player Player, VendorType Action, bool HadView, int Rows)> Approaches = new List<(Player, VendorType, bool, int)>();

            public TestStation(Weenie weenie, ObjectGuid guid) : base(weenie, guid) { }

            protected override Player ResolveViewer(uint viewerGuid) => Online.TryGetValue(viewerGuid, out var p) ? p : null;

            protected override bool IsViewerOutOfRange(Player viewer) => OutOfRange.Contains(viewer.Guid.Full);

            /// <summary>Run by the sweep between its drop decision and the drop itself.</summary>
            public Action<uint> SweepDropping;

            protected override void OnSweepDropping(uint viewerGuid) => SweepDropping?.Invoke(viewerGuid);

            public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)
            {
                if (player != null)
                    Approaches.Add((player, action, HasView(player), RowCount(player.Guid.Full)));

                base.ApproachVendor(player, action, altCurrencySpent);
            }

            /// <summary>What the station's Use does for the view, minus the manager fill and the panel.</summary>
            public void Open(Player player)
            {
                Online[player.Guid.Full] = player;
                OpenView(player.Guid.Full);
            }
        }

        internal static TestStation MakeStation(params Player[] viewers)
        {
            SeedVendorConstructible();

            var station = new TestStation(MakeStationWeenie(), new ObjectGuid(NextGuid()));

            foreach (var viewer in viewers)
                station.Open(viewer);

            return station;
        }

        /// <summary>A seeded player with its own guid, so two viewers are two characters.</summary>
        internal static Player MakePlayer(uint guid)
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();

            if (guid != PvpTemplateCoreTests.PlayerGuid)
                PvpTemplatePlayerTests.SeedWorldObject(player, PvpTemplateCoreTests.OwnBiota(), guid);

            return player;
        }

        /// <summary>A display object in the static guid range, so Destroy never reaches the dynamic guid allocator.</summary>
        private static WorldObject MakeDisplay(string name, ItemType itemType = ItemType.MeleeWeapon)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 8100,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.Value, 100 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        private sealed class Cand : ISalvageRollerRowCandidate
        {
            public Cand(int seq, string name)
            {
                Seq = seq;
                Name = name;
            }

            public int Seq { get; }
            public string Name { get; }
        }

        /// <summary>Sets one row per name on the viewer's view, seq 1..n, and returns the display objects in row order.</summary>
        private static List<WorldObject> SetRows(SalvageRollerVendor station, uint viewer, params string[] names)
        {
            var built = new List<WorldObject>();

            station.SetRows(viewer, names.Select((n, i) => new Cand(i + 1, n)), c =>
            {
                var display = MakeDisplay(c.Name);
                built.Add(display);
                return display;
            });

            return built;
        }

        private static List<WorldObject> Emitted(Vendor vendor, Player viewer)
        {
            var emitted = new List<WorldObject>();
            vendor.ForEachItemFor(viewer, emitted.Add);
            return emitted;
        }

        private static List<ItemProfile> Buy(params (uint guid, int amount)[] lines)
        {
            return lines.Select(l => new ItemProfile(l.amount, l.guid)).ToList();
        }

        /// <summary>
        /// The registry invariant: the keys of DefaultItemsForSale equal the union of every view's display guids, and no
        /// display guid belongs to two views.
        /// </summary>
        internal static void AssertRegistryMatchesViews(SalvageRollerVendor station, string when)
        {
            var union = new List<ObjectGuid>();

            foreach (var viewer in station.ViewerGuids())
                union.AddRange(station.DisplayGuidsOf(viewer));

            Assert.AreEqual(union.Count, union.Distinct().Count(), $"{when}: a display guid belongs to two views");
            CollectionAssert.AreEquivalent(union, station.DefaultItemsForSale.Keys.ToList(), $"{when}: DefaultItemsForSale must equal the union of the views' displays");
            Assert.AreEqual(0, station.UniqueItemsForSale.Count, $"{when}: UniqueItemsForSale is never populated");
        }

        private sealed class RecordingStation : TestStation
        {
            public readonly List<WorldObject> HandedBack = new List<WorldObject>();

            /// <summary>Items whose hand-back throws, to prove one failure does not skip the rest.</summary>
            public readonly HashSet<WorldObject> ThrowFor = new HashSet<WorldObject>();

            public int HandBackFailedTold;

            public int HandBackUnsavedTold;

            /// <summary>Report every placement as done, so the hand-back reaches the save.</summary>
            public bool PlacementSucceeds;

            /// <summary>Make the failed-hand-back tell itself throw.</summary>
            public bool TellThrows;

            public RecordingStation(Weenie weenie, ObjectGuid guid) : base(weenie, guid) { }

            protected override bool TryHandBack(WorldObject item, Player player)
            {
                HandedBack.Add(item);

                if (ThrowFor.Contains(item))
                    throw new InvalidOperationException("hand-back failure injected by the test");

                if (PlacementSucceeds)
                    return true;

                return base.TryHandBack(item, player);
            }

            protected override void TellHandBackFailed(Player player)
            {
                HandBackFailedTold++;

                if (TellThrows)
                    throw new InvalidOperationException("tell failure injected by the test");

                base.TellHandBackFailed(player);
            }

            protected override void TellHandBackUnsaved(Player player)
            {
                HandBackUnsavedTold++;
                base.TellHandBackUnsaved(player);
            }

            /// <summary>The production TellHandBackFailed, uncounted.</summary>
            public void TellProduction(Player player) => base.TellHandBackFailed(player);
        }

        private static RecordingStation MakeRecordingStation()
        {
            SeedVendorConstructible();

            return new RecordingStation(MakeStationWeenie(), new ObjectGuid(NextGuid()));
        }

        // ======================================================================================
        // PersonalVendor invariants
        // ======================================================================================

        [TestMethod]
        public void Factory_WithTheStationBool_BuildsASalvageRollerVendor()
        {
            SeedVendorConstructible();

            var wo = WorldObjectFactory.CreateWorldObject(MakeStationWeenie(), new ObjectGuid(NextGuid()));

            Assert.IsInstanceOfType(wo, typeof(SalvageRollerVendor));
        }

        [TestMethod]
        public void Factory_WithoutTheStationBool_BuildsAPlainVendor()
        {
            SeedVendorConstructible();

            var wo = WorldObjectFactory.CreateWorldObject(MakeStationWeenie(optIn: false, type: WeenieType.Vendor), new ObjectGuid(NextGuid()));

            Assert.IsInstanceOfType(wo, typeof(Vendor));
            Assert.IsFalse(wo is SalvageRollerVendor, "without PropertyBool.SalvageRollerStation the weenie must build a plain Vendor");
            Assert.IsFalse(wo is PersonalVendor);
        }

        [TestMethod]
        public void Factory_TypeTenWeenieWithTheStationBool_BuildsASalvageRollerVendor()
        {
            SeedVendorConstructible();

            var weenie = MakeStationWeenie();
            Assert.AreEqual(WeenieType.Creature, weenie.WeenieType, "sanity: the content ships the station as type 10");

            var wo = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(NextGuid()));

            Assert.IsInstanceOfType(wo, typeof(SalvageRollerVendor), "bool 9089 decides the class, not WeenieType");
            Assert.IsTrue(((SalvageRollerVendor)wo).OpenForBusiness, "the vendor rows make the type-10 station open for business");
        }

        [TestMethod]
        public void Factory_BiotaArm_TypeTenBiotaWithTheStationBool_BuildsASalvageRollerVendor()
        {
            SeedVendorConstructible();

            var weenie = MakeStationWeenie();
            var biota = new Biota
            {
                Id = NextGuid(),
                WeenieClassId = weenie.WeenieClassId,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>(weenie.PropertiesBool),
                PropertiesInt = new Dictionary<PropertyInt, int>(weenie.PropertiesInt),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(weenie.PropertiesFloat),
                PropertiesString = new Dictionary<PropertyString, string>(weenie.PropertiesString),
            };

            // The biota constructor chain loads inventory through DatabaseManager.Shard (Container.cs), which this host does
            // not have, so construction cannot finish here. What the arm routes to is still observable: the constructor
            // the factory entered is on the stack of the exception the missing shard raises.
            StringAssert.Contains(ConstructorEntered(biota), "SalvageRollerVendor..ctor", "a stray type-10 station row must be built as the shared station");

            biota.PropertiesBool.Remove(PropertyBool.SalvageRollerStation);
            biota.Id = NextGuid();

            var control = ConstructorEntered(biota);
            Assert.IsFalse(control.Contains("SalvageRollerVendor..ctor"), "control: without 9089 a type-10 biota is not the station");
            StringAssert.Contains(control, "Creature..ctor", "control: it takes the Creature arm");
        }

        private static string ConstructorEntered(Biota biota)
        {
            try
            {
                var wo = WorldObjectFactory.CreateWorldObject(biota);
                return wo == null ? "" : $"{wo.GetType().Name}..ctor (completed)";
            }
            catch (Exception ex)
            {
                return ex.StackTrace ?? "";
            }
        }

        [TestMethod]
        public void Factory_BothArmsKeyOnTheStationBool_AheadOfTheTypeSwitch_AndTheRetiredWindowArmIsGone()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/Factories/WorldObjectFactory.cs")));

            var weenieCheck = code.IndexOf("if (weenie.GetProperty(PropertyBool.SalvageRollerStation) == true)", StringComparison.Ordinal);
            var weenieSwitch = code.IndexOf("switch (objWeenieType)", StringComparison.Ordinal);
            var biotaCheck = code.IndexOf("biota.PropertiesBool.TryGetValue(PropertyBool.SalvageRollerStation, out var isRollerStation) && isRollerStation)", StringComparison.Ordinal);
            var biotaSwitch = code.IndexOf("switch (biota.WeenieType)", StringComparison.Ordinal);

            Assert.IsTrue(weenieCheck > 0 && weenieCheck < weenieSwitch, $"the weenie arm must test 9089 before its type switch (check={weenieCheck}, switch={weenieSwitch})");
            Assert.IsTrue(biotaCheck > 0 && biotaCheck < biotaSwitch, $"the biota arm must test 9089 before its type switch (check={biotaCheck}, switch={biotaSwitch})");
            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("return new SalvageRollerVendor(weenie, guid);")).Count);
            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("return new SalvageRollerVendor(biota);")).Count);
            Assert.IsFalse(code.Contains("SalvageRollerWindow"), "the retired per-player window arm must be gone");
        }

        [TestMethod]
        public void ActOnUse_AStationMissingAVendorRow_IsClosed_OpensNoView_AndNeverApproaches()
        {
            SeedVendorConstructible();

            var player = MakePlayer(GuidA);
            var closed = new TestStation(MakeStationWeenie(missing: PropertyInt.MerchandiseMinValue), new ObjectGuid(NextGuid()));

            Assert.IsFalse(closed.OpenForBusiness, "Vendor.ValidateVendorRequirements closes a vendor missing int 75");

            closed.ActOnUse(player);

            Assert.AreEqual(0, closed.Approaches.Count, "a closed station never sends a panel");
            Assert.IsFalse(closed.HasView(player), "and never opens a view");
            Assert.IsNull(player.CurrentSalvageRollerStation);
        }

        [TestMethod]
        public void ActOnUse_SourcePin_TheOpenForBusinessGuardRunsFirst()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs")));
            var use = PooledLootSourceText.MethodBody(code, "public override void ActOnUse(WorldObject wo)");

            var guard = use.IndexOf("if (!OpenForBusiness)", StringComparison.Ordinal);
            var open = use.IndexOf("SalvageRollerStation.Open(player, this);", StringComparison.Ordinal);

            Assert.IsTrue(guard > 0 && guard < open, $"the OpenForBusiness guard must precede the open (guard={guard}, open={open})");
            StringAssert.Contains(use, "Tell(player, NotOpenMessage);");
        }

        [TestMethod]
        public void Costs_AreAllZero()
        {
            var station = MakeStation();
            var item = MakeDisplay("Priced Sword");

            Assert.AreEqual(0u, station.GetSellCost(item));
            Assert.AreEqual(0u, station.GetSellCost(MakeStationWeenie(false)));
            Assert.AreEqual(0, station.GetBuyCost(item));
            Assert.AreEqual(0, station.GetBuyCost(MakeStationWeenie(false)));
            Assert.AreEqual(0, station.CalculatePayoutCoinAmount(new Dictionary<uint, WorldObject> { { item.Guid.Full, item } }));
        }

        [TestMethod]
        public void Ethereal_And_NotAttackable_AreSetInCode()
        {
            var station = MakeStation();

            Assert.IsTrue(station.Ethereal);
            Assert.IsFalse(station.Attackable);
        }

        [TestMethod]
        public void LoadInventory_RotUniques_AndPrepareResetToHome_AreInert()
        {
            var station = MakeStation();

            var unique = MakeDisplay("Rottable");
            unique.SoldTimestamp = Time.GetUnixTime() - 10_000;
            station.UniqueItemsForSale.Add(unique.Guid, unique);

            typeof(Vendor).GetMethod("LoadInventory", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(station, null);
            typeof(Vendor).GetMethod("RotUniques", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(station, null);
            typeof(Vendor).GetMethod("PrepareResetToHome", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(station, null);

            Assert.AreEqual(0, station.DefaultItemsForSale.Count, "LoadInventory must not stock anything");
            Assert.IsTrue(station.UniqueItemsForSale.ContainsKey(unique.Guid), "RotUniques must never remove anything");
            Assert.IsFalse(unique.IsDestroyed);

            var resetTimestamp = typeof(WorldObject).GetProperty("ResetTimestamp", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(station);
            Assert.IsNull(resetTimestamp, "PrepareResetToHome must never schedule a reset: the station has no Home for CheckResetToHome");
        }

        [TestMethod]
        public void Rotate_IsInert_SoOneUseNeverTurnsTheStationAwayFromAnother()
        {
            var station = MakeStation();
            var player = MakePlayer(GuidA);

            Assert.AreEqual(0f, station.Rotate(player));
        }

        [TestMethod]
        public void SourcePins_RotateAndPrepareResetToHomeAreInert_AndActOnUseNeverCallsBase()
        {
            var path = "Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs";
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree(path)));

            StringAssert.Contains(code, "public override float Rotate(WorldObject target) => 0f;");

            var reset = PooledLootSourceText.MethodBody(code, "protected override void PrepareResetToHome()");
            Assert.AreEqual("", Regex.Replace(reset, @"[\s{}]", ""), "PrepareResetToHome must stay empty: CheckResetToHome dereferences a Home the station does not have");

            // Vendor.ActOnUse keeps one lastPlayerInfo and runs CheckClose for a single customer; the shared station must
            // never reach it.
            var use = PooledLootSourceText.MethodBody(code, "public override void ActOnUse(WorldObject wo)");
            Assert.IsFalse(use.Contains("base."), "SalvageRollerVendor.ActOnUse must not call base");
            StringAssert.Contains(use, "player.IsBusy");
            StringAssert.Contains(use, "SalvageRollerStation.Open(player, this);");

            var heartbeat = PooledLootSourceText.MethodBody(code, "public override void Heartbeat(double currentUnixTime)");
            StringAssert.Contains(heartbeat, "base.Heartbeat(currentUnixTime);");
            StringAssert.Contains(heartbeat, "SweepViews(currentUnixTime);");
        }

        [TestMethod]
        public void IsNeverPersisted()
        {
            SeedVendorConstructible();

            var station = new SalvageRollerVendor(MakeStationWeenie(), new ObjectGuid(0x80F00001));

            Assert.IsTrue(station.Guid.IsDynamic(), "sanity: the exclusion is only meaningful for a dynamic guid");
            Assert.IsFalse(station.IsDynamicThatShouldPersistToShard());
        }

        [TestMethod]
        public void SellBypassProperties_StationBypassesBoth_MuleOnlyPriceGates_RetailNeither()
        {
            SeedVendorConstructible();

            var station = MakeStation();
            var plain = new Vendor(MakeStationWeenie(optIn: false), new ObjectGuid(NextGuid()));
            var mule = (PersonalVendor)WorldObjectFactory.CreateWorldObject(MuleWeenie(), new ObjectGuid(NextGuid()));

            Assert.IsTrue(station.SellBypassesPriceGates);
            Assert.IsTrue(station.SellBypassesAttunedRefusal);

            Assert.IsTrue(mule.SellBypassesPriceGates, "PersonalVendor behaviour must be unchanged: price gates bypassed");
            Assert.IsFalse(mule.SellBypassesAttunedRefusal, "PersonalVendor behaviour must be unchanged: Attuned still refused at a vault");

            Assert.IsFalse(plain.SellBypassesPriceGates);
            Assert.IsFalse(plain.SellBypassesAttunedRefusal);
        }

        private static Weenie MuleWeenie()
        {
            var weenie = MakeStationWeenie(optIn: false, type: WeenieType.Vendor);
            weenie.PropertiesBool[PropertyBool.PersonalVendor] = true;
            return weenie;
        }

        [TestMethod]
        public void ViewLifetime_HonoursTheConfiguredSeconds()
        {
            Assert.IsTrue(PropertyManager.ModifyLong(RotKey, 123));

            Assert.AreEqual(123.0, SalvageRollerVendor.ViewLifetimeSeconds());
        }

        [TestMethod]
        public void ViewLifetime_FallsBackTo60Seconds_WhenConfiguredNonPositive()
        {
            foreach (var bad in new long[] { 0, -1 })
            {
                Assert.IsTrue(PropertyManager.ModifyLong(RotKey, bad));

                Assert.AreEqual(60.0, SalvageRollerVendor.ViewLifetimeSeconds(), $"configured {bad} must fall back, never 'never expire' or instant expiry");
            }
        }

        // ======================================================================================
        // view check + CanAccept
        // ======================================================================================

        [TestMethod]
        public void CanAccept_OnlyAPlayerWithALiveView()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var item = MakeDisplay("Offered Sword");

            var station = MakeStation(a);

            Assert.IsTrue(station.CanAccept(item, a, out var reason), reason);
            Assert.IsNull(reason);

            Assert.IsFalse(station.CanAccept(item, b, out reason), "a player who never used the station has no view");
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);

            Assert.IsFalse(station.CanAccept(item, null, out reason), "no player never has a view");
        }

        [TestMethod]
        public void CanAccept_ResolvedAccessOverload_RoutesToTheViewCheck()
        {
            var b = MakePlayer(GuidB);
            var station = MakeStation(MakePlayer(GuidA));

            Assert.IsFalse(station.CanAccept(MakeDisplay("x"), b, ACE.Server.Entity.AccountVault.VaultAccess.DepositWithdraw, out var reason),
                "the overload the sell path actually calls must reach the view check");
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);
        }

        // ======================================================================================
        // rows
        // ======================================================================================

        [TestMethod]
        public void SetRows_DisplayObjectsAreRegisteredInDefaultItemsForSale_ParentedToTheStation()
        {
            var station = MakeStation();

            var displays = SetRows(station, GuidA, "Sword (#1)", "Sword (#2)", "Sword (#3)");

            Assert.AreEqual(3, station.RowCount(GuidA));
            CollectionAssert.AreEquivalent(displays.Select(d => d.Guid).ToList(), station.DefaultItemsForSale.Keys.ToList());
            AssertRegistryMatchesViews(station, "after SetRows");

            foreach (var display in displays)
                Assert.AreEqual(station.Guid.Full, display.ContainerId, "a display object is parented to the station, which is what the vendor root-owner guards key off");
        }

        [TestMethod]
        public void SetRows_CreatesTheViewIfAbsent()
        {
            var station = MakeStation();

            Assert.IsFalse(station.HasView(GuidA));

            SetRows(station, GuidA, "A");

            Assert.IsTrue(station.HasView(GuidA), "a roll answered after the view expired must still be shown");
        }

        [TestMethod]
        public void SetRows_MapsEachDisplayGuidToItsSeq_AndTheViewerResolvesIt()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);

            var displays = SetRows(station, GuidA, "A", "B", "C");

            for (var i = 0; i < displays.Count; i++)
            {
                Assert.IsTrue(station.TryGetRowSeq(GuidA, displays[i].Guid, out var seq));
                Assert.AreEqual(i + 1, seq);

                Assert.IsTrue(station.TryGetItemForSale(a, displays[i].Guid, out var found));
                Assert.AreSame(displays[i], found);
            }

            Assert.IsFalse(station.TryGetRowSeq(GuidA, new ObjectGuid(0x71CFFFFF), out _));
            Assert.IsFalse(station.TryGetItemForSale(a, new ObjectGuid(0x71CFFFFF), out _));
        }

        [TestMethod]
        public void GetItemsForSaleCount_MatchesForEachItemFor_InRowOrder()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);

            var displays = SetRows(station, GuidA, "Third Best", "Best", "Second Best");

            var emitted = Emitted(station, a);

            Assert.AreEqual(station.GetItemsForSaleCount(a), emitted.Count);
            CollectionAssert.AreEqual(displays, emitted, "rows are emitted in the order the caller supplied, so the caller owns the sort");
        }

        [TestMethod]
        public void SetRows_SkipsADuplicateSeq_AndANullDisplay()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);

            var candidates = new[] { new Cand(1, "one"), new Cand(1, "one again"), new Cand(2, "null display"), new Cand(3, "three") };

            var set = station.SetRows(GuidA, candidates, c => c.Name == "null display" ? null : MakeDisplay(c.Name));

            Assert.AreEqual(2, set);
            Assert.AreEqual(2, station.DefaultItemsForSale.Count);
            CollectionAssert.AreEqual(new[] { "one", "three" }, Emitted(station, a).Select(d => d.Name).ToList());
            AssertRegistryMatchesViews(station, "after a SetRows that skipped rows");
        }

        [TestMethod]
        public void SetRows_DestroysOnlyThatViewersPreviousRows()
        {
            var station = MakeStation();

            var first = SetRows(station, GuidA, "old 1", "old 2");
            var others = SetRows(station, GuidB, "B's row");
            var second = SetRows(station, GuidA, "new 1");

            Assert.IsTrue(first.All(d => d.IsDestroyed), "replaced display objects are disposable and must be destroyed");
            Assert.IsFalse(second[0].IsDestroyed);
            Assert.IsFalse(others[0].IsDestroyed, "another viewer's rows are never touched by a SetRows");
            Assert.AreEqual(1, station.RowCount(GuidB));
            Assert.AreEqual(2, station.DefaultItemsForSale.Count);
            Assert.IsFalse(station.TryGetRowSeq(GuidA, first[0].Guid, out _), "a replaced row's guid must no longer resolve");
            AssertRegistryMatchesViews(station, "after a second SetRows for A");
        }

        [TestMethod]
        public void SetRows_RefusesASavedDisplay_AndClearRowsNeverDestroysIt()
        {
            var station = MakeStation();

            var saved = MakeDisplay("looks real");
            typeof(WorldObject).GetProperty("LastRequestedDatabaseSave").GetSetMethod(true).Invoke(saved, new object[] { DateTime.UtcNow });
            Assert.IsTrue(saved.BiotaOriginatedFromOrHasBeenSavedToDatabase(), "sanity: the saved flag must be set for this test to mean anything");

            var fine = MakeDisplay("disposable");

            var set = station.SetRows(GuidA, new[] { new Cand(1, "saved"), new Cand(2, "fine") }, c => c.Seq == 1 ? saved : fine);

            Assert.AreEqual(1, set);
            Assert.IsFalse(station.DefaultItemsForSale.ContainsKey(saved.Guid), "a saved biota must never become a row");
            Assert.IsFalse(station.TryGetRowSeq(GuidA, saved.Guid, out _));
            Assert.IsNull(saved.ContainerId, "a refused object is left exactly as it was");

            station.ClearRows(GuidA);

            Assert.IsFalse(saved.IsDestroyed, "ClearRows must never destroy something that was refused as a row");
            Assert.IsTrue(fine.IsDestroyed, "positive control: the accepted display IS destroyed by ClearRows");
            Assert.IsTrue(station.HasView(GuidA), "ClearRows keeps the view");
            AssertRegistryMatchesViews(station, "after ClearRows");
        }

        [TestMethod]
        public void SetRows_RefusesAWieldedOrForeignParentedDisplay_ButAcceptsOneAlreadyParentedHere()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);

            var wielded = MakeDisplay("worn");
            wielded.WielderId = 0x50000001;

            var foreign = MakeDisplay("in a pack");
            foreign.ContainerId = 0x50000001;

            var here = MakeDisplay("already ours");
            here.ContainerId = station.Guid.Full;

            var byName = new Dictionary<string, WorldObject> { { "worn", wielded }, { "in a pack", foreign }, { "already ours", here } };

            var set = station.SetRows(GuidA, new[] { new Cand(1, "worn"), new Cand(2, "in a pack"), new Cand(3, "already ours") }, c => byName[c.Name]);

            Assert.AreEqual(1, set);
            CollectionAssert.AreEqual(new[] { here }, Emitted(station, a));
            Assert.AreEqual(0x50000001u, foreign.ContainerId, "a refused object's parent is not touched");

            station.ClearRows(GuidA);
            Assert.IsFalse(wielded.IsDestroyed);
            Assert.IsFalse(foreign.IsDestroyed);
        }

        [TestMethod]
        public void SetRows_RefusesADisplayThatIsAlreadyAnotherViewersRow()
        {
            var station = MakeStation();

            var aRows = SetRows(station, GuidA, "A's only row");

            var set = station.SetRows(GuidB, new[] { new Cand(1, "stolen") }, c => aRows[0]);

            Assert.AreEqual(0, set, "the duplicate-guid check is global: one display object is never a row of two views");
            Assert.IsFalse(station.TryGetRowSeq(GuidB, aRows[0].Guid, out _));
            Assert.IsTrue(station.TryGetRowSeq(GuidA, aRows[0].Guid, out _), "A's row is untouched");
            Assert.IsFalse(aRows[0].IsDestroyed);
            AssertRegistryMatchesViews(station, "after B tried to reuse A's display");
        }

        [TestMethod]
        public void AddDefaultItem_IsRefused()
        {
            var station = MakeStation();

            station.AddDefaultItem(MakeDisplay("Smuggled"));

            Assert.AreEqual(0, station.DefaultItemsForSale.Count, "rows enter only through SetRows");
        }

        // ======================================================================================
        // viewer isolation
        // ======================================================================================

        [TestMethod]
        public void ForEachItemFor_AndTheCount_ExcludeAnotherViewersRows()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1", "A2", "A3");
            var bRows = SetRows(station, GuidB, "B1");

            CollectionAssert.AreEqual(aRows, Emitted(station, a));
            CollectionAssert.AreEqual(bRows, Emitted(station, b), "B is shown only B's rows");
            Assert.AreEqual(3, station.GetItemsForSaleCount(a));
            Assert.AreEqual(1, station.GetItemsForSaleCount(b));
            AssertRegistryMatchesViews(station, "with two viewers");
        }

        [TestMethod]
        public void EveryViewerLessMember_FailsClosed_AndSoDoesAViewerWithNoView()
        {
            var a = MakePlayer(GuidA);
            var stranger = MakePlayer(GuidB);
            var station = MakeStation(a);

            var aRows = SetRows(station, GuidA, "A1", "A2");

            var viewerLess = new List<WorldObject>();
            station.forEachItem(viewerLess.Add);

            Assert.AreEqual(0, viewerLess.Count, "forEachItem with no viewer emits nothing");
            Assert.AreEqual(0, station.ItemsForSaleCount, "ItemsForSaleCount with no viewer is 0");
            Assert.IsFalse(station.TryGetItemForSale(aRows[0].Guid, out var found), "TryGetItemForSale with no viewer resolves nothing");
            Assert.IsNull(found);

            Assert.AreEqual(0, Emitted(station, null).Count);
            Assert.AreEqual(0, station.GetItemsForSaleCount(null));
            Assert.IsFalse(station.TryGetItemForSale(null, aRows[0].Guid, out _));

            Assert.AreEqual(0, Emitted(station, stranger).Count, "a player with no view is shown an empty panel");
            Assert.AreEqual(0, station.GetItemsForSaleCount(stranger));
            Assert.IsFalse(station.TryGetItemForSale(stranger, aRows[0].Guid, out _));
        }

        [TestMethod]
        public void TryGetItemForSale_ForAViewer_ResolvesTheirOwnRow_AndRefusesAnothersRow()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1");
            var bRows = SetRows(station, GuidB, "B1");

            Assert.IsTrue(station.TryGetItemForSale(a, aRows[0].Guid, out var mine));
            Assert.AreSame(aRows[0], mine);

            Assert.IsFalse(station.TryGetItemForSale(b, aRows[0].Guid, out var theirs), "FindObject's LastUsedContainer arm must not hand B one of A's rows");
            Assert.IsNull(theirs);
            Assert.IsTrue(station.TryGetItemForSale(b, bRows[0].Guid, out _));
        }

        [TestMethod]
        public void SearchFilter_IsPerViewer_ASearchLeavesTheOtherViewersRowsAlone()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            SetRows(station, GuidA, "Iron Sword", "Gold Dagger");
            var bRows = SetRows(station, GuidB, "Iron Axe", "Gold Ring", "Silver Ring");

            Assert.IsTrue(ItemTextSearch_TryCompile("iron", out var regex));
            Assert.IsTrue(station.SetSearchFilter(GuidA, regex));

            Assert.AreEqual(1, station.VisibleCount(GuidA));
            Assert.AreEqual(3, station.VisibleCount(GuidB), "A's search must leave B's visible rows unchanged");
            CollectionAssert.AreEqual(bRows, Emitted(station, b));
            Assert.IsNull(station.GetSearchFilter(GuidB));
        }

        // ======================================================================================
        // search filter
        // ======================================================================================

        [TestMethod]
        public void SearchFilter_ShowsOnlyMatchingRows_AndNullClearsIt()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);

            var displays = SetRows(station, GuidA, "Defensive Sword (#1)", "Plain Sword (#2)", "Defensive Axe (#3)");

            Assert.IsTrue(ItemTextSearch_TryCompile("defensive", out var regex));
            station.SetSearchFilter(GuidA, regex);

            var emitted = Emitted(station, a);
            CollectionAssert.AreEqual(new[] { displays[0], displays[2] }, emitted);
            Assert.AreEqual(2, station.GetItemsForSaleCount(a), "the count must follow the filter, or the approach packet disagrees with its own rows");

            station.SetSearchFilter(GuidA, null);
            Assert.AreEqual(3, Emitted(station, a).Count);
            Assert.AreEqual(3, station.GetItemsForSaleCount(a));
        }

        [TestMethod]
        public void SearchFilter_IsDisplayOnly_HiddenRowsStayHeldAndResolvable()
        {
            var station = MakeStation(MakePlayer(GuidA));

            var displays = SetRows(station, GuidA, "Match Me", "Hidden Row");

            Assert.IsTrue(ItemTextSearch_TryCompile("match", out var regex));
            station.SetSearchFilter(GuidA, regex);

            Assert.AreEqual(2, station.RowCount(GuidA));
            Assert.AreEqual(2, station.DefaultItemsForSale.Count, "the filter never removes a row");
            Assert.IsFalse(displays[1].IsDestroyed, "the filter never destroys a row");
            Assert.IsTrue(station.TryGetRowSeq(GuidA, displays[1].Guid, out var seq));
            Assert.AreEqual(2, seq);
        }

        [TestMethod]
        public void SearchFilter_SurvivesSetRows()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);

            Assert.IsTrue(ItemTextSearch_TryCompile("keep", out var regex));
            Assert.IsTrue(station.SetSearchFilter(GuidA, regex));

            SetRows(station, GuidA, "keep this", "drop this");

            CollectionAssert.AreEqual(new[] { "keep this" }, Emitted(station, a).Select(d => d.Name).ToList());
        }

        [TestMethod]
        public void SearchFilter_WithNoView_IsRefused_AndCreatesNoView()
        {
            var station = MakeStation();

            Assert.IsTrue(ItemTextSearch_TryCompile("x", out var regex));

            Assert.IsFalse(station.SetSearchFilter(GuidB, regex));
            Assert.IsFalse(station.HasView(GuidB), "only the station's Use creates a view");
        }

        [TestMethod]
        public void SearchFilter_MatchesAWeaponModsDisplayName_OnARowThatCarriesIt()
        {
            // #1961 put mod names into ItemTextSearch; the station filters with the same text, so a rolled result is
            // found by the mod it rolled, though the mod's name is nowhere in the row's own name
            var keys = new[] { "weapon_mod_magnitude_scale", "equipment_mod_potency_scale" };
            DefaultPropertyManager.LoadDefaultProperties();

            foreach (var key in keys)
                Assert.IsTrue(PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item), key);

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));

            try
            {
                var definition = ACE.Server.WeaponMods.WeaponModRegistry.Get(ACE.Server.WeaponMods.WeaponModId.DefensiveWeapon);
                var a = MakePlayer(GuidA);
                var station = MakeStation(a);
                var built = new List<WorldObject>();

                station.SetRows(GuidA, new[] { new Cand(1, "Roller Sword (#1, 120%)"), new Cand(2, "Roller Sword (#2, 80%)") }, c =>
                {
                    var display = new MeleeWeapon(new Weenie
                    {
                        WeenieClassId = 8101,
                        WeenieType = WeenieType.MeleeWeapon,
                        PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.MeleeWeapon }, { PropertyInt.Value, 100 } },
                        PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, c.Name } },
                    }, new ObjectGuid(NextGuid()));

                    if (c.Seq == 1)
                        ACE.Server.WeaponMods.WeaponModTinkerSet.ApplySpecial(display, definition, ACE.Server.WeaponMods.WeaponModValue.MaxMagnitude(definition));

                    built.Add(display);
                    return display;
                });

                Assert.AreEqual(2, station.RowCount(GuidA), "precondition: both rows were accepted");
                Assert.IsFalse(built[0].Name.Contains(definition.DisplayName), "precondition: the mod's name is not in the row's own name");

                Assert.IsTrue(ItemTextSearch_TryCompile(definition.DisplayName, out var regex));
                station.SetSearchFilter(GuidA, regex);

                CollectionAssert.AreEqual(new[] { built[0] }, Emitted(station, a), $"only the row carrying {definition.DisplayName} matches");
            }
            finally
            {
                foreach (var key in keys)
                    PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

                PropertyManager.ModifyBool("weapon_mods_enabled", DefaultPropertyManager.DefaultBooleanProperties["weapon_mods_enabled"].Item);
            }
        }

        private static bool ItemTextSearch_TryCompile(string text, out Regex regex)
        {
            return ACE.Server.Managers.Market.ItemTextSearch.TryCompile(text, out regex, out _);
        }

        // ======================================================================================
        // take (buy)
        // ======================================================================================

        [TestMethod]
        public void TryResolveTake_ViewerOneRowAmountOne_ResolvesTheSeq()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var displays = SetRows(station, GuidA, "A", "B", "C");

            Assert.IsTrue(station.TryResolveTake(a, Buy((displays[1].Guid.Full, 1)), out var seq, out var refusal), refusal);
            Assert.AreEqual(2, seq);
        }

        [TestMethod]
        public void TryResolveTake_RefusesAPlayerWithNoView()
        {
            var stranger = MakePlayer(GuidB);
            var station = MakeStation(MakePlayer(GuidA));
            var displays = SetRows(station, GuidA, "A");

            Assert.IsFalse(station.TryResolveTake(stranger, Buy((displays[0].Guid.Full, 1)), out _, out var refusal));
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, refusal);
        }

        [TestMethod]
        public void TryResolveTake_RefusesAnythingButExactlyOneProfileWithAmountOne()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var displays = SetRows(station, GuidA, "A", "B");

            var baskets = new[]
            {
                Buy((displays[0].Guid.Full, 1), (displays[1].Guid.Full, 1)),
                Buy((displays[0].Guid.Full, 1), (displays[0].Guid.Full, 1)),
                Buy((displays[0].Guid.Full, 2)),
                Buy((displays[0].Guid.Full, 0)),
                Buy(),
                null,
            };

            foreach (var basket in baskets)
            {
                Assert.IsFalse(station.TryResolveTake(a, basket, out _, out var refusal), $"basket of {basket?.Count.ToString() ?? "null"} must be refused");
                Assert.AreEqual(SalvageRollerVendor.TakeExactlyOneMessage, refusal);
            }
        }

        [TestMethod]
        public void TryResolveTake_RefusesAnUnknownRow()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            SetRows(station, GuidA, "A");

            Assert.IsFalse(station.TryResolveTake(a, Buy((0x71CFFFFE, 1)), out _, out var refusal));
            Assert.AreEqual(SalvageRollerVendor.UnknownRowMessage, refusal);
        }

        [TestMethod]
        public void TryResolveTake_AcceptsARowTheFilterHides()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var displays = SetRows(station, GuidA, "Shown", "Hidden");

            Assert.IsTrue(ItemTextSearch_TryCompile("shown", out var regex));
            station.SetSearchFilter(GuidA, regex);

            Assert.IsTrue(station.TryResolveTake(a, Buy((displays[1].Guid.Full, 1)), out var seq, out var refusal), refusal);
            Assert.AreEqual(2, seq);
        }

        [TestMethod]
        public void BuyItems_ValidateTransaction_HandsTheSeqToTakeHandler_OnlyAfterValidation()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var displays = SetRows(station, GuidA, "A", "B", "C");

            var calls = new List<(Player player, SalvageRollerVendor station, int seq)>();
            SalvageRollerVendor.TakeHandler = (p, s, k) => { calls.Add((p, s, k)); return true; };

            Assert.IsTrue(station.BuyItems_ValidateTransaction(Buy((displays[2].Guid.Full, 1)), a));
            Assert.AreEqual(1, calls.Count);
            Assert.AreSame(a, calls[0].player);
            Assert.AreSame(station, calls[0].station);
            Assert.AreEqual(3, calls[0].seq);

            Assert.IsFalse(station.BuyItems_ValidateTransaction(Buy((displays[0].Guid.Full, 1), (displays[1].Guid.Full, 1)), a));
            Assert.AreEqual(1, calls.Count, "a multi-profile buy must be refused before TakeHandler is reached");

            Assert.IsFalse(station.BuyItems_ValidateTransaction(Buy((displays[0].Guid.Full, 1)), MakePlayer(GuidB)));
            Assert.AreEqual(1, calls.Count, "a buy from a player with no view must be refused before TakeHandler is reached");
        }

        /// <summary>
        /// The concurrency pin. A and B each hold a view on the one station. B's buy names A's display guid: it must be
        /// refused as an unknown row, the take must never run, and A's view must be exactly as it was.
        /// </summary>
        [TestMethod]
        public void ConcurrencyPin_BBuyingAsRowGuid_IsRefused_TakeNeverRuns_AndAsViewIsUnchanged()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1", "A2", "A3");
            station.SetRows(GuidB, new[] { new Cand(7, "B7") }, c => MakeDisplay(c.Name));

            var calls = 0;
            SalvageRollerVendor.TakeHandler = (p, s, k) => { calls++; return true; };

            Assert.IsFalse(station.TryResolveTake(b, Buy((aRows[1].Guid.Full, 1)), out _, out var refusal));
            Assert.AreEqual(SalvageRollerVendor.UnknownRowMessage, refusal);

            Assert.IsFalse(station.BuyItems_ValidateTransaction(Buy((aRows[1].Guid.Full, 1)), b));
            Assert.AreEqual(0, calls, "TakeHandler must never be invoked for another viewer's row");

            Assert.AreEqual(3, station.RowCount(GuidA));
            CollectionAssert.AreEqual(aRows, Emitted(station, a));
            Assert.IsTrue(aRows.All(d => !d.IsDestroyed));
            Assert.IsTrue(station.TryGetRowSeq(GuidA, aRows[1].Guid, out var aSeq) && aSeq == 2);
            AssertRegistryMatchesViews(station, "after B's refused buy");
        }

        /// <summary>
        /// The concurrency pin, same-seq case: B's own batch ALSO has a #2. A global display-guid-to-seq map would resolve
        /// A's #2 guid to seq 2 and silently take B's own #2. The per-view lookup must refuse it instead.
        /// </summary>
        [TestMethod]
        public void ConcurrencyPin_BBuyingAsRowGuid_IsRefused_EvenWhenBsOwnBatchHasTheSameSeq()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1", "A2", "A3");
            var bRows = SetRows(station, GuidB, "B1", "B2", "B3");

            var calls = new List<int>();
            SalvageRollerVendor.TakeHandler = (p, s, k) => { calls.Add(k); return true; };

            Assert.IsFalse(station.TryResolveTake(b, Buy((aRows[1].Guid.Full, 1)), out var seq, out var refusal), $"resolved to seq {seq}");
            Assert.AreEqual(SalvageRollerVendor.UnknownRowMessage, refusal);

            Assert.IsFalse(station.BuyItems_ValidateTransaction(Buy((aRows[1].Guid.Full, 1)), b));
            CollectionAssert.AreEqual(new List<int>(), calls, "B's own #2 must not be taken through A's #2 guid");

            Assert.AreEqual(3, station.RowCount(GuidA));
            Assert.AreEqual(3, station.RowCount(GuidB));
            Assert.IsTrue(aRows.Concat(bRows).All(d => !d.IsDestroyed));

            // positive control: B's own #2 guid does resolve, to 2
            Assert.IsTrue(station.TryResolveTake(b, Buy((bRows[1].Guid.Full, 1)), out seq, out refusal), refusal);
            Assert.AreEqual(2, seq);
        }

        [TestMethod]
        public void TakeHandler_DefaultsToTheManagersTake()
        {
            // The default handler is the batch state machine. It reads the shard, so it is pinned at the source here and
            // driven through fakes in SalvageRollerBatchTests.
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs")));

            var iDecl = code.IndexOf("private static bool DefaultTakeHandler(", StringComparison.Ordinal);
            Assert.IsTrue(iDecl >= 0, "DefaultTakeHandler was not found");

            var body = code.Substring(iDecl, code.IndexOf('}', iDecl) - iDecl);
            StringAssert.Contains(body, "return SalvageRollerManager.TakeFromStation(player, station, seq);");
            Assert.IsFalse(body.Contains("TakeUnavailableMessage"), "the step-6 refusal must be gone from the default take");
        }

        [TestMethod]
        public void EveryLossLine_InTheStation_CarriesTheRefundPrefix_OnTheSalvageRollerLogger()
        {
            // Step 7 review F5: staff find refund work by grepping one prefix on one logger. A loss logged under the
            // station's own logger with a different tag is a loss nobody finds.
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs")));

            foreach (var marker in new[] { "ORPHAN:", "the sell pipeline threw for" })
            {
                var lines = code.Split('\n').Where(l => l.Contains(marker)).ToList();

                Assert.IsTrue(lines.Count > 0, $"no line logs \"{marker}\"");

                foreach (var line in lines)
                {
                    StringAssert.Contains(line, "SalvageRollerManager.Log.Error(", $"a \"{marker}\" line must log through the Salvage Roller logger: {line.Trim()}");
                    StringAssert.Contains(line, "{SalvageRollerManager.RefundPrefix}", $"a \"{marker}\" line must carry the refund prefix: {line.Trim()}");
                }
            }

            Assert.AreEqual(2, code.Split('\n').Count(l => l.Contains("ORPHAN:")), "both ORPHAN lines (the throwing hand-back and the failed one)");
        }

        [TestMethod]
        public void EveryErrorLog_InTheSalvageRollerSources_CarriesTheRefundPrefix()
        {
            // final review item 3: an ERROR from this feature is always something staff may owe a player for, so every one
            // carries the one prefix they grep for. A call spanning several lines is read whole, up to its closing ");".
            var dir = Path.GetDirectoryName(FindInSourceTree("Source/ACE.Server/SalvageRoller/SalvageRollerManager.cs"));
            var files = Directory.GetFiles(dir, "*.cs");
            var calls = 0;

            Assert.IsTrue(files.Length >= 5, $"the SalvageRoller sources were not found in {dir}");

            foreach (var file in files)
            {
                var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(file));

                foreach (Match call in Regex.Matches(code, @"\b[Ll]og\.(Error|Fatal)\((?:[^;]|;(?!\s*\r?\n))*?\);", RegexOptions.Singleline))
                {
                    calls++;
                    StringAssert.Contains(call.Value, "RefundPrefix", $"{Path.GetFileName(file)}: {call.Value}");
                }
            }

            Assert.IsTrue(calls >= 20, $"only {calls} error calls were read; the scan is not seeing the sources");
        }

        // ======================================================================================
        // sell hook (station side) + hand-back
        // ======================================================================================

        [TestMethod]
        public void TryValidateSellBasket_RefusesAPlayerWithNoView_WithoutConsultingTheVerifier()
        {
            var stranger = MakePlayer(GuidB);
            var station = MakeStation(MakePlayer(GuidA));
            var item = MakeDisplay("x");

            var verifierCalls = 0;
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> o, int n, out string r) => { verifierCalls++; r = null; return true; };

            Assert.IsFalse(station.TryValidateSellBasket(stranger, new[] { item }, new[] { item }, out var reason));
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);
            Assert.AreEqual(0, verifierCalls);
        }

        [TestMethod]
        public void TryValidateSellBasket_RefusesWhenAnyRequestedLineWasDropped()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var target = MakeDisplay("target");
            var hammer = MakeDisplay("hammer", ItemType.TinkeringMaterial);

            var verifierCalls = 0;
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> o, int n, out string r) => { verifierCalls++; r = null; return true; };

            Assert.IsFalse(station.TryValidateSellBasket(a, new WorldObject[] { target, hammer, null }, new[] { target, hammer }, out var reason),
                "a requested line VerifySellItems dropped must refuse the whole basket");
            Assert.AreEqual(SalvageRollerVendor.BasketLineDroppedMessage, reason);

            Assert.IsFalse(station.TryValidateSellBasket(a, new WorldObject[0], new WorldObject[0], out reason), "an empty basket is refused");

            Assert.AreEqual(0, verifierCalls, "the shape checks run before eligibility");
        }

        [TestMethod]
        public void TryValidateSellBasket_PassesTheAcceptedBasketToTheVerifier_AndReturnsItsAnswer()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var target = MakeDisplay("target");
            var hammer = MakeDisplay("hammer", ItemType.TinkeringMaterial);

            IReadOnlyList<WorldObject> seen = null;
            var seenCount = -1;
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> o, int n, out string r) => { seen = o; seenCount = n; r = "verifier says no"; return false; };

            Assert.IsFalse(station.TryValidateSellBasket(a, new[] { target, hammer }, new[] { target, hammer }, out var reason));
            Assert.AreEqual("verifier says no", reason);
            CollectionAssert.AreEqual(new[] { target, hammer }, seen.ToList());
            Assert.AreEqual(2, seenCount, "the requested line count must reach VerifyBasket's requestedCount");

            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> o, int n, out string r) => { r = null; return true; };
            Assert.IsTrue(station.TryValidateSellBasket(a, new[] { target, hammer }, new[] { target, hammer }, out reason));
        }

        [TestMethod]
        public void CallSite_DefaultBasketVerifier_CallsSalvageRollerEligibilityVerifyBasket()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs")));

            var iDecl = code.IndexOf("private static bool DefaultBasketVerifier(", StringComparison.Ordinal);
            Assert.IsTrue(iDecl >= 0, "DefaultBasketVerifier was not found");

            var iCall = code.IndexOf("SalvageRollerEligibility.VerifyBasket(player, offered, requestedCount, out _, out _, out _, out refusal)", iDecl, StringComparison.Ordinal);
            var iNext = code.IndexOf("\n        ", code.IndexOf('}', iDecl), StringComparison.Ordinal);

            Assert.IsTrue(iCall > iDecl && iCall < iNext, "the production basket verifier must delegate to SalvageRollerEligibility.VerifyBasket");

            Assert.IsTrue(code.Contains("if (verifier(player, accepted, requested.Count, out reason))"), "TryValidateSellBasket must pass the requested line count");
            Assert.IsFalse(code.Contains("StubRefusal"), "the station must not reference anything that lives only in the stub file");
            Assert.IsTrue(code.Contains("BasketVerifier = DefaultBasketVerifier;"), "BasketVerifier must default to the production verifier");
            Assert.IsTrue(code.Contains("TakeHandler = DefaultTakeHandler;"), "TakeHandler must default to the production take");
        }

        [TestMethod]
        public void ProcessItemsForPurchase_HandsEveryItemBack_DestroysNothing()
        {
            var station = MakeRecordingStation();

            var a = MakeDisplay("sold target");
            var b = MakeDisplay("sold hammer", ItemType.TinkeringMaterial);

            station.ProcessItemsForPurchase(null, new Dictionary<uint, WorldObject> { { a.Guid.Full, a }, { b.Guid.Full, b } });

            CollectionAssert.AreEquivalent(new[] { a, b }, station.HandedBack, "every sold item must be offered back to the seller");
            Assert.IsFalse(a.IsDestroyed);
            Assert.IsFalse(b.IsDestroyed);
            Assert.AreEqual(0, station.UniqueItemsForSale.Count);
            Assert.AreEqual(0, station.DefaultItemsForSale.Count);
        }

        [TestMethod]
        public void ProcessItemsForPurchase_DoesNotHandBackAnItemThatIsStillParented()
        {
            var station = MakeRecordingStation();

            var parented = MakeDisplay("already home");
            parented.ContainerId = 0x50000001;

            station.ProcessItemsForPurchase(null, new Dictionary<uint, WorldObject> { { parented.Guid.Full, parented } });

            Assert.AreEqual(0, station.HandedBack.Count, "an item still parented is already somewhere and must not be placed twice");
        }

        [TestMethod]
        public void ProcessItemsForPurchase_OneThrowingHandBack_DoesNotSkipTheRest_AndTellsTheSeller()
        {
            var station = MakeRecordingStation();

            var first = MakeDisplay("first");
            var throws = MakeDisplay("throws");
            var last = MakeDisplay("last");
            station.ThrowFor.Add(throws);

            station.ProcessItemsForPurchase(null, new Dictionary<uint, WorldObject> { { first.Guid.Full, first }, { throws.Guid.Full, throws }, { last.Guid.Full, last } });

            CollectionAssert.AreEqual(new[] { first, throws, last }, station.HandedBack, "a throw on one item must not stop the hand-back of the items after it");
            Assert.IsFalse(first.IsDestroyed || throws.IsDestroyed || last.IsDestroyed, "nothing is ever destroyed on the hand-back path");

            // With no player every placement fails, so all three are told: two by the failed placement and one by
            // the throw. The throw arm is what this test adds; the count proves it told too.
            Assert.AreEqual(3, station.HandBackFailedTold);
        }

        [TestMethod]
        public void ProcessItemsForPurchase_AThrowAlone_StillTellsTheSeller()
        {
            var station = MakeRecordingStation();

            var throws = MakeDisplay("throws");
            station.ThrowFor.Add(throws);

            station.ProcessItemsForPurchase(null, new Dictionary<uint, WorldObject> { { throws.Guid.Full, throws } });

            Assert.AreEqual(1, station.HandBackFailedTold, "a throwing hand-back must still tell the seller");
        }

        [TestMethod]
        public void HandBack_PlacedButTheSaveThrows_IsTheUnsavedCase_NotAnOrphan()
        {
            // step 6 re-review carry-in 1: the placement worked, so the item IS in the pack; only its save failed.
            // Under this harness the save itself throws (no shard), which is exactly the case to separate.
            var station = MakeRecordingStation();
            station.PlacementSucceeds = true;

            var item = MakeDisplay("placed");

            station.ProcessItemsForPurchase(null, new Dictionary<uint, WorldObject> { { item.Guid.Full, item } });

            Assert.AreEqual(1, station.HandedBack.Count);
            Assert.AreEqual(1, station.HandBackUnsavedTold, "a placed-but-unsaved item gets its own line");
            Assert.AreEqual(0, station.HandBackFailedTold, "and is never reported as not returned");
        }

        [TestMethod]
        public void HandBack_AThrowingTell_DoesNotStopTheRemainingHandBacks()
        {
            // step 6 re-review carry-in 2: the tell inside the throw arm is wrapped too
            var station = MakeRecordingStation();
            station.TellThrows = true;

            var first = MakeDisplay("first");
            var throws = MakeDisplay("throws");
            var last = MakeDisplay("last");
            station.ThrowFor.Add(throws);

            station.ProcessItemsForPurchase(null, new Dictionary<uint, WorldObject> { { first.Guid.Full, first }, { throws.Guid.Full, throws }, { last.Guid.Full, last } });

            CollectionAssert.AreEqual(new[] { first, throws, last }, station.HandedBack, "every item is still offered back when the tells throw");
            Assert.AreEqual(3, station.HandBackFailedTold, "each failure still attempted its tell");
        }

        [TestMethod]
        public void MaterializedCandidateRow_IsAcceptedBySetRows_AndNamedWithItsSeqAndTotal()
        {
            // step 6 re-review carry-in 3: the materializer builds through the WEENIE constructor, so SetRows' refusal of
            // anything that came from the shard never fires on a real candidate row.
            var station = MakeStation();

            var targetWeenie = new Weenie
            {
                WeenieClassId = 8101,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.MeleeWeapon }, { PropertyInt.Value, 500 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Escrowed Sword" } },
            };

            var target = new GenericObject(targetWeenie, new ObjectGuid(NextGuid()));
            var original = DuoGemSnapshot.Capture(target);

            var candidate = new SalvageRollerCandidate
            {
                Seq = 7,
                Diff = new SalvageRollerDiff(),
                Summary = new SalvageRollerSummary { Tier = 2, Total = 342 },
            };
            candidate.Diff.Ints[(int)PropertyInt.Value] = 900;

            var savedGuid = SalvageRollerManager.DisplayGuid;

            try
            {
                SalvageRollerManager.DisplayGuid = () => new ObjectGuid(NextGuid());

                WorldObject built = null;

                var count = station.SetRows(GuidA, new[] { candidate }, c => built = SalvageRollerManager.MaterializeDisplay(original, c, SalvageRollerAction.TourmalineReroll));

                Assert.AreEqual(1, count, "SetRows must accept a materialized candidate row");
                Assert.IsFalse(built.BiotaOriginatedFromOrHasBeenSavedToDatabase(), "a display row is never a shard object");
                Assert.AreEqual("Escrowed Sword (#7, 342%)", built.Name);
                Assert.AreEqual(900, built.GetProperty(PropertyInt.Value), "the candidate's result is on the row");
                Assert.AreNotEqual(target.Guid, built.Guid, "a fresh guid, never the escrowed target's");
                Assert.IsTrue(station.TryGetRowSeq(GuidA, built.Guid, out var seq) && seq == 7);
                Assert.AreEqual(500, target.GetProperty(PropertyInt.Value), "the escrowed target is never touched");
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedGuid;
            }
        }

        [TestMethod]
        public void HandBackFailure_SendsATransientErrorToTheSeller()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var seller = PvpTemplatePlayerTests.SeededPlayer();
            var rec = new PvpTemplateGateSiteTests.Recorder(seller);

            // The production half of the seam the two tests above count. Driven directly because the full
            // ProcessItemsForPurchase ends in the seller's panel refresh, which needs a landblock.
            var station = MakeRecordingStation();
            station.TellProduction(seller);

            rec.Drain();
            Assert.AreEqual(1, rec.Sent.OfType<GameEventCommunicationTransientString>().Count(), "the seller is told once, as PersonalVendor.HandBackOrphan does");

            // No player (the sell path never passes one, but the seam must not throw if it does): a no-op.
            station.TellProduction(null);
        }

        [TestMethod]
        public void RefusedBuy_ByAPlayerWithNoView_TellsThemOnce_AndTheReApproachShowsThemNothing()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var a = MakePlayer(GuidA);
            var stranger = MakePlayer(GuidB);
            var rec = new PvpTemplateGateSiteTests.Recorder(stranger);

            var station = MakeStation(a);
            var rows = SetRows(station, GuidA, "not theirs");

            // Exactly what Player_Commerce.HandleActionBuyItem does on a refusal for a vendor that is not a
            // PersonalVendor: validate, then re-approach.
            Assert.IsFalse(station.BuyItems_ValidateTransaction(Buy((rows[0].Guid.Full, 1)), stranger));
            station.ApproachVendor(stranger, VendorType.Undef);

            rec.Drain();
            Assert.AreEqual(1, rec.Sent.OfType<GameEventCommunicationTransientString>().Count(), "a player with no view must be told once, not once by the buy and again by the re-approach");
            Assert.IsFalse(station.HasView(stranger), "an approach never creates a view");
            Assert.AreEqual(0, station.GetItemsForSaleCount(stranger), "so the panel the re-approach sends is empty");
            Assert.AreEqual(1, station.RowCount(GuidA), "and A's rows are untouched");
            Assert.IsFalse(rows[0].IsDestroyed);
        }

        /// <summary>
        /// Review F2: inside one approach the count GameEventApproachVendor writes and the rows it serializes come from
        /// the same snapshot, even when the view is refilled or dropped between the two reads.
        /// </summary>
        [TestMethod]
        public void ApproachSnapshot_CountAndRowsAgree_EvenWhenTheViewChangesMidApproach()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var first = SetRows(station, GuidA, "A1", "A2", "A3");

            var (count, rows) = station.WithApproachSnapshot(a, () =>
            {
                var c = station.GetItemsForSaleCount(a);

                // another thread refills, then drops, the view between the count and the rows
                SetRows(station, GuidA, "B1");
                station.DropView(GuidA);

                return (c, Emitted(station, a));
            });

            Assert.AreEqual(3, count);
            Assert.AreEqual(count, rows.Count, "the count written must equal the rows serialized");
            CollectionAssert.AreEqual(first, rows);

            Assert.AreEqual(0, station.GetItemsForSaleCount(a), "outside the approach the live view is read again");
            Assert.AreEqual(0, Emitted(station, a).Count);
        }

        [TestMethod]
        public void ApproachVendor_SourcePin_RunsTheBaseApproachInsideTheSnapshot()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs")));
            var approach = PooledLootSourceText.MethodBody(code, "public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)");

            var snapshot = approach.IndexOf("WithApproachSnapshot(player, () =>", StringComparison.Ordinal);
            var baseCall = approach.IndexOf("base.ApproachVendor(player, action, altCurrencySpent);", StringComparison.Ordinal);

            Assert.IsTrue(snapshot > 0 && baseCall > snapshot, $"base.ApproachVendor (which builds GameEventApproachVendor) must run inside the snapshot (snapshot={snapshot}, base={baseCall})");
            Assert.AreEqual(1, Regex.Matches(approach, Regex.Escape("base.ApproachVendor(")).Count, "no second, unsnapshotted base call");

            StringAssert.Contains(code, "public override int GetItemsForSaleCount(Player viewer) => RowsFor(viewer).Count;");
            StringAssert.Contains(PooledLootSourceText.MethodBody(code, "public override void ForEachItemFor(Player viewer, Action<WorldObject> action)"), "RowsFor(viewer)");
        }

        /// <summary>
        /// Review F3: Player.FindObject's LastUsedContainer arm must pass the player as the viewer. Through the viewer-less
        /// overload the station fails closed, so a regression here does not leak rows, it breaks appraisal of the
        /// player's own rows; through any other viewer it would leak them.
        /// </summary>
        [TestMethod]
        public void FindObject_SourcePin_PassesThePlayerAsTheViewer()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(FindInSourceTree("Source/ACE.Server/WorldObjects/Player_Inventory.cs")));

            var arm = code.IndexOf("if (searchLocations.HasFlag(SearchLocations.LastUsedContainer))", StringComparison.Ordinal);
            Assert.IsTrue(arm > 0, "FindObject's LastUsedContainer arm was not found; re-aim this guard");

            var body = code.Substring(arm, code.IndexOf("lastOpenedContainer.IsOpen", arm, StringComparison.Ordinal) - arm);

            StringAssert.Contains(body, "lastUsedVendor.TryGetItemForSale(this, objectGuid, out result)");
            Assert.IsFalse(body.Contains("lastUsedVendor.TryGetItemForSale(objectGuid, out result)"), "the viewer-less overload must not be used here");
        }

        // ======================================================================================
        // eviction
        // ======================================================================================

        /// <summary>
        /// Re-review N1: a materializer that throws part way must not strand the displays it already built. They are
        /// fresh and unregistered, so they are destroyed (which recycles a dynamic guid); the viewer's previous rows and
        /// the registry are untouched, and the exception still reaches the caller.
        /// </summary>
        [TestMethod]
        public void SetRows_AMaterializerThatThrowsPartWay_DestroysWhatItBuilt_AndLeavesThePreviousRowsAlone()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var previous = SetRows(station, GuidA, "old 1", "old 2");

            var built = new List<WorldObject>();

            Assert.ThrowsExactly<InvalidOperationException>(() => station.SetRows(GuidA,
                new[] { new Cand(1, "n1"), new Cand(2, "n2"), new Cand(3, "throws"), new Cand(4, "n4") },
                c =>
                {
                    if (c.Seq == 3)
                        throw new InvalidOperationException("materializer failure injected by the test");

                    var display = MakeDisplay(c.Name);
                    built.Add(display);
                    return display;
                }));

            Assert.AreEqual(2, built.Count, "precondition: two displays were built before the throw");
            Assert.IsTrue(built.All(d => d.IsDestroyed), "the displays built before the throw are destroyed, not stranded");

            Assert.AreEqual(2, station.RowCount(GuidA), "the viewer's previous rows are untouched");
            Assert.IsTrue(previous.All(d => !d.IsDestroyed));
            CollectionAssert.AreEqual(previous, Emitted(station, a));
            AssertRegistryMatchesViews(station, "after a materializer threw");
        }

        /// <summary>
        /// Re-review N2: the sweep's drop decision and the drop must be one lock section. The seam runs between the two;
        /// from it, another thread touches the view. With the decision and drop under one lock, that touch waits until the
        /// drop is done and then re-creates the view, so the player keeps a view. With the drop outside the lock, the touch
        /// lands first and the drop then deletes the view the player just touched.
        /// </summary>
        [TestMethod]
        public void Sweep_ATouchFromAnotherThreadCannotLandBetweenTheDropDecisionAndTheDrop()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            SetRows(station, GuidA, "A1");

            station.Online.Remove(GuidA);

            System.Threading.Thread toucher = null;
            var touchedBeforeDropFinished = false;

            station.SweepDropping = guid =>
            {
                toucher = new System.Threading.Thread(() => station.OpenView(guid));
                toucher.Start();

                // with the lock held here, the toucher cannot get in; give it every chance to
                touchedBeforeDropFinished = toucher.Join(300);
            };

            Assert.AreEqual(1, station.SweepViews(Time.GetUnixTime()));

            Assert.IsNotNull(toucher, "precondition: the sweep decided to drop the view");
            Assert.IsTrue(toucher.Join(5000), "the toucher finishes once the drop releases the lock");

            Assert.IsFalse(touchedBeforeDropFinished, "the touch must not complete between the drop decision and the drop");
            Assert.IsTrue(station.HasView(GuidA), "the touch that waited for the drop leaves the player a view");
            AssertRegistryMatchesViews(station, "after the raced sweep");
        }

        [TestMethod]
        public void DropView_OnLogout_DestroysExactlyThatViewersDisplays()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1", "A2");
            var bRows = SetRows(station, GuidB, "B1");

            Assert.IsTrue(ItemTextSearch_TryCompile("a1", out var regex));
            station.SetSearchFilter(GuidA, regex);

            // what Player.LogOut_Inner does
            a.CurrentSalvageRollerStation = station;
            a.CurrentSalvageRollerStation?.DropView(a);

            Assert.IsFalse(station.HasView(GuidA));
            Assert.IsTrue(aRows.All(d => d.IsDestroyed));
            Assert.IsTrue(bRows.All(d => !d.IsDestroyed), "only the leaving viewer's rows go");
            Assert.AreEqual(1, station.RowCount(GuidB));
            Assert.IsNull(a.GetLiveSalvageRollerStation(), "a slot whose view is gone is not live");
            AssertRegistryMatchesViews(station, "after logout");

            station.Open(a);
            Assert.IsNull(station.GetSearchFilter(GuidA), "DropView clears the filter: a fresh view starts unfiltered");
        }

        [TestMethod]
        public void FinishTake_Delivered_EmptiesTheTakersView_KeepsIt_AndReApproachesWithAnEmptyPanel()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1", "A2");
            var bRows = SetRows(station, GuidB, "B1");

            Assert.IsTrue(ItemTextSearch_TryCompile("a1", out var filter));
            Assert.IsTrue(station.SetSearchFilter(GuidA, filter));
            Assert.IsTrue(station.SetSearchFilter(GuidB, filter));

            var told = new List<string>();
            var result = new SalvageRollerManager.TakeResult(SalvageRollerManager.TakeOutcome.Delivered, null);

            Assert.IsTrue(SalvageRollerManager.FinishTake(a, station, result, told.Add));

            Assert.IsTrue(station.HasView(GuidA), "the client's panel stays open after a take, so the view is kept for the next sale");
            Assert.IsNull(station.GetSearchFilter(GuidA), "the kept view's filter is cleared, so it cannot hide the next batch's rows");
            Assert.IsNotNull(station.GetSearchFilter(GuidB), "another viewer's filter is untouched");
            Assert.AreEqual(0, station.RowCount(GuidA));
            Assert.IsTrue(aRows.All(d => d.IsDestroyed));
            Assert.IsTrue(bRows.All(d => !d.IsDestroyed));
            AssertRegistryMatchesViews(station, "after a delivered take");

            Assert.AreEqual(1, station.Approaches.Count, "a successful buy does not re-approach on its own, so the take must");
            Assert.AreSame(a, station.Approaches[0].Player);
            Assert.AreEqual(VendorType.Buy, station.Approaches[0].Action);
            Assert.IsTrue(station.Approaches[0].HadView);
            Assert.AreEqual(0, station.Approaches[0].Rows, "the re-approach runs after the rows are cleared, so the client is sent an EMPTY panel");
            Assert.AreEqual(0, told.Count);
        }

        /// <summary>The owner's report: take a result, then sell the next item into the still-open panel. No reopen needed.</summary>
        [TestMethod]
        public void TakeThenSell_IntoTheStillOpenPanel_IsAccepted_WithoutAReopen()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            SetRows(station, GuidA, "A1");

            Assert.IsTrue(SalvageRollerManager.FinishTake(a, station, new SalvageRollerManager.TakeResult(SalvageRollerManager.TakeOutcome.Delivered, null), _ => { }));

            var next = MakeDisplay("Next Sword");
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> offered, int requested, out string refusal) => { refusal = null; return true; };

            Assert.IsTrue(station.CanAccept(next, a, out var reason), reason);
            Assert.IsTrue(station.TryValidateSellBasket(a, new[] { next }, new[] { next }, out reason), reason);
        }

        /// <summary>The idle sweep drops a view while the client's panel stays open; a sale from that panel makes an empty view and proceeds.</summary>
        [TestMethod]
        public void Sell_AfterTheIdleSweepDroppedTheView_AtTheOpenPanel_RecreatesAnEmptyViewAndIsAccepted()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);
            var bRows = SetRows(station, GuidB, "B1", "B2");

            a.LastOpenedContainerId = station.Guid;     // what Vendor.ApproachVendor set when the panel was sent

            Assert.IsTrue(PropertyManager.ModifyLong(RotKey, 1));
            Assert.AreEqual(2, station.SweepViews(Time.GetUnixTime() + 10), "precondition: both idle views are swept");
            Assert.IsTrue(PropertyManager.ModifyLong(RotKey, 600));

            Assert.IsFalse(station.HasView(GuidA), "precondition: A's view is gone while A's panel is open");

            var item = MakeDisplay("Offered Sword");
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> offered, int requested, out string refusal) => { refusal = null; return true; };

            Assert.IsTrue(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out var reason), reason);
            Assert.IsTrue(station.CanAccept(item, a, out reason), reason);

            Assert.IsTrue(station.HasView(GuidA));
            Assert.AreEqual(0, station.RowCount(GuidA), "the view made by a sale starts empty");
            Assert.AreEqual(0, Emitted(station, a).Count, "and shows nobody's rows");
            Assert.AreSame(station, a.CurrentSalvageRollerStation);
            Assert.IsTrue(bRows.All(d => d.IsDestroyed), "precondition held: B's swept rows are gone, not adopted");
            Assert.AreEqual(0, station.DefaultItemsForSale.Count);
            AssertRegistryMatchesViews(station, "after a sale recreated A's view");
        }

        [TestMethod]
        public void Sell_WithNoView_IsStillRefused_WhenNeverOpenedHere_OpenedElsewhereSince_OrOutOfRange()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation();
            var other = MakeStation();
            var item = MakeDisplay("Offered Sword");
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> offered, int requested, out string refusal) => { refusal = null; return true; };

            a.LastOpenedContainerId = ObjectGuid.Invalid;
            Assert.IsFalse(station.CanAccept(item, a, out var reason), "never opened this station");
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);
            Assert.IsFalse(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out reason));
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);

            a.LastOpenedContainerId = other.Guid;
            Assert.IsFalse(station.CanAccept(item, a, out reason), "the last panel sent was another vendor's");
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);

            a.LastOpenedContainerId = station.Guid;
            station.OutOfRange.Add(GuidA);
            Assert.IsFalse(station.CanAccept(item, a, out reason), "out of range");
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);
            Assert.IsFalse(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out reason));
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason);

            Assert.IsFalse(station.HasView(GuidA), "no refused sale creates a view");
            Assert.AreEqual(0, station.ViewCount);

            station.OutOfRange.Remove(GuidA);
            Assert.IsTrue(station.CanAccept(item, a, out reason), "positive control: in range at this station's panel is served");
        }

        /// <summary>
        /// Review F2: a view a sale re-made is UNFILLED. A player who held a Pending batch when the sweep took their view
        /// is pointed back at the Use, both on a sale (instead of "take one of its results first") and on a take of a
        /// row their client still shows (instead of "no longer on offer"). A Use or a fill ends the unfilled state.
        /// </summary>
        [TestMethod]
        public void SweptViewWithAPendingBatch_SaleAndStaleTake_AreToldToUseTheStation_UntilAUseOrFill()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var staleRows = SetRows(station, GuidA, "A1 (#1)", "A2 (#2)");
            a.LastOpenedContainerId = station.Guid;

            station.DropView(GuidA);     // what the idle sweep does
            Assert.IsTrue(staleRows.All(d => d.IsDestroyed), "precondition: the rows the client still shows are gone");

            var item = MakeDisplay("Offered Sword");
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> offered, int requested, out string refusal) =>
            {
                refusal = SalvageRollerEligibility.BatchExistsMessage;      // the verify's batch-exists step
                return false;
            };

            Assert.IsTrue(station.CanAccept(item, a, out var reason), reason);
            Assert.IsTrue(station.IsUnfilledView(GuidA), "a view made by a sale is unfilled");

            Assert.IsFalse(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out reason));
            Assert.AreEqual(SalvageRollerVendor.ResultsWaitingMessage, reason);

            Assert.IsFalse(station.TryResolveTake(a, Buy((staleRows[0].Guid.Full, 1)), out _, out reason));
            Assert.AreEqual(SalvageRollerVendor.NoViewMessage, reason, "a take from an unfilled view is pointed at the Use");

            // a Use ends it: the batch-exists line and the stale-row line are the ordinary ones again
            station.Open(a);
            Assert.IsFalse(station.IsUnfilledView(GuidA));

            Assert.IsFalse(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out reason));
            Assert.AreEqual(SalvageRollerEligibility.BatchExistsMessage, reason);
            Assert.IsFalse(station.TryResolveTake(a, Buy((staleRows[0].Guid.Full, 1)), out _, out reason));
            Assert.AreEqual(SalvageRollerVendor.UnknownRowMessage, reason);

            // and so does a fill
            station.DropView(GuidA);
            Assert.IsTrue(station.CanAccept(item, a, out reason), reason);
            Assert.IsTrue(station.IsUnfilledView(GuidA));
            var fresh = SetRows(station, GuidA, "B1 (#1)");
            Assert.IsFalse(station.IsUnfilledView(GuidA), "a fill ends the unfilled state");
            Assert.IsTrue(station.TryResolveTake(a, Buy((fresh[0].Guid.Full, 1)), out var seq, out reason), reason);
            Assert.AreEqual(1, seq);
            AssertRegistryMatchesViews(station, "after the unfilled view was filled");
        }

        [TestMethod]
        public void SweptViewWithNoBatch_SaleProceedsNormally()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation();
            a.LastOpenedContainerId = station.Guid;

            var item = MakeDisplay("Offered Sword");
            var verified = 0;
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> offered, int requested, out string refusal) => { verified++; refusal = null; return true; };

            Assert.IsTrue(station.CanAccept(item, a, out var reason), reason);
            Assert.IsTrue(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out reason), reason);
            Assert.IsNull(reason);
            Assert.AreEqual(1, verified);

            // any OTHER verify refusal on an unfilled view is passed through untouched
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> offered, int requested, out string refusal) => { refusal = "some other rule"; return false; };
            Assert.IsFalse(station.TryValidateSellBasket(a, new[] { item }, new[] { item }, out reason));
            Assert.AreEqual("some other rule", reason);
        }

        [TestMethod]
        public void Sell_RecreatingTheView_MovesTheSlot_AndDropsAViewHeldOnAnotherStation()
        {
            var a = MakePlayer(GuidA);
            var first = MakeStation(a);
            var firstRows = SetRows(first, GuidA, "on the first station");
            a.CurrentSalvageRollerStation = first;

            var second = MakeStation();
            a.LastOpenedContainerId = second.Guid;

            Assert.IsTrue(second.CanAccept(MakeDisplay("x"), a, out var reason), reason);

            Assert.IsTrue(second.HasView(GuidA));
            Assert.IsFalse(first.HasView(GuidA), "a player holds a view on one station at a time, as a Use enforces");
            Assert.IsTrue(firstRows.All(d => d.IsDestroyed));
            Assert.AreSame(second, a.CurrentSalvageRollerStation);
            AssertRegistryMatchesViews(first, "after the slot moved off the first station");
            AssertRegistryMatchesViews(second, "after the second station made a view");
        }

        [TestMethod]
        public void FinishTake_Deferred_ClearsTheTakersRows_KeepsTheView_AndTells()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1");
            var bRows = SetRows(station, GuidB, "B1");

            var told = new List<string>();
            var result = new SalvageRollerManager.TakeResult(SalvageRollerManager.TakeOutcome.Deferred, SalvageRollerManager.DeliveryDeferredMessage);

            Assert.IsFalse(SalvageRollerManager.FinishTake(a, station, result, told.Add));

            Assert.IsTrue(station.HasView(GuidA));
            Assert.AreEqual(0, station.RowCount(GuidA));
            Assert.IsTrue(aRows[0].IsDestroyed);
            Assert.IsFalse(bRows[0].IsDestroyed);
            CollectionAssert.AreEqual(new[] { SalvageRollerManager.DeliveryDeferredMessage }, told);
            AssertRegistryMatchesViews(station, "after a deferred take");
        }

        [TestMethod]
        public void Sweep_DropsAnOfflineViewer_AndKeepsAnOnlineOne()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1");
            var bRows = SetRows(station, GuidB, "B1");

            station.Online.Remove(GuidA);

            Assert.AreEqual(1, station.SweepViews(Time.GetUnixTime()));

            Assert.IsFalse(station.HasView(GuidA));
            Assert.IsTrue(aRows[0].IsDestroyed);
            Assert.IsTrue(station.HasView(GuidB));
            Assert.IsFalse(bRows[0].IsDestroyed);
            AssertRegistryMatchesViews(station, "after the offline sweep");
        }

        [TestMethod]
        public void Sweep_DropsAnOutOfRangeViewer_AndKeepsAnInRangeOne()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var aRows = SetRows(station, GuidA, "A1");
            var bRows = SetRows(station, GuidB, "B1");

            station.OutOfRange.Add(GuidB);

            Assert.AreEqual(1, station.SweepViews(Time.GetUnixTime()));

            Assert.IsTrue(station.HasView(GuidA));
            Assert.IsFalse(aRows[0].IsDestroyed);
            Assert.IsFalse(station.HasView(GuidB));
            Assert.IsTrue(bRows[0].IsDestroyed);
            AssertRegistryMatchesViews(station, "after the out-of-range sweep");
        }

        [TestMethod]
        public void Sweep_DropsAnIdleView_AtTheConfiguredLifetime_AndAtTheFallback()
        {
            var a = MakePlayer(GuidA);
            var station = MakeStation(a);
            var rows = SetRows(station, GuidA, "A1");

            var now = Time.GetUnixTime();

            Assert.AreEqual(0, station.SweepViews(now + 300), "inside the 600 s lifetime the view is kept");
            Assert.IsTrue(station.HasView(GuidA));

            Assert.AreEqual(1, station.SweepViews(now + 602), "past the 600 s lifetime the view is dropped");
            Assert.IsFalse(station.HasView(GuidA));
            Assert.IsTrue(rows[0].IsDestroyed);

            Assert.IsTrue(PropertyManager.ModifyLong(RotKey, 0));
            station.Open(a);
            now = Time.GetUnixTime();

            Assert.AreEqual(0, station.SweepViews(now + 30), "a misconfigured lifetime falls back to 60 s, never instant expiry");
            Assert.AreEqual(1, station.SweepViews(now + 62), "and never 'never expire'");
        }

        [TestMethod]
        public void ApproachVendor_SweepsLazily_AndNeverCreatesAView()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a);

            var aRows = SetRows(station, GuidA, "A1");
            station.Online.Remove(GuidA);

            station.ApproachVendor(b);

            Assert.IsFalse(station.HasView(GuidB), "an approach never creates a view");
            Assert.IsFalse(station.HasView(GuidA), "the approach ran the sweep, which dropped the offline viewer");
            Assert.IsTrue(aRows[0].IsDestroyed);
        }

        [TestMethod]
        public void StationDestroy_DestroysEveryViewersDisplays_AndClearsTheirSlots()
        {
            var a = MakePlayer(GuidA);
            var b = MakePlayer(GuidB);
            var station = MakeStation(a, b);

            var rows = SetRows(station, GuidA, "A1", "A2").Concat(SetRows(station, GuidB, "B1")).ToList();

            a.CurrentSalvageRollerStation = station;
            b.CurrentSalvageRollerStation = station;

            station.Destroy();

            Assert.IsTrue(rows.All(d => d.IsDestroyed), "WorldObject.Destroy's vendor branch reaches every viewer's displays");
            Assert.AreEqual(0, station.ViewCount);
            Assert.AreEqual(0, station.DefaultItemsForSale.Count);
            Assert.IsNull(a.CurrentSalvageRollerStation);
            Assert.IsNull(b.CurrentSalvageRollerStation);
        }

        [TestMethod]
        public void MemoryBound_TwentyViewersOfAHundredRows_AllEvicted_LeavesNothing()
        {
            var station = MakeStation();
            var all = new List<WorldObject>();

            for (uint v = 0; v < 20; v++)
            {
                var viewer = MakePlayer(0x50001000 + v);
                station.Open(viewer);
                all.AddRange(SetRows(station, viewer.Guid.Full, Enumerable.Range(1, 100).Select(i => $"row {i}").ToArray()));
            }

            Assert.AreEqual(20, station.ViewCount);
            Assert.AreEqual(2000, station.DefaultItemsForSale.Count);
            AssertRegistryMatchesViews(station, "with 20 views of 100 rows");

            station.Online.Clear();
            Assert.AreEqual(20, station.SweepViews(Time.GetUnixTime()));

            Assert.AreEqual(0, station.ViewCount);
            Assert.AreEqual(0, station.DefaultItemsForSale.Count);
            Assert.IsTrue(all.All(d => d.IsDestroyed));
        }

        internal static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }

    /// <summary>
    /// Salvage Roller step 6: the Vendor.TryValidateSellBasket hook in Player_Commerce and the sell-gate
    /// bypass properties. HandleActionSellItem itself resolves the vendor off a landblock, which no unit test
    /// has, so the hook's PLACEMENT is pinned by a source scan and its BEHAVIOUR is driven through
    /// Player.PassesSellBasketHook on a seeded Player whose Session records what is sent.
    /// </summary>
    [TestClass]
    public class SalvageRollerSellHookTests
    {
        private const ItemType AcceptsEverything = unchecked((ItemType)(-1));

        private SalvageRollerVendor.SellBasketVerifier savedVerifier;

        [TestInitialize]
        public void Setup()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            SalvageRollerVendorTests.SeedVendorConstructible();

            savedVerifier = SalvageRollerVendor.BasketVerifier;
        }

        [TestCleanup]
        public void Cleanup()
        {
            SalvageRollerVendor.BasketVerifier = savedVerifier;

            PropertyManager.ModifyBool("vendor_shop_uses_generator", DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
        }

        [TestMethod]
        public void RefusedBasket_DetachesNothing_AndReleasesTheClient()
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();
            var rec = new PvpTemplateGateSiteTests.Recorder(player);

            var target = PvpTemplatePlayerTests.Item(issued: false);
            var hammer = PvpTemplatePlayerTests.Item(issued: false);
            PvpTemplatePlayerTests.Give(player, target, 0);
            PvpTemplatePlayerTests.Give(player, hammer, 1);

            var containerBefore = (target.ContainerId, hammer.ContainerId);

            var station = SalvageRollerVendorTests.MakeStation(player);

            var verifierCalls = 0;
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> o, int n, out string r) => { verifierCalls++; r = "refused for the test"; return false; };

            var sellList = new Dictionary<uint, WorldObject> { { target.Guid.Full, target }, { hammer.Guid.Full, hammer } };

            var passed = player.PassesSellBasketHook(station, new List<WorldObject> { target, hammer }, sellList);

            Assert.IsFalse(passed);
            Assert.AreEqual(1, verifierCalls);

            Assert.IsTrue(player.Inventory.ContainsKey(target.Guid), "the target must still be in the pack");
            Assert.IsTrue(player.Inventory.ContainsKey(hammer.Guid), "the Hammer must still be in the pack");
            Assert.AreEqual(containerBefore, (target.ContainerId, hammer.ContainerId), "nothing may be re-parented by a refusal");
            Assert.AreEqual(0, station.DefaultItemsForSale.Count + station.UniqueItemsForSale.Count, "nothing reached the station");

            rec.Drain();
            Assert.AreEqual(1, rec.Sent.OfType<GameEventCommunicationTransientString>().Count(), "the refusal text is sent once");
            Assert.AreEqual(1, rec.Sent.OfType<GameEventMessage>().Count(e => e.EventType == GameEventType.InventoryServerSaveFailed), "the client is released with one InventoryServerSaveFailed, as on the handler's other whole-sale refusals");
            Assert.AreEqual(1, rec.Sent.OfType<GameEventMessage>().Count(e => e.EventType == GameEventType.UseDone), "and the use-done that releases the panel");
        }

        [TestMethod]
        public void SellFromAPlayerWithNoView_IsRefusedBeforeDetach_WithoutConsultingTheVerifier()
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();
            var rec = new PvpTemplateGateSiteTests.Recorder(player);

            var target = PvpTemplatePlayerTests.Item(issued: false);
            var hammer = PvpTemplatePlayerTests.Item(issued: false);
            PvpTemplatePlayerTests.Give(player, target, 0);
            PvpTemplatePlayerTests.Give(player, hammer, 1);

            var containerBefore = (target.ContainerId, hammer.ContainerId);

            // another character holds a view; this player never used the station
            var station = SalvageRollerVendorTests.MakeStation(SalvageRollerVendorTests.MakePlayer(SalvageRollerVendorTests.GuidB));

            var verifierCalls = 0;
            SalvageRollerVendor.BasketVerifier = (Player p, IReadOnlyList<WorldObject> o, int n, out string r) => { verifierCalls++; r = null; return true; };

            var sellList = new Dictionary<uint, WorldObject> { { target.Guid.Full, target }, { hammer.Guid.Full, hammer } };

            Assert.IsFalse(player.PassesSellBasketHook(station, new List<WorldObject> { target, hammer }, sellList));
            Assert.AreEqual(0, verifierCalls, "the view check runs before eligibility");

            Assert.IsTrue(player.Inventory.ContainsKey(target.Guid), "the target must still be in the pack");
            Assert.IsTrue(player.Inventory.ContainsKey(hammer.Guid), "the Hammer must still be in the pack");
            Assert.AreEqual(containerBefore, (target.ContainerId, hammer.ContainerId), "nothing may be re-parented by a refusal");
            Assert.IsFalse(station.HasView(player), "a refused sale creates no view");

            rec.Drain();
            Assert.AreEqual(1, rec.Sent.OfType<GameEventCommunicationTransientString>().Count(), "the refusal text is sent once");
        }

        [TestMethod]
        public void RetailVendor_PassesTheHook_AndSendsNothing()
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();
            var rec = new PvpTemplateGateSiteTests.Recorder(player);

            var item = PvpTemplatePlayerTests.Item(issued: false);
            PvpTemplatePlayerTests.Give(player, item, 0);

            var plain = new Vendor(SalvageRollerVendorTests.MakeStationWeenie(optIn: false), new ObjectGuid(0x71C80001));

            Assert.IsTrue(player.PassesSellBasketHook(plain, new List<WorldObject> { item }, new Dictionary<uint, WorldObject> { { item.Guid.Full, item } }));

            rec.Drain();
            Assert.AreEqual(0, rec.Sent.Count, "a vendor that does not override the hook must see no change at all");
        }

        [TestMethod]
        public void BuildRequestedSellBasket_KeepsOneEntryPerProfile_NullForUnowned()
        {
            var owned = PvpTemplatePlayerTests.Item(issued: false);
            var possessions = new Dictionary<uint, WorldObject> { { owned.Guid.Full, owned } };

            var profiles = new List<ItemProfile>
            {
                new ItemProfile(1, owned.Guid.Full),
                new ItemProfile(1, 0x7FFFFFF0),
                new ItemProfile(1, owned.Guid.Full),
            };

            var requested = Player.BuildRequestedSellBasket(profiles, possessions);

            Assert.AreEqual(3, requested.Count, "one entry per profile, so a dropped line is visible as a count mismatch");
            Assert.AreSame(owned, requested[0]);
            Assert.IsNull(requested[1]);
            Assert.AreSame(owned, requested[2], "a guid named twice appears twice");

            Assert.AreEqual(0, Player.BuildRequestedSellBasket(null, possessions).Count);
        }

        /// <summary>
        /// Placement, which no unit test can drive (the handler needs a landblock): the hook runs after
        /// VerifySellItems and before every side effect of the sale, and a refusal returns.
        /// </summary>
        [TestMethod]
        public void CallSite_HookRunsAfterVerifyAndBeforeAnythingIsDetached()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/WorldObjects/Player_Commerce.cs")));

            var iMethod = code.IndexOf("private void HandleActionSellItemCore(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "HandleActionSellItemCore was not found");

            var iVerify = code.IndexOf("VerifySellItems(", iMethod, StringComparison.Ordinal);
            var iHook = code.IndexOf("if (!PassesSellBasketHook(vendor, requestedSellItems, sellList))", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iVerify > iMethod, "VerifySellItems call not found");
            Assert.IsTrue(iHook > iVerify, "the basket hook must run AFTER VerifySellItems, against the filtered basket");

            var iReturn = code.IndexOf("return;", iHook, StringComparison.Ordinal);
            var iNextStatement = code.IndexOf(';', iHook);
            Assert.AreEqual(iReturn + "return".Length, iNextStatement, "a refused basket must return immediately");

            foreach (var sideEffect in new[] { "MoneyOutflow", "TryRemoveFromInventoryWithNetworking(", "TryDequipObjectWithNetworking(", "FlushDeferredSaves(", "ProcessItemsForPurchase(", "CalculatePayoutCoinAmount(" })
            {
                var at = code.IndexOf(sideEffect, iMethod, StringComparison.Ordinal);
                Assert.IsTrue(at > iHook, $"`{sideEffect}` must come AFTER the basket hook; a refusal must leave every item in the pack");
            }

            var hookBody = code.Substring(code.IndexOf("internal bool PassesSellBasketHook(", StringComparison.Ordinal));
            hookBody = hookBody.Substring(0, hookBody.IndexOf("\n        }", StringComparison.Ordinal));

            Assert.IsTrue(hookBody.Contains("vendor.TryValidateSellBasket("), "the helper must consult the vendor hook");

            foreach (var forbidden in new[] { "TryRemove", "TryDequip", "SaveBiota", "FlushDeferredSaves", "Destroy", "ProcessItemsForPurchase" })
                Assert.IsFalse(hookBody.Contains(forbidden), $"the hook helper must only read the basket; found `{forbidden}`");
        }

        // ---- IsAcceptableToSell with the window's bypasses ----

        private static Stackable MakeItem(int value = 100)
        {
            var item = FakeVaultWorld.MakeStack(8000, 1, 1000);
            item.ItemType = ItemType.TinkeringMaterial;
            item.Value = value;
            return item;
        }

        /// <summary>Uninitialized: the SellBypasses* overrides are constants that read no state.</summary>
        private static readonly Vendor Window = (Vendor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SalvageRollerVendor));
        private static readonly Vendor Mule = (Vendor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PersonalVendor));
        private static readonly Vendor Retail = (Vendor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vendor));

        /// <summary>The sell gate exactly as VerifySellItems calls it for <paramref name="vendor"/>.</summary>
        private static bool SellGate(WorldObject wo, Vendor vendor, out string message, ItemType mask = AcceptsEverything)
        {
            return Player.IsAcceptableToSell(wo, mask, isVault: vendor is PersonalVendor, vendor.SellBypassesPriceGates, vendor.SellBypassesAttunedRefusal, out message);
        }

        private static bool WindowGate(WorldObject wo, out string message, ItemType mask = AcceptsEverything) => SellGate(wo, Window, out message, mask);

        [TestMethod]
        public void WindowGate_AcceptsAttunedRetainedUnsellableAndValueless()
        {
            var attuned = MakeItem();
            attuned.Attuned = AttunedStatus.Attuned;
            Assert.IsTrue(WindowGate(attuned, out var m), m);

            var sticky = MakeItem();
            sticky.Attuned = AttunedStatus.Sticky;
            Assert.IsTrue(WindowGate(sticky, out m), m);

            var retained = MakeItem();
            retained.Retained = true;
            Assert.IsTrue(WindowGate(retained, out m), m);

            var unsellable = MakeItem();
            unsellable.SetProperty(PropertyBool.IsSellable, false);
            Assert.IsTrue(WindowGate(unsellable, out m), m);

            var valueless = MakeItem(value: 0);
            Assert.IsTrue(WindowGate(valueless, out m), m);
        }

        [TestMethod]
        public void WindowGate_StillEnforcesTheMaskAndTheNonEmptyContainerRefusal()
        {
            Assert.IsFalse(WindowGate(MakeItem(), out var m, mask: ItemType.MeleeWeapon), "the MerchandiseItemTypes mask is never bypassed");
            StringAssert.Contains(m, "unsellable");

            var container = FakeVaultWorld.MakeContainer(10);
            var inside = MakeItem();
            container.Inventory[inside.Guid] = inside;

            Assert.IsFalse(WindowGate(container, out m), "the non-empty-container refusal is never bypassed");
            StringAssert.Contains(m, "must be empty");
        }

        /// <summary>Regression pin: through the vendors' own SellBypasses* values, the mule and retail gate sets are unchanged.</summary>
        [TestMethod]
        public void MuleAndRetailGateSets_AreUnchanged()
        {
            var attuned = MakeItem();
            attuned.Attuned = AttunedStatus.Attuned;

            Assert.IsFalse(SellGate(attuned, Mule, out var m), "a vault still refuses attuned");
            StringAssert.Contains(m, "attuned to you");
            Assert.IsFalse(SellGate(attuned, Retail, out m), "retail still refuses attuned");

            var retained = MakeItem();
            retained.Retained = true;
            Assert.IsTrue(SellGate(retained, Mule, out m), m);
            Assert.IsFalse(SellGate(retained, Retail, out m));

            var valueless = MakeItem(value: 0);
            Assert.IsTrue(SellGate(valueless, Mule, out m), m);
            Assert.IsFalse(SellGate(valueless, Retail, out m));

            var unsellable = MakeItem();
            unsellable.SetProperty(PropertyBool.IsSellable, false);
            Assert.IsTrue(SellGate(unsellable, Mule, out m), m);
            Assert.IsFalse(SellGate(unsellable, Retail, out m));
        }

        [TestMethod]
        public void CallSite_PvpTemplateActionChoice_StaysTiedToPersonalVendor()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/WorldObjects/Player_Commerce.cs")));

            Assert.IsTrue(code.Contains("var isVault = vendor is PersonalVendor;"), "isVault must still be the PersonalVendor test");
            Assert.IsTrue(code.Contains("PvpTemplateBlocked(isVault ? PvpTemplateAction.Vault : PvpTemplateAction.VendorSell, wo)"), "the PvP-template sell action choice must stay tied to PersonalVendor");
            Assert.IsTrue(code.Contains("if (!vendor.SellBypassesPriceGates)"), "the payout skip must read the price-gate property");
        }
    }
}

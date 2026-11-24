using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.SalvageRoller;
using ACE.Server.Tests.Pvp;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Salvage Roller shared station (Docs/SalvageRoller/DESIGN.md 5.1, 5.4): the Use and Give wiring, the open flow
    /// (one view per player on the one placed station, nothing spawned), the player's slot, and the /salvageroller
    /// command core. A real landblock is not available in this host, so wiring is covered by source pins and
    /// everything else runs through the injected seams. Every PropertyManager key this class touches is seeded in
    /// Setup and restored in Cleanup.
    /// </summary>
    [TestClass]
    public class SalvageRollerStationTests
    {
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";
        private const string RotKey = "salvage_roller_window_rot_seconds";
        private const string MaxCandidatesKey = "salvage_roller_max_candidates";

        private const uint GuidA = SalvageRollerVendorTests.GuidA;
        private const uint GuidB = SalvageRollerVendorTests.GuidB;

        private static uint nextGuid = 0x71D00000;

        [TestInitialize]
        public void Setup()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();

            Assert.IsTrue(PropertyManager.ModifyLong(RotKey, 600), $"{RotKey} is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong(MaxCandidatesKey, 100), $"{MaxCandidatesKey} is missing from DefaultLongProperties");
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyLong(RotKey, DefaultPropertyManager.DefaultLongProperties[RotKey].Item);
            PropertyManager.ModifyLong(MaxCandidatesKey, DefaultPropertyManager.DefaultLongProperties[MaxCandidatesKey].Item);
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        // ======================================================================================
        // fixtures
        // ======================================================================================

        private static WorldObject MakeDisplay(string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 8100,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.MeleeWeapon }, { PropertyInt.Value, 100 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(++nextGuid));
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

        /// <summary>The production open flow with the manager fill and the panel recorded instead of run.</summary>
        private static bool Open(Player player, SalvageRollerVendor station, List<string> log = null, Action<Player, SalvageRollerVendor> onOpened = null)
        {
            if (station is SalvageRollerVendorTests.TestStation test)
                test.Online[player.Guid.Full] = player;

            return SalvageRollerStation.Open(player, station,
                (p, s) => { log?.Add($"opened view={s.HasView(p)} slot={ReferenceEquals(p.CurrentSalvageRollerStation, s)}"); onOpened?.Invoke(p, s); },
                (p, s) => log?.Add("approach"));
        }

        // ======================================================================================
        // Use and Give wiring
        // ======================================================================================

        [TestMethod]
        public void HandleGive_TellsTheUseInsteadMessage_AndTakesNothing()
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();
            var station = SalvageRollerVendorTests.MakeStation();
            var item = MakeDisplay("Sword");
            var told = new List<string>();

            SalvageRollerStation.HandleGive(player, station, item, (npc, p, text) =>
            {
                Assert.AreSame(station, npc);
                Assert.AreSame(player, p);
                told.Add(text);
            });

            CollectionAssert.AreEqual(new[] { SalvageRollerStation.GiveRefusedMessage }, told);
            StringAssert.Contains(SalvageRollerStation.GiveRefusedMessage, "Use me");
            Assert.IsNull(item.ContainerId, "the refused item must not have been moved");
        }

        [TestMethod]
        public void Wiring_UseIsTheVendorsOwnActOnUse_AndGiveIsInterceptedBeforeAcceptEverything()
        {
            var vendor = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SalvageRollerVendor.cs"),
                "public override void ActOnUse(WorldObject wo)");

            Assert.IsTrue(vendor.Contains("SalvageRollerStation.Open(player, this);"), "SalvageRollerVendor.ActOnUse does not open the station");

            // The old Creature.ActOnUse dispatch is dead (Vendor.ActOnUse never calls base) and must not come back.
            var creature = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature.cs"),
                "public override void ActOnUse(WorldObject worldObject)");

            Assert.IsFalse(creature.Contains("SalvageRollerStation"), "Creature.ActOnUse must not dispatch the station any more");

            var give = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Inventory.cs"),
                "private void GiveObjectToNPC(");

            var marker = give.IndexOf("PropertyBool.SalvageRollerStation", StringComparison.Ordinal);
            var handle = give.IndexOf("SalvageRollerStation.HandleGive(this, target, item)", StringComparison.Ordinal);
            var acceptAll = give.IndexOf("var acceptAll", StringComparison.Ordinal);

            Assert.IsTrue(marker > 0 && handle > marker, $"marker={marker} handle={handle}");
            Assert.IsTrue(acceptAll > handle, "the Give intercept must run before the AiAcceptEverything / emote logic");
        }

        [TestMethod]
        public void Wiring_NothingIsSpawned_LogoutReleasesTheView_AndDestroyForgetsEveryView()
        {
            var station = PooledLootSourceText.Read("Source/ACE.Server/SalvageRoller/SalvageRollerStation.cs");
            Assert.IsFalse(station.Contains("MuleSummonHandler"), "the shared station spawns nothing, so it needs no placement ladder");
            Assert.IsFalse(station.Contains("CreateNewWorldObject"), "the shared station spawns nothing");

            var player = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player.cs");
            Assert.IsTrue(player.Contains("CurrentSalvageRollerStation?.DropView(this);"), "logout does not release the player's view");

            var destroy = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject.cs");
            Assert.IsTrue(destroy.Contains("rollerStation.ForgetAllViews();"), "Destroy does not forget the station's views");
        }

        // ======================================================================================
        // the open flow
        // ======================================================================================

        [TestMethod]
        public void Open_CreatesTheView_SetsTheSlot_FillsThenApproaches_InThatOrder()
        {
            var player = SalvageRollerVendorTests.MakePlayer(GuidA);
            var station = SalvageRollerVendorTests.MakeStation();
            var log = new List<string>();

            Assert.IsTrue(Open(player, station, log));

            CollectionAssert.AreEqual(new[] { "opened view=True slot=True", "approach" }, log);
            Assert.AreSame(station, player.CurrentSalvageRollerStation);
            Assert.AreSame(station, player.GetLiveSalvageRollerStation());
            Assert.AreEqual(1, station.ViewCount);
        }

        [TestMethod]
        public void Open_ASecondUseRefillsTheSameView_AndNeverMakesASecond()
        {
            var player = SalvageRollerVendorTests.MakePlayer(GuidA);
            var station = SalvageRollerVendorTests.MakeStation();
            var batches = new List<List<WorldObject>>();

            for (var i = 0; i < 3; i++)
                Assert.IsTrue(Open(player, station, onOpened: (p, s) => { s.ClearRows(p.Guid.Full); batches.Add(SetRows(s, p.Guid.Full, $"use {i}")); }));

            Assert.AreEqual(1, station.ViewCount, "one view per player, however many times they use the station");
            Assert.AreEqual(1, station.RowCount(GuidA));
            Assert.IsTrue(batches[0].All(d => d.IsDestroyed) && batches[1].All(d => d.IsDestroyed), "each refill replaces the previous rows");
            Assert.IsFalse(batches[2][0].IsDestroyed);
        }

        [TestMethod]
        public void Open_TwoPlayers_GetTwoViewsOnTheOneStation()
        {
            var a = SalvageRollerVendorTests.MakePlayer(GuidA);
            var b = SalvageRollerVendorTests.MakePlayer(GuidB);
            var station = SalvageRollerVendorTests.MakeStation();

            Assert.IsTrue(Open(a, station));
            Assert.IsTrue(Open(b, station));

            Assert.AreEqual(2, station.ViewCount);
            Assert.AreSame(station, a.GetLiveSalvageRollerStation());
            Assert.AreSame(station, b.GetLiveSalvageRollerStation());
        }

        [TestMethod]
        public void Open_DropsTheViewTheyHeldOnAnotherStation()
        {
            var player = SalvageRollerVendorTests.MakePlayer(GuidA);
            var first = SalvageRollerVendorTests.MakeStation();
            var second = SalvageRollerVendorTests.MakeStation();

            Assert.IsTrue(Open(player, first));
            var rows = SetRows(first, GuidA, "on the first station");

            Assert.IsTrue(Open(player, second));

            Assert.IsFalse(first.HasView(GuidA), "a player holds a view on one station at a time");
            Assert.IsTrue(rows.All(d => d.IsDestroyed));
            Assert.AreSame(second, player.CurrentSalvageRollerStation);
        }

        [TestMethod]
        public void Open_ADestroyedStationOrNoPlayerIsNotOpened()
        {
            var player = SalvageRollerVendorTests.MakePlayer(GuidA);
            var station = SalvageRollerVendorTests.MakeStation();
            var log = new List<string>();

            station.Destroy();

            Assert.IsFalse(Open(player, station, log));
            Assert.IsFalse(SalvageRollerStation.Open(null, SalvageRollerVendorTests.MakeStation(), (p, s) => log.Add("x"), (p, s) => log.Add("x")));
            Assert.IsFalse(SalvageRollerStation.Open(player, null, (p, s) => log.Add("x"), (p, s) => log.Add("x")));

            Assert.AreEqual(0, log.Count);
            Assert.IsNull(player.CurrentSalvageRollerStation);
        }

        [TestMethod]
        public void Open_SweepsFirst_DroppingAViewerWhoHasGoneOffline()
        {
            var a = SalvageRollerVendorTests.MakePlayer(GuidA);
            var b = SalvageRollerVendorTests.MakePlayer(GuidB);
            var station = SalvageRollerVendorTests.MakeStation(a);
            var aRows = SetRows(station, GuidA, "A1");

            station.Online.Remove(GuidA);

            Assert.IsTrue(Open(b, station));

            Assert.IsFalse(station.HasView(GuidA), "the Use ran the sweep lazily");
            Assert.IsTrue(aRows[0].IsDestroyed);
            Assert.IsTrue(station.HasView(GuidB));
        }

        [TestMethod]
        public void LiveStation_NeedsALiveStationAndAView()
        {
            var player = SalvageRollerVendorTests.MakePlayer(GuidA);
            var station = SalvageRollerVendorTests.MakeStation();

            player.CurrentSalvageRollerStation = station;
            Assert.IsNull(player.GetLiveSalvageRollerStation(), "a slot with no view is not live");

            station.Open(player);
            Assert.AreSame(station, player.GetLiveSalvageRollerStation());

            station.DropView(player);
            Assert.IsNull(player.GetLiveSalvageRollerStation(), "a dropped view is not live");

            station.Open(player);
            station.Destroy();
            Assert.IsNull(player.GetLiveSalvageRollerStation(), "a destroyed station is not live");
        }

        /// <summary>
        /// The production open's outside steps, pinned in source because a real ActionChain and session cannot run here.
        /// The behaviour behind them (warm on every Use, the bounded deferred fill) is driven through the same core by
        /// SalvageRollerBatchTests' Open_* tests.
        /// </summary>
        [TestMethod]
        public void Wiring_ProductionOpen_WarmsFirst_WaitsOnThePlayersQueue_AndResendsThePanel()
        {
            var code = PooledLootSourceText.Read("Source/ACE.Server/SalvageRoller/SalvageRollerManager.cs");

            var production = PooledLootSourceText.MethodBody(code, "private static void OpenStation(Player player, SalvageRollerVendor station)");
            StringAssert.Contains(production, "chain.AddDelaySeconds(delay);");
            StringAssert.Contains(production, "chain.AddAction(player, action);");
            StringAssert.Contains(production, "Resend = () => station.ApproachVendor(player, VendorType.Open)");
            StringAssert.Contains(production, "ReferenceEquals(player.CurrentSalvageRollerStation, station)");
            StringAssert.Contains(production, "&& player.LastOpenedContainerId == station.Guid", "a player who opened another vendor or chest during the wait must not have the roller re-sent over it");

            var core = PooledLootSourceText.MethodBody(code, "internal static void OpenStation(ISalvageRollerHost host, SalvageRollerVendor station, OpenSteps steps)");
            var warm = core.IndexOf("WarmEscrow(host);", StringComparison.Ordinal);
            var fill = core.IndexOf("FillOpenedView(host, station, mayWait: true)", StringComparison.Ordinal);
            Assert.IsTrue(warm >= 0 && fill > warm, $"the escrow is warmed before the fill, on every Use (warm={warm} fill={fill})");
        }

        // ---- stale (out of range) ----

        private static Landblock MakeLandblock(params Landblock[] adjacents)
        {
            var lb = (Landblock)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            lb.Adjacents = new List<Landblock>(adjacents);
            return lb;
        }

        [TestMethod]
        public void IsStale_SameAdjacentFarAndUnknownLandblocks()
        {
            var far = MakeLandblock();
            var near = MakeLandblock();
            var home = MakeLandblock(near);

            Assert.IsFalse(SalvageRollerStation.IsStale(home, home), "same landblock");
            Assert.IsFalse(SalvageRollerStation.IsStale(near, home), "adjacent landblock");
            Assert.IsTrue(SalvageRollerStation.IsStale(far, home), "neither the viewer's nor adjacent");
            Assert.IsFalse(SalvageRollerStation.IsStale(null, home), "station not in the world");
            Assert.IsFalse(SalvageRollerStation.IsStale(far, null), "viewer mid-move");
        }

        [TestMethod]
        public void Sweep_ProductionRangeCheck_DropsAViewerInAFarLandblock_AndKeepsAnAdjacentOne()
        {
            // The production IsViewerOutOfRange (a plain SalvageRollerVendor, not the test subclass), with both
            // viewers registered online in PlayerManager so the production ResolveViewer finds them.
            var a = SalvageRollerVendorTests.MakePlayer(GuidA);
            var b = SalvageRollerVendorTests.MakePlayer(GuidB);
            var station = new SalvageRollerVendor(SalvageRollerVendorTests.MakeStationWeenie(), new ObjectGuid(++nextGuid));

            var near = MakeLandblock();
            station.CurrentLandblock = near;
            a.CurrentLandblock = MakeLandblock(near);
            b.CurrentLandblock = MakeLandblock();

            RegisterFakeOnlinePlayer(GuidA, a);
            RegisterFakeOnlinePlayer(GuidB, b);

            try
            {
                station.OpenView(GuidA);
                station.OpenView(GuidB);

                Assert.AreEqual(1, station.SweepViews(Time.GetUnixTime()));
                Assert.IsTrue(station.HasView(GuidA), "an adjacent landblock is in range");
                Assert.IsFalse(station.HasView(GuidB), "a far landblock is out of range");
            }
            finally
            {
                UnregisterFakeOnlinePlayer(GuidA);
                UnregisterFakeOnlinePlayer(GuidB);
            }
        }

        private static void RegisterFakeOnlinePlayer(uint guid, Player player)
        {
            var field = typeof(PlayerManager).GetField("onlinePlayers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(field, "PlayerManager.onlinePlayers was not found by reflection - has it been renamed?");

            ((Dictionary<uint, Player>)field.GetValue(null))[guid] = player;
        }

        private static void UnregisterFakeOnlinePlayer(uint guid)
        {
            var field = typeof(PlayerManager).GetField("onlinePlayers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            ((Dictionary<uint, Player>)field.GetValue(null)).Remove(guid);
        }

        // ======================================================================================
        // /salvageroller
        // ======================================================================================

        /// <summary>The command target over a station and one viewer guid, as the production PlayerTarget is over the live station.</summary>
        private sealed class FakeTarget : ISalvageRollerCommandTarget
        {
            public string Status = "STATUS LINE";
            public SalvageRollerVendor Station;
            public uint Viewer = GuidA;
            public readonly List<string> Told = new List<string>();
            public int Reopens;

            public string DescribeStatus() => Status;

            public bool HasLiveView => Station != null && Station.HasView(Viewer);

            public void Tell(string text) => Told.Add(text);

            public void SetFilter(System.Text.RegularExpressions.Regex filter) => Station.SetSearchFilter(Viewer, filter);

            public int VisibleCount => Station.VisibleCount(Viewer);

            public int RowCount => Station.RowCount(Viewer);

            public void ReopenPanel() => Reopens++;
        }

        private static SalvageRollerVendor StationWithRows(uint viewer, params string[] names)
        {
            var station = SalvageRollerVendorTests.MakeStation();

            SetRows(station, viewer, names);

            return station;
        }

        private static void Run(FakeTarget target, params string[] args)
            => SalvageRollerCommands.Dispatch(SalvageRollerCommands.Parse(args), target);

        [TestMethod]
        public void Parse_RecognisesEveryForm()
        {
            Assert.AreEqual(SalvageRollerCommandKind.Usage, SalvageRollerCommands.Parse(new string[0]).Kind);
            Assert.AreEqual(SalvageRollerCommandKind.Usage, SalvageRollerCommands.Parse(new[] { "frobnicate" }).Kind);
            Assert.AreEqual(SalvageRollerCommandKind.Status, SalvageRollerCommands.Parse(new[] { "STATUS" }).Kind);
            Assert.AreEqual(SalvageRollerCommandKind.ClearSearch, SalvageRollerCommands.Parse(new[] { "search" }).Kind);
            Assert.AreEqual(SalvageRollerCommandKind.ClearSearch, SalvageRollerCommands.Parse(new[] { "search", "  " }).Kind);

            var search = SalvageRollerCommands.Parse(new[] { "Search", "legendary", "strength" });
            Assert.AreEqual(SalvageRollerCommandKind.Search, search.Kind);
            Assert.AreEqual("legendary strength", search.Pattern);
        }

        [TestMethod]
        public void Status_TellsTheManagersLine_EvenWithoutAView()
        {
            var target = new FakeTarget();

            Run(target, "status");

            CollectionAssert.AreEqual(new[] { "STATUS LINE" }, target.Told);
            Assert.AreEqual(0, target.Reopens);
        }

        [TestMethod]
        public void Search_WithAPattern_FiltersTheLiveViewAndReopensThePanel()
        {
            var station = StationWithRows(GuidA, "Iron Sword", "Gold Dagger", "Iron Axe");
            var target = new FakeTarget { Station = station };

            Run(target, "search", "iron");

            Assert.IsNotNull(station.GetSearchFilter(GuidA));
            Assert.AreEqual(2, station.VisibleCount(GuidA));
            Assert.AreEqual(3, station.RowCount(GuidA));
            Assert.AreEqual(1, target.Reopens);
            Assert.AreEqual(1, target.Told.Count);
            StringAssert.Contains(target.Told[0], "2 of 3");
        }

        [TestMethod]
        public void Search_FiltersOnlyTheSearchersView()
        {
            var station = StationWithRows(GuidA, "Iron Sword", "Gold Dagger");
            SetRows(station, GuidB, "Iron Axe", "Gold Ring", "Silver Ring");

            Run(new FakeTarget { Station = station, Viewer = GuidA }, "search", "iron");

            Assert.AreEqual(1, station.VisibleCount(GuidA));
            Assert.AreEqual(3, station.VisibleCount(GuidB), "one player's search never filters another player's panel");
            Assert.IsNull(station.GetSearchFilter(GuidB));
        }

        [TestMethod]
        public void Search_WithNoPattern_ClearsTheFilterAndReopensThePanel()
        {
            var station = StationWithRows(GuidA, "Iron Sword", "Gold Dagger");
            var target = new FakeTarget { Station = station };

            Run(target, "search", "iron");
            Assert.AreEqual(1, station.VisibleCount(GuidA));

            target.Told.Clear();
            Run(target, "search");

            Assert.IsNull(station.GetSearchFilter(GuidA));
            Assert.AreEqual(2, station.VisibleCount(GuidA));
            Assert.AreEqual(2, target.Reopens);
            CollectionAssert.AreEqual(new[] { SalvageRollerCommands.SearchClearedMessage }, target.Told);
        }

        [TestMethod]
        public void Search_WithABadRegex_TellsTheCompileErrorAndLeavesTheFilterAlone()
        {
            var station = StationWithRows(GuidA, "Iron Sword");
            var target = new FakeTarget { Station = station };

            Run(target, "search", "(unclosed");

            Assert.IsNull(station.GetSearchFilter(GuidA));
            Assert.AreEqual(0, target.Reopens);
            Assert.AreEqual(1, target.Told.Count);
            StringAssert.StartsWith(target.Told[0], "Invalid search pattern");
        }

        [TestMethod]
        public void Search_WithNoLiveView_TellsToUseTheRollerFirst()
        {
            // a station with somebody else's view, and none for this player
            var target = new FakeTarget { Station = StationWithRows(GuidB, "not theirs"), Viewer = GuidA };

            Run(target, "search", "iron");
            Run(target, "search");

            CollectionAssert.AreEqual(new[] { SalvageRollerCommands.NoWindowMessage, SalvageRollerCommands.NoWindowMessage }, target.Told);
            Assert.AreEqual(0, target.Reopens);

            var noStation = new FakeTarget { Station = null };
            Run(noStation, "search", "iron");
            CollectionAssert.AreEqual(new[] { SalvageRollerCommands.NoWindowMessage }, noStation.Told);
        }

        [TestMethod]
        public void PlayerTarget_GoesThroughTheLiveStation_WithThePlayersOwnGuid()
        {
            var code = PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/SalvageRollerCommands.cs");

            StringAssert.Contains(code, "public bool HasLiveView => player.GetLiveSalvageRollerStation() != null;");
            StringAssert.Contains(code, "player.GetLiveSalvageRollerStation()?.SetSearchFilter(player.Guid.Full, filter);");
            StringAssert.Contains(code, "player.GetLiveSalvageRollerStation()?.VisibleCount(player.Guid.Full) ?? 0;");
            StringAssert.Contains(code, "player.GetLiveSalvageRollerStation()?.RowCount(player.Guid.Full) ?? 0;");
            StringAssert.Contains(code, "player.GetLiveSalvageRollerStation()?.ApproachVendor(player);");
        }

        [TestMethod]
        public void IsSalvageRollerSlashCommand_MatchesTheExactWordOnly()
        {
            Assert.IsTrue(SalvageRollerCommands.IsSalvageRollerSlashCommand("/salvageroller"));
            Assert.IsTrue(SalvageRollerCommands.IsSalvageRollerSlashCommand("/SalvageRoller status"));
            Assert.IsTrue(SalvageRollerCommands.IsSalvageRollerSlashCommand("/salvageroller search x"));
            Assert.IsFalse(SalvageRollerCommands.IsSalvageRollerSlashCommand("/salvagerollerX"));
            Assert.IsFalse(SalvageRollerCommands.IsSalvageRollerSlashCommand("/salvage"));
            Assert.IsFalse(SalvageRollerCommands.IsSalvageRollerSlashCommand("salvageroller"));
            Assert.IsFalse(SalvageRollerCommands.IsSalvageRollerSlashCommand(""));
            Assert.IsFalse(SalvageRollerCommands.IsSalvageRollerSlashCommand(null));
        }

        [TestMethod]
        public void Wiring_SlashIntercept_RunsBeforeTheAtBranch()
        {
            var talk = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Network/GameAction/Actions/GameActionTalk.cs"),
                "public static void Handle(ClientMessage clientMessage, Session session)");

            var intercept = talk.IndexOf("SalvageRollerCommands.IsSalvageRollerSlashCommand(message)", StringComparison.Ordinal);
            var atBranch = talk.IndexOf("if (message.StartsWith(\"@\"))", StringComparison.Ordinal);

            Assert.IsTrue(intercept > 0, "GameActionTalk does not intercept /salvageroller");
            Assert.IsTrue(atBranch > intercept, $"intercept={intercept} atBranch={atBranch}: the rewrite must precede the @ branch");
        }

        [TestMethod]
        public void BareCommand_TellsTheUsageLine()
        {
            var target = new FakeTarget();

            Run(target);

            CollectionAssert.AreEqual(new[] { SalvageRollerCommands.UsageMessage }, target.Told);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WorldEventAdminService (Docs/AdminPanel/WORLD-EVENTS-START.md): the location rules, webStartable
    /// enforcement, the refusal mapping, the audit line, and a start with no Session or Player anywhere.
    ///
    /// The start/stop cases run the REAL WorldEventManager.TryStart and TryStopCurrent against a fixture
    /// store published through InitializeWith, with the announcer's broadcast and Discord sinks captured.
    /// Every run uses a positive teaser, so it stays in Idle: Stage (the landblock hold, the audience
    /// sample) only ever runs from a Tick, and no test here ticks.
    ///
    /// Each validation guard in WorldEventAdminService, and each layer of WorldEventComposer's source
    /// restriction, has a case here that fails when that guard alone is removed (mutation-checked
    /// 2026-10-04 against the guard list in the review of this change). Most pair it with a control that
    /// changes one input and passes.
    /// </summary>
    [TestClass]
    public class WorldEventAdminServiceTests
    {
        private const string EnabledKey = "world_events_enabled";
        private const string Account = "weft";
        private const uint ZaikhalCell = 0x8090000C;

        // ---- fixture: three sources at three geometry radii, ambush NOT web-startable ----

        private const string SourceTemplate = @"{{
            ""id"": ""{0}"", ""displayName"": ""{1}"", ""geometry"": ""{2}"",
            ""geometryRadius"": {3}, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0,
            ""maxAlive"": 20, ""waveCount"": {{ ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 }},
            ""rewardRadius"": 60.0, ""compatibleGoals"": [ {4} ]{5},
            ""startFlavour"": ""x near {{anchor}}"", ""waveFlavour"": ""y"" }}";

        private static string Source(string id, string geometry, double radius, string goals, bool? webStartable) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, SourceTemplate, id, id, geometry, radius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), goals,
                webStartable == null ? "" : $", \"webStartable\": {(webStartable.Value ? "true" : "false")}");

        private static string Sources(bool? ambushWebStartable = false) => "{ \"sources\": [ " + string.Join(", ",
            Source("ambush", "edges", 45.0, "\"kill_count\"", ambushWebStartable),
            Source("rift", "ring", 25.0, "\"kill_count\"", null),
            Source("element_portal_fire", "disc", 10.0, "\"kill_count\", \"kill_boss\"", null)) + " ] }";

        private const string Families =
            @"{ ""families"": [ { ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"" }, " +
            @"{ ""id"": ""frostbound"", ""displayName"": ""the Frostbound"" } ] }";

        private const string Goals = @"{ ""goals"": [
            { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
              ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
              ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" },
            { ""id"": ""kill_boss"", ""displayName"": ""Slay the Champion"", ""type"": ""KillBoss"",
              ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"",
              ""progressTemplate"": ""The champion still stands."" } ] }";

        private const string Rewards = @"{ ""rewards"": [ {
            ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
            ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
            ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

        private const string Bosses =
            @"{ ""bosses"": [ { ""id"": ""ember_boss"", ""displayName"": ""Ember Boss"", ""wcid"": 1002619 } ] }";

        private static WorldEventAxisStore Store(bool? ambushWebStartable = false) =>
            WorldEventAxisStore.Parse(Sources(ambushWebStartable), Families, Goals, Rewards, null, Bosses);

        private static Weenie Member(uint wcid, string family, int level, bool caster)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "testcreature" + wcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    [PropertyBool.WorldEventCreature] = true,
                    [PropertyBool.Attackable] = true
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.Level] = level,
                    [PropertyInt.WorldEventRole] = 0
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.WorldEventFamily] = family,
                    [PropertyString.Name] = "Test Creature " + wcid
                }
            };

            if (caster)
                weenie.PropertiesSpellBook = new Dictionary<int, float> { [157] = 1.0f };

            return weenie;
        }

        private static WorldEventCatalog Catalog() => WorldEventCatalogBuilder.Build(new List<Weenie>
        {
            Member(1002604, "emberwrought", 20, true),
            Member(1002605, "frostbound", 30, false)
        }, out _);

        private static WorldEventLocationBody Location(double x = 96, double y = 96, uint cell = ZaikhalCell, long realm = 0, string label = "Zaikhal") =>
            new WorldEventLocationBody
            {
                Label = label, RealmId = realm, Cell = cell,
                X = x, Y = y, Z = 124, Qw = 1, Qx = 0, Qy = 0, Qz = 0
            };

        private static WorldEventStartBody Body(string source = "element_portal_fire", string goal = "kill_count",
            string boss = null, string family = "emberwrought", WorldEventLocationBody location = null, int? teaser = 60) =>
            new WorldEventStartBody
            {
                SourceId = source,
                GoalId = goal,
                BossId = boss,
                FamilyIds = family == null ? new List<string>() : new List<string> { family },
                TeaserSeconds = teaser,
                Location = location ?? Location()
            };

        private static bool RealmZeroOnly(ushort id) => id == 0;

        // ---- harness state ----

        private WorldEventAxisStore savedStore;
        private WorldEventCatalog savedCatalog;
        private Action<string> savedBroadcast;
        private Action<string> savedRelay;
        private List<string> broadcasts;
        private List<(string actor, string message)> audits;
        private int nearCount;

        [TestInitialize]
        public void Setup()
        {
            savedStore = WorldEventManager.Store;
            savedCatalog = WorldEventManager.Catalog;
            savedBroadcast = WorldEventAnnouncer.BroadcastSink;
            savedRelay = WorldEventAnnouncer.DiscordRelaySink;

            broadcasts = new List<string>();
            audits = new List<(string, string)>();
            nearCount = 0;

            WorldEventAnnouncer.BroadcastSink = broadcasts.Add;
            WorldEventAnnouncer.DiscordRelaySink = _ => { };

            WorldEventManager.InitializeWith(() => (Store(), Catalog()));
            PropertyManager.ModifyBool(EnabledKey, true);
        }

        [TestCleanup]
        public void Teardown()
        {
            try
            {
                WorldEventManager.StopCurrent(WorldEventOutcome.AbortedAdmin, "test cleanup");
            }
            finally
            {
                PropertyManager.ModifyBool(EnabledKey, false);
                WorldEventManager.InitializeWith(() => (savedStore, savedCatalog));
                WorldEventAnnouncer.BroadcastSink = savedBroadcast;
                WorldEventAnnouncer.DiscordRelaySink = savedRelay;
            }
        }

        private WorldEventAdminService Service(Action<IAction> enqueue = null, Func<ushort, bool> realms = null) =>
            new WorldEventAdminService(enqueue ?? (a => a.Act()), (actor, message) => audits.Add((actor, message)),
                TimeSpan.FromSeconds(5), realms ?? RealmZeroOnly, (_, _) => nearCount, () => 0, () => 180);

        private static string Json(object body) => JsonSerializer.Serialize(body, MarketApiHost.Json);

        private static JsonElement Parse(object body) => JsonDocument.Parse(Json(body)).RootElement.Clone();

        // ---- location validation (pure) ----

        private static bool Validate(WorldEventLocationBody location, Func<ushort, bool> realms, out Position position, out string reason) =>
            WorldEventAdminService.TryValidateLocation(location, realms, out position, out _, out reason);

        [TestMethod]
        public void Location_IndoorCell_IsRefused_OutdoorLowWordAccepted()
        {
            Assert.IsFalse(Validate(Location(cell: 0x80900100), RealmZeroOnly, out _, out var reason), "low word 0x0100 is an indoor cell");
            StringAssert.Contains(reason, "outdoor");

            Assert.IsTrue(Validate(Location(cell: 0x809000FF), RealmZeroOnly, out _, out _), "low word 0x00FF is below 0x0100 and must pass");
        }

        [TestMethod]
        public void Location_NaN_IsRefused_FiniteAccepted()
        {
            var location = Location();
            location.X = double.NaN;

            Assert.IsFalse(Validate(location, RealmZeroOnly, out _, out var reason));
            StringAssert.Contains(reason, "location.x");

            location.X = 96;
            Assert.IsTrue(Validate(location, RealmZeroOnly, out _, out _));
        }

        [TestMethod]
        public void Location_FloatOverflow_IsRefused()
        {
            var location = Location();
            location.Z = 1e39;  // finite as a double, infinity as the float a Position holds

            Assert.IsFalse(Validate(location, RealmZeroOnly, out _, out var reason));
            StringAssert.Contains(reason, "location.z");
        }

        [TestMethod]
        public void Location_X192_IsRefused_JustBelowAccepted()
        {
            Assert.IsFalse(Validate(Location(x: 192), RealmZeroOnly, out _, out var reason));
            StringAssert.Contains(reason, "[0, 192)");

            Assert.IsTrue(Validate(Location(x: 191.99), RealmZeroOnly, out _, out _));
        }

        [TestMethod]
        public void Location_UnknownRealm_IsRefused_KnownRealmAcceptedWithItsInstance()
        {
            Assert.IsFalse(Validate(Location(realm: 7), RealmZeroOnly, out _, out var reason));
            StringAssert.Contains(reason, "realm_id 7");

            Assert.IsTrue(Validate(Location(realm: 7), id => id == 0 || id == 7, out var position, out _));
            Assert.AreEqual(Position.InstanceIDFromVars(7, 0, false), position.Instance, "the instance is the realm's default instance");
            Assert.AreEqual(0x00070000u, position.Instance);

            Assert.IsTrue(Validate(Location(realm: 0), RealmZeroOnly, out var realm0, out _));
            Assert.AreEqual(0u, realm0.Instance, "realm 0 is instance 0");
        }

        [TestMethod]
        public void Location_Label_ControlCharacterOrTooLong_IsRefused()
        {
            Assert.IsFalse(Validate(Location(label: "Zai\u0007khal"), RealmZeroOnly, out _, out _), "a control character");
            Assert.IsFalse(Validate(Location(label: "Zai\u200Bkhal"), RealmZeroOnly, out _, out _), "an invisible formatting character");
            Assert.IsFalse(Validate(Location(label: new string('a', WorldEventAdminService.LabelMaxLength + 1)), RealmZeroOnly, out _, out _));

            Assert.IsTrue(Validate(Location(label: new string('a', WorldEventAdminService.LabelMaxLength)), RealmZeroOnly, out _, out _));
        }

        private static SourceThemeDef Def(string id) => Store().Sources[id];

        [TestMethod]
        public void EdgeMargin_IsRadiusPlus2ForDisc_PlusJitterForRingAndEdges()
        {
            Assert.AreEqual(10f + WorldEventAdminService.EdgeMarginMeters, WorldEventAdminService.EdgeMarginFor(Def("element_portal_fire")), "disc");
            Assert.AreEqual(25f + WorldEventAdminService.EdgeMarginMeters + WorldEventGeometry.DefaultJitterMetres,
                WorldEventAdminService.EdgeMarginFor(Def("rift")), "ring");
            Assert.AreEqual(45f + WorldEventAdminService.EdgeMarginMeters + WorldEventGeometry.DefaultJitterMetres,
                WorldEventAdminService.EdgeMarginFor(Def("ambush")), "edges");

            var single = Def("element_portal_fire");
            single.GeometryKind = SourceGeometry.Single;
            Assert.AreEqual(10f + WorldEventAdminService.EdgeMarginMeters, WorldEventAdminService.EdgeMarginFor(single), "single");
        }

        [TestMethod]
        public void FitsLandblock_EveryEdgeIsChecked_AndTheBoundaryIsInclusive()
        {
            // A disc of radius 10 needs 12 m of clearance.
            var margin = WorldEventAdminService.EdgeMarginFor(Def("element_portal_fire"));

            Assert.IsTrue(WorldEventAdminService.FitsLandblock(96, 96, margin), "control: the centre fits");

            Assert.IsTrue(WorldEventAdminService.FitsLandblock(12, 96, margin), "near x, exactly on the margin");
            Assert.IsFalse(WorldEventAdminService.FitsLandblock(11.99, 96, margin), "near x");
            Assert.IsTrue(WorldEventAdminService.FitsLandblock(180, 96, margin), "far x, exactly on the margin");
            Assert.IsFalse(WorldEventAdminService.FitsLandblock(180.5, 96, margin), "far x");
            Assert.IsTrue(WorldEventAdminService.FitsLandblock(96, 12, margin), "near y, exactly on the margin");
            Assert.IsFalse(WorldEventAdminService.FitsLandblock(96, 11.99, margin), "near y");
            Assert.IsTrue(WorldEventAdminService.FitsLandblock(96, 180, margin), "far y, exactly on the margin");
            Assert.IsFalse(WorldEventAdminService.FitsLandblock(96, 180.5, margin), "far y");
        }

        [TestMethod]
        public void Margin_RingSource_RefusedAtRadiusPlus3_AcceptedAtRadiusPlus4_01()
        {
            var store = Store();

            // rift is a ring of radius 25. radius + 3 clears the 2 m base but not the 2 m of jitter.
            var refused = WorldEventAdminService.TryBuildRequest(Body(source: "rift", location: Location(x: 28)), store,
                RealmZeroOnly, 180, "t", out _, out _);
            Assert.AreEqual(MarketError.InvalidLocation, refused?.Error);

            var accepted = WorldEventAdminService.TryBuildRequest(Body(source: "rift", location: Location(x: 29.01)), store,
                RealmZeroOnly, 180, "t", out _, out _);
            Assert.IsNull(accepted, accepted?.Reason);
        }

        [TestMethod]
        public void Margin_DiscSource_AcceptedAtRadiusPlus2_01()
        {
            var accepted = WorldEventAdminService.TryBuildRequest(Body(source: "element_portal_fire", location: Location(x: 12.01)), Store(),
                RealmZeroOnly, 180, "t", out _, out _);
            Assert.IsNull(accepted, accepted?.Reason);
        }

        [TestMethod]
        public void Catalog_PublishesEachSourcesEdgeMargin()
        {
            var sources = Parse(Service().Catalog().Body).GetProperty("sources").EnumerateArray()
                .ToDictionary(s => s.GetProperty("id").GetString(), s => s.GetProperty("edge_margin_meters").GetSingle());

            Assert.AreEqual(12f, sources["element_portal_fire"]);
            Assert.AreEqual(29f, sources["rift"]);
        }

        // A disc of geometry radius 25 that declares clearanceRadius 0 (the sky_rift shape).
        private static string OverheadSources() => "{ \"sources\": [ " + string.Join(", ",
            Source("sky", "disc", 25.0, "\"kill_count\"", null).Replace("\"geometryPoints\"", "\"clearanceRadius\": 0.0, \"geometryPoints\""),
            Source("rift", "ring", 25.0, "\"kill_count\"", null)) + " ] }";

        [TestMethod]
        public void EdgeMargin_UsesClearanceRadius_NotGeometryRadius()
        {
            var store = WorldEventAxisStore.Parse(OverheadSources(), Families, Goals, Rewards, null, Bosses);

            Assert.AreEqual(WorldEventAdminService.EdgeMarginMeters, WorldEventAdminService.EdgeMarginFor(store.Sources["sky"]), "clearance 0 leaves only the base margin");
            Assert.AreEqual(2f, WorldEventAdminService.EdgeMarginFor(store.Sources["sky"]));
            Assert.AreEqual(25f + WorldEventAdminService.EdgeMarginMeters + WorldEventGeometry.DefaultJitterMetres,
                WorldEventAdminService.EdgeMarginFor(store.Sources["rift"]), "a source with no clearanceRadius is unchanged");
        }

        [TestMethod]
        public void Margin_ZeroClearanceSource_AcceptedWhereSameSizedRingIsRefused()
        {
            var store = WorldEventAxisStore.Parse(OverheadSources(), Families, Goals, Rewards, null, Bosses);

            Assert.IsNull(WorldEventAdminService.TryBuildRequest(Body(source: "sky", location: Location(x: 3)), store,
                RealmZeroOnly, 180, "t", out _, out _), "sky: 3 m from the edge clears a 2 m margin");
            Assert.IsNotNull(WorldEventAdminService.TryBuildRequest(Body(source: "rift", location: Location(x: 3)), store,
                RealmZeroOnly, 180, "t", out _, out _), "control: the ring still needs its full radius");
        }

        [TestMethod]
        public void Catalog_PublishesClearanceRadiusBesideGeometryRadius()
        {
            WorldEventManager.InitializeWith(() => (WorldEventAxisStore.Parse(OverheadSources(), Families, Goals, Rewards, null, Bosses), Catalog()));

            var sources = Parse(Service().Catalog().Body).GetProperty("sources").EnumerateArray()
                .ToDictionary(s => s.GetProperty("id").GetString());

            Assert.AreEqual(0f, sources["sky"].GetProperty("clearance_radius").GetSingle());
            Assert.AreEqual(25f, sources["sky"].GetProperty("geometry_radius").GetSingle());
            Assert.AreEqual(2f, sources["sky"].GetProperty("edge_margin_meters").GetSingle());
            Assert.AreEqual(25f, sources["rift"].GetProperty("clearance_radius").GetSingle());
        }

        [TestMethod]
        public void Margin_PinnedSource_RefusedAtRadius25_AcceptedAtRadius10_SamePoint()
        {
            var store = Store();

            var refused = WorldEventAdminService.TryBuildRequest(Body(source: "rift", location: Location(x: 20)), store,
                RealmZeroOnly, 180, "t", out _, out _);

            Assert.IsNotNull(refused);
            Assert.AreEqual(MarketError.InvalidLocation, refused.Error);
            StringAssert.Contains(refused.Reason, "rift");

            var accepted = WorldEventAdminService.TryBuildRequest(Body(source: "element_portal_fire", location: Location(x: 20)), store,
                RealmZeroOnly, 180, "t", out var request, out _);

            Assert.IsNull(accepted, accepted?.Reason);
            Assert.AreEqual("element_portal_fire", request.SourceId);
        }

        // ---- webStartable ----

        [TestMethod]
        public void ShippedSources_AmbushIsTheOnlyNonWebStartableSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "events", "axes", "sources.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"could not find Content/events/axes/sources.json by walking up from {AppContext.BaseDirectory}");

            var json = File.ReadAllText(Path.Combine(dir.FullName, "Content", "events", "axes", "sources.json"));
            var store = WorldEventAxisStore.Parse(json, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("ambush"));
            Assert.IsFalse(store.Sources["ambush"].WebStartable, "sources.json must ship ambush with webStartable false");

            foreach (var source in store.Sources.Values.Where(s => s.Id != "ambush"))
                Assert.IsTrue(source.WebStartable, $"{source.Id} has no webStartable key and must default to true");
        }

        [TestMethod]
        public void Catalog_OmitsANonWebStartableSource_AndIncludesItWhenTheFlagIsAbsent()
        {
            var ids = SourceIds(Parse(Service().Catalog().Body));

            CollectionAssert.DoesNotContain(ids, "ambush");
            CollectionAssert.Contains(ids, "element_portal_fire");
            CollectionAssert.Contains(ids, "rift");

            // Same store with ambush's flag simply absent: the default (true) puts it back.
            WorldEventManager.InitializeWith(() => (Store(ambushWebStartable: null), Catalog()));

            CollectionAssert.Contains(SourceIds(Parse(Service().Catalog().Body)), "ambush");
        }

        private static List<string> SourceIds(JsonElement catalog) =>
            catalog.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();

        [TestMethod]
        public void Catalog_CarriesLimitsFromTheSharedConstants_AndTheAutoBoss()
        {
            var catalog = Parse(Service().Catalog().Body);
            var limits = catalog.GetProperty("limits");

            Assert.AreEqual(WorldEvent.MaxTeaserLeadSeconds, limits.GetProperty("teaser_max_seconds").GetInt32());
            Assert.AreEqual(180, limits.GetProperty("teaser_default_seconds").GetInt32());
            Assert.AreEqual(WorldEvent.DefaultMinDurationSeconds, limits.GetProperty("min_duration_default_seconds").GetInt32());
            Assert.AreEqual(WorldEvent.DefaultMaxDurationSeconds, limits.GetProperty("max_duration_seconds").GetInt32());
            Assert.AreEqual(WorldEventComposer.MaxComposedFamilies, limits.GetProperty("max_families").GetInt32());

            var bossIds = catalog.GetProperty("bosses").EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
            CollectionAssert.Contains(bossIds, "auto");
            CollectionAssert.Contains(bossIds, "none");
            CollectionAssert.Contains(bossIds, "ember_boss");

            var familyIds = catalog.GetProperty("families").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "emberwrought", "frostbound" }, familyIds);
        }

        [TestMethod]
        public void Start_PinnedAmbush_IsRefused_PinnedFireStarts()
        {
            var service = Service();

            var refused = service.Start(Body(source: "ambush"), Account);
            Assert.AreEqual(MarketError.SourceNotWebStartable, refused.Error);
            Assert.IsNull(WorldEventManager.Current, "a refused start stages nothing");

            var preview = service.Preview(Body(source: "ambush"));
            Assert.AreEqual(MarketError.SourceNotWebStartable, preview.Error, "preview refuses it too");

            var started = service.Start(Body(source: "element_portal_fire"), Account);
            Assert.AreEqual(MarketError.None, started.Error, started.Reason);
        }

        [TestMethod]
        public void Random_NeverResolvesToAmbush_ButCanWhenUnrestricted()
        {
            var store = Store();
            var catalog = Catalog();

            // The centre of the block: ambush's 45 m geometry FITS here, so only webStartable keeps it out.
            var body = Body(source: null, goal: null, family: null);
            body.Random = true;

            var refused = WorldEventAdminService.TryBuildRequest(body, store, RealmZeroOnly, 180, "t", out var request, out _);
            Assert.IsNull(refused, refused?.Reason);
            CollectionAssert.DoesNotContain(request.AllowedSourceIds.ToList(), "ambush");

            var restrictedSources = new HashSet<string>();
            var unrestrictedSources = new HashSet<string>();

            for (var seed = 0; seed < 300; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(seed)), error);
                restrictedSources.Add(composition.Source.Id);

                request.AllowedSourceIds = null;
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, request, out var open, out error, new Random(seed)), error);
                unrestrictedSources.Add(open.Source.Id);

                WorldEventAdminService.TryBuildRequest(body, store, RealmZeroOnly, 180, "t", out request, out _);
            }

            CollectionAssert.DoesNotContain(restrictedSources.ToList(), "ambush");
            CollectionAssert.Contains(unrestrictedSources.ToList(), "ambush",
                "control: the same search with no restriction must be able to reach ambush, or the assertion above proves nothing");
        }

        [TestMethod]
        public void Random_OnlyPicksASourceThatFitsTheLocation()
        {
            var store = Store();
            var catalog = Catalog();

            var body = Body(source: null, goal: null, family: null, location: Location(x: 20));
            body.Random = true;

            Assert.IsNull(WorldEventAdminService.TryBuildRequest(body, store, RealmZeroOnly, 180, "t", out var request, out _));
            CollectionAssert.AreEqual(new[] { "element_portal_fire" }, request.AllowedSourceIds.ToList(), "rift (25 m) does not fit at x = 20");

            for (var seed = 0; seed < 100; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(seed)), error);
                Assert.AreEqual("element_portal_fire", composition.Source.Id);
            }

            // At x = 10 nothing fits (even 10 m needs 12), so a random start is refused up front.
            var nothing = Body(source: null, goal: null, family: null, location: Location(x: 10));
            nothing.Random = true;

            var refused = WorldEventAdminService.TryBuildRequest(nothing, store, RealmZeroOnly, 180, "t", out _, out _);
            Assert.AreEqual(MarketError.InvalidLocation, refused?.Error);
        }

        // ---- the in-game path is unchanged ----

        [TestMethod]
        public void InGameRequest_KeepsAnchorHere_AndCanStillComposeAmbush()
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnchorPosition = new Position(ZaikhalCell, 96, 96, 124, 0, 0, 0, 1, 0),
                AnchorLabel = "Zaikhal (13.6N, 0.8E)"
            };

            Assert.IsTrue(WorldEventComposer.TryCompose(Store(), Catalog(), request, out var composition, out var error), error);
            Assert.AreEqual(WorldEventComposer.HereAnchorId, composition.Anchor.Id);
            Assert.AreEqual("ambush", composition.Source.Id, "webStartable never gates an in-game start");
        }

        // ---- refusal mapping through the real manager ----

        [TestMethod]
        public void TryStart_ReportsRefusalsAsAnEnum()
        {
            var request = new WorldEventRequest
            {
                SourceId = "element_portal_fire", FamilyId = "emberwrought", GoalId = "kill_count",
                AnchorPosition = new Position(ZaikhalCell, 96, 96, 124, 0, 0, 0, 1, 0), TeaserLeadSeconds = 60
            };

            PropertyManager.ModifyBool(EnabledKey, false);
            Assert.IsFalse(WorldEventManager.TryStart(request, out _, out _, out var refusal));
            Assert.AreEqual(WorldEventStartRefusal.Disabled, refusal);

            PropertyManager.ModifyBool(EnabledKey, true);

            request.MinDurationSeconds = WorldEvent.DefaultMaxDurationSeconds;
            Assert.IsFalse(WorldEventManager.TryStart(request, out _, out _, out refusal));
            Assert.AreEqual(WorldEventStartRefusal.InvalidDurations, refusal);
            request.MinDurationSeconds = WorldEvent.DefaultMinDurationSeconds;

            request.FamilyId = "no_such_family";
            Assert.IsFalse(WorldEventManager.TryStart(request, out _, out _, out refusal));
            Assert.AreEqual(WorldEventStartRefusal.CompositionFailed, refusal);
            request.FamilyId = "emberwrought";

            Assert.IsTrue(WorldEventManager.TryStart(request, out var evt, out var error, out refusal), error);
            Assert.AreEqual(WorldEventStartRefusal.None, refusal);

            Assert.IsFalse(WorldEventManager.TryStart(request, out _, out _, out refusal, out var runningRunId));
            Assert.AreEqual(WorldEventStartRefusal.AlreadyRunning, refusal);
            Assert.AreEqual(evt.RunId, runningRunId);

            // The original three-out signature still answers exactly as before.
            Assert.IsFalse(WorldEventManager.TryStart(request, out _, out var legacyError));
            StringAssert.Contains(legacyError, "already running");
        }

        [TestMethod]
        public void Start_Disabled_IsWorldEventsDisabled_EnabledStarts()
        {
            var service = Service();

            PropertyManager.ModifyBool(EnabledKey, false);
            Assert.AreEqual(MarketError.WorldEventsDisabled, service.Start(Body(), Account).Error);
            Assert.AreEqual(MarketError.WorldEventsDisabled, service.Preview(Body()).Error);

            PropertyManager.ModifyBool(EnabledKey, true);
            Assert.AreEqual(MarketError.None, service.Start(Body(), Account).Error);
        }

        [TestMethod]
        public void Start_WhileRunning_IsWorldEventRunning_CarryingTheRunId()
        {
            var service = Service();

            var first = Parse(service.Start(Body(), Account).Body);
            var runId = first.GetProperty("run_id").GetUInt32();

            var second = service.Start(Body(), Account);
            Assert.AreEqual(MarketError.WorldEventRunning, second.Error);
            Assert.AreEqual(runId, second.RunId);
        }

        [TestMethod]
        public void Start_KillBossWithBossNone_IsRefused_FamilyChampionStarts()
        {
            var service = Service();

            var refused = service.Start(Body(goal: "kill_boss", boss: "none"), Account);
            Assert.AreEqual(MarketError.WorldEventRefused, refused.Error);
            StringAssert.Contains(refused.Reason, "kill_boss");
            Assert.IsNull(WorldEventManager.Current);

            Assert.AreEqual(MarketError.None, service.Start(Body(goal: "kill_boss", boss: "family_champion"), Account).Error);
        }

        [TestMethod]
        public void Start_UnknownFamily_IsRefused_KnownFamilyStarts()
        {
            var service = Service();

            var refused = service.Start(Body(family: "no_such_family"), Account);
            Assert.AreEqual(MarketError.WorldEventRefused, refused.Error);
            StringAssert.Contains(refused.Reason, "unknown family");

            Assert.AreEqual(MarketError.None, service.Start(Body(family: "frostbound"), Account).Error);
        }

        [TestMethod]
        public void Start_TeaserOutOfRange_IsRefused_MaxIsAccepted()
        {
            var service = Service();

            var refused = service.Start(Body(teaser: WorldEvent.MaxTeaserLeadSeconds + 1), Account);
            Assert.AreEqual(MarketError.WorldEventRefused, refused.Error);
            StringAssert.Contains(refused.Reason, "teaser_seconds");

            var started = Parse(service.Start(Body(teaser: WorldEvent.MaxTeaserLeadSeconds), Account).Body);
            Assert.AreEqual(WorldEvent.MaxTeaserLeadSeconds, started.GetProperty("teaser_seconds").GetInt32());
        }

        [TestMethod]
        public void Start_AbsentTeaser_UsesTheTunableDefault()
        {
            var started = Parse(Service().Start(Body(teaser: null), Account).Body);
            Assert.AreEqual(180, started.GetProperty("teaser_seconds").GetInt32());
        }

        [TestMethod]
        public void Stop_NothingRunning_IsNotRunning()
        {
            var outcome = Service().Stop(new WorldEventStopBody { Mode = "abort", ExpectedRunId = 1 }, Account);
            Assert.AreEqual(MarketError.WorldEventNotRunning, outcome.Error);
        }

        [TestMethod]
        public void Stop_RunIdMismatch_IsRunChanged_WithTheLiveRunId_MatchStops()
        {
            var service = Service();
            var runId = Parse(service.Start(Body(), Account).Body).GetProperty("run_id").GetUInt32();

            var mismatch = service.Stop(new WorldEventStopBody { Mode = "abort", ExpectedRunId = runId + 1 }, Account);
            Assert.AreEqual(MarketError.WorldEventRunChanged, mismatch.Error);
            Assert.AreEqual(runId, mismatch.RunId);
            Assert.IsNotNull(WorldEventManager.Current, "a mismatched stop leaves the run alone");

            var stopped = service.Stop(new WorldEventStopBody { Mode = "abort", ExpectedRunId = runId }, Account);
            Assert.AreEqual(MarketError.None, stopped.Error, stopped.Reason);
            Assert.IsNull(WorldEventManager.Current, "the matching stop ends the run");
            Assert.AreEqual("aborted_admin", Parse(stopped.Body).GetProperty("outcome").GetString());
        }

        [TestMethod]
        public void Stop_MissingExpectedRunIdOrBadMode_IsRefused()
        {
            var service = Service();
            service.Start(Body(), Account);

            Assert.AreEqual(MarketError.WorldEventRefused, service.Stop(new WorldEventStopBody { Mode = "abort" }, Account).Error);
            Assert.AreEqual(MarketError.WorldEventRefused, service.Stop(new WorldEventStopBody { Mode = "explode", ExpectedRunId = 1 }, Account).Error);
            Assert.AreEqual(MarketError.WorldEventRefused, service.Stop(null, Account).Error);
            Assert.IsNotNull(WorldEventManager.Current);
        }

        [TestMethod]
        public void Stop_ReportsTheOutcomeTheRunActuallyEndedAs()
        {
            var service = Service();
            var runId = Parse(service.Start(Body(), Account).Body).GetProperty("run_id").GetUInt32();
            var evt = WorldEventManager.Current;

            var stopped = Parse(service.Stop(new WorldEventStopBody { Mode = "fail", ExpectedRunId = runId }, Account).Body);

            Assert.AreEqual(WorldEventOutcome.FailedTimeout, evt.Outcome);
            Assert.AreEqual("failed_timeout", stopped.GetProperty("outcome").GetString());
            Assert.AreEqual(JsonNamingPolicy.SnakeCaseLower.ConvertName(evt.State.ToString()), stopped.GetProperty("state").GetString());
        }

        // ---- F1: a run that has already resolved is not stopped ----

        private (WorldEventAdminService service, WorldEvent evt, uint runId) RunInRewarding()
        {
            var service = Service();
            var runId = Parse(service.Start(Body(), Account).Body).GetProperty("run_id").GetUInt32();
            var evt = WorldEventManager.Current;

            // A Success resolves the run and opens its claim window (Rewarding) - the state a late web stop
            // would land in.
            evt.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "precondition: the claim window is open");
            Assert.AreSame(evt, WorldEventManager.Current);

            return (service, evt, runId);
        }

        [TestMethod]
        public void Stop_Fail_AfterTheRunResolved_IsRefused_AndAuditsNothing()
        {
            var (service, evt, runId) = RunInRewarding();
            var auditsBefore = audits.Count;

            var outcome = service.Stop(new WorldEventStopBody { Mode = "fail", ExpectedRunId = runId }, Account);

            Assert.AreEqual(MarketError.WorldEventNotRunning, outcome.Error);
            StringAssert.Contains(outcome.Reason, "already finished");
            Assert.AreEqual(auditsBefore, audits.Count, "a refused stop writes no audit line");
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        [TestMethod]
        public void Stop_Abort_AfterTheRunResolved_IsRefused_AndLeavesTheClaimWindowOpen()
        {
            var (service, evt, runId) = RunInRewarding();
            var auditsBefore = audits.Count;

            var outcome = service.Stop(new WorldEventStopBody { Mode = "abort", ExpectedRunId = runId }, Account);

            Assert.AreEqual(MarketError.WorldEventNotRunning, outcome.Error);
            Assert.AreEqual(auditsBefore, audits.Count);

            // An abort that got through would have taken Finish's claim-window-cut-short branch: Outcome
            // becomes AbortedAdmin, DestroyCaches runs and the run goes to cleanup. None of that happened.
            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "the claim window is still open");
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome, "the caches were not cut short");
        }

        // ---- F3: guards that each need their own flipping case ----

        [TestMethod]
        public void Location_YOutOfRange_AndNegativeX_AreRefused()
        {
            Assert.IsFalse(Validate(Location(y: 192), RealmZeroOnly, out _, out _), "y = 192");
            Assert.IsFalse(Validate(Location(y: -1), RealmZeroOnly, out _, out _), "y = -1");
            Assert.IsFalse(Validate(Location(x: -1), RealmZeroOnly, out _, out _), "x = -1");

            Assert.IsTrue(Validate(Location(x: 0, y: 191.99), RealmZeroOnly, out _, out _), "control: 0 and 191.99 are inside");
        }

        [TestMethod]
        public void Location_RealmIdOutOfRange_IsRefused_NeverWrappedOrThrown()
        {
            // A probe that knows EVERY realm, so only the range check can refuse: 65536 would wrap to realm 0,
            // and 32768 would make InstanceIDFromVars throw.
            static bool Any(ushort _) => true;

            foreach (var realm in new long[] { 65536, 32768, -1 })
            {
                var body = Body(location: Location(realm: realm));
                var outcome = WorldEventAdminService.TryBuildRequest(body, Store(), Any, 180, "t", out _, out _);

                Assert.AreEqual(MarketError.InvalidLocation, outcome?.Error, $"realm {realm}");
                StringAssert.Contains(outcome.Reason, "realm_id");
            }

            Assert.IsNull(WorldEventAdminService.TryBuildRequest(Body(location: Location(realm: 32767)), Store(), Any, 180, "t", out _, out _),
                "control: the top realm id is accepted");
        }

        [TestMethod]
        public void Location_ZeroRotation_IsRefused_AnyNonZeroComponentAccepted()
        {
            var location = Location();
            location.Qw = 0;

            Assert.IsFalse(Validate(location, RealmZeroOnly, out _, out var reason));
            StringAssert.Contains(reason, "rotation");

            location.Qz = 1;
            Assert.IsTrue(Validate(location, RealmZeroOnly, out _, out _));
        }

        [TestMethod]
        public void Teaser_Negative_IsRefused_ZeroAccepted()
        {
            var refused = WorldEventAdminService.TryBuildRequest(Body(teaser: -1), Store(), RealmZeroOnly, 180, "t", out _, out _);
            Assert.AreEqual(MarketError.WorldEventRefused, refused?.Error);
            StringAssert.Contains(refused.Reason, "teaser_seconds");

            Assert.IsNull(WorldEventAdminService.TryBuildRequest(Body(teaser: 0), Store(), RealmZeroOnly, 180, "t", out _, out _));
        }

        [TestMethod]
        public void AdminWorldCall_WorkThrows_IsFaulted()
        {
            var status = AdminWorldCall.Run<int>(a => a.Act(), () => throw new InvalidOperationException("boom"),
                TimeSpan.FromSeconds(5), out var result);

            Assert.AreEqual(AdminWorldCall.Status.Faulted, status);
            Assert.AreEqual(0, result);

            Assert.AreEqual(AdminWorldCall.Status.Completed,
                AdminWorldCall.Run(a => a.Act(), () => 7, TimeSpan.FromSeconds(5), out result), "control");
            Assert.AreEqual(7, result);
        }

        [TestMethod]
        public void Service_WorkThrowsOnTheWorldThread_IsServerError()
        {
            // The realm probe runs inside the world action, so a throwing probe faults the work itself.
            var service = new WorldEventAdminService(a => a.Act(), (_, _) => { }, TimeSpan.FromSeconds(5),
                _ => throw new InvalidOperationException("realm registry down"), (_, _) => 0, () => 0, () => 180);

            Assert.AreEqual(MarketError.ServerError, service.Start(Body(), Account).Error);
            Assert.AreEqual(MarketError.ServerError, service.Preview(Body()).Error);
            Assert.IsNull(WorldEventManager.Current);
        }

        [TestMethod]
        public void PreChecks_DisabledAndRunning_AnswerBeforeTheBodyIsValidated()
        {
            var service = Service();
            var badLocation = Body(location: Location(x: double.NaN));

            PropertyManager.ModifyBool(EnabledKey, false);
            Assert.AreEqual(MarketError.WorldEventsDisabled, service.Start(badLocation, Account).Error);
            Assert.AreEqual(MarketError.WorldEventsDisabled, service.Preview(badLocation).Error);

            PropertyManager.ModifyBool(EnabledKey, true);
            Assert.AreEqual(MarketError.InvalidLocation, service.Start(badLocation, Account).Error, "control: enabled, the body is what refuses");

            var runId = Parse(service.Start(Body(), Account).Body).GetProperty("run_id").GetUInt32();
            var running = service.Start(badLocation, Account);
            Assert.AreEqual(MarketError.WorldEventRunning, running.Error);
            Assert.AreEqual(runId, running.RunId);
        }

        // ---- F3: the composer's source restriction, layer by layer ----

        private static WorldEventRequest ComposerRequest(bool random, string source, string goal, IReadOnlyCollection<string> allowed) =>
            new WorldEventRequest
            {
                Random = random,
                SourceId = source,
                FamilyIds = random ? new List<string>() : new List<string> { "emberwrought" },
                GoalId = goal,
                AnchorPosition = new Position(ZaikhalCell, 96, 96, 124, 0, 0, 0, 1, 0),
                PositionAnchorId = WorldEventComposer.WebAnchorId,
                AllowedSourceIds = allowed
            };

        [TestMethod]
        public void Composer_ExplicitSourceOutsideTheAllowedSet_IsRefused()
        {
            var request = ComposerRequest(false, "ambush", "kill_count", new[] { "rift" });

            Assert.IsFalse(WorldEventComposer.TryCompose(Store(), Catalog(), request, out _, out var error));
            StringAssert.Contains(error, "not allowed");

            request.AllowedSourceIds = null;
            Assert.IsTrue(WorldEventComposer.TryCompose(Store(), Catalog(), request, out _, out error), "control: " + error);
        }

        [TestMethod]
        public void Composer_RandomWithAPinnedDisallowedSource_IsRefusedAsNotAllowed()
        {
            // The pinned source is filtered out of the search, so the refusal comes from the probe compose -
            // which only knows the restriction because ClonePinned carries AllowedSourceIds across.
            var request = ComposerRequest(true, "ambush", null, new[] { "rift" });

            Assert.IsFalse(WorldEventComposer.TryCompose(Store(), Catalog(), request, out _, out var error, new Random(1)));
            StringAssert.Contains(error, "not allowed");
        }

        [TestMethod]
        public void Composer_RandomRoll_KeepsTheWebAnchorId()
        {
            // The composed run's anchor id comes from the CLONED request the search composes with.
            var request = ComposerRequest(true, null, null, new[] { "rift", "element_portal_fire" });

            Assert.IsTrue(WorldEventComposer.TryCompose(Store(), Catalog(), request, out var composition, out var error, new Random(1)), error);
            Assert.AreEqual(WorldEventComposer.WebAnchorId, composition.Anchor.Id);
        }

        [TestMethod]
        public void Composer_RandomWithNoCompatibleAllowedSource_NamesTheAllowedSource()
        {
            // Allowed: rift only, which has no kill_boss. With the random search restricted, nothing is
            // tried and the probe composes rift + kill_boss, so the reason names the real mismatch. An
            // unrestricted search would instead try element_portal_fire and report it as not allowed; a probe
            // that ignored the restriction would pick ambush (first by id) and report THAT as not allowed.
            var request = ComposerRequest(true, null, "kill_boss", new[] { "rift" });

            Assert.IsFalse(WorldEventComposer.TryCompose(Store(), Catalog(), request, out _, out var error, new Random(1)));
            StringAssert.Contains(error, "is not compatible with source 'rift'");
        }

        [TestMethod]
        public void Composer_RandomWithAnEmptyAllowedSet_SaysNoSourceIsAllowed()
        {
            var request = ComposerRequest(true, null, null, Array.Empty<string>());

            Assert.IsFalse(WorldEventComposer.TryCompose(Store(), Catalog(), request, out _, out var error, new Random(1)));
            StringAssert.Contains(error, "no loaded source is allowed");
        }

        // ---- F4: refusal reasons name the web body's fields, never an in-game flag ----

        private static string Reason(WorldEventStartBody body, WorldEventAxisStore store = null)
        {
            var outcome = WorldEventAdminService.TryBuildRequest(body, store ?? Store(), RealmZeroOnly, 180, "t", out _, out _);

            Assert.IsNotNull(outcome, "expected a refusal");
            Assert.AreEqual(MarketError.WorldEventRefused, outcome.Error);
            Assert.IsFalse(outcome.Reason.Contains("--"), $"an in-game flag leaked into a web reason: {outcome.Reason}");

            return outcome.Reason;
        }

        [TestMethod]
        public void Reasons_AreWebPhrased()
        {
            Assert.AreEqual("source_id is required unless random is true", Reason(Body(source: null)));
            Assert.AreEqual("goal_id is required unless random is true", Reason(Body(goal: null)));
            Assert.AreEqual("family_ids needs at least one family unless random is true", Reason(Body(family: null)));

            var blank = Body();
            blank.FamilyIds = new List<string> { "emberwrought", " " };
            Assert.AreEqual("family_ids cannot contain a blank id", Reason(blank));

            var three = Body();
            three.FamilyIds = new List<string> { "a", "b", "c" };
            StringAssert.Contains(Reason(three), "at most 2");

            StringAssert.Contains(Reason(Body(goal: "kill_boss", boss: null)), "needs boss_id");
            StringAssert.Contains(Reason(Body(goal: "kill_boss", boss: "none")), "needs boss_id");

            var minTooHigh = Body();
            minTooHigh.MinDurationSeconds = WorldEvent.DefaultMaxDurationSeconds;
            Assert.AreEqual($"min_duration_seconds must be between 0 and {WorldEvent.DefaultMaxDurationSeconds - 1}", Reason(minTooHigh));
        }

        [TestMethod]
        public void Reasons_MissingRewardOnAServerWithoutTheDefault_IsWebPhrased()
        {
            const string rewards = @"{ ""rewards"": [ {
                ""id"": ""special"", ""displayName"": ""Special"",
                ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
                ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

            var store = WorldEventAxisStore.Parse(Sources(), Families, Goals, rewards, null, Bosses);

            StringAssert.Contains(Reason(Body(), store), "reward_id is required");

            var withReward = Body();
            withReward.RewardId = "special";
            Assert.IsNull(WorldEventAdminService.TryBuildRequest(withReward, store, RealmZeroOnly, 180, "t", out _, out _), "control");
        }

        [TestMethod]
        public void Reasons_RandomRequest_DoesNotRequireTheAxes()
        {
            var body = Body(source: null, goal: "kill_boss", boss: null, family: null);
            body.Random = true;

            Assert.IsNull(WorldEventAdminService.TryBuildRequest(body, Store(), RealmZeroOnly, 180, "t", out _, out _),
                "random rolls the source, the families and (for kill_boss) an auto boss");
        }

        // ---- audit ----

        [TestMethod]
        public void Audit_ExactlyOneWebLineOnStartAndOnStop_NoneOnPreview()
        {
            var service = Service();

            Assert.AreEqual(MarketError.None, service.Preview(Body()).Error);
            Assert.AreEqual(0, audits.Count, "preview audits nothing");

            var runId = Parse(service.Start(Body(), Account).Body).GetProperty("run_id").GetUInt32();
            Assert.AreEqual(1, audits.Count, "one line per start");
            Assert.AreEqual("weft", audits[0].actor, "the bare web account, as the settings and announce routes");
            StringAssert.Contains(audits[0].message, $"run {runId}");

            service.Stop(new WorldEventStopBody { Mode = "fail", ExpectedRunId = runId }, Account);
            Assert.AreEqual(2, audits.Count, "one line per stop");
            Assert.AreEqual("weft", audits[1].actor);
            StringAssert.Contains(audits[1].message, "FailedTimeout");

            // A refused start writes nothing.
            service.Start(Body(source: "ambush"), Account);
            Assert.AreEqual(2, audits.Count);
        }

        // ---- no Session, no Player ----

        /// <summary>
        /// The in-game handler refuses a start without session.Player before it builds a request at all
        /// (WorldEventCommands.HandleStart). This harness has NO online player and NO session anywhere, and
        /// the web start still reaches Current - so nothing on the path needs one. The anchor, label and
        /// invoker all come from the body and the account name, which is also asserted.
        /// </summary>
        [TestMethod]
        public void Start_NeedsNoSessionOrPlayer()
        {
            Assert.AreEqual(0, PlayerManager.GetOnlineCount(), "precondition: no player is online in this harness");

            var outcome = Service().Start(Body(location: Location(x: 100, y: 90, label: "Zaikhal")), Account);
            Assert.AreEqual(MarketError.None, outcome.Error, outcome.Reason);

            var evt = WorldEventManager.Current;
            Assert.IsNotNull(evt);
            Assert.AreEqual(WorldEventState.Idle, evt.State, "teaser out, not yet staged");
            Assert.AreEqual("weft", evt.Request.Invoker);
            Assert.AreEqual(WorldEventComposer.WebAnchorId, evt.Composition.Anchor.Id);
            Assert.AreEqual(0u, evt.Composition.AnchorPosition.Instance);
            Assert.AreEqual(100f, evt.Composition.AnchorPosition.PositionX);

            var expectedLabel = $"Zaikhal ({evt.Composition.AnchorPosition.GetMapCoordStr()})";
            Assert.AreEqual(expectedLabel, evt.AnchorName);
            Assert.IsTrue(broadcasts.Any(b => b.Contains(expectedLabel)), "the teaser names the labelled spot");
        }

        // ---- preview ----

        [TestMethod]
        public void Preview_ResolvesRandom_ChangesNothing_AndWarnsOnTeaserZeroWithNobodyNear()
        {
            var service = Service();

            var body = Body(source: null, goal: null, family: null, teaser: 0);
            body.Random = true;

            var preview = service.Preview(body);
            Assert.AreEqual(MarketError.None, preview.Error, preview.Reason);
            Assert.IsNull(WorldEventManager.Current, "preview stages nothing");

            var json = Parse(preview.Body);
            Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("source_id").GetString()));
            Assert.AreNotEqual("ambush", json.GetProperty("source_id").GetString());
            Assert.IsTrue(json.GetProperty("family_ids").GetArrayLength() > 0);
            Assert.IsTrue(Warnings(json).Any(w => w.Contains("teaser is 0")));

            nearCount = 3;
            Assert.IsFalse(Warnings(Parse(service.Preview(body).Body)).Any(w => w.Contains("teaser is 0")),
                "with players near, the teaser-0 warning goes away");
        }

        [TestMethod]
        public void Preview_WhileRunning_WarnsInsteadOfRefusing()
        {
            var service = Service();
            service.Start(Body(), Account);

            var preview = service.Preview(Body());
            Assert.AreEqual(MarketError.None, preview.Error);
            Assert.IsTrue(Warnings(Parse(preview.Body)).Any(w => w.Contains("in progress")));
        }

        private static List<string> Warnings(JsonElement json) =>
            json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();

        // ---- status ----

        [TestMethod]
        public void Status_IdleThenTeasing()
        {
            var service = Service();

            var idle = Parse(service.Status().Body);
            Assert.IsFalse(idle.GetProperty("running").GetBoolean());
            Assert.AreEqual("none", idle.GetProperty("state").GetString());

            var runId = Parse(service.Start(Body(teaser: 600), Account).Body).GetProperty("run_id").GetUInt32();

            var live = Parse(service.Status().Body);
            Assert.IsTrue(live.GetProperty("running").GetBoolean());
            Assert.AreEqual(runId, live.GetProperty("run_id").GetUInt32());
            Assert.AreEqual("idle", live.GetProperty("state").GetString());
            Assert.IsTrue(live.GetProperty("teaser_remaining_seconds").GetInt32() > 500);
            StringAssert.StartsWith(live.GetProperty("location_label").GetString(), "Zaikhal (");
        }

        // ---- the world-thread marshal ----

        [TestMethod]
        public void WorldThread_NeverRun_IsTimeout_AndAThrowIsServerError()
        {
            var queued = new List<IAction>();
            var stalled = new WorldEventAdminService(queued.Add, (_, _) => { }, TimeSpan.FromMilliseconds(50),
                RealmZeroOnly, (_, _) => 0, () => 0, () => 180);

            Assert.AreEqual(MarketError.Timeout, stalled.Start(Body(), Account).Error);

            // The abandoned action, if the world thread reaches it late, must not start a run.
            queued[0].Act();
            Assert.IsNull(WorldEventManager.Current, "work not yet started when the waiter gave up is skipped");

            var throwing = new WorldEventAdminService(a => a.Act(), (_, _) => throw new InvalidOperationException("audit down"),
                TimeSpan.FromSeconds(5), RealmZeroOnly, (_, _) => 0, () => 0, () => 180);

            // The audit sink throwing AFTER a successful start is logged, not reported as a failure.
            Assert.AreEqual(MarketError.None, throwing.Start(Body(), Account).Error);
        }

        // ---- the label format ----

        [TestMethod]
        public void DescribeLabeled_IsLabelPlusCoords_AndFallsBackWhenBlank()
        {
            var position = new Position(ZaikhalCell, 36.86f, 79.69f, 124f, 0, 0, 0, 1, 0);

            Assert.AreEqual($"Zaikhal ({position.GetMapCoordStr()})", WorldEventTownIndex.DescribeLabeled("Zaikhal", position));
            Assert.AreEqual(WorldEventTownIndex.Describe(position), WorldEventTownIndex.DescribeLabeled("  ", position));
        }
    }
}

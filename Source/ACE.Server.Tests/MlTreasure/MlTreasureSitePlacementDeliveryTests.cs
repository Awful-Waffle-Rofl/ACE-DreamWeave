using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.MlTreasure;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// The DELIVERY half of the round 15 dig-site rule: MlTreasureDrop.StampSite and
    /// TreasureMapHandler.EnsureSiteStamped, run against a real (in-memory) map WorldObject and real
    /// Positions, so the property writes the drop hook and the first-use path actually make are what is
    /// asserted - not only the pure PlaceNear geometry MlTreasureSiteStoreTests covers.
    ///
    /// Both methods are the exact ones the runtime calls (MlTreasureDrop.TryDropTreasureMap and
    /// TreasureMapHandler.TryHandleUse), with the catalogue and radius passed in rather than read from
    /// MlTreasureSiteStore.Instance / PropertyManager, which this harness cannot serve. What stays
    /// uncovered is the two callers' own glue: the corpse add and the Player-side chat, which need a live
    /// world and a Player.
    ///
    /// TERRAIN. Whether the pickup cell may itself be a dig site is decided by catalogue membership
    /// (MlTreasureSiteStore.IsCatalogueCell): dig-sites.tsv holds one row per terrain cell that passed
    /// SurfaceDigSitesCommand.IsDigSite, whose water rule (SurfaceTerrain.IsWater on all four corners) is
    /// pinned in ACE.Content.Tools.Tests/SurfaceDigSitesTests. A cell on water terrain therefore has no row,
    /// and that is how the fixtures below model one - this harness has no dat files to read terrain from.
    /// </summary>
    [TestClass]
    public class MlTreasureSitePlacementDeliveryTests
    {
        private static uint nextGuid = 0x7D5F0000;   // static guid range, clear of GuidManager

        private const string Header = "landblock\tcell_x\tcell_y\tlocal_x\tlocal_y\tz\tmap_ns\tmap_ew\tboss_ok";

        /// <summary>720 m, the shipped cap, in map units.</summary>
        private const float Cap = 3f;

        /// <summary>The pickup cell: local (10, 10) is terrain cell (0, 0) of Bluespire 0x21B0.</summary>
        private const int PickupCx = 0;
        private const int PickupCy = 0;

        private static WorldObject MakeMap(bool relaria = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = MlTreasureDrop.TreasureMapWcid,
                WeenieType = WeenieType.Gem,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Gem } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Treasure Map" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.TreasureMap, true } },
            };

            if (relaria)
                weenie.PropertiesInt[PropertyInt.TreasureMapBossWcid] = (int)MlTreasureDrop.RelariaBossWcid;

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>An outdoor cell of Bluespire (0x21B0), realm 1 - the landblock MlTreasureLandblockTests uses.</summary>
        private static Position MlOutdoor(uint realm = 1, uint cell = 0x21B00001)
        {
            var instance = Position.InstanceIDFromVars(realmId: (ushort)realm, shortInstanceId: 0, isTemporaryRuleset: false);

            return new Position(cell, 10f, 10f, 0f, 0f, 0f, 0f, 1f, instance);
        }

        /// <summary>One catalogue row. Cells default to (5, 5) - NOT the pickup cell - unless stated.</summary>
        private static string Row(Vector2 mapCoords, bool bossOk, int cx = 5, int cy = 5)
            => $"0x21B0\t{cx}\t{cy}\t10\t10\t0\t{mapCoords.Y.ToString(CultureInfo.InvariantCulture)}\t{mapCoords.X.ToString(CultureInfo.InvariantCulture)}\t{(bossOk ? 1 : 0)}";

        private static MlTreasureSiteStore Store(params string[] rows)
        {
            var lines = new List<string> { Header };
            lines.AddRange(rows);
            return MlTreasureSiteStore.ParseLines(lines);
        }

        private static Vector2 Stamped(WorldObject map)
        {
            var ns = map.GetProperty(PropertyFloat.TreasureMapNorthSouth);
            var ew = map.GetProperty(PropertyFloat.TreasureMapEastWest);

            Assert.IsNotNull(ns, "9010 was not stamped");
            Assert.IsNotNull(ew, "9011 was not stamped");

            return new Vector2((float)ew.Value, (float)ns.Value);
        }

        [TestMethod]
        public void StampSite_WritesACatalogueSiteInRange()
        {
            var pickup = MlOutdoor();
            var pickupMap = pickup.GetMapCoords().Value;
            var near = pickupMap + new Vector2(0.5f, 0f);

            var map = MakeMap();

            Assert.IsTrue(MlTreasureDrop.StampSite(map, pickup, false,
                Store(Row(near, true), Row(pickupMap + new Vector2(40f, 40f), true)), Cap, _ => 0));

            var site = Stamped(map);

            Assert.AreEqual(near.X, site.X, 1e-3f);
            Assert.AreEqual(near.Y, site.Y, 1e-3f);
            Assert.IsNull(map.GetProperty(PropertyInt.TreasureMapBossWcid));
        }

        [TestMethod]
        public void StampSite_APickupOnWaterIsRefusedAndTheNearestCatalogueSiteIsChosen()
        {
            // The corpse lies on a water cell: (0, 0) has no catalogue row, because SurfaceDigSitesCommand
            // rejects any cell with a wet corner. Nothing is within the 720 m cap, so round 15's first cut
            // would have stamped the corpse's own position - underwater. The nearest catalogue site (40 map
            // units off) must be chosen instead, not the farther one and not the pickup point.
            var pickup = MlOutdoor();
            var pickupMap = pickup.GetMapCoords().Value;
            var store = Store(Row(pickupMap + new Vector2(0f, 60f), true), Row(pickupMap + new Vector2(40f, 0f), true));

            Assert.IsFalse(store.IsCatalogueCell(0x21B0, PickupCx, PickupCy), "fixture: the pickup cell must be the uncatalogued (water) one");

            var map = MakeMap();

            Assert.IsTrue(MlTreasureDrop.StampSite(map, pickup, false, store, Cap));

            var site = Stamped(map);

            Assert.AreNotEqual(pickupMap, site, "an underwater pickup point must never be the dig site");
            Assert.AreEqual(pickupMap.X + 40f, site.X, 1e-3f);
            Assert.AreEqual(pickupMap.Y, site.Y, 1e-3f);
        }

        [TestMethod]
        public void StampSite_AnUndiggablePickupStillTakesASiteInsideTheCapWhenOneExists()
        {
            var pickup = MlOutdoor();
            var pickupMap = pickup.GetMapCoords().Value;
            var map = MakeMap();

            Assert.IsTrue(MlTreasureDrop.StampSite(map, pickup, false,
                Store(Row(pickupMap + new Vector2(1f, 1f), false), Row(pickupMap + new Vector2(0.5f, 0f), false)), Cap));

            Assert.IsTrue(MlTreasureGeometry.DistanceMetres(pickupMap, Stamped(map)) <= MlTreasureSiteStore.MaxSiteDistanceMetres);
        }

        [TestMethod]
        public void StampSite_EmptyCatalogue_PlacesNothingBecauseNoCellCanBeVouchedFor()
        {
            var map = MakeMap();

            Assert.IsFalse(MlTreasureDrop.StampSite(map, MlOutdoor(), false, MlTreasureSiteStore.Empty, Cap));
            Assert.IsNull(map.GetProperty(PropertyFloat.TreasureMapNorthSouth));
        }

        [TestMethod]
        public void StampSite_RefusesAnIndoorPickupAndWritesNothing()
        {
            // 0x21B00101 is the first indoor cell of the same landblock: no map coordinates.
            var map = MakeMap();

            Assert.IsFalse(MlTreasureDrop.StampSite(map, MlOutdoor(cell: 0x21B00101), false, Store(), Cap));
            Assert.IsNull(map.GetProperty(PropertyFloat.TreasureMapNorthSouth));
            Assert.IsNull(map.GetProperty(PropertyFloat.TreasureMapEastWest));
        }

        [TestMethod]
        public void StampSite_RefusesRetailMaraeLasselInRealm0()
        {
            var map = MakeMap();

            Assert.IsFalse(MlTreasureDrop.StampSite(map, MlOutdoor(realm: 0), false, Store(Row(new Vector2(0f, 0f), true)), Cap));
            Assert.IsNull(map.GetProperty(PropertyFloat.TreasureMapNorthSouth));
        }

        [TestMethod]
        public void StampSite_RelariaWithABossSiteInRange_StaysARelariaMap()
        {
            var pickup = MlOutdoor();
            var pickupMap = pickup.GetMapCoords().Value;
            var map = MakeMap();

            Assert.IsTrue(MlTreasureDrop.StampSite(map, pickup, true, Store(Row(pickupMap + new Vector2(1f, 1f), true)), Cap));

            Assert.AreEqual((int)MlTreasureDrop.RelariaBossWcid, map.GetProperty(PropertyInt.TreasureMapBossWcid));
        }

        [TestMethod]
        public void StampSite_RelariaOnADiggableNonBossCell_IsDemotedAndPlacedAtThePickupPoint()
        {
            // The pickup cell is catalogued (diggable) but not boss_ok, and the only boss_ok site is out of
            // range. The boss spawner refuses non-boss_ok sites, so the map is demoted rather than left
            // uncompletable, and the dig is the pickup point.
            var pickup = MlOutdoor();
            var pickupMap = pickup.GetMapCoords().Value;
            var map = MakeMap(relaria: true);

            Assert.IsTrue(MlTreasureDrop.StampSite(map, pickup, true,
                Store(Row(pickupMap + new Vector2(0.05f, 0f), false, PickupCx, PickupCy), Row(pickupMap + new Vector2(50f, 0f), true)), Cap));

            Assert.IsNull(map.GetProperty(PropertyInt.TreasureMapBossWcid), "the Relaria variant should have been dropped");
            Assert.AreEqual(pickupMap.X, Stamped(map).X, 1e-3f);
        }

        [TestMethod]
        public void StampSite_RelariaOnWater_TakesTheNearestBossSiteAndStaysRelaria()
        {
            var pickup = MlOutdoor();
            var pickupMap = pickup.GetMapCoords().Value;
            var map = MakeMap(relaria: true);

            Assert.IsTrue(MlTreasureDrop.StampSite(map, pickup, true,
                Store(Row(pickupMap + new Vector2(10f, 0f), false), Row(pickupMap + new Vector2(30f, 0f), true)), Cap));

            Assert.AreEqual((int)MlTreasureDrop.RelariaBossWcid, map.GetProperty(PropertyInt.TreasureMapBossWcid));
            Assert.AreEqual(pickupMap.X + 30f, Stamped(map).X, 1e-3f);
        }

        [TestMethod]
        public void EnsureSiteStamped_LeavesAnAlreadyStampedMapAlone()
        {
            var map = MakeMap();
            map.SetProperty(PropertyFloat.TreasureMapNorthSouth, 12.5);
            map.SetProperty(PropertyFloat.TreasureMapEastWest, -80.25);

            Assert.IsTrue(TreasureMapHandler.EnsureSiteStamped(map, MlOutdoor(), MlTreasureSiteStore.Empty, Cap));

            Assert.AreEqual(12.5, map.GetProperty(PropertyFloat.TreasureMapNorthSouth));
            Assert.AreEqual(-80.25, map.GetProperty(PropertyFloat.TreasureMapEastWest));
        }

        [TestMethod]
        public void EnsureSiteStamped_PlacesAnUnstampedMapFromTheReadersPositionByTheSameRule()
        {
            // The lazy first-use path: no pickup point was stored, so the reader's own position is the
            // reference, capped exactly like a drop - never the island-wide draw it used to be.
            var reader = MlOutdoor();
            var readerMap = reader.GetMapCoords().Value;
            var map = MakeMap();

            Assert.IsTrue(TreasureMapHandler.EnsureSiteStamped(map, reader,
                Store(Row(readerMap + new Vector2(60f, 0f), false), Row(readerMap + new Vector2(0.02f, 0.02f), false, PickupCx, PickupCy)), Cap));

            Assert.IsTrue(MlTreasureGeometry.DistanceMetres(readerMap, Stamped(map)) <= MlTreasureSiteStore.MaxSiteDistanceMetres);
        }

        [TestMethod]
        public void EnsureSiteStamped_AReaderOnWaterGetsTheNearestSite()
        {
            var reader = MlOutdoor();
            var readerMap = reader.GetMapCoords().Value;
            var map = MakeMap();

            Assert.IsTrue(TreasureMapHandler.EnsureSiteStamped(map, reader,
                Store(Row(readerMap + new Vector2(20f, 0f), false), Row(readerMap + new Vector2(0f, 25f), false)), Cap));

            Assert.AreEqual(readerMap.X + 20f, Stamped(map).X, 1e-3f);
        }

        [TestMethod]
        public void PickupCellIsDigSite_UsesTheCellUnderTheLocalCoordinates()
        {
            var store = Store(Row(new Vector2(0f, 0f), false, PickupCx, PickupCy));

            Assert.IsTrue(MlTreasureDrop.PickupCellIsDigSite(MlOutdoor(), store), "local (10, 10) is cell (0, 0)");

            var elsewhere = new Position(0x21B00001, 100f, 30f, 0f, 0f, 0f, 0f, 1f,
                Position.InstanceIDFromVars(realmId: 1, shortInstanceId: 0, isTemporaryRuleset: false));

            Assert.IsFalse(MlTreasureDrop.PickupCellIsDigSite(elsewhere, store), "local (100, 30) is cell (4, 1)");
            Assert.IsFalse(MlTreasureDrop.PickupCellIsDigSite(MlOutdoor(), MlTreasureSiteStore.Empty));
        }

        [TestMethod]
        public void IsOutdoorMaraeLassel_IsOutdoorsOnMaraeLasselRealm1Only()
        {
            Assert.IsTrue(MlTreasureDrop.IsOutdoorMaraeLassel(0x21B0, 1, 0x21B00001));
            Assert.IsTrue(MlTreasureDrop.IsOutdoorMaraeLassel(0x21B0, 1, 0x21B00040));
            Assert.IsFalse(MlTreasureDrop.IsOutdoorMaraeLassel(0x21B0, 1, 0x21B00100), "an indoor cell");
            Assert.IsFalse(MlTreasureDrop.IsOutdoorMaraeLassel(0x21B0, 0, 0x21B00001), "retail Marae Lassel, realm 0");
            Assert.IsFalse(MlTreasureDrop.IsOutdoorMaraeLassel(0x7D64, 1, 0x7D640001), "realm 1 but not Marae Lassel");
        }
    }
}

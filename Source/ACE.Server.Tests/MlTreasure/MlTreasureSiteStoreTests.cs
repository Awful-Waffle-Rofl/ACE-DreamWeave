using System.Collections.Generic;
using System.Numerics;

using ACE.Server.Managers;
using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// MlTreasureSiteStore.ParseLines over fixture data - no file I/O, no PropertyManager (PropertyManager
    /// reads throw in this test harness). Covers the parse itself and every degrade-to-empty path: a
    /// malformed row, an empty file, and a header-only file all fall back to
    /// <see cref="MlTreasureSiteStore.Empty"/> rather than throwing.
    /// </summary>
    [TestClass]
    public class MlTreasureSiteStoreTests
    {
        private const string Header = "landblock\tcell_x\tcell_y\tlocal_x\tlocal_y\tz\tmap_ns\tmap_ew\tboss_ok";

        [TestMethod]
        public void ParseLines_ValidRows_ParsesAllOfThem()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t-10.5\t20.25\t1",
                "0x0FAD\t3\t4\t70\t80\t12\t-9.5\t21.25\t0"
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            Assert.AreEqual(2, store.Count);
        }

        [TestMethod]
        public void ParseLines_BossOkFlag_SelectsOnlyBossOkRows()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t1",   // boss_ok
                "0x0FAD\t3\t4\t70\t80\t12\t5\t5\t0"    // not boss_ok
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            // Every bossOk roll must come back as the boss_ok=1 row, never the other.
            for (var i = 0; i < 25; i++)
            {
                var rolled = store.Roll(bossOk: true);
                Assert.IsNotNull(rolled);
                Assert.IsTrue(rolled.Value.BossOk);
                Assert.AreEqual(0f, rolled.Value.Ns, 1e-6f);
                Assert.AreEqual(0f, rolled.Value.Ew, 1e-6f);
            }
        }

        [TestMethod]
        public void ParseLines_MalformedRow_IsSkippedNotThrown()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t-10.5\t20.25\t1",  // valid
                "garbage row with too few columns",           // malformed: wrong column count
                "0x0FAD\t3\t4\t70\t80\t12\tNaN_not_a_number\t21.25\t0" // malformed: bad map_ns
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            Assert.AreEqual(1, store.Count);
        }

        [TestMethod]
        public void ParseLines_EmptyInput_DegradesToEmpty()
        {
            var store = MlTreasureSiteStore.ParseLines(new List<string>());

            Assert.AreEqual(0, store.Count);
            Assert.IsNull(store.Roll(bossOk: false));
            Assert.IsNull(store.Roll(bossOk: true));
            Assert.IsNull(store.Nearest(Vector2.Zero));
        }

        [TestMethod]
        public void ParseLines_HeaderOnly_DegradesToEmpty()
        {
            var store = MlTreasureSiteStore.ParseLines(new List<string> { Header });

            Assert.AreEqual(0, store.Count);
            Assert.IsNull(store.Roll(bossOk: false));
        }

        [TestMethod]
        public void ParseLines_NullInput_DegradesToEmpty()
        {
            var store = MlTreasureSiteStore.ParseLines(null);

            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public void ParseLines_EveryRowMalformed_DegradesToEmpty()
        {
            var lines = new List<string>
            {
                Header,
                "only\tfour\tcolumns\there",
                "still\tnot\tenough"
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            Assert.AreEqual(0, store.Count);
            Assert.IsNull(store.Roll(bossOk: false));
        }

        [TestMethod]
        public void Nearest_ReturnsClosestSite()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t1",     // at origin
                "0x0FAD\t3\t4\t70\t80\t12\t100\t100\t0", // far away
                "0x0FAE\t5\t6\t70\t80\t12\t1\t1\t0"      // close to (2,2)
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            var nearest = store.Nearest(new Vector2(2f, 2f));

            Assert.IsNotNull(nearest);
            Assert.AreEqual(1f, nearest.Value.Ns, 1e-6f);
            Assert.AreEqual(1f, nearest.Value.Ew, 1e-6f);
        }

        [TestMethod]
        public void Load_MissingFile_DegradesToEmpty()
        {
            var store = MlTreasureSiteStore.Load(@"C:\this\path\definitely\does\not\exist\dig-sites.tsv");

            Assert.AreEqual(0, store.Count);
            Assert.IsNull(store.Roll(bossOk: false));
        }

        // ---- RollNear: the round 14 "a short run from where the map dropped" bias ------------------------
        //
        // Rows below are written (map_ns, map_ew) = (y, x), so a site at "0 5" sits at Vector2(5, 0) - the
        // X=EastWest, Y=NorthSouth order Site.MapCoords and PositionExtensions.GetMapCoords both use.

        /// <summary>A catalogue with three sites clustered near the origin and one 100 units away.</summary>
        private static MlTreasureSiteStore ClusteredStore() => MlTreasureSiteStore.ParseLines(new List<string>
        {
            Header,
            "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t1",       // (ew 0, ns 0)
            "0x0FAD\t1\t2\t50\t60\t10\t1\t0\t1",       // (ew 0, ns 1)
            "0x0FAE\t1\t2\t50\t60\t10\t0\t1\t1",       // (ew 1, ns 0)
            "0x0FAF\t1\t2\t50\t60\t10\t100\t100\t1"    // the outlier
        });

        [TestMethod]
        public void RollNear_NeverReturnsASiteOutsideTheRadiusWhenOneIsInside()
        {
            var store = ClusteredStore();

            for (var i = 0; i < 200; i++)
            {
                var rolled = store.RollNear(bossOk: false, new Vector2(0f, 0f), radiusMapUnits: 2f);

                Assert.IsNotNull(rolled);
                Assert.IsTrue(rolled.Value.Ns <= 1f && rolled.Value.Ew <= 1f,
                    $"the 100,100 outlier is outside a radius of 2 but was rolled ({rolled.Value.Ew}, {rolled.Value.Ns})");
            }
        }

        [TestMethod]
        public void RollNear_DrawsFromEveryQualifyingSite_NotJustTheNearest()
        {
            // The whole point of RollNear over Nearest: two maps dropped at the same farming spot must not
            // both name the same hole. The reference point is exactly ON one site, so a nearest-snap
            // implementation would return that one every single time.
            var store = ClusteredStore();

            var seen = new HashSet<string>();

            for (var i = 0; i < 400; i++)
            {
                var rolled = store.RollNear(bossOk: false, new Vector2(0f, 0f), radiusMapUnits: 2f);

                Assert.IsNotNull(rolled);
                seen.Add($"{rolled.Value.Ew},{rolled.Value.Ns}");
            }

            Assert.AreEqual(3, seen.Count, "all three sites inside the radius should be reachable");
        }

        [TestMethod]
        public void RollNear_PicksTheIndexTheDrawReturns()
        {
            // The seam, pinned: candidates are enumerated in catalogue order, so index 0 is the first
            // qualifying row and index 2 the third.
            var store = ClusteredStore();

            var first = store.RollNear(bossOk: false, new Vector2(0f, 0f), 2f, _ => 0);
            var third = store.RollNear(bossOk: false, new Vector2(0f, 0f), 2f, _ => 2);

            Assert.IsNotNull(first);
            Assert.AreEqual(0f, first.Value.Ns, 1e-6f);
            Assert.AreEqual(0f, first.Value.Ew, 1e-6f);

            Assert.IsNotNull(third);
            Assert.AreEqual(0f, third.Value.Ns, 1e-6f);
            Assert.AreEqual(1f, third.Value.Ew, 1e-6f);
        }

        [TestMethod]
        public void RollNear_OutOfRangeDrawIsClampedRatherThanReturningNull()
        {
            var store = ClusteredStore();

            Assert.IsNotNull(store.RollNear(bossOk: false, new Vector2(0f, 0f), 2f, _ => -5));
            Assert.IsNotNull(store.RollNear(bossOk: false, new Vector2(0f, 0f), 2f, _ => 99));
        }

        // ---- round 15: the radius is a hard cap - no widening, no island-wide fallback -------------------

        [TestMethod]
        public void RollNear_ReturnsNullWhenNothingIsInsideTheRadius()
        {
            // Before round 15 this widened three times and then fell back to an island-wide draw. The pick
            // counter proves no radius step found a candidate: RollNear itself gives up, and the
            // pickup-point fallback is PlaceNear's job, not a flat roll's.
            var store = ClusteredStore();
            var pickCalls = 0;

            var rolled = store.RollNear(bossOk: false, new Vector2(50f, 50f), radiusMapUnits: 1f,
                pick: count => { pickCalls++; return 0; });

            Assert.IsNull(rolled, "nothing within the radius must never be answered by a site outside it");
            Assert.AreEqual(0, pickCalls);
        }

        [TestMethod]
        public void RollNear_NoLongerWidensToASiteJustOutsideTheRadius()
        {
            // The round 14 widening case, now a refusal: from (96, 96) the outlier is ~5.66 units away, which
            // the old 1 -> 2 -> 4 -> 8 widening reached. A hard cap of 1 must not.
            var store = ClusteredStore();

            Assert.IsNull(store.RollNear(bossOk: false, new Vector2(96f, 96f), radiusMapUnits: 1f));

            // ...and the same outlier IS found once the cap itself encloses it.
            var enclosed = store.RollNear(bossOk: false, new Vector2(96f, 96f), radiusMapUnits: 6f);
            Assert.IsNotNull(enclosed);
            Assert.AreEqual(100f, enclosed.Value.Ns, 1e-6f);
        }

        [TestMethod]
        public void RollNear_KeepsTheBossOkFilterEvenWhenTheNearestSiteIsNotBossOk()
        {
            // bossOk is applied BEFORE the distance test: a Relaria map must never be snapped to a
            // non-boss_ok cell just because that cell happens to be the closest one.
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t0",       // right next door, NOT boss_ok
                "0x0FAD\t1\t2\t50\t60\t10\t1\t1\t1",       // inside the radius, boss_ok
                "0x0FAE\t1\t2\t50\t60\t10\t100\t100\t1"    // far away, boss_ok, outside the cap
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            for (var i = 0; i < 100; i++)
            {
                var rolled = store.RollNear(bossOk: true, new Vector2(0f, 0f), radiusMapUnits: 2f);

                Assert.IsNotNull(rolled);
                Assert.IsTrue(rolled.Value.BossOk);
                Assert.AreEqual(1f, rolled.Value.Ns, 1e-6f);
            }
        }

        [TestMethod]
        public void RollNear_NonPositiveOrNonFiniteInputsQualifyNothing()
        {
            // Nothing CAN be inside a zero, negative or NaN radius, so these return null and PlaceNear puts
            // the site at the pickup point - never the island-wide draw they used to degrade to.
            var store = ClusteredStore();

            Assert.IsNull(store.RollNear(bossOk: false, new Vector2(0f, 0f), 0f));
            Assert.IsNull(store.RollNear(bossOk: false, new Vector2(0f, 0f), -1f));
            Assert.IsNull(store.RollNear(bossOk: false, new Vector2(0f, 0f), float.NaN));
            Assert.IsNull(store.RollNear(bossOk: false, new Vector2(float.NaN, 0f), 2f));
        }

        [TestMethod]
        public void RollNear_EmptyStoreIsStillNull()
        {
            Assert.IsNull(MlTreasureSiteStore.Empty.RollNear(bossOk: false, Vector2.Zero, 2f));
            Assert.IsNull(MlTreasureSiteStore.Empty.RollNear(bossOk: true, Vector2.Zero, 2f));
        }

        // ---- PlaceNear: the shared round 15 placement rule ---------------------------------------------

        [TestMethod]
        public void PlaceNear_UsesACatalogueSiteWhenOneIsInRange()
        {
            var placed = ClusteredStore().PlaceNear(bossOk: false, new Vector2(0f, 0f), 2f, pickupIsDigSite: false, _ => 2);

            Assert.IsNotNull(placed);
            Assert.IsFalse(placed.Value.AtPickupPoint);
            Assert.IsFalse(placed.Value.BeyondRadius);
            Assert.IsFalse(placed.Value.BossVariantDropped);
            Assert.AreEqual(1f, placed.Value.Ew, 1e-6f);
            Assert.AreEqual(0f, placed.Value.Ns, 1e-6f);
        }

        [TestMethod]
        public void PlaceNear_FallsBackToADiggablePickupPointItself()
        {
            // Nothing within 3 units of (50, 47), and the pickup cell passes the catalogue's terrain test, so
            // the site IS the pickup point (owner ruling), with the axes the right way round.
            var pickup = new Vector2(50f, 47f); // X = EastWest 50, Y = NorthSouth 47

            var placed = ClusteredStore().PlaceNear(bossOk: false, pickup, 3f, pickupIsDigSite: true);

            Assert.IsNotNull(placed);
            Assert.IsTrue(placed.Value.AtPickupPoint);
            Assert.IsFalse(placed.Value.BeyondRadius);
            Assert.AreEqual(50f, placed.Value.Ew, 1e-6f);
            Assert.AreEqual(47f, placed.Value.Ns, 1e-6f);
            Assert.AreEqual(pickup, placed.Value.MapCoords);
        }

        [TestMethod]
        public void PlaceNear_AnUndiggablePickupTakesTheNearestSiteWhateverItsDistance()
        {
            // (60, 60) is ~56.6 units from the 100,100 outlier and ~84 from the origin cluster: nothing within
            // 3 units, and the pickup cell is water/steep (pickupIsDigSite false), so the NEAREST site wins.
            var placed = ClusteredStore().PlaceNear(bossOk: false, new Vector2(60f, 60f), 3f, pickupIsDigSite: false);

            Assert.IsNotNull(placed);
            Assert.IsFalse(placed.Value.AtPickupPoint);
            Assert.IsTrue(placed.Value.BeyondRadius);
            Assert.AreEqual(100f, placed.Value.Ew, 1e-6f);
            Assert.AreEqual(100f, placed.Value.Ns, 1e-6f);
        }

        [TestMethod]
        public void PlaceNear_AnUndiggablePickupStillPrefersASiteInsideTheCap()
        {
            // The cap applies while a site within it exists, whatever the pickup cell is.
            for (var i = 0; i < 50; i++)
            {
                var placed = ClusteredStore().PlaceNear(bossOk: false, new Vector2(0.5f, 0.5f), 2f, pickupIsDigSite: false);

                Assert.IsNotNull(placed);
                Assert.IsFalse(placed.Value.BeyondRadius);
                Assert.IsTrue(Vector2.Distance(new Vector2(0.5f, 0.5f), placed.Value.MapCoords) <= 2f);
            }
        }

        [TestMethod]
        public void PlaceNear_ABossMapWithAnUndiggablePickupTakesTheNearestBossSiteAndKeepsItsVariant()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t10\t10\t0",     // nearer, NOT boss_ok
                "0x0FAD\t1\t2\t50\t60\t10\t30\t30\t1"      // farther, boss_ok
            };

            var placed = MlTreasureSiteStore.ParseLines(lines).PlaceNear(bossOk: true, new Vector2(0f, 0f), 3f, pickupIsDigSite: false);

            Assert.IsNotNull(placed);
            Assert.IsTrue(placed.Value.BeyondRadius);
            Assert.IsFalse(placed.Value.BossVariantDropped);
            Assert.AreEqual(30f, placed.Value.Ns, 1e-6f);
        }

        [TestMethod]
        public void PlaceNear_EmptyCatalogue_PlacesOnlyAtADiggablePickup()
        {
            Assert.IsTrue(MlTreasureSiteStore.Empty.PlaceNear(bossOk: false, new Vector2(-80f, 40f), 3f, pickupIsDigSite: true).Value.AtPickupPoint);
            Assert.IsNull(MlTreasureSiteStore.Empty.PlaceNear(bossOk: false, new Vector2(-80f, 40f), 3f, pickupIsDigSite: false));
        }

        [TestMethod]
        public void PlaceNear_WithADiggablePickupIsNeverFartherThanTheCap()
        {
            // The invariant the owner ruling states, swept over a grid of pickup points around a catalogue
            // with sites both inside and outside the cap.
            var store = ClusteredStore();
            const float cap = 3f;

            for (var x = -10; x <= 110; x += 5)
            {
                for (var y = -10; y <= 110; y += 5)
                {
                    var pickup = new Vector2(x, y);
                    var placed = store.PlaceNear(bossOk: false, pickup, cap, pickupIsDigSite: true);

                    Assert.IsNotNull(placed);
                    Assert.IsTrue(Vector2.Distance(pickup, placed.Value.MapCoords) <= cap + 1e-4f,
                        $"pickup ({x}, {y}) placed {Vector2.Distance(pickup, placed.Value.MapCoords)} units away");
                }
            }
        }

        [TestMethod]
        public void PlaceNear_ABossMapWithNoBossSiteInRangeFallsBackAndIsFlaggedForDemotion()
        {
            // A non-boss site sits right at the (diggable) pickup point, but a Relaria map may only use boss_ok
            // rows, and the only boss_ok row is out of range. The pickup point cannot be a boss_ok row, so the
            // caller must strip the boss variant.
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t0",
                "0x0FAD\t1\t2\t50\t60\t10\t100\t100\t1"
            };

            var placed = MlTreasureSiteStore.ParseLines(lines).PlaceNear(bossOk: true, new Vector2(0f, 0f), 3f, pickupIsDigSite: true);

            Assert.IsNotNull(placed);
            Assert.IsTrue(placed.Value.AtPickupPoint);
            Assert.IsTrue(placed.Value.BossVariantDropped);
        }

        [TestMethod]
        public void PlaceNear_NonFinitePickupPointPlacesNothing()
        {
            Assert.IsNull(ClusteredStore().PlaceNear(bossOk: false, new Vector2(float.NaN, 0f), 3f, pickupIsDigSite: true));
        }

        // ---- IsCatalogueCell: the catalogue as the terrain test -------------------------------------------

        [TestMethod]
        public void IsCatalogueCell_AnswersForExactlyTheCataloguedCells()
        {
            var store = MlTreasureSiteStore.ParseLines(new List<string>
            {
                Header,
                "0x21B0\t3\t5\t84\t132\t10\t1\t1\t1",
                "0x21B1\t0\t7\t12\t180\t10\t2\t2\t0"
            });

            Assert.IsTrue(store.IsCatalogueCell(0x21B0, 3, 5));
            Assert.IsTrue(store.IsCatalogueCell(0x21B1, 0, 7), "a non-boss row still vouches for its terrain");
            Assert.IsFalse(store.IsCatalogueCell(0x21B0, 5, 3), "axes are not interchangeable");
            Assert.IsFalse(store.IsCatalogueCell(0x21B1, 3, 5), "another landblock's cell");
            Assert.IsFalse(store.IsCatalogueCell(0x21B0, 8, 0), "out of range");
            Assert.IsFalse(MlTreasureSiteStore.Empty.IsCatalogueCell(0x21B0, 3, 5));
        }

        [TestMethod]
        public void IsCatalogueCell_AMalformedCellColumnStillLoadsTheSiteButVouchesForNothing()
        {
            var store = MlTreasureSiteStore.ParseLines(new List<string>
            {
                Header,
                "notahex\tx\t5\t84\t132\t10\t1\t1\t1"
            });

            Assert.AreEqual(1, store.Count);
            Assert.IsFalse(store.IsCatalogueCell(0x21B0, 3, 5));
        }
        [TestMethod]
        public void The_cap_constant_is_three_map_units()
        {
            Assert.AreEqual(720f, MlTreasureSiteStore.MaxSiteDistanceMetres, 1e-6f);
            Assert.AreEqual(3f * MlTreasureGeometry.MetresPerMapUnit, MlTreasureSiteStore.MaxSiteDistanceMetres, 1e-6f);
        }

        [TestMethod]
        public void The_dig_site_radius_ships_at_its_designed_value_not_zero()
        {
            // Round 15 owner ruling: 720 m (three map units), and MlTreasureDrop.SiteRadiusMapUnits passes
            // MaxSiteDistanceMetres as its unseeded fallback, so the two are pinned together.
            //
            // Reads DefaultPropertyManager's dictionary directly - the CODE default - rather than
            // PropertyManager.GetDouble, which throws for an uncached key with no shard DB. Nothing is
            // seeded, so this class stays order-independent (see MlDigsiteRulesTests' class remarks).
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("ml_treasure_site_radius_metres"),
                "ml_treasure_site_radius_metres is not registered in DefaultDoubleProperties");

            Assert.AreEqual(720.0, DefaultPropertyManager.DefaultDoubleProperties["ml_treasure_site_radius_metres"].Item, 1e-9,
                "the dig site radius default moved");

            Assert.AreEqual((double)MlTreasureSiteStore.MaxSiteDistanceMetres,
                DefaultPropertyManager.DefaultDoubleProperties["ml_treasure_site_radius_metres"].Item, 1e-9);
        }
    }
}
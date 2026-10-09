using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.MlDigsite;
using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// ML treasure map / digsite ZONE ISOLATION (owner-approved design): a map found in one of Marae
    /// Lassel's three level zones must lead to a dig site in the SAME zone, and the digsite encounter that
    /// site opens must draw creatures from that zone only. Covers the pure pieces: MlTreasureZones.FromLevel,
    /// the catalogue's optional zone column, zone-filtered RollNear/Nearest/PlaceNear (including the border
    /// case a zone-blind lookup gets wrong), Unknown-is-unfiltered, and the roster's zone-filtered Pick.
    /// </summary>
    [TestClass]
    public class MlTreasureZoneTests
    {
        private const string Header = "landblock\tcell_x\tcell_y\tlocal_x\tlocal_y\tz\tmap_ns\tmap_ew\tboss_ok\tzone";

        // ---- MlTreasureZones.FromLevel: South <=210, North <=230, else Plateau -------------------------

        [DataTestMethod]
        [DataRow(0, MlTreasureZone.South)]
        [DataRow(185, MlTreasureZone.South)]
        [DataRow(200, MlTreasureZone.South)]
        [DataRow(205, MlTreasureZone.South)]
        [DataRow(210, MlTreasureZone.South)]
        [DataRow(211, MlTreasureZone.North)]
        [DataRow(215, MlTreasureZone.North)]
        [DataRow(225, MlTreasureZone.North)]
        [DataRow(230, MlTreasureZone.North)]
        [DataRow(231, MlTreasureZone.Plateau)]
        [DataRow(240, MlTreasureZone.Plateau)]
        [DataRow(265, MlTreasureZone.Plateau)]
        [DataRow(275, MlTreasureZone.Plateau)]
        [DataRow(999, MlTreasureZone.Plateau)]
        public void FromLevel_BandsMatchBestiaryRungs(int level, MlTreasureZone expected)
        {
            Assert.AreEqual(expected, MlTreasureZones.FromLevel(level));
        }

        [TestMethod]
        public void Parse_RecognisesAllThreeAndFallsBackToUnknown()
        {
            Assert.AreEqual(MlTreasureZone.South, MlTreasureZones.Parse("south"));
            Assert.AreEqual(MlTreasureZone.North, MlTreasureZones.Parse("North"));
            Assert.AreEqual(MlTreasureZone.Plateau, MlTreasureZones.Parse("PLATEAU"));
            Assert.AreEqual(MlTreasureZone.Unknown, MlTreasureZones.Parse(""));
            Assert.AreEqual(MlTreasureZone.Unknown, MlTreasureZones.Parse(null));
            Assert.AreEqual(MlTreasureZone.Unknown, MlTreasureZones.Parse("garbage"));
        }

        // ---- ParseLines: the optional 10th (zone) column -------------------------------------------------

        [TestMethod]
        public void ParseLines_NineColumns_EveryRowIsUnknownZone()
        {
            var lines = new List<string>
            {
                "landblock\tcell_x\tcell_y\tlocal_x\tlocal_y\tz\tmap_ns\tmap_ew\tboss_ok",
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t1",
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            Assert.AreEqual(1, store.Count);
            Assert.AreEqual(MlTreasureZone.Unknown, store.Roll(bossOk: false).Value.Zone);
            Assert.AreEqual(MlTreasureZone.Unknown, store.ZoneOf(0x0FAC));
        }

        [TestMethod]
        public void ParseLines_TenColumns_ParsesTheZoneAndBuildsTheLandblockMap()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t1\tsouth",
                "0x0FAD\t3\t4\t70\t80\t12\t5\t5\t0\tnorth",
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            Assert.AreEqual(MlTreasureZone.South, store.ZoneOf(0x0FAC));
            Assert.AreEqual(MlTreasureZone.North, store.ZoneOf(0x0FAD));
            Assert.AreEqual(MlTreasureZone.Unknown, store.ZoneOf(0x9999), "an uncatalogued landblock is Unknown");
        }

        [TestMethod]
        public void ParseLines_UnrecognisedZoneText_IsUnknownNotSkipped()
        {
            var lines = new List<string>
            {
                Header,
                "0x0FAC\t1\t2\t50\t60\t10\t0\t0\t1\tgarbage",
            };

            var store = MlTreasureSiteStore.ParseLines(lines);

            Assert.AreEqual(1, store.Count);
            Assert.AreEqual(MlTreasureZone.Unknown, store.Roll(bossOk: false).Value.Zone);
        }

        // ---- zone-filtered RollNear / Nearest / PlaceNear, incl. the border case ------------------------

        /// <summary>Two sites: a SOUTH one much closer to the origin, a NORTH one farther away.</summary>
        private static MlTreasureSiteStore BorderStore()
        {
            var lines = new List<string>
            {
                Header,
                "0x1000\t0\t0\t0\t0\t0\t1\t1\t0\tsouth",   // close to origin: (Ew=1, Ns=1)
                "0x2000\t0\t0\t0\t0\t0\t10\t10\t0\tnorth", // far from origin: (Ew=10, Ns=10)
            };

            return MlTreasureSiteStore.ParseLines(lines);
        }

        [TestMethod]
        public void Nearest_ZoneFiltered_SkipsTheCloserWrongZoneSite()
        {
            var store = BorderStore();

            // Zone-blind: the CLOSER (South) site wins.
            var blind = store.Nearest(Vector2.Zero, bossOk: false);
            Assert.AreEqual(MlTreasureZone.South, blind.Value.Zone);

            // Zone-filtered to North: must skip the closer South site and return the farther North one -
            // this is the discriminating case. If the zone filter in Nearest were ever removed or bypassed,
            // this assertion fails (proven below by disabling it).
            var filtered = store.Nearest(Vector2.Zero, bossOk: false, MlTreasureZone.North);
            Assert.IsNotNull(filtered);
            Assert.AreEqual(MlTreasureZone.North, filtered.Value.Zone);
            Assert.AreEqual(10f, filtered.Value.Ew, 1e-6f);
        }

        [TestMethod]
        public void RollNear_ZoneFiltered_NeverReturnsAnotherZonesSite()
        {
            var store = BorderStore();

            // Radius wide enough to cover both sites; filtered to North it must ALWAYS come back North,
            // never the South site even though it is closer and inside the same radius.
            for (var i = 0; i < 25; i++)
            {
                var rolled = store.RollNear(bossOk: false, Vector2.Zero, radiusMapUnits: 20f, pick: null, zone: MlTreasureZone.North);
                Assert.IsNotNull(rolled);
                Assert.AreEqual(MlTreasureZone.North, rolled.Value.Zone);
            }
        }

        [TestMethod]
        public void RollNear_UnknownZone_IsUnfiltered_MatchesPreZoneBehaviour()
        {
            var store = BorderStore();

            var rolled = store.RollNear(bossOk: false, Vector2.Zero, radiusMapUnits: 20f, pick: _ => 0, zone: MlTreasureZone.Unknown);
            Assert.IsNotNull(rolled);
            // pick index 0 over the unfiltered candidate scan resolves to the first qualifying site in list
            // order (South, since it was parsed first) - same as before zone isolation existed.
            Assert.AreEqual(MlTreasureZone.South, rolled.Value.Zone);
        }

        [TestMethod]
        public void PlaceNear_KnownZone_Step1_OnlyDrawsInZoneWithinRadius()
        {
            var store = BorderStore();

            // Both sites are within a 20-unit radius of the origin; filtered to South, only the South site
            // may come back, even though the North site is a valid RollNear candidate in general.
            for (var i = 0; i < 10; i++)
            {
                var placed = store.PlaceNear(bossOk: false, Vector2.Zero, radiusMapUnits: 20f, pickupIsDigSite: false,
                    pick: null, zone: MlTreasureZone.South);

                Assert.IsNotNull(placed);
                Assert.AreEqual(1f, placed.Value.Ew, 1e-6f);
            }
        }

        [TestMethod]
        public void PlaceNear_KnownZone_Step3_FallsBackToNearestInZoneSite()
        {
            var store = BorderStore();

            // Nothing within a 0.5-unit radius of the origin, and the pickup is not diggable: step 3 must
            // fall back to the nearest IN-ZONE (North) site, never the closer South one.
            var placed = store.PlaceNear(bossOk: false, Vector2.Zero, radiusMapUnits: 0.5f, pickupIsDigSite: false,
                pick: null, zone: MlTreasureZone.North);

            Assert.IsNotNull(placed);
            Assert.IsTrue(placed.Value.BeyondRadius);
            Assert.AreEqual(MlTreasureZone.North, store.ZoneAt(placed.Value.MapCoords));
        }

        [TestMethod]
        public void PlaceNear_KnownZone_NeverReturnsNullJustBecauseAnotherZoneHadASite()
        {
            // A zone with NO site at all (Plateau, in this two-row fixture) correctly returns null rather
            // than crossing into South or North.
            var store = BorderStore();

            var placed = store.PlaceNear(bossOk: false, Vector2.Zero, radiusMapUnits: 50f, pickupIsDigSite: false,
                pick: null, zone: MlTreasureZone.Plateau);

            Assert.IsNull(placed);
        }

        [TestMethod]
        public void ZoneAt_ReturnsTheNearestSitesZone()
        {
            var store = BorderStore();

            Assert.AreEqual(MlTreasureZone.South, store.ZoneAt(new Vector2(1.1f, 1.1f)));
            Assert.AreEqual(MlTreasureZone.North, store.ZoneAt(new Vector2(9.9f, 9.9f)));
        }

        // ---- roster Pick: zone filtering + empty-band fallback -------------------------------------------

        [TestMethod]
        public void RosterPick_FiltersToTheZonesLevelBand()
        {
            var rng = new Random(1);

            for (var i = 0; i < 50; i++)
            {
                var entry = MlDigsiteRoster.Pick(MlDigsiteRole.Wave, MlTreasureZone.South, rng);
                Assert.IsNotNull(entry);
                Assert.AreEqual(MlTreasureZone.South, MlTreasureZones.FromLevel(entry.Value.Level));
            }
        }

        [TestMethod]
        public void RosterPick_UnknownZone_IsUnfiltered()
        {
            var rng = new Random(2);
            var allLevels = MlDigsiteRoster.Entries(MlDigsiteRole.Wave).Select(e => e.Level).Distinct().ToHashSet();

            var seenLevels = new HashSet<int>();
            for (var i = 0; i < 200; i++)
                seenLevels.Add(MlDigsiteRoster.Pick(MlDigsiteRole.Wave, MlTreasureZone.Unknown, rng).Value.Level);

            // Unfiltered draws should see entries from more than one zone's level band over enough draws.
            Assert.IsTrue(seenLevels.Count > 1);
            Assert.IsTrue(seenLevels.IsSubsetOf(allLevels));
        }

        [TestMethod]
        public void RosterPick_AddRole_IsExemptFromTheZoneFilterEntirely()
        {
            // MlDigsiteRoster.AddEntries is deliberately ONE South-level entry, reused across every zone by
            // owner ruling (quoted on AddEntries): Add is explicitly EXEMPT from the zone filter in Pick, not
            // routed through the empty-band fallback. It must return wcid 1002844 for every zone, including
            // South, where a filter would have matched it anyway - the exemption is unconditional.
            var rng = new Random(3);

            foreach (var zone in new[] { MlTreasureZone.Unknown, MlTreasureZone.South, MlTreasureZone.North, MlTreasureZone.Plateau })
            {
                var entry = MlDigsiteRoster.Pick(MlDigsiteRole.Add, zone, rng);

                Assert.IsNotNull(entry);
                Assert.AreEqual(1002844u, entry.Value.Wcid);
            }
        }

        [TestMethod]
        public void RosterPick_SelectPool_EmptyBandFallback_SyntheticCase()
        {
            // The empty-band fallback in MlDigsiteRoster.SelectPool is unreached by any shipped role's real
            // table today (every role but Add has an entry in every zone - RosterPick_FiltersToTheZonesLevelBand
            // and its siblings cover that). This exercises the fallback branch directly via the pure seam,
            // with a synthetic entries list that genuinely has no South-band entry.
            var entries = new List<MlDigsiteRosterEntry>
            {
                new MlDigsiteRosterEntry(9999001, 215, "Synthetic North-only entry A"),
                new MlDigsiteRosterEntry(9999002, 220, "Synthetic North-only entry B"),
            };

            var pool = MlDigsiteRoster.SelectPool(entries, MlDigsiteRole.Wave, MlTreasureZone.South);

            // No South-band entry exists, so the fallback returns the FULL (unfiltered) list rather than an
            // empty one or null.
            Assert.AreEqual(2, pool.Count);
            CollectionAssert.AreEquivalent(entries, pool.ToList());

            // A zone that DOES have a qualifying entry still filters normally through the same seam.
            var filtered = MlDigsiteRoster.SelectPool(entries, MlDigsiteRole.Wave, MlTreasureZone.North);
            Assert.AreEqual(2, filtered.Count);
        }
    }
}

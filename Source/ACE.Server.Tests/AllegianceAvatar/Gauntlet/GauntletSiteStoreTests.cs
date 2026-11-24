using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Gauntlet sites store's lint, driven through GauntletTuning (no PropertyManager read). The fixture site sits on
    /// landblock 0x5456 with its centre at local (100, 96); the footprint is 0x5456, 0x5556 (east) and 0x5457 (north).
    /// </summary>
    [TestClass]
    public class GauntletSiteStoreTests
    {
        private const ushort Primary = 0x5456;
        private const ushort East = 0x5556;
        private const ushort North = 0x5457;
        private const ushort OffBlock = 0x6060;

        private static uint Cell(ushort landblock) => ((uint)landblock << 16) | 0x0001;

        private static DungeonSpawnPointDef At(ushort landblock, float x, float y, string kind = "outdoor", bool curated = true)
            => new DungeonSpawnPointDef { Cell = Cell(landblock), X = x, Y = y, Z = 50f, Kind = kind, Curated = curated };

        /// <summary>The Thread catalog row: a 200 m perimeter circle around the centre and four portal points 30-50 m out.</summary>
        private static DungeonEntryDef Row(string id = "ow_test", params DungeonSpawnPointDef[] points) => new DungeonEntryDef
        {
            Id = id,
            Kind = DungeonEntryDef.KindOutdoor,
            Landblock = Primary,
            ExitPortalWcid = 1003604,
            Points = points.Length > 0 ? points.ToList() : new List<DungeonSpawnPointDef>
            {
                At(Primary, 130, 96), At(Primary, 100, 136), At(Primary, 60, 96), At(Primary, 100, 46),
            },
            Outdoor = new OutdoorSiteDef
            {
                Primary = Primary,
                Footprint = new List<ushort> { Primary, East, North },
                Circle = new OutdoorCircleDef { LandblockHex = "0x5456", Landblock = Primary, X = 100, Y = 96, Radius = 200 },
            },
        };

        /// <summary>A valid site: pen 192 m east (on 0x5556), past 60 + 100; one entry 10 m from the centre.</summary>
        private static GauntletSiteDef Site(string id = "ow_test") => new GauntletSiteDef
        {
            Id = id,
            Centre = At(Primary, 100, 96),
            Radius = 60,
            Pen = At(East, 100, 96),
            Entries = new List<DungeonSpawnPointDef> { At(Primary, 110, 96) },
        };

        private static string Lint(GauntletSiteDef site, DungeonEntryDef row = null, GauntletTuning t = null)
            => GauntletSiteStore.Lint(site, row ?? Row(), t ?? GauntletTuning.Defaults);

        private static void AssertRefused(string refusal, string tag)
        {
            Assert.IsNotNull(refusal, $"expected a '{tag}' refusal");
            StringAssert.StartsWith(refusal, tag);
        }

        [TestMethod]
        public void ValidSite_Passes() => Assert.IsNull(Lint(Site()));

        [TestMethod]
        public void PenTooClose_Refused()
        {
            var site = Site();
            site.Pen = At(Primary, 100 + 150, 96);            // 150 m: inside 60 + 100
            AssertRefused(Lint(site), GauntletSiteStore.PenTooClose);

            site.Pen = At(Primary, 100 + 160, 96);            // exactly 60 + 100: allowed
            Assert.IsNull(Lint(site), "the boundary is allowed");

            site.Pen = At(Primary, 100 + 140, 96);
            Assert.IsNull(Lint(site, t: GauntletTuning.Defaults with { PenMinDistance = 50 }), "the tunable is what is read: 140 = 60 + 80 passes the snap floor too");

            // A lower pen_min_distance never lets the pen into the death hold's snap-back zone (60 + 75 m).
            site.Pen = At(Primary, 100 + 120, 96);            // 120 m: 60 + 60, past 60 + 50 but inside 60 + 76
            AssertRefused(Lint(site, t: GauntletTuning.Defaults with { PenMinDistance = 50 }), GauntletSiteStore.PenTooClose);
            site.Pen = At(Primary, 100 + 136, 96);            // 60 + 76: the snap floor's boundary
            Assert.IsNull(Lint(site, t: GauntletTuning.Defaults with { PenMinDistance = 50 }), "the snap floor's boundary is allowed");

            var wide = Site();
            wide.Radius = 100;                                // 192 m pen now inside 100 + 100
            AssertRefused(Lint(wide), GauntletSiteStore.PenTooClose);
        }

        [TestMethod]
        public void CentreOutsideCircle_Refused()
        {
            var row = Row();
            row.Outdoor.Circle.Radius = 20;
            row.Outdoor.Circle.X = 160;                       // the perimeter circle no longer contains (100, 96)

            AssertRefused(Lint(Site(), row), GauntletSiteStore.CentreOutsideCircle);

            // A 15 m fight circle, so the entry rule (inside portal_min_distance, 20 m) does not answer first.
            var entryOut = Site();
            entryOut.Radius = 15;
            entryOut.Entries.Add(At(Primary, 100, 96 + 16));  // 16 m from the centre: past the 15 m fight circle
            AssertRefused(Lint(entryOut), GauntletSiteStore.CentreOutsideCircle);

            var entryEdge = Site();
            entryEdge.Radius = 15;
            entryEdge.Entries.Add(At(Primary, 100, 96 + 15));
            Assert.IsNull(Lint(entryEdge), "an entry on the fight circle's edge is inside");
        }

        [TestMethod]
        public void EntryNearPortal_Refused()
        {
            var near = Site();
            near.Entries = new List<DungeonSpawnPointDef> { At(Primary, 118, 96) };   // 18 m out, 12 m from the portal point at (130, 96)
            AssertRefused(Lint(near), GauntletSiteStore.EntryNearPortal);

            var far = Site();
            far.Entries = new List<DungeonSpawnPointDef> { At(Primary, 100, 96 - 24) };   // 24 m out: past portal_min_distance (20 m)
            AssertRefused(Lint(far), GauntletSiteStore.EntryNearPortal);

            // A point too close to the centre to be picked as a portal does not count against an entry beside it.
            var row = Row("ow_test", At(Primary, 130, 96), At(Primary, 100, 136), At(Primary, 60, 96), At(Primary, 100, 46), At(Primary, 105, 96));
            Assert.IsNull(Lint(Site(), row), "control: only pickable portal points (20 to 55 m out) count");

            var clear = Site();
            clear.Entries = new List<DungeonSpawnPointDef> { At(Primary, 110, 96), At(Primary, 90, 96) };
            Assert.IsNull(Lint(clear), "control: 10 m out and 20 m from the nearest portal point");
        }

        [TestMethod]
        public void EntryOffFootprint_Refused()
        {
            var site = Site();
            site.Entries = new List<DungeonSpawnPointDef> { At(OffBlock, 10, 10) };
            AssertRefused(Lint(site), GauntletSiteStore.OffFootprint);

            var pen = Site();
            pen.Pen = At(OffBlock, 10, 10);
            AssertRefused(Lint(pen), GauntletSiteStore.OffFootprint);

            var centre = Site();
            centre.Centre = At(OffBlock, 10, 10);
            AssertRefused(Lint(centre), GauntletSiteStore.OffFootprint);

            var north = Site();
            north.Entries = new List<DungeonSpawnPointDef> { At(North, 108, -96) };   // (108, 96) on 0x5456, named from 0x5457: on the footprint
            Assert.IsNull(Lint(north), "control: a point on any footprint member passes");
        }

        [TestMethod]
        public void TooFewPortalPoints_Refused()
        {
            // Two good points, plus points that must not count: an interior one, an uncurated one, one too close
            // (10 m, under 20), one too far (80 m, over 55) and one on another landblock (PickPortals' anchor-landblock rule).
            var row = Row("ow_test",
                At(Primary, 130, 96), At(Primary, 100, 136),
                At(Primary, 60, 96, kind: "interior"), At(Primary, 100, 56, curated: false),
                At(Primary, 110, 96), At(Primary, 180, 96), At(North, 100, -150));

            AssertRefused(Lint(Site(), row), GauntletSiteStore.TooFewPortalPoints);

            row.Points.Add(At(Primary, 100, 46));
            Assert.IsNull(Lint(Site(), row), "control: a third qualifying point passes at portals_min 3");
            Assert.AreEqual(3, GauntletSiteStore.QualifyingPortalPoints(Site(), row, GauntletTuning.Defaults, out _).Count, "exactly the three good points");

            AssertRefused(Lint(Site(), row, GauntletTuning.Defaults with { PortalsMin = 4, PortalsMax = 5 }), GauntletSiteStore.TooFewPortalPoints);
        }

        [TestMethod]
        public void UnknownSiteId_Refused()
        {
            AssertRefused(GauntletSiteStore.Lint(Site(), null, GauntletTuning.Defaults), GauntletSiteStore.UnknownSite);
            AssertRefused(GauntletSiteStore.Lint(Site(), new DungeonEntryDef { Id = "ow_test", Landblock = 0x0150 }, GauntletTuning.Defaults), GauntletSiteStore.UnknownSite);

            var noId = Site();
            noId.Id = " ";
            AssertRefused(Lint(noId), GauntletSiteStore.UnknownSite);

            var catalog = new Dictionary<string, DungeonEntryDef> { ["ow_test"] = Row() };
            var store = GauntletSiteStore.Parse(Json(Site("ow_test"), Site("ow_missing"), Site("ow_test")), catalog, GauntletTuning.Defaults);

            Assert.AreEqual(1, store.Sites.Count, "the known site loads once");
            Assert.AreSame(catalog["ow_test"], store.Get("ow_test").Outdoor, "picked by id from the catalog, with no level filter");
            Assert.IsNull(store.Get("ow_missing"));
            Assert.AreEqual(2, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("'ow_missing'") && d.Contains(GauntletSiteStore.UnknownSite)));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("duplicate id")));
        }

        [TestMethod]
        public void BadShape_Refused()
        {
            var none = Site();
            none.Entries.Clear();
            AssertRefused(Lint(none), GauntletSiteStore.BadShape);

            var nine = Site();
            nine.Entries = Enumerable.Range(0, 9).Select(_ => At(Primary, 105, 96)).ToList();
            AssertRefused(Lint(nine), GauntletSiteStore.BadShape);

            var eight = Site();
            eight.Entries = Enumerable.Range(0, 8).Select(_ => At(Primary, 105, 96)).ToList();
            Assert.IsNull(Lint(eight), "eight entries is the maximum");

            var noPen = Site();
            noPen.Pen = null;
            AssertRefused(Lint(noPen), GauntletSiteStore.BadShape);

            var radius = Site();
            radius.Radius = double.NaN;
            AssertRefused(Lint(radius), GauntletSiteStore.BadShape);
        }

        [TestMethod]
        public void File_DefaultsAndFailures()
        {
            var catalog = new Dictionary<string, DungeonEntryDef> { ["ow_test"] = Row() };

            var parsed = GauntletSiteStore.Parse("{ \"version\": 1, \"sites\": [ { \"id\": \"ow_test\", " +
                "\"centre\": { \"cell\": 1414922241, \"x\": 100, \"y\": 96, \"z\": 50 }, " +
                "\"pen\": { \"cell\": 1431699457, \"x\": 100, \"y\": 96, \"z\": 50 }, " +
                "\"entries\": [ { \"cell\": 1414922241, \"x\": 110, \"y\": 96, \"z\": 50 } ], } ] }", catalog, GauntletTuning.Defaults);

            Assert.AreEqual(1, parsed.Sites.Count, string.Join(" | ", parsed.Diagnostics));
            Assert.AreEqual(GauntletSiteDef.DefaultRadius, parsed.Sites[0].Radius, "radius defaults to 60 m");

            Assert.AreEqual(0, GauntletSiteStore.Parse("{ \"version\": 2, \"sites\": [] }", catalog, GauntletTuning.Defaults).Sites.Count);
            Assert.AreEqual(1, GauntletSiteStore.Parse("{ \"version\": 2, \"sites\": [] }", catalog, GauntletTuning.Defaults).Diagnostics.Count);
            Assert.AreEqual(1, GauntletSiteStore.Parse("not json", catalog, GauntletTuning.Defaults).Diagnostics.Count);
            Assert.AreEqual(1, GauntletSiteStore.Parse("", catalog, GauntletTuning.Defaults).Diagnostics.Count);
        }

        private static readonly string[] ShippedIds = { "ow_boneyard", "ow_baishi", "ow_arwic" };

        private static string RepoRoot()
        {
            for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Content", "dungeons", "dynamic")))
                    return dir.FullName;

            Assert.Fail($"Could not find Content/dungeons/dynamic by walking up from {System.AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>The first fault of a sites.json text against the real Thread catalog (null when the shipped contract holds).</summary>
        private static string ShippedFileFault(string json)
        {
            var catalog = ACE.Server.ThreadDungeons.ThreadDungeonStore.Load(System.IO.Path.Combine(RepoRoot(), "Content", "dungeons", "dynamic")).Dungeons;
            var t = GauntletTuning.Defaults;
            var store = GauntletSiteStore.Parse(json, catalog, t);

            if (store.Diagnostics.Count != 0)
                return "diagnostics: " + string.Join(" | ", store.Diagnostics);

            if (!store.Sites.Select(x => x.Id).OrderBy(x => x).SequenceEqual(ShippedIds.OrderBy(x => x)))
                return "sites are " + string.Join(",", store.Sites.Select(x => x.Id));

            foreach (var site in store.Sites)
            {
                var points = GauntletSiteStore.QualifyingPortalPoints(site, catalog[site.Id], t, out var refusal);

                if (points == null || points.Count < t.PortalsMin)
                    return $"{site.Id}: fewer than {t.PortalsMin} portal points ({refusal})";
            }

            return null;
        }

        [TestMethod]
        public void ShippedFile_EverySiteParsesAndPassesLint()
        {
            var json = PooledLootSourceText.Read("Content/dungeons/gauntlet/sites.json");

            Assert.IsNull(ShippedFileFault(json), "the shipped sites.json must satisfy the contract");

            // mutation: a pen moved onto the centre is too close, so that site is dropped with a diagnostic
            var file = System.Text.Json.JsonSerializer.Deserialize<GauntletSitesFileDef>(json);
            var first = file.Sites[0];
            first.Pen = first.Centre;
            var mutated = System.Text.Json.JsonSerializer.Serialize(file);

            var fault = ShippedFileFault(mutated);
            Assert.IsNotNull(fault, "a pen too close must fault the test");
            StringAssert.Contains(fault, GauntletSiteStore.PenTooClose);
        }

        private static string Json(params GauntletSiteDef[] sites)
            => System.Text.Json.JsonSerializer.Serialize(new GauntletSitesFileDef { Version = 1, Sites = sites.ToList() });
    }
}

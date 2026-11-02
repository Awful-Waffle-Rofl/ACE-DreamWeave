using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Outdoor-site loading and lints (Open World Threads WP1). Every fixture is built in memory; nothing here reads
    /// PropertyManager (the warning band is the store's injectable parameter) and no shipped site exists yet.
    /// Every lint test first loads the VALID fixture (a positive control) and then the mutated one, so a lint that
    /// fires for the wrong reason, or a fixture that was never valid, cannot pass.
    ///
    /// Fixture geometry (frame in OutdoorSiteGeometry): primary 0x5456, circle centre (96, 96) r 60, so the edge is 60 m
    /// from the centre and the default 15 m warning band keeps points within 45 m of it.
    /// </summary>
    [TestClass]
    public class OutdoorSiteStoreTests
    {
        private const string Id = "ow_testsite";
        private const string Bosses = "{\"bosses\":[]}";
        private const string Mods = "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}";

        /// <summary>Diagnostics other than the unrelated "attunement.json: empty" every fixture parse raises (no attunement text is supplied).</summary>
        private static List<string> Own(ThreadDungeonStore store) => store.Diagnostics.Where(m => !m.StartsWith("attunement.json:")).ToList();

        private static JsonObject Point(string cell, double x, double y, string kind = "outdoor")
            => new JsonObject { ["cell"] = Convert.ToUInt32(cell, 16), ["x"] = x, ["y"] = y, ["z"] = 10.0, ["clearance"] = 2.0, ["depth"] = 0, ["curated"] = true, ["kind"] = kind };

        private static JsonObject ValidSite(string id = Id) => new JsonObject
        {
            ["version"] = 1,
            ["id"] = id,
            ["primary"] = "0x5456",
            ["footprint"] = new JsonArray("0x5456"),
            ["circle"] = new JsonObject { ["landblock"] = "0x5456", ["x"] = 96.0, ["y"] = 96.0, ["radius"] = 60.0 },
            ["entry"] = "0x54560015 96 70 10 1 0 0 0",
            ["exit"] = "0x54560015 96 78 10 1 0 0 0",
            ["bossAnchor"] = Point("54560030", 96, 96),
            ["include"] = new JsonArray(),
            ["exclude"] = new JsonArray(),
            ["interiorBuildings"] = new JsonArray(),
            ["targetPoints"] = 30,
            ["seed"] = 7,
            ["points"] = new JsonArray(Point("54560020", 100, 110), Point("54560021", 80, 90, "interior")),
        };

        private static string OutdoorIndexRow(string id = Id, string kind = "outdoor", string landblock = null, string levels = null)
            => "{\"id\":\"" + id + "\"," + (landblock == null ? "" : "\"landblock\":\"" + landblock + "\",")
             + "\"kind\":\"" + kind + "\",\"name\":\"Test Site\"," + (levels ?? "\"minLevel\":1,\"maxLevel\":500,") + "\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003604,\"enabled\":true}";

        private static ThreadDungeonStore Load(JsonObject site, string indexId = Id, string fileKey = null, double band = 15.0, string kind = "outdoor", bool omitFile = false, double dropMin = 30.0, string levels = null, double margin = 2.0)
        {
            var index = "{\"dungeons\":[" + OutdoorIndexRow(indexId, kind, levels: levels) + "]}";
            var files = new Dictionary<string, string>();
            if (!omitFile) files[fileKey ?? indexId] = site.ToJsonString();
            return ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string>(), outdoorJsonById: files, outdoorWarningBandM: band, outdoorDropMinInsideM: dropMin, outdoorContainmentMarginM: margin);
        }

        /// <summary>Valid fixture loads; the mutated one is dropped with a diagnostic containing <paramref name="fragment"/>.</summary>
        private static void AssertLint(Action<JsonObject> mutate, string fragment, string id = Id, double band = 15.0)
        {
            var control = Load(ValidSite(id), id, band: band);
            Assert.IsTrue(control.Dungeons.ContainsKey(id), "positive control failed: " + string.Join("\n", control.Diagnostics));

            var site = ValidSite(id);
            mutate(site);
            var store = Load(site, id, band: band);
            Assert.IsFalse(store.Dungeons.ContainsKey(id), "the lint did not drop the site");
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains(fragment)), $"no diagnostic containing '{fragment}':\n" + string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Valid_site_loads_into_a_DungeonEntryDef_of_kind_outdoor()
        {
            var store = Load(ValidSite());
            Assert.AreEqual(0, Own(store).Count, string.Join("\n", store.Diagnostics));
            var d = store.Dungeons[Id];
            Assert.IsTrue(d.IsOutdoor);
            Assert.AreEqual("outdoor", d.Kind);
            Assert.AreEqual((ushort)0x5456, d.Landblock);
            Assert.AreEqual("0x5456", d.LandblockHex);
            Assert.AreEqual("0x54560015 96 70 10 1 0 0 0", d.Entry);
            Assert.AreEqual(2, d.Points.Count);
            Assert.AreEqual("interior", d.Points[1].Kind);
            Assert.AreEqual(0x54560030u, d.BossAnchor.Cell);
            Assert.IsNotNull(d.Outdoor);
            Assert.AreEqual((ushort)0x5456, d.Outdoor.Primary);
            CollectionAssert.AreEqual(new ushort[] { 0x5456 }, d.Outdoor.Footprint);
            Assert.AreEqual(60.0, d.Outdoor.Circle.Radius);
            Assert.AreEqual((ushort)0x5456, d.Outdoor.Circle.Landblock);
            Assert.AreEqual(30, d.Outdoor.TargetPoints);
            Assert.AreEqual(7, d.Outdoor.Seed);
            Assert.AreEqual("0x54560015 96 78 10 1 0 0 0", d.Outdoor.Exit);
        }

        [TestMethod]
        public void Optional_point_maxCollisionHeight_round_trips_and_defaults_to_null()
        {
            var site = ValidSite();
            site["points"].AsArray()[1].AsObject()["maxCollisionHeight"] = 1.8;
            var d = Load(site).Dungeons[Id];
            Assert.IsNull(d.Points[0].MaxCollisionHeight);
            Assert.AreEqual(1.8f, d.Points[1].MaxCollisionHeight);
        }

        [TestMethod]
        public void Multi_landblock_footprint_with_a_circle_across_the_seam_loads()
        {
            var site = ValidSite();
            site["footprint"] = new JsonArray("0x5456", "0x5556");
            site["circle"] = new JsonObject { ["landblock"] = "0x5456", ["x"] = 190.0, ["y"] = 96.0, ["radius"] = 40.0 };
            site["entry"] = "0x54560015 180 96 10 1 0 0 0";
            site["exit"] = "0x54560015 186 96 10 1 0 0 0";
            site["bossAnchor"] = Point("54560030", 190, 96);
            site["points"] = new JsonArray(Point("55560020", 10, 96), Point("54560021", 175, 100));
            var store = Load(site);
            Assert.IsTrue(store.Dungeons.ContainsKey(Id), string.Join("\n", store.Diagnostics));
            CollectionAssert.AreEqual(new ushort[] { 0x5456, 0x5556 }, store.Dungeons[Id].Outdoor.Footprint);
        }

        /// <summary>
        /// The schema example in outdoor/README.md must be a loadable site, so the documentation cannot drift from the
        /// loader. Reads the first ```json block of the README by walking up to Content/dungeons/dynamic/outdoor.
        /// </summary>
        [TestMethod]
        public void Readme_example_is_a_valid_site()
        {
            // The build may copy index.json etc. into the output folder, so FindDynamicDir can stop at a copy that has no
            // README; walk up looking for the README itself.
            string readme = null;
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null && readme == null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "Content", "dungeons", "dynamic", "outdoor", "README.md");
                if (System.IO.File.Exists(candidate)) readme = candidate;
            }
            if (readme == null) Assert.Inconclusive("Could not locate Content/dungeons/dynamic/outdoor/README.md by walking up from the test assembly -- skipping.");
            var text = System.IO.File.ReadAllText(readme).Replace("\r\n", "\n");
            var start = text.IndexOf("```json\n", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "no json block in the README");
            start += "```json\n".Length;
            var json = text.Substring(start, text.IndexOf("```", start, StringComparison.Ordinal) - start);

            var store = Load(JsonNode.Parse(json).AsObject(), "ow_example");
            Assert.IsTrue(store.Dungeons.ContainsKey("ow_example"), string.Join("\n", store.Diagnostics));
            Assert.AreEqual(2, store.Dungeons["ow_example"].Points.Count);
        }

        // ---- dungeon path unaffected ----

        private const string DungeonRow = "{\"id\":\"plain_dungeon\",\"landblock\":\"0x0ABC\",\"name\":\"Plain\",\"minLevel\":1,\"maxLevel\":500,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}";
        private const string DungeonFile = "{\"version\":1,\"landblock\":\"0x0ABC\",\"entry\":\"0x0ABC0100 0 0 0.005\",\"points\":[{\"cell\":180093185,\"x\":1,\"y\":2,\"z\":3}],\"bossAnchor\":{\"cell\":180093185,\"x\":1,\"y\":2,\"z\":3}}";

        [TestMethod]
        public void Dungeon_row_without_kind_defaults_to_dungeon_and_loads_unchanged()
        {
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[" + DungeonRow + "]}", Bosses, Mods,
                new Dictionary<string, string> { ["0x0ABC"] = DungeonFile });
            Assert.AreEqual(0, Own(store).Count, string.Join("\n", store.Diagnostics));
            var d = store.Dungeons["plain_dungeon"];
            Assert.AreEqual("dungeon", d.Kind);
            Assert.IsFalse(d.IsOutdoor);
            Assert.IsNull(d.Outdoor);
            Assert.AreEqual((ushort)0x0ABC, d.Landblock);
        }

        [TestMethod]
        public void Dungeon_and_outdoor_rows_coexist_and_a_bad_outdoor_file_never_drops_the_dungeon()
        {
            var index = "{\"dungeons\":[" + DungeonRow + "," + OutdoorIndexRow() + "]}";
            var bad = ValidSite();
            bad["primary"] = "0x5556"; // not the entry's landblock and not in the footprint
            var store = ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string> { ["0x0ABC"] = DungeonFile },
                outdoorJsonById: new Dictionary<string, string> { [Id] = bad.ToJsonString() });
            Assert.IsTrue(store.Dungeons.ContainsKey("plain_dungeon"));
            Assert.IsFalse(store.Dungeons.ContainsKey(Id));
            Assert.AreEqual(1, Own(store).Count, string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Dungeon_off_landblock_point_lint_is_unchanged_and_ignores_footprints()
        {
            var off = DungeonFile.Replace("\"cell\":180093185,\"x\":1,\"y\":2,\"z\":3}],", "\"cell\":1,\"x\":1,\"y\":2,\"z\":3}],");
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[" + DungeonRow + "]}", Bosses, Mods, new Dictionary<string, string> { ["0x0ABC"] = off });
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("not on landblock 0x0ABC")));
        }

        [TestMethod]
        public void Unknown_kind_is_dropped_with_a_diagnostic()
        {
            var store = Load(ValidSite(), kind: "underwater");
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("unknown kind 'underwater'")));
        }

        // ---- one test per lint ----

        [TestMethod]
        public void Lint_point_outside_the_footprint()
            => AssertLint(s => s["points"].AsArray()[0].AsObject()["cell"] = 0x99990020u, "a point is outside the footprint");

        [TestMethod]
        public void Lint_entry_outside_the_footprint()
            => AssertLint(s => { s["entry"] = "0x99990015 96 70 10 1 0 0 0"; }, "the entry is outside the footprint");

        [TestMethod]
        public void Lint_exit_outside_the_footprint()
            => AssertLint(s => s["exit"] = "0x99990015 96 78 10 1 0 0 0", "the exit is outside the footprint");

        [TestMethod]
        public void Lint_boss_anchor_outside_the_footprint()
            => AssertLint(s => s["bossAnchor"].AsObject()["cell"] = 0x99990030u, "the boss anchor is outside the footprint");

        [TestMethod]
        public void Lint_circle_bounding_box_outside_the_footprint()
            => AssertLint(s => s["circle"].AsObject()["radius"] = 120.0, "bounding box is not inside the footprint");

        [TestMethod]
        public void Lint_circle_bounding_box_off_the_landblock_grid()
            => AssertLint(s =>
            {
                // A circle centred on the western edge of landblock 0x00xx spills off the grid; footprint cannot list it.
                s["primary"] = "0x0056"; s["footprint"] = new JsonArray("0x0056");
                s["circle"] = new JsonObject { ["landblock"] = "0x0056", ["x"] = 10.0, ["y"] = 96.0, ["radius"] = 60.0 };
                s["entry"] = "0x00560015 96 96 10 1 0 0 0"; s["exit"] = "0x00560015 96 96 10 1 0 0 0";
                s["bossAnchor"] = Point("00560030", 96, 96);
                s["points"] = new JsonArray(Point("00560020", 30, 96));
            }, "bounding box is not inside the footprint");

        // Entry (96,130) is 34 m from the centre = 26 m inside a 60 m circle; its exit (96,122) is 8 m toward the centre.
        private static void EntryAt26(JsonObject s) { s["entry"] = "0x54560015 96 130 10 1 0 0 0"; s["exit"] = "0x54560015 96 122 10 1 0 0 0"; }

        [TestMethod]
        public void Lint_entry_closer_to_the_edge_than_drop_min_inside()
            => AssertLint(EntryAt26, "the entry is 26 m inside the circle");

        [TestMethod]
        public void Entry_exactly_drop_min_inside_is_allowed()
        {
            var site = ValidSite();
            site["entry"] = "0x54560015 96 66 10 1 0 0 0"; // 30 m from the centre, 30 m from the edge
            site["exit"] = "0x54560015 96 74 10 1 0 0 0";
            Assert.IsTrue(Load(site).Dungeons.ContainsKey(Id));
        }

        [TestMethod]
        public void Drop_min_inside_is_the_injected_parameter_not_a_constant()
        {
            var site = ValidSite();
            EntryAt26(site);
            Assert.IsFalse(Load(site, dropMin: 30.0).Dungeons.ContainsKey(Id));
            Assert.IsTrue(Load(site, dropMin: 20.0).Dungeons.ContainsKey(Id));
        }

        [TestMethod]
        public void Entry_lint_uses_drop_min_not_the_warning_band()
        {
            // 26 m inside passes the 15 m band; it is the 30 m drop minimum that rejects it, whatever the band is.
            var site = ValidSite();
            EntryAt26(site);
            Assert.IsFalse(Load(site, band: 5.0, dropMin: 30.0).Dungeons.ContainsKey(Id));
            Assert.IsTrue(Load(site, band: 40.0, dropMin: 20.0).Dungeons.ContainsKey(Id), "a band wider than the entry's depth does not matter to the entry");
        }

        [TestMethod]
        public void Warning_band_is_the_injected_parameter_for_points()
        {
            var site = ValidSite();
            var p = site["points"].AsArray()[0].AsObject(); p["x"] = 96.0; p["y"] = 145.0; // 11 m inside
            Assert.IsFalse(Load(site, band: 15.0).Dungeons.ContainsKey(Id));
            Assert.IsTrue(Load(site, band: 5.0).Dungeons.ContainsKey(Id));
        }

        [TestMethod]
        public void Lint_exit_closer_than_5_m_to_the_entry()
            => AssertLint(s => s["exit"] = "0x54560015 96 73 10 1 0 0 0", "the exit is 3 m from the entry");

        [TestMethod]
        public void Lint_exit_further_than_10_m_from_the_entry()
            => AssertLint(s => s["exit"] = "0x54560015 96 85 10 1 0 0 0", "the exit is 15 m from the entry");

        [TestMethod]
        public void Exit_exactly_5_and_10_m_from_the_entry_is_allowed()
        {
            foreach (var y in new[] { 75, 80 })
            {
                var site = ValidSite();
                site["exit"] = "0x54560015 96 " + y + " 10 1 0 0 0";
                Assert.IsTrue(Load(site).Dungeons.ContainsKey(Id), "exit y=" + y);
            }
        }

        [TestMethod]
        public void Lint_exit_further_from_the_centre_than_the_entry()
            => AssertLint(s => s["exit"] = "0x54560015 96 62 10 1 0 0 0", "further from the circle centre than the entry");

        [TestMethod]
        public void Circle_distances_are_horizontal_so_wildly_different_z_changes_nothing()
        {
            // Entry far above, exit far below, a point deep underground, boss in the sky: every lint is XY only.
            var site = ValidSite();
            site["entry"] = "0x54560015 96 70 900 1 0 0 0";
            site["exit"] = "0x54560015 96 78 -400 1 0 0 0";
            site["points"].AsArray()[0].AsObject()["z"] = -500.0;
            site["points"].AsArray()[1].AsObject()["z"] = 1200.0;
            site["bossAnchor"].AsObject()["z"] = 3000.0;
            var store = Load(site);
            Assert.IsTrue(store.Dungeons.ContainsKey(Id), string.Join("\n", store.Diagnostics));

            // ... and a 3D reading of the exit gap (8 m horizontal, 1300 m vertical) would have rejected it.
            Assert.AreEqual(8.0, OutdoorSiteGeometry.HorizontalDistance(new OutdoorPos(0x5456, 96, 70), new OutdoorPos(0x5456, 96, 78)), 1e-9);
        }

        // ---- per-site levels (R3) ----

        [TestMethod]
        public void Outdoor_site_may_cap_below_the_run_ceiling_without_a_diagnostic()
        {
            var store = Load(ValidSite(), levels: "\"minLevel\":40,\"maxLevel\":300,");
            Assert.IsTrue(store.Dungeons.ContainsKey(Id));
            Assert.AreEqual(40, store.Dungeons[Id].MinLevel);
            Assert.AreEqual(300, store.Dungeons[Id].MaxLevel);
            Assert.IsFalse(Own(store).Any(m => m.Contains("run level ceiling")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Outdoor_row_without_levels_defaults_to_1_through_500()
        {
            var store = Load(ValidSite(), levels: "");
            var d = store.Dungeons[Id];
            Assert.AreEqual(1, d.MinLevel);
            Assert.AreEqual(500, d.MaxLevel);
        }

        [TestMethod]
        public void Outdoor_row_with_inverted_levels_still_reports()
        {
            var store = Load(ValidSite(), levels: "\"minLevel\":300,\"maxLevel\":100,");
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("exceeds maxLevel")));
        }

        [TestMethod]
        public void Lint_primary_is_not_the_entrys_landblock()
            => AssertLint(s =>
            {
                s["footprint"] = new JsonArray("0x5456", "0x5556");
                s["primary"] = "0x5556";
            }, "is not the entry's landblock");

        [TestMethod]
        public void Lint_non_boss_point_outside_radius_minus_band()
            => AssertLint(s => { var p = s["points"].AsArray()[0].AsObject(); p["x"] = 96.0; p["y"] = 145.0; }, "point 0 is 11 m inside");

        [TestMethod]
        public void Boss_anchor_is_exempt_from_the_warning_band()
        {
            var site = ValidSite();
            site["bossAnchor"].AsObject()["y"] = 150.0; // 54 m from the centre, 6 m from the edge
            var store = Load(site);
            Assert.IsTrue(store.Dungeons.ContainsKey(Id), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Lint_id_without_the_ow_prefix()
            => AssertLintBadId("site_testsite", "must start with 'ow_'");

        [TestMethod]
        public void Lint_id_with_invalid_characters()
            => AssertLintBadId("ow_Test-Site", "must be lowercase [a-z0-9_]+");

        [TestMethod]
        public void Lint_id_longer_than_16_characters()
            => AssertLintBadId("ow_abcdefghijklmn" /* 17 */, "the limit is 16");

        [TestMethod]
        public void Id_of_exactly_16_characters_is_allowed()
        {
            const string id = "ow_abcdefghijklm"; // 16
            Assert.AreEqual(16, id.Length);
            Assert.IsTrue(Load(ValidSite(id), id).Dungeons.ContainsKey(id));
        }

        private static void AssertLintBadId(string id, string fragment)
        {
            var store = Load(ValidSite(id), id);
            Assert.IsFalse(store.Dungeons.ContainsKey(id));
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains(fragment)), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Lint_file_id_does_not_match_the_index_row()
            => AssertLint(s => s["id"] = "ow_other", "does not match the index row");

        [TestMethod]
        public void Lint_duplicate_footprint_landblock()
            => AssertLint(s => s["footprint"] = new JsonArray("0x5456", "0x5456"), "duplicate footprint landblock 0x5456");

        [TestMethod]
        public void Lint_footprint_in_two_pieces()
            // 0x5458 is two landblocks north of 0x5456: neither edge- nor corner-adjacent, so the group would be split.
            => AssertLint(s => s["footprint"] = new JsonArray("0x5456", "0x5458"), "the footprint is not 8-connected");

        [TestMethod]
        public void Footprint_joined_at_a_corner_or_an_edge_loads()
        {
            foreach (var footprint in new[] { new JsonArray("0x5456", "0x5557"), new JsonArray("0x5456", "0x5556", "0x5657") })
            {
                var site = ValidSite();
                site["footprint"] = footprint;
                var store = Load(site);
                Assert.IsTrue(store.Dungeons.ContainsKey(Id), footprint.ToJsonString() + "\n" + string.Join("\n", store.Diagnostics));
            }
        }

        [TestMethod]
        public void Eight_connectivity_decision()
        {
            Assert.IsTrue(ThreadDungeonStore.IsEightConnected(new ushort[] { 0x5456 }));
            Assert.IsTrue(ThreadDungeonStore.IsEightConnected(new ushort[] { 0x5456, 0x5557 }), "diagonal");
            Assert.IsTrue(ThreadDungeonStore.IsEightConnected(new ushort[] { 0x5658, 0x5456, 0x5557 }), "a chain, listed out of order");
            Assert.IsFalse(ThreadDungeonStore.IsEightConnected(new ushort[] { 0x5456, 0x5458 }), "a one-landblock gap");
            Assert.IsFalse(ThreadDungeonStore.IsEightConnected(new ushort[] { 0x5456, 0x5557, 0x5A5A }), "an island");
        }

        [TestMethod]
        public void Lint_primary_missing_from_the_footprint()
            => AssertLint(s => s["footprint"] = new JsonArray("0x5556"), "is not in the footprint");

        [TestMethod]
        public void Lint_outdoor_index_row_without_a_site_file()
        {
            var store = Load(ValidSite(), omitFile: true);
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains(Id) && m.Contains("has no site file")), string.Join("\n", store.Diagnostics));

            // A file filed under another id is not a match.
            var wrongKey = Load(ValidSite(), fileKey: "ow_elsewhere");
            Assert.AreEqual(0, wrongKey.Dungeons.Count);
        }

        [TestMethod]
        public void Lint_circle_radius_must_be_positive()
            => AssertLint(s => s["circle"].AsObject()["radius"] = 0.0, "circle radius must be");

        [TestMethod]
        public void Lint_index_landblock_must_agree_with_primary()
        {
            var site = ValidSite().ToJsonString();
            var ok = ThreadDungeonStore.Parse("{\"dungeons\":[" + OutdoorIndexRow(landblock: "0x5456") + "]}", Bosses, Mods, new Dictionary<string, string>(),
                outdoorJsonById: new Dictionary<string, string> { [Id] = site });
            Assert.IsTrue(ok.Dungeons.ContainsKey(Id), "positive control");
            var bad = ThreadDungeonStore.Parse("{\"dungeons\":[" + OutdoorIndexRow(landblock: "0x5555") + "]}", Bosses, Mods, new Dictionary<string, string>(),
                outdoorJsonById: new Dictionary<string, string> { [Id] = site });
            Assert.IsFalse(bad.Dungeons.ContainsKey(Id));
            Assert.IsTrue(bad.Diagnostics.Any(m => m.Contains("disagrees with")));
        }

        [TestMethod]
        public void Lint_outdoor_row_needs_an_exit_portal_wcid()
        {
            var index = "{\"dungeons\":[" + OutdoorIndexRow().Replace("\"exitPortalWcid\":1003604", "\"exitPortalWcid\":0") + "]}";
            var store = ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string>(), outdoorJsonById: new Dictionary<string, string> { [Id] = ValidSite().ToJsonString() });
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("exitPortalWcid 0")));
        }

        [TestMethod]
        public void Lint_duplicate_outdoor_id()
        {
            var index = "{\"dungeons\":[" + OutdoorIndexRow() + "," + OutdoorIndexRow() + "]}";
            var store = ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string>(), outdoorJsonById: new Dictionary<string, string> { [Id] = ValidSite().ToJsonString() });
            Assert.AreEqual(1, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("duplicate dungeon id")));
        }

        // ---- review round: F1-F8 ----

        [TestMethod]
        public void Lint_outdoor_row_may_not_name_the_walk_in_exit()
        {
            var index = "{\"dungeons\":[" + OutdoorIndexRow().Replace("\"exitPortalWcid\":1003604", "\"exitPortalWcid\":1003601") + "]}";
            var store = ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string>(), outdoorJsonById: new Dictionary<string, string> { [Id] = ValidSite().ToJsonString() });
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("walk-in exit 1003601")), string.Join("\n", store.Diagnostics));
            Assert.IsTrue(Load(ValidSite()).Dungeons.ContainsKey(Id), "positive control: the click-only exit loads");
        }

        [TestMethod]
        public void Lint_boss_anchor_closer_to_the_edge_than_the_containment_margin()
            => AssertLint(s => s["bossAnchor"].AsObject()["y"] = 155.0 /* 59 m from the centre = 1 m inside */, "the boss anchor is 1 m inside the circle");

        [TestMethod]
        public void Boss_anchor_exactly_at_the_containment_margin_loads_and_the_margin_is_injected()
        {
            var site = ValidSite();
            site["bossAnchor"].AsObject()["y"] = 154.0; // 58 m from the centre = 2 m inside
            Assert.IsTrue(Load(site).Dungeons.ContainsKey(Id));
            site["bossAnchor"].AsObject()["y"] = 155.0; // 1 m inside
            Assert.IsFalse(Load(site, margin: 2.0).Dungeons.ContainsKey(Id));
            Assert.IsTrue(Load(site, margin: 0.5).Dungeons.ContainsKey(Id));
        }

        [TestMethod]
        public void Boss_anchor_is_still_exempt_from_the_warning_band_when_inside_the_margin()
        {
            var site = ValidSite();
            site["bossAnchor"].AsObject()["y"] = 150.0; // 6 m inside: inside the 15 m band, outside the 2 m margin
            Assert.IsTrue(Load(site, band: 15.0, margin: 2.0).Dungeons.ContainsKey(Id));
        }

        [TestMethod]
        public void Sideways_exit_on_the_same_radius_written_at_3_dp_loads_within_the_tolerance()
        {
            // Entry (96,70) is 26 m from the centre. 71.051 puts the exit at 26.2 m (0.2 m further out), 8 m sideways.
            var site = ValidSite();
            site["exit"] = "0x54560015 104 71.051 10 1 0 0 0";
            Assert.IsTrue(Load(site).Dungeons.ContainsKey(Id));
        }

        [TestMethod]
        public void Exit_beyond_the_radial_tolerance_is_rejected()
            => AssertLint(s => s["exit"] = "0x54560015 104 70.946 10 1 0 0 0" /* 26.3 m: 0.3 m further out */, "further from the circle centre than the entry");

        [TestMethod]
        public void Lint_non_boss_point_outside_every_include_area()
            => AssertLint(s => s["include"] = new JsonArray(new JsonObject { ["circle"] = new JsonObject { ["gx"] = 16224.0 + 40.0, ["gy"] = 16608.0 + 40.0, ["r"] = 5.0 } }),
                "point 0 is outside the include areas or inside an exclude area");

        [TestMethod]
        public void Lint_non_boss_point_inside_an_exclude_area()
            => AssertLint(s => s["exclude"] = new JsonArray(new JsonObject { ["circle"] = new JsonObject { ["gx"] = 16128.0 + 100.0, ["gy"] = 16512.0 + 110.0, ["r"] = 3.0 } }),
                "point 0 is outside the include areas or inside an exclude area");

        // An exclude polygon (global metres) around a "building" at local x 70..90, y 80..100 on 0x5456.
        private static JsonObject WithBuildingExclude(JsonObject s)
        {
            const double gx = 16128.0, gy = 16512.0;
            s["exclude"] = new JsonArray(new JsonObject
            {
                ["polygon"] = new JsonArray(new JsonArray(gx + 70, gy + 80), new JsonArray(gx + 90, gy + 80), new JsonArray(gx + 90, gy + 100), new JsonArray(gx + 70, gy + 100)),
            });
            return s;
        }

        [TestMethod]
        public void Interior_point_inside_an_exclude_polygon_around_its_building_loads()
        {
            var site = WithBuildingExclude(ValidSite());
            // The fixture's interior point (80,90) sits inside the polygon; its outdoor point (100,110) does not.
            var store = Load(site);
            Assert.IsTrue(store.Dungeons.ContainsKey(Id), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Outdoor_point_inside_the_same_exclude_polygon_is_dropped()
        {
            var site = WithBuildingExclude(ValidSite());
            site["points"].AsArray()[1].AsObject()["kind"] = "outdoor"; // same spot (80,90), now outdoor
            var store = Load(site);
            Assert.IsFalse(store.Dungeons.ContainsKey(Id));
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("point 1 is outside the include areas or inside an exclude area")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Interior_point_still_gets_the_circle_and_band_checks()
            => AssertLint(s => { WithBuildingExclude(s); var p = s["points"].AsArray()[1].AsObject(); p["x"] = 96.0; p["y"] = 145.0; }, "point 1 is 11 m inside");

        [TestMethod]
        public void A_throw_inside_the_parse_becomes_a_diagnostic_and_a_dropped_site()
        {
            Assert.IsTrue(Load(ValidSite()).Dungeons.ContainsKey(Id), "positive control");
            ThreadDungeonStore.OutdoorParseHook = id => throw new InvalidOperationException("boom " + id);
            try
            {
                ThreadDungeonStore store = null;
                try { store = Load(ValidSite()); }
                catch (Exception ex) { Assert.Fail("the wrapper let a " + ex.GetType().Name + " escape"); }
                Assert.IsFalse(store.Dungeons.ContainsKey(Id));
                Assert.IsTrue(store.Diagnostics.Any(m => m.Contains(Id) && m.Contains("malformed") && m.Contains("InvalidOperationException") && m.Contains("boom")), string.Join("\n", store.Diagnostics));
            }
            finally { ThreadDungeonStore.OutdoorParseHook = null; }
        }

        [TestMethod]
        public void Boss_anchor_is_exempt_from_include_and_exclude()
        {
            var site = ValidSite();
            // Exclude the boss anchor's spot (centre, 96/96) only; the points sit elsewhere.
            site["exclude"] = new JsonArray(new JsonObject { ["circle"] = new JsonObject { ["gx"] = 16128.0 + 96, ["gy"] = 16512.0 + 96, ["r"] = 3.0 } });
            Assert.IsTrue(Load(site).Dungeons.ContainsKey(Id), "the boss may stand in an excluded spot");
        }

        [TestMethod]
        public void A_null_entry_in_points_is_a_diagnostic_and_a_dropped_site_not_a_throw()
        {
            var control = Load(ValidSite());
            Assert.IsTrue(control.Dungeons.ContainsKey(Id));

            var site = ValidSite();
            site["points"].AsArray().Add(null);
            ThreadDungeonStore store = null;
            try { store = Load(site); }
            catch (Exception ex) { Assert.Fail("loading threw " + ex.GetType().Name + ": " + ex.Message); }
            Assert.IsFalse(store.Dungeons.ContainsKey(Id));
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("null entry")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Null_nested_objects_never_throw()
        {
            foreach (var field in new[] { "bossAnchor", "circle", "entry", "exit", "points", "footprint", "include", "exclude" })
            {
                var site = ValidSite();
                site[field] = null;
                ThreadDungeonStore store = null;
                try { store = Load(site); }
                catch (Exception ex) { Assert.Fail($"{field}: loading threw {ex.GetType().Name}: {ex.Message}"); }
                Assert.IsFalse(store.Dungeons.ContainsKey(Id) && (field == "bossAnchor" || field == "circle" || field == "entry" || field == "exit" || field == "points" || field == "footprint"),
                    $"{field} null should drop the site");
            }

            // A null element inside include / exclude / areas.
            foreach (var field in new[] { "include", "exclude" })
            {
                var site = ValidSite();
                site[field] = new JsonArray(null, new JsonObject());
                ThreadDungeonStore store = null;
                try { store = Load(site); }
                catch (Exception ex) { Assert.Fail($"{field}[null]: loading threw {ex.GetType().Name}: {ex.Message}"); }
                Assert.IsFalse(store.Dungeons.ContainsKey(Id));
            }
        }

        [TestMethod]
        public void Whole_file_null_or_not_an_object_never_throws()
        {
            foreach (var text in new[] { "null", "[]", "42", "{\"version\":1,\"id\":\"ow_testsite\"}" })
            {
                var index = "{\"dungeons\":[" + OutdoorIndexRow() + "]}";
                ThreadDungeonStore store = null;
                try { store = ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string>(), outdoorJsonById: new Dictionary<string, string> { [Id] = text }); }
                catch (Exception ex) { Assert.Fail($"'{text}': loading threw {ex.GetType().Name}"); }
                Assert.IsFalse(store.Dungeons.ContainsKey(Id), text);
            }
        }

        // ---- F1: outdoor ids in clearance.json and puzzle-gates.json ----

        private const string PuzzleSite = "{\"id\":\"s1\",\"kind\":\"gate\",\"anchor\":{\"cell\":\"0x0ABC0101\",\"x\":1.5,\"y\":2.5,\"z\":0.005},\"yaw\":90.0,"
            + "\"doorway\":{\"width\":3.0,\"height\":3.2,\"ceiling\":6.0},\"maxN\":5,\"types\":[\"sigil\",\"beam\",\"odd\",\"shuffle\"],"
            + "\"gateModel\":{\"kind\":\"door\",\"wcid\":1006850,\"scale\":1.0,\"panels\":1,\"residentGuid\":null},"
            + "\"shuffleSpots\":[{\"cell\":\"0x0ABC0101\",\"x\":0.0,\"y\":0.0,\"z\":0.0},{\"cell\":\"0x0ABC0102\",\"x\":1.0,\"y\":1.0,\"z\":0.0}],\"toolVersion\":\"puzzlesites/1\"}";

        private static ThreadDungeonStore MixedStore(string clearanceJson = null, string puzzleJson = null)
        {
            var index = "{\"dungeons\":[" + DungeonRow + "," + OutdoorIndexRow() + "]}";
            return ThreadDungeonStore.Parse(index, Bosses, Mods, new Dictionary<string, string> { ["0x0ABC"] = DungeonFile },
                clearanceJson: clearanceJson, puzzleGatesJson: puzzleJson,
                outdoorJsonById: new Dictionary<string, string> { [Id] = ValidSite().ToJsonString() });
        }

        [TestMethod]
        public void Clearance_row_for_an_outdoor_site_is_skipped_with_a_diagnostic_but_a_dungeon_row_loads()
        {
            var json = "{\"version\":1,\"dungeons\":[{\"id\":\"plain_dungeon\",\"landblock\":\"0x0ABC\",\"enabled\":true,\"maxCollisionHeightFullAccess\":2.5},"
                     + "{\"id\":\"" + Id + "\",\"landblock\":\"0x5456\",\"enabled\":true,\"maxCollisionHeightFullAccess\":2.5}]}";
            var store = MixedStore(clearanceJson: json);
            Assert.IsTrue(store.Clearance.ContainsKey("plain_dungeon"), "positive control: the dungeon row loads\n" + string.Join("\n", store.Diagnostics));
            Assert.IsFalse(store.Clearance.ContainsKey(Id));
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains(Id) && m.Contains("outdoor sites carry no clearance row")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Puzzle_gates_for_an_outdoor_site_are_skipped_with_a_diagnostic_but_a_dungeon_entry_loads()
        {
            var json = "{\"version\":1,\"dungeons\":{\"plain_dungeon\":{\"sites\":[" + PuzzleSite + "]},\"" + Id + "\":{\"sites\":[" + PuzzleSite + "]}}}";
            var store = MixedStore(puzzleJson: json);
            Assert.AreEqual(1, store.GetPuzzleSites("plain_dungeon").Count, "positive control: the dungeon entry loads\n" + string.Join("\n", store.Diagnostics));
            Assert.AreEqual(0, store.GetPuzzleSites(Id).Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains(Id) && m.Contains("outdoor sites carry no puzzle gates")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Lint_point_kind_must_be_outdoor_or_interior()
            => AssertLint(s => s["points"].AsArray()[0].AsObject()["kind"] = "cavern", "kind 'cavern' must be outdoor or interior");

        [TestMethod]
        public void Lint_malformed_include_area()
            => AssertLint(s => s["include"] = new JsonArray(new JsonObject { ["polygon"] = new JsonArray(new JsonArray(0, 0), new JsonArray(1, 1)) }), "include[0] must be exactly one of");

        [TestMethod]
        public void Lint_malformed_exclude_area_with_both_shapes()
            => AssertLint(s => s["exclude"] = new JsonArray(new JsonObject
            {
                ["circle"] = new JsonObject { ["gx"] = 1.0, ["gy"] = 1.0, ["r"] = 5.0 },
                ["polygon"] = new JsonArray(new JsonArray(0, 0), new JsonArray(1, 0), new JsonArray(1, 1)),
            }), "exclude[0] must be exactly one of");

        [TestMethod]
        public void Well_formed_include_and_exclude_areas_load()
        {
            var site = ValidSite();
            site["include"] = new JsonArray(new JsonObject { ["circle"] = new JsonObject { ["gx"] = 16224.0, ["gy"] = 16608.0, ["r"] = 40.0 } });
            site["exclude"] = new JsonArray(new JsonObject { ["polygon"] = new JsonArray(new JsonArray(0, 0), new JsonArray(10, 0), new JsonArray(10, 10)) });
            var store = Load(site);
            Assert.IsTrue(store.Dungeons.ContainsKey(Id), string.Join("\n", store.Diagnostics));
            Assert.AreEqual(1, store.Dungeons[Id].Outdoor.Include.Count);
            Assert.AreEqual(3, store.Dungeons[Id].Outdoor.Exclude[0].Polygon.Count);
        }
    }
}

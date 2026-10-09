using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Coverage for the puzzle-gates.json loader (ThreadDungeonStore.ParsePuzzleSites). Same fail-open contract
    /// as clearance.json: a missing, malformed or unsupported-version file is an empty result, a bad site costs
    /// only itself, and nothing throws. Pure: Parse over in-memory strings, no dat and no database.
    /// </summary>
    [TestClass]
    public class PuzzleSiteStoreTests
    {
        private const string Index = "{\"version\":1,\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos\",\"minLevel\":1,\"maxLevel\":400,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";

        private const string SpawnFile = "{\"version\":1,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";

        private const string ValidSite = """
            {
              "id": "s1",
              "kind": "gate",
              "anchor": { "cell": "0x0150018A", "x": 1.5, "y": 2.5, "z": 0.005 },
              "yaw": 90.0,
              "doorway": { "width": 3.0, "height": 3.2, "ceiling": 6.0 },
              "maxN": 5,
              "types": ["sigil", "beam", "odd", "shuffle"],
              "gateModel": { "kind": "door", "wcid": 1006850, "scale": 1.0, "panels": 1, "residentGuid": null },
              "shuffleSpots": [
                { "cell": "0x0150018A", "x": 0.0, "y": 0.0, "z": 0.0 },
                { "cell": "0x0150018B", "x": 1.0, "y": 1.0, "z": 0.0 }
              ],
              "toolVersion": "puzzlesites/1"
            }
            """;

        // ---- fixtures ----

        private static ThreadDungeonStore Build(string puzzleJson)
            => ThreadDungeonStore.Parse(Index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[]}",
                new Dictionary<string, string> { ["0x0150"] = SpawnFile }, puzzleGatesJson: puzzleJson);

        /// <summary>The valid site, mutated by <paramref name="edit"/> and given <paramref name="id"/>.</summary>
        private static JsonObject Site(string id, Action<JsonObject> edit = null)
        {
            var o = JsonNode.Parse(ValidSite).AsObject();
            o["id"] = id;
            edit?.Invoke(o);
            return o;
        }

        private static string File1(params JsonObject[] sites)
        {
            var arr = new JsonArray();
            foreach (var s in sites) arr.Add(s);
            return new JsonObject
            {
                ["version"] = 1,
                ["dungeons"] = new JsonObject { ["filos_doom"] = new JsonObject { ["sites"] = arr } },
            }.ToJsonString();
        }

        private static IEnumerable<string> PuzzleDiagnostics(ThreadDungeonStore s)
            => s.Diagnostics.Where(d => d.Contains("puzzle-gates.json"));

        /// <summary>One bad site beside one good one: only the bad one is dropped, and the diagnostic names it.</summary>
        private static void AssertOnlyBadSiteDropped(Action<JsonObject> breakIt, string expectedFragment)
        {
            var store = Build(File1(Site("bad", breakIt), Site("good")));
            var sites = store.GetPuzzleSites("filos_doom");

            Assert.AreEqual(1, sites.Count, string.Join("\n", store.Diagnostics));
            Assert.AreEqual("good", sites[0].Id);

            var line = PuzzleDiagnostics(store).SingleOrDefault(d => d.Contains("'bad'"));
            Assert.IsNotNull(line, "no diagnostic naming the bad site: " + string.Join("\n", store.Diagnostics));
            StringAssert.Contains(line, expectedFragment);
        }

        // ---- file level ----

        [TestMethod]
        public void Valid_file_round_trips_every_field()
        {
            var store = Build(File1(Site("s1")));
            Assert.AreEqual(0, PuzzleDiagnostics(store).Count(), string.Join("\n", store.Diagnostics));

            var sites = store.GetPuzzleSites("filos_doom");
            Assert.AreEqual(1, sites.Count);
            var s = sites[0];

            Assert.AreEqual("s1", s.Id);
            Assert.AreEqual(PuzzleSiteKind.Gate, s.Kind);
            Assert.AreEqual(0x0150018Au, s.Anchor.Cell);
            Assert.AreEqual(1.5f, s.Anchor.X);
            Assert.AreEqual(2.5f, s.Anchor.Y);
            Assert.AreEqual(0.005f, s.Anchor.Z);
            Assert.AreEqual(90f, s.Yaw);
            Assert.AreEqual(3.0f, s.Doorway.Width);
            Assert.AreEqual(3.2f, s.Doorway.Height);
            Assert.AreEqual(6.0f, s.Doorway.Ceiling);
            Assert.AreEqual(5, s.MaxN);
            CollectionAssert.AreEqual(
                new[] { PuzzleGateType.Sigil, PuzzleGateType.Beam, PuzzleGateType.Odd, PuzzleGateType.Shuffle }, s.Types);
            Assert.AreEqual(PuzzleGateModelKind.Door, s.GateModel.Kind);
            Assert.AreEqual(1006850u, s.GateModel.Wcid);
            Assert.AreEqual(1.0f, s.GateModel.Scale);
            Assert.AreEqual(1, s.GateModel.Panels);
            Assert.AreEqual(2, s.ShuffleSpots.Count);
            Assert.AreEqual(0x0150018Bu, s.ShuffleSpots[1].Cell);
            Assert.AreEqual("puzzlesites/1", s.ToolVersion);
        }

        [TestMethod]
        public void Missing_file_means_no_sites_and_no_diagnostic()
        {
            foreach (var json in new[] { null, "", "   " })
            {
                var store = Build(json);
                Assert.AreEqual(0, store.GetPuzzleSites("filos_doom").Count);
                Assert.AreEqual(0, PuzzleDiagnostics(store).Count());
            }

            Assert.AreEqual(0, ThreadDungeonStore.Empty.GetPuzzleSites("filos_doom").Count);
            Assert.AreEqual(0, ThreadDungeonStore.Empty.GetPuzzleSites(null).Count);
        }

        [TestMethod]
        public void Malformed_json_is_empty_and_never_throws_or_blocks_the_store()
        {
            foreach (var json in new[] { "{ not json", "[1,2,3]", "{\"version\":\"one\"}" })
            {
                var store = Build(json);
                Assert.AreEqual(0, store.GetPuzzleSites("filos_doom").Count, json);
                Assert.IsTrue(PuzzleDiagnostics(store).Any(), json);
                Assert.IsTrue(store.Dungeons.ContainsKey("filos_doom"), "the dungeon store must still load");
            }
        }

        [TestMethod]
        public void Wrong_version_is_rejected_like_the_other_files()
        {
            var json = File1(Site("s1")).Replace("\"version\":1", "\"version\":2");
            var store = Build(json);

            Assert.AreEqual(0, store.GetPuzzleSites("filos_doom").Count);
            Assert.IsTrue(PuzzleDiagnostics(store).Any(d => d.Contains("unsupported version 2")));
        }

        [TestMethod]
        public void Empty_dungeons_object_is_valid_and_empty()
        {
            var store = Build("{ \"version\": 1, \"dungeons\": {} }");
            Assert.AreEqual(0, store.GetPuzzleSites("filos_doom").Count);
            Assert.AreEqual(0, store.PuzzleSites.Count);
            Assert.AreEqual(0, PuzzleDiagnostics(store).Count());
        }

        [TestMethod]
        public void Unknown_dungeon_is_skipped_with_one_diagnostic_and_known_ones_survive()
        {
            var json = new JsonObject
            {
                ["version"] = 1,
                ["dungeons"] = new JsonObject
                {
                    ["ghost"] = new JsonObject { ["sites"] = new JsonArray(Site("a"), Site("b")) },
                    ["filos_doom"] = new JsonObject { ["sites"] = new JsonArray(Site("s1")) },
                },
            }.ToJsonString();

            var store = Build(json);

            Assert.AreEqual(0, store.GetPuzzleSites("ghost").Count);
            Assert.AreEqual(1, store.GetPuzzleSites("filos_doom").Count);
            Assert.AreEqual(1, PuzzleDiagnostics(store).Count(), string.Join("\n", store.Diagnostics));
            Assert.IsTrue(PuzzleDiagnostics(store).Single().Contains("'ghost'"));
        }

        [TestMethod]
        public void Dungeon_value_without_a_sites_array_is_skipped()
        {
            var store = Build("{\"version\":1,\"dungeons\":{\"filos_doom\":{\"sites\":5}}}");
            Assert.AreEqual(0, store.GetPuzzleSites("filos_doom").Count);
            Assert.AreEqual(1, PuzzleDiagnostics(store).Count());
        }

        [TestMethod]
        public void Load_reads_puzzle_gates_json_from_the_content_folder()
        {
            var shipped = ThreadDungeonStoreTests.FindDynamicDir();
            if (shipped == null)
                Assert.Inconclusive("Could not locate Content/dungeons/dynamic by walking up from the test assembly -- skipping.");

            var dir = Path.Combine(Path.GetTempPath(), "puzzle-site-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                // Everything the shipped folder holds EXCEPT puzzle-gates.json, which is shipped now: this test exercises the
                // absent-file and present-file cases itself, so it must not inherit the real one.
                foreach (var f in Directory.GetFiles(shipped, "*.json"))
                    if (!string.Equals(Path.GetFileName(f), "puzzle-gates.json", StringComparison.OrdinalIgnoreCase))
                        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));

                // No puzzle-gates.json present -> no sites, no diagnostic.
                var without = ThreadDungeonStore.Load(dir);
                Assert.AreEqual(0, without.PuzzleSites.Count);
                Assert.AreEqual(0, PuzzleDiagnostics(without).Count());

                File.WriteAllText(Path.Combine(dir, "puzzle-gates.json"), File1(Site("s1")));
                var with = ThreadDungeonStore.Load(dir);
                Assert.AreEqual(1, with.GetPuzzleSites("filos_doom").Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- per-site rules: each skips only the offending site ----

        [TestMethod] public void Unknown_kind_is_skipped() => AssertOnlyBadSiteDropped(o => o["kind"] = "window", "kind");
        [TestMethod] public void Empty_types_is_skipped() => AssertOnlyBadSiteDropped(o => o["types"] = new JsonArray(), "types");
        [TestMethod] public void Unknown_type_is_skipped() => AssertOnlyBadSiteDropped(o => o["types"] = new JsonArray("sigil", "riddle"), "riddle");
        [TestMethod] public void MaxN_zero_is_skipped() => AssertOnlyBadSiteDropped(o => o["maxN"] = 0, "maxN");
        [TestMethod] public void MaxN_six_is_skipped() => AssertOnlyBadSiteDropped(o => o["maxN"] = 6, "maxN");
        [TestMethod] public void Unknown_gate_model_kind_is_skipped() => AssertOnlyBadSiteDropped(o => o["gateModel"]["kind"] = "wall", "gateModel.kind");
        [TestMethod] public void Door_with_zero_wcid_is_skipped() => AssertOnlyBadSiteDropped(o => o["gateModel"]["wcid"] = 0, "wcid");
        [TestMethod] public void Scale_below_band_is_skipped() => AssertOnlyBadSiteDropped(o => o["gateModel"]["scale"] = 0.79, "scale");
        [TestMethod] public void Scale_above_band_is_skipped() => AssertOnlyBadSiteDropped(o => o["gateModel"]["scale"] = 1.26, "scale");
        [TestMethod] public void Barrier_with_zero_panels_is_skipped() => AssertOnlyBadSiteDropped(o => { o["gateModel"]["kind"] = "barrier"; o["gateModel"]["panels"] = 0; }, "panels");
        [TestMethod] public void Resident_without_guid_is_skipped() => AssertOnlyBadSiteDropped(o => { o["gateModel"]["kind"] = "resident"; o["gateModel"]["residentGuid"] = null; }, "residentGuid");
        [TestMethod] public void Shuffle_with_one_spot_is_skipped() => AssertOnlyBadSiteDropped(o => o["shuffleSpots"] = new JsonArray(Site("x")["anchor"].DeepClone()), "shuffleSpots");
        [TestMethod] public void Unparseable_anchor_cell_is_skipped() => AssertOnlyBadSiteDropped(o => o["anchor"]["cell"] = "northwest", "anchor cell");
        [TestMethod] public void Wrongly_typed_field_skips_only_that_site() => AssertOnlyBadSiteDropped(o => o["maxN"] = "five", "malformed");

        [TestMethod]
        public void Duplicate_site_id_keeps_the_first()
        {
            var store = Build(File1(Site("s1"), Site("s1")));
            Assert.AreEqual(1, store.GetPuzzleSites("filos_doom").Count);
            Assert.IsTrue(PuzzleDiagnostics(store).Any(d => d.Contains("duplicate")));
        }

        [TestMethod]
        public void Resident_ignores_wcid_and_parses_the_guid()
        {
            var store = Build(File1(Site("r", o =>
            {
                o["gateModel"] = new JsonObject { ["kind"] = "resident", ["wcid"] = 0, ["scale"] = 9.0, ["panels"] = 0, ["residentGuid"] = "0x7015018C" };
            })));

            var s = store.GetPuzzleSites("filos_doom").Single();
            Assert.AreEqual(PuzzleGateModelKind.Resident, s.GateModel.Kind);
            Assert.AreEqual(0x7015018Cu, s.GateModel.ResidentGuid);
        }

        [TestMethod]
        public void Shuffle_spots_are_only_required_when_shuffle_is_listed()
        {
            var store = Build(File1(Site("s1", o =>
            {
                o["types"] = new JsonArray("sigil");
                o["shuffleSpots"] = new JsonArray();
            })));

            Assert.AreEqual(1, store.GetPuzzleSites("filos_doom").Count, string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Barrier_with_panels_and_in_band_scale_is_accepted()
        {
            var store = Build(File1(Site("b", o =>
            {
                o["gateModel"] = new JsonObject { ["kind"] = "barrier", ["wcid"] = 1006851, ["scale"] = 1.25, ["panels"] = 2, ["residentGuid"] = null };
            })));

            var s = store.GetPuzzleSites("filos_doom").Single();
            Assert.AreEqual(PuzzleGateModelKind.Barrier, s.GateModel.Kind);
            Assert.AreEqual(2, s.GateModel.Panels);
        }

        // ---- reward sites need a bossAnchor ----

        [TestMethod]
        public void Reward_site_is_kept_where_the_dungeon_has_a_boss_anchor()
        {
            var store = Build(File1(Site("r1", o => o["kind"] = "reward")));
            var s = store.GetPuzzleSites("filos_doom").Single();
            Assert.AreEqual(PuzzleSiteKind.Reward, s.Kind);
        }

        /// <summary>
        /// Parse already drops a dungeon with no bossAnchor from the store, so this rule cannot be reached
        /// through Parse; the validator is driven directly with an anchorless dungeon instead.
        /// </summary>
        [TestMethod]
        public void Reward_site_without_a_boss_anchor_is_rejected_but_a_gate_site_is_not()
        {
            var noAnchor = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, BossAnchor = null };
            var withAnchor = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, BossAnchor = new DungeonSpawnPointDef() };

            PuzzleSiteDef Make(string kind) => System.Text.Json.JsonSerializer.Deserialize<PuzzleSiteDef>(Site("x", o => o["kind"] = kind).ToJsonString());

            var problem = ThreadDungeonStore.ValidatePuzzleSite(Make("reward"), noAnchor);
            Assert.IsNotNull(problem);
            StringAssert.Contains(problem, "bossAnchor");

            Assert.IsNull(ThreadDungeonStore.ValidatePuzzleSite(Make("reward"), withAnchor));
            Assert.IsNull(ThreadDungeonStore.ValidatePuzzleSite(Make("gate"), noAnchor));
        }

        // ---- the same rule through ParsePuzzleSites, with a dictionary that holds an anchorless dungeon ----

        [TestMethod]
        public void ParsePuzzleSites_drops_a_reward_site_in_an_anchorless_dungeon_but_keeps_its_gate_site()
        {
            var dungeons = new Dictionary<string, DungeonEntryDef>
            {
                ["filos_doom"] = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, BossAnchor = null },
            };
            var diagnostics = new List<string>();

            var result = ThreadDungeonStore.ParsePuzzleSites(File1(Site("rw", o => o["kind"] = "reward"), Site("g")), dungeons, diagnostics);

            Assert.AreEqual(1, result["filos_doom"].Count);
            Assert.AreEqual("g", result["filos_doom"][0].Id);
            Assert.IsTrue(diagnostics.Any(d => d.Contains("'rw'") && d.Contains("bossAnchor")));
        }

        // ---- doorway and gateModel: required for a gate, ignored entirely for a reward ----

        [TestMethod]
        public void Reward_site_loads_without_doorway_or_gateModel()
        {
            var store = Build(File1(Site("r1", o => { o["kind"] = "reward"; o.Remove("doorway"); o.Remove("gateModel"); })));

            Assert.AreEqual(0, PuzzleDiagnostics(store).Count(), string.Join("\n", store.Diagnostics));
            var s = store.GetPuzzleSites("filos_doom").Single();
            Assert.AreEqual(PuzzleSiteKind.Reward, s.Kind);
            Assert.IsNull(s.Doorway);
            Assert.IsNull(s.GateModel);
        }

        [TestMethod]
        public void Reward_site_ignores_an_invalid_doorway_and_gateModel_without_a_diagnostic()
        {
            // The placeholder shape the tool writes today, plus values a GATE would be rejected for.
            var store = Build(File1(Site("r1", o =>
            {
                o["kind"] = "reward";
                o["doorway"] = new JsonObject { ["width"] = 0, ["height"] = 0, ["ceiling"] = 4.0 };
                o["gateModel"]["kind"] = "wall";
                o["gateModel"]["wcid"] = 0;
                o["gateModel"]["scale"] = 9.0;
            })));

            Assert.AreEqual(0, PuzzleDiagnostics(store).Count(), "ignored entirely, so nothing is logged: " + string.Join("\n", store.Diagnostics));
            var s = store.GetPuzzleSites("filos_doom").Single();
            Assert.IsNull(s.GateModel, "cleared, so nothing downstream can read the placeholder");
            Assert.IsNull(s.Doorway);
        }

        [TestMethod] public void Gate_site_without_doorway_is_skipped() => AssertOnlyBadSiteDropped(o => o.Remove("doorway"), "doorway");
        [TestMethod] public void Gate_site_without_gateModel_is_skipped() => AssertOnlyBadSiteDropped(o => o.Remove("gateModel"), "gateModel");

        [TestMethod]
        public void The_shipped_file_loads_every_site_with_zero_diagnostics()
        {
            var sites = ThreadPuzzleSitePickerTests.ShippedSites(out var diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));

            var all = sites.Values.SelectMany(v => v).ToList();
            Assert.IsTrue(all.Any(s => s.Kind == PuzzleSiteKind.Reward), "the shipped file carries reward sites");
            Assert.IsTrue(all.Where(s => s.Kind == PuzzleSiteKind.Reward).All(s => s.GateModel == null && s.Doorway == null), "reward placeholders are ignored");
            Assert.IsTrue(all.Where(s => s.Kind == PuzzleSiteKind.Gate).All(s => s.GateModel != null && s.Doorway != null));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The encounters.json loader (ThreadEncounterStore.Parse, reached through ThreadDungeonStore.Parse and Load). Same
    /// fail-open contract as puzzle-gates.json: a missing file is silently empty, a malformed or unsupported file is one
    /// diagnostic and empty, a bad type or dungeon row costs only itself, nothing throws. Pure: no dat, no database, no
    /// PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadEncounterStoreTests
    {
        private const string Index = "{\"version\":1,\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos\",\"minLevel\":1,\"maxLevel\":500,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";

        private const string SpawnFile = "{\"version\":1,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";

        private const string GoodType = "{\"id\":\"shrine_a\",\"kind\":\"shrine\",\"weight\":2,\"placement\":{\"minClearance\":3,\"minDepth\":1,\"maxDepth\":9,\"minEntryWalk\":20,\"minBossDist\":15,\"minSpacing\":25,\"runKinds\":[\"dungeon\",\"outdoor\"]},\"config\":{\"adds\":3,\"nested\":{\"k\":\"v\"}}}";

        private static ThreadDungeonStore Build(string encountersJson)
            => ThreadDungeonStore.Parse(Index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[]}",
                new Dictionary<string, string> { ["0x0150"] = SpawnFile }, encountersJson: encountersJson);

        private static List<string> EncounterDiagnostics(ThreadDungeonStore s) => s.Diagnostics.Where(d => d.StartsWith("encounters.json", StringComparison.Ordinal)).ToList();

        private static string File(string types, string dungeons = "{}", int version = 1)
            => "{\"version\":" + version + ",\"types\":[" + types + "],\"dungeons\":" + dungeons + "}";

        // ---- file level -----------------------------------------------------------------------------------------

        [TestMethod]
        public void A_missing_file_is_empty_and_silent()
        {
            foreach (var missing in new[] { null, "", "   " })
            {
                var store = Build(missing);

                Assert.AreSame(ThreadEncounterStore.Empty, store.Encounters);
                Assert.AreEqual(0, EncounterDiagnostics(store).Count, string.Join("\n", store.Diagnostics));
            }

            Assert.AreSame(ThreadEncounterStore.Empty, ThreadDungeonStore.Empty.Encounters, "the empty Thread store carries the empty encounter store");
        }

        [TestMethod]
        public void A_malformed_file_is_one_diagnostic_and_empty()
        {
            var store = Build("{ \"version\": 1, \"types\": [ { \"id\": ");

            Assert.AreEqual(0, store.Encounters.Types.Count);
            Assert.AreEqual(1, EncounterDiagnostics(store).Count, string.Join("\n", store.Diagnostics));

            // Control: the rest of the store loaded normally.
            Assert.IsTrue(store.Dungeons.ContainsKey("filos_doom"));
        }

        [TestMethod]
        public void An_unsupported_version_is_ignored_with_a_diagnostic()
        {
            var store = Build(File(GoodType, version: 2));

            Assert.AreEqual(0, store.Encounters.Types.Count);
            StringAssert.Contains(EncounterDiagnostics(store).Single(), "unsupported version 2");

            // Control: the same file at version 1 loads its type.
            Assert.AreEqual(1, Build(File(GoodType)).Encounters.Types.Count);
        }

        [TestMethod]
        public void A_valid_type_round_trips_every_field_and_keeps_its_config_after_the_document_is_gone()
        {
            var store = Build(File(GoodType));
            var t = store.Encounters.Types.Single();

            Assert.AreEqual(0, EncounterDiagnostics(store).Count, string.Join("\n", store.Diagnostics));
            Assert.AreEqual("shrine_a", t.Id);
            Assert.AreEqual("shrine", t.Kind);
            Assert.IsTrue(t.Enabled);
            Assert.AreEqual(2.0, t.Weight);
            Assert.AreEqual(3f, t.Placement.MinClearance);
            Assert.AreEqual(1, t.Placement.MinDepth);
            Assert.AreEqual(9, t.Placement.MaxDepth);
            Assert.AreEqual(20f, t.Placement.MinEntryWalk);
            Assert.AreEqual(15f, t.Placement.MinBossDist);
            Assert.AreEqual(25f, t.Placement.MinSpacing);
            CollectionAssert.AreEqual(new[] { "dungeon", "outdoor" }, t.Placement.RunKinds);

            // The opaque block survives: it is a detached clone, readable after Parse returned.
            GC.Collect();
            Assert.AreEqual(JsonValueKind.Object, t.Config.ValueKind);
            Assert.AreEqual(3, t.Config.GetProperty("adds").GetInt32());
            Assert.AreEqual("v", t.Config.GetProperty("nested").GetProperty("k").GetString());

            Assert.AreSame(t, store.Encounters.FindType("shrine_a"));
            Assert.IsNull(store.Encounters.FindType("nope"));
        }

        [TestMethod]
        public void Defaults_apply_when_fields_are_absent()
        {
            var t = Build(File("{\"id\":\"bare\",\"kind\":\"shrine\"}")).Encounters.Types.Single();

            Assert.IsTrue(t.Enabled);
            Assert.AreEqual(1.0, t.Weight);
            Assert.AreEqual(0f, t.Placement.MinClearance);
            Assert.IsNull(t.Placement.MinDepth);
            Assert.IsTrue(t.Placement.AllowsRunKind(false) && t.Placement.AllowsRunKind(true), "no runKinds = both");
            Assert.AreEqual(JsonValueKind.Undefined, t.Config.ValueKind);
        }

        // ---- one bad row among good ones ------------------------------------------------------------------------

        private static void AssertOnlyBadDropped(string badType, string expectedFragment)
        {
            var store = Build(File(GoodType + "," + badType + ",{\"id\":\"good_b\",\"kind\":\"shrine\"}"));
            var ids = store.Encounters.Types.Select(t => t.Id).ToList();

            CollectionAssert.AreEqual(new[] { "shrine_a", "good_b" }, ids, string.Join("\n", store.Diagnostics));

            var lines = EncounterDiagnostics(store);
            Assert.AreEqual(1, lines.Count, string.Join("\n", store.Diagnostics));
            StringAssert.Contains(lines[0], expectedFragment);
        }

        [TestMethod]
        public void One_bad_type_costs_only_itself()
        {
            AssertOnlyBadDropped("{\"id\":\"Bad-Id\",\"kind\":\"shrine\"}", "must match [a-z0-9_]+");
            AssertOnlyBadDropped("{\"id\":\"no_kind\"}", "kind is missing");
            AssertOnlyBadDropped("{\"id\":\"neg\",\"kind\":\"shrine\",\"weight\":-1}", "weight -1");
            AssertOnlyBadDropped("{\"id\":\"typed\",\"kind\":\"shrine\",\"weight\":\"heavy\"}", "malformed");
            AssertOnlyBadDropped("{\"id\":\"depths\",\"kind\":\"shrine\",\"placement\":{\"minDepth\":5,\"maxDepth\":2}}", "exceeds maxDepth");
            AssertOnlyBadDropped("{\"id\":\"kinds\",\"kind\":\"shrine\",\"placement\":{\"runKinds\":[\"sky\"]}}", "runKinds entry 'sky'");
            AssertOnlyBadDropped("{\"id\":\"spacing\",\"kind\":\"shrine\",\"placement\":{\"minSpacing\":-3}}", "minSpacing");
            AssertOnlyBadDropped("42", "not an object");
        }

        [TestMethod]
        public void A_duplicate_id_keeps_the_first()
        {
            var store = Build(File(GoodType + ",{\"id\":\"shrine_a\",\"kind\":\"other\"}"));

            Assert.AreEqual("shrine", store.Encounters.Types.Single().Kind);
            StringAssert.Contains(EncounterDiagnostics(store).Single(), "duplicate type id 'shrine_a'");
        }

        // ---- per-dungeon rows -----------------------------------------------------------------------------------

        [TestMethod]
        public void A_dungeon_row_disables_that_dungeon_with_its_reason()
        {
            var store = Build(File(GoodType, "{\"filos_doom\":{\"disabled\":true,\"reason\":\"tight halls\"}}"));

            Assert.IsTrue(store.Encounters.IsDisabledFor("filos_doom", out var reason));
            Assert.AreEqual("tight halls", reason);
            Assert.IsFalse(store.Encounters.IsDisabledFor("other", out _));
            Assert.AreEqual(0, EncounterDiagnostics(store).Count, string.Join("\n", store.Diagnostics));

            // Control: disabled false leaves the dungeon on.
            Assert.IsFalse(Build(File(GoodType, "{\"filos_doom\":{\"disabled\":false}}")).Encounters.IsDisabledFor("filos_doom", out _));
        }

        [TestMethod]
        public void A_dungeon_row_for_an_unknown_dungeon_or_of_the_wrong_shape_costs_only_itself()
        {
            var store = Build(File(GoodType, "{\"ghost\":{\"disabled\":true},\"filos_doom\":{\"disabled\":true}}"));

            Assert.IsTrue(store.Encounters.IsDisabledFor("filos_doom", out _));
            StringAssert.Contains(EncounterDiagnostics(store).Single(), "'ghost' is not in index.json");

            var typed = Build(File(GoodType, "{\"filos_doom\":{\"disabled\":\"yes\"}}"));
            Assert.IsFalse(typed.Encounters.IsDisabledFor("filos_doom", out _));
            StringAssert.Contains(EncounterDiagnostics(typed).Single(), "malformed");
            Assert.AreEqual(1, typed.Encounters.Types.Count, "a bad dungeon row does not cost the types");
        }

        // ---- reload (Load twice, which is what /dd reload's ThreadDungeonManager.Reload does) ------------------

        [TestMethod]
        public void Load_rereads_encounters_json_so_a_reload_picks_up_an_edit()
        {
            var temp = Path.Combine(Path.GetTempPath(), "enc_store_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            try
            {
                var path = Path.Combine(temp, ThreadEncounterStore.FileName);

                Assert.AreEqual(0, ThreadDungeonStore.Load(temp).Encounters.Types.Count, "no file: no types");

                System.IO.File.WriteAllText(path, File(GoodType));
                Assert.AreEqual("shrine_a", ThreadDungeonStore.Load(temp).Encounters.Types.Single().Id);

                System.IO.File.WriteAllText(path, File("{\"id\":\"shrine_b\",\"kind\":\"shrine\"},{\"id\":\"shrine_c\",\"kind\":\"shrine\"}"));
                CollectionAssert.AreEqual(new[] { "shrine_b", "shrine_c" }, ThreadDungeonStore.Load(temp).Encounters.Types.Select(t => t.Id).ToList());
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        [TestMethod]
        public void The_shipped_file_parses_clean()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();

            Assert.AreEqual(0, EncounterDiagnostics(store).Count, string.Join("\n", EncounterDiagnostics(store)));
            Assert.IsTrue(System.IO.File.Exists(Path.Combine(ThreadDungeonStoreTests.FindDynamicDir(), ThreadEncounterStore.FileName)), "the shipped folder carries encounters.json");
        }

        // ---- countWeights ---------------------------------------------------------------------------------------

        private static string FileWithWeights(string weightsJson)
            => "{\"version\":1,\"countWeights\":" + weightsJson + ",\"types\":[" + GoodType + "],\"dungeons\":{}}";

        [TestMethod]
        public void Count_weights_round_trip_and_absence_is_null_and_silent()
        {
            var store = Build(FileWithWeights("[20, 40, 25, 10, 4, 1]"));
            CollectionAssert.AreEqual(new[] { 20.0, 40.0, 25.0, 10.0, 4.0, 1.0 }, store.Encounters.CountWeights.ToArray());
            Assert.AreEqual(0, EncounterDiagnostics(store).Count, string.Join("\n", store.Diagnostics));

            foreach (var absent in new[] { Build(File(GoodType)), Build(FileWithWeights("null")) })
            {
                Assert.IsNull(absent.Encounters.CountWeights);
                Assert.AreEqual(0, EncounterDiagnostics(absent).Count, string.Join("\n", absent.Diagnostics));
            }

            // The full ceiling + 1 entries are accepted.
            Assert.AreEqual(ThreadEncounterPicker.MaxPerRunCeiling + 1, Build(FileWithWeights("[1,1,1,1,1,1,1,1,1]")).Encounters.CountWeights.Count);
        }

        [TestMethod]
        public void Bad_count_weights_are_one_diagnostic_and_fall_back_without_costing_the_types()
        {
            var cases = new (string Json, string Expect)[]
            {
                ("{\"a\":1}", "must be an array of numbers"),
                ("[1, \"two\"]", "[1] must be a number"),
                ("[1, -1]", "[1] -1 must be a finite number >= 0"),
                ("[]", "must list 1 to 9 weights"),
                ("[1,1,1,1,1,1,1,1,1,1]", "must list 1 to 9 weights (counts 0 to 8), not 10"),
                ("[0, 0, 0]", "must have at least one weight > 0"),
            };

            foreach (var (json, expect) in cases)
            {
                var store = Build(FileWithWeights(json));
                var diagnostic = EncounterDiagnostics(store).Single();

                Assert.IsNull(store.Encounters.CountWeights, json);
                StringAssert.Contains(diagnostic, "countWeights " + expect, json);
                StringAssert.Contains(diagnostic, "; ignored, the slot_chance roll is used", json);
                Assert.AreEqual(1, store.Encounters.Types.Count, "the type rows still load: " + json);
            }
        }
    }
}

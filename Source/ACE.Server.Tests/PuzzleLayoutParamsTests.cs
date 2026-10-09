using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Per-site layout parameters: the pure core (generator input) and the puzzle-gates.json loader.</summary>
    [TestClass]
    public class PuzzleLayoutParamsTests
    {
        private static readonly Vector3 Anchor = new Vector3(100f, 50f, 12f);
        private const float Yaw = 37f;

        private static PuzzleGateOptions Opts(string line)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var o, out var err), err);
            return o;
        }

        private static PuzzleGateGenerator Gen(string line, int seed, PuzzleLayoutParams layout)
        {
            var input = new PuzzleGateInput { AnchorPosition = Anchor, AnchorYawDeg = Yaw, Options = Opts(line), Seed = seed, Layout = layout, SpotValidator = null };
            Assert.IsTrue(PuzzleGateGenerator.TryCreate(input, out var g, out var err), err);
            return g;
        }

        private static Vector3 Local(PuzzleObjectSpec s) => PuzzleGateGenerator.ToLocal(Anchor, Yaw, s.Position);

        // ---- core ----

        [TestMethod]
        public void Default_layout_changes_nothing_for_any_type()
        {
            foreach (var line in new[] { "sigil n=5", "beam n=5 beams=3", "odd n=4", "shuffle" })
            {
                for (var seed = 1; seed <= 20; seed++)
                {
                    var a = Gen(line, seed, null).Next();
                    var b = Gen(line, seed, PuzzleLayoutParams.Default).Next();
                    Assert.AreEqual(a.AnswerSlot, b.AnswerSlot);
                    Assert.AreEqual(a.Specs.Count, b.Specs.Count);
                    for (var i = 0; i < a.Specs.Count; i++)
                        Assert.AreEqual(a.Specs[i].Position, b.Specs[i].Position);
                    Assert.AreEqual(a.Gate.Position, b.Gate.Position);
                }
            }
        }

        [TestMethod]
        public void Gate_distance_and_heights_move_exactly_the_objects_they_name()
        {
            var layout = new PuzzleLayoutParams { GateDistance = 11f, LightHeight = 2.3f, IndicatorHeight = 2.8f, HubHeight = 3.1f };

            var sigil = Gen("sigil n=5", 3, layout).Next();
            Assert.AreEqual(11f, Local(sigil.Gate).Y, 1e-3f);
            foreach (var l in sigil.Specs.Where(s => s.Role == PuzzleRole.Light))
                Assert.AreEqual(2.3f, Local(l).Z, 1e-3f);
            foreach (var i in sigil.Specs.Where(s => s.Role == PuzzleRole.Indicator))
            {
                Assert.AreEqual(2.8f, Local(i).Z, 1e-3f);
                Assert.AreEqual(11f - PuzzleGateTunables.IndicatorForward, Local(i).Y, 1e-3f);
            }

            // levers stay where they were: the gate distance never moves the row
            foreach (var c in sigil.Specs.Where(s => s.Role == PuzzleRole.Candidate))
                Assert.AreEqual(3f, Local(c).Y, 1e-3f);

            // a beam host sits on the segment hub -> target, so its height is bounded by the lowered hub
            var beam = Gen("beam n=5 beams=3", 3, layout).Next();
            foreach (var h in beam.Specs.Where(s => s.Role == PuzzleRole.Light))
                Assert.IsTrue(Local(h).Z <= 3.1f + 1e-3f, $"host z {Local(h).Z} above the lowered hub");
        }

        [TestMethod]
        public void Stream_is_unchanged_by_the_layout_for_the_same_seed()
        {
            var layout = new PuzzleLayoutParams { GateDistance = 5f, HubHeight = 3f };
            for (var seed = 1; seed <= 30; seed++)
            {
                var a = Gen("sigil", seed, null);
                var b = Gen("sigil", seed, layout);
                Assert.AreEqual(a.N, b.N);
                Assert.AreEqual(a.Next().AnswerSlot, b.Next().AnswerSlot);
            }
        }

        [TestMethod]
        public void Shuffle_ring_stays_in_front_of_a_nearer_gate()
        {
            var layout = new PuzzleLayoutParams { GateDistance = 4f };
            var g = Gen("shuffle radius=8 spots=8", 1, layout);
            foreach (var s in g.Spots)
                Assert.IsTrue(PuzzleGateGenerator.ToLocal(Anchor, Yaw, s).Y < 4f - PuzzleGateTunables.ShuffleGateClearance + 1e-3f);
        }

        [TestMethod]
        public void Out_of_range_layout_is_refused_by_name()
        {
            foreach (var (layout, name) in new (PuzzleLayoutParams, string)[]
            {
                (new PuzzleLayoutParams { GateDistance = 3.9f }, "gateDistance"),
                (new PuzzleLayoutParams { GateDistance = 14.1f }, "gateDistance"),
                (new PuzzleLayoutParams { LightHeight = 2.1f }, "lightHeight"),
                (new PuzzleLayoutParams { IndicatorHeight = 2.1f }, "indicatorHeight"),
                (new PuzzleLayoutParams { HubHeight = 2.5f }, "hubHeight"),
                (new PuzzleLayoutParams { HubHeight = float.NaN }, "hubHeight"),
            })
            {
                var input = new PuzzleGateInput { AnchorPosition = Anchor, AnchorYawDeg = Yaw, Options = Opts("sigil"), Seed = 1, Layout = layout };
                Assert.IsFalse(PuzzleGateGenerator.TryCreate(input, out _, out var err));
                StringAssert.Contains(err, name);
            }

            Assert.IsTrue(new PuzzleLayoutParams { GateDistance = 4f, LightHeight = 2.2f, IndicatorHeight = 2.2f, HubHeight = 2.6f }.TryValidate(out _), "the lower bounds are inclusive");
        }

        // ---- loader ----

        private const string Index = "{\"version\":1,\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos\",\"minLevel\":1,\"maxLevel\":400,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
        private const string SpawnFile = "{\"version\":1,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";

        private static JsonObject Site(string id, JsonNode layout = null, bool withLayout = false)
        {
            var o = JsonNode.Parse("""
                {
                  "id": "x", "kind": "gate",
                  "anchor": { "cell": "0x0150018A", "x": 1.5, "y": 2.5, "z": 0.005 },
                  "yaw": 90.0,
                  "doorway": { "width": 3.0, "height": 3.2, "ceiling": 6.0 },
                  "maxN": 5, "types": ["sigil", "beam", "odd"],
                  "gateModel": { "kind": "door", "wcid": 1006850, "scale": 1.0, "panels": 1, "residentGuid": null },
                  "shuffleSpots": [], "toolVersion": "puzzlesites/2"
                }
                """).AsObject();
            o["id"] = id;
            if (withLayout)
                o["layout"] = layout;
            return o;
        }

        private static ThreadDungeonStore Build(params JsonObject[] sites)
        {
            var arr = new JsonArray();
            foreach (var s in sites) arr.Add(s);
            var json = new JsonObject { ["version"] = 1, ["dungeons"] = new JsonObject { ["filos_doom"] = new JsonObject { ["sites"] = arr } } }.ToJsonString();
            return ThreadDungeonStore.Parse(Index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[]}",
                new Dictionary<string, string> { ["0x0150"] = SpawnFile }, puzzleGatesJson: json);
        }

        [TestMethod]
        public void Loader_absent_layout_means_the_admin_defaults()
        {
            var s = Build(Site("a")).GetPuzzleSites("filos_doom").Single();
            Assert.IsTrue(s.Layout.IsDefault);
        }

        [TestMethod]
        public void Loader_reads_a_full_and_a_partial_layout()
        {
            var store = Build(
                Site("full", JsonNode.Parse("{\"gateDistance\":12.5,\"lightHeight\":2.4,\"indicatorHeight\":2.6,\"hubHeight\":2.9}"), true),
                Site("part", JsonNode.Parse("{\"gateDistance\":5}"), true));
            var sites = store.GetPuzzleSites("filos_doom");
            Assert.AreEqual(2, sites.Count, string.Join("\n", store.Diagnostics));

            var full = sites.Single(x => x.Id == "full").Layout;
            Assert.AreEqual(12.5f, full.GateDistance);
            Assert.AreEqual(2.4f, full.LightHeight);
            Assert.AreEqual(2.6f, full.IndicatorHeight);
            Assert.AreEqual(2.9f, full.HubHeight);

            var part = sites.Single(x => x.Id == "part").Layout;
            Assert.AreEqual(5f, part.GateDistance);
            Assert.AreEqual(PuzzleGateTunables.HubHeight, part.HubHeight, "unspecified fields keep the defaults");
        }

        [TestMethod]
        public void Loader_drops_only_the_site_with_an_out_of_range_layout_and_names_the_field()
        {
            foreach (var (json, field) in new[]
            {
                ("{\"gateDistance\":3}", "gateDistance"),
                ("{\"gateDistance\":15}", "gateDistance"),
                ("{\"lightHeight\":1}", "lightHeight"),
                ("{\"indicatorHeight\":9}", "indicatorHeight"),
                ("{\"hubHeight\":2}", "hubHeight"),
            })
            {
                var store = Build(Site("bad", JsonNode.Parse(json), true), Site("good"));
                var sites = store.GetPuzzleSites("filos_doom");
                Assert.AreEqual(1, sites.Count, json + "\n" + string.Join("\n", store.Diagnostics));
                Assert.AreEqual("good", sites[0].Id);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("'bad'") && d.Contains(field)), string.Join("\n", store.Diagnostics));
            }
        }

        [TestMethod]
        public void Loader_a_wrongly_typed_layout_costs_only_its_site()
        {
            var store = Build(Site("bad", JsonNode.Parse("\"deep\""), true), Site("good"));
            Assert.AreEqual(1, store.GetPuzzleSites("filos_doom").Count);
        }
    }
}

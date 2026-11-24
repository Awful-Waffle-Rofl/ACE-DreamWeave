using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The shrine encounter type itself: its config (defaults, bad configs refused, bad rows skipped, magnitudes clamped
    /// to the cap), the guardian plan (count in range, trash only, only the shrine's landblock), the shipped
    /// encounters.json row, a Place that refuses cleanly, and OnRunEnded releasing the run's blessings. Placement into a
    /// live world (EnterWorld) needs a loaded landblock and is left to the in-game checks. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ShrineEncounterTypeTests
    {
        private static JsonElement Json(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        private static ShrineConfig Parse(string text, out string error, List<string> problems = null, double cap = 1.25)
            => ShrineEncounterType.ParseConfig(text == null ? default : Json(text), cap, problems ?? new List<string>(), out error);

        // ---- config ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_missing_or_empty_config_takes_the_defaults()
        {
            foreach (var text in new[] { null, "{}" })
            {
                var config = Parse(text, out var error);

                Assert.IsNull(error, text);
                Assert.AreEqual(ShrineEncounterType.DefaultWcid, config.Wcid);
                Assert.AreEqual(1007950u, config.Wcid);
                Assert.AreEqual(5, config.AddsMin);
                Assert.AreEqual(8, config.AddsMax);
                Assert.AreEqual(6, config.Boons.Count);
                Assert.IsTrue(config.Boons.All(b => Math.Abs(b.Magnitude - 1.25) < 1e-12 && b.Weight == 1.0));
            }
        }

        [TestMethod]
        public void A_bad_config_is_refused_with_a_reason()
        {
            foreach (var text in new[]
            {
                "5",
                "{\"wcid\": -1}",
                "{\"wcid\": \"shrine\"}",
                "{\"wcid\": 0}",
                "{\"adds\": {\"min\": 4, \"max\": 2}}",
                "{\"adds\": {\"min\": -1, \"max\": 2}}",
                "{\"adds\": {\"min\": 1, \"max\": 99}}",
                "{\"adds\": 3}",
                "{\"boons\": {}}",
                "{\"boons\": []}",
                "{\"boons\": [{\"kind\": \"speed\"}]}",
            })
            {
                Assert.IsNull(Parse(text, out var error), text);
                Assert.IsFalse(string.IsNullOrEmpty(error), text);
            }
        }

        [TestMethod]
        public void A_bad_boon_row_costs_only_itself_and_an_over_cap_magnitude_is_clamped()
        {
            var problems = new List<string>();
            var config = Parse("{\"boons\": [" +
                               "{\"kind\": \"runSpeed\", \"magnitude\": 1.25}," +
                               "{\"kind\": \"damage\", \"magnitude\": 1.5, \"weight\": 2}," +
                               "{\"kind\": \"xp\", \"magnitude\": 0.9}," +
                               "{\"kind\": \"loot\", \"weight\": 0}," +
                               "{\"kind\": \"damage\", \"magnitude\": 1.1}," +
                               "{\"kind\": \"attackSpeed\"}]}", out var error, problems);

            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { ThreadBoonKind.Damage, ThreadBoonKind.AttackSpeed }, config.Boons.Select(b => b.Kind).ToList());
            Assert.AreEqual(1.25, config.Boons[0].Magnitude, 1e-12, "1.5 clamped to the cap");
            Assert.AreEqual(2.0, config.Boons[0].Weight);
            Assert.AreEqual(1.25, config.Boons[1].Magnitude, 1e-12, "an absent magnitude is the cap");
            Assert.AreEqual(5, problems.Count, string.Join("\n", problems)); // runSpeed, the clamp note, xp, loot weight, repeated damage
        }

        [TestMethod]
        public void The_shipped_row_parses_to_the_shrine_with_all_six_blessings()
        {
            var text = File.ReadAllText(Path.Combine(ThreadDungeonStoreTests.FindDynamicDir(), ThreadEncounterStore.FileName));
            var diagnostics = new List<string>();
            var store = ThreadEncounterStore.Parse(text, null, diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));

            var row = store.Types.Single(t => t.Kind == ShrineEncounterType.KindName);
            Assert.IsTrue(row.Enabled);
            Assert.AreEqual(1.0, row.Weight);
            Assert.AreEqual(1.5f, row.Placement.MinClearance);
            Assert.AreEqual(20f, row.Placement.MinEntryWalk);
            Assert.AreEqual(15f, row.Placement.MinBossDist);
            Assert.AreEqual(25f, row.Placement.MinSpacing);
            Assert.IsTrue(row.Placement.AllowsRunKind(false) && row.Placement.AllowsRunKind(true), "dungeon and outdoor");

            var problems = new List<string>();
            var config = ShrineEncounterType.ParseConfig(row.Config, ThreadDungeonRun.DefaultShrineBoonCap, problems, out var error);

            Assert.IsNull(error);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.AreEqual(1007950u, config.Wcid);
            Assert.AreEqual((5, 8), (config.AddsMin, config.AddsMax));
            CollectionAssert.AreEquivalent(Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>().ToList(), config.Boons.Select(b => b.Kind).ToList());
            Assert.IsTrue(config.Boons.All(b => Math.Abs(b.Magnitude - 1.25) < 1e-12 && b.Weight == 1.0));
        }

        // ---- guardians ----------------------------------------------------------------------------------------------

        private static List<DungeonSpawnPlanEntry> Plan(params (uint Wcid, DungeonRole Role)[] entries)
            => entries.Select(e => new DungeonSpawnPlanEntry(e.Wcid, e.Role, Point(DungeonCell(), 0, 0), upliftLevel: 0, stampLevel: 77)).ToList();

        [TestMethod]
        public void The_guardian_count_stays_in_range_and_only_trash_is_drawn()
        {
            var config = new ShrineConfig { AddsMin = 3, AddsMax = 5, Boons = AllKinds() };
            var plan = Plan((100, DungeonRole.Trash), (200, DungeonRole.Elite), (300, DungeonRole.Boss), (101, DungeonRole.Trash));
            var shrine = Point(DungeonCell(), 50, 50);
            var nearby = new[] { Point(DungeonCell(), 55, 50), Point(DungeonCell(), 45, 50) };
            var counts = new HashSet<int>();

            for (var seed = 0; seed < 500; seed++)
            {
                var guardians = ShrineEncounterType.PlanGuardians(config, plan, nearby, shrine, new Random(seed));

                Assert.IsTrue(guardians.Count >= 3 && guardians.Count <= 5, $"seed {seed}: {guardians.Count}");
                Assert.IsTrue(guardians.All(g => g.Role == DungeonRole.Trash && (g.Wcid == 100 || g.Wcid == 101)), $"seed {seed}");
                Assert.IsTrue(guardians.All(g => g.StampLevel == 77), "the run's stamp level is carried, so they are shaped as the run's trash");
                counts.Add(guardians.Count);
            }

            CollectionAssert.AreEquivalent(new[] { 3, 4, 5 }, counts.ToList(), "every count in range is reachable");
            Assert.AreEqual(0, ShrineEncounterType.PlanGuardians(config, Plan((200, DungeonRole.Elite), (300, DungeonRole.Boss)), nearby, shrine, new Random(1)).Count, "no trash, no guardians");
        }

        [TestMethod]
        public void Guardians_stand_only_on_the_shrines_landblock_reusing_the_nearest_points()
        {
            var config = new ShrineConfig { AddsMin = 5, AddsMax = 5, Boons = AllKinds() };
            var plan = Plan((100, DungeonRole.Trash));
            var shrine = Point(OutdoorCell(0x5456, 100, 100), 100, 100, kind: "outdoor");
            var near = Point(OutdoorCell(0x5456, 110, 100), 110, 100, kind: "outdoor");
            var nearer = Point(OutdoorCell(0x5456, 104, 100), 104, 100, kind: "outdoor");
            var otherBlock = Point(OutdoorCell(0x5556, 2, 100), 2, 100, kind: "outdoor");

            var guardians = ShrineEncounterType.PlanGuardians(config, plan, new[] { nearer, otherBlock, near }, shrine, new Random(3));

            Assert.AreEqual(5, guardians.Count);
            Assert.IsTrue(guardians.All(g => (ushort)(g.Point.Cell >> 16) == 0x5456), "never another landblock's queue");
            CollectionAssert.AreEqual(new[] { nearer, near, nearer, near, nearer }, guardians.Select(g => g.Point).ToList(), "nearest first, then reused in order");

            var alone = ShrineEncounterType.PlanGuardians(config, plan, new[] { otherBlock }, shrine, new Random(3));
            Assert.IsTrue(alone.All(g => ReferenceEquals(g.Point, shrine)), "no usable nearby point: the shrine's own point");
        }

        // ---- Place / OnRunEnded -------------------------------------------------------------------------------------

        [TestMethod]
        public void Place_refuses_cleanly_on_a_bad_config_or_a_missing_weenie()
        {
            var saved = ShrineEncounterType.CreateObject;

            try
            {
                var created = 0;
                ShrineEncounterType.CreateObject = _ => { created++; return null; };

                foreach (var (config, fragment) in new[] { ("{\"wcid\": 0}", "wcid"), ("{}", "did not create") })
                {
                    var fake = new ShrineEncounterType();
                    using var seams = new Seams(Store(), fake);
                    var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0), Point(DungeonCell(), 60, 0) }, max: 1);

                    var def = Type("shrine", kind: ShrineEncounterType.KindName);
                    def.Config = Json(config);

                    var pick = new ThreadEncounterPick(1, def, Candidate(Point(DungeonCell(), 0, 0)), 7);
                    var placed = ThreadEncounterPass.PlaceOne(run, null, pick, fake);

                    Assert.IsNull(placed, config);
                    Assert.AreEqual(0, run.PlacedEncounters.Count, config);
                    Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains(fragment)), config + ": " + string.Join("\n", run.EncounterOutcomes));
                }

                Assert.AreEqual(1, created, "a bad config never reaches the factory");
            }
            finally
            {
                ShrineEncounterType.CreateObject = saved;
            }
        }

        [TestMethod]
        public void A_cap_of_one_places_no_shrine_and_grants_nothing()
        {
            Assert.IsFalse(ShrineEncounterType.CapAllowsBlessings(1.0));
            Assert.IsFalse(ShrineEncounterType.CapAllowsBlessings(0.5), "clamped up to 1.0");
            Assert.IsTrue(ShrineEncounterType.CapAllowsBlessings(1.25));
            Assert.IsTrue(ShrineEncounterType.CapAllowsBlessings(double.NaN), "NaN reads as the default");

            var saved = ShrineEncounterType.CreateObject;

            try
            {
                var created = 0;
                ShrineEncounterType.CreateObject = _ => { created++; return null; };

                var fake = new ShrineEncounterType();
                using var seams = new Seams(Store(), fake);
                var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0), Point(DungeonCell(), 60, 0) }, max: 1);
                run.ShrineBoonCap = 1.0;

                var def = Type("shrine", kind: ShrineEncounterType.KindName);
                def.Config = Json("{}");

                var placed = ThreadEncounterPass.PlaceOne(run, null, new ThreadEncounterPick(1, def, Candidate(Point(DungeonCell(), 0, 0)), 7), fake);

                Assert.IsNull(placed);
                Assert.AreEqual(0, created, "refused before the factory");
                Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("no blessing above 1.0 to grant")), string.Join("\n", run.EncounterOutcomes));
            }
            finally
            {
                ShrineEncounterType.CreateObject = saved;
            }

            // The ledger agrees even if a shrine were somehow used under that cap: nothing above 1.0 is left to give.
            using var scope = new Releaser();
            var capped = scope.Track(Run());
            capped.ShrineBoonCap = 1.0;

            Assert.AreEqual(ShrineUseOutcome.Silent, Use(capped, AllKinds(), new[] { Owner }).Outcome);
            Assert.AreEqual(0, capped.GrantedBoonKinds.Count);
            Assert.IsFalse(capped.HoldsAnyBoon);
        }

        [TestMethod]
        public void OnRunEnded_releases_the_runs_blessings_and_the_request()
        {
            var run = Run();

            try
            {
                Use(run, Only(ThreadBoonKind.Damage), new[] { Owner });
                Assert.IsTrue(run.HoldsAnyBoon);

                var type = new ShrineEncounterType();
                var pick = new ThreadEncounterPick(1, Type("shrine", kind: ShrineEncounterType.KindName), Candidate(Point(DungeonCell(), 0, 0)), 7);
                var instance = run.NewEncounterInstance(pick, type);
                var state = new ShrineEncounterType.ShrineState(new ThreadEncounterPlaceRequest(run, null, instance, null), new ShrineConfig { Boons = AllKinds() });
                instance.State = state;

                type.OnRunEnded(instance, "test");
                type.OnRunEnded(instance, "again"); // a second shrine of the same run

                Assert.IsNull(state.Request);
                Assert.IsFalse(run.HoldsAnyBoon);
                Assert.AreEqual(1.0, run.BoonMagnitude(Owner, ThreadBoonKind.Damage));
            }
            finally
            {
                run.ReleaseBoons();
            }
        }

        [TestMethod]
        public void The_type_is_registered_by_the_manager_and_is_usable()
        {
            var type = new ShrineEncounterType();
            Assert.AreEqual("shrine", type.Kind);
            Assert.IsInstanceOfType(type, typeof(IThreadEncounterUsable));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var init = PooledLootSourceText.MethodBody(src, "public static void Initialize()");
            Assert.IsTrue(init.IndexOf("ThreadEncounterTypes.Register(new ShrineEncounterType());", StringComparison.Ordinal) < init.IndexOf("Store = store;", StringComparison.Ordinal));

            // The cap is read once by TryStart and stamped on the run.
            StringAssert.Contains(src, "var shrineBoonCap = ThreadDungeonRun.ClampShrineBoonCap(PropertyManager.GetDouble(\"dynamic_dungeons_shrine_boon_cap\", ThreadDungeonRun.DefaultShrineBoonCap).Item);");
            StringAssert.Contains(src, "ShrineBoonCap = shrineBoonCap,");
        }
    }
}

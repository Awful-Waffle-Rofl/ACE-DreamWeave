using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Defense encounter type's pure parts: its config (defaults, bad configs refused, bad wcid rows skipped), the
    /// portal pick and spread, the stream plan, the switch rule, the outcome table, the health milestones, the random
    /// pillar look, the shipped encounters.json row, and a Place that refuses cleanly. Placement into a live world
    /// (EnterWorld, the ActionChains) needs a loaded landblock and is left to the in-game checks. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class DefenseEncounterTypeTests
    {
        private static JsonElement Json(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        private static DefenseConfig Parse(string text, out string error, List<string> problems = null)
            => DefenseEncounterType.ParseConfig(text == null ? default : Json(text), problems ?? new List<string>(), out error);

        // ---- config ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_missing_or_empty_config_takes_the_defaults()
        {
            foreach (var text in new[] { null, "{}" })
            {
                var config = Parse(text, out var error);

                Assert.IsNull(error, text);
                CollectionAssert.AreEqual(new uint[] { 1007951, 1007952, 1007953, 1007954, 1007955 }, config.Wcids.ToList());
                Assert.AreEqual((1, 3), (config.PortalsMin, config.PortalsMax));
                Assert.AreEqual((3, 5), (config.PerPortalMin, config.PerPortalMax));
                Assert.AreEqual((2.0, 4.0), (config.StaggerMin, config.StaggerMax));
                Assert.AreEqual((8f, 25f), (config.PortalMinDistance, config.PortalMaxDistance));
                Assert.AreEqual(0.3, config.PortalScale(false), 1e-12);
                Assert.AreEqual(0.6, config.PortalScale(true), 1e-12);
            }
        }

        [TestMethod]
        public void A_full_config_parses_every_key()
        {
            var config = Parse("{\"wcids\": [7, 8], \"portals\": {\"min\": 2, \"max\": 4}, \"perPortal\": {\"min\": 1, \"max\": 2}," +
                               "\"staggerSeconds\": {\"min\": 0.5, \"max\": 1}, \"portalMinDistance\": 4, \"portalMaxDistance\": 30," +
                               "\"portalScale\": {\"dungeon\": 0.25, \"outdoor\": 1.2}}", out var error);

            Assert.IsNull(error);
            CollectionAssert.AreEqual(new uint[] { 7, 8 }, config.Wcids.ToList());
            Assert.AreEqual((2, 4), (config.PortalsMin, config.PortalsMax));
            Assert.AreEqual((1, 2), (config.PerPortalMin, config.PerPortalMax));
            Assert.AreEqual((0.5, 1.0), (config.StaggerMin, config.StaggerMax));
            Assert.AreEqual((4f, 30f), (config.PortalMinDistance, config.PortalMaxDistance));
            Assert.AreEqual(0.25, config.PortalScaleDungeon, 1e-12);
            Assert.AreEqual(1.2, config.PortalScaleOutdoor, 1e-12);
        }

        [TestMethod]
        public void A_bad_config_is_refused_with_a_reason()
        {
            foreach (var text in new[]
            {
                "5",
                "{\"wcids\": 1007951}",
                "{\"wcids\": []}",
                "{\"wcids\": [0, -1, \"x\"]}",
                "{\"portals\": {\"min\": 0, \"max\": 2}}",
                "{\"portals\": {\"min\": 3, \"max\": 2}}",
                "{\"portals\": {\"min\": 1, \"max\": 7}}",
                "{\"portals\": 2}",
                "{\"perPortal\": {\"min\": 1, \"max\": 13}}",
                "{\"staggerSeconds\": {\"min\": -1, \"max\": 2}}",
                "{\"staggerSeconds\": {\"min\": 3, \"max\": 2}}",
                "{\"staggerSeconds\": {\"min\": 1, \"max\": 31}}",
                "{\"portalMinDistance\": 25, \"portalMaxDistance\": 8}",
                "{\"portalMaxDistance\": 61}",
                "{\"portalMinDistance\": \"far\"}",
                "{\"portalScale\": 0.5}",
                "{\"portalScale\": {\"dungeon\": 0}}",
                "{\"portalScale\": {\"outdoor\": 2.5}}",
            })
            {
                Assert.IsNull(Parse(text, out var error), text);
                Assert.IsFalse(string.IsNullOrEmpty(error), text);
            }
        }

        [TestMethod]
        public void A_bad_wcid_row_costs_only_itself()
        {
            var problems = new List<string>();
            var config = Parse("{\"wcids\": [1007951, 0, \"x\", 1007951, 1007953]}", out var error, problems);

            Assert.IsNull(error);
            CollectionAssert.AreEqual(new uint[] { 1007951, 1007953 }, config.Wcids.ToList());
            Assert.AreEqual(3, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void The_retired_showHealthBar_key_is_ignored_with_a_note_never_refused()
        {
            foreach (var value in new[] { "true", "false", "1" })
            {
                var problems = new List<string>();
                var config = Parse("{\"showHealthBar\": " + value + "}", out var error, problems);

                Assert.IsNull(error, value);
                Assert.IsNotNull(config, value);
                CollectionAssert.AreEqual(new[] { DefenseEncounterType.RetiredShowHealthBarNote }, problems, value);
            }

            var clean = new List<string>();
            Parse("{}", out _, clean);
            Assert.AreEqual(0, clean.Count, "positive control: no note without the key");
        }

        [TestMethod]
        public void The_shipped_row_is_open_to_the_slot_roll_and_parses_without_a_problem()
        {
            var text = File.ReadAllText(Path.Combine(ThreadDungeonStoreTests.FindDynamicDir(), ThreadEncounterStore.FileName));
            var diagnostics = new List<string>();
            var store = ThreadEncounterStore.Parse(text, null, diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));

            var row = store.Types.Single(t => t.Kind == DefenseEncounterType.KindName);
            Assert.AreEqual("defense", row.Id);
            Assert.IsTrue(row.Enabled);
            Assert.AreEqual(1.0, row.Weight, "open to the slot roll (owner ruling 2026-11-11)");
            Assert.AreEqual(3f, row.Placement.MinClearance);
            Assert.AreEqual(30f, row.Placement.MinEntryWalk);
            Assert.AreEqual(30f, row.Placement.MinBossDist);
            Assert.AreEqual(30f, row.Placement.MinSpacing);
            Assert.IsTrue(row.Placement.AllowsRunKind(false) && row.Placement.AllowsRunKind(true), "dungeon and outdoor");

            var problems = new List<string>();
            var config = DefenseEncounterType.ParseConfig(row.Config, problems, out var error);

            Assert.IsNull(error);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            CollectionAssert.AreEqual(DefenseEncounterType.DefaultWcids.ToList(), config.Wcids.ToList());
            Assert.AreEqual((1, 3, 3, 5), (config.PortalsMin, config.PortalsMax, config.PerPortalMin, config.PerPortalMax));
            Assert.AreEqual((2.0, 4.0), (config.StaggerMin, config.StaggerMax));
            Assert.AreEqual((8f, 25f), (config.PortalMinDistance, config.PortalMaxDistance));
            Assert.IsFalse(row.Config.TryGetProperty("showHealthBar", out _), "the retired key is gone from the shipped row");
        }

        // ---- the look ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_random_variant_pick_reaches_all_five_looks()
        {
            var seen = new HashSet<uint>();

            for (var seed = 0; seed < 200; seed++)
            {
                var wcid = DefenseEncounterType.PickVariant(DefenseEncounterType.DefaultWcids, new Random(seed));
                Assert.IsTrue(DefenseEncounterType.DefaultWcids.Contains(wcid), $"seed {seed}: {wcid}");
                seen.Add(wcid);
            }

            CollectionAssert.AreEquivalent(DefenseEncounterType.DefaultWcids.ToList(), seen.ToList(), "every look is reachable");
            Assert.AreEqual(0u, DefenseEncounterType.PickVariant(Array.Empty<uint>(), new Random(1)));
        }

        // ---- portals -----------------------------------------------------------------------------------------------

        private static PortalWaveCandidate At(float x, float y, float? walk = null, uint? cell = null)
            => new PortalWaveCandidate(Point(cell ?? DungeonCell(), x, y), new Vector3(x, y, 0f), walk);

        private static readonly Vector3 Pillar = new Vector3(50f, 50f, 0f);

        [TestMethod]
        public void Portal_points_must_be_in_the_distance_band_on_the_pillars_landblock_and_walkable()
        {
            var config = new DefenseConfig { PortalsMin = 2, PortalsMax = 6 };
            var inBand = At(60f, 50f);                                       // 10 m
            var tooNear = At(54f, 50f);                                      // 4 m
            var tooFar = At(80f, 50f);                                       // 30 m
            var behindWall = At(50f, 62f, walk: 40f);                         // 12 m line, 40 m walk
            var walkOk = At(40f, 50f, walk: 14f);                             // 10 m line, 14 m walk
            var otherBlock = At(50f, 40f, cell: ((uint)0x0151 << 16) | 0x0100u);

            var picked = DefenseEncounterType.PickPortals(Pillar, DungeonLb, new[] { inBand, tooNear, tooFar, behindWall, walkOk, otherBlock },
                false, config, new Random(1), out var refusal);

            Assert.IsNull(refusal);
            CollectionAssert.AreEquivalent(new[] { inBand.Point, walkOk.Point }, picked);

            // Outdoors the walk distance is not consulted.
            var outdoor = DefenseEncounterType.PickPortals(Pillar, DungeonLb, new[] { behindWall }, true, new DefenseConfig(), new Random(1), out refusal);
            Assert.IsNull(refusal);
            Assert.AreEqual(1, outdoor.Count);
        }

        [TestMethod]
        public void Too_few_portal_points_refuse_with_a_reason()
        {
            var config = new DefenseConfig { PortalsMin = 2, PortalsMax = 3 };

            Assert.IsNull(DefenseEncounterType.PickPortals(Pillar, DungeonLb, new[] { At(60f, 50f) }, false, config, new Random(1), out var refusal));
            StringAssert.Contains(refusal, "2 needed");

            Assert.IsNull(DefenseEncounterType.PickPortals(Pillar, DungeonLb, Array.Empty<PortalWaveCandidate>(), false, new DefenseConfig(), new Random(1), out refusal));
            StringAssert.Contains(refusal, "0 portal point(s)");
        }

        [TestMethod]
        public void Portals_spread_by_greatest_angle_and_the_count_stays_in_range()
        {
            // Eight points on a 15 m ring, one every 45 degrees (north first, clockwise).
            var ring = Enumerable.Range(0, 8).Select(i =>
            {
                var a = i * Math.PI / 4;
                return At(50f + 15f * (float)Math.Sin(a), 50f + 15f * (float)Math.Cos(a));
            }).ToList();

            var counts = new HashSet<int>();

            for (var seed = 0; seed < 300; seed++)
            {
                var picked = DefenseEncounterType.PickPortals(Pillar, DungeonLb, ring, false, new DefenseConfig { PortalsMin = 1, PortalsMax = 3 }, new Random(seed), out _);
                counts.Add(picked.Count);
                Assert.AreEqual(picked.Count, picked.Distinct().Count(), "never the same point twice");

                if (picked.Count >= 2)
                {
                    var bearings = picked.Select(p => ThreadEncounterPortalWaves.Bearing(Pillar, new Vector3(p.X, p.Y, 0f))).ToList();
                    var minGap = bearings.SelectMany((a, i) => bearings.Skip(i + 1).Select(b => ThreadEncounterPortalWaves.AngleBetween(a, b))).Min();

                    // Two portals stand opposite; three are at least 90 degrees apart (the best an 8-point ring allows is 135/135/90).
                    var expected = picked.Count == 2 ? Math.PI : Math.PI / 2;
                    Assert.IsTrue(minGap >= expected - 1e-6, $"seed {seed}: {picked.Count} portals, closest pair {minGap * 180 / Math.PI:0} degrees");
                }
            }

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, counts.ToList(), "every count in range is reachable");

            // Never more portals than points qualify.
            var two = DefenseEncounterType.PickPortals(Pillar, DungeonLb, ring.Take(2).ToList(), false, new DefenseConfig { PortalsMin = 1, PortalsMax = 3 }, new Random(5), out _);
            Assert.IsTrue(two.Count <= 2);
        }

        [TestMethod]
        public void AngleBetween_wraps()
        {
            Assert.AreEqual(Math.PI / 2, ThreadEncounterPortalWaves.AngleBetween(0.0, Math.PI / 2), 1e-12);
            Assert.AreEqual(Math.PI / 4, ThreadEncounterPortalWaves.AngleBetween(-Math.PI / 8, Math.PI / 8 + 2 * Math.PI), 1e-12);
            Assert.AreEqual(0.2, ThreadEncounterPortalWaves.AngleBetween(Math.PI - 0.1, -Math.PI + 0.1), 1e-12);
            Assert.AreEqual(0.0, ThreadEncounterPortalWaves.Bearing(Pillar, new Vector3(50f, 60f, 0f)), 1e-6, "north is bearing 0");
            Assert.AreEqual(Math.PI / 2, ThreadEncounterPortalWaves.Bearing(Pillar, new Vector3(60f, 50f, 0f)), 1e-6, "east is a quarter turn clockwise");
        }

        // ---- stream ------------------------------------------------------------------------------------------------

        private static List<DungeonSpawnPlanEntry> Plan(params (uint Wcid, DungeonRole Role)[] entries)
            => entries.Select(e => new DungeonSpawnPlanEntry(e.Wcid, e.Role, Point(DungeonCell(), 0, 0), upliftLevel: 0, stampLevel: 66)).ToList();

        [TestMethod]
        public void The_stream_sends_three_to_five_trash_per_portal_with_rising_delays()
        {
            var portals = new[] { Point(DungeonCell(), 60, 50), Point(DungeonCell(), 40, 50), Point(DungeonCell(), 50, 65) };
            var plan = Plan((100, DungeonRole.Trash), (200, DungeonRole.Elite), (300, DungeonRole.Boss), (101, DungeonRole.Trash));
            var config = new DefenseConfig();
            var perPortalCounts = new HashSet<int>();

            for (var seed = 0; seed < 300; seed++)
            {
                var stream = DefenseEncounterType.PlanStream(portals, plan, config, new Random(seed));

                for (var i = 1; i < stream.Count; i++)
                    Assert.IsTrue(stream[i].Delay >= stream[i - 1].Delay, $"seed {seed}: the list is in delay order");

                for (var p = 0; p < portals.Length; p++)
                {
                    var mine = stream.Where(s => s.Portal == p).ToList();
                    perPortalCounts.Add(mine.Count);

                    Assert.IsTrue(mine.Count >= 3 && mine.Count <= 5, $"seed {seed} portal {p}: {mine.Count}");
                    Assert.AreEqual(DefenseEncounterType.FirstSpawnDelaySeconds, mine[0].Delay, 1e-12, "the first comes once the portals are open");

                    for (var k = 1; k < mine.Count; k++)
                    {
                        var gap = mine[k].Delay - mine[k - 1].Delay;
                        Assert.IsTrue(gap >= 2.0 - 1e-9 && gap <= 4.0 + 1e-9, $"seed {seed} portal {p}: stagger {gap}");
                    }

                    Assert.IsTrue(mine.All(s => ReferenceEquals(s.Entry.Point, portals[p])), "each stands on its own portal's point");
                }

                Assert.IsTrue(stream.All(s => s.Entry.Role == DungeonRole.Trash && (s.Entry.Wcid == 100 || s.Entry.Wcid == 101)), $"seed {seed}: trash only");
                Assert.IsTrue(stream.All(s => s.Entry.StampLevel == 66), "shaped as the run's own trash");
            }

            CollectionAssert.AreEquivalent(new[] { 3, 4, 5 }, perPortalCounts.ToList(), "every count in range is reachable");
            Assert.AreEqual(0, DefenseEncounterType.PlanStream(portals, Plan((200, DungeonRole.Elite)), config, new Random(1)).Count, "no trash, no stream");
        }

        // ---- switch, outcome, milestones ---------------------------------------------------------------------------

        [TestMethod]
        public void An_enemy_switches_only_after_switch_seconds_only_once_and_only_while_active()
        {
            Assert.IsFalse(DefenseEncounterType.ShouldSwitch(true, false, false, 100.0, 119.9, 20.0), "before switch_seconds");
            Assert.IsTrue(DefenseEncounterType.ShouldSwitch(true, false, false, 100.0, 120.0, 20.0), "at switch_seconds");
            Assert.IsTrue(DefenseEncounterType.ShouldSwitch(true, false, false, 100.0, 500.0, 20.0), "long after");
            Assert.IsFalse(DefenseEncounterType.ShouldSwitch(false, false, false, 100.0, 500.0, 20.0), "the defense is over");
            Assert.IsFalse(DefenseEncounterType.ShouldSwitch(true, true, false, 100.0, 500.0, 20.0), "already asked");
            Assert.IsFalse(DefenseEncounterType.ShouldSwitch(true, false, true, 100.0, 500.0, 20.0), "dead");
            Assert.IsTrue(DefenseEncounterType.ShouldSwitch(true, false, false, 100.0, 100.0, 0.0), "0 sends every enemy at once");
            Assert.IsFalse(DefenseEncounterType.ShouldSwitch(true, false, false, 100.0, double.NaN, 0.0), "a broken clock never switches");
        }

        [TestMethod]
        public void The_outcome_table()
        {
            // pillar standing, stream done, enemies alive, elapsed, max
            Assert.AreEqual(DefenseOutcome.Failure, DefenseEncounterType.Decide(false, true, 0, 10, 300), "the pillar down is a failure even with every enemy dead");
            Assert.AreEqual(DefenseOutcome.Failure, DefenseEncounterType.Decide(false, false, 4, 400, 300), "failure before timeout");
            Assert.AreEqual(DefenseOutcome.Success, DefenseEncounterType.Decide(true, true, 0, 10, 300));
            Assert.AreEqual(DefenseOutcome.Success, DefenseEncounterType.Decide(true, true, 0, 400, 300), "a clean sweep is a success, not a timeout");
            Assert.AreEqual(DefenseOutcome.None, DefenseEncounterType.Decide(true, false, 0, 10, 300), "every one so far is dead but more are coming");
            Assert.AreEqual(DefenseOutcome.None, DefenseEncounterType.Decide(true, true, 2, 10, 300));
            Assert.AreEqual(DefenseOutcome.Timeout, DefenseEncounterType.Decide(true, true, 2, 300, 300), "still standing at max_seconds");
            Assert.AreEqual(DefenseOutcome.Timeout, DefenseEncounterType.Decide(true, false, 0, 301, 300));
        }

        [TestMethod]
        public void Milestones_fall_in_order_and_a_big_drop_says_only_the_lower_one()
        {
            Assert.AreEqual(0, DefenseEncounterType.NextMilestone(100, 0.80));
            Assert.AreEqual(75, DefenseEncounterType.NextMilestone(100, 0.75));
            Assert.AreEqual(0, DefenseEncounterType.NextMilestone(75, 0.70), "75 was said");
            Assert.AreEqual(50, DefenseEncounterType.NextMilestone(75, 0.49));
            Assert.AreEqual(25, DefenseEncounterType.NextMilestone(100, 0.20), "80 to 20 in one tick: only 25");
            Assert.AreEqual(0, DefenseEncounterType.NextMilestone(25, 0.01));
            Assert.AreEqual(0, DefenseEncounterType.NextMilestone(100, double.NaN));

            Assert.AreEqual("The pillar is at 75% strength.", DefenseEncounterType.MilestoneLine(75));
            Assert.AreEqual("The pillar is at 50% strength.", DefenseEncounterType.MilestoneLine(50));
            Assert.AreEqual("The pillar is at 25% strength.", DefenseEncounterType.MilestoneLine(25));
        }

        [TestMethod]
        public void The_player_lines_are_the_owners_text()
        {
            Assert.AreEqual("Only members of this thread can wake the pillar.", DefenseEncounterType.RefusedLine);
            Assert.AreEqual("The pillar hums awake and portals tear open around it. Defend the pillar!", DefenseEncounterType.StartLine);
            Assert.AreEqual("The pillar holds, and the portals collapse.", DefenseEncounterType.SuccessLine);
            Assert.AreEqual("The pillar shatters, and the portals collapse.", DefenseEncounterType.FailureLine);
        }

        // ---- Place ---------------------------------------------------------------------------------------------------

        private static ThreadDungeonRun RunWithPlan(IEnumerable<DungeonSpawnPointDef> points, IEnumerable<DungeonSpawnPlanEntry> entries)
        {
            var run = DungeonRun(points, max: 1, context: false);
            var plan = new DungeonSpawnPlan();
            plan.Entries.AddRange(entries);
            run.MarkEncounterContext(new ThreadEncounterRunContext(plan, 0u, null, new Dictionary<ushort, ACE.Server.Entity.Landblock>(), null));
            return run;
        }

        private static ThreadEncounterTypeDef Row(string config)
        {
            var def = Type("defense", kind: DefenseEncounterType.KindName);
            def.Config = Json(config);
            return def;
        }

        [TestMethod]
        public void Place_refuses_cleanly_and_never_reaches_the_factory_until_the_portals_fit()
        {
            var saved = DefenseEncounterType.CreateObject;

            try
            {
                var created = new List<uint>();
                DefenseEncounterType.CreateObject = wcid => { created.Add(wcid); return null; };

                var pillar = Point(DungeonCell(), 50, 50);
                var near = Point(DungeonCell(), 60, 50);
                var trash = Plan((100, DungeonRole.Trash));

                var cases = new (ThreadDungeonRun Run, string Config, string Fragment)[]
                {
                    (RunWithPlan(new[] { pillar, near }, trash), "{\"portals\": 3}", "config.portals"),
                    (DungeonRun(new[] { pillar, near }, max: 1), "{}", "no trash"),
                    (RunWithPlan(new[] { pillar, near }, Plan((200, DungeonRole.Elite))), "{}", "no trash"),
                    (RunWithPlan(new[] { pillar, Point(DungeonCell(), 52, 50) }, trash), "{}", "0 portal point(s)"),
                };

                foreach (var (run, config, fragment) in cases)
                {
                    var fake = new DefenseEncounterType();
                    using var seams = new Seams(Store(), fake);

                    var placed = ThreadEncounterPass.PlaceOne(run, null, new ThreadEncounterPick(1, Row(config), Candidate(pillar), 7), fake);

                    Assert.IsNull(placed, fragment);
                    Assert.AreEqual(0, run.PlacedEncounters.Count, fragment);
                    Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains(fragment)), fragment + ": " + string.Join("\n", run.EncounterOutcomes));
                }

                Assert.AreEqual(0, created.Count, "every refusal above is decided before the factory");

                // With a portal point in the band the pillar look is drawn and asked for.
                var fits = RunWithPlan(new[] { pillar, near }, trash);
                var type = new DefenseEncounterType();
                using (new Seams(Store(), type))
                    Assert.IsNull(ThreadEncounterPass.PlaceOne(fits, null, new ThreadEncounterPick(1, Row("{}"), Candidate(pillar), 7), type));

                Assert.AreEqual(1, created.Count);
                Assert.IsTrue(DefenseEncounterType.DefaultWcids.Contains(created[0]));
                Assert.IsTrue(fits.EncounterOutcomes.Any(l => l.Contains("did not create")), string.Join("\n", fits.EncounterOutcomes));
            }
            finally
            {
                DefenseEncounterType.CreateObject = saved;
            }
        }

        // ---- use: only the pillar, only once -------------------------------------------------------------------------

        private static DefenseEncounterType.DefenseState State(out ThreadDungeonRun run)
        {
            run = DungeonRun(new[] { Point(DungeonCell(), 0, 0) }, max: 1);
            var pick = new ThreadEncounterPick(1, Type("defense", kind: DefenseEncounterType.KindName), Candidate(Point(DungeonCell(), 0, 0)), 7);
            var instance = run.NewEncounterInstance(pick, new DefenseEncounterType());
            var view = new ACE.Server.WorldEvents.Hold.ThreadDefenseHoldView(ACE.Server.WorldEvents.Hold.ThreadDefenseHoldView.BuildTuning(240, 45));
            return new DefenseEncounterType.DefenseState(new ThreadEncounterPlaceRequest(run, null, instance, null), new DefenseConfig(), view, null);
        }

        [TestMethod]
        public void Only_a_use_of_the_pillar_itself_counts()
        {
            var state = State(out _);
            var pillar = HoldFixtures.MakeCreature("Ward");
            var portal = HoldFixtures.MakeCreature("Portal layer");
            state.PillarGuid = pillar.Guid.Full;

            Assert.IsTrue(DefenseEncounterType.IsThePillar(state, pillar));
            Assert.IsFalse(DefenseEncounterType.IsThePillar(state, portal), "a portal layer or an enemy is tracked too, but is not the pillar");
            Assert.IsFalse(DefenseEncounterType.IsThePillar(null, pillar), "no state");
            Assert.IsFalse(DefenseEncounterType.IsThePillar(state, null));

            state.PillarGuid = 0;
            Assert.IsFalse(DefenseEncounterType.IsThePillar(state, pillar), "an unset guid matches nothing");
        }

        [TestMethod]
        public void A_finished_defense_never_starts_again()
        {
            var fresh = State(out _);
            Assert.IsTrue(DefenseEncounterType.TryStart(fresh, 1.0), "control: an unused pillar starts");

            var finished = State(out _);
            finished.Finished = true;
            Assert.IsFalse(DefenseEncounterType.TryStart(finished, 1.0), "an outcome was reached: a late use does nothing");
            Assert.IsFalse(finished.Started);
            Assert.IsFalse(finished.View.IsActive, "the view stays inactive");
        }

        // ---- wake: the outdoor perimeter -------------------------------------------------------------------------------

        [TestMethod]
        public void An_unswitched_enemy_is_never_woken_at_a_member_past_its_perimeter()
        {
            const ushort lb = 0x5456;
            var perimeter = new OutdoorCreaturePerimeter(OutdoorSiteGeometry.ToGlobalX(lb, 100), OutdoorSiteGeometry.ToGlobalY(lb, 100), 30, false, null);

            ACE.Entity.Position At(float x, float y) => new ACE.Entity.Position(OutdoorCell(lb, x, y), x, y, 0f, 0f, 0f, 0f, 1f, 0x80001234u);

            var inside = At(110, 100);
            var outside = At(150, 100);

            Assert.IsTrue(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, false, false, true, true, inside));
            Assert.IsFalse(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, false, false, true, true, outside), "past the radius: the perimeter would send it home");
            Assert.IsTrue(ThreadEncounterPortalWaves.IsWakeCandidate(null, false, false, true, true, outside), "a dungeon enemy has no perimeter");

            Assert.IsFalse(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, true, false, true, true, inside), "dead");
            Assert.IsFalse(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, false, true, true, true, inside), "teleporting");
            Assert.IsFalse(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, false, false, false, true, inside), "not attackable");
            Assert.IsFalse(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, false, false, true, false, inside), "no physics object");
            Assert.IsFalse(ThreadEncounterPortalWaves.IsWakeCandidate(perimeter, false, false, true, true, null), "no position reads as outside");

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPortalWaves.cs");
            StringAssert.Contains(src, "if (!IsWakeCandidate(creature.P_OutdoorPerimeter, p.IsDead, p.Teleporting, p.Attackable, p.PhysicsObj != null, p.Location))");
        }

        // ---- engage: owner playtest ruling 2026-11-11 (spawned enemies engage at once and never idle) ------------------

        [TestMethod]
        public void Engage_prefers_a_member_and_falls_back_to_the_pillar_only_when_no_member_is_inside()
        {
            var woke = ThreadEncounterPortalWaves.WakeResult.Woke;
            var untargetable = ThreadEncounterPortalWaves.WakeResult.MemberUntargetable;
            var none = ThreadEncounterPortalWaves.WakeResult.NoMember;

            Assert.AreEqual(DefenseEncounterType.EngageChoice.Member, DefenseEncounterType.Engage(woke, false, true, true));
            Assert.AreEqual(DefenseEncounterType.EngageChoice.Member, DefenseEncounterType.Engage(woke, true, false, false), "a member target wins whatever else holds");

            Assert.AreEqual(DefenseEncounterType.EngageChoice.Pillar, DefenseEncounterType.Engage(none, false, true, true), "no member inside: it turns on the pillar instead of idling");

            // Review R1: a member who is there but not targetable this tick must NOT cost the enemy its players-first window.
            Assert.AreEqual(DefenseEncounterType.EngageChoice.None, DefenseEncounterType.Engage(untargetable, false, true, true), "a present member: stay idle and retry next tick");

            Assert.AreEqual(DefenseEncounterType.EngageChoice.None, DefenseEncounterType.Engage(none, true, true, true), "already switched");
            Assert.AreEqual(DefenseEncounterType.EngageChoice.None, DefenseEncounterType.Engage(none, false, false, true), "the defense is over");
            Assert.AreEqual(DefenseEncounterType.EngageChoice.None, DefenseEncounterType.Engage(none, false, true, false), "the pillar fell");
        }

        [TestMethod]
        public void A_wake_miss_is_NoMember_only_when_nobody_is_inside()
        {
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.Woke, ThreadEncounterPortalWaves.ClassifyWake(1, true));
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.NoMember, ThreadEncounterPortalWaves.ClassifyWake(0, false));
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.MemberUntargetable, ThreadEncounterPortalWaves.ClassifyWake(1, false), "a member past range, the perimeter or visibility");
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.MemberUntargetable, ThreadEncounterPortalWaves.ClassifyWake(4, false));

            // Every miss inside Wake goes through the classifier, and its guards never say NoMember.
            var src = WorldEventHooksCallSiteTests.StripComments(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPortalWaves.cs"));
            var wake = PooledLootSourceText.MethodBody(src, "private static WakeResult Wake(Creature creature, ThreadDungeonRun run)");
            Assert.IsFalse(wake.Contains("WakeResult.NoMember"), "only ClassifyWake may say NoMember");
            Assert.IsFalse(wake.Contains("return false;"), "no bare false miss left");
            Assert.AreEqual(3, System.Text.RegularExpressions.Regex.Matches(wake, @"return ClassifyWake\(members, (false|true)\);").Count, "both misses and the wake are classified");
            StringAssert.Contains(wake, "members++;");
        }
        [TestMethod]
        public void A_spawned_enemy_is_engaged_on_its_spawn_and_an_idle_one_on_every_tick()
        {
            var src = WorldEventHooksCallSiteTests.StripComments(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DefenseEncounterType.cs"));

            var spawn = PooledLootSourceText.MethodBody(src, "private static void SpawnEnemy(ThreadEncounterInstance instance, DefenseState state, PortalWaveSpawn spawn)");
            var added = spawn.IndexOf("state.Enemies.Add(added);", StringComparison.Ordinal);
            var engage = spawn.IndexOf("EngageEnemy(added, state, instance.Run);", StringComparison.Ordinal);
            Assert.IsTrue(added >= 0, "the placed enemy is recorded");
            Assert.IsTrue(engage > added, "and engaged right after, not a monitor second later");

            var tick = PooledLootSourceText.MethodBody(src, "private static void Tick(ThreadEncounterInstance instance, DefenseState state, ACE.Server.Entity.Landblock landblock)");
            StringAssert.Contains(tick, "OnEnemyThread(creature, landblock, () => EngageEnemy(idle, state, run));");

            var body = PooledLootSourceText.MethodBody(src, "private static void EngageEnemy(DefenseEnemy enemy, DefenseState state, ThreadDungeonRun run)");
            StringAssert.Contains(body, "if (!ThreadEncounterPortalWaves.EngageEnemy(creature, run, out var wake))");
            StringAssert.Contains(body, "var choice = Engage(wake, enemy.SwitchRequested || ThreadDefenseHold.IsTagged(creature), state.View.IsActive, IsStanding(state.Pillar));");
            StringAssert.Contains(body, "if (choice != EngageChoice.Pillar)");
            StringAssert.Contains(body, "ThreadDefenseHold.SwitchToSeeker(creature, state.View)");
        }

        // ---- the portal recipe ------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_portal_layers_are_the_World_Events_goals_json_recipe()
        {
            var content = Directory.GetParent(Directory.GetParent(ThreadDungeonStoreTests.FindDynamicDir()).FullName).FullName;
            var path = Path.Combine(content, "events", "axes", "goals.json");
            Assert.IsTrue(File.Exists(path), path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            var found = new List<JsonElement>();

            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name == "portals" && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("parts", out var parts))
                            found.Add(parts.Clone());

                        Walk(p.Value);
                    }
                }
                else if (e.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in e.EnumerateArray())
                        Walk(x);
                }
            }

            Walk(doc.RootElement);

            Assert.AreEqual(1, found.Count, "goals.json has exactly one portals.parts recipe");

            var rows = found[0].EnumerateArray().ToList();
            var mine = ThreadEncounterPortalWaves.PortalParts;

            Assert.AreEqual(rows.Count, mine.Count);

            float F(JsonElement row, string name) => row.TryGetProperty(name, out var v) ? (float)v.GetDouble() : 0f;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var part = mine[i];

                Assert.AreEqual(row.GetProperty("wcid").GetUInt32(), part.Wcid, $"layer {i} wcid");
                Assert.AreEqual(F(row, "scale"), part.Scale, 1e-6f, $"layer {i} scale");
                Assert.AreEqual(F(row, "speed"), part.Speed, 1e-6f, $"layer {i} speed");
                Assert.AreEqual(F(row, "dz"), part.Dz, 1e-6f, $"layer {i} dz");
                Assert.AreEqual(F(row, "dy"), part.Dy, 1e-6f, $"layer {i} dy");
                Assert.AreEqual(F(row, "dx"), part.Dx, 1e-6f, $"layer {i} dx");
                Assert.AreEqual(F(row, "pitch"), part.Pitch, 1e-6f, $"layer {i} pitch");
                Assert.AreEqual(F(row, "yaw"), part.Yaw, 1e-6f, $"layer {i} yaw");
                Assert.AreEqual(row.TryGetProperty("inverted", out var inv) && inv.GetBoolean(), part.Inverted, $"layer {i} inverted");
            }
        }

        [TestMethod]
        public void All_five_pillar_weenies_are_unattackable_with_the_npc_radar_colour()
        {
            // The repo's Content/sql/weenies (the test output carries a copy of Content/dungeons only), walked up to.
            string weenies = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && weenies == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    weenies = candidate;
            }

            Assert.IsNotNull(weenies, $"Content/sql/weenies not found walking up from {AppContext.BaseDirectory}");

            foreach (var wcid in DefenseEncounterType.DefaultWcids)
            {
                var files = Directory.GetFiles(weenies, $"{wcid} *.sql");
                Assert.AreEqual(1, files.Length, $"{wcid}: one weenie file");

                var sql = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(files[0]), @"\s+", " ");

                StringAssert.Contains(sql, $"({wcid}, 19, False)", $"{wcid}: Attackable False");
                StringAssert.Contains(sql, $"({wcid}, 95, 8)", $"{wcid}: RadarBlipColor Yellow (NPC), as the Skyward Trialmaster");
                Assert.IsFalse(sql.Contains($"({wcid}, 134,"), $"{wcid}: no PlayerKillerStatus row; PreparePillar sets RubberGlue at spawn");
            }
        }

        [TestMethod]
        public void The_type_is_registered_by_the_manager_beside_the_shrine_and_is_usable()
        {
            var type = new DefenseEncounterType();
            Assert.AreEqual("defense", type.Kind);
            Assert.IsInstanceOfType(type, typeof(IThreadEncounterUsable));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var init = PooledLootSourceText.MethodBody(src, "public static void Initialize()");
            var shrine = init.IndexOf("ThreadEncounterTypes.Register(new ShrineEncounterType());", StringComparison.Ordinal);
            var defense = init.IndexOf("ThreadEncounterTypes.Register(new DefenseEncounterType());", StringComparison.Ordinal);

            Assert.IsTrue(shrine >= 0 && defense > shrine, "registered right after the shrine");
            Assert.IsTrue(defense < init.IndexOf("Store = store;", StringComparison.Ordinal), "before the store is published");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;
using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Loads the REAL Content/dungeons/dynamic folder (not a fixture) and checks the Speed Season 3 (Cells of the Black
    /// Book, landblock 0x0089) content: the parked Threads row, its puzzle sites, and speed-courses.json cross-checked
    /// against them and against the species tables. Reads files only: no PropertyManager, no database, so it is
    /// independent of the other test classes' static state.
    /// </summary>
    [TestClass]
    public class SpeedBlackBookContentTests
    {
        private const string DungeonId = "speed_black_book";
        private const int SeasonId = 3;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "dungeons", "dynamic", "index.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/dungeons/dynamic/index.json by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string DynamicFolder() => Path.Combine(RepoRoot(), "Content", "dungeons", "dynamic");

        private static bool Mentions(string diagnostic)
            => diagnostic.Contains(DungeonId, StringComparison.Ordinal) || diagnostic.Contains("0x0089", StringComparison.OrdinalIgnoreCase);

        private static SpeedCourseStore LoadCourses(out ThreadDungeonStore threads)
        {
            threads = ThreadDungeonStore.Load(DynamicFolder());
            var species = WorldEventAxisStore.Load(Path.Combine(RepoRoot(), "Content", "events", "axes")).SpeciesTables;
            return SpeedCourseStore.Load(DynamicFolder(), new ThreadStoreCourseLookup(threads, species));
        }

        [TestMethod]
        public void ThreadsRow_LoadsParkedWithNoDiagnostics()
        {
            var threads = ThreadDungeonStore.Load(DynamicFolder());

            var mine = threads.Diagnostics.Where(Mentions).ToList();
            Assert.AreEqual(0, mine.Count, string.Join(" | ", mine));

            Assert.IsTrue(threads.Dungeons.TryGetValue(DungeonId, out var row), "index.json has no speed_black_book row");
            Assert.IsFalse(row.Enabled, "the Black Book row must stay parked: it is entered through the season row, never the Threads gem");
            Assert.IsNotNull(row.BossAnchor, "0x0089.json must carry the hub bossAnchor");
            Assert.AreEqual(0x008902FAu, row.BossAnchor.Cell, "bossAnchor is the hub centre cell");
            Assert.IsTrue(row.Points.Count(p => p.Curated) > 0, "0x0089.json has no curated spawn points");
        }

        [TestMethod]
        public void Season3Course_LoadsWithNoDiagnostics()
        {
            var store = LoadCourses(out var threads);

            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));

            var course = store.GetCourse(SeasonId);
            Assert.IsNotNull(course, "speed-courses.json has no valid course for season 3");
            Assert.AreEqual(DungeonId, course.DungeonId);
            Assert.AreEqual(150, course.FixedLevel, "the course plays at a flat level 150 (owner ruling 2026-10-24)");
            Assert.AreEqual("bb_seal", course.SealKey);
            Assert.AreEqual(9.0, course.SealRequired, "all three rites are required (owner ruling 2026-10-25)");
            Assert.AreEqual(10, course.Hazards.Count);
            Assert.IsTrue(threads.Dungeons.ContainsKey(course.DungeonId));
        }

        [TestMethod]
        public void Season3Course_EveryHazardPassesItsMechanicValidate_AcrossRunLevels()
        {
            // Parsed WITHOUT the Threads cross-check on purpose: hazard args are valid or not whatever the puzzle site ids say,
            // so a site-id problem must not hide a hazard problem (Season3Course_LoadsWithNoDiagnostics owns the cross-check).
            var course = SpeedCourseStore.Parse(File.ReadAllText(Path.Combine(DynamicFolder(), SpeedCourseStore.FileName)), null).GetCourse(SeasonId);
            Assert.IsNotNull(course, "no parseable season 3 course to validate");

            // The settings layer the live run lays the course args over: the mechanic's shipped default string.
            var layers = new Dictionary<string, HazardArgs>(StringComparer.OrdinalIgnoreCase)
            {
                [HazardMechanicRegistry.Wisps] = HazardArgs.Parse(HazardDefaults.Wisps),
                [HazardMechanicRegistry.Balls] = HazardArgs.Parse(HazardDefaults.Balls),
                [HazardMechanicRegistry.Memory] = HazardArgs.Parse(HazardDefaults.Memory),
                [HazardMechanicRegistry.Zones] = HazardArgs.Empty,
                [HazardMechanicRegistry.Rain] = HazardArgs.Empty,
            };

            var problems = new List<string>();

            foreach (var h in course.Hazards)
            {
                Assert.IsTrue(HazardMechanicRegistry.TryCreate(h.Kind, out var mechanic), h.Id);
                Assert.IsTrue(HazardManager.TryResolvePattern(mechanic, h.Pattern, out var pattern, out var patternError), $"{h.Id}: {patternError}");

                var region = h.Region.Build(h.Id, 0x0089, 0x00000001u);
                var regionError = region.Validate();

                if (regionError != null)
                    problems.Add($"{h.Id}: region: {regionError}");

                foreach (var level in new[] { 60, 120, 200, 300, 400, 500 })
                {
                    var args = h.ToHazardArgs();

                    if (h.PctAuthored > 0)
                        args = args.With(HazardDefaults.PctKey, SpeedCourseRules.ScaledPct(h, level).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));

                    var merged = HazardArgs.Merge(args, layers[h.Kind]);
                    var error = mechanic.Validate(region, merged, pattern);

                    if (error != null)
                        problems.Add($"{h.Id} @ level {level}: {error}");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static SpeedCourseDef ParsedCourse()
        {
            var course = SpeedCourseStore.Parse(File.ReadAllText(Path.Combine(DynamicFolder(), SpeedCourseStore.FileName)), null).GetCourse(SeasonId);
            Assert.IsNotNull(course, "no parseable season 3 course");
            return course;
        }

        /// <summary>
        /// Every shipped hazard exactly as the live host starts it (SpeedCourseHazardHost.TryStart -> HazardManager.Start):
        /// the course args are the WHOLE settings layer (no hazard_mech_&lt;kind&gt; row underneath), the caller args the
        /// scaled pct at the course's fixed level, merged caller-over-settings, then the region's own Validate, the
        /// mechanic's Validate on the built region, and RequiredWcids naming only wcids the content ships.
        /// </summary>
        [TestMethod]
        public void Season3Course_EveryHazardValidatesAsTheLiveHostStartsIt()
        {
            var course = ParsedCourse();
            var level = course.FixedLevel;
            Assert.IsTrue(level > 0, "the course plays at a fixed level");

            var problems = new List<string>();

            foreach (var h in course.Hazards)
            {
                Assert.IsTrue(HazardMechanicRegistry.TryCreate(h.Kind, out var mechanic), h.Id);
                Assert.IsTrue(HazardManager.TryResolvePattern(mechanic, h.Pattern, out var pattern, out var patternError), $"{h.Id}: {patternError}");

                var region = h.Region.Build($"speed:{h.Id}", 0x0089, 0x00000001u);
                var regionError = region.Validate();

                if (regionError != null)
                    problems.Add($"{h.Id}: region: {regionError}");

                var caller = HazardArgs.Empty;

                if (h.PctAuthored > 0 && h.Kind != HazardMechanicRegistry.Memory)
                    caller = caller.With(HazardDefaults.PctKey, SpeedCourseRules.ScaledPct(h, level).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));

                var merged = HazardArgs.Merge(caller, h.ToHazardArgs());
                var error = mechanic.Validate(region, merged, pattern);

                if (error != null)
                    problems.Add($"{h.Id} @ level {level}: {error}");

                foreach (var wcid in mechanic.RequiredWcids(merged))
                    if (!ShippedWcids().Contains(wcid))
                        problems.Add($"{h.Id}: requires wcid {wcid}, which no Content/sql/weenies file creates");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static HashSet<uint> shipped;

        /// <summary>Every wcid a Content/sql/weenies/*.sql file inserts (the file name's leading number, the repo's convention).</summary>
        private static HashSet<uint> ShippedWcids()
        {
            if (shipped != null)
                return shipped;

            var set = new HashSet<uint>();

            // The test output carries a copy of Content/dungeons but not the weenie SQL: walk up to the repo's own.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}");

            foreach (var path in Directory.EnumerateFiles(Path.Combine(dir.FullName, "Content", "sql", "weenies"), "*.sql", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var digits = new string(name.TakeWhile(char.IsDigit).ToArray());

                if (uint.TryParse(digits, out var wcid))
                    set.Add(wcid);
            }

            return shipped = set;
        }

        /// <summary>The owner rulings of 2026-10-24 on the final fight, as shipped.</summary>
        [TestMethod]
        public void Season3Course_BossFightIsThePhasedFight()
        {
            var course = ParsedCourse();

            Assert.IsFalse(course.Hazards.Any(h => h.Id == "boss_fans" || h.Id == "hub_zones"), "the interval fans and the corner pillars are retired");
            Assert.IsFalse(course.Hazards.Any(h => h.Trigger.Type == SpeedHazardTriggers.BossAliveInterval || h.Trigger.Type == SpeedHazardTriggers.BossHealthBelow),
                "only the phases hit the player in the hub");

            Assert.AreEqual(4f, course.Named.Single(n => n.Slot == SpeedNamedSlots.Boss).Scale, "the boss is 4x scale");

            CollectionAssert.AreEqual(new[] { 1.0, 0.75, 0.50, 0.25 }, course.BossPhases.Select(p => p.AtFraction).ToArray());
            Assert.AreEqual(0, SpeedBossPhaseRules.Validate(course).Count, string.Join("; ", SpeedBossPhaseRules.Validate(course)));

            string KindPattern(string id)
            {
                var h = course.Hazards.Single(x => x.Id == id);
                return h.Kind + "/" + (h.Pattern ?? "-");
            }

            CollectionAssert.AreEqual(new[] { "balls/fan" }, course.BossPhases[0].Hazards.Select(KindPattern).ToArray(), "entry: fan");
            CollectionAssert.AreEqual(new[] { "rain/-" }, course.BossPhases[1].Hazards.Select(KindPattern).ToArray(), "75: rain");
            CollectionAssert.AreEqual(new[] { "balls/random" }, course.BossPhases[2].Hazards.Select(KindPattern).ToArray(), "50: scatterball");
            CollectionAssert.AreEqual(new[] { "balls/row", "balls/cross" }, course.BossPhases[3].Hazards.Select(KindPattern).ToArray(), "25: row then cross");
            Assert.IsNotNull(course.BossPhases[0].TriggerRegion, "the entry fan waits for the runner in the hub");

            var fan = course.Hazards.Single(h => h.Id == course.BossPhases[0].Hazards[0]);
            Assert.AreEqual("210", fan.Args["cx"], "the fans come from the hub centre");
            Assert.AreEqual("-210", fan.Args["cy"]);
            Assert.IsFalse(fan.OriginAtBoss);

            foreach (var id in course.BossPhases.SelectMany(p => p.Hazards))
            {
                var h = course.Hazards.Single(x => x.Id == id);
                CollectionAssert.AreEqual(new uint[] { 1006951, 1006957, 1006958, 1006959 }, HazardBallWcids.Candidates(h.ToHazardArgs(), 0).ToArray(), $"{id}: random colours");

                if (h.Kind == HazardMechanicRegistry.Balls)
                {
                    Assert.AreEqual("0", h.Args["marker"], $"{id}: no fire-pillar telegraph");
                    Assert.AreEqual("0", h.Args["ray_markers"], $"{id}: no ray telegraph");
                }
            }
        }

        /// <summary>The Seal door shuts behind the runner over exactly the hub the entry phase waits for them in.</summary>
        [TestMethod]
        public void Season3Course_SealLockRegionIsTheEntryPhaseTriggerRegion()
        {
            var course = ParsedCourse();
            var seal = course.SealLock?.Region;
            var entry = course.BossPhases[0].TriggerRegion;

            Assert.IsNotNull(seal, "the course has a sealLock region");
            Assert.IsNotNull(entry);

            Assert.AreEqual(entry.Shape, seal.Shape);
            Assert.AreEqual(entry.MinX, seal.MinX);
            Assert.AreEqual(entry.MaxX, seal.MaxX);
            Assert.AreEqual(entry.MinY, seal.MinY);
            Assert.AreEqual(entry.MaxY, seal.MaxY);
            Assert.AreEqual(entry.CenterX, seal.CenterX);
            Assert.AreEqual(entry.CenterY, seal.CenterY);
            Assert.AreEqual(entry.Radius, seal.Radius);
            Assert.AreEqual(entry.GroundZ, seal.GroundZ);
            Assert.AreEqual(entry.CeilingZ, seal.CeilingZ);
            Assert.AreEqual(entry.BandBelow, seal.BandBelow);
            Assert.AreEqual(entry.BandAbove, seal.BandAbove);
            Assert.AreEqual(199f, seal.MinX, "the region starts inside the Seal door at x 195");
        }

        [TestMethod]
        public void Season3Course_NamedPointsAndPuzzleSitesAreInTheDungeonsLandblock()
        {
            var threads = ThreadDungeonStore.Load(DynamicFolder());
            var course = SpeedCourseStore.Parse(File.ReadAllText(Path.Combine(DynamicFolder(), SpeedCourseStore.FileName)), null).GetCourse(SeasonId);
            Assert.IsNotNull(course, "no parseable season 3 course");

            foreach (var n in course.Named)
                Assert.AreEqual(0x0089u, n.Position.Cell >> 16, $"named {n.Slot} is not on landblock 0x0089");

            var sites = threads.GetPuzzleSites(DungeonId);
            foreach (var p in course.Puzzles)
                Assert.IsTrue(sites.Any(s => s.Id == p.SiteId), $"puzzle {p.Slot}: site '{p.SiteId}' is not in puzzle-gates.json for {DungeonId}");
        }

        /// <summary>Owner ruling 2026-10-25: every damaging hazard hits for 25% of max health, at every run level.</summary>
        [TestMethod]
        public void Season3Course_EveryDamagingHazardHitsForTwentyFivePercentAtEveryLevel60To500()
        {
            var course = ParsedCourse();
            var problems = new List<string>();
            var damaging = course.Hazards.Where(h => h.PctAuthored > 0).ToList();

            // Every hazard but the memory (which deals no damage) carries a pct.
            CollectionAssert.AreEquivalent(course.Hazards.Where(h => h.Kind != HazardMechanicRegistry.Memory).Select(h => h.Id).ToList(),
                damaging.Select(h => h.Id).ToList(), "every non-memory hazard is a damaging one");

            foreach (var h in damaging)
            {
                Assert.AreEqual(0.60, h.PctAuthored, 1e-9, $"{h.Id}: pctAuthored stays the benchmark");

                for (var level = 60; level <= 500; level += 5)
                {
                    var pct = SpeedCourseRules.ScaledPct(h, level);

                    if (Math.Abs(pct - 0.25) > 1e-9)
                        problems.Add($"{h.Id} @ level {level}: single hit {pct:P1}");
                }

                // zones ramp from pct to pct_end: the ceiling of a single hit is pct_end
                if (h.Kind == HazardMechanicRegistry.Zones && Math.Abs(h.ToHazardArgs().GetDouble("pct_end", 0.35, 0, 5) - 0.25) > 1e-9)
                    problems.Add($"{h.Id}: pct_end is not 25 percent");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The cloister zones tick every 2 s to 25% (owner ruling 2026-10-25), keeping a similar time-to-death.</summary>
        [TestMethod]
        public void Season3Course_CloisterZonesAndEastBalls_AreTheRetunedValues()
        {
            var course = ParsedCourse();

            var zones = course.Hazards.Single(h => h.Id == "cloister_zones").ToHazardArgs();
            Assert.AreEqual("2", zones.GetString("tick"));
            Assert.AreEqual("0.25", zones.GetString("pct_end"));

            var balls = course.Hazards.Single(h => h.Id == "east_balls").ToHazardArgs();
            Assert.AreEqual("3.75", balls.GetString("speed"));
            Assert.AreEqual("2.25", balls.GetString("wave_gap"));
            Assert.AreEqual("4", balls.GetString("count"), "count unchanged");
        }

        /// <summary>
        /// A hazard's seed is its list index (SpeedCourseHazardHost, HazardSeed(seed, slot.Index)), so the shipped hazards
        /// keep their indexes and new ones are appended: east_exit_wisps is last.
        /// </summary>
        [TestMethod]
        public void Season3Course_HazardsAreAppendOnly()
        {
            var course = ParsedCourse();

            CollectionAssert.AreEqual(new[]
            {
                "cloister_zones", "east_wisps", "east_balls", "south_memory", "phase_entry_fan", "phase_75_rain", "phase_50_scatter",
                "phase_25_row", "phase_25_cross", "east_exit_wisps",
            }, course.Hazards.Select(h => h.Id).ToArray());

            var exit = course.Hazards[9];
            var east = course.Hazards.Single(h => h.Id == "east_wisps");
            Assert.AreEqual("wisps/scatter", exit.Kind + "/" + exit.Pattern);
            Assert.AreEqual(SpeedHazardTriggers.WhileInRegion, exit.Trigger.Type);
            Assert.AreEqual(386f, exit.Region.MinX);
            Assert.AreEqual(404f, exit.Region.MaxX);
            Assert.AreEqual(east.TriggerRegion.MinX, exit.TriggerRegion.MinX, "the exit wisps start with the rest of the east arm");
            Assert.AreEqual(east.TriggerRegion.MaxX, exit.TriggerRegion.MaxX);
            CollectionAssert.AreEquivalent(east.Args.ToList(), exit.Args.ToList(), "the exit hall wisps play as the east wisps do");
        }

        [TestMethod]
        public void Season3Course_AllThreeRitesAreRequired_AndTheRunnerIsToldSo()
        {
            var course = ParsedCourse();

            Assert.AreEqual(9.0, course.Tokens.Values.Sum(), "every token is needed");
            Assert.AreEqual(3, course.ProgressGroups.Single(g => g.Name == "rites").Of);
            Assert.AreEqual("The Seal of the Black Book opens for one who has solved both ward gates and completed all three rites: the Archivist to the north, the Lectern to the east, the Recital to the south.",
                course.Messages.Intro);
            Assert.AreEqual("The Cell Keeper falls, and the doors of its chamber unseal.", course.Messages.GuardianDoor, "owner ruling 2026-10-25: a keeper opens both doors of its room");
        }

        [TestMethod]
        public void Season3Course_OneCellKeeperGuardsEachCornerExitDoor()
        {
            var course = ParsedCourse();

            CollectionAssert.AreEquivalent(new[] { "bb_nw_out", "bb_sw_out", "bb_ne_out", "bb_se_out" }, course.Guardians.Select(g => g.DoorKeys[0]).ToArray(), "each keeper's first key is its own room's exit door");

            foreach (var g in course.Guardians)
            {
                Assert.AreEqual("Cell Keeper", g.DisplayName, g.Id);
                Assert.AreEqual(1f, g.Scale, g.Id);
                Assert.AreEqual(0x0089u, g.Position.Cell >> 16, $"{g.Id} is not on landblock 0x0089");
                Assert.AreEqual(-6f, g.Position.Z, g.Id);
                CollectionAssert.DoesNotContain(g.DoorKeys, course.SealKey, g.Id);
                Assert.AreEqual(2, g.DoorKeys.Count, $"{g.Id}: both doors of its room");
                Assert.AreEqual(g.DoorKeys[0].Replace("_out", "_in"), g.DoorKeys[1], $"{g.Id}: the second key is the same room's entry door");
            }
        }
    }
}
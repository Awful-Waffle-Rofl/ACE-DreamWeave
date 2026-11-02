using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;
using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Speed Season 3 (the Black Book) against its REAL content, files only (no PropertyManager, no database):
    /// <list type="bullet">
    /// <item>the family coverage sweep: at every run level 60..500 step 5 the real population builder, over the real
    /// 0x0089 row and the real undead species table, fills every curated point with undead, fields a boss candidate and
    /// shapes K1/K2 as elites (with the course's forced-family widening, and a control without it);</item>
    /// <item>the weenie SQL of the course objects agrees with speed-courses.json (Seal key, Required, the lectern's
    /// token and weight, the stub key);</item>
    /// <item>the south memory's pads never land on the chute.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class SpeedBlackBookCoverageTests
    {
        private const int SeasonId = 3;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.sln by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }

        private static string Dynamic() => Path.Combine(RepoRoot(), "Content", "dungeons", "dynamic");

        private static SpeedCourseDef Course()
        {
            var course = SpeedCourseStore.Parse(File.ReadAllText(Path.Combine(Dynamic(), SpeedCourseStore.FileName)), null).GetCourse(SeasonId);
            Assert.IsNotNull(course, "no parseable season 3 course");
            return course;
        }

        // ------------------------------------------------------------------------------------------- family coverage

        private sealed class Fixture
        {
            public SpeedCourseDef Course;
            public ThreadDungeonStore Threads;
            public IReadOnlyDictionary<string, SpeciesTableDef> Species;
            public SpeciesTableDef Family;
            public DungeonEntryDef Dungeon;
            public Dictionary<uint, int> Levels;
            public int Curated;

            public int LevelOf(uint wcid) => Levels.TryGetValue(wcid, out var l) ? l : 0;

            // The same limits a live course builds with, over the compiled Threads defaults (no PropertyManager here).
            public DungeonPopulationLimits CourseLimits => SpeedCourseRules.CourseLimits(new DungeonPopulationLimits(Curated, 1.0, 1.0, 8, 1.0), Curated);

            public DungeonSpawnPlan Build(int runLevel, DungeonPopulationLimits limits)
                => DungeonPopulationBuilder.Build(SpeedCourseRules.BuildGemSpec(Course, runLevel, 0x50000001), Dungeon, Threads, Species,
                    LevelOf, _ => 1000u, limits, new Random(Course.Seed));
        }

        private static Fixture Load()
        {
            var f = new Fixture { Course = Course(), Threads = ThreadDungeonStore.Load(Dynamic()) };
            f.Species = WorldEventAxisStore.Load(Path.Combine(RepoRoot(), "Content", "events", "axes")).SpeciesTables;

            Assert.IsTrue(f.Species.TryGetValue(f.Course.Family, out f.Family), $"no species table '{f.Course.Family}'");
            Assert.IsTrue(f.Threads.Dungeons.TryGetValue(f.Course.DungeonId, out f.Dungeon), $"no Threads row '{f.Course.DungeonId}'");

            // The species table's own member levels stand in for the live weenie levels (no database here).
            f.Levels = new Dictionary<uint, int>();

            foreach (var table in f.Species.Values)
                foreach (var m in table.Members)
                    if (!f.Levels.ContainsKey(m.Wcid))
                        f.Levels[m.Wcid] = m.Level;

            var anchor = f.Dungeon.BossAnchor;
            f.Curated = f.Dungeon.Points.Count(p => p != null && p.Curated
                && !(anchor != null && (ReferenceEquals(p, anchor) || (p.Cell == anchor.Cell && p.X == anchor.X && p.Y == anchor.Y && p.Z == anchor.Z))));

            Assert.IsTrue(f.Curated > 2, "the course has curated points to fill");
            return f;
        }

        /// <summary>
        /// Every run level 60..500 step 5 fills EVERY curated point with an undead creature (no empty corridors on a
        /// time-only board), fields a boss, and shapes K1/K2 as elites. No exception list: a level-gap in the undead table
        /// is closed by the course's forced-family widening (SpeedCourseRules.CourseLimits).
        /// </summary>
        [TestMethod]
        public void FamilyCoverage_EveryLevel60To500_FillsEveryPointAndFieldsABossAndBothNamedSlots()
        {
            var f = Load();
            var k1 = f.Course.Named.Single(n => n.Slot == SpeedNamedSlots.K1).Position;
            var k2 = f.Course.Named.Single(n => n.Slot == SpeedNamedSlots.K2).Position;
            var bossPoint = f.Course.Named.Single(n => n.Slot == SpeedNamedSlots.Boss).Position;
            var undead = new HashSet<uint>(f.Family.Members.Select(m => m.Wcid));

            var problems = new List<string>();
            var reach = new List<string>();

            for (var level = 60; level <= 500; level += 5)
            {
                var runLevel = SpeedCourseRules.RunLevel(level, 60, f.Course.LevelFloor, 0); // 0: sweep the scaling path so every band keeps its coverage, even though the shipped course is flat
                DungeonSpawnPlan plan;

                try
                {
                    plan = f.Build(runLevel, f.CourseLimits);
                }
                catch (Exception ex)
                {
                    problems.Add($"level {level}: the builder threw: {ex.Message}");
                    continue;
                }

                if (plan == null)
                {
                    problems.Add($"level {level}: no plan");
                    continue;
                }

                var nonBoss = plan.Entries.Where(e => e.Role != DungeonRole.Boss).ToList();

                if (nonBoss.Count != f.Curated)
                    problems.Add($"level {level}: {nonBoss.Count} non-boss creatures for {f.Curated} curated points; notes: {string.Join(" | ", plan.Notes)}");

                if (nonBoss.Any(e => !undead.Contains(e.Wcid)))
                    problems.Add($"level {level}: a non-undead creature was drawn");

                var hasBoss = plan.Entries.Any(e => e.Role == DungeonRole.Boss)
                    || DungeonRosterSelector.PickBossCandidate(f.Family, runLevel, f.LevelOf, new Random(1), plan.Entries.Select(e => e.Wcid).ToList()) != 0;

                if (!hasBoss)
                    problems.Add($"level {level}: no boss entry and the family fields no boss candidate");

                var placed = SpeedCourseRules.PlaceNamed(plan.Entries, k1, k2, bossPoint, e => f.LevelOf(e.Wcid));

                if (placed.K1 == null || placed.K2 == null)
                    problems.Add($"level {level}: K1={(placed.K1 != null)} K2={(placed.K2 != null)}");
                else if (placed.K1.Role != DungeonRole.Elite || placed.K2.Role != DungeonRole.Elite)
                    problems.Add($"level {level}: K1/K2 not shaped as elites");

                if (nonBoss.Count > 0)
                    reach.Add($"{level}:{runLevel - nonBoss.Min(e => f.LevelOf(e.Wcid))}");

                foreach (var note in plan.Notes.Where(n => n.Contains("forced family")))
                    Console.WriteLine($"level {level}: {note}");
            }

            // Diagnostic only: how far below the run level the lowest authored creature sits (it is uplifted to the run level).
            Console.WriteLine("reach below run level, level:levels = " + string.Join(" ", reach));

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The shipped course plays at a flat level (owner ruling 2026-10-24): at that level every curated point still gets
        /// an undead creature and the family fields a boss.
        /// </summary>
        [TestMethod]
        public void FamilyCoverage_AtTheShippedFixedLevel_FillsEveryPointAndFieldsABoss()
        {
            var f = Load();
            Assert.AreEqual(150, f.Course.FixedLevel);

            var runLevel = SpeedCourseRules.RunLevel(1, 60, f.Course.LevelFloor, f.Course.FixedLevel);
            Assert.AreEqual(150, runLevel);

            var plan = f.Build(runLevel, f.CourseLimits);
            Assert.IsNotNull(plan);

            var undead = new HashSet<uint>(f.Family.Members.Select(m => m.Wcid));
            var nonBoss = plan.Entries.Where(e => e.Role != DungeonRole.Boss).ToList();

            Assert.AreEqual(f.Curated, nonBoss.Count, string.Join(" | ", plan.Notes));
            Assert.IsTrue(nonBoss.All(e => undead.Contains(e.Wcid)), "a non-undead creature was drawn");
            Assert.IsTrue(plan.Entries.Any(e => e.Role == DungeonRole.Boss)
                || DungeonRosterSelector.PickBossCandidate(f.Family, runLevel, f.LevelOf, new Random(1), plan.Entries.Select(e => e.Wcid).ToList()) != 0,
                "no boss entry and the family fields no boss candidate");
        }

        /// <summary>
        /// The control: the forced-family widening is what fills the undead level gaps. Built with the same limits but the
        /// widening off (what a Threads run forced to undead gets), level 205 and 260 open with no non-boss creature. If a
        /// future Threads change widens forced families itself, this control fails: delete it then, the coverage test above
        /// still guards the course.
        /// </summary>
        [TestMethod]
        public void FamilyCoverage_WithoutForcedFamilyWidening_TheUndeadGapsAreEmpty()
        {
            var f = Load();
            var off = f.CourseLimits.WithForcedFamilyWidening(false);

            foreach (var level in new[] { 205, 245, 260 })
            {
                Assert.AreEqual(0, f.Build(level, off).Entries.Count(e => e.Role != DungeonRole.Boss), $"level {level} without the widening");
                Assert.AreEqual(f.Curated, f.Build(level, f.CourseLimits).Entries.Count(e => e.Role != DungeonRole.Boss), $"level {level} with the widening");
            }

            // Away from a gap the flag changes nothing.
            foreach (var level in new[] { 200, 300 })
                CollectionAssert.AreEqual(f.Build(level, off).Entries.Select(e => e.Wcid).ToList(), f.Build(level, f.CourseLimits).Entries.Select(e => e.Wcid).ToList(),
                    $"level {level}: the widening must not move a level whose band already holds undead trash");
        }

        // ------------------------------------------------------------------------------------------- SQL vs course

        private static string Weenie(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "Content", "sql", "weenies", file));

        /// <summary>The value of property <paramref name="type"/> in a "(wcid, type, value)" row of <paramref name="sql"/>, or null.</summary>
        private static string Prop(string sql, uint wcid, int type)
        {
            var m = Regex.Match(sql, $@"\(\s*{wcid}\s*,\s*{type}\s*,\s*('(?<s>[^']*)'|(?<n>[-0-9.]+|True|False))\s*\)");
            return m.Success ? (m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["n"].Value) : null;
        }

        private const int ObjectiveLockKey = 9011;      // PropertyString
        private const int ObjectiveLockToken = 9012;    // PropertyString
        private const int ObjectiveLockRequired = 9057; // PropertyInt
        private const int ObjectiveLockWeight = 9058;   // PropertyInt

        [TestMethod]
        public void WeenieSql_AgreesWithTheCourse()
        {
            // The property ids this parser reads, checked against the enums so a renumbering cannot leave it reading the wrong rows.
            Assert.AreEqual(ObjectiveLockKey, (int)ACE.Entity.Enum.Properties.PropertyString.ObjectiveLockKey);
            Assert.AreEqual(ObjectiveLockToken, (int)ACE.Entity.Enum.Properties.PropertyString.ObjectiveLockToken);
            Assert.AreEqual(ObjectiveLockRequired, (int)ACE.Entity.Enum.Properties.PropertyInt.ObjectiveLockRequired);
            Assert.AreEqual(ObjectiveLockWeight, (int)ACE.Entity.Enum.Properties.PropertyInt.ObjectiveLockWeight);

            var course = Course();

            var seal = Weenie("1007503 The Seal of the Black Book.sql");
            Assert.AreEqual(course.SealKey, Prop(seal, 1007503, ObjectiveLockKey), "the Seal's key is the course sealKey");
            Assert.AreEqual(course.SealRequired.ToString(System.Globalization.CultureInfo.InvariantCulture), Prop(seal, 1007503, ObjectiveLockRequired), "Seal Required = sealRequired");
            Assert.IsNull(Prop(seal, 1007503, ObjectiveLockToken));

            var lectern = Weenie("1007505 Reading Lectern.sql");
            var lecternToken = Prop(lectern, 1007505, ObjectiveLockToken);
            Assert.AreEqual(course.SealKey, Prop(lectern, 1007505, ObjectiveLockKey), "the lectern contributes to the Seal");
            Assert.IsTrue(course.Tokens.ContainsKey(lecternToken ?? ""), $"the lectern's token '{lecternToken}' is in the course token table");
            Assert.AreEqual(course.WeightOf(lecternToken).ToString(System.Globalization.CultureInfo.InvariantCulture), Prop(lectern, 1007505, ObjectiveLockWeight), "lectern weight = the token's course weight");
            Assert.IsNull(Prop(lectern, 1007505, ObjectiveLockRequired), "the lectern is a contributor, not a gate");

            // No other course object claims the Seal's key: the run is the only other contributor, and exactly one gate exists.
            var lever = Weenie("1007501 Cell Lever.sql");
            var stub = Weenie("1007502 Cell Door.sql");
            var sealed_ = Weenie("1007504 Sealed Door.sql");

            Assert.AreEqual("bb_stub", Prop(lever, 1007501, ObjectiveLockKey), "the start lever opens the stub door");
            Assert.AreEqual(Prop(lever, 1007501, ObjectiveLockKey), Prop(stub, 1007502, ObjectiveLockKey), "lever and stub door share the stub key");
            Assert.AreEqual("1", Prop(stub, 1007502, ObjectiveLockRequired), "the stub door needs the one lever");
            Assert.AreEqual("True", Prop(lever, 1007501, 9047), "the lever is the SpeedChallengeStart object");
            Assert.AreNotEqual(course.SealKey, Prop(stub, 1007502, ObjectiveLockKey));
            Assert.IsNull(Prop(sealed_, 1007504, ObjectiveLockKey), "the permanent hub doors are no objective gate");

            // Every course token is reachable: the lectern's from content, the others from the run (puzzles, K1, the memory).
            var runTokens = course.Puzzles.Select(p => p.Token)
                .Concat(course.Named.Select(n => n.Token))
                .Concat(course.Hazards.Select(h => h.Token))
                .Where(t => t != null)
                .Append(lecternToken);
            CollectionAssert.AreEquivalent(course.Tokens.Keys.ToList(), runTokens.ToList(), "each token has exactly one source");
        }

        // ------------------------------------------------------------------------------------------- wings rework (2026-10-25)

        private const int PropActive = 119;            // PropertyInt.Active
        private const int PropDefaultLocked = 35;      // PropertyBool.DefaultLocked
        private const int PropLockCode = 12;           // PropertyString.LockCode
        private const int PropSpeedChallengeStart = 9047; // PropertyBool.SpeedChallengeStart
        private const int PropPortalSameInstance = 9048;  // PropertyBool.PortalSameInstance

        /// <summary>
        /// <see cref="Prop"/> scoped to one property table (`weenie_properties_&lt;table&gt;`, from its INSERT to the next).
        /// Type ids repeat across tables (string 12 LockCode vs bool 12 ReportCollisions), so a low id must be scoped.
        /// </summary>
        private static string PropIn(string sql, string table, uint wcid, int type)
        {
            foreach (var part in sql.Split(new[] { "INSERT INTO" }, StringSplitOptions.None))
                if (part.TrimStart().StartsWith($"`weenie_properties_{table}`", StringComparison.Ordinal))
                    return Prop(part, wcid, type);

            return null;
        }

        /// <summary>Every Content/sql/weenies file, by wcid (the file name's leading number), read once.</summary>
        private static Dictionary<uint, string> allWeenies;

        private static Dictionary<uint, string> AllWeenies()
        {
            if (allWeenies != null)
                return allWeenies;

            var map = new Dictionary<uint, string>();

            foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Content", "sql", "weenies"), "*.sql", SearchOption.AllDirectories))
            {
                var digits = new string(Path.GetFileNameWithoutExtension(path).TakeWhile(char.IsDigit).ToArray());

                if (uint.TryParse(digits, out var wcid))
                    map[wcid] = File.ReadAllText(path);
            }

            return allWeenies = map;
        }

        /// <summary>The wcids whose weenie SQL carries ObjectiveLockKey <paramref name="key"/>, split by whether they are a gate (Required set).</summary>
        private static (List<uint> Gates, List<uint> Contributors) Keyed(string key)
        {
            var gates = new List<uint>();
            var contributors = new List<uint>();

            foreach (var kv in AllWeenies())
            {
                if (!kv.Value.Contains("'" + key + "'", StringComparison.Ordinal) || PropIn(kv.Value, "string", kv.Key, ObjectiveLockKey) != key)
                    continue;

                if (PropIn(kv.Value, "int", kv.Key, ObjectiveLockRequired) != null)
                    gates.Add(kv.Key);
                else
                    contributors.Add(kv.Key);
            }

            return (gates, contributors);
        }

        [TestMethod]
        public void PropertyIdsUsedByTheWingsChecks_AreTheEnumValues()
        {
            Assert.AreEqual(PropActive, (int)ACE.Entity.Enum.Properties.PropertyInt.Active);
            Assert.AreEqual(PropDefaultLocked, (int)ACE.Entity.Enum.Properties.PropertyBool.DefaultLocked);
            Assert.AreEqual(PropLockCode, (int)ACE.Entity.Enum.Properties.PropertyString.LockCode);
            Assert.AreEqual(PropSpeedChallengeStart, (int)ACE.Entity.Enum.Properties.PropertyBool.SpeedChallengeStart);
            Assert.AreEqual(PropPortalSameInstance, (int)ACE.Entity.Enum.Properties.PropertyBool.PortalSameInstance);
        }

        /// <summary>The eight corner-room door keys: each room's entry (in) and exit (Keeper's, out) door.</summary>
        private static readonly string[] CornerDoorKeys =
        {
            "bb_nw_in", "bb_nw_out", "bb_sw_in", "bb_sw_out", "bb_ne_in", "bb_ne_out", "bb_se_in", "bb_se_out",
        };

        /// <summary>
        /// Owner ruling 2026-10-25: every corner door, entry (in) or Keeper's (out), pairs exactly one Cloister Lever with
        /// exactly one door, Required 1. The lever is a contributor that can be pulled, does not rebase the run clock and
        /// carries no token (the placement's own guid). The door can be clicked open by no player from either side
        /// (Active 0: WorldObject.OnActivate returns before Door.ActOnUse, so the locked door's "behind" exception is
        /// unreachable). The in levers are 1007508-1007511 and the out (keeper-side) levers 1007522-1007525.
        /// </summary>
        [TestMethod]
        public void EveryCornerDoorKey_PairsExactlyOneLeverWithExactlyOneDoor()
        {
            var inLevers = new HashSet<uint> { 1007508, 1007509, 1007510, 1007511 };
            var outLevers = new HashSet<uint> { 1007522, 1007523, 1007524, 1007525 };
            var seenLevers = new HashSet<uint>();
            var seenDoors = new HashSet<uint>();

            foreach (var key in CornerDoorKeys)
            {
                var (gates, contributors) = Keyed(key);

                Assert.AreEqual(1, gates.Count, $"{key}: doors [{string.Join(", ", gates)}]");
                Assert.AreEqual(1, contributors.Count, $"{key}: levers [{string.Join(", ", contributors)}]");

                var door = gates[0];
                var doorSql = AllWeenies()[door];
                Assert.AreEqual("1", PropIn(doorSql, "int", door, ObjectiveLockRequired), $"{key}: Required 1 (one lever pull or the keeper's death opens it)");
                AssertUnclickableLockedDoor(doorSql, door, key);

                var lever = contributors[0];
                var leverSql = AllWeenies()[lever];
                Assert.IsTrue((key.EndsWith("_in", StringComparison.Ordinal) ? inLevers : outLevers).Contains(lever), $"{key}: lever {lever} is not in the expected wcid set");
                Assert.IsNull(PropIn(leverSql, "bool", lever, PropSpeedChallengeStart), $"{key}: the lever ({lever}) must not rebase the run clock");
                Assert.IsNull(PropIn(leverSql, "string", lever, ObjectiveLockToken), $"{key}: no token (the placement's own guid)");
                Assert.AreNotEqual("0", PropIn(leverSql, "int", lever, PropActive), $"{key}: the lever can be pulled");

                Assert.IsTrue(seenLevers.Add(lever), $"{key}: lever {lever} serves two keys");
                Assert.IsTrue(seenDoors.Add(door), $"{key}: door {door} serves two keys");
            }
        }

        private static void AssertUnclickableLockedDoor(string sql, uint wcid, string key)
        {
            Assert.AreEqual("0", PropIn(sql, "int", wcid, PropActive), $"{key} ({wcid}): Active 0");
            Assert.AreEqual("True", PropIn(sql, "bool", wcid, PropDefaultLocked), $"{key} ({wcid}): DefaultLocked");
            Assert.IsNull(PropIn(sql, "string", wcid, PropLockCode), $"{key} ({wcid}): no LockCode (no key opens it)");
        }

        /// <summary>
        /// Owner ruling 2026-10-25: each Cell Keeper names both doors of its own room, out then in, and the four keepers
        /// between them name all eight corner keys exactly once (so no door waits on two keepers).
        /// </summary>
        [TestMethod]
        public void EachCellKeeper_NamesBothDoorsOfItsRoom_AndTheKeepersCoverEveryCornerKeyOnce()
        {
            var course = Course();
            Assert.AreEqual(4, course.Guardians.Count);

            foreach (var g in course.Guardians)
            {
                var room = g.Id.Substring(g.Id.IndexOf('_') + 1).ToLowerInvariant();
                CollectionAssert.AreEqual(new[] { $"bb_{room}_out", $"bb_{room}_in" }, g.DoorKeys, g.Id);
            }

            var all = course.Guardians.SelectMany(g => g.DoorKeys).ToList();
            CollectionAssert.AreEquivalent(CornerDoorKeys, all, "the keepers name every corner key exactly once");
            Assert.IsFalse(all.Contains(course.SealKey), "no keeper opens the Seal");
        }
        /// <summary>The three chutes land on the N, E and S junctions (GEOMETRY.md), each staying in the run's instance.</summary>
        [TestMethod]
        public void TheThreeChutes_LandOnTheirJunctions()
        {
            var expected = new (uint Wcid, uint Cell, float X, float Y, float Z, float Qw, float Qz)[]
            {
                (1007506, 0x00890288, 210, -160, -6, 0.7071f, -0.7071f), // north -> N junction, facing East
                (1007520, 0x008902B8, 260, -210, -6, 0.7071f, 0.7071f),  // east  -> E junction, facing West
                (1007521, 0x0089028D, 210, -260, -6, 0.7071f, -0.7071f), // south -> S junction, facing East
            };

            foreach (var e in expected)
            {
                var sql = AllWeenies()[e.Wcid];
                var m = Regex.Match(sql, $@"\(\s*{e.Wcid}\s*,\s*2\s*,\s*0x(?<cell>[0-9A-Fa-f]{{8}})\s*,\s*(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)\s*,\s*(?<z>-?[\d.]+)\s*,\s*(?<qw>-?[\d.]+)\s*,\s*(?<qx>-?[\d.]+)\s*,\s*(?<qy>-?[\d.]+)\s*,\s*(?<qz>-?[\d.]+)\s*\)");
                Assert.IsTrue(m.Success, $"{e.Wcid}: no Destination row");

                float F(string g) => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);

                Assert.AreEqual(e.Cell, Convert.ToUInt32(m.Groups["cell"].Value, 16), $"{e.Wcid}: destination cell");
                Assert.AreEqual(e.X, F("x"), 1e-3, $"{e.Wcid}: x");
                Assert.AreEqual(e.Y, F("y"), 1e-3, $"{e.Wcid}: y");
                Assert.AreEqual(e.Z, F("z"), 1e-3, $"{e.Wcid}: z");
                Assert.AreEqual(e.Qw, F("qw"), 1e-3, $"{e.Wcid}: facing qw");
                Assert.AreEqual(e.Qz, F("qz"), 1e-3, $"{e.Wcid}: facing qz");
                Assert.AreEqual("True", PropIn(sql, "bool", e.Wcid, PropPortalSameInstance),$"{e.Wcid}: PortalSameInstance keeps the runner in the run's instance");
            }

            // Each chute is placed exactly once by the realm unit.
            var realm = File.ReadAllText(Path.Combine(RepoRoot(), "Content", "realms", "speedrun_black_book.sql"));

            foreach (var e in expected)
                Assert.AreEqual(1, Regex.Matches(realm, $@"\(\s*1\s*,\s*0x[0-9A-Fa-f]{{8}}\s*,\s*{e.Wcid}\s*,").Count, $"{e.Wcid} placements");
        }

        /// <summary>
        /// At every run level 60..500 step 5 the real plan gives each of the four Cell Keepers an undead Elite clone at its
        /// own point, and placing them leaves the plan exactly as the builder drew it.
        /// </summary>
        [TestMethod]
        public void Guardians_EveryLevel60To500_AreFieldedFromThePlan_WithoutTouchingIt()
        {
            var f = Load();
            var undead = new HashSet<uint>(f.Family.Members.Select(m => m.Wcid));
            var problems = new List<string>();

            for (var level = 60; level <= 500; level += 5)
            {
                var runLevel = SpeedCourseRules.RunLevel(level, 60, f.Course.LevelFloor, 0);
                var plan = f.Build(runLevel, f.CourseLimits);

                if (plan == null)
                {
                    problems.Add($"level {level}: no plan");
                    continue;
                }

                var before = plan.Entries.Select(e => (e.Wcid, e.Role, e.Point, e.UpliftLevel, e.StampLevel)).ToList();
                var placed = SpeedCourseRules.PlaceGuardians(plan.Entries, f.Course.Guardians, e => f.LevelOf(e.Wcid));

                if (placed.Count != f.Course.Guardians.Count)
                    problems.Add($"level {level}: {placed.Count} of {f.Course.Guardians.Count} guardians fielded");

                foreach (var p in placed)
                {
                    if (p.Entry.Role != DungeonRole.Elite || !undead.Contains(p.Entry.Wcid) || !ReferenceEquals(p.Entry.Point, p.Guardian.Position))
                        problems.Add($"level {level}: {p.Guardian.Id} wcid {p.Entry.Wcid} role {p.Entry.Role}");
                }

                if (!before.SequenceEqual(plan.Entries.Select(e => (e.Wcid, e.Role, e.Point, e.UpliftLevel, e.StampLevel))))
                    problems.Add($"level {level}: placing the guardians changed the plan");

                // The draw is a pure function of the season seed: building again gives the same plan.
                if (!before.Select(b => b.Wcid).SequenceEqual(f.Build(runLevel, f.CourseLimits).Entries.Select(e => e.Wcid)))
                    problems.Add($"level {level}: the plan is not reproducible");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // ------------------------------------------------------------------------------------------- the south memory

        [TestMethod]
        public void SouthMemory_PadsNeverLandOnTheChute_AndQuadrantsWouldHave()
        {
            var course = Course();
            var memory = course.Hazards.Single(h => h.Id == "south_memory");
            var region = memory.Region.Build("speed:south_memory", 0x0089, 1u);
            var chute = new Vector2(199f, -422f);

            Assert.IsTrue(region.InExclusion(chute.X, chute.Y), "the chute is excluded");
            Assert.AreEqual("random", memory.ToHazardArgs().GetString("layout"));
            Assert.AreEqual("4", memory.ToHazardArgs().GetString("max_len"), "one sequence of four");

            var settings = HazardArgs.Merge(memory.ToHazardArgs(), HazardArgs.Parse(HazardDefaults.Memory));
            var padRadius = (float)settings.GetDouble("pad_radius", 1.0, 0.1, 10);
            var padGap = (float)settings.GetDouble("pad_gap", 6.0, 0.1, 50);
            var inset = (float)settings.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, 0, 10);

            Assert.IsNotNull(MemoryLayout.CheckPads(region, MemoryLayout.QuadrantCentres(region), padRadius, padGap, inset),
                "layout=quadrant would put a pad on the chute: random is required");

            for (var seed = 0; seed < 200; seed++)
            {
                var pads = MemoryLayout.Plan(region, true, new Random(seed), padRadius, padGap, inset);
                Assert.IsNotNull(pads, $"seed {seed}: no pads");

                foreach (var p in pads)
                    Assert.IsTrue(Vector2.Distance(p, chute) >= 3f + padRadius - 1e-3f, $"seed {seed}: pad at ({p.X:0.#}, {p.Y:0.#}) overlaps the chute");
            }
        }
    }
}

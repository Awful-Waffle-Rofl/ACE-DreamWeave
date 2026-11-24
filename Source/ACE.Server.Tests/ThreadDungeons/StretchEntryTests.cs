using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins the Stretched entry flag, its constructor invariant, StretchPlan's lookups, and that every site that
    /// COPIES an entry carries the flag (a dropped flag would silently de-stretch a creature on retry).
    /// </summary>
    [TestClass]
    public class StretchEntryTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "SpeedCourses", "SpeedCourseRules.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/SpeedCourses/SpeedCourseRules.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Src(params string[] parts)
        {
            var all = new List<string> { RepoRoot(), "ACE.Server" };
            all.AddRange(parts);
            return File.ReadAllText(Path.Combine(all.ToArray())).Replace("\r\n", "\n");
        }

        [TestMethod]
        public void Retry_copy_keeps_the_flag()
        {
            var e = new DungeonSpawnPlanEntry(7, DungeonRole.Trash, null, 423, 0, true);
            var q = ShippedRosterFixture.Dungeon(2).Points[1];

            Assert.IsTrue(ThreadDungeonSpawner.RetryEntry(e, q).Stretched);
            Assert.AreEqual(423, ThreadDungeonSpawner.RetryEntry(e, q).UpliftLevel);
            Assert.IsFalse(ThreadDungeonSpawner.RetryEntry(new DungeonSpawnPlanEntry(7, DungeonRole.Trash, null, 423), q).Stretched);
        }

        [TestMethod]
        public void Encounter_trash_draw_keeps_the_flag()
        {
            var e = new DungeonSpawnPlanEntry(7, DungeonRole.Trash, null, 423, 0, true);
            var drawn = ThreadEncounterTrash.Draw(new[] { e }, null, new Random(1));

            Assert.IsTrue(drawn.Stretched);
            Assert.AreEqual(423, drawn.UpliftLevel);
        }

        [TestMethod]
        public void Speed_course_copy_sites_carry_the_flag_by_source_scan()
        {
            var src = Src("SpeedCourses", "SpeedCourseRules.cs");

            Assert.IsTrue(src.Contains("e = new DungeonSpawnPlanEntry(e.Wcid, role, point, e.UpliftLevel, e.StampLevel, e.Stretched);"),
                "the moved-entry copy must pass e.Stretched");
            Assert.IsTrue(src.Contains("new DungeonSpawnPlanEntry(src.Wcid, DungeonRole.Elite, g.Position, src.UpliftLevel, src.StampLevel, src.Stretched)"),
                "the guardian copy must pass src.Stretched");
        }

        private static DungeonSpawnPointDef Pt(float x) => new DungeonSpawnPointDef { Cell = 1, X = x };

        [TestMethod]
        public void PlaceNamed_keeps_the_flag_on_moved_entries()
        {
            var plan = new List<DungeonSpawnPlanEntry>
            {
                new DungeonSpawnPlanEntry(1, DungeonRole.Trash, Pt(1)),
                new DungeonSpawnPlanEntry(2, DungeonRole.Trash, Pt(2), 423, 0, true),
                new DungeonSpawnPlanEntry(3, DungeonRole.Boss, Pt(3)),
            };

            var r = SpeedCourseRules.PlaceNamed(plan, Pt(100), Pt(200), Pt(300), e => e.UpliftLevel);

            var moved = r.Entries.Single(e => e.Wcid == 2);
            Assert.AreEqual(DungeonRole.Elite, moved.Role, "the stretched trash was promoted and moved");
            Assert.AreEqual(100f, moved.Point.X);
            Assert.AreEqual(423, moved.UpliftLevel);
            Assert.IsTrue(moved.Stretched);
        }

        [TestMethod]
        public void PlaceGuardians_keeps_the_flag_on_clones()
        {
            var plan = new List<DungeonSpawnPlanEntry>
            {
                new DungeonSpawnPlanEntry(2, DungeonRole.Trash, Pt(2), 423, 0, true),
                new DungeonSpawnPlanEntry(3, DungeonRole.Boss, Pt(3)),
            };
            var guardians = new List<SpeedGuardianDef> { new SpeedGuardianDef { Id = "g", Position = new DungeonSpawnPointDef { Cell = 1, X = 9 } } };

            var placed = SpeedCourseRules.PlaceGuardians(plan, guardians, e => e.UpliftLevel);

            Assert.AreEqual(1, placed.Count);
            Assert.AreEqual(2u, placed[0].Entry.Wcid);
            Assert.AreEqual(423, placed[0].Entry.UpliftLevel);
            Assert.IsTrue(placed[0].Entry.Stretched);
        }

        [TestMethod]
        public void Fingerprint_distinguishes_a_stretched_entry()
        {
            var p = ShippedRosterFixture.Dungeon(2).Points[0];

            string Fp(bool stretched)
            {
                var plan = new DungeonSpawnPlan();
                plan.Entries.Add(new DungeonSpawnPlanEntry(7, DungeonRole.Trash, p, 423, 0, stretched));
                return ShippedRosterFixture.Fingerprint(plan);
            }

            Assert.AreNotEqual(Fp(false), Fp(true));
            Assert.IsTrue(Fp(true).Contains(":x"));
            Assert.IsFalse(Fp(false).Contains(":x"));
        }

        [TestMethod]
        public void Stretched_invariant_is_enforced()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new DungeonSpawnPlanEntry(7, DungeonRole.Boss, null, 400, 0, true));
            Assert.ThrowsExactly<ArgumentException>(() => new DungeonSpawnPlanEntry(7, DungeonRole.Trash, null, 0, 0, true));
            Assert.ThrowsExactly<ArgumentException>(() => new DungeonSpawnPlanEntry(7, DungeonRole.Trash, null, 400, 410, true));
            Assert.IsTrue(new DungeonSpawnPlanEntry(7, DungeonRole.Elite, null, 400, 0, true).Stretched);
            Assert.IsFalse(new DungeonSpawnPlanEntry(7, DungeonRole.Boss, null).Stretched);
        }

        [TestMethod]
        public void RaisedLevel_is_the_mapped_level()
        {
            Assert.AreEqual(423, new DungeonSpawnPlanEntry(7, DungeonRole.Trash, null, 423, 0, true).RaisedLevel);
        }

        [TestMethod]
        public void StretchPlan_lookups()
        {
            var plan = new StretchPlan("fam", 400, 460, 405, 455, 6, 1.25,
                new Dictionary<int, DungeonBandStandard>(), new Dictionary<int, double> { { 410, 1234.5 } });

            Assert.IsNull(plan.StandardAt(999));
            Assert.AreEqual(0.0, plan.HealthStandardAt(999));
            Assert.AreEqual(1234.5, plan.HealthStandardAt(410));
            Assert.AreEqual("fam", plan.FamilyId);
            Assert.AreEqual(6, plan.PoolCount);
            Assert.IsNull(new DungeonSpawnPlan().Stretch);
        }
    }
}

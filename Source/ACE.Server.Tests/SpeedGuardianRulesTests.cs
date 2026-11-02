using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SpeedCourseRules.PlaceGuardians: which plan creature each door guardian clones, and that the plan itself is never
    /// touched (the season's population draw must stay byte-identical). Pure; no world, no PropertyManager.
    /// </summary>
    [TestClass]
    public class SpeedGuardianRulesTests
    {
        private static DungeonSpawnPointDef Pt(float x, float y) => new DungeonSpawnPointDef { Cell = 0x00890264, X = x, Y = y, Z = -6 };

        private static SpeedGuardianDef Guardian(string id, string key, float x)
            => new SpeedGuardianDef { Id = id, DoorKeys = new List<string> { key }, Position = Pt(x, -100), DisplayName = "Cell Keeper" };

        private static List<SpeedGuardianDef> Four()
            => new List<SpeedGuardianDef> { Guardian("G1", "k1", 1), Guardian("G2", "k2", 2), Guardian("G3", "k3", 3), Guardian("G4", "k4", 4) };

        /// <summary>Two trash, an elite, a trash, an elite, the boss: the elites are 200 then 201 in plan order.</summary>
        private static List<DungeonSpawnPlanEntry> Mixed() => new List<DungeonSpawnPlanEntry>
        {
            new DungeonSpawnPlanEntry(100, DungeonRole.Trash, Pt(10, -10)),
            new DungeonSpawnPlanEntry(101, DungeonRole.Trash, Pt(11, -10), 150, 0),
            new DungeonSpawnPlanEntry(200, DungeonRole.Elite, Pt(40, -40), 0, 175),
            new DungeonSpawnPlanEntry(102, DungeonRole.Trash, Pt(12, -10)),
            new DungeonSpawnPlanEntry(201, DungeonRole.Elite, Pt(41, -41), 160, 0),
            new DungeonSpawnPlanEntry(300, DungeonRole.Boss, Pt(99, -99)),
        };

        [TestMethod]
        public void Elites_AreClonedInPlanOrder_Cycling()
        {
            var guardians = Four();
            var placed = SpeedCourseRules.PlaceGuardians(Mixed(), guardians, null);

            Assert.AreEqual(4, placed.Count);
            CollectionAssert.AreEqual(new uint[] { 200, 201, 200, 201 }, placed.Select(p => p.Entry.Wcid).ToArray(), "guardian i clones elite i mod 2");

            for (var i = 0; i < 4; i++)
            {
                Assert.AreSame(guardians[i], placed[i].Guardian);
                Assert.AreSame(guardians[i].Position, placed[i].Entry.Point, "the clone stands on the guardian point");
                Assert.AreEqual(DungeonRole.Elite, placed[i].Entry.Role);
            }

            // Uplift and stamp ride along with the wcid.
            Assert.AreEqual(0, placed[0].Entry.UpliftLevel);
            Assert.AreEqual(175, placed[0].Entry.StampLevel);
            Assert.AreEqual(160, placed[1].Entry.UpliftLevel);
            Assert.AreEqual(0, placed[1].Entry.StampLevel);
        }

        [TestMethod]
        public void NoElites_EveryGuardianClonesTheHighestLevelTrash_TiesInPlanOrder()
        {
            var entries = new List<DungeonSpawnPlanEntry>
            {
                new DungeonSpawnPlanEntry(100, DungeonRole.Trash, Pt(10, -10)),
                new DungeonSpawnPlanEntry(101, DungeonRole.Trash, Pt(11, -10), 0, 7),
                new DungeonSpawnPlanEntry(102, DungeonRole.Trash, Pt(12, -10)),
                new DungeonSpawnPlanEntry(103, DungeonRole.Trash, Pt(13, -10)),
                new DungeonSpawnPlanEntry(300, DungeonRole.Boss, Pt(99, -99)),
            };

            // 101 and 103 tie at the top level: 101 comes first in the plan.
            var levels = new Dictionary<uint, int> { [100] = 50, [101] = 90, [102] = 60, [103] = 90, [300] = 999 };
            var placed = SpeedCourseRules.PlaceGuardians(entries, Four(), e => levels[e.Wcid]);

            Assert.AreEqual(4, placed.Count);
            Assert.IsTrue(placed.All(p => p.Entry.Wcid == 101), string.Join(",", placed.Select(p => p.Entry.Wcid)));
            Assert.IsTrue(placed.All(p => p.Entry.Role == DungeonRole.Elite), "a promoted trash clone is shaped as an Elite");
            Assert.IsTrue(placed.All(p => p.Entry.StampLevel == 7));
        }

        [TestMethod]
        public void NoNonBossEntries_IsEmpty()
        {
            var bossOnly = new List<DungeonSpawnPlanEntry> { new DungeonSpawnPlanEntry(300, DungeonRole.Boss, Pt(99, -99)) };

            Assert.AreEqual(0, SpeedCourseRules.PlaceGuardians(bossOnly, Four(), null).Count, "the boss is never cloned");
            Assert.AreEqual(0, SpeedCourseRules.PlaceGuardians(new List<DungeonSpawnPlanEntry>(), Four(), null).Count);
            Assert.AreEqual(0, SpeedCourseRules.PlaceGuardians(null, Four(), null).Count);
            Assert.AreEqual(0, SpeedCourseRules.PlaceGuardians(Mixed(), null, null).Count);
            Assert.AreEqual(0, SpeedCourseRules.PlaceGuardians(Mixed(), new List<SpeedGuardianDef>(), null).Count);
        }

        [TestMethod]
        public void GuardianWithNoPosition_IsSkipped_AndTheOthersKeepTheirIndex()
        {
            var guardians = Four();
            guardians[1].Position = null;
            guardians.Insert(2, null);

            var placed = SpeedCourseRules.PlaceGuardians(Mixed(), guardians, null);

            CollectionAssert.AreEqual(new[] { "G1", "G3", "G4" }, placed.Select(p => p.Guardian.Id).ToArray());
            CollectionAssert.AreEqual(new uint[] { 200, 201, 200 }, placed.Select(p => p.Entry.Wcid).ToArray(), "indexes 0, 3, 4 -> elites 0, 1, 0");
        }

        /// <summary>
        /// The draw guard: placing guardians removes, moves and modifies nothing in the plan, and every clone is a new
        /// object. PlaceGuardians takes no Random, so it cannot consume the season's draw either.
        /// </summary>
        [TestMethod]
        public void PlanEntries_AreNeverConsumedOrModified()
        {
            var entries = Mixed();
            var before = entries.ToList();
            var fields = entries.Select(e => (e.Wcid, e.Role, e.Point, e.UpliftLevel, e.StampLevel)).ToList();

            var placed = SpeedCourseRules.PlaceGuardians(entries, Four(), null);
            Assert.AreEqual(4, placed.Count);

            Assert.AreEqual(before.Count, entries.Count, "no entry added or removed");

            for (var i = 0; i < entries.Count; i++)
            {
                Assert.AreSame(before[i], entries[i], $"entry {i} replaced");
                Assert.AreEqual(fields[i], (entries[i].Wcid, entries[i].Role, entries[i].Point, entries[i].UpliftLevel, entries[i].StampLevel), $"entry {i} modified");
            }

            foreach (var p in placed)
                Assert.IsFalse(entries.Any(e => ReferenceEquals(e, p.Entry)), "a clone is a new entry, never a plan entry");

            Assert.IsFalse(typeof(SpeedCourseRules).GetMethod(nameof(SpeedCourseRules.PlaceGuardians)).GetParameters()
                .Any(p => p.ParameterType == typeof(System.Random)), "PlaceGuardians must not take the draw's Random");
        }
    }
}

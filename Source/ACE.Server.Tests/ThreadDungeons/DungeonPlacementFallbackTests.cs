using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The pure point ordering behind dynamic_dungeons_placement_fallback_attempts. The spawner half (fresh
    /// object per attempt, telemetry) needs a live landblock and is not unit-testable here.
    /// </summary>
    [TestClass]
    public class DungeonPlacementFallbackTests
    {
        private const uint Lb = 0x0197;

        private static DungeonSpawnPointDef P(float x, float y = 0, float z = 0, uint cellSuffix = 0x0104, bool curated = true, uint landblock = Lb)
            => new DungeonSpawnPointDef { Cell = (landblock << 16) | cellSuffix, X = x, Y = y, Z = z, Curated = curated };

        [TestMethod]
        public void Boss_candidates_are_nearest_to_the_anchor_first_and_exclude_the_anchor()
        {
            var anchor = P(0);
            var far = P(50);
            var near = P(5);
            var mid = P(20);
            var points = new List<DungeonSpawnPointDef> { far, anchor, mid, near };

            var order = DungeonPlacementFallback.OrderCandidates(points, anchor, anchor, isBoss: true, isUsed: _ => true, maxAttempts: 5);

            CollectionAssert.AreEqual(new[] { near, mid, far }, order, "nearest first, anchor itself never a candidate, used points still eligible for the boss");
        }

        [TestMethod]
        public void Trash_candidates_take_unused_points_first_then_nearest_used()
        {
            var refused = P(0);
            var usedNear = P(2);
            var unusedFar = P(40);
            var unusedNear = P(10);
            var anchor = P(3);
            var points = new List<DungeonSpawnPointDef> { refused, usedNear, unusedFar, unusedNear, anchor };
            var used = new HashSet<DungeonSpawnPointDef> { refused, usedNear, anchor };

            var order = DungeonPlacementFallback.OrderCandidates(points, refused, anchor, isBoss: false, isUsed: used.Contains, maxAttempts: 5);

            CollectionAssert.AreEqual(new[] { unusedNear, unusedFar, usedNear }, order,
                "unused by distance, then used by distance; the refused point and the boss anchor are both excluded");
        }

        [TestMethod]
        public void Attempts_cap_the_list_and_zero_disables_it()
        {
            var refused = P(0);
            var points = Enumerable.Range(1, 10).Select(i => P(i)).Prepend(refused).ToList();

            Assert.AreEqual(3, DungeonPlacementFallback.OrderCandidates(points, refused, null, false, _ => false, 3).Count);
            Assert.AreEqual(0, DungeonPlacementFallback.OrderCandidates(points, refused, null, false, _ => false, 0).Count);
            Assert.AreEqual(0, DungeonPlacementFallback.OrderCandidates(points, refused, null, false, _ => false, -1).Count);
        }

        [TestMethod]
        public void Uncurated_other_landblock_and_duplicate_points_are_never_candidates()
        {
            var refused = P(0);
            var good = P(8);
            var duplicate = P(8);
            var uncurated = P(1, curated: false);
            var elsewhere = P(1, landblock: 0x0198);
            var points = new List<DungeonSpawnPointDef> { refused, uncurated, elsewhere, good, duplicate, null };

            var order = DungeonPlacementFallback.OrderCandidates(points, refused, null, false, _ => false, 10);

            Assert.AreEqual(1, order.Count, "one distinct curated point on the same landblock");
            Assert.AreSame(good, order[0], "the first occurrence of a duplicated location is the one kept");
        }

        [TestMethod]
        public void Ties_break_on_cell_then_position_so_a_plan_always_yields_the_same_order()
        {
            var refused = P(0);
            var a = P(0, 5, 0, cellSuffix: 0x0106);
            var b = P(0, -5, 0, cellSuffix: 0x0105);
            var c = P(5, 0, 0, cellSuffix: 0x0105);

            var first = DungeonPlacementFallback.OrderCandidates(new List<DungeonSpawnPointDef> { refused, a, b, c }, refused, null, false, _ => false, 5);
            var second = DungeonPlacementFallback.OrderCandidates(new List<DungeonSpawnPointDef> { c, b, refused, a }, refused, null, false, _ => false, 5);

            CollectionAssert.AreEqual(first, second, "input order must not change the output");
            CollectionAssert.AreEqual(new[] { b, c, a }, first, "all three are 5 m away: cell 0x0105 before 0x0106, then x ascending");
        }

        [TestMethod]
        public void Null_inputs_are_an_empty_list_not_a_throw()
        {
            Assert.AreEqual(0, DungeonPlacementFallback.OrderCandidates(null, P(0), null, true, null, 5).Count);
            Assert.AreEqual(0, DungeonPlacementFallback.OrderCandidates(new List<DungeonSpawnPointDef> { P(1) }, null, null, true, null, 5).Count);
            Assert.AreEqual(1, DungeonPlacementFallback.OrderCandidates(new List<DungeonSpawnPointDef> { P(0), P(1) }, P(0), null, false, null, 5).Count,
                "a null isUsed treats every point as unused");
        }
    }
}

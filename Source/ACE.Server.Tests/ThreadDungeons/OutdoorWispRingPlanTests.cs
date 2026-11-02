using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>Open World Threads R21: the pure ring plan (spacing, footprint/buffer skip, per-landblock grouping, nearest markers).</summary>
    [TestClass]
    public class OutdoorWispRingPlanTests
    {
        private static OutdoorCircleDef Circle(double radius, ushort lb = 0x5456, double x = 96, double y = 96)
            => new OutdoorCircleDef { Landblock = lb, X = x, Y = y, Radius = radius };

        [TestMethod]
        public void PointCount_is_ceil_of_circumference_over_spacing()
        {
            Assert.AreEqual(79, OutdoorWispRingPlan.PointCount(100, 8));   // ceil(78.54)
            Assert.AreEqual(8, OutdoorWispRingPlan.PointCount(10, 8));     // ceil(7.85)
            Assert.AreEqual(0, OutdoorWispRingPlan.PointCount(0, 8));
            Assert.AreEqual(0, OutdoorWispRingPlan.PointCount(100, 0));
            Assert.AreEqual(0, OutdoorWispRingPlan.PointCount(double.NaN, 8));
        }

        [TestMethod]
        public void PointCount_floors_the_spacing_and_caps_the_count()
        {
            Assert.AreEqual(OutdoorWispRingPlan.PointCount(100, 2.0), OutdoorWispRingPlan.PointCount(100, 0.1), "spacing is floored at 2 m");
            Assert.AreEqual(OutdoorWispRingPlan.MaxPoints, OutdoorWispRingPlan.PointCount(5000, 2));
        }

        [TestMethod]
        public void Every_point_sits_on_the_circle_and_neighbours_are_about_one_spacing_apart()
        {
            var plan = OutdoorWispRingPlan.Build(Circle(100), new ushort[] { 0x5456 }, 8);
            Assert.AreEqual(79, plan.Total);
            Assert.AreEqual(79, plan.Points.Count);

            var centre = OutdoorSiteGeometry.Centre(Circle(100));
            foreach (var p in plan.Points)
            {
                var dx = p.Gx - centre.Gx;
                var dy = p.Gy - centre.Gy;
                Assert.AreEqual(100.0, Math.Sqrt(dx * dx + dy * dy), 1e-6, "on the circle (horizontal metres from the centre)");
            }

            for (var i = 0; i < plan.Points.Count; i++)
            {
                var a = plan.Points[i];
                var b = plan.Points[(i + 1) % plan.Points.Count];
                var chord = Math.Sqrt((a.Gx - b.Gx) * (a.Gx - b.Gx) + (a.Gy - b.Gy) * (a.Gy - b.Gy));
                Assert.IsTrue(chord <= 8.0 + 1e-9 && chord > 7.0, $"chord {chord} between points {i} and {i + 1}");
            }
        }

        [TestMethod]
        public void Landblock_and_local_coordinates_match_the_global_point()
        {
            var plan = OutdoorWispRingPlan.Build(Circle(100), new ushort[] { 0x5456 }, 8);
            foreach (var p in plan.Points)
            {
                Assert.IsTrue(p.X >= 0 && p.X < 192 && p.Y >= 0 && p.Y < 192, "local coordinates are normalised");
                Assert.AreEqual(p.Gx, OutdoorSiteGeometry.ToGlobalX(p.Landblock, p.X), 1e-9);
                Assert.AreEqual(p.Gy, OutdoorSiteGeometry.ToGlobalY(p.Landblock, p.Y), 1e-9);
            }
        }

        [TestMethod]
        public void Points_on_a_landblock_outside_footprint_and_buffer_are_skipped_and_counted()
        {
            // Radius 300 around a one-block footprint reaches two landblocks out: those points are beyond the buffer ring.
            var plan = OutdoorWispRingPlan.Build(Circle(300), new ushort[] { 0x5456 }, 8);
            Assert.IsTrue(plan.SkippedOutsideFootprint > 0);
            Assert.AreEqual(plan.Total, plan.Points.Count + plan.SkippedOutsideFootprint + plan.SkippedOffGrid);
            foreach (var p in plan.Points)
                Assert.IsTrue(OutdoorFootprintBuffer.Contains(p.Landblock, new ushort[] { 0x5456 }), $"0x{p.Landblock:X4} is footprint or buffer");
        }

        [TestMethod]
        public void A_bigger_footprint_keeps_points_the_small_one_skipped()
        {
            var small = OutdoorWispRingPlan.Build(Circle(300), new ushort[] { 0x5456 }, 8);
            var wide = OutdoorWispRingPlan.Build(Circle(300), new ushort[] { 0x5456, 0x5556, 0x5356, 0x5457, 0x5455 }, 8);
            Assert.IsTrue(wide.Points.Count > small.Points.Count);
        }

        [TestMethod]
        public void Points_off_the_world_grid_are_skipped()
        {
            // Centre on landblock 0x0000: half the ring is west/south of the grid.
            var plan = OutdoorWispRingPlan.Build(Circle(100, 0x0000, 10, 10), new ushort[] { 0x0000 }, 8);
            Assert.IsTrue(plan.SkippedOffGrid > 0);
            Assert.AreEqual(plan.Total, plan.Points.Count + plan.SkippedOutsideFootprint + plan.SkippedOffGrid);
        }

        [TestMethod]
        public void ByLandblock_groups_every_placeable_point_exactly_once_in_first_seen_order()
        {
            var footprint = new ushort[] { 0x5456, 0x5556 };
            var plan = OutdoorWispRingPlan.Build(Circle(100, 0x5456, 190, 96), footprint, 8);
            var groups = plan.ByLandblock();

            Assert.IsTrue(groups.Count >= 2, "the circle straddles the seam");
            Assert.AreEqual(plan.Points.Count, groups.Sum(g => g.Value.Count));
            Assert.AreEqual(groups.Count, groups.Select(g => g.Key).Distinct().Count());
            foreach (var g in groups)
                Assert.IsTrue(g.Value.All(p => p.Landblock == g.Key), "a group holds only its own landblock's points");
            Assert.AreEqual(plan.Points[0].Landblock, groups[0].Key);
        }

        [TestMethod]
        public void A_bad_circle_or_spacing_plans_nothing()
        {
            Assert.AreEqual(0, OutdoorWispRingPlan.Build(null, new ushort[] { 0x5456 }, 8).Points.Count);
            Assert.AreEqual(0, OutdoorWispRingPlan.Build(Circle(100), new ushort[] { 0x5456 }, 0).Points.Count);
            Assert.AreEqual(0, OutdoorWispRingPlan.Build(Circle(100), null, 8).Points.Count);
        }

        [TestMethod]
        public void Buffer_is_the_footprint_plus_its_eight_neighbours()
        {
            var fp = new ushort[] { 0x5456 };
            Assert.IsTrue(OutdoorFootprintBuffer.Contains(0x5456, fp));
            foreach (var lb in new ushort[] { 0x5355, 0x5356, 0x5357, 0x5455, 0x5457, 0x5555, 0x5556, 0x5557 })
                Assert.IsTrue(OutdoorFootprintBuffer.Contains(lb, fp), $"0x{lb:X4}");
            foreach (var lb in new ushort[] { 0x5256, 0x5656, 0x5454, 0x5458, 0x5258 })
                Assert.IsFalse(OutdoorFootprintBuffer.Contains(lb, fp), $"0x{lb:X4}");
            Assert.IsFalse(OutdoorFootprintBuffer.Contains(0x5456, null));
        }

        [TestMethod]
        public void NearestIndices_are_the_closest_markers_nearest_first_with_index_tiebreak()
        {
            var markers = new List<(double, double)> { (0, 0), (10, 0), (3, 0), (-3, 0), (50, 50) };
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, OutdoorWispRingPlan.NearestIndices(markers, 0, 0, 3));
            CollectionAssert.AreEqual(new[] { 2, 0, 3 }, OutdoorWispRingPlan.NearestIndices(markers, 3, 0, 3));
            Assert.AreEqual(5, OutdoorWispRingPlan.NearestIndices(markers, 0, 0, 99).Count);
            Assert.AreEqual(0, OutdoorWispRingPlan.NearestIndices(markers, 0, 0, 0).Count);
            Assert.AreEqual(0, OutdoorWispRingPlan.NearestIndices(null, 0, 0, 3).Count);
        }
    }
}

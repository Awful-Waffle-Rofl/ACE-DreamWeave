using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>Pure geometry for outdoor sites. The seam cases sit on the 0x5456 / 0x5556 border (x = 192 of the western block).</summary>
    [TestClass]
    public class OutdoorSiteGeometryTests
    {
        private static OutdoorCircleDef Circle(ushort lb, double x, double y, double r)
            => new OutdoorCircleDef { Landblock = lb, X = x, Y = y, Radius = r };

        private static OutdoorSiteDef Site(OutdoorCircleDef c, List<OutdoorAreaDef> include = null, List<OutdoorAreaDef> exclude = null)
            => new OutdoorSiteDef { Circle = c, Include = include ?? new List<OutdoorAreaDef>(), Exclude = exclude ?? new List<OutdoorAreaDef>() };

        private static OutdoorAreaDef PolyArea(params double[][] v)
            => new OutdoorAreaDef { Polygon = v.Select(p => p.ToList()).ToList() };

        private static OutdoorAreaDef CircleArea(double gx, double gy, double r)
            => new OutdoorAreaDef { Circle = new OutdoorAreaCircleDef { Gx = gx, Gy = gy, R = r } };

        // ---- frame ----

        [TestMethod]
        public void Global_frame_x_increases_east_and_y_increases_north()
        {
            Assert.AreEqual(0x54 * 192.0 + 10, OutdoorSiteGeometry.ToGlobalX(0x5456, 10));
            Assert.AreEqual(0x56 * 192.0 + 10, OutdoorSiteGeometry.ToGlobalY(0x5456, 10));
            // The eastern neighbour (XX + 1) starts exactly one block length east; the northern one (YY + 1) one north.
            Assert.AreEqual(OutdoorSiteGeometry.ToGlobalX(0x5456, 192), OutdoorSiteGeometry.ToGlobalX(0x5556, 0));
            Assert.AreEqual(OutdoorSiteGeometry.ToGlobalY(0x5456, 192), OutdoorSiteGeometry.ToGlobalY(0x5457, 0));
            // ... and a block's X does not leak into Y.
            Assert.AreEqual(OutdoorSiteGeometry.ToGlobalY(0x5456, 5), OutdoorSiteGeometry.ToGlobalY(0x0056, 5));
        }

        [TestMethod]
        public void ToGlobal_and_TryToLocal_round_trip_including_across_a_seam()
        {
            var (gx, gy) = OutdoorSiteGeometry.ToGlobal(0x5456, 190, 96);
            Assert.IsTrue(OutdoorSiteGeometry.TryToLocal(gx, gy, out var back));
            Assert.AreEqual((ushort)0x5456, back.Landblock);
            Assert.AreEqual(190.0, back.X, 1e-9);
            Assert.AreEqual(96.0, back.Y, 1e-9);

            // 10 m past the seam is the same point as local x = 8 on the eastern block, normalised on the way back.
            Assert.IsTrue(OutdoorSiteGeometry.TryToLocal(gx + 10, gy, out var east));
            Assert.AreEqual((ushort)0x5556, east.Landblock);
            Assert.AreEqual(8.0, east.X, 1e-9);

            Assert.IsTrue(OutdoorSiteGeometry.TryToLocal(gx, gy + 100, out var north));
            Assert.AreEqual((ushort)0x5457, north.Landblock);
            Assert.AreEqual(4.0, north.Y, 1e-9);
        }

        [TestMethod]
        public void Unnormalised_local_coordinates_name_the_same_global_point()
        {
            var a = new OutdoorPos(0x5456, 200, 96);
            var b = new OutdoorPos(0x5556, 8, 96);
            Assert.AreEqual(a.Gx, b.Gx, 1e-9);
            Assert.AreEqual(a.Gy, b.Gy, 1e-9);
        }

        [TestMethod]
        public void TryToLocal_rejects_points_off_the_landblock_grid()
        {
            Assert.IsFalse(OutdoorSiteGeometry.TryToLocal(-0.5, 10, out _));
            Assert.IsFalse(OutdoorSiteGeometry.TryToLocal(10, 256 * 192.0, out _));
        }

        // ---- circle ----

        [TestMethod]
        public void DistanceToEdge_is_positive_inside_zero_on_the_edge_negative_outside()
        {
            var c = Circle(0x5456, 96, 96, 50);
            Assert.AreEqual(50.0, OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5456, 96, 96)), 1e-9);
            Assert.AreEqual(20.0, OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5456, 126, 96)), 1e-9);
            Assert.AreEqual(0.0, OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5456, 146, 96)), 1e-9);
            Assert.AreEqual(-10.0, OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5456, 96, 156)), 1e-9);
        }

        [TestMethod]
        public void Contains_includes_the_edge_and_excludes_beyond_it()
        {
            var c = Circle(0x5456, 96, 96, 50);
            Assert.IsTrue(OutdoorSiteGeometry.Contains(c, new OutdoorPos(0x5456, 146, 96)));
            Assert.IsFalse(OutdoorSiteGeometry.Contains(c, new OutdoorPos(0x5456, 146.01, 96)));
            Assert.IsFalse(OutdoorSiteGeometry.Contains(c, new OutdoorPos(0x5456, 96 + 36, 96 + 36))); // 50.9 m diagonal
        }

        [TestMethod]
        public void Circle_straddling_a_landblock_seam_contains_points_on_both_sides()
        {
            var c = Circle(0x5456, 190, 96, 20);
            Assert.IsTrue(OutdoorSiteGeometry.Contains(c, new OutdoorPos(0x5556, 5, 96)), "east of the seam, 7 m from the centre");
            Assert.IsTrue(OutdoorSiteGeometry.Contains(c, new OutdoorPos(0x5456, 175, 96)), "west of the seam");
            Assert.IsFalse(OutdoorSiteGeometry.Contains(c, new OutdoorPos(0x5556, 25, 96)), "east, 27 m away");
            // The same global point reached from either block agrees.
            Assert.AreEqual(OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5456, 195, 96)),
                            OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5556, 3, 96)), 1e-9);
        }

        [TestMethod]
        public void Circle_centre_on_the_far_side_of_a_seam_from_the_point_still_measures_globally()
        {
            // Centre in the eastern block, point in the western one: a naive per-block distance would be ~190 m.
            var c = Circle(0x5556, 4, 96, 30);
            Assert.AreEqual(30.0 - 10.0, OutdoorSiteGeometry.DistanceToEdge(c, new OutdoorPos(0x5456, 186, 96)), 1e-9);
        }

        // ---- include / exclude ----

        [TestMethod]
        public void Empty_include_means_the_whole_circle()
        {
            var s = Site(Circle(0x5456, 96, 96, 50));
            Assert.IsTrue(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 100, 100)));
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 190, 96)), "outside the circle");
        }

        [TestMethod]
        public void Include_areas_restrict_and_are_clipped_to_the_circle()
        {
            var gx = OutdoorSiteGeometry.ToGlobalX(0x5456, 96);
            var gy = OutdoorSiteGeometry.ToGlobalY(0x5456, 96);
            // Include a circle on the east half, which also spills past the perimeter.
            var s = Site(Circle(0x5456, 96, 96, 50), include: new List<OutdoorAreaDef> { CircleArea(gx + 40, gy, 30) });
            Assert.IsTrue(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 130, 96)));
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 70, 96)), "inside the circle, outside every include");
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 160, 96)), "inside the include, outside the perimeter");
        }

        [TestMethod]
        public void Polygon_exclude_removes_its_interior_and_exclude_beats_include()
        {
            var gx = OutdoorSiteGeometry.ToGlobalX(0x5456, 0);
            var gy = OutdoorSiteGeometry.ToGlobalY(0x5456, 0);
            // A 20 m square at local (90..110, 90..110).
            var square = PolyArea(new[] { gx + 90, gy + 90 }, new[] { gx + 110, gy + 90 }, new[] { gx + 110, gy + 110 }, new[] { gx + 90, gy + 110 });
            var s = Site(Circle(0x5456, 96, 96, 50), exclude: new List<OutdoorAreaDef> { square });
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 100, 100)), "inside the excluded square");
            Assert.IsTrue(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 120, 100)), "beside the square");

            var both = Site(Circle(0x5456, 96, 96, 50), include: new List<OutdoorAreaDef> { CircleArea(gx + 96, gy + 96, 40) },
                exclude: new List<OutdoorAreaDef> { square });
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(both, new OutdoorPos(0x5456, 100, 100)), "include covers it but the exclude wins");
        }

        [TestMethod]
        public void Polygon_exclude_straddling_a_landblock_seam_works_from_either_block()
        {
            var seamX = OutdoorSiteGeometry.ToGlobalX(0x5556, 0); // x = 192 of the western block
            var gy = OutdoorSiteGeometry.ToGlobalY(0x5456, 0);
            var box = PolyArea(new[] { seamX - 10, gy + 80 }, new[] { seamX + 10, gy + 80 }, new[] { seamX + 10, gy + 100 }, new[] { seamX - 10, gy + 100 });
            var s = Site(Circle(0x5456, 190, 96, 60), exclude: new List<OutdoorAreaDef> { box });
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 185, 90)), "west of the seam, inside the box");
            Assert.IsFalse(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5556, 5, 90)), "east of the seam, inside the box");
            Assert.IsTrue(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5556, 15, 90)), "east, past the box");
            Assert.IsTrue(OutdoorSiteGeometry.InIncludeExclude(s, new OutdoorPos(0x5456, 170, 90)), "west, past the box");
        }

        [TestMethod]
        public void Concave_polygon_notch_is_outside()
        {
            // An L shape: the notch at (10..20, 10..20) is not part of it.
            var gx = OutdoorSiteGeometry.ToGlobalX(0x5456, 0);
            var gy = OutdoorSiteGeometry.ToGlobalY(0x5456, 0);
            var l = PolyArea(new[] { gx + 0, gy + 0 }, new[] { gx + 20, gy + 0 }, new[] { gx + 20, gy + 10 }, new[] { gx + 10, gy + 10 },
                             new[] { gx + 10, gy + 20 }, new[] { gx + 0, gy + 20 });
            Assert.IsTrue(OutdoorSiteGeometry.AreaContains(l, gx + 5, gy + 5));
            Assert.IsTrue(OutdoorSiteGeometry.AreaContains(l, gx + 15, gy + 5));
            Assert.IsFalse(OutdoorSiteGeometry.AreaContains(l, gx + 15, gy + 15), "the notch");
        }

        [TestMethod]
        public void Degenerate_area_contains_nothing()
        {
            Assert.IsFalse(OutdoorSiteGeometry.AreaContains(new OutdoorAreaDef(), 0, 0));
            Assert.IsFalse(OutdoorSiteGeometry.AreaContains(PolyArea(new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 }), 0.5, 0.5));
            Assert.IsFalse(OutdoorSiteGeometry.AreaContains(null, 0, 0));
        }

        // ---- helpers ----

        [TestMethod]
        public void TryParsePositionText_reads_cell_and_local_xyz()
        {
            Assert.IsTrue(OutdoorSiteGeometry.TryParsePositionText("0x54560015 96.5 -3 10.25 1 0 0 0", out var cell, out var x, out var y, out var z));
            Assert.AreEqual(0x54560015u, cell);
            Assert.AreEqual(96.5, x);
            Assert.AreEqual(-3.0, y);
            Assert.AreEqual(10.25, z);
            Assert.IsFalse(OutdoorSiteGeometry.TryParsePositionText("0x54560015 1 2", out _, out _, out _, out _));
            Assert.IsFalse(OutdoorSiteGeometry.TryParsePositionText("nope 1 2 3", out _, out _, out _, out _));
            Assert.IsFalse(OutdoorSiteGeometry.TryParsePositionText(null, out _, out _, out _, out _));
        }

        [TestMethod]
        public void BoundingBoxLandblocks_lists_every_overlapped_tile_and_marks_off_grid_ones()
        {
            CollectionAssert.AreEquivalent(new[] { 0x5456 }, OutdoorSiteGeometry.BoundingBoxLandblocks(Circle(0x5456, 96, 96, 60)).ToArray());
            CollectionAssert.AreEquivalent(new[] { 0x5456, 0x5556 }, OutdoorSiteGeometry.BoundingBoxLandblocks(Circle(0x5456, 190, 96, 20)).ToArray());
            // Bounding box edge exactly on a seam does not claim the next tile.
            CollectionAssert.AreEquivalent(new[] { 0x5456 }, OutdoorSiteGeometry.BoundingBoxLandblocks(Circle(0x5456, 96, 96, 96)).ToArray());
            CollectionAssert.AreEquivalent(new[] { 0x5456, 0x5556, 0x5457, 0x5557 }, OutdoorSiteGeometry.BoundingBoxLandblocks(Circle(0x5456, 190, 190, 20)).ToArray());
            Assert.IsTrue(OutdoorSiteGeometry.BoundingBoxLandblocks(Circle(0x0056, 10, 96, 60)).Contains(-1));
        }
    }

    [TestClass]
    public class OutdoorRotationTests
    {
        private static DungeonEntryDef Row(string kind) => new DungeonEntryDef { Id = "x", Kind = kind };

        [TestMethod]
        public void Dungeon_rows_are_never_affected_by_the_outdoor_switches()
        {
            var d = Row("dungeon");
            Assert.IsTrue(OutdoorRotation.IsEligible(d, outdoorEnabled: false, liveOutdoorRuns: 99, maxConcurrent: 0));
            Assert.IsTrue(OutdoorRotation.IsEligible(d, true, 10, 10));
            Assert.IsTrue(OutdoorRotation.IsEligible(new DungeonEntryDef { Id = "defaulted" }, false, 99, 0), "a row with no kind is a dungeon");
        }

        [TestMethod]
        public void Outdoor_row_is_dropped_when_the_kill_switch_is_off()
        {
            Assert.IsTrue(OutdoorRotation.IsEligible(Row("outdoor"), true, 0, 10), "positive control");
            Assert.IsFalse(OutdoorRotation.IsEligible(Row("outdoor"), false, 0, 10));
        }

        [TestMethod]
        public void Outdoor_row_is_dropped_once_live_runs_reach_the_cap()
        {
            Assert.IsTrue(OutdoorRotation.IsEligible(Row("outdoor"), true, 9, 10));
            Assert.IsFalse(OutdoorRotation.IsEligible(Row("outdoor"), true, 10, 10), "at the cap");
            Assert.IsFalse(OutdoorRotation.IsEligible(Row("outdoor"), true, 11, 10), "over the cap");
            Assert.IsTrue(OutdoorRotation.IsEligible(Row("outdoor"), true, 500, 0), "a cap of 0 means unlimited");
            Assert.IsTrue(OutdoorRotation.IsEligible(Row("outdoor"), true, 500, -1), "a negative cap also means unlimited");
            Assert.IsFalse(OutdoorRotation.IsEligible(Row("outdoor"), false, 0, 0), "unlimited does not override the kill switch");
        }

        private static DungeonEntryDef Full(string id, string kind, int min = 1, int max = 500, bool enabled = true)
            => new DungeonEntryDef { Id = id, Kind = kind, MinLevel = min, MaxLevel = max, Enabled = enabled };

        [TestMethod]
        public void Candidates_keep_dungeon_order_and_the_existing_enabled_and_level_rules()
        {
            var rows = new[] { Full("a", "dungeon"), Full("b", "dungeon", min: 100), Full("c", "dungeon", enabled: false), Full("d", "dungeon", max: 40), Full("e", "dungeon") };
            var ids = OutdoorRotation.Candidates(rows, 50, outdoorEnabled: false, liveOutdoorRuns: 99, maxConcurrent: 0).Select(r => r.Id).ToArray();
            CollectionAssert.AreEqual(new[] { "a", "e" }, ids);
        }

        [TestMethod]
        public void Candidates_drop_outdoor_rows_only_when_switched_off_or_capped()
        {
            var rows = new[] { Full("a", "dungeon"), Full("ow_x", "outdoor"), Full("ow_y", "outdoor", min: 200), Full("ow_z", "outdoor", enabled: false) };
            CollectionAssert.AreEqual(new[] { "a", "ow_x" }, OutdoorRotation.Candidates(rows, 50, true, 0, 10).Select(r => r.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "a" }, OutdoorRotation.Candidates(rows, 50, false, 0, 10).Select(r => r.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "a" }, OutdoorRotation.Candidates(rows, 50, true, 10, 10).Select(r => r.Id).ToArray());
        }

        [TestMethod]
        public void Bound_gem_to_an_ineligible_outdoor_row_resolves_to_nothing()
        {
            var outdoor = Full("ow_x", "outdoor");
            Assert.AreSame(outdoor, OutdoorRotation.Bound(outdoor, true, 0, 10));
            Assert.IsNull(OutdoorRotation.Bound(outdoor, false, 0, 10));
            Assert.IsNull(OutdoorRotation.Bound(outdoor, true, 10, 10));
            var dungeon = Full("a", "dungeon");
            Assert.AreSame(dungeon, OutdoorRotation.Bound(dungeon, false, 99, 0));
            Assert.IsNull(OutdoorRotation.Bound(null, true, 0, 10));
        }

        private static DungeonEntryDef[] Pool(int dungeons, int outdoor)
            => Enumerable.Range(0, dungeons).Select(i => Full("d" + i, "dungeon"))
                .Concat(Enumerable.Range(0, outdoor).Select(i => Full("ow_" + i, "outdoor"))).ToArray();

        [TestMethod]
        public void Pick_draws_every_row_in_proportion_to_the_row_counts_whatever_its_kind()
        {
            // 3 dungeons + 2 outdoor: one uniform draw, so each row is 1/5 and outdoor as a kind is 2/5 (not a fixed share).
            var pool = Pool(3, 2);
            var perRow = new Dictionary<string, int>();
            var outdoor = 0;
            const int n = 20000;
            for (var seed = 0; seed < n; seed++)
            {
                var r = OutdoorRotation.Pick(pool, new System.Random(seed));
                perRow[r.Id] = perRow.TryGetValue(r.Id, out var c) ? c + 1 : 1;
                if (r.IsOutdoor) outdoor++;
            }
            Assert.AreEqual(5, perRow.Count, "every row is reachable");
            foreach (var kv in perRow)
                Assert.IsTrue(kv.Value > n * 0.18 && kv.Value < n * 0.22, $"{kv.Key}: {kv.Value} of {n}");
            Assert.IsTrue(outdoor > n * 0.38 && outdoor < n * 0.42, "outdoor picks: " + outdoor);
        }

        [TestMethod]
        public void Pick_with_one_outdoor_among_many_dungeons_gives_it_its_one_over_count_odds()
        {
            var pool = Pool(9, 1);
            var outdoor = 0;
            const int n = 20000;
            for (var seed = 0; seed < n; seed++)
                if (OutdoorRotation.Pick(pool, new System.Random(seed)).IsOutdoor) outdoor++;
            Assert.IsTrue(outdoor > n * 0.09 && outdoor < n * 0.11, "outdoor picks: " + outdoor);
        }

        [TestMethod]
        public void Pick_with_a_single_candidate_returns_it_and_an_empty_pool_returns_null()
        {
            foreach (var kind in new[] { "dungeon", "outdoor" })
            {
                var only = new[] { Full("only", kind) };
                for (var seed = 0; seed < 50; seed++)
                    Assert.AreSame(only[0], OutdoorRotation.Pick(only, new System.Random(seed)), kind);
            }
            Assert.IsNull(OutdoorRotation.Pick(Pool(0, 0), new System.Random(1)));
            Assert.IsNull(OutdoorRotation.Pick(null, new System.Random(1)));
        }

        [TestMethod]
        public void Pick_is_one_Next_count_draw_over_the_list_in_store_order()
        {
            var pool = Pool(3, 2);
            for (var seed = 0; seed < 300; seed++)
                Assert.AreSame(pool[new System.Random(seed).Next(pool.Length)], OutdoorRotation.Pick(pool, new System.Random(seed)), $"seed {seed}");
        }

        [TestMethod]
        public void Resolve_for_a_dungeon_only_store_is_one_uniform_seeded_pick()
        {
            var rows = Pool(6, 0).ToDictionary(r => r.Id);
            foreach (var level in new[] { 1, 250, 500 })
                for (var seed = 0; seed < 100; seed++)
                {
                    var old = rows.Values.Where(d => d.Enabled && level >= d.MinLevel && level <= d.MaxLevel).ToList();
                    var expected = old[new System.Random(seed).Next(old.Count)];
                    Assert.AreSame(expected, OutdoorRotation.Resolve("any", rows, level, seed, true, 0, 10));
                }
        }

        [TestMethod]
        public void A_gem_bound_to_an_id_is_never_re_rolled()
        {
            var rows = Pool(3, 2).ToDictionary(r => r.Id);
            for (var seed = 0; seed < 200; seed++)
            {
                Assert.AreSame(rows["d1"], OutdoorRotation.Resolve("d1", rows, 50, seed, true, 0, 10), "dungeon-bound");
                Assert.AreSame(rows["ow_1"], OutdoorRotation.Resolve("ow_1", rows, 50, seed, true, 0, 10), "outdoor-bound");
            }
            Assert.IsNull(OutdoorRotation.Resolve("ow_1", rows, 50, 1, false, 0, 10), "bound outdoor is refused by the kill switch, not re-rolled to a dungeon");
            Assert.IsNull(OutdoorRotation.Resolve("missing", rows, 50, 1, true, 0, 10));
        }

        [TestMethod]
        public void Resolve_applies_kill_switch_and_cap_before_the_draw()
        {
            var rows = Pool(2, 2).ToDictionary(r => r.Id);
            var sawOutdoor = false;
            for (var seed = 0; seed < 100; seed++)
            {
                Assert.IsFalse(OutdoorRotation.Resolve("any", rows, 50, seed, false, 0, 10).IsOutdoor, "switch off");
                Assert.IsFalse(OutdoorRotation.Resolve("any", rows, 50, seed, true, 10, 10).IsOutdoor, "at cap");
                sawOutdoor |= OutdoorRotation.Resolve("any", rows, 50, seed, true, 500, 0).IsOutdoor;
            }
            Assert.IsTrue(sawOutdoor, "cap 0 = unlimited keeps outdoor rows in the pool (positive control)");
        }

        [TestMethod]
        public void Resolve_with_only_outdoor_rows_and_outdoor_unavailable_resolves_nothing()
        {
            var rows = Pool(0, 3).ToDictionary(r => r.Id);
            Assert.IsNull(OutdoorRotation.Resolve("any", rows, 50, 1, false, 0, 10), "switch off");
            Assert.IsNull(OutdoorRotation.Resolve("any", rows, 50, 1, true, 3, 3), "at cap");
            Assert.IsNotNull(OutdoorRotation.Resolve("any", rows, 50, 1, true, 0, 3), "positive control");
        }

        [TestMethod]
        public void Null_row_is_not_eligible()
            => Assert.IsFalse(OutdoorRotation.IsEligible(null, true, 0, 10));

        [TestMethod]
        public void Defaults_match_the_owner_spec()
        {
            Assert.IsTrue(OutdoorSiteTunables.DefaultOutdoorEnabled);
            Assert.AreEqual(5L, OutdoorSiteTunables.DefaultMaxConcurrentRuns);
            Assert.AreEqual(15.0, OutdoorSiteTunables.DefaultWarningBandM);
            Assert.IsTrue(OutdoorSiteTunables.DefaultUnravelEnabled);
            Assert.AreEqual(5.0, OutdoorSiteTunables.DefaultUnravelStartPct);
            Assert.AreEqual(5.0, OutdoorSiteTunables.DefaultUnravelStepPct);
            Assert.IsTrue(OutdoorSiteTunables.DefaultBackstopEnabled);
            Assert.IsTrue(OutdoorSiteTunables.DefaultRingEnabled);
            Assert.AreEqual(8.0, OutdoorSiteTunables.DefaultRingSpacingM);
            Assert.AreEqual(30.0, OutdoorSiteTunables.DefaultDropMinInsideM);
            Assert.IsFalse(OutdoorSiteTunables.DefaultUnravelLethal);
            Assert.AreEqual(10.0, OutdoorSiteTunables.DefaultUnravelGraceS);
            Assert.AreEqual(40.0, OutdoorSiteTunables.DefaultBackstopM);
            Assert.AreEqual(2.0, OutdoorSiteTunables.DefaultContainmentMarginM);
            Assert.AreEqual(12.0, OutdoorSiteTunables.DefaultInteriorAwarenessM);
        }
    }
}

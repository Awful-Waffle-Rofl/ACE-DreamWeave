using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b, R6 and R22 (placement half): which point each drawn creature of an outdoor run gets (the
    /// interior quota, the maxCollisionHeight fit, overflow scattered at least 2 m apart) and the per-landblock placement
    /// queues the spawner walks. Pure; the spawner wiring is pinned against source at the bottom.
    /// </summary>
    [TestClass]
    public class OutdoorPopulationTests
    {
        private const ushort Primary = 0x5456;
        private const ushort East = 0x5556;

        private const uint Short = 1;   // movement height 1.0 m
        private const uint Tall = 2;    // movement height 3.0 m
        private static float HeightOf(uint wcid) => wcid == Tall ? 3.0f : 1.0f;

        private static DungeonSpawnPointDef Interior(float x, float y, uint cellIndex = 0x105, float maxHeight = 1.5f) => new DungeonSpawnPointDef
        {
            Cell = ((uint)Primary << 16) | cellIndex, X = x, Y = y, Z = 10, Curated = true, Kind = OutdoorPopulation.KindInterior, MaxCollisionHeight = maxHeight,
        };

        private static DungeonSpawnPointDef Outdoor(float x, float y, ushort landblock = Primary) => new DungeonSpawnPointDef
        {
            Cell = ((uint)landblock << 16) | 0x0001u, X = x, Y = y, Z = 10, Curated = true, Kind = OutdoorPopulation.KindOutdoor,
        };

        private static double Distance(DungeonSpawnPointDef a, DungeonSpawnPointDef b)
        {
            var (ax, ay) = OutdoorPopulation.GlobalOf(a);
            var (bx, by) = OutdoorPopulation.GlobalOf(b);
            return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        }

        private static List<(int Pick, DungeonSpawnPointDef Point)> Assign(IReadOnlyList<uint> wcids, IReadOnlyList<DungeonSpawnPointDef> points, int seed, out int dropped)
            => OutdoorPopulation.Assign(wcids, points, new Random(seed), HeightOf, null, null, out dropped);

        // ---- the quota ----

        [TestMethod]
        public void Every_point_takes_one_creature_before_any_takes_a_second()
        {
            var points = new List<DungeonSpawnPointDef> { Interior(140, 90), Interior(160, 100, 0x106), Outdoor(130, 80), Outdoor(170, 110), Outdoor(5, 96, East) };

            for (var seed = 0; seed < 20; seed++)
            {
                var result = Assign(new uint[] { Short, Short, Short, Short, Short }, points, seed, out var dropped);

                Assert.AreEqual(0, dropped);
                Assert.AreEqual(5, result.Count);
                Assert.AreEqual(5, result.Select(r => r.Point).Distinct().Count(), $"seed {seed}: five picks over five points must use every point once");
                Assert.IsFalse(result.Any(r => r.Point.Scattered), "nothing overflows while a point is free");
            }
        }

        [TestMethod]
        public void Every_interior_point_is_filled_before_overflow_when_creatures_fit()
        {
            var points = new List<DungeonSpawnPointDef> { Interior(140, 90), Interior(160, 100, 0x106), Outdoor(130, 80) };

            for (var seed = 0; seed < 20; seed++)
            {
                var result = Assign(new uint[] { Short, Short, Short, Short, Short, Short, Short, Short }, points, seed, out _);
                foreach (var interior in points.Where(OutdoorPopulation.IsInterior))
                    Assert.AreEqual(1, result.Count(r => ReferenceEquals(r.Point, interior)), $"seed {seed}: each interior point holds exactly one creature");
            }
        }

        [TestMethod]
        public void With_fewer_creatures_than_points_the_interior_points_are_served_first()
        {
            var points = new List<DungeonSpawnPointDef> { Outdoor(130, 80), Interior(140, 90), Outdoor(170, 110), Interior(160, 100, 0x106), Outdoor(150, 120) };

            for (var seed = 0; seed < 20; seed++)
            {
                var result = Assign(new uint[] { Short, Short }, points, seed, out _);
                Assert.IsTrue(result.All(r => OutdoorPopulation.IsInterior(r.Point)), $"seed {seed}: both creatures go to the two interior points");
            }
        }

        [TestMethod]
        public void A_tall_creature_never_lands_on_a_low_interior_point_and_goes_elsewhere()
        {
            var interior = Interior(140, 90);
            var points = new List<DungeonSpawnPointDef> { interior, Outdoor(130, 80), Outdoor(170, 110) };

            for (var seed = 0; seed < 20; seed++)
            {
                // The tall picks come first in draw order: the interior point must skip them and take the short one.
                var wcids = new uint[] { Tall, Tall, Short };
                var result = Assign(wcids, points, seed, out var dropped);

                Assert.AreEqual(0, dropped, "every creature still gets a point");
                Assert.AreEqual(3, result.Count);
                var atInterior = result.Single(r => ReferenceEquals(r.Point, interior));
                Assert.AreEqual(Short, wcids[atInterior.Pick], $"seed {seed}: the interior point takes the one creature that fits");
            }
        }

        [TestMethod]
        public void An_interior_point_no_creature_fits_stays_empty()
        {
            var interior = Interior(140, 90);
            var points = new List<DungeonSpawnPointDef> { interior, Outdoor(130, 80), Outdoor(170, 110) };

            var result = Assign(new uint[] { Tall, Tall, Tall, Tall }, points, 3, out var dropped);

            Assert.IsFalse(result.Any(r => ReferenceEquals(r.Point, interior)), "no tall creature at a low interior point");
            Assert.IsFalse(result.Any(r => OutdoorPopulation.IsInterior(r.Point)));
            Assert.AreEqual(4, result.Count + dropped);
            Assert.AreEqual(4, result.Count, "the outdoor points absorb all four");
        }

        [TestMethod]
        public void Interior_points_are_never_reused_for_overflow()
        {
            var points = new List<DungeonSpawnPointDef> { Interior(140, 90) };
            var result = Assign(new uint[] { Short, Short, Short }, points, 1, out var dropped);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, dropped);
        }

        // ---- overflow and spacing ----

        [TestMethod]
        public void Overflow_scatters_around_outdoor_points_never_within_2_m()
        {
            var points = new List<DungeonSpawnPointDef> { Outdoor(96, 96), Interior(140, 90) };
            var wcids = Enumerable.Repeat(Short, 15).ToArray();

            var result = Assign(wcids, points, 9, out var dropped);

            Assert.AreEqual(0, dropped);
            Assert.AreEqual(15, result.Count);

            for (var i = 0; i < result.Count; i++)
                for (var j = i + 1; j < result.Count; j++)
                    Assert.IsTrue(Distance(result[i].Point, result[j].Point) >= OutdoorPopulation.MinSpacingM - 1e-6,
                        $"picks {result[i].Pick} and {result[j].Pick} are {Distance(result[i].Point, result[j].Point):F2} m apart");

            var scattered = result.Where(r => r.Point.Scattered).ToList();
            Assert.AreEqual(13, scattered.Count, "15 picks, 2 curated points: 13 scatter copies");
            Assert.IsTrue(scattered.All(r => !OutdoorPopulation.IsInterior(r.Point)), "scatter copies are outdoor only");
            CollectionAssert.AreEqual(Enumerable.Range(0, 15).ToList(), result.Select(r => r.Pick).ToList(), "results are in pick order");
        }

        [TestMethod]
        public void Two_curated_points_under_2_m_apart_never_both_take_a_creature()
        {
            var a = Outdoor(96, 96);
            var b = Outdoor(97, 96);
            var result = Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { a, b }, 0, out _);

            Assert.IsFalse(result.Any(r => ReferenceEquals(r.Point, a)) && result.Any(r => ReferenceEquals(r.Point, b)));
            Assert.IsTrue(Distance(result[0].Point, result[1].Point) >= OutdoorPopulation.MinSpacingM - 1e-6);

            // The same-floor interior case: one cell, 1 m apart, 1 m of height: still one creature.
            var lowA = InteriorAt(140, 90, 10f);
            var lowB = InteriorAt(141, 90, 11f);
            var sameFloor = Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { lowA, lowB }, 0, out _);
            Assert.AreEqual(1, sameFloor.Count, "one cell, one floor: the 2 m rule holds");
        }

        private static DungeonSpawnPointDef InteriorAt(float x, float y, float z, uint cellIndex = 0x105) => new DungeonSpawnPointDef
        {
            Cell = ((uint)Primary << 16) | cellIndex, X = x, Y = y, Z = z, Curated = true, Kind = OutdoorPopulation.KindInterior,
        };

        [TestMethod]
        public void Stacked_interior_floors_each_keep_their_point()
        {
            // Positive control for F4: the same x,y on two floors of one building (same cell, 3 m apart vertically).
            var ground = InteriorAt(140, 90, 10f);
            var upper = InteriorAt(140, 90, 13f);
            var stacked = Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { ground, upper }, 0, out var dropped);
            Assert.AreEqual(2, stacked.Count, "two floors, two creatures");
            Assert.AreEqual(0, dropped);

            // Stacked floors in DIFFERENT cells (a stairwell cell over a room) are clear too.
            var upperOtherCell = InteriorAt(140, 90, 13f, 0x106);
            Assert.IsTrue(OutdoorPopulation.Clear(OutdoorPopulation.SpotOf(ground), OutdoorPopulation.SpotOf(upperOtherCell)), "different cells, different floors");
            Assert.AreEqual(2, Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { ground, upperOtherCell }, 0, out _).Count, "different cells, different floors");
        }

        [TestMethod]
        public void Adjacent_interior_cells_on_one_floor_keep_the_2m_rule()
        {
            // A cell boundary is not a wall: two rooms side by side on one floor, 1 m apart, take one creature.
            var roomA = InteriorAt(140, 90, 10f, 0x105);
            var roomB = InteriorAt(141, 90, 10f, 0x106);
            Assert.IsFalse(OutdoorPopulation.Clear(OutdoorPopulation.SpotOf(roomA), OutdoorPopulation.SpotOf(roomB)), "different cells, same floor, 1 m");
            var result = Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { roomA, roomB }, 0, out _);
            Assert.AreEqual(1, result.Count(r => !r.Point.Scattered), "different cells under 2 m on one floor: one creature");

            // Same floor with a small step (2.4 m, under the stacked-floor threshold) is still one floor.
            Assert.IsFalse(OutdoorPopulation.Clear(OutdoorPopulation.SpotOf(roomA), OutdoorPopulation.SpotOf(InteriorAt(141, 90, 12.4f, 0x106))), "dz 2.4: same floor");

            // Positive control: the same two cells 2 m apart are clear.
            var roomFar = InteriorAt(142, 90, 10f, 0x106);
            Assert.IsTrue(OutdoorPopulation.Clear(OutdoorPopulation.SpotOf(roomA), OutdoorPopulation.SpotOf(roomFar)), "different cells, 2 m apart");
            Assert.AreEqual(2, Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { roomA, roomFar }, 0, out _).Count, "2 m apart: both placed");
        }

        [TestMethod]
        public void Stacked_floor_rule_edge()
        {
            var ground = InteriorAt(140, 90, 10f);

            // An interior point and an outdoor one at the same x,y are NOT exempt: the horizontal rule applies.
            var outside = Outdoor(140, 90);
            Assert.AreEqual(1, Assign(new uint[] { Short, Short }, new List<DungeonSpawnPointDef> { InteriorAt(140, 90, 10f), outside }, 0, out _)
                .Count(r => !r.Point.Scattered), "an interior and an outdoor spot at one x,y share the 2 m rule");

            Assert.IsTrue(OutdoorPopulation.Clear(OutdoorPopulation.SpotOf(ground), OutdoorPopulation.SpotOf(InteriorAt(140, 90, 12.5f))), "exactly 2.5 m: clear");
            Assert.IsFalse(OutdoorPopulation.Clear(OutdoorPopulation.SpotOf(ground), OutdoorPopulation.SpotOf(InteriorAt(140, 90, 12.49f))), "under 2.5 m: same floor");
        }

        [TestMethod]
        public void Nothing_is_placed_within_the_spacing_rule_of_the_boss_anchor()
        {
            var nearAnchor = Outdoor(97, 96);
            var points = new List<DungeonSpawnPointDef> { nearAnchor, Outdoor(130, 80) };

            for (var seed = 0; seed < 10; seed++)
            {
                var reserved = OutdoorPopulation.Assign(new uint[] { Short, Short }, points, new Random(seed), HeightOf, null,
                    new[] { Outdoor(96, 96) }, out _);
                Assert.IsFalse(reserved.Any(r => ReferenceEquals(r.Point, nearAnchor)), $"seed {seed}: 1 m from the anchor");
                Assert.IsTrue(reserved.All(r => Distance(r.Point, Outdoor(96, 96)) >= OutdoorPopulation.MinSpacingM - 1e-6));

                // Positive control: with nothing reserved the same point is used.
                var free = OutdoorPopulation.Assign(new uint[] { Short, Short }, points, new Random(seed), HeightOf, null, null, out _);
                Assert.IsTrue(free.Any(r => ReferenceEquals(r.Point, nearAnchor)), $"seed {seed}: control");
            }
        }

        [TestMethod]
        public void The_scatter_admission_predicate_is_honoured()
        {
            var result = OutdoorPopulation.Assign(new uint[] { Short, Short, Short }, new List<DungeonSpawnPointDef> { Outdoor(96, 96) }, new Random(1),
                HeightOf, (source, copy) => false, null, out var dropped);

            Assert.AreEqual(1, result.Count, "the curated point itself");
            Assert.AreEqual(2, dropped, "no scatter copy was admitted");
        }

        [TestMethod]
        public void Assign_is_deterministic_per_seed()
        {
            var points = new List<DungeonSpawnPointDef> { Interior(140, 90), Outdoor(130, 80), Outdoor(170, 110), Outdoor(5, 96, East) };
            var wcids = new uint[] { Short, Tall, Short, Tall, Short, Short, Short };

            var first = Assign(wcids, points, 42, out _).Select(r => (r.Pick, r.Point.Cell, r.Point.X, r.Point.Y)).ToList();
            var second = Assign(wcids, points, 42, out _).Select(r => (r.Pick, r.Point.Cell, r.Point.X, r.Point.Y)).ToList();
            CollectionAssert.AreEqual(first, second);
        }

        [TestMethod]
        public void ScatterCopy_stays_on_its_landblock_and_recomputes_the_cell()
        {
            var copy = OutdoorPopulation.ScatterCopy(Outdoor(28, 40), 2, 10);
            Assert.AreEqual(0x5456000Bu, copy.Cell, "x 30, y 50: cell (1, 2) -> 1 * 8 + 2 + 1 = 11");
            Assert.IsTrue(copy.Scattered);

            Assert.IsNull(OutdoorPopulation.ScatterCopy(Outdoor(191, 96), 2, 0), "an offset across the east edge");
            Assert.IsNull(OutdoorPopulation.ScatterCopy(Outdoor(1, 96), -1, 0), "an offset within 0.5 m of the west edge");
            Assert.IsNull(OutdoorPopulation.ScatterCopy(Interior(140, 90), 2, 0), "a building's cells are never scattered into");
        }

        [TestMethod]
        public void Scatter_offsets_on_a_ring_are_at_least_2_m_apart()
        {
            var offsets = Enumerable.Range(0, 200).Select(OutdoorPopulation.ScatterOffset).TakeWhile(o => o != null).Select(o => o.Value).ToList();
            Assert.AreEqual(6 + 12 + 18, offsets.Count);

            for (var i = 0; i < offsets.Count; i++)
                for (var j = i + 1; j < offsets.Count; j++)
                {
                    var dx = offsets[i].Dx - offsets[j].Dx;
                    var dy = offsets[i].Dy - offsets[j].Dy;
                    Assert.IsTrue(Math.Sqrt(dx * dx + dy * dy) >= OutdoorPopulation.MinSpacingM, $"slots {i} and {j}");
                }
        }

        [TestMethod]
        public void AdmitsScatter_keeps_copies_inside_no_further_out_and_off_the_drop()
        {
            var site = new OutdoorSiteDef { Primary = Primary, Footprint = new List<ushort> { Primary }, Circle = new OutdoorCircleDef { Landblock = Primary, X = 96, Y = 96, Radius = 60 } };
            var drop = new OutdoorPos(Primary, 96, 36);   // 60 m south of the centre
            var source = Outdoor(126, 96);                 // 30 m east of the centre, 67 m from the drop

            Assert.IsTrue(OutdoorPopulation.AdmitsScatter(site, drop, source, Outdoor(124, 96)), "a copy beside its source, nearer the centre");
            Assert.IsFalse(OutdoorPopulation.AdmitsScatter(site, drop, source, Outdoor(129, 96)), "further from the centre than the source");

            // 42.4 m from both the centre and the drop: the drop floor is min(40, 42.4) = 40.
            var nearDrop = Outdoor(126, 66);
            Assert.IsFalse(OutdoorPopulation.AdmitsScatter(site, drop, nearDrop, Outdoor(120, 66)), "38.4 m from the drop, inside the 40 m floor");
            Assert.IsTrue(OutdoorPopulation.AdmitsScatter(site, drop, nearDrop, Outdoor(126, 70)), "nearer the centre and further from the drop");

            Assert.IsFalse(OutdoorPopulation.AdmitsScatter(site, drop, Outdoor(160, 96), Outdoor(160, 96)), "outside the circle");

            site.Exclude.Add(new OutdoorAreaDef { Circle = new OutdoorAreaCircleDef { Gx = OutdoorSiteGeometry.ToGlobalX(Primary, 124), Gy = OutdoorSiteGeometry.ToGlobalY(Primary, 96), R = 1 } });
            Assert.IsFalse(OutdoorPopulation.AdmitsScatter(site, drop, source, Outdoor(124, 96)), "inside an exclude area");
        }

        // ---- the per-landblock placement queues (R22) ----

        private sealed record E(int Id, ushort Landblock, bool Boss = false);

        private static List<(ushort Landblock, List<int> Ids)> Groups(params E[] entries)
            => OutdoorPopulation.PlacementGroups(entries, e => e.Landblock, e => e.Boss, Primary)
                .Select(g => (g.Landblock, g.Entries.Select(e => e.Id).ToList())).ToList();

        [TestMethod]
        public void A_dungeon_plan_is_one_group_in_plan_order()
        {
            var groups = Groups(new E(0, Primary), new E(1, Primary), new E(2, Primary, Boss: true), new E(3, Primary));

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(Primary, groups[0].Landblock);
            CollectionAssert.AreEqual(new[] { 0, 1, 3, 2 }, groups[0].Ids, "plan order, the boss last");
        }

        [TestMethod]
        public void Each_entry_is_routed_to_its_own_landblock_and_the_boss_is_last()
        {
            var groups = Groups(new E(0, Primary), new E(1, East), new E(2, Primary), new E(3, Primary, Boss: true), new E(4, East));

            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual(Primary, groups[0].Landblock);
            CollectionAssert.AreEqual(new[] { 0, 2 }, groups[0].Ids);
            Assert.AreEqual(East, groups[1].Landblock);
            CollectionAssert.AreEqual(new[] { 1, 4 }, groups[1].Ids);
            Assert.AreEqual(Primary, groups[2].Landblock, "the boss's own landblock");
            CollectionAssert.AreEqual(new[] { 3 }, groups[2].Ids);
        }

        [TestMethod]
        public void The_boss_joins_the_last_group_when_it_shares_that_landblock_and_the_primary_group_always_exists()
        {
            var groups = Groups(new E(0, East), new E(1, East, Boss: true));

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual(Primary, groups[0].Landblock);
            Assert.AreEqual(0, groups[0].Ids.Count, "the primary group starts the chain even when empty");
            CollectionAssert.AreEqual(new[] { 0, 1 }, groups[1].Ids);
        }

        // ---- the outdoor fallback candidates ----

        [TestMethod]
        public void The_outdoor_fallback_skips_used_points_and_points_the_creature_does_not_fit()
        {
            var used = Outdoor(130, 80);
            var low = Interior(140, 90);
            var free = Outdoor(170, 110);
            var curated = new List<DungeonSpawnPointDef> { used, low, free };

            var occupied = new[] { Key(used) };

            var forTall = ThreadDungeonSpawner.OutdoorFallbackCandidates(curated, Tall, occupied, HeightOf);
            CollectionAssert.AreEqual(new[] { free }, forTall);

            var forShort = ThreadDungeonSpawner.OutdoorFallbackCandidates(curated, Short, occupied, HeightOf);
            CollectionAssert.AreEqual(new[] { low, free }, forShort);
        }

        private static (uint, float, float, float) Key(DungeonSpawnPointDef p) => (p.Cell, p.X, p.Y, p.Z);

        [TestMethod]
        public void The_outdoor_fallback_never_lands_within_2_m_of_a_placed_creature()
        {
            var placed = Outdoor(130, 80);
            var crowding = Outdoor(131, 80);   // a different point 1 m from a placed creature: its key is not occupied
            var clear = Outdoor(170, 110);
            var curated = new List<DungeonSpawnPointDef> { crowding, clear };

            CollectionAssert.AreEqual(new[] { clear }, ThreadDungeonSpawner.OutdoorFallbackCandidates(curated, Short, new[] { Key(placed) }, HeightOf));

            // Positive control: nothing placed, both are candidates.
            CollectionAssert.AreEqual(new[] { crowding, clear },
                ThreadDungeonSpawner.OutdoorFallbackCandidates(curated, Short, Array.Empty<(uint, float, float, float)>(), HeightOf));
        }

        // ---- the scatter terrace guard (F6) ----

        [TestMethod]
        public void A_scatter_copy_is_refused_when_its_ground_is_more_than_10_m_off_the_source_height()
        {
            Assert.IsTrue(ThreadDungeonSpawner.ScatterGroundAcceptable(52f, 50f), "2 m of slope");
            Assert.IsTrue(ThreadDungeonSpawner.ScatterGroundAcceptable(40f, 50f), "exactly 10 m: physics' own threshold allows it");
            Assert.IsFalse(ThreadDungeonSpawner.ScatterGroundAcceptable(39.9f, 50f), "off a terrace");
            Assert.IsFalse(ThreadDungeonSpawner.ScatterGroundAcceptable(60.1f, 50f), "onto a cliff top");
            Assert.IsFalse(ThreadDungeonSpawner.ScatterGroundAcceptable(float.NaN, 50f), "no terrain answer");
        }

        internal static string ScatterGuardFault(string source)
        {
            var body = PooledLootSourceText.MethodBody(source, "private static bool SnapScatteredToTerrain(Position position)");
            var guard = body.IndexOf("if (!ScatterGroundAcceptable(terrainZ, position.PositionZ))", StringComparison.Ordinal);
            var write = body.IndexOf("position.PositionZ = terrainZ + 0.05f;", StringComparison.Ordinal);
            return guard >= 0 && write > guard ? null : "the terrace guard does not run before the z write";
        }

        [TestMethod]
        public void SnapScatteredToTerrain_checks_the_height_before_writing_it()
        {
            var source = Spawner();
            Assert.IsNull(ScatterGuardFault(source));
            Assert.IsNotNull(ScatterGuardFault(source.Replace("if (!ScatterGroundAcceptable(terrainZ, position.PositionZ))", "if (false)")));
        }

        // ---- the builder ----

        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int>
        {
            [100] = 100, [101] = 120, [102] = 105, [103] = 112, [104] = 118, [105] = 101, [110] = 130, [111] = 128, [10981] = 110,
        };

        private static Dictionary<string, SpeciesTableDef> Species() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", DisplayName = "Banderlings", CreatureType = "Banderling", Source = "retail",
                Members = new List<SpeciesMemberDef>
                {
                    new SpeciesMemberDef { Wcid = 100, Role = 0 }, new SpeciesMemberDef { Wcid = 101, Role = 0 },
                    new SpeciesMemberDef { Wcid = 102, Role = 0 }, new SpeciesMemberDef { Wcid = 103, Role = 0 },
                    new SpeciesMemberDef { Wcid = 104, Role = 0 }, new SpeciesMemberDef { Wcid = 105, Role = 0 },
                    new SpeciesMemberDef { Wcid = 110, Role = 1 }, new SpeciesMemberDef { Wcid = 111, Role = 1 },
                }
            },
        };

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[]}]}",
            "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000}]}",
            new Dictionary<string, string>());

        private static DungeonPopulationLimits Limits(int minMonsters)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, minMonsters: minMonsters);

        private static DungeonGemSpec Spec(string id) => new DungeonGemSpec(id, 100, 6, "any", 1, Array.Empty<(string, double)>(), 0, 0);

        private static List<DungeonSpawnPointDef> SitePoints() => new List<DungeonSpawnPointDef>
        {
            Interior(140, 90), Interior(160, 100, 0x106), Outdoor(130, 80), Outdoor(170, 110), Outdoor(150, 120),
        };

        private static DungeonEntryDef OutdoorSite() => new DungeonEntryDef
        {
            Id = "ow_test", Kind = DungeonEntryDef.KindOutdoor, Landblock = Primary, Name = "Test Site", ExitPortalWcid = 1003604,
            Entry = "0x54560001 100 96 10",
            Points = SitePoints(),
            BossAnchor = Outdoor(150, 70),
            CreatureTypes = new List<string> { "Banderling" },
            Outdoor = new OutdoorSiteDef
            {
                Primary = Primary, Footprint = new List<ushort> { Primary },
                Circle = new OutdoorCircleDef { Landblock = Primary, X = 150, Y = 96, Radius = 60 },
                Exit = "0x54560001 101 96 10",
            },
        };

        private static int LevelOf(uint w) => Levels.TryGetValue(w, out var l) ? l : 0;

        [TestMethod]
        public void An_outdoor_build_honours_the_interior_height_and_reports_its_population()
        {
            var site = OutdoorSite();

            // Every creature too tall for the interior points: they stay empty, and the rest still populates.
            var tall = DungeonPopulationBuilder.Build(Spec(site.Id), site, Store(), Species(), LevelOf, w => 0, Limits(12), new Random(5),
                movementHeightOf: w => 3.0f);

            var packs = tall.Entries.Where(e => e.Role != DungeonRole.Boss).ToList();
            Assert.IsTrue(packs.Count > 0, "the run is still populated");
            Assert.IsFalse(packs.Any(e => OutdoorPopulation.IsInterior(e.Point)), "no tall creature at an interior point");
            Assert.IsTrue(tall.Notes.Any(n => n.StartsWith("outdoor population:", StringComparison.Ordinal)), string.Join(" | ", tall.Notes));

            // Every creature short: both interior points hold exactly one creature.
            var fits = DungeonPopulationBuilder.Build(Spec(site.Id), OutdoorSite(), Store(), Species(), LevelOf, w => 0, Limits(12), new Random(5),
                movementHeightOf: w => 1.0f);

            var fitPacks = fits.Entries.Where(e => e.Role != DungeonRole.Boss).ToList();
            Assert.AreEqual(2, fitPacks.Count(e => OutdoorPopulation.IsInterior(e.Point)), "the interior quota");
            Assert.IsTrue(fitPacks.Any(e => e.Point.Scattered), "12 creatures over 5 points overflow as scatter copies");

            for (var i = 0; i < fitPacks.Count; i++)
                for (var j = i + 1; j < fitPacks.Count; j++)
                    Assert.IsTrue(Distance(fitPacks[i].Point, fitPacks[j].Point) >= OutdoorPopulation.MinSpacingM - 1e-6, "2 m spacing");
        }

        [TestMethod]
        public void An_outdoor_build_keeps_pack_creatures_off_the_boss_anchor()
        {
            DungeonEntryDef Crowded() => new DungeonEntryDef
            {
                Id = "ow_test", Kind = DungeonEntryDef.KindOutdoor, Landblock = Primary, Name = "Test Site", ExitPortalWcid = 1003604,
                Entry = "0x54560001 100 96 10",
                Points = new List<DungeonSpawnPointDef> { Outdoor(150.5f, 70), Outdoor(130, 80), Outdoor(170, 110) },
                BossAnchor = Outdoor(150, 70),
                CreatureTypes = new List<string> { "Banderling" },
                Outdoor = OutdoorSite().Outdoor,
            };

            for (var seed = 0; seed < 10; seed++)
            {
                var plan = DungeonPopulationBuilder.Build(Spec("ow_test"), Crowded(), Store(), Species(), LevelOf, w => 0, Limits(3), new Random(seed),
                    movementHeightOf: w => 1.0f);

                foreach (var e in plan.Entries.Where(e => e.Role != DungeonRole.Boss))
                    Assert.IsTrue(Distance(e.Point, Outdoor(150, 70)) >= OutdoorPopulation.MinSpacingM - 1e-6,
                        $"seed {seed}: a pack creature at ({e.Point.X}, {e.Point.Y}) crowds the boss anchor");
            }
        }

        // ---- group scaling over what was actually placed (F8) ----

        private static GroupScaling Group(int n) => GroupScaling.Compute(n, 1.0, 0.5, 180, 15, 300, 0.05, 1.5, 0.0);

        private static DungeonPopulationLimits GroupLimits(GroupScaling group, int minMonsters)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, healthCurveEnabled: false, minMonsters: minMonsters, group: group);

        [TestMethod]
        public void OutdoorGroupCountActual_divides_the_placed_count_by_what_a_solo_run_would_place()
        {
            Assert.AreEqual(1.0, DungeonPopulationBuilder.OutdoorGroupCountActual(3, 3), 1e-9, "capacity 3, solo 3: no group extra survived");
            Assert.AreEqual(2.0, DungeonPopulationBuilder.OutdoorGroupCountActual(10, 20), 1e-9, "room for all: the slot ratio");
            Assert.AreEqual(1.0, DungeonPopulationBuilder.OutdoorGroupCountActual(10, 7), 1e-9, "capacity 7 below solo 10: solo would place 7 too");
            Assert.AreEqual(1.0, DungeonPopulationBuilder.OutdoorGroupCountActual(10, 0), 1e-9, "nothing placed reads as 1.0, never NaN");
        }

        [TestMethod]
        public void An_outdoor_group_run_scales_health_by_the_creatures_actually_placed()
        {
            // Three interior points and nothing else: interior points are never reused, so a group's extra picks are all
            // dropped, and the run places exactly what a solo run would.
            DungeonEntryDef InteriorOnly() => new DungeonEntryDef
            {
                Id = "ow_test", Kind = DungeonEntryDef.KindOutdoor, Landblock = Primary, Name = "Test Site", ExitPortalWcid = 1003604,
                Entry = "0x54560001 100 96 10",
                Points = new List<DungeonSpawnPointDef> { Interior(140, 90), Interior(160, 100, 0x106), Interior(150, 110, 0x107) },
                BossAnchor = Outdoor(150, 70),
                CreatureTypes = new List<string> { "Banderling" },
                Outdoor = OutdoorSite().Outdoor,
            };

            var group = Group(4);
            var plan = DungeonPopulationBuilder.Build(Spec("ow_test"), InteriorOnly(), Store(), Species(), LevelOf, w => 0, GroupLimits(group, 3),
                new Random(5), movementHeightOf: w => 1.0f);

            Assert.AreEqual(3, plan.Entries.Count(e => e.Role != DungeonRole.Boss), "three points, three creatures");
            Assert.AreEqual(1.0, plan.GroupCountActual, 1e-9, "C_actual from the placed count, not the slot count");
            Assert.AreEqual(group.Effort, plan.GroupHealthMult, 1e-9, "H = E: each of the three carries the whole group's effort");

            // Positive control: plenty of outdoor room, nothing dropped, the slot ratio stands (C_actual above 1).
            var roomy = DungeonPopulationBuilder.Build(Spec("ow_test"), OutdoorSite(), Store(), Species(), LevelOf, w => 0, GroupLimits(group, 3),
                new Random(5), movementHeightOf: w => 1.0f);
            Assert.IsTrue(roomy.GroupCountActual > 1.0, $"C_actual {roomy.GroupCountActual}");
            Assert.IsTrue(roomy.Notes.Any(n => n.Contains("scattered overflow, 0 with no admissible point", StringComparison.Ordinal)),
                "the control dropped nothing: " + string.Join(" | ", roomy.Notes));
        }

        [TestMethod]
        public void A_dungeon_build_is_unchanged_by_the_height_source()
        {
            DungeonEntryDef Dungeon() => new DungeonEntryDef
            {
                Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
                Points = Enumerable.Range(1, 6).Select(i => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Clearance = 2, Curated = true }).ToList(),
                BossAnchor = new DungeonSpawnPointDef { Cell = 0x01500199u, X = 99, Clearance = 2, Curated = true },
                CreatureTypes = new List<string> { "Banderling" },
            };

            for (var seed = 0; seed < 10; seed++)
            {
                var without = DungeonPopulationBuilder.Build(Spec("filos_doom"), Dungeon(), Store(), Species(), LevelOf, w => 0, Limits(12), new Random(seed));
                var with = DungeonPopulationBuilder.Build(Spec("filos_doom"), Dungeon(), Store(), Species(), LevelOf, w => 0, Limits(12), new Random(seed),
                    movementHeightOf: w => 99f);

                CollectionAssert.AreEqual(without.Entries.Select(e => (e.Wcid, e.Point.Cell)).ToList(), with.Entries.Select(e => (e.Wcid, e.Point.Cell)).ToList(), $"seed {seed}");
                Assert.IsFalse(with.Notes.Any(n => n.StartsWith("outdoor population:", StringComparison.Ordinal)));
            }
        }

        // ---- the spawner wiring (source pins: TryPopulate and TryPlaceOnce need a live landblock) ----

        private static string Spawner() => PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");

        internal static string RoutingFault(string source)
        {
            var place = PooledLootSourceText.MethodBody(source, "void PlaceBatch()");
            var populate = PooledLootSourceText.MethodBody(source, "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)");

            if (!populate.Contains("OutdoorPopulation.PlacementGroups(plan.Entries", StringComparison.Ordinal)) return "the plan is not split per landblock";
            if (!place.Contains("TryPlace(run, stepLandblock,", StringComparison.Ordinal)) return "a step does not place on its own group's landblock";
            if (!place.Contains("stepLandblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));", StringComparison.Ordinal)) return "the chain does not continue on the group's landblock queue";
            if (!place.Contains("(stepLandblock, queue) = placementGroups.Dequeue();", StringComparison.Ordinal)) return "the chain never hops to the next landblock";
            return null;
        }

        internal static string PlaceOnceFault(string source)
        {
            var body = PooledLootSourceText.MethodBody(source, "private static PlaceOutcome TryPlaceOnce(");
            var guard = body.IndexOf("!ThreadRunGeometry.IsOnRunLandblock(run, creature.Location)", StringComparison.Ordinal);
            var stepGuard = body.IndexOf("creature.Location.LandblockId.Landblock != landblock.Id.Landblock", StringComparison.Ordinal);
            var bind = body.IndexOf("BindToOutdoorPerimeter(creature, outdoor, p);", StringComparison.Ordinal);
            var enter = body.IndexOf("creature.EnterWorld()", StringComparison.Ordinal);

            if (guard < 0) return "the run-landblock check is not ThreadRunGeometry.IsOnRunLandblock";
            if (stepGuard < 0) return "the step-landblock (threading) check is gone";
            if (bind < 0) return "outdoor creatures are not bound to the perimeter";
            if (enter < 0 || bind > enter) return "the binding is not written before EnterWorld";
            return null;
        }

        [TestMethod]
        public void Each_creature_is_placed_on_its_own_point_landblock_queue()
        {
            Assert.IsNull(RoutingFault(Spawner()));
        }

        [TestMethod]
        public void TryPlaceOnce_checks_the_run_footprint_and_binds_before_EnterWorld()
        {
            Assert.IsNull(PlaceOnceFault(Spawner()));
        }

        [TestMethod]
        public void The_spawner_wiring_checks_discriminate()
        {
            var source = Spawner();
            Assert.IsNotNull(RoutingFault(source.Replace("TryPlace(run, stepLandblock,", "TryPlace(run, landblock,")));
            Assert.IsNotNull(RoutingFault(source.Replace("stepLandblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));", "landblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));")));
            Assert.IsNotNull(PlaceOnceFault(source.Replace("!ThreadRunGeometry.IsOnRunLandblock(run, creature.Location)", "false")));

            var moved = source.Replace("BindToOutdoorPerimeter(creature, outdoor, p);", "")
                .Replace("// Nothing after EnterWorld.", "BindToOutdoorPerimeter(creature, outdoor, p); // Nothing after EnterWorld.");
            Assert.AreNotEqual(source.Replace("BindToOutdoorPerimeter(creature, outdoor, p);", ""), moved, "the mutation anchor must exist");
            Assert.IsNotNull(PlaceOnceFault(moved), "a binding written after EnterWorld");
        }
    }
}

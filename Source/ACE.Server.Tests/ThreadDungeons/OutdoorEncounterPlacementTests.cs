using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Outdoor encounter placement across a multi-landblock site (Open World Threads R22): every pick's action goes to
    /// the landblock its own point is on, a landblock the run copy does not hold drops only that pick, the ground probe
    /// is asked about every outdoor candidate at the type's clearance, and nearby points stay on the same landblock.
    /// </summary>
    [TestClass]
    public class OutdoorEncounterPlacementTests
    {
        private static readonly ushort[] Footprint = { 0x5456, 0x5556, 0x5457 };

        private static List<ACE.Server.ThreadDungeons.Defs.DungeonSpawnPointDef> SitePoints()
            => new List<ACE.Server.ThreadDungeons.Defs.DungeonSpawnPointDef>
            {
                Point(OutdoorCell(0x5456, 60, 60), 60, 60, kind: "outdoor"),
                Point(OutdoorCell(0x5556, 60, 60), 60, 60, kind: "outdoor"),
                Point(OutdoorCell(0x5457, 60, 60), 60, 60, kind: "outdoor"),
                Point(OutdoorCell(0x5457, 70, 60), 70, 60, kind: "outdoor"),
                Point(OutdoorCell(0x5456, 90, 90), 90, 90, kind: "interior"),
            };

        [TestMethod]
        public void Each_pick_is_queued_on_its_own_points_landblock_and_placed_there()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", placement: p => p.MinSpacing = 5f)), fake);
            var run = OutdoorRun(SitePoints(), max: 4);

            ThreadEncounterPass.OnRunPopulated(run);

            var picks = run.EncounterPlan.Picks;
            Assert.AreEqual(4, picks.Count, string.Join("\n", run.EncounterPlan.Dropped));
            CollectionAssert.AreEqual(picks.Select(p => p.Point.Landblock).ToList(), seams.Queued.Select(q => q.Landblock).ToList(), "one action per pick, on its point's landblock");
            Assert.IsTrue(picks.All(p => p.Point.Point.Kind == "outdoor"), "an interior point is never used");
            CollectionAssert.AreEquivalent(Footprint, seams.Queued.Select(q => q.Landblock).Distinct().ToList(), "the picks span all three landblocks");

            // Each queued action places the pick whose point is on the landblock it was queued on.
            foreach (var (landblock, action) in seams.Queued.ToList())
            {
                var before = fake.Placed.Count;
                action(null);
                Assert.AreEqual(before + 1, fake.Placed.Count);
                Assert.AreEqual(landblock, fake.Placed[before].Point.Landblock);
            }

            Assert.AreEqual(4, run.PlacedEncounters.Count);
        }

        [TestMethod]
        public void A_footprint_landblock_the_copy_does_not_hold_drops_only_its_picks()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", placement: p => p.MinSpacing = 5f)), fake);

            // The production enqueue, with the copy holding 0x5456 and 0x5556 but not 0x5457.
            seams.UseProductionEnqueueWithHeld(0x5456, 0x5556);

            var run = OutdoorRun(SitePoints(), max: 4);
            ThreadEncounterPass.OnRunPopulated(run);

            var picks = run.EncounterPlan.Picks;
            var held = picks.Where(p => p.Point.Landblock != 0x5457).ToList();
            var unheld = picks.Where(p => p.Point.Landblock == 0x5457).ToList();

            Assert.IsTrue(held.Count > 0 && unheld.Count > 0, "the plan must span held and unheld landblocks, or this proves nothing");

            // Picks on held landblocks survive, each posted to its own landblock id; picks on 0x5457 drop, naming it.
            CollectionAssert.AreEqual(held.Select(p => p.Point.Landblock).ToList(), seams.Queued.Select(q => q.Landblock).ToList());

            var drops = run.EncounterOutcomes.Where(l => l.Contains("is not loaded")).ToList();
            Assert.AreEqual(unheld.Count, drops.Count);
            Assert.IsTrue(drops.All(d => d.Contains("0x5457")));

            seams.Drain();
            Assert.AreEqual(held.Count, fake.Placed.Count);
            Assert.AreEqual(held.Count, run.PlacedEncounters.Count);
            Assert.IsTrue(fake.Placed.All(r => r.Point.Landblock != 0x5457));
        }

        [TestMethod]
        public void The_ground_probe_is_asked_about_open_ground_candidates_at_the_type_clearance_and_can_veto_one()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", placement: p => p.MinClearance = 6f)), fake);
            var points = SitePoints();
            var vetoed = points[1];
            seams.Probe = (c, r) => !ReferenceEquals(c.Point, vetoed);

            var run = OutdoorRun(points, max: 4);
            ThreadEncounterPass.OnRunPopulated(run);

            Assert.IsTrue(seams.Probed.Count > 0);
            Assert.IsTrue(seams.Probed.All(p => p.Radius == 6f));
            Assert.IsFalse(seams.Probed.Any(p => p.Point.Kind == "interior"), "an interior point is refused before any terrain read");
            Assert.IsFalse(run.EncounterPlan.Picks.Any(p => ReferenceEquals(p.Point.Point, vetoed)));
            Assert.AreEqual(3, run.EncounterPlan.Picks.Count, "the other three open points still fill slots");
        }

        [TestMethod]
        public void Candidate_distances_are_straight_line_in_global_metres_outdoors()
        {
            var run = OutdoorRun(SitePoints());
            var candidates = ThreadEncounterPass.BuildCandidates(run);

            // Entry at 0x5456 (10, 10); a point at 0x5556 (60, 60) is 192 + 50 m east and 50 m north.
            var east = candidates.Single(c => c.Landblock == 0x5556);
            Assert.AreEqual((float)System.Math.Sqrt(242.0 * 242.0 + 50.0 * 50.0), east.EntryDistance, 0.01f);

            // Boss anchor at 0x5456 (100, 100).
            var home = candidates.Single(c => c.Landblock == 0x5456 && c.Point.Kind == "outdoor");
            Assert.AreEqual((float)System.Math.Sqrt(40.0 * 40.0 * 2), home.BossDistance, 0.01f);
        }

        [TestMethod]
        public void Nearby_points_are_on_the_same_landblock_open_ground_nearest_first()
        {
            var points = SitePoints();
            points.Add(Point(OutdoorCell(0x5457, 100, 60), 100, 60, kind: "outdoor"));
            var run = OutdoorRun(points);
            var around = ThreadEncounterPass.BuildCandidates(run).Single(c => c.Point == points[2]);

            var nearby = ThreadEncounterPass.NearbyPoints(run, around, 50f);

            CollectionAssert.AreEqual(new[] { points[3], points[5] }, nearby.ToList());
            Assert.AreEqual(1, ThreadEncounterPass.NearbyPoints(run, around, 15f).Count, "the radius bounds it");
        }

        [TestMethod]
        public void The_probe_height_tolerance_is_half_the_radius_floored_at_1_5_m_and_capped_at_the_scatters()
        {
            Assert.AreEqual(1.5f, ThreadEncounterPass.ProbeMaxDropZ(0f));
            Assert.AreEqual(1.5f, ThreadEncounterPass.ProbeMaxDropZ(2f));
            Assert.AreEqual(3f, ThreadEncounterPass.ProbeMaxDropZ(6f));
            Assert.AreEqual(ThreadDungeonSpawner.ScatterMaxDropZ, ThreadEncounterPass.ProbeMaxDropZ(100f));
            Assert.AreEqual(1.5f, ThreadEncounterPass.ProbeMaxDropZ(float.NaN));

            Assert.IsTrue(ThreadDungeonSpawner.GroundWithin(52f, 50f, 3f));
            Assert.IsFalse(ThreadDungeonSpawner.GroundWithin(53.1f, 50f, 3f), "a 3.1 m step fails a 3 m tolerance");
            Assert.IsTrue(ThreadDungeonSpawner.ScatterGroundAcceptable(53.1f, 50f), "the scatter's own 10 m rule is unchanged");
            Assert.IsFalse(ThreadDungeonSpawner.GroundWithin(float.NaN, 50f, 10f));
        }

        [TestMethod]
        public void The_ring_probe_is_the_point_and_four_compass_points()
        {
            CollectionAssert.AreEqual(new[] { (0f, 0f), (3f, 0f), (-3f, 0f), (0f, 3f), (0f, -3f) }, ThreadEncounterPass.RingOffsets(3f).ToList());
            CollectionAssert.AreEqual(new[] { (0f, 0f) }, ThreadEncounterPass.RingOffsets(0f).ToList());
        }
    }
}

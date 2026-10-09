using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadCachePlacementTests
    {
        private const uint OwnerCell = 0x01500100;
        private const float OX = 100f, OY = 100f, OZ = 10f;
        private const float Tol = 1e-3f;

        private static readonly (float X, float Y, float Z)[] None = new (float, float, float)[0];

        private static List<CacheCandidate> Build(float qz = 0f, float qw = 1f, IReadOnlyList<DungeonSpawnPointDef> curated = null, IReadOnlyList<(float X, float Y, float Z)> exclusions = null)
            => ThreadCachePlacement.BuildCandidates(OwnerCell, OX, OY, OZ, qz, qw, curated ?? new List<DungeonSpawnPointDef>(), exclusions ?? None);

        private static DungeonSpawnPointDef P(uint cell, float x, float y, float z, bool curated = true)
            => new DungeonSpawnPointDef { Cell = cell, X = x, Y = y, Z = z, Curated = curated };

        [TestMethod]
        public void Stages_come_in_spec_order_with_no_curated_points()
        {
            var stages = Build().Select(c => c.Stage).ToList();

            Assert.AreEqual(18, stages.Count, "1 in front + 16 ring + 0 curated + 1 owner spot");
            Assert.AreEqual(CacheCandidateStage.InFront, stages[0]);
            Assert.IsTrue(stages.Skip(1).Take(16).All(s => s == CacheCandidateStage.Ring));
            Assert.AreEqual(CacheCandidateStage.OwnerSpot, stages[17]);
        }

        [TestMethod]
        public void In_front_uses_Position_InFrontOf_math_and_asks_for_a_cell_recompute()
        {
            var front = Build()[0];

            Assert.AreEqual(OX, front.X, Tol);
            Assert.AreEqual(OY + 2f, front.Y, Tol);
            Assert.AreEqual(OZ + 0.05f, front.Z, Tol);
            Assert.AreEqual(OwnerCell, front.Cell);
            Assert.IsTrue(front.RecomputeCell);

            var s = (float)Math.Sqrt(0.5);
            var turned = Build(qz: s, qw: s)[0];
            Assert.AreEqual(OX - 2f, turned.X, Tol, "a 90 degree heading moves -X, as InFrontOf does");
            Assert.AreEqual(OY, turned.Y, Tol);
        }

        [TestMethod]
        public void Ring_is_ordered_by_angular_distance_positive_offset_first_and_near_radius_first()
        {
            var ring = Build().Skip(1).Take(16).ToList();
            var expected = new (double Deg, float R)[]
            {
                (0, 1.5f), (0, 3f), (45, 1.5f), (45, 3f), (-45, 1.5f), (-45, 3f), (90, 1.5f), (90, 3f),
                (-90, 1.5f), (-90, 3f), (135, 1.5f), (135, 3f), (-135, 1.5f), (-135, 3f), (180, 1.5f), (180, 3f),
            };

            for (var i = 0; i < 16; i++)
            {
                var a = expected[i].Deg * Math.PI / 180.0;
                Assert.AreEqual((float)(OX - Math.Sin(a) * expected[i].R), ring[i].X, Tol, $"ring {i} x");
                Assert.AreEqual((float)(OY + Math.Cos(a) * expected[i].R), ring[i].Y, Tol, $"ring {i} y");
                Assert.IsTrue(ring[i].RecomputeCell);
            }
        }

        [TestMethod]
        public void Curated_points_within_15m_on_this_landblock_nearest_first()
        {
            var points = new List<DungeonSpawnPointDef>
            {
                P(0x01500105, 105f, 100f, 10f),
                P(0x01500105, 120f, 100f, 10f),
                P(0x01500105, 110f, 100f, 10f),
                P(0x01510100, 103f, 100f, 10f),
                P(0x01500105, 102f, 100f, 10f, curated: false),
                P(0x01500105, 115f, 100f, 10f),
            };

            var curated = Build(curated: points).Where(c => c.Stage == CacheCandidateStage.Curated).ToList();

            CollectionAssert.AreEqual(new[] { 105f, 110f, 115f }, curated.Select(c => c.X).ToArray(), "15 m is inclusive; other landblocks and non-curated points are out");
            Assert.IsTrue(curated.All(c => c.Cell == 0x01500105 && !c.RecomputeCell));
        }

        [TestMethod]
        public void Owner_spot_is_last_and_exact()
        {
            var last = Build().Last();

            Assert.AreEqual(CacheCandidateStage.OwnerSpot, last.Stage);
            Assert.AreEqual(OX, last.X);
            Assert.AreEqual(OY, last.Y);
            Assert.AreEqual(OZ, last.Z);
            Assert.IsFalse(last.RecomputeCell);
        }

        [TestMethod]
        public void Exclusions_drop_every_candidate_closer_than_1_5m()
        {
            var atFront = new[] { (OX, OY + 2f, OZ + 0.05f) };
            var kept = Build(exclusions: atFront);

            Assert.AreEqual(13, kept.Count, "in front, ring 0 at 1.5 and 3, and ring +-45 at 1.5 are all within 1.5 m of it");
            Assert.IsFalse(kept.Any(c => ThreadCachePlacement.IsExcluded(c.X, c.Y, c.Z, atFront)));

            var atOwner = new[] { (OX, OY, OZ) };
            var withPortal = Build(exclusions: atOwner);
            Assert.IsFalse(withPortal.Any(c => c.Stage == CacheCandidateStage.OwnerSpot), "the summoned exit sits on the owner's spot");
            Assert.AreEqual(17, withPortal.Count, "ring points at 1.5 m (plus the bump height) are not closer than 1.5 m");
        }

        [TestMethod]
        public void Exclusion_boundary_is_exclusive()
        {
            var ex = new[] { (0f, 0f, 0f) };

            Assert.IsFalse(ThreadCachePlacement.IsExcluded(1.5f, 0f, 0f, ex));
            Assert.IsTrue(ThreadCachePlacement.IsExcluded(1.49f, 0f, 0f, ex));
            Assert.IsFalse(ThreadCachePlacement.IsExcluded(0f, 0f, 0f, null));
        }
    }
}

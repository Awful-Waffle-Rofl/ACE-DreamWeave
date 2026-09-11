using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventGeometry"/> (TECH-DESIGN 2.4, 5.6). Pure coordinate
    /// arithmetic: no landblock, no physics, no terrain, no live object (D6).
    /// </summary>
    [TestClass]
    public class WorldEventGeometryTests
    {
        // An OUTDOOR cell (LandblockId.Indoors is "cell bits >= 0x100", and SetPosition is a no-op indoors)
        // in the middle of its landblock, so a 45 m offset in any direction stays in the same block and the
        // distance assertions are never confused by a landblock transition rebasing X/Y.
        // 0x25 = 37 = cellX 4 * 8 + cellY 4 + 1, the cell that (96, 96) falls in at CellLength 24.
        private const uint OutdoorCell = 0x016C0025;

        private const float CentreX = 96f;
        private const float CentreY = 96f;
        private const float CentreZ = 42f;

        private const uint TestInstance = 7u;

        private static Position Centre()
        {
            return new Position(OutdoorCell, CentreX, CentreY, CentreZ, 0f, 0f, 0f, 1f, TestInstance);
        }

        /// <summary>
        /// 2D distance from the fixed centre. Only valid while the offset keeps the position in the SAME
        /// landblock, which every case here does by construction (see OutdoorCell).
        /// </summary>
        private static double DistanceFromCentre(Position pos)
        {
            var dx = pos.PositionX - CentreX;
            var dy = pos.PositionY - CentreY;

            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double AngleFromCentre(Position pos)
        {
            return Math.Atan2(pos.PositionX - CentreX, pos.PositionY - CentreY);
        }

        /// <summary>Smallest absolute difference between two bearings, in radians.</summary>
        private static double AngularGap(double a, double b)
        {
            var diff = Math.Abs(a - b) % (Math.PI * 2);

            return diff > Math.PI ? Math.PI * 2 - diff : diff;
        }

        // ---- (a) counts ------------------------------------------------------------------------------

        [TestMethod]
        public void Ring_ReturnsExactlyThePointCount()
        {
            foreach (var points in new[] { 1, 2, 3, 4, 8, 17 })
                Assert.AreEqual(points, WorldEventGeometry.Ring(Centre(), 25f, points, 0f, new Random(1)).Count,
                    $"ring of {points}");
        }

        [TestMethod]
        public void Edges_NeverReturnsMoreThanThePointCount()
        {
            for (var points = 1; points <= 24; points++)
            {
                for (var seed = 0; seed < 8; seed++)
                {
                    var placed = WorldEventGeometry.Edges(Centre(), 45f, points, 2f, new Random(seed));

                    Assert.IsTrue(placed.Count <= points, $"{points} points, seed {seed}: got {placed.Count}");
                    Assert.AreEqual(points, placed.Count, $"{points} points, seed {seed}");
                }
            }
        }

        [TestMethod]
        public void Ring_AndEdges_RefuseNonsenseInput()
        {
            Assert.AreEqual(0, WorldEventGeometry.Ring(null, 25f, 4, 0f, new Random(1)).Count);
            Assert.AreEqual(0, WorldEventGeometry.Edges(null, 25f, 4, 0f, new Random(1)).Count);
            Assert.AreEqual(0, WorldEventGeometry.Ring(Centre(), 25f, 0, 0f, new Random(1)).Count);
            Assert.AreEqual(0, WorldEventGeometry.Edges(Centre(), 25f, -3, 0f, new Random(1)).Count);
            Assert.AreEqual(0, WorldEventGeometry.Single(null).Count);
        }

        // ---- (b) ring spacing and radius ---------------------------------------------------------------

        [TestMethod]
        public void Ring_WithoutJitter_SpacesPointsEvenly()
        {
            const int points = 6;

            var placed = WorldEventGeometry.Ring(Centre(), 30f, points, 0f, new Random(4242));

            var expectedGap = Math.PI * 2 / points;

            var angles = placed.Select(AngleFromCentre).ToList();

            for (var i = 0; i < points; i++)
            {
                var gap = AngularGap(angles[i], angles[(i + 1) % points]);

                Assert.AreEqual(expectedGap, gap, 1e-3, $"gap between point {i} and {i + 1}");
            }
        }

        [TestMethod]
        public void Ring_WithoutJitter_PlacesEveryPointAtExactlyTheRadius()
        {
            var placed = WorldEventGeometry.Ring(Centre(), 30f, 8, 0f, new Random(99));

            foreach (var pos in placed)
                Assert.AreEqual(30.0, DistanceFromCentre(pos), 1e-2);
        }

        [TestMethod]
        public void Ring_WithJitter_StaysInsideRadiusPlusMinusJitter()
        {
            const float radius = 30f;
            const float jitter = 2f;

            for (var seed = 0; seed < 25; seed++)
            {
                foreach (var pos in WorldEventGeometry.Ring(Centre(), radius, 8, jitter, new Random(seed)))
                {
                    var distance = DistanceFromCentre(pos);

                    Assert.IsTrue(distance >= radius - jitter - 1e-2 && distance <= radius + jitter + 1e-2,
                        $"seed {seed}: distance {distance} outside [{radius - jitter}, {radius + jitter}]");
                }
            }
        }

        // ---- (c) edges cluster onto 2 or 3 arcs --------------------------------------------------------

        [TestMethod]
        public void Edges_ClustersPositionsOntoTwoOrThreeArcs()
        {
            // With 12 positions on 2 or 3 arcs 30 degrees wide, every position must fall within 30 degrees of
            // the first position of its own arc, and the arcs themselves are 120 or 180 degrees apart - so
            // greedy clustering at a 30 degree radius recovers exactly the arc count. That clustering is
            // what makes this "walk in from the edges" rather than "surround the anchor".
            for (var seed = 0; seed < 20; seed++)
            {
                var angles = WorldEventGeometry.Edges(Centre(), 45f, 12, 0f, new Random(seed))
                    .Select(AngleFromCentre)
                    .ToList();

                var clusters = new List<double>();

                foreach (var angle in angles)
                {
                    if (!clusters.Any(c => AngularGap(c, angle) <= Math.PI / 6 + 1e-3))
                        clusters.Add(angle);
                }

                Assert.IsTrue(clusters.Count >= 2 && clusters.Count <= 3,
                    $"seed {seed}: expected 2-3 arcs, found {clusters.Count}");
            }
        }

        [TestMethod]
        public void Edges_WithoutJitter_PlacesEveryPointAtExactlyTheRadius()
        {
            foreach (var pos in WorldEventGeometry.Edges(Centre(), 45f, 9, 0f, new Random(11)))
                Assert.AreEqual(45.0, DistanceFromCentre(pos), 1e-2);
        }

        // ---- (d) determinism -----------------------------------------------------------------------------

        [TestMethod]
        public void SeededOverloads_AreDeterministic()
        {
            var ringA = WorldEventGeometry.Ring(Centre(), 25f, 5, 2f, new Random(1234));
            var ringB = WorldEventGeometry.Ring(Centre(), 25f, 5, 2f, new Random(1234));

            AssertSameLayout(ringA, ringB);

            var edgesA = WorldEventGeometry.Edges(Centre(), 45f, 7, 2f, new Random(1234));
            var edgesB = WorldEventGeometry.Edges(Centre(), 45f, 7, 2f, new Random(1234));

            AssertSameLayout(edgesA, edgesB);
        }

        [TestMethod]
        public void SeededOverloads_DifferentSeedsGiveDifferentLayouts()
        {
            var a = WorldEventGeometry.Ring(Centre(), 25f, 5, 2f, new Random(1));
            var b = WorldEventGeometry.Ring(Centre(), 25f, 5, 2f, new Random(2));

            var identical = a.Zip(b, (x, y) => Math.Abs(x.PositionX - y.PositionX) < 1e-6
                                            && Math.Abs(x.PositionY - y.PositionY) < 1e-6).All(same => same);

            Assert.IsFalse(identical, "two different seeds produced an identical ring");
        }

        private static void AssertSameLayout(IReadOnlyList<Position> a, IReadOnlyList<Position> b)
        {
            Assert.AreEqual(a.Count, b.Count);

            for (var i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].PositionX, b[i].PositionX, 1e-6, $"X at {i}");
                Assert.AreEqual(a[i].PositionY, b[i].PositionY, 1e-6, $"Y at {i}");
                Assert.AreEqual(a[i].Cell, b[i].Cell, $"cell at {i}");
            }
        }

        // ---- (e) the returned positions are new, instanced and cell-consistent -------------------------

        [TestMethod]
        public void EveryPosition_CarriesTheCentreInstanceAndZ_AndNeverMutatesTheCentre()
        {
            var centre = Centre();

            var placed = WorldEventGeometry.Ring(centre, 30f, 6, 2f, new Random(7))
                .Concat(WorldEventGeometry.Edges(centre, 45f, 6, 2f, new Random(7)))
                .Concat(WorldEventGeometry.Single(centre))
                .ToList();

            foreach (var pos in placed)
            {
                Assert.AreNotSame(centre, pos, "the centre itself must never be handed back");
                Assert.AreEqual(TestInstance, pos.Instance);
                Assert.AreEqual(CentreZ, pos.PositionZ, 1e-4, "Z is the centre's Z; terrain snapping happens at spawn time");
            }

            Assert.AreEqual(CentreX, centre.PositionX, 1e-6, "the centre was mutated");
            Assert.AreEqual(CentreY, centre.PositionY, 1e-6, "the centre was mutated");
            Assert.AreEqual(OutdoorCell, centre.Cell, "the centre's cell was mutated");
        }

        [TestMethod]
        public void Single_ReturnsTheCentreCoordinatesAndInstance()
        {
            var centre = Centre();

            var placed = WorldEventGeometry.Single(centre);

            Assert.AreEqual(1, placed.Count);
            Assert.AreEqual(CentreX, placed[0].PositionX, 1e-6);
            Assert.AreEqual(CentreY, placed[0].PositionY, 1e-6);
            Assert.AreEqual(CentreZ, placed[0].PositionZ, 1e-6);
            Assert.AreEqual(TestInstance, placed[0].Instance);
            Assert.AreEqual(OutdoorCell, placed[0].Cell);
        }

        [TestMethod]
        public void OutdoorPositions_HaveTheirLandCellRecomputed()
        {
            // 30 m east of a cell-boundary-aligned centre lands in a different land cell of the same
            // landblock; SetPosition is what keeps the cell field consistent with the coordinates.
            var placed = WorldEventGeometry.Ring(Centre(), 30f, 4, 0f, new Random(1));

            Assert.IsTrue(placed.Any(p => p.Cell != OutdoorCell),
                "no returned position had its land cell recomputed");

            foreach (var pos in placed)
            {
                // Position.SetLandCell: cellID = (X / CellLength) * CellSide + (Y / CellLength) + 1.
                var expectedCell = (uint)pos.PositionX / 24u * 8u + (uint)pos.PositionY / 24u + 1u;

                Assert.AreEqual(expectedCell, pos.Cell & 0xFFFFu, $"cell bits disagree with X/Y at {pos.ToLOCString()}");
                Assert.AreEqual(OutdoorCell >> 16, pos.Cell >> 16, "stayed in the same landblock");
            }
        }

        // ---- (f) jitter helper ---------------------------------------------------------------------------

        [TestMethod]
        public void Jitter_StaysWithinRangeAndKeepsInstanceAndFacing()
        {
            var basePosition = Centre();

            for (var seed = 0; seed < 25; seed++)
            {
                var jittered = WorldEventGeometry.Jitter(basePosition, 2f, new Random(seed));

                var dx = jittered.PositionX - CentreX;
                var dy = jittered.PositionY - CentreY;

                Assert.IsTrue(Math.Sqrt(dx * dx + dy * dy) <= 2f + 1e-3, $"seed {seed}");
                Assert.AreEqual(TestInstance, jittered.Instance);
                Assert.AreEqual(basePosition.RotationW, jittered.RotationW, 1e-6, "facing must be preserved");
            }

            Assert.IsNull(WorldEventGeometry.Jitter(null, 2f, new Random(1)));
        }

        // ---- (g) disc (WP-16) ---------------------------------------------------------------------------

        [TestMethod]
        public void Disc_ReturnsExactlyThePointCount()
        {
            foreach (var points in new[] { 1, 2, 3, 4, 8, 17 })
                Assert.AreEqual(points, WorldEventGeometry.Disc(Centre(), 25f, points, new Random(1)).Count,
                    $"disc of {points}");
        }

        [TestMethod]
        public void Disc_EveryPointIsWithinTheRadius()
        {
            const float radius = 25f;

            for (var seed = 0; seed < 25; seed++)
            {
                foreach (var pos in WorldEventGeometry.Disc(Centre(), radius, 12, new Random(seed)))
                {
                    var distance = DistanceFromCentre(pos);

                    Assert.IsTrue(distance <= radius + 1e-3, $"seed {seed}: distance {distance} exceeds {radius}");
                }
            }
        }

        [TestMethod]
        public void Disc_CarriesTheCentreInstanceAndZ_AndNeverMutatesTheCentre()
        {
            var centre = Centre();

            var placed = WorldEventGeometry.Disc(centre, 25f, 8, new Random(3));

            foreach (var pos in placed)
            {
                Assert.AreNotSame(centre, pos, "the centre itself must never be handed back");
                Assert.AreEqual(TestInstance, pos.Instance);
                Assert.AreEqual(CentreZ, pos.PositionZ, 1e-4, "Z is the centre's Z; terrain snapping happens at spawn time");
            }

            Assert.AreEqual(CentreX, centre.PositionX, 1e-6, "the centre was mutated");
            Assert.AreEqual(CentreY, centre.PositionY, 1e-6, "the centre was mutated");
        }

        [TestMethod]
        public void Disc_SeededOverload_IsDeterministic()
        {
            var a = WorldEventGeometry.Disc(Centre(), 25f, 6, new Random(4242));
            var b = WorldEventGeometry.Disc(Centre(), 25f, 6, new Random(4242));

            AssertSameLayout(a, b);
        }

        [TestMethod]
        public void Disc_DifferentSeedsGiveDifferentLayouts()
        {
            var a = WorldEventGeometry.Disc(Centre(), 25f, 6, new Random(1));
            var b = WorldEventGeometry.Disc(Centre(), 25f, 6, new Random(2));

            var identical = a.Zip(b, (x, y) => Math.Abs(x.PositionX - y.PositionX) < 1e-6
                                            && Math.Abs(x.PositionY - y.PositionY) < 1e-6).All(same => same);

            Assert.IsFalse(identical, "two different seeds produced an identical disc");
        }

        [TestMethod]
        public void Disc_RefusesNonsenseInput()
        {
            Assert.AreEqual(0, WorldEventGeometry.Disc(null, 25f, 4, new Random(1)).Count);
            Assert.AreEqual(0, WorldEventGeometry.Disc(Centre(), 25f, 0, new Random(1)).Count);
            Assert.AreEqual(0, WorldEventGeometry.Disc(Centre(), 25f, -3, new Random(1)).Count);
        }
    }
}

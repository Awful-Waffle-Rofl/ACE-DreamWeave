using System.Linq;

using ACE.Entity;
using ACE.Server.MlDigsite;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The digsite reward chest is kept off the landblock edge (prod 2026-10-21, digsite 39: a chest at local
    /// X 191.918 was nudged into the neighbouring landblock by spawn physics and discarded). Pure geometry only:
    /// no PropertyManager read, no live world.
    /// </summary>
    [TestClass]
    public class MlDigsiteChestEdgePlacementTests
    {
        private const uint Block = 0x11B90000u;
        private const float Margin = MlDigsiteRewards.LandblockEdgeMarginMetres;

        private static Position At(float x, float y)
        {
            // The Position constructor itself recomputes the outdoor cell from X/Y (SetPosition).
            return new Position(Block | 0x0001u, x, y, 10f, 0f, 0f, 0f, 1f, 0u);
        }

        private static uint ExpectedCell(float x, float y)
        {
            return (uint)((int)(x / 24) * 8 + (int)(y / 24) + 1);
        }

        private static Position Place(Position source, float dx, float dy)
        {
            return MlDigsiteRewards.PlaceInsideLandblock(source, source, dx, dy, Margin);
        }

        private static System.Collections.Generic.List<Position> Candidates(Position planned)
        {
            return MlDigsiteRewards.PlacementCandidates(planned, planned);
        }

        private static void AssertNeighbourClamped(Position anchor, Position planned, float expectedX, float expectedY)
        {
            Assert.AreNotEqual(anchor.LandblockId.Raw & 0xFFFF0000u, planned.LandblockId.Raw & 0xFFFF0000u, "fixture is not in a neighbour");

            var p = MlDigsiteRewards.PlaceInsideLandblock(planned, anchor, 0f, 0f, Margin);

            Assert.AreEqual(expectedX, p.PositionX, 0.0001f);
            Assert.AreEqual(expectedY, p.PositionY, 0.0001f);
            AssertInside(p);

            foreach (var c in MlDigsiteRewards.PlacementCandidates(planned, anchor))
                AssertInside(c);
        }

        private static void AssertInside(Position p)
        {
            Assert.IsTrue(p.PositionX >= Margin && p.PositionX <= 192f - Margin, "X " + p.PositionX);
            Assert.IsTrue(p.PositionY >= Margin && p.PositionY <= 192f - Margin, "Y " + p.PositionY);
            Assert.AreEqual(Block, p.LandblockId.Raw & 0xFFFF0000u, "landblock id changed");
            Assert.AreEqual(ExpectedCell(p.PositionX, p.PositionY), p.LandblockId.Raw & 0xFFFFu, "cell id not recomputed");
        }

        [TestMethod]
        public void ProdCase_X191_918_IsPulledInside()
        {
            var p = Place(At(191.918274f, 71.439667f), 0f, 0f);

            Assert.AreEqual(192f - Margin, p.PositionX, 0.0001f);
            Assert.AreEqual(71.439667f, p.PositionY, 0.0001f);
            AssertInside(p);
        }

        [TestMethod]
        public void NearEachEdge_IsPulledToTheMargin()
        {
            var west = Place(At(0.1f, 90f), 0f, 0f);
            var east = Place(At(191.9f, 90f), 0f, 0f);
            var south = Place(At(90f, 0.1f), 0f, 0f);
            var north = Place(At(90f, 191.9f), 0f, 0f);

            Assert.AreEqual(Margin, west.PositionX, 0.0001f);
            Assert.AreEqual(192f - Margin, east.PositionX, 0.0001f);
            Assert.AreEqual(Margin, south.PositionY, 0.0001f);
            Assert.AreEqual(192f - Margin, north.PositionY, 0.0001f);

            foreach (var p in new[] { west, east, south, north })
                AssertInside(p);
        }

        [TestMethod]
        public void Corners_AreClampedOnBothAxes()
        {
            foreach (var (x, y) in new[] { (0f, 0f), (191.99f, 0f), (0f, 191.99f), (191.99f, 191.99f) })
            {
                var p = Place(At(x, y), 0f, 0f);

                AssertInside(p);
                Assert.IsTrue(p.PositionX == Margin || p.PositionX == 192f - Margin);
                Assert.IsTrue(p.PositionY == Margin || p.PositionY == 192f - Margin);
            }
        }

        [TestMethod]
        public void Interior_IsUnchanged()
        {
            var src = At(96f, 40.5f);
            var p = Place(src, 0f, 0f);

            Assert.AreEqual(96f, p.PositionX, 0.0001f);
            Assert.AreEqual(40.5f, p.PositionY, 0.0001f);
            Assert.AreEqual(10f, p.PositionZ, 0.0001f);
            AssertInside(p);
        }

        [TestMethod]
        public void Source_IsNotMutated()
        {
            var src = At(191.9f, 0.2f);

            Place(src, 0f, 0f);

            Assert.AreEqual(191.9f, src.PositionX, 0.0001f);
            Assert.AreEqual(0.2f, src.PositionY, 0.0001f);
            Assert.AreEqual(ExpectedCell(191.9f, 0.2f), src.LandblockId.Raw & 0xFFFFu);
        }

        [TestMethod]
        public void OffsetThatWouldCrossTheEdge_StaysInTheSameLandblock()
        {
            var p = Place(At(190f, 190f), 4f, 4f);

            AssertInside(p);
        }

        [TestMethod]
        public void IndoorPosition_IsReturnedUnchanged()
        {
            var indoor = new Position(0x01AB0105u, 0.5f, 191.9f, 0f, 0f, 0f, 0f, 1f, 0u);
            var p = Place(indoor, 0f, 0f);

            Assert.AreEqual(0x01AB0105u, p.LandblockId.Raw);
            Assert.AreEqual(0.5f, p.PositionX, 0.0001f);
            Assert.AreEqual(191.9f, p.PositionY, 0.0001f);
        }

        [TestMethod]
        public void Candidates_FirstIsClamped_AllInsideSameLandblock()
        {
            var list = Candidates(At(191.918f, 0.05f));

            Assert.IsTrue(list.Count > 1, "no retry positions");
            Assert.AreEqual(192f - Margin, list[0].PositionX, 0.0001f);
            Assert.AreEqual(Margin, list[0].PositionY, 0.0001f);

            foreach (var p in list)
                AssertInside(p);

            Assert.IsTrue(list.Skip(1).Any(p => p.PositionX != list[0].PositionX || p.PositionY != list[0].PositionY),
                "retry positions are all identical to the first");
        }

        [TestMethod]
        public void PlannedInTheEastNeighbour_IsClampedInTheAnchorsFrame()
        {
            // A helper scatter point past the east edge is normalised into the neighbour (0x12B9) at local X ~1.
            var anchor = At(191.5f, 50f);
            var planned = new Position(0x12B90001u, 1.0f, 50f, 10f, 0f, 0f, 0f, 1f, 0u);

            Assert.AreEqual(0x12B90000u, planned.LandblockId.Raw & 0xFFFF0000u, "fixture is not in the neighbour");

            var p = MlDigsiteRewards.PlaceInsideLandblock(planned, anchor, 0f, 0f, Margin);

            Assert.AreEqual(192f - Margin, p.PositionX, 0.0001f);
            Assert.AreEqual(50f, p.PositionY, 0.0001f);
            AssertInside(p);

            foreach (var c in MlDigsiteRewards.PlacementCandidates(planned, anchor))
                AssertInside(c);
        }

        [TestMethod]
        public void PlannedInTheWestNeighbour_IsClampedInTheAnchorsFrame()
        {
            var anchor = At(0.5f, 50f);
            var planned = new Position(0x10B90001u, 190f, 50f, 10f, 0f, 0f, 0f, 1f, 0u);

            AssertNeighbourClamped(anchor, planned, Margin, 50f);
        }

        [TestMethod]
        public void PlannedInTheSouthNeighbour_IsClampedInTheAnchorsFrame()
        {
            var anchor = At(50f, 0.5f);
            var planned = new Position(0x11B80001u, 50f, 190f, 10f, 0f, 0f, 0f, 1f, 0u);

            AssertNeighbourClamped(anchor, planned, 50f, Margin);
        }

        [TestMethod]
        public void PlannedInADiagonalNeighbour_IsClampedInTheAnchorsFrame()
        {
            var anchor = At(191.5f, 191.5f);
            var planned = new Position(0x12BA0001u, 1.0f, 1.0f, 10f, 0f, 0f, 0f, 1f, 0u);

            AssertNeighbourClamped(anchor, planned, 192f - Margin, 192f - Margin);
        }

        [TestMethod]
        public void Candidates_HaveNoDuplicates_AtACorner()
        {
            var list = Candidates(At(191.99f, 191.99f));

            var keys = list.Select(p => (p.Cell, p.PositionX, p.PositionY)).ToList();

            Assert.AreEqual(keys.Count, keys.Distinct().Count());
            Assert.IsTrue(list.Count > 1);
        }
    }
}

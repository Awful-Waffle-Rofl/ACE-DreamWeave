using System;
using System.Linq;

using ACE.Entity;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventArena"/>. Pure coordinate arithmetic, no live object.
    /// </summary>
    [TestClass]
    public class WorldEventArenaTests
    {
        // Outdoor cells (see WorldEventGeometryTests for why 0x25 is a valid outdoor cell).
        private const uint CellHere = 0x016C0025;
        private const uint CellNorth = 0x016D0001;   // landblock Y + 1

        private static Position At(uint cell, float x, float y, float z, uint instance = 7u)
            => new Position(cell, x, y, z, 0f, 0f, 0f, 1f, instance);

        private static WorldEventArena Arena(float x = 96f, float y = 96f, float radius = 30f, float half = 15f)
            => new WorldEventArena(At(CellHere, x, y, 42f), radius, half);

        [TestMethod]
        public void Contains_InsideAndBoundary()
        {
            var a = Arena();

            Assert.IsTrue(a.Contains(At(CellHere, 96f, 96f, 42f)));
            Assert.IsTrue(a.Contains(At(CellHere, 96f + 30f, 96f, 42f)), "the rim is inside (<=)");
            Assert.IsFalse(a.Contains(At(CellHere, 96f + 30.5f, 96f, 42f)));
            Assert.IsFalse(a.Contains(null));
        }

        [TestMethod]
        public void Contains_InstanceMismatch_IsOutside()
        {
            var a = Arena();

            Assert.IsFalse(a.Contains(At(CellHere, 96f, 96f, 42f, instance: 8u)),
                "a realm copy of the same landblock is a different place");
        }

        [TestMethod]
        public void Contains_HeightLimit_IsInclusive()
        {
            var a = Arena(half: 15f);

            Assert.IsTrue(a.Contains(At(CellHere, 96f, 96f, 42f + 15f)));
            Assert.IsTrue(a.Contains(At(CellHere, 96f, 96f, 42f - 15f)));
            Assert.IsFalse(a.Contains(At(CellHere, 96f, 96f, 42f + 15.5f)));
            Assert.IsFalse(a.Contains(At(CellHere, 96f, 96f, 42f - 15.5f)));
        }

        [TestMethod]
        public void Contains_CrossLandblockOffsets_UseTheSharedFrame()
        {
            // Centre 2 m from the north edge of its block; the player stands 4 m north of it, in the next block.
            var a = new WorldEventArena(At(CellHere, 96f, 190f, 42f), 10f, 15f);

            var near = At(CellNorth, 96f, 2f, 42f);      // 4 m away across the block edge
            var far = At(CellNorth, 96f, 20f, 42f);      // 22 m away

            Assert.IsTrue(a.Contains(near));
            Assert.IsFalse(a.Contains(far));
        }

        [TestMethod]
        public void RingPointCount_ClampsAndRounds()
        {
            var a = Arena(radius: 30f);   // circumference ~188.5

            Assert.AreEqual(19, a.RingPointCount(10, 12, 40), "ceil(188.5 / 10)");
            Assert.AreEqual(40, a.RingPointCount(1, 12, 40), "clamped to max");
            Assert.AreEqual(12, a.RingPointCount(100, 12, 40), "clamped to min");
            Assert.AreEqual(12, a.RingPointCount(0, 12, 40), "non-positive spacing -> min");
            Assert.AreEqual(12, Arena(radius: 0f).RingPointCount(10, 12, 40));
        }

        [TestMethod]
        public void RingPoints_AreEvenlySpacedOnTheRim()
        {
            var a = Arena(radius: 30f);
            var pts = a.RingPoints(10, 12, 40, new Random(3));

            Assert.AreEqual(19, pts.Count);

            foreach (var p in pts)
            {
                Assert.AreEqual(30.0, a.Centre.Distance2D(p), 0.01);
                Assert.AreEqual(42f, p.PositionZ, 0.0001f, "Z stays the centre's");
                Assert.AreEqual(7u, p.Instance);
            }
        }

        [TestMethod]
        public void CrossesBlockEdge_DetectsEachSide()
        {
            Assert.IsFalse(Arena(x: 96f, y: 96f, radius: 30f).CrossesBlockEdge());
            Assert.IsFalse(Arena(x: 30f, y: 96f, radius: 30f).CrossesBlockEdge(), "touching the edge is not crossing");
            Assert.IsTrue(Arena(x: 29f, y: 96f, radius: 30f).CrossesBlockEdge());
            Assert.IsTrue(Arena(x: 164f, y: 96f, radius: 30f).CrossesBlockEdge());
            Assert.IsTrue(Arena(x: 96f, y: 20f, radius: 30f).CrossesBlockEdge());
            Assert.IsTrue(Arena(x: 96f, y: 170f, radius: 30f).CrossesBlockEdge());
        }

        [TestMethod]
        public void RandomPointInside_StaysInsideAndSpreadsByArea()
        {
            var a = Arena(radius: 30f);
            var rng = new Random(11);
            var inner = 0;
            const int n = 4000;

            for (var i = 0; i < n; i++)
            {
                var p = a.RandomPointInside(rng);

                Assert.IsTrue(a.Contains(p));

                if (a.Centre.Distance2D(p) <= 15.0)
                    inner++;
            }

            // Uniform by area: the inner half-radius disc holds a quarter of the points.
            Assert.AreEqual(0.25, inner / (double)n, 0.03);
        }
    }
}
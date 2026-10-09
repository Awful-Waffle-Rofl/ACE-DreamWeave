using System;
using System.Numerics;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the per-door anti-blink width: the global anti_blink_door_width is a floor, and a door wider than
    /// it is measured by its own collision shape (DoorCollisionWidth). Pure arithmetic - no landblock, no
    /// physics object, no dat, and no PropertyManager read.
    /// </summary>
    [TestClass]
    public class AntiBlinkDoorWidthTests
    {
        // an outdoor cell in landblock 0x0102, as in AntiBlinkGeometryTests
        private const uint Block = 0x01020025;

        /// <summary>The prod floor (anti_blink_door_width = 2.5 on prod, 3.0 default).</summary>
        private const float ProdFloor = 2.5f;

        /// <summary>A door facing along +Y (identity rotation), so its blocking plane runs east-west.</summary>
        private static Position DoorFacingNorth(float x, float y)
        {
            return new Position(Block, x, y, 0f, 0f, 0f, 0f, 1f, 0u);
        }

        private static Position At(float x, float y)
        {
            return new Position(Block, x, y, 0f, 0f, 0f, 0f, 1f, 0u);
        }

        #region GetEffectiveWidth: max(floor, real)

        [TestMethod]
        public void GetEffectiveWidth_RealWiderThanFloor_UsesReal()
        {
            Assert.AreEqual(7.2f, DoorCollisionWidth.GetEffectiveWidth(ProdFloor, 7.2f), 0.0001f);
        }

        [TestMethod]
        public void GetEffectiveWidth_RealNarrowerThanFloor_UsesFloor()
        {
            Assert.AreEqual(ProdFloor, DoorCollisionWidth.GetEffectiveWidth(ProdFloor, 1.2f), 0.0001f);
        }

        /// <summary>0 is what Door caches when the collision shape could not be read.</summary>
        [TestMethod]
        public void GetEffectiveWidth_UnavailableShape_UsesFloor()
        {
            Assert.AreEqual(ProdFloor, DoorCollisionWidth.GetEffectiveWidth(ProdFloor, 0f), 0.0001f);
            Assert.AreEqual(ProdFloor, DoorCollisionWidth.GetEffectiveWidth(ProdFloor, float.NaN), 0.0001f);
            Assert.AreEqual(ProdFloor, DoorCollisionWidth.GetEffectiveWidth(ProdFloor, float.PositiveInfinity), 0.0001f);
        }

        #endregion

        #region End to end: crossing beside the centre of a wide gate

        /// <summary>
        /// POSITIVE CONTROL. The 2026-09-18 prod miss in miniature: a player crosses the plane of a closed
        /// 7.2 m gate 3.3 m east of its centre. The old logic built the segment from the 2.5 m floor alone
        /// (x within 1.25 of the centre) and let it through; the effective width must catch it.
        /// </summary>
        [TestMethod]
        public void WideDoor_CrossingAt3Point3FromCentre_IsDetected()
        {
            var door = DoorFacingNorth(96f, 96f);

            var width = DoorCollisionWidth.GetEffectiveWidth(ProdFloor, 7.2f);
            var segment = Player.GetDoorSegment(door, width);

            var start = Player.GetGlobalPos(At(96f + 3.3f, 94f));
            var end = Player.GetGlobalPos(At(96f + 3.3f, 98f));

            Assert.IsNotNull(Player.GetSegmentIntersection(start, end, segment.Start, segment.End));
        }

        /// <summary>
        /// A narrow door keeps exactly the floor: a crossing 2 m from its centre (outside the 2.5 m floor)
        /// is still not a crossing of that door, so honest movement beside it is untouched.
        /// </summary>
        [TestMethod]
        public void NarrowDoor_UsesFloor_CrossingBesideItIsNotDetected()
        {
            var door = DoorFacingNorth(96f, 96f);

            var width = DoorCollisionWidth.GetEffectiveWidth(ProdFloor, 1.2f);
            Assert.AreEqual(ProdFloor, width, 0.0001f);

            var segment = Player.GetDoorSegment(door, width);
            Assert.AreEqual(ProdFloor, (segment.End - segment.Start).Length(), 0.001f);

            var start = Player.GetGlobalPos(At(96f + 2f, 94f));
            var end = Player.GetGlobalPos(At(96f + 2f, 98f));

            Assert.IsNull(Player.GetSegmentIntersection(start, end, segment.Start, segment.End));
        }

        #endregion

        #region Shape measurement in the door's frame

        [TestMethod]
        public void WidthFromDoorLocalPoints_IsTwiceTheFarthestReachOnEitherSide()
        {
            // an off-centre (hinged) leaf reaching 3.6 m east and 0.1 m west of the origin: the segment stays
            // centred on the origin, so it must reach 3.6 m both ways
            var points = new[] { new Vector3(-0.1f, 0f, 0f), new Vector3(3.6f, 0.2f, 4f), new Vector3(1f, -0.2f, 0f) };

            Assert.AreEqual(7.2f, DoorCollisionWidth.WidthFromDoorLocalPoints(points), 0.0001f);
        }

        [TestMethod]
        public void WidthFromDoorLocalPoints_IgnoresDepthAndHeight()
        {
            // Y is the facing (door thickness) axis and Z is height; neither widens the segment
            var points = new[] { new Vector3(0.5f, 9f, 0f), new Vector3(-0.5f, -9f, 12f) };

            Assert.AreEqual(1f, DoorCollisionWidth.WidthFromDoorLocalPoints(points), 0.0001f);
        }

        [TestMethod]
        public void WidthFromDoorLocalPoints_NoPoints_IsZero()
        {
            Assert.AreEqual(0f, DoorCollisionWidth.WidthFromDoorLocalPoints(Array.Empty<Vector3>()), 0.0001f);
        }

        /// <summary>
        /// A part frame rotated 90 degrees about Z turns a vertex on the part's Y axis onto the door's X axis,
        /// and the object Scale applies to the part frame origin while GfxObjScale.Z applies to the vertex.
        /// </summary>
        [TestMethod]
        public void PartVertexToDoorLocal_AppliesPartFrameAndBothScales()
        {
            var rot90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2.0));

            var local = DoorCollisionWidth.PartVertexToDoorLocal(
                new Vector3(1f, 0f, 0f), rot90, new Vector3(2f, 2f, 2f), 3f, new Vector3(0f, 1f, 0f));

            // origin (1,0,0) * scale 2 = (2,0,0); vertex (0,1,0) * 3 = (0,3,0) rotated +90 about Z = (-3,0,0)
            Assert.AreEqual(-1f, local.X, 0.0001f);
            Assert.AreEqual(0f, local.Y, 0.0001f);
            Assert.AreEqual(0f, local.Z, 0.0001f);
        }

        #endregion
    }
}

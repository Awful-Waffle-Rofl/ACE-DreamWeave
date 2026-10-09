using System.Collections.Generic;
using System.Numerics;

using ACE.DatLoader.Entity;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.Physics.BSP;
using ACE.Server.Physics.Collision;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using PhysicsPolygon = ACE.Server.Physics.Polygon;
using PhysicsSphere = ACE.Server.Physics.Sphere;
using PhysicsCylSphere = ACE.Server.Physics.CylSphere;
using PhysicsVertex = ACE.Server.Physics.Entity.Vertex;
using PhysicsSetup = ACE.Server.Physics.Setup;
using Frame = ACE.DatLoader.Entity.Frame;
using BSPLeaf = ACE.Server.Physics.BSP.BSPLeaf;
using BSPNode = ACE.Server.Physics.BSP.BSPNode;
using BSPTree = ACE.Server.Physics.BSP.BSPTree;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The delivery path of the per-door anti-blink width: DoorCollisionWidth.Derive against a real (hand-built,
    /// dat-free) PhysicsObj / PartArray / Setup / PhysicsPart / GfxObj / BSP, and Door.GetAntiBlinkDoorWidth's
    /// once-only cache. No seam: these are the production types, populated through their public fields.
    /// </summary>
    [TestClass]
    public class AntiBlinkDoorDeriveTests
    {
        private const float Floor = 2.5f;

        #region builders

        /// <summary>
        /// A part whose physics BSP holds one polygon over the given vertices, placed in a LEAF below an
        /// empty root, so the walk has to descend the tree to find it.
        /// </summary>
        private static PhysicsPart BspPart(params Vector3[] vertices)
        {
            var polygon = new PhysicsPolygon(0, vertices.Length, CullMode.None);

            for (var i = 0; i < vertices.Length; i++)
                polygon.Vertices[i] = new PhysicsVertex(vertices[i]);

            var leaf = new BSPLeaf { Polygons = new List<PhysicsPolygon> { polygon }, NumPolys = 1 };
            var root = new BSPNode { PosNode = leaf };

            return new PhysicsPart { GfxObj = new GfxObj { PhysicsBSP = new BSPTree(root) } };
        }

        /// <summary>A part spanning x in [-halfWidth, +halfWidth] in its own frame.</summary>
        private static PhysicsPart Leaf(float halfWidth)
        {
            return BspPart(new Vector3(-halfWidth, -0.1f, 0f), new Vector3(halfWidth, -0.1f, 0f), new Vector3(halfWidth, 0.1f, 2.5f), new Vector3(-halfWidth, 0.1f, 2.5f));
        }

        private static PhysicsObj MakePhysicsObj(bool hasPhysicsBsp, List<PhysicsPart> parts, List<Frame> frames,
            List<PhysicsCylSphere> cylSpheres = null, List<PhysicsSphere> spheres = null, float scale = 1.0f)
        {
            var setup = new PhysicsSetup();

            if (cylSpheres != null)
            {
                setup.CylSphere = cylSpheres;
                setup.NumCylsphere = cylSpheres.Count;
            }

            if (spheres != null)
            {
                setup.Sphere = spheres;
                setup.NumSphere = spheres.Count;
            }

            var partArray = new PartArray { Setup = setup, Parts = parts ?? new List<PhysicsPart>(), NumParts = parts?.Count ?? 0 };

            if (frames != null)
            {
                var animFrame = new AnimationFrame();

                foreach (var frame in frames)
                    animFrame.Frames.Add(frame);

                partArray.Sequence.SetPlacementFrame(animFrame, 0);
            }

            var obj = new PhysicsObj { PartArray = partArray, Scale = scale };

            obj.State = hasPhysicsBsp ? PhysicsState.HasPhysicsBSP : 0;

            return obj;
        }

        private static Frame At(float x) => new Frame(new Vector3(x, 0f, 0f), Quaternion.Identity);

        /// <summary>A two-leaf gate: each leaf 1.5 wide, placed by its OWN frame at x = +1.5 and x = -1.5.</summary>
        private static PhysicsObj TwoLeafGate() =>
            MakePhysicsObj(true, new List<PhysicsPart> { Leaf(1.5f), Leaf(1.5f) }, new List<Frame> { At(1.5f), At(-1.5f) });

        private static Door MakeClosedDoor()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999998,
                ClassName = "antiblinktestdoor",
                WeenieType = WeenieType.Door,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Anti-Blink Test Door" } },
            };

            var door = new Door(weenie, new ObjectGuid(0x7F000002));

            Assert.IsFalse(door.IsOpen, "fixture: the door must start closed");
            Assert.AreNotEqual(true, door.Ethereal, "fixture: the door must start solid");

            return door;
        }

        /// <summary>WorldObject.PhysicsObj has a protected setter; InitPhysicsObj would need the dats.</summary>
        private static void AttachPhysics(Door door, PhysicsObj physicsObj)
        {
            typeof(WorldObject).GetProperty(nameof(WorldObject.PhysicsObj)).SetValue(door, physicsObj);
        }

        #endregion

        #region Derive: BSP branch

        /// <summary>
        /// Each part must be placed by ITS OWN animation frame. The two frames are asymmetric on purpose:
        /// placing both leaves by frame 0 would put both at x = +0.5 and give 2.0 instead of 7.0.
        /// </summary>
        [TestMethod]
        public void Derive_Bsp_EachPartPlacedByItsOwnFrame()
        {
            var obj = MakePhysicsObj(true,
                new List<PhysicsPart> { Leaf(0.5f), Leaf(0.5f) },
                new List<Frame> { At(0.5f), At(-3.0f) });

            // east leaf spans [0, 1]; west leaf spans [-3.5, -2.5] -> farthest reach 3.5 -> width 7.0
            Assert.AreEqual(7.0f, DoorCollisionWidth.Derive(obj).Value, 0.0001f);
        }

        [TestMethod]
        public void Derive_Bsp_TwoLeafGate()
        {
            Assert.AreEqual(6.0f, DoorCollisionWidth.Derive(TwoLeafGate()).Value, 0.0001f);
        }

        /// <summary>
        /// The part frame's rotation, the object Scale (on the frame origin) and GfxObjScale.Z (on the vertex)
        /// all reach the result.
        /// </summary>
        [TestMethod]
        public void Derive_Bsp_AppliesFrameRotationAndBothScales()
        {
            // vertices on the part's Y axis only: unrotated they contribute nothing along X
            var part = BspPart(new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 1f));
            part.GfxObjScale = new Vector3(2f, 2f, 2f);

            var rot90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(System.Math.PI / 2.0));

            var obj = MakePhysicsObj(true, new List<PhysicsPart> { part }, new List<Frame> { new Frame(new Vector3(0.5f, 0f, 0f), rot90) });
            obj.PartArray.Scale = new Vector3(2f, 2f, 2f);

            // origin 0.5 * 2 = 1; vertices (0, +-1) * 2 rotated +90 -> x = -+2 -> x in {-1, 3} -> width 6
            Assert.AreEqual(6.0f, DoorCollisionWidth.Derive(obj).Value, 0.0001f);
        }

        /// <summary>UpdateParts places a lone part at the object's own origin when there is no frame.</summary>
        [TestMethod]
        public void Derive_Bsp_SinglePartWithoutFrame_UsesIdentity()
        {
            var obj = MakePhysicsObj(true, new List<PhysicsPart> { Leaf(1.2f) }, null);

            Assert.AreEqual(2.4f, DoorCollisionWidth.Derive(obj).Value, 0.0001f);
        }

        #endregion

        #region Derive: branch priority BSP > cylsphere > sphere

        [TestMethod]
        public void Derive_BspBeatsCylsphereAndSphere()
        {
            var obj = MakePhysicsObj(true, new List<PhysicsPart> { Leaf(1.5f) }, new List<Frame> { At(0f) },
                new List<PhysicsCylSphere> { new PhysicsCylSphere(Vector3.Zero, 2f, 5f) },
                new List<PhysicsSphere> { new PhysicsSphere(Vector3.Zero, 7f) });

            Assert.AreEqual(3.0f, DoorCollisionWidth.Derive(obj).Value, 0.0001f);
        }

        [TestMethod]
        public void Derive_CylsphereBeatsSphere_AndAppliesObjectScale()
        {
            var obj = MakePhysicsObj(false, new List<PhysicsPart>(), null,
                new List<PhysicsCylSphere> { new PhysicsCylSphere(new Vector3(1f, 0f, 0f), 2f, 0.5f) },
                new List<PhysicsSphere> { new PhysicsSphere(Vector3.Zero, 10f) },
                scale: 2f);

            // reach |1 * 2| + 0.5 * 2 = 3 -> width 6; the 10 m sphere is not the collision shape here
            Assert.AreEqual(6.0f, DoorCollisionWidth.Derive(obj).Value, 0.0001f);
        }

        [TestMethod]
        public void Derive_SphereOnly()
        {
            var obj = MakePhysicsObj(false, new List<PhysicsPart>(), null,
                spheres: new List<PhysicsSphere> { new PhysicsSphere(new Vector3(-0.5f, 0f, 0f), 1f) });

            Assert.AreEqual(3.0f, DoorCollisionWidth.Derive(obj).Value, 0.0001f);
        }

        /// <summary>
        /// FindObjCollisions only uses the BSP when the State flag says so; without it the parts' BSPs are not
        /// what a player collides with, and neither is anything else here.
        /// </summary>
        [TestMethod]
        public void Derive_BspGeometryWithoutTheFlag_IsNotUsed()
        {
            var obj = MakePhysicsObj(false, new List<PhysicsPart> { Leaf(3f) }, new List<Frame> { At(0f) });

            Assert.IsNull(DoorCollisionWidth.Derive(obj));
        }

        [TestMethod]
        public void Derive_NoShape_IsNull()
        {
            Assert.IsNull(DoorCollisionWidth.Derive(null));
            Assert.IsNull(DoorCollisionWidth.Derive(new PhysicsObj()));
        }

        #endregion

        #region Door.GetAntiBlinkDoorWidth: once-only cache

        [TestMethod]
        public void DoorWidth_DerivedOnce_ThenCached()
        {
            var door = MakeClosedDoor();
            var obj = TwoLeafGate();
            AttachPhysics(door, obj);

            Assert.AreEqual(6.0f, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);

            // widen the geometry after the first call: part 0 now spans [-13.5, 16.5], so a re-derivation gives 33
            obj.PartArray.Parts[0].GfxObjScale = new Vector3(10f, 10f, 10f);
            Assert.AreEqual(33.0f, DoorCollisionWidth.Derive(obj).Value, 0.0001f, "fixture: the mutation must change what Derive returns");

            Assert.AreEqual(6.0f, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);

            // the cached real width still competes with whatever floor is passed in
            Assert.AreEqual(8.0f, door.GetAntiBlinkDoorWidth(8.0f), 0.0001f);
        }

        [TestMethod]
        public void DoorWidth_UnavailableShape_CachesZero_FloorApplies()
        {
            var door = MakeClosedDoor();
            var obj = new PhysicsObj();     // no PartArray: Derive returns null
            AttachPhysics(door, obj);

            Assert.AreEqual(Floor, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);

            // a shape appearing later is not picked up: the 0 was cached on the first closed call
            obj.PartArray = TwoLeafGate().PartArray;
            obj.State = PhysicsState.HasPhysicsBSP;

            Assert.AreEqual(Floor, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);
            Assert.AreEqual(4.0f, door.GetAntiBlinkDoorWidth(4.0f), 0.0001f);
        }

        /// <summary>An open (swung) door is never measured, and nothing is cached until it is seen closed.</summary>
        [TestMethod]
        public void DoorWidth_OpenDoor_UsesFloorWithoutCaching()
        {
            var door = MakeClosedDoor();
            AttachPhysics(door, TwoLeafGate());

            door.IsOpen = true;
            Assert.AreEqual(Floor, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);

            door.IsOpen = false;
            Assert.AreEqual(6.0f, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);
        }

        [TestMethod]
        public void DoorWidth_NoPhysicsObjYet_UsesFloorWithoutCaching()
        {
            var door = MakeClosedDoor();

            Assert.AreEqual(Floor, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);

            AttachPhysics(door, TwoLeafGate());
            Assert.AreEqual(6.0f, door.GetAntiBlinkDoorWidth(Floor), 0.0001f);
        }

        #endregion
    }
}

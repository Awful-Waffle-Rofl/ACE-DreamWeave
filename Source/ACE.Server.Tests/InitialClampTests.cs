using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Physics;
using ACE.Server.Physics.Common;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ObjectMaint's initial-visibility clamp (112.5 m, 2D) and the WaffleACE per-object exemption
    /// (PropertyBool.IgnoreInitialClamp, cached as PhysicsObj.IgnoreInitialClamp). DB- and dat-free:
    /// bare PhysicsObjs with positions are enough to drive AddVisibleObject.
    /// </summary>
    [TestClass]
    public class InitialClampTests
    {
        private static PhysicsObj MakeObj(uint id, float x, float y)
        {
            var obj = new PhysicsObj();
            obj.ID = id;
            obj.Position.ObjCellID = 0x21B00001;
            obj.Position.Frame.Origin = new Vector3(x, y, 0);
            obj.set_weenie_obj(new WeenieObject());   // IsMonster false
            return obj;
        }

        [TestMethod]
        public void FarUnknownObject_IsClampedByDefault()
        {
            Assert.IsTrue(ObjectMaint.InitialClamp, "test assumes the clamp is on (ACE default)");

            var viewer = MakeObj(0x50000001, 0, 0);
            var far = MakeObj(0x7F000001, ObjectMaint.InitialClamp_Dist + 50, 0);
            var maint = new ObjectMaint(viewer);

            Assert.IsFalse(maint.AddVisibleObject(far), "an unknown object past 112.5 m must be withheld");
            Assert.IsFalse(maint.VisibleObjectsContainsKey(far.ID));
        }

        [TestMethod]
        public void FarUnknownObject_WithIgnoreInitialClamp_IsVisible()
        {
            var viewer = MakeObj(0x50000001, 0, 0);
            var far = MakeObj(0x7F000002, ObjectMaint.InitialClamp_Dist + 50, 0);
            far.IgnoreInitialClamp = true;
            var maint = new ObjectMaint(viewer);

            Assert.IsTrue(maint.AddVisibleObject(far), "a flagged object must be visible regardless of the clamp distance");
            Assert.IsTrue(maint.VisibleObjectsContainsKey(far.ID));
        }

        [TestMethod]
        public void NearUnknownObject_IsVisibleWithoutTheFlag()
        {
            var viewer = MakeObj(0x50000001, 0, 0);
            var near = MakeObj(0x7F000003, ObjectMaint.InitialClamp_Dist - 10, 0);
            var maint = new ObjectMaint(viewer);

            Assert.IsTrue(maint.AddVisibleObject(near));
        }

        [TestMethod]
        public void HeightDoesNotCountTowardTheClamp()
        {
            // the clamp is 2D by design: a sky object 150 m up but 100 m away horizontally is inside it
            var viewer = MakeObj(0x50000001, 0, 0);
            var sky = MakeObj(0x7F000004, 100, 0);
            sky.Position.Frame.Origin = new Vector3(100, 0, 150);
            var maint = new ObjectMaint(viewer);

            Assert.IsTrue(maint.AddVisibleObject(sky));
        }
    }
}

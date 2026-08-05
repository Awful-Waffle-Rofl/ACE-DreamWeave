using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Physics
{
    /// <summary>
    /// ObjectMaint's tracking tables (KnownObjects / VisibleObjects / KnownPlayers / VisibleTargets)
    /// are keyed by bare 32-bit object guid. In the ACRealms port a single landblock can be loaded as
    /// several instances at once (e.g. a realm-0 and a realm-1 view of the same landblock), so those
    /// keys are ambiguous across instances. These tests pin the instance-equality gate that stops an
    /// object in one landblock instance from ever entering the tracking set of an observer in another -
    /// the mechanism that keeps two concurrently-loaded instances of one landblock from cross-populating
    /// each other's clients.
    ///
    /// A bare PhysicsObj with no CurLandblock/CurCell resolves CurInstance to its KnownInstance, which is
    /// exactly the seam being tested, so no dat data or live server is required.
    /// </summary>
    [TestClass]
    public class ObjectMaintInstanceTests
    {
        private const uint InstanceA = 0x0001_0007;
        private const uint InstanceB = 0x0002_0007;

        // player guid range is 0x50000001 - 0x5FFFFFFF (PhysicsObj.IsPlayer)
        private const uint PlayerId = 0x50000001;

        private static PhysicsObj MakeObj(uint id, uint instance)
        {
            return new PhysicsObj { ID = id, KnownInstance = instance };
        }

        [TestMethod]
        public void AddKnownObject_SameInstance_IsTracked()
        {
            var player = MakeObj(PlayerId, InstanceA);
            var obj = MakeObj(0x1000, InstanceA);

            Assert.IsTrue(player.ObjMaint.AddKnownObject(obj));
            Assert.IsTrue(player.ObjMaint.KnownObjectsContainsKey(obj.ID));
        }

        [TestMethod]
        public void AddKnownObject_DifferentInstance_IsRejected()
        {
            var player = MakeObj(PlayerId, InstanceA);
            var otherInstanceObj = MakeObj(0x1000, InstanceB);

            Assert.IsFalse(player.ObjMaint.AddKnownObject(otherInstanceObj));
            Assert.IsFalse(player.ObjMaint.KnownObjectsContainsKey(otherInstanceObj.ID));
        }

        [TestMethod]
        public void AddVisibleObject_DifferentInstance_IsRejected()
        {
            var player = MakeObj(PlayerId, InstanceA);
            var otherInstanceObj = MakeObj(0x2000, InstanceB);

            Assert.IsFalse(player.ObjMaint.AddVisibleObject(otherInstanceObj));
            Assert.IsFalse(player.ObjMaint.VisibleObjectsContainsKey(otherInstanceObj.ID));
        }

        [TestMethod]
        public void AddKnownObject_DifferentInstance_DoesNotAddPlayerToBroadcastList()
        {
            // the inverse of AddKnownObject registers the observer in the object's KnownPlayers,
            // which is the broadcast recipient list - a cross-instance leak here would push every
            // CreateObject / motion / update to a client in the other instance
            var player = MakeObj(PlayerId, InstanceA);
            var otherInstanceObj = MakeObj(0x3000, InstanceB);

            player.ObjMaint.AddKnownObject(otherInstanceObj);

            Assert.AreEqual(0, otherInstanceObj.ObjMaint.GetKnownPlayersCount());
        }

        [TestMethod]
        public void AddKnownObject_SameInstance_AddsPlayerToBroadcastList()
        {
            var player = MakeObj(PlayerId, InstanceA);
            var obj = MakeObj(0x3000, InstanceA);

            player.ObjMaint.AddKnownObject(obj);

            Assert.AreEqual(1, obj.ObjMaint.GetKnownPlayersCount());
        }
    }
}

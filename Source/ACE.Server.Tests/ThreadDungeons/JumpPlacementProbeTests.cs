using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Physics;
using ACE.Server.Physics.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// JumpPlacementProbe's own contract, dat-free: a create returning null or throwing refuses (Form uses the same
    /// factory); a probe FAULT (a check that throws) fails open and still tears down (the WorldObject destroyed, the physics body's
    /// ServerObjectManager entry gone). The real placement verdicts need the dats and live in
    /// JumpPlacementProbeGeometryTests, which also covers a Setup the spawn refuses (IsValidSetupId only validates
    /// against a loaded client_portal.dat, so that case cannot be driven dat-free). Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class JumpPlacementProbeTests
    {
        private const uint Wcid = 1007961;
        private static uint nextGuid = 0x7F0E6001;

        /// <summary>A pad whose InitPhysicsObj builds and REGISTERS a bare physics body, then throws: the check faults mid-way.</summary>
        private sealed class FaultingPad : GenericObject
        {
            public FaultingPad(uint guid) : base(Weenie(), new ObjectGuid(guid)) { }

            public PhysicsObj Body { get; private set; }

            public override void InitPhysicsObj()
            {
                PhysicsObj = new PhysicsObj();
                PhysicsObj.set_object_guid(Guid, Location?.Instance ?? 0);
                Body = PhysicsObj;
                throw new InvalidOperationException("probe path fault");
            }
        }

        private static Weenie Weenie() => new Weenie
        {
            WeenieClassId = Wcid,
            WeenieType = WeenieType.Generic,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "probe test pad" } },
        };

        private static Position At()
        {
            var p = new Position(0x54560019, 96f, 96f, 40f, 0f, 0f, 0f, 1f, 0x80005678u);
            p.SetLandCell();
            return p;
        }

        [TestMethod]
        public void A_create_returning_null_refuses_with_a_reason()
        {
            var why = JumpPlacementProbe.Probe(_ => null, Wcid, At(), true);

            Assert.IsNotNull(why);
            StringAssert.Contains(why, "did not create");
        }

        [TestMethod]
        public void A_create_that_throws_refuses_like_a_create_returning_null()
        {
            // Form calls the same factory, so a create that throws here would throw at Use time too: refuse, never place.
            var why = JumpPlacementProbe.Probe(_ => throw new InvalidOperationException("factory fault"), Wcid, At(), true);

            Assert.IsNotNull(why);
            StringAssert.Contains(why, "threw on create");
            StringAssert.Contains(why, "InvalidOperationException");
        }

        [TestMethod]
        public void A_check_that_throws_fails_open_and_still_tears_down_without_a_trace()
        {
            var at = At();
            FaultingPad pad = null;
            var before = ServerObjectManager.ServerObjects.Count;

            var why = JumpPlacementProbe.Probe(_ => pad = new FaultingPad(nextGuid++), Wcid, at, true);

            Assert.IsNull(why, "a probe fault never refuses the point");
            Assert.IsNotNull(pad.Body, "control: the check reached InitPhysicsObj and registered a body before it threw");
            Assert.IsTrue(pad.IsDestroyed, "the WorldObject is destroyed");
            Assert.IsNull(pad.CurrentLandblock, "never joined a landblock");
            Assert.IsNull(pad.Body.CurCell, "never joined a cell");
            Assert.AreEqual(0, pad.Body.ShadowObjects.Count, "no shadow cells");
            Assert.IsNull(ServerObjectManager.GetObjectA(pad.Guid.Full, at.Instance), "the body's ServerObjectManager entry is gone");
            Assert.AreEqual(before, ServerObjectManager.ServerObjects.Count, "the object manager holds exactly what it held before");
        }
    }
}

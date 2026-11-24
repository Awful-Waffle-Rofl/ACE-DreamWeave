using System;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Dat-free pins for PropertyBool.ForcePhysicsStatic (9091). ForcePhysicsStaticTests and the Pinnacle geometry tests
    /// prove the behaviour but need the client dats, so CI reports them Inconclusive; these source scans are what CI
    /// actually runs. They pin the three lines of WorldObject.InitPhysicsObj that honour the bool and the Creature guard.
    /// </summary>
    [TestClass]
    public class ForcePhysicsStaticSourceTests
    {
        [TestMethod]
        public void InitPhysicsObj_KeepsStatic_AndBuildsNonDynamic_WhenForced()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject.cs");
            var body = PooledLootSourceText.MethodBody(src, "public virtual void InitPhysicsObj(");

            var read = body.IndexOf("var forceStatic = ForcedPhysicsStatic;", StringComparison.Ordinal);
            var keep = body.IndexOf("defaultState |= PhysicsState.Static", StringComparison.Ordinal);
            var dynamic = body.IndexOf("isDynamic = !forceStatic", StringComparison.Ordinal);
            var assign = body.IndexOf("PhysicsObj.State = defaultState;", StringComparison.Ordinal);

            Assert.IsTrue(read >= 0, "InitPhysicsObj reads ForcedPhysicsStatic");
            Assert.IsTrue(keep >= 0, "InitPhysicsObj puts Static back on defaultState when forced");
            Assert.IsTrue(dynamic >= 0, "InitPhysicsObj builds a forced object non-dynamic");
            Assert.IsTrue(assign >= 0, "InitPhysicsObj assigns defaultState to the body");
            Assert.IsTrue(read < keep && keep < assign, "the Static bit is added before defaultState reaches the body");
        }

        [TestMethod]
        public void ForcedPhysicsStatic_IsNeverHonouredOnACreature()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Properties.cs");

            StringAssert.Contains(src, "public bool ForcedPhysicsStatic => ForcePhysicsStatic && !(this is Creature);");
            StringAssert.Contains(src, "get => GetProperty(PropertyBool.ForcePhysicsStatic) ?? false;");
        }
    }
}

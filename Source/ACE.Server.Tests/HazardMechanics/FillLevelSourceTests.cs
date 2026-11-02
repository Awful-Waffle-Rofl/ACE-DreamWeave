using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class FillLevelSourceTests
    {
        [TestMethod]
        public void FillMechanic_IsALevelSource() => Assert.IsInstanceOfType(new FillMechanic(), typeof(IHazardLevelSource));

        [TestMethod]
        public void TryGetLevel_IsFalseBeforeStart()
        {
            Assert.IsFalse(((IHazardLevelSource)new FillMechanic()).TryGetLevel(out var z));
            Assert.AreEqual(0.0, z);
        }

        [TestMethod]
        public void TryGetLevel_ReadsTheDirectorSurface()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/FillMechanic.cs"), "public bool TryGetLevel(");
            StringAssert.Contains(body, "director.SurfaceAt(");
        }

        [TestMethod]
        public void OnStop_ReleasesTheContext()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/FillMechanic.cs"), "public void OnStop(");
            StringAssert.Contains(body, "levelCtx = null");
        }
    }
}

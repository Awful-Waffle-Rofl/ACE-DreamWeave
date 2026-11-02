using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The pure half of HazardContext.HitPlayerFlat: a flat damage number scaled by the resist mod.</summary>
    [TestClass]
    public class HazardFlatHitTests
    {
        [TestMethod]
        public void FlatAmount_RoundsAwayFromZero_AndFloorsAtZero()
        {
            Assert.AreEqual(40u, HazardHitRules.FlatAmount(40, 1.0));
            Assert.AreEqual(44u, HazardHitRules.FlatAmount(43.75, 1.0));
            Assert.AreEqual(50u, HazardHitRules.FlatAmount(100, 0.5));
            Assert.AreEqual(60u, HazardHitRules.FlatAmount(40, 1.5));
            Assert.AreEqual(1u, HazardHitRules.FlatAmount(0.5, 1.0));
            Assert.AreEqual(3u, HazardHitRules.FlatAmount(2.5, 1.0));
            Assert.AreEqual(0u, HazardHitRules.FlatAmount(100, 0.0));
            Assert.AreEqual(0u, HazardHitRules.FlatAmount(-5, 1.0));
            Assert.AreEqual(0u, HazardHitRules.FlatAmount(double.NaN, 1.0));
            Assert.AreEqual(0u, HazardHitRules.FlatAmount(40, double.PositiveInfinity));
        }

        [TestMethod]
        public void FlatAmount_SaturatesAtUIntMax()
            => Assert.AreEqual(uint.MaxValue, HazardHitRules.FlatAmount(1e12, 1.0));
    }
}

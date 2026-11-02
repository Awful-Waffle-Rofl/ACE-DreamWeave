using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>HazardRamp.At: the one linear start-to-end ramp the ball waves, the stream and the wisp cycles share.</summary>
    [TestClass]
    public class HazardRampTests
    {
        [TestMethod]
        public void At_FirstIndex_IsStart()
            => Assert.AreEqual(3.0, HazardRamp.At(3.0, 9.0, 1, 6), 1e-9);

        [TestMethod]
        public void At_LastIndex_IsEnd()
            => Assert.AreEqual(9.0, HazardRamp.At(3.0, 9.0, 6, 6), 1e-9);

        [TestMethod]
        public void At_Midpoints_AreLinear()
        {
            var expected = new[] { 3.0, 4.2, 5.4, 6.6, 7.8, 9.0 };

            for (var i = 1; i <= 6; i++)
                Assert.AreEqual(expected[i - 1], HazardRamp.At(3.0, 9.0, i, 6), 1e-9, "index " + i);
        }

        [TestMethod]
        public void At_DescendingRamp_Works()
            => Assert.AreEqual(2.0, HazardRamp.At(3.0, 1.0, 2, 3), 1e-9);

        [TestMethod]
        public void At_CountOne_IsStart()
        {
            Assert.AreEqual(3.0, HazardRamp.At(3.0, 9.0, 1, 1), 1e-9);
            Assert.AreEqual(3.0, HazardRamp.At(3.0, 9.0, 1, 0), 1e-9);
        }

        [TestMethod]
        public void At_NullEnd_IsStart()
        {
            for (var i = 1; i <= 6; i++)
                Assert.AreEqual(3.0, HazardRamp.At(3.0, null, i, 6), 1e-9, "index " + i);
        }

        [TestMethod]
        public void At_IndexOutsideRange_Clamps()
        {
            Assert.AreEqual(3.0, HazardRamp.At(3.0, 9.0, 0, 6), 1e-9);
            Assert.AreEqual(3.0, HazardRamp.At(3.0, 9.0, -4, 6), 1e-9);
            Assert.AreEqual(9.0, HazardRamp.At(3.0, 9.0, 7, 6), 1e-9);
            Assert.AreEqual(9.0, HazardRamp.At(3.0, 9.0, 600, 6), 1e-9);
        }
    }
}

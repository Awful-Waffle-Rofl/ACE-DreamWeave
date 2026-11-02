using System;

using ACE.Server.Knots;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// PinnacleRollBand (Docs/Knots/KNOTS-DESIGN.md section 7). Linear in level through (185 -> 1-40) and
    /// (375 -> 60-100), extrapolated both ways, then the low edge clamped to [1, 80] and the high edge to
    /// [40, 100]. The expected values below are computed by hand from those anchors, not from the code.
    /// </summary>
    [TestClass]
    public class PinnacleRollBandTests
    {
        private static readonly RollBandDef Default = new RollBandDef();

        private static void AssertBand(int level, double low, double high, RollBandDef def = null)
        {
            var (l, h) = PinnacleRollBand.Band(level, def ?? Default);
            Assert.AreEqual(low, l, 0.05, $"low edge at level {level}");
            Assert.AreEqual(high, h, 0.05, $"high edge at level {level}");
        }

        [TestMethod] public void Level100_BelowTheLowAnchor_ClampsToTheLowBand() => AssertBand(100, 1, 40);
        [TestMethod] public void Level185_IsTheLowAnchor() => AssertBand(185, 1, 40);
        [TestMethod] public void Level280_IsHalfwayBetweenTheAnchors() => AssertBand(280, 30.5, 70);
        [TestMethod] public void Level375_IsTheHighAnchor() => AssertBand(375, 60, 100);
        [TestMethod] public void Level439_LowEdgeApproachesTheClamp() => AssertBand(439, 79.87, 100);
        [TestMethod] public void Level500_BothEdgesClamped() => AssertBand(500, 80, 100);

        [TestMethod]
        public void LowEdge_StopsAtTheClamp_NotBefore()
        {
            // Unclamped, level 439 gives 79.87 (below the 80 clamp) and level 440 gives 80.18 (above it).
            Assert.IsTrue(PinnacleRollBand.Band(439, Default).Low < 80.0);
            Assert.AreEqual(80.0, PinnacleRollBand.Band(440, Default).Low, 1e-9);
        }

        [TestMethod]
        public void HighEdge_ReachesOneHundredAt375_AndStaysThere()
        {
            Assert.AreEqual(100.0, PinnacleRollBand.Band(375, Default).High, 1e-9);
            Assert.AreEqual(100.0, PinnacleRollBand.Band(1000, Default).High, 1e-9);
        }

        [TestMethod]
        public void Band_IsMonotoneInLevel()
        {
            var prev = PinnacleRollBand.Band(1, Default);
            for (var level = 2; level <= 600; level++)
            {
                var cur = PinnacleRollBand.Band(level, Default);
                Assert.IsTrue(cur.Low >= prev.Low && cur.High >= prev.High, $"band fell between {level - 1} and {level}");
                Assert.IsTrue(cur.Low <= cur.High, $"band inverted at {level}");
                prev = cur;
            }
        }

        [TestMethod]
        public void CustomAnchorsAndClamp_AreHonoured()
        {
            var def = new RollBandDef { LowLevel = 100, LowBand = new[] { 10, 30 }, HighLevel = 200, HighBand = new[] { 50, 90 }, ClampLow = 45 };
            AssertBand(100, 10, 30, def);
            AssertBand(150, 30, 60, def);
            AssertBand(200, 45, 90, def); // low edge would be 50 but the clamp is 45
            AssertBand(400, 45, 100, def);
        }

        [TestMethod]
        public void NullDef_FallsBackToTheDesignDefaults()
        {
            var (l, h) = PinnacleRollBand.Band(280, null);
            Assert.AreEqual(30.5, l, 0.05);
            Assert.AreEqual(70, h, 0.05);
        }

        [TestMethod]
        public void Roll_IsUniformInsideTheBand_AndHitsBothEnds()
        {
            var band = PinnacleRollBand.Band(280, Default); // 30.5 - 70
            Assert.AreEqual(band.Low, PinnacleRollBand.Roll(band, () => 0.0), 1e-9);
            Assert.AreEqual((band.Low + band.High) / 2, PinnacleRollBand.Roll(band, () => 0.5), 1e-9);
            Assert.IsTrue(PinnacleRollBand.Roll(band, () => 0.999999) < band.High);

            var rng = new Random(12345);
            for (var i = 0; i < 2000; i++)
            {
                var v = PinnacleRollBand.Roll(band, rng);
                Assert.IsTrue(v >= band.Low && v <= band.High, $"roll {v} escaped {band}");
            }
        }
    }
}

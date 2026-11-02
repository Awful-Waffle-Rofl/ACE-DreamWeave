using System;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class HazardGroundTests
    {
        private static readonly Func<float, float, float> Slope = (x, y) => 10f + x * 0.25f;

        private static HazardRegion Plane(float? hazardPlane = null, float offset = 0f) => HazardRegion.Rect("p", 0x0069, 0, false,
            0, 40, -40, 0, HazardGroundMode.Plane, -47.75f, hazardPlane, offset, null, 2f, 6f);

        private static HazardRegion Terrain(float offset = 0f) => HazardRegion.Circle("t", 0xA9B4, 0, true,
            96, 96, 30, HazardGroundMode.Terrain, 5f, null, offset, null, 2f, 6f);

        [TestMethod]
        public void PlaneRegion_IgnoresTheTerrainSampler()
        {
            Assert.AreEqual(-47.75f, HazardGround.GroundZ(Plane(), 20f, -20f, Slope), 1e-5);
        }

        [TestMethod]
        public void TerrainRegion_UsesTheSampler_AndFallsBackToThePlaneWithoutOne()
        {
            var t = Terrain();

            Assert.AreEqual(10f + 100f * 0.25f, HazardGround.GroundZ(t, 100f, 96f, Slope), 1e-5);
            Assert.AreEqual(5f, HazardGround.GroundZ(t, 100f, 96f, null), 1e-5, "no sampler -> plane");
            Assert.AreEqual(5f, HazardGround.GroundZ(t, 100f, 96f, (x, y) => float.NaN), 1e-5, "NaN -> plane");
        }

        [TestMethod]
        public void HazardZ_IsThePlaneWhenSet_ElseGroundPlusOffset()
        {
            Assert.AreEqual(-51.0f, HazardGround.HazardZ(Plane(-51.0f), 3f, -3f, Slope), 1e-5);
            Assert.AreEqual(-45.75f, HazardGround.HazardZ(Plane(null, 2f), 3f, -3f, Slope), 1e-5);
            Assert.AreEqual(10f + 25f + 3f, HazardGround.HazardZ(Terrain(3f), 100f, 96f, Slope), 1e-5, "terrain + offset follows the ground");
        }

        [TestMethod]
        public void InBand_IsMeasuredFromTheGroundAtThePlayersXY()
        {
            var t = Terrain();

            // ground at x=100 is 35; band [33, 41]
            Assert.IsTrue(t.InBand(new Vector3(100f, 96f, 33f), Slope));
            Assert.IsTrue(t.InBand(new Vector3(100f, 96f, 41f), Slope));
            Assert.IsFalse(t.InBand(new Vector3(100f, 96f, 32.9f), Slope));
            Assert.IsFalse(t.InBand(new Vector3(100f, 96f, 41.1f), Slope));
        }

        /// <summary>A lookup modelled on the Pinnacle recon: a block whose top is -48.00; inside it (z below -48) no cell.</summary>
        private static uint? BlockLookup(Vector3 p) => p.Z >= -48.0f ? 0x00690119u : (uint?)null;

        [TestMethod]
        public void RaiseToCell_LiftsAPillarOriginOutOfASolid()
        {
            Assert.IsTrue(HazardGround.TryRaiseToCell(BlockLookup, new Vector3(30f, -20f, -48.5f), out var cell, out var resolved));
            Assert.AreEqual(0x00690119u, cell);
            Assert.AreEqual(-48.0f, resolved.Z, 1e-5, "two 0.25 m steps");
        }

        [TestMethod]
        public void RaiseToCell_GivesUpAfterOnePointFiveMetres()
        {
            // -51 is 3 m inside the block; 6 x 0.25 m reaches only -49.5
            Assert.IsFalse(HazardGround.TryRaiseToCell(BlockLookup, new Vector3(30f, -20f, -51.0f), out _, out _));
        }

        /// <summary>The moving-body rule: with 0 steps a point inside a solid is refused, never lifted into the player plane.</summary>
        [TestMethod]
        public void RaiseToCell_WithZeroSteps_NeverLiftsAMovingBody()
        {
            Assert.IsFalse(HazardGround.TryRaiseToCell(BlockLookup, new Vector3(30f, -20f, -48.25f), 0, out _, out _));
            Assert.IsTrue(HazardGround.TryRaiseToCell(BlockLookup, new Vector3(30f, -20f, -47.9f), 0, out _, out var r));
            Assert.AreEqual(-47.9f, r.Z, 1e-5);
        }

        [TestMethod]
        public void RaiseToCell_RefusesNonFinitePoints()
        {
            Assert.IsFalse(HazardGround.TryRaiseToCell(BlockLookup, new Vector3(float.NaN, 0f, 0f), out _, out _));
        }
    }
}

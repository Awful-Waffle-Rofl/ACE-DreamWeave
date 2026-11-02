using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HazardRegion.ArenaCircle and the spanning-region rules (HAZARDS-PLAN S0): validation, the absolute z band, and
    /// that every pre-existing region keeps its one-landblock behaviour. Pure geometry; no PropertyManager.
    /// </summary>
    [TestClass]
    public class HazardRegionSpanTests
    {
        private static HazardRegion Arena(float cx, float cy, float r, float bandMin = 10f, float bandMax = 50f)
            => HazardRegion.ArenaCircle("arena", 0xA9B4, 0, cx, cy, r, 1.5f, bandMin, bandMax);

        [TestMethod]
        public void ArenaCircle_CarriesTheSpanFlagsAndTheAbsoluteBand()
        {
            var r = Arena(96f, 96f, 60f);

            Assert.IsTrue(r.SpansLandblocks);
            Assert.IsTrue(r.HitsRequireInside);
            Assert.AreEqual(10f, r.AbsoluteBandMinZ);
            Assert.AreEqual(50f, r.AbsoluteBandMaxZ);
            Assert.IsTrue(r.Outdoor);
            Assert.AreEqual(HazardGroundMode.Terrain, r.GroundMode);
            Assert.AreEqual(HazardShape.Circle, r.Shape);
            Assert.AreEqual(0xA9B4, r.Landblock);
            Assert.AreEqual(1.5f, r.HazardOffset);
            Assert.IsNull(r.Validate());
        }

        [TestMethod]
        public void ExistingRegions_AreNotSpanning_AndKeepTheirBand()
        {
            var circle = HazardRegion.Circle("t", 0xA9B4, 0, true, 96f, 96f, 20f, HazardGroundMode.Terrain, 0f, null, 0f, null, 2f, 6f);
            var rect = HazardRegion.Rect("t", 0x0069, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);
            var adhoc = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 30f), HazardShapeSpec.Circle(10f), 0f, out var error);

            Assert.IsNull(error);

            foreach (var r in new[] { circle, rect, adhoc })
            {
                Assert.IsFalse(r.SpansLandblocks);
                Assert.IsFalse(r.HitsRequireInside);
                Assert.IsNull(r.AbsoluteBandMinZ);
                Assert.IsNull(r.AbsoluteBandMaxZ);
            }
        }

        [TestMethod]
        public void Spanning_AcceptsNegativeAndPast192Coordinates()
        {
            Assert.IsNull(Arena(-100f, 96f, 60f).Validate(), "x -160..-40: west neighbour");
            Assert.IsNull(Arena(96f, -100f, 60f).Validate(), "y -160..-40: south neighbour");
            Assert.IsNull(Arena(300f, 96f, 60f).Validate(), "x 240..360: east neighbour");
            Assert.IsNull(Arena(96f, 300f, 60f).Validate(), "y 240..360: north neighbour");
            Assert.IsNull(Arena(190f, 190f, 60f).Validate(), "the corner of the anchor landblock");
        }

        [TestMethod]
        public void Spanning_RefusesBoundsAtOrBeyondMinus192AndPlus384()
        {
            // bounds are (-192, 384) exclusive
            StringAssert.Contains(Arena(-132f, 96f, 60f).Validate(), "reaches more than one landblock", "MinX == -192 exactly");
            Assert.IsNull(Arena(-131f, 96f, 60f).Validate(), "MinX -191");
            StringAssert.Contains(Arena(324f, 96f, 60f).Validate(), "reaches more than one landblock", "MaxX == 384 exactly");
            Assert.IsNull(Arena(323f, 96f, 60f).Validate(), "MaxX 383");
            StringAssert.Contains(Arena(96f, -140f, 60f).Validate(), "reaches more than one landblock");
            StringAssert.Contains(Arena(96f, 330f, 60f).Validate(), "reaches more than one landblock");
        }

        [TestMethod]
        public void Spanning_RefusesASpanOf192OrMorePerAxis()
        {
            Assert.IsNull(Arena(96f, 96f, 95.5f).Validate(), "191 m across");
            StringAssert.Contains(Arena(96f, 96f, 96f).Validate(), "under 192", "exactly 192 m across");
            StringAssert.Contains(Arena(96f, 96f, 100f).Validate(), "under 192");
        }

        [TestMethod]
        public void NonSpanning_StillRefusesAnythingOutsideItsLandblock()
        {
            var inside = HazardRegion.Circle("t", 0xA9B4, 0, true, 96f, 96f, 60f, HazardGroundMode.Terrain, 0f, null, 0f, null, 2f, 6f);
            var overhang = HazardRegion.Circle("t", 0xA9B4, 0, true, 150f, 96f, 60f, HazardGroundMode.Terrain, 0f, null, 0f, null, 2f, 6f);
            var negative = HazardRegion.Circle("t", 0xA9B4, 0, true, 30f, 96f, 60f, HazardGroundMode.Terrain, 0f, null, 0f, null, 2f, 6f);

            Assert.IsNull(inside.Validate());
            StringAssert.Contains(overhang.Validate(), "one landblock per run");
            StringAssert.Contains(negative.Validate(), "one landblock per run");
        }

        [TestMethod]
        public void Spanning_RefusesAnUpsideDownOrNonFiniteBand()
        {
            StringAssert.Contains(Arena(96f, 96f, 60f, 50f, 10f).Validate(), "upside down");
            Assert.IsNotNull(Arena(96f, 96f, 60f, float.NaN, 10f).Validate());
            Assert.IsNotNull(Arena(96f, 96f, 60f, 10f, float.PositiveInfinity).Validate());
            Assert.IsNull(Arena(96f, 96f, 60f, 20f, 20f).Validate(), "a zero-height band is allowed");
        }

        [TestMethod]
        public void InBand_UsesTheAbsoluteBandWhenSet_IgnoringTheGround()
        {
            var r = Arena(96f, 96f, 60f, 10f, 50f);

            Assert.IsTrue(r.InBand(new Vector3(96f, 96f, 10f)));
            Assert.IsTrue(r.InBand(new Vector3(96f, 96f, 50f)));
            Assert.IsFalse(r.InBand(new Vector3(96f, 96f, 9.99f)));
            Assert.IsFalse(r.InBand(new Vector3(96f, 96f, 50.01f)));

            // the terrain sampler is not consulted for the band
            Assert.IsTrue(r.InBand(new Vector3(96f, 96f, 30f), (x, y) => 1000f));
            Assert.IsTrue(r.ContainsPlayer(new Vector3(96f, 96f, 30f), (x, y) => 1000f));
            Assert.IsFalse(r.ContainsPlayer(new Vector3(96f + 61f, 96f, 30f)), "outside the circle");
        }

        [TestMethod]
        public void InBand_WithoutAnAbsoluteBand_IsStillTheGroundRelativeBand()
        {
            var r = HazardRegion.Circle("t", 0xA9B4, 0, true, 96f, 96f, 20f, HazardGroundMode.Terrain, 0f, null, 0f, null, 2f, 6f);

            Assert.IsTrue(r.InBand(new Vector3(96f, 96f, 105f), (x, y) => 100f));
            Assert.IsFalse(r.InBand(new Vector3(96f, 96f, 107f), (x, y) => 100f));
            Assert.IsFalse(r.InBand(new Vector3(96f, 96f, 97f), (x, y) => 100f));
        }

        [TestMethod]
        public void WithInstance_CopiesTheSpanFields()
        {
            var copy = Arena(96f, 96f, 60f).WithInstance(0x1234);

            Assert.AreEqual(0x1234u, copy.Instance);
            Assert.IsTrue(copy.SpansLandblocks);
            Assert.IsTrue(copy.HitsRequireInside);
            Assert.AreEqual(10f, copy.AbsoluteBandMinZ);
            Assert.AreEqual(50f, copy.AbsoluteBandMaxZ);
            Assert.IsNull(copy.Validate());
        }
    }
}

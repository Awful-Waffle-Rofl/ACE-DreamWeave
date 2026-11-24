using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The wisps' mixed_pool arg: a restriction of the patterns "mixed" may pick (no scatter for the Harbinger's lightning).</summary>
    [TestClass]
    public class WispMixedPoolTests
    {
        private static HazardRegion Region30()
        {
            var r = HazardRegion.AdHoc(0x0123, 0, false, new Vector3(100f, -60f, -47.75f), HazardShapeSpec.Rect(30f, 30f), 0f, out var error);
            Assert.IsNull(error);
            return r;
        }

        [TestMethod]
        public void MixedPool_ExcludesScatter()
        {
            var s = WispSettings.From(HazardArgs.Parse("mixed_pool=row,column,diagonal,ring,sweep"));
            var pool = WispRules.Pool(Region30(), s).ToList();

            CollectionAssert.DoesNotContain(pool, WispRules.Scatter);
            CollectionAssert.Contains(pool, WispRules.Row);
            CollectionAssert.Contains(pool, WispRules.Diagonal);
        }

        [TestMethod]
        public void MixedPool_UnknownName_Refused()
            => StringAssert.Contains(WispRules.Validate(Region30(), HazardArgs.Parse("mixed_pool=row,zigzag"), "mixed"), "zigzag");

        [TestMethod]
        public void MixedPool_NamingMixed_Refused()
            => StringAssert.Contains(WispRules.Validate(Region30(), HazardArgs.Parse("mixed_pool=row,mixed"), "mixed"), "mixed");

        [TestMethod]
        public void MixedPool_LeavingNothingThatFits_Refused()
        {
            // dir=n allows rows but not columns; a pool of column alone leaves nothing
            var line = WispRules.Validate(Region30(), HazardArgs.Parse("mixed_pool=column dir=n"), "mixed");
            StringAssert.Contains(line, "mixed_pool leaves no pattern that fits this region");
        }

        [TestMethod]
        public void MixedPool_Absent_PoolUnchanged()
        {
            var s = WispSettings.From(HazardArgs.Empty);

            Assert.IsNull(s.MixedPool);
            CollectionAssert.Contains(WispRules.Pool(Region30(), s).ToList(), WispRules.Scatter);
        }

        [TestMethod]
        public void MixedPool_Valid_IsAccepted()
            => Assert.IsNull(WispRules.Validate(Region30(), HazardArgs.Parse("mixed_pool=row,column,diagonal"), "mixed"));

        private const string BigPillars = "pillar_radius=5 hold=3 scatter_cover=0.1";

        [TestMethod]
        public void MixedPool_WithoutScatter_SkipsTheScatterFitCheck()
            => Assert.IsNull(WispRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Parse("mixed_pool=row,diagonal " + BigPillars), "mixed"));

        [TestMethod]
        public void MixedPool_WithScatter_StillGetsTheScatterRefusal()
            => StringAssert.Contains(WispRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Parse("mixed_pool=scatter,row " + BigPillars), "mixed"), "too big for scatter");

        [TestMethod]
        public void MixedPool_WithoutRing_SkipsTheRingFitCheck()
        {
            var region = HazardRegionPresets.Pinnacle(0);

            Assert.IsNotNull(WispRules.Validate(region, HazardArgs.Parse("ring_radius=500"), "mixed"), "the ring is in the pool, so its misfit is refused");
            Assert.IsNull(WispRules.Validate(region, HazardArgs.Parse("ring_radius=500 mixed_pool=row,column"), "mixed"));
        }
    }
}

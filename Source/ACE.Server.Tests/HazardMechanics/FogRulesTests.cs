using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>FogRules and FogSettings: the lattice, Validate and the radar plan (Grael pit).</summary>
    [TestClass]
    public class FogRulesTests
    {
        private static HazardRegion Pit() => HazardRegionPresets.Pinnacle(0);

        private static Vector3 Normal(FogPanel p) => Vector3.Transform(Vector3.UnitZ, p.Rotation);

        [TestMethod]
        public void FogRules_GraelLattice_Is49Single98Doubled()
        {
            var single = FogRules.Lattice(Pit(), 8f, 5.7f, -47.75f, false);
            var doubled = FogRules.Lattice(Pit(), 8f, 5.7f, -47.75f, true);

            Assert.AreEqual(49, single.Count);
            Assert.AreEqual(98, doubled.Count);

            var alongY = single.Where(p => System.Math.Abs(Normal(p).Y) > 0.5f).ToList();
            var alongX = single.Where(p => System.Math.Abs(Normal(p).X) > 0.5f).ToList();

            Assert.AreEqual(24, alongY.Count, "4 constant-y lines of 6");
            Assert.AreEqual(25, alongX.Count, "5 constant-x lines of 5");

            var ys = alongY.Select(p => p.Centre.Y).Distinct().OrderBy(y => y).ToList();
            CollectionAssert.AreEqual(new[] { -30.95f, -23.65f, -16.35f, -9.05f }, ys.Select(y => (float)System.Math.Round(y, 2)).ToList());
            Assert.IsTrue(alongY.All(p => System.Math.Abs(p.Edge - 6.5333f) < 1e-3f));
            Assert.IsTrue(alongY.All(p => System.Math.Abs(p.Centre.Z + 44.4833f) < 1e-3f));

            var xs = alongX.Select(p => p.Centre.X).Distinct().OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(new[] { -0.68f, 7.16f, 15.0f, 22.84f, 30.68f }, xs.Select(x => (float)System.Math.Round(x, 2)).ToList());
            Assert.IsTrue(alongX.All(p => System.Math.Abs(p.Edge - 5.84f) < 1e-3f));
            Assert.IsTrue(alongX.All(p => System.Math.Abs(p.Centre.Z + 44.83f) < 1e-3f));

            // every panel's bottom sits on base_z
            Assert.IsTrue(single.All(p => System.Math.Abs(p.Centre.Z - p.Edge / 2f + 47.75f) < 1e-3f));

            // doubled: the partner shares the centre and faces the opposite way, still horizontal
            for (var i = 0; i < doubled.Count; i += 2)
            {
                Assert.AreEqual(doubled[i].Centre, doubled[i + 1].Centre);
                Assert.AreEqual(0f, (Normal(doubled[i]) + Normal(doubled[i + 1])).Length(), 1e-4f, "opposite normals");
                Assert.AreEqual(0f, Normal(doubled[i + 1]).Z, 1e-4f);
            }
        }

        [TestMethod]
        public void Lattice_ALineShorterThanTheHeight_UsesOnePanelOfEdgeHeight()
        {
            var room = HazardRegion.Rect("tiny", 0x0069, 0, false, 0f, 3f, 0f, 3f,
                HazardGroundMode.Plane, 0f, null, 0f, 10f, 1f, 1f);

            var panels = FogRules.Lattice(room, 8f, 5.7f, 0f, false);

            Assert.AreEqual(2, panels.Count, "one line each way, one panel each");
            Assert.IsTrue(panels.All(p => p.Edge == 5.7f));
        }

        [TestMethod]
        public void FogRules_Validate_RefusesOverMaxPanels()
        {
            Assert.AreEqual("fog lattice needs 98 panels; max_panels is 40", FogRules.Validate(Pit(), HazardArgs.Parse("max_panels=40")));
            Assert.IsNull(FogRules.Validate(Pit(), HazardArgs.Parse("max_panels=98")));
            Assert.IsNull(FogRules.Validate(Pit(), HazardArgs.Empty));
        }

        [TestMethod]
        public void Validate_RefusesWithOneLine_NeverThrows()
        {
            var circle = HazardRegion.AdHoc(0x0069, 0, false, Vector3.Zero, HazardShapeSpec.Circle(10f), 0f, out _);
            var terrain = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 30f), HazardShapeSpec.Rect(20f, 20f), 0f, out _);
            var noCeiling = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(0, 0, -47.75f), HazardShapeSpec.Rect(20f, 20f), 0f, out _);

            foreach (var (region, args) in new[]
            {
                (circle, ""),
                (terrain, ""),
                (noCeiling, ""),
                (Pit(), "spacing=wide"),
                (Pit(), "color=mud"),
            })
            {
                var line = FogRules.Validate(region, HazardArgs.Parse(args));

                Assert.IsNotNull(line, args);
                Assert.IsFalse(line.Contains('\n'), line);
            }

            Assert.AreEqual("height is required when the region has no ceiling", FogRules.Validate(noCeiling, HazardArgs.Empty));
            Assert.IsNull(FogRules.Validate(noCeiling, HazardArgs.Parse("height=5")));
            Assert.IsNotNull(FogRules.Validate(null, HazardArgs.Empty));
        }

        [TestMethod]
        public void Settings_Defaults_OnTheGraelPit()
        {
            var s = FogSettings.From(HazardArgs.Empty, Pit());

            Assert.AreEqual("smoke", s.Colour);
            Assert.AreEqual(0.15, s.Opacity, 1e-9);
            Assert.AreEqual(8f, s.Spacing);
            Assert.AreEqual(5.7f, s.Height, 1e-4f);
            Assert.AreEqual(-47.75f, s.BaseZ, 1e-4f);
            Assert.IsTrue(s.Doubled);
            Assert.AreEqual(160, s.MaxPanels);
            Assert.IsTrue(s.Radar);
            Assert.AreEqual(1.0, s.RadarScan, 1e-9);
            Assert.AreEqual(0.0, s.Duration);
            Assert.AreEqual(HazardDefaults.PanelSmokeWcid, s.PanelWcid);
        }

        [TestMethod]
        public void RadarPlan_HidesTheNewInside_RestoresTheLeft_AndRestoresAllWhenDisabled()
        {
            var on = FogRules.RadarPlan(new uint[] { 1, 2, 3 }, new uint[] { 2, 4 }, true);

            CollectionAssert.AreEqual(new uint[] { 1, 3 }, on.Hide);
            CollectionAssert.AreEqual(new uint[] { 4 }, on.Restore);

            var off = FogRules.RadarPlan(new uint[] { 1, 2, 3 }, new uint[] { 2, 4 }, false);

            Assert.AreEqual(0, off.Hide.Count);
            CollectionAssert.AreEqual(new uint[] { 2, 4 }, off.Restore);
        }
    }
}

using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>HazardPanels: colour -> wcid, rotations, scale, translucency, the spawn spec and the region refusal.</summary>
    [TestClass]
    public class HazardPanelsTests
    {
        private const float Eps = 1e-5f;

        private static Vector3 Normal(Quaternion q) => Vector3.Transform(Vector3.UnitZ, q);

        private static void AssertVec(Vector3 expected, Vector3 actual, string what)
        {
            Assert.AreEqual(expected.X, actual.X, Eps, what + " x");
            Assert.AreEqual(expected.Y, actual.Y, Eps, what + " y");
            Assert.AreEqual(expected.Z, actual.Z, Eps, what + " z");
        }

        [TestMethod]
        public void Rotations_TurnThePanelNormalTheNamedWay()
        {
            AssertVec(Vector3.UnitZ, Normal(HazardPanels.FaceUp), "FaceUp");
            AssertVec(-Vector3.UnitZ, Normal(HazardPanels.FaceDown), "FaceDown");
            AssertVec(Vector3.UnitY, Normal(HazardPanels.FaceAlongY(true)), "FaceAlongY(true)");
            AssertVec(-Vector3.UnitY, Normal(HazardPanels.FaceAlongY(false)), "FaceAlongY(false)");
            AssertVec(Vector3.UnitX, Normal(HazardPanels.FaceAlongX(true)), "FaceAlongX(true)");
            AssertVec(-Vector3.UnitX, Normal(HazardPanels.FaceAlongX(false)), "FaceAlongX(false)");
        }

        [TestMethod]
        public void Rotations_MatchTheShippedPinnacleShroudRows()
        {
            // pinnacle_arena.sql:124 ceiling row (w,x,y,z) = (0,1,0,0); :119 west wall row = (0.5,0.5,0.5,0.5)
            AssertVec(Normal(new Quaternion(1f, 0f, 0f, 0f)), Normal(HazardPanels.FaceDown), "ceiling row");
            AssertVec(Normal(new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)), Normal(HazardPanels.FaceAlongX(true)), "west wall row");
        }

        [TestMethod]
        public void Flipped_NegatesTheNormal_AndKeepsItHorizontal()
        {
            foreach (var q in new[] { HazardPanels.FaceAlongX(true), HazardPanels.FaceAlongX(false), HazardPanels.FaceAlongY(true), HazardPanels.FaceAlongY(false) })
            {
                var n = Normal(q);
                var f = Normal(HazardPanels.Flipped(q));

                AssertVec(-n, f, "flipped normal");
                Assert.AreEqual(0f, f.Z, Eps, "still horizontal");
            }
        }

        [TestMethod]
        public void TranslucencyFor_IsOneMinusOpacity_AndNullWhenOpaque()
        {
            Assert.IsNull(HazardPanels.TranslucencyFor(1.0));
            Assert.AreEqual(0.85f, HazardPanels.TranslucencyFor(0.15).Value, 1e-6f);
            Assert.AreEqual(1f, HazardPanels.TranslucencyFor(0).Value, 1e-6f);
            Assert.IsNull(HazardPanels.TranslucencyFor(2));
            Assert.AreEqual(1f, HazardPanels.TranslucencyFor(-1).Value, 1e-6f);
            Assert.IsNull(HazardPanels.TranslucencyFor(double.NaN));
        }

        [TestMethod]
        public void ScaleFor_IsEdgeOverTen()
            => Assert.AreEqual(1.04f, HazardPanels.ScaleFor(10.4f), 1e-6f);

        [TestMethod]
        public void Spec_NeverRaises_AndPassesMovingThrough()
        {
            var still = HazardPanels.Spec(HazardDefaults.PanelSmokeWcid, new Vector3(1, 2, 3), HazardPanels.FaceDown, 5f, 0.5, false, "t");
            var moving = HazardPanels.Spec(HazardDefaults.PanelAcidWcid, new Vector3(1, 2, 3), HazardPanels.FaceUp, 10.4f, 1.0, true, "t");

            Assert.AreEqual(false, still.AllowRaise);
            Assert.AreEqual(false, moving.AllowRaise);
            Assert.IsFalse(still.Moving);
            Assert.IsTrue(moving.Moving);
            Assert.AreEqual(0.5f, still.Scale, 1e-6f);
            Assert.AreEqual(0.5f, still.Translucency.Value, 1e-6f);
            Assert.IsNull(moving.Translucency);
            Assert.AreEqual(HazardDefaults.PanelSmokeWcid, still.Wcid);
            Assert.AreEqual(new Vector3(1, 2, 3), still.Position);
        }

        [TestMethod]
        public void TryWcid_MapsColours_PoisonIsAcid_UnknownRefused()
        {
            Assert.IsTrue(HazardPanels.TryWcid("POISON", out var w));
            Assert.AreEqual(1006967u, w);
            Assert.IsTrue(HazardPanels.TryWcid("Lava", out w));
            Assert.AreEqual(1006968u, w);
            Assert.IsTrue(HazardPanels.TryWcid("smoke", out w));
            Assert.AreEqual(1006969u, w);
            Assert.IsFalse(HazardPanels.TryWcid("mud", out _));
            Assert.IsFalse(HazardPanels.TryWcid(null, out _));
            CollectionAssert.AreEqual(new[] { "acid", "lava", "smoke", "poison" }, new System.Collections.Generic.List<string>(HazardPanels.Colours));
        }

        [TestMethod]
        public void RegionRefusal_RectPlaneOnly()
        {
            Assert.AreEqual("no region", HazardPanels.RegionRefusal("fill", null));

            var circle = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(0, 0, 0), HazardShapeSpec.Circle(10f), 0f, out var e1);
            Assert.IsNull(e1);
            Assert.AreEqual("fill needs a rect region (contained rooms only in this build)", HazardPanels.RegionRefusal("fill", circle));

            var terrain = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 30f), HazardShapeSpec.Rect(20f, 20f), 0f, out var e2);
            Assert.IsNull(e2);
            Assert.AreEqual("fog needs a plane ground (contained rooms only in this build)", HazardPanels.RegionRefusal("fog", terrain));

            Assert.IsNull(HazardPanels.RegionRefusal("fill", HazardRegionPresets.Pinnacle(0)));
        }
    }
}

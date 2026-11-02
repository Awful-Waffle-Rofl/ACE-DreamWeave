using System;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>HazardLevelScale (pure) and the two live clamp settings pinnacle_hazard_pct_min / _max.</summary>
    [TestClass]
    public class HazardLevelScaleTests
    {
        private const double Eps = 1e-9;

        [TestMethod]
        public void BenchmarkPct_DefaultClamps_AtTheDesignLevels()
        {
            const double min = 0.25, max = 0.75;

            Assert.AreEqual(0.25, HazardLevelScale.BenchmarkPct(0, min, max), Eps, "L0 raw -0.25 clamps up");
            Assert.AreEqual(0.25, HazardLevelScale.BenchmarkPct(100, min, max), Eps, "L100 raw 0.0 clamps up");
            Assert.AreEqual(0.25, HazardLevelScale.BenchmarkPct(200, min, max), Eps);
            Assert.AreEqual(0.50, HazardLevelScale.BenchmarkPct(300, min, max), Eps);
            Assert.AreEqual(0.75, HazardLevelScale.BenchmarkPct(400, min, max), Eps);
            Assert.AreEqual(0.75, HazardLevelScale.BenchmarkPct(500, min, max), Eps, "L500 raw 1.0 clamps down");
        }

        [TestMethod]
        public void BenchmarkPct_CustomClamps()
        {
            Assert.AreEqual(0.10, HazardLevelScale.BenchmarkPct(100, 0.10, 1.0), Eps, "raw 0.0 clamps up to the custom min");
            Assert.AreEqual(0.50, HazardLevelScale.BenchmarkPct(300, 0.10, 1.0), Eps, "inside the band: untouched");
            Assert.AreEqual(1.00, HazardLevelScale.BenchmarkPct(500, 0.10, 1.0), Eps, "raw 1.0 is inside a wider max");
            Assert.AreEqual(0.40, HazardLevelScale.BenchmarkPct(500, 0.10, 0.40), Eps, "narrow max");
            Assert.AreEqual(0.40, HazardLevelScale.BenchmarkPct(0, 0.40, 0.40), Eps, "min == max pins it");
        }

        [TestMethod]
        public void Scaled_KeepsTheAuthoredRatioToTheBall()
        {
            const double min = 0.25, max = 0.75;

            Assert.AreEqual(HazardLevelScale.BenchmarkPct(300, min, max), HazardLevelScale.Scaled(300, 0.60, min, max), Eps, "the ball itself");
            Assert.AreEqual(0.25, HazardLevelScale.Scaled(300, 0.30, min, max), Eps, "half the ball");
            Assert.AreEqual(0.05 / 0.60 * 0.75, HazardLevelScale.Scaled(400, 0.05, min, max), Eps, "corner start at L400");
            Assert.AreEqual(0.35 / 0.60 * 0.75, HazardLevelScale.Scaled(400, 0.35, min, max), Eps, "corner end at L400");
            Assert.AreEqual(0.0, HazardLevelScale.Scaled(400, 0.0, min, max), Eps, "visual-only stays visual-only");
        }

        [TestMethod]
        public void Sanitize_ClampsEachBoundTo0To5_AndFallsBackWhenMinExceedsMax()
        {
            Assert.AreEqual((0.25, 0.75), PinnacleTunables.SanitizeHazardPct(0.25, 0.75));
            Assert.AreEqual((0.0, 5.0), PinnacleTunables.SanitizeHazardPct(-3, 99));
            Assert.AreEqual((0.5, 0.5), PinnacleTunables.SanitizeHazardPct(0.5, 0.5));
            Assert.AreEqual((0.25, 0.75), PinnacleTunables.SanitizeHazardPct(0.9, 0.3), "min above max: both defaults");
            Assert.AreEqual((0.25, 0.75), PinnacleTunables.SanitizeHazardPct(double.NaN, double.PositiveInfinity), "non-finite: defaults");
            Assert.AreEqual((0.25, 5.0), PinnacleTunables.SanitizeHazardPct(double.NaN, 9), "one bad bound defaults alone");
        }

        [TestMethod]
        public void Settings_DefaultsAreRegistered_AndInTheSnapshots()
        {
            Assert.AreEqual(0.25, DefaultPropertyManager.DefaultDoubleProperties[PinnacleTunables.HazardPctMinKey].Item, Eps);
            Assert.AreEqual(0.75, DefaultPropertyManager.DefaultDoubleProperties[PinnacleTunables.HazardPctMaxKey].Item, Eps);
            Assert.AreEqual("pinnacle_hazard_pct_min", PinnacleTunables.HazardPctMinKey);
            Assert.AreEqual("pinnacle_hazard_pct_max", PinnacleTunables.HazardPctMaxKey);

            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv");
            var meta = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv");

            StringAssert.Contains(defaults, "pinnacle_hazard_pct_min\tdouble\t0.25\t0.25");
            StringAssert.Contains(defaults, "pinnacle_hazard_pct_max\tdouble\t0.75\t0.75");

            foreach (var key in new[] { PinnacleTunables.HazardPctMinKey, PinnacleTunables.HazardPctMaxKey })
                Assert.IsTrue(meta.Split('\n').Any(l => l.StartsWith(key + "\t", StringComparison.Ordinal)), key);
        }

        [TestMethod]
        public void Settings_DescriptionsCarryTheClampAndDefault()
        {
            foreach (var key in new[] { PinnacleTunables.HazardPctMinKey, PinnacleTunables.HazardPctMaxKey })
            {
                var d = DefaultPropertyManager.DefaultDoubleProperties[key].Description;

                StringAssert.Contains(d, "0 to 5", key);
                StringAssert.Contains(d, "Read when a fight is created", key);
                StringAssert.Contains(d, "Default", key);
            }
        }

        private static void WithSeeded(double min, double max, Action body)
        {
            Assert.IsTrue(PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMinKey, min), "min key missing from DefaultDoubleProperties");
            Assert.IsTrue(PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMaxKey, max), "max key missing from DefaultDoubleProperties");

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMinKey, PinnacleTunables.DefaultHazardPctMin);
                PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMaxKey, PinnacleTunables.DefaultHazardPctMax);
            }
        }

        [TestMethod]
        public void HazardPctClamp_ReadsTheLiveSettings()
        {
            WithSeeded(0.25, 0.75, () => Assert.AreEqual((0.25, 0.75), PinnacleTunables.HazardPctClamp()));
            WithSeeded(0.10, 1.50, () => Assert.AreEqual((0.10, 1.50), PinnacleTunables.HazardPctClamp()));
            WithSeeded(0.5, 0.5, () => Assert.AreEqual((0.5, 0.5), PinnacleTunables.HazardPctClamp()));
        }

        [TestMethod]
        public void HazardPctClamp_ClampsAndFallsBack()
        {
            WithSeeded(-1, 50, () => Assert.AreEqual((0.0, 5.0), PinnacleTunables.HazardPctClamp()));
            WithSeeded(0.9, 0.3, () => Assert.AreEqual((0.25, 0.75), PinnacleTunables.HazardPctClamp()));
        }

        [TestMethod]
        public void HazardPctClamp_FeedsTheScale()
        {
            WithSeeded(0.30, 0.60, () =>
            {
                var (min, max) = PinnacleTunables.HazardPctClamp();

                Assert.AreEqual(0.30, HazardLevelScale.BenchmarkPct(200, min, max), Eps);
                Assert.AreEqual(0.60, HazardLevelScale.BenchmarkPct(400, min, max), Eps);
            });
        }
    }
}

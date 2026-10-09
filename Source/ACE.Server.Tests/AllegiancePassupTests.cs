using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DreamWeave allegiance passup: flat rates. A patron always receives a fixed share of a
    /// vassal's earned XP, and the grandpatron always receives a fixed (smaller) share, regardless
    /// of how many vassals the patron holds. Replaces the old diminishing-returns curve, which
    /// penalised patrons for adopting new players or retaining idle ones.
    /// </summary>
    [TestClass]
    public class AllegiancePassupTests
    {
        [TestMethod]
        public void PatronPassupRate_DefaultsToTenPercent()
        {
            Assert.AreEqual(0.10, AllegianceManager.PatronPassupRate, 1e-9);
        }

        [TestMethod]
        public void GrandPatronPassupRate_DefaultsToTwoPercent()
        {
            Assert.AreEqual(0.02, AllegianceManager.GrandPatronPassupRate, 1e-9);
        }

        [TestMethod]
        public void GetPatronPassupRate_ReturnsTheCompiledDefault_WhenNoShardConfig()
        {
            // ACE.Server.Tests has no shard config, so this exercises the NullReferenceException
            // fallback path in GetPassupTunable.
            Assert.AreEqual(AllegianceManager.PatronPassupRate, AllegianceManager.GetPatronPassupRate(), 1e-9);
        }

        [TestMethod]
        public void GetGrandPatronPassupRate_ReturnsTheCompiledDefault_WhenNoShardConfig()
        {
            Assert.AreEqual(AllegianceManager.GrandPatronPassupRate, AllegianceManager.GetGrandPatronPassupRate(), 1e-9);
        }

        [TestMethod]
        public void ClampPassupRate_LeavesInRangeValuesUnchanged()
        {
            Assert.AreEqual(0.0, AllegianceManager.ClampPassupRate(0.0), 1e-12);
            Assert.AreEqual(0.10, AllegianceManager.ClampPassupRate(0.10), 1e-12);
            Assert.AreEqual(1.0, AllegianceManager.ClampPassupRate(1.0), 1e-12);
        }

        [TestMethod]
        public void ClampPassupRate_ClampsAboveOneDownToOne()
        {
            // An admin typo must never hand a patron more XP than the vassal actually earned - the
            // ulong cast in DoPassXP saturates rather than throwing, so this clamp is load-bearing.
            Assert.AreEqual(1.0, AllegianceManager.ClampPassupRate(1.5), 1e-12);
            Assert.AreEqual(1.0, AllegianceManager.ClampPassupRate(1000.0), 1e-12);
            Assert.AreEqual(1.0, AllegianceManager.ClampPassupRate(double.MaxValue), 1e-12);
        }

        [TestMethod]
        public void ClampPassupRate_ClampsNegativeDownToZero()
        {
            Assert.AreEqual(0.0, AllegianceManager.ClampPassupRate(-0.01), 1e-12);
            Assert.AreEqual(0.0, AllegianceManager.ClampPassupRate(-5.0), 1e-12);
        }

        [TestMethod]
        public void ClampPassupRate_NaNFallsBackToTheGivenFallback()
        {
            Assert.AreEqual(0.10, AllegianceManager.ClampPassupRate(double.NaN, 0.10), 1e-12);
            Assert.AreEqual(0.0, AllegianceManager.ClampPassupRate(double.NaN), 1e-12);
        }

        [TestMethod]
        public void ClampPassupRate_InfinityFallsBackToTheGivenFallback()
        {
            Assert.AreEqual(0.02, AllegianceManager.ClampPassupRate(double.PositiveInfinity, 0.02), 1e-12);
            Assert.AreEqual(0.02, AllegianceManager.ClampPassupRate(double.NegativeInfinity, 0.02), 1e-12);
        }

        [TestMethod]
        public void ClampPassupRate_OutOfRangeFallbackIsAlsoClamped()
        {
            // If the fallback itself is nonsense, do not let NaN/infinity input smuggle it through
            // unclamped.
            Assert.AreEqual(1.0, AllegianceManager.ClampPassupRate(double.NaN, 5.0), 1e-12);
            Assert.AreEqual(0.0, AllegianceManager.ClampPassupRate(double.NaN, -5.0), 1e-12);
        }

        [TestMethod]
        public void PatronRate_IsFlat_RegardlessOfVassalCount()
        {
            // The old model varied the rate with TotalVassals; the new one takes no vassal count
            // parameter at all, which this test would fail to compile against if that reappeared.
            var first = AllegianceManager.GetPatronPassupRate();
            var second = AllegianceManager.GetPatronPassupRate();

            Assert.AreEqual(first, second, 1e-12);
            Assert.AreEqual(0.10, first, 1e-9);
        }
    }
}

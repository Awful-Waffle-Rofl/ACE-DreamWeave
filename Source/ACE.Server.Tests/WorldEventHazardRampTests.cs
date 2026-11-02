using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The pure live-count ramp of a World Event hazard (WorldEventHazards.CountAt).</summary>
    [TestClass]
    public class WorldEventHazardRampTests
    {
        private const double Active = 1000.0;

        private static int At(double elapsed, int min = 300, int low = 4, int high = 10, bool boss = false, bool placed = false)
            => WorldEventHazards.CountAt(Active + elapsed, Active, min, low, high, boss, placed);

        [TestMethod]
        public void NoBoss_RampsLinearly_FromLowToHigh_OverTheMinimumDuration()
        {
            Assert.AreEqual(4, At(0));
            Assert.AreEqual(6, At(75));    // 4 + 6 * 0.25 = 5.5, rounded half away from zero
            Assert.AreEqual(7, At(150));
            Assert.AreEqual(10, At(300));
        }

        [TestMethod]
        public void NoBoss_HoldsHigh_AfterTheMinimum()
        {
            Assert.AreEqual(10, At(301));
            Assert.AreEqual(10, At(5000));
        }

        [TestMethod]
        public void BeforeActive_ReadsLow()
        {
            Assert.AreEqual(4, At(-50));
        }

        [TestMethod]
        public void Boss_RampsToHighMinusOne_AndHoldsThereUntilPlaced()
        {
            Assert.AreEqual(4, At(0, boss: true));
            Assert.AreEqual(9, At(300, boss: true));
            Assert.AreEqual(9, At(900, boss: true), "an unplaced boss keeps the count a step short");
        }

        [TestMethod]
        public void Boss_PlacedLiftsTheCountToHigh()
        {
            Assert.AreEqual(10, At(301, boss: true, placed: true));
            Assert.AreEqual(10, At(2000, boss: true, placed: true));
        }

        [TestMethod]
        public void ZeroMinimum_ReadsHighAtOnce()
        {
            Assert.AreEqual(10, At(0, min: 0));
            Assert.AreEqual(10, At(0, min: 0, boss: true));
        }

        [TestMethod]
        public void IsMonotonic_AndStaysWithinTheRange()
        {
            var previous = 0;

            for (var t = 0; t <= 400; t++)
            {
                var c = At(t);

                Assert.IsTrue(c >= previous, $"t={t}: {c} fell below {previous}");
                Assert.IsTrue(c >= 4 && c <= 10, $"t={t}: {c}");

                previous = c;
            }
        }

        [TestMethod]
        public void BadRange_IsRepaired()
        {
            Assert.AreEqual(1, WorldEventHazards.CountAt(Active, Active, 300, 0, 0, false, false), "low is raised to 1");
            Assert.AreEqual(6, WorldEventHazards.CountAt(Active + 300, Active, 300, 6, 3, false, false), "high is raised to low");
            Assert.AreEqual(3, WorldEventHazards.CountAt(Active + 300, Active, 300, 3, 3, true, false), "a boss cannot hold the count under low");
        }
    }
}

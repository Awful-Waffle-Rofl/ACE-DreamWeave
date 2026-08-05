using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure enlightenment-milestone entitlement rule (EnlightenmentCapMilestones): one class
    /// ability point at ENL 1, 2, 3, 4, 5, 7, 10, 15, 20, 25, 30, 35, 40, 45, 50, then every 10 beyond 50
    /// (DESIGN.md sec 2c). The idempotent grant/catch-up that consumes this
    /// (Player.GrantEnlightenmentClassAbilityPoints) needs a live Player and is exercised in-game on
    /// enlightenment and login.
    /// </summary>
    [TestClass]
    public class EnlightenmentCapMilestonesTests
    {
        [TestMethod]
        public void EntitledCount_FixedScheduleValues()
        {
            Assert.AreEqual(0, EnlightenmentCapMilestones.EntitledCount(0));
            Assert.AreEqual(1, EnlightenmentCapMilestones.EntitledCount(1));
            Assert.AreEqual(5, EnlightenmentCapMilestones.EntitledCount(5));
            Assert.AreEqual(5, EnlightenmentCapMilestones.EntitledCount(6));  // 6 is not a schedule entry (next is 7)
            Assert.AreEqual(6, EnlightenmentCapMilestones.EntitledCount(7));
            Assert.AreEqual(6, EnlightenmentCapMilestones.EntitledCount(9));
            Assert.AreEqual(7, EnlightenmentCapMilestones.EntitledCount(10));
            Assert.AreEqual(7, EnlightenmentCapMilestones.EntitledCount(14));
            Assert.AreEqual(8, EnlightenmentCapMilestones.EntitledCount(15));
        }

        [TestMethod]
        public void EntitledCount_TailEvery10Past50()
        {
            Assert.AreEqual(15, EnlightenmentCapMilestones.EntitledCount(50));
            Assert.AreEqual(15, EnlightenmentCapMilestones.EntitledCount(55)); // no new milestone until 60
            Assert.AreEqual(16, EnlightenmentCapMilestones.EntitledCount(60));
            Assert.AreEqual(20, EnlightenmentCapMilestones.EntitledCount(100)); // 15 + (100-50)/10
        }

        [TestMethod]
        public void EntitledCount_NegativeIsZero()
        {
            Assert.AreEqual(0, EnlightenmentCapMilestones.EntitledCount(-1));
            Assert.AreEqual(0, EnlightenmentCapMilestones.EntitledCount(-100));
        }

        [TestMethod]
        public void EntitledCount_IsMonotonicAcrossEveryEnlightenment()
        {
            var previous = 0;

            for (var enl = 0; enl <= 200; enl++)
            {
                var entitled = EnlightenmentCapMilestones.EntitledCount(enl);
                Assert.IsTrue(entitled >= previous, $"entitlement decreased at enlightenment {enl}");
                previous = entitled;
            }
        }

        [TestMethod]
        public void NextMilestoneAfter_ReturnsSmallestScheduleEntryStrictlyGreater()
        {
            Assert.AreEqual(1, EnlightenmentCapMilestones.NextMilestoneAfter(0));
            Assert.AreEqual(7, EnlightenmentCapMilestones.NextMilestoneAfter(5));
            Assert.AreEqual(10, EnlightenmentCapMilestones.NextMilestoneAfter(7));
            Assert.AreEqual(15, EnlightenmentCapMilestones.NextMilestoneAfter(12));
            Assert.AreEqual(60, EnlightenmentCapMilestones.NextMilestoneAfter(50));
            Assert.AreEqual(70, EnlightenmentCapMilestones.NextMilestoneAfter(60));
            Assert.AreEqual(110, EnlightenmentCapMilestones.NextMilestoneAfter(105));
        }
    }
}

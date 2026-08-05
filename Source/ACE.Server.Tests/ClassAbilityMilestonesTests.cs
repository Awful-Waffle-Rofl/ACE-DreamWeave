using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure level-milestone entitlement rule (ClassAbilityMilestones.EntitledCount): one class
    /// skill point at each of levels 20, 40, 60, 80, 100, 125, 150, 175, 200, 225, 250, 275 (twelve total,
    /// DESIGN.md sec 2a). The idempotent grant/catch-up that consumes this (Player.GrantMilestoneClassAbilityPoints)
    /// needs a live Player and is exercised in-game on level-up and login.
    /// </summary>
    [TestClass]
    public class ClassAbilityMilestonesTests
    {
        [TestMethod]
        public void ThereAreExactlyTwelveMilestones()
        {
            Assert.AreEqual(12, ClassAbilityMilestones.Levels.Count);
            Assert.AreEqual(12, ClassAbilityMilestones.EntitledCount(275));
            Assert.AreEqual(12, ClassAbilityMilestones.EntitledCount(9999));
        }

        [TestMethod]
        public void BelowFirstMilestone_EntitlesNothing()
        {
            Assert.AreEqual(0, ClassAbilityMilestones.EntitledCount(1));
            Assert.AreEqual(0, ClassAbilityMilestones.EntitledCount(19));
        }

        [TestMethod]
        public void EntitlementIsInclusiveOfTheMilestoneLevel()
        {
            Assert.AreEqual(1, ClassAbilityMilestones.EntitledCount(20));
            Assert.AreEqual(1, ClassAbilityMilestones.EntitledCount(39));
            Assert.AreEqual(2, ClassAbilityMilestones.EntitledCount(40));
        }

        [TestMethod]
        public void MidAndLateMilestonesCountCorrectly()
        {
            // 20,40,60,80,100 reached by level 100 -> 5
            Assert.AreEqual(5, ClassAbilityMilestones.EntitledCount(100));
            // + 125,150,175,200 by level 200 -> 9
            Assert.AreEqual(9, ClassAbilityMilestones.EntitledCount(200));
            // + 225,250 by level 260 (275 not yet) -> 11
            Assert.AreEqual(11, ClassAbilityMilestones.EntitledCount(260));
        }

        [TestMethod]
        public void EntitlementIsMonotonicAcrossEveryLevel()
        {
            var previous = 0;

            for (var level = 1; level <= 300; level++)
            {
                var entitled = ClassAbilityMilestones.EntitledCount(level);
                Assert.IsTrue(entitled >= previous, $"entitlement decreased at level {level}");
                previous = entitled;
            }

            Assert.AreEqual(12, previous);
        }
    }
}

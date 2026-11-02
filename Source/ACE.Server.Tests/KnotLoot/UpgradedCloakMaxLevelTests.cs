using ACE.Server.KnotLoot;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>The upgraded cloak's max-level window by Knot level (ruling 3), and the roll over it.</summary>
    [TestClass]
    public class UpgradedCloakMaxLevelTests
    {
        [DataTestMethod]
        [DataRow(0, 5, 6)]
        [DataRow(150, 5, 6)]
        [DataRow(200, 5, 6)]
        [DataRow(225, 5, 7)]
        [DataRow(250, 6, 7)]
        [DataRow(275, 6, 8)]
        [DataRow(300, 7, 8)]
        [DataRow(325, 7, 9)]
        [DataRow(350, 7, 9)]
        [DataRow(375, 8, 10)]
        [DataRow(400, 8, 10)]
        [DataRow(500, 8, 10)]
        [DataRow(int.MaxValue, 8, 10)]
        [DataRow(int.MinValue, 5, 6)]
        public void Window_MatchesTheRulingTable(int knotLevel, int low, int high)
            => Assert.AreEqual((low, high), UpgradedCloak.MaxLevelWindow(knotLevel));

        [TestMethod]
        public void Roll_CoversTheWindowEvenly_AndClampsAtTheTop()
        {
            // 375: window 8-10, three equal thirds
            Assert.AreEqual(8, UpgradedCloak.RollMaxLevel(375, 0.0));
            Assert.AreEqual(8, UpgradedCloak.RollMaxLevel(375, 0.333));
            Assert.AreEqual(9, UpgradedCloak.RollMaxLevel(375, 0.334));
            Assert.AreEqual(9, UpgradedCloak.RollMaxLevel(375, 0.666));
            Assert.AreEqual(10, UpgradedCloak.RollMaxLevel(375, 0.667));
            Assert.AreEqual(10, UpgradedCloak.RollMaxLevel(375, 0.999999));
            Assert.AreEqual(10, UpgradedCloak.RollMaxLevel(375, 1.0), "u at 1 clamps to hi");
            Assert.AreEqual(10, UpgradedCloak.RollMaxLevel(375, 7.0));

            // 200: window 5-6, halves
            Assert.AreEqual(5, UpgradedCloak.RollMaxLevel(200, 0.4999));
            Assert.AreEqual(6, UpgradedCloak.RollMaxLevel(200, 0.5));
        }

        [TestMethod]
        public void Roll_BadDraw_ReadsAsTheLowEnd()
        {
            Assert.AreEqual(7, UpgradedCloak.RollMaxLevel(300, double.NaN));
            Assert.AreEqual(7, UpgradedCloak.RollMaxLevel(300, -0.5));
        }

        [TestMethod]
        public void Roll_EveryLevelStaysInsideItsWindow()
        {
            for (var knot = 150; knot <= 450; knot++)
            {
                var (lo, hi) = UpgradedCloak.MaxLevelWindow(knot);

                Assert.IsTrue(lo >= 5 && hi <= 10 && lo <= hi, $"knot {knot}: {lo}-{hi}");

                for (var step = 0; step <= 20; step++)
                {
                    var level = UpgradedCloak.RollMaxLevel(knot, step / 20.0);
                    Assert.IsTrue(level >= lo && level <= hi, $"knot {knot} u {step / 20.0}: {level}");
                }
            }
        }
    }
}

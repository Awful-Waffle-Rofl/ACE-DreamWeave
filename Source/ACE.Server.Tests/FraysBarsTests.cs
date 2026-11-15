using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Frays' bars: faster Heat, chat bars, and the Great Snarl's bar sized to the campers.</summary>
    [TestClass]
    public class FraysBarsTests
    {
        [TestMethod]
        public void Bar_IsAsciiAndClamped()
        {
            Assert.AreEqual("[----------] 0%", FraysRules.Bar(0));
            Assert.AreEqual("[####------] 40%", FraysRules.Bar(0.4));
            Assert.AreEqual("[#########-] 99%", FraysRules.Bar(0.999));
            Assert.AreEqual("[##########] 100%", FraysRules.Bar(1.7));
            Assert.AreEqual("[----------] 0%", FraysRules.Bar(-1));
            Assert.AreEqual("[----------] 0%", FraysRules.Bar(double.NaN));
            Assert.IsTrue(FraysRules.Bar(0.55).All(ch => ch < 128), "ASCII only in player text");
        }

        [TestMethod]
        public void BarStep_CountsWholeSteps()
        {
            Assert.AreEqual(0, FraysRules.BarStep(0.24, 25));
            Assert.AreEqual(1, FraysRules.BarStep(0.25, 25));
            Assert.AreEqual(3, FraysRules.BarStep(0.3, 10));
            Assert.AreEqual(-1, FraysRules.BarStep(0.9, 0), "off");
        }

        [TestMethod]
        public void GreatSnarlNeed_GrowsWithCampers_AtLeastOne()
        {
            Assert.AreEqual(350, FraysRules.GreatSnarlNeed(100, 250, 0));
            Assert.AreEqual(350, FraysRules.GreatSnarlNeed(100, 250, 1));
            Assert.AreEqual(1100, FraysRules.GreatSnarlNeed(100, 250, 4));
            Assert.AreEqual(1, FraysRules.GreatSnarlNeed(0, 0, 3), "never zero");
        }

        [TestMethod]
        public void FasterHeat_Defaults_ReachHeat10InAboutAQuarterOfTheKills()
        {
            var b = FraysSettings.All.First(d => d.Key == FraysSettings.HeatPointsBase).Default;
            var per = FraysSettings.All.First(d => d.Key == FraysSettings.HeatPointsPerLevel).Default;
            Assert.AreEqual(10.0, b);
            Assert.AreEqual(1.0, per);

            var toTen = Enumerable.Range(0, 10).Sum(l => FraysRules.PointsToNext(l, (int)b, (int)per));
            Assert.AreEqual(145, toTen, "was 425 at 20 + 5 a level, then 190 at 10 + 2");
        }

        [TestMethod]
        public void HeatCredit_PaysTheRealHealth_SoLevelsStayEven()
        {
            Assert.AreEqual(1.9, FraysRules.HeatCredit(1, 1, 1.9), 1e-9);
            Assert.AreEqual(0.95, FraysRules.HeatCredit(1, 0.5, 1.9), 1e-9, "shared by damage done");
            Assert.AreEqual(2.0, FraysRules.HeatCredit(2, 1, 0.5), 1e-9, "never less than the base health");
            Assert.AreEqual(0, FraysRules.HeatCredit(-1, 1, 2), 1e-9);

            // At a steady pace of health removed (125 standard creatures an hour at Heat 0), a level
            // takes about 8 minutes at Heat 6 and stays under 15 minutes all the way to Heat 20.
            const double pace = 125;
            // Kills an hour fall with the Heat health (heat_curve_health: 1.46 at Heat 6, 5.25 at 19); each pays it back.
            double Minutes(int heat, double health) => FraysRules.PointsToNext(heat, 10, 1) / (pace / health * FraysRules.HeatCredit(1, 1, health)) * 60;
            Assert.AreEqual(7.7, Minutes(6, 1.46), 0.2);
            Assert.IsTrue(Minutes(19, 5.25) < 15);
        }

        [TestMethod]
        public void NextMilestone_NamesTheFirstChange()
        {
            Assert.AreEqual("Heat 5: one more creature a spawn.", FraysRules.NextMilestone(3, 20, 5));
            Assert.AreEqual("Heat 10: one more creature a spawn.", FraysRules.NextMilestone(7, 20, 5));
            Assert.AreEqual("", FraysRules.NextMilestone(20, 20, 5));
        }
    }
}

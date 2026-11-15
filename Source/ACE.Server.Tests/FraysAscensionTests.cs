using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Ascension rules: an Ember cost, one a week, a title, a camp spark, a /top column.</summary>
    [TestClass]
    public class FraysAscensionTests
    {
        [TestMethod]
        public void Cost_IsTheAscensionTimesTheBase()
        {
            Assert.AreEqual(5000, FraysSettings.Int(FraysSettings.AscensionCost));
            Assert.AreEqual(1, FraysSettings.Int(FraysSettings.AscensionPerWeek));
            var all = Enumerable.Range(1, 5).Sum(n => FraysLongTerm.AscensionCostFor(n, 5000));
            Assert.AreEqual(5000, FraysLongTerm.AscensionCostFor(1, 5000));
            Assert.AreEqual(25000, FraysLongTerm.AscensionCostFor(5, 5000));
            Assert.AreEqual(75000, all, "5k, 10k, 15k, 20k, 25k");
        }

        [TestMethod]
        public void OnePerWeek_CountsOnlyThisWeek()
        {
            var rec = new FraysPlayerRecord { AscendWeek = 148, AscendsThisWeek = 1 };
            Assert.AreEqual(1, FraysLongTerm.AscendsThisWeek(rec, 148));
            Assert.AreEqual(0, FraysLongTerm.AscendsThisWeek(rec, 149), "a new week opens the next one");
            Assert.AreEqual(0, FraysLongTerm.AscendsThisWeek(new FraysPlayerRecord(), 148), "never ascended");
        }

        [TestMethod]
        public void Titles_OnePerAscension_AsciiOnly()
        {
            Assert.AreEqual(5, FraysLongTerm.AscensionTitles.Length);
            Assert.IsNull(FraysLongTerm.AscensionTitle(0));
            Assert.AreEqual("the Kindled", FraysLongTerm.AscensionTitle(1));
            Assert.AreEqual("the Ashen Sovereign", FraysLongTerm.AscensionTitle(5));
            Assert.AreEqual("the Ashen Sovereign", FraysLongTerm.AscensionTitle(7), "past the list: the last");
            Assert.IsTrue(FraysLongTerm.AscensionTitles.All(t => t.All(ch => ch < 128)));
            Assert.AreEqual("", FraysLeaderboards.DetailText(0));
            Assert.AreEqual(" - Ascension III, the Flamewarden", FraysLeaderboards.DetailText(3));
            Assert.AreEqual("IV", FraysLongTerm.Roman(4));
        }

        [TestMethod]
        public void Spark_GrowsWithEachAscension()
        {
            var counts = Enumerable.Range(0, 6).Select(n => FraysLongTerm.AscensionSparkScripts(n).Length).ToArray();
            CollectionAssert.AreEqual(new[] { 0, 2, 4, 6, 8, 12 }, counts);
            Assert.AreEqual(12, FraysLongTerm.AscensionSparkScripts(9).Length, "held at V");
            Assert.AreEqual(12, FraysLongTerm.AscensionSparkScripts(5).Distinct().Count());
        }

        [TestMethod]
        public void Ascension_NeedsACampAtHeat20_OneAWeek()
        {
            Assert.AreEqual(20, FraysSettings.Int(FraysSettings.AscensionHeat));
            Assert.AreEqual(1, FraysSettings.Int(FraysSettings.AscensionPerWeek));
        }

        [TestMethod]
        public void GreatSnarl_AboutFiveADay_WithAFourHourGap()
        {
            Assert.AreEqual(300, FraysSettings.Number(FraysSettings.GreatSnarlWork), 1e-9);
            Assert.AreEqual(750, FraysSettings.Number(FraysSettings.GreatSnarlWorkPerCamper), 1e-9);
            Assert.AreEqual(4, FraysSettings.Number(FraysSettings.GreatSnarlGapHours), 1e-9);
            Assert.AreEqual(20, FraysSettings.Number(FraysSettings.GreatHoldMinutes), 1e-9);

            // 5 campers at the average 164 an hour: (300 + 750 x 5) / (164 x 5) = about 4.9 hours a bar
            var need = FraysRules.GreatSnarlNeed(300, 750, 5);
            Assert.AreEqual(4.9, need / (164.0 * 5), 0.1);

            Assert.AreEqual(0, FraysRules.GreatGapLeft(0, 4, 1000), 1e-9, "never called: now");
            Assert.AreEqual(4 * 3600 - 600, FraysRules.GreatGapLeft(1000, 4, 1600), 1e-9);
            Assert.AreEqual(0, FraysRules.GreatGapLeft(1000, 4, 1000 + 4 * 3600), 1e-9);
            Assert.AreEqual(0, FraysRules.GreatGapLeft(1000, 0, 1001), 1e-9, "0 = no gap");
        }

        [TestMethod]
        public void LucidNight_OnlyOnTheFinalNight_After25GreatSnarls()
        {
            Assert.AreEqual(25, FraysSettings.Int(FraysSettings.LucidGreatSnarls));
            Assert.AreEqual(24, FraysSettings.Number(FraysSettings.LucidHours), 1e-9);
            Assert.IsTrue(FraysRules.LucidOn(25, 25, 10, 24));
            Assert.IsFalse(FraysRules.LucidOn(24, 25, 10, 24), "too few Great Snarls");
            Assert.IsFalse(FraysRules.LucidOn(40, 25, 30, 24), "not the final night yet");
            Assert.IsTrue(FraysRules.LucidOn(40, 25, 24, 24));
        }

        [TestMethod]
        public void GreatSnarl_LeavesAfter15Minutes_KeepingHalfItsBar()
        {
            Assert.AreEqual(15, FraysSettings.Number(FraysSettings.GreatTimeoutMinutes), 1e-9);
            Assert.AreEqual(0.5, FraysSettings.Number(FraysSettings.GreatLeaveKeep), 1e-9);
            Assert.AreEqual(0, FraysSettings.Number(FraysSettings.GreatLeaveHoldMinutes), 1e-9, "camps released at once");
            Assert.IsFalse(FraysRules.GreatTimedOut(1000, 15, 1000 + 14 * 60));
            Assert.IsTrue(FraysRules.GreatTimedOut(1000, 15, 1000 + 15 * 60));
            Assert.IsFalse(FraysRules.GreatTimedOut(1000, 0, 1e9), "0 = it never leaves");
            Assert.IsFalse(FraysRules.GreatTimedOut(0, 15, 1e9), "not spawned");
        }
    }
}

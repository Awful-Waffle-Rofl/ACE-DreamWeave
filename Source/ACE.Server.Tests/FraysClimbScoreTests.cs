using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The climb score, its weekly Vault Key tiers and the weekly score Epithets.</summary>
    [TestClass]
    public class FraysClimbScoreTests
    {
        private Func<string, string> previousSource;
        private Dictionary<string, string> values;

        [TestInitialize]
        public void Init()
        {
            previousSource = FraysSettings.StringSource;
            values = new Dictionary<string, string>();
            FraysSettings.StringSource = p => values.TryGetValue(p, out var v) ? v : null;
            FraysSettings.ResetCacheForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FraysSettings.StringSource = previousSource;
            FraysSettings.ResetCacheForTest();
        }

        [TestMethod]
        public void TimeBonus_FullForAnInstantStep_HalfAtPar_NoneAtTwicePar()
        {
            Assert.AreEqual(100, FraysScore.TimeBonus(new[] { 0.0 }, 100, 4), 1e-9);
            Assert.AreEqual(50, FraysScore.TimeBonus(new[] { 4.0 }, 100, 4), 1e-9);
            Assert.AreEqual(0, FraysScore.TimeBonus(new[] { 8.0, 20.0 }, 100, 4), 1e-9);
            Assert.AreEqual(0, FraysScore.TimeBonus(new[] { 1.0 }, 100, 0), 1e-9, "par 0 turns it off");
        }

        [TestMethod]
        public void StepMinutes_FromTheMinuteEachHeatWasReached()
        {
            var reached = new Dictionary<int, double> { [6] = 2, [7] = 5, [8] = 5, [9] = 11 };
            CollectionAssert.AreEqual(new List<double> { 2, 3, 0, 6 }, FraysScore.StepMinutes(reached, 5, 9));
            Assert.AreEqual(0, FraysScore.StepMinutes(reached, 9, 9).Count, "a start at the peak has no steps");
        }

        [TestMethod]
        public void ClimbScore_CountsEveryPart_AndTheOldRunScoreStillMatches()
        {
            // peak 20 (4000) + 300 kills + 4 Snarls (200) + 30 Gilded (150) + 1000 time - 2 deaths (50)
            Assert.AreEqual(4000 + 300 + 200 + 150 + 1000 - 50, FraysScore.ClimbScore(20, 300, 4, 30, 2, 1000, 10, 1, 50, 5, 25));
            Assert.AreEqual(4000 + 500 + 100 - 50, FraysScore.RunScore(20, 500, 2, 2));
            Assert.AreEqual(0, FraysScore.ClimbScore(0, 0, 0, 0, 50, 0, 10, 1, 50, 5, 25));
        }

        [TestMethod]
        public void Tiers_PayTwoFourSixKeys_OnceAWeek()
        {
            double b = FraysSettings.Number(FraysSettings.ScoreBronze), s = FraysSettings.Number(FraysSettings.ScoreSilver), g = FraysSettings.Number(FraysSettings.ScoreGold);
            Assert.IsTrue(b < s && s < g);
            Assert.AreEqual(0, FraysScore.ScoreTier((long)b - 1, b, s, g));
            Assert.AreEqual(1, FraysScore.ScoreTier((long)b, b, s, g));
            Assert.AreEqual(2, FraysScore.ScoreTier((long)s, b, s, g));
            Assert.AreEqual(3, FraysScore.ScoreTier((long)g + 5000, b, s, g));

            int bk = FraysSettings.Int(FraysSettings.ScoreBronzeKeys), sk = FraysSettings.Int(FraysSettings.ScoreSilverKeys), gk = FraysSettings.Int(FraysSettings.ScoreGoldKeys);
            Assert.AreEqual((2, 4, 6), (bk, sk, gk));
            Assert.AreEqual(2, FraysScore.KeysOwed(0, 1, bk, sk, gk));
            Assert.AreEqual(2, FraysScore.KeysOwed(1, 2, bk, sk, gk), "Silver after Bronze pays the difference");
            Assert.AreEqual(6, FraysScore.KeysOwed(0, 3, bk, sk, gk));
            Assert.AreEqual(0, FraysScore.KeysOwed(3, 3, bk, sk, gk), "paid once");
            Assert.AreEqual(0, FraysScore.KeysOwed(2, 1, bk, sk, gk));
        }

        [TestMethod]
        public void SizedTiers_SilverAtHeatTwenty_GoldNeedsAFastClimb()
        {
            // A middling camper (pace 500) reaches Heat 20 in about 1.1 hours with about 315 kills and
            // 4 Snarls; a slow one (pace 300) takes 2 hours. About 30 Gilded an hour.
            var par = FraysSettings.Number(FraysSettings.ScoreParMinutes);
            var pts = FraysSettings.Number(FraysSettings.ScoreTimePoints);
            long Score(double minutesAHeat, int kills, int snarls, int gilded) =>
                FraysScore.ClimbScore(20, kills, snarls, gilded, 0, FraysScore.TimeBonus(Enumerable.Repeat(minutesAHeat, 20), pts, par), 10, 1, 50, 5, 25);

            double b = FraysSettings.Number(FraysSettings.ScoreBronze), s = FraysSettings.Number(FraysSettings.ScoreSilver), g = FraysSettings.Number(FraysSettings.ScoreGold);
            Assert.AreEqual(2, FraysScore.ScoreTier(Score(6, 331, 5, 30), b, s, g), "slow to Heat 20: Silver");
            Assert.AreEqual(2, FraysScore.ScoreTier(Score(3.3, 315, 4, 30), b, s, g), "middling, one hour: Silver");
            Assert.AreEqual(3, FraysScore.ScoreTier(Score(2.25, 479, 7, 60), b, s, g), "a strong camper for 2 hours: Gold");
            Assert.AreEqual(1, FraysScore.ScoreTier(FraysScore.ClimbScore(13, 252, 3, 20, 0, FraysScore.TimeBonus(Enumerable.Repeat(4.6, 13), pts, par), 10, 1, 50, 5, 25), b, s, g), "Heat 13 in an hour: Bronze");
        }

        [TestMethod]
        public void ScoreTitles_MoveToTheNewTopThree()
        {
            var titles = FraysLongTerm.ScoreTitles();
            Assert.AreEqual(3, titles.Count);
            Assert.IsTrue(string.Join("", titles).All(ch => ch < 128));

            var rec = new FraysPlayerRecord();
            rec.Epithets.Add("the Inferno");
            Assert.IsTrue(FraysLongTerm.SetScoreTitle(rec, titles, 0));
            Assert.AreEqual(titles[0], rec.Epithet);

            Assert.IsTrue(FraysLongTerm.SetScoreTitle(rec, titles, 2), "moved from first to third");
            Assert.AreEqual(titles[2], rec.Epithet);
            Assert.IsFalse(rec.Epithets.Contains(titles[0]));

            Assert.IsTrue(FraysLongTerm.SetScoreTitle(rec, titles, -1), "off the board: the title goes");
            Assert.AreEqual("", rec.Epithet);
            CollectionAssert.AreEqual(new List<string> { "the Inferno" }, rec.Epithets);
            Assert.IsFalse(FraysLongTerm.SetScoreTitle(rec, titles, -1), "nothing to change");
        }
    }
}

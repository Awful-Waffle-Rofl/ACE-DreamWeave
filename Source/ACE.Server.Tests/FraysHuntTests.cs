using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Fray Huntmaster's daily hunts (FraysHunts).</summary>
    [TestClass]
    public class FraysHuntTests
    {
        [TestInitialize]
        public void Init() => FraysSettings.ResetCacheForTest();

        private static FraysFamily Family => FraysFamilies.Custom[0];

        private static string NameOf(uint wcid) => $"creature {wcid}";

        [TestMethod]
        public void Hunts_OffersAreTheSameAllDayAndPlayerTextIsAscii()
        {
            var a = FraysHunts.Offers(Family, 1000, NameOf);
            var b = FraysHunts.Offers(Family, 1000, NameOf);

            CollectionAssert.AreEqual(a.Select(h => h.Key).ToList(), b.Select(h => h.Key).ToList());
            Assert.AreEqual(FraysHunts.AnyKey, a.First().Key);
            Assert.AreEqual(FraysHunts.EliteKey, a.Last(h => h.Heat == 0).Key);
            CollectionAssert.AreEqual(new[] { 10, 15, 20 }, a.Where(h => h.Heat > 0).Select(h => h.Heat).ToArray(), "the Heat hunts come last");
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, a.Where(h => h.Heat > 0).Select(h => h.Keys).ToArray());
            Assert.AreEqual(a.Count, a.Select(h => h.Key).Distinct().Count(), "no hunt twice");
            Assert.IsTrue(a.All(h => h.Target > 0 && h.Text.All(c => c < 128)));
            Assert.AreEqual(0, FraysHunts.Offers(null, 1000, NameOf).Count, "no family, no hunts");
        }

        [TestMethod]
        public void Hunts_KillsCountOnlyForTheirOwnHunt()
        {
            var offers = FraysHunts.Offers(Family, 1000, NameOf);
            var any = offers.First(h => h.Key == FraysHunts.AnyKey);
            var elite = offers.First(h => h.Key == FraysHunts.EliteKey);
            var one = offers.First(h => h.Wcid != 0);

            Assert.AreEqual(0.4, FraysHunts.Counts(any, Family, Family.Swarm, 0.4, false), 1e-9, "any: by size");
            Assert.AreEqual(1, FraysHunts.Counts(elite, Family, Family.Elite, 2.5, true));
            Assert.AreEqual(0, FraysHunts.Counts(elite, Family, Family.Swarm, 0.4, false), "an elite hunt skips the rest");
            Assert.AreEqual(1, FraysHunts.Counts(one, Family, one.Wcid, 1, false));
            Assert.AreEqual(0, FraysHunts.Counts(one, Family, one.Wcid + 99999, 1, false), "another creature does not count");
            Assert.AreEqual(0, FraysHunts.Counts(any, Family, 1, 1, false), "a creature outside the family does not count");
        }

        [TestMethod]
        public void Hunts_ANewDayClearsEverything()
        {
            var rec = new FraysPlayerRecord();
            FraysHunts.Roll(rec, 5);
            rec.HuntTaken.Add("any");
            rec.HuntProgress["any"] = 10;
            rec.HuntDone.Add("elite");

            FraysHunts.Roll(rec, 5);
            Assert.AreEqual(1, rec.HuntTaken.Count, "the same day keeps them");

            FraysHunts.Roll(rec, 6);
            Assert.AreEqual(0, rec.HuntTaken.Count + rec.HuntProgress.Count + rec.HuntDone.Count);
            Assert.AreEqual(6, rec.HuntDay);
        }
    }
}

using ACE.Entity.Enum.Properties;
using ACE.Server.Knots;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    [TestClass]
    public class KnotClearRecordsTests
    {
        [TestMethod]
        public void IsBetter_NoRecordAlwaysLoses()
        {
            Assert.IsTrue(KnotClearRecords.IsBetter(1, 999999, null, null));
            Assert.IsTrue(KnotClearRecords.IsBetter(1, 999999, 0, null));
            Assert.IsTrue(KnotClearRecords.IsBetter(1, 999999, null, 5000));
        }

        [TestMethod]
        public void IsBetter_HigherLevelWins_EvenWhenSlower()
        {
            Assert.IsTrue(KnotClearRecords.IsBetter(313, 90000, 312, 1000));
        }

        [TestMethod]
        public void IsBetter_LowerLevelLoses_EvenWhenFaster()
        {
            Assert.IsFalse(KnotClearRecords.IsBetter(311, 100, 312, 90000));
        }

        [TestMethod]
        public void IsBetter_SameLevel_OnlyStrictlyFasterWins()
        {
            Assert.IsTrue(KnotClearRecords.IsBetter(312, 999, 312, 1000), "faster");
            Assert.IsFalse(KnotClearRecords.IsBetter(312, 1001, 312, 1000), "slower");
            Assert.IsFalse(KnotClearRecords.IsBetter(312, 1000, 312, 1000), "equal is not better");
        }

        [TestMethod]
        public void IsBetter_SameLevel_StoredWithoutUsableTime_IsBeaten()
        {
            Assert.IsTrue(KnotClearRecords.IsBetter(312, 5000, 312, null));
            Assert.IsTrue(KnotClearRecords.IsBetter(312, 5000, 312, 0));
        }

        [TestMethod]
        public void ToCentiseconds_RoundsAndNeverReturnsBelowOne()
        {
            Assert.AreEqual(27720L, KnotClearRecords.ToCentiseconds(277.2));
            Assert.AreEqual(1L, KnotClearRecords.ToCentiseconds(0.001), "finite sub-centisecond positive rounds up to 1");
            Assert.AreEqual(0L, KnotClearRecords.ToCentiseconds(-3));
            Assert.AreEqual(0L, KnotClearRecords.ToCentiseconds(double.NaN));
            Assert.AreEqual(0L, KnotClearRecords.ToCentiseconds(double.PositiveInfinity));
            Assert.AreEqual(0L, KnotClearRecords.ToCentiseconds(double.NegativeInfinity));
        }

        private static LeaderboardRankingTests.WaveBoardFakePlayer Stored(int? level, long? centi, string key)
        {
            var p = new LeaderboardRankingTests.WaveBoardFakePlayer("P", 1, 0, null);
            if (level.HasValue) p.IntProps[PropertyInt.BestKnotClearLevel] = level.Value;
            if (centi.HasValue) p.Int64Props[PropertyInt64.BestKnotClearTimeCenti] = centi.Value;
            if (key != null) p.StringProps[PropertyString.BestKnotClearPinnacleKey] = key;
            return p;
        }

        private static void AssertRecord(LeaderboardRankingTests.WaveBoardFakePlayer p, int level, long centi, string key)
        {
            Assert.AreEqual(level, p.GetProperty(PropertyInt.BestKnotClearLevel));
            Assert.AreEqual(centi, p.GetProperty(PropertyInt64.BestKnotClearTimeCenti));
            Assert.AreEqual(key, p.GetProperty(PropertyString.BestKnotClearPinnacleKey));
        }

        [TestMethod]
        public void Record_FirstClear_WritesAllThree()
        {
            var p = Stored(null, null, null);
            Assert.IsTrue(KnotClearRecords.Record(p, "grael", 312, 27720));
            AssertRecord(p, 312, 27720, "grael");
        }

        [TestMethod]
        public void Record_HigherLevel_OverwritesAllThree_EvenWhenSlower()
        {
            var p = Stored(300, 1000, "old");
            Assert.IsTrue(KnotClearRecords.Record(p, "grael", 312, 90000));
            AssertRecord(p, 312, 90000, "grael");
        }

        [TestMethod]
        public void Record_SlowerAtSameLevel_ChangesNothing()
        {
            var p = Stored(312, 1000, "old");
            Assert.IsFalse(KnotClearRecords.Record(p, "grael", 312, 2000));
            AssertRecord(p, 312, 1000, "old");
        }

        [TestMethod]
        public void Record_FasterAtSameLevel_OverwritesAllThree()
        {
            var p = Stored(312, 1000, "old");
            Assert.IsTrue(KnotClearRecords.Record(p, "grael", 312, 999));
            AssertRecord(p, 312, 999, "grael");
        }

        [TestMethod]
        public void Record_EmptyKey_RemovesTheString()
        {
            var p = Stored(300, 1000, "old");
            Assert.IsTrue(KnotClearRecords.Record(p, "", 312, 900));
            Assert.AreEqual(312, p.GetProperty(PropertyInt.BestKnotClearLevel));
            Assert.AreEqual(900L, p.GetProperty(PropertyInt64.BestKnotClearTimeCenti));
            Assert.IsNull(p.GetProperty(PropertyString.BestKnotClearPinnacleKey));
        }

        [TestMethod]
        public void Record_NonPositiveLevelOrTime_IsRefused()
        {
            var p = Stored(null, null, null);
            Assert.IsFalse(KnotClearRecords.Record(p, "grael", 0, 100));
            Assert.IsFalse(KnotClearRecords.Record(p, "grael", -1, 100));
            Assert.IsFalse(KnotClearRecords.Record(p, "grael", 312, 0));
            Assert.IsFalse(KnotClearRecords.Record(p, "grael", 312, -5));
            Assert.IsNull(p.GetProperty(PropertyInt.BestKnotClearLevel));
            Assert.IsNull(p.GetProperty(PropertyInt64.BestKnotClearTimeCenti));
            Assert.IsNull(p.GetProperty(PropertyString.BestKnotClearPinnacleKey));
        }

        [TestMethod]
        public void FormatDetail_UsesTheNameSeamAndTheWaveTimeFormat()
        {
            Assert.AreEqual(" - Grael - 4:37.20", KnotClearRecords.FormatDetail("grael", 27720, k => "Grael"));
            Assert.IsNull(KnotClearRecords.FormatDetail("grael", 0, k => "Grael"));
            Assert.IsNull(KnotClearRecords.FormatDetail("grael", null, k => "Grael"));
        }

        [TestMethod]
        public void KnotName_FallsBackToKeyThenKnot_WhenNoStoreIsLoaded()
        {
            Assert.AreEqual("zz_unknown_knot", KnotClearRecords.KnotName("zz_unknown_knot"));
            Assert.AreEqual("Knot", KnotClearRecords.KnotName(null));
        }
    }
}
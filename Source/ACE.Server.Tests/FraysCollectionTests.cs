using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The boon collection: ranks raised by Sigils (FraysCollection, FraysBoonDef).</summary>
    [TestClass]
    public class FraysCollectionTests
    {
        [TestMethod]
        public void Collection_RanksGrowByHalfTheFirstValue()
        {
            var b = FraysBoons.Find("wildfire");
            Assert.AreEqual(0.02, b.ValueAt(1), 1e-9);   // sized so rank 4 is the +5% budget
            Assert.AreEqual(0.03, b.ValueAt(2), 1e-9);
            Assert.AreEqual(0.05, b.ValueAt(4), 1e-9);
            Assert.AreEqual(0.05, b.ValueAt(9), 1e-9, "never past the top rank");
            Assert.AreEqual("+5% damage to pack creatures; but 2% less damage to Gilded creatures", b.TextAt(4), "the cost does not grow with rank");

            var m = new FraysCampMods();
            var before = m.BoonPack;
            FraysBoons.Apply(new[] { "wildfire" }, m, k => 3);
            Assert.AreEqual(before + 0.04, m.BoonPack, 1e-9);
        }

        [TestMethod]
        public void Collection_RaresStopAtTwoAndOnOffBoonsHaveNoRanks()
        {
            Assert.IsTrue(FraysBoons.All.Where(b => b.Rare).All(b => b.MaxRank <= 2));
            Assert.AreEqual(1, FraysBoons.Find("undyingflame").MaxRank);
            Assert.AreEqual(1, FraysBoons.Find("phoenixcoal").MaxRank);
            Assert.IsTrue(FraysBoons.All.All(b => b.TextAt(1).All(c => c < 128) && !b.TextAt(1).Contains("{")));
        }

        [TestMethod]
        public void Collection_SigilsNeedACollectedBoonAndStopAtTheTop()
        {
            var rec = new FraysPlayerRecord();
            var b = FraysBoons.Find("coalpurse");

            Assert.IsNotNull(FraysCollection.RankUp(rec, b), "not collected yet");
            FraysCodex.Note(rec, FraysCodex.Boon + b.Key);

            Assert.AreEqual(FraysCollection.LesserSigil, FraysCollection.SigilFor(b, 1));
            Assert.IsNull(FraysCollection.RankUp(rec, b));
            Assert.AreEqual(FraysCollection.GreaterSigil, FraysCollection.SigilFor(b, 2));
            Assert.IsNull(FraysCollection.RankUp(rec, b));
            Assert.IsNull(FraysCollection.RankUp(rec, b));
            Assert.AreEqual(4, FraysCollection.Rank(rec, b.Key));
            Assert.AreEqual(0u, FraysCollection.SigilFor(b, 4));
            Assert.IsNotNull(FraysCollection.RankUp(rec, b), "top rank");

            Assert.AreEqual("snarlsbane", FraysCollection.Match("Snarl's Bane").Key);
            Assert.AreEqual("wildfire", FraysCollection.Match("wild").Key);
        }
    }
}

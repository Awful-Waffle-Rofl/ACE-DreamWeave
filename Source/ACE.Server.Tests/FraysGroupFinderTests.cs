using System.Collections.Generic;
using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The boon vote, the group finder and the group run board.</summary>
    [TestClass]
    public class FraysGroupFinderTests
    {
        [TestMethod]
        public void Vote_MostVotesWinATieOrNoneGoesToThePlan()
        {
            Assert.AreEqual(2, FraysFire.Tally(new[] { 2, 2, 1 }, 3));
            Assert.AreEqual(0, FraysFire.Tally(new[] { 1, 2 }, 3), "a tie");
            Assert.AreEqual(0, FraysFire.Tally(new int[0], 3), "no votes");
            Assert.AreEqual(3, FraysFire.Tally(new[] { 3, 9, 9 }, 3), "a number off the offer does not count");
        }

        [TestMethod]
        public void Lfg_AMatchNeedsEnoughWaitingAndYes()
        {
            Assert.AreEqual(0, FraysLfg.PickMatch(new List<uint> { 1, 2 }, 3, 9).Count);
            CollectionAssert.AreEqual(new List<uint> { 1, 2, 3 }, FraysLfg.PickMatch(new List<uint> { 1, 2, 3 }, 3, 9));
            Assert.AreEqual(9, FraysLfg.PickMatch(Enumerable.Range(1, 12).Select(i => (uint)i).ToList(), 3, 9).Count, "a fellowship holds 9");
            Assert.IsFalse(FraysLfg.Forms(2, 3));
            Assert.IsTrue(FraysLfg.Forms(3, 3));
        }

        [TestMethod]
        public void GroupRuns_OneEntryPerRunNamingEveryMember()
        {
            var runs = new List<FraysGroupRuns.Run>();
            runs = FraysGroupRuns.Add(runs, "vs1:100", "Sky", "Tester", 12, 5000, 7);
            runs = FraysGroupRuns.Add(runs, "vs1:100", "Sky", "Bob", 12, 5200, 7);
            runs = FraysGroupRuns.Add(runs, "vs1:100", "Sky", "Ann", 11, 4000, 7);
            runs = FraysGroupRuns.Add(runs, "vs2:200", "Sky", "Zed", 5, 900, 6);

            Assert.AreEqual(2, runs.Count);
            CollectionAssert.AreEqual(new[] { "Tester", "Bob", "Ann" }, runs[0].Names);
            Assert.AreEqual(5200, runs[0].Score);

            var text = FraysGroupRuns.Text(runs, true, 7);
            StringAssert.Contains(text, "Tester, Bob, Ann - Heat 12");
            Assert.IsFalse(text.Contains("Zed"), "last week's run is not on this week's board");
            Assert.IsTrue(text.All(c => c < 128));
        }
    }
}

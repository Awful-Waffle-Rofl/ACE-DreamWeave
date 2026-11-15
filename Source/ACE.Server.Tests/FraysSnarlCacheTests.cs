using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Snarl cache prizes and the Snarl Trophy Wall.</summary>
    [TestClass]
    public class FraysSnarlCacheTests
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
        public void CachePrizes_EachHaveTheirOwnSetting()
        {
            Assert.AreEqual(0.01, FraysSettings.Number(FraysSettings.SnarlTrophyChance), 1e-9, "the Snarl Trophy at 1%");
            Assert.AreEqual(0, FraysSettings.Number(FraysSettings.SnarlAugChance), 1e-9, "the augmentation gem and stipend items are not in the Snarl cache");
            Assert.AreEqual(0.05, FraysSettings.Number(FraysSettings.GreatSnarlAugChance), 1e-9, "the Great Snarl: 5% for each player it pays");

            foreach (var k in new[] { FraysSettings.SnarlScarabChance, FraysSettings.SnarlMmdChance, FraysSettings.SnarlEmberChance, FraysSettings.SnarlShardChance })
                Assert.IsTrue(FraysSettings.Number(k) > 0 && FraysSettings.Number(k) <= 1, k);

            Assert.IsTrue(FraysSettings.Int(FraysSettings.SnarlMmdCount) >= 1);
            Assert.IsTrue(FraysSettings.Int(FraysSettings.SnarlEmberCount) >= 1);
            CollectionAssert.AreEqual(new List<uint> { 44105, 46423 }, FraysLongTerm.ParseWcids(FraysSettings.Text(FraysSettings.SnarlAugWcids)));
            CollectionAssert.AreEqual(new List<uint> { 7, 9 }, FraysLongTerm.ParseWcids(" 7, x,,9 "));
            Assert.AreEqual(10, FraysLongTerm.ShardsPerSigil);
        }

        [TestMethod]
        public void TrophyWall_KeepsTheNewestFirst_AndShowsTheLatestN()
        {
            var list = new List<FraysTrophyWall.Entry>();

            for (var i = 0; i < 60; i++)
                list.Insert(0, new FraysTrophyWall.Entry { Date = new DateTime(2026, 11, 1).AddDays(i % 9), Finder = "P" + i, Family = "Olthoi" });

            var text = FraysTrophyWall.Format(list);
            var back = FraysTrophyWall.Parse(text);
            Assert.AreEqual(FraysTrophyWall.Kept, back.Count);
            Assert.AreEqual("P59", back[0].Finder);

            var pages = FraysTrophyWall.Pages(back, 10);
            var all = string.Join("\n", pages);
            Assert.IsTrue(all.Contains(" - P59, Olthoi Snarl"), all);
            Assert.IsFalse(all.Contains("P49,"), "only the latest 10");
            Assert.IsTrue(pages.All(p => p.Length <= 1000));
            Assert.IsTrue(all.All(ch => ch < 128), "ASCII only in player text");

            Assert.IsTrue(FraysTrophyWall.Pages(new List<FraysTrophyWall.Entry>(), 10)[0].Contains("No Snarl Trophy"));
            Assert.AreEqual(1, FraysTrophyWall.Parse("2026-11-09|A|B;bad;2026-13-01|x|y").Count, "bad entries are skipped");
            Assert.AreEqual("A B", FraysTrophyWall.Parse(FraysTrophyWall.Format(new[] { new FraysTrophyWall.Entry { Date = DateTime.UtcNow, Finder = "A|B", Family = "C;D" } }))[0].Finder);
        }
    }
}

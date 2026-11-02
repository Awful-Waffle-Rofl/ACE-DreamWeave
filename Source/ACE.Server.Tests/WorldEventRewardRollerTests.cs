using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventRewardRoller"/> with seeded Randoms.
    /// </summary>
    [TestClass]
    public class WorldEventRewardRollerTests
    {
        private static RewardPoolEntryDef Stack(uint wcid, int qty, double w = 1)
            => new RewardPoolEntryDef { Wcid = wcid, Qty = qty, Weight = w, Scale = "stack" };

        private static RewardPoolEntryDef Chance(uint wcid, int qty, double w = 1)
            => new RewardPoolEntryDef { Wcid = wcid, Qty = qty, Weight = w, Scale = "chance" };

        private static RewardPoolEntryDef Bags(uint hammer, uint bag, double w = 1, int perUnit = 10)
            => new RewardPoolEntryDef { Wcid = hammer, Qty = 1, Weight = w, Scale = "bags", BagWcid = bag, BagsPerUnit = perUnit };

        /// <summary>The live pool later content will carry (orchestrator-supplied fixture).</summary>
        private static List<RewardPoolEntryDef> LivePool() => new List<RewardPoolEntryDef>
        {
            Bags(1001910, 1001755, 6), Bags(1001911, 1001756, 6), Bags(1001912, 1001752, 6),
            Bags(1001916, 1001759, 6), Bags(1001918, 1001751, 6),
            Chance(6331, 1, 18), Stack(7299, 10, 15), Stack(43901, 25, 14), Stack(20630, 25, 15),
            Chance(29295, 1, 4), Chance(46423, 1, 4),
        };

        private static int Total(List<(uint Wcid, int Qty)> r) => r.Sum(x => x.Qty);

        [TestMethod]
        public void NonPositiveMultiplier_OrEmptyPool_RollsNothing()
        {
            var rng = new Random(1);

            Assert.AreEqual(0, WorldEventRewardRoller.Roll(LivePool(), 0, rng).Count);
            Assert.AreEqual(0, WorldEventRewardRoller.Roll(LivePool(), -1, rng).Count);
            Assert.AreEqual(0, WorldEventRewardRoller.Roll(LivePool(), double.NaN, rng).Count);
            Assert.AreEqual(0, WorldEventRewardRoller.Roll(new List<RewardPoolEntryDef>(), 1, rng).Count);
            Assert.AreEqual(0, WorldEventRewardRoller.Roll(null, 1, rng).Count);
        }

        [TestMethod]
        public void Boundaries_AlwaysPayAtLeastSomething_AndFullRollsPayFullQty()
        {
            var pool = new List<RewardPoolEntryDef> { Stack(100, 10) };

            foreach (var m in new[] { 0.02, 0.99 })
                for (var seed = 0; seed < 300; seed++)
                {
                    var r = WorldEventRewardRoller.Roll(pool, m, new Random(seed));
                    Assert.IsTrue(Total(r) >= 1, $"m={m} seed={seed} must never pay zero");
                    Assert.IsTrue(Total(r) <= 10);
                }

            Assert.AreEqual(10, Total(WorldEventRewardRoller.Roll(pool, 1.0, new Random(5))));
            Assert.AreEqual(20, Total(WorldEventRewardRoller.Roll(pool, 2.0, new Random(5))), "two full rolls, merged");
            Assert.AreEqual(1, WorldEventRewardRoller.Roll(pool, 2.0, new Random(5)).Count, "same wcid merges into one row");

            var oneAndAHalf = Total(WorldEventRewardRoller.Roll(pool, 1.5, new Random(5)));
            Assert.AreEqual(15, oneAndAHalf, "10 full + 5 (10 * 0.5, exact so no rounding noise)");
        }

        [TestMethod]
        public void LivePool_FullRolls_GiveWholeHammersOrFullQty()
        {
            var hammers = new HashSet<uint> { 1001910, 1001911, 1001912, 1001916, 1001918 };

            for (var seed = 0; seed < 500; seed++)
            {
                var r = WorldEventRewardRoller.Roll(LivePool(), 1.0, new Random(seed));

                Assert.AreEqual(1, r.Count);
                var (wcid, qty) = r[0];

                if (hammers.Contains(wcid))
                    Assert.AreEqual(1, qty);
                else
                    Assert.IsTrue(new[] { (6331u, 1), (7299u, 10), (43901u, 25), (20630u, 25), (29295u, 1), (46423u, 1) }.Contains((wcid, qty)));
            }
        }

        [TestMethod]
        public void StackEntry_HalfMultiplier_MeanMatchesAnalyticExpectation()
        {
            var pool = new List<RewardPoolEntryDef> { Stack(100, 7) };   // 7 * 0.5 = 3.5: stochastic rounding matters
            var rng = new Random(2026);
            long sum = 0;
            const int n = 100000;

            for (var i = 0; i < n; i++)
                sum += Total(WorldEventRewardRoller.Roll(pool, 0.5, rng));

            var mean = sum / (double)n;
            Assert.AreEqual(3.5, mean, 3.5 * 0.01, "mean within 1% of qty * f");
        }

        [TestMethod]
        public void Bags_RoundingToTen_GivesTheHammer()
        {
            var pool = new List<RewardPoolEntryDef> { Bags(1001910, 1001755) };

            // f = 1.0 - epsilon-ish: 9.9 bags rounds to 10 about 90% of the time -> Hammer
            var hammer = 0;
            var bags = 0;

            for (var seed = 0; seed < 2000; seed++)
            {
                var r = WorldEventRewardRoller.Roll(pool, 0.99, new Random(seed));

                Assert.AreEqual(1, r.Count);

                if (r[0].Wcid == 1001910) { hammer++; Assert.AreEqual(1, r[0].Qty); }
                else { bags++; Assert.AreEqual(1001755u, r[0].Wcid); Assert.AreEqual(9, r[0].Qty); }
            }

            Assert.IsTrue(hammer > 1500 && bags > 50, $"hammer={hammer} bags={bags}");
        }

        [TestMethod]
        public void Bags_Half_PaysFiveBags()
        {
            var r = WorldEventRewardRoller.Roll(new List<RewardPoolEntryDef> { Bags(1001910, 1001755) }, 0.5, new Random(9));

            CollectionAssert.AreEqual(new[] { (1001755u, 5) }, r.ToArray());
        }

        [TestMethod]
        public void LoneFractionalRoll_RoundingToZero_PaysOneUnit()
        {
            // 0.02 * 10 bags = 0.2 -> usually 0, minimum rule makes it 1 bag
            for (var seed = 0; seed < 500; seed++)
            {
                var b = WorldEventRewardRoller.Roll(new List<RewardPoolEntryDef> { Bags(1001910, 1001755) }, 0.02, new Random(seed));
                Assert.AreEqual(1, b.Count);
                Assert.AreEqual(1001755u, b[0].Wcid);
                Assert.IsTrue(b[0].Qty >= 1);

                var s = WorldEventRewardRoller.Roll(new List<RewardPoolEntryDef> { Stack(100, 10) }, 0.02, new Random(seed));
                Assert.IsTrue(Total(s) >= 1);
            }
        }

        [TestMethod]
        public void ChanceMiss_RedrawsOnlyNonChanceEntries()
        {
            // The chance entry dominates the weights; every miss must land on the stack entry instead.
            var pool = new List<RewardPoolEntryDef> { Chance(1, 1, 1000), Chance(2, 1, 1000), Stack(3, 10, 1) };
            var chanceHits = 0;
            var stackRolls = 0;

            for (var seed = 0; seed < 4000; seed++)
            {
                var r = WorldEventRewardRoller.Roll(pool, 0.3, new Random(seed));

                Assert.IsTrue(r.Count >= 1, "lone fractional roll never pays zero");

                foreach (var (wcid, qty) in r)
                {
                    if (wcid == 3) { stackRolls++; Assert.IsTrue(qty >= 1 && qty <= 10); }
                    else { chanceHits++; Assert.AreEqual(1, qty, "a chance entry is never scaled"); }
                }
            }

            // ~30% of picks hit; the rest redraw onto the stack entry
            Assert.IsTrue(chanceHits > 1000 && chanceHits < 1400, $"chanceHits={chanceHits}");
            Assert.IsTrue(stackRolls > 2600, $"stackRolls={stackRolls}");
        }

        [TestMethod]
        public void ChanceMiss_WithNoOtherEntries_StillPaysOnLoneRoll()
        {
            var pool = new List<RewardPoolEntryDef> { Chance(1, 2, 1) };

            for (var seed = 0; seed < 200; seed++)
                CollectionAssert.AreEqual(new[] { (1u, 2) }, WorldEventRewardRoller.Roll(pool, 0.1, new Random(seed)).ToArray());
        }

        [TestMethod]
        public void SameSeed_SameResult()
        {
            var a = WorldEventRewardRoller.Roll(LivePool(), 1.7, new Random(77));
            var b = WorldEventRewardRoller.Roll(LivePool(), 1.7, new Random(77));

            CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
        }
    }
}
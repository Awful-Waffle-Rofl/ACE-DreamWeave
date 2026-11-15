using System.Collections.Generic;
using System.Linq;

using ACE.Server.Frays;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Campfire perks, daily Heat chests and Heat pay on the Threads XP ladder.</summary>
    [TestClass]
    public class FraysPerksTests
    {
        private static FraysPlayerRecord Rec(int tier) => new FraysPlayerRecord { FireTier = tier };

        [TestMethod]
        public void Spend_TakesOnePointATier_UpToTheMaxRank()
        {
            var rec = Rec(2);
            StringAssert.Contains(FraysPerks.Spend(rec, "bellows"), "Bellows rank 1");
            StringAssert.Contains(FraysPerks.Spend(rec, "bellows"), "highest rank");
            StringAssert.Contains(FraysPerks.Spend(rec, "sear"), "Searing rank 1");
            StringAssert.Contains(FraysPerks.Spend(rec, "searing"), "No perk points left");
            Assert.AreEqual(2, FraysPerks.Spent(rec));
            StringAssert.Contains(FraysPerks.Spend(rec, "nonsense"), "No perk called");
        }

        [TestMethod]
        public void Reset_IsFreeOnceAWeek()
        {
            var rec = Rec(3);
            FraysPerks.Spend(rec, "higherflame");
            StringAssert.Contains(FraysPerks.Reset(rec, 100), "reset");
            Assert.AreEqual(0, FraysPerks.Spent(rec));

            FraysPerks.Spend(rec, "higherflame");
            StringAssert.Contains(FraysPerks.Reset(rec, 100), "used this week");
            Assert.AreEqual(1, FraysPerks.Spent(rec));
            StringAssert.Contains(FraysPerks.Reset(rec, 101), "reset");
        }

        [TestMethod]
        public void Apply_PutsEachPerkOnTheModifiers()
        {
            var rec = Rec(10);
            foreach (var k in new[] { "emberhearth", "emberhearth", "snarllure", "bellows", "searing", "ashguard", "ashguard", "higherflame", "higherflame" })
                FraysPerks.Spend(rec, k);

            var m = new FraysCampMods();
            FraysPerks.Apply(rec, m);

            Assert.AreEqual(1.10, m.Embers, 1e-9);
            Assert.AreEqual(1.05, m.SnarlBarSpeed, 1e-9);   // Snarl Lure: 5% a rank
            Assert.AreEqual(0, m.PackDelta, "Bellows is not in the pack size or the rest scaling");
            Assert.AreEqual(0.10, m.PackShare, 1e-9);
            Assert.AreEqual(0.02, m.BonusDamage, 1e-9);   // 2% a rank, in the pools
            Assert.AreEqual(0.04, m.BonusReduction, 1e-9);
            Assert.AreEqual(2, FraysPerks.CapBonus(rec));
            Assert.AreEqual(1.0, m.HeatGain, 1e-9, "no perk makes Heat come faster");
        }

        [TestMethod]
        public void PerkText_IsAscii()
        {
            var text = FraysPerks.Text(Rec(4), 1) + string.Join("", FraysPerks.All.Select(p => p.Text(p.MaxRank)));
            Assert.IsTrue(text.All(ch => ch < 128), "ASCII only in player text");
        }

        [TestMethod]
        public void HeatHunts_DoneWhenTheClimbCrossesThem()
        {
            var taken = new[] { 10, 15, 20 }.Select((h, i) => new FraysHunts.HuntDef { Key = $"heat{h}", Heat = h, Keys = i + 1, Target = 1 }).ToList();
            CollectionAssert.AreEqual(new[] { 10 }, FraysHunts.HeatDone(taken, 9, 10).Select(h => h.Heat).ToArray());
            CollectionAssert.AreEqual(new[] { 10, 15, 20 }, FraysHunts.HeatDone(taken, 0, 20).Select(h => h.Heat).ToArray());
            Assert.AreEqual(0, FraysHunts.HeatDone(taken, 10, 14).Count, "started at 10: not crossed");
            Assert.AreEqual(0, FraysHunts.HeatDone(taken.Skip(1), 9, 10).Count, "only hunts taken count");
        }

        [TestMethod]
        public void HeatMilestone_EveryFifthLevel()
        {
            Assert.IsTrue(FraysRules.HeatMilestone(4, 5));
            Assert.IsTrue(FraysRules.HeatMilestone(8, 11), "a jump over 10");
            Assert.IsFalse(FraysRules.HeatMilestone(5, 6));
            Assert.IsFalse(FraysRules.HeatMilestone(0, 3));
            Assert.IsFalse(FraysRules.HeatMilestone(10, 9), "a drop is no milestone");
        }

        [TestMethod]
        public void MapCoords_RoundLikeTheClient()
        {
            // Shell Strand's fire: 78.35N, 60.75E reads 78.4N, 60.8E in the client (the old text said 78.3N, 60.7E).
            Assert.AreEqual("78.4N, 60.8E", FraysRules.MapCoords(78.35, 60.75));
            Assert.AreEqual("12.0S, 3.1W", FraysRules.MapCoords(-12.04, -3.06));

            // From global metres with the client's 101.95 offset. Foam Steps' fire (0xCFDF, 108, 60) is 76.7N, 64.1E.
            var (ns, ew) = FraysRules.MapFromGlobal(0xCF * 192 + 108, 0xDF * 192 + 60);
            Assert.AreEqual("76.7N, 64.1E", FraysRules.MapCoords(ns, ew));
            var (ns2, ew2) = FraysRules.MapFromGlobal((60.75 + 102) * 240, (78.35 + 102) * 240);
            Assert.AreEqual("78.4N, 60.8E", FraysRules.MapCoords(ns2, ew2), "Shell Strand still reads as the client shows it");
        }

        [TestMethod]
        public void Compass_NamesTheWay()
        {
            Assert.AreEqual("north", FraysRules.Compass(0, 10));
            Assert.AreEqual("east", FraysRules.Compass(10, 0));
            Assert.AreEqual("south-west", FraysRules.Compass(-10, -10));
            Assert.AreEqual("north-west", FraysRules.Compass(-10, 9));
        }

        [TestMethod]
        public void WeekShift_StableAndKeepsTheMix()
        {
            Assert.AreEqual(FraysRules.WeekShift("vc2", 7), FraysRules.WeekShift("vc2", 7), "the same all week, every start");
            Assert.IsTrue(Enumerable.Range(0, 50).All(w => FraysRules.WeekShift("vc2", w) is >= 0 and < 30));
            Assert.IsTrue(Enumerable.Range(0, 10).Select(w => FraysRules.WeekShift("vc2", w)).Distinct().Count() > 3, "it moves week to week");

            // The layout turns, but the zone's kinds keep about the same mix.
            int Nests(int shift) => Enumerable.Range(0, 20).Count(i => FraysRules.PointKind(i + shift) == FraysPointKind.Nest);
            Assert.IsTrue(Enumerable.Range(0, 30).All(s => Nests(s) == 4));
        }

        [TestMethod]
        public void OrphanedPoints_FreesWaitingPointsWithNothingAlive()
        {
            var ready = new Dictionary<int, double> { [0] = double.MaxValue, [1] = double.MaxValue, [2] = 50, [3] = double.MaxValue };
            CollectionAssert.AreEquivalent(new List<int> { 0, 3 }, FraysRules.OrphanedPoints(ready, new[] { 1, 1, 2 }));
            Assert.AreEqual(0, FraysRules.OrphanedPoints(ready, new[] { 0, 1, 3 }).Count);
        }

        [TestMethod]
        public void LadderRatio_FollowsThreads_NeverBelowOne()
        {
            var ladder = new List<XpLadderRungDef>
            {
                new XpLadderRungDef { Level = 300, Xp = 4_000_000 },
                new XpLadderRungDef { Level = 325, Xp = 5_000_000 },
                new XpLadderRungDef { Level = 350, Xp = 6_000_000 },
                new XpLadderRungDef { Level = 375, Xp = 7_350_000 },
            };

            Assert.AreEqual(1.25, FraysCurve.LadderRatio(ladder, 300, 325).Value, 1e-9);
            Assert.AreEqual(1.77, FraysCurve.LadderRatio(ladder, 300, 370).Value, 0.01);
            Assert.AreEqual(1.0, FraysCurve.LadderRatio(ladder, 325, 300).Value, 1e-9);
            Assert.IsNull(FraysCurve.LadderRatio(new List<XpLadderRungDef>(), 300, 325));
        }

        [TestMethod]
        public void SnarlSpeed_EverySourceSharesTheCap()
        {
            Assert.AreEqual(0.5, FraysSettings.Number(FraysSettings.SnarlSpeedCap), 1e-9);
            var bane = new FraysCampMods();
            bane.SnarlBarSpeed *= 1.35;   // Snarl's Bane alone
            Assert.AreEqual(1.35, bane.SnarlSpeed, 1e-9, "Snarl's Bane's +35% counts in full");
            var m = new FraysCampMods();
            m.SnarlBarSpeed *= 1.15;   // Snarl Lure r3
            m.SnarlBarSpeed *= 1.15 * 1.15 * 1.15 * 1.15;   // Snarl Bait four times
            m.SnarlBarSpeed *= 1.35;   // Snarl's Bane
            Assert.AreEqual(1.5, m.SnarlSpeed, 1e-9, "all together, at most +50%");
            Assert.AreEqual(1.10, FraysRules.CapSnarlSpeed(1.10, 0.25), 1e-9, "under the cap: as is");
            Assert.AreEqual(3.0, FraysRules.CapSnarlSpeed(3.0, -1), 1e-9, "below 0: no cap");
        }

        [TestMethod]
        public void RichCoals_IsGone_AndItsPointComesBack()
        {
            Assert.IsNull(FraysPerks.All.FirstOrDefault(p => p.Key == "richcoals"));
            Assert.IsFalse(FraysPerks.Presets.Values.Any(v => v.Contains("richcoals")), "no preset spends on it");

            var rec = Rec(2);
            rec.Perks["richcoals"] = 1;
            FraysPerks.Spend(rec, "searing");
            Assert.AreEqual(1, FraysPerks.Unspent(rec), "the Rich Coals point is free to spend again");

            FraysPerks.Apply(rec, new FraysCampMods());
            Assert.IsFalse(rec.Perks.ContainsKey("richcoals"), "Apply drops the old rank");
            Assert.AreEqual(1, FraysPerks.Rank(rec, "searing"));
            Assert.IsTrue(FraysPerks.Text(rec, 1).All(ch => ch < 128));
            Assert.IsFalse(FraysPerks.Text(rec, 1).Contains("Rich Coals"));
        }

        [TestMethod]
        public void Bellows_IsAboutTenPercentMoreCreatures_AtAnyPackSize()
        {
            foreach (var n in new[] { 1, 3, 5, 7, 9 })
            {
                var added = Enumerable.Range(0, 10000).Sum(i => FraysRules.ShareExtra(n, 0.10, (i + 0.5) / 10000));
                Assert.AreEqual(0.10, added / 10000.0 / n, 1e-3, $"pack of {n}");
            }

            Assert.AreEqual(0, FraysRules.ShareExtra(5, 0, 0), "no Bellows, no extra");
            Assert.AreEqual(1, FraysRules.ShareExtra(12, 0.10, 0.999), "at most one, chance capped at 1");
            StringAssert.Contains(FraysPerks.Find("bellows").Text(1), "10% more creatures an hour");
        }
    }
}

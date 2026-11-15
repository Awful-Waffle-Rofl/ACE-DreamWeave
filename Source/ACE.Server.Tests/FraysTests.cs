using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Frays prototype: Heat math, limit resolution, rating, leash zone, ownership rules, settings, defs.</summary>
    [TestClass]
    public class FraysTests
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

        // ---- Heat ----

        [TestMethod]
        public void PointsToNext_Is20Plus5N()
        {
            Assert.AreEqual(20, FraysRules.PointsToNext(0));
            Assert.AreEqual(25, FraysRules.PointsToNext(1));
            Assert.AreEqual(70, FraysRules.PointsToNext(10));
            Assert.AreEqual(115, FraysRules.PointsToNext(19));
        }

        [TestMethod]
        public void AddPoints_ClimbsAndCarries()
        {
            Assert.AreEqual((0, 19), FraysRules.AddPoints(0, 0, 19, 20));
            Assert.AreEqual((1, 0), FraysRules.AddPoints(0, 19, 1, 20));

            // 20 to level 1, 25 to level 2, 5 left over.
            Assert.AreEqual((2, 5), FraysRules.AddPoints(0, 0, 50, 20));
        }

        [TestMethod]
        public void AddPoints_StopsAtTheLimit()
        {
            // Limit 1: progress stops one short of level 2.
            Assert.AreEqual((1, 24), FraysRules.AddPoints(1, 20, 100, 1));
            Assert.AreEqual((0, 19), FraysRules.AddPoints(0, 0, 100, 0));
        }

        [TestMethod]
        public void AddPoints_DecayDropsLevelsAndFloorsAtZero()
        {
            Assert.AreEqual((0, 19), FraysRules.AddPoints(1, 0, -1, 20));
            Assert.AreEqual((0, 0), FraysRules.AddPoints(0, 3, -10, 20));
            Assert.AreEqual((5, 4), FraysRules.AddPoints(5, 4, 0, 20));
            Assert.AreEqual((4, 39), FraysRules.AddPoints(5, 4, -5, 20));
        }

        [TestMethod]
        public void DeathDrop_TwoLevels()
        {
            Assert.AreEqual((8, 10), FraysRules.DeathDrop(10, 10, 2));
            Assert.AreEqual((0, 0), FraysRules.DeathDrop(1, 0, 2));

            // Progress is clamped into the lower level.
            Assert.AreEqual((0, 19), FraysRules.DeathDrop(2, 24, 2));
        }

        [TestMethod]
        public void SpawnInterval_MatchesTheDocTable()
        {
            Assert.AreEqual(8.0, FraysRules.SpawnIntervalSeconds(0), 1e-9);
            Assert.AreEqual(5.3, FraysRules.SpawnIntervalSeconds(10), 0.05);
            Assert.AreEqual(3.5, FraysRules.SpawnIntervalSeconds(20), 0.05);
            Assert.AreEqual(2.0, FraysRules.SpawnIntervalSeconds(50, 8, 0.04, 2), 1e-9);
        }

        [TestMethod]
        public void PackSize_OnePlusOneEveryFive()
        {
            Assert.AreEqual(1, FraysRules.PackSize(0));
            Assert.AreEqual(1, FraysRules.PackSize(4));
            Assert.AreEqual(2, FraysRules.PackSize(5));
            Assert.AreEqual(3, FraysRules.PackSize(10));
            Assert.AreEqual(5, FraysRules.PackSize(20));
            Assert.AreEqual(1, FraysRules.PackSize(20, 0));
        }

        [TestMethod]
        public void LevelMult_PerLevelPercent()
        {
            Assert.AreEqual(1.4, FraysRules.LevelMult(10, 0.04), 1e-9);
            Assert.AreEqual(1.8, FraysRules.LevelMult(20, 0.04), 1e-9);
            Assert.AreEqual(2.0, FraysRules.LevelMult(20, 0.05), 1e-9);
        }

        [TestMethod]
        public void ActiveSpawnPoints_FollowsFellowshipSize()
        {
            Assert.AreEqual(2, FraysRules.ActiveSpawnPoints(1, 6));
            Assert.AreEqual(3, FraysRules.ActiveSpawnPoints(3, 6));
            Assert.AreEqual(4, FraysRules.ActiveSpawnPoints(5, 6));
            Assert.AreEqual(6, FraysRules.ActiveSpawnPoints(10, 6));
            Assert.AreEqual(6, FraysRules.ActiveSpawnPoints(20, 6));
            Assert.AreEqual(3, FraysRules.ActiveSpawnPoints(5, 3));
        }

        // ---- limit ----

        private static FraysMemberSetting M(string name, FraysMode mode, int limit = 0, int rating = 0) => new FraysMemberSetting(name, mode, limit, rating);

        [TestMethod]
        public void Limit_ClimbIsTheCap()
        {
            var l = FraysRules.ResolveLimit(new[] { M("a", FraysMode.Climb), M("b", FraysMode.Climb) }, 3, 20);
            Assert.AreEqual(20, l.Level);
            Assert.IsFalse(l.Frozen);
            Assert.AreEqual("cap", l.SetBy);
        }

        [TestMethod]
        public void Limit_LowestAmongMembersWins()
        {
            var l = FraysRules.ResolveLimit(new[] { M("a", FraysMode.Climb), M("b", FraysMode.Limit, 12), M("c", FraysMode.Push, rating: 7) }, 3, 20);
            Assert.AreEqual(8, l.Level);
            Assert.AreEqual("c", l.SetBy);
        }

        [TestMethod]
        public void Limit_PushIsRatingPlusOne_NeverAboveCap()
        {
            Assert.AreEqual(1, FraysRules.ResolveLimit(new[] { M("a", FraysMode.Push) }, 0, 20).Level);
            Assert.AreEqual(20, FraysRules.ResolveLimit(new[] { M("a", FraysMode.Push, rating: 25) }, 0, 20).Level);
        }

        [TestMethod]
        public void Limit_AnyHoldFreezesTheFire()
        {
            var l = FraysRules.ResolveLimit(new[] { M("a", FraysMode.Climb), M("b", FraysMode.Hold) }, 6, 20);
            Assert.IsTrue(l.Frozen);
            Assert.AreEqual(6, l.Level);
            Assert.AreEqual("b", l.SetBy);

            // A lower limit still names the limit, and the fire stays frozen.
            l = FraysRules.ResolveLimit(new[] { M("a", FraysMode.Limit, 4), M("b", FraysMode.Hold) }, 6, 20);
            Assert.IsTrue(l.Frozen);
            Assert.AreEqual(4, l.Level);
            Assert.AreEqual("a", l.SetBy);
        }

        [TestMethod]
        public void Limit_NobodyPresentIsTheCap()
        {
            var l = FraysRules.ResolveLimit(Enumerable.Empty<FraysMemberSetting>(), 5, 20);
            Assert.AreEqual(20, l.Level);
            Assert.IsFalse(l.Frozen);
        }

        [TestMethod]
        public void Mode_ParseAndText()
        {
            Assert.IsTrue(FraysRules.TryParseMode("PUSH", out var m));
            Assert.AreEqual(FraysMode.Push, m);
            Assert.IsTrue(FraysRules.TryParseMode("steady", out m));
            Assert.AreEqual(FraysMode.Hold, m);
            Assert.IsFalse(FraysRules.TryParseMode("fast", out _));
            Assert.AreEqual("limit 7", FraysRules.ModeText(FraysMode.Limit, 7));
            Assert.AreEqual("hold", FraysRules.ModeText(FraysMode.Hold, 7));
        }

        // ---- rating ----

        [TestMethod]
        public void RatingMinute_FightingOnly()
        {
            // Cleared 70% or more of what spawned: counts.
            Assert.IsTrue(FraysRules.RatingMinuteCounts(false, true, 10, 7, 0.7));
            Assert.IsFalse(FraysRules.RatingMinuteCounts(false, true, 10, 6.9, 0.7));

            // Hold mode, away or dead never counts.
            Assert.IsFalse(FraysRules.RatingMinuteCounts(true, true, 10, 10, 0.7));
            Assert.IsFalse(FraysRules.RatingMinuteCounts(false, false, 10, 10, 0.7));

            // Nothing spawned (at the alive cap): counts only while still killing.
            Assert.IsTrue(FraysRules.RatingMinuteCounts(false, true, 0, 3, 0.7));
            Assert.IsFalse(FraysRules.RatingMinuteCounts(false, true, 0, 0, 0.7));
        }

        private static List<(double, int)> Samples(double from, double to, Func<double, int> level)
        {
            var list = new List<(double, int)>();

            for (var t = from; t <= to; t += 1)
                list.Add((t, level(t)));

            return list;
        }

        [TestMethod]
        public void Sustained_IsTheLowestLevelOverTheWindow()
        {
            var s = Samples(0, 1000, t => t < 500 ? 6 : 8);

            // Window of 900 s ending at 1000 starts at 100: it saw 6.
            Assert.AreEqual(6, FraysRules.SustainedLevel(s, 1000, 900, 0));

            var later = Samples(0, 1500, t => t < 500 ? 6 : 8);
            Assert.AreEqual(8, FraysRules.SustainedLevel(later, 1500, 900, 0));
        }

        [TestMethod]
        public void Sustained_NeedsAFullStreakAndCoverage()
        {
            var s = Samples(0, 1000, t => 8);

            // Died (or arrived) at 300: only 700 s of streak.
            Assert.AreEqual(-1, FraysRules.SustainedLevel(s, 1000, 900, 300));

            // Samples only from 500: the window is not covered.
            Assert.AreEqual(-1, FraysRules.SustainedLevel(Samples(500, 1000, t => 8), 1000, 900, 0));
            Assert.AreEqual(-1, FraysRules.SustainedLevel(new List<(double, int)>(), 1000, 900, 0));
        }

        [TestMethod]
        public void Rating_NeverDecreases()
        {
            Assert.AreEqual(7, FraysRules.RaiseRating(5, 7));
            Assert.AreEqual(5, FraysRules.RaiseRating(5, 3));
            Assert.AreEqual(5, FraysRules.RaiseRating(5, -1));
        }

        // ---- embers ----

        // ---- ownership ----

        [TestMethod]
        public void GoesOut_WhenNoOwnerOnline_AtOnce()
        {
            Assert.IsTrue(FraysRules.ShouldGoOut(0, 0, 0, 120));
        }

        [TestMethod]
        public void GoesOut_AfterTwoMinutesAway()
        {
            Assert.IsFalse(FraysRules.ShouldGoOut(3, 0, 119, 120));
            Assert.IsTrue(FraysRules.ShouldGoOut(3, 0, 120, 120));
            Assert.IsFalse(FraysRules.ShouldGoOut(3, 1, 500, 120));
        }

        [TestMethod]
        public void CampReport_LootLine_ShowsWhatDropped()
        {
            Assert.AreEqual("weapons", FraysReport.LootKind(ItemType.MeleeWeapon));
            Assert.AreEqual("weapons", FraysReport.LootKind(ItemType.Caster));
            Assert.AreEqual("armor", FraysReport.LootKind(ItemType.Clothing));
            Assert.AreEqual("jewelry", FraysReport.LootKind(ItemType.Jewelry));
            Assert.AreEqual("gems", FraysReport.LootKind(ItemType.Gem));
            Assert.AreEqual("other", FraysReport.LootKind(ItemType.ManaStone));

            Assert.AreEqual("Loot dropped: nothing.", FraysReport.LootLine(new Dictionary<string, int>()));
            Assert.AreEqual("Loot dropped: 2,412 items - weapons 1,200, armor 900, gems 312.",
                FraysReport.LootLine(new Dictionary<string, int> { ["armor"] = 900, ["gems"] = 312, ["weapons"] = 1200 }));
            Assert.AreEqual("Loot dropped: 3 items - other 3.", FraysReport.LootLine(new Dictionary<string, int> { ["other"] = 3 }));
        }

        [TestMethod]
        public void Start_NearYourRating_UnlessYouChoseOne()
        {
            Assert.AreEqual(2, FraysSettings.Int(FraysSettings.HeatStartBelowRating));
            Assert.AreEqual(8, FraysRules.WantedStart(false, 0, 10, 2), "rating 10: start at 8");
            Assert.AreEqual(0, FraysRules.WantedStart(false, 0, 1, 2), "never below 0");
            Assert.AreEqual(0, FraysRules.WantedStart(false, 0, 0, 2), "no rating: Heat 0");
            Assert.AreEqual(3, FraysRules.WantedStart(true, 3, 10, 2), "a /frays start choice wins");
            Assert.AreEqual(0, FraysRules.WantedStart(true, 0, 10, 2), "/frays start 0 is a choice too");

            var rec = FraysPlayerRecord.Parse("{}");
            rec.Rating = 10;
            Assert.AreEqual(8, rec.WantedStart(false));
            Assert.AreEqual(8, FraysRules.StartLevel(rec.WantedStart(false), rec.Rating, 20));
            rec.StartHeat = 5;   // an older record that set a start before the flag existed
            Assert.AreEqual(5, rec.WantedStart(false));
            rec.StartHeat = 0;
            rec.StartHeatChosen = true;
            Assert.AreEqual(0, FraysPlayerRecord.Parse(rec.ToJson()).WantedStart(false), "the choice is saved");
        }

        [TestMethod]
        public void EarlyHeats_ClimbFaster_ForPlayersWithNoRating()
        {
            Assert.AreEqual(5, FraysSettings.Int(FraysSettings.HeatEarlyClimbUntil));
            Assert.AreEqual(2.5, FraysSettings.Number(FraysSettings.HeatEarlyClimbGain), 1e-9);
            Assert.AreEqual(2.5, FraysRules.EarlyClimbGain(0, 0, 5, 2.5), 1e-9);
            Assert.AreEqual(2.5, FraysRules.EarlyClimbGain(0, 4, 5, 2.5), 1e-9);
            Assert.AreEqual(1.0, FraysRules.EarlyClimbGain(0, 5, 5, 2.5), 1e-9, "from Heat 5 on, the normal climb");
            Assert.AreEqual(1.0, FraysRules.EarlyClimbGain(3, 0, 5, 2.5), 1e-9, "anyone with a rating climbs normally");
        }

        [TestMethod]
        public void LootTier_FollowsTheScaledLevel()
        {
            // Level 115 Monouga (their own tier 4 loot) lifted to a level 440 camper roll tier 8.
            const string byLevel = "0:1,30:2,60:3,90:4,120:5,160:6,200:7,275:8";
            Assert.AreEqual(8, FraysRules.TierForLevel(byLevel, 440));
            Assert.AreEqual(8, FraysRules.TierForLevel(byLevel, 275));
            Assert.AreEqual(7, FraysRules.TierForLevel(byLevel, 274));
            Assert.AreEqual(4, FraysRules.TierForLevel(byLevel, 115));
            Assert.AreEqual(1, FraysRules.TierForLevel(byLevel, 1));

            // The default is tier 8 at every level.
            var table = FraysSettings.Text(FraysSettings.LootTierByLevel);
            Assert.AreEqual(8, FraysRules.TierForLevel(table, 1));
            Assert.AreEqual(8, FraysRules.TierForLevel(table, 115));
            Assert.AreEqual(0, FraysRules.TierForLevel("", 440), "empty = no floor");
            Assert.AreEqual(0, FraysRules.TierForLevel("junk", 440));

            // Past tier 8 the Heat steps turn into quality.
            Assert.AreEqual((8, 2), FraysRules.RaiseTier(System.Math.Max(4, FraysRules.TierForLevel(table, 440)), 2));
        }

        [TestMethod]
        public void Chests_FollowThreads_ByHeat()
        {
            // Threads give 3 to 4 ten-item chests a 10 minute run (about 200 cache items an hour).
            // The key bar gives Vault Keys (the Vault's 10 items) and every Snarl
            // gives its 10-item cache and a Vault Key.
            // Both are off the kill budget.
            var table = FraysSettings.Text(FraysSettings.KeyBarWork);
            Assert.AreEqual(228, FraysRules.KeyBarNeed(table, 0), 1e-9);
            Assert.AreEqual(124, FraysRules.KeyBarNeed(table, 10), 1e-9);
            Assert.AreEqual(82, FraysRules.KeyBarNeed(table, 20), 1e-9);
            Assert.AreEqual(82, FraysRules.KeyBarNeed(table, 30), 1e-9, "no growth past the end");
            Assert.IsTrue(FraysRules.KeyBarNeed(table, 5) < 228 && FraysRules.KeyBarNeed(table, 5) > 124);
            Assert.AreEqual(0, FraysRules.KeyBarNeed("", 10), "empty = no key bar");

            // The XP kill budget: past xp_kill_cap hours of kill share an hour, a kill pays nothing more (a 490-kill hour pays 1.25 x pace).
            var cap = FraysSettings.Number(FraysSettings.XpKillCap);
            var pace = FraysSettings.Number(FraysSettings.XpKillPace);
            var bucket = cap / 6;
            var filled = 0.0;

            for (var i = 0; i < 490; i++)
            {
                bucket = FraysRules.RefillKillBudget(bucket, 3600.0 / 490, cap);
                double paid;
                (paid, bucket) = FraysRules.SpendKillBudget(bucket, 1 / pace, cap);
                filled += paid;
            }

            Assert.IsTrue(filled <= cap * pace + cap / 6 * pace + 1, $"filled {filled:0} standard creatures' worth");
        }

        [TestMethod]
        public void LootQuality_StartsAtRetailT8_AndGrowsWithHeat()
        {
            // Heat 0 loot matches tier 8 hunting spots: Heat 0 is retail's own quality, Heat climbs from there.
            double Q(int heat, int steps = 0, double own = 0, bool cache = false) => FraysRules.LootQuality(own, steps,
                FraysSettings.Number(FraysSettings.LootQualityPerStep), FraysSettings.Number(FraysSettings.LootQualityBase),
                FraysSettings.Number(FraysSettings.LootQualityPerHeat), heat, FraysSettings.Number(FraysSettings.LootQualityCap), cache);

            Assert.AreEqual(0, Q(0), 1e-9, "Heat 0 is the retail T8 spots' 0");
            Assert.AreEqual(0.15, Q(10), 1e-9);
            Assert.AreEqual(0.30, Q(20), 1e-9);
            Assert.AreEqual(0.50, Q(20, steps: 2), 1e-9, "held under the cap (the Vault's 0.6 stays better)");
            Assert.AreEqual(0.60, Q(20, steps: 2, cache: true), 1e-9, "a Snarl cache is not capped");
            Assert.AreEqual(0.7, Q(0, own: 0.7), 1e-9, "never under the creature's own quality");
            Assert.IsTrue(Q(10) > Q(5) && Q(5) > Q(0));

            Assert.AreEqual(1, FraysSettings.Int(FraysSettings.SnarlKeys), "every Snarl kill gives a Vault Key");
        }

        [TestMethod]
        public void LootImprovesWithHeat_FromEightAndSixteen()
        {
            // On by default (was 99 = off), so loot gets better as Heat climbs.
            Assert.AreEqual(8, FraysSettings.Int(FraysSettings.LootTierFirstHeat));
            Assert.AreEqual(16, FraysSettings.Int(FraysSettings.LootTierSecondHeat));
            Assert.AreEqual(0, FraysRules.LootTierSteps(7, 8, 16));
            Assert.AreEqual(1, FraysRules.LootTierSteps(8, 8, 16));
            Assert.AreEqual(2, FraysRules.LootTierSteps(16, 8, 16));
        }

        [TestMethod]
        public void HearthstonePopup_IsAsciiAndExplainsTheStone()
        {
            var text = FraysLongTerm.HearthstonePopup();

            Assert.IsTrue(text.All(c => c < 128), "player text is ASCII only");
            StringAssert.Contains(text, "Campfire Hearthstone is in your pack");
            StringAssert.Contains(text, "once every 2 minutes");
            StringAssert.Contains(text, "for 15 minutes");

            var rec = FraysPlayerRecord.Parse("{}");
            Assert.IsFalse(rec.HearthstoneGiven, "nobody has had the popup yet");
            rec.HearthstoneGiven = true;
            Assert.IsTrue(FraysPlayerRecord.Parse(rec.ToJson()).HearthstoneGiven, "the flag is saved");
        }

        [TestMethod]
        public void GoesOut_WhenNoOwnerAtTheFire_ForTheFireAwayTime()
        {
            // Stops a dead camper holding a camp for hours from across the island.
            Assert.AreEqual(900, FraysSettings.Number(FraysSettings.FireAwaySeconds));
            Assert.IsFalse(FraysRules.AwayFromFireTooLong(899, 900));
            Assert.IsTrue(FraysRules.AwayFromFireTooLong(900, 900));
            Assert.IsTrue(FraysRules.AwayFromFireTooLong(3.5 * 3600, 900));
            Assert.IsFalse(FraysRules.AwayFromFireTooLong(3.5 * 3600, 0), "0 turns it off");
        }

        [TestMethod]
        public void RetailSpawnFilter_DropsCreaturesAndGenerators()
        {
            var rows = new[] { 30965u, 31005u, 31006u, 1154u, 7932u, 99999u };
            var types = new Dictionary<uint, WeenieType>
            {
                [30965] = WeenieType.Portal, [31005] = WeenieType.Creature, [31006] = WeenieType.Creature,
                [1154] = WeenieType.Generic, [7932] = WeenieType.Generic,
            };
            var generators = new HashSet<uint> { 1154, 7932 };

            var kept = FraysManager.FilterRetailSpawns(rows, r => r, w => types.TryGetValue(w, out var t) ? t : (WeenieType?)null, generators.Contains);

            CollectionAssert.AreEqual(new[] { 30965u }, kept);
        }

        // ---- leash zone ----

        private static FraysLeashZone Zone() =>
            new FraysLeashZone(new ushort[] { 0x100, 0x101 }, new Vector3(5, 5, 0), new[]
            {
                ((ushort)0x100, (ushort)0x101, new Vector3(10, 5, 0)),   // inside the room
                ((ushort)0x101, (ushort)0x102, new Vector3(20, 5, 0)),   // the doorway out
                ((ushort)0x102, (ushort)0x101, new Vector3(20, 5, 0)),   // the other side's view: not a doorway of ours
            });

        [TestMethod]
        public void Zone_FindsOnlyTheDoorwaysOut()
        {
            var z = Zone();
            Assert.AreEqual(1, z.Doorways.Count);
            Assert.AreEqual((ushort)0x102, z.Doorways[0].Outside);
        }

        [TestMethod]
        public void Zone_RoomCellsAreInside()
        {
            var z = Zone();
            Assert.IsTrue(z.Contains(0x100, new Vector3(1, 1, 0), 5, 30));
            Assert.IsTrue(z.Contains(0x101, new Vector3(15, 5, 0), 5, 30));
        }

        [TestMethod]
        public void Zone_DoorwayMarginReachesIntoTheCorridor()
        {
            var z = Zone();
            Assert.IsTrue(z.Contains(0x102, new Vector3(24, 5, 0), 5, 30));
            Assert.IsFalse(z.Contains(0x102, new Vector3(26, 5, 0), 5, 30));
            Assert.IsFalse(z.Contains(0x102, new Vector3(22, 5, 0), 0, 30));
        }

        [TestMethod]
        public void Zone_MaxChaseCutsEvenInsideTheRoom()
        {
            var z = Zone();
            Assert.IsFalse(z.Contains(0x101, new Vector3(40, 5, 0), 5, 30));
            Assert.IsTrue(z.Contains(0x101, new Vector3(40, 5, 0), 5, 0));
        }

        [TestMethod]
        public void Zone_OtherCellsAreOutside()
        {
            Assert.IsFalse(Zone().Contains(0x200, new Vector3(5, 5, 0), 5, 30));
        }

        [TestMethod]
        public void Gap_BetweenBoxes()
        {
            var a = new[] { (new Vector3(0, 0, 0), new Vector3(10, 10, 3)) };
            var b = new[] { (new Vector3(40, 0, 0), new Vector3(50, 10, 3)) };

            Assert.AreEqual(30, FraysLeashZone.Gap(a, b, 0), 1e-6);
            Assert.AreEqual(20, FraysLeashZone.Gap(a, b, 5), 1e-6);
            Assert.AreEqual(0, FraysLeashZone.Gap(a, b, 20), 1e-6);
        }

        // ---- settings ----

        [TestMethod]
        public void Settings_DefaultsWhenNotSet()
        {
            Assert.AreEqual(10, FraysSettings.Number(FraysSettings.LeashMargin));
            Assert.AreEqual(50, FraysSettings.Number(FraysSettings.LeashMaxChase));
            Assert.AreEqual(2, FraysSettings.Number(FraysSettings.LeashGraceSeconds));
            Assert.AreEqual(1.5, FraysSettings.Number(FraysSettings.LeashReturnSpeed));
            Assert.AreEqual(20, FraysSettings.Int(FraysSettings.HeatCap));
            Assert.AreEqual(600, FraysSettings.Number(FraysSettings.AwaySeconds));
            Assert.IsFalse(FraysSettings.Toggle(FraysSettings.Enabled));
            Assert.AreEqual("", FraysSettings.Text(FraysSettings.Landblocks));
        }

        [TestMethod]
        public void Settings_PropertyWinsAndIsClamped()
        {
            values["frays_leash_margin"] = "8";
            Assert.AreEqual(8, FraysSettings.Number(FraysSettings.LeashMargin));

            values["frays_leash_margin"] = "999";
            Assert.AreEqual(30, FraysSettings.Number(FraysSettings.LeashMargin));

            values["frays_leash_margin"] = "lots";
            Assert.AreEqual(10, FraysSettings.Number(FraysSettings.LeashMargin));

            values["frays_enabled"] = "on";
            Assert.IsTrue(FraysSettings.Toggle(FraysSettings.Enabled));
        }

        [TestMethod]
        public void Settings_PerFireOverride()
        {
            values["frays_leash_max_chase"] = "25";
            values["frays_fire_overrides"] = "eo2:leash_max_chase=18,leash_margin=7;eo3:heat_cap=5";

            Assert.AreEqual(18, FraysSettings.Number(FraysSettings.LeashMaxChase, "eo2"));
            Assert.AreEqual(7, FraysSettings.Number(FraysSettings.LeashMargin, "eo2"));
            Assert.AreEqual(25, FraysSettings.Number(FraysSettings.LeashMaxChase, "eo1"));

            // heat_cap is not a per-fire key: ignored.
            Assert.AreEqual(20, FraysSettings.Number(FraysSettings.HeatCap, "eo3"));
        }

        [TestMethod]
        public void Settings_OverridesRoundTrip()
        {
            var parsed = FraysSettings.ParseOverrides("eo2:leash_margin=7,leash_max_chase=18;bad;eo1:nope=3;eo3:leash_grace_seconds=4");
            var text = FraysSettings.FormatOverrides(parsed);

            Assert.AreEqual("eo2:leash_margin=7,leash_max_chase=18;eo3:leash_grace_seconds=4", text);
        }

        [TestMethod]
        public void Settings_EveryKeyIsUniqueAndPrefixed()
        {
            var keys = FraysSettings.All.Select(s => s.PropertyName).ToList();
            Assert.AreEqual(keys.Count, keys.Distinct().Count());
            Assert.IsTrue(keys.All(k => k.StartsWith("frays_")));
        }

        // ---- record ----

        [TestMethod]
        public void Record_RoundTripsAndToleratesJunk()
        {
            var r = new FraysPlayerRecord { Mode = FraysMode.Limit, Limit = 9, Rating = 6, Embers = 123.5 };
            var back = FraysPlayerRecord.Parse(r.ToJson());

            Assert.AreEqual(FraysMode.Limit, back.Mode);
            Assert.AreEqual(9, back.Limit);
            Assert.AreEqual(6, back.Rating);
            Assert.AreEqual(123.5, back.Embers, 1e-9);

            Assert.AreEqual(FraysMode.Climb, FraysPlayerRecord.Parse("{not json").Mode);
            Assert.AreEqual(FraysMode.Climb, new FraysPlayerRecord().Mode);
            Assert.AreEqual(0, FraysPlayerRecord.Parse(null).Rating);

            var start = FraysPlayerRecord.Parse(new FraysPlayerRecord { StartHeat = 7 }.ToJson());
            Assert.AreEqual(7, start.StartHeat);
        }

        // ---- personal Heat ----

        [TestMethod]
        public void PersonalLimit_AutoHoldsAfterADeathElseClimbs()
        {
            var climbing = FraysRules.PersonalLimit("a", FraysMode.Auto, 0, 5, 3, 20, autoCap: -1);
            Assert.AreEqual(20, climbing.Level);

            var held = FraysRules.PersonalLimit("a", FraysMode.Auto, 0, 5, 3, 20, autoCap: 6);
            Assert.AreEqual(6, held.Level);
            Assert.IsFalse(held.Frozen);

            Assert.AreEqual(6, FraysRules.PersonalLimit("a", FraysMode.Push, 0, 5, 3, 20, -1).Level);
            Assert.IsTrue(FraysRules.PersonalLimit("a", FraysMode.Hold, 0, 5, 3, 20, -1).Frozen);
            Assert.AreEqual(8, FraysRules.AutoCapAfterDeath(10));
            Assert.AreEqual(0, FraysRules.AutoCapAfterDeath(1));
        }

        [TestMethod]
        public void Frays_ClimbSettlesBelowWhereYouKeepDying()
        {
            var one = new List<(double, int)> { (0, 15) };
            var two = new List<(double, int)> { (0, 15), (300, 13) };

            Assert.IsNull(FraysRules.Settle(one, 2, 2, 30, 120, 0), "one death does not settle");
            Assert.IsNull(FraysRules.Settle(two, 0, 2, 30, 120, 0), "0 turns it off");

            var s = FraysRules.Settle(two, 2, 2, 30, 120, 0).Value;
            Assert.AreEqual(11, s.Cap, "2 below the lowest Heat died at");
            Assert.AreEqual(30 * 60, s.HoldSeconds, 0.001);

            Assert.AreEqual(60 * 60, FraysRules.Settle(two, 2, 2, 30, 120, 1).Value.HoldSeconds, 0.001, "a second settle doubles");
            Assert.AreEqual(120 * 60, FraysRules.Settle(two, 2, 2, 30, 120, 5).Value.HoldSeconds, 0.001, "never past the max");

            // Climbing with no settle is unchanged; a settle caps the Heat like a limit.
            Assert.AreEqual(FraysRules.PersonalLimit("a", FraysMode.Climb, 0, 5, 3, 20, -1).Level, FraysRules.PersonalLimit("a", FraysMode.Auto, 0, 5, 3, 20, -1).Level);
            Assert.AreEqual(11, FraysRules.PersonalLimit("a", FraysMode.Auto, 0, 5, 3, 20, 11).Level);

            // A settle under the Heat the camper fell to takes them down to it (one settled at 16 and fought on at 17).
            Assert.AreEqual(16, FraysRules.SettledLevel(17, 16), "over the cap: down to it");
            Assert.AreEqual(14, FraysRules.SettledLevel(14, 16), "under the cap: unchanged");
            Assert.AreEqual(17, FraysRules.SettledLevel(17, -1), "no cap: unchanged");
        }

        [TestMethod]
        public void StartLevel_NeverAboveRatingOrCap()
        {
            Assert.AreEqual(5, FraysRules.StartLevel(12, 5, 20));
            Assert.AreEqual(4, FraysRules.StartLevel(4, 9, 20));
            Assert.AreEqual(20, FraysRules.StartLevel(30, 30, 20));
            Assert.AreEqual(0, FraysRules.StartLevel(-3, 9, 20));
        }

        [TestMethod]
        public void Decay_OnlyAfterTheGraceWithoutKills()
        {
            Assert.IsFalse(FraysRules.DecayRuns(100, 110, 15));
            Assert.IsTrue(FraysRules.DecayRuns(100, 115, 15));
            Assert.IsTrue(FraysRules.DecayRuns(100, 101, 0));
        }

        [TestMethod]
        public void HitHeat_IsTheLowerOfMonsterAndDefender()
        {
            Assert.AreEqual(4, FraysRules.HitHeat(15, 4));
            Assert.AreEqual(9, FraysRules.HitHeat(9, 15));
            Assert.AreEqual(0, FraysRules.HitHeat(5, -1));
        }

        [TestMethod]
        public void WorkUnits_FollowHealthSoAoeAndSingleTargetEarnAlike()
        {
            // Five creatures with a fifth of the reference health are worth one reference creature.
            var small = FraysRules.WorkUnits(200, 1000);
            Assert.AreEqual(1.0, 5 * small, 1e-9);

            // An elite with 2.5x health is worth 2.5 reference creatures.
            Assert.AreEqual(2.5, FraysRules.WorkUnits(1000, 1000, 2.5), 1e-9);

            // Shares split a kill's credit by damage.
            Assert.AreEqual(0.25, FraysRules.Share(25, 100), 1e-9);
            Assert.AreEqual(0, FraysRules.Share(10, 0), 1e-9);
        }

        [TestMethod]
        public void LootTiers_StepAtEightAndSixteen_QualityPastTheCap()
        {
            Assert.AreEqual(0, FraysRules.LootTierSteps(7));
            Assert.AreEqual(1, FraysRules.LootTierSteps(8));
            Assert.AreEqual(2, FraysRules.LootTierSteps(16));

            Assert.AreEqual((6, 0), FraysRules.RaiseTier(5, 1));
            Assert.AreEqual((8, 0), FraysRules.RaiseTier(7, 1));
            Assert.AreEqual((8, 1), FraysRules.RaiseTier(7, 2));
        }

        [TestMethod]
        public void StaticXp_IsAFixedRateForActiveTime()
        {
            Assert.AreEqual(3_500_000_000L, FraysRules.ForSeconds(3_500_000_000, 3600));
            Assert.AreEqual(58_333_333L, FraysRules.ForSeconds(3_500_000_000, 60));
            Assert.AreEqual(10_000L, FraysRules.ForSeconds(600_000, 60));
            Assert.AreEqual(0L, FraysRules.ForSeconds(600_000, 0));

            Assert.IsTrue(FraysRules.IsActive(100, 150, 60));
            Assert.IsFalse(FraysRules.IsActive(100, 161, 60));
            Assert.IsFalse(FraysRules.IsActive(0, 10, 60));
        }

        [TestMethod]
        public void Settings_StaticXpDefaults()
        {
            Assert.AreEqual(150, FraysSettings.Number(FraysSettings.HeatDecayGraceSeconds));   // a full rebuff never cools the fire
            Assert.AreEqual(30, FraysSettings.Number(FraysSettings.HeatDecaySeconds));
            Assert.AreEqual(60, FraysSettings.Number(FraysSettings.CorpseRotSeconds));
        }

        // ---- the prototype's fires ----

        [TestMethod]
        public void Defs_NoDungeonFiresLeft()
        {
            // The Egg Orchard and Elemental Caverns dungeon Frays were removed: no fire is in either.
            Assert.IsFalse(FraysDefs.All.Any(f => f.Landblock == 0x002B || f.Landblock == 0x5F47), "no Egg Orchard or Elemental Caverns fire");
            Assert.IsTrue(FraysDefs.All.Count > 0);

            foreach (var f in FraysDefs.All.Where(d => !d.Outdoor))
            {
                Assert.IsTrue(f.RoomCells.Contains(f.FireCell), $"{f.Id}: the fire's cell is not a room cell");
                Assert.IsTrue(f.SpawnPoints.Count >= 2 && f.SpawnPoints.Count <= 6, $"{f.Id}: {f.SpawnPoints.Count} spawn points");

                foreach (var p in f.SpawnPoints)
                {
                    Assert.IsTrue(f.RoomCells.Contains(p.Cell), $"{f.Id}: spawn cell {p.Cell:X3} is not a room cell");
                    Assert.IsTrue(Vector3.Distance(p.Pos, f.FirePos) <= 30, $"{f.Id}: spawn point {p.Cell:X3} is past the default chase distance");
                }

                Assert.IsTrue(f.Mix.All(m => (m.Wcid == 31005 || m.Wcid == 31007 || m.Wcid == 31008 || (m.Wcid >= 2000059 && m.Wcid <= 2000074))), $"{f.Id}: a creature outside the Fray roster (no larvae)");
                Assert.IsTrue(f.MixAt(0).Any() && f.MixAt(20).Any(), $"{f.Id}: nothing spawns at some Heat");
            }

            // No two fires of one Fray share a cell.
            var all = FraysDefs.All.SelectMany(f => f.RoomCells.Select(c => (f.Landblock, c))).ToList();
            Assert.AreEqual(all.Count, all.Distinct().Count(), "a cell belongs to two fires");
        }

        [TestMethod]
        public void Defs_PlayerTextIsAscii()
        {
            foreach (var f in FraysDefs.All)
                Assert.IsTrue(f.RoomName.All(c => c < 128), f.Id);

            foreach (var s in FraysSettings.All)
                Assert.IsTrue(s.Description.All(c => c < 128), s.Key);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Knots;
using ACE.Server.Managers.Analytics;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// Track A (Drops) of Knots (Docs/Knots/KNOTS-PLAN.md): the planned Fiber level, the drop roll and its bad-luck
    /// protection, the run gates, the Knotted cache choice and naming, the placement sort, and the clear wiring. Every
    /// test here is pure or source-text; none reads a PropertyManager tunable (the roll takes its chances as arguments).
    /// </summary>
    [TestClass]
    public class KnotDropsTests
    {
        private static uint nextGuid = 0x7E200000;

        private static WorldObject KnotItem(int kind, WeenieType type = WeenieType.Generic, int value = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 990000,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.KnotItemKind, kind }, { PropertyInt.Value, value } },
            };

            return WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Plain(uint wcid, WeenieType type, int value)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Value, value } },
            };

            return WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static DungeonSpawnPlan Plan(int bossLevel, params DungeonSpawnPlanEntry[] entries)
        {
            var plan = new DungeonSpawnPlan { BossLevel = bossLevel };
            plan.Entries.AddRange(entries);
            return plan;
        }

        private static DungeonSpawnPlanEntry Trash(uint wcid, int uplift = 0, int stamp = 0) => new DungeonSpawnPlanEntry(wcid, DungeonRole.Trash, null, uplift, stamp);

        private static DungeonSpawnPlanEntry Boss(uint wcid, int uplift = 0) => new DungeonSpawnPlanEntry(wcid, DungeonRole.Boss, null, uplift);

        // Authored levels by wcid: 1 -> 100, 2 -> 200, 3 -> 300, 9 (the boss weenie) -> 150.
        private static int Authored(uint wcid) => wcid == 9 ? 150 : (int)wcid * 100;

        private static ThreadDungeonRun NewRun()
        {
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        // ---- KnotLevelMath.PlannedAverage ----

        [TestMethod]
        public void PlannedAverage_AuthoredLevels_IsTheFlooredMean()
        {
            // (100 + 200 + 200) / 3 = 166.67 -> 166.
            Assert.AreEqual(166, KnotLevelMath.PlannedAverage(Plan(0, Trash(1), Trash(2), Trash(2)), Authored));
        }

        [TestMethod]
        public void PlannedAverage_ReachUpStamp_RaisesOnlyUpward()
        {
            // A stamp above the authored level is placed at the stamp; one below it leaves the authored level (upward-only,
            // as the spawner's `entry.StampLevel > (creature.Level ?? 0)` write).
            Assert.AreEqual(300, KnotLevelMath.PlannedAverage(Plan(0, Trash(1, stamp: 400), Trash(2, stamp: 150)), Authored));
        }

        [TestMethod]
        public void PlannedAverage_Uplift_RaisesOnlyUpward()
        {
            // BandUplift.LevelFloor: the uplift level when it is above the authored one, else the authored one.
            Assert.AreEqual(250, KnotLevelMath.PlannedAverage(Plan(0, Trash(1, uplift: 200), Trash(3, uplift: 250)), Authored));
        }

        [TestMethod]
        public void PlannedAverage_BossTakesThePlanBossLevel_NotItsStampOrAuthoredLevel()
        {
            // Boss weenie authored 150, plan.BossLevel 401; trash 100 and 200. (100 + 200 + 401) / 3 = 233.67 -> 233.
            Assert.AreEqual(233, KnotLevelMath.PlannedAverage(Plan(401, Trash(1), Trash(2), Boss(9)), Authored));
        }

        [TestMethod]
        public void PlannedAverage_BossWithNoPlanLevel_IsAuthored_AndUpliftFloorsIt()
        {
            // No BossLevel: authored 150. Uplifted to 260: ApplyUplift runs after the boss stamp, so 260 wins.
            Assert.AreEqual(150, KnotLevelMath.PlannedAverage(Plan(0, Boss(9)), Authored));
            Assert.AreEqual(260, KnotLevelMath.PlannedAverage(Plan(0, Boss(9, uplift: 260)), Authored));

            // With a BossLevel above the uplift, the BossLevel stands; below it, the uplift floors it.
            Assert.AreEqual(401, KnotLevelMath.PlannedAverage(Plan(401, Boss(9, uplift: 260)), Authored));
            Assert.AreEqual(260, KnotLevelMath.PlannedAverage(Plan(200, Boss(9, uplift: 260)), Authored));
        }

        [TestMethod]
        public void PlannedAverage_CountsTheBossOnce()
        {
            // A second boss-role entry is not counted: (100 + 400) / 2 = 250, never (100 + 400 + 400) / 3.
            Assert.AreEqual(250, KnotLevelMath.PlannedAverage(Plan(400, Trash(1), Boss(9), Boss(9)), Authored));
        }

        [TestMethod]
        public void PlannedAverage_EmptyOrNullPlan_IsNull()
        {
            Assert.IsNull(KnotLevelMath.PlannedAverage(Plan(0), Authored));
            Assert.IsNull(KnotLevelMath.PlannedAverage(null, Authored));
            Assert.IsNull(KnotLevelMath.PlannedAverage(Plan(0, Trash(1)), null));
        }

        // ---- KnotDrops.Decide ----

        [TestMethod]
        public void Decide_PityStepRaisesTheChanceEachMiss()
        {
            // Base 0.20, step 0.05: a draw of 0.32 misses at 0, 1 and 2 misses (0.20, 0.25, 0.30) and drops at 3 (0.35).
            var misses = 0;
            for (var i = 0; i < 3; i++)
            {
                var (dropped, next) = KnotDrops.Decide(0.20, 0.05, misses, 0.32);
                Assert.IsFalse(dropped, $"miss {i}");
                Assert.AreEqual(misses + 1, next);
                misses = next;
            }

            var (finalDrop, reset) = KnotDrops.Decide(0.20, 0.05, misses, 0.32);
            Assert.IsTrue(finalDrop);
            Assert.AreEqual(0, reset, "a drop resets the counter");
        }

        [TestMethod]
        public void Decide_DropResetsToZero_FromAnyCount()
        {
            Assert.AreEqual((true, 0), KnotDrops.Decide(0.20, 0.05, 7, 0.0));
        }

        [TestMethod]
        public void Decide_ChanceCapsAtOne()
        {
            // 0.20 + 40 x 0.05 = 2.2, capped at 1: the highest possible draw below 1 still drops.
            Assert.IsTrue(KnotDrops.Decide(0.20, 0.05, 40, 0.999999).Dropped);
            Assert.IsTrue(KnotDrops.Decide(1.0, 0.0, 0, 0.999999).Dropped);

            // Chance 0 never drops, even at a draw of 0.
            Assert.IsFalse(KnotDrops.Decide(0.0, 0.0, 0, 0.0).Dropped);
        }

        [TestMethod]
        public void Decide_NegativeMissesAndNaNs_ReadAsZero()
        {
            Assert.AreEqual((false, 1), KnotDrops.Decide(0.20, 0.05, -5, 0.21), "negative misses read as 0, so chance 0.20");
            Assert.AreEqual((false, 1), KnotDrops.Decide(double.NaN, 0.05, 0, 0.0001));
            Assert.AreEqual((false, 4), KnotDrops.Decide(0.20, double.NaN, 3, 0.21));
        }

        // ---- KnotDrops.Roll ----

        [TestMethod]
        public void Roll_NoEnabledPinnacle_SkipsThePatternWithoutADraw()
        {
            var draws = 0;
            var outcome = KnotDrops.Roll(0, 1.0, 0.05, 1.0, 0, () => { draws++; return 0.0; }, _ => 0);

            Assert.IsTrue(outcome.FiberDropped);
            Assert.IsFalse(outcome.PatternDropped);
            Assert.AreEqual(1, draws, "only the Fiber roll draws");
        }

        [TestMethod]
        public void Roll_PatternIsIndependentOfTheFiber_AndPicksAmongTheEnabled()
        {
            var seq = new Queue<double>(new[] { 0.99, 0.01 }); // Fiber dry, Pattern hit
            var picked = -1;
            var outcome = KnotDrops.Roll(2, 0.20, 0.05, 0.03, 4, () => seq.Dequeue(), n => { picked = n; return 2; });

            Assert.IsFalse(outcome.FiberDropped);
            Assert.AreEqual(3, outcome.NewMisses);
            Assert.AreEqual(2, outcome.PatternIndex);
            Assert.AreEqual(4, picked, "the pick ranges over every enabled pinnacle");

            var miss = KnotDrops.Roll(0, 0.20, 0.05, 0.03, 4, () => 0.5, _ => 0);
            Assert.IsFalse(miss.PatternDropped, "a draw at or above the Pattern chance drops nothing");
        }

        [TestMethod]
        public void Roll_OutOfRangePick_DropsNoPattern()
        {
            Assert.IsFalse(KnotDrops.Roll(0, 0.0, 0.0, 1.0, 2, () => 0.0, _ => 5).PatternDropped);
        }

        // ---- KnotDrops.ShouldRoll ----

        [TestMethod]
        public void ShouldRoll_OnlyAnOrdinaryPooledFoughtRunWithALevelAndTheSwitchOn()
        {
            Assert.IsTrue(KnotDrops.ShouldRoll(false, 200, true, 12, true));

            Assert.IsFalse(KnotDrops.ShouldRoll(true, 200, true, 12, true), "Thread-Guide runs never roll");
            Assert.IsFalse(KnotDrops.ShouldRoll(false, null, true, 12, true), "no stamped level");
            Assert.IsFalse(KnotDrops.ShouldRoll(false, 0, true, 12, true), "a zero level is no level");
            Assert.IsFalse(KnotDrops.ShouldRoll(false, 200, false, 12, true), "pooled loot off: legacy path");
            Assert.IsFalse(KnotDrops.ShouldRoll(false, 200, true, 0, true), "a run that placed nothing cleared without a fight");
            Assert.IsFalse(KnotDrops.ShouldRoll(false, 200, true, 12, false), "dynamic_dungeons_fiber_enabled off");
        }

        [TestMethod]
        public void FiberCountForLine_IsHeldPlusLanded()
        {
            Assert.AreEqual(7, KnotDrops.FiberCountForLine(6, 1));
            Assert.AreEqual(1, KnotDrops.FiberCountForLine(0, 1));
            Assert.AreEqual(1, KnotDrops.FiberCountForLine(-3, 0), "never below one: the Fiber that just landed counts");
            StringAssert.Contains(KnotItems.FiberLanded(KnotDrops.FiberCountForLine(12, 1)), "(Fiber 10 of 10)");
        }

        // ---- the run stamp and the held-pile query ----

        [TestMethod]
        public void MarkKnotLevel_IsWriteOnce()
        {
            var run = NewRun();
            Assert.IsNull(run.KnotLevel);

            run.MarkKnotLevel(233);
            run.MarkKnotLevel(400);
            Assert.AreEqual(233, run.KnotLevel);

            var empty = NewRun();
            empty.MarkKnotLevel(null);
            empty.MarkKnotLevel(300);
            Assert.IsNull(empty.KnotLevel, "an empty plan's null is the stamp, and it latches too");
        }

        [TestMethod]
        public void HeldPileContains_SeesOnlyThatMembersPile()
        {
            var run = NewRun();
            var owner = run.OwnerGuid;

            Assert.IsFalse(run.HeldPileContains(owner, KnotItems.IsKnotDrop));

            run.AddToHeldPile(owner, new HeldPileItem(Plain(30000, WeenieType.Generic, 5), null, 1));
            Assert.IsFalse(run.HeldPileContains(owner, KnotItems.IsKnotDrop), "plain loot is not a Knot drop");

            run.AddToHeldPile(owner, new HeldPileItem(KnotItem(KnotItemKind.Fiber), null, 2));
            Assert.IsTrue(run.HeldPileContains(owner, KnotItems.IsKnotDrop));

            Assert.IsFalse(run.HeldPileContains(0x50000999u, KnotItems.IsKnotDrop), "not a member");
            Assert.IsFalse(run.HeldPileContains(owner, null));
        }

        // ---- the Knotted cache ----

        [TestMethod]
        public void ChooseCacheWcid_KnottedOnlyWhenTheHolderHasAKnotDropWaiting()
        {
            Assert.AreEqual(KnotItems.KnottedThreadCacheWcid, ThreadCachePlacer.ChooseCacheWcid(true));
            Assert.AreEqual(ThreadDungeonRewardSpawner.BossCacheWcid, ThreadCachePlacer.ChooseCacheWcid(false));
        }

        [TestMethod]
        public void CacheWcidFor_ReadsTheHoldersPile()
        {
            var run = NewRun();
            Assert.AreEqual(ThreadDungeonRewardSpawner.BossCacheWcid, ThreadCachePlacer.CacheWcidFor(run, run.OwnerGuid));

            run.AddToHeldPile(run.OwnerGuid, new HeldPileItem(KnotItem(KnotItemKind.Pattern), null, 1));
            Assert.AreEqual(KnotItems.KnottedThreadCacheWcid, ThreadCachePlacer.CacheWcidFor(run, run.OwnerGuid), "a Pattern alone makes it Knotted");
        }

        [TestMethod]
        public void KnottedCache_IsNamedForItsHolder_FromItsOwnBaseName()
        {
            Assert.AreEqual(ThreadDungeonRewardSpawner.CacheBaseName, ThreadDungeonRewardSpawner.CacheBaseNameFor(ThreadDungeonRewardSpawner.BossCacheWcid));
            Assert.AreEqual(KnotItems.KnottedThreadCacheName, ThreadDungeonRewardSpawner.CacheBaseNameFor(KnotItems.KnottedThreadCacheWcid));

            Assert.AreEqual("Awfulwaffle's Knotted Thread Cache",
                ThreadDungeonRewardSpawner.ComposeCacheName("Awfulwaffle", ThreadDungeonRewardSpawner.CacheBaseNameFor(KnotItems.KnottedThreadCacheWcid)));
            Assert.AreEqual("Kess' Knotted Thread Cache",
                ThreadDungeonRewardSpawner.ComposeCacheName("Kess", KnotItems.KnottedThreadCacheName));
        }

        [TestMethod]
        public void KnottedCache_BaseName_MatchesTheShippedWeenieRow()
        {
            var sql = RepoFile(Path.Combine("Content", "sql", "weenies", "1007004 Knotted Thread Cache.sql"));
            StringAssert.Contains(sql, $"(1007004,   1, '{KnotItems.KnottedThreadCacheName}') /* Name */");
        }

        [TestMethod]
        public void PinnacleCache_IsNamedForItsHolder_FromItsOwnBaseName()
        {
            Assert.AreEqual(KnotItems.PinnacleCacheName, ThreadDungeonRewardSpawner.CacheBaseNameFor(KnotItems.PinnacleCacheWcid));
            Assert.AreNotEqual(KnotItems.KnottedThreadCacheWcid, KnotItems.PinnacleCacheWcid, "the pinnacle chest is its own weenie");
            Assert.AreEqual("Kess' Pinnacle Cache", ThreadDungeonRewardSpawner.ComposeCacheName("Kess", KnotItems.PinnacleCacheName));
        }

        [TestMethod]
        public void PinnacleCache_BaseName_MatchesTheShippedWeenieRow()
        {
            var sql = RepoFile(Path.Combine("Content", "sql", "weenies", "1007005 Pinnacle Cache.sql"));
            StringAssert.Contains(sql, $"(1007005,   1, '{KnotItems.PinnacleCacheName}') /* Name */");
        }

        [TestMethod]
        public void PinnacleCache_IsTheKnottedCacheAtTwiceTheScale()
        {
            var knotted = RepoFile(Path.Combine("Content", "sql", "weenies", "1007004 Knotted Thread Cache.sql"));
            var pinnacle = RepoFile(Path.Combine("Content", "sql", "weenies", "1007005 Pinnacle Cache.sql"));

            var knottedSetup = System.Text.RegularExpressions.Regex.Match(knotted, @"\(1007004,\s+1,\s+(0x[0-9A-Fa-f]{8})\)");
            var pinnacleSetup = System.Text.RegularExpressions.Regex.Match(pinnacle, @"\(1007005,\s+1,\s+(0x[0-9A-Fa-f]{8})\)");
            Assert.IsTrue(knottedSetup.Success && pinnacleSetup.Success);
            Assert.AreEqual(knottedSetup.Groups[1].Value.ToUpperInvariant(), pinnacleSetup.Groups[1].Value.ToUpperInvariant(), "same model");

            StringAssert.Matches(pinnacle, new System.Text.RegularExpressions.Regex(@"\(1007005,\s+39,\s+2\.0\) /\* DefaultScale"));
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(knotted, @"\(1007004,\s+39,"), "the Threads Knotted cache is 1x: no DefaultScale row");
        }

        [TestMethod]
        public void KnotDropWcids_AreInTheLootWarmupSet()
        {
            var set = ThreadLootWeenieWarmup.DeliveryWcids(null, null).ToList();
            CollectionAssert.IsSubsetOf(new[] { KnotItems.FiberWcid, KnotItems.PatternWcid, KnotItems.KnottedThreadCacheWcid, KnotItems.PinnacleCacheWcid }, set);
        }

        // ---- placement order ----

        [TestMethod]
        public void SortForPlacement_PutsKnotDropsFirst_AheadOfStackablesAndGear()
        {
            var gear = Plain(9502, WeenieType.Generic, 900);
            var stack = Plain(9500, WeenieType.Stackable, 500);
            var fiber = KnotItem(KnotItemKind.Fiber, WeenieType.Gem);
            var pattern = KnotItem(KnotItemKind.Pattern);

            var sorted = ThreadLootStacking.SortForPlacement(new List<(WorldObject Item, string RareFinderName)>
            {
                (gear, null), (stack, null), (pattern, null), (fiber, null),
            });

            CollectionAssert.AreEqual(new[] { pattern, fiber, stack, gear }, sorted.Select(s => s.Item).ToList(),
                "Knot drops first in arrival order, then the existing stackables-then-gear order");
        }

        [TestMethod]
        public void SortPileForPlacement_PutsKnotDropsFirst()
        {
            var gear = Plain(9602, WeenieType.Generic, 9000);
            var stack = Plain(9600, WeenieType.Stackable, 600);
            var fiber = KnotItem(KnotItemKind.Fiber, WeenieType.Gem);

            var sorted = ThreadLootStacking.SortPileForPlacement(new List<HeldPileItem>
            {
                new HeldPileItem(gear, null, 1), new HeldPileItem(stack, null, 1), new HeldPileItem(fiber, null, 2),
            });

            CollectionAssert.AreEqual(new[] { fiber, stack, gear }, sorted.Select(h => h.Item).ToList());
        }

        // ---- the clear wiring ----

        [TestMethod]
        public void AnnounceCleared_RollsKnotDropsOnce_BeforeTheSurvey()
        {
            var run = NewRun();
            run.MarkPopulated(0, 0, 0);

            var order = new List<string>();
            var originalKnots = ThreadDungeonManager.KnotDropRoller;
            var originalSurvey = ThreadDungeonManager.SurveyRecorder;
            var originalReward = ThreadDungeonManager.ClearRewardHandler;
            var originalGuide = ThreadDungeonManager.GuideRecorder;
            var originalCounter = ThreadDungeonManager.ClearCounter;
            ThreadDungeonManager.KnotDropRoller = _ => order.Add("knots");
            ThreadDungeonManager.SurveyRecorder = _ => order.Add("survey");
            ThreadDungeonManager.ClearRewardHandler = (_, __) => { };
            ThreadDungeonManager.GuideRecorder = _ => { };
            ThreadDungeonManager.ClearCounter = _ => { };

            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
                ThreadDungeonManager.OnRunPopulated(run);
            }
            finally
            {
                ThreadDungeonManager.KnotDropRoller = originalKnots;
                ThreadDungeonManager.SurveyRecorder = originalSurvey;
                ThreadDungeonManager.ClearRewardHandler = originalReward;
                ThreadDungeonManager.GuideRecorder = originalGuide;
                ThreadDungeonManager.ClearCounter = originalCounter;
            }

            CollectionAssert.AreEqual(new[] { "knots", "survey" }, order, "one roll per run, behind the clear latch, before the survey");
        }

        [TestMethod]
        public void OnRunCleared_ReturnsQuietly_ForARunWithNoStampedLevel()
        {
            // The production seam must be safe from every AnnounceCleared a unit test drives: no level, no roll, no
            // tunable read, no throw, and nothing added to the owner's pile.
            var run = NewRun();
            run.MarkPooledLoot(true);
            run.MarkPopulated(3, 3, 0);

            KnotDrops.OnRunCleared(run);
            KnotDrops.OnRunCleared(null);

            Assert.IsFalse(run.HasHeldPile(run.OwnerGuid));
        }

        [TestMethod]
        public void TheSpawner_StampsTheKnotLevel_RightAfterThePlanIsBuilt_AndBeforeAnyCreatureIsQueued()
        {
            var src = RepoFile(Path.Combine("Source", "ACE.Server", "ThreadDungeons", "ThreadDungeonSpawner.cs")).Replace("\r\n", "\n");

            var build = src.IndexOf("plan = DungeonPopulationBuilder.Build(", StringComparison.Ordinal);
            var stamp = src.IndexOf("run.MarkKnotLevel(KnotLevelMath.PlannedAverage(plan, LiveLevelOf));", StringComparison.Ordinal);
            // The placement queue is the per-landblock group queue since Open World Threads WP3b (one group for a dungeon).
            var queue = src.IndexOf("var placementGroups = new Queue<(Landblock Block, Queue<DungeonSpawnPlanEntry> Entries)>();", StringComparison.Ordinal);

            Assert.IsTrue(build >= 0 && build < stamp && stamp < queue);
        }

        // ---- per-seat rolls (owner ruling 2026-10-21) ----

        private const uint SeatA = 0x50000002u;
        private const uint SeatB = 0x50000003u;
        private const uint SeatC = 0x50000004u;

        private static ThreadDungeonRun NewGroupRun(params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(0x50000001u, "Owner", 7, 200) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"Seat{g:X}", 8, 200)));

            var group = GroupScaling.Compute(seats.Count, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare,
                GroupScaling.DefaultGroupMaxMonsters, GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap,
                GroupScaling.DefaultRewardBonusPerMember, GroupScaling.DefaultRewardBonusCap, sharePlateau: 0);

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001299u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        /// <summary>Everything one RollSeats call did: who was opened, what each wrote, and the draws consumed.</summary>
        private sealed class SeatLog
        {
            public readonly List<uint> Opened = new List<uint>();
            public readonly Dictionary<uint, int> Written = new Dictionary<uint, int>();
            public int Draws;
        }

        private static SeatLog RunSeats(ThreadDungeonRun run, bool perSeat, Func<uint, bool> online, Func<uint, int> misses, Queue<double> draws,
            Func<int, WorldObject> fiberFactory = null, Func<uint, bool> openThrows = null, IReadOnlyList<PinnacleDef> enabled = null)
        {
            var log = new SeatLog();
            var oDraw = KnotDrops.Draw;
            var oPick = KnotDrops.Pick;
            var oOpen = KnotDrops.OpenSeat;
            var oFiber = KnotDrops.FiberFactory;

            KnotDrops.Draw = () => { log.Draws++; return draws.Dequeue(); };
            KnotDrops.Pick = _ => 0;
            KnotDrops.FiberFactory = fiberFactory ?? (_ => KnotItem(KnotItemKind.Fiber, WeenieType.Gem));
            KnotDrops.OpenSeat = (r, guid) =>
            {
                log.Opened.Add(guid);
                if (openThrows != null && openThrows(guid))
                    throw new InvalidOperationException("seat failed");
                return (misses(guid), v => log.Written[guid] = v);
            };

            try
            {
                KnotDrops.RollSeats(run, 233, perSeat, 0.20, 0.05, 0.03, enabled ?? new List<PinnacleDef>(), online);
            }
            finally
            {
                KnotDrops.Draw = oDraw;
                KnotDrops.Pick = oPick;
                KnotDrops.OpenSeat = oOpen;
                KnotDrops.FiberFactory = oFiber;
            }

            return log;
        }

        private static int FibersIn(ThreadDungeonRun run, uint guid)
            => run.HeldPileContains(guid, w => KnotItems.IsFiber(w)) ? 1 : 0;

        [TestMethod]
        public void TwoSeatGroupRoll_EnqueuesOneFiberRollPerSeat_WithTheSeatsCharacterId()
        {
            AnalyticsManager.SetEnabledForTests(true);
            AnalyticsManager.ResetKnotQueueForTests();

            try
            {
                var run = NewGroupRun(SeatA);
                run.MarkMemberEntered(SeatA);

                // one pinnacle enabled: each seat's Pattern draw (0.0 < 0.03) hits and the pick takes it
                var oPattern = KnotDrops.PatternFactory;
                KnotDrops.PatternFactory = _ => PatternItem();

                try
                {
                    RunSeats(run, true, _ => true, _ => 3, new Queue<double>(new[] { 0.0, 0.0, 0.0, 0.0 }), enabled: new List<PinnacleDef> { OnePinnacle });
                }
                finally
                {
                    KnotDrops.PatternFactory = oPattern;
                }

                var rows = AnalyticsManager.DrainKnotQueueForTests();
                var fibers = rows.Where(r => r.Kind == "fiber_roll").ToList();

                Assert.AreEqual(2, fibers.Count, "one fiber_roll per seat");
                CollectionAssert.AreEqual(new[] { run.OwnerGuid, SeatA }, fibers.Select(r => r.CharacterId).ToList());
                CollectionAssert.AreEqual(new[] { "Owner", $"Seat{SeatA:X}" }, fibers.Select(r => r.Name).ToList());
                CollectionAssert.AreEqual(new[] { 7u, 8u }, fibers.Select(r => r.AccountId).ToList());
                Assert.IsTrue(fibers.All(r => r.Result == "dropped" && r.KnotLevel == 233 && r.MissesBefore == 3 && r.MissesAfter == 0 && r.ItemGuid.HasValue));
                Assert.AreEqual(200, fibers[0].CharLevel, "the owner seat falls back to the run's start level");
                Assert.IsNull(fibers[1].CharLevel, "an offline non-owner seat has no level");
                var patterns = rows.Where(r => r.Kind == "pattern_roll").ToList();
                Assert.AreEqual(2, patterns.Count, "one pattern_roll per seat");
                CollectionAssert.AreEqual(fibers.Select(r => r.CharacterId).ToList(), patterns.Select(r => r.CharacterId).ToList());
                Assert.IsTrue(patterns.All(r => r.Result == "dropped" && r.PinnacleKey == OnePinnacle.Key && r.EnabledPinnacles == 1 && r.ItemGuid.HasValue));
                Assert.IsTrue(fibers.All(r => r.StartGroup == run.StartGroup && r.DungeonId == "filos_doom" && r.InstanceId == run.RunId));
            }
            finally
            {
                AnalyticsManager.SetEnabledForTests(false);
                AnalyticsManager.ResetKnotQueueForTests();
            }
        }

        [TestMethod]
        public void Solo_RollsExactlyOneSeat_TheOwner_WithOneDraw()
        {
            var run = NewRun();

            var log = RunSeats(run, true, _ => true, _ => 2, new Queue<double>(new[] { 0.0 }));

            CollectionAssert.AreEqual(new[] { run.OwnerGuid }, log.Opened);
            Assert.AreEqual(1, log.Draws, "one Fiber draw, no Pattern draw with no enabled pinnacle");
            Assert.AreEqual(1, FibersIn(run, run.OwnerGuid));
            Assert.AreEqual(0, log.Written[run.OwnerGuid], "a delivered drop resets the counter");
        }

        [TestMethod]
        public void Group_ThreeEligibleSeats_RollIndependently_EachIntoItsOwnPileAndCounter()
        {
            var run = NewGroupRun(SeatA, SeatB);
            run.MarkMemberEntered(SeatA);
            run.MarkMemberEntered(SeatB);
            var owner = run.OwnerGuid;

            var misses = new Dictionary<uint, int> { { owner, 0 }, { SeatA, 3 }, { SeatB, 5 } };

            // Owner drops (0.0 < 0.20), SeatA is dry (0.99 >= 0.35), SeatB drops (0.10 < 0.45).
            var log = RunSeats(run, true, _ => true, g => misses[g], new Queue<double>(new[] { 0.0, 0.99, 0.10 }));

            CollectionAssert.AreEqual(new[] { owner, SeatA, SeatB }, log.Opened);
            Assert.AreEqual(3, log.Draws);

            Assert.AreEqual(1, FibersIn(run, owner));
            Assert.AreEqual(0, FibersIn(run, SeatA), "a dry seat gets no Fiber");
            Assert.AreEqual(1, FibersIn(run, SeatB));

            Assert.AreEqual(0, log.Written[owner]);
            Assert.AreEqual(4, log.Written[SeatA], "the dry seat's own counter moves up from ITS 3");
            Assert.AreEqual(0, log.Written[SeatB]);
        }

        [TestMethod]
        public void Group_IneligibleMembers_GetNoRoll_AndTheirCounterIsUntouched()
        {
            var run = NewGroupRun(SeatA, SeatB, SeatC);
            run.MarkMemberEntered(SeatB);
            run.MarkMemberEntered(SeatC);
            run.MarkPuzzleRemoved(SeatC);
            var owner = run.OwnerGuid;

            // SeatA never entered; SeatB entered but is offline; SeatC entered and online but puzzle-removed.
            var log = RunSeats(run, true, g => g != SeatB, _ => 0, new Queue<double>(new[] { 0.0, 0.0, 0.0 }));

            CollectionAssert.AreEqual(new[] { owner }, log.Opened);
            CollectionAssert.AreEquivalent(new[] { owner }, log.Written.Keys.ToList());
            Assert.AreEqual(1, log.Draws);
            Assert.IsFalse(run.HasHeldPile(SeatA) || run.HasHeldPile(SeatB) || run.HasHeldPile(SeatC));
        }

        [TestMethod]
        public void OfflineOwner_StillRolls_AsToday()
        {
            var run = NewGroupRun(SeatA);
            run.MarkMemberEntered(SeatA);

            var log = RunSeats(run, true, g => g != run.OwnerGuid, _ => 0, new Queue<double>(new[] { 0.0, 0.0 }));

            CollectionAssert.AreEqual(new[] { run.OwnerGuid, SeatA }, log.Opened);
        }

        [TestMethod]
        public void OneSeatThrowing_DoesNotCostTheOthersTheirRolls()
        {
            var run = NewGroupRun(SeatA, SeatB);
            run.MarkMemberEntered(SeatA);
            run.MarkMemberEntered(SeatB);

            var log = RunSeats(run, true, _ => true, _ => 0, new Queue<double>(new[] { 0.0, 0.0 }), openThrows: g => g == SeatA);

            CollectionAssert.AreEqual(new[] { run.OwnerGuid, SeatA, SeatB }, log.Opened);
            Assert.AreEqual(1, FibersIn(run, run.OwnerGuid));
            Assert.AreEqual(1, FibersIn(run, SeatB));
            Assert.IsFalse(log.Written.ContainsKey(SeatA));
        }

        [TestMethod]
        public void OneSeatsFailedCreate_LeavesThatCounterAlone_AndTheOthersRoll()
        {
            var run = NewGroupRun(SeatA);
            run.MarkMemberEntered(SeatA);
            var calls = 0;

            // The owner's Fiber fails to create; SeatA's succeeds.
            var log = RunSeats(run, true, _ => true, _ => 4, new Queue<double>(new[] { 0.0, 0.0 }),
                fiberFactory: _ => ++calls == 1 ? null : KnotItem(KnotItemKind.Fiber, WeenieType.Gem));

            Assert.IsFalse(log.Written.ContainsKey(run.OwnerGuid), "a drop that never reached the pile keeps the pity");
            Assert.AreEqual(0, FibersIn(run, run.OwnerGuid));
            Assert.AreEqual(1, FibersIn(run, SeatA));
            Assert.AreEqual(0, log.Written[SeatA]);
        }

        [TestMethod]
        public void KillSwitchOff_RollsTheOwnerOnly_EvenInAGroup()
        {
            var run = NewGroupRun(SeatA, SeatB);
            run.MarkMemberEntered(SeatA);
            run.MarkMemberEntered(SeatB);

            var log = RunSeats(run, false, _ => true, _ => 0, new Queue<double>(new[] { 0.0 }));

            CollectionAssert.AreEqual(new[] { run.OwnerGuid }, log.Opened);
            Assert.AreEqual(1, log.Draws);
            Assert.IsFalse(run.HasHeldPile(SeatA) || run.HasHeldPile(SeatB));
        }

        [TestMethod]
        public void SeatEligibility_IsTheSurveyPredicate_NotACopy()
        {
            var src = RepoFile(Path.Combine("Source", "ACE.Server", "Knots", "KnotDrops.cs")).Replace("\r\n", "\n");
            StringAssert.Contains(src, "ThreadDungeonManager.SurveyRecipients(run.SnapshotRoster(), isOnline)");

            var start = src.IndexOf("internal static IReadOnlyList<uint> Seats", StringComparison.Ordinal);
            var end = src.IndexOf("internal static", start + 10, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start);

            var body = src.Substring(start, end - start);
            Assert.IsFalse(body.Contains(".PuzzleRemoved"), "no second copy of the eligibility predicate");
            Assert.IsFalse(body.Contains(".Entered"), "no second copy of the eligibility predicate");
        }

        [TestMethod]
        public void RollFor_PassesTheLiveTunable_ToRollSeats()
        {
            var src = RepoFile(Path.Combine("Source", "ACE.Server", "Knots", "KnotDrops.cs")).Replace("\r\n", "\n");
            StringAssert.Contains(src, "RollSeats(run, level.Value, KnotTunables.FiberPerSeat,");
        }

        private static WorldObject PatternItem() => KnotItem(KnotItemKind.Pattern);

        private static readonly PinnacleDef OnePinnacle = new PinnacleDef { Key = "p1", Name = "P One" };

        [TestMethod]
        public void Group_PatternLandsOnlyInTheSeatThatRolledIt()
        {
            var run = NewGroupRun(SeatA, SeatB);
            run.MarkMemberEntered(SeatA);
            run.MarkMemberEntered(SeatB);

            var oPattern = KnotDrops.PatternFactory;
            KnotDrops.PatternFactory = _ => PatternItem();

            try
            {
                // Per seat: Fiber draw then Pattern draw. Only the middle seat's (SeatA) Pattern draw (0.0 < 0.03) hits.
                var log = RunSeats(run, true, _ => true, _ => 0, new Queue<double>(new[] { 0.99, 0.99, 0.99, 0.0, 0.99, 0.99 }),
                    enabled: new List<PinnacleDef> { OnePinnacle });

                Assert.AreEqual(6, log.Draws);
            }
            finally
            {
                KnotDrops.PatternFactory = oPattern;
            }

            Assert.IsFalse(run.HeldPileContains(run.OwnerGuid, KnotItems.IsPattern), "owner's Pattern draw missed");
            Assert.IsTrue(run.HeldPileContains(SeatA, KnotItems.IsPattern), "the 4th draw (SeatA's Pattern) hit");
            Assert.IsFalse(run.HeldPileContains(SeatB, KnotItems.IsPattern), "SeatB's Pattern draw missed");
        }

        [TestMethod]
        public void Solo_DrawOrderIsFiber_ThenPattern_ThenPick()
        {
            var run = NewRun();
            var order = new List<string>();

            var oDraw = KnotDrops.Draw;
            var oPick = KnotDrops.Pick;
            var oOpen = KnotDrops.OpenSeat;
            var oFiber = KnotDrops.FiberFactory;
            var oPattern = KnotDrops.PatternFactory;

            KnotDrops.Draw = () => { order.Add("draw"); return 0.0; };
            KnotDrops.Pick = _ => { order.Add("pick"); return 0; };
            KnotDrops.OpenSeat = (r, g) => (0, _ => { });
            KnotDrops.FiberFactory = _ => KnotItem(KnotItemKind.Fiber, WeenieType.Gem);
            KnotDrops.PatternFactory = _ => PatternItem();

            try
            {
                KnotDrops.RollSeats(run, 233, true, 0.20, 0.05, 0.03, new List<PinnacleDef> { OnePinnacle }, _ => true);
            }
            finally
            {
                KnotDrops.Draw = oDraw;
                KnotDrops.Pick = oPick;
                KnotDrops.OpenSeat = oOpen;
                KnotDrops.FiberFactory = oFiber;
                KnotDrops.PatternFactory = oPattern;
            }

            CollectionAssert.AreEqual(new[] { "draw", "draw", "pick" }, order, "Fiber draw, Pattern draw, then the pick");
            Assert.IsTrue(run.HeldPileContains(run.OwnerGuid, KnotItems.IsPattern));
        }
        private static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.Fail($"{relative} not found by walking up from the test assembly");
            return null;
        }
    }
}

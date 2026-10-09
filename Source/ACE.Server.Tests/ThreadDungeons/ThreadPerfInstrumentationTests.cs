using System;
using System.Linq;

using ACE.Server.Factories.Enum;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads performance work of 2026-09-18 that is not the step budget itself (ThreadCacheBatchingTests owns
    /// that): the loot weenie warm-up's wcid set, the per-run perf counters and their summary line, and the live
    /// wiring that cannot run under this harness, pinned on source.
    ///
    /// No PropertyManager key is read by any path driven here: ThreadLootWeenieWarmup.Collect takes its tier cap as an
    /// argument, and Start (which reads the tunables) is only pinned on source.
    /// </summary>
    [TestClass]
    public class ThreadPerfInstrumentationTests
    {
        // ------------------------------------------------------------------------------------------------------
        // Loot weenie warm-up
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_warm_set_covers_the_factory_tables_the_inline_wcids_and_the_delivery_wcids()
        {
            var extra = ThreadLootWeenieWarmup.DeliveryWcids(new uint[] { 20983, 0 }, new uint[] { 0, 21086 }).ToList();
            var set = ThreadLootWeenieWarmup.Collect(ThreadLootWeenieWarmup.MaxTier, extra);

            Console.WriteLine($"warm set at tier {ThreadLootWeenieWarmup.MaxTier}: {set.Wcids.Count} wcids from {set.Included.Count} included fields; {set.Excluded.Count} excluded");
            Console.WriteLine("included: " + string.Join(", ", set.Included));
            Console.WriteLine("excluded: " + string.Join(", ", set.Excluded));

            // RollWcid's two inline wcids.
            Assert.IsTrue(set.Wcids.Contains((uint)WeenieClassName.coinstack));
            Assert.IsTrue(set.Wcids.Contains((uint)WeenieClassName.ace49485_encapsulatedspirit));

            // GemMaterialChance (one namespace up from the Wcids tables, carried as GemResult rather than a bare wcid).
            Assert.IsTrue(set.Wcids.Contains((uint)WeenieClassName.gemagate));
            Assert.IsTrue(set.Wcids.Contains((uint)WeenieClassName.jeweldiamond));

            // The delivery path's own wcids; zeros dropped.
            Assert.IsTrue(set.Wcids.Contains(ThreadDungeonRewardSpawner.BossCacheWcid));
            Assert.IsTrue(set.Wcids.Contains(ThreadDungeonRewardSpawner.TradeNoteWcid));
            Assert.IsTrue(set.Wcids.Contains(ThreadDungeonRewardSpawner.SummonedExitWcid));
            Assert.IsTrue(set.Wcids.Contains(20983u) && set.Wcids.Contains(21086u));
            Assert.IsFalse(set.Wcids.Contains(0u));

            // Tables from both namespaces and the Weapons subfolders were walked.
            Assert.IsTrue(set.Included.Any(f => f.StartsWith("AetheriaWcids.", StringComparison.Ordinal)));
            Assert.IsTrue(set.Included.Any(f => f.StartsWith("GemMaterialChance.", StringComparison.Ordinal)));
            Assert.IsTrue(set.Included.Any(f => f.StartsWith("JewelryWcids.", StringComparison.Ordinal)));
            Assert.IsTrue(set.Included.Any(f => f.StartsWith("SwordWcids_Aluvian.", StringComparison.Ordinal)), "the Weapons/Legacy subfolder");
            Assert.IsTrue(set.Included.Any(f => f.StartsWith("ArmorWcids.", StringComparison.Ordinal)));

            // The derived all-tier indexes are skipped by name.
            Assert.IsTrue(set.Excluded.Any(f => f.StartsWith("ArmorWcids._combined", StringComparison.Ordinal)));
            Assert.IsFalse(set.Included.Any(f => f.Contains("._combined")));

            // Scrolls are excluded whole (see Collect's doc): the spell lookup that precedes the wcid is not warmable here.
            Assert.IsTrue(set.Excluded.Any(f => f.StartsWith("ScrollWcids.", StringComparison.Ordinal)));
            Assert.IsFalse(set.Included.Any(f => f.StartsWith("ScrollWcids.", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void A_lower_tier_cap_warms_a_strict_subset()
        {
            var full = ThreadLootWeenieWarmup.Collect(ThreadLootWeenieWarmup.MaxTier, null).Wcids;
            var tier1 = ThreadLootWeenieWarmup.Collect(1, null).Wcids;

            Console.WriteLine($"tier 1: {tier1.Count} wcids; tier {ThreadLootWeenieWarmup.MaxTier}: {full.Count}");

            Assert.IsTrue(tier1.IsSubsetOf(full));
            Assert.IsTrue(tier1.Count < full.Count, "the tier-indexed tables contribute only tiers 1..cap");
            Assert.IsTrue(ThreadLootWeenieWarmup.Collect(0, null).Wcids.SetEquals(tier1), "a cap below 1 reads as 1");
            Assert.IsTrue(ThreadLootWeenieWarmup.Collect(99, null).Wcids.SetEquals(full), "a cap above 8 reads as 8");
        }

        [TestMethod]
        public void The_warm_up_runs_after_the_store_loads_and_snapshots_its_plain_dictionary_inputs()
        {
            var program = PooledLootSourceText.Read("Source/ACE.Server/Program.cs");
            var init = program.IndexOf("ACE.Server.ThreadDungeons.ThreadDungeonManager.Initialize();", StringComparison.Ordinal);
            var warm = program.IndexOf("ACE.Server.ThreadDungeons.ThreadLootWeenieWarmup.Start();", StringComparison.Ordinal);
            Assert.IsTrue(init >= 0 && warm > init, "started after ThreadDungeonManager.Initialize, so the store's salvage-affinity wcids exist");

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootWeenieWarmup.cs");
            var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            // Since 2026-09-19 the task DOES warm the scroll path, which is what made scrollsBySpellID a
            // ConcurrentDictionary; ThreadScrollWarmupTests owns that half.
            StringAssert.Contains(code, "GetScrollWeenie", "the scroll path is warmed by the route the factory uses");
            StringAssert.Contains(code, "WorldDatabasePrecaching");
            StringAssert.Contains(code, "dynamic_dungeons_loot_weenie_warmup");
            StringAssert.Contains(code, "Task.Run(");

            // The plain-Dictionary inputs are read BEFORE the task starts, on the calling thread.
            var start = PooledLootSourceText.MethodBody(src, "public static Task Start()");
            var salvage = start.IndexOf("Player.MaterialSalvage", StringComparison.Ordinal);
            var modifiers = start.IndexOf("ThreadDungeonManager.Store?.Modifiers", StringComparison.Ordinal);
            var run = start.IndexOf("Task.Run(", StringComparison.Ordinal);
            Assert.IsTrue(salvage >= 0 && modifiers >= 0 && run > salvage && run > modifiers);
            StringAssert.Contains(start, ".ToList();", "snapshotted, not handed to the task as a lazy query");
        }

        [TestMethod]
        public void A_weenie_cache_miss_is_counted_process_wide_and_per_thread()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Database/WorldDatabaseWithEntityCache.cs");
            var body = PooledLootSourceText.MethodBody(src, "public ACE.Entity.Models.Weenie GetCachedWeenie(uint weenieClassId)");

            var hit = body.IndexOf("return value;", StringComparison.Ordinal);
            var total = body.IndexOf("System.Threading.Interlocked.Increment(ref weenieCacheMissTotal);", StringComparison.Ordinal);
            var thread = body.IndexOf("weenieCacheMissesOnThread++;", StringComparison.Ordinal);
            var load = body.IndexOf("GetWeenie(weenieClassId);", StringComparison.Ordinal);

            Assert.IsTrue(hit >= 0 && total > hit && thread > hit && load > total && load > thread, "counted only on the miss path, before the DB read");
            StringAssert.Contains(src, "[ThreadStatic]");
        }

        // ------------------------------------------------------------------------------------------------------
        // Per-run perf counters and the summary line
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Perf_stats_sum_steps_and_keep_the_single_slowest_step()
        {
            var perf = new ThreadRunPerfStats();

            Assert.AreEqual(-1, perf.PlanMs, "unset until a plan is built");

            perf.RecordLootStep(5, 1);
            perf.RecordLootStep(9, 2);
            perf.RecordLootStep(3, -4);

            Assert.AreEqual(3, perf.LootSteps);
            Assert.AreEqual(17, perf.LootTotalMs);
            Assert.AreEqual(9, perf.LootMaxStepMs);
            Assert.AreEqual(3, perf.WeenieMisses, "a negative miss delta is never subtracted");

            perf.RecordPlaceStep(4);
            perf.RecordPlaceStep(6);
            perf.RecordPlan(12, 180);

            Assert.AreEqual(10, perf.PlaceMs);
            Assert.AreEqual(2, perf.PlaceSteps);
            Assert.AreEqual(12, perf.PlanMs);
            Assert.AreEqual(180, perf.BandLow);
        }

        [TestMethod]
        public void The_run_summary_line_carries_every_field_the_log_reader_needs()
        {
            var run = PooledRun();
            run.Perf.RecordPlan(12, 180);
            run.Perf.RecordPlaceStep(40);
            run.Perf.RecordLootChain();
            run.Perf.RecordLootStep(9, 3);
            Assert.IsTrue(run.MarkEnded("test"));

            var line = ThreadDungeonManager.RunSummaryLine(run);

            foreach (var field in new[] { "[DYNDUNGEON] run summary", "run=0x80001234", "dungeon=filos_doom", "level=200", "bandLow=180", "seats=1", "group=False",
                "placed=0/0", "kills=0", "planMs=12", "placeMs=40", "placeSteps=1", "lootChains=1", "lootSteps=1", "lootTotalMs=9", "lootMaxStepMs=9",
                "weenieMiss=3", "pooledLeft=0", "buffered=0", "end=test" })
            {
                StringAssert.Contains(line, field);
            }
        }

        [TestMethod]
        public void EndRun_logs_the_summary_at_Info_and_the_per_kill_line_is_Debug()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

            var endRun = PooledLootSourceText.MethodBody(manager, "public static void EndRun(ThreadDungeonRun run, string why)");
            StringAssert.Contains(endRun, "log.Info(RunSummaryLine(run, drained));");
            StringAssert.Contains(endRun, "UnclaimedPooledLootLine(run, drained)");

            var progress = PooledLootSourceText.MethodBody(manager, "private static void AnnounceProgress(ThreadDungeonRun run)");
            Assert.IsFalse(progress.Contains("log.Info("), "one line per kill is Debug now");
            StringAssert.Contains(progress, "log.Debug($\"[DYNDUNGEON] progress");
        }

        [TestMethod]
        public void The_step_budget_tunable_is_read_on_the_world_thread_and_stamped_before_publication()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

            var read = manager.IndexOf("PropertyManager.GetLong(\"dynamic_dungeons_cache_step_budget_ms\", ThreadLootRollBudget.DefaultStepBudgetMs)", StringComparison.Ordinal);
            var clamp = manager.IndexOf("CacheStepBudgetMsMin, CacheStepBudgetMsMax", read, StringComparison.Ordinal);
            var stamp = manager.IndexOf("CacheStepBudgetMs = cacheStepBudgetMs,", StringComparison.Ordinal);
            var publish = manager.IndexOf("runs.TryAdd(instance, run)", stamp, StringComparison.Ordinal);

            Assert.IsTrue(read >= 0 && clamp > read && stamp > clamp && publish > stamp);
            Assert.AreEqual(1L, ThreadDungeonManager.CacheStepBudgetMsMin);
            Assert.AreEqual(250L, ThreadDungeonManager.CacheStepBudgetMsMax);
            Assert.AreEqual(ThreadLootRollBudget.DefaultStepBudgetMs, new ThreadDungeonRun(0x80000001u, 0x50000001u, "T", 7, 0x80000099u,
                NewRun().Spec, NewRun().Dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(5)).CacheStepBudgetMs, "an unstamped run gets the shipped default");
        }
    }
}

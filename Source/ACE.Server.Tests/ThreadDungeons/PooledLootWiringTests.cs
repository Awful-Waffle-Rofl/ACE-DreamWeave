using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Which loot model a death and a boss death take, and where delivery is requested. The decisions are
    /// driven through their seams; Creature.Die, OnRunCreatureDied, Tick and EndRun need a live world, so
    /// their ORDER is pinned on source.
    /// </summary>
    [TestClass]
    public class PooledLootWiringTests
    {
        [TestMethod]
        public void Pooled_loot_death_needs_both_run_keys_and_an_on_stamp()
        {
            Assert.IsFalse(Creature.IsPooledLootDeath(null, 5), "no back-reference");
            Assert.IsFalse(Creature.IsPooledLootDeath(NewRun(true), null), "no persisted stamp");
            Assert.IsFalse(Creature.IsPooledLootDeath(NewRun(null), 5), "an unstamped run is the corpse model");
            Assert.IsFalse(Creature.IsPooledLootDeath(NewRun(false), 5), "switch OFF keeps the corpse");
            Assert.IsTrue(Creature.IsPooledLootDeath(NewRun(true), 5));
        }

        [TestMethod]
        public void Switch_off_boss_death_spawns_the_boss_cache_and_pools_nothing()
        {
            foreach (var pooled in new bool?[] { null, false })
            {
                var run = NewRun(pooled);
                var caches = 0;
                var bonuses = 0;
                var originalCache = ThreadDungeonManager.BossCacheSpawner;
                var originalMarker = ThreadDungeonManager.PooledBonusMarker;
                ThreadDungeonManager.BossCacheSpawner = _ => caches++;
                ThreadDungeonManager.PooledBonusMarker = _ => bonuses++;

                try
                {
                    ThreadDungeonManager.DispatchBossDeath(run);
                }
                finally
                {
                    ThreadDungeonManager.BossCacheSpawner = originalCache;
                    ThreadDungeonManager.PooledBonusMarker = originalMarker;
                }

                Assert.AreEqual(1, caches, $"stamp {pooled}");
                Assert.AreEqual(0, bonuses, $"stamp {pooled}");
            }
        }

        [TestMethod]
        public void Switch_on_boss_death_pools_the_bonus_and_spawns_no_cache()
        {
            var run = NewRun(true);
            var caches = 0;
            var bonuses = 0;
            var originalCache = ThreadDungeonManager.BossCacheSpawner;
            var originalMarker = ThreadDungeonManager.PooledBonusMarker;
            ThreadDungeonManager.BossCacheSpawner = _ => caches++;
            ThreadDungeonManager.PooledBonusMarker = _ => bonuses++;

            try
            {
                ThreadDungeonManager.DispatchBossDeath(run);
            }
            finally
            {
                ThreadDungeonManager.BossCacheSpawner = originalCache;
                ThreadDungeonManager.PooledBonusMarker = originalMarker;
            }

            Assert.AreEqual(0, caches);
            Assert.AreEqual(1, bonuses);
        }

        [TestMethod]
        public void Pooled_bonus_is_always_marked_and_the_boss_chest_switch_governs_only_the_off_path()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var marker = PooledLootSourceText.MethodBody(manager, "private static void MarkPooledBonus(ThreadDungeonRun run)");

            StringAssert.Contains(marker, "run.TryMarkLootBonusPending()");
            Assert.IsFalse(marker.Contains("dynamic_dungeons_boss_chest_enabled"), "user ruling 2026-09-14: the pooled bonus is always added");
            Assert.IsFalse(marker.Contains("PropertyManager"), "no switch read at all on the pooled bonus path");

            var spawner = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");
            var boss = PooledLootSourceText.MethodBody(spawner, "public static void TrySpawnBossCache(ThreadDungeonRun run)");
            StringAssert.Contains(boss, "dynamic_dungeons_boss_chest_enabled", "the OFF path keeps its switch");
        }

        [TestMethod]
        public void A_populated_pooled_run_asks_for_delivery_and_an_unpooled_one_does_not()
        {
            foreach (var pooled in new[] { true, false })
            {
                var run = NewRun(pooled);
                run.MarkPopulated(0, 0, 0);

                var triggers = 0;
                var originalTrigger = ThreadDungeonManager.PooledLootTrigger;
                var originalGem = ThreadDungeonManager.GemDestroyer;
                var originalSurvey = ThreadDungeonManager.SurveyRecorder;
                ThreadDungeonManager.PooledLootTrigger = _ => { triggers++; return CacheRequestOutcome.Queued; };
                ThreadDungeonManager.GemDestroyer = _ => { };
                ThreadDungeonManager.SurveyRecorder = _ => { };

                try
                {
                    ThreadDungeonManager.OnRunPopulated(run);
                }
                finally
                {
                    ThreadDungeonManager.PooledLootTrigger = originalTrigger;
                    ThreadDungeonManager.GemDestroyer = originalGem;
                    ThreadDungeonManager.SurveyRecorder = originalSurvey;
                }

                Assert.AreEqual(pooled ? 1 : 0, triggers, $"pooled {pooled}");
            }
        }

        [TestMethod]
        public void Die_banks_before_the_kill_hook_and_skips_only_the_corpse()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");
            var body = PooledLootSourceText.MethodBody(src, "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)");

            var decide = body.IndexOf("var pooledLoot = IsPooledLootDeath(P_DungeonRun, GetProperty(PropertyInt.ThreadDungeonRunId));", StringComparison.Ordinal);
            var bank = body.IndexOf("ACE.Server.ThreadDungeons.ThreadLootPool.BankKill(this, topDamager);", StringComparison.Ordinal);
            var hook = body.IndexOf("ACE.Server.ThreadDungeons.ThreadDungeonManager.OnRunCreatureDied(this, lastDamager, topDamager);", StringComparison.Ordinal);
            var delay = body.IndexOf("dieChain.AddDelaySeconds(deathAnimLength);", StringComparison.Ordinal);
            // The corpse guard is no longer keyed on pooledLoot alone: ML digsite encounters OR a second
            // predicate into the SAME local, so this one call site now serves both systems. Only the literal
            // moved; every ordering and exclusivity claim below is unchanged, and the NoCorpse assertion
            // still covers the digsite path because it scans the whole method body.
            var skip = body.IndexOf("if (!suppressCorpse)", StringComparison.Ordinal);
            var corpse = body.IndexOf("CreateCorpse(topDamager);", StringComparison.Ordinal);
            var destroy = body.IndexOf("Destroy();", corpse, StringComparison.Ordinal);

            Assert.IsTrue(decide >= 0 && decide < bank && bank < hook, "bank before OnRunCreatureDied so the clearing kill is in the ledger");

            // A throw from BankKill must not abort Die() after dieEntered: the call sits in a try whose catch closes
            // before the run kill hook, so the kill is still recorded and the creature still destroyed.
            var guard = body.IndexOf("try", decide, bank - decide, StringComparison.Ordinal);
            var guardCatch = body.IndexOf("catch (Exception", bank, StringComparison.Ordinal);

            Assert.IsTrue(guard > decide && body.IndexOf("{", guard, bank - guard, StringComparison.Ordinal) >= 0, "BankKill sits inside a try block");
            Assert.IsTrue(guardCatch > bank && guardCatch < hook, "the BankKill catch closes before OnRunCreatureDied");
            Assert.IsTrue(delay < skip && skip < corpse && corpse < destroy, "Destroy still runs after the death-animation delay");
            Assert.IsFalse(body.Substring(skip, corpse - skip).Contains("{"), "only CreateCorpse is conditional, never Destroy");
            Assert.IsFalse(body.Contains("NoCorpse"), "spec section 2: pooled mode must not use NoCorpse");
        }

        [TestMethod]
        public void Manager_dispatches_boss_deaths_and_triggers_after_the_clear_line()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

            var died = PooledLootSourceText.MethodBody(src, "public static void OnRunCreatureDied(Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager = null, ACE.Server.Entity.DamageHistoryInfo topDamager = null)");
            var record = died.IndexOf("run.RecordKill(", StringComparison.Ordinal);
            var dispatch = died.IndexOf("DispatchBossDeath(run);", StringComparison.Ordinal);
            var cleared = died.IndexOf("AnnounceCleared(run);", StringComparison.Ordinal);
            var trigger = died.IndexOf("PooledLootTrigger(run);", StringComparison.Ordinal);
            var progress = died.IndexOf("AnnounceProgress(run);", StringComparison.Ordinal);

            Assert.IsTrue(record >= 0 && record < dispatch && dispatch < cleared && cleared < trigger && trigger < progress);
            Assert.IsFalse(died.Contains("BossCacheSpawner(run)"), "the boss cache is reached only through DispatchBossDeath");

            var populated = PooledLootSourceText.MethodBody(src, "public static void OnRunPopulated(ThreadDungeonRun run)");
            Assert.IsTrue(populated.IndexOf("AnnounceCleared(run);", StringComparison.Ordinal) < populated.IndexOf("PooledLootTrigger(run);", StringComparison.Ordinal));
        }

        [TestMethod]
        public void Tick_retries_a_waiting_delivery_before_populating()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var tick = PooledLootSourceText.MethodBody(src, "public static void Tick()");

            var wanted = tick.IndexOf("run.IsPlacementWanted", StringComparison.Ordinal);
            var inside = tick.IndexOf("ThreadCachePlacer.IsOwnerInside(run, PlayerManager.GetOnlinePlayer(run.OwnerGuid))", StringComparison.Ordinal);
            var retry = tick.IndexOf("RetryWaitingDelivery(run);", StringComparison.Ordinal);
            var populate = tick.IndexOf("ThreadDungeonSpawner.TryPopulate(run, landblock);", StringComparison.Ordinal);

            Assert.IsTrue(wanted >= 0 && wanted < inside && inside < retry && retry < populate);

            // The claim and the trigger live in RetryWaitingDelivery, whose behaviour is driven below.
            var helper = PooledLootSourceText.MethodBody(src, "internal static void RetryWaitingDelivery(ThreadDungeonRun run)");
            var claim = helper.IndexOf("run.TryClaimPlacementWanted()", StringComparison.Ordinal);
            var trigger = helper.IndexOf("PooledLootTrigger(run)", StringComparison.Ordinal);

            Assert.IsTrue(claim >= 0 && claim < trigger);
        }

        [TestMethod]
        public void A_retry_that_finds_delivery_busy_keeps_the_delivery_waiting()
        {
            foreach (var outcome in new[] { CacheRequestOutcome.Busy, CacheRequestOutcome.Queued, CacheRequestOutcome.NotApplicable })
            {
                var run = NewRun(true);
                run.MarkPlacementWanted();

                var triggers = 0;
                var originalTrigger = ThreadDungeonManager.PooledLootTrigger;
                ThreadDungeonManager.PooledLootTrigger = _ => { triggers++; return outcome; };

                try
                {
                    ThreadDungeonManager.RetryWaitingDelivery(run);
                }
                finally
                {
                    ThreadDungeonManager.PooledLootTrigger = originalTrigger;
                }

                Assert.AreEqual(1, triggers, $"outcome {outcome}");

                // Task 8 review caller contract: a Busy outcome ran nothing, so the claimed flag must be put back
                // or the waiting delivery is lost. Queued hands the wait to Execute, which re-marks it itself if
                // the owner has left again; NotApplicable has nothing to deliver.
                Assert.AreEqual(outcome == CacheRequestOutcome.Busy, run.IsPlacementWanted, $"outcome {outcome}");
            }
        }

        [TestMethod]
        public void A_retry_with_nothing_waiting_requests_nothing()
        {
            var run = NewRun(true);

            var triggers = 0;
            var originalTrigger = ThreadDungeonManager.PooledLootTrigger;
            ThreadDungeonManager.PooledLootTrigger = _ => { triggers++; return CacheRequestOutcome.Busy; };

            try
            {
                ThreadDungeonManager.RetryWaitingDelivery(run);
            }
            finally
            {
                ThreadDungeonManager.PooledLootTrigger = originalTrigger;
            }

            Assert.AreEqual(0, triggers);
            Assert.IsFalse(run.IsPlacementWanted);
        }

        [TestMethod]
        public void EndRun_disposes_pooled_loot_once_after_the_ended_latch()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var end = PooledLootSourceText.MethodBody(src, "public static void EndRun(ThreadDungeonRun run, string why)");

            var latch = end.IndexOf("run.MarkEnded(why, out var priorState)", StringComparison.Ordinal);
            var dispose = end.IndexOf("ThreadLootPool.DisposeForRunEnd(run)", StringComparison.Ordinal);
            var evict = end.IndexOf("GetAllWorldObjectsForDiagnostics()", StringComparison.Ordinal);

            Assert.IsTrue(latch >= 0 && latch < dispose && dispose < evict);
        }
    }
}

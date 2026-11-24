using System;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Shrine guardians (CreatureShapeOptions.ExcludedFromClear -> Creature.IsThreadBonusCreature): a guardian's death is a
    /// bonus kill. It never reaches RecordKill, so killed, the clear target and the clear progress do not move, and the
    /// kill hook returns before the armed notice and the progress line. Its pooled loot still banks, and a cleared pooled
    /// run still asks for delivery. OnRunCreatureDied needs a registered live run, so the route is driven through
    /// ThreadDungeonManager.TryRecordBonusKill and its place in the hook is pinned on the source. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadBonusKillTests
    {
        private static (int Killed, int Target, double Progress, int Spawned, int Planned, ThreadDungeonRunState State) Snapshot(ThreadDungeonRun run)
            => (run.TrashKilled, run.ClearTarget, run.ClearProgress, run.Spawned, run.Planned, run.State);

        [TestMethod]
        public void A_flagged_kill_counts_as_a_bonus_kill_and_leaves_the_clear_untouched()
        {
            var triggered = 0;
            var saved = ThreadDungeonManager.PooledLootTrigger;

            try
            {
                ThreadDungeonManager.PooledLootTrigger = _ => { triggered++; return CacheRequestOutcome.Busy; };

                var run = Run(pooled: true);
                var before = Snapshot(run);

                for (var i = 0; i < 3; i++)
                    Assert.IsTrue(ThreadDungeonManager.TryRecordBonusKill(run, RunCreature(run, bonus: true)));

                Assert.AreEqual(before, Snapshot(run), "killed, clear target, progress, spawned, planned and state unchanged");
                Assert.AreEqual(3, run.BonusKills);
                Assert.AreEqual(3, triggered, "a pooled run still asks for delivery on every later kill (a no-op until Cleared)");

                // Control: an unflagged creature is not a bonus kill, and a real kill does move the clear.
                Assert.IsFalse(ThreadDungeonManager.TryRecordBonusKill(run, RunCreature(run, bonus: false)));
                run.RecordKill(false);
                Assert.AreEqual(before.Killed + 1, run.TrashKilled, "control: RecordKill moves killed");
                Assert.AreEqual(3, run.BonusKills);
            }
            finally
            {
                ThreadDungeonManager.PooledLootTrigger = saved;
            }
        }

        [TestMethod]
        public void A_non_pooled_bonus_kill_asks_for_no_delivery()
        {
            var saved = ThreadDungeonManager.PooledLootTrigger;

            try
            {
                ThreadDungeonManager.PooledLootTrigger = _ => throw new AssertFailedException("not a pooled run");

                var run = Run(pooled: false);
                Assert.IsTrue(ThreadDungeonManager.TryRecordBonusKill(run, RunCreature(run, bonus: true)));
                Assert.AreEqual(1, run.BonusKills);
            }
            finally
            {
                ThreadDungeonManager.PooledLootTrigger = saved;
            }
        }

        [TestMethod]
        public void A_flagged_kill_still_banks_its_pooled_loot()
        {
            var savedBoon = ThreadLootPool.KillerLootBoon;

            try
            {
                ThreadLootPool.KillerLootBoon = (_, __) => 1.0;
                ThreadLootPool.HeldRareRoller = (_, __) => null;

                var run = Run(pooled: true);
                Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run, bonus: true), new DamageHistoryInfo(Named(Owner, "Owner"))));
                Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));
                Assert.AreEqual(1, entry.Rolls);
            }
            finally
            {
                ThreadLootPool.KillerLootBoon = savedBoon;
                ThreadLootPool.HeldRareRoller = null;
            }
        }

        [TestMethod]
        public void The_kill_hook_routes_bonus_kills_before_RecordKill_and_never_reaches_the_progress_line()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var died = PooledLootSourceText.MethodBody(src, "public static void OnRunCreatureDied(Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager = null, ACE.Server.Entity.DamageHistoryInfo topDamager = null)");

            var live = died.IndexOf("!ReferenceEquals(live, run)", StringComparison.Ordinal);
            var bonus = died.IndexOf("if (TryRecordBonusKill(run, creature))\n                return;", StringComparison.Ordinal);
            var record = died.IndexOf("run.RecordKill(", StringComparison.Ordinal);
            var armed = died.IndexOf("AnnounceRewardArmed(", StringComparison.Ordinal);
            var progress = died.IndexOf("AnnounceProgress(run);", StringComparison.Ordinal);

            Assert.IsTrue(live >= 0 && live < bonus && bonus < record && record < armed && armed < progress);

            var helper = PooledLootSourceText.MethodBody(src, "internal static bool TryRecordBonusKill(ThreadDungeonRun run, Creature creature)");
            StringAssert.Contains(helper, "run.RecordBonusKill();");
            Assert.IsFalse(helper.Replace("RecordBonusKill(", "").Contains("RecordKill(", StringComparison.Ordinal), "never the clear counter");
            Assert.IsFalse(helper.Contains("AnnounceProgress", StringComparison.Ordinal));
            Assert.IsFalse(helper.Contains("AnnounceRewardArmed", StringComparison.Ordinal));
        }

        [TestMethod]
        public void Only_shaping_with_ExcludedFromClear_flags_a_creature_and_only_MarkPopulated_sets_the_denominator()
        {
            var spawner = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var shape = PooledLootSourceText.MethodBody(spawner, "internal static void ShapeCreature(Creature creature, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, uint observedMaxNonBoss, CreatureShapeOptions options)");
            StringAssert.Contains(shape, "creature.IsThreadBonusCreature = options.ExcludedFromClear;");

            var shrine = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ShrineEncounterType.cs");
            StringAssert.Contains(shrine, "ExcludedFromClear = true,");

            // Guardians are spawned after populate; the run's planned/spawned are written only by MarkPopulated.
            var run = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun.cs");
            Assert.AreEqual(1, Count(run, "this.spawned = spawned;"));
            Assert.IsFalse(shrine.Contains("MarkPopulated", StringComparison.Ordinal));
        }

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}

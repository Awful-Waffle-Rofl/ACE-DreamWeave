using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Time-budgeted creature placement (dynamic_dungeons_spawn_step_budget_ms) and the ended-copy unload watch.
    /// <see cref="ThreadDungeonSpawner.RunPlacementStep{T}"/> is the loop PlaceBatch runs per landblock action step;
    /// a fake clock drives the budget the way the step's Stopwatch does in production.
    /// </summary>
    [TestClass]
    public class ThreadSpawnStepBudgetTests
    {
        private sealed class FakeClock
        {
            public double Now;
            public double Read() => Now;
        }

        private static Queue<int> Plan(int count) => new Queue<int>(Enumerable.Range(0, count));

        [TestMethod]
        public void TimedStep_StopsAfterTheCreatureThatCrossesTheBudget()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(20, clock.Read, 4);
            var queue = Plan(20);
            var placed = new List<int>();

            // 1.5 ms per creature: 1.5, 3.0 are under 4; the third crosses to 4.5 and is the last of the step.
            var attempted = ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { placed.Add(e); clock.Now += 1.5; }, out _);

            Assert.AreEqual(3, attempted);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, placed);
            Assert.AreEqual(17, queue.Count);
        }

        [TestMethod]
        public void TimedStep_AlwaysPlacesAtLeastOne_EvenWhenTheBudgetIsAlreadySpent()
        {
            // The door pass has had its own step since 2026-09-21 (ThreadDoorPassStepTests), so a step no longer
            // starts with time already spent - but the floor that made that survivable is still the rule.
            var clock = new FakeClock { Now = 50 };
            var budget = new ThreadLootRollBudget(20, clock.Read, 4);
            var queue = Plan(5);

            var attempted = ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => clock.Now += 30, out _);

            Assert.AreEqual(1, attempted);
            Assert.AreEqual(4, queue.Count);
        }

        [TestMethod]
        public void TimedStep_HonoursTheBatchSizeAsAHardCap()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(5, clock.Read, 4);
            var queue = Plan(20);

            // Free creatures: time never runs out, so only the cap stops the step.
            var attempted = ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { }, out _);

            Assert.AreEqual(5, attempted);
            Assert.AreEqual(15, queue.Count);
        }

        [TestMethod]
        public void FailedAttempts_ChargeTheCapExactlyAsTheOldCounterDid()
        {
            // TryPlace returning false (a creature that failed to place) was still counted by the old `n < batchSize`.
            var budget = new ThreadLootRollBudget(3, () => 0, 4);
            var queue = Plan(10);
            var attempts = 0;

            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => attempts++, out _);

            Assert.AreEqual(3, attempts);
        }

        [TestMethod]
        public void Steps_PreservePlanOrder_AndDrainTheWholePlan()
        {
            var queue = Plan(23);
            var placed = new List<int>();
            var steps = 0;

            while (queue.Count > 0)
            {
                var clock = new FakeClock();
                var budget = new ThreadLootRollBudget(20, clock.Read, 4);
                ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { placed.Add(e); clock.Now += 1.0; }, out _);
                steps++;
            }

            // 1 ms each: the fourth creature reaches 4.0 and ends each step, so 23 entries take 6 steps (4 x 5 + 3).
            Assert.AreEqual(6, steps);
            CollectionAssert.AreEqual(Enumerable.Range(0, 23).ToArray(), placed, "plan order - boss last - is kept across steps");
        }

        [TestMethod]
        public void AThrowFromPlace_PropagatesToPlaceBatchsOwnCatch()
        {
            var budget = new ThreadLootRollBudget(20, () => 0, 4);
            var queue = Plan(5);

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { if (e == 1) throw new InvalidOperationException(); }, out _));

            Assert.AreEqual(3, queue.Count, "the throwing entry was consumed, as the inline loop consumed it");
        }

        [TestMethod]
        public void StepBudget_ClampsToOneToOneHundred()
        {
            Assert.AreEqual(1, ThreadDungeonSpawner.ClampSpawnStepBudgetMs(0));
            Assert.AreEqual(1, ThreadDungeonSpawner.ClampSpawnStepBudgetMs(-3));
            Assert.AreEqual(4, ThreadDungeonSpawner.ClampSpawnStepBudgetMs(4));
            Assert.AreEqual(100, ThreadDungeonSpawner.ClampSpawnStepBudgetMs(101));
            Assert.AreEqual(100, ThreadDungeonSpawner.ClampSpawnStepBudgetMs(long.MaxValue));
            Assert.AreEqual(4, ThreadDungeonSpawner.DefaultSpawnStepBudgetMs);
        }

        /// <summary>
        /// Wiring pin: PlaceBatch must build its step budget from the batch size (the hard cap), its own step Stopwatch
        /// and the run's step budget, and must route its loop through RunPlacementStep - a return to the bare
        /// `n &lt; batchSize` counter would pass every test above while placing untimed.
        /// </summary>
        [TestMethod]
        public void PlaceBatch_UsesTheTimedStepBudget()
        {
            var source = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "ThreadDungeons", "ThreadDungeonSpawner.cs")));

            StringAssert.Contains(source, "new ThreadLootRollBudget(batchSize, () => stepWatch.Elapsed.TotalMilliseconds, stepBudgetMs)");
            StringAssert.Contains(source, "RunPlacementStep(queue, budget, entry =>");
            StringAssert.Contains(source, "PropertyManager.GetLong(\"dynamic_dungeons_spawn_step_budget_ms\", DefaultSpawnStepBudgetMs)");
            Assert.IsFalse(source.Contains("n < batchSize"), "the untimed per-step counter is back");
        }

        [TestMethod]
        public void EndedCopy_QueuedOnlyOnceEmpty_AndDroppedOnceGoneOrReissued()
        {
            var copy = new object();
            var reissued = new object();

            Assert.AreEqual(ThreadDungeonManager.EndedCopyAction.Wait, ThreadDungeonManager.DecideEndedCopy(copy, copy, playerInside: true));
            Assert.AreEqual(ThreadDungeonManager.EndedCopyAction.Queue, ThreadDungeonManager.DecideEndedCopy(copy, copy, playerInside: false));
            Assert.AreEqual(ThreadDungeonManager.EndedCopyAction.Drop, ThreadDungeonManager.DecideEndedCopy(copy, null, playerInside: false), "unloaded");
            Assert.AreEqual(ThreadDungeonManager.EndedCopyAction.Drop, ThreadDungeonManager.DecideEndedCopy(copy, reissued, playerInside: false), "the id now belongs to another copy, which must never be queued on this run's behalf");
            Assert.AreEqual(ThreadDungeonManager.EndedCopyAction.Drop, ThreadDungeonManager.DecideEndedCopy(null, null, playerInside: false));
        }

        private static string FindInSourceTree(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}

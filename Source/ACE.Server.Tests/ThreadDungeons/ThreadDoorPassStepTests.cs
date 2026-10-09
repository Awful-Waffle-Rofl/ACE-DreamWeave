using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The 2026-09-21 placement split: the one-off door pass is its own chain step (so it can no longer eat the
    /// first creature step's time budget), and every placement step writes one Debug line carrying what it
    /// attempted, what it placed and WHICH rule stopped it.
    ///
    /// Two halves, for the usual reason. The stopping rule and the log line are pure functions
    /// (<see cref="ThreadDungeonSpawner.RunPlacementStep{T}"/>, ResolvePlacementStepStop, PlacementStepLine) and
    /// are exercised directly. The CHAIN - which delegate is enqueued first, which step calls MarkPopulated -
    /// needs a live Landblock and an action queue, neither of which this harness has, so its shape is pinned
    /// against the source text instead.
    ///
    /// No PropertyManager key is read by anything driven here: every dial is passed in as an argument.
    /// </summary>
    [TestClass]
    public class ThreadDoorPassStepTests
    {
        private const string SpawnerPath = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";

        private sealed class FakeClock
        {
            public double Now;
            public double Read() => Now;
        }

        private static Queue<int> Plan(int count) => new Queue<int>(Enumerable.Range(0, count));

        // ------------------------------------------------------------------------------------------------------
        // Fix 1: the door pass is its own step
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_populate_chain_starts_with_the_door_step_and_the_door_step_starts_the_placement_steps()
        {
            var src = PooledLootSourceText.Read(SpawnerPath);

            // The chain-start enqueue is TryPopulate's own last statement, so it sits AFTER both local functions
            // (DoorPass and PlaceBatch are declared inside TryPopulate, so a MethodBody pin on TryPopulate would
            // match their bodies too and prove nothing).
            var start = src.LastIndexOf("landblock.EnqueueAction(new ActionEventDelegate(DoorPass));", StringComparison.Ordinal);
            var placeBatchTail = src.LastIndexOf("ThreadDungeonManager.OnRunPopulated(run);", StringComparison.Ordinal);

            Assert.IsTrue(placeBatchTail >= 0 && start > placeBatchTail, "the chain is started on the door step, not on PlaceBatch");
            Assert.AreEqual(1, CountOf(src, "ActionEventDelegate(DoorPass)"), "one door step per run");

            var doorPass = PooledLootSourceText.MethodBody(src, "void DoorPass()");

            StringAssert.Contains(doorPass, "doorsUnlocked = UnlockDoors(landblock);", "the pass itself still runs here");
            StringAssert.Contains(doorPass, "landblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));",
                "and hands off to the first creature step");
            StringAssert.Contains(doorPass, "run.State == ThreadDungeonRunState.Ended",
                "the run-state check is on the door step too, so an ended copy is not walked");
            StringAssert.Contains(doorPass, "ForgetRun(run.RunId);", "an ended copy drops the latch here as PlaceBatch does");
        }

        [TestMethod]
        public void The_first_creature_step_starts_with_a_fresh_budget_of_its_own()
        {
            var src = PooledLootSourceText.Read(SpawnerPath);
            var doorPass = PooledLootSourceText.MethodBody(src, "void DoorPass()");
            var placeBatch = PooledLootSourceText.MethodBody(src, "void PlaceBatch()");

            Assert.IsFalse(placeBatch.Contains("UnlockDoors"), "the door pass no longer runs inside a placement step");
            Assert.IsFalse(placeBatch.Contains("doorPassDone"), "the once-per-run flag went with it");
            Assert.IsFalse(doorPass.Contains("stepWatch"), "the door pass does not share the placement step's clock");

            // The budget is built from a Stopwatch STARTED IN THIS STEP, which is what makes every step's
            // allowance fresh: a slow door pass in an earlier action cannot have spent any of it.
            var watch = placeBatch.IndexOf("var stepWatch = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal);
            var budget = placeBatch.IndexOf("new ThreadLootRollBudget(batchSize, () => stepWatch.Elapsed.TotalMilliseconds, stepBudgetMs)", StringComparison.Ordinal);

            Assert.IsTrue(watch >= 0, "the step starts its own Stopwatch");
            Assert.IsTrue(budget > watch, "and builds its budget from it");
        }

        [TestMethod]
        public void MarkPopulated_is_called_once_from_the_terminal_placement_step()
        {
            var src = PooledLootSourceText.Read(SpawnerPath);
            var placeBatch = PooledLootSourceText.MethodBody(src, "void PlaceBatch()");
            var doorPass = PooledLootSourceText.MethodBody(src, "void DoorPass()");

            Assert.IsFalse(doorPass.Contains("run.MarkPopulated("),
                "the door step never finishes the populate itself; its only route there is the placement step it hands off to");

            var guard = placeBatch.IndexOf("if (!finished)", StringComparison.Ordinal);
            var mark = placeBatch.IndexOf("run.MarkPopulated(", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && mark > guard, "MarkPopulated sits behind the terminal-step guard");
            Assert.AreEqual(1, CountOf(placeBatch, "run.MarkPopulated("), "exactly one call site in the placement chain");
            Assert.AreEqual(2, CountOf(src, "run.MarkPopulated("), "the only other one in the file is the plan-build failure path");
            Assert.AreEqual(1, CountOf(src, "run.MarkPopulated(0, 0, 0);"), "and that is it");

            // And the run itself refuses a second one, so a duplicated step could not double-publish.
            var run = PooledRun();
            run.ClearFraction = 0.5;
            run.MarkPopulated(7, 5, 1234u, 1234u);
            run.MarkPopulated(99, 99, 99u, 99u);

            Assert.AreEqual(7, run.Planned);
            Assert.AreEqual(5, run.Spawned);
        }

        // ------------------------------------------------------------------------------------------------------
        // Fix 2: the per-step line
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_budget_stop_is_reported_as_a_budget_stop()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(20, clock.Read, 4);
            var queue = Plan(20);
            var attempted = 0;
            var placed = 0;

            // 1.5 ms each: the third creature crosses 4 ms and ends the step, well short of the cap of 20.
            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { attempted++; placed++; clock.Now += 1.5; }, out var stop);

            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Budget, stop);

            var line = ThreadDungeonSpawner.PlacementStepLine("run=0xABC", 2, attempted, placed, clock.Now, stop, 20, 4, queue.Count, placed, 20);
            Console.WriteLine(line);

            StringAssert.Contains(line, "place step 2:");
            StringAssert.Contains(line, "attempted=3");
            StringAssert.Contains(line, "placed=3");
            StringAssert.Contains(line, "ms=4.5");
            StringAssert.Contains(line, "stop=budget");
            StringAssert.Contains(line, "cap=20");
            StringAssert.Contains(line, "budgetMs=4");
            StringAssert.Contains(line, "left=17");
            StringAssert.Contains(line, "total=3/20");
        }

        [TestMethod]
        public void A_cap_stop_is_reported_as_a_cap_stop()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(5, clock.Read, 4);
            var queue = Plan(20);
            var attempted = 0;

            // Free creatures: time never runs out, so only dynamic_dungeons_spawn_batch_size can stop the step.
            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => attempted++, out var stop);

            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Cap, stop);
            Assert.AreEqual(5, attempted);
            Assert.AreEqual(15, queue.Count, "the hard cap still applies");

            var line = ThreadDungeonSpawner.PlacementStepLine("run=0xABC", 1, attempted, attempted, 0.5, stop, 5, 4, queue.Count, attempted, 20);
            Console.WriteLine(line);

            StringAssert.Contains(line, "attempted=5 placed=5");
            StringAssert.Contains(line, "stop=cap");
            StringAssert.Contains(line, "cap=5");
            StringAssert.Contains(line, "left=15");
        }

        [TestMethod]
        public void A_step_that_empties_the_plan_reports_drained_whatever_else_was_also_true()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(5, clock.Read, 5);
            var queue = Plan(5);

            // Both the cap and the clock are spent on the last entry, but nothing was cut short.
            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => clock.Now += 1.0, out var stop);

            Assert.AreEqual(0, queue.Count);
            Assert.IsTrue(budget.TimeUp && budget.Spent >= budget.MaxRolls, "both stopping rules are true");
            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Drained, stop);
        }

        /// <summary>
        /// Code review of #1260: the first version of this classifier asked ThreadLootRollBudget.TimeUp AFTER the
        /// loop had exited, which re-invokes the budget's clock. A step that really stopped on the cap could then be
        /// reported as a budget stop purely because wall-clock crossed the budget in the gap between the loop's last
        /// condition check and the classify call. The race itself is not testable, but what the fix makes testable
        /// is: the reason no longer depends on WHEN it is read. Advancing the clock far past the budget after the
        /// loop must not turn a cap stop into a budget stop, and here it cannot, because nothing reads a clock.
        /// </summary>
        [TestMethod]
        public void A_cap_stop_stays_a_cap_stop_when_the_clock_runs_past_the_budget_afterwards()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(5, clock.Read, 4);
            var queue = Plan(20);

            // Free creatures: the cap, and only the cap, ends this step.
            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { }, out var stop);

            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Cap, stop, "the reason captured where the loop exited");

            // Now let wall-clock run away, exactly as a preempted world thread would between the loop and the log line.
            clock.Now = 5000;

            Assert.IsTrue(budget.TimeUp, "guard: the budget's clock now reads far past the allowance");
            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Cap, stop, "the captured reason does not drift");
            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Cap,
                ThreadDungeonSpawner.ResolvePlacementStepStop(queue.Count, budget),
                "and re-resolving gives the same answer, because the classifier reads no clock");
        }

        [TestMethod]
        public void The_cap_wins_the_tie_when_both_are_true_and_entries_remain()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(2, clock.Read, 4);
            var queue = Plan(10);

            // The second creature both fills the cap and crosses the budget. The cap wins, because a spent cap is
            // the one exit condition still observable after the loop without re-reading a clock - and a step that
            // spent its whole batch size could not have taken another attempt whatever the clock said. Pinned
            // because a log reader counts these by token.
            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => clock.Now += 2.0, out var stop);

            Assert.IsTrue(budget.TimeUp && budget.Spent >= budget.MaxRolls, "guard: both rules really are true here");
            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Cap, stop);
        }

        [TestMethod]
        public void A_step_always_places_at_least_one_creature_however_spent_its_budget_already_is()
        {
            // The pre-split worry: a slow door pass had already burned the step's allowance. It cannot any more,
            // but the floor that made that survivable is still here and still pinned.
            var clock = new FakeClock { Now = 500 };
            var budget = new ThreadLootRollBudget(20, clock.Read, 4);
            var queue = Plan(5);
            var attempted = 0;

            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e => { attempted++; clock.Now += 30; }, out var stop);

            Assert.AreEqual(1, attempted);
            Assert.AreEqual(4, queue.Count);
            Assert.AreEqual(ThreadDungeonSpawner.PlacementStepStop.Budget, stop);
        }

        [TestMethod]
        public void Attempted_counts_entries_that_failed_to_place_and_placed_does_not()
        {
            var clock = new FakeClock();
            var budget = new ThreadLootRollBudget(4, clock.Read, 100);
            var queue = Plan(10);
            var attempted = 0;
            var placed = 0;

            // Two of the four attempts "fail to place" (TryPlace false), exactly as a fallback-exhausted entry does.
            ThreadDungeonSpawner.RunPlacementStep(queue, budget, e =>
            {
                attempted++;

                if (e % 2 == 0)
                    placed++;
            }, out var stop);

            var line = ThreadDungeonSpawner.PlacementStepLine("run=0xABC", 1, attempted, placed, 1.0,
                stop, 4, 100, queue.Count, placed, 10);
            Console.WriteLine(line);

            StringAssert.Contains(line, "attempted=4 placed=2", "attempted > placed is the failed-placement signal");
            StringAssert.Contains(line, "stop=cap");
        }

        [TestMethod]
        public void The_error_reason_is_reported_when_the_loop_throws()
        {
            var budget = new ThreadLootRollBudget(20, () => 0, 4);
            var queue = Plan(5);
            var attempted = 0;

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ThreadDungeonSpawner.RunPlacementStep(queue, budget, e =>
                {
                    attempted++;

                    if (e == 1)
                        throw new InvalidOperationException();
                }, out _));

            var line = ThreadDungeonSpawner.PlacementStepLine("run=0xABC", 3, attempted, 1, 2.0,
                ThreadDungeonSpawner.PlacementStepStop.Error, 20, 4, queue.Count, 1, 5);
            Console.WriteLine(line);

            StringAssert.Contains(line, "attempted=2", "the throwing entry was attempted, and counting in the delegate keeps that true");
            StringAssert.Contains(line, "stop=error");
        }

        [TestMethod]
        public void The_milliseconds_are_formatted_invariantly()
        {
            var line = ThreadDungeonSpawner.PlacementStepLine("run=0xABC", 1, 1, 1, 12.3456,
                ThreadDungeonSpawner.PlacementStepStop.Drained, 20, 4, 0, 1, 1);

            StringAssert.Contains(line, "ms=12.35", "two decimals, a period, whatever the machine's culture is");
            StringAssert.Contains(line, "stop=drained");
        }

        [TestMethod]
        public void The_step_line_is_built_only_when_Debug_is_enabled()
        {
            var placeBatch = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath), "void PlaceBatch()");

            var guard = placeBatch.IndexOf("if (log.IsDebugEnabled)", StringComparison.Ordinal);
            var line = placeBatch.IndexOf("log.Debug(PlacementStepLine(", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && line > guard,
                "the line is built inside the IsDebugEnabled guard, so nothing is formatted on the hot path when Debug is off");
        }

        /// <summary>
        /// The other half of the code-review fix: PlaceBatch must TAKE the reason the loop captured, never
        /// recompute one after the fact. A recomputation would reintroduce the clock re-read that made a cap stop
        /// readable as a budget stop, and it would pass every pure test above, so it is pinned on source.
        /// </summary>
        [TestMethod]
        public void PlaceBatch_takes_the_stop_reason_from_the_loop_and_never_recomputes_it()
        {
            var placeBatch = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath), "void PlaceBatch()");

            StringAssert.Contains(placeBatch, "}, out stop);", "the reason comes out of RunPlacementStep");
            Assert.IsFalse(placeBatch.Contains("ResolvePlacementStepStop("),
                "PlaceBatch must not classify the stop itself; the loop already did, where it exited");

            var loop = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath),
                "internal static int RunPlacementStep<T>(Queue<T> queue, ThreadLootRollBudget budget, Action<T> place, out PlacementStepStop stop)");

            StringAssert.Contains(loop, "stop = ResolvePlacementStepStop(queue.Count, budget);",
                "classified immediately after the loop exits, inside the method that owns the loop");
        }

        [TestMethod]
        public void The_door_step_records_its_own_duration_rather_than_charging_it_to_a_placement_step()
        {
            var doorPass = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath), "void DoorPass()");

            StringAssert.Contains(doorPass, "ServerMetrics.RecordThreadsDoorPass(", "moved out of place_batch, not made invisible");
            StringAssert.Contains(doorPass, "run.Perf.RecordDoorPass(");
            Assert.IsFalse(doorPass.Contains("RecordThreadsPlaceBatch"), "the door step is not a place_batch sample");
            Assert.IsFalse(doorPass.Contains("run.Perf.RecordPlaceStep"), "and does not inflate placeSteps");

            var perf = new ThreadRunPerfStats();
            Assert.AreEqual(-1, perf.DoorPassMs, "unset until the door step runs");

            perf.RecordDoorPass(87, 12);
            Assert.AreEqual(87, perf.DoorPassMs);
            Assert.AreEqual(12, perf.DoorsUnlocked);
            Assert.AreEqual(0, perf.PlaceMs, "the door pass is not summed into placeMs");
            Assert.AreEqual(0, perf.PlaceSteps);

            perf.RecordDoorPass(3, -1);
            Assert.AreEqual(3, perf.DoorPassMs, "a second pass would be a bug; it overwrites rather than summing");
            Assert.AreEqual(0, perf.DoorsUnlocked, "a negative door count never lands");
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;
using ACE.Server.SpeedCourses;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The speed-course puzzle host: the speed-only fail policy (a wrong answer costs the placement's own lockout and a
    /// re-rolled round, never the Thread fail sink), the Seal contributions on a solve, and the run-scoped clear.
    /// </summary>
    [TestClass]
    public class SpeedPuzzleGateHostTests
    {
        private sealed class RecordingFailSink : IThreadPuzzleFailSink
        {
            public readonly List<(ThreadDungeonRun Run, PuzzleGatePlacement Placement)> Calls = new List<(ThreadDungeonRun, PuzzleGatePlacement)>();

            public void OnScoredWrong(ThreadDungeonRun run, Player player, PuzzleGatePlacement placement) => Calls.Add((run, placement));
        }

        private static (SpeedCourseRun Run, SpeedCourseFakeWorld World) NewRun()
        {
            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();
            var run = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, 1007501, 120, 0x50000042, 77, world, 0.0);
            return (run, world);
        }

        private static SpeedPuzzleDef Puzzle(string slot) => SpeedCourseFixtures.Course().Puzzles.Single(p => p.Slot == slot);

        private static int nextId = 930000;

        private static PuzzleGatePlacement Placement(IPuzzleGateHost host)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "beam", "n=3", "lockout=5" }, out var options, out _));
            var anchor = new Position(0x00890148, 118, -210, -12, 0, 0, 0, 1, 0);
            return new PuzzleGatePlacement(System.Threading.Interlocked.Increment(ref nextId), options, 9, anchor, 270f, null, 0, null, DateTime.UtcNow, host);
        }

        [TestMethod]
        public void Contract_NoPolicy_InstanceRunId_NoAmbush_NoVeto()
        {
            var (run, _) = NewRun();
            var host = new SpeedPuzzleGateHost(run, Puzzle("P1"));

            Assert.AreEqual(PuzzlePolicyMode.None, host.PolicyMode);
            Assert.AreEqual(SpeedCourseFakeWorld.FakeInstance, host.RunId);
            Assert.IsFalse(host.AllowAmbush);
            Assert.IsNull(host.CheckActivation(null, null));
        }

        [TestMethod]
        public void WrongAnswers_NeverReachTheThreadFailSink()
        {
            var sink = new RecordingFailSink();
            var saved = ThreadPuzzleRunHost.FailSink;

            try
            {
                ThreadPuzzleRunHost.FailSink = sink;

                // Positive control: a Thread run host DOES reach the swapped sink, so a zero below is not a dead seam.
                var threadHost = new ThreadPuzzleRunHost(ThreadLootTestFixtures.PooledRun(), false);
                threadHost.OnWrong(null, null, true);
                Assert.AreEqual(1, sink.Calls.Count, "control: the Thread host reports a scored wrong");
                sink.Calls.Clear();

                var (run, world) = NewRun();

                foreach (var slot in SpeedPuzzleSlots.All)
                {
                    var host = new SpeedPuzzleGateHost(run, Puzzle(slot));
                    host.OnWrong(null, null, scored: true);
                    host.OnWrong(null, null, scored: false);
                }

                Assert.AreEqual(0, sink.Calls.Count, "no speed wrong answer reaches the Thread fail policy");
                Assert.AreEqual(0, world.Contributions.Count, "a wrong answer contributes nothing");
                Assert.IsFalse(run.IsEnded, "and does not end the run");
            }
            finally
            {
                ThreadPuzzleRunHost.FailSink = saved;
            }
        }

        [TestMethod]
        public void WrongAnswer_CostsTheLockout_ThenTheWholePuzzleAgain()
        {
            // The rules every host shares (PuzzleGateRules): the placement the course builds has lockout 5 and the
            // course's rounds. A wrong answer resets progress and locks for exactly 5 s; the reshuffle after it is the
            // manager's one wrong-answer path (HandleActivation's Wrong case), so the next attempt is a re-rolled round 1.
            var t0 = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
            var rules = new PuzzleGateRules(rounds: 3, lockoutSeconds: 5);

            Assert.AreEqual(PuzzleActivation.RoundAdvanced, rules.Activate(true, t0));
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, rules.Activate(true, t0));
            Assert.AreEqual(PuzzleActivation.Wrong, rules.Activate(false, t0));

            Assert.AreEqual(0, rules.RoundsCompleted(t0), "progress is reset");
            Assert.AreEqual(PuzzleActivation.Refused, rules.Activate(true, t0.AddSeconds(4.9)), "inside the 5 s delay");
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, rules.Activate(true, t0.AddSeconds(5)), "after it, round 1 again");
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, rules.Activate(true, t0.AddSeconds(5)));
            Assert.AreEqual(PuzzleActivation.Solved, rules.Activate(true, t0.AddSeconds(5)), "all rounds must be solved again");
        }

        [TestMethod]
        public void OnSolved_P1AndP3_ContributeTheirTokenAtTheCourseWeight_P2Nothing()
        {
            var (run, world) = NewRun();

            new SpeedPuzzleGateHost(run, Puzzle("P2")).OnSolved(null, null);
            Assert.AreEqual(0, world.Contributions.Count, "P2 contributes nothing");

            new SpeedPuzzleGateHost(run, Puzzle("P1")).OnSolved(null, null);
            new SpeedPuzzleGateHost(run, Puzzle("P3")).OnSolved(null, null);

            CollectionAssert.AreEqual(
                new[] { ("bb_seal", "p1", 3.0), ("bb_seal", "p3", 3.0) },
                world.Contributions.ToArray());
        }

        [TestMethod]
        public void OnSolved_AfterTheRunEnded_ContributesNothing()
        {
            var (run, world) = NewRun();
            run.End("test");

            new SpeedPuzzleGateHost(run, Puzzle("P1")).OnSolved(null, null);

            Assert.AreEqual(0, world.Contributions.Count);
        }

        [TestMethod]
        public void ClearForRun_ClearsOnlyThisCoursesPlacements_AndEachHostHearsItOnce()
        {
            var (run, world) = NewRun();
            run.End("teardown");   // as at teardown: the run is marked ended before its placements are cleared

            var mine = Placement(new SpeedPuzzleGateHost(run, Puzzle("P1")));
            var threadRun = ThreadLootTestFixtures.PooledRun();
            var other = Placement(new ThreadPuzzleRunHost(threadRun, false));

            PuzzleGateManager.TrackForTest(mine);
            PuzzleGateManager.TrackForTest(other);

            try
            {
                Assert.AreNotEqual(SpeedCourseFakeWorld.FakeInstance, threadRun.RunId, "fixture: the Thread run is another instance");

                var cleared = PuzzleGateManager.ClearForRun(SpeedCourseFakeWorld.FakeInstance);

                Assert.AreEqual(1, cleared);
                Assert.IsFalse(PuzzleGateManager.IsRegistered(mine));
                Assert.IsTrue(PuzzleGateManager.IsRegistered(other), "a Thread run's placement is untouched");

                world.Drain();
                Assert.AreEqual(0, world.Placements.Count, "a cleared placement of an ended run is not re-placed");
            }
            finally
            {
                PuzzleGateManager.UntrackForTest(mine);
                PuzzleGateManager.UntrackForTest(other);
            }
        }

        [TestMethod]
        public void ReapedWhileTheRunIsLive_IsRePlaced()
        {
            var (run, world) = NewRun();
            run.Tick(0.5);   // populate: the three placements, their types recorded
            world.Drain();
            var before = world.Placements.Count;

            var host = (SpeedPuzzleGateHost)world.Placements.Single(p => p.Site.Id == "bb_p2").Host;
            host.OnRemoved(null, PuzzleRemovalReason.Reaped, solved: false);

            Assert.AreEqual(before, world.Placements.Count, "the re-place is marshalled, not run on the calling thread");

            world.Drain();

            Assert.AreEqual(before + 1, world.Placements.Count);
            Assert.AreEqual("bb_p2", world.Placements.Last().Site.Id);
        }
    }
}

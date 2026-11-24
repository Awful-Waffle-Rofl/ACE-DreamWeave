using System;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Thread Shrine boon ledger (ThreadDungeonRun_Boons.cs) and its read side (ThreadBoons): neutral outside the copy,
    /// when puzzle-removed and after the run ends; one kind per run (so the cap is never stacked past); a later shrine
    /// draws only kinds the run does not hold; every kind held means a silent shrine. Presence is asked through
    /// ThreadRunPresence.IsMemberInside's guid + position overload (the Player overload forwards to it). Reads no
    /// PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadBoonLedgerTests
    {
        [TestMethod]
        public void A_holder_is_blessed_only_inside_the_copy_and_a_non_holder_never()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member));

            Assert.AreEqual(ShrineUseOutcome.Granted, Use(run, Only(ThreadBoonKind.Damage), new[] { Owner }).Outcome);

            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Damage), 1e-12, "holder inside");
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Owner, OtherInstance(run), ThreadBoonKind.Damage), "holder in another instance");
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Owner, OffLandblock(run), ThreadBoonKind.Damage), "holder off the copy's landblock");
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Owner, null, ThreadBoonKind.Damage), "no position (logged out)");
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Member, Inside(run), ThreadBoonKind.Damage), "a member inside who was not a recipient");
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Xp), "another kind");

            // A stranger standing in the copy is never inside (roster clause), even if a recipients list named them.
            Assert.AreEqual(ShrineUseOutcome.Granted, Use(run, Only(ThreadBoonKind.Loot), new[] { Stranger }).Outcome);
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Stranger, Inside(run), ThreadBoonKind.Loot));

            // The instance lookup (ThreadBoons.For) resolves the run from the position's instance.
            var lookup = ThreadBoons.RunLookup;

            try
            {
                ThreadBoons.RunLookup = instance => instance == run.Instance ? run : null;
                Assert.AreEqual(1.25, ThreadBoons.For(Owner, Inside(run), ThreadBoonKind.Damage), 1e-12);
                Assert.AreEqual(1.0, ThreadBoons.For(Owner, OtherInstance(run), ThreadBoonKind.Damage));
            }
            finally
            {
                ThreadBoons.RunLookup = lookup;
            }
        }

        [TestMethod]
        public void A_puzzle_removed_holder_is_never_blessed()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member));

            Use(run, Only(ThreadBoonKind.Mitigation), new[] { Owner, Member });
            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Member, Inside(run), ThreadBoonKind.Mitigation), 1e-12, "control");

            Assert.IsTrue(run.MarkPuzzleRemoved(Member));

            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Member, Inside(run), ThreadBoonKind.Mitigation));
            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Mitigation), 1e-12, "the others keep theirs");
        }

        [TestMethod]
        public void Every_blessing_ends_with_the_run_and_a_later_use_grants_nothing()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run());

            Use(run, Only(ThreadBoonKind.Xp), new[] { Owner });
            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Xp), 1e-12, "control");

            run.MarkEnded("test");

            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Xp), "Ended");
            Assert.AreEqual(0, run.BoonsHeldBy(Owner).Count);
            Assert.AreEqual(ShrineUseOutcome.RunOver, Use(run, Only(ThreadBoonKind.Loot), new[] { Owner }).Outcome);
        }

        [TestMethod]
        public void A_grant_racing_a_release_never_leaves_the_hot_path_gate_up()
        {
            var baseline = ThreadBoons.LiveBoonRuns;

            // Probabilistic backstop only: the race window is narrow, so this loop catches a counter move outside the lock
            // in some runs, not all (a reviewer's probe: 500 rounds missed it 8 of 8 times; 200000 caught it 2 of 3). The
            // structural pin below is the deterministic guard.
            for (var round = 0; round < 100000; round++)
            {
                var run = Run();

                try
                {
                    using (var gate = new System.Threading.Barrier(2))
                    {
                        System.Threading.Tasks.Parallel.Invoke(new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 },
                            () => { gate.SignalAndWait(); Use(run, AllKinds(), new[] { Owner }); },
                            () => { gate.SignalAndWait(); run.ReleaseBoons(); });
                    }
                }
                finally
                {
                    run.ReleaseBoons();
                }

                Assert.AreEqual(baseline, ThreadBoons.LiveBoonRuns, $"round {round}: one lowering per raise, whichever side won");
                Assert.IsFalse(run.HoldsAnyBoon, $"round {round}");
            }

            // The deterministic guard: both sides move the counter only inside the run lock, the raise guarded by
            // boonsCounted and the lowering by boonsReleased.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun_Boons.cs");
            var use = PooledLootSourceText.MethodBody(src, "public ShrineUseOutcome TryUseShrine(ShrineLatch latch, bool userInside, IReadOnlyCollection<uint> recipients, IReadOnlyList<ShrineBoonRow> table,");
            var release = PooledLootSourceText.MethodBody(src, "public void ReleaseBoons()");

            Assert.IsTrue(use.IndexOf("lock (stateLock)", StringComparison.Ordinal) < use.IndexOf("ThreadBoons.OnRunGainedBoons();", StringComparison.Ordinal));
            StringAssert.Contains(use, "if (!boonsCounted)\n                {\n                    boonsCounted = true;\n                    ThreadBoons.OnRunGainedBoons();");
            Assert.IsTrue(release.IndexOf("lock (stateLock)", StringComparison.Ordinal) < release.IndexOf("ThreadBoons.OnRunReleasedBoons();", StringComparison.Ordinal));
            Assert.IsTrue(release.IndexOf("if (boonsReleased)\n                    return;", StringComparison.Ordinal) >= 0);
        }

        [TestMethod]
        public void The_combat_read_of_a_blessing_takes_no_lock()
        {
            // Every damage, resist and attack-speed calculation inside a blessed thread reads here: no run lock on that path.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun_Boons.cs");

            foreach (var signature in new[]
            {
                "public double BoonMagnitude(uint guid, ThreadBoonKind kind)",
                "public IReadOnlyList<(ThreadBoonKind Kind, double Magnitude)> BoonsHeldBy(uint guid)",
            })
            {
                var body = PooledLootSourceText.MethodBody(src, signature);
                Assert.IsFalse(body.Contains("lock (", StringComparison.Ordinal), signature);
                StringAssert.Contains(body, "IsEndedLockFree", signature);
            }

            // The presence half: a run nobody was puzzle-removed from answers without the lock.
            var puzzle = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun_Puzzle.cs");
            StringAssert.Contains(puzzle, "private volatile int puzzleRemovedCount;");
            StringAssert.Contains(puzzle, "if (puzzleRemovedCount == 0)\n                return false;");

            // Behaviour behind the fast path: removal still lands (the counter moves under the lock with the set).
            using var scope = new Releaser();
            var run = scope.Track(Run(Member));
            Assert.IsFalse(run.IsPuzzleRemoved(Member));
            Assert.IsTrue(run.MarkPuzzleRemoved(Member));
            Assert.IsTrue(run.IsPuzzleRemoved(Member));
            Assert.IsFalse(run.IsPuzzleRemoved(Owner));
        }

        [TestMethod]
        public void Releasing_drops_the_ledger_and_lowers_the_hot_path_gate()
        {
            var run = Run();

            try
            {
                Assert.IsFalse(run.HoldsAnyBoon);
                Use(run, Only(ThreadBoonKind.Loot), new[] { Owner });
                Assert.IsTrue(run.HoldsAnyBoon);
                Assert.IsTrue(ThreadBoons.AnyLive);
            }
            finally
            {
                run.ReleaseBoons();
                run.ReleaseBoons(); // idempotent: the gate is lowered once
            }

            Assert.IsFalse(run.HoldsAnyBoon);
            Assert.IsFalse(ThreadBoons.AnyLive, "no other run holds a blessing in this process");
            Assert.AreEqual(1.0, run.BoonMagnitude(Owner, ThreadBoonKind.Loot));
            Assert.AreEqual(ShrineUseOutcome.RunOver, Use(run, Only(ThreadBoonKind.Damage), new[] { Owner }).Outcome, "released means no later grant");
        }

        [TestMethod]
        public void The_same_kind_never_exceeds_the_cap_across_two_shrines()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run());

            var first = Use(run, Only(ThreadBoonKind.Damage, 1.5), new[] { Owner });
            Assert.AreEqual(ShrineUseOutcome.Granted, first.Outcome);
            Assert.AreEqual(1.25, first.Magnitude, 1e-12, "an over-cap table row is clamped to the run's cap");

            var second = Use(run, Only(ThreadBoonKind.Damage, 1.25), new[] { Owner });
            Assert.AreEqual(ShrineUseOutcome.Silent, second.Outcome, "the run already holds damage");
            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Damage), 1e-12, "never 1.5, never 1.5625");
        }

        [TestMethod]
        public void The_cap_is_the_stamped_tunable_clamped_to_one_and_two()
        {
            Assert.AreEqual(1.25, ThreadDungeonRun.ClampShrineBoonCap(double.NaN));
            Assert.AreEqual(1.0, ThreadDungeonRun.ClampShrineBoonCap(0.5));
            Assert.AreEqual(2.0, ThreadDungeonRun.ClampShrineBoonCap(5.0));
            Assert.AreEqual(1.4, ThreadDungeonRun.ClampShrineBoonCap(1.4), 1e-12);

            using var scope = new Releaser();
            var run = scope.Track(Run());
            run.ShrineBoonCap = 1.1;

            Assert.AreEqual(1.1, Use(run, Only(ThreadBoonKind.Luminance), new[] { Owner }).Magnitude, 1e-12);
        }

        [TestMethod]
        public void A_later_shrine_draws_only_from_kinds_the_run_does_not_hold()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member));

            // draw 0 always takes the first available row in table order
            var first = Use(run, AllKinds(), new[] { Owner, Member }, draw: 0.0);
            var second = Use(run, AllKinds(), new[] { Owner, Member }, draw: 0.0);

            Assert.AreEqual(ThreadBoonKind.Damage, first.Kind);
            Assert.AreEqual(ThreadBoonKind.Mitigation, second.Kind, "damage is held, so the next draw skips it");

            // Six shrines grant six different kinds whatever the draw.
            using var scope2 = new Releaser();
            var run2 = scope2.Track(Run());
            var rng = new Random(5);
            var kinds = Enumerable.Range(0, 6).Select(_ => Use(run2, AllKinds(), new[] { Owner }, draw: rng.NextDouble()).Kind).ToList();

            CollectionAssert.AreEquivalent(Enum.GetValues(typeof(ThreadBoonKind)).Cast<ThreadBoonKind>().ToList(), kinds);
            Assert.AreEqual(6, run2.BoonsHeldBy(Owner).Count);
            Assert.IsTrue(run2.BoonsHeldBy(Owner).All(b => Math.Abs(b.Magnitude - 1.25) < 1e-12));
        }

        [TestMethod]
        public void Every_kind_held_makes_the_shrine_silent_and_spent()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run());

            for (var i = 0; i < 6; i++)
                Assert.AreEqual(ShrineUseOutcome.Granted, Use(run, AllKinds(), new[] { Owner }, draw: 0.5).Outcome);

            var latch = new ShrineLatch();
            Assert.AreEqual(ShrineUseOutcome.Silent, Use(run, AllKinds(), new[] { Owner }, latch: latch).Outcome);
            Assert.IsTrue(latch.IsSpent(run), "a silent shrine is spent anyway");
            Assert.AreEqual(ShrineUseOutcome.Spent, Use(run, AllKinds(), new[] { Owner }, latch: latch).Outcome);
            Assert.AreEqual(6, run.GrantedBoonKinds.Count);
        }

        [TestMethod]
        public void Only_the_members_inside_at_use_time_receive_and_a_later_arrival_gets_nothing()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member, Member2));

            Use(run, Only(ThreadBoonKind.AttackSpeed), new[] { Owner, Member }); // Member2 was outside at the use

            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.AttackSpeed), 1e-12);
            Assert.AreEqual(1.25, ThreadBoons.ForRun(run, Member, Inside(run), ThreadBoonKind.AttackSpeed), 1e-12);
            Assert.AreEqual(1.0, ThreadBoons.ForRun(run, Member2, Inside(run), ThreadBoonKind.AttackSpeed), "arrived after the use");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The puzzle reward scene's seal on a Thread run (ThreadDungeonRun_Puzzle.cs), its run host
    /// (ThreadPuzzleRunHost), the watchdog and the pass's world-free planning. The clear-time side effects are
    /// observed through the manager's existing swappable seams (GemDestroyer, SurveyRecorder, ClearRewardHandler,
    /// PooledLootTrigger), the same way GroupRunLifecycleTests observes a clear. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class ThreadPuzzleRewardSealTests
    {
        /// <summary>A pooled solo run at ClearFraction 0.9 with 10 trash and no boss, populated.</summary>
        private static ThreadDungeonRun SealedRun(out object placement)
        {
            var run = PooledRun();
            run.ClearFraction = 0.9;
            placement = new object();

            // Sealed during Starting, as the puzzle pass does (it runs before the creature batches).
            Assert.IsTrue(run.SealReward(placement));
            run.MarkHasPuzzles();
            run.MarkPopulated(10, 10, 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            return run;
        }

        private static void KillAll(ThreadDungeonRun run, int n = 10)
        {
            for (var i = 0; i < n; i++)
                run.RecordKill(false);
        }

        private sealed class ClearCounters : IDisposable
        {
            public int Surveys, Gems, Loot, Posted;

            /// <summary>Follow-ups posted to the run landblock and not yet run (when <see cref="RunPostsInline"/> is false).</summary>
            public readonly List<Action> Queue = new List<Action>();

            /// <summary>True (default): a posted follow-up runs immediately, standing in for the landblock running its queue.</summary>
            public bool RunPostsInline = true;

            private readonly Action<ThreadDungeonRun> gem = ThreadDungeonManager.GemDestroyer;
            private readonly Action<ThreadDungeonRun> survey = ThreadDungeonManager.SurveyRecorder;
            private readonly Action<ThreadDungeonRun, ACE.Server.WorldObjects.Player> reward = ThreadDungeonManager.ClearRewardHandler;
            private readonly Func<ThreadDungeonRun, CacheRequestOutcome> loot = ThreadDungeonManager.PooledLootTrigger;
            private readonly Action<ThreadDungeonRun, Action> post = ThreadPuzzleRunHost.PostToRunLandblock;

            public ClearCounters()
            {
                ThreadDungeonManager.GemDestroyer = _ => Gems++;
                ThreadDungeonManager.SurveyRecorder = _ => Surveys++;
                ThreadDungeonManager.ClearRewardHandler = (_, __) => { };
                ThreadDungeonManager.PooledLootTrigger = r => { if (r.State == ThreadDungeonRunState.Cleared) Loot++; return CacheRequestOutcome.NotApplicable; };
                ThreadPuzzleRunHost.PostToRunLandblock = (_, action) =>
                {
                    Posted++;

                    if (RunPostsInline)
                        action();
                    else
                        Queue.Add(action);
                };
            }

            public void Dispose()
            {
                ThreadDungeonManager.GemDestroyer = gem;
                ThreadDungeonManager.SurveyRecorder = survey;
                ThreadDungeonManager.ClearRewardHandler = reward;
                ThreadDungeonManager.PooledLootTrigger = loot;
                ThreadPuzzleRunHost.PostToRunLandblock = post;
            }
        }

        [TestMethod]
        public void A_sealed_run_does_not_clear_on_its_kills_and_an_unsealed_one_does()
        {
            var run = SealedRun(out _);
            KillAll(run);

            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "sealed: every kill landed but the run holds");
            Assert.IsTrue(run.IsRewardArmed, "and the reward scene is now armed");
            Assert.IsNull(run.ClearedUtc);

            // Discriminating control: the same run shape WITHOUT a seal clears on the same kills.
            var control = PooledRun();
            control.ClearFraction = 0.9;
            control.MarkPopulated(10, 10, 0);
            KillAll(control);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, control.State);

            Assert.IsTrue(run.UnsealReward());
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "lifting the seal re-runs the clear check");
            Assert.IsFalse(run.UnsealReward(), "the seal lifts once");
        }

        [TestMethod]
        public void The_reward_scene_is_not_armed_until_the_clear_fraction_and_refuses_without_penalty()
        {
            var run = SealedRun(out _);
            var host = new ThreadPuzzleRunHost(run, true);

            KillAll(run, 8);
            Assert.IsFalse(run.IsRewardArmed, "8/10 is below 0.9");
            Assert.AreEqual(PuzzleGateText.RewardNotArmed, host.CheckActivation(null, null));

            run.RecordKill(false);
            Assert.IsTrue(run.IsRewardArmed, "9/10 reaches 0.9");
            Assert.IsNull(host.CheckActivation(null, null));

            // A gate host never vetoes.
            Assert.IsNull(new ThreadPuzzleRunHost(PooledRun(), false).CheckActivation(null, null));
        }

        [TestMethod]
        public void A_solve_unseals_and_announces_the_clear_exactly_once()
        {
            using var c = new ClearCounters();
            var run = SealedRun(out _);
            var host = new ThreadPuzzleRunHost(run, true);

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run); // a stray populate-path call while sealed announces nothing
            Assert.AreEqual(0, c.Surveys);

            host.OnSolved(null, null);

            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.AreEqual(1, c.Surveys, "the clear announced once");
            Assert.AreEqual(1, c.Gems, "solo: the gem dies at the clear");
            Assert.AreEqual(1, c.Loot, "pooled loot asked for");
            Assert.AreEqual(0, c.Posted, "a solve is already on the run landblock (HandleActivation): the follow-up runs inline");

            // Every later path is a no-op: a second solve callback, the removal after a solve, the watchdog, a
            // direct OnRewardUnsealed.
            host.OnSolved(null, null);
            host.OnRemoved(null, PuzzleRemovalReason.Cleared, true);
            host.OnRemoved(null, PuzzleRemovalReason.Reaped, false);
            ThreadDungeonManager.OnRewardUnsealed(run);

            Assert.AreEqual(1, c.Surveys, "still once");
            Assert.AreEqual(1, c.Gems);
        }

        [TestMethod]
        public void A_seal_lifted_before_the_kills_are_done_leaves_the_run_to_clear_normally()
        {
            using var c = new ClearCounters();
            var run = SealedRun(out _);
            var host = new ThreadPuzzleRunHost(run, true);

            host.OnRemoved(null, PuzzleRemovalReason.SpawnFailed, false);

            Assert.IsFalse(run.IsRewardSealed, "a failed spawn unseals");
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "but there is nothing to clear yet");
            Assert.AreEqual(0, c.Surveys);

            KillAll(run);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "the run is an ordinary run again");
        }

        [TestMethod]
        public void Every_removal_without_a_solve_unseals()
        {
            foreach (PuzzleRemovalReason reason in Enum.GetValues(typeof(PuzzleRemovalReason)))
            {
                using var c = new ClearCounters();
                var run = SealedRun(out _);
                KillAll(run);

                new ThreadPuzzleRunHost(run, true).OnRemoved(null, reason, false);

                Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, $"{reason} must unseal");
                Assert.AreEqual(1, c.Posted, $"{reason}: OnRemoved may arrive off the landblock thread, so the follow-up is posted");
                Assert.AreEqual(1, c.Surveys, $"{reason}: the clear announced once");
            }

            // A GATE placement's removal touches no seal.
            var gateRun = SealedRun(out _);
            new ThreadPuzzleRunHost(gateRun, false).OnRemoved(null, PuzzleRemovalReason.SpawnFailed, false);
            Assert.IsTrue(gateRun.IsRewardSealed);
        }

        [TestMethod]
        public void The_watchdog_unseals_a_sealed_run_whose_reward_placement_is_gone_and_leaves_a_live_one_alone()
        {
            using var c = new ClearCounters();
            var alive = ThreadPuzzleWatchdog.PlacementAlive;
            var reap = ThreadPuzzleWatchdog.ReapRun;
            var reapedFor = new List<uint>();

            try
            {
                ThreadPuzzleWatchdog.ReapRun = (id, _) => { reapedFor.Add(id); return 0; };

                var run = SealedRun(out var placement);
                KillAll(run);

                ThreadPuzzleWatchdog.PlacementAlive = p => ReferenceEquals(p, placement);
                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, DateTime.UtcNow), "live placement: nothing to do");
                Assert.IsTrue(run.IsRewardSealed);
                CollectionAssert.AreEqual(new[] { run.RunId }, reapedFor, "the run's dead placements are reaped every check");

                // Review F1: the watchdog runs on the WORLD thread, so the clear follow-up (AnnounceCleared spawns the
                // exit and reads tunables) must be QUEUED onto the run copy's landblock, never run here.
                c.RunPostsInline = false;
                ThreadPuzzleWatchdog.PlacementAlive = _ => false;
                Assert.IsTrue(ThreadPuzzleWatchdog.Check(run, DateTime.UtcNow), "placement gone: unseal");
                Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "the seal itself lifts inline, under the run's lock");
                Assert.AreEqual(1, c.Posted, "the follow-up was posted to the run landblock");
                Assert.AreEqual(0, c.Surveys, "and NOT run on the calling (world) thread");
                Assert.AreEqual(0, c.Gems);

                c.Queue.Single()();
                Assert.AreEqual(1, c.Surveys, "the landblock runs it: the clear announces once");

                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, DateTime.UtcNow), "once");
                Assert.AreEqual(1, c.Posted);
            }
            finally
            {
                ThreadPuzzleWatchdog.PlacementAlive = alive;
                ThreadPuzzleWatchdog.ReapRun = reap;
            }
        }

        [TestMethod]
        public void A_sealed_and_armed_run_stops_printing_kill_progress()
        {
            var run = SealedRun(out _);
            KillAll(run, 8);

            Assert.IsTrue(run.TryGetKillProgress(out var killed, out var required, out _), "not armed yet: progress still reports");
            Assert.AreEqual(8, killed);

            run.RecordKill(false);
            Assert.IsTrue(run.IsRewardArmed);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "still Active: the seal holds");
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _), "sealed and armed: no more x/y lines");

            run.RecordKill(false);
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _), "nor on any later kill");

            // Control: an unsealed Active run below the fraction reports as before.
            var open = PooledRun();
            open.ClearFraction = 0.9;
            open.MarkPopulated(10, 10, 0);
            KillAll(open, 8);
            Assert.IsTrue(open.TryGetKillProgress(out _, out _, out _));
        }

        [TestMethod]
        public void A_run_seals_once_and_never_after_it_cleared()
        {
            var run = PooledRun();
            Assert.IsFalse(run.SealReward(null));
            Assert.IsTrue(run.SealReward(new object()));
            Assert.IsFalse(run.SealReward(new object()), "one reward scene per run");

            var cleared = PooledRun();
            cleared.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, cleared.State);
            Assert.IsFalse(cleared.SealReward(new object()), "a cleared run cannot be sealed after the fact");
        }

        [TestMethod]
        public void The_fail_sink_hears_only_scored_wrongs()
        {
            var sink = ThreadPuzzleRunHost.FailSink;
            var heard = new List<ThreadDungeonRun>();

            try
            {
                ThreadPuzzleRunHost.FailSink = new RecordingSink(heard);
                var run = PooledRun();
                var host = new ThreadPuzzleRunHost(run, false);

                host.OnWrong(null, null, false);
                Assert.AreEqual(0, heard.Count, "a refused (lockout) wrong is not scored");

                host.OnWrong(null, null, true);
                CollectionAssert.AreEqual(new[] { run }, heard);

                Assert.AreEqual(PuzzlePolicyMode.Run, host.PolicyMode);
                Assert.IsFalse(host.AllowAmbush, "no ambush creatures in runs");
                Assert.AreEqual(run.RunId, host.RunId);
            }
            finally
            {
                ThreadPuzzleRunHost.FailSink = sink;
            }

            Assert.AreSame(ThreadPuzzleFailPolicySink.Instance, ThreadPuzzleRunHost.FailSink, "the shipped sink is the IP-wide fail policy");
        }

        private sealed class RecordingSink : IThreadPuzzleFailSink
        {
            private readonly List<ThreadDungeonRun> heard;
            public RecordingSink(List<ThreadDungeonRun> heard) => this.heard = heard;
            public void OnScoredWrong(ThreadDungeonRun run, ACE.Server.WorldObjects.Player player, PuzzleGatePlacement placement) => heard.Add(run);
        }

        [TestMethod]
        public void The_pass_plans_nothing_unless_stamped_and_the_reward_only_with_pooled_loot()
        {
            var sites = new List<PuzzleSiteDef>
            {
                new PuzzleSiteDef { Id = "g", Kind = PuzzleSiteKind.Gate, Anchor = new PuzzleSitePointDef(), MaxN = 5, Types = new List<PuzzleGateType> { PuzzleGateType.Beam },
                    GateModel = new PuzzleGateModelDef { Kind = PuzzleGateModelKind.Door, Wcid = 1006850, Scale = 1f } },
                new PuzzleSiteDef { Id = "r", Kind = PuzzleSiteKind.Reward, Anchor = new PuzzleSitePointDef { X = 100f }, MaxN = 5, Types = new List<PuzzleGateType> { PuzzleGateType.Sigil } },
            };

            var unstamped = PooledRun();
            Assert.AreEqual(0, ThreadPuzzlePass.Plan(unstamped, sites).Count, "a run TryStart never stamped places nothing");

            var pooled = PooledRun();
            pooled.PuzzleGatesEnabled = true;
            pooled.PuzzleGatesPerRun = 1;
            pooled.PuzzleRewardSceneEnabled = true;
            var plan = ThreadPuzzlePass.Plan(pooled, sites);
            Assert.AreEqual(2, plan.Count);
            Assert.AreEqual(1, plan.Count(p => p.IsReward));

            var corpses = NewRun(pooled: false);
            corpses.PuzzleGatesEnabled = true;
            corpses.PuzzleGatesPerRun = 1;
            corpses.PuzzleRewardSceneEnabled = true;
            Assert.AreEqual(0, ThreadPuzzlePass.Plan(corpses, sites).Count(p => p.IsReward), "no reward scene without pooled loot");

            var masterOff = PooledRun();
            masterOff.PuzzleGatesEnabled = false;
            masterOff.PuzzleGatesPerRun = 1;
            masterOff.PuzzleRewardSceneEnabled = true;
            Assert.AreEqual(0, ThreadPuzzlePass.Plan(masterOff, sites).Count, "the gates switch is the master switch");
        }

        [TestMethod]
        public void The_reward_model_is_the_focal_stand_in_and_a_barrier_carries_the_doorway_width()
        {
            var reward = ThreadPuzzlePass.ModelFor(new PuzzleSiteDef(), true);
            Assert.AreEqual(PuzzleGateForm.Focal, reward.Form);
            Assert.AreEqual(PuzzleGateTunables.RewardFocalWcid, reward.Wcid);
            Assert.AreEqual(1006852u, reward.Wcid, "the Ward Beacon host stands in until a target weenie exists");
            Assert.AreNotEqual(0u, reward.Script);

            var barrier = ThreadPuzzlePass.ModelFor(new PuzzleSiteDef
            {
                Doorway = new PuzzleDoorwayDef { Width = 7.5f, Height = 3f },
                GateModel = new PuzzleGateModelDef { Kind = PuzzleGateModelKind.Barrier, Wcid = 1006856, Scale = 0.9f, Panels = 3 },
            }, false);
            Assert.AreEqual(PuzzleGateForm.Barrier, barrier.Form);
            Assert.AreEqual(3, barrier.Panels);
            Assert.AreEqual(7.5f, barrier.DoorwayWidth);

            Assert.IsNull(ThreadPuzzlePass.ModelFor(new PuzzleSiteDef { GateModel = new PuzzleGateModelDef { Kind = PuzzleGateModelKind.Resident } }, false), "resident: no model");
        }
    }
}

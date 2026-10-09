using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The reward scene placed at ARMING: the pure site selector (ThreadPuzzleRewardSelector), the directional
    /// armed line, and the pending-seal state machine (ThreadDungeonRun_Puzzle.cs, ThreadPuzzlePass.PlaceRewardAtArming,
    /// the watchdog and EndRun's release). No world: placements go through the ArmingPlacer seam and the clear-time
    /// side effects through the manager's existing seams. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class ThreadPuzzleRewardArmingTests
    {
        private static readonly PuzzleGateType[] AllTypes = { PuzzleGateType.Sigil, PuzzleGateType.Beam, PuzzleGateType.Odd, PuzzleGateType.Shuffle };

        private static PuzzleSiteDef Site(string id, float x, float y, float z, PuzzleSiteKind kind = PuzzleSiteKind.Reward,
            PuzzleGateModelKind model = PuzzleGateModelKind.Door, params PuzzleGateType[] types)
            => new PuzzleSiteDef
            {
                Id = id,
                Kind = kind,
                Anchor = new PuzzleSitePointDef { Cell = 0x0150018A, X = x, Y = y, Z = z },
                MaxN = 5,
                Types = (types.Length == 0 ? AllTypes : types).ToList(),
                GateModel = kind == PuzzleSiteKind.Gate ? new PuzzleGateModelDef { Kind = model, Wcid = model == PuzzleGateModelKind.Resident ? 0u : 1006850u, Scale = 1f } : null,
                ShuffleSpots = Enumerable.Range(0, 2).Select(i => new PuzzleSitePointDef { Cell = 0x0150018A, X = x + i, Y = y, Z = z }).ToList(),
            };

        private static ThreadPuzzlePick Gate(PuzzleSiteDef site, PuzzleGateType type) => new ThreadPuzzlePick(site, type, 4, 11, false);

        private static ThreadPuzzlePick Curated(PuzzleSiteDef site) => new ThreadPuzzlePick(site, PuzzleGateType.Odd, 4, 99, true);

        // ---- the selector ------------------------------------------------------------------------------

        [TestMethod]
        public void A_same_floor_site_beats_a_nearer_site_on_another_floor()
        {
            var below = Site("below", 2, 0, -10);    // 10.2 m away in 3D, on another floor
            var level = Site("level", 30, 0, 1);     // 30 m away, same floor
            var player = new Vector3(0, 0, 0);

            var choice = ThreadPuzzleRewardSelector.Select(new[] { below, level }, null, null, 6, player);

            Assert.AreEqual("level", choice.Pick.Site.Id);
            Assert.IsTrue(choice.SameFloor);
            Assert.IsFalse(choice.CuratedFallback);
            Assert.IsTrue(choice.Pick.IsReward);
            Assert.AreEqual(new Vector3(30, 0, 1).Length(), choice.Distance, 0.001f);

            // Discriminating control: with the level site gone, the other-floor site (nearer in 3D) is what is left.
            Assert.AreEqual("below", ThreadPuzzleRewardSelector.Select(new[] { below }, null, null, 6, player).Pick.Site.Id);
        }

        [TestMethod]
        public void Same_floor_is_within_three_metres_of_height_and_the_nearest_wins_within_it()
        {
            var edge = Site("edge", 20, 0, 3.0f);    // exactly 3 m up: same floor
            var near = Site("near", 10, 0, 2.5f);
            var over = Site("over", 5, 0, 3.2f);     // just past 3 m: another floor, though nearest
            var choice = ThreadPuzzleRewardSelector.Select(new[] { edge, near, over }, null, null, 6, Vector3.Zero);

            Assert.AreEqual("near", choice.Pick.Site.Id);
            Assert.IsTrue(choice.SameFloor);
            Assert.AreEqual(3.0f, ThreadPuzzleSitePicker.SameLevelHeight, "the same-floor band is the picker's level band");
        }

        [TestMethod]
        public void With_no_same_floor_site_the_nearest_in_3D_wins()
        {
            var far = Site("far", 40, 0, -10);
            var close = Site("close", 3, 0, 9);
            var choice = ThreadPuzzleRewardSelector.Select(new[] { far, close }, null, null, 6, Vector3.Zero);

            Assert.AreEqual("close", choice.Pick.Site.Id);
            Assert.IsFalse(choice.SameFloor);
            Assert.AreEqual(new Vector3(3, 0, 9).Length(), choice.Distance, 0.01f);
        }

        [TestMethod]
        public void A_used_gate_site_and_any_site_within_the_spacing_of_a_placed_gate_are_excluded()
        {
            var used = Site("used", 1, 0, 0, PuzzleSiteKind.Gate);
            var crowded = Site("crowded", 1, 10, 0);                 // 10 m from the used gate: inside the 16 m spacing
            var open = Site("open", 60, 0, 0, PuzzleSiteKind.Gate);
            var placed = new[] { Gate(used, PuzzleGateType.Beam) };

            var choice = ThreadPuzzleRewardSelector.Select(new[] { used, crowded, open }, placed, null, 6, Vector3.Zero);

            Assert.AreEqual("open", choice.Pick.Site.Id);
            Assert.IsTrue(choice.GateAsReward, "a gate site used as the reward scene");

            // Discriminating control: with no gate placed, the nearest site (the gate site itself) wins.
            Assert.AreEqual("used", ThreadPuzzleRewardSelector.Select(new[] { used, crowded, open }, null, null, 6, Vector3.Zero).Pick.Site.Id);
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsCandidate(crowded, placed), "the spacing rule is the picker's TooClose");
            Assert.IsTrue(ThreadPuzzleRewardSelector.IsCandidate(crowded, null));
        }

        [TestMethod]
        public void A_resident_door_gate_site_is_never_a_reward_site()
        {
            var resident = Site("resident", 1, 0, 0, PuzzleSiteKind.Gate, PuzzleGateModelKind.Resident);
            var other = Site("other", 50, 0, 0);

            Assert.AreEqual("other", ThreadPuzzleRewardSelector.Select(new[] { resident, other }, null, null, 6, Vector3.Zero).Pick.Site.Id);
        }

        [TestMethod]
        public void The_reward_never_repeats_a_type_the_run_gates_used()
        {
            var placedSite = Site("g", 500, 0, 0, PuzzleSiteKind.Gate);
            var sigilOnly = Site("sigil-only", 1, 0, 0, PuzzleSiteKind.Reward, PuzzleGateModelKind.Door, PuzzleGateType.Sigil);
            var any = Site("any", 30, 0, 0);
            var placed = new[] { Gate(placedSite, PuzzleGateType.Sigil) };

            var choice = ThreadPuzzleRewardSelector.Select(new[] { sigilOnly, any }, placed, null, 6, Vector3.Zero);
            Assert.AreEqual("any", choice.Pick.Site.Id, "the nearest site offers only the type a gate already used");

            for (var seed = 1; seed <= 100; seed++)
                Assert.AreNotEqual(PuzzleGateType.Sigil, ThreadPuzzleRewardSelector.Select(new[] { any }, placed, null, seed, Vector3.Zero).Pick.Type, $"seed {seed}");

            // Control: with no gate placed, the sigil-only site is nearest and wins.
            Assert.AreEqual("sigil-only", ThreadPuzzleRewardSelector.Select(new[] { sigilOnly, any }, null, null, 6, Vector3.Zero).Pick.Site.Id);
        }

        [TestMethod]
        public void An_empty_pool_or_no_player_falls_back_to_the_curated_site_and_nothing_at_all_gives_null()
        {
            var curatedSite = Site("curated", 100, 100, 0);
            var curated = Curated(curatedSite);
            var used = Site("used", 1, 0, 0, PuzzleSiteKind.Gate);

            // The curated site is in the current list (the fallback resolves it there by id) but already holds a gate here.
            var empty = ThreadPuzzleRewardSelector.Select(new[] { used, curatedSite }, new[] { Gate(used, PuzzleGateType.Beam), Gate(curatedSite, PuzzleGateType.Odd) }, curated, 6, Vector3.Zero);
            Assert.AreEqual("curated", empty.Pick.Site.Id);
            Assert.IsTrue(empty.CuratedFallback);
            Assert.AreEqual(curated.Type, empty.Pick.Type, "the curated pick is used as planned");

            var noPlayer = ThreadPuzzleRewardSelector.Select(new[] { Site("near", 1, 0, 0), curatedSite }, null, curated, 6, null);
            Assert.AreEqual("curated", noPlayer.Pick.Site.Id, "no clearing player: the curated site, as before this change");
            Assert.IsTrue(noPlayer.CuratedFallback);

            Assert.IsNull(ThreadPuzzleRewardSelector.Select(Array.Empty<PuzzleSiteDef>(), null, null, 6, Vector3.Zero));
        }

        [TestMethod]
        public void The_choice_is_deterministic_per_run_seed_and_the_seed_is_consumed()
        {
            var sites = new[] { Site("a", 10, 0, 0), Site("b", 20, 0, 0) };

            var x = ThreadPuzzleRewardSelector.Select(sites, null, null, 4242, Vector3.Zero).Pick;
            var y = ThreadPuzzleRewardSelector.Select(sites, null, null, 4242, Vector3.Zero).Pick;
            Assert.AreEqual((x.Site.Id, x.Type, x.N, x.Seed), (y.Site.Id, y.Type, y.N, y.Seed));

            var distinct = Enumerable.Range(1, 30)
                .Select(s => ThreadPuzzleRewardSelector.Select(sites, null, null, s, Vector3.Zero).Pick)
                .Select(p => $"{p.Type}:{p.Seed}")
                .Distinct().Count();
            Assert.IsTrue(distinct > 1, "different run seeds draw differently");

            Assert.AreEqual(ThreadPuzzleRewardSelector.StreamSeed(5, "site-x"), ThreadPuzzleRewardSelector.StreamSeed(5, "site-x"), "a stable hash, not string.GetHashCode");
            Assert.AreNotEqual(ThreadPuzzleRewardSelector.StreamSeed(5, "site-x"), ThreadPuzzleRewardSelector.StreamSeed(5, "site-y"));
        }

        [TestMethod]
        public void A_gate_site_used_as_the_reward_gets_the_focal_model()
        {
            var gate = Site("g", 1, 0, 0, PuzzleSiteKind.Gate);
            var choice = ThreadPuzzleRewardSelector.Select(new[] { gate }, null, null, 6, Vector3.Zero);

            Assert.IsTrue(choice.GateAsReward);
            var model = ThreadPuzzlePass.ModelFor(choice.Pick.Site, choice.Pick.IsReward);
            Assert.AreEqual(PuzzleGateForm.Focal, model.Form);
            Assert.AreEqual(PuzzleGateTunables.RewardFocalWcid, model.Wcid);
        }

        // ---- the armed line ----------------------------------------------------------------------------

        [TestMethod]
        public void The_armed_line_names_all_eight_directions_in_whole_metres()
        {
            var d = 10f / MathF.Sqrt(2f);
            var cases = new (Vector3 Anchor, string Dir)[]
            {
                (new Vector3(0, 10, 0), "north"),
                (new Vector3(d, d, 0), "northeast"),
                (new Vector3(10, 0, 0), "east"),
                (new Vector3(d, -d, 0), "southeast"),
                (new Vector3(0, -10, 0), "south"),
                (new Vector3(-d, -d, 0), "southwest"),
                (new Vector3(-10, 0, 0), "west"),
                (new Vector3(-d, d, 0), "northwest"),
            };

            foreach (var (anchor, dir) in cases)
            {
                var member = new Vector3(100, 100, 5);
                Assert.AreEqual(
                    "The dungeon falls quiet, but its reward is sealed. Break the seal to claim it. It lies to the " + dir + ", about 10 m away.",
                    ThreadPuzzleRewardSelector.ArmedLineFor(member, member + anchor), dir);
            }

            Assert.AreEqual("The dungeon falls quiet, but its reward is sealed. Break the seal to claim it. It lies to the east, about 13 m away, 5 m above you.", ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(12, 0, 5)), "with no walk, the distance is 3D, rounded");
            Assert.AreEqual("The dungeon falls quiet, but its reward is sealed. Break the seal to claim it. It lies to the east, about 6 m away.", ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(5.5f, 0, 0)), "5.5 rounds to 6");
        }

        [TestMethod]
        public void Within_five_metres_the_scene_forms_beside_you()
        {
            const string beside = "The dungeon falls quiet, but its reward is sealed. Break the seal to claim it. It forms beside you.";

            Assert.AreEqual(beside, ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(3, 0, 0)));
            Assert.AreEqual(beside, ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(5, 0, 0)), "5 m is within");
            Assert.AreEqual(beside, ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, Vector3.Zero));
            StringAssert.Contains(ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(5.2f, 0, 0)), "It lies to the east, about 5 m away.");
            Assert.AreEqual(PuzzleGateText.RewardArmed + " It lies directly below you, about 9 m away.", ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(0, 0, -9)), "straight below: the vertical is the direction");

            foreach (var line in new[] { beside, PuzzleGateText.RewardArmedToward("to the northwest", 120, true, ", 12 m below you", true) })
                Assert.IsTrue(line.All(c => c < 128), "ASCII only");
        }

        // ---- the pending seal ----------------------------------------------------------------------------

        /// <param name="seams">When given, its site list (ThreadPuzzlePass.SiteSource) is the curated site: the arming
        /// fallback reads the curated site by id from the CURRENT list (review F7), so a store without it refuses.</param>
        private static ThreadDungeonRun PendingRun(out ThreadPuzzlePick curated, Seams seams = null)
        {
            var run = PooledRun();
            run.ClearFraction = 0.9;
            curated = Curated(Site("curated", 10, 10, 0));

            if (seams != null)
                seams.Sites = new[] { curated.Site };

            Assert.IsTrue(run.SealRewardPending(curated));
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

        private static PuzzleGatePlacement Placement(ThreadDungeonRun run)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
            var anchor = new Position(0x0150018A, 10, 10, 0, 0, 0, 0, 1, 0);
            return new PuzzleGatePlacement(920001, options, 7, anchor, 0f, null, 0, null, DateTime.UtcNow, new ThreadPuzzleRunHost(run, true));
        }

        /// <summary>Swaps the clear-time seams (as ThreadPuzzleRewardSealTests does) and the arming placer.</summary>
        private sealed class Seams : IDisposable
        {
            public int Surveys, Gems, Posted, PlacerCalls;
            public ThreadPuzzlePick? LastPick;

            /// <summary>What ThreadPuzzlePass.SiteSource returns while these seams are in place (default: no sites).</summary>
            public IReadOnlyList<PuzzleSiteDef> Sites = Array.Empty<PuzzleSiteDef>();
            private readonly Func<string, IReadOnlyList<PuzzleSiteDef>> siteSource = ThreadPuzzlePass.SiteSource;
            public Func<ThreadDungeonRun, PuzzleGatePlacement> Place = _ => null;
            public Exception Throw;

            private readonly Action<ThreadDungeonRun> gem = ThreadDungeonManager.GemDestroyer;
            private readonly Action<ThreadDungeonRun> survey = ThreadDungeonManager.SurveyRecorder;
            private readonly Action<ThreadDungeonRun, ACE.Server.WorldObjects.Player> reward = ThreadDungeonManager.ClearRewardHandler;
            private readonly Func<ThreadDungeonRun, CacheRequestOutcome> loot = ThreadDungeonManager.PooledLootTrigger;
            private readonly Action<ThreadDungeonRun, Action> post = ThreadPuzzleRunHost.PostToRunLandblock;
            private readonly ThreadPuzzlePass.ArmingPlaceFn placer = ThreadPuzzlePass.ArmingPlacer;
            private readonly Func<ThreadDungeonRun, ACE.Server.WorldObjects.Player, PuzzleGatePlacement> armer = ThreadDungeonManager.RewardArmingPlacer;

            /// <param name="defaultArmer">Leave the production ThreadDungeonManager.RewardArmingPlacer in place.</param>
            /// <param name="defaultPlacer">Leave the production ThreadPuzzlePass.ArmingPlacer in place.</param>
            public Seams(bool defaultArmer = false, bool defaultPlacer = false)
            {
                ThreadDungeonManager.GemDestroyer = _ => Gems++;
                ThreadDungeonManager.SurveyRecorder = _ => Surveys++;
                ThreadDungeonManager.ClearRewardHandler = (_, __) => { };
                ThreadDungeonManager.PooledLootTrigger = _ => CacheRequestOutcome.NotApplicable;
                ThreadPuzzleRunHost.PostToRunLandblock = (_, action) => { Posted++; action(); };
                ThreadPuzzlePass.SiteSource = _ => Sites;
                if (!defaultArmer)
                    ThreadDungeonManager.RewardArmingPlacer = (run, _) => ThreadPuzzlePass.PlaceRewardAtArming(run, null, null);

                if (!defaultPlacer)
                    ThreadPuzzlePass.ArmingPlacer = (ThreadDungeonRun run, ThreadPuzzlePick pick, out string error) =>
                {
                    PlacerCalls++;
                    LastPick = pick;

                    if (Throw != null)
                        throw Throw;

                    var p = Place(run);
                    error = p == null ? "refused (test)" : null;
                    return p;
                };
            }

            public void Dispose()
            {
                ThreadDungeonManager.GemDestroyer = gem;
                ThreadDungeonManager.SurveyRecorder = survey;
                ThreadDungeonManager.ClearRewardHandler = reward;
                ThreadDungeonManager.PooledLootTrigger = loot;
                ThreadPuzzleRunHost.PostToRunLandblock = post;
                ThreadPuzzlePass.ArmingPlacer = placer;
                ThreadDungeonManager.RewardArmingPlacer = armer;
                ThreadPuzzlePass.SiteSource = siteSource;
            }
        }

        [TestMethod]
        public void A_pending_seal_withholds_the_clear_with_nothing_placed()
        {
            var run = PendingRun(out var curated);
            KillAll(run);

            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "sealed pending: every kill landed but the run holds");
            Assert.IsTrue(run.IsRewardSealed);
            Assert.IsTrue(run.IsRewardPending);
            Assert.IsNull(run.RewardPuzzle, "nothing placed at populate");
            Assert.AreEqual(curated, run.RewardFallbackPick);
            Assert.IsTrue(run.IsRewardArmed);

            // Control: the same run shape with no seal clears on the same kills.
            var open = PooledRun();
            open.ClearFraction = 0.9;
            open.MarkPopulated(10, 10, 0);
            KillAll(open);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, open.State);

            Assert.IsFalse(run.SealReward(new object()), "one reward scene per run: a pending seal refuses a second seal");
            Assert.IsFalse(run.SealRewardPending(curated));
        }

        [TestMethod]
        public void A_successful_arming_placement_attaches_and_keeps_the_seal()
        {
            using var s = new Seams();
            var run = PendingRun(out var curated, s);
            var placement = Placement(run);
            s.Place = _ => placement;

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run); // the arming entry point that needs no Creature

            Assert.AreEqual(1, s.PlacerCalls);
            Assert.AreEqual("curated", s.LastPick.Value.Site.Id, "no player: the curated site, read from the current site list");
            Assert.IsTrue(s.LastPick.Value.IsReward);
            Assert.AreSame(placement, run.RewardPuzzle, "attached");
            Assert.IsFalse(run.IsRewardPending);
            Assert.IsTrue(run.IsRewardSealed, "the seal now waits for the solve");
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.AreEqual(0, s.Surveys);

            ThreadDungeonManager.OnRunPopulated(run);
            Assert.AreEqual(1, s.PlacerCalls, "arming places once");

            new ThreadPuzzleRunHost(run, true).OnSolved(placement, null);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "the solve clears the run as before");
            Assert.AreEqual(1, s.Surveys);
        }

        /// <summary>Review F7, end to end: a store that no longer lists the curated site (a reload dropped or renamed it) gives no fallback, and the run fails open.</summary>
        [TestMethod]
        public void Arming_does_not_place_the_curated_site_when_the_current_list_lacks_it()
        {
            using var s = new Seams();   // Sites stays empty: the reloaded store has no "curated"
            var run = PendingRun(out _);
            s.Place = _ => Placement(run);

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run);

            Assert.AreEqual(0, s.PlacerCalls, "no current site: nothing placed");
            Assert.IsNull(run.RewardPuzzle);
            Assert.IsFalse(run.IsRewardSealed, "fail-open");
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
        }

        [TestMethod]
        public void The_arming_placement_is_not_attempted_before_the_clear_fraction()
        {
            using var s = new Seams();
            var run = PendingRun(out _, s);
            KillAll(run, 8);

            ThreadDungeonManager.OnRunPopulated(run);
            Assert.AreEqual(0, s.PlacerCalls, "8/10 is below 0.9: not armed, nothing placed");
            Assert.IsTrue(run.IsRewardPending);

            run.RecordKill(false);
            ThreadDungeonManager.OnRunPopulated(run);
            Assert.AreEqual(1, s.PlacerCalls, "9/10 arms");
        }

        [TestMethod]
        public void A_refused_arming_placement_unseals_at_once_and_the_run_clears()
        {
            using var s = new Seams();
            var run = PendingRun(out _, s);
            s.Place = _ => null;

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run);

            Assert.AreEqual(1, s.PlacerCalls);
            Assert.IsFalse(run.IsRewardSealed, "fail-open");
            Assert.IsFalse(run.IsRewardPending);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.AreEqual(1, s.Surveys, "the clear announced once");
            Assert.AreEqual(0, s.Posted, "the arming kill is on the run's own thread: the follow-up runs inline");
        }

        [TestMethod]
        public void A_throwing_arming_placement_unseals_and_the_run_clears()
        {
            using var s = new Seams();
            var run = PendingRun(out _);
            s.Throw = new InvalidOperationException("boom");

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run);

            Assert.IsFalse(run.IsRewardSealed);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.AreEqual(1, s.Surveys);
        }

        [TestMethod]
        public void A_run_that_ended_while_pending_is_never_left_sealed()
        {
            using var s = new Seams();

            // EndRun's release (it runs after MarkEnded).
            var ended = PendingRun(out _);
            Assert.IsTrue(ended.MarkEnded("test"));
            Assert.IsTrue(ThreadPuzzlePass.ReleasePendingSealOnEnd(ended));
            Assert.IsFalse(ended.IsRewardSealed);
            Assert.IsFalse(ThreadPuzzlePass.ReleasePendingSealOnEnd(ended), "once");

            // A placed (not pending) seal is left to ClearForRun's OnRemoved, never released here.
            var placed = PooledRun();
            Assert.IsTrue(placed.SealReward(new object()));
            Assert.IsFalse(ThreadPuzzlePass.ReleasePendingSealOnEnd(placed));
            Assert.IsTrue(placed.IsRewardSealed);

            // The arming placer reached on a run that is no longer Active unseals without placing.
            var ending = PendingRun(out _);
            KillAll(ending);
            Assert.IsTrue(ending.MarkEnded("test"));
            Assert.IsNull(ThreadPuzzlePass.PlaceRewardAtArming(ending, Vector3.Zero, "x"));
            Assert.AreEqual(0, s.PlacerCalls, "nothing placed into an ending run");
            Assert.IsFalse(ending.IsRewardSealed);

            // A run that ends between the placement and the attach: the attach is refused.
            var raced = PendingRun(out _);
            KillAll(raced);
            var placement = Placement(raced);
            s.Place = r => { r.MarkEnded("raced"); return placement; };
            Assert.IsNull(ThreadPuzzlePass.PlaceRewardAtArming(raced, Vector3.Zero, "x"));
            Assert.IsNull(raced.RewardPuzzle);
            Assert.IsFalse(raced.IsRewardSealed);
        }

        [TestMethod]
        public void EndRun_releases_a_pending_seal_after_marking_the_run_ended()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static void EndRun(ThreadDungeonRun run, string why)");

            var ended = body.IndexOf("run.MarkEnded(why, out var priorState)", StringComparison.Ordinal);
            var clear = body.IndexOf("PuzzleGateManager.ClearForRun(run.RunId);", StringComparison.Ordinal);
            var release = body.IndexOf("ThreadPuzzlePass.ReleasePendingSealOnEnd(run)", StringComparison.Ordinal);

            Assert.IsTrue(ended >= 0 && ended < clear && clear < release, "EndRun lifts a pending seal, after MarkEnded and the placement sweep");
        }

        [TestMethod]
        public void The_watchdog_holds_a_pending_seal_until_armed_then_unseals_it_after_the_grace()
        {
            using var s = new Seams();
            var reap = ThreadPuzzleWatchdog.ReapRun;

            try
            {
                ThreadPuzzleWatchdog.ReapRun = (_, __) => 0;
                var run = PendingRun(out _);
                var t0 = DateTime.UtcNow;

                KillAll(run, 8);
                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, t0), "pending and not armed: the seal is waiting legitimately");
                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, t0.AddMinutes(30)), "however long the kills take");
                Assert.IsTrue(run.IsRewardSealed);

                run.RecordKill(false); // armed, but the arming placement never ran
                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, t0.AddMinutes(31)), "first sight starts the stall clock");
                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, t0.AddMinutes(31).AddSeconds(5)), "inside the grace");
                Assert.IsTrue(ThreadPuzzleWatchdog.Check(run, t0.AddMinutes(31).Add(ThreadDungeonRun.PendingArmedGrace)), "past the grace: unseal");
                Assert.IsFalse(run.IsRewardSealed);
                Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
                Assert.AreEqual(1, s.Posted, "world thread: the follow-up is posted to the run landblock");
                Assert.AreEqual(1, s.Surveys);
            }
            finally
            {
                ThreadPuzzleWatchdog.ReapRun = reap;
            }
        }

        [TestMethod]
        public void The_pass_defers_the_reward_to_arming_and_places_only_gates_at_populate()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadPuzzlePass.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static int Run(ThreadDungeonRun run, Landblock landblock)");

            var reward = body.IndexOf("if (pick.IsReward)", StringComparison.Ordinal);
            var pending = body.IndexOf("DeferReward(run, pick)", StringComparison.Ordinal);
            var skip = pending < 0 ? -1 : body.IndexOf("continue;", pending, StringComparison.Ordinal);
            var place = body.IndexOf("TryPlace(run, landblock, pick)", StringComparison.Ordinal);

            Assert.IsTrue(reward >= 0 && reward < pending && pending < skip && skip < place, "a reward pick is deferred (sealed pending) and skipped before TryPlace");

            // DeferReward seals pending, then prebuilds the walk graph (behaviour: ThreadPuzzleWalkDistanceTests
            // Deferring_the_reward_at_populate_builds_the_walk_graph_so_arming_reads_the_cache).
            var defer = PooledLootSourceText.MethodBody(src, "internal static bool DeferReward(ThreadDungeonRun run, ThreadPuzzlePick pick)");
            var seal = defer.IndexOf("run.SealRewardPending(pick)", StringComparison.Ordinal);
            var build = defer.IndexOf("TryGetWalkGraph(run,", StringComparison.Ordinal);
            Assert.IsTrue(seal >= 0 && seal < build, "sealed, then the graph is built at populate");
        }

        // ---- review F1/F3: the clearing player ------------------------------------------------------------

        private static ThreadDungeonRun GroupRun(uint owner, params uint[] members)
        {
            var seats = new List<RosterSeat> { new RosterSeat(owner, "Owner", 7, 150) };
            seats.AddRange(members.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));
            var n = ThreadDungeonRun.OrderRoster(seats).Count;
            var group = GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        [TestMethod]
        public void The_clearing_player_chain_runs_killer_pet_owner_top_damagers_owner_then_roster()
        {
            var damagers = new[] { new ThreadClearingPlayer.Damager(4, 10f), new ThreadClearingPlayer.Damager(5, 50f), new ThreadClearingPlayer.Damager(3, 99f) };
            var candidates = ThreadClearingPlayer.Candidates(1, 2, 3, damagers, 6, new uint[] { 6, 7, 8 });

            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 5, 4, 6, 7, 8 }, candidates, "killer, pet owner, top, damagers by damage (repeats dropped), owner, roster");

            // Each tier wins only when everything ahead of it is ineligible.
            var expected = new uint[] { 1, 2, 3, 5, 4, 6, 7, 8 };

            for (var i = 0; i < expected.Length; i++)
            {
                var ineligible = new HashSet<uint>(expected.Take(i));
                Assert.AreEqual(expected[i], ThreadClearingPlayer.Resolve(candidates, g => !ineligible.Contains(g)), $"tier {i}");
            }

            Assert.IsNull(ThreadClearingPlayer.Resolve(candidates, _ => false), "nobody eligible: null (the placer then uses the curated site)");
            CollectionAssert.AreEqual(new uint[] { 6, 7 }, ThreadClearingPlayer.Candidates(null, null, null, null, 6, new uint[] { 6, 7 }), "the fallback chain is owner then roster");
        }

        [TestMethod]
        public void A_puzzle_removed_member_or_a_non_roster_player_is_never_the_clearing_player()
        {
            const uint owner = 0x50000010, a = 0x50000011, admin = 0x50000099;
            var run = GroupRun(owner, a);
            var candidates = ThreadClearingPlayer.Candidates(a, null, null, null, owner, run.Roster.Select(m => m.Guid));
            bool Eligible(uint g) => ThreadClearingPlayer.IsEligible(run, g, online: true, inCopy: true);

            // Control: A dealt the killing blow and is chosen.
            Assert.AreEqual(a, ThreadClearingPlayer.Resolve(candidates, Eligible));

            // MarkPuzzleRemoved lands before the eject teleport does, so A can still be standing in the copy.
            Assert.IsTrue(run.MarkPuzzleRemoved(a));
            Assert.AreEqual(owner, ThreadClearingPlayer.Resolve(candidates, Eligible), "a removed member is skipped");

            // An admin inside the copy who is not on the roster is never chosen, even with the killing blow.
            var adminKill = ThreadClearingPlayer.Candidates(admin, null, null, null, owner, run.Roster.Select(m => m.Guid));
            Assert.AreEqual(owner, ThreadClearingPlayer.Resolve(adminKill, Eligible));
            Assert.IsFalse(ThreadClearingPlayer.IsEligible(run, admin, true, true));
        }

        [TestMethod]
        public void An_offline_or_outside_candidate_is_not_eligible()
        {
            const uint owner = 0x50000020, a = 0x50000021;
            var run = GroupRun(owner, a);

            Assert.IsTrue(ThreadClearingPlayer.IsEligible(run, a, online: true, inCopy: true), "control");
            Assert.IsFalse(ThreadClearingPlayer.IsEligible(run, a, online: false, inCopy: true), "the damage history's WeakReference can outlive a logout");
            Assert.IsFalse(ThreadClearingPlayer.IsEligible(run, a, online: true, inCopy: false), "outside the copy");
            Assert.IsFalse(ThreadClearingPlayer.IsEligible(null, a, true, true));

            var solo = PooledRun();
            Assert.IsTrue(ThreadClearingPlayer.IsEligible(solo, solo.OwnerGuid, true, true), "a solo run's owner is its roster");
        }

        // ---- review F2: the per-recipient line and the production placers ---------------------------------

        [TestMethod]
        public void A_recipient_outside_the_copy_or_without_an_anchor_gets_the_plain_line()
        {
            var member = new Vector3(0, 0, 0);
            var anchor = new Vector3(0, 20, 0);

            Assert.AreEqual("The dungeon falls quiet, but its reward is sealed. Break the seal to claim it. It lies to the north, about 20 m away.", ThreadPuzzleRewardSelector.ArmedLineForRecipient(true, member, anchor), "control: inside");
            Assert.AreEqual(PuzzleGateText.RewardArmed, ThreadPuzzleRewardSelector.ArmedLineForRecipient(false, member, anchor), "outside the copy: a direction would be meaningless");
            Assert.AreEqual(PuzzleGateText.RewardArmed, ThreadPuzzleRewardSelector.ArmedLineForRecipient(true, member, null), "no anchor");
            Assert.AreEqual(PuzzleGateText.RewardArmed, ThreadPuzzleRewardSelector.ArmedLineForRecipient(true, null, anchor), "no position");
        }

        [TestMethod]
        public void Arming_through_the_production_reward_arming_placer_places_at_the_curated_site_when_nobody_is_inside()
        {
            using var s = new Seams(defaultArmer: true);
            var run = PendingRun(out _, s);
            var placement = Placement(run);
            s.Place = _ => placement;

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run);

            Assert.AreEqual(1, s.PlacerCalls, "the production RewardArmingPlacer reached PlaceRewardAtArming");
            Assert.AreEqual("curated", s.LastPick.Value.Site.Id, "no online member inside: no position, so the curated site");
            Assert.AreSame(placement, run.RewardPuzzle);
            Assert.IsTrue(run.IsRewardSealed);
        }

        [TestMethod]
        public void Arming_through_both_production_placers_fails_open_when_the_run_landblock_is_gone()
        {
            using var s = new Seams(defaultArmer: true, defaultPlacer: true);
            var run = PendingRun(out _, s);

            KillAll(run);
            ThreadDungeonManager.OnRunPopulated(run);

            // No ephemeral landblock exists for this instance in the harness: PlaceOnRunLandblock reports it gone,
            // and the run unseals and clears rather than staying sealed.
            Assert.IsFalse(run.IsRewardSealed);
            Assert.IsNull(run.RewardPuzzle);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.AreEqual(1, s.Surveys);
        }

        // ---- review F4: a refused attach on a live run leaves no orphan -----------------------------------

        [TestMethod]
        public void A_placement_the_live_run_refused_to_attach_is_cleared()
        {
            using var s = new Seams();
            var run = PendingRun(out _, s);
            KillAll(run);

            var placement = Placement(run);
            PuzzleGateManager.TrackForTest(placement);

            try
            {
                // A fail-open path lifts the seal between the claim and the attach; the run is NOT ended.
                s.Place = r => { r.UnsealReward(); return placement; };

                Assert.IsNull(ThreadPuzzlePass.PlaceRewardAtArming(run, Vector3.Zero, "x"));
                Assert.AreNotEqual(ThreadDungeonRunState.Ended, run.State);
                Assert.IsFalse(PuzzleGateManager.IsRegistered(placement), "the orphan is cleared");
                Assert.AreEqual(PuzzlePlacementState.Cleared, placement.State);
            }
            finally
            {
                PuzzleGateManager.UntrackForTest(placement);
            }

            // Control: an attached placement stays registered.
            var ok = PendingRun(out _);
            KillAll(ok);
            var kept = new PuzzleGatePlacement(920002, placement.Options, 7, placement.Anchor, 0f, null, 0, null, DateTime.UtcNow, new ThreadPuzzleRunHost(ok, true));
            PuzzleGateManager.TrackForTest(kept);

            try
            {
                s.Place = _ => kept;
                Assert.AreSame(kept, ThreadPuzzlePass.PlaceRewardAtArming(ok, Vector3.Zero, "x"));
                Assert.IsTrue(PuzzleGateManager.IsRegistered(kept));
            }
            finally
            {
                PuzzleGateManager.UntrackForTest(kept);
            }
        }

        // ---- 2026-10-07: the arming placer measures on foot from the clearing player's cell -------------------

        [TestMethod]
        public void The_arming_placer_chooses_by_walk_from_the_clearing_players_cell_and_falls_back_without_one()
        {
            const uint a = 0x01500101, b = 0x01500102, c = 0x01500103, d = 0x01500104;

            // c is 8 m north through a wall (68 m on foot round through b); d is 25 m west through an open door.
            var graph = new DungeonWalkGraph(new[] { a, b, c, d }, new[]
            {
                new DungeonWalkGraph.Portal(a, b, new Vector3(30, 0, 0)), new DungeonWalkGraph.Portal(b, a, new Vector3(30, 0, 0)),
                new DungeonWalkGraph.Portal(b, c, new Vector3(30, 8, 0)), new DungeonWalkGraph.Portal(c, b, new Vector3(30, 8, 0)),
                new DungeonWalkGraph.Portal(a, d, new Vector3(-12, 0, 0)), new DungeonWalkGraph.Portal(d, a, new Vector3(-12, 0, 0)),
            });

            var wall = Site("wall", 0, 8, 0);
            wall.Anchor.Cell = c;
            var open = Site("open", -25, 0, 0);
            open.Anchor.Cell = d;

            var siteSource = ThreadPuzzlePass.SiteSource;
            var graphSource = ThreadPuzzlePass.WalkGraphSource;
            var unsolved = ThreadPuzzlePass.UnsolvedGateSiteIds;
            ushort askedFor = 0;

            using var s = new Seams();
            ThreadPuzzlePass.SiteSource = _ => new[] { wall, open };
            ThreadPuzzlePass.WalkGraphSource = lb => { askedFor = lb; return graph; };
            ThreadPuzzlePass.UnsolvedGateSiteIds = _ => Array.Empty<string>();

            try
            {
                var run = PendingRun(out _);
                KillAll(run);
                var placement = Placement(run);
                s.Place = _ => placement;

                Assert.AreSame(placement, ThreadPuzzlePass.PlaceRewardAtArming(run, Vector3.Zero, "x", a));
                Assert.AreEqual("open", s.LastPick.Value.Site.Id, "25 m on foot beats 8 m through a wall");
                Assert.AreEqual(run.Dungeon.Landblock, askedFor, "the graph is the run dungeon's landblock");

                // Control: no player cell, so no walk - the straight-line rule picks the site behind the wall.
                var straight = PendingRun(out _);
                KillAll(straight);
                var other = Placement(straight);
                s.Place = _ => other;

                Assert.AreSame(other, ThreadPuzzlePass.PlaceRewardAtArming(straight, Vector3.Zero, "x"));
                Assert.AreEqual("wall", s.LastPick.Value.Site.Id);

                // And a missing graph falls back the same way.
                ThreadPuzzlePass.WalkGraphSource = _ => null;
                var noGraph = PendingRun(out _);
                KillAll(noGraph);
                var third = Placement(noGraph);
                s.Place = _ => third;

                Assert.AreSame(third, ThreadPuzzlePass.PlaceRewardAtArming(noGraph, Vector3.Zero, "x", a));
                Assert.AreEqual("wall", s.LastPick.Value.Site.Id);
            }
            finally
            {
                ThreadPuzzlePass.SiteSource = siteSource;
                ThreadPuzzlePass.WalkGraphSource = graphSource;
                ThreadPuzzlePass.UnsolvedGateSiteIds = unsolved;
            }
        }
    }
}

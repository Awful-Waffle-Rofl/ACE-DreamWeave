using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The 2026-10-07 playtest pass on the reward scene: walk distance over the dungeon's cell graph
    /// (DungeonWalkGraph, ThreadPuzzleRewardSelector's walk rule, ThreadPuzzlePass.BlockedDoorways), the armed line's
    /// vertical and boss-chamber clauses, the boss-chamber rule, the reward scene's full teardown on its solve
    /// (PuzzleGateManager.RemoveGateForSolve / FinishSolve), and one graph built from the real cell dat.
    /// </summary>
    [TestClass]
    public class ThreadPuzzleWalkDistanceTests
    {
        private const uint A = 0x01500101, B = 0x01500102, C = 0x01500103, D = 0x01500104, E = 0x01500105;

        private static readonly PuzzleGateType[] AllTypes = { PuzzleGateType.Sigil, PuzzleGateType.Beam, PuzzleGateType.Odd, PuzzleGateType.Shuffle };

        /// <summary>Both directions of one doorway between two cells.</summary>
        private static IEnumerable<DungeonWalkGraph.Portal> Door(uint a, uint b, float x, float y, float z = 0)
        {
            yield return new DungeonWalkGraph.Portal(a, b, new Vector3(x, y, z));
            yield return new DungeonWalkGraph.Portal(b, a, new Vector3(x, y, z));
        }

        /// <summary>
        /// The player stands in A at the origin. C (site "wall") sits 8 m north, through a wall: the only way in is east
        /// through B (A-B at (30,0), B-C at (30,8)), a 68 m walk. D (site "open") sits 25 m west through an open
        /// doorway at (-12,0): a 25 m walk. E is a cell with no doorway at all.
        /// </summary>
        private static DungeonWalkGraph Maze()
            => new DungeonWalkGraph(new[] { A, B, C, D, E },
                Door(A, B, 30, 0).Concat(Door(B, C, 30, 8)).Concat(Door(A, D, -12, 0)));

        private static PuzzleSiteDef Site(string id, uint cell, float x, float y, float z, PuzzleSiteKind kind = PuzzleSiteKind.Reward, float yaw = 0f)
            => new PuzzleSiteDef
            {
                Id = id,
                Kind = kind,
                Anchor = new PuzzleSitePointDef { Cell = cell, X = x, Y = y, Z = z },
                Yaw = yaw,
                MaxN = 5,
                Types = AllTypes.ToList(),
                GateModel = kind == PuzzleSiteKind.Gate ? new PuzzleGateModelDef { Kind = PuzzleGateModelKind.Door, Wcid = 1006850u, Scale = 1f } : null,
                ShuffleSpots = Enumerable.Range(0, 2).Select(i => new PuzzleSitePointDef { Cell = cell, X = x + i, Y = y, Z = z }).ToList(),
            };

        // ---- the pure Dijkstra ------------------------------------------------------------------------------

        [TestMethod]
        public void The_walk_goes_round_through_the_doorways_and_never_through_a_wall()
        {
            var field = Maze().From(A, Vector3.Zero);

            Assert.AreEqual(68f, field.DistanceTo(C, new Vector3(0, 8, 0)).Value, 0.001f, "30 to the A-B door, 8 to the B-C door, 30 back west");
            Assert.AreEqual(25f, field.DistanceTo(D, new Vector3(-25, 0, 0)).Value, 0.001f, "12 to the A-D door, 13 on");
            Assert.AreEqual(5f, field.DistanceTo(A, new Vector3(3, 4, 0)).Value, 0.001f, "the start cell is a straight line");
            Assert.IsNull(field.DistanceTo(E, new Vector3(1, 0, 0)), "a cell no doorway reaches");
            Assert.IsNull(field.DistanceTo(0x01509999, Vector3.Zero), "a cell the graph does not know");
            Assert.IsNull(Maze().From(0x01509999, Vector3.Zero), "a start cell the graph does not know: no field");
        }

        [TestMethod]
        public void A_blocked_doorway_is_never_passed_in_either_direction()
        {
            var graph = Maze();
            var blocked = new HashSet<(uint, uint)> { DungeonWalkGraph.Pair(D, A) };

            Assert.IsNull(graph.From(A, Vector3.Zero, blocked).DistanceTo(D, new Vector3(-25, 0, 0)), "A to D through the closed door");
            Assert.IsNull(graph.From(D, new Vector3(-25, 0, 0), blocked).DistanceTo(A, Vector3.Zero), "and D to A");
            Assert.AreEqual(68f, graph.From(A, Vector3.Zero, blocked).DistanceTo(C, new Vector3(0, 8, 0)).Value, 0.001f, "other doorways stay open");
            Assert.AreEqual(25f, graph.From(A, Vector3.Zero).DistanceTo(D, new Vector3(-25, 0, 0)).Value, 0.001f, "control: unblocked");
        }

        // ---- the selector ------------------------------------------------------------------------------------

        [TestMethod]
        public void The_walk_rule_picks_the_site_nearer_on_foot_over_the_one_nearer_in_a_straight_line()
        {
            // The playtest feedback: "player walk distance" over "dungeon geometric distance".
            var wall = Site("wall", C, 0, 8, 0);     // 8 m straight, 68 m on foot
            var open = Site("open", D, -25, 0, 0);   // 25 m straight, 25 m on foot
            var sites = new[] { wall, open };
            var field = Maze().From(A, Vector3.Zero);

            var walked = ThreadPuzzleRewardSelector.Select(sites, null, null, 6, Vector3.Zero, field.DistanceTo);

            Assert.AreEqual("open", walked.Pick.Site.Id);
            Assert.AreEqual(ThreadPuzzleRewardSelector.RuleWalk, walked.Rule);
            Assert.AreEqual(25f, walked.Walk.Value, 0.001f);
            Assert.AreEqual(25f, walked.Distance, 0.001f, "the straight distance is still reported");

            // Discriminating control: the same sites with no walk measure fall to the straight-line rule.
            var straight = ThreadPuzzleRewardSelector.Select(sites, null, null, 6, Vector3.Zero);
            Assert.AreEqual("wall", straight.Pick.Site.Id);
            Assert.AreEqual(ThreadPuzzleRewardSelector.RuleStraight, straight.Rule);
            Assert.IsNull(straight.Walk);
        }

        [TestMethod]
        public void Walk_drops_the_same_floor_preference_because_the_walk_prices_the_stairs()
        {
            // "upstairs" is 3.5 m up (another floor by the straight rule) but 10 m on foot; "level" is on the floor, 30 m.
            var graph = new DungeonWalkGraph(new[] { A, B, C }, Door(A, B, 5, 0, 0).Concat(Door(A, C, -15, 0, 0)));
            var upstairs = Site("upstairs", B, 5, 5, 3.5f);
            var level = Site("level", C, -30, 0, 0);
            var field = graph.From(A, Vector3.Zero);

            Assert.AreEqual("upstairs", ThreadPuzzleRewardSelector.Select(new[] { level, upstairs }, null, null, 6, Vector3.Zero, field.DistanceTo).Pick.Site.Id);
            Assert.AreEqual("level", ThreadPuzzleRewardSelector.Select(new[] { level, upstairs }, null, null, 6, Vector3.Zero).Pick.Site.Id, "control: the straight rule keeps the floor");
        }

        [TestMethod]
        public void An_unreachable_site_is_excluded_by_the_walk_rule()
        {
            var cutOff = Site("cut-off", E, 1, 0, 0);  // nearest of all in a straight line, no doorway to it
            var open = Site("open", D, -25, 0, 0);
            var field = Maze().From(A, Vector3.Zero);

            Assert.AreEqual("open", ThreadPuzzleRewardSelector.Select(new[] { cutOff, open }, null, null, 6, Vector3.Zero, field.DistanceTo).Pick.Site.Id);
            Assert.AreEqual("cut-off", ThreadPuzzleRewardSelector.Select(new[] { cutOff, open }, null, null, 6, Vector3.Zero).Pick.Site.Id, "control");
        }

        [TestMethod]
        public void With_nothing_reachable_on_foot_the_straight_rule_applies_and_says_why()
        {
            var cutOff = Site("cut-off", E, 1, 0, 0);
            var field = Maze().From(A, Vector3.Zero);

            var choice = ThreadPuzzleRewardSelector.Select(new[] { cutOff }, null, null, 6, Vector3.Zero, field.DistanceTo);

            Assert.AreEqual("cut-off", choice.Pick.Site.Id);
            Assert.AreEqual(ThreadPuzzleRewardSelector.RuleStraight, choice.Rule);
            Assert.AreEqual("none-reachable", choice.WalkNote);
            Assert.IsNull(choice.Walk);
        }

        [TestMethod]
        public void A_closed_gate_blocks_its_doorway_and_the_walk_goes_the_long_way()
        {
            var graph = Maze();
            var wall = Site("wall", C, 0, 8, 0);
            var open = Site("open", D, -25, 0, 0);

            // A gate site 5 m east of the A-D doorway facing west (yaw 90: forward = (-1, 0)); its gate stands the
            // default 7 m ahead, at (-12, 0) - in the doorway.
            var gate = Site("g1", A, -5, 0, 0, PuzzleSiteKind.Gate, 90f);
            var gatePoint = ThreadPuzzlePass.GatePoint(gate);
            Assert.AreEqual(-12f, gatePoint.X, 0.001f);
            Assert.AreEqual(0f, gatePoint.Y, 0.001f);

            var sites = new[] { wall, open, gate };
            var blocked = ThreadPuzzlePass.BlockedDoorways(graph, sites, new[] { "g1" });
            CollectionAssert.AreEquivalent(new[] { DungeonWalkGraph.Pair(A, D) }, blocked.ToList());

            var closed = graph.From(A, Vector3.Zero, blocked);
            Assert.AreEqual("wall", ThreadPuzzleRewardSelector.Select(new[] { wall, open }, null, null, 6, Vector3.Zero, closed.DistanceTo).Pick.Site.Id,
                "the open site is behind the closed gate: the long way round wins");

            Assert.AreEqual(0, ThreadPuzzlePass.BlockedDoorways(graph, sites, Array.Empty<string>()).Count, "control: a solved gate blocks nothing");
            Assert.AreEqual("open", ThreadPuzzleRewardSelector.Select(new[] { wall, open }, null, null, 6, Vector3.Zero, graph.From(A, Vector3.Zero).DistanceTo).Pick.Site.Id);
        }

        [TestMethod]
        public void A_gate_with_no_doorway_within_reach_blocks_nothing()
        {
            var graph = Maze();
            var gate = Site("far", A, 0, -50, 0, PuzzleSiteKind.Gate, 0f);   // gate at (0,-43): no portal within 3 m
            Assert.AreEqual(0, ThreadPuzzlePass.BlockedDoorways(graph, new[] { gate }, new[] { "far" }).Count);

            Assert.IsNotNull(graph.NearestDoorway(new Vector3(-12, 0, -2)), "a portal centroid 2 m above the gate's floor (mid-doorway) is found");
            Assert.IsNotNull(graph.NearestDoorway(new Vector3(-12, 0, 0.5f)), "half a metre below the floor is found");
            Assert.IsNull(graph.NearestDoorway(new Vector3(-12, 0, 2)), "2 m below the gate's floor is another floor's doorway");
            Assert.IsNotNull(graph.NearestDoorway(new Vector3(-12, 2.9f, 0)), "within 3 m horizontally");
            Assert.IsNull(graph.NearestDoorway(new Vector3(-12, 3.1f, 0)));
        }

        // ---- the armed line ----------------------------------------------------------------------------------

        private const string Base = "The dungeon falls quiet, but its reward is sealed. Break the seal to claim it.";

        [TestMethod]
        public void The_armed_line_says_beside_you_within_five_metres_on_foot_or_straight()
        {
            Assert.AreEqual(Base + " It forms beside you.", ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(4, 0, 0), 4.5f, false));
            Assert.AreEqual(Base + " It forms beside you.", ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(4, 0, 0), null, false), "no walk: straight");

            // 4 m away in a straight line but 40 m on foot (the far side of a wall): not beside.
            Assert.AreEqual(Base + " It lies to the east, about 40 m away on foot.", ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(4, 0, 0), 40f, false));
        }

        [TestMethod]
        public void The_armed_line_uses_the_walk_and_the_compass_and_falls_back_to_a_straight_line()
        {
            Assert.AreEqual(Base + " It lies to the northeast, about 64 m away on foot, 12 m below you, in the boss's chamber.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(30, 30, -12), 64.2f, true), "the orchestrator's example");

            Assert.AreEqual(Base + " It lies to the north, about 20 m away.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(0, 20, 0), null, false), "no walk for this member: straight metres, no 'on foot'");

            Assert.AreEqual(Base + " It lies to the north, about 21 m away on foot.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(0, 20, 0), 20.5f, false), "20.5 rounds away from zero");
        }

        [TestMethod]
        public void The_vertical_clause_starts_at_three_metres_and_names_above_or_below()
        {
            Assert.AreEqual(Base + " It lies to the east, about 30 m away on foot.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(20, 0, 2.9f), 30f, false), "under 3 m: no clause");
            Assert.AreEqual(Base + " It lies to the east, about 30 m away on foot, 3 m above you.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(20, 0, 3f), 30f, false), "3 m: the clause");
            Assert.AreEqual(Base + " It lies to the east, about 30 m away on foot, 7 m below you.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(20, 0, -6.5f), 30f, false), "6.5 rounds to 7");
        }

        [TestMethod]
        public void Directly_above_or_below_under_two_metres_of_horizontal_offset_and_no_second_vertical_clause()
        {
            Assert.AreEqual(Base + " It lies directly above you, about 25 m away on foot.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(1.9f, 0, 12), 25f, false));
            Assert.AreEqual(Base + " It lies directly below you, about 25 m away on foot, in the boss's chamber.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(0, -1, -12), 25f, true));

            // 2 m of horizontal offset: the compass again, with its vertical clause.
            Assert.AreEqual(Base + " It lies to the east, about 25 m away on foot, 12 m above you.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(2f, 0, 12), 25f, false));
        }

        [TestMethod]
        public void Every_armed_line_constant_and_branch_is_ascii()
        {
            var lines = new[]
            {
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(30, 30, -12), 64f, true),
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(0, 0, 12), 25f, true),
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(0, 0, 0), 40f, false),
                PuzzleGateText.RewardArmedBossChamber,
            };

            foreach (var line in lines)
                Assert.IsTrue(line.All(c => c < 128), "ASCII only: " + line);

            Assert.AreEqual(Base + " It lies nearby, about 40 m away on foot.", lines[2], "the uncovered branch: same spot, far on foot");
        }

        [TestMethod]
        public void The_notice_measures_each_member_on_foot_and_falls_back_per_member()
        {
            var graph = Maze();
            var notice = new ThreadPuzzlePass.RewardArmedNotice(graph, null, new Vector3(0, 8, 0), C, false);

            Assert.AreEqual(Base + " It lies to the north, about 68 m away on foot.", notice.LineFor(true, A, Vector3.Zero));
            Assert.AreEqual(Base + " It lies to the north, about 8 m away.", notice.LineFor(true, 0, Vector3.Zero), "a member with no cell: straight");
            Assert.AreEqual(Base + " It lies to the north, about 8 m away.", notice.LineFor(true, E, Vector3.Zero), "a member the walk cannot reach the scene from: straight");
            Assert.AreEqual(PuzzleGateText.RewardArmed, notice.LineFor(false, A, Vector3.Zero), "outside the copy: the plain line");
        }

        // ---- the boss's chamber -------------------------------------------------------------------------------

        [TestMethod]
        public void The_boss_chamber_is_the_boss_cell_or_fifteen_metres_on_foot_on_its_floor()
        {
            var boss = new Vector3(100, 100, -30);

            Assert.IsTrue(ThreadPuzzleRewardSelector.IsBossChamber(B, new Vector3(140, 100, -10), B, boss, null), "same cell, whatever the distance");
            Assert.IsTrue(ThreadPuzzleRewardSelector.IsBossChamber(A, new Vector3(110, 100, -30), B, boss, 15f), "15 m on foot");
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsBossChamber(A, new Vector3(110, 100, -30), B, boss, 15.1f));
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsBossChamber(A, new Vector3(110, 100, -27), B, boss, 10f), "3 m of height: another floor");
            Assert.IsTrue(ThreadPuzzleRewardSelector.IsBossChamber(A, new Vector3(110, 100, -27.1f), B, boss, 10f));
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsBossChamber(A, new Vector3(101, 100, -30), B, boss, null), "no walk: only the same cell counts");
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsBossChamber(B, boss, null, null, 0f), "no boss anchor: never");
        }

        // ---- the reward scene tears down on its solve ---------------------------------------------------------

        private static int nextGuid = 0x7F310000;

        private static WorldObject Obj(string name)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            }, new ObjectGuid((uint)System.Threading.Interlocked.Increment(ref nextGuid)));

        private sealed class RecordingHost : IPuzzleGateHost
        {
            public readonly List<(PuzzleRemovalReason Reason, bool Solved)> Removed = new List<(PuzzleRemovalReason, bool)>();
            public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.Run;
            public uint RunId => 0x7777;
            public bool AllowAmbush => false;
            public string CheckActivation(PuzzleGatePlacement placement, Player player) => null;
            public void OnSolved(PuzzleGatePlacement placement, Player player) { }
            public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored) { }
            public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) => Removed.Add((reason, solved));
            public void Report(PuzzleGatePlacement placement, string text) { }
        }

        private static PuzzleGatePlacement Scene(PuzzleGateModel model, IPuzzleGateHost host, out List<WorldObject> levers, out List<WorldObject> gate)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "beam" }, out var options, out _));
            var anchor = new Position(0x0150018A, 10, 10, 0, 0, 0, 0, 1, 0);
            var p = new PuzzleGatePlacement(930000 + nextGuid % 1000, options, 7, anchor, 0f, null, 0, null, DateTime.UtcNow, host, model);

            levers = new List<WorldObject> { Obj("lever 1"), Obj("lever 2"), Obj("beam host"), Obj("indicator") };
            gate = model.Form == PuzzleGateForm.Barrier ? new List<WorldObject> { Obj("panel 1"), Obj("panel 2") } : new List<WorldObject> { Obj("focal") };

            foreach (var lever in levers)
            {
                lever.P_PuzzleGate = p;
                p.RoundObjects.Add(lever);
            }

            p.GateParts.AddRange(gate);
            p.Gate = gate[0];
            p.BeginRound(null, levers.Take(2).Select((l, i) => (l.Guid.Full, i)), DateTime.UtcNow);
            p.MarkLive(DateTime.UtcNow);
            p.MarkSolved("Solver", DateTime.UtcNow);
            return p;
        }

        [TestMethod]
        public void A_solved_reward_scene_fades_every_object_and_leaves_the_registry_without_an_unsolved_removal()
        {
            var faded = new List<WorldObject>();
            var fade = PuzzleGateManager.FadeOut;
            PuzzleGateManager.FadeOut = faded.Add;
            var host = new RecordingHost();
            var p = Scene(PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, 0), host, out var levers, out var gate);
            PuzzleGateManager.TrackForTest(p);

            try
            {
                lock (p.Sync)
                    PuzzleGateManager.RemoveGateForSolve(p, levers[0]);

                CollectionAssert.AreEquivalent(gate.Concat(levers).ToList(), faded, "focal, levers, beam host and indicator all fade");
                Assert.IsTrue(levers.All(l => l.P_PuzzleGate == null), "a lever mid-fade no longer answers");
                Assert.AreEqual(0, p.RoundObjects.Count + p.GateParts.Count, "nothing left for the clear to cut short");
                Assert.AreEqual(PuzzleActivationRoute.StaleRound, p.Route(levers[0].Guid.Full, false, out _), "the round is retired");

                PuzzleGateManager.FinishSolve(p);

                Assert.IsFalse(PuzzleGateManager.IsRegistered(p), "the placement leaves the registry");
                Assert.AreEqual(PuzzlePlacementState.Cleared, p.State);
                CollectionAssert.AreEqual(new[] { (PuzzleRemovalReason.Cleared, true) }, host.Removed, "one removal, reported as solved: the run host's fail-open unseal never runs");
            }
            finally
            {
                PuzzleGateManager.FadeOut = fade;
                PuzzleGateManager.UntrackForTest(p);
            }
        }

        [TestMethod]
        public void A_solved_gate_puzzle_fades_only_its_gate_and_keeps_its_levers_registered()
        {
            var faded = new List<WorldObject>();
            var fade = PuzzleGateManager.FadeOut;
            PuzzleGateManager.FadeOut = faded.Add;
            var host = new RecordingHost();
            var p = Scene(PuzzleGateModel.Barrier(1006850u, 1f, 2, 4f), host, out var levers, out var gate);
            PuzzleGateManager.TrackForTest(p);

            try
            {
                lock (p.Sync)
                    PuzzleGateManager.RemoveGateForSolve(p, levers[0]);

                CollectionAssert.AreEquivalent(gate, faded, "only the barrier panels fade");
                CollectionAssert.AreEqual(levers, p.RoundObjects, "the levers stay and answer 'spent'");
                Assert.IsTrue(levers.All(l => l.P_PuzzleGate == p));
                Assert.AreEqual(PuzzleActivationRoute.Current, p.Route(levers[0].Guid.Full, false, out _), "a solved gate's lever still routes, to the spent line");

                PuzzleGateManager.FinishSolve(p);

                Assert.IsTrue(PuzzleGateManager.IsRegistered(p), "a gate puzzle stays registered until its run ends");
                Assert.AreEqual(PuzzlePlacementState.Solved, p.State);
                Assert.AreEqual(0, host.Removed.Count);
            }
            finally
            {
                PuzzleGateManager.FadeOut = fade;
                PuzzleGateManager.UntrackForTest(p);
            }
        }

        [TestMethod]
        public void Only_a_focal_scene_tears_down_on_its_solve_and_the_unsolved_gate_list_skips_it()
        {
            Assert.IsTrue(PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, 0).TearsDownOnSolve);
            Assert.IsFalse(PuzzleGateModel.Barrier(1006850u, 1f, 2, 4f).TearsDownOnSolve);
            Assert.IsFalse(PuzzleGateModel.Door(1006850u, 1f).TearsDownOnSolve);
            Assert.IsFalse(PuzzleGateModel.Default.TearsDownOnSolve);

            var host = new RecordingHost();
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
            var anchor = new Position(0x0150018A, 10, 10, 0, 0, 0, 0, 1, 0);
            var live = new PuzzleGatePlacement(940001, options, 7, anchor, 0f, null, 0, null, DateTime.UtcNow, host, PuzzleGateModel.Door(1006850u, 1f)) { SiteId = "live-gate" };
            var solved = new PuzzleGatePlacement(940002, options, 7, anchor, 0f, null, 0, null, DateTime.UtcNow, host, PuzzleGateModel.Door(1006850u, 1f)) { SiteId = "solved-gate" };
            var reward = new PuzzleGatePlacement(940003, options, 7, anchor, 0f, null, 0, null, DateTime.UtcNow, host, PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, 0)) { SiteId = "reward" };
            live.MarkLive(DateTime.UtcNow);
            solved.MarkLive(DateTime.UtcNow);
            solved.MarkSolved("x", DateTime.UtcNow);
            reward.MarkLive(DateTime.UtcNow);

            foreach (var p in new[] { live, solved, reward })
                PuzzleGateManager.TrackForTest(p);

            try
            {
                CollectionAssert.AreEquivalent(new[] { "live-gate" }, PuzzleGateManager.UnsolvedGateSiteIds(host.RunId));
                Assert.AreEqual(0, PuzzleGateManager.UnsolvedGateSiteIds(0).Count);
            }
            finally
            {
                foreach (var p in new[] { live, solved, reward })
                    PuzzleGateManager.UntrackForTest(p);
            }
        }

        // ---- review follow-ups (F1, F3-F6) ---------------------------------------------------------------------

        [TestMethod]
        public void Nearby_under_two_metres_across_and_three_up_or_down_when_the_walk_is_long()
        {
            Assert.AreEqual(Base + " It lies nearby, about 30 m away on foot.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(1.5f, 0, 0), 30f, false));
            Assert.AreEqual(Base + " It lies nearby, about 30 m away on foot, in the boss's chamber.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(1.5f, 0, 2.5f), 30f, true));
            Assert.AreEqual(Base + " It lies to the east, about 30 m away on foot.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(2.0f, 0, 0), 30f, false), "control: 2 m across is a compass direction");
            Assert.AreEqual(Base + " It forms beside you.",
                ThreadPuzzleRewardSelector.ArmedLineFor(Vector3.Zero, new Vector3(1.5f, 0, 0), null, false), "no walk: 1.5 m straight is beside");
        }

        [TestMethod]
        public void Deferring_the_reward_at_populate_builds_the_walk_graph_so_arming_reads_the_cache()
        {
            var builder = DungeonWalkGraphSource.Builder;
            var graphSource = ThreadPuzzlePass.WalkGraphSource;
            var builds = 0;

            DungeonWalkGraphSource.ClearCacheForTest();
            DungeonWalkGraphSource.Builder = _ => { builds++; return (Maze(), null); };
            ThreadPuzzlePass.WalkGraphSource = lb => DungeonWalkGraphSource.Get(lb, out _); // the production delegate

            try
            {
                var run = PooledRun();
                var curated = new ThreadPuzzlePick(Site("curated", A, 0, 0, 0), PuzzleGateType.Odd, 4, 99, true);

                Assert.IsTrue(ThreadPuzzlePass.DeferReward(run, curated));
                Assert.IsTrue(run.IsRewardPending);
                Assert.IsTrue(DungeonWalkGraphSource.IsCached(run.Dungeon.Landblock), "populate built the graph");
                Assert.AreEqual(1, builds);

                Assert.IsNotNull(DungeonWalkGraphSource.Get(run.Dungeon.Landblock, out _));
                Assert.AreEqual(1, builds, "arming reads the cache");

                // A failed build is remembered: the placer and the notice of one arming never build twice.
                DungeonWalkGraphSource.ClearCacheForTest();
                builds = 0;
                DungeonWalkGraphSource.Builder = _ => { builds++; return (null, "no dat (test)"); };
                Assert.IsNull(DungeonWalkGraphSource.Get(0x0150, out var e1));
                Assert.IsNull(DungeonWalkGraphSource.Get(0x0150, out var e2));
                Assert.AreEqual(1, builds);
                Assert.AreEqual("no dat (test)", e2);
            }
            finally
            {
                DungeonWalkGraphSource.Builder = builder;
                ThreadPuzzlePass.WalkGraphSource = graphSource;
                DungeonWalkGraphSource.ClearCacheForTest();
            }
        }

        [TestMethod]
        public void A_solved_tearing_down_scene_is_not_reaped_before_its_host_hears_the_solve()
        {
            var fade = PuzzleGateManager.FadeOut;
            PuzzleGateManager.FadeOut = _ => { };
            var host = new RecordingHost();
            var p = Scene(PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, 0), host, out var levers, out _);
            PuzzleGateManager.TrackForTest(p);

            try
            {
                lock (p.Sync)
                    PuzzleGateManager.RemoveGateForSolve(p, levers[0]);

                Assert.IsTrue(p.GateMissing(), "the fading scene reads as missing");
                Assert.AreEqual(0, PuzzleGateManager.ReapForRun(host.RunId, DateTime.UtcNow), "the world-thread reap must not steal it");
                Assert.IsTrue(PuzzleGateManager.IsRegistered(p));
                Assert.AreEqual(0, host.Removed.Count);

                PuzzleGateManager.FinishSolve(p);

                Assert.IsFalse(PuzzleGateManager.IsRegistered(p));
                CollectionAssert.AreEqual(new[] { (PuzzleRemovalReason.Cleared, true) }, host.Removed);
            }
            finally
            {
                PuzzleGateManager.FadeOut = fade;
                PuzzleGateManager.UntrackForTest(p);
            }

            Assert.IsFalse(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Solved, true, TimeSpan.Zero, true));
            Assert.IsTrue(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Solved, true, TimeSpan.Zero, false), "control");
        }

        private sealed class ThrowingHost : IPuzzleGateHost
        {
            public readonly List<bool> Removed = new List<bool>();
            public PuzzlePolicyMode PolicyMode => PuzzlePolicyMode.Run;
            public uint RunId => 0x7778;
            public bool AllowAmbush => false;
            public string CheckActivation(PuzzleGatePlacement placement, Player player) => null;
            public void OnSolved(PuzzleGatePlacement placement, Player player) => throw new InvalidOperationException("host failed (test)");
            public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored) { }
            public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) => Removed.Add(solved);
            public void Report(PuzzleGatePlacement placement, string text) { }
        }

        [TestMethod]
        public void The_solve_follow_up_finishes_the_solve_even_when_the_host_throws()
        {
            var host = new ThrowingHost();
            var p = Scene(PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, 0), host, out _, out _);
            PuzzleGateManager.TrackForTest(p);

            try
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => PuzzleGateManager.SolvedFollowUp(p, null)());
                Assert.IsFalse(PuzzleGateManager.IsRegistered(p), "FinishSolve ran in the finally");
                CollectionAssert.AreEqual(new[] { true }, host.Removed);
            }
            finally
            {
                PuzzleGateManager.UntrackForTest(p);
            }
        }

        [TestMethod]
        public void The_solve_follow_up_with_the_real_run_host_unseals_once_and_clears_the_scene()
        {
            var unsealed = ThreadPuzzleRunHost.RewardUnsealed;
            var calls = 0;
            ThreadPuzzleRunHost.RewardUnsealed = _ => calls++;

            var run = PooledRun();
            run.ClearFraction = 0.9;
            var p = Scene(PuzzleGateModel.Focal(PuzzleGateTunables.RewardFocalWcid, 0), new ThreadPuzzleRunHost(run, true), out _, out _);
            Assert.IsTrue(run.SealReward(p));
            run.MarkHasPuzzles();
            run.MarkPopulated(10, 10, 0);
            for (var i = 0; i < 10; i++)
                run.RecordKill(false);
            PuzzleGateManager.TrackForTest(p);

            try
            {
                Assert.IsTrue(run.IsRewardSealed);

                PuzzleGateManager.SolvedFollowUp(p, null)();

                Assert.IsFalse(run.IsRewardSealed, "the solve lifted the seal");
                Assert.AreEqual(1, calls, "one unseal follow-up");
                Assert.IsFalse(PuzzleGateManager.IsRegistered(p), "the scene left the registry");
                Assert.AreEqual(PuzzlePlacementState.Cleared, p.State);

                Assert.IsFalse(ThreadPuzzleWatchdog.Check(run, DateTime.UtcNow), "the watchdog finds nothing to unseal");
                Assert.AreEqual(1, calls, "and no second unseal");
            }
            finally
            {
                ThreadPuzzleRunHost.RewardUnsealed = unsealed;
                PuzzleGateManager.UntrackForTest(p);
            }
        }

        /// <summary>Seams for the walk inputs of a run (graph, sites, closed gates), restored on dispose.</summary>
        private sealed class WalkSeams : IDisposable
        {
            private readonly Func<ushort, DungeonWalkGraph> graph = ThreadPuzzlePass.WalkGraphSource;
            private readonly Func<string, IReadOnlyList<PuzzleSiteDef>> sites = ThreadPuzzlePass.SiteSource;
            private readonly Func<ThreadDungeonRun, IReadOnlyCollection<string>> closed = ThreadPuzzlePass.UnsolvedGateSiteIds;

            public WalkSeams(DungeonWalkGraph g, IReadOnlyList<PuzzleSiteDef> s, params string[] closedIds)
            {
                ThreadPuzzlePass.WalkGraphSource = _ => g;
                ThreadPuzzlePass.SiteSource = _ => s;
                ThreadPuzzlePass.UnsolvedGateSiteIds = _ => closedIds;
            }

            public void Dispose()
            {
                ThreadPuzzlePass.WalkGraphSource = graph;
                ThreadPuzzlePass.SiteSource = sites;
                ThreadPuzzlePass.UnsolvedGateSiteIds = closed;
            }
        }

        [TestMethod]
        public void WalkFrom_blocks_the_doorway_of_a_closed_gate_listed_by_the_registry_seam()
        {
            var gate = Site("g1", A, -5, 0, 0, PuzzleSiteKind.Gate, 90f);  // its gate stands in the A-D doorway
            var sites = new[] { gate };

            using (new WalkSeams(Maze(), sites, "g1"))
            {
                var field = ThreadPuzzlePass.WalkFrom(PooledRun(), sites, A, Vector3.Zero, out var why, out var blocked, out var open);

                Assert.IsNull(why);
                Assert.AreEqual(1, blocked);
                Assert.IsNull(field.DistanceTo(D, new Vector3(-25, 0, 0)), "behind the closed gate");
                Assert.AreEqual(25f, open.DistanceTo(D, new Vector3(-25, 0, 0)).Value, 0.001f, "reachable with it open");
            }

            using (new WalkSeams(Maze(), sites))
            {
                var field = ThreadPuzzlePass.WalkFrom(PooledRun(), sites, A, Vector3.Zero, out _, out var blocked, out var open);
                Assert.AreEqual(0, blocked, "control: no closed gate");
                Assert.AreEqual(25f, field.DistanceTo(D, new Vector3(-25, 0, 0)).Value, 0.001f);
                Assert.IsNull(open, "no second walk when nothing was blocked");
            }
        }

        [TestMethod]
        public void The_straight_fallback_never_picks_a_site_behind_a_closed_gate()
        {
            // Nothing is reachable on foot with g1 closed: "open" sits behind it, "far" in a cell no doorway reaches; "open" is the nearer in a straight line.
            var gate = Site("g1", A, -5, 0, 0, PuzzleSiteKind.Gate, 90f);
            var open = Site("open", D, -60, 0, 0);   // far enough from g1 to clear the picker's spacing
            var far = Site("far", E, 100, 0, 0);
            var sites = new[] { gate, open, far };
            var graph = Maze();
            var blocked = ThreadPuzzlePass.BlockedDoorways(graph, sites, new[] { "g1" });
            var closed = graph.From(A, Vector3.Zero, blocked);
            var unblocked = graph.From(A, Vector3.Zero);
            var placed = new[] { new ThreadPuzzlePick(gate, PuzzleGateType.Beam, 4, 11, false) };

            bool Behind(uint c, Vector3 pt) => closed.DistanceTo(c, pt) == null && unblocked.DistanceTo(c, pt) != null;

            var choice = ThreadPuzzleRewardSelector.Select(sites, placed, null, 6, Vector3.Zero, closed.DistanceTo, Behind);
            Assert.AreEqual("far", choice.Pick.Site.Id);
            Assert.AreEqual(ThreadPuzzleRewardSelector.RuleStraight, choice.Rule);
            Assert.AreEqual("none-reachable", choice.WalkNote);

            Assert.AreEqual("open", ThreadPuzzleRewardSelector.Select(sites, placed, null, 6, Vector3.Zero, closed.DistanceTo).Pick.Site.Id,
                "control: without the behind-a-gate test the nearer gated site wins the fallback");
        }

        [TestMethod]
        public void The_arming_placer_wires_the_behind_a_gate_exclusion()
        {
            var gate = Site("g1", A, -5, 0, 0, PuzzleSiteKind.Gate, 90f);
            var open = Site("open", D, -60, 0, 0);   // far enough from g1 to clear the picker's spacing
            var far = Site("far", E, 100, 0, 0);
            var sites = new[] { gate, open, far };

            var placer = ThreadPuzzlePass.ArmingPlacer;
            ThreadPuzzlePick? last = null;

            try
            {
                foreach (var (closedIds, expected) in new[] { (new[] { "g1" }, "far"), (Array.Empty<string>(), "open") })
                {
                    using (new WalkSeams(Maze(), sites, closedIds))
                    {
                        var run = PooledRun();
                        run.ClearFraction = 0.9;
                        Assert.IsTrue(run.SealRewardPending(new ThreadPuzzlePick(Site("curated", A, 0, 0, 0), PuzzleGateType.Odd, 4, 99, true)));
                        run.RecordPlacedGatePick(new ThreadPuzzlePick(gate, PuzzleGateType.Beam, 4, 11, false));
                        run.MarkPopulated(10, 10, 0);
                        for (var i = 0; i < 10; i++)
                            run.RecordKill(false);

                        Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
                        var placement = new PuzzleGatePlacement(950001, options, 7, new Position(D, -25, 0, 0, 0, 0, 0, 1, 0), 0f, null, 0, null, DateTime.UtcNow, new ThreadPuzzleRunHost(run, true));

                        ThreadPuzzlePass.ArmingPlacer = (ThreadDungeonRun r, ThreadPuzzlePick pick, out string error) => { last = pick; error = null; return placement; };

                        Assert.AreSame(placement, ThreadPuzzlePass.PlaceRewardAtArming(run, Vector3.Zero, "x", A));
                        Assert.AreEqual(expected, last.Value.Site.Id, $"closed gates: [{string.Join(",", closedIds)}]");
                    }
                }
            }
            finally
            {
                ThreadPuzzlePass.ArmingPlacer = placer;
            }
        }

        [TestMethod]
        public void The_armed_notice_says_boss_chamber_from_the_dungeons_boss_anchor()
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
            PuzzleGatePlacement SceneAt(uint cell, float x)
                => new PuzzleGatePlacement(960001, options, 7, new Position(cell, x, 0, 0, 0, 0, 0, 1, 0), 0f, null, 0, null, DateTime.UtcNow, AdminPuzzleGateHost.Instance);

            using (new WalkSeams(Maze(), Array.Empty<PuzzleSiteDef>()))
            {
                var run = PooledRun();

                Assert.IsFalse(ThreadPuzzlePass.BuildArmedNotice(run, SceneAt(D, -25)).BossChamber, "no boss anchor: never");

                run.Dungeon.BossAnchor = new DungeonSpawnPointDef { Cell = D, X = -40, Y = 0, Z = 0 };
                Assert.IsTrue(ThreadPuzzlePass.BuildArmedNotice(run, SceneAt(D, -25)).BossChamber, "same cell");

                // Scene in A at (-10,0,0), boss in D: 2 m to the A-D doorway, then on.
                run.Dungeon.BossAnchor = new DungeonSpawnPointDef { Cell = D, X = -25, Y = 0, Z = 0 };
                Assert.IsTrue(ThreadPuzzlePass.BuildArmedNotice(run, SceneAt(A, -10)).BossChamber, "15 m on foot");
                Assert.IsFalse(ThreadPuzzlePass.BuildArmedNotice(run, SceneAt(A, -9)).BossChamber, "16 m on foot");

                var notice = ThreadPuzzlePass.BuildArmedNotice(run, SceneAt(A, -10));
                Assert.AreEqual(Base + " It lies to the west, about 10 m away, in the boss's chamber.", notice.LineFor(true, 0, Vector3.Zero));
            }
        }

        [TestMethod]
        public void The_notice_reads_the_members_cell_from_their_location()
        {
            var notice = new ThreadPuzzlePass.RewardArmedNotice(Maze(), null, new Vector3(0, 8, 0), C, false);

            Assert.AreEqual(Base + " It lies to the north, about 68 m away on foot.", notice.LineFor(true, new Position(A, 0, 0, 0, 0, 0, 0, 1, 0)));
            Assert.AreEqual(Base + " It lies to the north, about 8 m away.", notice.LineFor(true, new Position(E, 0, 0, 0, 0, 0, 0, 1, 0)), "a cell with no way there: straight");
            Assert.AreEqual(PuzzleGateText.RewardArmed, notice.LineFor(true, (Position)null), "no location");
        }

        [TestMethod]
        public void The_arming_info_line_carries_walk_rule_blocked_and_the_skip_reason()
        {
            var site = Site("r1", D, -25, 0, 0);
            var pick = new ThreadPuzzlePick(site, PuzzleGateType.Odd, 4, 99, true);

            var walked = new ThreadRewardArmingChoice(pick, false, true, 25f, false) { Walk = 25f, Rule = ThreadPuzzleRewardSelector.RuleWalk };
            Assert.AreEqual("reward scene #7 placed at arming site=r1 kind=reward type=odd n=4 player=Alice dist=25.0 walk=25.0 rule=walk blocked_doorways=1 same_floor=true",
                ThreadPuzzlePass.ArmingLine(7, walked, "Alice", 1, null));

            var straight = new ThreadRewardArmingChoice(pick, false, false, 12.5f, false);
            Assert.AreEqual("reward scene #7 placed at arming site=r1 kind=reward type=odd n=4 player=- dist=12.5 walk=- rule=straight blocked_doorways=0 walk_skipped=no-graph same_floor=false",
                ThreadPuzzlePass.ArmingLine(7, straight, null, 0, "no-graph"));

            var none = new ThreadRewardArmingChoice(pick, false, false, 12.5f, false) { WalkNote = "none-reachable" };
            StringAssert.Contains(ThreadPuzzlePass.ArmingLine(7, none, "x", 2, null), " walk_skipped=none-reachable ");
        }

        // ---- the real graph from the cell dat ----------------------------------------------------------------

        private static CellDatDatabase cellDat;
        private static string datSkip;
        private static bool datTried;

        private static void RequireDats()
        {
            if (!datTried)
            {
                datTried = true;
                datSkip = LoadDats();
            }

            if (datSkip != null)
                Assert.Inconclusive(datSkip);
        }

        /// <summary>The cell dat opened into a PRIVATE instance (never swapped into DatManager), as ExactPlacementTests does.</summary>
        private static string LoadDats()
        {
            var candidates = new List<string>();
            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);
            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")) && File.Exists(Path.Combine(c, "client_cell_1.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat + client_cell_1.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                if (DatManager.PortalDat == null)
                    DatManager.Initialize(datDir, true, false);

                cellDat = new CellDatDatabase(Path.Combine(datDir, "client_cell_1.dat"), true);
            }
            catch (Exception ex)
            {
                return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
            }

            return DatManager.PortalDat == null || cellDat == null ? $"Skipped: {datDir} did not produce both dats." : null;
        }

        [TestMethod]
        public void Tiny_Hive_builds_from_the_dat_and_its_reward_sites_are_reachable_from_the_entry()
        {
            RequireDats();

            var graph = DungeonWalkGraphSource.Build(0x0289, cellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);
            Assert.IsTrue(graph.CellCount > 0 && graph.Portals.Count > 0, $"{graph.CellCount} cells, {graph.Portals.Count} portals");

            // Content/dungeons/dynamic/0x0289.json entry and bossAnchor; puzzle-gates.json tiny_hive r1 and r2.
            var entry = new Vector3(110, -20, -30);
            var field = graph.From(0x028902A5, entry);
            Assert.IsNotNull(field, "the entry cell is in the graph");

            var r1 = new Vector3(80, -94, -78);
            var r2 = new Vector3(85, -105, -78);
            var boss = new Vector3(80, -100, -78);

            var w1 = field.DistanceTo(0x02890106, r1);
            var w2 = field.DistanceTo(0x02890108, r2);
            var wb = field.DistanceTo(0x02890108, boss);

            Assert.IsTrue(w1.HasValue && w2.HasValue && wb.HasValue, $"reachable: r1={w1} r2={w2} boss={wb}");
            Assert.IsTrue(w1.Value >= Vector3.Distance(entry, r1) - 0.01f, $"a walk is never shorter than the straight line: {w1} vs {Vector3.Distance(entry, r1)}");
            Assert.IsTrue(w1.Value > Vector3.Distance(entry, r1) * 1.2f, $"Tiny Hive winds: walk {w1:0.0} m vs straight {Vector3.Distance(entry, r1):0.0} m");

            Console.WriteLine($"tiny_hive: {graph.CellCount} cells, {graph.Portals.Count} directed portals; entry->r1 walk {w1:0.0} straight {Vector3.Distance(entry, r1):0.0}; entry->r2 walk {w2:0.0}; entry->boss walk {wb:0.0}");

            // r2 stands in the boss anchor's cell: the boss-chamber rule says so; r1, one cell over, is within 15 m on foot.
            var bossField = graph.From(0x02890106, r1);
            var r1ToBoss = bossField.DistanceTo(0x02890108, boss);
            Console.WriteLine($"tiny_hive: r1->boss walk {r1ToBoss:0.0}");
            Assert.IsTrue(ThreadPuzzleRewardSelector.IsBossChamber(0x02890108, r2, 0x02890108, boss, null));
        }

        [TestMethod]
        public void Every_curated_gate_site_of_Filos_Doom_finds_its_doorway_in_the_dat()
        {
            RequireDats();

            var graph = DungeonWalkGraphSource.Build(0x0150, cellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);

            // Copied from Content/dungeons/dynamic/puzzle-gates.json (filos_doom): id, cell, anchor, yaw, gateDistance.
            var sites = new (string Id, uint Cell, float X, float Y, float Z, float Yaw, float Gd)[]
            {
                ("s1", 0x01500199, 40, -18, 0, 180, 7),
                ("s2", 0x01500199, 40, -21.333f, 0, 180, 7),
                ("s3", 0x0150018F, 31, -30, 0, 270, 14),
                ("s4", 0x01500163, 50, -38, -6, 180, 7),
                ("s5", 0x0150013D, 69.39f, -68.027f, -12, 185, 7),
                ("s6", 0x01500141, 66.667f, -76.667f, -12, 270, 5),
                ("s7", 0x0150013F, 68, -80, -12, 270, 7),
                ("s8", 0x01500141, 66.5f, -80, -12, 270, 9),
                ("s9", 0x01500143, 77.5f, -80, -12, 270, 7),
                ("s10", 0x01500140, 75, -80, -12, 270, 10),
            };

            var missing = new List<string>();

            foreach (var s in sites)
            {
                var def = Site(s.Id, s.Cell, s.X, s.Y, s.Z, PuzzleSiteKind.Gate, s.Yaw);
                def.Layout = new PuzzleLayoutParams { GateDistance = s.Gd };
                var gatePoint = ThreadPuzzlePass.GatePoint(def);
                var doorway = graph.NearestDoorway(gatePoint);

                var nearest = graph.Portals.Select(p => new Vector2(p.Centroid.X - gatePoint.X, p.Centroid.Y - gatePoint.Y).Length()).Min();
                Console.WriteLine($"filos_doom {s.Id}: gate ({gatePoint.X:0.0},{gatePoint.Y:0.0},{gatePoint.Z:0.0}) doorway={(doorway.HasValue ? $"0x{doorway.Value.Item1:X8}-0x{doorway.Value.Item2:X8}" : "none")} nearest-portal-xy={nearest:0.00}");

                if (!doorway.HasValue)
                {
                    missing.Add(s.Id);
                    continue;
                }

                // The tool fits gateDistance so the gate stands IN the doorway: the chosen portal's centroid is the gate point.
                var chosen = graph.Portals.First(p => DungeonWalkGraph.Pair(p.From, p.To) == doorway.Value);
                Assert.AreEqual(0f, new Vector2(chosen.Centroid.X - gatePoint.X, chosen.Centroid.Y - gatePoint.Y).Length(), 0.05f, $"{s.Id}: the doorway is at the gate point");
            }

            Assert.AreEqual(0, missing.Count, "gate sites with no doorway within reach: " + string.Join(", ", missing));

            // s7 and s8 (and s9 and s10) close the two faces of ONE half-metre doorway cell: 0x01500145 joins only
            // 0x01500140 and 0x01500143, 0x01500146 only 0x01500143 and 0x01500147. So the portal 0.5 m from a gate point
            // is the far face of the same doorway, and blocking either face cuts the same passage.
            CollectionAssert.AreEquivalent(new uint[] { 0x01500140, 0x01500143 }, graph.Portals.Where(p => p.From == 0x01500145).Select(p => p.To).ToList());
            CollectionAssert.AreEquivalent(new uint[] { 0x01500143, 0x01500147 }, graph.Portals.Where(p => p.From == 0x01500146).Select(p => p.To).ToList());

            var west = new Vector3(70, -80, -12);
            var east = new Vector3(80, -80, -12);
            var nearFace = graph.From(0x01500140, west, new HashSet<(uint, uint)> { DungeonWalkGraph.Pair(0x01500140, 0x01500145) }).DistanceTo(0x01500143, east);
            var farFace = graph.From(0x01500140, west, new HashSet<(uint, uint)> { DungeonWalkGraph.Pair(0x01500143, 0x01500145) }).DistanceTo(0x01500143, east);
            Assert.AreEqual(nearFace.HasValue, farFace.HasValue, $"either face: {nearFace} vs {farFace}");
            if (nearFace.HasValue)
                Assert.AreEqual(nearFace.Value, farFace.Value, 0.6f);
            Console.WriteLine($"filos_doom 140->143 with either face of 0x01500145 closed: {nearFace?.ToString("0.0") ?? "unreachable"} / {farFace?.ToString("0.0") ?? "unreachable"}; open: {graph.From(0x01500140, west).DistanceTo(0x01500143, east):0.0}");
        }
    }
}

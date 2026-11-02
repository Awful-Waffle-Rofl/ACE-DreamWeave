using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b, the orchestrator defaults from WP3a: a Thread-Guide gem never rolls an outdoor site, an
    /// outdoor run's eviction has its own line (the dungeon line is unchanged), and the cache exclusion scan compares
    /// positions across footprint seams in one frame.
    /// </summary>
    [TestClass]
    public class OutdoorRunPolicyTests
    {
        // ---- guide runs never roll outdoor ----

        private static Dictionary<string, DungeonEntryDef> Rows(bool withDungeon = true)
        {
            var rows = new Dictionary<string, DungeonEntryDef>
            {
                ["ow_site"] = new DungeonEntryDef { Id = "ow_site", Kind = DungeonEntryDef.KindOutdoor, MinLevel = 1, MaxLevel = 275 },
            };
            if (withDungeon)
                rows["filos_doom"] = new DungeonEntryDef { Id = "filos_doom", MinLevel = 1, MaxLevel = 275 };
            return rows;
        }

        private static DungeonEntryDef Resolve(bool isGuide, IReadOnlyDictionary<string, DungeonEntryDef> rows, int seed, string id = null)
            => OutdoorRotation.ResolveForGem(isGuide, id ?? DungeonGemSpec.Any, rows, 100, seed, outdoorEnabled: true, liveOutdoorRuns: 0, maxConcurrent: 10);

        [TestMethod]
        public void A_guide_gem_never_rolls_an_outdoor_site()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                Assert.AreEqual("filos_doom", Resolve(true, Rows(), seed).Id, $"seed {seed}: a normal gem could draw the outdoor row, a guide gem never does");
                Assert.IsNull(Resolve(true, Rows(withDungeon: false), seed), "with only outdoor rows a guide gem resolves nothing");
            }
        }

        [TestMethod]
        public void A_normal_gem_still_rolls_outdoor_and_dungeons_from_one_pool()
        {
            var ids = new HashSet<string>();
            for (var seed = 0; seed < 50; seed++)
                ids.Add(Resolve(false, Rows(), seed).Id);
            CollectionAssert.AreEquivalent(new[] { "ow_site", "filos_doom" }, ids.ToArray());
        }

        [TestMethod]
        public void A_guide_gem_resolves_exactly_as_a_dungeon_only_pool()
        {
            // With outdoor off, Pick draws once over the dungeon rows: identical to Resolve with the kill switch off.
            var rows = Rows();
            rows["second"] = new DungeonEntryDef { Id = "second", MinLevel = 1, MaxLevel = 275 };

            for (var seed = 0; seed < 50; seed++)
            {
                var expected = OutdoorRotation.Resolve(DungeonGemSpec.Any, rows, 100, seed, false, 0, 10);
                Assert.AreSame(expected, Resolve(true, rows, seed), $"seed {seed}");
            }
        }

        internal static string GuideWiringFault(string body)
        {
            if (!body.Contains("OutdoorRotation.ResolveForGem(ThreadGuideRules.IsGuide(spec),", StringComparison.Ordinal))
                return "ResolveDungeon does not pass the guide flag";
            if (body.Contains("OutdoorRotation.Resolve(", StringComparison.Ordinal))
                return "ResolveDungeon still calls the guide-blind Resolve";
            return null;
        }

        private static string ResolveDungeonBody() => PooledLootSourceText.MethodBody(
            PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
            "private static Defs.DungeonEntryDef ResolveDungeon(DungeonGemSpec spec)");

        [TestMethod]
        public void The_gem_handler_resolves_with_the_guide_flag()
        {
            var body = ResolveDungeonBody();
            Assert.IsNull(GuideWiringFault(body));
            Assert.IsNotNull(GuideWiringFault(body.Replace("ResolveForGem(ThreadGuideRules.IsGuide(spec), ", "Resolve(")));
        }

        // ---- the eviction line ----

        [TestMethod]
        public void The_eviction_line_follows_the_run_kind()
        {
            Assert.AreEqual("The Thread unravels and the land around you fades.", ThreadDungeonManager.EvictionLine(true));
            Assert.AreEqual("The dungeon collapses around you.", ThreadDungeonManager.EvictionLine(false), "the dungeon wording is unchanged");
        }

        internal static string EvictionWiringFault(string body)
            => body.Contains("new GameMessageSystemChat(EvictionLine(run.Dungeon?.IsOutdoor == true),", StringComparison.Ordinal)
                ? null
                : "EndRun does not pick the eviction line by run kind";

        [TestMethod]
        public void EndRun_tells_each_evicted_player_the_line_for_its_run_kind()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs"),
                "public static void EndRun(ThreadDungeonRun run, string why)");

            Assert.IsNull(EvictionWiringFault(body));
            Assert.IsNotNull(EvictionWiringFault(body.Replace("EvictionLine(run.Dungeon?.IsOutdoor == true)", "\"The dungeon collapses around you.\"")));
        }

        // ---- cache exclusion across seams ----

        private static Position At(ushort landblock, float x, float y) => new Position(((uint)landblock << 16) | 0x0001u, x, y, 10f, 0f, 0f, 0f, 1f, 0u);

        [TestMethod]
        public void InFrame_re_expresses_a_neighbour_position_in_the_frame_landblock()
        {
            Assert.AreEqual((202f, 20f, 10f), ThreadCachePlacer.InFrame(At(0x5556, 10, 20), 0x5456), "one block east");
            Assert.AreEqual((10f, 197f, 10f), ThreadCachePlacer.InFrame(At(0x5457, 10, 5), 0x5456), "one block north");
            Assert.AreEqual((-182f, 20f, 10f), ThreadCachePlacer.InFrame(At(0x5356, 10, 20), 0x5456), "one block west");
            Assert.AreEqual((10f, 20f, 10f), ThreadCachePlacer.InFrame(At(0x5456, 10, 20), 0x5456), "identity on the frame landblock");
        }

        // ---- a faulted load ends the run at once ----

        private static bool End(ThreadDungeonRunState state, bool createCompleted, bool createFaulted, TimeSpan sinceStart, out string why)
        {
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            return ThreadDungeonManager.ShouldEnd(state, landblockPresent: true, createCompleted, createFaulted, playerEverObserved: false, playerPresent: false,
                lootHeld: false, pvpHeld: false, sinceStart, TimeSpan.Zero, now, now.AddHours(3), out why);
        }

        [TestMethod]
        public void A_faulted_copy_ends_at_once_with_LoadFailed()
        {
            Assert.IsTrue(End(ThreadDungeonRunState.Starting, false, true, TimeSpan.FromSeconds(1), out var why), "faulted: no 60 s wait");
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.LoadFailed, why);

            Assert.IsFalse(End(ThreadDungeonRunState.Starting, false, false, TimeSpan.FromSeconds(1), out _), "control: a slow but healthy load is left alone");
            Assert.IsTrue(End(ThreadDungeonRunState.Starting, false, false, TimeSpan.FromSeconds(61), out why), "control: the timeout still ends it");
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.LoadFailed, why);
        }

        [TestMethod]
        public void A_fault_changes_nothing_once_the_copy_completed_or_the_run_left_Starting()
        {
            Assert.IsFalse(End(ThreadDungeonRunState.Starting, true, true, TimeSpan.FromSeconds(1), out _), "completed wins");
            Assert.IsFalse(End(ThreadDungeonRunState.Active, false, true, TimeSpan.FromSeconds(1), out _), "only a Starting run is judged on its load");

            // The old overload (no fault argument) is exactly the faulted=false decision.
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            Assert.IsFalse(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Starting, true, false, false, false, false, false,
                TimeSpan.FromSeconds(1), TimeSpan.Zero, now, now.AddHours(3), out _));
        }

        [TestMethod]
        public void Only_a_fault_before_the_completion_action_is_queued_blocks_completion()
        {
            Assert.IsTrue(ACE.Server.Entity.Landblock.FaultBlocksCompletion(completionQueued: false));
            Assert.IsFalse(ACE.Server.Entity.Landblock.FaultBlocksCompletion(completionQueued: true), "a later fault leaves the queued completion to run");
        }

        internal static string FaultWiringFault(string landblock, string manager)
        {
            var init = PooledLootSourceText.MethodBody(landblock, "public void Init(bool reload = false)");
            if (!init.Contains("if (FaultBlocksCompletion(createWorldObjectsCompletionQueued))\n                    createWorldObjectsFaulted = true;", StringComparison.Ordinal))
                return "the faulted continuation does not set CreateWorldObjectsFaulted";

            var create = PooledLootSourceText.MethodBody(landblock, "private void CreateWorldObjects()");
            var enqueue = create.IndexOf("actionQueue.EnqueueAction(", StringComparison.Ordinal);
            var queued = create.IndexOf("createWorldObjectsCompletionQueued = true;", StringComparison.Ordinal);
            if (enqueue < 0 || queued < enqueue) return "the completion-queued latch must be set after the completion action is queued";

            if (!manager.Contains("state == ThreadDungeonRunState.Starting && !createCompleted && RunCopyCreateFaulted(run, landblock)", StringComparison.Ordinal))
                return "the reap does not pass the copy's fault to ShouldEnd";

            return null;
        }

        [TestMethod]
        public void The_fault_reaches_the_reap()
        {
            var lb = PooledLootSourceText.Read("Source/ACE.Server/Entity/Landblock.cs");
            var mgr = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            Assert.IsNull(FaultWiringFault(lb, mgr));
            Assert.IsNotNull(FaultWiringFault(lb.Replace("createWorldObjectsFaulted = true;", ";"), mgr));
            Assert.IsNotNull(FaultWiringFault(lb, mgr.Replace("&& RunCopyCreateFaulted(run, landblock)", "&& false")));
        }
    }
}

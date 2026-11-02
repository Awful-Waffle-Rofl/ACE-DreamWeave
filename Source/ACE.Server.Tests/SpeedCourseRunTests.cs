using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;
using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The speed-course controller's run logic against a fake world: populate (named placement, boss held), the Seal
    /// latch and the boss with its objective item, the puzzle placements, the runner lifecycle and teardown. No
    /// landblock, no database, no PropertyManager.
    /// </summary>
    [TestClass]
    public class SpeedCourseRunTests
    {
        private const uint BookWcid = 1007500;
        private const uint Owner = 0x50000042;

        private static (SpeedCourseRun Run, SpeedCourseFakeWorld World, RecordingHazardDriver Hazards) NewRun(int playerLevel = 120, int fixedLevel = 0)
        {
            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();

            var hazards = new RecordingHazardDriver();
            var course = SpeedCourseFixtures.Course();
            course.FixedLevel = fixedLevel;
            var run = new SpeedCourseRun(course, 3, 60, BookWcid, playerLevel, Owner, 12345, world, now: 0.0);
            run.AttachHazardDriver(hazards);

            return (run, world, hazards);
        }

        private static SpeedNamedDef Named(SpeedCourseRun run, string slot) => run.Course.Named.Single(n => n.Slot == slot);

        [TestMethod]
        public void Populate_WaitsForCreateWorldObjectsCompleted()
        {
            var (run, world, _) = NewRun();
            world.CreateWorldObjectsCompleted = false;

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(0, world.BuildPlanCalls);
            Assert.AreEqual(0, world.Spawns.Count);
            Assert.AreEqual(0, world.Placements.Count);

            world.CreateWorldObjectsCompleted = true;
            run.Tick(1.0);
            world.Drain();

            Assert.AreEqual(1, world.BuildPlanCalls);
            Assert.IsTrue(run.Populated);

            run.Tick(1.5);
            world.Drain();
            Assert.AreEqual(1, world.BuildPlanCalls, "populate runs once");
        }

        [TestMethod]
        public void Populate_PutsK1AndK2OnTheCoursePoints_AndHoldsTheBoss()
        {
            var (run, world, _) = NewRun();

            run.Tick(0.5);
            world.Drain();

            // Every non-boss entry spawned once, except K1 (held behind P2); the boss entry is held for the Seal.
            Assert.AreEqual(7, world.Spawns.Count);
            Assert.IsFalse(world.Spawns.Any(s => s.Entry.Role == DungeonRole.Boss), "the boss is not spawned at populate");
            Assert.IsTrue(run.BossHeld);
            Assert.IsTrue(run.Boss.IsNone);
            Assert.IsTrue(run.IsNamedHeld(SpeedNamedSlots.K1), "K1 waits behind P2");
            Assert.IsTrue(run.K1.IsNone);

            var k2 = world.Spawns.Single(s => s.Stamp?.Slot == SpeedNamedSlots.K2);
            Assert.AreSame(Named(run, "K2").Position, k2.Entry.Point, "K2 stands on the course K2 point");
            Assert.AreEqual(201u, k2.Entry.Wcid, "K2 is the second elite");
            Assert.AreEqual("Warden", k2.Stamp.DisplayName);
            Assert.IsNull(k2.Stamp.ObjectiveKey, "no named creature is an objective contributor");
            Assert.AreEqual(k2.Handle, run.K2);

            // P2 solved: K1 enters on its point.
            var p2 = world.Placements.Single(p => p.Site.Id == "bb_p2");
            ((SpeedPuzzleGateHost)p2.Host).OnSolved(null, null);

            var k1 = world.Spawns.Single(s => s.Stamp?.Slot == SpeedNamedSlots.K1);
            Assert.AreSame(Named(run, "K1").Position, k1.Entry.Point, "K1 stands on the course K1 point");
            Assert.AreEqual(200u, k1.Entry.Wcid, "K1 is the first elite");
            Assert.AreEqual("Archivist", k1.Stamp.DisplayName);
            Assert.IsNull(k1.Stamp.ObjectiveKey, "the run contributes north_rite itself when K1 dies");
            Assert.AreEqual(k1.Handle, run.K1);
            Assert.IsFalse(run.IsNamedHeld(SpeedNamedSlots.K1));

            // Pack creatures keep their plan points and carry no name or objective.
            foreach (var s in world.Spawns.Where(s => s.Stamp?.Slot == null))
            {
                Assert.IsNull(s.Stamp.ObjectiveKey);
                Assert.IsNull(s.Stamp.DisplayName);
                Assert.AreEqual(0u, s.Stamp.TreasureWcid);
            }
        }

        [TestMethod]
        public void Populate_SpawnsInBatches_ThroughThePostedQueue()
        {
            var (run, world, _) = NewRun();
            world.Plan = SpeedCourseFakeWorld.SamplePlan();

            for (var i = 0; i < 12; i++)
                world.Plan.Entries.Insert(0, new DungeonSpawnPlanEntry(150u + (uint)i, DungeonRole.Trash, SpeedCourseFakeWorld.Point(i, i)));

            run.Tick(0.5);

            Assert.AreEqual(SpeedCourseRun.SpawnBatchSize, world.Spawns.Count, "the first batch runs in the tick");
            Assert.IsFalse(run.Populated);

            world.Drain();

            Assert.AreEqual(19, world.Spawns.Count, "every entry but the boss and K1 (held behind P2)");
            Assert.IsTrue(run.Populated);
        }

        [TestMethod]
        public void Boss_SpawnsOnlyWhenTheSealLatches_WithTheObjectiveItem()
        {
            var (run, world, _) = NewRun();
            world.MaxHealthByWcid[201] = 4321;

            run.Tick(0.5);
            world.Drain();

            world.Seals["bb_seal"] = false;
            run.Tick(1.0);
            Assert.IsTrue(run.Boss.IsNone, "an unlatched Seal holds the boss");

            world.Seals["bb_seal"] = true;
            run.Tick(1.5);

            var boss = world.Spawns.Single(s => s.Entry.Role == DungeonRole.Boss);
            Assert.AreSame(Named(run, "BOSS").Position, boss.Entry.Point, "the boss stands on the course BOSS point");
            Assert.AreEqual(BookWcid, boss.Stamp.TreasureWcid, "the boss carries the season objective");
            Assert.AreEqual("The Reader", boss.Stamp.DisplayName);
            Assert.AreEqual(4321u, boss.Stamp.ObservedMaxNonBoss, "the boss is normalized against the pack actually placed");
            Assert.IsNull(boss.Stamp.ObjectiveKey);
            Assert.AreEqual(boss.Handle, run.Boss);

            run.Tick(2.0);
            Assert.AreEqual(1, world.Spawns.Count(s => s.Entry.Role == DungeonRole.Boss), "the boss spawns once");
        }

        [TestMethod]
        public void Boss_RetriesAFailedSpawn_ThenGivesUpWithAnError()
        {
            var (run, world, _) = NewRun();
            world.FailingWcids.Add(300);

            run.Tick(0.5);
            world.Drain();
            world.Seals["bb_seal"] = true;

            for (var i = 0; i < 6; i++)
                run.Tick(1.0 + i);

            Assert.AreEqual(SpeedCourseRun.MaxBossSpawnAttempts, world.Spawns.Count(s => s.Entry.Role == DungeonRole.Boss));
            Assert.IsTrue(world.Errors.Any(e => e.Contains("giving up")));

            // The run can still finish: the objective goes on the floor at the boss point, once.
            Assert.AreEqual(1, world.PlacedItems.Count);
            Assert.AreEqual(BookWcid, world.PlacedItems[0].Wcid);
            Assert.IsTrue(run.ObjectiveItemPlaced);
        }

        private static DungeonSpawnPlan PlanWithoutBoss()
        {
            var plan = SpeedCourseFakeWorld.SamplePlan();
            plan.Entries.RemoveAll(e => e.Role == DungeonRole.Boss);
            plan.BossWcid = 0;
            plan.BossNoCandidate = true;
            return plan;
        }

        [TestMethod]
        public void NoBossEntry_FallsBackToTheFamilyCandidate_ShapedAsTheBoss()
        {
            var (run, world, _) = NewRun();
            world.Plan = PlanWithoutBoss();
            world.FallbackBoss = 777;

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(1, world.FallbackBossCalls.Count);
            Assert.AreEqual("undead", world.FallbackBossCalls[0].Family);
            Assert.AreEqual(run.RunLevel, world.FallbackBossCalls[0].Level);
            Assert.IsTrue(world.FallbackBossCalls[0].Drawn.SetEquals(world.Plan.Entries.Select(e => e.Wcid)), "the pack already drawn is handed in");
            Assert.IsTrue(run.BossHeld, "the stand-in is held for the Seal");
            Assert.AreEqual(run.RunLevel + 1, world.Plan.BossLevel, "an unset boss level is floored at run level + 1");

            world.Seals["bb_seal"] = true;
            run.Tick(1.0);

            var boss = world.Spawns.Single(s => s.Entry.Role == DungeonRole.Boss);
            Assert.AreEqual(777u, boss.Entry.Wcid);
            Assert.AreSame(Named(run, "BOSS").Position, boss.Entry.Point);
            Assert.AreEqual(BookWcid, boss.Stamp.TreasureWcid, "the stand-in carries the objective");
            Assert.AreEqual(0, world.PlacedItems.Count);
            Assert.IsTrue(world.Warnings.Any(w => w.Contains("fallback boss wcid 777")));
        }

        [TestMethod]
        public void NoBossAndNoCandidate_PlacesTheObjectiveAtTheBossPoint_OnSealLatch()
        {
            var (run, world, _) = NewRun();
            world.Plan = PlanWithoutBoss();
            world.FallbackBoss = 0;

            run.Tick(0.5);
            world.Drain();
            Assert.IsFalse(run.BossHeld);

            world.Seals["bb_seal"] = false;
            run.Tick(1.0);
            Assert.AreEqual(0, world.PlacedItems.Count, "nothing until the Seal latches");

            world.Seals["bb_seal"] = true;
            run.Tick(1.5);
            run.Tick(2.0);

            Assert.AreEqual(1, world.PlacedItems.Count, "placed once");
            Assert.AreEqual(BookWcid, world.PlacedItems[0].Wcid);
            Assert.AreSame(Named(run, "BOSS").Position, world.PlacedItems[0].Point);
            Assert.IsFalse(world.Spawns.Any(s => s.Entry.Role == DungeonRole.Boss));
            Assert.IsTrue(world.Warnings.Any(w => w.Contains("placed on the floor")));
        }

        [TestMethod]
        public void Puzzles_ThreeDistinctTypes_P2FromItsOwnSet_WithTheCourseLockoutAndRounds()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
                world.AddBlackBookSites();

                var run = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, BookWcid, 120, Owner, seed, world, 0.0);
                run.Tick(0.5);
                world.Drain();

                Assert.AreEqual(3, world.Placements.Count);
                Assert.AreEqual(3, world.Placements.Select(p => p.Pick.Type).Distinct().Count(), $"seed {seed}: no repeated type");

                var p2 = world.Placements.Single(p => p.Site.Id == "bb_p2");
                Assert.IsTrue(p2.Pick.Type == PuzzleGateType.Beam || p2.Pick.Type == PuzzleGateType.Shuffle, $"seed {seed}: P2 is beam or shuffle");

                foreach (var p in world.Placements)
                {
                    var def = SpeedCourseFixtures.Course().Puzzles.Single(d => d.SiteId == p.Site.Id);

                    Assert.AreEqual(5, p.Options.LockoutSeconds);
                    Assert.AreEqual(def.Rounds, p.Options.Rounds);
                    Assert.AreEqual(p.Pick.Type, p.Options.Type);
                    Assert.AreNotEqual(0, p.Pick.Seed);

                    var host = (SpeedPuzzleGateHost)p.Host;
                    Assert.AreEqual(PuzzlePolicyMode.None, host.PolicyMode);
                    Assert.AreEqual(SpeedCourseFakeWorld.FakeInstance, host.RunId);
                    Assert.IsFalse(host.AllowAmbush);
                }
            }
        }

        [TestMethod]
        public void Puzzles_DifferentRunSeeds_DrawDifferentPlans()
        {
            var plans = Enumerable.Range(0, 30).Select(seed =>
            {
                var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
                world.AddBlackBookSites();
                var run = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, BookWcid, 120, Owner, seed, world, 0.0);
                run.Tick(0.5);
                return string.Join(",", world.Placements.Select(p => p.Pick.Type));
            }).Distinct().Count();

            Assert.IsTrue(plans > 1, "puzzles re-roll per run");
        }

        [TestMethod]
        public void RemovedWithoutASolve_RePlacesTheSameSite_AtMostTwice()
        {
            var (run, world, _) = NewRun();
            run.Tick(0.5);
            world.Drain();

            var p1 = world.Placements.Single(p => p.Site.Id == "bb_p1");
            var host = (SpeedPuzzleGateHost)p1.Host;

            for (var i = 0; i < 3; i++)
            {
                host.OnRemoved(null, PuzzleRemovalReason.Reaped, solved: false);
                world.Drain();
            }

            var p1Placements = world.Placements.Where(p => p.Site.Id == "bb_p1").ToList();
            Assert.AreEqual(1 + SpeedCourseRun.MaxPuzzleReplaceTries, p1Placements.Count, "the original plus two re-places");
            Assert.AreEqual(1, p1Placements.Select(p => p.Pick.Type).Distinct().Count(), "a re-place keeps the slot's type");
            Assert.AreEqual(p1Placements.Count, p1Placements.Select(p => p.Pick.Seed).Distinct().Count(), "each re-place has a new seed");
            Assert.IsTrue(world.Errors.Any(e => e.Contains("puzzle P1") && e.Contains("giving up")));
        }

        [TestMethod]
        public void RemovedAfterASolve_IsNotRePlaced()
        {
            var (run, world, _) = NewRun();
            run.Tick(0.5);
            world.Drain();

            var host = (SpeedPuzzleGateHost)world.Placements.Single(p => p.Site.Id == "bb_p3").Host;
            host.OnRemoved(null, PuzzleRemovalReason.Cleared, solved: true);
            world.Drain();

            Assert.AreEqual(3, world.Placements.Count);
        }

        [TestMethod]
        public void Snapshot_ReportsNamedDeathsAndBossHealth_ForTheHazardDriver()
        {
            var (run, world, hazards) = NewRun();
            run.Tick(0.5);
            world.Drain();

            world.Dead.Add(run.K2.Guid);
            run.Tick(1.0);

            var s = hazards.Ticks.Last();
            Assert.IsFalse(s.K1Spawned, "K1 is held behind P2");
            Assert.IsFalse(s.K1Dead);
            Assert.IsTrue(s.K2Dead);
            Assert.IsFalse(s.BossSpawned);
            Assert.AreEqual(1.0, s.Now);

            world.Seals["bb_seal"] = true;
            run.Tick(1.5);
            world.Health[run.Boss.Guid] = 0.25;
            run.Tick(2.0);

            s = hazards.Ticks.Last();
            Assert.IsTrue(s.BossSpawned);
            Assert.IsFalse(s.BossDead);
            Assert.AreEqual(0.25, s.BossHealthFraction, 1e-9);

            var view = s.ForHazard(true, false, null, null, SpeedHazardEnd.None);
            Assert.IsTrue(view.BossAlive);
            Assert.IsTrue(view.K2Dead);
        }

        [TestMethod]
        public void Teardown_WhenTheRunnerFinishesOrForfeits_ClearsPuzzlesAndStopsHazardsOnce()
        {
            var (run, world, hazards) = NewRun();
            SpeedCourseRun endedCallback = null;
            run.Ended = r => endedCallback = r;

            run.Tick(0.5);
            world.Drain();

            // Finish, forfeit and death all leave the runner online but no longer bound to the instance.
            world.RunnerView = new SpeedRunnerView { Online = true, BoundHere = false };
            run.Tick(1.0);

            Assert.IsTrue(run.IsEnded);
            Assert.AreEqual(1, world.ClearCalls);
            CollectionAssert.AreEqual(new[] { "run finished or forfeited" }, hazards.Stops);
            Assert.AreSame(run, endedCallback);

            // A removal arriving after teardown (the ClearForRun it just did) re-places nothing; further ticks are inert.
            var host = (SpeedPuzzleGateHost)world.Placements[0].Host;
            host.OnRemoved(null, PuzzleRemovalReason.Cleared, solved: false);
            world.Drain();
            run.Tick(1.5);
            run.End("again");

            Assert.AreEqual(3, world.Placements.Count);
            Assert.AreEqual(1, world.ClearCalls);
            Assert.AreEqual(1, hazards.Stops.Count);
        }

        [TestMethod]
        public void Teardown_RunnerOffline_EndsTheRun()
        {
            var (run, world, _) = NewRun();
            world.RunnerView = new SpeedRunnerView { Online = false };

            run.Tick(0.5);

            Assert.IsTrue(run.IsEnded);
            Assert.AreEqual("runner offline", run.EndReason);
            Assert.AreEqual(0, world.BuildPlanCalls, "an ended run never populates");
        }

        [TestMethod]
        public void NeverBound_IsHeldThroughTheGrace_ThenEnded()
        {
            var (run, world, _) = NewRun();
            world.RunnerView = new SpeedRunnerView { Online = true, BoundHere = false };

            run.Tick(SpeedCourseRules.RunnerBindGraceSeconds - 1);
            Assert.IsFalse(run.IsEnded, "the arrival has not armed yet");
            Assert.IsTrue(run.Populated, "the course populates while the runner is still in transit");

            run.Tick(SpeedCourseRules.RunnerBindGraceSeconds + 1);
            Assert.IsTrue(run.IsEnded);
        }

        [TestMethod]
        public void EndDuringPopulate_SweepsThePuzzlesPlacedMeanwhile_AndSpawnsNothing()
        {
            var (run, world, hazards) = NewRun();

            // End lands (world thread) while the first gate is being placed.
            world.OnPlacePuzzle = _ => run.End("instance landblock gone");

            run.Tick(0.5);
            world.Drain();

            Assert.IsTrue(run.IsEnded);
            Assert.AreEqual(1, world.Placements.Count, "the in-flight placement registered, then the loop stopped");
            Assert.AreEqual(2, world.ClearCalls, "End's own clear, then the sweep after the placements");
            Assert.AreEqual(0, world.Spawns.Count, "an ended run spawns nothing");
            Assert.AreEqual(1, hazards.Stops.Count);
        }

        [TestMethod]
        public void EndDuringARePlace_SweepsTheNewPlacement()
        {
            var (run, world, _) = NewRun();
            run.Tick(0.5);
            world.Drain();

            var host = (SpeedPuzzleGateHost)world.Placements.Single(p => p.Site.Id == "bb_p1").Host;
            world.OnPlacePuzzle = _ => run.End("instance landblock gone");

            host.OnRemoved(null, PuzzleRemovalReason.Reaped, solved: false);
            world.Drain();

            Assert.IsTrue(run.IsEnded);
            Assert.AreEqual(4, world.Placements.Count, "the re-place registered while End ran");
            Assert.AreEqual(2, world.ClearCalls, "End's clear, then the sweep after the re-place");
        }

        [TestMethod]
        public void NoSealGate_IsLoggedOnceAsAnError()
        {
            var (run, world, _) = NewRun();

            for (var i = 1; i <= 10; i++)
            {
                run.Tick(0.5 * i);
                world.Drain();
            }

            Assert.AreEqual(1, world.Errors.Count(e => e.Contains("no objective gate keyed 'bb_seal'")), string.Join(" | ", world.Errors));

            // A latched Seal still works normally afterwards.
            world.Seals["bb_seal"] = true;
            run.Tick(6.0);
            Assert.IsFalse(run.Boss.IsNone);
        }

        [TestMethod]
        public void ObjectivePlacement_IsCapped_ThenGivesUpWithAnError()
        {
            var (run, world, _) = NewRun();
            world.Plan = PlanWithoutBoss();
            world.FallbackBoss = 0;
            world.FailPlaceItem = true;

            run.Tick(0.5);
            world.Drain();
            world.Seals["bb_seal"] = true;

            for (var i = 0; i < 10; i++)
                run.Tick(1.0 + i);

            Assert.AreEqual(SpeedCourseRun.MaxObjectivePlaceAttempts, world.PlaceItemCalls);
            Assert.AreEqual(1, world.Errors.Count(e => e.Contains("could not be placed") && e.Contains("giving up")));
            Assert.IsFalse(run.ObjectiveItemPlaced);
        }

        [TestMethod]
        public void PlacePuzzleThrowing_GivesEverySlotUp_K1Enters_AndP1P3AreGranted()
        {
            var (run, world, _) = NewRun();
            world.OnPlacePuzzle = _ => throw new System.InvalidOperationException("placement threw in the fake");

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(0, world.Placements.Count);
            Assert.IsTrue(run.WasGranted("p1"), "P1 given up: p1 granted");
            Assert.IsTrue(run.WasGranted("p3"), "P3 given up: p3 granted");
            Assert.IsTrue(world.SealTokens.Contains("p1") && world.SealTokens.Contains("p3"), "the grants reach the Seal");
            Assert.IsFalse(run.IsNamedHeld(SpeedNamedSlots.K1), "P2 given up: K1 is released");
            Assert.IsFalse(run.K1.IsNone, "K1 spawned");
            Assert.AreEqual(3, world.Errors.Count(e => e.Contains("placement threw; giving the slot up")), string.Join(" | ", world.Errors));
        }

        [TestMethod]
        public void PlacePuzzleThrowingForOneSlot_StillPlacesTheLaterSlots()
        {
            var (run, world, _) = NewRun();
            world.OnPlacePuzzle = site => { if (site.Id == "bb_p1") throw new System.InvalidOperationException("P1 threw in the fake"); };

            run.Tick(0.5);
            world.Drain();

            CollectionAssert.AreEquivalent(new[] { "bb_p2", "bb_p3" }, world.Placements.Select(p => p.Site.Id).ToList(), "P2 and P3 are still placed");
            Assert.IsTrue(run.WasGranted("p1"));
            Assert.IsFalse(run.WasGranted("p3"), "P3 stands: its token is earned, not granted");
            Assert.IsTrue(run.IsNamedHeld(SpeedNamedSlots.K1), "P2 stands: K1 still waits behind it");
        }

        [TestMethod]
        public void PuzzlePlacementThrowingOutsideASlot_GivesUpEverySlotNotPlaced()
        {
            var (run, world, _) = NewRun();
            world.ThrowOnPuzzleSites = true;

            run.Tick(0.5);
            world.Drain();

            Assert.IsTrue(run.WasGranted("p1"));
            Assert.IsTrue(run.WasGranted("p3"));
            Assert.IsFalse(run.K1.IsNone, "K1 is not held behind a gate that was never placed");
            Assert.AreEqual(1, world.Errors.Count(e => e.Contains("giving up every slot not placed")));
        }

        [TestMethod]
        public void RunLevel_UsesTheSeasonAndCourseFloors()
        {
            Assert.AreEqual(60, NewRun(playerLevel: 20).Run.RunLevel);
            Assert.AreEqual(180, NewRun(playerLevel: 180).Run.RunLevel);
        }

        [TestMethod]
        public void RunLevel_FixedLevelCourse_IsFlatWhateverThePlayerLevel()
        {
            foreach (var player in new[] { 20, 275 })
            {
                var (run, world, _) = NewRun(playerLevel: player, fixedLevel: 150);
                Assert.AreEqual(150, run.RunLevel, $"player level {player}");

                run.Tick(0.5);
                world.Drain();
                Assert.IsNotNull(world.LastSpec, "the run built a plan");
                Assert.AreEqual(150, world.LastSpec.Level, $"gem spec level at player level {player}");
            }
        }
    }
}

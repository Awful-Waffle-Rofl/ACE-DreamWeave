using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The run's door guardians against the fake world: how each is stamped and placed, the single contribution per door
    /// key once all of that key's guardians are down, a failed spawn or a missing plan opening the door instead of
    /// softlocking it, an ended run contributing nothing, and the plan's own creatures spawning exactly as without
    /// guardians. No landblock, no database, no PropertyManager.
    /// </summary>
    [TestClass]
    public class SpeedCourseGuardianRunTests
    {
        private const uint BookWcid = 1007500;
        private const uint Owner = 0x50000042;
        private const string DoorLine = "DOOR OPEN";

        private static DungeonSpawnPointDef Pt(float x, float y) => new DungeonSpawnPointDef { Cell = 0x00890264, X = x, Y = y, Z = -6 };

        private static SpeedGuardianDef Guardian(string id, string key, float x, float scale = 1f)
            => new SpeedGuardianDef { Id = id, DoorKeys = new List<string> { key }, Position = Pt(x, -150), DisplayName = "Cell Keeper", Scale = scale };

        private static SpeedGuardianDef Keeper(string id, float x, params string[] keys)
            => new SpeedGuardianDef { Id = id, DoorKeys = keys.ToList(), Position = Pt(x, -150), DisplayName = "Cell Keeper" };

        private static (SpeedCourseRun Run, SpeedCourseFakeWorld World) NewRun(IEnumerable<SpeedGuardianDef> guardians, DungeonSpawnPlan plan = null, bool usePlan = true)
        {
            var world = new SpeedCourseFakeWorld { Plan = usePlan ? plan ?? SpeedCourseFakeWorld.SamplePlan() : null };
            world.AddBlackBookSites();

            var course = SpeedCourseFixtures.Course();
            course.Guardians = guardians?.ToList() ?? new List<SpeedGuardianDef>();
            course.Messages.GuardianDoor = DoorLine;

            var run = new SpeedCourseRun(course, 3, 60, BookWcid, 120, Owner, 12345, world, now: 0.0);
            run.AttachHazardDriver(new RecordingHazardDriver());
            return (run, world);
        }

        private static (DungeonSpawnPlanEntry Entry, SpeedCreatureStamp Stamp, SpeedCreatureHandle Handle) SpawnOf(SpeedCourseFakeWorld world, string id)
            => world.Spawns.Single(s => s.Stamp?.Slot == id);

        private static List<(string Key, string Token, double Weight)> DoorContributions(SpeedCourseFakeWorld world, string key)
            => world.Contributions.Where(c => c.Key == key).ToList();

        [TestMethod]
        public void Guardians_AreStampedAndPlaced_AfterThePlansCreatures()
        {
            var g1 = Guardian("G1", "door_a", 1, 1.5f);
            var g2 = Guardian("G2", "door_b", 2);
            var g3 = Guardian("G3", "door_c", 3);
            var (run, world) = NewRun(new[] { g1, g2, g3 });

            run.Tick(0.5);
            world.Drain();

            // 6 trash + K2 (K1 is held behind P2) + 3 guardians.
            Assert.AreEqual(10, world.Spawns.Count);
            CollectionAssert.AreEqual(new[] { "G1", "G2", "G3" }, world.Spawns.Skip(7).Select(s => s.Stamp.Slot).ToArray(), "guardians queue behind the plan's creatures");

            var s1 = SpawnOf(world, "G1");
            Assert.AreEqual(200u, s1.Entry.Wcid, "guardian 0 clones the first elite");
            Assert.AreEqual(201u, SpawnOf(world, "G2").Entry.Wcid, "guardian 1 clones the second elite");
            Assert.AreEqual(200u, SpawnOf(world, "G3").Entry.Wcid, "guardian 2 cycles back to the first");
            Assert.AreEqual(DungeonRole.Elite, s1.Entry.Role);
            Assert.AreSame(g1.Position, s1.Entry.Point);
            Assert.AreEqual("Cell Keeper", s1.Stamp.DisplayName);
            Assert.AreEqual(1.5f, s1.Stamp.Scale);
            Assert.IsNull(s1.Stamp.ObjectiveKey, "a guardian is not an objective contributor: the run contributes for it");
            Assert.AreEqual(0u, s1.Stamp.TreasureWcid);

            Assert.AreEqual(0, world.Contributions.Count, "nothing opens while every guardian stands");
            Assert.IsFalse(run.GuardianDoorOpened("door_a"));
        }

        [TestMethod]
        public void KillingAGuardian_ContributesToItsDoor_ExactlyOnce()
        {
            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_b", 2) });

            run.Tick(0.5);
            world.Drain();

            world.Dead.Add(SpawnOf(world, "G1").Handle.Guid);

            for (var t = 1; t <= 5; t++)
                run.Tick(t);

            var door = DoorContributions(world, "door_a");
            Assert.AreEqual(1, door.Count, "one contribution per key, whatever the tick count");
            Assert.AreEqual((Key: "door_a", Token: SpeedCourseRun.GuardianToken, Weight: 1.0), door[0]);
            Assert.IsTrue(run.GuardianDoorOpened("door_a"));
            Assert.AreEqual(1, world.Tells.Count(t => t == DoorLine), "the runner is told once");

            Assert.AreEqual(0, DoorContributions(world, "door_b").Count, "the other door waits for its own guardian");
            Assert.AreEqual(0, DoorContributions(world, "bb_seal").Count, "a guardian never feeds the Seal");
        }

        /// <summary>Owner ruling 2026-10-25: a Cell Keeper's death opens BOTH doors of its room, each once, with one line.</summary>
        [TestMethod]
        public void AKeeperWithTwoKeys_OpensBothDoorsOnOneDeath_WithOneLine()
        {
            var (run, world) = NewRun(new[] { Keeper("G_NW", 1, "nw_out", "nw_in"), Keeper("G_SW", 2, "sw_out", "sw_in") });

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(0, world.Contributions.Count, "both keepers stand");

            world.Dead.Add(SpawnOf(world, "G_NW").Handle.Guid);

            for (var t = 1; t <= 4; t++)
                run.Tick(t);

            Assert.AreEqual(1, DoorContributions(world, "nw_out").Count);
            Assert.AreEqual(1, DoorContributions(world, "nw_in").Count);
            Assert.IsTrue(run.GuardianDoorOpened("nw_out") && run.GuardianDoorOpened("nw_in"));
            Assert.AreEqual(1, world.Tells.Count(t => t == DoorLine), "one line for the keeper, not one per door");

            Assert.AreEqual(0, DoorContributions(world, "sw_out").Count + DoorContributions(world, "sw_in").Count, "the other room waits for its own keeper");
        }

        /// <summary>A keeper that cannot be fielded opens every door it names.</summary>
        [TestMethod]
        public void AFailedKeeperSpawn_OpensAllItsDoors()
        {
            var plan = SpeedCourseFakeWorld.SamplePlan();
            plan.Entries.Insert(8, new DungeonSpawnPlanEntry(202, DungeonRole.Elite, SpeedCourseFakeWorld.Point(42, -42)));

            var (run, world) = NewRun(new[] { Keeper("G1", 1, "a_out", "a_in"), Keeper("G2", 2, "b_out", "b_in"), Keeper("G3", 3, "c_out", "c_in") }, plan);
            world.FailingWcids.Add(202);

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(1, DoorContributions(world, "c_out").Count);
            Assert.AreEqual(1, DoorContributions(world, "c_in").Count);
            Assert.AreEqual(0, world.Tells.Count(t => t == DoorLine), "nobody killed it");
            Assert.AreEqual(0, DoorContributions(world, "a_out").Count + DoorContributions(world, "b_in").Count);
        }

        [TestMethod]
        public void NoPlan_GrantsEveryKeysDoor()
        {
            var (run, world) = NewRun(new[] { Keeper("G1", 1, "a_out", "a_in"), Keeper("G2", 2, "b_out", "b_in") }, usePlan: false);

            run.Tick(0.5);
            world.Drain();

            foreach (var key in new[] { "a_out", "a_in", "b_out", "b_in" })
                Assert.AreEqual(1, DoorContributions(world, key).Count, key);
        }

        /// <summary>A key two keepers name opens only when both are down; each keeper's own key opens on its own death.</summary>
        [TestMethod]
        public void ASharedKey_WaitsForEveryKeeperNamingIt()
        {
            var (run, world) = NewRun(new[] { Keeper("G1", 1, "a_only", "shared"), Keeper("G2", 2, "b_only", "shared") });

            run.Tick(0.5);
            world.Drain();

            world.Dead.Add(SpawnOf(world, "G1").Handle.Guid);
            run.Tick(1);

            Assert.AreEqual(1, DoorContributions(world, "a_only").Count);
            Assert.AreEqual(0, DoorContributions(world, "shared").Count, "G2 still names it");
            Assert.AreEqual(1, world.Tells.Count(t => t == DoorLine));

            world.Dead.Add(SpawnOf(world, "G2").Handle.Guid);
            run.Tick(2);
            run.Tick(3);

            Assert.AreEqual(1, DoorContributions(world, "b_only").Count);
            Assert.AreEqual(1, DoorContributions(world, "shared").Count);
            Assert.AreEqual(2, world.Tells.Count(t => t == DoorLine), "one line per keeper");
        }

        [TestMethod]
        public void TwoGuardiansOnOneKey_TheDoorOpensOnlyOnTheSecondDeath()
        {
            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_a", 2) });

            run.Tick(0.5);
            world.Drain();

            world.Dead.Add(SpawnOf(world, "G1").Handle.Guid);
            run.Tick(1);
            run.Tick(2);

            Assert.AreEqual(0, DoorContributions(world, "door_a").Count, "one of two down: the door stays shut");
            Assert.AreEqual(0, world.Tells.Count(t => t == DoorLine));

            world.Dead.Add(SpawnOf(world, "G2").Handle.Guid);
            run.Tick(3);
            run.Tick(4);

            Assert.AreEqual(1, DoorContributions(world, "door_a").Count);
            Assert.AreEqual(1, world.Tells.Count(t => t == DoorLine));
        }

        [TestMethod]
        public void AFailedGuardianSpawn_OpensItsDoor_NeverThroughTheNamedGrant()
        {
            // Three elites so guardian 2 clones its own wcid (202), which the fake refuses.
            var plan = SpeedCourseFakeWorld.SamplePlan();
            plan.Entries.Insert(8, new DungeonSpawnPlanEntry(202, DungeonRole.Elite, SpeedCourseFakeWorld.Point(42, -42)));

            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_b", 2), Guardian("G3", "door_c", 3) }, plan);
            world.FailingWcids.Add(202);

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(SpeedCreatureHandle.None, SpawnOf(world, "G3").Handle, "the fake refused G3");

            var door = DoorContributions(world, "door_c");
            Assert.AreEqual(1, door.Count, "the failed guardian's door opens");
            Assert.AreEqual(SpeedCourseRun.GuardianToken, door[0].Token);
            Assert.AreEqual(0, world.Tells.Count(t => t == DoorLine), "nobody killed it: no fall line");
            Assert.IsTrue(world.Errors.Any(e => e.Contains("guardian G3") && e.Contains("failed to spawn")), string.Join(" | ", world.Errors));

            // No named-slot fallout: no Seal token was granted and K2 stands.
            Assert.AreEqual(0, DoorContributions(world, "bb_seal").Count, string.Join(" | ", world.Contributions));
            Assert.IsFalse(run.WasGranted("north_rite"));
            Assert.IsFalse(run.WasGranted("south_rite"));
            Assert.IsFalse(run.K2.IsNone);

            Assert.AreEqual(0, DoorContributions(world, "door_a").Count);
            Assert.AreEqual(0, DoorContributions(world, "door_b").Count);

            run.Tick(1);
            run.Tick(2);
            Assert.AreEqual(1, DoorContributions(world, "door_c").Count, "still once");
        }

        [TestMethod]
        public void ADoorWithOneFailedAndOneKilledGuardian_OpensOnTheKill_WithTheLine()
        {
            var plan = SpeedCourseFakeWorld.SamplePlan();
            plan.Entries.Insert(8, new DungeonSpawnPlanEntry(202, DungeonRole.Elite, SpeedCourseFakeWorld.Point(42, -42)));

            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_b", 2), Guardian("G3", "door_a", 3) }, plan);
            world.FailingWcids.Add(202);

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(0, DoorContributions(world, "door_a").Count, "G1 still stands");

            world.Dead.Add(SpawnOf(world, "G1").Handle.Guid);
            run.Tick(1);

            Assert.AreEqual(1, DoorContributions(world, "door_a").Count);
            Assert.AreEqual(1, world.Tells.Count(t => t == DoorLine));
        }

        [TestMethod]
        public void NoPlan_GrantsEveryGuardianDoor_Once()
        {
            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_a", 2), Guardian("G3", "door_b", 3) }, usePlan: false);

            run.Tick(0.5);
            world.Drain();
            run.Tick(1);

            Assert.AreEqual(0, world.Spawns.Count);
            Assert.AreEqual(1, DoorContributions(world, "door_a").Count);
            Assert.AreEqual(1, DoorContributions(world, "door_b").Count);
            Assert.AreEqual(0, world.Tells.Count(t => t == DoorLine));
            Assert.IsTrue(run.GuardianDoorOpened("door_a") && run.GuardianDoorOpened("door_b"));
        }

        [TestMethod]
        public void APlanWithNoNonBossCreature_GrantsEveryGuardianDoor()
        {
            var plan = new DungeonSpawnPlan { BossWcid = 300 };
            plan.Entries.Add(new DungeonSpawnPlanEntry(300, DungeonRole.Boss, SpeedCourseFakeWorld.Point(99, -99)));

            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_b", 2) }, plan);

            run.Tick(0.5);
            world.Drain();

            Assert.AreEqual(1, DoorContributions(world, "door_a").Count);
            Assert.AreEqual(1, DoorContributions(world, "door_b").Count);
        }

        /// <summary>
        /// A spawn step that throws abandons the rest of its queue. Every abandoned guardian, including the one being
        /// spawned when it threw (already dequeued), counts as down, so no door is left shut with nothing to kill.
        /// </summary>
        [TestMethod]
        public void AnAbandonedSpawnStep_OpensEveryUnfieldedGuardiansDoor()
        {
            // Elites 202, 200, 201 in plan order: G1 clones 202, which fails; the failure's error log throws inside the step.
            var plan = SpeedCourseFakeWorld.SamplePlan();
            plan.Entries.Insert(6, new DungeonSpawnPlanEntry(202, DungeonRole.Elite, SpeedCourseFakeWorld.Point(42, -42)));

            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_b", 2), Guardian("G3", "door_c", 3) }, plan);
            world.FailingWcids.Add(202);
            world.ThrowOnLogError = 1;

            run.Tick(0.5);
            world.Drain();

            Assert.IsTrue(world.Errors.Any(e => e.Contains("spawn step threw")), string.Join(" | ", world.Errors));
            Assert.IsFalse(world.Spawns.Any(s => s.Stamp?.Slot == "G2" || s.Stamp?.Slot == "G3"), "G2 and G3 were abandoned, never spawned");

            foreach (var key in new[] { "door_a", "door_b", "door_c" })
                Assert.AreEqual(1, DoorContributions(world, key).Count, $"{key} opens once");

            Assert.AreEqual(0, world.Tells.Count(t => t == DoorLine), "nobody was killed");
        }

        /// <summary>A contribution that throws is not counted as made: the door is retried on the next tick, and opened once.</summary>
        [TestMethod]
        public void AThrowingContribution_IsRetriedNextTick_ExactlyOnce()
        {
            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1) });

            run.Tick(0.5);
            world.Drain();

            world.Dead.Add(SpawnOf(world, "G1").Handle.Guid);
            world.ThrowOnContribute = 1;

            run.Tick(1);
            Assert.AreEqual(0, DoorContributions(world, "door_a").Count);
            Assert.IsFalse(run.GuardianDoorOpened("door_a"));
            Assert.IsTrue(world.Errors.Any(e => e.Contains("door_a") && e.Contains("retrying")), string.Join(" | ", world.Errors));

            run.Tick(2);
            run.Tick(3);
            Assert.AreEqual(1, DoorContributions(world, "door_a").Count);
            Assert.IsTrue(run.GuardianDoorOpened("door_a"));
            Assert.AreEqual(1, world.Tells.Count(t => t == DoorLine));
        }

        [TestMethod]
        public void AnEndedRun_ContributesNothing()
        {
            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1) });

            run.Tick(0.5);
            world.Drain();

            run.End("test ended");
            world.Dead.Add(SpawnOf(world, "G1").Handle.Guid);

            run.Tick(1);
            run.Tick(2);
            world.Drain();

            Assert.AreEqual(0, DoorContributions(world, "door_a").Count);
            Assert.IsFalse(run.GuardianDoorOpened("door_a"));
            Assert.AreEqual(0, world.Tells.Count(t => t == DoorLine));
        }

        /// <summary>
        /// The draw guard at run level: the plan's own creatures spawn exactly as they do without guardians (same order,
        /// wcids, roles, points, stamps), and the plan the world built is left exactly as it was.
        /// </summary>
        [TestMethod]
        public void Guardians_LeaveThePlansOwnSpawnsAndThePlanItselfUnchanged()
        {
            var (bare, bareWorld) = NewRun(null);
            bare.Tick(0.5);
            bareWorld.Drain();

            var (run, world) = NewRun(new[] { Guardian("G1", "door_a", 1), Guardian("G2", "door_b", 2), Guardian("G3", "door_c", 3), Guardian("G4", "door_d", 4) });
            var planBefore = world.Plan.Entries.ToList();
            var fieldsBefore = world.Plan.Entries.Select(e => (e.Wcid, e.Role, e.Point, e.UpliftLevel, e.StampLevel)).ToList();

            run.Tick(0.5);
            world.Drain();

            string Describe((DungeonSpawnPlanEntry Entry, SpeedCreatureStamp Stamp, SpeedCreatureHandle Handle) s)
                => $"{s.Entry.Wcid}/{s.Entry.Role}/{s.Entry.Point.X},{s.Entry.Point.Y}/{s.Stamp?.Slot ?? "-"}/{s.Stamp?.DisplayName ?? "-"}";

            var withGuardians = world.Spawns.Where(s => s.Stamp?.Slot == null || !s.Stamp.Slot.StartsWith("G")).Select(Describe).ToList();
            CollectionAssert.AreEqual(bareWorld.Spawns.Select(Describe).ToList(), withGuardians, "the plan's own spawns are unchanged");
            Assert.AreEqual(4, world.Spawns.Count - bareWorld.Spawns.Count, "exactly one extra creature per guardian");

            Assert.AreEqual(planBefore.Count, world.Plan.Entries.Count);

            for (var i = 0; i < planBefore.Count; i++)
            {
                Assert.AreSame(planBefore[i], world.Plan.Entries[i]);
                var e = world.Plan.Entries[i];
                Assert.AreEqual(fieldsBefore[i], (e.Wcid, e.Role, e.Point, e.UpliftLevel, e.StampLevel));
            }

            Assert.AreEqual(1, world.BuildPlanCalls, "one plan build: guardians never ask for another draw");
            Assert.AreEqual(bareWorld.LastSpec.Seed, world.LastSpec.Seed);
        }
    }
}

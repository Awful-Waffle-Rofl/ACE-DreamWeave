using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;
using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The speed-course boss phases: SpeedBossPhaseRules (floors, validation), SpeedBossPhaseMachine against a recording
    /// port (transitions, latch, failsafe timeout, refusal give-up, master switch off, boss death), and the full
    /// entry -> 75 -> 50 -> 25 -> killable sequence through SpeedCourseHazardHost on the fake world and hazard port.
    /// </summary>
    [TestClass]
    public class SpeedBossPhaseTests
    {
        // ======================================================================================== rules

        [TestMethod]
        public void FloorFor_is_the_ceiling_clamped_to_one_and_max()
        {
            Assert.AreEqual(1000u, SpeedBossPhaseRules.FloorFor(1000, 1.0));
            Assert.AreEqual(750u, SpeedBossPhaseRules.FloorFor(1000, 0.75));
            Assert.AreEqual(334u, SpeedBossPhaseRules.FloorFor(1001, 1.0 / 3.0), "ceil(333.67)");
            Assert.AreEqual(1u, SpeedBossPhaseRules.FloorFor(10, 0.0001), "never 0 for a positive fraction");
            Assert.AreEqual(10u, SpeedBossPhaseRules.FloorFor(10, 5.0), "clamped to max");
            Assert.AreEqual(0u, SpeedBossPhaseRules.FloorFor(0, 0.5), "no health, no floor");
            Assert.AreEqual(0u, SpeedBossPhaseRules.FloorFor(100, double.NaN));
        }

        private static SpeedHazardDef PhaseHazard(string id, string trigger = SpeedHazardTriggers.BossPhase)
            => new SpeedHazardDef
            {
                Id = id,
                Kind = "balls",
                Pattern = "fan",
                Region = HubRegion(),
                Trigger = new SpeedHazardTriggerDef { Type = trigger, Seconds = 8 },
            };

        private static SpeedHazardRegionDef HubRegion()
            => new SpeedHazardRegionDef { Shape = "rect", MinX = 195, MaxX = 225, MinY = -225, MaxY = -195, GroundZ = 0, CeilingZ = 16 };

        private static List<SpeedBossPhaseDef> StandardPhases(bool entryRegion = true) => new List<SpeedBossPhaseDef>
        {
            new SpeedBossPhaseDef { Id = "entry", AtFraction = 1.0, Hazards = { "p_entry" }, TimeoutSeconds = 45, TriggerRegion = entryRegion ? HubRegion() : null },
            new SpeedBossPhaseDef { Id = "p75", AtFraction = 0.75, Hazards = { "p_rain" }, TimeoutSeconds = 60 },
            new SpeedBossPhaseDef { Id = "p50", AtFraction = 0.50, Hazards = { "p_scatter" }, TimeoutSeconds = 90 },
            new SpeedBossPhaseDef { Id = "p25", AtFraction = 0.25, Hazards = { "p_row", "p_cross" }, TimeoutSeconds = 120 },
        };

        private static readonly string[] StandardHazardIds = { "p_entry", "p_rain", "p_scatter", "p_row", "p_cross" };

        private static SpeedCourseDef PhasedCourse()
        {
            var c = new SpeedCourseDef { BossPhases = StandardPhases() };
            foreach (var id in StandardHazardIds)
                c.Hazards.Add(PhaseHazard(id));
            return c;
        }

        [TestMethod]
        public void Validate_accepts_the_standard_phases()
        {
            var problems = SpeedBossPhaseRules.Validate(PhasedCourse());
            Assert.AreEqual(0, problems.Count, string.Join("; ", problems));
        }

        [TestMethod]
        public void Validate_refuses_bad_phases()
        {
            void Expect(Action<SpeedCourseDef> mutate, string fragment)
            {
                var c = PhasedCourse();
                mutate(c);
                var problems = SpeedBossPhaseRules.Validate(c);
                Assert.IsTrue(problems.Any(p => p.Contains(fragment)), $"expected '{fragment}' in [{string.Join("; ", problems)}]");
            }

            Expect(c => c.BossPhases[2].AtFraction = 0.75, "must be below the previous phase");
            Expect(c => c.BossPhases[0].AtFraction = 1.5, "must be in (0, 1]");
            Expect(c => c.BossPhases[3].AtFraction = 0, "must be in (0, 1]");
            Expect(c => c.BossPhases[1].TimeoutSeconds = 0, "timeoutSeconds");
            Expect(c => c.BossPhases[1].Hazards.Add("nope"), "'nope' is not a hazard of this course");
            Expect(c => c.BossPhases[1].Hazards.Add("p_entry"), "already a step of another phase");
            Expect(c => c.BossPhases[1].Id = "entry", "duplicate id");
            Expect(c => c.BossPhases[1].Hazards.Clear(), "hazards is empty");
            Expect(c => c.Hazards.Add(PhaseHazard("orphan")), "no boss phase lists it");
            Expect(c => c.Hazards[1].Trigger.Type = SpeedHazardTriggers.BossAliveInterval, "must use trigger boss_phase");
            Expect(c => c.Hazards[0].Token = "south_rite", "cannot carry a Seal token");
        }

        [TestMethod]
        public void Validate_ascending_threshold_is_refused()
        {
            var c = PhasedCourse();
            c.BossPhases[1].AtFraction = 0.4; // 0.4 then 0.5
            var problems = SpeedBossPhaseRules.Validate(c);
            Assert.IsTrue(problems.Any(p => p.Contains("'p50'") && p.Contains("must be below")), string.Join("; ", problems));
        }

        [TestMethod]
        public void A_course_without_phases_validates_clean()
        {
            Assert.AreEqual(0, SpeedBossPhaseRules.Validate(new SpeedCourseDef()).Count);
            Assert.AreEqual(0, SpeedBossPhaseRules.Validate(null).Count);
        }

        // ======================================================================================== machine

        private sealed class FakePort : ISpeedBossPhasePort
        {
            public readonly List<(bool Immune, uint Floor)> Guards = new List<(bool, uint)>();
            public readonly List<string> Starts = new List<string>();
            public readonly List<string> Stops = new List<string>();
            public readonly List<string> Lines = new List<string>();
            public Func<string, SpeedBossStepStart> Result = _ => SpeedBossStepStart.Started;

            public void SetGuard(bool immune, uint floorHp) => Guards.Add((immune, floorHp));

            public SpeedBossStepStart StartStep(string hazardId, double now)
            {
                var r = Result(hazardId);
                if (r == SpeedBossStepStart.Started)
                    Starts.Add(hazardId);
                return r;
            }

            public void StopStep(string hazardId, string reason) => Stops.Add(hazardId);

            public void Log(string line) => Lines.Add(line);
        }

        private const uint Max = 1000;

        [TestMethod]
        public void Machine_runs_the_full_sequence_and_ends_killable()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(), port);
            var inHub = false;
            bool RunnerIn(SpeedHazardRegionDef _) => inHub;

            // Entry phase: waits for the runner, not for damage.
            m.Tick(1, true, Max, Max, RunnerIn);
            Assert.IsFalse(m.InPhase, "the entry phase needs the runner in its region");
            Assert.AreEqual(0, port.Starts.Count);
            Assert.AreEqual(Max, m.CurrentFloor, "pinned at full health before the entry phase");

            inHub = true;
            m.Tick(2, true, Max, Max, RunnerIn);
            Assert.IsTrue(m.InPhase);
            CollectionAssert.AreEqual(new[] { "p_entry" }, port.Starts);
            Assert.AreEqual((true, Max), port.Guards.Last());

            m.Tick(3, true, Max, Max, RunnerIn);
            Assert.AreEqual(1, port.Starts.Count, "a running step is not started again");

            m.OnStepEnded("p_entry", 4);
            Assert.IsFalse(m.InPhase);
            Assert.AreEqual((false, 750u), port.Guards.Last(), "vulnerable down to the 75% floor");

            // Above the floor nothing happens; at the floor the 75 phase starts at once.
            m.Tick(5, true, 800, Max, RunnerIn);
            Assert.IsFalse(m.InPhase);
            m.Tick(6, true, 750, Max, RunnerIn);
            Assert.IsTrue(m.InPhase);
            Assert.AreEqual((true, 750u), port.Guards.Last());
            Assert.AreEqual("p_rain", port.Starts.Last());

            // The machine ignores the end of a hazard that is not its running step.
            m.OnStepEnded("p_entry", 7);
            Assert.IsTrue(m.InPhase);

            m.OnStepEnded("p_rain", 8);
            Assert.AreEqual((false, 500u), port.Guards.Last());

            // Latch: the finished phase never restarts, even with health still at its floor.
            m.Tick(9, true, 750, Max, RunnerIn);
            Assert.IsFalse(m.InPhase);
            Assert.AreEqual(2, port.Starts.Count);

            m.Tick(10, true, 500, Max, RunnerIn);
            Assert.AreEqual("p_scatter", port.Starts.Last());
            m.OnStepEnded("p_scatter", 11);
            Assert.AreEqual((false, 250u), port.Guards.Last());

            // The 25 phase runs row THEN cross; the boss stays immune between them.
            m.Tick(12, true, 250, Max, RunnerIn);
            Assert.AreEqual("p_row", port.Starts.Last());
            var guardsBefore = port.Guards.Count;
            m.OnStepEnded("p_row", 13);
            Assert.AreEqual("p_cross", port.Starts.Last(), "the next step starts as the previous one ends");
            Assert.IsTrue(m.InPhase);
            Assert.AreEqual(guardsBefore, port.Guards.Count, "no guard change between steps");

            m.OnStepEnded("p_cross", 14);
            Assert.IsTrue(m.Killable);
            Assert.AreEqual((false, 0u), port.Guards.Last(), "floor 0: killable");
            Assert.AreEqual(0u, m.CurrentFloor);

            m.Tick(15, true, 1, Max, RunnerIn);
            CollectionAssert.AreEqual(new[] { "p_entry", "p_rain", "p_scatter", "p_row", "p_cross" }, port.Starts);
            Assert.AreEqual(0, port.Stops.Count);
        }

        [TestMethod]
        public void Machine_timeout_stops_the_running_step_and_moves_on()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(entryRegion: false), port);

            m.Tick(0, true, Max, Max, null);
            Assert.AreEqual("p_entry", port.Starts.Single());

            m.Tick(44.9, true, Max, Max, null);
            Assert.IsTrue(m.InPhase, "before the timeout");

            m.Tick(45, true, Max, Max, null);
            CollectionAssert.AreEqual(new[] { "p_entry" }, port.Stops);
            Assert.IsFalse(m.InPhase);
            Assert.AreEqual((false, 750u), port.Guards.Last());

            // A late end of the stopped step is ignored.
            m.OnStepEnded("p_entry", 46);
            Assert.AreEqual(1, m.PhaseIndex);
        }

        [TestMethod]
        public void Machine_skips_steps_that_cannot_start_when_the_master_switch_is_off()
        {
            var port = new FakePort { Result = _ => SpeedBossStepStart.Disabled };
            var m = new SpeedBossPhaseMachine(StandardPhases(entryRegion: false), port);

            m.Tick(0, true, Max, Max, null);
            Assert.IsFalse(m.InPhase, "the entry phase completes in the same tick");
            Assert.AreEqual(1, m.PhaseIndex);
            Assert.AreEqual((false, 750u), port.Guards.Last());

            m.Tick(1, true, 100, Max, null);
            m.Tick(2, true, 100, Max, null);
            m.Tick(3, true, 100, Max, null);
            Assert.IsTrue(m.Killable, "every phase passes through with no hazards");
            Assert.AreEqual((false, 0u), port.Guards.Last());
            Assert.AreEqual(0, port.Starts.Count);
        }

        [TestMethod]
        public void Machine_gives_a_refused_step_up_after_the_refusal_window()
        {
            var refuse = true;
            var port = new FakePort { Result = id => refuse && id == "p_row" ? SpeedBossStepStart.Refused : SpeedBossStepStart.Started };
            var phases = new List<SpeedBossPhaseDef>
            {
                new SpeedBossPhaseDef { Id = "p25", AtFraction = 0.25, Hazards = { "p_row", "p_cross" }, TimeoutSeconds = 500 },
            };
            var m = new SpeedBossPhaseMachine(phases, port);

            m.Tick(0, true, Max, Max, null);
            Assert.AreEqual(0, port.Guards.Count, "no guard write before the phase");

            m.Tick(10, true, 200, Max, null);
            Assert.IsTrue(m.InPhase);
            Assert.AreEqual(0, port.Starts.Count);

            m.Tick(39.9, true, 250, Max, null);
            Assert.AreEqual(0, port.Starts.Count, "still inside the refusal window");

            m.Tick(40, true, 250, Max, null);
            CollectionAssert.AreEqual(new[] { "p_cross" }, port.Starts, "row given up after 30 s; cross starts");
        }

        [TestMethod]
        public void Machine_stops_when_the_boss_dies_mid_phase()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(entryRegion: false), port);

            m.Tick(0, true, Max, Max, null);
            m.Tick(1, false, 0, 0, null);

            CollectionAssert.AreEqual(new[] { "p_entry" }, port.Stops);
            Assert.IsFalse(m.InPhase);

            m.Tick(2, true, Max, Max, null);
            Assert.AreEqual(1, port.Starts.Count, "a finished machine does nothing more");
        }

        [TestMethod]
        public void Machine_waits_for_the_boss()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(entryRegion: false), port);

            m.Tick(0, false, 0, 0, null);
            m.Tick(1, true, 0, 0, null);
            Assert.AreEqual(0, port.Starts.Count + port.Guards.Count);
            Assert.AreEqual(0u, m.CurrentFloor);

            m.Tick(2, true, Max, Max, null);
            Assert.AreEqual(1, port.Starts.Count);
        }

        [TestMethod]
        public void Machine_refreshes_the_pending_floor_when_max_health_drifts_between_phases()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(entryRegion: false), port);

            m.Tick(0, true, Max, Max, null);
            m.OnStepEnded("p_entry", 1);
            Assert.AreEqual((false, 750u), port.Guards.Last());

            var guards = port.Guards.Count;
            m.Tick(2, true, 800, 800, null);
            Assert.AreEqual(guards + 1, port.Guards.Count, "the drift re-issues the guard");
            Assert.AreEqual((false, 600u), port.Guards.Last(), "FloorFor(800, 0.75) = 600");
            Assert.AreEqual(600u, m.CurrentFloor);
            Assert.IsFalse(m.InPhase, "800 is above the new floor 600");

            m.Tick(3, true, 800, 800, null);
            Assert.AreEqual(guards + 1, port.Guards.Count, "no drift, no re-issue");

            m.Tick(4, true, 600, 800, null);
            Assert.IsTrue(m.InPhase, "the phase starts at the refreshed floor");
            Assert.AreEqual((true, 600u), port.Guards.Last());
        }

        [TestMethod]
        public void Machine_drift_before_the_entry_phase_keeps_the_boss_immune()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(), port);

            m.Tick(0, true, Max, Max, _ => false);
            m.Tick(1, true, 900, 900, _ => false);

            Assert.AreEqual((true, 900u), port.Guards.Single(), "the spawn guard was immune; the drift must not drop it");
            Assert.IsFalse(m.InPhase);
        }

        [TestMethod]
        public void Machine_keeps_a_running_phase_floor_through_a_drift()
        {
            var port = new FakePort();
            var m = new SpeedBossPhaseMachine(StandardPhases(entryRegion: false), port);

            m.Tick(0, true, Max, Max, null);
            m.OnStepEnded("p_entry", 1);
            m.Tick(2, true, 750, Max, null);
            Assert.IsTrue(m.InPhase);
            var guards = port.Guards.Count;

            m.Tick(3, true, 750, 800, null);
            Assert.AreEqual(guards, port.Guards.Count, "in a phase the floor stays where it started");

            m.OnStepEnded("p_rain", 4);
            Assert.AreEqual((false, 500u), port.Guards.Last(), "the phase ends on the max it ran with: FloorFor(1000, 0.5)");

            m.Tick(5, true, 750, 800, null);
            Assert.AreEqual((false, 400u), port.Guards.Last(), "out of the phase the floor follows the live max: FloorFor(800, 0.5)");
        }

        // ======================================================================================== host

        private const uint BookWcid = 1007500;
        private const uint Owner = 0x50000042;

        private sealed class HostRig
        {
            public SpeedCourseRun Run;
            public SpeedCourseFakeWorld World;
            public FakeSpeedHazardPort Port;
            public SpeedCourseHazardHost Host;
            public double Now;

            public void Tick(double dt = 0.5)
            {
                Now += dt;
                Run.Tick(Now);
                World.Drain();
            }

            public void Runner(float x, float y, float z)
                => World.RunnerView = new SpeedRunnerView { Online = true, BoundHere = true, HasPosition = true, Cell = 0x008902FA, X = x, Y = y, Z = z };

            public int StartOf(string id) => Port.StartsOf(id).Single().Id;

            public void End(string id, HazardOutcome outcome)
            {
                Host.OnRunEnded(StartOf(id), outcome);
                World.Drain();
            }

            public (bool Immune, uint Floor) Guard => World.Guards[Run.Boss.Guid];
        }

        private static HostRig NewHostRig(bool enabled = true)
        {
            var course = SpeedCourseFixtures.Course();
            course.Hazards.RemoveAll(h => h.Trigger?.Type == SpeedHazardTriggers.BossAliveInterval); // the retired boss fans
            foreach (var id in StandardHazardIds)
                course.Hazards.Add(PhaseHazard(id));
            course.BossPhases = StandardPhases();
            course.Named.Single(n => n.Slot == SpeedNamedSlots.Boss).Scale = 4f;

            Assert.AreEqual(0, SpeedBossPhaseRules.Validate(course).Count, "the rig's course validates");

            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();

            var run = new SpeedCourseRun(course, 3, 60, BookWcid, 120, Owner, 12345, world, now: 0.0);
            var port = new FakeSpeedHazardPort { Enabled = enabled };
            var host = new SpeedCourseHazardHost(run, port);
            run.AttachHazardDriver(host);

            var rig = new HostRig { Run = run, World = world, Port = port, Host = host };
            rig.Runner(-50, -50, -6);
            return rig;
        }

        private static void SpawnBoss(HostRig rig)
        {
            rig.World.Seals["bb_seal"] = true;
            rig.Tick();
            Assert.IsFalse(rig.Run.Boss.IsNone, "the Seal latch spawns the boss");
        }

        [TestMethod]
        public void Host_runs_entry_75_50_25_then_the_boss_is_killable()
        {
            var rig = NewHostRig();
            SpawnBoss(rig);

            var stamp = rig.World.Spawns.Single(s => s.Handle.Equals(rig.Run.Boss)).Stamp;
            Assert.AreEqual(4f, stamp.Scale, "the boss's scale reaches the stamp");
            Assert.AreEqual(1.0, stamp.GuardFloorFraction, "the first phase's threshold reaches the stamp");
            Assert.AreEqual((true, 1000u), rig.Guard, "spawned immune, pinned at full health");

            rig.Tick();
            Assert.AreEqual(0, rig.Port.StartsOf("p_entry").Count(), "the entry fan waits for the runner in the hub");

            rig.Runner(210, -210, 0);
            rig.Tick();
            var entry = rig.Port.StartsOf("p_entry").Single();
            Assert.IsFalse(entry.Request.CallerArgs.Has("cx"), "a boss_phase hazard is region-centred unless its args say otherwise");

            rig.Tick();
            Assert.AreEqual(1, rig.Port.StartsOf("p_entry").Count(), "boss_phase hazards are never started by the trigger rules");

            rig.End("p_entry", HazardOutcome.Completed);
            Assert.AreEqual((false, 750u), rig.Guard);

            // Overkill: the fake reads health through the floor, as Creature.UpdateVital pins it.
            rig.World.Damage(rig.Run.Boss, 0.10);
            Assert.AreEqual(750u, rig.World.CurrentHealth(rig.Run.Boss));
            rig.Tick();
            Assert.AreEqual((true, 750u), rig.Guard, "75: immune at the floor");
            rig.Port.StartsOf("p_rain").Single();

            // A Failed/Aborted end is restarted once before it completes a step (the abort guard, tested below).
            rig.End("p_rain", HazardOutcome.Completed);
            Assert.AreEqual((false, 500u), rig.Guard, "a Completed end completes the step");

            rig.Tick();
            rig.Port.StartsOf("p_scatter").Single();
            Assert.AreEqual((true, 500u), rig.Guard, "50: health was already under the floor");

            rig.End("p_scatter", HazardOutcome.Completed);
            Assert.AreEqual((false, 250u), rig.Guard);

            rig.Tick();
            rig.Port.StartsOf("p_row").Single();
            Assert.AreEqual(0, rig.Port.StartsOf("p_cross").Count());

            rig.End("p_row", HazardOutcome.Completed);
            rig.Port.StartsOf("p_cross").Single();
            Assert.AreEqual((true, 250u), rig.Guard, "still immune between row and cross");

            rig.End("p_cross", HazardOutcome.Completed);
            Assert.AreEqual((false, 0u), rig.Guard, "killable");
            Assert.IsTrue(rig.Host.BossPhases.Killable);

            var order = rig.Port.Starts.Select(s => s.Request.Hazard.Id).Where(StandardHazardIds.Contains).ToList();
            CollectionAssert.AreEqual(StandardHazardIds, order);
            Assert.AreEqual(0, rig.Port.Stops.Count, "no step was stopped");
        }

        // ---- the abort guard: a boss step whose hazard fails or aborts on its own is restarted once ----------------

        /// <summary>A rig in the 75% phase with p_rain running (its first start).</summary>
        private static HostRig RigInRainPhase()
        {
            var rig = NewHostRig();
            SpawnBoss(rig);
            rig.Runner(210, -210, 0);
            rig.Tick();
            rig.End("p_entry", HazardOutcome.Completed);

            rig.World.Damage(rig.Run.Boss, 0.10);
            rig.Tick();
            Assert.AreEqual((true, 750u), rig.Guard, "precondition: 75 phase, immune");
            rig.Port.StartsOf("p_rain").Single();
            Assert.AreEqual(0, rig.World.Errors.Count, "precondition: no errors yet");
            return rig;
        }

        private static void EndRun(HostRig rig, int runId, HazardOutcome outcome, string reason)
        {
            rig.Host.OnRunEnded(runId, outcome, reason);
            rig.World.Drain();
        }

        [TestMethod]
        public void Host_boss_step_first_abort_restarts_at_the_next_attempt_without_advancing()
        {
            var rig = RigInRainPhase();
            var first = rig.Port.StartsOf("p_rain").Single();

            EndRun(rig, first.Id, HazardOutcome.Aborted, "no rain drop could be placed");

            var starts = rig.Port.StartsOf("p_rain").ToList();
            Assert.AreEqual(2, starts.Count, "the aborted step is restarted");
            Assert.AreEqual(first.Request.Attempt + 1, starts[1].Request.Attempt, "with the next attempt");
            Assert.AreEqual(SpeedCourseRules.HazardSeed(rig.Run.Course.Seed, first.Request.SlotIndex, first.Request.Attempt + 1), starts[1].Request.Seed, "and that attempt's seed");
            Assert.AreNotEqual(first.Request.Seed, starts[1].Request.Seed);

            Assert.AreEqual((true, 750u), rig.Guard, "the phase did NOT advance: still immune at the 75 floor");
            Assert.IsTrue(rig.Host.BossPhases.InPhase);
            Assert.AreEqual(1, rig.Host.BossPhases.PhaseIndex);

            Assert.AreEqual(1, rig.World.Errors.Count, string.Join(" | ", rig.World.Errors));
            StringAssert.Contains(rig.World.Errors[0], "no rain drop could be placed", "the error quotes the run's end reason");
            StringAssert.Contains(rig.World.Errors[0], "Aborted");
        }

        [TestMethod]
        public void Host_boss_step_second_abort_advances_the_phase()
        {
            var rig = RigInRainPhase();

            EndRun(rig, rig.Port.StartsOf("p_rain").Single().Id, HazardOutcome.Failed, "first");
            var restart = rig.Port.StartsOf("p_rain").Last();
            Assert.AreEqual((true, 750u), rig.Guard, "a Failed first end restarts too");

            EndRun(rig, restart.Id, HazardOutcome.Aborted, "second");

            Assert.AreEqual(2, rig.Port.StartsOf("p_rain").Count(), "no third start");
            Assert.AreEqual((false, 500u), rig.Guard, "the second broken end advances: vulnerable down to the 50 floor");
            Assert.AreEqual(2, rig.Host.BossPhases.PhaseIndex);
            Assert.AreEqual(2, rig.World.Errors.Count, string.Join(" | ", rig.World.Errors));
            StringAssert.Contains(rig.World.Errors[1], "second");
        }

        [TestMethod]
        public void Host_boss_step_refused_restart_advances_with_an_error()
        {
            var rig = RigInRainPhase();
            rig.Port.RefuseWith = "landblock not ready";

            EndRun(rig, rig.Port.StartsOf("p_rain").Single().Id, HazardOutcome.Aborted, "no rain drop could be placed");

            Assert.AreEqual(1, rig.Port.StartsOf("p_rain").Count(), "the restart was refused");
            Assert.AreEqual((false, 500u), rig.Guard, "a refused restart advances as before");
            Assert.AreEqual(2, rig.World.Errors.Count, string.Join(" | ", rig.World.Errors));
            StringAssert.Contains(rig.World.Errors[1], "restart was refused");
        }

        [TestMethod]
        public void Host_boss_step_completed_end_is_unchanged()
        {
            var rig = RigInRainPhase();

            EndRun(rig, rig.Port.StartsOf("p_rain").Single().Id, HazardOutcome.Completed, "done");

            Assert.AreEqual(1, rig.Port.StartsOf("p_rain").Count(), "no restart after a Completed end");
            Assert.AreEqual((false, 500u), rig.Guard, "Completed advances at once");
            Assert.AreEqual(0, rig.World.Errors.Count, string.Join(" | ", rig.World.Errors));
        }

        [TestMethod]
        public void Host_phase_timeout_stops_the_step_and_its_late_end_is_not_counted_twice()
        {
            var rig = NewHostRig();
            SpawnBoss(rig);
            rig.Runner(210, -210, 0);
            rig.Tick();
            var id = rig.StartOf("p_entry");

            for (var i = 0; i < 100 && !rig.Port.Stops.Any(); i++)
                rig.Tick();

            Assert.AreEqual(id, rig.Port.Stops.Single().Id, "the failsafe stops the running fan");
            Assert.AreEqual((false, 750u), rig.Guard);

            rig.Host.OnRunEnded(id, HazardOutcome.Stopped);
            rig.World.Drain();
            Assert.AreEqual(1, rig.Host.BossPhases.PhaseIndex, "the stopped run's end does not advance another phase");
        }

        [TestMethod]
        public void Host_teardown_mid_phase_releases_the_boss_guard()
        {
            var rig = NewHostRig();
            SpawnBoss(rig);
            rig.Runner(210, -210, 0);
            rig.Tick();
            rig.StartOf("p_entry");
            Assert.AreEqual((true, 1000u), rig.Guard, "mid-phase: immune at full health");

            rig.Run.End("runner offline");
            rig.World.Drain();

            Assert.AreEqual((false, 0u), rig.Guard, "an ended run leaves no immune, floored boss behind");
        }

        [TestMethod]
        public void Host_outside_stop_completes_the_step()
        {
            var rig = NewHostRig();
            SpawnBoss(rig);
            rig.Runner(210, -210, 0);
            rig.Tick();

            rig.End("p_entry", HazardOutcome.Stopped); // /hazard stop or the kill switch, not this host
            Assert.AreEqual((false, 750u), rig.Guard);
        }

        [TestMethod]
        public void Host_with_hazards_switched_off_lets_the_boss_through_every_phase()
        {
            var rig = NewHostRig(enabled: false);
            SpawnBoss(rig);
            rig.Runner(210, -210, 0);
            rig.Tick();
            Assert.AreEqual((false, 750u), rig.Guard, "the entry phase passes at once");

            rig.World.Damage(rig.Run.Boss, 0.0);
            for (var i = 0; i < 4; i++)
                rig.Tick();

            Assert.AreEqual((false, 0u), rig.Guard);
            Assert.IsTrue(rig.Host.BossPhases.Killable);
            Assert.AreEqual(0, rig.Port.Starts.Count);
            Assert.IsTrue(rig.World.Info.Any(l => l.Contains("hazard_mech_enabled is false")), "the master switch is logged");
        }

        [TestMethod]
        public void A_course_without_phases_spawns_an_unguarded_boss()
        {
            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();
            var run = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, BookWcid, 120, Owner, 12345, world, now: 0.0);
            var host = new SpeedCourseHazardHost(run, new FakeSpeedHazardPort());
            run.AttachHazardDriver(host);
            world.RunnerView = new SpeedRunnerView { Online = true, BoundHere = true, HasPosition = true, X = -50, Y = -50, Z = -6 };

            world.Seals["bb_seal"] = true;
            run.Tick(0.5);
            world.Drain();

            Assert.IsFalse(run.Boss.IsNone);
            var stamp = world.Spawns.Single(s => s.Handle.Equals(run.Boss)).Stamp;
            Assert.IsNull(stamp.GuardFloorFraction);
            Assert.AreEqual(1f, stamp.Scale);
            Assert.IsNull(host.BossPhases);
            Assert.IsFalse(world.Guards.ContainsKey(run.Boss.Guid));
        }
    }
}

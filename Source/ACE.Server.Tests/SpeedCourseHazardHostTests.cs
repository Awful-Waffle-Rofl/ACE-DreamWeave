using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;
using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>A recording hazard port: no HazardManager, no landblock, no PropertyManager.</summary>
    internal sealed class FakeSpeedHazardPort : ISpeedHazardPort
    {
        public ushort LandblockId { get; set; } = 0x0089;

        public bool Enabled { get; set; } = true;

        /// <summary>Non-null: every start is refused with this error.</summary>
        public string RefuseWith { get; set; }

        public readonly List<(SpeedHazardStart Request, IHazardHost Host, int Id)> Starts = new List<(SpeedHazardStart, IHazardHost, int)>();
        public readonly List<(int Id, string Reason)> Stops = new List<(int, string)>();
        public readonly List<int> Forgotten = new List<int>();
        public int StartAttempts;

        private int nextId = 100;

        public int Start(SpeedHazardStart request, IHazardHost host, out string error)
        {
            StartAttempts++;

            if (RefuseWith != null)
            {
                error = RefuseWith;
                return 0;
            }

            error = null;
            var id = ++nextId;
            Starts.Add((request, host, id));
            return id;
        }

        public void Stop(int runId, string reason) => Stops.Add((runId, reason));

        public void Forget(int runId) => Forgotten.Add(runId);

        public IEnumerable<(SpeedHazardStart Request, IHazardHost Host, int Id)> StartsOf(string hazardId)
            => Starts.Where(s => s.Request.Hazard.Id == hazardId);
    }

    /// <summary>
    /// The speed-course hazard host against a fake world and a fake hazard port: start/stop sequencing per trigger kind,
    /// the memory hazard's Completed contribution and Failed retry, the boss-centred fan origin, teardown, and the
    /// hazard_mech_enabled master switch.
    /// </summary>
    [TestClass]
    public class SpeedCourseHazardHostTests
    {
        private const uint BookWcid = 1007500;
        private const uint Owner = 0x50000042;
        private const int CourseSeed = 3003;

        // Hazard slot indices in SpeedCourseFixtures.SampleJson.
        private const int CloisterSlot = 0;
        private const int EastSlot = 1;
        private const int SouthSlot = 2;
        private const int FansSlot = 3;

        private sealed class Rig
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

            public void Runner(float x, float y, float z = -6f, bool rebased = false, bool bound = true)
                => World.RunnerView = new SpeedRunnerView
                {
                    Online = true,
                    BoundHere = bound,
                    ClockRebased = rebased,
                    HasPosition = true,
                    Cell = 0x00890150,
                    X = x,
                    Y = y,
                    Z = z,
                };

            public void End(int id, HazardOutcome outcome)
            {
                Host.OnRunEnded(id, outcome);
                World.Drain();
            }

            public void SpawnBoss()
            {
                World.Seals["bb_seal"] = true;
                Tick();
                Assert.IsFalse(Run.Boss.IsNone, "the Seal latch spawns the boss");
            }
        }

        private static Rig NewRig(int playerLevel = 120)
        {
            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();

            var run = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, BookWcid, playerLevel, Owner, 12345, world, now: 0.0);
            var port = new FakeSpeedHazardPort();
            var host = new SpeedCourseHazardHost(run, port);
            run.AttachHazardDriver(host);

            var rig = new Rig { Run = run, World = world, Port = port, Host = host };

            // Somewhere in no hazard region, clock not rebased: nothing starts.
            rig.Runner(-50, -50);
            return rig;
        }

        private static double Pct(HazardArgs args) => double.Parse(args.GetString("pct"), CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------------------------------- clock_rebase

        [TestMethod]
        public void ClockRebase_StartsTheCloisterZonesOnce_AndNeverStopsThem()
        {
            var rig = NewRig();

            rig.Tick();
            rig.Tick();
            Assert.AreEqual(0, rig.Port.Starts.Count, "nothing starts before the clock is rebased");

            rig.Runner(-50, -50, rebased: true);

            for (var i = 0; i < 10; i++)
                rig.Tick();

            var zones = rig.Port.StartsOf("cloister_zones").ToList();
            Assert.AreEqual(1, zones.Count, "the Cloister zones start once");
            Assert.AreEqual(0, rig.Port.Stops.Count, "zones never stop while the run lives");

            var request = zones[0].Request;
            Assert.AreEqual("zones", request.Kind);
            Assert.AreEqual("corners", request.Pattern);
            Assert.AreSame(rig.Host, zones[0].Host, "the host is the IHazardHost of every run");
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, CloisterSlot, 0), request.Seed);
            Assert.AreEqual(0, request.Attempt);

            // The authored args are the settings layer; the caller args carry the level-scaled pct over them.
            Assert.AreEqual("0.2", request.SettingsLayer.GetString("pct"));
            var expected = SpeedCourseRules.ScaledPct(rig.Run.Course.Hazards[CloisterSlot], rig.Run.RunLevel);
            Assert.AreEqual(expected, Pct(request.CallerArgs), 1e-4);
            Assert.IsFalse(request.CallerArgs.Has("cx"), "the Cloister zones are region-centred");

            Assert.AreEqual(0x0089, request.Region.Landblock);
            Assert.AreEqual(rig.Run.Instance, request.Region.Instance);
            Assert.IsTrue(rig.Host.PlayerFacing);
        }

        // ---------------------------------------------------------------------------------------- while_in_region

        [TestMethod]
        public void WhileInRegion_StartsOnEntry_StopsOnExit_AndRestartsWithTheNextSeed()
        {
            var rig = NewRig();

            rig.Runner(40, 10);
            rig.Tick();

            var first = rig.Port.StartsOf("east_balls").Single();
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, EastSlot, 0), first.Request.Seed);

            rig.Tick();
            Assert.AreEqual(1, rig.Port.StartsOf("east_balls").Count(), "no second start while it runs");

            rig.Runner(-50, -50);
            rig.Tick();
            Assert.AreEqual(first.Id, rig.Port.Stops.Single().Id, "leaving the region stops it");

            rig.Tick();
            Assert.AreEqual(1, rig.Port.Stops.Count, "a stopped run is not stopped again");

            // The framework reports the stop later (any thread); the host handles it on the landblock.
            rig.End(first.Id, HazardOutcome.Stopped);
            CollectionAssert.Contains(rig.Port.Forgotten, first.Id);

            rig.Runner(40, 10);
            rig.Tick();

            var starts = rig.Port.StartsOf("east_balls").ToList();
            Assert.AreEqual(2, starts.Count, "re-entering restarts it");
            Assert.AreEqual(1, starts[1].Request.Attempt);
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, EastSlot, 1), starts[1].Request.Seed);
            Assert.AreNotEqual(starts[0].Request.Seed, starts[1].Request.Seed);
        }

        // ---------------------------------------------------------------------------------------- memory

        private static (Rig Rig, int Id) StartSouthMemory()
        {
            var rig = NewRig();

            rig.Tick(); // populate
            rig.Runner(5, -20);
            rig.Tick();
            Assert.AreEqual(0, rig.Port.StartsOf("south_memory").Count(), "not before K2 dies");

            rig.World.Dead.Add(rig.Run.K2.Guid);
            rig.Tick();

            var start = rig.Port.StartsOf("south_memory").Single();
            Assert.IsFalse(start.Request.CallerArgs.Has("pct"), "memory deals no damage: no pct is passed");
            return (rig, start.Id);
        }

        [TestMethod]
        public void Memory_Completed_ContributesSouthRiteOnce_AndDoesNotRestart()
        {
            var (rig, id) = StartSouthMemory();

            rig.Host.OnRunEnded(id, HazardOutcome.Completed);
            Assert.AreEqual(0, rig.World.Contributions.Count, "OnEnded is posted, never handled on the ending thread");

            rig.World.Drain();

            Assert.AreEqual(1, rig.World.Contributions.Count);
            Assert.AreEqual(("bb_seal", "south_rite", 1.0), rig.World.Contributions[0]);

            // A duplicate end report and further ticks in the region change nothing.
            rig.End(id, HazardOutcome.Completed);

            for (var i = 0; i < 20; i++)
                rig.Tick();

            Assert.AreEqual(1, rig.World.Contributions.Count, "south_rite is contributed once");
            Assert.AreEqual(1, rig.Port.StartsOf("south_memory").Count(), "a Completed memory is final");
        }

        [TestMethod]
        public void Memory_Failed_RetriesAfterRetrySeconds_WithANewSeed()
        {
            var (rig, id) = StartSouthMemory();
            var firstSeed = rig.Port.StartsOf("south_memory").Single().Request.Seed;

            rig.End(id, HazardOutcome.Failed);
            var failedAt = rig.Now;

            Assert.AreEqual(0, rig.World.Contributions.Count, "a Failed memory contributes nothing");

            while (rig.Now - failedAt < 4.4)
            {
                rig.Tick();
                Assert.AreEqual(1, rig.Port.StartsOf("south_memory").Count(), $"no restart {rig.Now - failedAt:0.0}s after the fail");
            }

            rig.Tick(0.5);

            var starts = rig.Port.StartsOf("south_memory").ToList();
            Assert.AreEqual(2, starts.Count, "restarts once retrySeconds have passed");
            Assert.AreEqual(1, starts[1].Request.Attempt);
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, SouthSlot, 1), starts[1].Request.Seed);
            Assert.AreNotEqual(firstSeed, starts[1].Request.Seed, "the retry plays a new sequence");

            // The retry can still complete the rite.
            rig.End(starts[1].Id, HazardOutcome.Completed);
            Assert.AreEqual(1, rig.World.Contributions.Count(c => c.Token == "south_rite"));
        }

        [TestMethod]
        public void Memory_CompletedJustAsTheHostStoppedIt_StillContributes()
        {
            var (rig, id) = StartSouthMemory();

            rig.Runner(-50, -50);

            for (var i = 0; i < 20 && rig.Port.Stops.Count == 0; i++)
                rig.Tick();

            Assert.AreEqual(id, rig.Port.Stops.Single().Id);

            // The run had already completed; the framework reports Completed, not Stopped.
            rig.End(id, HazardOutcome.Completed);
            Assert.AreEqual(1, rig.World.Contributions.Count(c => c.Token == "south_rite"));

            rig.Runner(5, -20);

            for (var i = 0; i < 20; i++)
                rig.Tick();

            Assert.AreEqual(1, rig.Port.StartsOf("south_memory").Count(), "the completed memory does not restart");
        }

        // ---------------------------------------------------------------------------------------- boss triggers

        [TestMethod]
        public void BossFans_StartAtTheBossPosition_EveryInterval_AndStopWhenTheBossDies()
        {
            var rig = NewRig();
            rig.Tick(); // populate

            // The first wave starts on the tick that spawns the boss. The fake has no position for it yet, so that wave
            // falls back to the region centre (no cx/cy).
            rig.World.Seals["bb_seal"] = true;
            rig.Tick();
            var boss = rig.Run.Boss;
            Assert.IsFalse(boss.IsNone);

            var first = rig.Port.StartsOf("boss_fans").Single();
            var firstAt = rig.Now;
            Assert.IsFalse(first.Request.CallerArgs.Has("cx"), "no boss position: the region centre");
            Assert.IsFalse(first.Request.CallerArgs.Has("pct"), "boss_fans authors no pct");
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, FansSlot, 0), first.Request.Seed);

            // The wave ends on its own; the next waits for the interval since the last START.
            rig.End(first.Id, HazardOutcome.Completed);
            rig.World.Positions[boss.Guid] = (52.5f, 11.25f);

            while (rig.Now - firstAt < 19.4)
            {
                rig.Tick();
                Assert.AreEqual(1, rig.Port.StartsOf("boss_fans").Count(), $"no fan {rig.Now - firstAt:0.0}s after the last start");
            }

            rig.Tick();
            var second = rig.Port.StartsOf("boss_fans").ToList();
            Assert.AreEqual(2, second.Count, "the next wave starts once the interval has passed");
            var secondAt = rig.Now;

            Assert.AreEqual("52.5", second[1].Request.CallerArgs.GetString("cx"), "the fan originates at the boss");
            Assert.AreEqual("11.25", second[1].Request.CallerArgs.GetString("cy"));
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, FansSlot, 1), second[1].Request.Seed);

            // The boss moved: the next start reads where it is now.
            rig.End(second[1].Id, HazardOutcome.Completed);
            rig.World.Positions[boss.Guid] = (45f, 5f);

            while (rig.Now - secondAt < 20.0)
                rig.Tick();

            var third = rig.Port.StartsOf("boss_fans").Last();
            Assert.AreEqual(3, rig.Port.StartsOf("boss_fans").Count());
            Assert.AreEqual("45", third.Request.CallerArgs.GetString("cx"), "each start reads the boss's current position");
            Assert.AreEqual("5", third.Request.CallerArgs.GetString("cy"));

            rig.World.Dead.Add(boss.Guid);
            rig.Tick();

            Assert.AreEqual(1, rig.Port.Stops.Count(s => s.Id == third.Id), "the fans stop when the boss dies");
        }

        [TestMethod]
        public void HubZones_StartAtOneThirdBossHealth_AndStopOnlyWhenTheBossDies()
        {
            var rig = NewRig();
            rig.Tick();
            rig.SpawnBoss();
            var boss = rig.Run.Boss;

            rig.World.Health[boss.Guid] = 0.5;
            rig.Tick();
            Assert.AreEqual(0, rig.Port.StartsOf("hub_zones").Count(), "not above one third");

            rig.World.Health[boss.Guid] = 0.33;
            rig.Tick();
            var zones = rig.Port.StartsOf("hub_zones").Single();

            rig.World.Health[boss.Guid] = 0.9;
            rig.Tick();
            Assert.IsFalse(rig.Port.Stops.Any(s => s.Id == zones.Id), "healing does not stop the hub zones");

            rig.World.Dead.Add(boss.Guid);
            rig.Tick();
            Assert.AreEqual(1, rig.Port.Stops.Count(s => s.Id == zones.Id), "the hub zones stop when the boss dies");
        }

        // ---------------------------------------------------------------------------------------- teardown

        [TestMethod]
        public void Teardown_StopsEveryLiveRunExactlyOnce()
        {
            var rig = NewRig();
            rig.Runner(40, 10, rebased: true);
            rig.Tick();

            var zones = rig.Port.StartsOf("cloister_zones").Single();
            var balls = rig.Port.StartsOf("east_balls").Single();

            rig.Run.End("test teardown");
            rig.Run.End("again");
            rig.Host.Stop(rig.Run, "and directly");

            Assert.AreEqual(2, rig.Port.Stops.Count);
            Assert.AreEqual(1, rig.Port.Stops.Count(s => s.Id == zones.Id));
            Assert.AreEqual(1, rig.Port.Stops.Count(s => s.Id == balls.Id));
            Assert.IsTrue(rig.Host.IsStopped);

            // Late end reports are harmless and nothing starts again.
            rig.End(zones.Id, HazardOutcome.Stopped);
            rig.End(balls.Id, HazardOutcome.Completed);
            rig.Host.Tick(rig.Run, rig.Run.Snapshot(rig.Now + 1, rig.World.RunnerView));

            Assert.AreEqual(2, rig.Port.Starts.Count);
            Assert.AreEqual(0, rig.World.Contributions.Count);
        }

        [TestMethod]
        public void RunnerForfeitOrDeath_EndsTheRun_AndStopsItsHazards()
        {
            var rig = NewRig();
            rig.Runner(40, 10, rebased: true);
            rig.Tick();
            Assert.AreEqual(2, rig.Port.Starts.Count);

            // A death or a forfeit clears the runner's speed run: they are no longer bound to the instance.
            rig.Runner(40, 10, rebased: true, bound: false);
            rig.Tick();

            Assert.IsTrue(rig.Run.IsEnded);
            Assert.AreEqual("run finished or forfeited", rig.Run.EndReason);
            Assert.AreEqual(2, rig.Port.Stops.Count, "every live hazard run is stopped");
            Assert.IsTrue(rig.Host.IsStopped);
        }

        // ---------------------------------------------------------------------------------------- master switch and refusals

        [TestMethod]
        public void MasterSwitchOff_NoStartIsAttempted_AndItIsLoggedOncePerRun()
        {
            var rig = NewRig();
            rig.Port.Enabled = false;
            rig.Runner(40, 10, rebased: true);

            for (var i = 0; i < 20; i++)
                rig.Tick();

            Assert.AreEqual(0, rig.Port.StartAttempts, "starts are refused before the framework is asked");
            Assert.AreEqual(1, rig.World.Info.Count(l => l.Contains("hazard_mech_enabled")), "logged once, not every tick");
            Assert.AreEqual(0, rig.World.Warnings.Count(l => l.Contains("hazard")));
            Assert.IsFalse(rig.Run.IsEnded, "the run continues without hazards");

            // Switched back on mid-run: the hazards start, and the off notice is not repeated.
            rig.Port.Enabled = true;
            rig.Tick();
            Assert.AreEqual(2, rig.Port.Starts.Count);
            Assert.AreEqual(1, rig.World.Info.Count(l => l.Contains("hazard_mech_enabled")));
        }

        [TestMethod]
        public void RefusedStart_BacksOff_AndIsLoggedOnce()
        {
            var rig = NewRig();
            rig.Port.RefuseWith = "landblock not loaded";
            rig.Runner(-50, -50, rebased: true);

            rig.Tick();
            Assert.AreEqual(1, rig.Port.StartAttempts);

            for (var i = 0; i < 8; i++)
                rig.Tick();

            Assert.AreEqual(1, rig.Port.StartAttempts, "no retry inside the backoff");

            rig.Tick(SpeedCourseHazardHost.RefusalBackoffSeconds);
            Assert.AreEqual(2, rig.Port.StartAttempts, "retried after the backoff");
            Assert.AreEqual(1, rig.World.Warnings.Count(l => l.Contains("cloister_zones") && l.Contains("refused")));

            rig.Port.RefuseWith = null;
            rig.Tick(SpeedCourseHazardHost.RefusalBackoffSeconds);
            Assert.AreEqual(1, rig.Port.StartsOf("cloister_zones").Count());
            Assert.AreEqual(SpeedCourseRules.HazardSeed(CourseSeed, CloisterSlot, 0), rig.Port.Starts[0].Request.Seed,
                "a refused start does not use up an attempt");
        }

        // ---------------------------------------------------------------------------------------- trigger area and grace

        [TestMethod]
        public void TriggerRegion_StartsWhenTheRunnerIsAnywhereInTheArm_AndStopsOnLeavingIt()
        {
            var rig = NewRig();

            // Outside the balls' own circle (40,10 r8) but inside the trigger rect (30..70, 0..30).
            rig.Runner(65, 25);
            rig.Tick();
            var balls = rig.Port.StartsOf("east_balls").Single();

            rig.Runner(55, 5);
            rig.Tick();
            Assert.AreEqual(0, rig.Port.Stops.Count, "moving within the arm keeps it running");

            rig.Runner(75, 25);
            rig.Tick();
            Assert.AreEqual(balls.Id, rig.Port.Stops.Single().Id, "leaving the arm stops it at once (no grace for while_in_region)");
        }

        [TestMethod]
        public void Memory_StepOffThePads_IsGraced_ThreeSecondsOutsideStopsIt()
        {
            var (rig, id) = StartSouthMemory();

            // Out for 2.5 s, back in: nothing stops, and the grace clock resets.
            rig.Runner(-50, -50);

            for (var i = 0; i < 5; i++)
                rig.Tick();

            Assert.AreEqual(0, rig.Port.Stops.Count, "2.5 s outside is inside the grace");

            rig.Runner(5, -20);
            rig.Tick();

            // The first tick outside starts the clock; 2.5 s later it still runs, at 3.0 s it stops.
            rig.Runner(-50, -50);

            for (var i = 0; i < 6; i++)
                rig.Tick();

            Assert.AreEqual(0, rig.Port.Stops.Count, "the grace clock restarted when the runner came back");

            rig.Tick();
            Assert.AreEqual(id, rig.Port.Stops.Single().Id, "3 s outside without a break stops it");
        }

        // ---------------------------------------------------------------------------------------- grants

        [TestMethod]
        public void MasterSwitchOff_GrantsTheMemoryToken_WhenItWouldHaveStarted()
        {
            var rig = NewRig();
            rig.Port.Enabled = false;
            rig.Tick(); // populate

            rig.Runner(5, -20);
            rig.Tick();
            Assert.AreEqual(0, rig.World.Contributions.Count, "nothing before K2 dies: the hazard would not have started yet");

            rig.World.Dead.Add(rig.Run.K2.Guid);

            for (var i = 0; i < 5; i++)
                rig.Tick();

            Assert.AreEqual(1, rig.World.Contributions.Count(c => c.Token == "south_rite"), "granted once");
            Assert.IsTrue(rig.Run.WasGranted("south_rite"));
            Assert.AreEqual(0, rig.Port.StartAttempts);
            Assert.AreEqual(1, rig.World.Tells.Count(t => t.StartsWith("South rite granted")));
        }

        [TestMethod]
        public void RefusedFor30Seconds_GrantsTheMemoryToken_AndATokenlessHazardGrantsNothing()
        {
            var rig = NewRig();
            rig.Port.RefuseWith = "region does not fit";
            rig.Tick(); // populate

            rig.World.Dead.Add(rig.Run.K2.Guid);
            rig.Runner(5, -20, rebased: true);

            var firstAt = rig.Now + 0.5;

            while (rig.Now - firstAt < SpeedCourseHazardHost.RefusalGrantSeconds - 5)
            {
                rig.Tick();
                Assert.AreEqual(0, rig.World.Contributions.Count, $"no grant {rig.Now - firstAt:0.0}s into the refusals");
            }

            for (var i = 0; i < 20; i++)
                rig.Tick();

            Assert.AreEqual(1, rig.World.Contributions.Count(c => c.Token == "south_rite"));
            Assert.AreEqual(1, rig.World.Contributions.Count, "the Cloister zones (refused too) carry no token");
        }

        [TestMethod]
        public void RefusalStreak_ResetsWhenTheStartIsNoLongerWanted_SoAReturnLaterIsNotAGrant()
        {
            var rig = NewRig();
            rig.Port.RefuseWith = "region does not fit";
            rig.Tick(); // populate

            rig.World.Dead.Add(rig.Run.K2.Guid);
            rig.Runner(5, -20);
            rig.Tick();
            Assert.AreEqual(0, rig.World.Contributions.Count, "refused once, nothing granted");

            // Leave: the memory no longer wants to start, which ends the streak.
            rig.Runner(-50, -50);
            rig.Tick();

            // Back 40 s later (past the 30 s grant window measured from the FIRST refusal): one refusal is not a grant.
            rig.Tick(40);
            rig.Runner(5, -20);
            var attemptsBefore = rig.Port.StartAttempts;
            rig.Tick();

            Assert.AreEqual(attemptsBefore + 1, rig.Port.StartAttempts, "the return really asked for a start, and it was refused");
            Assert.AreEqual(0, rig.World.Contributions.Count(c => c.Token == "south_rite"), "a broken streak restarts the 30 s count");

            // Positive control: an unbroken 30 s from here does grant.
            for (var i = 0; i < 70; i++)
                rig.Tick();

            Assert.AreEqual(1, rig.World.Contributions.Count(c => c.Token == "south_rite"));
        }

        // ---------------------------------------------------------------------------------------- factory

        [TestMethod]
        public void DefaultFactory_IsNoopOffTheLiveWorld_AndForACourseWithoutHazards()
        {
            var world = new SpeedCourseFakeWorld();
            var withHazards = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, BookWcid, 120, Owner, 1, world, now: 0.0);
            Assert.AreSame(NoopSpeedHazardDriver.Instance, SpeedCourseManager.DefaultHazardDriver(withHazards), "a fake world gets no live hazards");

            var course = SpeedCourseFixtures.Course();
            course.Hazards.Clear();
            var without = new SpeedCourseRun(course, 3, 60, BookWcid, 120, Owner, 1, world, now: 0.0);
            Assert.AreSame(NoopSpeedHazardDriver.Instance, SpeedCourseManager.DefaultHazardDriver(without));

            Assert.AreSame(NoopSpeedHazardDriver.Instance, SpeedCourseManager.DefaultHazardDriver(null));
        }
    }
}

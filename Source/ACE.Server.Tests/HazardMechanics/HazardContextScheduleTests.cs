using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HazardContext's schedule and teardown, driven by hand on a registry-only run (no landblock, no world): the
    /// driver's job is reproduced with SetNow + RunDue.
    /// </summary>
    [TestClass]
    public class HazardContextScheduleTests
    {
        private sealed class RecordingMechanic : IHazardMechanic
        {
            public int Stops;
            public HazardOutcome? StopOutcome;

            public string Kind => "test";
            public IReadOnlyList<string> Patterns => Array.Empty<string>();
            public IEnumerable<uint> RequiredWcids(HazardArgs args) => Array.Empty<uint>();
            public string Validate(HazardRegion region, HazardArgs args, string pattern) => null;
            public void Start(HazardContext ctx) { }
            public void Tick(HazardContext ctx) { }
            public void OnStop(HazardContext ctx, HazardOutcome outcome) { Stops++; StopOutcome = outcome; }
            public string Describe(HazardContext ctx) => "";
            public string Reveal(HazardContext ctx) => null;
        }

        private HazardRun run;
        private RecordingMechanic mechanic;

        [TestInitialize]
        public void Init()
        {
            mechanic = new RecordingMechanic();
            run = HazardManager.RegisterForTest("test", mechanic,
                HazardRegion.Rect("t", 0x0069, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1), null, DateTime.UtcNow);
        }

        [TestCleanup]
        public void Cleanup() => HazardManager.Stop(run, HazardOutcome.Stopped, "test cleanup");

        [TestMethod]
        public void DueSteps_RunByDueTime_ThenInScheduleOrder()
        {
            var ctx = run.Context;
            var order = new List<string>();

            ctx.SetNow(0.0);
            ctx.Schedule(2.0, () => order.Add("late"));
            ctx.Schedule(1.0, () => order.Add("first"));
            ctx.Schedule(1.0, () => order.Add("second"));
            ctx.Schedule(0.5, () => order.Add("earliest"));

            ctx.SetNow(0.4);
            ctx.RunDue();
            CollectionAssert.AreEqual(Array.Empty<string>(), order, "nothing due yet");

            ctx.SetNow(1.0);
            ctx.RunDue();
            CollectionAssert.AreEqual(new[] { "earliest", "first", "second" }, order);

            ctx.SetNow(5.0);
            ctx.RunDue();
            CollectionAssert.AreEqual(new[] { "earliest", "first", "second", "late" }, order);
            Assert.AreEqual(0, ctx.ScheduledCountForTest);
        }

        [TestMethod]
        public void AZeroDelayStep_ScheduledByARunningStep_RunsOnTheNextTick()
        {
            var ctx = run.Context;
            var order = new List<string>();

            ctx.SetNow(0.0);
            ctx.Schedule(0.0, () =>
            {
                order.Add("outer");
                ctx.Schedule(0.0, () => order.Add("inner"));
            });

            ctx.RunDue();
            CollectionAssert.AreEqual(new[] { "outer" }, order, "the inner step must not run inside the same pass");

            ctx.RunDue(); // next tick, same Now
            CollectionAssert.AreEqual(new[] { "outer", "inner" }, order);
        }

        /// <summary>
        /// Review F2, option (b): a ZERO lead (Grael's fan, fair_lead=0 lead=0) scheduled the way RollingBallMechanic does
        /// it (the spawn step schedules the launch at ctx.Schedule(lead)) launches on the tick AFTER the spawn tick, never
        /// in it: the scheduler does not run a step queued by a running step in the same pass, and MoveLegs on a group
        /// spawned this tick defers. Nothing is hittable on the spawn tick. Driven by hand: BeginTick + SetNow + RunDue.
        /// </summary>
        [TestMethod]
        public void ZeroLead_TheSpawnTickLaunchesNothing_TheNextTickLaunches()
        {
            var ctx = run.Context;
            var fan = BallRules.Read(HazardArgs.Merge(ACE.Server.Pinnacles.Grael.GraelSettings.Defaults.P4FanArgs(), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Fan);
            var lead = BallRules.LeadFor(HazardRegionPresets.Pinnacle(0), BallPatterns.Fan, BallRules.ForWave(fan, 1), 20.0);

            Assert.AreEqual(0.0, lead, "Grael's fan has no lead");

            var clock = new BallWaveClock(1);
            var group = new List<(HazardHandle Handle, Vector3 End)>();
            var tick = 0;
            int? launchTick = null;
            var moveResult = -1;

            clock.StartWave(2);

            // tick 1: the spawn step (as SpawnGroup) spawns the balls and schedules their launch `lead` seconds later
            ctx.BeginTick();
            tick++;
            ctx.SetNow(10.0);
            ctx.Schedule(0.0, () =>
            {
                foreach (var id in new[] { 1, 2 })
                {
                    var h = new HazardHandle(id, null, new HazardSpawnSpec { Wcid = 1, Tag = "ball", Moving = true }, Vector3.Zero) { SpawnTick = tick };
                    ctx.AddOwnedForTest(h);
                    clock.OnSpawned(id);
                    group.Add((h, new Vector3(5f, 0f, 0f)));
                }

                ctx.Schedule(lead, () =>
                {
                    // the launch step (as Launch -> RollGroup): move the group, then mark the balls launched
                    launchTick = tick;
                    moveResult = ctx.MoveLegs(group, 5.0);

                    foreach (var (h, _) in group)
                        clock.OnLaunched(h.Id);
                });
            });

            ctx.RunDue();

            Assert.AreEqual(2, ctx.Handles.Count, "spawned on tick 1");
            Assert.IsNull(launchTick, "a zero-lead launch must not run inside the spawn pass");
            Assert.AreEqual(0, clock.Hittable().Count, "nothing is hittable on the spawn tick");
            Assert.AreEqual(BallState.Spawned, clock.StateOf(1));
            Assert.AreEqual(1, ctx.ScheduledCountForTest, "the launch is queued for the next tick");

            // tick 2, same clock time (a zero lead adds none): the launch runs, and MoveLegs is NOT deferred again
            ctx.BeginTick();
            tick++;
            ctx.RunDue();

            Assert.AreEqual(2, launchTick, "launched on the tick after the spawn tick");
            Assert.AreEqual(0, ctx.ScheduledCountForTest, "MoveLegs did not defer: the group was not spawned this tick");
            Assert.AreEqual(0, moveResult, "control: these test handles carry no world object, so no leg is really sent");
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, clock.Hittable().ToArray(), "hittable once launched");
        }

        [TestMethod]
        public void MoveLegs_OnTheSpawnTick_DefersTheWholeGroupToTheNextTick()
        {
            // control for the test above: a launch that DID run in the spawn tick is held back by MoveLegs itself
            var ctx = run.Context;

            ctx.BeginTick();
            ctx.BeginTick();
            ctx.SetNow(0.0);

            var spawnedNow = new HazardHandle(1, null, new HazardSpawnSpec { Moving = true }, Vector3.Zero);
            var older = new HazardHandle(2, null, new HazardSpawnSpec { Moving = true }, Vector3.Zero) { SpawnTick = 1 };

            ctx.AddOwnedForTest(spawnedNow);
            ctx.AddOwnedForTest(older);

            // which tick is "now" is not visible to a test, so find it: only the matching SpawnTick defers
            var deferred = false;

            for (var t = 0; t <= 3 && !deferred; t++)
            {
                spawnedNow.SpawnTick = t;
                var before = ctx.ScheduledCountForTest;
                var n = ctx.MoveLegs(new List<(HazardHandle, Vector3)> { (spawnedNow, Vector3.One), (older, Vector3.One) }, 5.0);

                if (ctx.ScheduledCountForTest == before + 1)
                {
                    deferred = true;
                    Assert.AreEqual(2, t, "two BeginTicks: this is tick 2");
                    Assert.AreEqual(2, n, "a deferred group reports every member as started");
                    Assert.IsNull(spawnedNow.Path, "no leg yet");
                    Assert.IsNull(older.Path, "the whole group waits, so it still starts in unison");
                }
                else
                {
                    Assert.AreEqual(0, n, "not deferred: these handles have no world object, so nothing starts");
                }
            }

            Assert.IsTrue(deferred, "a group with a member spawned this tick is deferred");
        }

        [TestMethod]
        public void Steps_AreDropped_WhenTheRunEnds()
        {
            var ctx = run.Context;
            var ran = false;

            ctx.SetNow(0.0);
            ctx.Schedule(1.0, () => ran = true);

            run.Stop("ended");

            ctx.SetNow(10.0);
            ctx.RunDue();
            Assert.IsFalse(ran, "a step due after the end must not run");

            ctx.Schedule(0.0, () => ran = true);
            ctx.RunDue();
            Assert.IsFalse(ran, "Schedule after the end is refused");
        }

        [TestMethod]
        public void AStepThatEndsTheRun_StopsTheRestOfThePass()
        {
            var ctx = run.Context;
            var ran = new List<int>();

            ctx.SetNow(0.0);
            ctx.Schedule(0.0, () => { ran.Add(1); ctx.End(HazardOutcome.Completed); });
            ctx.Schedule(0.0, () => ran.Add(2));

            ctx.RunDue();
            CollectionAssert.AreEqual(new[] { 1 }, ran);
            Assert.AreEqual(HazardOutcome.Completed, run.Outcome);
        }

        [TestMethod]
        public void Teardown_CallsOnStopOnce_DestroysEveryOwnedHandle_AndClearsTheSchedule()
        {
            var ctx = run.Context;
            var destroyed = new List<int>();

            ctx.DestroyForTeardown = h => { destroyed.Add(h.Id); return true; };

            ctx.AddOwnedForTest(new HazardHandle(1, null, new HazardSpawnSpec { Wcid = 1, Tag = "a" }, Vector3.Zero));
            ctx.AddOwnedForTest(new HazardHandle(2, null, new HazardSpawnSpec { Wcid = 1, Tag = "b" }, Vector3.Zero));
            ctx.Schedule(3.0, () => { });

            ctx.Teardown(HazardOutcome.Failed, wasStarted: true);

            Assert.AreEqual(1, mechanic.Stops);
            Assert.AreEqual(HazardOutcome.Failed, mechanic.StopOutcome);
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, destroyed);
            Assert.AreEqual(0, ctx.Handles.Count);
            Assert.AreEqual(0, ctx.ScheduledCountForTest);
        }

        [TestMethod]
        public void Teardown_OfARunThatNeverStarted_SkipsOnStop()
        {
            var ctx = run.Context;
            ctx.DestroyForTeardown = h => true;

            ctx.Teardown(HazardOutcome.Stopped, wasStarted: false);

            Assert.AreEqual(0, mechanic.Stops);
        }

        [TestMethod]
        public void Teardown_SurvivesAThrowingDestroy()
        {
            var ctx = run.Context;
            var calls = 0;

            ctx.DestroyForTeardown = h => { calls++; throw new InvalidOperationException("boom"); };
            ctx.AddOwnedForTest(new HazardHandle(1, null, new HazardSpawnSpec(), Vector3.Zero));
            ctx.AddOwnedForTest(new HazardHandle(2, null, new HazardSpawnSpec(), Vector3.Zero));

            ctx.Teardown(HazardOutcome.Stopped, wasStarted: true);

            Assert.AreEqual(2, calls, "one throwing destroy must not skip the rest");
            Assert.AreEqual(0, ctx.Handles.Count);
            Assert.AreEqual(0, ctx.LastTeardownDestroyed, "a throwing destroy is not counted");
        }

        [TestMethod]
        public void Teardown_CountsOnlyObjectsItActuallyDestroyed()
        {
            var ctx = run.Context;

            // handle 2 stands for one already destroyed (a /reload-landblock got it first): the seam returns false
            ctx.DestroyForTeardown = h => h.Id != 2;
            ctx.AddOwnedForTest(new HazardHandle(1, null, new HazardSpawnSpec(), Vector3.Zero));
            ctx.AddOwnedForTest(new HazardHandle(2, null, new HazardSpawnSpec(), Vector3.Zero));
            ctx.AddOwnedForTest(new HazardHandle(3, null, new HazardSpawnSpec(), Vector3.Zero));

            ctx.Teardown(HazardOutcome.Stopped, wasStarted: true);

            Assert.AreEqual(2, ctx.LastTeardownDestroyed);
            Assert.AreEqual(0, ctx.Handles.Count);
        }

        [TestMethod]
        public void DestroyHandle_ANullHandleOrAHandleWithNoObject_DestroysNothing()
        {
            Assert.IsFalse(HazardContext.DestroyHandle(null));
            Assert.IsFalse(HazardContext.DestroyHandle(new HazardHandle(1, null, new HazardSpawnSpec(), Vector3.Zero)));
        }

        [TestMethod]
        public void StubLine_SaysNotBuiltYet_AndNamesTheLiveTools()
        {
            var line = StubHazardMechanic.StubLine(7, "wisps");

            StringAssert.Contains(line, "#7");
            StringAssert.Contains(line, "'wisps'");
            StringAssert.Contains(line, "NOT BUILT YET");
            StringAssert.Contains(line, "/hazard show");
            StringAssert.Contains(line, "/hazard move");
        }
    }
}

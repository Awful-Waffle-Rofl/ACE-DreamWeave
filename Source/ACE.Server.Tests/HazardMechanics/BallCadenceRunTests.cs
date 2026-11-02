using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The balls' cadence mode (wave_every) EXECUTED through the framework's own driver call, HazardManager.TickRun, on a
    /// registry-only run (no landblock: every spawn is refused, every broadcast is silent). Pure of PropertyManager: the
    /// args are the shipped HazardDefaults.Balls row with the test's keys laid over it, as HazardManager.Start lays them.
    /// </summary>
    [TestClass]
    public class BallCadenceRunTests
    {
        private const string Args = "waves=3 wave_every=1 speed=3";

        private sealed class CountingHost : IHazardHost
        {
            public int Ended;
            public HazardOutcome? Outcome;

            public void OnEnded(HazardRun run, HazardOutcome outcome)
            {
                Ended++;
                Outcome = outcome;
            }
        }

        private HazardRun run;
        private CountingHost host;
        private RollingBallMechanic mechanic;
        private List<string> logs;

        [TestInitialize]
        public void Init()
        {
            host = new CountingHost();
            mechanic = new RollingBallMechanic();
            logs = new List<string>();

            var args = HazardArgs.Merge(HazardArgs.Parse(Args), HazardArgs.Parse(HazardDefaults.Balls));

            run = HazardManager.RegisterForTest("balls", mechanic, HazardRegionPresets.Pinnacle(0), host, DateTime.UtcNow, BallPatterns.Row, args);
            run.Context.LogForTest = logs.Add;
        }

        [TestCleanup]
        public void Cleanup() => HazardManager.Stop(run, HazardOutcome.Stopped, "test cleanup");

        private void Tick() => HazardManager.TickRun(run, DateTime.UtcNow);

        [TestMethod]
        public void Cadence_LogsItsLaunchSchedule_ThenAWaveWithEveryBallRefused_AbortsTheRunOnce()
        {
            Tick(); // the first driver tick runs Start

            Assert.AreEqual(1, logs.Count(l => l.StartsWith("balls cadence: launches at 0, 1, 2 s", StringComparison.Ordinal)),
                "the schedule is fixed at Start from the args alone: " + string.Join(" | ", logs));
            Assert.AreEqual(HazardRunState.Running, run.State);

            // the next tick runs wave 1's spawn step (every ball refused: no landblock), and Tick closes the wave
            for (var i = 0; i < 10 && !run.IsEnded; i++)
                Tick();

            Assert.IsTrue(run.IsEnded, "a wave whose every planned ball was proven lost ends the run");
            Assert.AreEqual(HazardOutcome.Aborted, run.Outcome);
            Assert.AreEqual("all balls lost in wave 1/3", run.EndReason);
            Assert.AreEqual(1, host.Ended, "OnEnded fires exactly once");
            Assert.AreEqual(HazardOutcome.Aborted, host.Outcome);
            Assert.IsTrue(logs.Any(l => l.StartsWith("wave 1: no cell", StringComparison.Ordinal)), "control: the balls really were refused for want of a cell");

            // the ended run never ticks again, and nothing ends it twice
            for (var i = 0; i < 5; i++)
                Tick();

            Assert.AreEqual(1, host.Ended);
            Assert.IsFalse(HazardManager.Stop(run, HazardOutcome.Stopped, "again"), "already ended");
        }

        [TestMethod]
        public void Cadence_WithMaxInFlight_LogsThatTheScheduleSlides()
        {
            var capped = HazardManager.RegisterForTest("balls", new RollingBallMechanic(), HazardRegionPresets.Pinnacle(0), new CountingHost(), DateTime.UtcNow, BallPatterns.Row,
                HazardArgs.Merge(HazardArgs.Parse(Args + " max_in_flight=1 max_in_flight_end=2"), HazardArgs.Parse(HazardDefaults.Balls)));
            var lines = new List<string>();

            try
            {
                capped.Context.LogForTest = lines.Add;
                HazardManager.TickRun(capped, DateTime.UtcNow);

                Assert.IsTrue(lines.Any(l => l.StartsWith("balls cadence: launches at 0, 1, 2 s at the earliest; max_in_flight 1->2", StringComparison.Ordinal) && l.Contains("the schedule then slides")), string.Join(" | ", lines));
                Tick(); // the uncapped run of this class starts too
                Assert.IsTrue(logs.Any(l => l.StartsWith("balls cadence: launches at", StringComparison.Ordinal)), "control: the uncapped run logged its schedule");
                Assert.IsFalse(logs.Any(l => l.Contains("slides")), "and says nothing of sliding");
            }
            finally
            {
                HazardManager.Stop(capped, HazardOutcome.Stopped, "test cleanup");
            }
        }

        [TestMethod]
        public void Cadence_StoppedBetweenTicks_LaunchesNothingMore_AndOnStopLogsOnce()
        {
            Tick(); // Start: wave 1 launches at once (wave_every schedules 0, 1, 2)

            var before = mechanic.Describe(run.Context);

            StringAssert.StartsWith(before, "wave 1/3 launched", "control: wave 1 is in flight before the stop");

            Assert.IsTrue(HazardManager.Stop(run, HazardOutcome.Stopped, "test stop"));
            Assert.AreEqual(1, host.Ended);

            // the teardown the landblock would run after the stop (this run has no landblock to queue it on)
            run.Context.DestroyForTeardown = _ => true;
            run.Context.Teardown(HazardOutcome.Stopped, wasStarted: true);

            // later driver ticks do nothing ...
            for (var i = 0; i < 5; i++)
                Tick();

            // ... and neither does a call that was already in flight when the stop landed, well past waves 2 and 3's times
            run.Context.SetNow(10.0);
            mechanic.Tick(run.Context);

            Assert.AreEqual(before, mechanic.Describe(run.Context), "no wave launched after the stop");
            Assert.AreEqual(1, logs.Count(l => l.StartsWith("balls stop outcome=Stopped", StringComparison.Ordinal)), "OnStop logs once: " + string.Join(" | ", logs));
            Assert.AreEqual(1, host.Ended, "OnEnded still exactly once");
        }
    }
}

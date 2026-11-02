using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class HazardRegistryTests
    {
        private sealed class CountingHost : IHazardHost
        {
            public int Ended;
            public HazardOutcome? LastOutcome;
            public bool HeldRunLock;

            public void OnEnded(HazardRun run, HazardOutcome outcome)
            {
                Interlocked.Increment(ref Ended);
                LastOutcome = outcome;

                if (Monitor.IsEntered(run.Sync))
                    HeldRunLock = true;
            }
        }

        private readonly List<HazardRun> created = new List<HazardRun>();

        private static HazardRegion Region() => HazardRegion.Rect("t", 0x0069, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);

        private HazardRun Register(IHazardHost host, DateTime? queued = null)
        {
            HazardMechanicRegistry.TryCreate(HazardMechanicRegistry.Wisps, out var mechanic);
            var run = HazardManager.RegisterForTest(HazardMechanicRegistry.Wisps, mechanic, Region(), host, queued ?? DateTime.UtcNow);
            created.Add(run);
            return run;
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (var run in created)
                HazardManager.Stop(run, HazardOutcome.Stopped, "test cleanup");
        }

        [TestMethod]
        public void Stop_IsIdempotent_AndOnEndedFiresExactlyOnce()
        {
            var host = new CountingHost();
            var run = Register(host);

            Assert.IsTrue(HazardManager.IsRegistered(run));

            Assert.IsTrue(run.Stop("first"));
            Assert.IsFalse(run.Stop("second"));
            Assert.IsFalse(HazardManager.Stop(run, HazardOutcome.Failed, "third"));

            Assert.AreEqual(1, host.Ended);
            Assert.AreEqual(HazardOutcome.Stopped, host.LastOutcome);
            Assert.AreEqual(HazardOutcome.Stopped, run.Outcome, "the first outcome sticks");
            Assert.AreEqual("first", run.EndReason);
            Assert.IsTrue(run.IsEnded);
            Assert.IsFalse(HazardManager.IsRegistered(run));
        }

        [TestMethod]
        public void OnEnded_RunsOutsideTheRunLock()
        {
            var host = new CountingHost();
            var run = Register(host);

            run.Stop();

            Assert.IsFalse(host.HeldRunLock, "OnEnded must not run under HazardRun.Sync");
        }

        [TestMethod]
        public void ConcurrentStops_FromManyThreads_FireOnEndedOnce()
        {
            for (var round = 0; round < 20; round++)
            {
                var host = new CountingHost();
                var run = Register(host);
                var wins = 0;

                Parallel.For(0, 16, i =>
                {
                    if (HazardManager.Stop(run, (HazardOutcome)(i % Enum.GetValues(typeof(HazardOutcome)).Length), "race"))
                        Interlocked.Increment(ref wins);
                });

                Assert.AreEqual(1, wins, $"round {round}: Stop returned true {wins} times");
                Assert.AreEqual(1, host.Ended, $"round {round}: OnEnded fired {host.Ended} times");
            }
        }

        [TestMethod]
        public void StoppingAPendingRun_MeansItNeverStarts()
        {
            var run = Register(new CountingHost());

            Assert.AreEqual(HazardRunState.Pending, run.State);
            run.Stop();
            Assert.IsFalse(run.TryMarkRunning());
        }

        [TestMethod]
        public void Reap_EndsAStalledRunAsReaped_Once()
        {
            var host = new CountingHost();
            var now = DateTime.UtcNow;
            var run = Register(host, now.AddSeconds(-60));

            Func<Landblock, bool> alwaysLive = _ => true;

            Assert.IsNotNull(HazardManager.ReapReason(run, now, alwaysLive));
            Assert.IsTrue(HazardManager.Reap(now, alwaysLive) >= 1);
            Assert.AreEqual(HazardOutcome.Reaped, run.Outcome);
            Assert.AreEqual(1, host.Ended);

            HazardManager.Reap(now, alwaysLive);
            Assert.AreEqual(1, host.Ended, "a reaped run is not reaped again");
        }

        [TestMethod]
        public void Reap_WithADeadLandblock_FiresOnEndedReaped_EvenWhileTicking()
        {
            var host = new CountingHost();
            var now = DateTime.UtcNow;
            var run = Register(host, now);

            run.LastTickUtc = now;

            HazardManager.Reap(now, _ => true);
            Assert.AreEqual(0, host.Ended, "alive and ticking: not reaped");

            HazardManager.Reap(now, _ => false);

            Assert.AreEqual(1, host.Ended);
            Assert.AreEqual(HazardOutcome.Reaped, host.LastOutcome);
            Assert.AreEqual("landblock unloaded", run.EndReason);
        }

        [TestMethod]
        public void LastTickUtc_RoundTripsThroughTheVolatileField()
        {
            var run = Register(new CountingHost());
            var t = new DateTime(2026, 10, 6, 1, 2, 3, DateTimeKind.Utc);

            Assert.IsNull(run.LastTickUtc);
            run.LastTickUtc = t;
            Assert.AreEqual(t, run.LastTickUtc);
            Assert.AreEqual(DateTimeKind.Utc, run.LastTickUtc.Value.Kind);
            run.LastTickUtc = null;
            Assert.IsNull(run.LastTickUtc);
        }

        [TestMethod]
        public void ReapReason_AliveWhileTicking_DeadWhenTheLandblockGoes()
        {
            var now = DateTime.UtcNow;
            var run = Register(new CountingHost(), now.AddSeconds(-60));

            run.LastTickUtc = now.AddSeconds(-1);

            Assert.IsNull(HazardManager.ReapReason(run, now, _ => true), "ticked a second ago");
            StringAssert.Contains(HazardManager.ReapReason(run, now, _ => false), "unloaded");
            Assert.IsNull(HazardManager.ReapReason(run, now.AddSeconds(HazardDefaults.ReapStaleSeconds - 2), _ => true));
            Assert.IsNotNull(HazardManager.ReapReason(run, now.AddSeconds(HazardDefaults.ReapStaleSeconds + 1), _ => true));
        }

        [TestMethod]
        public void Registry_KnowsTheFourMechanicsAndTheTools_FreshInstancePerRun()
        {
            CollectionAssert.AreEqual(new[] { "wisps", "balls", "beams", "memory" }, HazardMechanicRegistry.MechanicKinds.ToArray());
            CollectionAssert.IsSubsetOf(new[] { "move", "show" }, HazardMechanicRegistry.Kinds.ToArray());

            Assert.IsTrue(HazardMechanicRegistry.TryCreate("WISPS", out var a));
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("wisps", out var b));
            Assert.AreNotSame(a, b);
            Assert.IsFalse(HazardMechanicRegistry.TryCreate("lava", out _));

            foreach (var kind in HazardMechanicRegistry.MechanicKinds)
                Assert.IsNotNull(HazardTunables.SettingsKeyFor(kind), kind);

            Assert.IsNull(HazardTunables.SettingsKeyFor("move"));
        }

        [TestMethod]
        public void ResolvePattern_DefaultsToTheFirst_RefusesUnknown_AndPatternlessKinds()
        {
            HazardMechanicRegistry.TryCreate("wisps", out var wisps);
            HazardMechanicRegistry.TryCreate("beams", out var beams);

            Assert.IsTrue(HazardManager.TryResolvePattern(wisps, null, out var p, out _));
            Assert.AreEqual(wisps.Patterns[0], p);

            Assert.IsTrue(HazardManager.TryResolvePattern(wisps, "ROW", out p, out _));
            Assert.AreEqual("row", p);

            Assert.IsFalse(HazardManager.TryResolvePattern(wisps, "zigzag", out _, out var error));
            StringAssert.Contains(error, "scatter");

            Assert.IsTrue(HazardManager.TryResolvePattern(beams, null, out p, out _));
            Assert.IsNull(p);
            Assert.IsFalse(HazardManager.TryResolvePattern(beams, "row", out _, out _));
        }

        [TestMethod]
        public void ShippedSettingsDefaults_AreTheDefaultsConstants()
        {
            Assert.AreEqual(HazardDefaults.Wisps, HazardTunables.ShippedDefaultFor("wisps"));
            Assert.AreEqual(HazardDefaults.Balls, HazardTunables.ShippedDefaultFor("balls"));
            Assert.AreEqual(HazardDefaults.Beams, HazardTunables.ShippedDefaultFor("beams"));
            Assert.AreEqual(HazardDefaults.Memory, HazardTunables.ShippedDefaultFor("memory"));
            Assert.AreEqual("", HazardTunables.ShippedDefaultFor("show"));
        }
    }
}

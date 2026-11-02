using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class HazardDriverTests
    {
        /// <summary>Two landblock stand-ins (the prune is generic over the value type): only the dead one is dropped.</summary>
        [TestMethod]
        public void DeadKeys_DropsOnlyTheDeadLandblock()
        {
            var live = new object();
            var dead = new object();

            var entries = new Dictionary<ulong, object> { { 0x0000000000690000UL, live }, { 0x8001000500690000UL, dead } };

            var result = HazardDriver.DeadKeys(entries, lb => ReferenceEquals(lb, live));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0x8001000500690000UL, result[0].Key);
            Assert.AreSame(dead, result[0].Value);
        }

        [TestMethod]
        public void DeadKeys_AllLive_OrNoCheck_DropsNothing()
        {
            var entries = new Dictionary<ulong, object> { { 1, new object() }, { 2, new object() } };

            Assert.AreEqual(0, HazardDriver.DeadKeys(entries, _ => true).Count);
            Assert.AreEqual(0, HazardDriver.DeadKeys<ulong, object>(entries, null).Count);
            Assert.AreEqual(2, HazardDriver.DeadKeys(entries, _ => false).Count);
        }

        [TestMethod]
        public void Prune_WithNoDrivers_IsANoOp()
        {
            var calls = 0;

            Assert.AreEqual(0, HazardDriver.CountForTest(), "tests never create a driver");
            Assert.AreEqual(0, HazardDriver.Prune(_ => { calls++; return false; }));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void IsStale_UsesThreeTicks_WithAOneSecondFloor()
        {
            var t0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

            // 100 ms tick: 3 ticks = 0.3 s, floored to 1 s
            Assert.IsFalse(HazardDriver.IsStale(t0, t0.AddSeconds(0.9), 100));
            Assert.IsFalse(HazardDriver.IsStale(t0, t0.AddSeconds(1.0), 100));
            Assert.IsTrue(HazardDriver.IsStale(t0, t0.AddSeconds(1.01), 100));

            // 1000 ms tick: 3 s
            Assert.IsFalse(HazardDriver.IsStale(t0, t0.AddSeconds(2.9), 1000));
            Assert.IsTrue(HazardDriver.IsStale(t0, t0.AddSeconds(3.1), 1000));

            // well under the run reap window (5 s): a Start after a reload replaces the driver within a second
            Assert.IsTrue(HazardDriver.IsStale(t0, t0.AddSeconds(1.5), 100));
        }

        [TestMethod]
        public void ShouldForget_OnlyThisLandblocksDriver_AndOnlyWithNoRunsLeft()
        {
            Assert.IsTrue(HazardDriver.ShouldForget(sameObject: true, anyRuns: false));
            Assert.IsFalse(HazardDriver.ShouldForget(sameObject: true, anyRuns: true), "a Start registered a run between the caller's check and Forget");
            Assert.IsFalse(HazardDriver.ShouldForget(sameObject: false, anyRuns: false), "a newer copy's driver");
            Assert.IsFalse(HazardDriver.ShouldForget(sameObject: false, anyRuns: true));
        }
    }
}

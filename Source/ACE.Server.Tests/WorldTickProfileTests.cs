using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: the pure lap accumulator, the phase tag table, and the four
    /// world_tick_slow_log_* code defaults. Synthetic timestamps at 1000 ticks per second, so one tick is
    /// one millisecond and every expectation below is exact.
    ///
    /// Not covered: the WorldManager / LandblockManager lap call sites and the SlowTickReporter emit path,
    /// which need a running world (see SlowTickCostTests for the hot path driven end to end).
    /// </summary>
    [TestClass]
    public class WorldTickProfileTests
    {
        private static TickPhaseAccumulator NewAccumulator() => new TickPhaseAccumulator(1000);

        [TestMethod]
        public void PhasesSumExactlyToEndMinusBegin()
        {
            var acc = NewAccumulator();

            acc.Begin(1000);
            acc.Lap(WorldTickPhase.PlayerManager, 1003);
            acc.Lap(WorldTickPhase.InboundMessages, 1010);
            acc.Lap(WorldTickPhase.LbPhysics, 1050);
            acc.Lap(WorldTickPhase.SessionWork, 1061);
            acc.End(1064);

            long sum = 0;
            foreach (WorldTickPhase phase in Enum.GetValues(typeof(WorldTickPhase)))
                sum += acc.GetPhaseTicks(phase);

            Assert.AreEqual(64, acc.TotalTicks);
            Assert.AreEqual(acc.TotalTicks, sum, "leaf phases must sum exactly to the iteration");
            Assert.AreEqual(64.0, acc.TotalMs, 1e-9);
            Assert.AreEqual(64.0, acc.PhaseMs.Sum(), 1e-9);
        }

        [TestMethod]
        public void RepeatedLapsOfOnePhaseAccumulate()
        {
            var acc = NewAccumulator();

            acc.Begin(0);
            acc.Lap(WorldTickPhase.WorldManagers, 4);
            acc.Lap(WorldTickPhase.ThreadDungeons, 6);
            acc.Lap(WorldTickPhase.WorldManagers, 13);
            acc.End(13);

            Assert.AreEqual(11, acc.GetPhaseTicks(WorldTickPhase.WorldManagers), "4 + 7");
            Assert.AreEqual(2, acc.GetPhaseTicks(WorldTickPhase.ThreadDungeons));
        }

        [TestMethod]
        public void TimeBeforeTheFirstLapIsChargedToTheFirstLappedPhase()
        {
            var acc = NewAccumulator();

            acc.Begin(100);
            acc.Lap(WorldTickPhase.PlayerManager, 125);
            acc.End(125);

            Assert.AreEqual(25, acc.GetPhaseTicks(WorldTickPhase.PlayerManager));
            Assert.AreEqual(0, acc.GetPhaseTicks(WorldTickPhase.Other));
        }

        [TestMethod]
        public void ResidualAfterTheLastLapGoesToOther()
        {
            var acc = NewAccumulator();

            acc.Begin(0);
            acc.Lap(WorldTickPhase.SessionWork, 10);
            acc.End(17);

            Assert.AreEqual(7, acc.GetPhaseTicks(WorldTickPhase.Other));
            Assert.AreEqual(7.0, acc.PhaseMs[(int)WorldTickPhase.Other], 1e-9);
        }

        [TestMethod]
        public void BeginClearsThePreviousIteration()
        {
            var acc = NewAccumulator();

            acc.Begin(0);
            acc.Lap(WorldTickPhase.ActionQueue, 50);
            acc.End(50);

            acc.Begin(60);
            acc.Lap(WorldTickPhase.DelayManager, 61);
            acc.End(61);

            Assert.AreEqual(0, acc.GetPhaseTicks(WorldTickPhase.ActionQueue));
            Assert.AreEqual(0.0, acc.PhaseMs[(int)WorldTickPhase.ActionQueue]);
            Assert.AreEqual(1, acc.TotalTicks);
        }

        [TestMethod]
        public void WorldMsSumsOnlyThePhasesInsideUpdateGameWorld()
        {
            var acc = NewAccumulator();

            acc.Begin(0);
            acc.Lap(WorldTickPhase.PlayerManager, 1);     // outside
            acc.Lap(WorldTickPhase.DelayManager, 3);      // outside
            acc.Lap(WorldTickPhase.LbPhysics, 10);        // 7
            acc.Lap(WorldTickPhase.LbMultiThreaded, 20);  // 10
            acc.Lap(WorldTickPhase.LbSingleThreaded, 25); // 5
            acc.Lap(WorldTickPhase.LbUnload, 26);         // 1
            acc.Lap(WorldTickPhase.HouseManager, 27);     // 1
            acc.Lap(WorldTickPhase.WorldManagers, 29);    // 2
            acc.Lap(WorldTickPhase.ThreadDungeons, 30);   // 1
            acc.Lap(WorldTickPhase.SessionWork, 40);      // outside
            acc.End(41);                                  // other, outside

            Assert.AreEqual(27.0, acc.WorldMs, 1e-9);
        }

        [TestMethod]
        public void RealTimestampFrequencyConvertsToMilliseconds()
        {
            var acc = new TickPhaseAccumulator(10_000_000);

            acc.Begin(0);
            acc.Lap(WorldTickPhase.LbPhysics, 25_000); // 2.5 ms at 10 MHz
            acc.End(25_000);

            Assert.AreEqual(2.5, acc.PhaseMs[(int)WorldTickPhase.LbPhysics], 1e-9);
        }

        [TestMethod]
        public void TagTableMatchesThePhaseEnumExactly()
        {
            var expected = new[]
            {
                "player_manager", "inbound_messages", "action_queue", "delay_manager",
                "lb_physics", "lb_multithreaded", "lb_singlethreaded", "lb_unload",
                "house_manager", "thread_dungeons", "world_managers", "session_work", "other",
            };

            CollectionAssert.AreEqual(expected, WorldTickPhases.TagNames, "the dashboard and README name these strings byte for byte");
            var values = Enum.GetValues(typeof(WorldTickPhase)).Cast<int>().ToList();
            Assert.AreEqual(WorldTickPhases.Count, values.Count);
            Assert.AreEqual(WorldTickPhases.Count, WorldTickPhases.TagNames.Length);
            Assert.AreEqual((int)WorldTickPhase.Other, values.Max(), "Other must stay last");
            CollectionAssert.AreEqual(Enumerable.Range(0, WorldTickPhases.Count).ToList(), values, "values must be dense from 0: they index TagNames");

            var inside = Enum.GetValues(typeof(WorldTickPhase)).Cast<WorldTickPhase>().Where(WorldTickPhases.IsInsideUpdateGameWorld).ToList();
            CollectionAssert.AreEqual(new List<WorldTickPhase>
            {
                WorldTickPhase.LbPhysics, WorldTickPhase.LbMultiThreaded, WorldTickPhase.LbSingleThreaded, WorldTickPhase.LbUnload,
                WorldTickPhase.HouseManager, WorldTickPhase.ThreadDungeons, WorldTickPhase.WorldManagers,
            }, inside);
        }

        [TestMethod]
        public void FacadeIgnoresLapsFromAnotherThread()
        {
            WorldTickProfile.BeginIteration();

            var t = new System.Threading.Thread(() => WorldTickProfile.Lap(WorldTickPhase.LbPhysics));
            t.Start();
            t.Join();

            WorldTickProfile.Lap(WorldTickPhase.PlayerManager);
            Assert.IsTrue(WorldTickProfile.EndIteration());

            Assert.AreEqual(0, WorldTickProfile.Accumulator.GetPhaseTicks(WorldTickPhase.LbPhysics), "a lap from a non-world thread must be dropped");
            Assert.IsFalse(WorldTickProfile.EndIteration(), "a second End with no open iteration is a no-op");
        }

        [TestMethod]
        public void SlowLogDefaultsAreDeclaredAndShipOn()
        {
            // Code defaults straight from the dictionaries, NOT PropertyManager.Get* - those throw for an uncached
            // key with no live shard DB in this test host (see ConfigMetadataCoverageTests).
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["world_tick_slow_log_enabled"].Item, "world_tick_slow_log_enabled must default ON");
            Assert.AreEqual(100L, DefaultPropertyManager.DefaultLongProperties["world_tick_slow_log_threshold_ms"].Item);
            Assert.AreEqual(10L, DefaultPropertyManager.DefaultLongProperties["world_tick_slow_log_min_interval_seconds"].Item);
            Assert.AreEqual(3.0, DefaultPropertyManager.DefaultDoubleProperties["world_tick_slow_log_median_multiplier"].Item);
        }
    }
}

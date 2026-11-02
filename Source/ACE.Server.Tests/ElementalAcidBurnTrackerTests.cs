using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Elemental Proficiency's Acid burn (owner ruling 2026-11-02: "similar to the acid proc where it has a single
    /// tick counter and a new attack resets the duration but not the starting delay"). The pure new-versus-refresh
    /// plan, and the stateful loop driven by a fake clock standing in for the caster's ActionChain queue. Reads no
    /// server settings, so it needs no seeding and passes alone and in any order.
    /// </summary>
    [TestClass]
    public class ElementalAcidBurnTrackerTests
    {
        private const uint Guid = 0x80001234;
        private const double Interval = 2.0;

        /// <summary>A fake scheduler: actions run in time order when the clock is advanced.</summary>
        private sealed class FakeClock
        {
            private readonly List<(double At, int Seq, Action Action)> pending = new List<(double, int, Action)>();
            private int seq;

            public double Now { get; private set; }

            public int Pending => pending.Count;

            public IEnumerable<double> PendingTimes => pending.Select(p => p.At);

            public void Schedule(double delay, Action action) => pending.Add((Now + delay, seq++, action));

            public void AdvanceTo(double time)
            {
                while (true)
                {
                    var due = pending.Where(p => p.At <= time).OrderBy(p => p.At).ThenBy(p => p.Seq).ToList();
                    if (due.Count == 0)
                        break;

                    var next = due[0];
                    pending.Remove(next);
                    Now = next.At;
                    next.Action();
                }

                Now = time;
            }
        }

        private sealed class Harness
        {
            public readonly ElementalAcidBurnTracker Tracker = new ElementalAcidBurnTracker();
            public readonly FakeClock Clock = new FakeClock();
            public readonly List<(double At, uint Amount)> Ticks = new List<(double, uint)>();
            public bool Alive = true;

            public ElementalAcidBurnTracker.Plan Hit(uint amount, int ticks = 3) =>
                Tracker.Apply(Guid, amount, ticks, Interval, () => Alive, a => Ticks.Add((Clock.Now, a)), Clock.Schedule);
        }

        [TestMethod]
        public void PlanHit_RunningLoopRefreshes_OtherwiseStartsNew()
        {
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.Refresh, ElementalAcidBurnTracker.PlanHit(true));
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew, ElementalAcidBurnTracker.PlanHit(false));
        }

        [TestMethod]
        public void NewBurn_FirstTickIsOneIntervalAfterTheHit_NotImmediate()
        {
            var h = new Harness();

            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew, h.Hit(10));
            Assert.AreEqual(0, h.Ticks.Count, "no immediate tick: the hit itself just landed");
            Assert.AreEqual(1, h.Clock.Pending);
            Assert.AreEqual(Interval, h.Clock.PendingTimes.Single(), 1e-12);

            h.Clock.AdvanceTo(6.0);

            CollectionAssert.AreEqual(new[] { 2.0, 4.0, 6.0 }, h.Ticks.Select(t => t.At).ToArray());
            CollectionAssert.AreEqual(new uint[] { 10, 10, 10 }, h.Ticks.Select(t => t.Amount).ToArray());
            Assert.IsFalse(h.Tracker.IsBurning(Guid), "the entry goes when the loop ends");
            Assert.AreEqual(0, h.Clock.Pending);
        }

        [TestMethod]
        public void Refresh_LeavesExactlyOneLoop_AndDoesNotDelayTheNextTick()
        {
            var h = new Harness();

            h.Hit(10);
            h.Clock.AdvanceTo(1.5);

            // a second hit 1.5 s in: a refresh, nothing new scheduled, and the pending tick is still at 2.0
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.Refresh, h.Hit(25));
            Assert.AreEqual(1, h.Clock.Pending, "exactly one loop");
            Assert.AreEqual(2.0, h.Clock.PendingTimes.Single(), 1e-12, "the refresh did not push the next tick back");

            Assert.IsTrue(h.Tracker.TryGetState(Guid, out var amount, out var remaining));
            Assert.AreEqual(25u, amount, "the new hit's per-tick amount");
            Assert.AreEqual(3, remaining, "the full tick budget again");

            h.Clock.AdvanceTo(2.0);
            Assert.AreEqual(1, h.Ticks.Count);
            Assert.AreEqual(2.0, h.Ticks[0].At, 1e-12, "the tick landed on the original cadence");
            Assert.AreEqual(25u, h.Ticks[0].Amount, "and dealt the refreshed amount");
            Assert.AreEqual(1, h.Clock.Pending, "still one loop after the tick");
        }

        [TestMethod]
        public void SteadyCastingFasterThanTheInterval_StillTicksOnCadence()
        {
            // the bug this ruling fixes: a hit every 1.5 s used to restart the 2 s timer and never tick at all
            var h = new Harness();

            for (var t = 0.0; t <= 12.0 + 1e-9; t += 1.5)
            {
                h.Clock.AdvanceTo(t);
                h.Hit(10);
                Assert.IsTrue(h.Clock.Pending <= 1, $"one loop at {t}");
            }

            h.Clock.AdvanceTo(12.0);

            CollectionAssert.AreEqual(new[] { 2.0, 4.0, 6.0, 8.0, 10.0, 12.0 }, h.Ticks.Select(t => t.At).ToArray());
        }

        [TestMethod]
        public void RefreshResetsTheDuration_SoTheBurnOutlivesTheFirstHit()
        {
            var h = new Harness();

            h.Hit(10);
            h.Clock.AdvanceTo(4.5);   // two ticks dealt, one left
            Assert.AreEqual(2, h.Ticks.Count);

            h.Hit(10);                // resets to three remaining
            h.Clock.AdvanceTo(20.0);

            CollectionAssert.AreEqual(new[] { 2.0, 4.0, 6.0, 8.0, 10.0 }, h.Ticks.Select(t => t.At).ToArray());
        }

        [TestMethod]
        public void AfterTheLoopEnds_ANewHitStartsANewBurn()
        {
            var h = new Harness();

            h.Hit(10);
            h.Clock.AdvanceTo(7.0);
            Assert.IsFalse(h.Tracker.IsBurning(Guid));

            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew, h.Hit(10));
            Assert.AreEqual(9.0, h.Clock.PendingTimes.Single(), 1e-12, "one interval after the new hit");
        }

        [TestMethod]
        public void DeadTarget_StopsTheLoop_AndDropsTheEntry()
        {
            var h = new Harness();

            h.Hit(10);
            h.Clock.AdvanceTo(2.0);
            h.Alive = false;
            h.Clock.AdvanceTo(10.0);

            Assert.AreEqual(1, h.Ticks.Count);
            Assert.IsFalse(h.Tracker.IsBurning(Guid));
            Assert.AreEqual(0, h.Clock.Pending);
        }

        [TestMethod]
        public void Clear_StrandsTheOldChain_WhichNeverAdoptsANewBurn()
        {
            var h = new Harness();

            h.Hit(10);
            h.Clock.AdvanceTo(1.0);
            h.Tracker.Clear();

            // a new burn after the reset: its own loop at 3.0; the stale chain at 2.0 must not tick it
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew, h.Hit(7));
            Assert.AreEqual(2, h.Clock.Pending);

            h.Clock.AdvanceTo(2.5);
            Assert.AreEqual(0, h.Ticks.Count, "the stale chain bailed");
            Assert.AreEqual(1, h.Clock.Pending, "and did not reschedule");

            h.Clock.AdvanceTo(7.0);
            CollectionAssert.AreEqual(new[] { 3.0, 5.0, 7.0 }, h.Ticks.Select(t => t.At).ToArray());
        }

        [TestMethod]
        public void ClearDuringATick_DoesNotRescheduleOrTouchANewerBurn()
        {
            var tracker = new ElementalAcidBurnTracker();
            var clock = new FakeClock();
            var ticks = 0;

            tracker.Apply(Guid, 10, 3, Interval, () => true, a => { ticks++; tracker.Clear(); }, clock.Schedule);
            clock.AdvanceTo(10.0);

            Assert.AreEqual(1, ticks);
            Assert.AreEqual(0, clock.Pending);
            Assert.IsFalse(tracker.IsBurning(Guid));
        }

        [TestMethod]
        public void ThrowingTick_DropsTheEntry_SoTheNextHitStartsANewBurn()
        {
            var tracker = new ElementalAcidBurnTracker();
            var clock = new FakeClock();

            tracker.Apply(Guid, 10, 3, Interval, () => true, a => throw new InvalidOperationException("tick failed"), clock.Schedule);

            Assert.ThrowsExactly<InvalidOperationException>(() => clock.AdvanceTo(2.0), "the exception propagates");
            Assert.IsFalse(tracker.IsBurning(Guid), "no stranded entry");
            Assert.AreEqual(0, clock.Pending, "and no loop left");

            var ticks = new List<double>();
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew,
                tracker.Apply(Guid, 10, 3, Interval, () => true, a => ticks.Add(clock.Now), clock.Schedule));
            clock.AdvanceTo(8.0);
            CollectionAssert.AreEqual(new[] { 4.0, 6.0, 8.0 }, ticks.ToArray(), "the new burn ticks normally");
        }

        [TestMethod]
        public void ThrowingTick_LeavesANewerBurnAlone()
        {
            var tracker = new ElementalAcidBurnTracker();
            var clock = new FakeClock();
            var newerTicks = 0;

            // the failing tick first clears the table and starts a newer burn, then throws: the newer one must survive
            tracker.Apply(Guid, 10, 3, Interval, () => true, a =>
            {
                tracker.Clear();
                tracker.Apply(Guid, 5, 3, Interval, () => true, b => newerTicks++, clock.Schedule);
                throw new InvalidOperationException("tick failed");
            }, clock.Schedule);

            Assert.ThrowsExactly<InvalidOperationException>(() => clock.AdvanceTo(2.0));
            Assert.IsTrue(tracker.IsBurning(Guid), "the newer instance was not removed");

            clock.AdvanceTo(10.0);
            Assert.AreEqual(3, newerTicks);
        }

        [TestMethod]
        public void TargetsAreIndependent()
        {
            var tracker = new ElementalAcidBurnTracker();
            var clock = new FakeClock();

            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew, tracker.Apply(1, 10, 3, Interval, () => true, a => { }, clock.Schedule));
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.StartNew, tracker.Apply(2, 10, 3, Interval, () => true, a => { }, clock.Schedule));
            Assert.AreEqual(2, tracker.Count);
            Assert.AreEqual(2, clock.Pending);
        }

        [TestMethod]
        public void ZeroAmountOrNoTicks_DoesNothing_AndSendsNoLine()
        {
            var h = new Harness();

            Assert.AreEqual(ElementalAcidBurnTracker.Plan.Refresh, h.Hit(0));
            Assert.AreEqual(ElementalAcidBurnTracker.Plan.Refresh, h.Hit(10, ticks: 0));
            Assert.AreEqual(0, h.Tracker.Count);
            Assert.AreEqual(0, h.Clock.Pending);
        }
    }
}

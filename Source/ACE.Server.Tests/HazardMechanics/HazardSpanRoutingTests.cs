using System;
using System.Numerics;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The routing decisions of a spanning run (HAZARDS-PLAN A1, review round 1): which thread destroys an owned
    /// object (HazardContext.RouteDestroy), where a leg-end snap goes (HazardContext.RouteSnap), when an ended
    /// spanning run's teardown must be routed off its dead anchor (HazardManager.OrphanTeardownDue and
    /// SweepPendingTeardowns), and the settings-layer name in a refusal hint. No PropertyManager, no world.
    /// </summary>
    [TestClass]
    public class HazardSpanRoutingTests
    {
        // ---- destroy ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void RouteDestroy_SingleLandblock_IsAlwaysNow_LikeToday()
        {
            foreach (var hasLb in new[] { true, false })
                foreach (var usable in new[] { true, false })
                    foreach (var onAnchor in new[] { true, false })
                        Assert.AreEqual(HazardDestroyRoute.Now, HazardContext.RouteDestroy(false, true, hasLb, usable, onAnchor), $"lb={hasLb} usable={usable} anchor={onAnchor}");
        }

        [TestMethod]
        public void RouteDestroy_NothingAlive_IsSkipped()
        {
            Assert.AreEqual(HazardDestroyRoute.Skip, HazardContext.RouteDestroy(false, false, true, true, true));
            Assert.AreEqual(HazardDestroyRoute.Skip, HazardContext.RouteDestroy(true, false, true, true, true));
            Assert.AreEqual(HazardDestroyRoute.Skip, HazardContext.RouteDestroy(true, false, true, false, false));
        }

        [TestMethod]
        public void RouteDestroy_SpanningOnTheAnchorThread_NowWhenUsable_ElseItsOwnQueue()
        {
            Assert.AreEqual(HazardDestroyRoute.Now, HazardContext.RouteDestroy(true, true, true, true, true), "anchor or a same-group neighbour");
            Assert.AreEqual(HazardDestroyRoute.EnqueueOnItsLandblock, HazardContext.RouteDestroy(true, true, true, false, true), "a neighbour on another thread");
            Assert.AreEqual(HazardDestroyRoute.Now, HazardContext.RouteDestroy(true, true, false, false, true), "in no landblock");
        }

        [TestMethod]
        public void RouteDestroy_SpanningWithTheAnchorDead_AlwaysGoesToItsOwnLandblock()
        {
            // the orphaned teardown runs off the anchor's thread: even a landblock the span last saw as usable is
            // another thread's now, so the destroy is queued there
            Assert.AreEqual(HazardDestroyRoute.EnqueueOnItsLandblock, HazardContext.RouteDestroy(true, true, true, true, false));
            Assert.AreEqual(HazardDestroyRoute.EnqueueOnItsLandblock, HazardContext.RouteDestroy(true, true, true, false, false));
            Assert.AreEqual(HazardDestroyRoute.Now, HazardContext.RouteDestroy(true, true, false, false, false), "in no landblock: nobody else owns it");
        }

        // ---- snap ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void RouteSnap_SingleLandblock_IsTheRunLandblock_Spanning_OnlyOntoItsOwnLandblock()
        {
            foreach (var located in new[] { true, false })
                foreach (var own in new[] { true, false })
                    Assert.AreEqual(HazardSnapRoute.RunLandblock, HazardContext.RouteSnap(false, located, own));

            Assert.AreEqual(HazardSnapRoute.OwnerLocal, HazardContext.RouteSnap(true, true, true));
            Assert.AreEqual(HazardSnapRoute.StopInPlace, HazardContext.RouteSnap(true, true, false), "never relocated across a landblock edge");
            Assert.AreEqual(HazardSnapRoute.StopInPlace, HazardContext.RouteSnap(true, false, false), "target on no usable landblock");
            Assert.AreEqual(HazardSnapRoute.StopInPlace, HazardContext.RouteSnap(true, false, true));
        }

        // ---- orphaned teardown -------------------------------------------------------------------------------------

        [TestMethod]
        public void OrphanTeardownDue_AnchorDeadOrQueueDropped_NeverOnceClaimed()
        {
            var stale = HazardDefaults.ReapStaleSeconds;

            Assert.IsTrue(HazardManager.OrphanTeardownDue(false, false, 0.0), "anchor unloaded: its queue never runs");
            Assert.IsFalse(HazardManager.OrphanTeardownDue(false, true, 0.0), "anchor live: the queued Teardown will run");
            Assert.IsFalse(HazardManager.OrphanTeardownDue(false, true, stale), "not yet stale");
            Assert.IsTrue(HazardManager.OrphanTeardownDue(false, true, stale + 0.5), "live but the queued action was dropped");
            Assert.IsFalse(HazardManager.OrphanTeardownDue(true, false, 1e6), "Teardown already ran");
        }

        private static HazardRun SpanningRun(int id, out Landblock anchor)
        {
            anchor = HazardSpanTests.Token();
            var region = HazardRegion.ArenaCircle("arena", 0x7F7F, 0, 180f, 96f, 30f, 1.5f, 0f, 20f);
            var run = new HazardRun(id, "test", null, region, HazardArgs.Empty, 1, null, null, anchor, DateTime.UtcNow);

            // a span over a fake world, so nothing asks the live LandblockManager
            var world = new HazardSpanTests.FakeWorld();
            var group = new object();
            world.Loaded[0x7F7F] = anchor;
            world.Groups[anchor] = group;
            world.Add(0x807F, group);
            run.Context.Span = HazardSpan.For(region, anchor, world);

            // owned handles with no object (a test has no world objects): routed as Skip, but the teardown is claimed
            run.Context.AddOwnedForTest(new HazardHandle(1, null, new HazardSpawnSpec { Wcid = 1006951 }, Vector3.Zero));
            run.Context.AddOwnedForTest(new HazardHandle(2, null, new HazardSpawnSpec { Wcid = 1006951 }, new Vector3(200f, 96f, 1f)));
            return run;
        }

        [TestMethod]
        public void Sweep_AnchorDead_RoutesTheTeardownOnce_AndForgetsIt()
        {
            var run = SpanningRun(900001, out var anchor);
            var now = DateTime.UtcNow;
            HazardManager.TrackTeardownForTest(run, now);

            try
            {
                Assert.AreEqual(1, HazardManager.SweepPendingTeardowns(now, lb => !ReferenceEquals(lb, anchor)));
                Assert.IsTrue(run.Context.TeardownClaimed);
                Assert.AreEqual(0, run.Context.OwnedCount, "every owned handle was routed and dropped");
                Assert.IsFalse(HazardManager.IsTeardownPendingForTest(run));

                Assert.AreEqual(0, run.Context.RouteOrphanedTeardown(), "claimed: a second route does nothing");
            }
            finally
            {
                HazardManager.SweepPendingTeardowns(now.AddDays(1), _ => false);
            }
        }

        [TestMethod]
        public void Sweep_AnchorLive_WaitsForTheQueuedTeardown_ThenForgetsItWithoutRouting()
        {
            var run = SpanningRun(900002, out _);
            var now = DateTime.UtcNow;
            HazardManager.TrackTeardownForTest(run, now);

            try
            {
                Assert.AreEqual(0, HazardManager.SweepPendingTeardowns(now.AddSeconds(1), _ => true));
                Assert.IsTrue(HazardManager.IsTeardownPendingForTest(run), "still waiting for the anchor's queue");
                Assert.IsFalse(run.Context.TeardownClaimed);

                // the anchor runs the queued Teardown: it claims, and the sweep only drops the entry
                run.Context.DestroyForTeardown = _ => true;
                run.Context.Teardown(HazardOutcome.Stopped, false);
                Assert.IsTrue(run.Context.TeardownClaimed);

                Assert.AreEqual(0, HazardManager.SweepPendingTeardowns(now.AddSeconds(2), _ => true));
                Assert.IsFalse(HazardManager.IsTeardownPendingForTest(run));
                Assert.AreEqual(0, run.Context.RouteOrphanedTeardown(), "Teardown ran: the orphan route never also runs");
            }
            finally
            {
                HazardManager.SweepPendingTeardowns(now.AddDays(1), _ => false);
            }
        }

        [TestMethod]
        public void Sweep_AnchorLiveButTheQueuedActionWasDropped_RoutesOnceStale()
        {
            var run = SpanningRun(900003, out _);
            var now = DateTime.UtcNow;
            HazardManager.TrackTeardownForTest(run, now);

            try
            {
                Assert.AreEqual(0, HazardManager.SweepPendingTeardowns(now.AddSeconds(HazardDefaults.ReapStaleSeconds), _ => true));
                Assert.AreEqual(1, HazardManager.SweepPendingTeardowns(now.AddSeconds(HazardDefaults.ReapStaleSeconds + 1), _ => true));
                Assert.IsTrue(run.Context.TeardownClaimed);
                Assert.IsFalse(HazardManager.IsTeardownPendingForTest(run));
            }
            finally
            {
                HazardManager.SweepPendingTeardowns(now.AddDays(1), _ => false);
            }
        }

        // ---- the settings-layer name in a refusal ------------------------------------------------------------------

        [TestMethod]
        public void WithSettingHint_NamesTheCallersLayer_DefaultIsHazardMech()
        {
            var merged = HazardArgs.Parse("speed=99 len=3");
            var caller = HazardArgs.Parse("len=3");
            const string error = "speed=99 is outside 0.5..20";

            Assert.AreEqual(error + " (speed from setting hazard_mech_wisps)", HazardManager.WithSettingHint(error, "wisps", caller, merged));
            Assert.AreEqual(error + " (speed from setting hazard_mech_wisps)", HazardManager.WithSettingHint(error, "wisps", caller, merged, null));
            Assert.AreEqual(error + " (speed from setting world_events_hazard_wisps)", HazardManager.WithSettingHint(error, "wisps", caller, merged, "world_events_hazard_wisps"));
        }
    }
}

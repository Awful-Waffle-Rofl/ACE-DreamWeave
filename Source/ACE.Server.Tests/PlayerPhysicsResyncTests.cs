using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Physics;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PlayerPhysicsResync: the two decisions that keep a FastTick player's server-side physics object from
    /// running away from its client and being refused until relog. Each test pairs the outcome that must stay
    /// unchanged with the outcome the fix introduced, so it fails against the old behavior (never re-anchor,
    /// never resync).
    /// </summary>
    [TestClass]
    public class PlayerPhysicsResyncTests
    {
        [TestMethod]
        public void NeedsResync_ProdRunawayCase_FastTickResyncs()
        {
            // Billdozer, 2026-09-16 10:52:13: physics copy in the sea six landblocks west of the client at Tou-Tou
            Assert.IsTrue(PlayerPhysicsResync.NeedsResync(true, 0xF15A0004, 0xF75C000A));
        }

        [TestMethod]
        public void NeedsResync_NonFastTickPastOneLandblock_LeftToUpstream_WhereFastTickResyncs()
        {
            Assert.IsFalse(PlayerPhysicsResync.NeedsResync(false, 0xF15A0004, 0xF75C000A),
                "a non-FastTick player keeps the upstream update_object_server path");

            Assert.IsTrue(PlayerPhysicsResync.NeedsResync(true, 0xF15A0004, 0xF75C000A),
                "the same gap on a FastTick player resyncs");
        }

        [TestMethod]
        public void NeedsResync_BoundaryIsTwoLandblocks()
        {
            // one landblock away (diagonal included) is the normal transition
            Assert.IsFalse(PlayerPhysicsResync.NeedsResync(true, 0xF65B0001, 0xF75C000A));

            // two away on a single axis: Swick, 2026-09-14, physics copy 0xF85A002D vs client 0xF85C0002
            Assert.IsTrue(PlayerPhysicsResync.NeedsResync(true, 0xF85A002D, 0xF85C0002));
        }

        [TestMethod]
        public void ShouldDeleteForUntrackedTarget_OnlyWhenTargetDroppedOffKnownList()
        {
            Assert.IsTrue(PlayerPhysicsResync.ShouldDeleteForUntrackedTarget(false, true),
                "a target that dropped off the known list still gets the delete");

            Assert.IsFalse(PlayerPhysicsResync.ShouldDeleteForUntrackedTarget(true, true),
                "a known target already gets it from the removal broadcast");

            Assert.IsFalse(PlayerPhysicsResync.ShouldDeleteForUntrackedTarget(false, false),
                "a target that left for another instance is not sent a guid from this one");
        }

        [TestMethod]
        public void ShouldReanchorStationary_RunawayDrift_ReanchorsFastTickOnly()
        {
            // a copy that ran 300 m while the client sat still
            var driftSq = 300.0f * 300.0f;

            Assert.IsTrue(PlayerPhysicsResync.ShouldReanchorStationary(true, driftSq), "a FastTick player is pulled back");
            Assert.IsFalse(PlayerPhysicsResync.ShouldReanchorStationary(false, driftSq), "a non-FastTick player is never touched");
        }

        [TestMethod]
        public void ShouldReanchorStationary_TinyDriftIsNoOp_JustPastThresholdReanchors()
        {
            var threshold = PlayerPhysicsResync.StationaryReanchorThreshold;

            Assert.IsFalse(PlayerPhysicsResync.ShouldReanchorStationary(true, 0.05f * 0.05f), "float noise in the same cell is left alone");
            Assert.IsFalse(PlayerPhysicsResync.ShouldReanchorStationary(true, threshold * threshold), "exactly at the threshold is left alone");

            var justPast = threshold + 0.01f;
            Assert.IsTrue(PlayerPhysicsResync.ShouldReanchorStationary(true, justPast * justPast), "just past the threshold re-anchors");
        }
    }
}

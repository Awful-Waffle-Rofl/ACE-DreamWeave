using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards Portal.IsPortalTeleportPending, the pure rule behind the portal double-use guard. Portal.ActOnUse
    /// stamps Player.PendingPortalTeleportTime just before it queues the teleport through
    /// WorldManager.ThreadSafeTeleport, and both CheckUseRequirements and the top of ActOnUse refuse while this
    /// rule says a teleport is pending. Without it, a second activation that arrives before the queued
    /// Player.Teleport runs (the 2026-09-30 Defense incident: two teleports in the same millisecond, two
    /// ephemeral instances) passes every other guard, because Teleporting and LastPortalTeleportTimestamp are
    /// only set once Player.Teleport runs.
    /// <para/>
    /// The bound tests pin the lock-out invariant: a marker that is never cleared must not keep a player out of
    /// portals for longer than the ordinary 3.5 s portal cooldown.
    /// </summary>
    [TestClass]
    public class PortalDoubleUseGuardTests
    {
        private const double Now = 1_800_000_000.0;

        [TestMethod]
        public void IsPortalTeleportPending_NoMarker_IsNotPending()
        {
            Assert.IsFalse(Portal.IsPortalTeleportPending(null, Now),
                "with no accepted portal use queued, a portal use must not be refused");
        }

        [TestMethod]
        public void IsPortalTeleportPending_SecondUseInSameTick_IsPending()
        {
            // the incident shape: the duplicate activation runs in the same tick as the first, before the
            // queued teleport has executed
            Assert.IsTrue(Portal.IsPortalTeleportPending(Now, Now),
                "a second use arriving before the first use's queued teleport runs must be refused");
        }

        [TestMethod]
        public void IsPortalTeleportPending_JustInsideTheBound_IsPending()
        {
            // covers the fog-color branch of Player.Teleport, which re-queues the teleport a full second later
            Assert.IsTrue(Portal.IsPortalTeleportPending(Now - 3.4, Now),
                "a marker 3.4 s old is still inside the 3.5 s bound and must still refuse a duplicate");
        }

        [TestMethod]
        public void IsPortalTeleportPending_AtTheBound_IsNotPending()
        {
            Assert.IsFalse(Portal.IsPortalTeleportPending(Now - 3.5, Now),
                "a stale marker must expire at the 3.5 s portal cooldown, never lock the player out longer");
        }

        [TestMethod]
        public void IsPortalTeleportPending_LongStaleMarker_IsNotPending()
        {
            Assert.IsFalse(Portal.IsPortalTeleportPending(Now - 3600, Now),
                "a marker that was never cleared (a queued teleport that never ran) must not lock the player out");
        }

        [TestMethod]
        public void IsPortalTeleportPending_MarkerFarInTheFuture_IsNotPending()
        {
            // a wall-clock step backwards must not turn the marker into an open-ended lock-out
            Assert.IsFalse(Portal.IsPortalTeleportPending(Now + 3600, Now),
                "a marker an hour ahead of the clock must not refuse portal use");
        }
    }
}

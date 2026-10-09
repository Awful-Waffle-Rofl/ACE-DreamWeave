using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the two pure distance rules behind the arrival-overlap portal guard (Portal.IsArrivalOverlap and
    /// Portal.HasLeftArrivalOverlap). A player who materialises standing inside a portal - the 2026-09-30 Attack
    /// arena exit, whose return stamp was taken inside the Attack portal - must not be re-entered by the
    /// collision their first step reports, while walking clear and back on must still work. Distances are the
    /// signed edge-to-edge cylinder distance (negative = interpenetrating).
    /// </summary>
    [TestClass]
    public class PortalArrivalOverlapTests
    {
        [TestMethod]
        public void ArrivingAtThePortalCentre_IsAnOverlap()
        {
            // the incident: returned to the exact portal origin
            Assert.IsTrue(Portal.IsArrivalOverlap(-1.5), "a player landing inside a portal must be held");
        }

        [TestMethod]
        public void ArrivingJustTouchingTheEdge_IsAnOverlap()
        {
            Assert.IsTrue(Portal.IsArrivalOverlap(0.0));
            Assert.IsTrue(Portal.IsArrivalOverlap(Portal.ArrivalOverlapEnterDistance));
        }

        [TestMethod]
        public void ArrivingClearOfThePortal_IsNotAnOverlap()
        {
            Assert.IsFalse(Portal.IsArrivalOverlap(Portal.ArrivalOverlapEnterDistance + 0.01),
                "a player who lands clear of a portal must be able to walk straight into it");
            Assert.IsFalse(Portal.IsArrivalOverlap(5.0));
        }

        [TestMethod]
        public void StillInsideOrNearThePortal_IsNotReleased()
        {
            Assert.IsFalse(Portal.HasLeftArrivalOverlap(-1.5), "a player still standing in the portal must stay held");
            Assert.IsFalse(Portal.HasLeftArrivalOverlap(0.5), "jitter just outside the edge must not release the hold");
            Assert.IsFalse(Portal.HasLeftArrivalOverlap(Portal.ArrivalOverlapReleaseDistance));
        }

        [TestMethod]
        public void WalkedClearOfThePortal_IsReleased()
        {
            Assert.IsTrue(Portal.HasLeftArrivalOverlap(Portal.ArrivalOverlapReleaseDistance + 0.01),
                "walking clear must release the hold so walking back on activates the portal");
        }

        [TestMethod]
        public void ReleaseIsHysteretic()
        {
            // a release distance at or below the enter distance would let edge jitter release and re-collide
            Assert.IsTrue(Portal.ArrivalOverlapReleaseDistance > Portal.ArrivalOverlapEnterDistance);
        }
    }
}

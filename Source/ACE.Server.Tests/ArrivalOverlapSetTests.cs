using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Behavioural tests for ArrivalOverlapSet, the hold/release state behind the arrival-overlap portal guard:
    /// Player.CaptureArrivalPortalOverlaps feeds Capture at materialise, Portal.OnCollideObject(Player) asks
    /// IsHeld, and UpdatePlayerPosition calls Release after every moving position report. These walk the
    /// 2026-09-30 sequence (return stamp inside the Attack portal) and its walk-off / walk-back case, rather
    /// than restating the distance constants.
    /// </summary>
    [TestClass]
    public class ArrivalOverlapSetTests
    {
        private const uint AttackPortal = 0x7016C001;
        private const uint OtherPortal = 0x7016C002;

        [TestMethod]
        public void ArriveInside_StepWithinReach_ThenWalkClear_ThenWalkBack()
        {
            var set = new ArrivalOverlapSet();

            // materialise at the portal origin (the incident) - a second portal nearby is clear
            set.Capture(new[] { (AttackPortal, -1.5), (OtherPortal, 4.0) });

            Assert.IsTrue(set.IsHeld(AttackPortal), "the first step's collision with the portal the player landed in must be skipped");
            Assert.IsFalse(set.IsHeld(OtherPortal), "a portal the player arrived clear of must fire on the first walk-in");

            // step to 0.5 m outside the edge: still within the release margin, still held
            set.Release(guid => guid == AttackPortal ? 0.5 : (double?)null);
            Assert.IsTrue(set.IsHeld(AttackPortal), "a step to 0.5 m must not release the hold");

            // walk 1.01 m clear: released
            set.Release(guid => guid == AttackPortal ? 1.01 : (double?)null);
            Assert.IsFalse(set.IsHeld(AttackPortal), "walking clear must release the hold");
            Assert.IsTrue(set.IsEmpty);

            // walk back on: no longer held, so the collision fires
            set.Release(guid => -1.5);
            Assert.IsFalse(set.IsHeld(AttackPortal), "walking back onto a released portal must activate it");
        }

        [TestMethod]
        public void DestroyedPortal_IsReleased()
        {
            var set = new ArrivalOverlapSet();
            set.Capture(new[] { (AttackPortal, -1.5) });

            set.Release(guid => null);

            Assert.IsFalse(set.IsHeld(AttackPortal), "a held portal that no longer exists must be released");
            Assert.IsTrue(set.IsEmpty);
        }

        [TestMethod]
        public void SecondCapture_ReplacesThePreviousHold()
        {
            var set = new ArrivalOverlapSet();
            set.Capture(new[] { (AttackPortal, -1.5) });

            // the next teleport lands somewhere else, inside a different portal
            set.Capture(new[] { (OtherPortal, -0.2) });

            Assert.IsFalse(set.IsHeld(AttackPortal), "a later arrival must drop the earlier arrival's hold");
            Assert.IsTrue(set.IsHeld(OtherPortal));
        }

        [TestMethod]
        public void CaptureWithNoOverlap_LeavesNothingHeld()
        {
            var set = new ArrivalOverlapSet();
            set.Capture(new[] { (AttackPortal, -1.5) });

            set.Capture(new List<(uint, double)>());

            Assert.IsTrue(set.IsEmpty, "arriving away from every portal must clear any earlier hold");
        }
    }
}

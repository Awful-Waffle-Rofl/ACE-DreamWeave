using System;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    [TestClass]
    public class PinnacleFightRulesTests
    {
        [TestMethod]
        public void HoldExpired_AtExactlyHoldSeconds()
        {
            Assert.IsFalse(PinnacleFightRules.HoldExpired(100, 699.99, 600));
            Assert.IsTrue(PinnacleFightRules.HoldExpired(100, 700, 600));
            Assert.IsTrue(PinnacleFightRules.HoldExpired(100, 5000, 600));
        }

        [TestMethod]
        public void ResumeDue_NeedsArmPresenceAndGrace()
        {
            Assert.IsFalse(PinnacleFightRules.ResumeDue(false, 10, 100, 5), "not armed");
            Assert.IsFalse(PinnacleFightRules.ResumeDue(true, null, 100, 5), "not present");
            Assert.IsFalse(PinnacleFightRules.ResumeDue(true, 10, 14.9, 5), "inside the grace");
            Assert.IsTrue(PinnacleFightRules.ResumeDue(true, 10, 15, 5));
            Assert.IsTrue(PinnacleFightRules.ResumeDue(true, 10, 10, 0), "zero grace resumes at once");
        }

        [TestMethod]
        public void UnloadOverride_OnlyHeldOrWonAwaiting()
        {
            Assert.IsNull(PinnacleFightRules.UnloadOverride(PinnacleFightState.NotStarted, false, 600));
            Assert.IsNull(PinnacleFightRules.UnloadOverride(PinnacleFightState.Running, false, 600));
            Assert.IsNull(PinnacleFightRules.UnloadOverride(PinnacleFightState.Won, false, 600), "won and delivered");
            Assert.IsNull(PinnacleFightRules.UnloadOverride(PinnacleFightState.Lost, false, 600));
            Assert.IsNull(PinnacleFightRules.UnloadOverride(PinnacleFightState.Running, true, 600), "awaiting means nothing outside Won");

            Assert.AreEqual(TimeSpan.FromSeconds(660), PinnacleFightRules.UnloadOverride(PinnacleFightState.Held, false, 600));
            Assert.AreEqual(TimeSpan.FromSeconds(160), PinnacleFightRules.UnloadOverride(PinnacleFightState.Won, true, 100));
            Assert.AreEqual(TimeSpan.FromSeconds(60), PinnacleFightRules.UnloadOverride(PinnacleFightState.Held, false, -5), "never below the margin");
        }

        [TestMethod]
        public void Classify_CoreStopped_AlwaysIgnored()
        {
            foreach (HazardOutcome o in Enum.GetValues(typeof(HazardOutcome)))
            {
                Assert.AreEqual(RetryDecision.Ignore, PinnacleFightRules.Classify(o, true, 1), o.ToString());
                Assert.AreEqual(RetryDecision.Ignore, PinnacleFightRules.Classify(o, true, 2), o.ToString());
            }
        }

        [TestMethod]
        public void Classify_CompletedAndFailed_DeliverOnFirstAttempt()
        {
            Assert.AreEqual(RetryDecision.Deliver, PinnacleFightRules.Classify(HazardOutcome.Completed, false, 1));
            Assert.AreEqual(RetryDecision.Deliver, PinnacleFightRules.Classify(HazardOutcome.Failed, false, 1));
            Assert.AreEqual(RetryDecision.Deliver, PinnacleFightRules.Classify(HazardOutcome.Completed, false, 2));
        }

        [TestMethod]
        [DataRow(HazardOutcome.Aborted)]
        [DataRow(HazardOutcome.Reaped)]
        [DataRow(HazardOutcome.Stopped)]
        public void Classify_Faults_RetryOnceThenDeliver(HazardOutcome fault)
        {
            Assert.AreEqual(RetryDecision.Retry, PinnacleFightRules.Classify(fault, false, 1));
            Assert.AreEqual(RetryDecision.Deliver, PinnacleFightRules.Classify(fault, false, 2));
            Assert.AreEqual(RetryDecision.Deliver, PinnacleFightRules.Classify(fault, false, 3));
            Assert.AreEqual(HazardOutcome.Aborted, PinnacleFightRules.DeliveredOutcome(fault));
        }

        [TestMethod]
        public void DeliveredOutcome_KeepsRealResults()
        {
            Assert.AreEqual(HazardOutcome.Completed, PinnacleFightRules.DeliveredOutcome(HazardOutcome.Completed));
            Assert.AreEqual(HazardOutcome.Failed, PinnacleFightRules.DeliveredOutcome(HazardOutcome.Failed));
        }
    }
}

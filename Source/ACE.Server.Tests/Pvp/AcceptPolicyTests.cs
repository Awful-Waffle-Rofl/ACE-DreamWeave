using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>AcceptPolicy (Docs/Pvp/DESIGN.md "Accept (FFA)"): all-or-nothing vs proceed-if-at-least.</summary>
    [TestClass]
    public class AcceptPolicyTests
    {
        [TestMethod]
        public void AllOrNothing_EveryoneArrived_Proceeds()
        {
            Assert.IsTrue(AcceptPolicy.AllOrNothing.Proceeds(arrivedCount: 2, expectedCount: 2));
        }

        [TestMethod]
        public void AllOrNothing_OneMissing_DoesNotProceed()
        {
            Assert.IsFalse(AcceptPolicy.AllOrNothing.Proceeds(arrivedCount: 1, expectedCount: 2));
        }

        [TestMethod]
        public void ProceedIfAtLeast_MeetsMinimum_Proceeds()
        {
            var policy = AcceptPolicy.ProceedIfAtLeast(5);

            Assert.IsTrue(policy.Proceeds(arrivedCount: 5, expectedCount: 10));
        }

        [TestMethod]
        public void ProceedIfAtLeast_BelowMinimum_DoesNotProceed()
        {
            var policy = AcceptPolicy.ProceedIfAtLeast(5);

            Assert.IsFalse(policy.Proceeds(arrivedCount: 4, expectedCount: 10));
        }
    }
}

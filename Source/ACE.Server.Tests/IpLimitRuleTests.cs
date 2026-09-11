using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE: IP active-player limit - the pure rule (IpLimitManager.SelectViolators / Evaluate).
    ///
    /// These exercise only the pure decision function, not the runtime helpers (Enabled, IsExempt,
    /// GetResidents, etc), which read live config/session/world state and are intentionally kept out of the
    /// pure rule so it can be unit-tested without a world - same rationale as Player.IsEligibleForMule in
    /// MuleSystemTests.
    /// </summary>
    [TestClass]
    public class IpLimitRuleTests
    {
        private static IpLimitCandidate C(uint guid, int level, bool confined, double loginTimestamp)
        {
            return new IpLimitCandidate(guid, level, confined, loginTimestamp);
        }

        [TestMethod]
        public void AllConfined_NoViolators()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 10, true, 100),
                C(2, 20, true, 200),
                C(3, 30, true, 300),
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(0, violators.Count);
        }

        [TestMethod]
        public void OneFreeTwoConfined_CanonicalAllowedCase_NoViolators()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 10, false, 100),
                C(2, 20, true, 200),
                C(3, 30, true, 300),
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(0, violators.Count);
        }

        [TestMethod]
        public void TwoNotConfined_LowerLevelIsViolator()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 10, false, 100), // lower level
                C(2, 20, false, 200),
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            CollectionAssert.AreEqual(new List<uint> { 1 }, violators.ToList());
        }

        [TestMethod]
        public void TwoNotConfinedTwoConfined_LowerLevelOfTheTwoNotConfinedIsViolator()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 10, false, 100), // lower level, not confined
                C(2, 20, false, 200), // higher level, not confined
                C(3, 5, true, 300),   // lower level but confined - not eligible for outside-cap eviction
                C(4, 6, true, 400),
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            CollectionAssert.AreEqual(new List<uint> { 1 }, violators.ToList());
        }

        [TestMethod]
        public void FourConfined_LowestLevelIsViolator()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 40, true, 100),
                C(2, 10, true, 200), // lowest level
                C(3, 20, true, 300),
                C(4, 30, true, 400),
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            CollectionAssert.AreEqual(new List<uint> { 2 }, violators.ToList());
        }

        [TestMethod]
        public void TwoNotConfinedEqualLevel_HigherLoginTimestampIsViolator()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 10, false, 500), // most recently logged in
                C(2, 10, false, 100), // logged in longer ago - should survive
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            CollectionAssert.AreEqual(new List<uint> { 1 }, violators.ToList());
        }

        [TestMethod]
        public void TwoNotConfinedEqualLevel_TieBreakDirectionReversesWithTimestamps()
        {
            var candidates = new List<IpLimitCandidate>
            {
                C(1, 10, false, 100), // logged in longer ago - should survive
                C(2, 10, false, 500), // most recently logged in - should be evicted
            };

            var violators = IpLimitManager.SelectViolators(candidates, maxFree: 1, maxConfined: 2);

            CollectionAssert.AreEqual(new List<uint> { 2 }, violators.ToList());
        }

        [TestMethod]
        public void Evaluate_IncomingSelected_ReturnsRefuse()
        {
            var incoming = C(0, 5, false, 999); // low level, not confined, incoming
            var residents = new List<IpLimitCandidate>
            {
                C(1, 50, false, 100), // established resident, higher level, survives
            };

            var decision = IpLimitManager.Evaluate(incoming, residents, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(IpLimitAction.Refuse, decision.Action);
            Assert.AreEqual(0, decision.Evict.Count);
        }

        [TestMethod]
        public void Evaluate_IncomingOutranksOccupant_ReturnsAdmitAfterEvictingCorrectGuid()
        {
            var incoming = C(0, 50, false, 999); // high level, not confined, incoming
            var residents = new List<IpLimitCandidate>
            {
                C(7, 5, false, 100), // low level resident, not confined - should be evicted
            };

            var decision = IpLimitManager.Evaluate(incoming, residents, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(IpLimitAction.AdmitAfterEvicting, decision.Action);
            CollectionAssert.AreEqual(new List<uint> { 7 }, decision.Evict.ToList());
        }

        [TestMethod]
        public void Evaluate_NoViolators_ReturnsAdmit()
        {
            var incoming = C(0, 10, true, 999); // confined, incoming
            var residents = new List<IpLimitCandidate>
            {
                C(1, 20, false, 100),
                C(2, 30, true, 200),
            };

            var decision = IpLimitManager.Evaluate(incoming, residents, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(IpLimitAction.Admit, decision.Action);
            Assert.AreEqual(0, decision.Evict.Count);
        }

        /// <summary>
        /// Regression: a login-gate caller may pass double.MaxValue as the incoming candidate's LoginTimestamp
        /// (e.g. because the character's persisted PropertyFloat.LoginTimestamp is a stale value from a
        /// previous session and the gate does not want to trust it as "oldest"). This pins that the rule does
        /// NOT reward that inflated timestamp: on a level tie, a higher LoginTimestamp means "more recently
        /// logged in" and is evicted FIRST (see the class doc comment), so incoming still loses the tie to an
        /// established resident of the same level with a genuinely older timestamp.
        /// </summary>
        [TestMethod]
        public void Evaluate_IncomingWithMaxValueTimestamp_StillLosesLevelTieToResident()
        {
            var incoming = C(0, 10, false, double.MaxValue);
            var residents = new List<IpLimitCandidate>
            {
                C(1, 10, false, 100), // same level, genuinely older login - should survive
            };

            var decision = IpLimitManager.Evaluate(incoming, residents, maxFree: 1, maxConfined: 2);

            Assert.AreEqual(IpLimitAction.Refuse, decision.Action);
        }
    }
}

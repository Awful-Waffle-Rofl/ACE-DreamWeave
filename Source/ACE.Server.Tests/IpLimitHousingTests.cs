using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Housing confinement (any character): the housing landblock set builder (never caches an empty or failed build),
    /// the pin that Housing never starts an activity tail, and the Defaults pin for the housing dial. Pure - no PropertyManager reads, no world database.
    /// </summary>
    [TestClass]
    public class IpLimitHousingTests
    {
        // ================= housing set builder =================

        [TestMethod]
        public void BuildHousingLandblocks_Populated_YieldsObjCellIdShiftedDown()
        {
            var set = IpLimitManager.BuildHousingLandblocks(() => new uint[] { 0x01230001, 0x04560102, 0x01230005 });

            Assert.AreEqual(2, set.Count);
            Assert.IsTrue(set.Contains(0x0123));
            Assert.IsTrue(set.Contains(0x0456));
        }

        [TestMethod]
        public void BuildHousingLandblocks_ZeroRows_Throws()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => IpLimitManager.BuildHousingLandblocks(() => new List<uint>()));
        }

        // ================= negative cache for a failed build =================

        [TestMethod]
        public void HousingSetCache_ThrowingSource_QueriedOncePerWindow_ThenRetried()
        {
            var calls = 0;
            double now = 1000;
            var cache = new IpLimitManager.HousingSetCache(() => { calls++; throw new InvalidOperationException("db down"); }, () => now);

            for (var i = 0; i < 10; i++)
                Assert.IsFalse(cache.Contains(0x0123));
            Assert.AreEqual(1, calls, "inside the window the source must not be touched again");

            now += IpLimitManager.HousingSetCache.RetryAfterSeconds - 1;
            Assert.IsFalse(cache.Contains(0x0123));
            Assert.AreEqual(1, calls, "still inside the window");

            now += 2;
            Assert.IsFalse(cache.Contains(0x0123));
            Assert.AreEqual(2, calls, "after the window the source is tried again");
        }

        [TestMethod]
        public void HousingSetCache_ZeroRowsIsAFailureAndRecoversLater()
        {
            var calls = 0;
            double now = 0;
            var cache = new IpLimitManager.HousingSetCache(() => { calls++; return calls == 1 ? new List<uint>() : new uint[] { 0x01230001 }; }, () => now);

            Assert.IsFalse(cache.Contains(0x0123));
            now += IpLimitManager.HousingSetCache.RetryAfterSeconds + 1;
            Assert.IsTrue(cache.Contains(0x0123), "the failed first build must not have been cached");
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void HousingSetCache_Success_IsCachedForever()
        {
            var calls = 0;
            double now = 0;
            var cache = new IpLimitManager.HousingSetCache(() => { calls++; return new uint[] { 0x01230001 }; }, () => now);

            Assert.IsTrue(cache.Contains(0x0123));
            now += 100000;
            Assert.IsTrue(cache.Contains(0x0123));
            Assert.IsFalse(cache.Contains(0x0456));
            Assert.AreEqual(1, calls);
        }
        // ================= Housing never starts a tail =================

        [TestMethod]
        public void IsActivityDialOn_Housing_IsFalse()
        {
            var allOn = new IpLimitZoneDials(true, true, true, true, 15);

            Assert.IsFalse(IpLimitManager.IsActivityDialOn(IpLimitConfinementReason.Housing, allOn));
        }

        [TestMethod]
        public void IsWithinActivityTail_StampedByHousing_IsFalse()
        {
            var allOn = new IpLimitZoneDials(true, true, true, true, 15);

            var within = IpLimitManager.IsWithinActivityTail(
                seenAt: 1000.0, stampedInstance: 12345u, stampedReason: IpLimitConfinementReason.Housing,
                isEphemeralRealm: true, currentInstance: 12345u, now: 1005.0, dials: allOn);

            Assert.IsFalse(within);
        }

        // ================= housing dial default =================

        [TestMethod]
        public void ZoneDialDefaults_HousingDialIsOn()
        {
            Assert.IsTrue(IpLimitZoneTunables.Defaults.ConfineHousing);
        }
    }
}

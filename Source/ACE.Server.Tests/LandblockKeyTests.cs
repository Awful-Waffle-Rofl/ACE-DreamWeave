using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the 64-bit (instance, landblock) key that LandblockManager uses for its
    /// landblock table since Phase 1 of the ACRealms instancing port. Accessed via
    /// InternalsVisibleTo.
    /// </summary>
    [TestClass]
    public class LandblockKeyTests
    {
        [TestMethod]
        public void LandblockKey_NormalizesCellBits()
        {
            // any cell within the same landblock must map to the same key
            var fromCell = LandblockManager.LandblockKey(0x7F7F001C, 0);
            var fromBlock = LandblockManager.LandblockKey(0x7F7FFFFF, 0);
            var fromZeroCell = LandblockManager.LandblockKey(0x7F7F0000, 0);

            Assert.AreEqual(fromBlock, fromCell);
            Assert.AreEqual(fromBlock, fromZeroCell);
            Assert.AreEqual(0x000000007F7FFFFFul, fromBlock);
        }

        [TestMethod]
        public void LandblockKey_PlacesInstanceInHighBits()
        {
            var key = LandblockManager.LandblockKey(0x7F7FFFFF, 0x80020001);

            Assert.AreEqual(0x800200017F7FFFFFul, key);
        }

        [TestMethod]
        public void LandblockKey_DistinctInstances_YieldDistinctKeys()
        {
            var instance0 = LandblockManager.LandblockKey(0x7F7FFFFF, 0);
            var instance1 = LandblockManager.LandblockKey(0x7F7FFFFF, 1);

            Assert.AreNotEqual(instance0, instance1);

            // and the same instance across different landblocks stays distinct
            var otherBlock = LandblockManager.LandblockKey(0x7F7EFFFF, 1);
            Assert.AreNotEqual(instance1, otherBlock);
        }
    }
}

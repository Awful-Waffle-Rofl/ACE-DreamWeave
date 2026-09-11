using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards Portal.CanUseSameInstanceForPortal, the pure decision behind the PortalSameInstance flag: a
    /// plain portal may reuse the player's current instance (PlayerInstanceSelectMode.Same) only when its
    /// Destination is in the SAME landblock the player is standing in, because that is the only landblock
    /// guaranteed to exist in whatever instance the player is currently in. Runs with no database, no dat
    /// file and no world - everything here is exercised through the static pure function over hand-built
    /// LandblockId values.
    /// </summary>
    [TestClass]
    public class PortalSameInstanceTests
    {
        [TestMethod]
        public void CanUseSameInstanceForPortal_SameLandblock_ReturnsTrue()
        {
            var destination = new LandblockId((ushort)0x0022);
            var playerLocation = new LandblockId((ushort)0x0022);

            var result = Portal.CanUseSameInstanceForPortal(destination, playerLocation);

            Assert.IsTrue(result, "a destination in the same landblock the player is standing in must be allowed to stay in the player's current instance");
        }

        [TestMethod]
        public void CanUseSameInstanceForPortal_DifferentLandblock_ReturnsFalse()
        {
            var destination = new LandblockId((ushort)0x0023);
            var playerLocation = new LandblockId((ushort)0x0022);

            var result = Portal.CanUseSameInstanceForPortal(destination, playerLocation);

            Assert.IsFalse(result, "a destination in a different landblock must NOT reuse the player's current instance - that instance is not guaranteed to contain it");
        }

        [TestMethod]
        public void CanUseSameInstanceForPortal_SameLandblockDifferentCell_ReturnsTrue()
        {
            // LandblockId.Landblock is the top 16 bits only; two positions inside the same landblock but in
            // different cells (indoor rooms of the same dungeon) must still compare equal for this purpose.
            var destination = new LandblockId(0x00220034u);
            var playerLocation = new LandblockId(0x00220001u);

            var result = Portal.CanUseSameInstanceForPortal(destination, playerLocation);

            Assert.IsTrue(result, "the cell portion of the landblock id must not affect the decision - only the landblock number itself");
        }
    }
}

using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers Phase 2 of the ACRealms port: ephemeral instance id allocation and
    /// instance persistence through the runtime biota position properties.
    /// </summary>
    [TestClass]
    public class EphemeralInstanceTests
    {
        [TestMethod]
        public void RequestNewEphemeralInstanceID_SetsEphemeralBitAndRealm()
        {
            var seen = new HashSet<uint>();

            for (var i = 0; i < 500; i++)
            {
                var instance = LandblockManager.RequestNewEphemeralInstanceIDv1(homeRealmId: 42);

                Position.ParseInstanceID(instance, out var isEphemeral, out var realmId, out var shortInstanceId);

                Assert.IsTrue(isEphemeral, $"0x{instance:X8} is missing the ephemeral bit");
                Assert.AreEqual((ushort)42, realmId);
                Assert.IsTrue(shortInstanceId >= 1 && shortInstanceId <= 0xFFFD, $"short id {shortInstanceId} out of range");

                // every allocation must be unique while pending
                Assert.IsTrue(seen.Add(instance), $"0x{instance:X8} was allocated twice");
            }
        }

        [TestMethod]
        public void PropertiesPosition_Clone_KeepsInstance()
        {
            var pos = new PropertiesPosition { ObjCellId = 0x7F7F001C, Instance = 0x80020001 };

            var clone = pos.Clone();

            Assert.AreEqual(pos.Instance, clone.Instance);
        }

        [TestMethod]
        public void Biota_SetGetPosition_RoundTripsInstance()
        {
            var biota = new Biota();
            var rwLock = new System.Threading.ReaderWriterLockSlim();

            var original = new Position(0x7F7F001C, 84, 84, 80, 0, 0, 0, 1, 0x00020001);

            biota.SetPosition(PositionType.Location, original, rwLock);

            var loaded = biota.GetPosition(PositionType.Location, rwLock);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(original.Instance, loaded.Instance);
            Assert.AreEqual(original.Cell, loaded.Cell);
        }
    }
}

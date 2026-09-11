using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// <see cref="NoLogLandblock.Apply"/> rewrites a character's persisted Location to their lifestone
    /// when they logged out on a "no lifestone-on-relog" landblock. The rewrite has to carry the lifestone's
    /// INSTANCE as well as its cell: the Drift Network hub is realm 1's copy of 0x0007 (Town Network, a no-log
    /// block), so a player logging out there used to come back on a realm-0 lifestone cell with the realm-1
    /// instance still attached - an empty realm-1 copy of their lifestone's landblock.
    /// </summary>
    [TestClass]
    public class NoLogLandblockRelogTests
    {
        private const uint Realm1Instance = 0x00010000;
        private const uint TownNetworkCell = 0x00070149;   // Drift Network hub, realm 1 copy of 0x0007
        private const uint HoltburgLifestone = 0xA9B40019;

        private static Biota MakeBiota(WeenieType weenieType, uint locationCell, uint? locationInstance, uint? sanctuaryInstance)
        {
            return new Biota
            {
                WeenieType = weenieType,
                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    [PositionType.Location] = new PropertiesPosition { ObjCellId = locationCell, Instance = locationInstance, PositionX = 1, PositionY = 2, PositionZ = 3, RotationW = 1 },
                    [PositionType.Sanctuary] = new PropertiesPosition { ObjCellId = HoltburgLifestone, Instance = sanctuaryInstance, PositionX = 84, PositionY = 7.1f, PositionZ = 94, RotationW = 0.996917f, RotationZ = -0.0784591f },
                },
            };
        }

        [TestMethod]
        public void NoLogBlock_InRealmCopy_RelogsToLifestoneInTheLifestonesInstance()
        {
            var biota = MakeBiota(WeenieType.Creature, TownNetworkCell, Realm1Instance, 0);

            NoLogLandblock.Apply(biota, out var moved);

            Assert.IsTrue(moved);
            var location = biota.PropertiesPosition[PositionType.Location];
            Assert.AreEqual(HoltburgLifestone, location.ObjCellId);
            Assert.AreEqual(0u, location.Instance ?? 0, "the lifestone's instance (base world) must replace the realm-1 instance");
            Assert.AreEqual(84f, location.PositionX);
            Assert.AreEqual(0.996917f, location.RotationW);
        }

        [TestMethod]
        public void NoLogBlock_LifestoneInARealm_RelogKeepsThatRealm()
        {
            // the inverse direction: a lifestone bound inside a realm must carry its own instance too
            var biota = MakeBiota(WeenieType.Creature, TownNetworkCell, 0, Realm1Instance);

            NoLogLandblock.Apply(biota, out var moved);

            Assert.IsTrue(moved);
            Assert.AreEqual(Realm1Instance, biota.PropertiesPosition[PositionType.Location].Instance);
        }

        [TestMethod]
        public void OrdinaryBlock_IsLeftAlone()
        {
            var biota = MakeBiota(WeenieType.Creature, 0x017D0175, Realm1Instance, 0);

            NoLogLandblock.Apply(biota, out var moved);

            Assert.IsFalse(moved);
            var location = biota.PropertiesPosition[PositionType.Location];
            Assert.AreEqual(0x017D0175u, location.ObjCellId);
            Assert.AreEqual(Realm1Instance, location.Instance);
        }

        [TestMethod]
        public void AdminOnNoLogBlock_IsExempt()
        {
            var biota = MakeBiota(WeenieType.Admin, TownNetworkCell, Realm1Instance, 0);

            NoLogLandblock.Apply(biota, out var moved);

            Assert.IsFalse(moved);
            Assert.AreEqual(TownNetworkCell, biota.PropertiesPosition[PositionType.Location].ObjCellId);
        }
    }
}

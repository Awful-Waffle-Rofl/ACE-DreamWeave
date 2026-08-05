using System;
using System.Collections.Generic;

using ACE.Database.Adapter;
using ACE.Database.Models.World;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Realms Phase 3: per-realm content rows must map losslessly into the base
    /// LandblockInstance shape that WorldObjectFactory consumes.
    /// </summary>
    [TestClass]
    public class RealmContentTests
    {
        [TestMethod]
        public void RealmContentConverter_MapsAllSpawnFields()
        {
            var row = new LandblockInstanceRealm
            {
                RealmId = 1,
                Guid = 0x7019EFFF,
                Landblock = 0x019E,
                WeenieClassId = 3113,
                ObjCellId = 0x019E0109,
                OriginX = 10.5f,
                OriginY = -20.25f,
                OriginZ = 0.005f,
                AnglesW = 0.707107f,
                AnglesX = 0f,
                AnglesY = 0f,
                AnglesZ = -0.707107f,
                IsLinkChild = true,
                LastModified = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            };

            var instance = RealmContentConverter.ConvertToLandblockInstance(row);

            Assert.AreEqual(row.Guid, instance.Guid);
            Assert.AreEqual(row.Landblock, instance.Landblock);
            Assert.AreEqual(row.WeenieClassId, instance.WeenieClassId);
            Assert.AreEqual(row.ObjCellId, instance.ObjCellId);
            Assert.AreEqual(row.OriginX, instance.OriginX);
            Assert.AreEqual(row.OriginY, instance.OriginY);
            Assert.AreEqual(row.OriginZ, instance.OriginZ);
            Assert.AreEqual(row.AnglesW, instance.AnglesW);
            Assert.AreEqual(row.AnglesX, instance.AnglesX);
            Assert.AreEqual(row.AnglesY, instance.AnglesY);
            Assert.AreEqual(row.AnglesZ, instance.AnglesZ);
            Assert.AreEqual(row.IsLinkChild, instance.IsLinkChild);
        }

        [TestMethod]
        public void RealmContentConverter_MapsGeneratorLinks()
        {
            var row = new LandblockInstanceRealm
            {
                RealmId = 1,
                Guid = 0x7019EFFE,
                WeenieClassId = 5085,
                LandblockInstanceLinkRealm = new List<LandblockInstanceLinkRealm>
                {
                    new LandblockInstanceLinkRealm { RealmId = 1, ParentGuid = 0x7019EFFE, ChildGuid = 0x7019EFFD },
                    new LandblockInstanceLinkRealm { RealmId = 1, ParentGuid = 0x7019EFFE, ChildGuid = 0x7019EFFC },
                },
            };

            var instance = RealmContentConverter.ConvertToLandblockInstance(row);

            Assert.AreEqual(2, instance.LandblockInstanceLink.Count);

            foreach (var link in instance.LandblockInstanceLink)
                Assert.AreEqual(instance.Guid, link.ParentGuid);
        }
    }
}

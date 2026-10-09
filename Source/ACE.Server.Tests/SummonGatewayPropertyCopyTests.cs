using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using RuntimeBiota = ACE.Entity.Models.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the fork properties a summoned gateway carries over from the portal it was summoned from.
    ///
    /// The regression this pins: BluespireLadderRung (9069) was not copied, and Portal.CheckUseRequirements
    /// applies the Bluespire ladder gate only when the portal carries 9069, so a summoned rung portal
    /// skipped the level and prerequisite gate.
    ///
    /// Two layers. The SummonedGateway_* tests drive WorldObject.BuildSummonedGateway - everything
    /// SummonPortal does before EnterWorld - with a seeded rung portal weenie, and assert on the gateway it
    /// really builds, so deleting the CopyForkGatewayProperties call in it fails them (checked by doing
    /// exactly that, 2026-09-18). The remaining tests pin the helper itself. World weenies and GuidManager
    /// are seeded by reflection, the same way PortalDestinationGuardTests and MuleSummonTests do it, so no
    /// database is needed. No PropertyManager key is read here.
    /// </summary>
    [TestClass]
    public class SummonGatewayPropertyCopyTests
    {
        private static uint nextWcid = 90900;

        private static uint NextWcid() => ++nextWcid;

        private static void SeedWorldWeenie(Weenie weenie)
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[weenie.WeenieClassId] = weenie;
        }

        private static void SeedPortalGatewayWeenie()
        {
            var wcid = NextWcid();
            SeedWorldWeenie(new Weenie { WeenieClassId = wcid, ClassName = "portalgateway", WeenieType = WeenieType.Portal });

            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var nameDict = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);
            nameDict["portalgateway"] = wcid;
        }

        /// <summary>
        /// A portal weenie with a Destination row (BuildSummonedGateway refuses one without) and, when
        /// given, the fork properties under test.
        /// </summary>
        private static uint SeedRungPortal(int? ladderRung, int? portalRealm)
        {
            var wcid = NextWcid();

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"testrungportal{wcid}",
                WeenieType = WeenieType.Portal,
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    [PositionType.Destination] = new PropertiesPosition { ObjCellId = 0x001501CA, PositionX = 12.267f, PositionY = -59.989f, PositionZ = 12.005f, RotationW = 1f },
                },
            };

            if (ladderRung.HasValue)
                weenie.PropertiesInt[PropertyInt.BluespireLadderRung] = ladderRung.Value;
            if (portalRealm.HasValue)
                weenie.PropertiesInt[PropertyInt.PortalRealm] = portalRealm.Value;

            SeedWorldWeenie(weenie);
            return wcid;
        }

        /// <summary>
        /// Mirrors PortalDestinationGuardTests.EnsureGuidManagerConstructible so CreateNewWorldObject's
        /// GuidManager.NewDynamicGuid() runs without a shard database. Seeded at 0x8E800000, clear of that
        /// file's 0x8E000000 and MuleSummonTests' 0x8Dxxxxxx counters.
        /// </summary>
        private static bool guidManagerSetUpDone;

        private static void EnsureGuidManagerConstructible()
        {
            if (guidManagerSetUpDone)
                return;

            var allocatorType = typeof(GuidManager).GetNestedType("DynamicGuidAllocator", BindingFlags.NonPublic);
            Assert.IsNotNull(allocatorType, "GuidManager.DynamicGuidAllocator was not found by reflection - has it been renamed?");

            var allocator = FormatterServices.GetUninitializedObject(allocatorType);

            void SetField(string name, object value) =>
                allocatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(allocator, value);

            SetField("min", ObjectGuid.DynamicMin);
            SetField("max", ObjectGuid.DynamicMax);
            SetField("current", 0x8E800000u);
            SetField("name", "test-dynamic");
            SetField("recycledGuids", System.Activator.CreateInstance(typeof(Queue<System.Tuple<System.DateTime, uint>>)));

            var availableIdsFieldType = allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType;
            SetField("availableIDs", System.Activator.CreateInstance(availableIdsFieldType));
            SetField("useSequenceGapExhaustedMessageDisplayed", false);

            var dynamicAllocField = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(dynamicAllocField, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");
            dynamicAllocField.SetValue(null, allocator);

            guidManagerSetUpDone = true;
        }

        private static Portal BuildGateway(uint portalWcid)
        {
            EnsureGuidManagerConstructible();
            SeedPortalGatewayWeenie();

            var location = new Position(0x7F200002, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0);
            var gateway = WorldObject.BuildSummonedGateway(portalWcid, location, 300d);

            Assert.IsNotNull(gateway, "BuildSummonedGateway refused a portal that has a Destination - the fixture no longer reaches the property copy.");
            return gateway;
        }

        [TestMethod]
        public void SummonedGateway_FromRungPortal_CarriesTheLadderRung()
        {
            var gateway = BuildGateway(SeedRungPortal(ladderRung: 1, portalRealm: null));

            Assert.AreEqual(1, gateway.GetProperty(PropertyInt.BluespireLadderRung),
                "The summoned gateway lost BluespireLadderRung, so Portal.CheckUseRequirements would skip the ladder gate for it.");
        }

        [TestMethod]
        public void SummonedGateway_FromRealmPortal_CarriesThePortalRealm()
        {
            var gateway = BuildGateway(SeedRungPortal(ladderRung: null, portalRealm: 1));

            Assert.AreEqual(1, gateway.GetProperty(PropertyInt.PortalRealm));
        }

        [TestMethod]
        public void SummonedGateway_FromOrdinaryPortal_GainsNeitherProperty()
        {
            var gateway = BuildGateway(SeedRungPortal(ladderRung: null, portalRealm: null));

            Assert.IsNull(gateway.GetProperty(PropertyInt.BluespireLadderRung));
            Assert.IsNull(gateway.GetProperty(PropertyInt.PortalRealm));
        }

        private static WorldObject CreateBare(uint id)
        {
            var biota = new RuntimeBiota
            {
                Id = id,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        [TestMethod]
        public void LadderRung_IsCopiedOntoTheGateway()
        {
            var portal = CreateBare(0x7F100001);
            var gateway = CreateBare(0x7F100002);
            portal.SetProperty(PropertyInt.BluespireLadderRung, 2);

            WorldObject.CopyForkGatewayProperties(portal, gateway);

            Assert.AreEqual(2, gateway.GetProperty(PropertyInt.BluespireLadderRung));
        }

        [TestMethod]
        public void PortalRealm_IsStillCopiedOntoTheGateway()
        {
            var portal = CreateBare(0x7F100003);
            var gateway = CreateBare(0x7F100004);
            portal.SetProperty(PropertyInt.PortalRealm, 1);

            WorldObject.CopyForkGatewayProperties(portal, gateway);

            Assert.AreEqual(1, gateway.GetProperty(PropertyInt.PortalRealm));
        }

        [TestMethod]
        public void OrdinaryPortal_GatewayGainsNeitherProperty()
        {
            var portal = CreateBare(0x7F100005);
            var gateway = CreateBare(0x7F100006);

            WorldObject.CopyForkGatewayProperties(portal, gateway);

            Assert.IsNull(gateway.GetProperty(PropertyInt.BluespireLadderRung));
            Assert.IsNull(gateway.GetProperty(PropertyInt.PortalRealm));
        }
    }
}

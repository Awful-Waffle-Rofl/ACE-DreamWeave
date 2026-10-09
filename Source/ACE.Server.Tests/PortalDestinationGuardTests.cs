using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the 2026-09-01 world-thread crash (Docs postmortem: WorldObject_Magic.cs's SummonPortal
    /// building `new Position(portal.Destination)` off a Destination-less portal weenie - wcid 1003000,
    /// ace1003000-theprovinggroundsspeed, has no PropertiesPosition row for PositionType.Destination,
    /// so Portal.Destination reads null and the Position copy constructor NREs on it).
    ///
    /// SummonPortal is `protected static bool SummonPortal(uint portalId, Position location, double
    /// portalLifetime)` - no Player/Session/ActionChain dependency, only GetPortal (a
    /// DatabaseManager.World.GetCachedWeenie lookup) and WorldObjectFactory.CreateNewWorldObject
    /// ("portalgateway"). Both resolve from the world weenie cache without a live world database using
    /// the same reflection seeding MuleSummonTests.cs already relies on (SeedWorldWeenie /
    /// SeedWorldWeenieByName against WorldDatabaseWithEntityCache's private caches). The other two
    /// guarded call sites (HandleCastSpell_PortalRecall, the PortalTie case) take a live Player/Session
    /// and run inside an ActionChain lambda - not reachable this way, per code review.
    /// </summary>
    [TestClass]
    public class PortalDestinationGuardTests
    {
        private static uint nextWcid = 90800;

        private static uint NextWcid() => ++nextWcid;

        private static void SeedWorldWeenie(uint wcid, WeenieType type)
        {
            var weenie = new Weenie { WeenieClassId = wcid, WeenieType = type };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        private static void SeedWorldWeenieByName(string className, uint wcid)
        {
            SeedWorldWeenie(wcid, WeenieType.Portal);

            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var nameDict = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);
            nameDict[className.ToLower()] = wcid;
        }

        /// <summary>
        /// Mirrors MuleSummonTests.EnsureGuidManagerConstructible - seeds GuidManager's private
        /// dynamicAlloc field via reflection so WorldObjectFactory.CreateNewWorldObject's real
        /// GuidManager.NewDynamicGuid() call (needed to materialize the "portalgateway" object) can run
        /// without a live shard database. Seeded well clear of that file's own 0x8Dxxxxxx counter to
        /// avoid any cross-file collision if both classes run in the same process.
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
            SetField("current", 0x8E000000u);
            SetField("name", "test-dynamic");
            SetField("recycledGuids", System.Activator.CreateInstance(typeof(System.Collections.Generic.Queue<System.Tuple<System.DateTime, uint>>)));

            var availableIdsFieldType = allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType;
            SetField("availableIDs", System.Activator.CreateInstance(availableIdsFieldType));
            SetField("useSequenceGapExhaustedMessageDisplayed", false);

            var dynamicAllocField = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(dynamicAllocField, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");
            dynamicAllocField.SetValue(null, allocator);

            guidManagerSetUpDone = true;
        }

        private static MethodInfo SummonPortalMethod =>
            typeof(WorldObject).GetMethod("SummonPortal", BindingFlags.NonPublic | BindingFlags.Static);

        [TestMethod]
        public void SummonPortal_WithNoDestination_ReturnsFalseInsteadOfThrowing()
        {
            EnsureGuidManagerConstructible();

            // No PropertiesPosition entry at all (matches SeedWorldWeenie's shape, and matches the
            // 95 destination-less portal rows in prod) - Portal.Destination reads null.
            var portalWcid = NextWcid();
            SeedWorldWeenie(portalWcid, WeenieType.Portal);

            // SummonPortal materializes "portalgateway" via WorldObjectFactory.CreateNewWorldObject,
            // which resolves the name through the same weenie-name cache.
            SeedWorldWeenieByName("portalgateway", NextWcid());

            var location = new Position(0x7F200002, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0);

            Assert.IsNotNull(SummonPortalMethod, "WorldObject.SummonPortal was not found by reflection - has it been renamed or its signature changed?");

            var args = new object[] { portalWcid, location, 300d };

            bool result;
            try
            {
                result = (bool)SummonPortalMethod.Invoke(null, args);
            }
            catch (TargetInvocationException ex)
            {
                Assert.Fail($"SummonPortal threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} - the null-Destination guard did not hold.");
                return;
            }

            Assert.IsFalse(result, "SummonPortal should refuse a Destination-less portal, not summon a broken gateway.");
        }
    }
}

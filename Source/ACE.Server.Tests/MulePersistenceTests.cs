using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 11.5; risk R9): WorldObject_Database.
    /// IsDynamicThatShouldPersistToShard must exclude a summoned PersonalVendor - WeenieType.Vendor is
    /// NOT in the WeenieType exemption list (Pet and CombatPet are), so without the exclusion added by
    /// this task, Landblock.SaveDB would write one biota per summon. Also confirms no collateral
    /// damage: a PLAIN Vendor (not PersonalVendor) must be unaffected.
    /// </summary>
    [TestClass]
    public class MulePersistenceTests
    {
        // Dynamic-range guids (>= ObjectGuid.DynamicMin, 0x80000000) - IsDynamicThatShouldPersistToShard
        // returns false immediately for a non-dynamic guid, so a static-range guid would make both
        // tests pass for the wrong reason (never reaching the PersonalVendor check at all).
        private static uint nextGuid = 0x8F320000;

        private static uint NextGuid() => ++nextGuid;

        private static bool vendorConstructibleSetUpDone;

        private static void EnsureVendorConstructible()
        {
            if (vendorConstructibleSetUpDone)
                return;

            TestGameTables.EnsureInitialized();

            PropertyManager.ModifyBool("vendor_shop_uses_generator", false);
            PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            PropertyManager.ModifyLong("account_vault_landblock", 0x7F20);

            SeedWorldWeenie((uint)WeenieClassName.W_COINSTACK_CLASS, WeenieType.Coin);

            vendorConstructibleSetUpDone = true;
        }

        /// <summary>Mirrors PersonalVendorTests.SeedWorldWeenie - Vendor.ValidateVendorRequirements looks up the currency weenie in this cache.</summary>
        private static void SeedWorldWeenie(uint wcid, WeenieType type)
        {
            var weenie = new Weenie { WeenieClassId = wcid, WeenieType = type };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        private static Weenie MakeVendorWeenie(bool personalVendor)
        {
            var properties = new Dictionary<PropertyBool, bool>();

            if (personalVendor)
                properties[PropertyBool.PersonalVendor] = true;

            return new Weenie
            {
                WeenieClassId = 90303,
                WeenieType = WeenieType.Vendor,
                PropertiesBool = properties,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, -1 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, 1000000 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.BuyPrice, 1.0 },
                    { PropertyFloat.SellPrice, 1.0 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Test Mule" },
                },
            };
        }

        [TestMethod]
        public void SummonedVendor_IsNotDynamicThatShouldPersistToShard()
        {
            EnsureVendorConstructible();

            var vendor = new PersonalVendor(MakeVendorWeenie(true), new ObjectGuid(NextGuid()));

            Assert.IsTrue(vendor.Guid.IsDynamic(), "sanity: the exclusion under test is only reachable for a dynamic guid");
            Assert.IsFalse(vendor.IsDynamicThatShouldPersistToShard(), "R9: a summoned PersonalVendor must never be written to the shard by Landblock.SaveDB");
        }

        [TestMethod]
        public void PlainVendor_IsUnaffectedByTheExclusion()
        {
            EnsureVendorConstructible();

            var guid = new ObjectGuid(NextGuid());
            var wo = WorldObjectFactory.CreateWorldObject(MakeVendorWeenie(false), guid);

            Assert.IsInstanceOfType(wo, typeof(Vendor));
            Assert.IsFalse(wo is PersonalVendor, "sanity: this must be a PLAIN Vendor, not a PersonalVendor");
            Assert.IsTrue(wo.Guid.IsDynamic(), "sanity: the exclusion under test is only reachable for a dynamic guid");
            Assert.IsTrue(wo.IsDynamicThatShouldPersistToShard(), "no collateral damage: a plain Vendor's persistence must be unaffected by the PersonalVendor exclusion");
        }

        /// <summary>
        /// Fix round 2, F2: the PersonalVendor exclusion must sit ABOVE
        /// BiotaOriginatedFromOrHasBeenSavedToDatabase()'s short-circuit, not below it - a mule that
        /// was EVER saved (a pre-exclusion-build shard's leftover row, rehydrated by the factory on
        /// the next landblock activation) must still be excluded, or it becomes a permanent,
        /// store-less zombie vendor that Landblock.SaveDB keeps re-saving forever. SaveBiotaToDatabase
        /// (false) sets LastRequestedDatabaseSave without an actual DB round trip, which is exactly
        /// what BiotaOriginatedFromOrHasBeenSavedToDatabase() checks (WorldObject_Database.cs:41).
        /// This test FAILED against the pre-fix-round-2 ordering (the short-circuit returned true
        /// before the PersonalVendor check ever ran) - confirmed by moving the check back below the
        /// short-circuit locally, rerunning, and seeing it go red, then restoring the fixed order.
        /// </summary>
        [TestMethod]
        public void SummonedVendor_ThatWasEverSaved_IsStillExcluded()
        {
            EnsureVendorConstructible();

            var vendor = new PersonalVendor(MakeVendorWeenie(true), new ObjectGuid(NextGuid()));

            vendor.SaveBiotaToDatabase(false);

            Assert.IsTrue(vendor.BiotaOriginatedFromOrHasBeenSavedToDatabase(), "sanity: the already-saved short-circuit must actually be armed by this call");
            Assert.IsFalse(vendor.IsDynamicThatShouldPersistToShard(), "R9/F2: a PersonalVendor that was ever saved must still be excluded - order matters, not just presence, of the exclusion check");
        }
    }
}

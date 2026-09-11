using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor prerequisite: PersonalVendor exists only to override retail Vendor behavior that is
    /// actively wrong for a private store (DESIGN 9.1) - it destroys stackables on sale, deletes their
    /// biotas, rots player-sold stock after 300 s, and floors every price at 1. None of those can be
    /// overridden unless the base members are reachable and virtual, and Vendor originally had zero
    /// virtual members with three of the relevant ones private.
    ///
    /// This test pins the enabling change so a later tidy-up cannot quietly revert it. A reverted
    /// modifier would otherwise surface as PersonalVendor silently inheriting the destructive retail
    /// path, which is item loss, not a compile error, if the override keyword is dropped at the same time.
    /// </summary>
    [TestClass]
    public class VendorVirtualizationTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [TestMethod]
        public void EveryMemberPersonalVendorOverrides_IsVirtualOnVendor()
        {
            var required = new[]
            {
                "LoadInventory", "PrepareResetToHome", "RotUniques",
                "forEachItem", "TryGetItemForSale", "AddDefaultItem",
                "GetSellCost", "GetBuyCost", "CalculatePayoutCoinAmount",
                "ProcessItemsForPurchase", "BuyItems_ValidateTransaction", "ApproachVendor",
                "CanAccept",
            };

            foreach (var name in required)
            {
                var overloads = typeof(Vendor).GetMethods(Any).Where(m => m.Name == name && m.DeclaringType == typeof(Vendor)).ToList();

                Assert.AreNotEqual(0, overloads.Count, $"Vendor.{name} not found");

                // GetSellCost/GetBuyCost keep a private non-virtual (int?, ItemType?) helper; only the
                // public one-argument forms are entry points and only those must be virtual.
                var entryPoints = overloads.Where(m => m.IsPublic || name == "LoadInventory" || name == "PrepareResetToHome" || name == "RotUniques").ToList();

                Assert.AreNotEqual(0, entryPoints.Count, $"Vendor.{name} has no reachable entry point");

                foreach (var m in entryPoints)
                    Assert.IsTrue(m.IsVirtual, $"Vendor.{name} must be virtual for PersonalVendor to override it");
            }

            // ItemsForSaleCount (fix round 1, I1) is a PROPERTY, not a method - GetMethods above would
            // silently find zero overloads for it (the compiled method is get_ItemsForSaleCount) and
            // the loop's own "not found" assert would catch a truly missing member, but folding a
            // property into a method-name loop is itself the bug this separate check exists to avoid
            // introducing. Checked on its own terms instead.
            var itemsForSaleCount = typeof(Vendor).GetProperty("ItemsForSaleCount", Any);
            Assert.IsNotNull(itemsForSaleCount, "Vendor.ItemsForSaleCount not found");
            Assert.IsTrue(itemsForSaleCount.GetGetMethod(true).IsVirtual, "Vendor.ItemsForSaleCount must be virtual for PersonalVendor to override it");
        }

        [TestMethod]
        public void ProtectedMembers_AreNotPrivate()
        {
            foreach (var name in new[] { "LoadInventory", "PrepareResetToHome", "RotUniques" })
            {
                var m = typeof(Vendor).GetMethod(name, Any);

                Assert.IsNotNull(m, $"Vendor.{name} not found");
                Assert.IsFalse(m.IsPrivate, $"Vendor.{name} must be protected, not private - a subclass cannot reach a private member");
            }
        }
    }
}

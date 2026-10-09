using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Vendor sell pricing of STACKED purchases (Vendor.CalcSellCost / Vendor.GetStackUnitValue).
    ///
    /// A quantity-N buy of a stackable reaches the price calculation as ONE WorldObject with
    /// StackSize = N and Value = StackUnitValue * N, so pricing the aggregate Value applied the
    /// max(1, ...) floor and the Ceiling once per STACK instead of once per unit. On the Marketplace
    /// MMD sundries vendor (SellPrice 1.25, Value-0 clone stock) that sold 100 Black Market Health
    /// Elixirs for 1 MMD; on its Promissory Note it sold 1000 notes for 1250 MMD instead of 2000.
    /// These pin the per-unit arithmetic, which is also what the client displays: it is sent only
    /// the sell rate and the unit Value, so it can only price a purchase as unit price x quantity.
    ///
    /// The helpers are static and take plain values, so nothing here needs a live Vendor (which
    /// would reach DatabaseManager during ValidateVendorRequirements).
    /// </summary>
    [TestClass]
    public class VendorStackPricingTests
    {
        // The Marketplace sundries vendor's rate. Its stock spans all three interesting cases:
        // Value 0 (the clones), Value 1 (the retail Promissory Note), Value 4 (the spirit box).
        private const double SundriesRate = 1.25;

        [TestMethod]
        public void SingleUnitPricing_IsUnchanged()
        {
            Assert.AreEqual(1u, Vendor.CalcSellCost(SundriesRate, 0, ItemType.Misc, 1), "Value 0 floors to 1");
            Assert.AreEqual(2u, Vendor.CalcSellCost(SundriesRate, 1, ItemType.Misc, 1), "ceil(1.25 - 0.1) = 2");
            Assert.AreEqual(5u, Vendor.CalcSellCost(SundriesRate, 4, ItemType.Misc, 1), "ceil(5.0 - 0.1) = 5");
        }

        [TestMethod]
        public void ValueZeroStack_ChargesTheFloorPerUnit_NotPerStack()
        {
            // The reported exploit: 100 elixirs for 1 MMD. The floor is a per-UNIT minimum.
            Assert.AreEqual(100u, Vendor.CalcSellCost(SundriesRate, 0, ItemType.Misc, 100));
            Assert.AreEqual(25u, Vendor.CalcSellCost(SundriesRate, 0, ItemType.Misc, 25));
            Assert.AreEqual(10u, Vendor.CalcSellCost(SundriesRate, 0, ItemType.Misc, 10));
        }

        [TestMethod]
        public void FractionalUnitPrice_RoundsUpOncePerUnit_NotOncePerStack()
        {
            // 43901 Promissory Note, Value 1, at rate 1.25: 2 MMD each, so 1000 of them cost 2000.
            // Pricing the aggregate Value gave ceil(1.25 * 1000 - 0.1) = 1250 - a 37.5% bulk discount
            // that no listed price ever advertised.
            Assert.AreEqual(2000u, Vendor.CalcSellCost(SundriesRate, 1, ItemType.Misc, 1000));
            Assert.AreEqual(4u, Vendor.CalcSellCost(SundriesRate, 1, ItemType.Misc, 2));
        }

        [TestMethod]
        public void IntegerUnitPrice_ScalesLinearly()
        {
            Assert.AreEqual(500u, Vendor.CalcSellCost(SundriesRate, 4, ItemType.Misc, 100));
            Assert.AreEqual(300u, Vendor.CalcSellCost(1.0, 3, ItemType.Misc, 100));
        }

        [TestMethod]
        public void PromissoryNoteItemType_StillOverridesTheRate_AndScales()
        {
            // ItemType.PromissoryNote forces rate 1.15 regardless of the vendor's SellPrice. 43901 is
            // ItemType.Misc and does NOT take this path; something that does must still scale per unit.
            Assert.AreEqual(115u, Vendor.CalcSellCost(50.0, 100, ItemType.PromissoryNote, 1));
            Assert.AreEqual(1150u, Vendor.CalcSellCost(50.0, 100, ItemType.PromissoryNote, 10));
        }

        [TestMethod]
        public void StackSizeBelowOne_IsTreatedAsOne()
        {
            Assert.AreEqual(2u, Vendor.CalcSellCost(SundriesRate, 1, ItemType.Misc, 0));
            Assert.AreEqual(2u, Vendor.CalcSellCost(SundriesRate, 1, ItemType.Misc, -5));
        }

        [TestMethod]
        public void HugeStack_ClampsInsteadOfWrapping()
        {
            Assert.AreEqual(uint.MaxValue, Vendor.CalcSellCost(1.0, int.MaxValue, ItemType.Misc, int.MaxValue));
        }

        [TestMethod]
        public void StackUnitValue_IsUsedWhenSet()
        {
            // A stack of 100 whose Value SetStackSize rewrote to 7 * 100.
            Assert.AreEqual(7, Vendor.GetStackUnitValue(7, 700, 100));
        }

        [TestMethod]
        public void MissingStackUnitValue_DividesTheAggregateValueBackOut()
        {
            // Stackable weenies with no INT 15 row (1002826, 1002828) leave StackUnitValue null while
            // SetStackSize has still rewritten Value. Treating the stack's Value as one unit's would
            // multiply it by the stack size a second time.
            Assert.AreEqual(0, Vendor.GetStackUnitValue(null, 0, 100));
            Assert.AreEqual(7, Vendor.GetStackUnitValue(null, 700, 100));
        }

        [TestMethod]
        public void MissingStackUnitValue_OnASingleItem_IsThatItemsValue()
        {
            Assert.AreEqual(250, Vendor.GetStackUnitValue(null, 250, 1));
            Assert.IsNull(Vendor.GetStackUnitValue(null, null, 1));
        }
    }
}

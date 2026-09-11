using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MarketEquipmentSet.Classify/Label are pure and take a primitive, so these tests never need a
    /// WorldObject or a Weenie - they exercise the classifier directly.
    /// </summary>
    [TestClass]
    public class MarketEquipmentSetTests
    {
        // ---- spacing correctness ----

        [TestMethod]
        public void Classify_Defenders_SingleWordName_ReturnsUnspaced()
            => Assert.AreEqual("Defenders", MarketEquipmentSet.Classify((int)EquipmentSet.Defenders));

        [TestMethod]
        public void Classify_NobleRelic_MultiWordName_ReturnsSpaced()
            => Assert.AreEqual("Noble Relic", MarketEquipmentSet.Classify((int)EquipmentSet.NobleRelic));

        [TestMethod]
        public void Classify_CarraidasBenediction_MultiWordName_ReturnsSpaced()
            => Assert.AreEqual("Carraidas Benediction", MarketEquipmentSet.Classify((int)EquipmentSet.CarraidasBenediction));

        [TestMethod]
        public void Classify_AetheriaDestruction_MultiWordName_ReturnsSpaced()
            => Assert.AreEqual("Aetheria Destruction", MarketEquipmentSet.Classify((int)EquipmentSet.AetheriaDestruction));

        // ---- junk ids ----

        [TestMethod]
        public void Classify_Invalid_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Classify((int)EquipmentSet.Invalid));

        [TestMethod]
        public void Classify_Test_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Classify((int)EquipmentSet.Test));

        [TestMethod]
        public void Classify_Test2_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Classify((int)EquipmentSet.Test2));

        [TestMethod]
        public void Classify_Unknown3_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Classify((int)EquipmentSet.Unknown3));

        // ---- null and out-of-range ----

        [TestMethod]
        public void Classify_Null_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Classify(null));

        [TestMethod]
        public void Classify_IdOutsideDefinedRange_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Classify(99999));

        // ---- label overrides ----

        [TestMethod]
        public void Label_OlthoiArmorDRed_ReturnsDamageReductionOverride()
            => Assert.AreEqual("Olthoi Armor (Damage Reduction)", MarketEquipmentSet.Label((int)EquipmentSet.OlthoiArmorDRed));

        [TestMethod]
        public void Label_OlthoiArmorDRat_ReturnsDamageRatingOverride()
            => Assert.AreEqual("Olthoi Armor (Damage Rating)", MarketEquipmentSet.Label((int)EquipmentSet.OlthoiArmorDRat));

        [TestMethod]
        public void Label_OlthoiArmorCRed_ReturnsCriticalReductionOverride()
            => Assert.AreEqual("Olthoi Armor (Critical Reduction)", MarketEquipmentSet.Label((int)EquipmentSet.OlthoiArmorCRed));

        [TestMethod]
        public void Label_OlthoiArmorCRat_ReturnsCriticalRatingOverride()
            => Assert.AreEqual("Olthoi Armor (Critical Rating)", MarketEquipmentSet.Label((int)EquipmentSet.OlthoiArmorCRat));

        [TestMethod]
        public void Label_NinjaNew_ReturnsShoujenShozokuOverride()
            => Assert.AreEqual("Shou-jen Shozoku", MarketEquipmentSet.Label((int)EquipmentSet.Ninja_New));

        [TestMethod]
        public void Label_NoOverride_ReturnsTheSpacedToken()
            => Assert.AreEqual("Noble Relic", MarketEquipmentSet.Label((int)EquipmentSet.NobleRelic));

        [TestMethod]
        public void Label_Null_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Label(null));

        [TestMethod]
        public void Label_JunkId_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Label((int)EquipmentSet.Invalid));
    }
}

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

        /// <summary>
        /// Ninja_New's "Shou-jen Shozoku" comes from the EquipmentSetNames catalog now, not a Label
        /// switch override - the catalog lookup runs before the switch, so a case arm for it would be
        /// unreachable.
        /// </summary>
        [TestMethod]
        public void Label_NinjaNew_ReturnsShoujenShozokuFromCatalog()
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

        [TestMethod]
        public void Label_Test_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Label((int)EquipmentSet.Test));

        [TestMethod]
        public void Label_Test2_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Label((int)EquipmentSet.Test2));

        [TestMethod]
        public void Label_Unknown3_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Label((int)EquipmentSet.Unknown3));

        [TestMethod]
        public void Label_IdOutsideDefinedRange_ReturnsNull()
            => Assert.IsNull(MarketEquipmentSet.Label(9999));

        // ---- retail catalog (EquipmentSetNames) ----

        /// <summary>
        /// Label for Defenders (id 16) must return the retail name extracted from acclient.exe
        /// ("Defender's" - see EquipmentSetNames' remarks), not the spaced enum token ("Defenders"),
        /// which Classify still returns unchanged.
        /// </summary>
        [TestMethod]
        public void Label_Defenders_ReturnsTheRetailCatalogName()
        {
            Assert.AreEqual("Defenders", MarketEquipmentSet.Classify((int)EquipmentSet.Defenders));
            Assert.AreEqual("Defender's", MarketEquipmentSet.Label((int)EquipmentSet.Defenders));
        }

        /// <summary>
        /// Pins the catalog's size and a handful of sample rows against the acclient.exe probe output
        /// (plus the ace_world DB corroboration for the six rows added 2026-09-12) this catalog was
        /// generated from, so a future edit that silently drops or corrupts entries is caught here
        /// rather than only in a diff review.
        /// </summary>
        [TestMethod]
        public void EquipmentSetNames_CountAndSampleEntries_MatchTheExtractedCatalog()
        {
            Assert.AreEqual(112, EquipmentSetNames.Names.Count);

            Assert.AreEqual("Defender's", EquipmentSetNames.Names[EquipmentSet.Defenders]);
            Assert.AreEqual("Noble Relic", EquipmentSetNames.Names[EquipmentSet.NobleRelic]);
            Assert.AreEqual("Weave of Alchemy", EquipmentSetNames.Names[EquipmentSet.CloakAlchemy]);
            Assert.AreEqual("Carraida's Benediction", EquipmentSetNames.Names[EquipmentSet.CarraidasBenediction]);
            Assert.AreEqual("Coat of Perfect Light", EquipmentSetNames.Names[EquipmentSet.ArmorPerfectLight]);
            Assert.AreEqual("Leggings of Perfect Light", EquipmentSetNames.Names[EquipmentSet.ArmorPerfectLight2]);
            Assert.AreEqual("Gladiatorial Clothing", EquipmentSetNames.Names[EquipmentSet.ColosseumClothing]);
            Assert.AreEqual("Ceremonial Clothing", EquipmentSetNames.Names[EquipmentSet.GraveyardClothing]);
            Assert.AreEqual("Protective Clothing", EquipmentSetNames.Names[EquipmentSet.OlthoiClothing]);
        }

        /// <summary>
        /// EquipmentSet ids with no candidate string (or no retail evidence - see EquipmentSetNames'
        /// remarks for SocietyArmor/NoobieArmor) must keep today's Label fallback rather than silently
        /// returning null or a guessed name.
        /// </summary>
        [TestMethod]
        public void Label_IdsWithNoCatalogEntry_FallBackToOverrideOrSpacedToken()
        {
            Assert.IsFalse(EquipmentSetNames.Names.ContainsKey(EquipmentSet.SocietyArmor));
            Assert.IsFalse(EquipmentSetNames.Names.ContainsKey(EquipmentSet.NoobieArmor));
            Assert.IsFalse(EquipmentSetNames.Names.ContainsKey(EquipmentSet.OlthoiArmorDRed));

            // OlthoiArmorDRed still gets its pre-existing override, unaffected by the catalog.
            Assert.AreEqual("Olthoi Armor (Damage Reduction)", MarketEquipmentSet.Label((int)EquipmentSet.OlthoiArmorDRed));

            // SocietyArmor has neither a catalog entry nor an override, so it falls all the way back
            // to the spaced token.
            Assert.AreEqual("Society Armor", MarketEquipmentSet.Label((int)EquipmentSet.SocietyArmor));
        }
    }
}

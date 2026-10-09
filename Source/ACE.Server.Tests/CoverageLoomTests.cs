using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.RefireStations;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CoverageLoomStation's pure coverage-mask helpers (IsUndergarment, HasFullCoverage, UpgradeCoverage,
    /// UpgradeValidLocations, Describe). A Player/WorldObject cannot be constructed in a test in this repo (see
    /// SpellRerollTests' remarks), so VerifyEligible/Apply are not exercised here - only the pure math the
    /// station's HandleGive delegates to.
    ///
    /// Retail coverage values used throughout (verified against local ace_world, weenie_properties_int type 4
    /// ClothingPriority / type 9 ValidLocations, for type=2 items with a nonzero underwear-arm-or-leg bit and
    /// no outerwear bit): shirts - Jerkin/Doublet/Vest family coverage 8 (chest only, ValidLocations 6), Tunic
    /// family coverage 40 (chest+upper arms, ValidLocations 14), Shirt family coverage 104 (chest+upper+lower
    /// arms, FULL, ValidLocations 30), Kimono Top coverage 120 (chest+abdomen+upper+lower arms, FULL,
    /// ValidLocations 30); pants - Olthoi shorts coverage 3 (Unknown+upper legs, ValidLocations 68), Breeches
    /// family coverage 19 (Unknown+girth+upper legs, ValidLocations 68), Pants family coverage 22 (girth+upper
    /// +lower legs, FULL, ValidLocations 196); Toga coverage 26 (girth+chest+upper legs); Asheron's Raiment
    /// coverage 126 (everything, FULL).
    /// </summary>
    [TestClass]
    public class CoverageLoomTests
    {
        // ---------------- IsUndergarment ----------------

        [TestMethod]
        public void IsUndergarment_NullCoverage_IsFalse()
        {
            Assert.IsFalse(CoverageLoomStation.IsUndergarment(null));
        }

        [TestMethod]
        public void IsUndergarment_ZeroCoverage_IsFalse()
        {
            Assert.IsFalse(CoverageLoomStation.IsUndergarment((CoverageMask)0));
        }

        [DataTestMethod]
        [DataRow(8, DisplayName = "Jerkin (chest only)")]
        [DataRow(40, DisplayName = "Tunic (chest+upper arms)")]
        [DataRow(104, DisplayName = "Shirt (full)")]
        [DataRow(120, DisplayName = "Kimono Top (chest+abdomen+upper+lower arms, full)")]
        [DataRow(3, DisplayName = "Olthoi shorts")]
        [DataRow(19, DisplayName = "Breeches")]
        [DataRow(22, DisplayName = "Pants (full)")]
        [DataRow(26, DisplayName = "Toga")]
        [DataRow(126, DisplayName = "Asheron's Raiment (full)")]
        public void IsUndergarment_RetailUndergarments_AreTrue(int coverage)
        {
            var mask = (CoverageMask)coverage;

            Assert.IsTrue(CoverageLoomStation.IsUndergarment(mask));
        }

        [TestMethod]
        public void IsUndergarment_OuterwearMask_IsFalse()
        {
            // robe-like: OuterwearAbdomen (0x800) | OuterwearChest (0x400)
            var mask = (CoverageMask)0x400 | (CoverageMask)0x800;

            Assert.IsFalse(CoverageLoomStation.IsUndergarment(mask));
        }

        [TestMethod]
        public void IsUndergarment_HeadMask_IsFalse()
        {
            Assert.IsFalse(CoverageLoomStation.IsUndergarment(CoverageMask.Head));
        }

        [TestMethod]
        public void IsUndergarment_AbdomenAlone_IsFalse()
        {
            // abdomen alone touches neither the shirt half nor the pants-legs half
            Assert.IsFalse(CoverageLoomStation.IsUndergarment(CoverageMask.UnderwearAbdomen));
        }

        // ---------------- HasFullCoverage ----------------

        [DataTestMethod]
        [DataRow(104, DisplayName = "Shirt (full)")]
        [DataRow(120, DisplayName = "Kimono Top (full)")]
        [DataRow(22, DisplayName = "Pants (full)")]
        [DataRow(126, DisplayName = "Asheron's Raiment (full)")]
        public void HasFullCoverage_FullRetailGarments_AreTrue(int coverage)
        {
            var mask = (CoverageMask)coverage;

            Assert.IsTrue(CoverageLoomStation.HasFullCoverage(mask));
        }

        [DataTestMethod]
        [DataRow(8, DisplayName = "Jerkin (partial)")]
        [DataRow(40, DisplayName = "Tunic (partial)")]
        [DataRow(3, DisplayName = "Olthoi shorts (partial)")]
        [DataRow(19, DisplayName = "Breeches (partial)")]
        [DataRow(26, DisplayName = "Toga (partial)")]
        public void HasFullCoverage_PartialRetailGarments_AreFalse(int coverage)
        {
            var mask = (CoverageMask)coverage;

            Assert.IsFalse(CoverageLoomStation.HasFullCoverage(mask));
        }

        // ---------------- UpgradeCoverage ----------------

        [TestMethod]
        public void UpgradeCoverage_Jerkin_BecomesFullShirt()
        {
            var upgraded = CoverageLoomStation.UpgradeCoverage((CoverageMask)8);
            var shirtFull = CoverageLoomStation.ShirtFull;

            Assert.AreEqual(shirtFull, upgraded);
        }

        [TestMethod]
        public void UpgradeCoverage_Tunic_BecomesFullShirt()
        {
            var upgraded = CoverageLoomStation.UpgradeCoverage((CoverageMask)40);
            var shirtFull = CoverageLoomStation.ShirtFull;

            Assert.AreEqual(shirtFull, upgraded);
        }

        [TestMethod]
        public void UpgradeCoverage_Breeches_BecomesFullPants()
        {
            var upgraded = CoverageLoomStation.UpgradeCoverage((CoverageMask)19);
            var pantsFull = CoverageLoomStation.PantsFull;

            Assert.AreEqual(pantsFull, upgraded);
        }

        [TestMethod]
        public void UpgradeCoverage_OlthoiShorts_BecomesFullPants()
        {
            var upgraded = CoverageLoomStation.UpgradeCoverage((CoverageMask)3);
            var pantsFull = CoverageLoomStation.PantsFull;

            Assert.AreEqual(pantsFull, upgraded);
        }

        [TestMethod]
        public void UpgradeCoverage_Toga_BecomesFullRaiment()
        {
            var upgraded = CoverageLoomStation.UpgradeCoverage((CoverageMask)26);
            var raimentFull = (CoverageMask)126;

            Assert.AreEqual(raimentFull, upgraded);
        }

        // ---------------- UpgradeValidLocations ----------------

        [TestMethod]
        public void UpgradeValidLocations_Jerkin_Becomes30()
        {
            var upgraded = CoverageLoomStation.UpgradeValidLocations((CoverageMask)8, (EquipMask)6);
            var expected = (EquipMask)30;

            Assert.AreEqual(expected, upgraded);
        }

        [TestMethod]
        public void UpgradeValidLocations_Tunic_Becomes30()
        {
            var upgraded = CoverageLoomStation.UpgradeValidLocations((CoverageMask)40, (EquipMask)14);
            var expected = (EquipMask)30;

            Assert.AreEqual(expected, upgraded);
        }

        [TestMethod]
        public void UpgradeValidLocations_Breeches_Becomes196()
        {
            var upgraded = CoverageLoomStation.UpgradeValidLocations((CoverageMask)19, (EquipMask)68);
            var expected = (EquipMask)196;

            Assert.AreEqual(expected, upgraded);
        }

        [TestMethod]
        public void UpgradeValidLocations_OlthoiShorts_Becomes196()
        {
            var upgraded = CoverageLoomStation.UpgradeValidLocations((CoverageMask)3, (EquipMask)68);
            var expected = (EquipMask)196;

            Assert.AreEqual(expected, upgraded);
        }

        // ---------------- Describe ----------------

        [TestMethod]
        public void Describe_Tunic_ReadsChestUpperArms()
        {
            var description = CoverageLoomStation.Describe((CoverageMask)40);

            Assert.AreEqual("chest, upper arms", description);
        }

        [TestMethod]
        public void Describe_Pants_ReadsGirthUpperLowerLegs()
        {
            var description = CoverageLoomStation.Describe((CoverageMask)22);

            Assert.AreEqual("girth, upper legs, lower legs", description);
        }

        [TestMethod]
        public void Describe_Empty_ReadsNone()
        {
            var description = CoverageLoomStation.Describe((CoverageMask)0);

            Assert.AreEqual("none", description);
        }

        // ---------------- VisualSiblings / TryGetVisualSibling (visual follow-up, 2026-08-18) ----------------

        [TestMethod]
        public void VisualSiblings_HasExactlyTwelveRows()
        {
            Assert.AreEqual(12, CoverageLoomStation.VisualSiblings.Count);
        }

        [TestMethod]
        public void VisualSiblings_NoSelfMaps_EveryTargetDiffersFromSource()
        {
            foreach (var kvp in CoverageLoomStation.VisualSiblings)
            {
                Assert.AreNotEqual(kvp.Key, kvp.Value.ClothingBase, $"0x{kvp.Key:X8} maps to itself");
            }
        }

        [TestMethod]
        public void TryGetVisualSibling_NullClothingBase_IsFalse()
        {
            var found = CoverageLoomStation.TryGetVisualSibling(null, hasOwnVisualRows: false, out _);

            Assert.IsFalse(found);
        }

        [TestMethod]
        public void TryGetVisualSibling_Tunic_MapsToShirt()
        {
            var found = CoverageLoomStation.TryGetVisualSibling(0x10000003, hasOwnVisualRows: false, out var sibling);

            Assert.IsTrue(found);
            Assert.AreEqual((uint)0x10000001, sibling.ClothingBase);
            Assert.AreEqual((uint)0x020000D4, sibling.Setup);
        }

        [TestMethod]
        public void TryGetVisualSibling_Breeches_MapsToPants()
        {
            var found = CoverageLoomStation.TryGetVisualSibling(0x1000001B, hasOwnVisualRows: false, out var sibling);

            Assert.IsTrue(found);
            Assert.AreEqual((uint)0x10000002, sibling.ClothingBase);
            Assert.AreEqual((uint)0x020000DD, sibling.Setup);
        }

        [TestMethod]
        public void TryGetVisualSibling_ViamontianLeggings_IsFalse()
        {
            // 0x100005AE (Viamontian Leggings, 31238) is deliberately NOT in the table - the only full
            // Viamontian pants uses a different palette set (spec section 1, "NOT mappable").
            var found = CoverageLoomStation.TryGetVisualSibling(0x100005AE, hasOwnVisualRows: false, out _);

            Assert.IsFalse(found);
        }

        [TestMethod]
        public void TryGetVisualSibling_MappedClothingBaseWithOwnVisualRows_IsFalse()
        {
            var found = CoverageLoomStation.TryGetVisualSibling(0x10000003, hasOwnVisualRows: true, out _);

            Assert.IsFalse(found);
        }

        // ---------------- HasOwnVisualRows (pure overload) ----------------

        [TestMethod]
        public void HasOwnVisualRows_AllZero_IsFalse()
        {
            Assert.IsFalse(CoverageLoomStation.HasOwnVisualRows(animPartCount: 0, textureCount: 0, paletteCount: 0));
        }

        [TestMethod]
        public void HasOwnVisualRows_TextureOnly_IsTrue()
        {
            Assert.IsTrue(CoverageLoomStation.HasOwnVisualRows(animPartCount: 0, textureCount: 1, paletteCount: 0));
        }

        [TestMethod]
        public void HasOwnVisualRows_PaletteOnly_IsTrue()
        {
            Assert.IsTrue(CoverageLoomStation.HasOwnVisualRows(animPartCount: 0, textureCount: 0, paletteCount: 1));
        }

        [TestMethod]
        public void HasOwnVisualRows_AnimPartOnly_IsTrue()
        {
            // WorldObject.CalculateObjDesc's early-return gate (WorldObject_Networking.cs:945-954) treats
            // AnimPart as an equal third member of the same triple - an AnimPart-only fork garment renders its
            // own raw part rows too, same as texture/palette-only, so this must also be TRUE.
            Assert.IsTrue(CoverageLoomStation.HasOwnVisualRows(animPartCount: 1, textureCount: 0, paletteCount: 0));
        }

        // ---------------- VisualClause (prompt/message text builder) ----------------

        [TestMethod]
        public void VisualClause_HasSibling_NamesTheSibling()
        {
            var clause = CoverageLoomStation.VisualClause(hasSibling: true, siblingName: "Shirt");

            Assert.AreEqual(" The look becomes the full-coverage Shirt.", clause);
        }

        [TestMethod]
        public void VisualClause_NoSibling_SaysLookStaysAsIs()
        {
            var clause = CoverageLoomStation.VisualClause(hasSibling: false, siblingName: null);

            Assert.AreEqual(" The look stays as it is - only the coverage changes.", clause);
        }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure unit tests for VaultDisplayOrder, the classifier backing PersonalVendor.forEachItem's
    /// default display sort (see PersonalVendorTests.cs's "forEachItem default display sort" region
    /// for the end-to-end sort tests that exercise these classifiers through a real vendor). Every
    /// member under test takes only primitives, so none of these need a WorldObject, a live Player, or
    /// client dat files.
    /// </summary>
    [TestClass]
    public class VaultDisplayOrderTests
    {
        #region CategoryOrdinal

        [TestMethod]
        public void CategoryOrdinal_CollapsesWeaponFamilyToOne()
        {
            Assert.AreEqual(1u, VaultDisplayOrder.CategoryOrdinal(ItemType.MeleeWeapon));
            Assert.AreEqual(1u, VaultDisplayOrder.CategoryOrdinal(ItemType.MissileWeapon));
            Assert.AreEqual(1u, VaultDisplayOrder.CategoryOrdinal(ItemType.Caster));
        }

        [TestMethod]
        public void CategoryOrdinal_CollapsesEquipmentFamilyToTwo()
        {
            Assert.AreEqual(2u, VaultDisplayOrder.CategoryOrdinal(ItemType.Armor));
            Assert.AreEqual(2u, VaultDisplayOrder.CategoryOrdinal(ItemType.Clothing));
            Assert.AreEqual(2u, VaultDisplayOrder.CategoryOrdinal(ItemType.Jewelry));
        }

        [TestMethod]
        public void CategoryOrdinal_EverythingElseKeepsItsOwnValue()
        {
            Assert.AreEqual((uint)ItemType.Food, VaultDisplayOrder.CategoryOrdinal(ItemType.Food));
            Assert.AreEqual((uint)ItemType.Money, VaultDisplayOrder.CategoryOrdinal(ItemType.Money));
        }

        /// <summary>
        /// A multi-bit ItemType carrying both a weapon bit and an equipment bit (e.g. the real
        /// ace_world weenies at ItemType 63 = MeleeWeapon|Armor|Clothing|Jewelry|Creature|Food, see
        /// PersonalVendor.PanelRenderableItemTypes' doc comment) must resolve to the weapon bucket -
        /// the weapon mask is tested first, first match wins.
        /// </summary>
        [TestMethod]
        public void CategoryOrdinal_MultiBitItemTypeHitsWeaponFamilyFirst()
        {
            var multiBit = ItemType.MeleeWeapon | ItemType.Armor | ItemType.Clothing | ItemType.Jewelry | ItemType.Creature | ItemType.Food;

            Assert.AreEqual(1u, VaultDisplayOrder.CategoryOrdinal(multiBit));
        }

        /// <summary>
        /// Gameboard is 0x80000000 - casting ItemType to int instead of uint would wrap this to
        /// int.MinValue and sort it FIRST instead of last. This is the same trap
        /// PersonalVendor.forEachItem's original ItemType rung already had to avoid (see
        /// ForEachItem_OrdersByTheFullCompositeSortKey's Gameboard case).
        /// </summary>
        [TestMethod]
        public void CategoryOrdinal_GameboardSortsLastNotFirst()
        {
            var ordinal = VaultDisplayOrder.CategoryOrdinal(ItemType.Gameboard);

            Assert.AreEqual((uint)ItemType.Gameboard, ordinal);
            Assert.IsTrue(ordinal > VaultDisplayOrder.CategoryOrdinal(ItemType.TinkeringMaterial), "Gameboard's ordinal must be the largest, not int.MinValue wrapped from a signed cast");
        }

        #endregion

        #region SlotOrdinal

        /// <summary>
        /// A robe-shaped mask covering both chest and abdomen must sort by the head-down PRIMARY
        /// (highest, "chest") slot, not by any other token Classify also returns for it.
        /// </summary>
        [TestMethod]
        public void SlotOrdinal_MultiSlotRobeUsesHeadDownPrimarySlot()
        {
            var robeMask = (int)(EquipMask.ChestArmor | EquipMask.AbdomenArmor);

            var chestIndex = VaultDisplayOrder.SlotOrdinal((int)EquipMask.ChestArmor);
            var robeIndex = VaultDisplayOrder.SlotOrdinal(robeMask);

            Assert.AreEqual(chestIndex, robeIndex, "a chest+abdomen mask must resolve to the same ordinal as chest alone - chest is the head-down primary slot");
        }

        [TestMethod]
        public void SlotOrdinal_NullValidLocationsSortsLast()
        {
            Assert.AreEqual(int.MaxValue, VaultDisplayOrder.SlotOrdinal(null));
        }

        /// <summary>A mask matching no offered slot (e.g. a pure weapon bit) resolves to an empty token list.</summary>
        [TestMethod]
        public void SlotOrdinal_UnclassifiableMaskSortsLast()
        {
            Assert.AreEqual(int.MaxValue, VaultDisplayOrder.SlotOrdinal((int)EquipMask.MeleeWeapon));
        }

        #endregion

        #region WeaponClassOrdinal

        [TestMethod]
        public void WeaponClassOrdinal_HeavyBeforeLightBeforeFinesseBeforeTwoHanded()
        {
            var heavy = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MeleeWeapon, (int)Skill.HeavyWeapons, null, null);
            var light = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MeleeWeapon, (int)Skill.LightWeapons, null, null);
            var finesse = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MeleeWeapon, (int)Skill.FinesseWeapons, null, null);
            var twoHanded = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MeleeWeapon, (int)Skill.TwoHandedCombat, null, null);

            Assert.IsTrue(heavy < light);
            Assert.IsTrue(light < finesse);
            Assert.IsTrue(finesse < twoHanded);
        }

        [TestMethod]
        public void WeaponClassOrdinal_ResolvesBowCrossbowAtlatlFromMissileLauncherAmmoType()
        {
            var bow = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MissileLauncher, null, (int)AmmoType.Arrow, null);
            var crossbow = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MissileLauncher, null, (int)AmmoType.Bolt, null);
            var atlatl = VaultDisplayOrder.WeaponClassOrdinal(WeenieType.MissileLauncher, null, (int)AmmoType.Atlatl, null);

            Assert.AreNotEqual(int.MaxValue, bow);
            Assert.AreNotEqual(int.MaxValue, crossbow);
            Assert.AreNotEqual(int.MaxValue, atlatl);
            Assert.IsTrue(bow < crossbow, "bow must sort before crossbow in the display order");
            Assert.IsTrue(crossbow < atlatl, "crossbow must sort before atlatl in the display order");
        }

        [TestMethod]
        public void WeaponClassOrdinal_UnknownTokenSortsLast()
        {
            // Generic WeenieType, no weapon skill/ammo/type data at all: MarketWeaponClass.Classify
            // returns null, which must map to int.MaxValue.
            Assert.AreEqual(int.MaxValue, VaultDisplayOrder.WeaponClassOrdinal(WeenieType.Generic, null, null, null));
        }

        #endregion

        #region MinLevelRequirement / MinSkillRequirement

        [TestMethod]
        public void MinLevelRequirement_FoundInSlotTwoRatherThanSlotOne()
        {
            // Slot 1 occupied by something other than Level (e.g. an Attrib requirement); slot 2 carries
            // the Level requirement - the covenant/olthoi armor shape SetWieldLevelReq produces.
            var result = VaultDisplayOrder.MinLevelRequirement(
                ((int)WieldRequirement.Attrib, 50),
                ((int)WieldRequirement.Level, 100),
                (null, null),
                (null, null));

            Assert.AreEqual(100, result);
        }

        [TestMethod]
        public void MinLevelRequirement_TakesMaximumAcrossSlots()
        {
            var result = VaultDisplayOrder.MinLevelRequirement(
                ((int)WieldRequirement.Level, 50),
                ((int)WieldRequirement.Level, 150),
                ((int)WieldRequirement.Level, 75),
                (null, null));

            Assert.AreEqual(150, result);
        }

        [TestMethod]
        public void MinLevelRequirement_NullWhenNoSlotHasLevel()
        {
            var result = VaultDisplayOrder.MinLevelRequirement(
                ((int)WieldRequirement.Attrib, 50),
                ((int)WieldRequirement.Skill, 100),
                (null, null),
                (null, null));

            Assert.IsNull(result);
        }

        [TestMethod]
        public void MinSkillRequirement_FoundInSlotTwoRatherThanSlotOne()
        {
            var result = VaultDisplayOrder.MinSkillRequirement(
                ((int)WieldRequirement.Level, 50),
                ((int)WieldRequirement.Skill, 200),
                (null, null),
                (null, null));

            Assert.AreEqual(200, result);
        }

        [TestMethod]
        public void MinSkillRequirement_TreatsSkillAndRawSkillTheSameAndTakesMaximum()
        {
            var result = VaultDisplayOrder.MinSkillRequirement(
                ((int)WieldRequirement.Skill, 100),
                ((int)WieldRequirement.RawSkill, 250),
                (null, null),
                (null, null));

            Assert.AreEqual(250, result);
        }

        [TestMethod]
        public void MinSkillRequirement_NullWhenNoSlotHasSkillOrRawSkill()
        {
            var result = VaultDisplayOrder.MinSkillRequirement(
                ((int)WieldRequirement.Level, 50),
                ((int)WieldRequirement.Attrib, 100),
                (null, null),
                (null, null));

            Assert.IsNull(result);
        }

        #endregion
    }
}

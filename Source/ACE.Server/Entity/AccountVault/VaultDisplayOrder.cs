using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Classifiers backing PersonalVendor.forEachItem's default display sort (see that method's doc
    /// comment for the full composite key). Pure and static - every member takes only the primitives
    /// a WorldObject property lookup already returns (no WorldObject/Weenie dependency), so tests can
    /// exercise every rung without constructing a live vendor or a real biota.
    ///
    /// Slot and weapon-class classification are NOT reimplemented here: <see cref="SlotOrdinal"/> and
    /// <see cref="WeaponClassOrdinal"/> both delegate to the market snapshot classifiers
    /// (MarketEquipSlots.Classify / MarketWeaponClass.Classify) that already own those token sets as a
    /// contract with the web app, and only add the head-down / heavy-first ORDERING on top of the
    /// token each one returns.
    /// </summary>
    public static class VaultDisplayOrder
    {
        /// <summary>
        /// The weapon ItemTypes collapsed into one display category (ordinal 1) so melee, missile, and
        /// caster weapons cluster together ahead of the more granular WeaponClassOrdinal rung that
        /// separates them again within the category.
        /// </summary>
        private const ItemType WeaponFamily = ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster;

        /// <summary>
        /// The equipment ItemTypes collapsed into one display category (ordinal 2) so armor, clothing,
        /// and jewelry cluster together ahead of the SlotOrdinal rung that separates them by body slot.
        /// </summary>
        private const ItemType EquipmentFamily = ItemType.Armor | ItemType.Clothing | ItemType.Jewelry;

        /// <summary>
        /// The category ordinal <see cref="CategoryOrdinal"/> returns for the weapon family. Named,
        /// and public, because PersonalVendor.forEachItem's rungs 2-4 must ask "is this row in the
        /// weapon category" and a bare literal 1 at those call sites would silently stop matching if
        /// this type ever renumbered its categories - rungs 2-4 would then be applied to the wrong
        /// items with no compile error and no test failure.
        /// </summary>
        public const uint WeaponCategory = 1;

        /// <summary>
        /// The category ordinal <see cref="CategoryOrdinal"/> returns for the equipment family. Same
        /// reasoning as <see cref="WeaponCategory"/>.
        /// </summary>
        public const uint EquipmentCategory = 2;

        /// <summary>
        /// Collapses the weapon family (MeleeWeapon, MissileWeapon, Caster) to 1 and the equipment
        /// family (Armor, Clothing, Jewelry) to 2; every other ItemType keeps its own value. ItemType
        /// is [Flags] and a real weenie can carry several bits at once (e.g. ItemType 63 =
        /// MeleeWeapon|Armor|Clothing|Jewelry|Creature|Food - see PersonalVendor.PanelRenderableItemTypes'
        /// doc comment), so the weapon mask is tested first, then the equipment mask, and only an item
        /// with NEITHER bit falls through to its raw value - first match wins, exactly like
        /// MarketWeaponClass.Classify's rule ordering.
        ///
        /// Returns uint, not int: ItemType's underlying type is uint and Gameboard is 0x80000000. An
        /// int cast would wrap that to int.MinValue and sort it FIRST instead of last, which is the
        /// same trap PersonalVendor.forEachItem's original ItemType rung already had to avoid (see
        /// PersonalVendor.cs's forEachItem doc comment, rung 1).
        /// </summary>
        public static uint CategoryOrdinal(ItemType itemType)
        {
            if ((itemType & WeaponFamily) != 0)
                return WeaponCategory;

            if ((itemType & EquipmentFamily) != 0)
                return EquipmentCategory;

            return (uint)itemType;
        }

        /// <summary>
        /// The canonical head-down slot order shared with MarketEquipSlots.Classify's token contract.
        /// Declared once here as the single source of truth for SlotOrdinal's index lookup.
        /// </summary>
        private static readonly string[] SlotOrder =
        {
            "head", "chest", "abdomen", "upper_arm", "lower_arm", "hands",
            "upper_leg", "lower_leg", "feet", "neck", "wrist", "finger",
            "trinket", "cloak", "shield",
        };

        /// <summary>
        /// The display-order index of the FIRST (head-down) slot MarketEquipSlots.Classify reports for
        /// this ValidLocations mask - a multi-slot item like a robe (chest + abdomen) sorts by its
        /// highest, "chest" slot. Returns int.MaxValue when Classify returns null (unset) or an empty
        /// list (a mask that matches no offered slot), so an unclassifiable equipment-category item
        /// sorts last within its category rather than first.
        /// </summary>
        public static int SlotOrdinal(int? validLocations)
        {
            var tokens = MarketEquipSlots.Classify(validLocations);

            if (tokens == null || tokens.Count == 0)
                return int.MaxValue;

            var index = Array.IndexOf(SlotOrder, tokens[0]);

            return index >= 0 ? index : int.MaxValue;
        }

        /// <summary>
        /// Display order for MarketWeaponClass.Classify's token set: the modern combat styles first
        /// (heavy/light/finesse/two_handed/unarmed), then the retired per-weapon-type skills, then the
        /// missile families, then ammunition and casters last. This is a DIFFERENT order from
        /// MarketWeaponClass's own token list - that class orders tokens by classification rule, this
        /// one orders them for display - so the mapping is declared once here rather than reused from
        /// there.
        /// </summary>
        private static readonly string[] WeaponClassOrder =
        {
            "heavy", "light", "finesse", "two_handed", "unarmed",
            "sword", "axe", "mace", "spear", "dagger", "staff",
            "bow", "crossbow", "atlatl", "thrown", "ammunition", "caster",
        };

        /// <summary>
        /// The display-order index of this weapon's MarketWeaponClass.Classify token. Returns
        /// int.MaxValue for null (not a weapon, or unresolved) or any token this class does not
        /// recognize, so such an item sorts last within its category.
        /// </summary>
        public static int WeaponClassOrdinal(WeenieType weenieType, int? weaponSkill, int? ammoType, int? weaponType)
        {
            var token = MarketWeaponClass.Classify(weenieType, weaponSkill, ammoType, weaponType);

            if (token == null)
                return int.MaxValue;

            var index = Array.IndexOf(WeaponClassOrder, token);

            return index >= 0 ? index : int.MaxValue;
        }

        /// <summary>
        /// The maximum PropertyInt.WieldDifficulty across whichever of the four wield-requirement
        /// slots carries WieldRequirement.Level, or null when none does. All four slots must be
        /// scanned, not just the first: SetWieldLevelReq
        /// (LootGenerationFactory_Clothing.cs:307-333) writes the Level requirement into slot 2
        /// (WieldRequirements2) whenever slot 1 is already occupied - the covenant/olthoi armor case -
        /// so a level requirement can legitimately live in any of the four. This mirrors the four-slot
        /// scan Player_Inventory's own wield-requirement check already does (Player_Inventory.cs around
        /// line 2342).
        /// </summary>
        public static int? MinLevelRequirement(
            (int? requirements, int? difficulty) slot1,
            (int? requirements, int? difficulty) slot2,
            (int? requirements, int? difficulty) slot3,
            (int? requirements, int? difficulty) slot4)
        {
            int? max = null;

            foreach (var slot in new[] { slot1, slot2, slot3, slot4 })
            {
                if (!slot.requirements.HasValue || (WieldRequirement)slot.requirements.Value != WieldRequirement.Level)
                    continue;

                var difficulty = slot.difficulty ?? 0;

                if (!max.HasValue || difficulty > max.Value)
                    max = difficulty;
            }

            return max;
        }

        /// <summary>
        /// The maximum PropertyInt.WieldDifficulty across whichever of the four wield-requirement
        /// slots carries WieldRequirement.Skill or WieldRequirement.RawSkill, or null when none does.
        /// Same four-slot scan requirement as <see cref="MinLevelRequirement"/> - see its doc comment
        /// for why a skill (or level) requirement is not guaranteed to live in slot 1.
        /// </summary>
        public static int? MinSkillRequirement(
            (int? requirements, int? difficulty) slot1,
            (int? requirements, int? difficulty) slot2,
            (int? requirements, int? difficulty) slot3,
            (int? requirements, int? difficulty) slot4)
        {
            int? max = null;

            foreach (var slot in new[] { slot1, slot2, slot3, slot4 })
            {
                if (!slot.requirements.HasValue)
                    continue;

                var requirement = (WieldRequirement)slot.requirements.Value;

                if (requirement != WieldRequirement.Skill && requirement != WieldRequirement.RawSkill)
                    continue;

                var difficulty = slot.difficulty ?? 0;

                if (!max.HasValue || difficulty > max.Value)
                    max = difficulty;
            }

            return max;
        }
    }
}

using System;
using System.Collections.Generic;

namespace ACE.Server.Managers.Market.Suit
{
    /// <summary>
    /// The lean wire shape of one equippable item for the browser-side suit optimizer. A CROSS-REPO
    /// CONTRACT: do not add, rename or drop a field without changing the consumer in the same step.
    /// Serialize with <see cref="MarketSnapshot.JsonOptions"/> (snake_case, nulls omitted), the same
    /// policy every market DTO uses, so <c>ItemGuid</c> is <c>item_guid</c>, <c>WieldReqs</c> is
    /// <c>wield_reqs</c> and so on. Build one only through <see cref="SuitItemProjector"/>.
    /// </summary>
    public sealed record SuitItem
    {
        public const string SourceVault = "vault";
        public const string SourceEquipped = "equipped";

        /// <summary>"vault" or "equipped" today; later phases add more.</summary>
        public string Source { get; init; }

        /// <summary>The stored item's guid; null for a collapsed ledger row, which has no biota.</summary>
        public uint? ItemGuid { get; init; }

        public uint Wcid { get; init; }

        /// <summary>The account_vault_class pool key when this line is a collapsed class row; else null.</summary>
        public string ClassKey { get; init; }

        /// <summary>Units this line stands for: 1 for a stored item, the pool count for a class line.</summary>
        public int Count { get; init; } = 1;

        /// <summary>Whether the item is currently listed on the market. Filled by a later step; always false here.</summary>
        public bool Listed { get; init; }

        public string Name { get; init; }

        /// <summary>The resolved icon (ClothingBase override applied), as MarketSnapshot resolves it.</summary>
        public uint IconId { get; init; }
        public uint? IconOverlayId { get; init; }
        public uint? IconUnderlayId { get; init; }

        /// <summary>ItemType == Clothing (shirts, pants, underclothing), as opposed to Armor.</summary>
        public bool IsClothing { get; init; }

        /// <summary>PropertyInt.ValidLocations as the EquipMask bits, cast to int; 0 when absent.</summary>
        public int ValidLocations { get; init; }

        /// <summary>PropertyInt.ClothingPriority as the CoverageMask bits, cast to int; 0 when absent.</summary>
        public int ClothingPriority { get; init; }

        public int? ArmorLevel { get; init; }

        /// <summary>PropertyInt.EquipmentSetId, raw.</summary>
        public int? EquipmentSetId { get; init; }

        /// <summary>The item's level when it can level (ItemBaseXp, ItemMaxLevel and ItemXpStyle set); else null.</summary>
        public int? ItemLevel { get; init; }

        /// <summary>
        /// Spells that apply to the wearer while the item is equipped: the spellbook EXCLUDING the
        /// proc spell and EXCLUDING the spell DID (a cloak's Surge and a wand's primary spell are not
        /// worn buffs). Ascending by id so the output is stable.
        /// </summary>
        public uint[] WornSpellIds { get; init; } = Array.Empty<uint>();

        public int? ItemCurMana { get; init; }
        public int? ItemMaxMana { get; init; }

        /// <summary>Arcane lore (PropertyInt.ItemDifficulty).</summary>
        public int? ItemDifficulty { get; init; }

        /// <summary>PropertyDataId.ItemSkillLimit, the Skill id the item's level limit applies to.</summary>
        public uint? ItemSkillLimit { get; init; }
        public int? ItemSkillLevelLimit { get; init; }

        /// <summary>One entry per occupied WieldRequirements slot (1-4), in slot order; empty slots omitted.</summary>
        public IReadOnlyList<WieldReq> WieldReqs { get; init; } = Array.Empty<WieldReq>();

        /// <summary>PropertyInt.HeritageSpecificArmor (a HeritageGroup id), raw.</summary>
        public int? HeritageSpecificArmor { get; init; }

        /// <summary>PropertyInstanceId.AllowedWielder.</summary>
        public uint? AllowedWielder { get; init; }

        /// <summary>PropertyInt.EncumbranceVal as stored (the whole stack's burden for a stack).</summary>
        public int? Encumbrance { get; init; }

        /// <summary>
        /// PropertyInt.CurrentWieldedLocation as the EquipMask bits, cast to int: the slot the item is worn in
        /// right now (a left ring and a right ring differ here; ValidLocations names both). Set only for an
        /// "equipped" item; null, and so omitted from the JSON, for a vault, ledger or class line.
        /// </summary>
        public int? CurrentWieldedLocation { get; init; }

        /// <summary>
        /// What each armor reduction tool would turn this item into, one entry per tool that would succeed and
        /// change the item's slots or coverage priority, in main, lower, middle order. Computed by Tailoring.ReductionResult, the same decision the in-game
        /// reduction uses. Null, and so omitted from the JSON, when no tool would succeed (single-slot items,
        /// ineligible items, non-armor) and for a template-weenie projection (no live item to judge).
        /// </summary>
        public IReadOnlyList<SuitReduction> Reductions { get; init; }
    }

    /// <summary>
    /// One reduction a tool can perform: Tool is "main", "lower" or "middle"; ValidLocations and
    /// ClothingPriority are the resulting EquipMask / CoverageMask bits, cast to int like the item's own fields.
    /// </summary>
    public sealed record SuitReduction
    {
        public string Tool { get; init; }
        public int ValidLocations { get; init; }
        public int ClothingPriority { get; init; }
    }

    /// <summary>One occupied wield requirement slot: WieldRequirement kind, the skill or attribute id it names, and the difficulty.</summary>
    public sealed record WieldReq
    {
        public int Kind { get; init; }
        public int SkillOrAttr { get; init; }
        public int Difficulty { get; init; }
    }
}

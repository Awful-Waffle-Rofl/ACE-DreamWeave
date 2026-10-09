using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// THE vault display order, as one reusable ordering over WorldObjects. Two callers share it:
    /// PersonalVendor.forEachItem (the /mule panel it was written for) and ThreadCacheSort (solo Thread
    /// Cache contents, user ruling 2026-09-21: "solo caches get the FULL composite /mule sort").
    ///
    /// It lives here rather than on <see cref="VaultDisplayOrder"/> on purpose. VaultDisplayOrder's own
    /// doc comment states its contract as "pure and static - every member takes only the primitives a
    /// WorldObject property lookup already returns (no WorldObject/Weenie dependency), so tests can
    /// exercise every rung without constructing a live vendor or a real biota". Hanging a WorldObject
    /// -taking method off it would break exactly that. This type is the layer above: it owns the
    /// COMPOSITE (which rung comes first, which direction, what null means), calls VaultDisplayOrder for
    /// the classifiers, and reads the properties off the objects.
    ///
    /// The composite key, read off the display object itself (a mule proxy is already stamped with a
    /// renderable ItemType - see PersonalVendor.NeedsDisplayProxy - so this needs no branch for proxied
    /// vs. direct rows). The category ordinal (rung 1) is computed ONCE per item into a small projection
    /// rather than recomputed in every rung that depends on it:
    ///   1. VaultDisplayOrder.CategoryOrdinal(ItemType) ascending - collapses the weapon family
    ///      (MeleeWeapon, MissileWeapon, Caster) to 1 and the equipment family (Armor, Clothing,
    ///      Jewelry) to 2, everything else keeping its own ItemType value cast to uint (NOT int,
    ///      which would wrap a high bit like Gameboard's 0x80000000 to int.MinValue and sort it
    ///      first instead of last). Clusters rows the same way the client's own tabs do, while
    ///      still separating weapons from armor/clothing/jewelry for the more granular rungs
    ///      below.
    ///   2. Within a weapon-category item, VaultDisplayOrder.WeaponClassOrdinal (weapon skill/
    ///      style, via MarketWeaponClass.Classify); within an equipment-category item,
    ///      VaultDisplayOrder.SlotOrdinal(PropertyInt.ValidLocations) (head-down body slot, via
    ///      MarketEquipSlots.Classify); every other item's own WeenieType through
    ///      VaultDisplayOrder.FamilyOrdinal - a curated ordinal for the WeenieTypes a Misc/Stackable
    ///      tab actually separates by eye (pet devices/essences, spell components, stackables, mana
    ///      stones, healers, food, gems, keys, books, scrolls), clustering e.g. summoning essences
    ///      away from the level-8 tinkering glyphs/inks/powders that would otherwise interleave with
    ///      them under a bare Name sort. Ascending.
    ///   3. DamageType ascending, weapon-category items only - groups elemental weapons of the same
    ///      class together (Slash=0x1, Pierce=0x2, Bludgeon=0x4, Cold=0x8, Fire=0x10, Acid=0x20,
    ///      Electric=0x40, ..., Nether=0x400 - DamageType.cs around lines 10-22). DamageType is
    ///      itself [Flags]; a multi-element weapon's combined value sorting between two
    ///      single-element ones is accepted as deterministic, not special-cased. Equipment-category
    ///      items are explicitly a no-op (0) here too - PropertyInt.UiEffects is not this rung's
    ///      business for armor/clothing/jewelry, so an item that carries both an equipment ItemType
    ///      bit AND WeenieType.PetDevice must not fall into the PetDevice branch below. For a
    ///      PetDevice OUTSIDE the weapon/equipment categories (summoning essences), PropertyInt.
    ///      UiEffects ascending instead (null -> 0) - essences carry their element there (Fire/
    ///      Lightning/Frost/Acid/... - UiEffects.cs), not DamageType, so this clusters essences of
    ///      the same element together. 0 (a no-op key) for everything else.
    ///   4. Minimum requirement descending, absent LAST - VaultDisplayOrder.MinSkillRequirement
    ///      for a weapon-category item, VaultDisplayOrder.MinLevelRequirement for an
    ///      equipment-category item, PropertyInt.UseRequiresSkillLevel for a PetDevice (the
    ///      Summoning skill level an essence needs - e.g. "Acid Child Essence (125)" carries 430
    ///      there), 0 (a no-op key) for everything else. Null is coalesced to int.MinValue so an
    ///      item with no such requirement sorts last under the descending order - the same
    ///      "coalesce to the type's minimum" idiom rung 7 (Workmanship) already uses for the same
    ///      reason, not an incidental side effect of the coalesce.
    ///   5. Material name ascending, ordinal case-insensitive (empty when MaterialType is null,
    ///      which sorts first). Live testing (2026-08-31) found that Name alone scatters a
    ///      material family across the tab, because the same material shows up in three unrelated
    ///      naming shapes: an unsealed tinkering hammer is named for the material directly
    ///      ("Amethyst Hammer"), a full salvage bag leads with "Full Bag of" ("Full Bag of Amethyst
    ///      Salvage"), and a partial bag leads with "Salvaged" ("Salvaged Amethyst") - three
    ///      different first letters for the same material, so a pure alphabetical Name sort put
    ///      them nowhere near each other. Grouping by material first fixes that. The name comes
    ///      from RecipeManager.GetMaterialName(MaterialType) (RecipeManager.cs:1643), the same
    ///      resolver the appraisal panel's own material line uses, NOT MaterialType.ToString() -
    ///      GetMaterialName is only called when MaterialType is non-null AND not MaterialType.Unknown
    ///      (0). GetMaterialName logs an error for a value its DualDidMapper has no entry for, and
    ///      Unknown is exactly such a value - it is the enum's zero/unset sentinel, not a real
    ///      material with a client-side name, so there is nothing to look up for it any more than
    ///      there is for null, and calling it anyway would spam that log on every row that happens
    ///      to carry an explicit Unknown rather than an absent property. Both cases fall into the
    ///      same empty-material run. Known, approved consequence: a SEALED hammer carries no
    ///      MaterialType at all, so it sorts in the leading empty-material run (clustered under its
    ///      own "Sealed ..." name) rather than beside its material family - there is nothing to
    ///      group it by until it is unsealed.
    ///   6. Name ascending, ordinal case-insensitive (null treated as empty) - an alphabetical
    ///      scan within a material group (or within a tab, for the empty-material run). Reads
    ///      through <paramref name="baseName"/> first when that resolver returns non-null for the
    ///      row, so a mule row whose displayed Name carries a " (N in vault)" count suffix sorts on
    ///      its BASE name instead - the count changes on every withdrawal (10 -> 9, and the suffix
    ///      vanishes entirely once a group drops to 1), so letting it into the sort key made rows of
    ///      the same material jump around the tab as they were bought down. See
    ///      PersonalVendor.sortNames' own doc comment for where that map is populated. A caller with
    ///      no such suffixes (Thread Caches) passes null and gets Name directly.
    ///   7. Workmanship descending, null last. This is WorldObject.Workmanship, the PER-UNIT
    ///      AVERAGE (ItemWorkmanship / NumItemsInMaterial - WorldObject_Properties.cs:1617-1646),
    ///      not the raw ItemWorkmanship total - so two salvage bags of the same material compare
    ///      on the number the appraisal panel actually shows, not on a total that scales with
    ///      stack size. Coalescing null to float.MinValue is what puts a workmanship-less row
    ///      last under a DESCENDING sort - deliberate, not an accident of the coalesce. The
    ///      getter also WRITES back to the object under some conditions (see its own doc comment);
    ///      it was already in this chain before it moved here and is left exactly as-is.
    ///   8. Value descending, null coalesced to 0 - of two otherwise-identical rows, the more
    ///      valuable one first.
    ///   9. Guid.Full ascending - a total order, so the result is stable across restarts no
    ///      matter what order the backend happened to enumerate in on a given run.
    ///
    /// A within-Stackable sub-cluster on whether an item's wcid appears in the client's spell-
    /// component map (DualDidMapper at DID 0x27000002) was considered and DROPPED (2026-09-22): a
    /// probe of the 80 level-8 tinkering glyphs/inks/powders (WeenieType.Stackable, ItemType.Misc)
    /// against that map's 164 entries found zero matches - those items are not spell components at
    /// all, so there is nothing in the dat to sub-cluster on.
    ///
    /// Armor, Clothing, Jewelry, MeleeWeapon, MissileWeapon, and Caster are all in
    /// PersonalVendor.PanelRenderableItemTypes, so none of them is ever display-proxied - rungs 2-4
    /// always read a real biota's properties, never a proxy's stand-in values.
    ///
    /// Items are only READ here, never mutated (rung 7 excepted, see above): this decides an order and
    /// nothing else. What a caller then DOES with the order is its own - the mule serializes it,
    /// ThreadCacheSort writes PlacementPosition from it.
    /// </summary>
    public static class VaultDisplaySort
    {
        /// <summary>
        /// <paramref name="items"/> in the composite display order above. Lazy, like any LINQ ordering:
        /// nothing is compared until the result is enumerated.
        /// </summary>
        /// <param name="baseName">
        /// Optional rung-6 override: the pre-suffix name a row must sort on, or null for "use Name".
        /// Only the mule needs it (its " (N in vault)" count suffix); every other caller passes null.
        /// </param>
        public static IEnumerable<WorldObject> Order(IEnumerable<WorldObject> items, Func<ObjectGuid, string> baseName = null)
        {
            if (items == null)
                return Enumerable.Empty<WorldObject>();

            return items
                .Where(item => item != null)
                .Select(item => new
                {
                    item,
                    category = VaultDisplayOrder.CategoryOrdinal(item.ItemType),
                })
                .OrderBy(row => row.category)
                .ThenBy(row => row.category == VaultDisplayOrder.WeaponCategory
                    ? VaultDisplayOrder.WeaponClassOrdinal(row.item.WeenieType, row.item.GetProperty(PropertyInt.WeaponSkill), row.item.GetProperty(PropertyInt.AmmoType), row.item.GetProperty(PropertyInt.WeaponType))
                    : row.category == VaultDisplayOrder.EquipmentCategory
                        ? VaultDisplayOrder.SlotOrdinal(row.item.GetProperty(PropertyInt.ValidLocations))
                        : VaultDisplayOrder.FamilyOrdinal(row.item.WeenieType))
                .ThenBy(row => row.category == VaultDisplayOrder.WeaponCategory
                    ? (uint)(row.item.GetProperty(PropertyInt.DamageType) ?? 0)
                    : row.category == VaultDisplayOrder.EquipmentCategory
                        ? 0u
                        : row.item.WeenieType == WeenieType.PetDevice
                            ? (uint)(row.item.GetProperty(PropertyInt.UiEffects) ?? 0)
                            : 0)
                .ThenByDescending(row => row.category == VaultDisplayOrder.WeaponCategory
                    ? VaultDisplayOrder.MinSkillRequirement((row.item.GetProperty(PropertyInt.WieldRequirements), row.item.GetProperty(PropertyInt.WieldDifficulty)), (row.item.GetProperty(PropertyInt.WieldRequirements2), row.item.GetProperty(PropertyInt.WieldDifficulty2)), (row.item.GetProperty(PropertyInt.WieldRequirements3), row.item.GetProperty(PropertyInt.WieldDifficulty3)), (row.item.GetProperty(PropertyInt.WieldRequirements4), row.item.GetProperty(PropertyInt.WieldDifficulty4))) ?? int.MinValue
                    : row.category == VaultDisplayOrder.EquipmentCategory
                        ? VaultDisplayOrder.MinLevelRequirement((row.item.GetProperty(PropertyInt.WieldRequirements), row.item.GetProperty(PropertyInt.WieldDifficulty)), (row.item.GetProperty(PropertyInt.WieldRequirements2), row.item.GetProperty(PropertyInt.WieldDifficulty2)), (row.item.GetProperty(PropertyInt.WieldRequirements3), row.item.GetProperty(PropertyInt.WieldDifficulty3)), (row.item.GetProperty(PropertyInt.WieldRequirements4), row.item.GetProperty(PropertyInt.WieldDifficulty4))) ?? int.MinValue
                        : row.item.WeenieType == WeenieType.PetDevice
                            ? row.item.GetProperty(PropertyInt.UseRequiresSkillLevel) ?? int.MinValue
                            : 0)
                // MaterialType.Unknown is qualified with its full namespace, not just the enum name. In
                // PersonalVendor - where this chain used to live - WorldObject declares an instance property
                // called MaterialType, which shadows the enum TYPE name for an unqualified reference from
                // inside that class and fails to compile. It compiles unqualified HERE (this is not a
                // WorldObject), but the qualification is kept so the chain can be read against, and moved
                // back next to, the class that needs it.
                .ThenBy(row => row.item.MaterialType.HasValue && row.item.MaterialType.Value != ACE.Entity.Enum.MaterialType.Unknown ? RecipeManager.GetMaterialName(row.item.MaterialType.Value) : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => baseName?.Invoke(row.item.Guid) ?? row.item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(row => row.item.Workmanship ?? float.MinValue)
                .ThenByDescending(row => row.item.Value ?? 0)
                .ThenBy(row => row.item.Guid.Full)
                .Select(row => row.item);
        }
    }
}

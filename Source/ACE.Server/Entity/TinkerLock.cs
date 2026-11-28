using System.Collections.Generic;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.EquipmentMods;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// ML Bluespire quest weapon tinker lock (WaffleACE, 2026-09-27). A target carrying
    /// PropertyBool.TinkerLocked (9068) refuses every use-on-target tinker source except one with
    /// a cook_book row keyed on exactly (source wcid, target wcid), or a WeenieType.ManaStone
    /// source (mana recharging is not tinkering). A refire/give-target station has no tinker
    /// source to check against cook_book, so it refuses a locked item outright.
    ///
    /// The 8 Bluespire quest weapons (1005450-1005457) ship with the row, and their allow-list is
    /// exactly the cook_book rows Content/sql/recipes/BluespireBaseWeaponSlayerCookbook.sql adds
    /// for them (the 4 Ward-Stones 1004110/1004111/1004112/1006300 and the 2 Thirst-Stones
    /// 1005460/1005461). The mechanism is generic: it is not itself specific to those 8 wcids, and
    /// the class stays reusable for any future locked target. The 3 Siraluun crests
    /// (1005480-1005482) do NOT carry this property (owner ruling, 2026-09-27, reversing an
    /// earlier draft) - they are tinkerable normally.
    ///
    /// Read from the item's OWN biota only - no weenie fallback, no migration, no shard write.
    /// Only newly issued items (authored with the row in their weenie SQL) are locked.
    ///
    /// Drift Atlan weapons (PropertyBool.DriftAtlanWeapon, 9080) are a second, wider-allow-list rule in
    /// the same gate: they accept a ManaStone, an exact cook_book (source wcid, target wcid) pair (the
    /// three Atlan Drift infusions), a Paragon mod gem or the Paragon reset item (ParagonModManager.IsParagonSource,
    /// detected by property, allowed even with weapon_mods_enabled off so the manager can refuse with its own
    /// message), and - while weapon_mods_enabled is on - the fork's weapon-mod salvage
    /// (Tourmaline and Amethyst, WeaponModManager.IsModMaterial), as if the weapon were loot-dropped.
    /// Everything else is refused, with its own message. Refire stations refuse the weapon outright.
    ///
    /// Tinker-locked SHIELDS (owner ruling 2026-11-25; today only the Shield of the Rose, 1007451) are an exception to
    /// the lock, keyed on properties (TinkerLocked AND IsShield), never on wcid, so it also covers shields already
    /// held: they admit the four shield salvage materials ShieldSalvageTargets.WidensToShields names (Tourmaline and
    /// Amethyst only while weapon_mods_enabled is on, Black Opal and Fire Opal regardless). A locked weapon is not
    /// affected. The workmanship the imbue recipes and WeaponModManager need is data, not code: the Shield of the Rose
    /// weenie carries ItemWorkmanship 10, and Database/Updates/Shard/2026-11-25-01-Shield-Of-The-Rose-Workmanship.sql
    /// adds it to Roses created earlier.
    /// </summary>
    public static class TinkerLock
    {
        /// <summary>
        /// Pure: does the tinker-locked-shield exception admit this source? Only for a target that is both TinkerLocked
        /// and a shield, and a source ShieldSalvageTargets.WidensToShields accepts. Tourmaline / Amethyst
        /// (sourceIsWeaponModMaterial) need weapon mods enabled, as on a Drift Atlan weapon; Black / Fire Opal do not.
        /// The enabled flag is a parameter so tests never read PropertyManager.
        /// </summary>
        public static bool LockedShieldAdmits(bool targetLocked, bool targetIsShield, bool sourceWidensToShields,
            bool sourceIsWeaponModMaterial, bool weaponModsEnabled)
        {
            if (!targetLocked || !targetIsShield || !sourceWidensToShields)
                return false;

            return !sourceIsWeaponModMaterial || weaponModsEnabled;
        }

        /// <summary>
        /// Pure core including the tinker-locked-shield exception: when <paramref name="lockedShieldAdmits"/> is true (the
        /// answer of <see cref="LockedShieldAdmits(WorldObject, WorldObject)"/> in production), the lock rule stands down
        /// for the source; the Drift Atlan rule (and, in production, the salvage allow-list mask) still apply on their own
        /// terms. Otherwise identical to the 7-argument overload.
        /// </summary>
        public static bool IsRefused(bool targetLocked, bool lockedShieldAdmits, bool targetIsDriftAtlan, bool sourceIsManaStone, bool hasCookbookRow,
            bool sourceIsWeaponModMaterial, bool weaponModsEnabled, bool sourceIsParagonModSource)
        {
            var lockApplies = targetLocked && !lockedShieldAdmits;

            return IsRefused(lockApplies, targetIsDriftAtlan, sourceIsManaStone, hasCookbookRow, sourceIsWeaponModMaterial, weaponModsEnabled,
                sourceIsParagonModSource);
        }

        /// <summary>True for a target the exception covers: TinkerLocked AND a shield (properties, never wcid).</summary>
        public static bool IsLockedShield(WorldObject target)
        {
            return target != null && (target.GetProperty(PropertyBool.TinkerLocked) ?? false) && target.IsShield;
        }

        /// <summary>
        /// Production: does the tinker-locked-shield exception admit this source on this target? The single derivation
        /// of the exception's inputs; production IsRefused(WorldObject, WorldObject) calls exactly this.
        /// </summary>
        public static bool LockedShieldAdmits(WorldObject source, WorldObject target)
        {
            if (source == null || !IsLockedShield(target))
                return false;

            return LockedShieldAdmits(true, true, ShieldSalvageTargets.WidensToShields(source), WeaponModManager.IsModMaterial(source),
                WeaponModRegistry.Enabled());
        }

        /// <summary>
        /// Pure core, injectable so unit tests never touch PropertyManager or the database (a
        /// PropertyManager read throws outside a running server - see
        /// Source/ACE.Server.Tests/TinkerLockTests.cs). True means refuse the use.
        /// </summary>
        public static bool IsRefused(bool targetLocked, bool sourceIsManaStone, bool hasCookbookRow)
        {
            if (!targetLocked)
                return false;

            if (sourceIsManaStone)
                return false;

            return !hasCookbookRow;
        }

        /// <summary>
        /// Pure core including the Drift Atlan rule. Refused when EITHER rule refuses: the Siraluun lock
        /// (see the 3-argument overload) or the Drift rule - a Drift Atlan target refuses everything except
        /// a mana stone, an exact cook_book pair, or a weapon-mod salvage source while weapon mods are
        /// enabled, or a Paragon mod gem / Paragon reset item (sourceIsParagonModSource). The enabled flag is a
        /// parameter so tests never read PropertyManager.
        /// </summary>
        public static bool IsRefused(bool targetLocked, bool targetIsDriftAtlan, bool sourceIsManaStone, bool hasCookbookRow,
            bool sourceIsWeaponModMaterial, bool weaponModsEnabled, bool sourceIsParagonModSource = false)
        {
            if (IsRefused(targetLocked, sourceIsManaStone, hasCookbookRow))
                return true;

            if (!targetIsDriftAtlan)
                return false;

            if (sourceIsManaStone || hasCookbookRow)
                return false;

            // A Paragon mod gem or the Paragon reset item (ParagonModManager.IsParagonSource) is allowed on a Drift
            // Atlan weapon whether or not weapon_mods_enabled is on: with the system off the manager still claims
            // the use and answers with its own refusal, so refusing here would only replace that message.
            if (sourceIsParagonModSource)
                return false;

            return !(sourceIsWeaponModMaterial && weaponModsEnabled);
        }

        /// <summary>
        /// What a tinker source is, for the salvage allow-list (PropertyInt.ModSalvageAllowMask). Tourmaline,
        /// Amethyst (weapon mods) and Obsidian (equipment mods) are the fork-mod salvage bags; Tailoring is any
        /// tailoring kit, reduction or layering tool, or tailored intermediate (Tailoring.IsTailoringKit, vendor
        /// clones included); anything else is Other.
        /// </summary>
        public enum ModSalvageKind
        {
            Other = 0,
            Tourmaline = 1,
            Amethyst = 2,
            Obsidian = 4,
            Tailoring = 8,
        }

        public const int ModSalvageTourmalineBit = 1;
        public const int ModSalvageAmethystBit = 2;
        public const int ModSalvageObsidianBit = 4;
        public const int ModSalvageTailoringBit = 8;

        /// <summary>
        /// Pure classification of a use-on-target source for the mask rule: a tailoring kit (by wcid) is Tailoring,
        /// otherwise the fork-mod salvage classification by item type and material.
        /// </summary>
        public static ModSalvageKind ClassifyMaskSource(uint sourceWcid, ItemType itemType, MaterialType? materialType)
        {
            if (Tailoring.IsTailoringKit(sourceWcid))
                return ModSalvageKind.Tailoring;

            return ClassifyModSalvage(itemType, materialType);
        }

        /// <summary>
        /// Pure classification of a source by item type and material. A Salvage Hammer of an allowed material
        /// counts as that material because the match is on MaterialType + ItemType.TinkeringMaterial, exactly as
        /// WeaponModManager.IsModMaterial / EquipmentModManager.IsModMaterial do.
        /// </summary>
        public static ModSalvageKind ClassifyModSalvage(ItemType itemType, MaterialType? materialType)
        {
            if (WeaponModManager.IsModMaterial(itemType, materialType))
                return materialType == WeaponModManager.RerollMaterial ? ModSalvageKind.Tourmaline : ModSalvageKind.Amethyst;

            if (EquipmentModManager.IsModMaterial(itemType, materialType))
                return ModSalvageKind.Obsidian;

            return ModSalvageKind.Other;
        }

        /// <summary>
        /// True for the Left-hand Tether (45683) and the Left-hand Tether Remover (45684). Both are code-mapped recipes
        /// (RecipeManager_New), not cook_book rows, so the exact-pair exemption can never match them; the mask rule
        /// exempts them by wcid instead.
        /// </summary>
        public static bool IsTetherSource(uint sourceWcid)
        {
            return sourceWcid == (uint)WeenieClassName.W_LEFTHANDTETHER_CLASS
                || sourceWcid == (uint)WeenieClassName.W_LEFTHANDTETHERREMOVER_CLASS;
        }

        /// <summary>
        /// Pure core for the salvage allow-list rule. A null mask means the property is absent: never refuses. With
        /// any mask (including 0) every source is refused except a mana stone, an exact cook_book pair, a Left-hand
        /// Tether or its remover (sourceIsTether), a tailoring kit whose bit (8) is set in the mask, or a fork-mod
        /// salvage whose bit is set in the mask AND whose system is enabled. True means refuse the use.
        /// </summary>
        public static bool ModSalvageMaskRefuses(int? mask, bool isManaStone, bool hasExactCookbookPair, ModSalvageKind sourceKind,
            bool weaponModsEnabled, bool equipmentModsEnabled, bool sourceIsTether)
        {
            if (!mask.HasValue)
                return false;

            if (isManaStone || hasExactCookbookPair || sourceIsTether)
                return false;

            switch (sourceKind)
            {
                case ModSalvageKind.Tourmaline:
                    return !((mask.Value & ModSalvageTourmalineBit) != 0 && weaponModsEnabled);
                case ModSalvageKind.Amethyst:
                    return !((mask.Value & ModSalvageAmethystBit) != 0 && weaponModsEnabled);
                case ModSalvageKind.Obsidian:
                    return !((mask.Value & ModSalvageObsidianBit) != 0 && equipmentModsEnabled);
                case ModSalvageKind.Tailoring:
                    return (mask.Value & ModSalvageTailoringBit) == 0;
                default:
                    return true;
            }
        }

        /// <summary>Refusal text for the salvage allow-list, built from the allowed bits in the order Tourmaline, Amethyst, Obsidian, tailoring kits.</summary>
        public static string ModSalvageMaskRefusalText(int mask)
        {
            var names = new List<string>();
            if ((mask & ModSalvageTourmalineBit) != 0) names.Add("Tourmaline");
            if ((mask & ModSalvageAmethystBit) != 0) names.Add("Amethyst");
            if ((mask & ModSalvageObsidianBit) != 0) names.Add("Obsidian");
            if ((mask & ModSalvageTailoringBit) != 0) names.Add("tailoring kits");

            if (names.Count == 0)
                return "This item cannot be tinkered.";

            var list = names.Count == 1
                ? names[0]
                : string.Join(", ", names.GetRange(0, names.Count - 1)) + " or " + names[names.Count - 1];

            return "This item cannot be tinkered, except with " + list + ".";
        }

        public const string SiraluunRefusalFormat = "The {0} is bound by Siraluun craft and cannot be altered by that.";

        /// <summary>
        /// Refusal for a Knot treasure item (one carrying any KnotTraitRegistry property). It replaces the Siraluun line, which
        /// would be wrong for it; the lock itself is unchanged (a ManaStone or an exact cook_book row still passes).
        /// </summary>
        public const string KnotRefusalText = "The glass and thread of this piece will not take salvage.";

        /// <summary>
        /// Refusal for a Knot item that is a tinker-locked shield (<see cref="IsLockedShield"/>), whose lock admits the four
        /// shield materials: it replaces <see cref="KnotRefusalText"/> there only. Every other Knot item keeps that text.
        /// </summary>
        public const string LockedShieldKnotRefusalText = "The glass and thread of this piece will take only Tourmaline, Amethyst, Black Opal and Fire Opal.";

        /// <summary>True when the item carries any Knot trait property (behaviour is keyed on the property, never on wcid).</summary>
        public static bool IsKnotItem(WorldObject item)
        {
            foreach (var trait in KnotTraitRegistry.All)
            {
                if (item.GetProperty(trait.Property) != null)
                    return true;
            }

            return false;
        }

        public const string DriftRefusalText =
            "The Drift's power in this weapon rejects that material. Only Tourmaline, Amethyst, mana stones and Atlan Drift infusions can be used on it.";

        /// <summary>
        /// Player-facing refusal text. Sent as the same kind of message as the existing TargetType
        /// refusal in Player_Use.HandleActionUseWithTarget (SendTransientError). A Drift Atlan weapon
        /// gets its own text; the Siraluun wording would be wrong for it.
        /// </summary>
        public static string RefusalMessage(WorldObject source, WorldObject target)
        {
            var targetIsDriftAtlan = target.GetProperty(PropertyBool.DriftAtlanWeapon) ?? false;

            // The mask text applies only when the mask rule itself refuses this source; a source the mask lets
            // through but another lock refuses keeps that lock's text. A null source is a station.
            int? maskForText = null;
            var mask = target.GetProperty(PropertyInt.ModSalvageAllowMask);
            if (mask.HasValue && (source == null || ModSalvageMaskRefusesProduction(mask, source, target)))
                maskForText = ModSalvageMaskForText(mask.Value, isStation: source == null);

            return RefusalMessage(source?.WeenieClassId, targetIsDriftAtlan, target.Name, IsKnotItem(target), maskForText, IsLockedShield(target));
        }

        /// <summary>
        /// Pure: the mask value whose text is shown. A station refuses a masked item unconditionally, so an
        /// "except with ..." clause would be wrong there; it gets the no-materials text (mask 0 wording).
        /// </summary>
        public static int ModSalvageMaskForText(int mask, bool isStation) => isStation ? 0 : mask;

        private static bool ModSalvageMaskRefusesProduction(int? mask, WorldObject source, WorldObject target)
        {
            var hasCookbookRow = DatabaseManager.World.GetCachedCookbook(source.WeenieClassId, target.WeenieClassId) != null;

            return ModSalvageMaskRefuses(mask, source.WeenieType == WeenieType.ManaStone, hasCookbookRow,
                ClassifyMaskSource(source.WeenieClassId, source.ItemType, source.MaterialType), WeaponModRegistry.Enabled(),
                PropertyManager.GetBool("equipment_mods_enabled").Item, IsTetherSource(source.WeenieClassId));
        }

        public const uint FirstDriftInfusionWcid = 1007400;
        public const uint LastDriftInfusionWcid = 1007402;

        public const string DriftInfusionMismatchText = "That infusion cannot be used on this weapon.";

        public const string DriftConversionPromptFormat =
            "Infusing the {0} will transform it into a Drift weapon. The original weapon, along with any tinkering or imbue it carries, will be lost. Proceed?";

        /// <summary>
        /// Pure trigger for the "Are you sure?" prompt: a Drift infusion (wcid 1007400-1007402) used on a target
        /// that is not already a Drift Atlan weapon is a CONVERSION (the target is destroyed and replaced). A
        /// re-imbue of a Drift weapon, or any other source, needs no prompt.
        /// </summary>
        public static bool IsDriftConversion(uint sourceWcid, bool targetIsDriftAtlan)
        {
            return !targetIsDriftAtlan && sourceWcid >= FirstDriftInfusionWcid && sourceWcid <= LastDriftInfusionWcid;
        }

        public static bool IsDriftConversion(WorldObject source, WorldObject target)
        {
            return IsDriftConversion(source.WeenieClassId, target.GetProperty(PropertyBool.DriftAtlanWeapon) ?? false);
        }

        public static string DriftConversionPrompt(string targetName) => string.Format(DriftConversionPromptFormat, targetName);

        /// <summary>
        /// Pure message selection, injectable for tests. A Drift target refused an Atlan Drift infusion
        /// (wcid 1007400-1007402) gets the infusion-mismatch text; any other refused source on a Drift
        /// target gets the Drift material text; a Knot item gets the Knot text (the locked-shield variant when
        /// <paramref name="targetIsLockedShield"/>); any other locked target gets the Siraluun text.
        /// The source wcid is null for a refire station, which has no tinker source.
        /// </summary>
        public static string RefusalMessage(uint? sourceWcid, bool targetIsDriftAtlan, string targetName, bool targetIsKnotItem = false,
            int? modSalvageMask = null, bool targetIsLockedShield = false)
        {
            if (targetIsDriftAtlan)
            {
                if (sourceWcid.HasValue && sourceWcid.Value >= FirstDriftInfusionWcid && sourceWcid.Value <= LastDriftInfusionWcid)
                    return DriftInfusionMismatchText;

                return DriftRefusalText;
            }

            if (modSalvageMask.HasValue)
                return ModSalvageMaskRefusalText(modSalvageMask.Value);

            if (targetIsKnotItem)
                return targetIsLockedShield ? LockedShieldKnotRefusalText : KnotRefusalText;

            return string.Format(SiraluunRefusalFormat, targetName);
        }

        /// <summary>
        /// Production check for a use-on-target tinker: reads the target's own biota bool, the
        /// source's WeenieType, and looks up cook_book(source wcid, target wcid) via
        /// DatabaseManager.World.GetCachedCookbook. True means refuse the use.
        /// </summary>
        public static bool IsRefused(WorldObject source, WorldObject target)
        {
            var targetLocked = target.GetProperty(PropertyBool.TinkerLocked) ?? false;
            var targetIsDriftAtlan = target.GetProperty(PropertyBool.DriftAtlanWeapon) ?? false;

            var mask = target.GetProperty(PropertyInt.ModSalvageAllowMask);

            if (!targetLocked && !targetIsDriftAtlan && !mask.HasValue)
                return false;

            var sourceIsManaStone = source.WeenieType == WeenieType.ManaStone;

            var hasCookbookRow = DatabaseManager.World.GetCachedCookbook(source.WeenieClassId, target.WeenieClassId) != null;

            var sourceIsWeaponModMaterial = WeaponModManager.IsModMaterial(source);

            var weaponModsEnabled = WeaponModRegistry.Enabled();

            var sourceIsParagonModSource = ParagonModManager.IsParagonSource(source);

            var lockedShieldAdmits = LockedShieldAdmits(source, target);

            if (IsRefused(targetLocked, lockedShieldAdmits, targetIsDriftAtlan, sourceIsManaStone, hasCookbookRow, sourceIsWeaponModMaterial, weaponModsEnabled, sourceIsParagonModSource))
                return true;

            return ModSalvageMaskRefuses(mask, sourceIsManaStone, hasCookbookRow, ClassifyMaskSource(source.WeenieClassId, source.ItemType, source.MaterialType),
                weaponModsEnabled, PropertyManager.GetBool("equipment_mods_enabled").Item, IsTetherSource(source.WeenieClassId));
        }

        /// <summary>
        /// Production check for a refire/give-target station: there is no tinker source to check
        /// against cook_book here, so a locked item is refused outright. A Drift Atlan weapon is refused
        /// the same way: a station is not one of the sources the Drift rule allows.
        /// </summary>
        public static bool IsRefusedByStation(WorldObject item)
        {
            var targetLocked = item.GetProperty(PropertyBool.TinkerLocked) ?? false;
            var targetIsDriftAtlan = item.GetProperty(PropertyBool.DriftAtlanWeapon) ?? false;

            return IsRefusedByStation(targetLocked, targetIsDriftAtlan, item.GetProperty(PropertyInt.ModSalvageAllowMask));
        }

        /// <summary>Pure station core: a salvage allow-list mask (any value, including 0) refuses a station outright.</summary>
        public static bool IsRefusedByStation(bool targetLocked, bool targetIsDriftAtlan, int? modSalvageMask)
        {
            if (modSalvageMask.HasValue)
                return true;

            return IsRefused(targetLocked, targetIsDriftAtlan, sourceIsManaStone: false, hasCookbookRow: false,
                sourceIsWeaponModMaterial: false, weaponModsEnabled: false);
        }
    }
}

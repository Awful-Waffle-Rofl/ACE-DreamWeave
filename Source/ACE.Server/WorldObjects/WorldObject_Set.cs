using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        /// <summary>
        /// For item leveling,
        /// ~ the amount of item xp per level
        /// </summary>
        public long? ItemBaseXp
        {
            get => GetProperty(PropertyInt64.ItemBaseXp);
            set { if (!value.HasValue) RemoveProperty(PropertyInt64.ItemBaseXp); else SetProperty(PropertyInt64.ItemBaseXp, value.Value); }
        }

        public int? ItemMaxLevel
        {
            get => GetProperty(PropertyInt.ItemMaxLevel);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.ItemMaxLevel); else SetProperty(PropertyInt.ItemMaxLevel, value.Value); }
        }

        /// <summary>
        /// The total amount of XP earned for this item
        /// </summary>
        public long? ItemTotalXp
        {
            get => GetProperty(PropertyInt64.ItemTotalXp);
            set { if (!value.HasValue) RemoveProperty(PropertyInt64.ItemTotalXp); else SetProperty(PropertyInt64.ItemTotalXp, value.Value); }
        }

        /// <summary>
        /// The item xp formula type
        /// determines the amount of XP for each item level
        /// </summary>
        public ItemXpStyle? ItemXpStyle
        {
            get => (ItemXpStyle?)GetProperty(PropertyInt.ItemXpStyle);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.ItemXpStyle); else SetProperty(PropertyInt.ItemXpStyle, (int)value.Value); }
        }

        /// <summary>
        /// The item set this piece of equipment belongs to
        /// https://asheron.fandom.com/wiki/Item_Sets
        /// </summary>
        public EquipmentSet? EquipmentSetId
        {
            get => (EquipmentSet?)GetProperty(PropertyInt.EquipmentSetId);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.EquipmentSetId); else SetProperty(PropertyInt.EquipmentSetId, (int)value.Value); }
        }

        /// <summary>
        /// Returns the level for an item
        /// </summary>
        public int? ItemLevel
        {
            get
            {
                if (!HasItemLevel) return null;

                return ExperienceSystem.ItemTotalXPToLevel((ulong)ItemTotalXp.Value, (ulong)ItemBaseXp.Value, ItemMaxLevel.Value, ItemXpStyle.Value);
            }
        }

        /// <summary>
        /// Returns TRUE if this item is part of a set
        /// </summary>
        public bool HasItemSet => EquipmentSetId != null;

        /// <summary>
        /// Returns TRUE if this item can be leveled
        /// </summary>
        public bool HasItemLevel
        {
            get
            {
                // seems like ItemMaxLevel would be good enough here,
                // but using the client formula from ItemExamineUI::Appraisal_ShowItemLevelInfo
                return ItemBaseXp != null && ItemBaseXp > 0 &&
                       ItemMaxLevel != null && ItemMaxLevel > 0 &&
                       ItemXpStyle != null && ItemXpStyle > 0;
            }
        }

        /// <summary>
        /// Grants XP to an item that can be leveled up
        /// </summary>
        /// <param name="amount">The amount of item XP to add</param>
        public long AddItemXP(long amount)
        {
            if (!HasItemLevel)
            {
                Console.WriteLine($"{Name}.GrantItemXp({amount}): no item level");
                return 0;
            }

            var itemTotalXp = ItemTotalXp ?? 0;
            var prevAmount = itemTotalXp;
            itemTotalXp += amount;

            var maxLevelXp = (long)ExperienceSystem.ItemLevelToTotalXP(ItemMaxLevel.Value, (ulong)ItemBaseXp.Value, ItemMaxLevel.Value, ItemXpStyle.Value);

            if (itemTotalXp > maxLevelXp)
                itemTotalXp = maxLevelXp;

            if (itemTotalXp == prevAmount)
                return 0;

            ItemTotalXp = itemTotalXp;

            // return amount added
            return itemTotalXp - prevAmount;
        }

        /// <summary>
        /// Returns all spells from all levels in the item set
        /// </summary>
        public static List<Spell> GetSpellSetAll(EquipmentSet equipmentSet)
        {
            var spells = new List<Spell>();

            if (!DatManager.PortalDat.SpellTable.SpellSet.TryGetValue((uint)equipmentSet, out var spellSet))
                return spells;

            var spellIds = new HashSet<uint>();

            foreach (var level in spellSet.SpellSetTiers.Values)
            {
                foreach (var spell in level.Spells)
                    spellIds.Add(spell);
            }

            foreach (var spellId in spellIds)
                spells.Add(new Spell(spellId, false));

            return spells;
        }

        /// <summary>
        /// Returns the item set spells for a particular level
        /// </summary>
        public static List<Spell> GetSpellSet(List<WorldObject> setItems, int levelDiff = 0)
        {
            var spells = new List<Spell>();

            var firstSetItem = setItems.FirstOrDefault();

            if (firstSetItem is null)
                return spells;

            var equipmentSet = firstSetItem.EquipmentSetId;
            var itemXpStyle = firstSetItem.ItemXpStyle ?? 0;

            if (!DatManager.PortalDat.SpellTable.SpellSet.TryGetValue((uint)equipmentSet, out var spellSet))
                return spells;

            // apply maximum level cap here?
            var level = (uint)SetLevel(setItems, levelDiff);

            var highestTier = spellSet.HighestTier;

            //Console.WriteLine($"Total level: {level}");
            level = Math.Min(level, highestTier);

            if (!spellSet.SpellSetTiersNoGaps.TryGetValue(level, out var spellSetTiers))
                return spells;

            foreach (var spellId in spellSetTiers.Spells)
                spells.Add(new Spell(spellId, false));

            return spells;
        }

        /// <summary>
        /// The summed set level GetSpellSet picks its tier from, before the dat's HighestTier clamp: the sum of the
        /// items' ItemLevel (plus <paramref name="levelDiff"/>) when the first item levels, otherwise the item count.
        /// </summary>
        public static int SetLevel(List<WorldObject> setItems, int levelDiff = 0)
        {
            var firstSetItem = setItems.FirstOrDefault();

            if (firstSetItem is null)
                return 0;

            var itemXpStyle = firstSetItem.ItemXpStyle ?? 0;

            if (itemXpStyle > 0)
                return setItems.Sum(i => i.ItemLevel ?? 0) + levelDiff;
            else
                return setItems.Count;
        }

        /// <summary>
        /// The highest PropertyInt.SetBonusMaxLevel among the set items, or 0 when none carries it (retail).
        /// </summary>
        public static int SetBonusMaxLevelOf(List<WorldObject> setItems)
        {
            var cap = 0;

            foreach (var item in setItems)
                cap = Math.Max(cap, item.GetProperty(PropertyInt.SetBonusMaxLevel) ?? 0);

            return cap;
        }

        /// <summary>
        /// Pure: the factor applied to the StatModValue of a set spell. Past the dat's top tier the top tier's spell
        /// is still the one applied (GetSpellSet clamps), and when a set item carries SetBonusMaxLevel its value keeps
        /// growing linearly: value x min(level, cap) / highestTier. Exactly 1.0 when the cap is absent or not above
        /// the top tier, or the level has not passed it - so every retail set is untouched.
        /// </summary>
        public static double SetSpellScale(int setLevel, int setBonusMaxLevel, uint highestTier)
        {
            if (highestTier == 0 || setBonusMaxLevel <= highestTier || setLevel <= highestTier)
                return 1.0;

            return (double)Math.Min(setLevel, setBonusMaxLevel) / highestTier;
        }

        /// <summary>
        /// Pure: a set spell's StatModValue under <paramref name="setScale"/>. A scale of exactly 1.0 returns the value
        /// untouched (no float round trip), which is every retail set.
        /// </summary>
        public static float ApplySetSpellScale(float statModValue, double setScale)
        {
            if (setScale == 1.0)
                return statModValue;

            return (float)(statModValue * setScale);
        }

        /// <summary>
        /// The set-spell scale for an item set: <see cref="SetSpellScale"/> over the set's level, cap and dat HighestTier.
        /// 1.0 for an empty list, an unknown set, or a set no item of which carries SetBonusMaxLevel (no dat read then).
        /// </summary>
        public static double GetSpellSetScale(List<WorldObject> setItems, int levelDiff = 0)
        {
            var firstSetItem = setItems.FirstOrDefault();

            if (firstSetItem?.EquipmentSetId == null)
                return 1.0;

            var cap = SetBonusMaxLevelOf(setItems);

            if (cap <= 0)
                return 1.0;

            if (!DatManager.PortalDat.SpellTable.SpellSet.TryGetValue((uint)firstSetItem.EquipmentSetId, out var spellSet))
                return 1.0;

            return SetSpellScale(SetLevel(setItems, levelDiff), cap, spellSet.HighestTier);
        }

        /// <summary>
        /// The set-spell scale for an enchantment being built by set item <paramref name="setItem"/> on
        /// <paramref name="target"/>: the set is the items of that set equipped by the wearer (the target when it is a
        /// creature, otherwise the set item's wielder). 1.0 when there is no wearer.
        /// </summary>
        public static double GetSpellSetScale(WorldObject setItem, WorldObject target)
        {
            if (setItem?.EquipmentSetId == null)
                return 1.0;

            // The caster may be a surrogate set item (DequipItemFromSet), so the cap is read across the whole
            // equipped set, never from the caster alone.
            var wearer = target as Creature ?? setItem.Wielder as Creature;

            if (wearer == null)
                return 1.0;

            var setItems = wearer.EquippedObjects.Values.Where(i => i.HasItemSet && i.EquipmentSetId == setItem.EquipmentSetId).ToList();

            if (setItems.Count == 0)
                return 1.0;

            return GetSpellSetScale(setItems);
        }

        /// <summary>
        /// Returns TRUE if spell is contained within any tier for this equipment set
        /// </summary>
        public bool ItemSetContains(uint spellID)
        {
            if (!HasItemSet) return false;

            // get all spells from this set - cache?
            if (!DatManager.PortalDat.SpellTable.SpellSet.TryGetValue((uint)EquipmentSetId, out var spellSet))
                return false;

            foreach (var tier in spellSet.SpellSetTiers.Values)
                if (tier.Spells.Contains(spellID))
                    return true;

            return false;
        }
    }
}

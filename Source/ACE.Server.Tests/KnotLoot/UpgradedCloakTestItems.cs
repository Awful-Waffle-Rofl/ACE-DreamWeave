using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>Hand-built items for the upgraded-cloak tests. No database and no dat files are read.</summary>
    internal static class UpgradedCloakTestItems
    {
        private static uint nextGuid = 0x7F280000;

        /// <summary>A plain generic item; <paramref name="cloak"/> sets ValidLocations = Cloak, which is all Cloak.IsCloak reads.</summary>
        public static WorldObject Item(bool cloak, uint wcid = 1007602)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.Clothing },
                { PropertyInt.Value, 10 },
                { PropertyInt.EncumbranceVal, 1 },
            };

            if (cloak)
                ints[PropertyInt.ValidLocations] = (int)EquipMask.Cloak;
            else
                ints[PropertyInt.ValidLocations] = (int)EquipMask.NeckWear;

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = ints,
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A leveling set item at <paramref name="level"/> on the retail doubling curve.</summary>
        public static WorldObject SetItem(EquipmentSet set, int level, int maxLevel, int? setBonusMaxLevel)
        {
            const ulong baseXp = 1_000_000_000;

            var item = Item(cloak: true);

            item.EquipmentSetId = set;
            item.ItemBaseXp = (long)baseXp;
            item.ItemMaxLevel = maxLevel;
            item.ItemXpStyle = ItemXpStyle.ScalesWithLevel;
            item.ItemTotalXp = (long)ExperienceSystem.ItemLevelToTotalXP(level, baseXp, maxLevel, ItemXpStyle.ScalesWithLevel);

            if (setBonusMaxLevel != null)
                item.SetProperty(PropertyInt.SetBonusMaxLevel, setBonusMaxLevel.Value);

            return item;
        }
    }
}

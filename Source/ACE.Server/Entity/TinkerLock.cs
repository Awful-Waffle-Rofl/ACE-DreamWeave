using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
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
    /// </summary>
    public static class TinkerLock
    {
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
        /// Player-facing refusal text. Sent as the same kind of message as the existing TargetType
        /// refusal in Player_Use.HandleActionUseWithTarget (SendTransientError).
        /// </summary>
        public static string RefusalMessage(WorldObject target) =>
            $"The {target.Name} is bound by Siraluun craft and cannot be altered by that.";

        /// <summary>
        /// Production check for a use-on-target tinker: reads the target's own biota bool, the
        /// source's WeenieType, and looks up cook_book(source wcid, target wcid) via
        /// DatabaseManager.World.GetCachedCookbook. True means refuse the use.
        /// </summary>
        public static bool IsRefused(WorldObject source, WorldObject target)
        {
            var targetLocked = target.GetProperty(PropertyBool.TinkerLocked) ?? false;

            if (!targetLocked)
                return false;

            var sourceIsManaStone = source.WeenieType == WeenieType.ManaStone;

            var hasCookbookRow = DatabaseManager.World.GetCachedCookbook(source.WeenieClassId, target.WeenieClassId) != null;

            return IsRefused(targetLocked, sourceIsManaStone, hasCookbookRow);
        }

        /// <summary>
        /// Production check for a refire/give-target station: there is no tinker source to check
        /// against cook_book here, so a locked item is refused outright.
        /// </summary>
        public static bool IsRefusedByStation(WorldObject item)
        {
            var targetLocked = item.GetProperty(PropertyBool.TinkerLocked) ?? false;

            return IsRefused(targetLocked, sourceIsManaStone: false, hasCookbookRow: false);
        }
    }
}

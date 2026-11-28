using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Use-on-target gate for the four weapon-mod materials (Tourmaline, Amethyst, Black Opal, Fire Opal) on
    /// shields. Their bags and the Tourmaline / Amethyst hammers carry TargetType melee|missile|caster, and a
    /// shield is ItemType Armor, so both the client's own check and Player.HandleActionUseOnTarget's re-verify
    /// refused a shield before any crafting code ran. The fix lives in code, not content, so it reaches bags
    /// already held. The client may now offer every armor piece as a drag target; authority stays here, and
    /// <see cref="IsCompatible"/> admits the Armor bit only for a shield.
    /// </summary>
    public static class ShieldSalvageTargets
    {
        /// <summary>TinkeringMaterial AND one of the four weapon-mod materials.</summary>
        public static bool WidensToShields(ItemType itemType, MaterialType? materialType)
        {
            if (itemType != ItemType.TinkeringMaterial || !materialType.HasValue)
                return false;

            switch (materialType.Value)
            {
                case MaterialType.Tourmaline:
                case MaterialType.Amethyst:
                case MaterialType.BlackOpal:
                case MaterialType.FireOpal:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The ItemType rule above, plus the retail foolproof Black Opal and Fire Opal bags (wcids 36621, 36623),
        /// which are ItemType Misc and route to the same imbue recipe. Keyed on RecipeManager.IsFoolproofTinker
        /// rather than all of Misc, so an arbitrary Misc item with the material (or a Gem) never widens.
        /// </summary>
        public static bool WidensToShields(WorldObject source)
        {
            if (source == null)
                return false;

            if (WidensToShields(source.ItemType, source.MaterialType))
                return true;

            return source.ItemType == ItemType.Misc
                && RecipeManager.IsFoolproofTinker(source.WeenieClassId)
                && WidensToShields(ItemType.TinkeringMaterial, source.MaterialType);
        }

        /// <summary>The stored TargetType, OR'd with Armor when widened; null stays null when not widened.</summary>
        public static ItemType? EffectiveTargetType(WorldObject source)
        {
            var stored = source.TargetType;

            if (!WidensToShields(source))
                return stored;

            return (stored ?? ItemType.None) | ItemType.Armor;
        }

        public static bool IsCompatible(WorldObject source, WorldObject target)
        {
            if (((source.TargetType ?? ItemType.None) & target.ItemType) != ItemType.None)
                return true;

            return WidensToShields(source) && target.IsShield;
        }
    }
}

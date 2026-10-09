using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Classifies PropertyInt.ValidLocations into a fixed set of lowercase tokens for the web app's
    /// "Slot" market filter. Pure and static - it takes the primitive a snapshot already has (no
    /// WorldObject/Weenie dependency), so MarketSnapshot's two projections and this class's own tests
    /// call it identically.
    ///
    /// The token set (head/chest/abdomen/upper_arm/lower_arm/hands/upper_leg/lower_leg/feet/neck/
    /// wrist/finger/trinket/cloak/shield) and its canonical order are a CONTRACT shared with the web
    /// app: do not add, rename, or reorder tokens without updating both sides.
    ///
    /// Weapon slots (MeleeWeapon, MissileWeapon, MissileAmmo, Held, TwoHanded) and the Sigil slots
    /// are deliberately NOT offered here - the web app already has a separate Weapon Type filter
    /// (MarketWeaponClass) that covers weapons, so an item carrying only those bits classifies to an
    /// empty list rather than a bucket.
    ///
    /// WHY Wear and Armor bits for the same body region share one bucket: a shirt (a Wear bit) and a
    /// breastplate (the matching Armor bit) both cover the same slot from the player's point of view,
    /// so both map to the same token (e.g. "chest" covers both ChestWear and ChestArmor). The web
    /// app's existing item-type filter is what separates the wear layer from the armor layer; this
    /// classifier only answers "what slot does this occupy".
    ///
    /// WHY WristWearLeft|WristWearRight and FingerWearLeft|FingerWearRight collapse to ONE token each:
    /// those pairs are the client's "either side" idiom for a single wrist/finger slot (an item wears
    /// on whichever side is free), not two simultaneous slots, so "wrist" and "finger" each appear at
    /// most once regardless of which side bit(s) are set.
    /// </summary>
    public static class MarketEquipSlots
    {
        /// <summary>
        /// Classifies PropertyInt.ValidLocations. Null input returns null (unknown - the item was
        /// never classified, e.g. a listing captured before this field shipped). A non-null input
        /// always returns a list, possibly empty when the item's bits match no offered slot (a pure
        /// weapon, for instance) - callers must not treat an empty list the same as null.
        /// </summary>
        public static List<string> Classify(int? validLocations)
        {
            if (!validLocations.HasValue)
                return null;

            var mask = unchecked((EquipMask)validLocations.Value);
            var tokens = new List<string>();

            if ((mask & EquipMask.HeadWear) != 0)
                tokens.Add("head");

            if ((mask & (EquipMask.ChestWear | EquipMask.ChestArmor)) != 0)
                tokens.Add("chest");

            if ((mask & (EquipMask.AbdomenWear | EquipMask.AbdomenArmor)) != 0)
                tokens.Add("abdomen");

            if ((mask & (EquipMask.UpperArmWear | EquipMask.UpperArmArmor)) != 0)
                tokens.Add("upper_arm");

            if ((mask & (EquipMask.LowerArmWear | EquipMask.LowerArmArmor)) != 0)
                tokens.Add("lower_arm");

            if ((mask & EquipMask.HandWear) != 0)
                tokens.Add("hands");

            if ((mask & (EquipMask.UpperLegWear | EquipMask.UpperLegArmor)) != 0)
                tokens.Add("upper_leg");

            if ((mask & (EquipMask.LowerLegWear | EquipMask.LowerLegArmor)) != 0)
                tokens.Add("lower_leg");

            if ((mask & EquipMask.FootWear) != 0)
                tokens.Add("feet");

            if ((mask & EquipMask.NeckWear) != 0)
                tokens.Add("neck");

            if ((mask & (EquipMask.WristWearLeft | EquipMask.WristWearRight)) != 0)
                tokens.Add("wrist");

            if ((mask & (EquipMask.FingerWearLeft | EquipMask.FingerWearRight)) != 0)
                tokens.Add("finger");

            if ((mask & EquipMask.TrinketOne) != 0)
                tokens.Add("trinket");

            if ((mask & EquipMask.Cloak) != 0)
                tokens.Add("cloak");

            if ((mask & EquipMask.Shield) != 0)
                tokens.Add("shield");

            return tokens;
        }

        /// <summary>Display label for a Classify token, or null for an unknown/unrecognized token.</summary>
        public static string Label(string token)
        {
            switch (token)
            {
                case "head":
                    return "Head";
                case "chest":
                    return "Chest";
                case "abdomen":
                    return "Abdomen";
                case "upper_arm":
                    return "Upper Arm";
                case "lower_arm":
                    return "Lower Arm";
                case "hands":
                    return "Hands";
                case "upper_leg":
                    return "Upper Leg";
                case "lower_leg":
                    return "Lower Leg";
                case "feet":
                    return "Feet";
                case "neck":
                    return "Neck";
                case "wrist":
                    return "Wrist";
                case "finger":
                    return "Finger";
                case "trinket":
                    return "Trinket";
                case "cloak":
                    return "Cloak";
                case "shield":
                    return "Shield";
                default:
                    return null;
            }
        }
    }
}

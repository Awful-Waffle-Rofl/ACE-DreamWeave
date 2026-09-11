using System.Text;

using ACE.Entity.Enum;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Classifies PropertyInt.EquipmentSetId into the canonical token the web app's "Item Set" market
    /// filter uses - the spaced EquipmentSet enum name, exactly as MarketAppraisal's "Set: ..." panel
    /// line already renders it (MarketAppraisal.cs, the AddIdentity method's Spaced(((EquipmentSet)
    /// set.Value).ToString()) call). Pure and static - it takes the primitive a snapshot already has
    /// (no WorldObject/Weenie dependency), so MarketSnapshot's two projections and this class's own
    /// tests call it identically.
    ///
    /// The spacing logic below is a DELIBERATE DUPLICATE of MarketAppraisal's private Spaced helper,
    /// not a refactor to share it: the in-game appraisal panel's exact text is a locked behavior (a
    /// standing user decision - see MarketAppraisal.cs), so it must never be touched to support a new
    /// caller. If MarketAppraisal.Spaced's algorithm ever changes, this copy must be updated to match
    /// it, because the web app's fallback parser reads that very panel line and this token must keep
    /// agreeing with it.
    /// </summary>
    public static class MarketEquipmentSet
    {
        /// <summary>
        /// Classifies PropertyInt.EquipmentSetId into the canonical spaced-name token. Returns null
        /// for a null id, an id outside the enum's defined range, or one of the junk values Invalid
        /// (0), Test (1), Test2 (2), Unknown3 (3).
        /// </summary>
        public static string Classify(int? equipmentSetId)
        {
            if (!equipmentSetId.HasValue)
                return null;

            var set = (EquipmentSet)equipmentSetId.Value;

            switch (set)
            {
                case EquipmentSet.Invalid:
                case EquipmentSet.Test:
                case EquipmentSet.Test2:
                case EquipmentSet.Unknown3:
                    return null;
            }

            if (!System.Enum.IsDefined(typeof(EquipmentSet), set))
                return null;

            return Spaced(set.ToString());
        }

        /// <summary>
        /// Display label for a Classify token - the spaced name, except for a handful of overrides
        /// where the spaced enum name reads poorly. Null exactly when Classify(equipmentSetId) is
        /// null.
        /// </summary>
        public static string Label(int? equipmentSetId)
        {
            if (!equipmentSetId.HasValue)
                return null;

            var set = (EquipmentSet)equipmentSetId.Value;
            var token = Classify(equipmentSetId);

            if (token == null)
                return null;

            switch (set)
            {
                case EquipmentSet.OlthoiArmorDRed:
                    return "Olthoi Armor (Damage Reduction)";
                case EquipmentSet.OlthoiArmorDRat:
                    return "Olthoi Armor (Damage Rating)";
                case EquipmentSet.OlthoiArmorCRed:
                    return "Olthoi Armor (Critical Reduction)";
                case EquipmentSet.OlthoiArmorCRat:
                    return "Olthoi Armor (Critical Rating)";
                case EquipmentSet.Ninja_New:
                    // EquipmentSet.cs carries "// Shou-Jen Shozoku Set" as a comment above this member;
                    // the raw enum name (and its spaced form) is unreadable, hence the override.
                    return "Shou-jen Shozoku";
                default:
                    return token;
            }
        }

        /// <summary>
        /// MUST STAY IDENTICAL to MarketAppraisal.Spaced - see the class remarks. Splits an enum
        /// member into words so a token reads the way the game's own appraisal panel names it.
        /// </summary>
        private static string Spaced(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var text = new StringBuilder(name.Length + 8);

            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    text.Append(' ');

                text.Append(name[i]);
            }

            return text.ToString();
        }
    }
}

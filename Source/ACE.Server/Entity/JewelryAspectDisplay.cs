using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Appraisal lines for an aspected ring, bracelet or necklace. Computed live from the item and the settings on every
    /// appraisal, never stored, so retuning a per-slot value retunes every existing piece. Each layer line is THIS piece's
    /// share (per-slot times its count, rounded to one decimal) and is not capped: the cap applies to the worn total.
    /// </summary>
    public static class JewelryAspectDisplay
    {
        public static IEnumerable<string> GetAppraisalLines(WorldObject wo)
        {
            var lines = new List<string>();

            if (wo == null || !JewelryAspectMath.IsAspected(wo))
                return lines;

            var counts = JewelryAspectMath.ReadCounts(wo);

            lines.Add($"Aspects: Attack {counts.Attack} / Defense {counts.Defense} / Utility {counts.Utility}{(counts.Total < JewelryAspectMath.SlotCount ? $" ({counts.Total} of {JewelryAspectMath.SlotCount})" : string.Empty)}");

            // a switched-off system contributes nothing, so it advertises nothing
            if (!PropertyManager.GetBool("jewelry_aspects_enabled").Item)
                return lines;

            if (counts.Defense > 0)
                lines.Add($"Fortitude: {LayerText(AspectType.Defense, counts.Defense)}");

            if (counts.Attack > 0)
                lines.Add($"Penetration: {LayerText(AspectType.Attack, counts.Attack)}");

            if (counts.Utility > 0)
                lines.Add($"Resolve: {LayerText(AspectType.Utility, counts.Utility)}");

            return lines;
        }

        /// <summary>
        /// The text after the label on the appraisal line for one aspect type, this piece's share: "3% less damage taken"
        /// (Defense), "+4% elemental damage, or 4% of elemental resistance ignored if that is greater" (Attack; the old "4% of elemental resistance ignored" when the amplify tunable is 0), "+4% chance on kill to remove a debuff" (Utility).
        /// The ONE source: GetAppraisalLines prefixes it with the layer name, and the market listing's jewelry row uses it as
        /// its effect. Empty when the type has no aspects on the piece.
        /// </summary>
        public static string LayerText(AspectType type, int count)
        {
            if (count <= 0)
                return string.Empty;

            switch (type)
            {
                case AspectType.Defense:
                    return $"{Share(count, "jewelry_aspects_fortitude_per_slot")}% less damage taken";
                case AspectType.Attack:
                {
                    var perSlot = PropertyManager.GetDouble("jewelry_aspects_penetration_per_slot").Item;
                    var k = JewelryAspectMath.ClampAmplify(PropertyManager.GetDouble("jewelry_aspects_penetration_amplify").Item);

                    if (k <= 0.0)
                        return $"{Share(count, perSlot)}% of elemental resistance ignored";

                    return $"+{Share(count, perSlot * k)}% elemental damage, or {Share(count, perSlot)}% of elemental resistance ignored if that is greater";
                }
                default:
                    return $"+{Share(count, "jewelry_aspects_resolve_per_slot")}% chance on kill to remove a debuff";
            }
        }

        /// <summary>A whole-percent figure for count slots at the setting's per-slot value: rounded AwayFromZero to one decimal.</summary>
        internal static string Share(int count, string perSlotKey)
        {
            return Share(count, PropertyManager.GetDouble(perSlotKey).Item);
        }

        internal static string Share(int count, double perSlot)
        {
            var percent = Math.Round(count * perSlot * 100.0, 1, MidpointRounding.AwayFromZero);

            return percent.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}

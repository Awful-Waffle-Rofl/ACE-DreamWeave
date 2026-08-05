using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// The one place a stored potency is turned into player-facing text. Both the appraisal panel
    /// (AppraiseInfo's "Property Details:" block) and the application-flow success messages render through
    /// here, so a mod always reads the same way wherever it appears.
    ///
    /// Everything is computed live from the item's current potency values - no rendered string is ever stored,
    /// which is what lets a magnitude or equipment_mod_potency_scale retune show up instantly on gear that was
    /// modded months ago.
    /// </summary>
    public static class EquipmentModDisplay
    {
        /// <summary>
        /// "Deadeye: +2% missile damage" - one mod at one potency, resolved through the registry.
        /// </summary>
        public static string Describe(EquipmentModDefinition definition, double potency)
        {
            if (definition == null)
                return string.Empty;

            return $"{definition.DisplayName}: {definition.Format(EquipmentModValue.Resolve(definition, potency))}";
        }

        /// <summary>
        /// Every mod present on an item, in registry order, with its stored potency.
        /// </summary>
        public static List<(EquipmentModDefinition Definition, double Potency)> GetMods(WorldObject item)
        {
            var mods = new List<(EquipmentModDefinition, double)>();

            if (item == null)
                return mods;

            foreach (var definition in EquipmentModRegistry.AllMods)
            {
                var potency = item.GetProperty(definition.Property);

                if (potency != null)
                    mods.Add((definition, potency.Value));
            }

            return mods;
        }

        /// <summary>
        /// The mod bullet lines for an item's appraisal panel, including its mod capacity when set.
        /// Empty when the item carries nothing - the caller decides whether a block is worth printing.
        /// </summary>
        public static List<string> GetAppraisalLines(WorldObject item)
        {
            var lines = new List<string>();

            if (item == null)
                return lines;

            foreach (var (definition, potency) in GetMods(item))
                lines.Add($"- {Describe(definition, potency)}");

            var capacity = item.GetProperty(PropertyInt.GearModCapacity) ?? 0;

            if (capacity > 0)
                lines.Add($"- Mod Capacity: {capacity}");

            return lines;
        }
    }
}

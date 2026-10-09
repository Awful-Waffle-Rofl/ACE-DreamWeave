using System;
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
        /// How strong a roll is as a whole percent of the best that mod can reach, on a 1-100 scale. 0 means
        /// UNREPORTABLE and the caller must omit the figure rather than print it.
        ///
        /// ADDED 2026-08-07 to match the weapon-mod panel, and deliberately built the same way: the ratio of
        /// the applied magnitude to the magnitude a PERFECT roll would produce. Because
        /// <see cref="EquipmentModValue.Resolve"/> is linear in potency, that ratio reduces to the stored
        /// potency itself - both MaxMagnitude and equipment_mod_potency_scale cancel - so the figure cannot
        /// drift when either is retuned. Deriving it from the magnitudes rather than returning the potency
        /// directly is what keeps that true if Resolve ever gains quantization the way the weapon side has.
        ///
        /// TWO DIFFERENCES FROM THE WEAPON BRACKET, both inherent rather than oversights:
        ///
        ///   - NO ROUNDING. Workmanship is already inside the stored number, which since 2026-08-07 is a roll
        ///     fraction (<see cref="EquipmentModValue.RollFraction"/>), so a lower-workmanship piece reads lower
        ///     here just as a weapon does. <see cref="EquipmentModValue.Resolve"/> never rounds, so this figure
        ///     is that fraction itself, where a weapon's integer rows can round above it.
        ///   - THE FLOOR IS LOWER AND VARIES BY ROW. Weapon rows default to a 0.60 potency floor. Here the floor
        ///     is <see cref="EquipmentModRoller.DefaultMinPotency"/> (0.10) unless a row raises it, and it bounds
        ///     the potency BEFORE workmanship scales it: at workmanship 10 most mods read 10-100 and the
        ///     0.25-floor rows read 25-100, and below workmanship 10 a roll can read under its row's floor.
        ///
        /// A player comparing an armour bracket against a weapon bracket is therefore NOT comparing like with
        /// like, and that is a property of the two roll ranges rather than of this method.
        /// </summary>
        public static int IntensityPercent(EquipmentModDefinition definition, double potency)
        {
            if (definition == null || double.IsNaN(potency) || potency <= 0.0)
                return 0;

            var magnitude = EquipmentModValue.Resolve(definition, potency);
            var max = EquipmentModValue.Resolve(definition, 1.0);

            // covers NaN, a muted equipment_mod_potency_scale, and any row whose ceiling is not a positive
            // number - each of which would otherwise divide into an infinity and clamp to a confident 100
            if (double.IsNaN(magnitude) || double.IsNaN(max) || max <= 0.0)
                return 0;

            return Math.Clamp((int)Math.Round(magnitude / max * 100.0, MidpointRounding.AwayFromZero), 1, 100);
        }

        /// <summary>
        /// The effect text alone - "+2.32% missile damage" - with no name and no bracket, resolved through the
        /// registry at the live equipment_mod_potency_scale. Describe prints exactly this after its colon, and
        /// MarketItemMods projects exactly this for the web market.
        /// </summary>
        public static string Effect(EquipmentModDefinition definition, double potency) =>
            definition == null ? string.Empty : definition.Format(EquipmentModValue.Resolve(definition, potency));

        /// <summary>
        /// "Deadeye [83%]: +2.32% missile damage" - one mod at one potency, resolved through the registry,
        /// prefixed by how close that roll sits to the mod's ceiling.
        ///
        /// The bracket is dropped entirely when <see cref="IntensityPercent"/> cannot report one, so a line
        /// that cannot compute an honest figure loses the figure and keeps the mod.
        /// </summary>
        public static string Describe(EquipmentModDefinition definition, double potency)
        {
            if (definition == null)
                return string.Empty;

            var percent = IntensityPercent(definition, potency);
            var intensity = percent > 0 ? $" [{percent}%]" : string.Empty;

            return $"{definition.DisplayName}{intensity}: {Effect(definition, potency)}";
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

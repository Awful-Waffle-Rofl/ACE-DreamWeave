using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The one place a weapon mod becomes player-facing text. Both the appraisal panel's "Property Details:"
    /// block and the craft-success chat lines render through here.
    ///
    /// The native properties these modifiers feed are already rendered by the client's own panels (ratings, the
    /// weapon profile). This block exists so a player can tell WHICH part of those numbers came from this
    /// system, and so the 8130-8135 records - which carry no [AssessmentProperty] and never reach the client -
    /// have a surface at all.
    /// </summary>
    public static class WeaponModDisplay
    {
        /// <summary>
        /// How strong a rolled magnitude is as a whole percent of the best that modifier can reach, reported
        /// on a 1-100 scale. 0 means UNREPORTABLE and the caller must omit the figure rather than print it.
        ///
        /// The denominator is a perfect roll ON A WORKMANSHIP 10 WEAPON, not on this weapon, and that is the
        /// whole point of the number. Both axes the roll varies on are folded in: a workmanship 5 weapon
        /// cannot exceed 50% no matter how well it rolls, so the figure answers "how good is this modifier"
        /// rather than "how lucky was this roll", and a player comparing two weapons can compare the numbers
        /// directly. The potency floor (<see cref="WeaponModDefinition.DefaultMinPotency"/>, 0.60) puts a
        /// workmanship 10 roll in 60-100 and everything below that band is workmanship, not luck.
        ///
        /// COMPUTED FROM THE STORED MAGNITUDE, never from a potency, so it always agrees with the magnitude
        /// printed beside it - including the integer rows, where a Devastation showing "+4 critical damage
        /// rating" against a ceiling of 6 reads 67% off the rounded value the player can actually see, rather
        /// than off the unrounded potency behind it.
        ///
        /// TWO CONSEQUENCES OF READING THE LIVE SCALE, both accepted. Retuning MaxRoll or
        /// weapon_mod_magnitude_scale re-reads every existing weapon against the NEW ceiling while the stored
        /// magnitudes stay where they were - the same drift <see cref="WeaponModDefinition"/> already accepts
        /// for MaxRoll retunes, surfaced rather than hidden. A magnitude rolled at a scale since lowered can
        /// therefore exceed its own ceiling, which is why the result is clamped rather than trusted; the clamp
        /// is what keeps "[140%]" off the panel.
        /// </summary>
        public static int IntensityPercent(WeaponModDefinition definition, double magnitude, double scale)
        {
            if (definition == null || double.IsNaN(magnitude) || magnitude <= 0.0)
                return 0;

            var max = WeaponModValue.MaxMagnitude(definition, scale);

            // covers NaN, a muted scale, and any row whose ceiling is not a positive number - all of which
            // would otherwise divide into an infinity and clamp to a confident-looking 100
            if (double.IsNaN(max) || max <= 0.0)
                return 0;

            var percent = (int)Math.Round(magnitude / max * 100.0, MidpointRounding.AwayFromZero);

            return Math.Clamp(percent, 1, 100);
        }

        /// <summary>IntensityPercent against the live weapon_mod_magnitude_scale tunable.</summary>
        public static int IntensityPercent(WeaponModDefinition definition, double magnitude) =>
            IntensityPercent(definition, magnitude, WeaponModValue.MagnitudeScale());

        /// <summary>
        /// "Devastation [67%]: +4 critical damage rating" - one modifier at its applied magnitude, prefixed by
        /// how close that magnitude sits to the modifier's workmanship 10 ceiling.
        ///
        /// The bracket is dropped entirely when <see cref="IntensityPercent"/> cannot report one, so a line
        /// that cannot compute an honest figure loses the figure and keeps the modifier.
        /// </summary>
        public static string Describe(WeaponModDefinition definition, double magnitude, double scale)
        {
            if (definition == null)
                return string.Empty;

            var percent = IntensityPercent(definition, magnitude, scale);
            var intensity = percent > 0 ? $" [{percent}%]" : string.Empty;

            return $"{definition.DisplayName}{intensity}: {definition.Format(magnitude)}";
        }

        /// <summary>Describe against the live weapon_mod_magnitude_scale tunable.</summary>
        public static string Describe(WeaponModDefinition definition, double magnitude) =>
            Describe(definition, magnitude, WeaponModValue.MagnitudeScale());

        /// <summary>"4 Iron, 3 Brass, 3 Granite" - the weapon's current layer 1 composition, most-used first.</summary>
        public static string DescribeComposition(IEnumerable<MaterialType> materials)
        {
            var counts = WeaponModTinkerSet.Counts(materials);

            if (counts.Count == 0)
                return string.Empty;

            var parts = counts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => (int)kvp.Key)
                .Select(kvp => WeaponTinkerTable.TryGet(kvp.Key, out var definition)
                    ? $"{kvp.Value} {definition.DisplayName}"
                    : $"{kvp.Value} {kvp.Key}");

            return string.Join(", ", parts);
        }

        /// <summary>
        /// The appraisal bullet lines for a weapon, computed live from its current property values. Empty when
        /// the weapon carries nothing from this system - the caller decides whether a block is worth printing.
        ///
        /// NOTHING IS FILTERED BY TIER HERE, and that is a consequence of the single gate rather than an
        /// oversight. This method used to drop Tier B lines while weapon_mod_tier_b_enabled was false, so the
        /// panel could not advertise an effect that a separate gate had made inert. That tunable was removed on
        /// 2026-07-30: Tier B now rides weapon_mods_enabled, and the only caller
        /// (AppraiseInfo.BuildProfile) is itself behind that same bool - so by the time this method runs, every
        /// Tier B hook is live and the condition could never have been false. A tier filter added back here
        /// would be dead code claiming to protect against a state that cannot exist.
        /// </summary>
        public static List<string> GetAppraisalLines(WorldObject weapon)
        {
            var lines = new List<string>();

            if (weapon == null)
                return lines;

            foreach (var (definition, magnitude) in WeaponModTinkerSet.ReadSpecials(weapon))
                lines.Add($"- {Describe(definition, magnitude)}");

            if (!WeaponModTinkerSet.IsManaged(weapon))
                return lines;

            var composition = DescribeComposition(WeaponModTinkerSet.ReadComposition(weapon));

            if (composition.Length > 0)
                lines.Add($"- Tinkers: {composition}");

            return lines;
        }
    }
}

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
        /// <summary>"Devastation: +4 critical damage rating" - one modifier at its applied magnitude.</summary>
        public static string Describe(WeaponModDefinition definition, double magnitude)
        {
            if (definition == null)
                return string.Empty;

            return $"{definition.DisplayName}: {definition.Format(magnitude)}";
        }

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

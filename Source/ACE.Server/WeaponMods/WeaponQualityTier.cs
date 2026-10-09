using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Quality tiers awarded for the TOTAL intensity of a weapon's special modifiers.
    ///
    /// Intensity is <see cref="WeaponModDisplay.IntensityPercent"/> - each special's magnitude as a
    /// 1-100 percentage of its own maximum - summed across every special the weapon holds. With
    /// <see cref="WeaponModRegistry.MaxSpecials"/> = 4 the ceiling is 400, so the ladder below reads as
    /// an average intensity per special: 300 is a 75% average, 350 is 87.5%, 375 is 93.75%.
    ///
    /// A special whose intensity is UNREPORTABLE contributes 0 rather than being guessed at. That is
    /// deliberate: an unreportable magnitude means the registry has no maximum to measure against, and
    /// inventing one would let a weapon reach a tier on a figure the appraisal panel refuses to show.
    /// </summary>
    public enum WeaponQualityTier
    {
        None = 0,
        Exceptional = 1,
        Elite = 2,
        God = 3,
    }

    public static class WeaponQualityTiers
    {
        public sealed class TierDefinition
        {
            public WeaponQualityTier Tier { get; }

            /// <summary>Minimum summed intensity to earn this tier. INCLUSIVE - 300 exactly is Exceptional.</summary>
            public int Threshold { get; }

            public string Name { get; }

            /// <summary>
            /// Raw 0x33 PhysicsScript DataID granted at this tier, or null if the tier has no visual yet.
            /// </summary>
            public uint? VisualEffectScript { get; }

            public TierDefinition(WeaponQualityTier tier, int threshold, string name, uint? visualEffectScript)
            {
                Tier = tier;
                Threshold = threshold;
                Name = name;
                VisualEffectScript = visualEffectScript;
            }
        }

        /// <summary>
        /// Ordered HIGHEST FIRST, which is what makes <see cref="Evaluate"/> a simple first-match.
        ///
        /// All three effects assigned 2026-08-08 (repo-owner directive), chosen to escalate visibly:
        ///
        ///   Exceptional  0x33001186  the Empyrean Spherule halo (setup 0x02001771), 60 particles at
        ///                            scale 0.08 - a fine blue shimmer, the quietest of the three
        ///   Elite        0x330011BE  the bright white-blue orb - K'nath Mother (setup 0x02001834),
        ///                            4 particles at scale 3, the effect this system was built on
        ///   God          0x3300101B  the frost bloom (setup 0x0200167F) - large and unmistakable,
        ///                            the same effect the original Tourmaline probe applied to every
        ///                            reroll while the mechanism was being proven
        ///
        /// <see cref="ScriptFor"/> still walks DOWN to the highest tier at or below the one earned that
        /// has a script. That is now a no-op because every tier carries one, and it is kept deliberately:
        /// it is the guard that stops a future tier added without an effect silently stripping the aura
        /// from weapons good enough to reach it.
        /// </summary>
        private static readonly TierDefinition[] Ladder =
        {
            new TierDefinition(WeaponQualityTier.God,         375, "God",         0x3300101B),
            new TierDefinition(WeaponQualityTier.Elite,       350, "Elite",       0x330011BE),
            new TierDefinition(WeaponQualityTier.Exceptional, 300, "Exceptional", 0x33001186),
        };

        /// <summary>
        /// Sum of every special's intensity percentage. 0 for a weapon with no specials.
        /// </summary>
        public static int TotalIntensity(WorldObject weapon)
        {
            if (weapon == null)
                return 0;

            var total = 0;

            foreach (var (definition, magnitude) in WeaponModTinkerSet.ReadSpecials(weapon))
                total += WeaponModDisplay.IntensityPercent(definition, magnitude);

            return total;
        }

        /// <summary>
        /// The tier a weapon currently qualifies for. Thresholds are INCLUSIVE (repo-owner directive,
        /// 2026-08-08), so a weapon sitting exactly on 300 IS Exceptional.
        /// </summary>
        public static WeaponQualityTier Evaluate(WorldObject weapon) => FromTotal(TotalIntensity(weapon));

        public static WeaponQualityTier FromTotal(int total) =>
            Ladder.FirstOrDefault(t => total >= t.Threshold)?.Tier ?? WeaponQualityTier.None;

        public static string NameFor(WeaponQualityTier tier) =>
            Ladder.FirstOrDefault(t => t.Tier == tier)?.Name;

        public static int ThresholdFor(WeaponQualityTier tier) =>
            Ladder.FirstOrDefault(t => t.Tier == tier)?.Threshold ?? 0;

        /// <summary>
        /// The script a tier grants. Falls back DOWN the ladder when the earned tier has no visual of
        /// its own, so an unimplemented higher tier never costs a weapon the effect it already earned.
        /// </summary>
        public static uint? ScriptFor(WeaponQualityTier tier) =>
            Ladder.Where(t => t.Tier <= tier && t.VisualEffectScript.HasValue)
                  .OrderByDescending(t => t.Tier)
                  .FirstOrDefault()?.VisualEffectScript;
    }
}

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Shared "peripheral-skill scaling" math for class abilities whose magnitude scales off a legacy
    /// trained Skill (SKILL-TABLES-PREVIEW cross-class notes). Kept as a pure static so the dual-ratio
    /// model is unit-testable without a live Player; the Player-facing wrapper
    /// (Player.GetClassAbilityScaling) reads the source skill's effective value + advancement class and
    /// forwards here.
    ///
    /// Model (dual-ratio, user 2026-07-15):
    ///  - reads the source skill's Current (buffed / geared) value,
    ///  - contributes NOTHING unless the source skill is Trained or Specialized (gated at the wrapper),
    ///  - a Specialized source scales at a tighter (~25-33% smaller) divisor than a Trained one,
    ///  - an optional flat threshold discounts the first N effective points (e.g. Poison's Alchemy hook
    ///    only counts Alchemy above 100).
    ///
    /// The return value is a raw quotient ("+1 per <divisor> points above <threshold>"): a flat-damage
    /// hook (Poison Weapon) adds it directly; a percent hook (Multishot, Thorns) multiplies it by its own
    /// per-point percent. Units live at the call site so this stays a single, tiny, tested primitive.
    /// </summary>
    public static class ClassAbilityScaling
    {
        /// <summary>
        /// effectiveSkill: the source skill's Current value. isSpecialized selects the tighter divisor.
        /// perTrained / perSpec: points of source skill per +1 unit of bonus (larger = weaker). threshold:
        /// effective points discounted before scaling. Returns 0 for a non-positive effective value or a
        /// non-positive divisor.
        /// </summary>
        public static double Compute(double effectiveSkill, bool isSpecialized, double perTrained, double perSpec, double threshold = 0.0)
        {
            var effective = effectiveSkill - threshold;
            if (effective <= 0.0)
                return 0.0;

            var divisor = isSpecialized ? perSpec : perTrained;
            if (divisor <= 0.0)
                return 0.0;

            return effective / divisor;
        }
    }
}

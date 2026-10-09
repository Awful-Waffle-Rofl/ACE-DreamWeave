namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The MULTIPLICATIVE affinity model for class abilities: a legacy trained Skill scales an ability's
    /// OWN rank bonus rather than adding a separate rider beside it. Kept as a pure static so the model is
    /// unit-testable without a live Player; the Player-facing wrapper
    /// (Player.GetClassAbilityAffinityMultiplier) reads the source skill's effective value + advancement
    /// class and forwards here.
    ///
    /// Model (user 2026-09-12): "+R of the ability's own bonus per 100 points of the affinity skill".
    /// The standard rate is 0.12 Trained / 0.17 Specialized, so 200 points of a Specialized affinity skill
    /// makes the ability's bonus 1.34x what rank alone would give.
    ///
    /// CONTRAST WITH <see cref="ClassAbilityScaling"/>.Compute, which this is replacing one ability at a
    /// time and which still runs at every un-migrated call site. The two are NOT interchangeable:
    ///
    ///  - Compute returns a raw QUOTIENT ("+1 per divisor points"), starting at 0, which the call site ADDS
    ///    as a flat rider in its own units. Its tunables are DIVISORS, so a larger number is weaker.
    ///  - Multiplier returns a FACTOR &gt;= 1.0, which the call site MULTIPLIES the ability's rank bonus by.
    ///    Its tunables are RATES, so a larger number is stronger.
    ///
    /// Feeding one's tunables to the other, or adding this return value instead of multiplying by it, is
    /// silently wrong rather than a compile error - the shapes are identical.
    ///
    /// There is deliberately NO cap here. An ability that needs a ceiling clamps its own final magnitude at
    /// the call site, where the units are known.
    /// </summary>
    public static class ClassAbilityAffinity
    {
        /// <summary>
        /// effectiveSkill: the source skill's Current (buffed / geared) value. isSpecialized selects the
        /// stronger rate. ratePerTrained / ratePerSpec: added fraction of the ability's own bonus per 100
        /// points of source skill (larger = stronger). Returns EXACTLY 1.0 - the neutral factor, leaving the
        /// ability's bonus bit-identical to rank alone - for a non-positive effective value or a
        /// non-positive rate. Never returns below 1.0.
        /// </summary>
        public static double Multiplier(double effectiveSkill, bool isSpecialized, double ratePerTrained, double ratePerSpec)
        {
            if (effectiveSkill <= 0.0)
                return 1.0;

            var rate = isSpecialized ? ratePerSpec : ratePerTrained;
            if (rate <= 0.0)
                return 1.0;

            return 1.0 + (effectiveSkill / 100.0) * rate;
        }
    }
}

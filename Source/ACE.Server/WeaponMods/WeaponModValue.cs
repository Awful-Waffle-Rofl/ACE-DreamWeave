using System;

using ACE.Server.Managers;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The single place a potency roll becomes an applied magnitude:
    ///
    ///     applied = MaxRoll x potency x (Workmanship / 10) x weapon_mod_magnitude_scale
    ///
    /// The workmanship term is what makes the design's +101.1% endgame ceiling a top-weapon outcome rather than
    /// a flat grant, and it is why a leveling character with a workmanship 3 drop gets roughly a quarter of it.
    /// It applies to SPECIALS ONLY, never to layer 1 tinkers - a +1 Iron is a +1 Iron on any weapon, and scaling
    /// those too would break the equivalence with hand-tinkering that layer 1 depends on.
    ///
    /// Unlike EquipmentMods this resolution happens ONCE, at roll time, and the result is persisted. See
    /// WeaponModDefinition for why storing a potency instead would let the native property drift.
    /// </summary>
    public static class WeaponModValue
    {
        /// <summary>The global magnitude multiplier, applied at roll time only.</summary>
        public static double MagnitudeScale() => PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

        /// <summary>
        /// Turns a potency and a workmanship into the magnitude actually written onto the native property. Pure:
        /// every tunable arrives as an argument.
        ///
        /// Integer-valued natives round to the nearest whole number, with a floor of 1 whenever the unrounded
        /// value was above 0, so a rolled special is never a no-op. Binary modifiers ignore potency,
        /// workmanship and scale entirely and always apply exactly their MaxRoll. No row is Binary today - the
        /// only one that ever was, Cleave, was retired 2026-08-07 - so this branch is machinery held for the
        /// next such row, not dead code that can be deleted.
        /// </summary>
        public static double Resolve(WeaponModDefinition definition, double potency, double workmanship, double scale)
        {
            if (definition == null)
                return 0.0;

            if (definition.Binary)
                return definition.MaxRoll;

            var raw = definition.MaxRoll * Clamp01(potency) * (ClampWorkmanship(workmanship) / 10.0) * SanitizeScale(scale);

            if (!definition.IsInteger)
                return raw;

            var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);

            if (raw > 0.0 && rounded < 1.0)
                rounded = 1.0;

            return rounded;
        }

        /// <summary>Resolve against the live weapon_mod_magnitude_scale tunable.</summary>
        public static double Resolve(WeaponModDefinition definition, double potency, double workmanship) =>
            Resolve(definition, potency, workmanship, MagnitudeScale());

        /// <summary>
        /// The magnitude a PERFECT roll would produce on a workmanship 10 weapon - the denominator the
        /// appraisal panel's intensity percentage is read against. Not a separate formula: it is
        /// <see cref="Resolve"/> at its two ceilings, so integer quantization and the Binary short-circuit
        /// apply to the maximum exactly as they applied to the roll being compared against it.
        ///
        /// Zero or negative means "no meaningful ceiling" - a muted scale, or a hypothetical row with a
        /// non-positive MaxRoll - and callers must treat that as unreportable rather than dividing by it.
        /// </summary>
        public static double MaxMagnitude(WeaponModDefinition definition, double scale) =>
            Resolve(definition, 1.0, 10.0, scale);

        /// <summary>MaxMagnitude against the live weapon_mod_magnitude_scale tunable.</summary>
        public static double MaxMagnitude(WeaponModDefinition definition) =>
            MaxMagnitude(definition, MagnitudeScale());

        /// <summary>
        /// TRUE when a resolved magnitude actually buys its slot something. A special that resolves to zero is
        /// NOT APPLIED AT ALL (HARD): it writes no record, occupies no slot, and its slot converts to a layer 1
        /// tinker instead. The budget still sums to exactly ten.
        ///
        /// WHY THIS IS NOT AN EDGE CASE. The design's MinPotency floor of 0.25 exists precisely so an
        /// integer-quantized modifier never rounds away to nothing, and the "floor of 1 whenever the unrounded
        /// value was above 0" rule in <see cref="Resolve"/> is the other half of it. Both are defeated by
        /// weapon_mod_magnitude_scale = 0, which drives every non-binary magnitude to exactly 0. Without this
        /// test the reroll would spend up to three slots writing 0.0 records into the reserved 8130-8135
        /// PropertyFloat band - exactly the dead rows the design forbids ("clear with RemoveProperty, never
        /// SetProperty(0)"), reached from the other direction. A scale of 0 must read as "mute the layer", which
        /// means all ten slots go to tinkers.
        ///
        /// The test is PER DEFINITION because the quantization is: an integer-valued native is dead whenever the
        /// magnitude ROUNDS to zero, since <see cref="WeaponModDefinition.ApplyValue"/> rounds before writing,
        /// while a float-valued native carries any non-zero magnitude faithfully.
        ///
        /// Binary modifiers are deliberately unaffected: they ignore the scale by design and always apply
        /// their full MaxRoll, so their magnitude is never zero and they are never dropped. No row is Binary
        /// today (Cleave, the only one that ever was, was retired 2026-08-07), so at a muted scale every
        /// modifier now resolves to zero and no special is applied at all.
        /// </summary>
        public static bool IsLiveMagnitude(WeaponModDefinition definition, double magnitude)
        {
            if (definition == null || double.IsNaN(magnitude))
                return false;

            if (definition.IsInteger)
                return Math.Round(magnitude, MidpointRounding.AwayFromZero) != 0.0;

            return magnitude != 0.0;
        }

        /// <summary>Rolls a potency and resolves it in one step - the application path.</summary>
        public static double Roll(WeaponModDefinition definition, double workmanship) =>
            Resolve(definition, WeaponModRoller.RollPotency(definition), workmanship, MagnitudeScale());

        // ---------------- roll fraction: the Tier B storage unit ----------------
        //
        // A TIER B ROW STORES A ROLL FRACTION, NOT A MAGNITUDE. See WeaponModDefinition's storage contract for
        // why Tier A cannot do the same and why that asymmetry is deliberate rather than an inconsistency.
        //
        // The fraction is everything the ROLL contributed - potency and workmanship - with everything the
        // CATALOG contributes (MaxRoll, the scale tunable) deliberately left out, so the catalog side can be
        // retuned and every existing weapon moves with it. It is exactly the number the appraisal panel
        // reports: a fraction of 0.83 is the "[83%]" a player reads.

        /// <summary>
        /// The roll's own contribution, in [0, 1]: potency scaled by workmanship. This is what a Tier B record
        /// holds.
        ///
        /// Both catalog terms are absent BY DESIGN. <see cref="WeaponModDefinition.MaxRoll"/> and the scale
        /// tunable are re-applied at every read instead, which is what makes a magnitude retune retroactive -
        /// halving a MaxRoll halves the effect on weapons already in the world, rather than only on new rolls.
        /// </summary>
        public static double RollFraction(double potency, double workmanship) =>
            Clamp01(potency) * (ClampWorkmanship(workmanship) / 10.0);

        /// <summary>
        /// A stored roll fraction back into the magnitude the engine reads, against the LIVE catalog. The exact
        /// inverse of <see cref="RollFraction"/> composed with <see cref="Resolve"/>: for any potency and
        /// workmanship, MagnitudeFromFraction(RollFraction(p, w), scale) equals Resolve(p, w, scale). Asserted
        /// in the tests rather than left as a claim.
        ///
        /// Integer quantization and the Binary short-circuit are applied here too, so this stays substitutable
        /// for <see cref="Resolve"/> even on a row shape that no Tier B entry uses today.
        /// </summary>
        public static double MagnitudeFromFraction(WeaponModDefinition definition, double fraction, double scale)
        {
            if (definition == null)
                return 0.0;

            if (definition.Binary)
                return definition.MaxRoll;

            var raw = definition.MaxRoll * Clamp01(fraction) * SanitizeScale(scale);

            if (!definition.IsInteger)
                return raw;

            var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);

            if (raw > 0.0 && rounded < 1.0)
                rounded = 1.0;

            return rounded;
        }

        /// <summary>MagnitudeFromFraction against the live weapon_mod_magnitude_scale tunable.</summary>
        public static double MagnitudeFromFraction(WeaponModDefinition definition, double fraction) =>
            MagnitudeFromFraction(definition, fraction, MagnitudeScale());

        /// <summary>
        /// The roll fraction that produces <paramref name="magnitude"/> under the CURRENT catalog - the
        /// inversion, used only where a caller has a magnitude in hand rather than the roll behind it.
        ///
        /// LOSSY BY CONSTRUCTION, and the loss is why the roll path does not use it: a magnitude carries no
        /// record of which potency and workmanship produced it, so a magnitude above the row's current ceiling
        /// clamps to 1.0 rather than round-tripping. Production rolls call
        /// <see cref="WeaponModTinkerSet.ApplySpecialAtFraction"/> with the fraction they already computed and
        /// never come through here.
        ///
        /// Returns 0 when there is no ceiling to divide by - a muted scale, or a non-positive MaxRoll - which
        /// is the same "unreportable" reading the display layer takes.
        /// </summary>
        public static double FractionFor(WeaponModDefinition definition, double magnitude, double scale)
        {
            if (definition == null || double.IsNaN(magnitude))
                return 0.0;

            if (definition.Binary)
                return 1.0;

            var ceiling = definition.MaxRoll * SanitizeScale(scale);

            if (double.IsNaN(ceiling) || ceiling <= 0.0)
                return 0.0;

            return Clamp01(magnitude / ceiling);
        }

        public static double Clamp01(double value)
        {
            if (double.IsNaN(value))
                return 0.0;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// Workmanship is normally an integer 1-10 on a looted weapon. Clamped rather than trusted, because the
        /// derived accessor has a recovery branch for historically botched data. A weapon with NO workmanship is
        /// refused in verification and never reaches here - it must never be defaulted to zero, which would
        /// silently mint worthless specials.
        /// </summary>
        public static double ClampWorkmanship(double workmanship)
        {
            if (double.IsNaN(workmanship))
                return 0.0;

            return Math.Clamp(workmanship, 0.0, 10.0);
        }

        private static double SanitizeScale(double scale)
        {
            if (double.IsNaN(scale) || scale < 0.0)
                return 0.0;

            return scale;
        }
    }
}

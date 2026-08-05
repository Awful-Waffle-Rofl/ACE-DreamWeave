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
        /// value was above 0, so a rolled special is never a no-op. Binary modifiers (Cleave) ignore potency,
        /// workmanship and scale entirely and always apply exactly their MaxRoll.
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
        /// Binary modifiers (Cleave) are deliberately unaffected: they ignore the scale by design and always
        /// apply their full MaxRoll, so their magnitude is never zero and their slot is never dead.
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

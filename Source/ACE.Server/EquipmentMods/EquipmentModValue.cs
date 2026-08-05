using ACE.Server.Managers;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// The single place a stored potency becomes an applied magnitude.
    ///
    ///     applied = clamp01(potency) x definition.MaxMagnitude x equipment_mod_potency_scale
    ///
    /// Every combat hook, every appraisal line and every test goes through here, so a mod's magnitude has
    /// exactly one definition and the global scale tunable is impossible to bypass. Items store potency only:
    /// a resolved value is never written back to a biota.
    /// </summary>
    public static class EquipmentModValue
    {
        /// <summary>
        /// Resolves a single stored potency into the mod's applied value, in the hook's own units (a fraction
        /// for percent / percentage-point mods, an absolute for flat mods). The potency is clamped first, so a
        /// contaminated shard row cannot exceed the registry maximum.
        /// </summary>
        public static double Resolve(EquipmentModDefinition definition, double potency)
        {
            if (definition == null)
                return 0.0;

            return EquipmentModRoller.Clamp01(potency) * definition.MaxMagnitude * PotencyScale();
        }

        /// <summary>
        /// The mod's value at a perfect 100% roll under the current scale tunable - i.e. the multiplier that
        /// turns an already-clamped potency SUM (which may legitimately exceed 1.0 across several equipped
        /// items, since there is no per-type stack cap) into an applied value. Do not feed a sum to
        /// <see cref="Resolve"/>: that clamps its argument and would silently cap the stack.
        /// </summary>
        public static double MagnitudePerPotency(EquipmentModDefinition definition)
        {
            if (definition == null)
                return 0.0;

            return definition.MaxMagnitude * PotencyScale();
        }

        /// <summary>
        /// Global magnitude multiplier over the whole catalog. Lets an operator dial the entire power layer up
        /// or down live, with no item or schema impact.
        /// </summary>
        public static double PotencyScale() => PropertyManager.GetDouble("equipment_mod_potency_scale").Item;
    }
}

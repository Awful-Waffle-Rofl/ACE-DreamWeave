using System;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// The single place a stored roll fraction becomes an applied magnitude.
    ///
    ///     applied = clamp01(fraction) x definition.MaxMagnitude x equipment_mod_potency_scale
    ///
    /// Every combat hook, every appraisal line and every test goes through here, so a mod's magnitude has
    /// exactly one definition and the global scale tunable is impossible to bypass. Items store the fraction
    /// only: a resolved value is never written back to a biota.
    ///
    /// WHAT THE STORED NUMBER MEANS CHANGED ON 2026-08-07. It was a bare potency; it is now a ROLL FRACTION -
    /// potency scaled by the item's workmanship, exactly as a weapon mod stores it (see
    /// WeaponModValue.RollFraction). <see cref="RollFraction"/> is the only place that product is formed, and
    /// it is formed ONCE at application time rather than on every read.
    ///
    /// NOTHING ON THE READ SIDE MOVED, and that is why the change is as small as it is: this method, the
    /// equipped-stack sum, every combat hook and the appraisal panel all take the stored number at face value
    /// exactly as before. Only the two places that STAMP a value learned about workmanship.
    ///
    /// Applying workmanship at stamp time rather than at read time is deliberate, for the same two reasons
    /// the weapon side settled on: it keeps the roll's inputs frozen (an item's mods cannot shift because its
    /// workmanship property was later edited), and it keeps <see cref="WorldObject.Workmanship"/> - whose
    /// getter can WRITE to the item on malformed data - off the per-hit combat path entirely.
    /// </summary>
    public static class EquipmentModValue
    {
        /// <summary>
        /// The roll's own contribution, in [0, 1]: potency scaled by the item's workmanship. This is what an
        /// item's mod row holds.
        ///
        /// A workmanship 10 item is unchanged from the pre-2026-08-07 behaviour (the term is 1.0), which is
        /// why the change is close to invisible in practice - measured on stage the day it landed, the 963
        /// modded items averaged workmanship 9.99. What it closes is modding a LOW-workmanship piece for the
        /// same price as a perfect one.
        /// </summary>
        public static double RollFraction(double potency, double workmanship) =>
            EquipmentModRoller.Clamp01(potency) * (ClampWorkmanship(workmanship) / 10.0);

        /// <summary>
        /// Workmanship is normally an integer 1-10 on a looted item. Clamped rather than trusted, because the
        /// derived accessor has a recovery branch for historically botched data.
        ///
        /// An item with NO workmanship is REFUSED before it reaches here - by an explicit check in
        /// EquipmentModManager.VerifyUseRequirements, alongside the eligible-target test - rather than
        /// defaulted to zero, which would silently stamp a mod worth nothing. That case is not hypothetical:
        /// 4 of the 963 modded items on stage carry no workmanship row at all.
        /// </summary>
        public static double ClampWorkmanship(double workmanship)
        {
            if (double.IsNaN(workmanship))
                return 0.0;

            return Math.Clamp(workmanship, 0.0, 10.0);
        }

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

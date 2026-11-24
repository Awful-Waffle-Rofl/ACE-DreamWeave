using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using static ACE.Server.Tests.CeilingProbeSupport;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the BloodMage abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesBloodMage
    {
        /// <summary>Grow the Blood Charge pool one granted charge at a time, through the helper Player.AddBloodChargeStack uses, until it stops moving.</summary>
        private static double BloodChargesAtSaturation()
        {
            var cap = BloodChargeMath.StackCap(ClassAbilityText.Live, 3);
            var stacks = 0;
            while (stacks < 1000 && BloodChargeMath.NextStacks(stacks, cap) != stacks)
                stacks = BloodChargeMath.NextStacks(stacks, cap);
            return stacks;
        }

        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Sanguine Reserve and Exsanguinate share the one Blood Charge pool, so both ceilings are the Reserve rank's pool cap.
            new CeilingProbe(ClassAbilityId.SanguineReserve, "stacks", BloodChargesAtSaturation, () => Max(ClassAbilityId.SanguineReserve, "stacks")),
            new CeilingProbe(ClassAbilityId.Exsanguinate, "stacks", BloodChargesAtSaturation, () => Max(ClassAbilityId.Exsanguinate, "stacks")),

            // Weakened Blood: the slow cannot take a monster below the speed floor. Rank 3 at a saturating affinity multiplier, so the floor alone decides.
            // DeclaredMax restates the clamp from the tunable (1 - floor), independent of the MaxSlow helper the ceiling reads.
            new CeilingProbe(ClassAbilityId.WeakenedBlood, "slowfloor",
                () => 1.0 - WeakenedBloodMath.SpeedFactor(ClassAbilityText.Live, 3, Saturating),
                () => 1.0 - Tunable("class_ability_weakenedblood_slow_floor")),
        };
    }
}

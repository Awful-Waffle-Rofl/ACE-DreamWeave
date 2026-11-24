using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using static ACE.Server.Tests.CeilingProbeSupport;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Archer abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesArcher
    {
        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Multishot: the per-arrow multiplier is clamped at class_ability_multishot_damage_mult_cap.
            new CeilingProbe(ClassAbilityId.Multishot, "total",
                () => MultishotAbility.PerShotMultiplier(Tunable("class_ability_multishot_damage_mult"), Saturating, 0.0, Tunable("class_ability_multishot_damage_mult_cap")),
                () => F(Max(ClassAbilityId.Multishot, "total"))),

            // Pinning Shot chance: the amount affinity adds is capped (shared chance cap). Isolate it as saturated minus neutral.
            new CeilingProbe(ClassAbilityId.PinningShot, "affinity",
                () => PinningShotAbility.Chance(1, Tunable("class_ability_pinningshot_chance_base"), Tunable("class_ability_pinningshot_chance_step"), Saturating, Tunable("class_ability_affinity_chance_cap"))
                    - PinningShotAbility.Chance(1, Tunable("class_ability_pinningshot_chance_base"), Tunable("class_ability_pinningshot_chance_step"), 1.0, Tunable("class_ability_affinity_chance_cap")),
                () => Max(ClassAbilityId.PinningShot, "affinity")),

            // Pinning Shot reduction: the amount affinity adds to the damage reduction is capped.
            new CeilingProbe(ClassAbilityId.PinningShot, "affinity_reduction",
                () => PinReductionMath.Magnitude(Tunable("class_ability_pinningshot_damage_reduction"), Saturating, Tunable("class_ability_pinningshot_damage_reduction_affinity_cap"))
                    - PinReductionMath.Magnitude(Tunable("class_ability_pinningshot_damage_reduction"), 1.0, Tunable("class_ability_pinningshot_damage_reduction_affinity_cap")),
                () => Max(ClassAbilityId.PinningShot, "affinity_reduction")),

            // Pinning Shot total reduction can never exceed PinReductionMath.MaxReduction (uncapped affinity, saturating base).
            new CeilingProbe(ClassAbilityId.PinningShot, "pinmax",
                () => PinReductionMath.Magnitude(Tunable("class_ability_pinningshot_damage_reduction"), Saturating, 0.0),
                () => Max(ClassAbilityId.PinningShot, "pinmax")),

            // Hunter's Echo: the seconds affinity adds are capped.
            new CeilingProbe(ClassAbilityId.HuntersEcho, "affinity",
                () => HuntersEchoMath.AddedSeconds(Tunable("class_ability_huntersecho_duration_seconds"), Saturating, Tunable("class_ability_huntersecho_affinity_cap_seconds")),
                () => Max(ClassAbilityId.HuntersEcho, "affinity")),

            // Double Volley gear: the Double Volley mod's cross-item StackCap, through the shared cap helper and the
            // gear term the ability's own Chance() takes. Isolated at rank 1 with a zero rank bonus.
            new CeilingProbe(ClassAbilityId.DoubleVolley, "gear",
                () => DoubleVolleyAbility.Chance(1, 0.0, 0.0, 1.0, 0.0, GearAtSaturation(EquipmentModId.DoubleVolley)),
                () => F(Max(ClassAbilityId.DoubleVolley, "gear") / EquipmentModRegistry.Get(EquipmentModId.DoubleVolley).DisplayScale)),
        };
    }
}

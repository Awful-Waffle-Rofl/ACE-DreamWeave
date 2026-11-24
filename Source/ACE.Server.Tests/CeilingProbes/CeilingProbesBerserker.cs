using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using static ACE.Server.Tests.CeilingProbeSupport;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Berserker abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesBerserker
    {
        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Break Armor: the amount affinity adds to the proc chance is capped (shared chance cap). A unit base chance makes
            // Chance() return 1 + added, so the float result minus one isolates what affinity added.
            new CeilingProbe(ClassAbilityId.BreakArmor, "affinity",
                () => BreakArmorAbility.Chance(1, 1.0, Saturating, Tunable("class_ability_affinity_chance_cap")) - 1.0,
                () => F(1.0 + Max(ClassAbilityId.BreakArmor, "affinity")) - 1.0),

            // Break Armor gear: the mod's StackCap, through the shared cap helper and the ability's own Chance() (a zero base isolates the gear term).
            new CeilingProbe(ClassAbilityId.BreakArmor, "gear",
                () => BreakArmorAbility.Chance(1, 0.0, 1.0, 0.0, GearAtSaturation(EquipmentModId.BreakArmor)),
                () => F(Max(ClassAbilityId.BreakArmor, "gear") / EquipmentModRegistry.Get(EquipmentModId.BreakArmor).DisplayScale)),

            // Frenzy: landed hits stop adding stacks at the rank's cap (rank 3).
            new CeilingProbe(ClassAbilityId.Frenzy, "stacks",
                () => { var stacks = 0; for (var i = 0; i < 99; i++) stacks = Player.NextFrenzyStacks(stacks, FrenzyAbility.StackCap(3)); return stacks; },
                () => Max(ClassAbilityId.Frenzy, "stacks")),

            // Frenzy attack speed: the composed animation speed is clamped at class_ability_attack_speed_ceiling.
            new CeilingProbe(ClassAbilityId.Frenzy, "attackspeed",
                () => Player.ComposeAttackSpeed(2.0f, 100.0f, (float)Tunable("class_ability_attack_speed_ceiling")),
                () => F(Max(ClassAbilityId.Frenzy, "attackspeed"))),
        };
    }
}

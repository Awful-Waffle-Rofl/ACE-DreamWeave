using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using static ACE.Server.Tests.CeilingProbeSupport;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Rogue abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesRogue
    {
        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Parry shares the pooled Shield Block + Parry avoidance cap: two chances over the cap are scaled to sum to it.
            new CeilingProbe(ClassAbilityId.Parry, "avoidance",
                () => { var (block, parry, _) = ClassAbilityAvoidance.Pooled(0.4, 0.4, Tunable("class_ability_avoidance_cap")); return block + parry; },
                () => Max(ClassAbilityId.Parry, "avoidance")),

            // Pocket Sand: the amount affinity adds to the proc chance is capped (shared chance cap).
            new CeilingProbe(ClassAbilityId.PocketSand, "affinity",
                () => PocketSandAbility.Chance(1, Tunable("class_ability_pocketsand_chance_base"), Tunable("class_ability_pocketsand_chance_step"), Saturating, Tunable("class_ability_affinity_chance_cap"))
                    - PocketSandAbility.Chance(1, Tunable("class_ability_pocketsand_chance_base"), Tunable("class_ability_pocketsand_chance_step"), 1.0, Tunable("class_ability_affinity_chance_cap")),
                () => Max(ClassAbilityId.PocketSand, "affinity")),

            // Acid Proc gear: the Acid Proc mod's StackCap, through the shared cap helper and the ability's own Chance()
            // (a zero base chance isolates the gear term).
            new CeilingProbe(ClassAbilityId.AcidProc, "gear",
                () => AcidProcAbility.Chance(1, 0.0, GearAtSaturation(EquipmentModId.AcidProc)),
                () => F(Max(ClassAbilityId.AcidProc, "gear") / EquipmentModRegistry.Get(EquipmentModId.AcidProc).DisplayScale)),

            // Acid Proc poison damage: the amount affinity adds is capped by class_ability_acidproc_damage_affinity_cap.
            new CeilingProbe(ClassAbilityId.AcidProc, "affinity",
                () => AcidProcAbility.PoisonDamageBonus(1, Tunable("class_ability_acidproc_damage_base"), Tunable("class_ability_acidproc_damage_step"), Saturating, Tunable("class_ability_acidproc_damage_affinity_cap"))
                    - AcidProcAbility.PoisonDamageBonus(1, Tunable("class_ability_acidproc_damage_base"), Tunable("class_ability_acidproc_damage_step"), 1.0, Tunable("class_ability_acidproc_damage_affinity_cap")),
                () => Max(ClassAbilityId.AcidProc, "affinity")),

            // Attack Speed: the composed animation speed is clamped at class_ability_attack_speed_ceiling.
            new CeilingProbe(ClassAbilityId.AttackSpeed, "attackspeed",
                () => Player.ComposeAttackSpeed(2.0f, 100.0f, (float)Tunable("class_ability_attack_speed_ceiling")),
                () => F(Max(ClassAbilityId.AttackSpeed, "attackspeed"))),

            // Killer Instinct: Openings stop accruing at the stack cap.
            new CeilingProbe(ClassAbilityId.KillerInstinct, "stacks",
                () => { var stacks = 0; for (var i = 0; i < 99; i++) stacks = Player.NextKillerInstinctStacks(stacks, Player.KillerInstinctStackCap()); return stacks; },
                () => Max(ClassAbilityId.KillerInstinct, "stacks")),
        };
    }
}

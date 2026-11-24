using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using static ACE.Server.Tests.CeilingProbeSupport;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Archmage abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesArchmage
    {
        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Elemental Rend (vulnerability proc): the amount affinity adds to the chance is capped (shared chance cap); zero base and step isolate it.
            new CeilingProbe(ClassAbilityId.ElementalRendVuln, "affinity",
                () => ElementalRendVulnAbility.RendChance(1, 0.0, 0.0, Saturating, 0.0, Tunable("class_ability_affinity_chance_cap")),
                () => F(Max(ClassAbilityId.ElementalRendVuln, "affinity"))),

            // Elemental Rend gear: the mod's StackCap, through the shared cap helper and RendChance's gear term.
            new CeilingProbe(ClassAbilityId.ElementalRendVuln, "gear",
                () => ElementalRendVulnAbility.RendChance(1, 0.0, 0.0, 0.0, GearAtSaturation(EquipmentModId.ElementalRendVuln)),
                () => F(Max(ClassAbilityId.ElementalRendVuln, "gear") / EquipmentModRegistry.Get(EquipmentModId.ElementalRendVuln).DisplayScale)),

            // Elemental Proficiency: the affinity multiplier is clamped at 1 + cap, so affinity adds at most cap of the effect
            // (a unit per-rank value at rank 1 makes the magnitude 1 x multiplier).
            new CeilingProbe(ClassAbilityId.ElementalRend, "affinity",
                () => ElementalProficiencyMath.Magnitude(1.0, 1, true, Saturating, 0.0, Tunable("class_ability_elementalproficiency_affinity_cap")) - 1.0,
                () => Max(ClassAbilityId.ElementalRend, "affinity")),

            // Elemental Proficiency gear: the Elemental Rend amplifier at the mod's StackCap, through the same magnitude helper.
            new CeilingProbe(ClassAbilityId.ElementalRend, "gear",
                () => ElementalProficiencyMath.Magnitude(1.0, 1, true, 1.0, GearAtSaturation(EquipmentModId.ElementalRend), Tunable("class_ability_elementalproficiency_affinity_cap")) - 1.0,
                () => Max(ClassAbilityId.ElementalRend, "gear") / EquipmentModRegistry.Get(EquipmentModId.ElementalRend).DisplayScale),

            // Mana Barrier: the diverted share is clamped at class_ability_manabarrier_max_share.
            new CeilingProbe(ClassAbilityId.ManaBarrier, "total",
                () => ManaBarrierAbility.DivertShare(3, Tunable("class_ability_manabarrier_base"), Tunable("class_ability_manabarrier_step"), Saturating, Tunable("class_ability_manabarrier_max_share")),
                () => Max(ClassAbilityId.ManaBarrier, "total")),

            // Spell AOE: the radiated fraction is clamped at class_ability_spellaoe_damage_mult_cap (rank 3 base, saturating rider).
            new CeilingProbe(ClassAbilityId.SpellAoe, "total",
                () => SpellAoeAbility.ClampRadiated(Tunable("class_ability_spellaoe_damage_mult_r3"), Saturating, 0.0, Tunable("class_ability_spellaoe_damage_mult_cap")),
                () => Max(ClassAbilityId.SpellAoe, "total")),

            // Echo Cast wisps: a wisp may spawn only while UnderCap holds, so count the spawns from an empty field.
            new CeilingProbe(ClassAbilityId.EchoCast, "wisps",
                () => { var active = 0; while (active < 1000 && EchoWispMath.UnderCap(active, PropertyManager.GetLong("class_ability_echocast_wisp_max_active").Item)) active++; return active; },
                () => Max(ClassAbilityId.EchoCast, "wisps")),

            // Echo Cast gear: the mod's StackCap, through the shared cap helper and EchoChance's gear term (zero base and step isolate it).
            new CeilingProbe(ClassAbilityId.EchoCast, "gear",
                () => EchoCastAbility.EchoChance(1, 0.0, 0.0, 0.0, GearAtSaturation(EquipmentModId.EchoCast)),
                () => F(Max(ClassAbilityId.EchoCast, "gear") / EquipmentModRegistry.Get(EquipmentModId.EchoCast).DisplayScale)),
        };
    }
}

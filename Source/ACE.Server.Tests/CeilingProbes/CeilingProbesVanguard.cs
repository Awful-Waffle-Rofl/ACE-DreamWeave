using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using static ACE.Server.Tests.CeilingProbeSupport;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Vanguard abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesVanguard
    {
        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Shield Block shares the pooled Shield Block + Parry avoidance cap: two chances over the cap are scaled to sum to it.
            new CeilingProbe(ClassAbilityId.ShieldBlock, "avoidance",
                () => { var (block, parry, _) = ClassAbilityAvoidance.Pooled(0.4, 0.4, Tunable("class_ability_avoidance_cap")); return block + parry; },
                () => Max(ClassAbilityId.ShieldBlock, "avoidance")),

            // Shield Block: the amount affinity adds to the block chance is capped (shared chance cap).
            new CeilingProbe(ClassAbilityId.ShieldBlock, "affinity",
                () => ShieldBlockAbility.BlockChance(1, Tunable("class_ability_shieldblock_base"), Tunable("class_ability_shieldblock_step"), Saturating, Tunable("class_ability_affinity_chance_cap"))
                    - ShieldBlockAbility.BlockChance(1, Tunable("class_ability_shieldblock_base"), Tunable("class_ability_shieldblock_step"), 1.0, Tunable("class_ability_affinity_chance_cap")),
                () => Max(ClassAbilityId.ShieldBlock, "affinity")),

            // Shield Block gear: the Shield Wall mod's StackCap, through the shared cap helper and BlockChance's gear term
            // (a zero base chance isolates it).
            new CeilingProbe(ClassAbilityId.ShieldBlock, "gear",
                () => ShieldBlockAbility.BlockChance(1, 0.0, 0.0, 1.0, 0.0, GearAtSaturation(EquipmentModId.ShieldWall)),
                () => Max(ClassAbilityId.ShieldBlock, "gear") / EquipmentModRegistry.Get(EquipmentModId.ShieldWall).DisplayScale),

            // Battle Hardened: the total reduction is clamped at class_ability_battlehardened_max_reduction.
            new CeilingProbe(ClassAbilityId.BattleHardened, "total",
                () => 1.0 - ResistFactor(),
                () => 1.0 - F(1.0 - Max(ClassAbilityId.BattleHardened, "total"))),

            // Cloaked in Power: the amount affinity adds to the proc floor is capped (shared chance cap).
            new CeilingProbe(ClassAbilityId.CloakedInPower, "affinity",
                () => CloakedInPowerAbility.Floor(1, Tunable("class_ability_cloakedinpower_floor"), Saturating, Tunable("class_ability_affinity_chance_cap"))
                    - CloakedInPowerAbility.Floor(1, Tunable("class_ability_cloakedinpower_floor"), 1.0, Tunable("class_ability_affinity_chance_cap")),
                () => Max(ClassAbilityId.CloakedInPower, "affinity")),

            // Reflect Magic: the amount affinity adds to the bounce chance is capped (shared chance cap).
            new CeilingProbe(ClassAbilityId.ReflectMagic, "affinity",
                () => ReflectMagicAbility.ReflectChance(1, Tunable("class_ability_reflectmagic_chance_base"), Tunable("class_ability_reflectmagic_chance_step"), Saturating, Tunable("class_ability_affinity_chance_cap"))
                    - ReflectMagicAbility.ReflectChance(1, Tunable("class_ability_reflectmagic_chance_base"), Tunable("class_ability_reflectmagic_chance_step"), 1.0, Tunable("class_ability_affinity_chance_cap")),
                () => Max(ClassAbilityId.ReflectMagic, "affinity")),
        };

        // Strength and the per-point rate far past the cap, so max_reduction alone decides the factor.
        private static double ResistFactor() =>
            BattleHardenedAbility.GetDamageResistMod(100000u, 1.0, Tunable("class_ability_battlehardened_max_reduction"));
    }
}

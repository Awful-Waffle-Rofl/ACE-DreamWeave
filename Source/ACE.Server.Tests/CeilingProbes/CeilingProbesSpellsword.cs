using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using static ACE.Server.Tests.CeilingProbeSupport;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Spellsword abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesSpellsword
    {
        private static double ChanceCap => Tunable("class_ability_affinity_chance_cap");

        /// <summary>The Spellblade/Runeblade/Sundermark/Dispelling Dance mods are all expressed as a fraction of the proc chance, in the helper's own unit.</summary>
        private static double GearFraction(ClassAbilityId ability, EquipmentModId mod) =>
            Max(ability, "gear") / EquipmentModRegistry.Get(mod).DisplayScale;

        /// <summary>
        /// What a proc helper that returns float reports for "base + capped affinity" minus "base": the helper rounds each sum to
        /// float, and the probe subtracts two floats (a float subtraction), so the expected added amount is the same three float
        /// roundings, not the exact double cap.
        /// </summary>
        private static double AffinityAdded(double chanceBase, double cap) => F(F(chanceBase + cap) - F(chanceBase));

        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Spellblade: the amount affinity adds to the proc chance is capped (shared chance cap). Isolate it as saturated minus neutral.
            new CeilingProbe(ClassAbilityId.Spellblade, "affinity",
                () => SpellbladeAbility.Chance(1, Tunable("class_ability_spellblade_chance"), Saturating, 0.0, ChanceCap)
                    - SpellbladeAbility.Chance(1, Tunable("class_ability_spellblade_chance"), 1.0, 0.0, ChanceCap),
                () => AffinityAdded(Tunable("class_ability_spellblade_chance"), Max(ClassAbilityId.Spellblade, "affinity"))),

            // Spellblade gear: the mod's StackCap, through the shared cap helper and Chance's gear term (a zero base isolates it).
            new CeilingProbe(ClassAbilityId.Spellblade, "gear",
                () => SpellbladeAbility.Chance(1, 0.0, 1.0, 0.0, 0.0, GearAtSaturation(EquipmentModId.Spellblade)),
                () => F(GearFraction(ClassAbilityId.Spellblade, EquipmentModId.Spellblade))),

            // Runeblade: affinity cap and gear, the same shapes as Spellblade.
            new CeilingProbe(ClassAbilityId.Runeblade, "affinity",
                () => RunebladeAbility.Chance(1, Tunable("class_ability_runeblade_chance"), Saturating, 0.0, ChanceCap)
                    - RunebladeAbility.Chance(1, Tunable("class_ability_runeblade_chance"), 1.0, 0.0, ChanceCap),
                () => AffinityAdded(Tunable("class_ability_runeblade_chance"), Max(ClassAbilityId.Runeblade, "affinity"))),
            new CeilingProbe(ClassAbilityId.Runeblade, "gear",
                () => RunebladeAbility.Chance(1, 0.0, 1.0, 0.0, 0.0, GearAtSaturation(EquipmentModId.Runeblade)),
                () => F(GearFraction(ClassAbilityId.Runeblade, EquipmentModId.Runeblade))),

            // Dispelling Dance: affinity cap and gear (note its Chance takes the gear term before the cap).
            new CeilingProbe(ClassAbilityId.DispellingEdge, "affinity",
                () => DispellingEdgeAbility.Chance(1, Tunable("class_ability_dispellingedge_chance_base"), Tunable("class_ability_dispellingedge_chance_step"), Saturating, 0.0, ChanceCap)
                    - DispellingEdgeAbility.Chance(1, Tunable("class_ability_dispellingedge_chance_base"), Tunable("class_ability_dispellingedge_chance_step"), 1.0, 0.0, ChanceCap),
                () => AffinityAdded(Tunable("class_ability_dispellingedge_chance_base"), Max(ClassAbilityId.DispellingEdge, "affinity"))),
            new CeilingProbe(ClassAbilityId.DispellingEdge, "gear",
                () => DispellingEdgeAbility.Chance(1, 0.0, 0.0, 1.0, GearAtSaturation(EquipmentModId.DispellingEdge)),
                () => F(GearFraction(ClassAbilityId.DispellingEdge, EquipmentModId.DispellingEdge))),

            // Cascade: affinity cap, gear, and the generation budget (a child may hop on only while its generation is under the cap).
            new CeilingProbe(ClassAbilityId.Cascade, "affinity",
                () => CascadeAbility.CascadeChance(1, Tunable("class_ability_cascade_chance_r1"), 0.0, Saturating, ChanceCap)
                    - CascadeAbility.CascadeChance(1, Tunable("class_ability_cascade_chance_r1"), 0.0, 1.0, ChanceCap),
                () => AffinityAdded(Tunable("class_ability_cascade_chance_r1"), Max(ClassAbilityId.Cascade, "affinity"))),
            new CeilingProbe(ClassAbilityId.Cascade, "gear",
                () => CascadeAbility.CascadeChance(1, 0.0, GearAtSaturation(EquipmentModId.Cascade)),
                () => F(GearFraction(ClassAbilityId.Cascade, EquipmentModId.Cascade))),
            new CeilingProbe(ClassAbilityId.Cascade, "generation",
                () =>
                {
                    var max = PropertyManagerLong("class_ability_cascade_max_generation");
                    var hops = 0;
                    while (hops < 1000 && CascadeAbility.CanCascade(true, hops, max))
                        hops++;
                    return hops;
                },
                () => Max(ClassAbilityId.Cascade, "generation")),

            // Sundermark: affinity cap, gear, and the two level safety valves (the Math.Clamp upper bound is the ladder's top, 8).
            new CeilingProbe(ClassAbilityId.Sundermark, "affinity",
                () => SundermarkAbility.Chance(1, Tunable("class_ability_sundermark_chance_base"), Tunable("class_ability_sundermark_chance_step"), Saturating, ChanceCap)
                    - SundermarkAbility.Chance(1, Tunable("class_ability_sundermark_chance_base"), Tunable("class_ability_sundermark_chance_step"), 1.0, ChanceCap),
                () => AffinityAdded(Tunable("class_ability_sundermark_chance_base"), Max(ClassAbilityId.Sundermark, "affinity"))),
            new CeilingProbe(ClassAbilityId.Sundermark, "gear",
                () => SundermarkAbility.Chance(1, 0.0, 0.0, 1.0, 0.0, GearAtSaturation(EquipmentModId.Sundermark)),
                () => F(GearFraction(ClassAbilityId.Sundermark, EquipmentModId.Sundermark))),
            new CeilingProbe(ClassAbilityId.Sundermark, "level",
                () => SundermarkAbility.VulnerabilityMaxLevel(long.MaxValue),
                () => Max(ClassAbilityId.Sundermark, "level")),
            new CeilingProbe(ClassAbilityId.Sundermark, "level_nether",
                () => SundermarkAbility.NetherMaxLevel(long.MaxValue),
                () => Max(ClassAbilityId.Sundermark, "level_nether")),

            // Spellstorm gear: the mod's StackCap, through the shared cap helper and Chance's gear term (a zero base isolates it).
            new CeilingProbe(ClassAbilityId.Spellstorm, "gear",
                () => SpellstormAbility.Chance(1, 0.0, 0.0, GearAtSaturation(EquipmentModId.Spellstorm)),
                () => F(GearFraction(ClassAbilityId.Spellstorm, EquipmentModId.Spellstorm))),

            // Volatile Elements: the amount affinity adds to the weakest-element chance is capped (shared chance cap).
            new CeilingProbe(ClassAbilityId.VolatileElements, "affinity",
                () => VolatileElementsMath.Chance(1, Tunable("class_ability_volatileelements_chance_per_rank"), Saturating, ChanceCap)
                    - VolatileElementsMath.Chance(1, Tunable("class_ability_volatileelements_chance_per_rank"), 1.0, ChanceCap),
                () => Max(ClassAbilityId.VolatileElements, "affinity")),

            // Spellsurge stacks: grow the pool one landed proc at a time through the helper AddSpellsurgeStack uses.
            new CeilingProbe(ClassAbilityId.Spellsurge, "stacks",
                () =>
                {
                    var cap = SpellsurgeAbility.MaxStacks(ClassAbilityText.Live);
                    var stacks = 0;
                    while (stacks < 1000 && SpellsurgeAbility.NextStacks(stacks, cap) != stacks)
                        stacks = SpellsurgeAbility.NextStacks(stacks, cap);
                    return stacks;
                },
                () => Max(ClassAbilityId.Spellsurge, "stacks")),

            // Spellsurge gear: the Surge mod rides the per-stack rate inside the ramp, so one stack at a zero ability rate is the mod's own value.
            new CeilingProbe(ClassAbilityId.Spellsurge, "gear",
                () => SpellsurgeAbility.ProcChanceBonus(1, 1, 0.0, GearAtSaturation(EquipmentModId.Surge)),
                () => F(GearFraction(ClassAbilityId.Spellsurge, EquipmentModId.Surge))),

            // Resonance stacks: grow the pool one landed magic hit at a time through the helper AddResonanceStack uses.
            new CeilingProbe(ClassAbilityId.Resonance, "stacks",
                () =>
                {
                    var cap = ResonanceAbility.MaxStacks(ClassAbilityText.Live);
                    var stacks = 0;
                    while (stacks < 1000 && ResonanceAbility.NextStacks(stacks, cap) != stacks)
                        stacks = ResonanceAbility.NextStacks(stacks, cap);
                    return stacks;
                },
                () => Max(ClassAbilityId.Resonance, "stacks")),

            // Runic Ward: the pool is clamped to the rank's fraction of max health. Inscribe an enormous hit against a 1,000,000 point health pool.
            new CeilingProbe(ClassAbilityId.RunicWard, "total",
                () =>
                {
                    const uint maxHealth = 1_000_000;
                    var fraction = RunicWardMath.CapFraction(3, Tunable("class_ability_runicward_cap_fraction_r1"), Tunable("class_ability_runicward_cap_fraction_r2"), Tunable("class_ability_runicward_cap_fraction_r3"));
                    var ward = RunicWardMath.Inscribe(default, 2_000_000_000u, 1.0, RunicWardMath.Cap(maxHealth, fraction), 0.0, 12.0);
                    return ward.Amount / (double)maxHealth;
                },
                () => Max(ClassAbilityId.RunicWard, "total")),
        };

        private static long PropertyManagerLong(string key) => ACE.Server.Managers.PropertyManager.GetLong(key).Item;
    }
}

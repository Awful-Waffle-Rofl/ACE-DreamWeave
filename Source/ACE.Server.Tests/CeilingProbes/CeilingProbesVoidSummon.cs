using System.Collections.Generic;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using static ACE.Server.Tests.CeilingProbeSupport;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Ceiling probes for the Void/Summon abilities: one per declared ceiling. Each drives the SAME pure helper the combat
    /// hook calls with a saturating input, in the helper's own unit; DeclaredMax is the metadata's raw Value.
    /// </summary>
    public static class CeilingProbesVoidSummon
    {
        /// <summary>Loyalty multiplier the Mana Rush probe runs at: the declared max scales with it (AffinityScalesMax).</summary>
        private const double ManaRushMultiplier = 1.5;

        public static IReadOnlyList<CeilingProbe> All { get; } = new[]
        {
            // Mana Rush total: the cap the live hook passes is MaxBonus(base, Loyalty multiplier), so at multiplier 1.5 the
            // ceiling is the declared base x 1.5 (0.75 at defaults). The added fraction saturates the ramp so the clamp decides.
            new CeilingProbe(ClassAbilityId.NetherRush, "total",
                () => NetherRushAbility.CastSpeedMultiplier(1000, 3, Tunable("class_ability_netherrush_percent_per_rank"), Saturating,
                    NetherRushAbility.MaxBonus(Tunable("class_ability_netherrush_max_bonus"), ManaRushMultiplier)) - 1.0,
                () => F(1.0 + Max(ClassAbilityId.NetherRush, "total") * ManaRushMultiplier) - 1.0),

            // Mana Rush stacks: CastSpeedMultiplier clamps the stack count at MaxStacks (a unit per-stack rate makes the bonus the count).
            new CeilingProbe(ClassAbilityId.NetherRush, "stacks",
                () => NetherRushAbility.CastSpeedMultiplier(1000, 1, 1.0) - 1.0,
                () => Max(ClassAbilityId.NetherRush, "stacks")),

            // Nether Bloom gear: the extra-jump probability the mod gives at its StackCap. ApplyGearBloomChance only compares a roll with
            // the probability, so bisect the roll at which the extra jump stops: that threshold IS the probability the helper applies.
            new CeilingProbe(ClassAbilityId.NetherBloom, "gear",
                () =>
                {
                    var gear = GearAtSaturation(EquipmentModId.NetherBloom);
                    double lo = 0.0, hi = 1.0;
                    for (var i = 0; i < 60; i++)
                    {
                        var mid = (lo + hi) / 2.0;
                        if (NetherBloomAbility.ApplyGearBloomChance(0, gear, mid) > 0)
                            lo = mid;
                        else
                            hi = mid;
                    }
                    return lo;
                },
                () => Max(ClassAbilityId.NetherBloom, "gear") / EquipmentModRegistry.Get(EquipmentModId.NetherBloom).DisplayScale),

            // Soul Tether: the pet damage reduction is clamped at class_ability_soultether_max_reduction.
            new CeilingProbe(ClassAbilityId.SoulTether, "total",
                () => SoulTetherAbility.DamageReduction(3, Tunable("class_ability_soultether_base"), Tunable("class_ability_soultether_step"), Saturating, Tunable("class_ability_soultether_max_reduction")),
                () => Max(ClassAbilityId.SoulTether, "total")),

            // Empowered Summons: the duration multiplier's bonus is clamped at class_ability_empoweredsummons_max_duration_bonus.
            new CeilingProbe(ClassAbilityId.EmpoweredSummons, "total",
                () => EmpoweredSummonsAbility.DurationMultiplier(3, Saturating, Tunable("class_ability_empoweredsummons_max_duration_bonus")) - 1.0,
                () => F(1.0 + Max(ClassAbilityId.EmpoweredSummons, "total")) - 1.0),
        };
    }
}

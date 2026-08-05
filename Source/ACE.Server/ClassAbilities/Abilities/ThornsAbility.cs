using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Requires an equipped shield, and reflects a fraction of the shield's effective armor level
    /// (per rank) back at the attacker on every damaging hit, so the skill scales with shield
    /// quality (gear synergy) rather than damage taken. Monsters can't have Thorns, so no reflect
    /// loops. Reflected damage is attributed to the player, so kill credit and the attacker's
    /// DamageHistory work through the normal path.
    /// </summary>
    public class ThornsAbility : IIncomingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Thorns,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 1,
            Name = "thorns",
            DisplayName = "Thorns",
            Description = "While you have a shield equipped, creatures that strike you take reflected damage " +
                          "equal to 10% of your shield's armor level per rank.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },   // Tier-1 GC: rank 1 always 1 point (the class's power splash)
            Implemented = true,
        };

        public void OnDamageTaken(Player defender, int rank, Creature attacker, DamageType damageType, uint damageTaken)
        {
            // full-strength reflect on any landed hit (contact) - the shield lookup, Shield-skill rider,
            // reflect math, and messaging live in Player.ApplyThornsReflect (shared with the block/parry
            // path). Fires even when the hit is fully mitigated to 0 (still enemy contact - user ruling).
            defender.ApplyThornsReflect(attacker, damageType, 1.0);
        }

        /// <summary>
        /// Pure reflect-damage math (extracted for testability):
        /// (rank * percentPerRank + shieldScale + gearModPercent) of the shield's effective armor level,
        /// rounded. shieldScale is the Shield-skill rider fraction (0 when the skill is untrained). A
        /// negative effective AL (e.g. from a brittlemail debuff) clamps to zero so the skill can never heal
        /// the attacker.
        ///
        /// <paramref name="gearModPercent"/> is the Thorns equipment mod, a STANDALONE mod: same axis,
        /// additive, and reachable at rank 0 (where rank * percentPerRank is 0 and the reflect is the mod
        /// alone). A shield is still required - that is the ability's own condition, kept for the mod.
        /// Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static uint ComputeReflectDamage(double effectiveShieldArmorLevel, int rank, double percentPerRank, double shieldScale = 0.0, double gearModPercent = 0.0)
        {
            var percent = Math.Max(0, rank) * percentPerRank + shieldScale + Math.Max(0.0, gearModPercent);
            return (uint)Math.Round(Math.Max(effectiveShieldArmorLevel, 0) * Math.Max(0.0, percent));
        }

        /// <summary>
        /// Reports the reflect FRACTION (not a damage amount - ComputeReflectDamage above returns damage,
        /// not a percentage): rank term + Shield-skill rider + gear, x100 for display. Mirrors the exact
        /// terms ApplyThornsReflect (Player_ClassAbilityCombat.cs) feeds into ComputeReflectDamage - the two
        /// must stay in step. No cap on the percent itself (only a floor at zero on a negative armor level,
        /// which never triggers here since this is a fraction, not the armor level), so Effective always
        /// equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.Shield,
                PropertyManager.GetDouble("class_ability_thorns_shield_per_trained").Item,
                PropertyManager.GetDouble("class_ability_thorns_shield_per_spec").Item) * 0.01 * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Thorns) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "shield AL",
                Per = null,
                CapNote = null,
            };
        }
    }
}

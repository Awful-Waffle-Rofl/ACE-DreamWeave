using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2, the mirror of Blood Fury (you low = hit harder; them low = hit harder): +4/8/12%
    /// melee damage against targets below the execute threshold (25% HP). Scales with Dirty Fighting - a
    /// deliberate cross-class source that lives in Rogue's Questionable Tactics bundle.
    /// </summary>
    public class ExecutionerAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Executioner,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "executioner",
            DisplayName = "Executioner",
            Description = "Deal 4% more melee damage per rank to targets below 25% health. Higher Dirty Fighting increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.DirtyFighting,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent.CombatType != CombatType.Melee)
                return;

            if (!IsExecuteRange(target, PropertyManager.GetDouble("class_ability_executioner_hp_fraction").Item))
                return;

            // Executioner equipment mod (STANDALONE): additive inside the same parenthesis, and behind the
            // same execute-range condition. The rank-0 half in Player.ApplyEquipmentModOutgoingDamage
            // re-uses IsExecuteRange so the condition is computed identically without this handler existing.
            var bonus = rank * PropertyManager.GetDouble("class_ability_executioner_percent_per_rank").Item
                + attacker.GetClassAbilityScaling(Skill.DirtyFighting,
                    PropertyManager.GetDouble("class_ability_executioner_dirty_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_executioner_dirty_per_spec").Item) * 0.01
                + attacker.GetEquippedModValue(EquipmentModId.Executioner);

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// TRUE when the target is inside the execute window: current health below
        /// <paramref name="hpFraction"/> of its maximum. Extracted so the standalone Executioner equipment
        /// mod can evaluate the identical condition at ability rank 0, where no handler runs. A target with
        /// no maximum health is never in range.
        /// </summary>
        public static bool IsExecuteRange(Creature target, double hpFraction)
        {
            var max = target?.Health.MaxValue ?? 0;

            if (max == 0)
                return false;

            return target.Health.Current / (double)max < hpFraction;
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms in ModifyOutgoingDamage above exactly (x100 for display) -
        /// the two must stay in step. Reports the bonus as if the target were in execute range (this is a
        /// "how strong is the ability" readout, not a "is a target currently below 25% HP" simulation).
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_executioner_percent_per_rank").Item * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.DirtyFighting,
                PropertyManager.GetDouble("class_ability_executioner_dirty_per_trained").Item,
                PropertyManager.GetDouble("class_ability_executioner_dirty_per_spec").Item) * 0.01 * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Executioner) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "melee dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}

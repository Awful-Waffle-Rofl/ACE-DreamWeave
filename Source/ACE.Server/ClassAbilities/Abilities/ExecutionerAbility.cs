using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2, the mirror of Blood Fury (you low = hit harder; them low = hit harder): +4/8/12%
    /// melee damage against targets below the execute threshold (25% HP). Scales with Recklessness (a
    /// deliberate all-in aggression source, 2026-09-12 overhaul - was Dirty Fighting).
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
            Description = "Deal 7% more melee damage per rank to targets below 25% health. Higher Recklessness increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Recklessness,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || target == null || damageEvent.CombatType != CombatType.Melee)
                return;

            if (!IsExecuteRange(target, PropertyManager.GetDouble("class_ability_executioner_hp_fraction").Item))
                return;

            var rankBonus = rank * PropertyManager.GetDouble("class_ability_executioner_percent_per_rank").Item;

            // Recklessness is MULTIPLICATIVE on this ability's OWN rank bonus (2026-09-12 overhaul), not an
            // additive rider beside it - so the skill is worth more the more ranks you have bought, and
            // worth exactly nothing at rank 0. At zero effective Recklessness the multiplier is exactly 1.0,
            // leaving the hit bit-identical to rank alone.
            var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.Recklessness);

            // Executioner equipment mod (STANDALONE): additive inside the same parenthesis, and behind the
            // same execute-range condition. The rank-0 half in Player.ApplyEquipmentModOutgoingDamage
            // re-uses IsExecuteRange so the condition is computed identically without this handler existing.
            // It stays OUTSIDE the affinity multiply: gear must never compound with the affinity skill.
            var bonus = rankBonus * affinity + attacker.GetEquippedModValue(EquipmentModId.Executioner);

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
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective bonus.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank * PropertyManager.GetDouble("class_ability_executioner_percent_per_rank").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Recklessness);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

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

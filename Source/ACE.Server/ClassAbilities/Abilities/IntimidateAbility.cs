using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2 (Berserker pass 2026-10-24, owner ruling; takes the slot the retired Vengeance vacated):
    /// while the player's attack qualifies as reckless (Recklessness trained, melee or missile mode, power bar
    /// between 0.1 and 0.9 - Player.IsRecklessAttackQualifying), the target's defense skill for THAT evade
    /// roll drops by 15 / 30 / 45 by rank, multiplied by Recklessness affinity, floored at 0. Applied BEFORE
    /// the evade roll, in DamageEvent.GetEvadeChance, by name rather than through a dispatch (no hook fires
    /// before the evade roll). PvE only: the call site requires a Player attacker and a non-Player defender,
    /// and a suppressed player (PK facet, arena mask) reads rank 0.
    ///
    /// Carries IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class IntimidateAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public const string DefensePerRankKey = "class_ability_intimidate_defense_per_rank";

        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Intimidate,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "intimidate",
            DisplayName = "Intimidate",
            Description = "While your attack is reckless (Recklessness trained, power bar between 10% and 90%), the " +
                          "monster's defense skill against it is reduced by 15 per rank. Higher Recklessness " +
                          "increases the reduction.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Recklessness,
        };

        /// <summary>
        /// The defense skill left for the roll after a reduction of perRank x rank x affinity, floored at 0.
        /// Unchanged for rank 0 or a non-positive reduction. Pure.
        /// </summary>
        public static uint ReducedDefense(uint defenseSkill, int rank, double perRank, double affinity)
        {
            if (rank <= 0 || perRank <= 0.0)
                return defenseSkill;

            var reduction = perRank * rank * affinity;

            var left = defenseSkill - reduction;

            return left <= 0.0 ? 0u : (uint)Math.Round(left);
        }

        /// <summary>
        /// The call-site entry point (DamageEvent.GetEvadeChance): the roll's defense skill after Intimidate,
        /// or the input unchanged when the player does not own it, is suppressed, or the attack is not reckless.
        /// </summary>
        public static uint ApplyToEvadeRoll(Player attacker, uint rollDefenseSkill)
        {
            if (attacker == null)
                return rollDefenseSkill;

            // TryGetClassAbility is the suppression-aware RANK read (PK facet / arena mask -> false). Not
            // the Rating-family grant read, which returns +3/6/10 rather than a rank.
            if (!attacker.TryGetClassAbility(ClassAbilityId.Intimidate, out var rank) || !attacker.IsRecklessAttackQualifying())
                return rollDefenseSkill;

            return ReducedDefenseForRank(rollDefenseSkill, rank,
                PropertyManager.GetDouble(DefensePerRankKey).Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.Recklessness));
        }

        /// <summary>
        /// The end-to-end pure step ApplyToEvadeRoll takes once it has the RAW owned rank (1-3, clamped): the
        /// roll's defense skill after the 15/30/45 x affinity reduction. Pure, for tests.
        /// </summary>
        public static uint ReducedDefenseForRank(uint rollDefenseSkill, int rawRank, double perRank, double affinity)
            => ReducedDefense(rollDefenseSkill, Math.Clamp(rawRank, 0, 3), perRank, affinity);

        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble(DefensePerRankKey).Item;
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Recklessness);

            var skill = rank <= 0 ? 0.0 : perRank * rank;
            var affinity = skill * multiplier - skill;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "",
                Prefix = "-",
                Label = "target defense (reckless)",
                Per = null,
                CapNote = null,
            };
        }
    }
}

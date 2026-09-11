using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T3: +8% per rank to void (nether) DoT tick damage - Corruption/Corrosion and friends,
    /// the DoT that is void's damage identity. Scales with Creature Enchantment (debuff mastery). Bespoke:
    /// applied to the caster's nether DoT ticks in EnchantmentManager.ApplyDamageTick via
    /// Player.GetWitheringVoidDotMod. Carries IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class WitheringAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Withering,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 3,
            Name = "withering",
            DisplayName = "Withering",
            Description = "Increases your void damage-over-time tick damage by 8% per rank (+24% at rank 3). " +
                          "Higher Creature Enchantment increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.CreatureEnchantment,
        };

        /// <summary>
        /// The void-DoT tick multiplier at a given rank (1.0 = none): 1 + rank*perRank + the Creature
        /// Enchantment rider fraction + the equipment-mod term. Pure for testability.
        ///
        /// <paramref name="gearModFraction"/> is the Withering equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction. Defaults to 0,
        /// which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float DotMultiplier(int rank, double percentPerRank, double creatureEnchantFraction, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, creatureEnchantFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms fed into DotMultiplier above (x100 for display) - the two
        /// must stay in step.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_withering_percent_per_rank").Item) * 100.0;

            var affinity = (rank <= 0 ? 0.0 : player.GetClassAbilityScaling(Skill.CreatureEnchantment,
                PropertyManager.GetDouble("class_ability_withering_creatureench_per_trained").Item,
                PropertyManager.GetDouble("class_ability_withering_creatureench_per_spec").Item) * 0.01) * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Withering) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "void dot",
                Per = null,
                CapNote = null,
            };
        }
    }
}

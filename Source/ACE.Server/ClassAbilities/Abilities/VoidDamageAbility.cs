using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T3: a flat void-spell damage bonus per rank (+8%/rank, +24% at rank 3) - the deep
    /// damage payoff of the void line. Deliberately unscaled (SKILL-TABLES-PREVIEW). Bespoke: applied as
    /// a multiplier on the finalDamage of a void SpellProjectile via Player.GetClassAbilitySpellDamageMod.
    /// Carries IPassiveStatAbility.
    /// </summary>
    public class VoidDamageAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.VoidDamage,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 3,
            Name = "voiddamage",
            DisplayName = "Void Damage",
            Description = "Increases your void-magic damage by 8% per rank (+24% at rank 3).",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
        };

        /// <summary>
        /// The void-damage multiplier at a given rank (1.0 = none): 1 + rank*perRank + the equipment-mod
        /// term. <paramref name="gearModFraction"/> is the Void Damage equipment mod, a STANDALONE mod:
        /// same axis, additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction.
        /// Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float DamageMultiplier(int rank, double percentPerRank, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank;
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the rank/gear terms fed into DamageMultiplier above (x100 for display) - the two must
        /// stay in step. No affinity rider exists for this ability.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_voiddamage_percent_per_rank").Item) * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.VoidDamage) * 100.0;
            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "void dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}

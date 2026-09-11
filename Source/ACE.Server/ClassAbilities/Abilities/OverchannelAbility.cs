using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T2: +8% damage per rank on ALL war spells (+24% at rank 3), scaled by Arcane Lore. No
    /// longer single-projectile-only (user, 2026-07-15) - the bonus synergizes with Spell AOE, carrying
    /// into the radiated copies because both the primary and each child route through
    /// SpellProjectile.CalculateDamage (Player.GetClassAbilitySpellDamageMod).
    ///
    /// Its resource cost lives elsewhere: a qualifying war cast pays +100% mana (Player.
    /// GetClassAbilityManaSurcharge), stacking with Spell AOE's surcharge to +200% on an Arc war cast when
    /// both are learned. Bespoke; carries IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class OverchannelAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Overchannel,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 2,
            Name = "overchannel",
            DisplayName = "Overchannel",
            Description = "Increases all of your war-magic damage by 8% per rank (+24% at rank 3); higher " +
                          "Arcane Lore increases the bonus. Boosted casts cost +100% mana (stacking with " +
                          "Spell AOE to +200% on an Arc war cast).",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ArcaneLore,
        };

        /// <summary>
        /// The war-spell damage multiplier at a given rank (1.0 = none): 1 + rank*perRank + the Arcane
        /// Lore rider fraction + the equipment-mod term. Pure for testability (mirrors AttackSpeedAbility /
        /// VoidDamageAbility).
        ///
        /// <paramref name="gearModFraction"/> is the Overchannel equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction. The ability's own
        /// per-rank base and Arcane Lore rider stay rank-gated. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float DamageMultiplier(int rank, double percentPerRank, double arcaneLoreFraction, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, arcaneLoreFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms fed into DamageMultiplier above (x100 for display) - the
        /// two must stay in step.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_overchannel_percent_per_rank").Item) * 100.0;

            var affinity = (rank <= 0 ? 0.0 : player.GetClassAbilityScaling(Skill.ArcaneLore,
                PropertyManager.GetDouble("class_ability_overchannel_arcanelore_per_trained").Item,
                PropertyManager.GetDouble("class_ability_overchannel_arcanelore_per_spec").Item) * 0.01) * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Overchannel) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "war dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}

using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T2: combat-pet stats (health, damage, defenses) scale up per rank, with a Leadership
    /// rider on the stat bonus. Bespoke: applied to a freshly summoned CombatPet in CombatPet.Init via
    /// Player.GetEmpoweredSummonsStatMod. Carries IPassiveStatAbility because it hooks no combat event of
    /// its own.
    ///
    /// The doc also ties pet *duration* to Loyalty, but ACE combat pets carry no server-enforced
    /// lifespan (they persist until slain or the owner leaves range), so the duration half has nothing to
    /// attach to yet and is intentionally deferred to the pet-subsystem design pass.
    /// </summary>
    public class EmpoweredSummonsAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EmpoweredSummons,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 2,
            Name = "empoweredsummons",
            DisplayName = "Empowered Summons",
            Description = "Your combat pets are stronger - more health, damage, and defenses per rank. " +
                          "Higher Leadership increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Leadership,
        };

        /// <summary>
        /// The combat-pet stat multiplier at a given rank (1.0 = none): 1 + rank*perRank + the Leadership
        /// rider fraction + the equipment-mod term. Pure for testability.
        ///
        /// <paramref name="gearModFraction"/> is the Empowered Summons equipment mod, a STANDALONE mod:
        /// same axis, additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction and a
        /// player who never learned the ability still gets a slightly stronger pet. Defaults to 0, which
        /// reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float StatMultiplier(int rank, double percentPerRank, double leadershipFraction, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, leadershipFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms fed into StatMultiplier above (x100 for display) - the two
        /// must stay in step.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_empoweredsummons_percent_per_rank").Item) * 100.0;

            var affinity = (rank <= 0 ? 0.0 : player.GetClassAbilityScaling(Skill.Leadership,
                PropertyManager.GetDouble("class_ability_empoweredsummons_leadership_per_trained").Item,
                PropertyManager.GetDouble("class_ability_empoweredsummons_leadership_per_spec").Item) * 0.01) * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.EmpoweredSummons) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "pet stats",
                Per = null,
                CapNote = null,
            };
        }
    }
}

using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T3 game-changer: one activation of a summoning essence brings out TWO combat pets, for a
    /// single charge and a single cooldown. Deliberately unscaled (SKILL-TABLES-PREVIEW). Bespoke:
    /// PetDevice.ActOnUse calls SummonCreature a second time in the same activation, and the normal
    /// single-pet gate (Pet.HandleCurrentActivePet_* / PetDevice.CheckUseRequirements) consults
    /// Player.CanSummonAdditionalCombatPet, which admits that second CombatPet into
    /// Player.SecondaryActivePet. Carries IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class Summon2xSkill : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Summon2x,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 3,
            Name = "summon2x",
            DisplayName = "Summon 2x",
            Description = "Your summoning essence calls up two combat pets at once, for a single charge.",
            MaxRank = 1,
            CostPerRank = new[] { 3 },
            Implemented = true,
        };

        /// <summary>
        /// Pure shape change (a second concurrent pet slot) with no scalable magnitude - no single number
        /// is worth printing.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank) => new ClassAbilityReadout { HasValue = false };
    }
}

using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T2: +4/8/12% accuracy - a flat percentage bonus to the archer's effective missile attack
    /// skill at the to-hit roll (never melee). Deliberately unscaled (SKILL-TABLES-PREVIEW). Bespoke:
    /// applied in Player.GetEffectiveAttackSkill via Player.GetEagleEyeAccuracyMod when the current weapon
    /// is a missile weapon. Carries IPassiveStatAbility because it hooks no combat event of its own.
    /// </summary>
    public class EagleEyeAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EagleEye,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 2,
            Name = "eagleeye",
            DisplayName = "Eagle Eye",
            Description = "Increases your missile accuracy by 4/8/12% (by rank) - a bonus to your effective " +
                          "missile attack skill at the to-hit roll.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
        };

        /// <summary>
        /// The accuracy multiplier at a given rank (1.0 = none): 1 + rank*perRank + the equipment-mod
        /// term. Ranks 1-3 with the 0.04 default -> +4/8/12%. Pure for testability.
        ///
        /// <paramref name="gearModFraction"/> is the Eagle Eye equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction. Defaults to 0,
        /// which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float AccuracyMultiplier(int rank, double percentPerRank, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank;
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Mirrors the terms Player.GetEagleEyeAccuracyMod (Player_ClassAbilityBuffs.cs) feeds into
        /// AccuracyMultiplier above - the two must stay in step. No affinity rider exists for this ability
        /// (deliberately unscaled). No cap, so Effective always equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_eagleeye_percent_per_rank").Item * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.EagleEye) * 100.0;

            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "accuracy",
                Per = null,
                CapNote = null,
            };
        }
    }
}

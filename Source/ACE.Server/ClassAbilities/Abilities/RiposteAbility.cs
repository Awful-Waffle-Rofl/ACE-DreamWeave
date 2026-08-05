using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T2: when you Parry an attack, you instantly counter with a free weapon strike at 40/70/100%
    /// of normal damage (by rank). The counter is a real landed hit routed through the normal attack path
    /// (Player.DamageTarget), so Poison Weapon and Acid Proc apply. Only does anything with Parry - the
    /// Rogue mirror of Shield Check (the Vanguard turns parries into reflects, the Rogue into offense).
    /// Read in Player.OnClassAbilityAttackAvoided when a hit is parried. Carries IPassiveStatAbility.
    /// </summary>
    public class RiposteAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Riposte,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 2,
            Name = "riposte",
            DisplayName = "Riposte",
            Description = "When you Parry a melee attack, counter with a free weapon strike at 40/70/100% damage by rank. " +
                          "The counter is a real hit, so Poison Weapon and Acid Proc apply. Requires Parry.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
        };

        /// <summary>
        /// Counter-strike damage fraction at a given rank: base + (rank-1)*step (0.4/0.7/1.0), plus the
        /// equipment-mod term.
        ///
        /// <paramref name="gearModFraction"/> is the Riposte equipment mod, a MACHINERY mod: it amplifies the
        /// ability's own counter and stays behind the same rank check - there is no counter-strike to
        /// strengthen without Riposte. Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static double CounterFraction(int rank, double baseFraction, double stepPerRank, double gearModFraction = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Max(0.0, baseFraction + (rank - 1) * stepPerRank + Math.Max(0.0, gearModFraction));
        }

        /// <summary>
        /// Mirrors the terms Player.TriggerRiposte (Player_ClassAbilityCombat.cs) feeds into CounterFraction
        /// above - the two must stay in step. No affinity rider exists for this ability. No cap on the
        /// counter fraction itself, so Effective always equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseFraction = PropertyManager.GetDouble("class_ability_riposte_fraction_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_riposte_fraction_step").Item;

            var skill = rank <= 0 ? 0.0 : (baseFraction + (rank - 1) * stepPerRank) * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.Riposte) * 100.0;

            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "riposte",
                Per = null,
                CapNote = null,
            };
        }
    }
}

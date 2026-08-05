using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T2: while a shield is equipped, attacks you Parry trigger Thorns at 40/70/100% reflect
    /// strength (by rank). Only does anything with Parry (Rogue T2) and Thorns (Vanguard T1) - a
    /// deliberate cross-class bridge for the deep tank. Read in Player.OnClassAbilityAttackAvoided when a
    /// hit is parried. Carries IPassiveStatAbility.
    /// </summary>
    public class ShieldCheckAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ShieldCheck,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 2,
            Name = "shieldcheck",
            DisplayName = "Shield Check",
            Description = "While a shield is equipped, attacks you Parry also trigger Thorns at 40/70/100% strength by rank. " +
                          "Requires Parry and Thorns.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
        };

        /// <summary>
        /// Thorns reflect-strength fraction a parried hit triggers at a given rank: base + (rank-1)*step
        /// (0.4/0.7/1.0), plus the equipment-mod term, capped at 1.0 (a parry can never reflect MORE than a
        /// block does).
        ///
        /// <paramref name="gearModStrength"/> is the Shield Check equipment mod, a MACHINERY mod: it
        /// amplifies the ability's own parry-to-thorns conversion and stays behind the same rank check. Note
        /// the mod is intentionally dead weight at rank 3, where the ability already converts at 100% - that
        /// was priced and accepted as trade fodder. Defaults to 0, which reproduces the pre-equipment-mod
        /// behavior exactly (the cap cannot bite without a mod, since the ability tops out at exactly 1.0).
        /// </summary>
        public static double ReflectStrength(int rank, double baseStrength, double stepPerRank, double gearModStrength = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var abilityStrength = Math.Max(0.0, baseStrength + (rank - 1) * stepPerRank);
            var gear = Math.Max(0.0, gearModStrength);

            // with no mod this is byte-for-byte the original expression - the 100% ceiling is deliberately
            // only imposed on the MOD's contribution, so an operator who tunes the ability itself above 1.0
            // is not silently clamped by a change that was meant to bound gear
            if (gear <= 0.0)
                return abilityStrength;

            return Math.Clamp(abilityStrength + gear, 0.0, Math.Max(1.0, abilityStrength));
        }

        /// <summary>
        /// Calls the real ReflectStrength composition above for Effective (rather than restating its clamp),
        /// so this can never drift from the actual parry-to-thorns conversion. ReflectStrength clamps
        /// abilityStrength + gear to Math.Max(1.0, abilityStrength) - so at rank 3 (abilityStrength == 1.0)
        /// the gear term contributes nothing. CapNote is "100% ceiling" exactly when that clamp reduces the
        /// value below the raw abilityStrength + gear sum.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseStrength = PropertyManager.GetDouble("class_ability_shieldcheck_strength_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_shieldcheck_strength_step").Item;
            var gear = player.GetEquippedModValue(EquipmentModId.ShieldCheck);

            var abilityStrength = Math.Max(0.0, baseStrength + (rank - 1) * stepPerRank);
            var effective = ReflectStrength(rank, baseStrength, stepPerRank, gear);

            var rawTotal = abilityStrength + Math.Max(0.0, gear);
            var capBit = gear > 0.0 && effective < rawTotal - 0.0000001;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = abilityStrength * 100.0,
                Affinity = 0.0,
                Gear = gear * 100.0,
                Effective = effective * 100.0,
                Unit = "%",
                Label = "reflect %",
                Per = null,
                CapNote = capBit ? "100% ceiling" : null,
            };
        }
    }
}

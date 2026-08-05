using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T2: melee damage rises as the attacker's own health falls - nothing above the start
    /// fraction (75% HP), ramping linearly to +10/20/30% (by rank) at/below the peak fraction (25% HP).
    /// A conditional bonus (you have to be hurt), which is why it is a plain T2 entry rather than the
    /// game-changer.
    /// </summary>
    public class BloodFuryAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.BloodFury,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 2,
            Name = "bloodfury",
            DisplayName = "Blood Fury",
            Description = "Your melee damage grows as your health drops below 75%, up to +10% per rank at 25% health.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Melee)
                return;

            var max = attacker.Health.MaxValue;
            if (max == 0)
                return;

            var hpFraction = attacker.Health.Current / (double)max;

            // Blood Fury equipment mod (STANDALONE): another additive term on the SAME peak, so it inherits
            // the ability's own low-health ramp instead of becoming an unconditional bonus. The rank-0 half
            // in Player.ApplyEquipmentModOutgoingDamage calls LowHealthBonus with the mod value as the peak,
            // which is this identical expression with the ability's terms zeroed.
            var peak = rank * PropertyManager.GetDouble("class_ability_bloodfury_peak_per_rank").Item
                + attacker.GetEquippedModValue(EquipmentModId.BloodFury);

            var bonus = LowHealthBonus(hpFraction, peak,
                PropertyManager.GetDouble("class_ability_bloodfury_start_hp_fraction").Item,
                PropertyManager.GetDouble("class_ability_bloodfury_peak_hp_fraction").Item);

            if (bonus > 0.0)
                damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// Pure ramp math (testable): 0 at/above startFraction, ramping linearly to peakBonus at/below
        /// peakFraction. A degenerate range (start &lt;= peak) yields no bonus.
        /// </summary>
        public static double LowHealthBonus(double hpFraction, double peakBonus, double startFraction, double peakFraction)
        {
            if (peakBonus <= 0.0 || startFraction <= peakFraction || hpFraction >= startFraction)
                return 0.0;

            var t = Math.Clamp((startFraction - hpFraction) / (startFraction - peakFraction), 0.0, 1.0);
            return t * peakBonus;
        }

        /// <summary>
        /// Reports the PEAK bonus (the ramp's ceiling at/below the low-health threshold), not the current
        /// instantaneous ramp value - a readout that changes as the player takes damage would not be useful
        /// in a list. Mirrors the "peak" term built in ModifyOutgoingDamage above (rank term + gear, no
        /// affinity rider exists for this ability) - the two must stay in step.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_bloodfury_peak_per_rank").Item * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.BloodFury) * 100.0;
            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
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

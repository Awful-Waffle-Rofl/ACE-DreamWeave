using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T3: missile damage scales with distance to the target - no bonus at close range, ramping
    /// linearly to +10/20/30% (by rank) at long range. Deliberately situational: an archer performs
    /// better in the open than in a close dungeon (user). Thresholds are tunable.
    /// </summary>
    public class LongDrawAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.LongDraw,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 3,
            Name = "longdraw",
            DisplayName = "Long Draw",
            Description = "Your missile damage grows with distance to the target: no bonus up close, up to " +
                          "+10% per rank at long range.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Missile)
                return;

            if (attacker.Location == null || target?.Location == null)
                return;

            var distance = attacker.Location.DistanceTo(target.Location);

            // Long Draw equipment mod (STANDALONE): the peak of the SAME ramp, so the mod is additive
            // inside the parenthesis and inherits the ability's exact distance curve. The rank-0 half in
            // Player.ApplyEquipmentModOutgoingDamage re-uses DistanceBonus with rank 1 and the mod value as
            // the per-rank peak, which is the identical expression with the ability's terms zeroed.
            var peak = rank * PropertyManager.GetDouble("class_ability_longdraw_percent_per_rank").Item
                + attacker.GetEquippedModValue(EquipmentModId.LongDraw);

            var bonus = DistanceBonus(distance, 1, peak,
                PropertyManager.GetDouble("class_ability_longdraw_min_distance").Item,
                PropertyManager.GetDouble("class_ability_longdraw_max_distance").Item);

            if (bonus > 0.0)
                damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// Pure distance-ramp math (testable): 0 at/below minDistance, ramping linearly to rank*peakPerRank
        /// at/above maxDistance. A degenerate range (max &lt;= min) yields no bonus.
        /// </summary>
        public static double DistanceBonus(double distance, int rank, double peakPerRank, double minDistance, double maxDistance)
        {
            if (rank <= 0 || maxDistance <= minDistance)
                return 0.0;

            var t = Math.Clamp((distance - minDistance) / (maxDistance - minDistance), 0.0, 1.0);
            return t * rank * peakPerRank;
        }

        /// <summary>
        /// Reports the PEAK bonus (at/above max range), mirroring the "peak" term fed into DistanceBonus in
        /// ModifyOutgoingDamage above - the two must stay in step. No affinity rider exists for this ability.
        /// No cap on the peak itself, so Effective always equals Total. The actual in-combat bonus ranges
        /// from 0 up to this peak depending on distance to target - this readout is "how strong at best",
        /// not "how strong right now" (no target is available at readout time).
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_longdraw_percent_per_rank").Item * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.LongDraw) * 100.0;

            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "range dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}

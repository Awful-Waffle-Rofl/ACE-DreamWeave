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
    ///
    /// AFFINITY: Missile Weapons MULTIPLIES this ability's own rank term (the peak), at the shared affinity
    /// rate, via the single-argument GetClassAbilityAffinityMultiplier(Skill) overload - the same shape Void
    /// Damage uses for Void Magic. At zero effective Missile Weapons the factor is exactly 1.0 and the bonus
    /// is bit-identical to rank alone. The equipment-mod term sits OUTSIDE the multiply.
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
                          "+10% per rank at long range. Higher Missile Weapons increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.MissileWeapons,
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
            // Missile Weapons multiplies the RANK term only (rank 0 contributes nothing to multiply); the gear
            // mod is added after, unscaled, exactly as the readout reports it.
            var peak = Peak(rank, PropertyManager.GetDouble("class_ability_longdraw_percent_per_rank").Item,
                attacker.GetClassAbilityAffinityMultiplier(Skill.MissileWeapons),
                attacker.GetEquippedModValue(EquipmentModId.LongDraw));

            var bonus = DistanceBonus(distance, 1, peak,
                PropertyManager.GetDouble("class_ability_longdraw_min_distance").Item,
                PropertyManager.GetDouble("class_ability_longdraw_max_distance").Item);

            if (bonus > 0.0)
                damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// The peak bonus (at/above max range) as a fraction: rank * perRank * affinityMultiplier + gear.
        /// The affinity multiplies the RANK term only; the gear term is added after, unscaled. Used by BOTH
        /// ModifyOutgoingDamage and GetReadout so the two cannot drift. A multiplier below 1.0 is floored at
        /// 1.0 (neutral), and rank 0 yields only the gear term.
        /// </summary>
        public static double Peak(int rank, double perRank, double affinityMultiplier, double gear)
        {
            var rankTerm = rank <= 0 ? 0.0 : rank * perRank * Math.Max(1.0, affinityMultiplier);

            return rankTerm + Math.Max(0.0, gear);
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
        /// ModifyOutgoingDamage above - the two must stay in step. Affinity is the AMOUNT the Missile Weapons
        /// multiply adds to the rank term, so Skill + Affinity + Gear sums to Effective.
        /// No cap on the peak itself, so Effective always equals Total. The actual in-combat bonus ranges
        /// from 0 up to this peak depending on distance to target - this readout is "how strong at best",
        /// not "how strong right now" (no target is available at readout time).
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_longdraw_percent_per_rank").Item;

            // The null-conditionals are for the readout unit tests, which call this with a null Player
            // because Player's static initializer cannot run under the test host. Neutral values: 1.0 for
            // the multiplier, 0 for the additive gear term.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.MissileWeapons) ?? 1.0;

            var gearFraction = player?.GetEquippedModValue(EquipmentModId.LongDraw) ?? 0.0;

            var skill = rankBonus * 100.0;
            var gear = Math.Max(0.0, gearFraction) * 100.0;

            // Effective comes from the SAME Peak ModifyOutgoingDamage uses; Affinity is the remainder, so
            // Skill + Affinity + Gear sums to Effective by construction.
            var total = Peak(rank, PropertyManager.GetDouble("class_ability_longdraw_percent_per_rank").Item, multiplier, gearFraction) * 100.0;
            var affinity = total - skill - gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
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

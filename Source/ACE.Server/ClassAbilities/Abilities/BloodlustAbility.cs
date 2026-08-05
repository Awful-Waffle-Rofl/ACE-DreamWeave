using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Berserker T3: melee hits heal a fraction of the damage dealt (1/2/3% by rank), scaling with
    /// Salvaging (harvest the fallen). Feeds the Blood Fury loop - live at low HP without dying there.
    /// PvE only (the outgoing-damage dispatch already excludes player targets).
    /// </summary>
    public class BloodlustAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Bloodlust,
            AbilityClass = ClassAbilityClass.Berserker,
            Tier = 3,
            Name = "bloodlust",
            DisplayName = "Bloodlust",
            Description = "Your melee hits heal you for 1% of the damage dealt per rank. Higher Salvaging increases the lifesteal.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Melee || damageEvent.Damage <= 0.0f)
                return;

            // Bloodlust equipment mod (STANDALONE): the same lifesteal axis, but kept as a SEPARATE fraction
            // rather than summed into the ability's, because the two are integerized differently - see
            // ApplyLifesteal. The rank-0 half in Player.ApplyEquipmentModOutgoingDamage passes an ability
            // fraction of 0 and the mod alone.
            var abilityFraction = rank * PropertyManager.GetDouble("class_ability_bloodlust_percent_per_rank").Item
                + attacker.GetClassAbilityScaling(Skill.Salvaging,
                    PropertyManager.GetDouble("class_ability_bloodlust_salvage_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_bloodlust_salvage_per_spec").Item) * 0.01;

            ApplyLifesteal(attacker, damageEvent.Damage, abilityFraction, attacker.GetEquippedModValue(EquipmentModId.Bloodlust));
        }

        /// <summary>
        /// Heals the attacker for a fraction of the damage dealt. Shared with the standalone Bloodlust
        /// equipment mod's rank-0 path (which passes an ability fraction of 0) so both heal identically.
        /// UpdateVitalDelta clamps to max, so an over-heal is harmless.
        ///
        /// The two fractions are integerized SEPARATELY and on purpose:
        ///   - <paramref name="abilityFraction"/> keeps the original per-hit Math.Round, so a player with no
        ///     equipment mods heals bit-identically to before the mod system existed. This mirrors the freeze
        ///     in PoisonWeaponAbility.ComputePoisonDamage.
        ///   - <paramref name="gearFraction"/> is ACCUMULATED across hits (see <see cref="AccrueHeal"/>).
        ///     Bloodlust's lifesteal is a small percentage of one hit, so a low roll would round to zero on
        ///     every single hit and the mod would be permanently dead - the exact "useless mod" the potency
        ///     floors elsewhere prevent. A floor cannot fix it here (it would need ~0.8 potency at the damage
        ///     anchor, making the mod near-deterministic), so instead the sub-point remainder carries forward
        ///     and pays out once it adds up. That extends the "fractional is additive" rule across TIME, the
        ///     way the equipped-item sum extends it across items.
        /// </summary>
        public static void ApplyLifesteal(Player attacker, float damage, double abilityFraction, double gearFraction = 0.0)
        {
            if (attacker == null || damage <= 0.0f)
                return;

            var heal = 0;

            if (abilityFraction > 0.0)
                heal += (int)Math.Round(damage * abilityFraction);

            if (gearFraction > 0.0)
                heal += attacker.AccrueBloodlustGearHeal(damage * gearFraction);

            if (heal <= 0)
                return;

            attacker.UpdateVitalDelta(attacker.Health, heal);
        }

        /// <summary>
        /// Adds one hit's exact (fractional) gear lifesteal to the carried remainder and returns the whole
        /// points to pay out now, leaving the new remainder in <paramref name="newCarry"/>.
        ///
        /// Rounds the running total half-up, consistent with the poison precedent, so the carry lands in
        /// [-0.5, 0.5] and the mod can pay slightly ahead rather than only ever lagging. That bound is what
        /// makes the scheme DRIFT-FREE: after any number of hits the total healed is within half a point of
        /// the exact total, so accumulation changes only WHEN the value arrives, never HOW MUCH.
        /// Pure (no player state) so the sequence behavior is unit-testable.
        /// </summary>
        public static int AccrueHeal(double carry, double exactHeal, out double newCarry)
        {
            if (double.IsNaN(carry) || double.IsInfinity(carry))
                carry = 0.0;

            newCarry = carry;

            if (double.IsNaN(exactHeal) || double.IsInfinity(exactHeal) || exactHeal <= 0.0)
                return 0;

            var total = carry + exactHeal;
            var heal = (int)Math.Round(total, MidpointRounding.AwayFromZero);

            if (heal <= 0)
            {
                newCarry = total;
                return 0;
            }

            newCarry = total - heal;
            return heal;
        }

        /// <summary>
        /// Mirrors the ability-fraction terms fed into ApplyLifesteal above (x100 for display) - the two
        /// must stay in step. Gear reports the raw equipment-mod FRACTION (not the accumulated carry
        /// AccrueHeal tracks per player), since the carry is a rounding remainder rather than a strength
        /// number.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_bloodlust_percent_per_rank").Item * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.Salvaging,
                PropertyManager.GetDouble("class_ability_bloodlust_salvage_per_trained").Item,
                PropertyManager.GetDouble("class_ability_bloodlust_salvage_per_spec").Item) * 0.01 * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Bloodlust) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "lifesteal",
                Per = null,
                CapNote = null,
            };
        }
    }
}

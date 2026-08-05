using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T2, the resource-surcharge family: +6/12/18% missile damage, but each empowered shot costs
    /// extra stamina. If the player lacks the stamina the shot lands normally with no bonus (and no
    /// stamina is spent) - so the surcharge makes the overabundant late-game stamina pool matter. Higher
    /// Run reduces the extra stamina cost, up to a hard cap.
    /// </summary>
    public class HeavyDrawAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.HeavyDraw,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 2,
            Name = "heavydraw",
            DisplayName = "Heavy Draw",
            Description = "Your missile attacks hit for 6% more per rank but each shot costs extra stamina. " +
                          "With insufficient stamina the shot lands normally with no bonus. " +
                          "Higher Run reduces the extra stamina cost.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Missile)
                return;

            var baseCost = (int)PropertyManager.GetDouble("class_ability_heavydraw_stamina_cost").Item;
            if (baseCost > 0)
            {
                // Heavy Draw's Run rider (COST REDUCTION, not a damage bonus): higher effective Run
                // reduces the stamina surcharge, clamped so it can never be reduced past the configured
                // floor. Untrained Run reproduces baseCost exactly (reduction fraction = 0).
                var runReduction = attacker.GetClassAbilityScaling(Skill.Run,
                    PropertyManager.GetDouble("class_ability_heavydraw_run_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_heavydraw_run_per_spec").Item) * 0.01;
                var reductionCap = PropertyManager.GetDouble("class_ability_heavydraw_run_reduction_cap").Item;

                var cost = EffectiveStaminaCost(baseCost, runReduction, reductionCap);

                if (cost > 0)
                {
                    if (attacker.Stamina.Current < cost)
                        return;
                    attacker.UpdateVitalDelta(attacker.Stamina, -cost);
                }
            }

            // Heavy Draw equipment mod (MACHINERY): inside the ability's own parenthesis, same axis, and
            // deliberately read AFTER the stamina-affordability gate above - an unaffordable shot lands with
            // no bonus at all, mod included, exactly as the ability's own bonus behaves.
            var bonus = rank * PropertyManager.GetDouble("class_ability_heavydraw_percent_per_rank").Item
                + attacker.GetEquippedModValue(EquipmentModId.HeavyDraw);

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// The actual stamina cost charged for a Heavy Draw shot, after the Run rider's reduction fraction
        /// is applied and clamped to <paramref name="reductionCap"/> (0 = no reduction, so this reproduces
        /// <paramref name="baseCost"/> exactly when Run is untrained). Pure for testability.
        /// </summary>
        public static int EffectiveStaminaCost(int baseCost, double runReductionFraction, double reductionCap)
        {
            var reduction = Math.Clamp(runReductionFraction, 0.0, reductionCap);
            return (int)Math.Round(baseCost * (1.0 - reduction));
        }

        /// <summary>
        /// Mirrors the rank/gear terms in ModifyOutgoingDamage above exactly (x100 for display) - the two
        /// must stay in step. Affinity is deliberately 0.0: the Run rider reduces the stamina SURCHARGE, not
        /// the damage bonus, so it never belongs on this damage readout (see class doc comment and
        /// EffectiveStaminaCost). Deliberately ignores the stamina gate, like SavageBlows. No cap on the
        /// damage bonus itself, so Effective always equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_heavydraw_percent_per_rank").Item * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.HeavyDraw) * 100.0;

            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "missile dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}

using System;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 1: scales missile hit damage by distance to the defender - the monster-side mirror of
    /// LongDrawAbility, reusing its exact distance curve. Melee hits are untouched.
    /// </summary>
    public sealed class RangeRampEffect : IMonsterEffect, IMonsterOutgoingHit
    {
        public string Kind => "rangeramp";

        /// <summary>
        /// Runs ahead of every reader on its hooks: this effect CHANGES the damage figure they size
        /// themselves from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            if (spec.GetDouble("peak", 0.0) <= 0.0)
            {
                error = "rangeramp requires peak= > 0";
                return false;
            }

            var min = spec.GetDouble("min", 0.0);
            var max = spec.GetDouble("max", 0.0);
            if (max <= min)
            {
                error = "rangeramp requires max= > min=";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damageEvent.CombatType != CombatType.Missile || damageEvent.Damage <= 0.0f)
                return;

            var peak = spec.GetDouble("peak", 0.0);
            if (peak <= 0.0)
                return;

            if (attacker.Location == null || defender.Location == null)
                return;

            var min = spec.GetDouble("min", 0.0);
            var max = spec.GetDouble("max", 0.0);

            var distance = attacker.Location.DistanceTo(defender.Location);

            var bonus = LongDrawAbility.DistanceBonus(distance, 1, peak, min, max);
            if (bonus <= 0.0)
                return;

            var extra = Math.Min(damageEvent.Damage * bonus, MonsterEffectCaps.DamageRiderCap);
            if (extra <= 0.0)
                return;

            damageEvent.Damage += (float)extra;
        }
    }
}

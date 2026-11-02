using System;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 1: scales up the damage of a landed hit when the defender is at or below a health threshold.
    /// Reuses ExecutionerAbility.IsExecuteRange for the threshold check so the condition matches the player
    /// ability's exactly.
    ///
    /// Mutates DamageEvent.Damage in place rather than dealing a separate proc - the same shape
    /// ExecutionerAbility itself uses - so the scaled amount is what the defender actually takes, not an
    /// addition on top of the unscaled hit.
    /// </summary>
    public sealed class ExecuteEffect : IMonsterEffect, IMonsterOutgoingHit
    {
        public string Kind => "execute";

        /// <summary>
        /// The largest bonus= Validate accepts. bonus= is a MULTIPLIER of the landed hit, not a flat
        /// amount: the handler adds damage * bonus, so 0.4 is +40% and 40 would be +4000% (a 41x hit,
        /// bounded only by monster_effect_damage_rider_cap). A value above this is almost certainly an
        /// author who read bonus= as a flat amount or a percent, so the record is rejected whole at
        /// construction rather than run.
        /// </summary>
        public const double MaxBonus = 2.0;

        /// <summary>
        /// Runs ahead of every reader on its hooks: this effect CHANGES the damage figure they size
        /// themselves from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var bonus = spec.GetDouble("bonus", 0.0);
            if (bonus <= 0.0)
            {
                error = "execute requires bonus= > 0";
                return false;
            }

            if (bonus > MaxBonus)
            {
                error = $"execute bonus= is a MULTIPLIER of the landed hit (0.4 = +40%), not a flat amount, and must be <= {MaxBonus}";
                return false;
            }

            var hp = spec.GetDouble("hp", 0.25);
            if (hp <= 0.0 || hp > 1.0)
            {
                error = "execute hp= must be in (0, 1]";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damageEvent.Damage <= 0.0f)
                return;

            var bonus = spec.GetDouble("bonus", 0.0);
            if (bonus <= 0.0)
                return;

            var hp = spec.GetDouble("hp", 0.25);

            if (!ExecutionerAbility.IsExecuteRange(defender, hp))
                return;

            var extra = Math.Min(damageEvent.Damage * bonus, MonsterEffectCaps.DamageRiderCap);
            if (extra <= 0.0)
                return;

            damageEvent.Damage += (float)extra;
        }
    }
}

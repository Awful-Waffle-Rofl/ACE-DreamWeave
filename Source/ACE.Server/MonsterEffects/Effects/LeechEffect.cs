using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// The token a "vital=" arg names. Matched by <see cref="MonsterEffectSpec.GetEnum{T}"/> case-insensitively.
    /// </summary>
    public enum MonsterEffectVital
    {
        Health,
        Stamina,
        Mana,
    }

    /// <summary>
    /// Phase 1: heals the attacking monster for a fraction of the damage it just dealt, into the named
    /// vital. Reuses BloodlustAbility.AccrueHeal for the fractional carry so a small percentage of a small
    /// hit still pays out over time instead of always rounding to zero.
    /// </summary>
    public sealed class LeechEffect : IMonsterEffect, IMonsterOutgoingHit
    {
        public string Kind => "leech";

        /// <summary>
        /// Runs after every mutator on its hooks: this effect SIZES ITSELF from a damage figure they
        /// may have changed, so it must see the final number. See the ordering paragraph in
        /// MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageReader;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            if (spec.GetDouble("pct", 0.0) <= 0.0)
            {
                error = "leech requires pct= > 0";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damageEvent.Damage <= 0.0f)
                return;

            var pct = spec.GetDouble("pct", 0.0);
            if (pct <= 0.0)
                return;

            var clamped = Math.Min(pct, MonsterEffectCaps.LeechCap);
            if (clamped <= 0.0)
                return;

            var vital = spec.GetEnum("vital", MonsterEffectVital.Health);
            var creatureVital = attacker.GetCreatureVital(ToAttribute(vital));

            var exactHeal = damageEvent.Damage * clamped;

            var heal = BloodlustAbility.AccrueHeal(state.Carry, exactHeal, out var newCarry);
            state.Carry = newCarry;

            if (heal <= 0)
                return;

            attacker.UpdateVitalDelta(creatureVital, heal);
        }

        private static PropertyAttribute2nd ToAttribute(MonsterEffectVital vital) => vital switch
        {
            MonsterEffectVital.Stamina => PropertyAttribute2nd.Stamina,
            MonsterEffectVital.Mana => PropertyAttribute2nd.Mana,
            _ => PropertyAttribute2nd.Health,
        };
    }
}

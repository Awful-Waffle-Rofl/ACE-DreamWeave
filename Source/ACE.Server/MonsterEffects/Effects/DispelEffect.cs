using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// A landed hit, or a landed spell hit (on=hit|spellhit, default hit), has a chance to strip one
    /// beneficial enchantment from the defender - the monster-side mirror of DispellingEdgeAbility, reusing
    /// its selection rule and the shipped EnchantmentManager.Dispel machinery. on=spellhit exists because
    /// some monsters attack entirely through spellcasting - a 100%-spell caster never reaches the S1
    /// outgoing-hit dispatch site at all, so a dispel record authored on one would otherwise parse, cache,
    /// and never roll.
    /// </summary>
    public sealed class DispelEffect : IMonsterEffect, IMonsterOutgoingHit, IMonsterSpellHit
    {
        public string Kind => "dispel";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var chance = spec.GetDouble("chance", 0.0);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "dispel requires chance= in (0, 1]";
                return false;
            }

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (trigger.HasFlag(MonsterEffectTrigger.Avoid))
            {
                error = "dispel has no avoidance hook - on= must resolve to hit and/or spellhit, never avoid";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            // a hit that landed for nothing (an Invincible defender: DoCalculateDamage returns 0 without
            // setting Evaded/Blocked/Parried, so HasDamage is still true) must not proc a rider - the
            // same guard leech, execute, rangeramp, ramp and debuff already carry on this hook
            if (damageEvent.Damage <= 0.0f)
                return;

            if (defender.IsDead)
                return;

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Hit))
                return;

            Apply(attacker, defender, spec);
        }

        public void OnSpellHit(Creature caster, Creature target, SpellProjectile projectile, ref float damage, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.SpellHit))
                return;

            if (target == null || target.IsDead)
                return;

            Apply(caster, target, spec);
        }

        private static void Apply(Creature attacker, Creature defender, MonsterEffectSpec spec)
        {
            var chance = spec.GetDouble("chance", 0.0);
            if (chance <= 0.0)
                return;

            // scaled by the attacking monster's ramp axis=procchance, then capped - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = attacker.ScaleMonsterEffectProcChance(chance);
            if (clamped <= 0.0)
                return;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > clamped)
                return;

            var dispellable = DispellingEdgeAbility.SelectDispellable(
                defender.EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Beneficial));

            if (dispellable.Count == 0)
                return;

            var entry = dispellable[ThreadSafeRandom.Next(0, dispellable.Count - 1)];

            defender.EnchantmentManager.Dispel(entry);

            if (defender is Player defenderPlayer && defenderPlayer.Session != null &&
                !defenderPlayer.SquelchManager.Squelches.Contains(attacker, ChatMessageType.CombatEnemy))
            {
                var spellName = new Spell((uint)entry.SpellId).Name;

                defenderPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{attacker.Name} strips your {spellName}!", ChatMessageType.Magic));
            }
        }
    }
}

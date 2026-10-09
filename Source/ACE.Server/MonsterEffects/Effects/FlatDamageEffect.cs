using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 1: a landed hit has a chance to deal a separate flat damage rider of its own type, after a
    /// short delay so it reads as a follow-up to the main strike - the monster-side mirror of
    /// PoisonWeaponAbility's SchedulePoisonProc.
    ///
    /// TWO CLAMPS, like every other proc effect in this catalog: chance= is scaled by the attacker's ramp
    /// axis=procchance and capped by monster_effect_proc_chance_cap before the roll (Creature.
    /// ScaleMonsterEffectProcChance), and amount= is separately capped by monster_effect_damage_rider_cap
    /// once the roll succeeds. The two caps guard different knobs and both apply.
    /// </summary>
    public sealed class FlatDamageEffect : IMonsterEffect, IMonsterOutgoingHit
    {
        public string Kind => "flatdamage";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var typeRaw = spec.GetString("type");

            if (typeRaw == null || !Enum.TryParse<DamageType>(typeRaw, true, out var type) || !Enum.IsDefined(typeof(DamageType), type))
            {
                error = "flatdamage requires a valid type= (DamageType)";
                return false;
            }

            if (spec.GetDouble("amount", 0.0) <= 0.0)
            {
                error = "flatdamage requires amount= > 0";
                return false;
            }

            var chance = spec.GetDouble("chance", 1.0);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "flatdamage chance= must be in (0, 1]";
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

            var amount = spec.GetDouble("amount", 0.0);
            if (amount <= 0.0)
                return;

            var chance = spec.GetDouble("chance", 1.0);
            if (chance <= 0.0)
                return;

            // scaled by the attacking monster's ramp axis=procchance, then capped - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = attacker.ScaleMonsterEffectProcChance(chance);
            if (clamped <= 0.0)
                return;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > clamped)
                return;

            var type = spec.GetEnum("type", DamageType.Undef);
            if (type == DamageType.Undef)
                return;

            var dealt = ComputeDealtAmount(amount);
            if (dealt == 0)
                return;

            var delay = spec.GetDouble("delay", 0.75);

            // marks that this proc has rolled a hit at least once - the observable half of the deferred
            // proc a bare test Creature cannot otherwise see run (it has no landblock to pump its action
            // queue), so this is what a disabled-tunable test asserts against.
            state.Announced = true;

            var chain = new ActionChain();
            chain.AddDelaySeconds(delay);
            chain.AddAction(attacker, () =>
            {
                if (attacker.IsDead || defender.IsDead)
                    return;

                DealRiderDamage(attacker, defender, type, dealt);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// The rider amount actually dealt: the authored amount clamped by monster_effect_damage_rider_cap,
        /// rounded once. Pure and internal so the clamp is directly testable without needing the deferred
        /// ActionChain to run.
        /// </summary>
        internal static uint ComputeDealtAmount(double amount) =>
            (uint)Math.Round(Math.Min(amount, MonsterEffectCaps.DamageRiderCap));

        /// <summary>
        /// Deals the rider's damage through a TakeDamage overload (never a direct vital write) and tells the
        /// victim what hit it, if it can hear anything at all.
        /// </summary>
        private static void DealRiderDamage(Creature attacker, Creature defender, DamageType type, uint amount)
        {
            if (defender is Player defenderPlayer)
                defenderPlayer.TakeDamage(attacker, type, (float)amount, BodyPart.Chest);
            else
                defender.TakeDamage(attacker, type, (float)amount);

            if (defender is Player messagePlayer && messagePlayer.Session != null &&
                !messagePlayer.SquelchManager.Squelches.Contains(attacker, ChatMessageType.CombatEnemy))
            {
                messagePlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{attacker.Name}'s {type} attack deals {amount:N0} additional damage to you!", ChatMessageType.CombatEnemy));
            }
        }
    }
}

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
    /// Phase 1: a landed hit has a chance to apply a damage-over-time to the defender. Unlike
    /// AcidProcAbility's DoT this needs no per-target bookkeeping dictionary - a monster's DoT is scheduled
    /// as one self-contained ActionChain closure per proc, re-checking IsDead on both actors every tick,
    /// rather than a registry keyed by target guid.
    ///
    /// TWO CLAMPS, like every other proc effect in this catalog: chance= is scaled by the attacker's ramp
    /// axis=procchance and capped by monster_effect_proc_chance_cap before the roll (Creature.
    /// ScaleMonsterEffectProcChance), and amount= (per tick) is separately capped by
    /// monster_effect_damage_rider_cap once the roll succeeds. The two caps guard different knobs and both
    /// apply.
    /// </summary>
    public sealed class DotEffect : IMonsterEffect, IMonsterOutgoingHit
    {
        public string Kind => "dot";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var typeRaw = spec.GetString("type");

            if (typeRaw == null || !Enum.TryParse<DamageType>(typeRaw, true, out var type) || !Enum.IsDefined(typeof(DamageType), type))
            {
                error = "dot requires a valid type= (DamageType)";
                return false;
            }

            if (spec.GetDouble("amount", 0.0) <= 0.0)
            {
                error = "dot requires amount= > 0";
                return false;
            }

            if (spec.GetInt("ticks", 3) <= 0)
            {
                error = "dot requires ticks= > 0";
                return false;
            }

            if (spec.GetDouble("interval", 3.0) <= 0.0)
            {
                error = "dot requires interval= > 0";
                return false;
            }

            var chance = spec.GetDouble("chance", 0.25);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "dot chance= must be in (0, 1]";
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

            var chance = spec.GetDouble("chance", 0.25);
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

            var ticks = spec.GetInt("ticks", 3);
            if (ticks <= 0)
                return;

            var interval = spec.GetDouble("interval", 3.0);

            var perTick = ComputePerTickAmount(amount);
            if (perTick == 0)
                return;

            // marks that this DoT has rolled a proc at least once - the observable half of the deferred
            // ticks a bare test Creature cannot otherwise see run (it has no landblock to pump its action
            // queue), so this is what a disabled-tunable test asserts against.
            state.Announced = true;

            ScheduleTick(attacker, defender, type, perTick, ticks, interval);
        }

        /// <summary>
        /// The per-tick amount actually dealt: the authored amount clamped by monster_effect_damage_rider_cap,
        /// rounded once. Pure and internal so the clamp is directly testable without needing the deferred
        /// ActionChain to run.
        /// </summary>
        internal static uint ComputePerTickAmount(double amount) =>
            (uint)Math.Round(Math.Min(amount, MonsterEffectCaps.DamageRiderCap));

        private static void ScheduleTick(Creature attacker, Creature defender, DamageType type, uint perTick, int remainingTicks, double interval)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(interval);
            chain.AddAction(attacker, () =>
            {
                if (attacker.IsDead || defender.IsDead)
                    return;

                DealTick(attacker, defender, type, perTick);

                if (remainingTicks - 1 > 0)
                    ScheduleTick(attacker, defender, type, perTick, remainingTicks - 1, interval);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// One tick's damage and message. Internal so a test can drive the real tick body (the scheduled
        /// ActionChain never runs on a bare test Creature, which has no landblock to pump it).
        /// </summary>
        internal static void DealTick(Creature attacker, Creature defender, DamageType type, uint amount)
        {
            // Secondary, stated rather than left to the default: a DoT tick is never an initial direct hit,
            // so a defender's reflect on=hit must not answer it
            if (defender is Player defenderPlayer)
                defenderPlayer.TakeDamage(attacker, type, (float)amount, BodyPart.Chest);
            else
                defender.TakeDamage(attacker, type, (float)amount, false, IncomingDamageOrigin.Secondary);

            if (defender is Player messagePlayer && messagePlayer.Session != null &&
                !messagePlayer.SquelchManager.Squelches.Contains(attacker, ChatMessageType.CombatEnemy))
            {
                messagePlayer.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{attacker.Name}'s {type} lingers, dealing {amount:N0} damage to you!", ChatMessageType.CombatEnemy));
            }
        }
    }
}

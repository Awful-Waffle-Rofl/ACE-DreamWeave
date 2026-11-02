using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

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
    /// AcidProcAbility's DoT the ticks are one self-contained ActionChain closure per proc, re-checking
    /// IsDead on both actors every tick.
    ///
    /// RESTART RULE: a new proc from the same attacker on the same defender RESTARTS the DoT - the old chain
    /// stops ticking and the new one runs its full tick count at the new per-tick amount. The only
    /// bookkeeping is a generation token per (attacker, defender guid) key (see BeginDot / IsCurrent /
    /// EndDot); different attackers on one defender each keep their own DoT. The key is per attacker plus
    /// defender, NOT per spec, so a monster carrying two dot specs would have one cancel the other (no
    /// shipped content does this today).
    ///
    /// TWO CLAMPS, like every other proc effect in this catalog: chance= is scaled by the attacker's ramp
    /// axis=procchance and capped by monster_effect_proc_chance_cap before the roll (Creature.
    /// ScaleMonsterEffectProcChance), and amount= (per tick) is separately capped by
    /// monster_effect_damage_rider_cap once the roll succeeds. The two caps guard different knobs and both
    /// apply.
    /// </summary>
    public sealed class DotEffect : IMonsterEffect, IMonsterOutgoingHit, IMonsterReflectedRider
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

            TryProc(attacker, attacker, defender, spec, ref state, tickActor: attacker);
        }

        /// <summary>
        /// Reflect (Vanguard T3) bounced this monster's missile: the DoT lands on the MONSTER, ticking as the
        /// reflector's damage. The proc chance is still the monster's own (its ramp axis=procchance, then the
        /// cap) - it is the monster's rider, only its target changed. Only a missile carries this rider (dot
        /// has no spell-hit hook), so a SpellHit bounce never reaches here.
        ///
        /// The tick chain is queued on the MONSTER (the defender of the reflected DoT), not on the reflector:
        /// each tick writes the monster's health, so it runs on the monster's own landblock thread even when
        /// the player has since crossed into another landblock group. The player stays the damage source and
        /// the ActiveDots key, so a re-proc still restarts the player's DoT on that monster.
        /// </summary>
        public void OnReflected(Creature monster, Creature reflector, MonsterEffectTrigger firedAs, DamageType damageType, float damage, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damage <= 0.0f || monster == null || reflector == null || monster.IsDead)
                return;

            TryProc(monster, reflector, monster, spec, ref state, tickActor: monster);
        }

        /// <summary>
        /// The roll and the schedule, shared by a normal hit and a reflected one. <paramref name="chanceSource"/>
        /// is always the monster carrying the record; <paramref name="attacker"/> is who the ticks are
        /// attributed to and keyed under, <paramref name="defender"/> who they land on, and
        /// <paramref name="tickActor"/> whose action queue runs them (the attacker on a normal hit, the
        /// monster defender on a reflected one).
        /// </summary>
        private static void TryProc(Creature chanceSource, Creature attacker, Creature defender, MonsterEffectSpec spec, ref MonsterEffectState state, Creature tickActor)
        {
            var amount = spec.GetDouble("amount", 0.0);
            if (amount <= 0.0)
                return;

            var chance = spec.GetDouble("chance", 0.25);
            if (chance <= 0.0)
                return;

            // scaled by the attacking monster's ramp axis=procchance, then capped - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = chanceSource.ScaleMonsterEffectProcChance(chance);
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

            var defenderGuid = defender.Guid.Full;
            var token = BeginDot(attacker, defenderGuid);

            ScheduleTick(tickActor, attacker, defender, type, perTick, ticks, interval, token);
        }

        // Generation counter per (attacker, defender guid). Scoped to the attacker OBJECT, not a static
        // keyed map: a tick closure is queued on the tick actor (the attacker, or the monster on a reflected
        // DoT), and WorldObject.EnqueueAction silently drops work for a Creature with no landblock, so a
        // monster that dies, despawns or unloads before its last tick never reaches EndDot. A weak table
        // collects the bookkeeping with the attacker instead of leaking it for the process lifetime (a
        // reflected DoT's stale entry lives on the player at most until it is collected, one per monster
        // guid, since a re-proc overwrites it). Ticks may run on different landblock threads, so the inner
        // map is concurrent and updates are atomic.
        private static readonly ConditionalWeakTable<Creature, ConcurrentDictionary<uint, long>> ActiveDots = new();
        private static long _nextToken;

        /// <summary>Registers a new DoT for the pair, superseding any existing one; returns its token.</summary>
        internal static long BeginDot(Creature attacker, uint defenderGuid)
        {
            var token = Interlocked.Increment(ref _nextToken);
            ActiveDots.GetOrCreateValue(attacker).AddOrUpdate(defenderGuid, token, (_, old) => Math.Max(old, token));
            return token;
        }

        /// <summary>True while the token is the pair's current DoT (not superseded, not ended).</summary>
        internal static bool IsCurrent(Creature attacker, uint defenderGuid, long token) =>
            CurrentToken(attacker, defenderGuid) == token;

        /// <summary>The pair's current token, or 0 when none is registered.</summary>
        internal static long CurrentToken(Creature attacker, uint defenderGuid) =>
            ActiveDots.TryGetValue(attacker, out var inner) && inner.TryGetValue(defenderGuid, out var current) ? current : 0;

        /// <summary>Removes the pair, but only if the token is still current (never evicts a newer DoT).</summary>
        internal static void EndDot(Creature attacker, uint defenderGuid, long token)
        {
            if (ActiveDots.TryGetValue(attacker, out var inner))
                inner.TryRemove(new KeyValuePair<uint, long>(defenderGuid, token));
        }

        /// <summary>Number of live defender keys for the attacker; for leak tests.</summary>
        internal static int ActiveDotCount(Creature attacker) =>
            ActiveDots.TryGetValue(attacker, out var inner) ? inner.Count : 0;

        /// <summary>
        /// The per-tick amount actually dealt: the authored amount clamped by monster_effect_damage_rider_cap,
        /// rounded once. Pure and internal so the clamp is directly testable without needing the deferred
        /// ActionChain to run.
        /// </summary>
        internal static uint ComputePerTickAmount(double amount) =>
            (uint)Math.Round(Math.Min(amount, MonsterEffectCaps.DamageRiderCap));

        internal enum TickOutcome { Superseded, Ended, Dealt, Finished }

        private static void ScheduleTick(Creature tickActor, Creature attacker, Creature defender, DamageType type, uint perTick, int remainingTicks, double interval, long token)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(interval);
            chain.AddAction(tickActor, () =>
            {
                if (RunTick(attacker, defender, type, perTick, remainingTicks, token) == TickOutcome.Dealt)
                    ScheduleTick(tickActor, attacker, defender, type, perTick, remainingTicks - 1, interval, token);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// The body of one scheduled tick. Superseded: a newer proc restarted the DoT, quit without touching
        /// the key or the defender. Ended: either party died, key removed. Dealt: damage dealt and more ticks
        /// remain (the caller reschedules). Finished: last tick dealt, key removed. Internal so tests drive
        /// the production path without pumping an ActionChain.
        /// </summary>
        internal static TickOutcome RunTick(Creature attacker, Creature defender, DamageType type, uint perTick, int remainingTicks, long token)
        {
            var defenderGuid = defender.Guid.Full;

            if (!IsCurrent(attacker, defenderGuid, token))
                return TickOutcome.Superseded;

            if (attacker.IsDead || defender.IsDead)
            {
                EndDot(attacker, defenderGuid, token);
                return TickOutcome.Ended;
            }

            DealTick(attacker, defender, type, perTick);

            if (remainingTicks - 1 > 0)
                return TickOutcome.Dealt;

            EndDot(attacker, defenderGuid, token);
            return TickOutcome.Finished;
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
                // the custom chat line below replaces the generic defender notification
                defenderPlayer.TakeDamage(attacker, type, (float)amount, BodyPart.Chest, sendDefenderNotification: false);
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

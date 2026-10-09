using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Sends damage back at the source, on one of two triggers, both as a FLAT amount:
    ///
    ///   on=hit   - round(monster Level x monster_effect_reflect_per_level x mult=), clamped by
    ///              monster_effect_damage_rider_cap. Owner ruling 2026-09-24: this used to be a fraction (pct=)
    ///              of the damage the monster took, and pct= is now REJECTED by Validate so nothing can be
    ///              authored that way again. It fires ONLY on an initial direct hit (IncomingDamageOrigin.
    ///              DirectHit, stated by the call site that produced the damage - never inferred here from the
    ///              damage type or amount) whose attacker stands within monster_effect_reflect_max_range metres
    ///              of the monster when it lands. Any attack type qualifies - melee, missile, a cast spell - so
    ///              the range gate, not the attack type, is what makes it a close-range punish. DoT ticks,
    ///              procs, splash and cascade children, thorns and other reflects all arrive as Secondary and
    ///              never trigger it.
    ///
    ///   on=avoid - a FLAT amount (flat=), because there is no damage figure at all on an avoided attack.
    ///              Verified against DamageEvent.DoCalculateDamage: the monster-effect avoidance roll
    ///              (defender.RollMonsterEffectAvoidance) runs and returns BEFORE GetBaseDamage or any of the
    ///              damage math executes. Unchanged by the 2026-09-24 ruling.
    ///
    /// DISTANCE MEASURE. WorldObject.GetCylinderDistance - the edge-to-edge cylinder distance
    /// Player.HandleActionTargetedMeleeAttack_Inner uses for its melee reach check and Monster.
    /// GetDistanceToTarget uses for IsMeleeRange. Either side lacking a PhysicsObj (not in the world) fails
    /// closed: no reflect. The check lives in Entity.CloseRangeReflect, shared with the player's Thorns
    /// (class_ability_thorns_max_range), which is the same close-range punish on the player side.
    ///
    /// Validate: on=hit rejects pct= and a non-positive mult=; on=avoid requires flat= > 0.
    ///
    /// THE REFLECT LATCH IS ALREADY HELD by the caller for the on=hit path (Creature.AbsorbMonsterEffectDamage
    /// wraps the whole IMonsterIncomingDamage dispatch in TryEnterReflect/ExitReflect - see its doc comment).
    /// This handler must never call TryEnterReflect/ExitReflect itself for on=hit. OnAvoided is NOT dispatched
    /// from inside that latch (it runs from RollMonsterEffectAvoidance, a different call site entirely, before
    /// any damage or reflect dispatch exists for this exchange) - so the flat on=avoid reflect would deal its
    /// damage unlatched, exactly like RiposteEffect's counter-swing does from the same dispatch site.
    ///
    /// on=avoid NEEDS A GRANTER ON THE SAME MONSTER. This effect's GetAvoidChance is a pure 0.0 - it answers
    /// an avoid, it does not cause one - so a weenie authoring only "reflect on=avoid" never avoids anything
    /// and this hook never runs. Paired with a real granter (avoid), every pooled win reaches it:
    /// RollMonsterEffectAvoidance notifies EVERY carried IMonsterAvoidance handler rather than one weighted
    /// winner, exactly so a reactor like this one is not required to supply avoidance chance to hear about
    /// an avoid. Verified by MonsterEffectAvoidIntegrationTests, which pairs avoid + debuff on=avoid +
    /// reflect on=avoid and asserts all three fire.
    ///
    /// COOLDOWN. monster_effect_reflect_cooldown bounds how often ONE reflect record on ONE monster may fire,
    /// shared by on=hit and on=avoid (they are the same record's damage, just two triggers) and across every
    /// attacker - a monster with several attackers on it does not get a separate timer per attacker. Stamped
    /// into state.LastTime ONLY when damage is actually sent, so a hit that missed the range gate, was not a
    /// DirectHit, or reflected 0 never starts the clock; a fresh MonsterEffectState (LastTime defaults to 0)
    /// always lets the first reflect through. 0 or less disables the cooldown. See IsReflectReady for the pure
    /// check.
    /// </summary>
    public sealed class ReflectEffect : IMonsterEffect, IMonsterIncomingDamage, IMonsterAvoidance
    {
        public string Kind => "reflect";

        /// <summary>
        /// Runs after every mutator on its hooks, so a hit a ward fully absorbed (landed for 0) reflects
        /// nothing. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageReader;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            if (spec.Has("pct"))
            {
                error = "reflect no longer takes pct= - on=hit reflects a flat amount scaled by the monster's level (monster_effect_reflect_per_level, optional mult=); remove pct=";
                return false;
            }

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);

            if (trigger.HasFlag(MonsterEffectTrigger.Hit) && spec.Has("mult") && spec.GetDouble("mult", 0.0) <= 0.0)
            {
                error = "reflect on=hit mult= must be > 0 (omit it for 1.0)";
                return false;
            }

            if (trigger.HasFlag(MonsterEffectTrigger.Avoid) && spec.GetDouble("flat", 0.0) <= 0.0)
            {
                error = "reflect on=avoid requires flat= > 0";
                return false;
            }

            error = null;
            return true;
        }

        public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, IncomingDamageOrigin origin, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Hit))
                return amount;

            // the structural DoT/proc/splash exclusion: only the producing call site knows what this damage
            // is, and it said so
            if (origin != IncomingDamageOrigin.DirectHit)
                return amount;

            if (amount == 0)
                return amount;

            if (!(source is Creature attacker) || attacker.IsDead)
                return amount;

            if (!CloseRangeReflect.IsWithinRange(defender, attacker, MonsterEffectCaps.ReflectMaxRange))
                return amount;

            var reflected = ComputeHitReflect(defender.Level ?? 0,
                MonsterEffectCaps.ReflectPerLevel,
                spec.GetDouble("mult", 1.0),
                MonsterEffectCaps.DamageRiderCap);

            if (reflected == 0)
                return amount;

            var now = Time.GetUnixTime();
            if (!IsReflectReady(now, state.LastTime, MonsterEffectCaps.ReflectCooldown))
                return amount;

            if (attacker is Player attackerPlayer)
                attackerPlayer.TakeDamage(defender, damageType, reflected, BodyPart.Chest);
            else
                attacker.TakeDamage(defender, damageType, reflected);

            state.LastTime = now;

            // the monster itself still takes the FULL incoming amount - a reflect sends damage back, it
            // does not reduce what lands, matching IMonsterIncomingDamage's own doc: "a reflect returns the
            // amount unchanged and sends damage back at the source"
            return amount;
        }

        /// <summary>
        /// True when a reflect may fire: cooldownSeconds &lt;= 0 disables the cooldown entirely; otherwise at
        /// least cooldownSeconds must have elapsed since lastTime. Pure so the arithmetic is testable on its
        /// own; the dispatch tests drive the same check through the real call path.
        /// </summary>
        internal static bool IsReflectReady(double now, double lastTime, double cooldownSeconds)
        {
            if (cooldownSeconds <= 0.0)
                return true;

            return now - lastTime >= cooldownSeconds;
        }

        /// <summary>
        /// on=hit's magnitude: round(level x perLevel x mult), then clamped to cap. Pure so the arithmetic is
        /// testable on its own; the dispatch tests drive the same number through the real call path.
        /// </summary>
        internal static uint ComputeHitReflect(int level, double perLevel, double mult, double cap)
        {
            if (level <= 0 || perLevel <= 0.0 || mult <= 0.0 || cap <= 0.0)
                return 0;

            var raw = Math.Round(level * perLevel * mult);

            return (uint)Math.Min(raw, Math.Round(cap));
        }

        /// <summary>Never contributes avoidance chance - reflect answers an avoid, it does not grant one.</summary>
        public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state) => 0.0;

        /// <summary>
        /// on=avoid's flat reflect - see the class doc comment for why this is a flat amount rather than a
        /// fraction of anything.
        /// </summary>
        public void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Avoid))
                return;

            if (attacker == null || attacker.IsDead)
                return;

            var flat = spec.GetDouble("flat", 0.0);
            if (flat <= 0.0)
                return;

            var clamped = Math.Min(flat, MonsterEffectCaps.DamageRiderCap);
            var reflected = (uint)Math.Round(clamped);
            if (reflected == 0)
                return;

            var now = Time.GetUnixTime();
            if (!IsReflectReady(now, state.LastTime, MonsterEffectCaps.ReflectCooldown))
                return;

            var damageType = attacker.GetDamageType(false, combatType: CombatType.Melee);

            if (attacker is Player attackerPlayer)
                attackerPlayer.TakeDamage(defender, damageType, reflected, BodyPart.Chest);
            else
                attacker.TakeDamage(defender, damageType, reflected);

            state.LastTime = now;
        }
    }
}

using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 2: sends damage back at the source, on one of two triggers with two DIFFERENT magnitude
    /// arguments - a deliberate split, not an oversight, forced by what each trigger actually has to measure
    /// with:
    ///
    ///   on=hit   - a fraction (pct=) of the damage the monster just TOOK. Unlike Player.ApplyThornsReflect,
    ///              which reflects a fraction of the DEFENDER's shield armor level, this is a fraction of
    ///              the incoming hit itself - ThornsAbility.ComputeReflectDamage is the wrong formula for
    ///              this shape and is deliberately not reused; the arithmetic is a single multiply done
    ///              inline in OnIncomingDamage.
    ///
    ///   on=avoid - a FLAT amount (flat=), because there is no damage figure to take a fraction OF. Verified
    ///              against DamageEvent.DoCalculateDamage: the monster-effect avoidance roll
    ///              (defender.RollMonsterEffectAvoidance) runs and returns BEFORE GetBaseDamage or any of
    ///              the damage math executes, so an avoided attack never produces a "damage taken" number at
    ///              all - pct= literally has nothing to multiply. This mirrors the player-side original,
    ///              where thorns-on-parry strength comes from the SHIELD (a fixed rating) rather than from
    ///              the incoming hit, which a parry also has no damage figure for.
    ///
    /// Validate enforces the split: on=hit requires pct= (and rejects a bare flat=-only record), on=avoid
    /// requires flat= (and rejects a bare pct=-only record). A record naming both triggers, "on=hit,avoid",
    /// must author both args.
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
    /// </summary>
    public sealed class ReflectEffect : IMonsterEffect, IMonsterIncomingDamage, IMonsterAvoidance
    {
        public string Kind => "reflect";

        /// <summary>
        /// Runs after every mutator on its hooks: this effect SIZES ITSELF from a damage figure they
        /// may have changed, so it must see the final number. See the ordering paragraph in
        /// MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageReader;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);

            if (trigger.HasFlag(MonsterEffectTrigger.Hit) && spec.GetDouble("pct", 0.0) <= 0.0)
            {
                error = "reflect on=hit requires pct= > 0";
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

        public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Hit))
                return amount;

            if (amount == 0)
                return amount;

            if (!(source is Creature attacker) || attacker.IsDead)
                return amount;

            var pct = spec.GetDouble("pct", 0.0);
            var clamped = Math.Min(pct, MonsterEffectCaps.ReflectCap);
            if (clamped <= 0.0)
                return amount;

            var reflected = (uint)Math.Round(amount * clamped);
            if (reflected == 0)
                return amount;

            if (attacker is Player attackerPlayer)
                attackerPlayer.TakeDamage(defender, damageType, reflected, BodyPart.Chest);
            else
                attacker.TakeDamage(defender, damageType, reflected);

            // the monster itself still takes the FULL incoming amount - a reflect sends damage back, it
            // does not reduce what lands, matching IMonsterIncomingDamage's own doc: "a reflect returns the
            // amount unchanged and sends damage back at the source"
            return amount;
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

            var damageType = attacker.GetDamageType(false, combatType: CombatType.Melee);

            if (attacker is Player attackerPlayer)
                attackerPlayer.TakeDamage(defender, damageType, reflected, BodyPart.Chest);
            else
                attacker.TakeDamage(defender, damageType, reflected);
        }
    }
}

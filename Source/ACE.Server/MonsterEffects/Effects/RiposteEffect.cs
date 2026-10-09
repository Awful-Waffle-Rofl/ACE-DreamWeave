using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 2: when this monster avoids a melee attack, it counters with a free strike at a fraction of a
    /// normal swing - the monster-side mirror of RiposteAbility/Player_ClassAbilityCombat.TriggerRiposte.
    ///
    /// BUILDS ITS OWN DamageEvent RATHER THAN RE-ENTERING Creature.MeleeAttack, and that is load bearing, not
    /// stylistic. MeleeAttack is animation- and NextAttackTime-stateful: it picks a combat maneuver, plays a
    /// swing motion, and unconditionally advances NextAttackTime/NextMoveTime at the end. Calling it from
    /// inside OnAvoided (itself called synchronously from DoCalculateDamage, mid-resolution of the ORIGINAL
    /// hit) would desync this monster's own swing timer and clobber whatever attack it already has queued.
    /// DamageEvent.CalculateDamage needs no maneuver or animation - passing a null motionCommand/attackHook
    /// falls through GetAttackPart's default body-part selection (Monster_Melee.GetAttackPart), exactly the
    /// branch a monster with no CMT-driven special attack already uses - so the counter's damage is computed
    /// the normal way without touching any of MeleeAttack's scheduling state. See
    /// MonsterEffectRiposteTests.OnAvoided_DoesNotAdvanceNextAttackTime for the assertion this buys.
    ///
    /// DEFERRED THROUGH A SHORT ACTIONCHAIN, matching Player_ClassAbilityCombat.TriggerRiposte's own 0.1s
    /// delay - the counter doesn't run inside the same call stack as the original hit's resolution.
    ///
    /// pct scales the fully-computed DamageEvent.Damage after mitigation, the same place
    /// Player.DamageTarget's damageMultiplier parameter scales a multi-shot's extra hits - not a cap tunable,
    /// since a counter-strike fraction is an authored balance knob per monster (a fraction over 1.0 is a
    /// deliberate "the counter hits harder than the original swing" boss mechanic, not a bug).
    ///
    /// THIS EFFECT NEEDS A GRANTER ON THE SAME MONSTER, and that is the whole of its reachability story.
    /// Its own GetAvoidChance is a pure 0.0 - it answers an avoid, it does not cause one - so a weenie that
    /// authors riposte alone never avoids anything and never counters. Pair it with a real granter (avoid)
    /// and every pooled win reaches this OnAvoided: RollMonsterEffectAvoidance notifies EVERY carried
    /// IMonsterAvoidance handler, granters and reactors alike, precisely so a reactor does not have to buy
    /// its notification with avoidance chance it does not supply. See that method's doc comment, and
    /// MonsterEffectRiposteTests.Magnitude_SchedulesACounterWhenAvoided, which drives the real dispatch site.
    ///
    /// (It was NOT always so. The dispatch site used to attribute a pooled win to one weighted winner, which
    /// gave a pure-0.0 reactor zero width on that wheel and left this effect inert in live play for its
    /// whole first phase. Do not reintroduce that weighting.)
    /// </summary>
    public sealed class RiposteEffect : IMonsterEffect, IMonsterAvoidance
    {
        public string Kind => "riposte";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            if (spec.GetDouble("pct", 0.0) <= 0.0)
            {
                error = "riposte requires pct= > 0";
                return false;
            }

            var chance = spec.GetDouble("chance", 1.0);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "riposte chance= must be in (0, 1]";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>Never contributes avoidance chance - riposte answers an avoid, it does not grant one.</summary>
        public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state) => 0.0;

        public void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (!ShouldCounter(defender, spec))
                return;

            if (attacker == null || attacker.IsDead || defender.IsDead)
                return;

            var pct = spec.GetDouble("pct", 0.0);
            if (pct <= 0.0)
                return;

            var weapon = defender.GetEquippedMeleeWeapon();

            // marks that this counter has been scheduled at least once - the observable half of the
            // deferred swing a bare test Creature cannot otherwise see run (it has no landblock to pump its
            // action queue), so this is what a disabled-tunable test asserts against, same as
            // FlatDamageEffect/DotEffect's own proc sentinel.
            state.Announced = true;

            var chain = new ActionChain();
            chain.AddDelaySeconds(0.1);
            chain.AddAction(defender, () =>
            {
                if (defender.IsDead || attacker.IsDead)
                    return;

                DealCounterDamage(defender, attacker, weapon, pct);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// The roll-only half: true when the (capped) chance rolls a hit. Pure and split out so the proc
        /// math is directly testable without needing the deferred ActionChain to run, mirroring
        /// RecastEffect.ShouldChain.
        /// </summary>
        internal static bool ShouldCounter(Creature defender, MonsterEffectSpec spec)
        {
            var chance = spec.GetDouble("chance", 1.0);
            if (chance <= 0.0)
                return false;

            // scaled by the countering monster's ramp axis=procchance, then capped - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = defender.ScaleMonsterEffectProcChance(chance);
            if (clamped <= 0.0)
                return false;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > clamped)
                return false;

            return true;
        }

        /// <summary>
        /// The counter-swing itself. Internal so a test can drive the real strike body (the deferred ActionChain
        /// never runs on a bare test Creature).
        /// </summary>
        internal static void DealCounterDamage(Creature counterAttacker, Creature counterTarget, WorldObject weapon, double pct)
        {
            var damageEvent = DamageEvent.CalculateDamage(counterAttacker, counterTarget, weapon);

            if (!damageEvent.HasDamage)
                return;

            damageEvent.Damage *= (float)pct;
            if (damageEvent.Damage <= 0.0f)
                return;

            if (counterTarget is Player targetPlayer)
                targetPlayer.TakeDamage(counterAttacker, damageEvent);
            else
                                // DirectHit: the counter is a real melee strike landing on its target, so a reflect on=hit
                // carried by that target (a combat pet, a faction mob) answers it like any other swing
                counterTarget.TakeDamage(counterAttacker, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);
        }
    }
}

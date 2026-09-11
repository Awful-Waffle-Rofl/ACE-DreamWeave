using System;

using ACE.Common;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 6: a general stacking buff. One record accrues state.Stacks on whichever triggers on= names
    /// (hit, spellhit, avoid), lapsing the whole pool once window= elapses with no qualifying trigger, and
    /// publishes a multiplier on whichever axis= it was authored for via IMonsterRampSource - the seam
    /// MonsterEffectSet.GetRampMultiplier exists for, so a consumer reads "how ramped is this monster" by
    /// axis without knowing which effect (if any) is doing the ramping. FrenzyAbility.AttackSpeedMultiplier
    /// (1 + stacks*per) is reused verbatim for every axis rather than restating the arithmetic per axis.
    ///
    /// TRIGGER AND AXIS ARE INDEPENDENT. on= says what adds a stack; axis= says what the held stacks
    /// multiply. A weenie authoring "ramp axis=magicdamage on=hit ..." builds stacks from landed MELEE hits
    /// that boost this monster's own SPELL damage - a deliberate decoupling, not a shape every axis needs to
    /// use both halves of.
    ///
    /// AXIS COVERAGE. Every one of the five axes has a real consumer, and each is read where its own cap is
    /// applied:
    ///   attackspeed - Creature.GetMonsterEffectSpeedMultiplier(MonsterSpeedAxis.Attack), which folds this
    ///                 seam in alongside the IMonsterSpeedMod handlers and clamps the product by
    ///                 monster_effect_speed_cap. This axis IS frenzy; it reaches the player through
    ///                 Creature.GetAnimSpeed.
    ///   castspeed   - the same composer on MonsterSpeedAxis.Cast, read by ScaleMonsterEffectCastTime.
    ///   magicdamage - self-consumed: OnSpellHit below reads its OWN current multiplier (from stacks already
    ///                 held BEFORE this hit, so the hit that triggers accrual is not itself boosted - the
    ///                 same trigger/application split FrenzyAbility uses) and scales damage by ref.
    ///   procchance  - Creature.ScaleMonsterEffectProcChance, which every effect rolling a chance= reads its
    ///                 number through, so one ramp record lifts all of them at once, capped by
    ///                 monster_effect_proc_chance_cap.
    ///   avoid       - Creature.RollMonsterEffectAvoidance scales the pooled avoid grant by it before
    ///                 clamping against monster_effect_avoidance_cap.
    ///
    /// on=avoid accrual needs an avoidance GRANTER on the same monster: this effect's own GetAvoidChance is a
    /// pure 0.0 (a ramp reacts to an avoid, it never grants one), so OnAvoided runs only when the monster
    /// also carries something that can actually make the pooled roll succeed, such as AvoidEffect.
    /// </summary>
    public sealed class RampEffect : IMonsterEffect, IMonsterOutgoingHit, IMonsterSpellHit, IMonsterAvoidance, IMonsterHeartbeat, IMonsterRampSource
    {
        public string Kind => "ramp";

        /// <summary>
        /// Runs ahead of every reader on its hooks: this effect CHANGES the damage figure they size
        /// themselves from. See the ordering paragraph in MonsterEffectHooks.cs.
        /// </summary>
        public int DispatchOrder => MonsterEffectDispatchOrder.DamageMutator;

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var axisRaw = spec.GetString("axis");
            if (axisRaw == null || !Enum.TryParse<MonsterRampAxis>(axisRaw, true, out var axis) || !Enum.IsDefined(typeof(MonsterRampAxis), axis))
            {
                error = "ramp requires a valid axis= (attackspeed|castspeed|magicdamage|procchance|avoid)";
                return false;
            }

            if (spec.GetDouble("per", 0.0) <= 0.0)
            {
                error = "ramp requires per= > 0";
                return false;
            }

            if (spec.GetInt("max", 0) <= 0)
            {
                error = "ramp requires max= > 0";
                return false;
            }

            if (spec.GetDouble("window", 0.0) <= 0.0)
            {
                error = "ramp requires window= > 0";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damageEvent.Damage <= 0.0f)
                return;

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Hit))
                return;

            Accrue(spec, ref state);
        }

        public void OnSpellHit(Creature caster, Creature target, SpellProjectile projectile, ref float damage, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (spec.GetEnum("axis", MonsterRampAxis.AttackSpeed) == MonsterRampAxis.MagicDamage)
            {
                var mult = ComputeMultiplier(spec, ref state);
                if (mult != 1.0)
                    damage *= (float)mult;
            }

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (trigger.HasFlag(MonsterEffectTrigger.SpellHit))
                Accrue(spec, ref state);
        }

        /// <summary>Never contributes avoidance chance - a ramp accrues from an avoid, it does not grant one.</summary>
        public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state) => 0.0;

        public void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Avoid))
                return;

            Accrue(spec, ref state);
        }

        /// <summary>
        /// The lapse half: once window= seconds pass with no qualifying trigger, the whole pool drops to
        /// zero rather than decaying gradually - a ramp is either being actively fed or it is not.
        /// </summary>
        public void OnHeartbeat(Creature creature, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (state.Stacks == 0)
                return;

            var window = spec.GetDouble("window", 0.0);
            if (window <= 0.0)
                return;

            if (Time.GetUnixTime() - state.LastTime >= window)
                state.Stacks = 0;
        }

        public bool ProvidesRamp(MonsterRampAxis axis, MonsterEffectSpec spec) =>
            spec.GetEnum("axis", MonsterRampAxis.AttackSpeed) == axis;

        public double GetRampMultiplier(MonsterRampAxis axis, MonsterEffectSpec spec, ref MonsterEffectState state) =>
            ComputeMultiplier(spec, ref state);

        /// <summary>
        /// Adds one stack (clamped by max= and, as the one cap this kind reads,
        /// monster_effect_ramp_stack_cap - whichever is lower) and stamps LastTime, the lapse clock
        /// OnHeartbeat reads.
        /// </summary>
        private static void Accrue(MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var authoredMax = Math.Max(1, spec.GetInt("max", 1));
            var cap = Math.Max(1, MonsterEffectCaps.RampStackCap);
            var max = Math.Min(authoredMax, cap);

            state.Stacks = Math.Min(state.Stacks + 1, max);
            state.LastTime = Time.GetUnixTime();
        }

        /// <summary>
        /// The current multiplier from held stacks alone - pure, so a ramped hit's own trigger never boosts
        /// itself (see the class doc comment's trigger/application split). Internal so the arithmetic is
        /// directly testable without needing a real landed hit.
        /// </summary>
        internal static double ComputeMultiplier(MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (state.Stacks <= 0)
                return 1.0;

            var per = spec.GetDouble("per", 0.0);
            return FrenzyAbility.AttackSpeedMultiplier(state.Stacks, per);
        }
    }
}

using System;

using ACE.Server.ClassAbilities;
using ACE.Server.Entity;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Elemental Proficiency Bludgeon (Archmage T3) target-side state: a full stun that stops the monster's
    /// NORMAL AI - movement, turning, and starting or finishing melee, missile and magic attacks - for a few
    /// seconds, followed by an immunity window measured from when the stun ENDS (Pinning Shot's model), so a
    /// stun can never be chained into a permanent lock. Bosses are not exempt; the immunity is the brake.
    ///
    /// WHAT IT DELIBERATELY DOES NOT STOP (owner ruling): scripted abilities. Emote-driven casts, the
    /// monster-effect heartbeat and boss fight mechanics are driven from outside Monster_Tick and run as
    /// designed. The gates that enforce the stun are therefore all on the AI's own paths:
    ///
    ///  - Monster_Tick: an early return right after the EmoteManager.IsBusy return, so no target search, no
    ///    movement, no turn and no attack start happens on a stunned tick.
    ///  - MoveReady: refuses while stunned, beside the pin check, for every StartTurn caller.
    ///  - Monster_Melee.MeleeAttack: each scheduled strike action refuses while stunned (a swing winding up
    ///    when the stun landed never deals its damage).
    ///  - Monster_Magic.MagicAttack: the post-windup cast action refuses while stunned and returns the
    ///    creature to its ready stance instead of casting.
    ///  - Monster_Missile.LaunchMissile: refuses to start while stunned, and the delayed projectile launch
    ///    refuses too (a shot winding up when the stun landed is never fired).
    ///
    /// Transient: never persisted, like the pin timers.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Timers.RunningTime until which this creature is stunned. Zero (the default) means never stunned.
        /// </summary>
        public double ClassAbilityStunnedUntil;

        /// <summary>
        /// Timers.RunningTime until which this creature may not be stunned again: the stun's end plus the
        /// immunity seconds, so the immunity counts from when the stun ENDS.
        /// </summary>
        public double ClassAbilityStunImmuneUntil;

        /// <summary>
        /// TRUE while this creature is stunned. One double compare, read on the monster AI's hot paths.
        /// </summary>
        public bool IsClassAbilityStunned => Timers.RunningTime < ClassAbilityStunnedUntil;

        /// <summary>
        /// Stuns this creature for <paramref name="seconds"/>, then makes it immune for a further
        /// <paramref name="immunitySeconds"/>.
        ///
        /// Returns TRUE only when the stun NEWLY landed. Returns FALSE on a refusal: an ineligible target
        /// (<see cref="ElementalProficiencyMath.IsEligibleTarget"/> - players, pets, game pieces), a dead
        /// creature, a non-positive duration, a creature that is already stunned (no refresh: a running stun
        /// is never extended), or one still inside its immunity window.
        ///
        /// On landing the movement in flight is cancelled and the melee stick-to-target hold released; see
        /// <see cref="HaltForClassAbilityStun"/>.
        /// </summary>
        public bool TryApplyClassAbilityStun(double seconds, double immunitySeconds)
        {
            if (!TryApplyClassAbilityStunState(seconds, immunitySeconds, Timers.RunningTime))
                return false;

            HaltForClassAbilityStun();

            return true;
        }

        /// <summary>
        /// The state half of <see cref="TryApplyClassAbilityStun"/> at an explicit running-time clock, with no
        /// movement side effect, so the refusal and immunity rules are testable without waiting out a window.
        /// </summary>
        internal bool TryApplyClassAbilityStunState(double seconds, double immunitySeconds, double now)
        {
            if (seconds <= 0.0)
                return false;

            if (!ElementalProficiencyMath.IsEligibleTarget(this) || IsDead)
                return false;

            if (now < ClassAbilityStunnedUntil || now < ClassAbilityStunImmuneUntil)
                return false;

            ClassAbilityStunnedUntil = now + seconds;
            ClassAbilityStunImmuneUntil = ClassAbilityStunnedUntil + Math.Max(0.0, immunitySeconds);

            return true;
        }

        /// <summary>
        /// Stops what the creature is physically doing when a stun lands.
        ///
        /// CANCEL: the LIGHTWEIGHT stop ReissueMoveForSpeedChange uses, and only for an AI move in flight
        /// (State.Awake, IsMoving, a target, and no emote running): the MoveToManager leg is cancelled,
        /// FailProgressCount cleared, IsMoving dropped, and a Ready motion broadcast. DELIBERATELY NOT the full
        /// Creature.CancelMoveTo(), which also calls ResetAttack() and FindNextTarget() - re-rolling aggro and
        /// able to fire EmoteManager.OnNewEnemy - and would cancel a scripted emote walk (EmoteType.Move,
        /// MoveHome, MoveToPos go through Creature.MoveTo and the same MoveToManager). The stun must never
        /// disturb scripted behaviour (owner ruling). A returning-home creature finishes its leg (it is not
        /// State.Awake); the Monster_Tick gate stops it issuing another.
        ///
        /// UNSTICK: a melee swing sticks the creature to its target (Monster_Melee.MeleeAttack,
        /// PhysicsObj.stick_to_object), and MoveToManager.CancelMoveTo does not release that hold - only a new
        /// movement does (MoveToManager.PerformMovement). While held, StickyManager.adjust_offset keeps sliding
        /// and turning the creature toward its target for up to StickyManager.StickyTime, so a stunned melee
        /// monster would keep chasing for a second. Released explicitly, in every state.
        ///
        /// NextMoveTime is then held to the stun's end (never pulled forward).
        /// </summary>
        private void HaltForClassAbilityStun()
        {
            // an emote walk can replace an AI leg in the shared MoveToManager while IsMoving is still set from
            // that leg; while an emote runs, its movement is scripted and is left alone
            if (!EmoteManager.IsBusy)
                ReissueMoveForSpeedChange();

            PhysicsObj?.unstick_from_object();

            // never pull an existing, longer hold forward
            if (NextMoveTime < ClassAbilityStunnedUntil)
                NextMoveTime = ClassAbilityStunnedUntil;
        }
    }
}

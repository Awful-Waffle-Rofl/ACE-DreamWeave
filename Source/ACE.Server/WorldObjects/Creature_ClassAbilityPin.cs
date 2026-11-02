using System;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Pinning Shot (Archer T2) target-side state: whether this creature is currently pinned in place, and
    /// whether it is still inside the immunity window that stops a pin being chained into a permanent lock.
    ///
    /// Filed as its own partial rather than added to Monster_Navigation so the pin is legible as one unit,
    /// and so the two plain double fields sit next to the only code that reads them.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Timers.RunningTime after which this creature may move again. Zero (the default) means never
        /// pinned. Read by <see cref="MoveReady"/>, which is the single gate every movement path funnels
        /// through.
        /// </summary>
        public double ClassAbilityPinnedUntil;

        /// <summary>
        /// Timers.RunningTime after which this creature may be pinned AGAIN. Set when a pin is applied, to
        /// the end of that pin plus class_ability_pinningshot_immunity_seconds, so the immunity is measured
        /// from when the pin ENDS rather than from when it started - which is what the tunable's own
        /// description says and what makes the effect impossible to chain.
        /// </summary>
        public double ClassAbilityPinImmuneUntil;

        /// <summary>
        /// TRUE while this creature is held in place by Pinning Shot.
        /// </summary>
        public bool IsClassAbilityPinned => Timers.RunningTime < ClassAbilityPinnedUntil;

        /// <summary>
        /// Whether a pin may be applied right now: a legal target, alive, not already pinned, and past its
        /// immunity window.
        ///
        /// PLAYERS, PETS AND CHESS PIECES ARE EXCLUDED HERE, not only at the dispatch. Pinning Shot is PvE
        /// only, and the outgoing-damage dispatch already refuses a Player target - but a CombatPet is a
        /// Creature and is NOT a Player, so a stray hit on a player's own summon would otherwise be pinnable.
        /// Excluding Pet (CombatPet derives from it) at the state boundary means no present or future call site
        /// can pin either, which is the same belt-and-braces placement ApplyCreatureDeathClassAbilities uses.
        ///
        /// GamePiece is here for the same reason and one more: it also derives from Creature, it drives its own
        /// movement through Creature.GetMovementParameters, and the movement slow's factor is read there. With
        /// the exclusion, "a chess piece is never slowed" is true BY CONSTRUCTION. Without it the claim would
        /// rest on no damage path happening to reach a chess piece today, which is the weakest form of evidence
        /// there is - it would be refuted silently by any future site that pinned from something other than a
        /// landed player hit.
        /// </summary>
        public bool CanBeClassAbilityPinned()
        {
            if (this is Player || this is Pet || this is GamePiece)
                return false;

            if (IsDead)
                return false;

            var now = Timers.RunningTime;

            return now >= ClassAbilityPinnedUntil && now >= ClassAbilityPinImmuneUntil;
        }

        /// <summary>
        /// Pins this creature in place for <paramref name="durationSeconds"/>, then makes it immune for a
        /// further <paramref name="immunitySeconds"/>. Returns FALSE when the pin was refused (illegal
        /// target, already pinned, still immune, or a non-positive duration), so the caller can tell a
        /// refused pin from an applied one.
        ///
        /// THE HOLD ON NextMoveTime IS NOT SUFFICIENT ON ITS OWN, and that is the whole reason
        /// ClassAbilityPinnedUntil exists as a separate field rather than this method just pushing
        /// NextMoveTime out. A pinned creature deliberately keeps attacking, and EVERY completed attack
        /// rewrites NextMoveTime from its own animation timing - Monster_Melee.DoAttackStance and the melee
        /// swing set `NextMoveTime = PrevAttackTime + animLength + 0.5f`, Monster_Missile sets
        /// `NextMoveTime = NextAttackTime = ...` on launch and on stance change, and Monster_Magic sets it
        /// after a cast. So the creature would clear its own pin on its first swing, typically within a
        /// fraction of the intended 5 seconds. MoveReady() therefore consults ClassAbilityPinnedUntil
        /// directly, and NextMoveTime is pushed out as well only so that anything reading that field raw
        /// sees a consistent picture.
        ///
        /// ORDER IS LOAD BEARING: CancelMoveTo() itself assigns `NextMoveTime = Timers.RunningTime + 1.0f`,
        /// so the hold must be written AFTER the cancel or the cancel would shorten a 5 second pin to 1.
        /// </summary>
        public bool TryApplyClassAbilityPin(double durationSeconds, double immunitySeconds)
        {
            if (durationSeconds <= 0.0)
                return false;

            if (!CanBeClassAbilityPinned())
                return false;

            // CancelMoveTo stops the movement already in flight - without it the creature keeps sliding to
            // the end of the MoveTo the physics layer is already running, and the pin only takes visible
            // effect once that completes.
            //
            // NOT CALLED WHILE RETURNING HOME. CancelMoveTo calls ForceHome() when MonsterState is Return,
            // which teleports the creature to its home position outright - a pin that snapped a fleeing
            // creature home would be a far larger effect than the one advertised, and in the wrong
            // direction. A returning creature is still pinned by the timers below; only the cancel is
            // skipped.
            if (MonsterState != State.Return)
                CancelMoveTo();

            var now = Timers.RunningTime;

            ClassAbilityPinnedUntil = now + durationSeconds;
            ClassAbilityPinImmuneUntil = ClassAbilityPinnedUntil + Math.Max(0.0, immunitySeconds);

            // never pull an existing, longer hold forward
            if (NextMoveTime < ClassAbilityPinnedUntil)
                NextMoveTime = ClassAbilityPinnedUntil;

            return true;
        }

        /// <summary>
        /// Timers.RunningTime after which this creature's post-pin movement slow ends. Zero (the default)
        /// means "no slow", and is also what <see cref="TickClassAbilityMoveSlow"/> restores it to.
        ///
        /// SET AT PIN APPLICATION TIME, NOT WHEN THE PIN LAPSES, as ClassAbilityPinnedUntil + slowSeconds.
        /// There is no end-of-pin callback to hang the arming off, and MoveReady() is not one: Monster_Tick
        /// only reaches it when the creature actually wants to move (it needs targetDist > MaxRange or a
        /// facing miss, plus !IsTurning and !IsMoving), so a pinned creature already in melee range and
        /// facing its target never calls it - the commonest case this ability produces. Arming the window
        /// up front makes the clock exact and needs no new hook. The factor being nominally live DURING the
        /// pin is harmless, because a pinned creature cannot move at all.
        /// </summary>
        public double ClassAbilityMoveSlowUntil;

        /// <summary>
        /// The movement speed multiplier in force while the window above is open, in the open interval
        /// (0.0, 1.0]. Written only by <see cref="TryApplyClassAbilityMoveSlow"/> and
        /// <see cref="TryApplyClassAbilityFrostSlow"/>. Not persisted, like the
        /// two pin timers above - a slow does not survive a server restart, and is not worth a biota row.
        /// </summary>
        public float ClassAbilityMoveSlowFactor;

        /// <summary>
        /// The factor the movement paths actually read: the stored factor while the window is open, and a
        /// neutral 1.0f in every other case - no window, an expired window, or a stored factor outside
        /// (0.0, 1.0] (which is how a tunable set to 1.0, 0, or a negative disables the feature with no
        /// code-path change).
        ///
        /// READ ON A HOT PATH - once per move issuance, on both the wire and the server side. Deliberately
        /// allocation-free and free of any PropertyManager read: the factor was resolved once, at pin time.
        /// </summary>
        public float CurrentMovementSlowFactor
        {
            get
            {
                if (ClassAbilityMoveSlowUntil == 0.0)
                    return 1.0f;

                return GetMovementSlowFactorAt(Timers.RunningTime);
            }
        }

        /// <summary>
        /// <see cref="CurrentMovementSlowFactor"/> at an explicit running-time clock. The same rules: neutral
        /// 1.0f with no window, past the window, or for a stored factor outside (0.0, 1.0].
        /// </summary>
        internal float GetMovementSlowFactorAt(double now)
        {
            if (ClassAbilityMoveSlowUntil == 0.0)
                return 1.0f;

            if (now >= ClassAbilityMoveSlowUntil)
                return 1.0f;

            var factor = ClassAbilityMoveSlowFactor;

            if (factor <= 0.0f || factor > 1.0f)
                return 1.0f;

            return factor;
        }

        /// <summary>
        /// The speed multiplier every monster movement emit site writes into BOTH halves of a move: the wire
        /// (MoveToParameters.Speed) and the server (MovementParameters.Speed). It is CurrentMovementSlowFactor
        /// for an ordinary creature. CombatPet overrides it to multiply in the pet movement-speed floor
        /// (CombatPet_SpeedFloor.cs), so a pet's value can exceed 1.0.
        ///
        /// Kept separate from CurrentMovementSlowFactor on purpose: that property keeps its (0.0, 1.0] contract
        /// for the code that reasons about slows. Emit sites read this one, so the two halves of every move carry
        /// the same number. Same hot-path rules as CurrentMovementSlowFactor: no allocation, no PropertyManager
        /// read per call.
        /// </summary>
        public virtual float MovementSpeedFactor => CurrentMovementSlowFactor;

        /// <summary>
        /// Arms the post-pin movement slow. One of the TWO write sites for the two fields above; the other is
        /// <see cref="TryApplyClassAbilityFrostSlow"/>, and both follow "the stronger slow wins, the later end
        /// is kept" (see <see cref="ApplyPinSlowState"/>). Returns FALSE when the slow was refused, so the
        /// caller can tell an armed slow from a skipped one and only broadcast a visual for the former. A pin
        /// slow that meets a stronger running slow is refused (FALSE) and leaves that slow untouched.
        ///
        /// THE WINDOW IS DERIVED FROM ClassAbilityPinnedUntil, never from an independently recomputed
        /// now + pinSeconds. The pin may have been clamped or shaped by TryApplyClassAbilityPin, and the
        /// advertised rule is "the slow starts when the pin ENDS" - recomputing the pin end here would make
        /// the two drift apart silently the first time either side gains a clamp.
        /// </summary>
        public bool TryApplyClassAbilityMoveSlow(double slowSeconds, float factor)
        {
            if (slowSeconds <= 0.0)
                return false;

            if (factor <= 0.0f || factor >= 1.0f)
                return false;

            if (IsDead)
                return false;

            // a slow is a rider ON a pin, never a standalone effect: no live pin, no slow
            if (!IsClassAbilityPinned)
                return false;

            return ApplyPinSlowState(slowSeconds, factor, Timers.RunningTime);
        }

        /// <summary>
        /// The write half of <see cref="TryApplyClassAbilityMoveSlow"/>, after its refusals, at an explicit
        /// running-time clock. The slot is SHARED with Elemental Proficiency Frost and the stronger slow wins
        /// (ELEMENTAL-PROFICIENCY-DESIGN.md), with the same rule as the Frost entry point: while a STRONGER
        /// (lower-factor) slow is running, the pin slow changes nothing - neither the running factor nor its
        /// end time, so a weaker slow can never stretch a stronger one - and returns FALSE. Otherwise (nothing
        /// running, or an equal or weaker running slow) it writes its own factor and the later of the running
        /// end and this pin's window (ClassAbilityPinnedUntil + slowSeconds), and returns TRUE. With nothing
        /// running that is exactly the pin's own factor and window, as before.
        /// </summary>
        internal bool ApplyPinSlowState(double slowSeconds, float factor, double now)
        {
            var existing = GetMovementSlowFactorAt(now);
            var running = existing < 1.0f;

            // one slot, the stronger slow wins: a weaker incoming slow neither replaces nor extends it
            if (running && factor > existing)
                return false;

            var endTime = ClassAbilityPinnedUntil + slowSeconds;

            if (running && ClassAbilityMoveSlowUntil > endTime)
                endTime = ClassAbilityMoveSlowUntil;

            ClassAbilityMoveSlowFactor = factor;
            ClassAbilityMoveSlowUntil = endTime;

            return true;
        }

        /// <summary>
        /// Ends the movement slow when its window closes, and forces the creature to re-issue its move so
        /// the restored speed takes effect immediately.
        ///
        /// THE RE-ISSUE IS THE WHOLE POINT. Forward velocity is latched once per movement leg, in
        /// MoveToManager.BeginMoveForward by way of MotionTable.add_motion (whose first statement is
        /// sequence.SetVelocity(motionData.Velocity * speed)), so clearing the factor alone would leave the
        /// creature running at a fifth speed until its current leg happened to end.
        ///
        /// Called from Monster_Tick, guarded there by a ClassAbilityMoveSlowUntil != 0.0 compare so an
        /// unslowed creature pays one double compare per tick and nothing else.
        /// </summary>
        public void TickClassAbilityMoveSlow()
        {
            if (IsDead)
            {
                ClearClassAbilityMoveSlow();
                return;
            }

            if (Timers.RunningTime < ClassAbilityMoveSlowUntil)
                return;

            ClearClassAbilityMoveSlow();

            ReissueMoveForSpeedChange();
        }

        /// <summary>
        /// Stops the leg in flight so the next Monster_Tick re-issues it at the CURRENT MovementSpeedFactor.
        /// Forward velocity is latched once per movement leg (see <see cref="TickClassAbilityMoveSlow"/>), so
        /// any change to the slow factor - the window closing, or Frost landing on a creature that is already
        /// running - takes visible effect only through this. A no-op unless the creature is awake, has a
        /// target and is moving.
        /// </summary>
        private void ReissueMoveForSpeedChange()
        {
            // NOT WHILE RETURNING HOME. Cancelling a return-home move empties MoveToManager.PendingActions,
            // and Monster_Navigation.UpdatePosition then calls Sleep() on the spot - which would park the
            // creature wherever it stood instead of sending it home. TryApplyClassAbilityPin carves State.Return
            // out of its own cancel for a different reason (ForceHome is a teleport); this is the same shape.
            // A returning creature simply finishes its leg at the slowed speed and runs home at full speed on
            // the next one.
            if (MonsterState != State.Awake || AttackTarget == null || !IsMoving)
                return;

            // DELIBERATELY NOT Creature.CancelMoveTo(). That calls ForceHome() in State.Return (a teleport),
            // ResetAttack(), FindNextTarget(), and pushes NextMoveTime a full second out - which would insert a
            // visible one second dead stop at the exact moment the creature is supposed to speed back up. This
            // is the lightweight shape CombatPet_Follow.CancelPhysicsMoveTo uses, and for the same reasons.
            var moveToManager = PhysicsObj?.MovementManager?.MoveToManager;

            if (moveToManager != null)
            {
                moveToManager.CancelMoveTo(WeenieError.ActionCancelled);
                moveToManager.FailProgressCount = 0;
            }

            IsMoving = false;

            // Stop the run on the clients and resync, so they do not keep dead-reckoning the slowed run for the
            // rest of the leg they were told about. For a non-Player EnqueueBroadcastMotion leaves server
            // physics alone (applyPhysics defaults to false off the Player test in
            // WorldObject_Networking.EnqueueBroadcastMotion), so this is purely a client-side correction.
            EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance, MotionCommand.Ready));
            SendUpdatePosition();

            // NextMoveTime is deliberately NOT pushed out: the next Monster_Tick (within 0.2s) sees
            // !IsTurning && !IsMoving, calls StartTurn(), and re-issues the move at the current factor on both
            // the wire and the server.
        }

        /// <summary>
        /// Elemental Proficiency Frost (Archmage T3): a movement slow written into the SAME slot Pinning Shot's
        /// post-pin slow uses (ClassAbilityMoveSlowFactor / ClassAbilityMoveSlowUntil), so every movement emit
        /// site, the stuck-detection suppression and the Monster_Tick restore apply to it unchanged.
        ///
        /// UNLIKE <see cref="TryApplyClassAbilityMoveSlow"/> IT NEEDS NO PIN. That method's pin-only gate is
        /// Pinning Shot's own rule (its window is derived from the pin end) and stays as it is; this is a
        /// second entry point, not a widening of the first.
        ///
        /// MERGE RULE (one shared slot, the stronger slow wins): applied when no slow is running, or when the
        /// incoming factor is equal or stronger (lower) than the running one; the end time is then the later
        /// of the two. A weaker incoming slow is refused outright and changes nothing - neither the factor nor
        /// the window. No immunity window.
        ///
        /// Returns TRUE only for a NEW landing (no slow was running). A refresh returns FALSE, and so does a
        /// refusal: an ineligible target (<see cref="ElementalProficiencyMath.IsEligibleTarget"/>), a dead
        /// creature, a non-positive duration, a factor outside (0.0, 1.0), or a weaker slow than the running
        /// one.
        ///
        /// When the factor actually changes, the leg in flight is re-issued
        /// (<see cref="ReissueMoveForSpeedChange"/>) so a monster that is already running slows immediately
        /// rather than at the end of its current leg. A same-factor refresh does not re-issue, so repeated
        /// frost hits do not stutter the creature.
        /// </summary>
        public bool TryApplyClassAbilityFrostSlow(double seconds, float speedFactor)
        {
            if (!TryApplyClassAbilityFrostSlowState(seconds, speedFactor, Timers.RunningTime, out var newlyLanded, out var factorChanged))
                return false;

            if (factorChanged)
                ReissueMoveForSpeedChange();

            return newlyLanded;
        }

        /// <summary>
        /// The state half of <see cref="TryApplyClassAbilityFrostSlow"/> at an explicit running-time clock, with
        /// no movement side effect. Returns FALSE when refused (nothing written); otherwise TRUE, with
        /// <paramref name="newlyLanded"/> = no slow was running and <paramref name="factorChanged"/> = the
        /// effective speed factor moved.
        /// </summary>
        internal bool TryApplyClassAbilityFrostSlowState(double seconds, float speedFactor, double now, out bool newlyLanded, out bool factorChanged)
        {
            newlyLanded = false;
            factorChanged = false;

            if (seconds <= 0.0)
                return false;

            if (speedFactor <= 0.0f || speedFactor >= 1.0f)
                return false;

            if (!ElementalProficiencyMath.IsEligibleTarget(this) || IsDead)
                return false;

            var existing = GetMovementSlowFactorAt(now);
            var running = existing < 1.0f;

            // one slot, the stronger slow wins: a weaker incoming slow never displaces a running one
            if (running && speedFactor > existing)
                return false;

            var endTime = now + seconds;

            if (running && ClassAbilityMoveSlowUntil > endTime)
                endTime = ClassAbilityMoveSlowUntil;

            ClassAbilityMoveSlowFactor = speedFactor;
            ClassAbilityMoveSlowUntil = endTime;

            newlyLanded = !running;
            factorChanged = speedFactor != existing;

            return true;
        }

        private void ClearClassAbilityMoveSlow()
        {
            ClassAbilityMoveSlowUntil = 0.0;
            ClassAbilityMoveSlowFactor = 1.0f;
        }

        /// <summary>
        /// The stuck-detection suppression that rides the movement slow, as a pure decision so both consumers
        /// of MoveToManager.FailProgressCount can share it and so it is unit testable without a physics object.
        /// Returns the value the counter should hold.
        ///
        /// WHY. MoveToManager.CheckProgressMade fails a leg whose CLOSING rate is under 0.25 m/s measured over
        /// more than a second, and each failure increments FailProgressCount. Against a target that is running
        /// away the closing rate goes negative no matter how fast the creature itself is moving, so a creature
        /// slowed to a fifth speed trips it routinely - and the two consumers then cancel the move, retarget,
        /// and make the creature read as losing interest rather than as being slowed. It is not stuck; the
        /// progress check is drawing the wrong conclusion from a rate it was never meant to judge. Worse,
        /// CheckProgressMade does NOT advance PreviousDistanceTime on its failing branch, so once the rate test
        /// starts failing it keeps failing on every subsequent physics tick: the counter climbs monotonically
        /// rather than decaying.
        ///
        /// SCOPED TO THE LEG THAT IS ACTUALLY RUNNING SLOWED, not merely to a creature that has a slow window
        /// open. <paramref name="inFlightSpeed"/> is MoveToManager.MovementParams.Speed, the speed the move
        /// currently in flight was issued with, and suppression needs it to equal the slow factor. Two real
        /// cases need that precision:
        ///
        /// - Creature_Navigation.AddMoveToTick, one of the two consumers, is reached ONLY from
        ///   MoveTo(Position, ..., setLoc: true, ...), whose callers are Vendor.MoveTo(Home) and the three
        ///   EmoteManager sites that pass emote.Extent as the speed. None of those is slowed by this feature,
        ///   so a creature-level flag would defeat stuck detection on a scripted emote walk that is running at
        ///   full speed.
        /// - TryApplyClassAbilityPin deliberately skips its cancel in State.Return, so a return-home leg issued
        ///   before the slow was armed keeps running at 1.0. That leg really is at full speed and must keep its
        ///   stuck detection.
        ///
        /// Float equality is exact here rather than approximate, and that is deliberate: both values are copied
        /// verbatim from the same float through MovementParameters' copy constructors, never recomputed.
        ///
        /// THIS DELIBERATELY BLINDS STUCK DETECTION ON ONE CREATURE'S ONE SLOWED LEG. The trade is acceptable
        /// because nothing DISTANCE-based is suppressed: Movement()'s MaxChaseRange check and CheckMissHome's
        /// HomeRadius leash both still run untouched, so a slowed creature that really cannot make progress is
        /// still released - by range, a beat later - instead of grinding at a wall forever. The counter is
        /// CLEARED rather than merely ignored so a slow-era backlog cannot be consumed by the first tick after
        /// the window closes.
        /// </summary>
        public static int SuppressStuckProgress(int failProgressCount, float inFlightSpeed, float slowFactor)
        {
            // not slowed at all: CurrentMovementSlowFactor is exactly 1.0f outside a live window
            if (slowFactor >= 1.0f)
                return failProgressCount;

            // slowed, but this leg was issued at some other speed - an emote's Extent, or a pre-slow leg
            if (inFlightSpeed != slowFactor)
                return failProgressCount;

            return 0;
        }
    }
}

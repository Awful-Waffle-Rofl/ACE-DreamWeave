using log4net;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Physics.Animation;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Idle follow (WaffleACE, 2026-09-10). A combat pet with no attack target runs back to its owner and
    /// stays within a few metres of them, instead of standing wherever its last fight ended.
    ///
    /// Driven from Monster_Tick's no-target branch, which used to call Sleep() - a no-op for combat pets.
    /// It cannot ride the generic StartTurn/Movement() path: both are keyed on AttackTarget, and Movement()
    /// cancels the move as soon as GetDistanceToTarget() reaches MaxChaseRange, which with no target is
    /// immediate (GetDistanceToTarget returns float.MaxValue). So this issues its own MoveTo toward the
    /// owner and advances the physics itself through UpdatePosition.
    ///
    /// Target acquisition is untouched and still runs first on every tick (CombatPet.HandleFindTarget): a
    /// monster that comes into range pulls the pet off the follow (StopFollowingOwner). There is no leash
    /// while the pet HAS a target - today's chase rules apply unchanged.
    ///
    /// Teleport triggers (2026-09-21, all tunable, read once per tick into tickSettings):
    ///   - distance: farther than pet_follow_teleport_distance from the owner, teleport at once;
    ///   - max time: a follow that has not arrived within pet_follow_max_seconds teleports, whatever progress
    ///     it shows;
    ///   - stuck: no net progress toward the owner for pet_follow_stuck_seconds (PetFollowProgressTracker).
    /// Every teleport is subject to PetFollowRules.CanTeleportToOwner, and its landing spot is validated
    /// (TryTeleportToOwner).
    ///
    /// Threading: this runs on the pet's own monster tick, i.e. on its landblock group's thread in
    /// LandblockManager.TickMultiThreadedWork. Nothing here moves the pet into a landblock that another
    /// group's thread owns - see CanFollowOwner and Pet.GetInFrontPlacement.
    /// </summary>
    partial class CombatPet
    {
        private static readonly ILog followLog = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// TRUE from the moment a follow MoveTo toward the owner is issued until the pet arrives, is
        /// teleported, or leaves the follow (a target was acquired, or the owner became unreachable).
        /// IsMoving is NOT this: UpdatePosition clears IsMoving whenever MoveToManager runs out of pending
        /// actions, and that flip is exactly how the follow learns a MoveTo has ended.
        /// </summary>
        private bool isFollowingOwner;

        /// <summary>Timers.RunningTime before which a follow MoveTo may not be re-issued.</summary>
        private double nextFollowIssueTime;

        /// <summary>Timers.RunningTime the current follow began (or its last teleport attempt), for the max-time trigger.</summary>
        private double followStartTime;

        /// <summary>Timers.RunningTime before which no teleport is attempted, whichever trigger asks.</summary>
        private double nextTeleportAttemptTime;

        private readonly PetFollowProgressTracker followProgress = new PetFollowProgressTracker();

        /// <summary>
        /// This tick's follow and reach tunables. Refreshed at the top of HandleFindTarget, which Monster_Tick
        /// runs before the follow and attack branches on every tick that gets that far.
        /// </summary>
        private PetFollowSettings tickSettings = PetFollowSettings.Defaults;

        /// <summary>
        /// One idle-follow step. Called from Monster_Tick in place of Sleep() when this pet has no target.
        /// </summary>
        public void TickFollowOwner()
        {
            var owner = P_PetOwner;

            if (!CanFollowOwner(owner))
            {
                StopFollowingOwner();
                return;
            }

            // advance the physics MoveTo and broadcast the new position. In State.Awake this also clears
            // IsMoving once MoveToManager has no pending actions (Monster_Navigation.UpdatePosition), which
            // the arrival and re-issue checks below read.
            if (IsMoving)
                UpdatePosition();

            var distance = GetCylinderDistance(owner);
            var now = Timers.RunningTime;
            var settings = tickSettings;

            // distance trigger: too far to be worth running - an owner who ran off, recalled across the
            // landblock group, or a pet left on the far side of a fight
            if (PetFollowRules.ShouldTeleportForDistance(distance, settings.TeleportDistance) && now >= nextTeleportAttemptTime)
            {
                TryTeleportToOwner(owner, now);
                return;
            }

            if (!isFollowingOwner)
            {
                if (PetFollowRules.ShouldStartFollow(distance) && IssueFollowMove(owner))
                {
                    isFollowingOwner = true;
                    followStartTime = now;
                    followProgress.Reset();
                    followProgress.Update(now, Location.ToGlobal(), owner.Location.ToGlobal(), settings.StuckSeconds);
                }
                return;
            }

            // arrived: the MoveTo ended and the pet is inside the start band
            if (!IsMoving && !PetFollowRules.ShouldStartFollow(distance))
            {
                isFollowingOwner = false;
                followProgress.Reset();
                return;
            }

            // max-time trigger: however much progress the tracker is crediting, a follow this long is not working
            if (PetFollowRules.FollowTimedOut(followStartTime, now, settings.MaxFollowSeconds) && now >= nextTeleportAttemptTime)
            {
                TryTeleportToOwner(owner, now);
                return;
            }

            // stuck = no net distance closed on the owner (see the tracker): global coordinates, the same frame
            // CheckMissHome compares positions in. Location was synced from physics by the UpdatePosition above.
            if (followProgress.Update(now, Location.ToGlobal(), owner.Location.ToGlobal(), settings.StuckSeconds) && now >= nextTeleportAttemptTime)
            {
                TryTeleportToOwner(owner, now);
                return;
            }

            // the MoveTo ended but the owner is still out of range - they walked off after the pet arrived, or
            // MoveToManager gave up against geometry. Re-issue, throttled.
            if (!IsMoving && now >= nextFollowIssueTime)
                IssueFollowMove(owner);
        }

        /// <summary>
        /// Whether this pet may follow its owner at all: the owner is in the world, not mid-teleport, in the
        /// same landblock instance, and ticked on the same thread as this pet. LandblockGroup puts any two
        /// landblocks closer than its min spacing (LandblockGroupMinSpacing, or LandblockGroupMinSpacingWhenDormant)
        /// into one group, and a dungeon landblock is always a group of its own, so an owner outside the pet's
        /// group is several landblocks away or in a different dungeon - there is nothing sensible to run to, and
        /// walking toward them would eventually carry the pet into a landblock another thread ticks. The pet
        /// then simply waits where it is, as it always did.
        /// </summary>
        private bool CanFollowOwner(Player owner)
        {
            if (owner?.PhysicsObj == null || owner.Location == null || owner.Teleporting || PhysicsObj == null || Location == null)
                return false;

            if (owner.Location.Instance != Location.Instance)
                return false;

            return SharesTickThreadWith(owner);
        }

        private bool SharesTickThreadWith(Player owner)
        {
            var petBlock = CurrentLandblock;
            var ownerBlock = owner.CurrentLandblock;

            if (petBlock == null || ownerBlock == null)
                return false;

            return PetFollowRules.SharesTickThread(petBlock == ownerBlock, petBlock.CurrentLandblockGroup, ownerBlock.CurrentLandblockGroup);
        }

        /// <summary>
        /// Starts a run toward the owner. Returns FALSE, issuing nothing, while the pet is not ready to move.
        /// The broadcast and the server-side MoveTo use the same parameters as a passive pet's follow
        /// (Pet.MoveTo / Pet.StartFollow): always run, stop at FollowArriveDistance, no Sticky.
        /// </summary>
        private bool IssueFollowMove(Player owner)
        {
            // the same gate StartTurn uses: not before NextMoveTime (an attack's recovery), not mid-animation
            if (!MoveReady())
                return false;

            if (MoveSpeed == 0.0f)
                GetMovementSpeed();

            // broadcast to clients
            var motion = new Motion(this, owner, MovementType.MoveToObject);

            motion.MoveToParameters.MovementParameters |= MovementParams.CanCharge;
            motion.MoveToParameters.DistanceToObject = PetFollowRules.FollowArriveDistance;
            motion.MoveToParameters.WalkRunThreshold = 0.0f;

            motion.RunRate = RunRate;

            CurrentMotionState = motion;

            EnqueueBroadcastMotion(motion);

            // perform the movement on the server
            var mvp = new MovementParameters();
            mvp.DistanceToObject = PetFollowRules.FollowArriveDistance;
            mvp.WalkRunThreshold = 0.0f;

            PhysicsObj.MoveToObject(owner.PhysicsObj, mvp);

            // prevent snap forward
            PhysicsObj.UpdateTime = Physics.Common.PhysicsTimer.CurrentTime;

            IsMoving = true;
            LastMoveTime = Timers.RunningTime;
            nextFollowIssueTime = LastMoveTime + PetFollowRules.ReissueIntervalSeconds;

            return true;
        }

        /// <summary>
        /// Ends the follow, cancelling a MoveTo toward the owner that is still in flight. Called when a target
        /// is acquired - Monster_Tick's attack branch calls Movement() rather than StartTurn() while IsMoving
        /// is set, which would carry the pet on to its owner instead of toward the target - and when the owner
        /// becomes unreachable. A no-op when the pet is not following.
        /// </summary>
        private void StopFollowingOwner()
        {
            if (!isFollowingOwner)
                return;

            isFollowingOwner = false;
            followProgress.Reset();

            if (IsMoving)
                CancelPhysicsMoveTo();

            // stop the run on the clients; the attack branch broadcasts its own MoveTo toward the target next
            EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance, MotionCommand.Ready));
        }

        /// <summary>
        /// Cancels any physics MoveTo/TurnTo in flight. Deliberately NOT Creature.CancelMoveTo(): that also
        /// calls ResetAttack() and FindNextTarget() - which would overwrite a target that HandleFindTarget just
        /// chose - calls ForceHome() in State.Return, and pushes NextMoveTime a full second out.
        /// </summary>
        private void CancelPhysicsMoveTo()
        {
            var moveToManager = PhysicsObj?.MovementManager?.MoveToManager;

            if (moveToManager != null)
            {
                moveToManager.CancelMoveTo(WeenieError.ActionCancelled);
                moveToManager.FailProgressCount = 0;
            }

            IsMoving = false;
        }

        /// <summary>
        /// Puts the pet back beside its owner. Refused while the owner is teleporting or dead, across instances,
        /// or across tick threads (PetFollowRules.CanTeleportToOwner). Refused or not, the progress window and
        /// the max-time clock restart and further attempts wait TeleportRetryIntervalSeconds, so a blocked
        /// teleport is retried at a bounded rate rather than on every tick.
        ///
        /// The spot in front of the owner is tried first, but only outdoors (Pet.GetInFrontPlacement) and only
        /// accepted when the pet really ended up there (Pet.IsPlacedAt): a Slide placement reports OK for any
        /// spot the transition resolves to, including the far side of a wall. Anything else falls back to the
        /// owner's own spot. Location is synced and broadcast once, after the final attempt.
        /// </summary>
        private void TryTeleportToOwner(Player owner, double now)
        {
            followProgress.Reset();
            followStartTime = now;
            nextTeleportAttemptTime = now + PetFollowRules.TeleportRetryIntervalSeconds;

            if (!PetFollowRules.CanTeleportToOwner(owner.Teleporting, owner.IsDead, owner.Location.Instance == Location.Instance, SharesTickThreadWith(owner)))
                return;

            CancelPhysicsMoveTo();

            var moved = false;
            var placed = false;

            var inFront = GetInFrontPlacement(owner, GetOwnerPlacementDistance(owner), false);

            if (inFront != null && PhysicsTeleport(inFront) == Physics.Common.SetPositionError.OK)
            {
                moved = true;
                placed = IsPlacedAt(inFront);
            }

            var result = Physics.Common.SetPositionError.OK;

            if (!placed)
            {
                var ownerSpot = GetOwnerSpotPlacement(owner);

                result = PhysicsTeleport(ownerSpot);

                if (result == Physics.Common.SetPositionError.OK)
                {
                    moved = true;
                    placed = true;

                    if (!IsPlacedAt(ownerSpot))
                        followLog.Debug($"{Name} (0x{Guid}) was teleported toward its owner {owner.Name}'s spot but slid to {PhysicsObj.Position}");
                }
            }

            if (moved)
                SyncTeleportedPosition();

            if (!placed)
            {
                followLog.Debug($"{Name} (0x{Guid}) could not be teleported to its owner {owner.Name}: {result}");
                return;
            }

            isFollowingOwner = false;
        }
    }
}

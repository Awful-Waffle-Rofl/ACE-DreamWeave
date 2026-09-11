using System;
using System.Numerics;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// Return to home if target distance exceeds this range
        /// </summary>
        public const float MaxChaseRange = 96.0f;
        public const float MaxChaseRangeSq = MaxChaseRange * MaxChaseRange;

        /// <summary>
        /// Determines if a monster is within melee range of target
        /// </summary>
        //public const float MaxMeleeRange = 1.5f;
        public const float MaxMeleeRange = 0.75f;
        //public const float MaxMeleeRange = 1.5f + 0.6f + 0.1f;    // max melee range + distance from + buffer

        /// <summary>
        /// The maximum range for a monster missile attack
        /// </summary>
        //public const float MaxMissileRange = 80.0f;
        //public const float MaxMissileRange = 40.0f;   // for testing

        /// <summary>
        /// The distance per second from running animation
        /// </summary>
        public float MoveSpeed;

        /// <summary>
        /// The run skill via MovementSystem GetRunRate()
        /// </summary>
        public float RunRate;

        /// <summary>
        /// Flag indicates monster is turning towards target
        /// </summary>
        public bool IsTurning = false;

        /// <summary>
        /// Flag indicates monster is moving towards target
        /// </summary>
        public bool IsMoving = false;

        /// <summary>
        /// The last time a movement tick was processed
        /// </summary>
        public double LastMoveTime;

        public bool DebugMove;

        public double NextMoveTime;
        public double NextCancelTime;

        /// <summary>
        /// Starts the process of monster turning towards target
        /// </summary>
        public void StartTurn()
        {
            //if (Timers.RunningTime < NextMoveTime)
            //return;
            if (!MoveReady())
                return;

            if (DebugMove)
                Console.WriteLine($"{Name} ({Guid}) - StartTurn, ranged={IsRanged}");

            if (MoveSpeed == 0.0f)
                GetMovementSpeed();

            //Console.WriteLine($"[{Timers.RunningTime}] - {Name} ({Guid}) - starting turn");

            IsTurning = true;

            // send network actions
            var targetDist = GetDistanceToTarget();
            var turnTo = IsRanged || (CurrentAttack == CombatType.Magic && targetDist <= GetSpellMaxRange()) || AiImmobile;
            if (turnTo && !PhysicsObj.State.HasFlag(PhysicsState.Gravity))
            {
                // A creature without the Gravity bit never gains a contact plane, and MoveToManager.UseTime (the client
                // runs the same code) returns before its pending actions while Contact is clear, so a TurnToObject on it
                // never completes: its TurnRight motion spins it in place until another motion replaces it. Seen live
                // 2026-09-01 on the Warspeaker's Gate tower garrison (1003551 / 1003554, PhysicsState 8 so they hold a
                // mesh-only deck). Face the target directly and broadcast the position instead of starting a turn.
                FaceTargetDirectly(AttackTarget);
                IsTurning = false;
                return;
            }
            if (turnTo)
                TurnTo(AttackTarget);
            else
                MoveTo(AttackTarget, RunRate);

            // need turning listener?
            IsTurning = false;
            IsMoving = true;
            LastMoveTime = Timers.RunningTime;
            NextCancelTime = LastMoveTime + ThreadSafeRandom.Next(2, 4);
            moveBit = false;

            var mvp = GetMovementParameters();
            if (turnTo)
                PhysicsObj.TurnToObject(AttackTarget.PhysicsObj, mvp);
            else
                PhysicsObj.MoveToObject(AttackTarget.PhysicsObj, mvp);

            // prevent initial snap
            PhysicsObj.UpdateTime = PhysicsTimer.CurrentTime;
        }

        /// <summary>
        /// Called when the TurnTo process has completed
        /// </summary>
        public void OnTurnComplete()
        {
            var dir = Vector3.Normalize(AttackTarget.Location.ToGlobal() - Location.ToGlobal());
            Location.Rotate(dir);

            IsTurning = false;

            if (!IsRanged)
                StartMove();
        }

        /// <summary>
        /// Starts the process of monster moving towards target
        /// </summary>
        public void StartMove()
        {
            LastMoveTime = Timers.RunningTime;
            IsMoving = true;
        }

        /// <summary>
        /// Called when the MoveTo process has completed
        /// </summary>
        public override void OnMoveComplete(WeenieError status)
        {
            if (DebugMove)
                Console.WriteLine($"{Name} ({Guid}) - OnMoveComplete({status})");

            if (status != WeenieError.None)
                return;

            if (AiImmobile && CurrentAttack == CombatType.Melee)
            {
                var targetDist = GetDistanceToTarget();
                if (targetDist > MaxRange)
                    ResetAttack();
            }

            if (MonsterState == State.Return)
                Sleep();

            PhysicsObj.CachedVelocity = Vector3.Zero;
            IsMoving = false;
        }

        /// <summary>
        /// Estimates the time it will take the monster to turn and move towards target
        /// </summary>
        public float EstimateTargetTime()
        {
            return EstimateTurnTo() + EstimateMoveTo();
        }

        /// <summary>
        /// Estimates the time it will take the monster to turn towards target
        /// </summary>
        public float EstimateTurnTo()
        {
            return GetRotateDelay(AttackTarget);
        }

        /// <summary>
        /// Estimates the time it will take the monster to move towards target
        /// </summary>
        public float EstimateMoveTo()
        {
            return GetDistanceToTarget() / MoveSpeed;
        }

        /// <summary>
        /// Returns TRUE if monster is within target melee range
        /// </summary>
        public bool IsMeleeRange()
        {
            return GetDistanceToTarget() <= MaxMeleeRange;
        }

        /// <summary>
        /// Returns TRUE if monster in range for current attack type
        /// </summary>
        public bool IsAttackRange()
        {
            return GetDistanceToTarget() <= MaxRange;
        }

        /// <summary>
        /// Gets the distance to target, with radius excluded
        /// </summary>
        public float GetDistanceToTarget()
        {
            if (AttackTarget == null)
                return float.MaxValue;

            //var matchIndoors = Location.Indoors == AttackTarget.Location.Indoors;
            //var targetPos = matchIndoors ? AttackTarget.Location.ToGlobal() : AttackTarget.Location.Pos;
            //var sourcePos = matchIndoors ? Location.ToGlobal() : Location.Pos;

            //var dist = (targetPos - sourcePos).Length();
            //var radialDist = dist - (AttackTarget.PhysicsObj.GetRadius() + PhysicsObj.GetRadius());

            // always use spheres?
            var cylDist = (float)Physics.Common.Position.CylinderDistance(PhysicsObj.GetRadius(), PhysicsObj.GetHeight(), PhysicsObj.Position,
                AttackTarget.PhysicsObj.GetRadius(), AttackTarget.PhysicsObj.GetHeight(), AttackTarget.PhysicsObj.Position);

            if (DebugMove)
                Console.WriteLine($"{Name}.DistanceToTarget: {cylDist}");

            //return radialDist;
            return cylDist;
        }

        /// <summary>
        /// Returns the destination position the monster is attempting to move to
        /// to perform a melee attack
        /// </summary>
        public Vector3 GetDestination()
        {
            var dir = Vector3.Normalize(Location.ToGlobal() - AttackTarget.Location.ToGlobal());
            return AttackTarget.Location.Pos + dir * (AttackTarget.PhysicsObj.GetRadius() + PhysicsObj.GetRadius());
        }

        /// <summary>
        /// Primary movement handler, determines if target in range
        /// </summary>
        public void Movement()
        {
            //if (!IsRanged)
                UpdatePosition();

            if (MonsterState == State.Awake && GetDistanceToTarget() >= MaxChaseRange)
            {
                CancelMoveTo();
                FindNextTarget();
                return;
            }

            if (PhysicsObj.MovementManager.MoveToManager.FailProgressCount > 0 && Timers.RunningTime > NextCancelTime)
                CancelMoveTo();
        }

        public void UpdatePosition(bool netsend = true)
        {
            //stopwatch.Restart();
            PhysicsObj.update_object(Location.Instance);
            //ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Monster_Navigation_UpdatePosition_PUO, stopwatch.Elapsed.TotalSeconds);
            UpdatePosition_SyncLocation();

            if (netsend)
                SendUpdatePosition();

            if (DebugMove)
                //Console.WriteLine($"{Name} ({Guid}) - UpdatePosition (velocity: {PhysicsObj.CachedVelocity.Length()})");
                Console.WriteLine($"{Name} ({Guid}) - UpdatePosition: {Location.ToLOCString()}");

            if (MonsterState == State.Return && PhysicsObj.MovementManager.MoveToManager.PendingActions.Count == 0)
                Sleep();

            if (MonsterState == State.Awake && IsMoving && PhysicsObj.MovementManager.MoveToManager.PendingActions.Count == 0)
                IsMoving = false;
        }

        /// <summary>
        /// Synchronizes the WorldObject Location with the Physics Location
        /// </summary>
        public void UpdatePosition_SyncLocation()
        {
            // was the position successfully moved to?
            // use the physics position as the source-of-truth?
            var newPos = PhysicsObj.Position;

            if (Location.LandblockId.Raw != newPos.ObjCellID)
            {
                var prevBlockCell = Location.LandblockId.Raw;
                var prevBlock = Location.LandblockId.Raw >> 16;
                var prevCell = Location.LandblockId.Raw & 0xFFFF;

                var newBlockCell = newPos.ObjCellID;
                var newBlock = newPos.ObjCellID >> 16;
                var newCell = newPos.ObjCellID & 0xFFFF;

                Location.LandblockId = new LandblockId(newPos.ObjCellID);

                if (prevBlock != newBlock)
                {
                    LandblockManager.RelocateObjectForPhysics(this, true);
                    //Console.WriteLine($"Relocating {Name} from {prevBlockCell:X8} to {newBlockCell:X8}");
                    //Console.WriteLine("Old position: " + Location.Pos);
                    //Console.WriteLine("New position: " + newPos.Frame.Origin);
                }
                //else
                    //Console.WriteLine("Moving " + Name + " to " + Location.LandblockId.Raw.ToString("X8"));
            }

            // skip ObjCellID check when updating from physics
            // TODO: update to newer version of ACE.Entity.Position
            Location.PositionX = newPos.Frame.Origin.X;
            Location.PositionY = newPos.Frame.Origin.Y;
            Location.PositionZ = newPos.Frame.Origin.Z;

            Location.Rotation = newPos.Frame.Orientation;

            if (DebugMove)
                DebugDistance();
        }

        public void DebugDistance()
        {
            if (AttackTarget == null) return;

            var dist = GetDistanceToTarget();
            var angle = GetAngle(AttackTarget);
            //Console.WriteLine("Dist: " + dist);
            //Console.WriteLine("Angle: " + angle);
        }

        public void GetMovementSpeed()
        {
            var moveSpeed = MotionTable.GetRunSpeed(MotionTableId);
            if (moveSpeed == 0) moveSpeed = 2.5f;
            var scale = ObjScale ?? 1.0f;

            RunRate = GetRunRate();

            MoveSpeed = moveSpeed * RunRate * scale;

            //Console.WriteLine(Name + " - Run: " + runSkill + " - RunRate: " + RunRate + " - Move: " + MoveSpeed + " - Scale: " + scale);
        }

        /// <summary>
        /// Returns the RunRate that is sent to the client as myRunRate
        /// </summary>
        public float GetRunRate()
        {
            var burden = 0.0f;

            // assuming burden only applies to players...
            if (this is Player player)
            {
                // Capacity comes from Player.GetEncumbranceCapacity, the single authority for a Player (see its
                // summary). Numerically identical to the old inline call for every normal character; it
                // additionally honours the mule capacity override, so a loaded mule's run rate is not throttled
                // by a capacity the pickup gate never used.
                var capacity = player.GetEncumbranceCapacity();
                burden = EncumbranceSystem.GetBurden(capacity, EncumbranceVal ?? 0);

                // TODO: find this exact formula in client
                // technically this would be based on when the player releases / presses the movement key after stamina > 0
                if (player.IsExhausted)
                    burden = 3.0f;
            }

            var runSkill = GetCreatureSkill(Skill.Run).Current;
            var runRate = MovementSystem.GetRunRate(burden, (int)runSkill, 1.0f);

            return (float)runRate;
        }

        /// <summary>
        /// Sets the corpse to the final position
        /// </summary>
        public void SetFinalPosition()
        {
            if (AttackTarget == null) return;

            var playerDir = AttackTarget.Location.GetCurrentDir();
            Location.Pos = AttackTarget.Location.Pos + playerDir * (AttackTarget.PhysicsObj.GetRadius() + PhysicsObj.GetRadius());
            SendUpdatePosition();
        }

        /// <summary>
        /// Points the monster at its target with no turn animation: sets the heading on the physics object and on
        /// Location, then broadcasts the position. For creatures that cannot run a turn (no Gravity bit, see StartTurn).
        /// </summary>
        private void FaceTargetDirectly(WorldObject target)
        {
            if (target?.Location == null) return;

            // raw delta, not GetDirection: that normalizes, and normalizing a zero vector yields NaN, which a
            // == Vector3.Zero test does not catch and which would otherwise be broadcast as the rotation
            var targetDir = Location.Indoors == target.Location.Indoors
                ? target.Location.ToGlobal() - Location.ToGlobal()
                : target.Location.Pos - Location.Pos;
            targetDir.Z = 0.0f;
            if (targetDir.LengthSquared() < 0.0001f) return;

            Location.Rotate(Vector3.Normalize(targetDir));
            // deliberately set the orientation directly and set_frame, rather than set_heading(degrees): the
            // quaternion is already exact, and a degrees round trip is lossy for nothing
            PhysicsObj.Position.Frame.Orientation = Location.Rotation;
            PhysicsObj.set_frame(PhysicsObj.Position.Frame);
            SendUpdatePosition();
        }

        /// <summary>
        /// Returns TRUE if monster is facing towards the target
        /// </summary>
        public bool IsFacing(WorldObject target)
        {
            if (target?.Location == null) return false;

            var angle = GetAngle(target);
            var dist = Math.Max(0, GetDistanceToTarget());

            // rotation accuracy?
            var threshold = 5.0f;

            var minDist = 10.0f;

            if (dist < minDist)
                threshold += (minDist - dist) * 1.5f;

            if (DebugMove)
                Console.WriteLine($"{Name}.IsFacing({target.Name}): Angle={angle}, Dist={dist}, Threshold={threshold}, {angle < threshold}");

            return angle < threshold;
        }

        public MovementParameters GetMovementParameters()
        {
            var mvp = new MovementParameters();

            // set non-default params for monster movement
            mvp.Flags &= ~MovementParamFlags.CanWalk;

            var turnTo = IsRanged || (CurrentAttack == CombatType.Magic && GetDistanceToTarget() <= GetSpellMaxRange()) || AiImmobile;

            mvp.Flags |= GetMonsterMoveToFlags(turnTo, DisableSticky);

            return mvp;
        }

        /// <summary>
        /// The MoveTo flags a monster adds on top of the defaults. Pure, so the DisableSticky opt-out is
        /// unit-testable without a physics object.
        ///
        /// WaffleACE fork (PropertyBool 9052): with <paramref name="disableSticky"/> set, Sticky is the ONLY
        /// flag omitted - FailWalk, UseFinalHeading and MoveAway are unchanged. A monster with the property
        /// absent gets exactly the flag set it got before the property existed.
        /// </summary>
        public static MovementParamFlags GetMonsterMoveToFlags(bool turnTo, bool disableSticky)
        {
            if (turnTo)
                return default;

            var flags = MovementParamFlags.FailWalk | MovementParamFlags.UseFinalHeading | MovementParamFlags.MoveAway;

            if (!disableSticky)
                flags |= MovementParamFlags.Sticky;

            return flags;
        }

        /// <summary>
        /// The maximum distance a monster can travel outside of its home position
        /// </summary>
        public double? HomeRadius
        {
            get => GetProperty(PropertyFloat.HomeRadius);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.HomeRadius); else SetProperty(PropertyFloat.HomeRadius, value.Value); }
        }

        public static float DefaultHomeRadius = 192.0f;
        public static float DefaultHomeRadiusSq = DefaultHomeRadius * DefaultHomeRadius;

        private float? homeRadiusSq;

        public float HomeRadiusSq
        {
            get
            {
                if (homeRadiusSq == null)
                {
                    var homeRadius = HomeRadius ?? DefaultHomeRadius;
                    homeRadiusSq = (float)(homeRadius * homeRadius);
                }
                return homeRadiusSq.Value;
            }
        }

        /// <summary>
        /// WaffleACE fork (PropertyFloat 9009). World Events boss confinement: metres from this creature's
        /// Home position inside which it is allowed to hold a target. Absent (or <= 0) means the whole
        /// feature is off for this creature and every targeting/movement path behaves exactly as before.
        ///
        /// Deliberately NOT cached the way <see cref="HomeRadiusSq"/> is: this is written by
        /// WorldEventSpawner.TryPlace on an object that has not entered the world yet, and a cache filled by
        /// an earlier read would have to be invalidated by hand.
        /// </summary>
        public double? TetherRadius
        {
            get => GetProperty(PropertyFloat.TetherRadius);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.TetherRadius); else SetProperty(PropertyFloat.TetherRadius, value.Value); }
        }

        /// <summary>True when this creature carries a usable tether radius.</summary>
        public bool IsTethered => (TetherRadius ?? 0.0) > 0.0;

        /// <summary>
        /// How long a tether-forced retarget must wait before it may fire again. <see cref="CheckMissHome"/>
        /// runs on the monster tick, so without this a boss parked outside its tether would re-run target
        /// selection every tick.
        /// </summary>
        public const float TetherRetargetCooldown = 2.0f;

        /// <summary>Timers.RunningTime before which no further tether-forced retarget may run.</summary>
        public double NextTetherRetarget;

        /// <summary>
        /// Whether <paramref name="globalTargetPos"/> is inside <paramref name="tetherRadius"/> of
        /// <paramref name="globalHomePos"/>. A null or non-positive radius means "no tether", which admits
        /// everything - the feature-off answer. Pure, and the single place the tether comparison is made.
        /// </summary>
        public static bool IsWithinTether(Vector3 globalHomePos, Vector3 globalTargetPos, double? tetherRadius)
        {
            if (tetherRadius == null || tetherRadius.Value <= 0.0)
                return true;

            var radiusSq = (float)(tetherRadius.Value * tetherRadius.Value);

            return Vector3.DistanceSquared(globalHomePos, globalTargetPos) <= radiusSq;
        }

        public void CheckMissHome()
        {
            if (MonsterState == State.Return)
                return;

            var homePosition = GetPosition(PositionType.Home);
            //var matchIndoors = Location.Indoors == homePosition.Indoors;

            //var globalPos = matchIndoors ? Location.ToGlobal() : Location.Pos;
            //var globalHomePos = matchIndoors ? homePosition.ToGlobal() : homePosition.Pos;
            var globalPos = Location.ToGlobal();
            var globalHomePos = homePosition.ToGlobal();

            var homeDistSq = Vector3.DistanceSquared(globalHomePos, globalPos);

            if (homeDistSq > HomeRadiusSq)
            {
                MoveToHome();
                return;
            }

            // WaffleACE fork, World Events boss confinement. Past the tether but still inside HomeRadius:
            // drop the current target (a Taunt hold included - the tether outranks it) and re-acquire from
            // the in-tether candidates only, so a boss that has been walked out by one kiter turns on the
            // players still standing at the event instead of finishing the walk. FindNextTarget falls back to
            // MoveToHome on its own when nothing is in range, which is the old leash behaviour.
            if (!IsTethered || IsWithinTether(globalHomePos, globalPos, TetherRadius))
                return;

            if (Timers.RunningTime < NextTetherRetarget)
                return;

            NextTetherRetarget = Timers.RunningTime + TetherRetargetCooldown;

            TauntTarget = null;
            AttackTarget = null;

            if (!FindNextTarget() && MonsterState != State.Return)
                MoveToHome();
        }

        public void MoveToHome()
        {
            if (DebugMove)
                Console.WriteLine($"{Name}.MoveToHome()");

            var prevAttackTarget = AttackTarget;

            MonsterState = State.Return;
            AttackTarget = null;

            var home = GetPosition(PositionType.Home);

            if (Location.Equals(home))
            {
                Sleep();
                return;
            }

            NextCancelTime = Timers.RunningTime + 5.0f;

            MoveTo(home, RunRate, false, 1.0f);

            var homePos = new Physics.Common.Position(home);

            var mvp = GetMovementParameters();
            mvp.DistanceToObject = 0.6f;
            mvp.DesiredHeading = homePos.Frame.get_heading();

            PhysicsObj.MoveToPosition(homePos, mvp);
            IsMoving = true;

            EmoteManager.OnHomeSick(prevAttackTarget);
        }

        public void CancelMoveTo()
        {
            //Console.WriteLine($"{Name}.CancelMoveTo()");

            PhysicsObj.MovementManager.MoveToManager.CancelMoveTo(WeenieError.ActionCancelled);
            PhysicsObj.MovementManager.MoveToManager.FailProgressCount = 0;

            if (MonsterState == State.Return)
                ForceHome();

            EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance, MotionCommand.Ready));

            IsMoving = false;
            NextMoveTime = Timers.RunningTime + 1.0f;

            ResetAttack();

            FindNextTarget();
        }

        public void ForceHome()
        {
            var homePos = GetPosition(PositionType.Home);

            if (DebugMove)
                Console.WriteLine($"{Name} ({Guid}) - ForceHome({homePos.ToLOCString()})");

            var setPos = new SetPosition(Location.Instance);
            setPos.Pos = new Physics.Common.Position(homePos);
            setPos.Flags = SetPositionFlags.Teleport;

            PhysicsObj.SetPosition(setPos);

            UpdatePosition_SyncLocation();

            SendUpdatePosition();

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(0.5f);
            actionChain.AddAction(this, Sleep);
            actionChain.EnqueueChain();
        }
    }
}

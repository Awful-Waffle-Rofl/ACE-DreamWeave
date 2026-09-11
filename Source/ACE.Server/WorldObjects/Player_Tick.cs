using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Sequence;
using ACE.Server.Network.Structure;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private readonly ActionQueue actionQueue = new ActionQueue();

        private int initialAge;
        private DateTime initialAgeTime;

        private const double ageUpdateInterval = 7;
        private double nextAgeUpdateTime;

        private double houseRentWarnTimestamp;
        private const double houseRentWarnInterval = 3600;

        public void Player_Tick(double currentUnixTime)
        {
            if (CharacterSaveFailed)
            {
                // Boot the player as their Character object is not saving properly
                if (!IsLoggingOut)
                {
                    log.Error($"{Session.Player.Name} | 0x{Guid} | Account: {Account.AccountName} - disconnected for CharacterSaveFailed");
                    //Session.SendCharacterError(CharacterError.AccountLogin); // forces client to error screen
                    Session.Terminate(SessionTerminationReason.CharacterSaveFailed, new GameMessageCharacterError(CharacterError.AccountLogin));
                    //Session.LogOffPlayer(true);
                    CharacterSaveFailed = false;
                }
                return;
            }

            if (BiotaSaveFailed)
            {
                // Boot the player as their Biota object is not saving properly
                if (!IsLoggingOut)
                {
                    log.Error($"{Session.Player.Name} | 0x{Guid} | Account: {Account.AccountName} - disconnected for BiotaSaveFailed");
                    //Session.SendCharacterError(CharacterError.AccountLogin); // forces client to error screen
                    Session.Terminate(SessionTerminationReason.BiotaSaveFailed, new GameMessageCharacterError(CharacterError.AccountLogin));
                    //Session.LogOffPlayer(true);
                    BiotaSaveFailed = false;
                }
                return;
            }

            actionQueue.RunActions();

            if (nextAgeUpdateTime <= currentUnixTime)
            {
                nextAgeUpdateTime = currentUnixTime + ageUpdateInterval;

                if (initialAgeTime == DateTime.MinValue)
                {
                    initialAge = Age ?? 1;
                    initialAgeTime = DateTime.UtcNow;
                }

                Age = initialAge + (int)(DateTime.UtcNow - initialAgeTime).TotalSeconds;

                // In retail, this is sent every 7 seconds. If you adjust ageUpdateInterval from 7, you'll need to re-add logic to send this every 7s (if you want to match retail)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.Age, Age ?? 1));
            }

            if (FellowVitalUpdate && Fellowship != null)
            {
                // only flags this member as dirty; FellowshipManager.Tick() does the actual sending on a
                // fixed cadence, so vitals traffic no longer scales with the 60Hz world tick
                Fellowship.MarkVitalDirty(this);
                FellowVitalUpdate = false;
            }

            if (House != null && PropertyManager.GetBool("house_rent_enabled").Item)
            {
                if (houseRentWarnTimestamp > 0 && currentUnixTime > houseRentWarnTimestamp)
                {
                    HouseManager.GetHouse(House.Guid.Full, (house) =>
                    {
                        if (house != null && house.HouseStatus == HouseStatus.Active && !house.SlumLord.IsRentPaid())
                            Session.Network.EnqueueSend(new GameMessageSystemChat($"Warning!  You have not paid your maintenance costs for the last {(house.IsApartment ? "90" : "30")} day maintenance period.  Please pay these costs by this deadline or you will lose your house, and all your items within it.", ChatMessageType.Broadcast));
                    });

                    houseRentWarnTimestamp = Time.GetFutureUnixTime(houseRentWarnInterval);
                }
                else if (houseRentWarnTimestamp == 0)
                    houseRentWarnTimestamp = Time.GetFutureUnixTime(houseRentWarnInterval);
            }
        }

        private static readonly TimeSpan MaximumTeleportTime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Called every ~5 seconds for Players
        /// </summary>
        public override void Heartbeat(double currentUnixTime)
        {
            NotifyLandblocks();

            ManaConsumersTick();

            HandleTargetVitals();

            LifestoneProtectionTick();

            PK_DeathTick();

            GagsTick();

            ClassAbilityBuffsHeartbeat();

            // Mule Vendor (DESIGN 11.4): distance leash, checked every player heartbeat.
            // Early-return inside MuleVendorLeashTick on a null CurrentSummonedVendor keeps this a
            // single null check for the overwhelming majority of players who have no mule summoned.
            MuleVendorLeashTick();

            // reconcile banked offline bonus time against time spent online since the last check - draining
            // it 1:1 while actively earning qualifying combat XP/Luminance (plus a trailing idle-timeout
            // window), accruing it again once idle longer than that - so the persisted value stays current
            // between saves (and across a crash) rather than only reconciling at logout
            UpdateOfflineBonus();

            PhysicsObj.ObjMaint.DestroyObjects();

            // Check if we're due for our periodic SavePlayer
            if (LastRequestedDatabaseSave == DateTime.MinValue)
                LastRequestedDatabaseSave = DateTime.UtcNow;

            if (LastRequestedDatabaseSave.AddSeconds(PlayerSaveIntervalSecs) <= DateTime.UtcNow)
                SavePlayerToDatabase();

            if (Teleporting && DateTime.UtcNow > Time.GetDateTimeFromTimestamp(LastTeleportStartTimestamp ?? 0).Add(MaximumTeleportTime))
            {
                if (Session != null)
                    Session.LogOffPlayer(true);
                else
                    LogOut();
            }

            base.Heartbeat(currentUnixTime);
        }

        public static float MaxSpeed = 50;
        public static float MaxSpeedSq = MaxSpeed * MaxSpeed;

        public static bool DebugPlayerMoveToStatePhysics { get; set; } = false;

        /// <summary>
        /// Flag indicates if player is doing full physics simulation.
        /// Upstream ACE enables this for PK/PKLite players only; the fork's
        /// 'fast_tick_all_players' server property extends it to every player.
        /// </summary>
        public bool FastTick => IsPKType || FastTickPolicy.AllPlayers();

        /// <summary>
        /// For advanced spellcasting / players glitching around during powersliding,
        /// the reason for this retail bug is from 2 different functions for player movement
        /// 
        /// The client's self-player uses DoMotion/StopMotion
        /// The server and other players on the client use apply_raw_movement
        ///
        /// When a 3+ button powerslide is performed, this bugs out apply_raw_movement,
        /// and causes the player to spin in place. With DoMotion/StopMotion, it performs a powerslide.
        ///
        /// With this option enabled (retail defaults to false), the player's position on the server
        /// will match up closely with the player's client during powerslides.
        ///
        /// Since the client uses apply_raw_movement to simulate the movement of nearby players,
        /// the other players will still glitch around on screen, even with this option enabled.
        ///
        /// If you wish for the positions of other players to be less glitchy, the 'MoveToState_UpdatePosition_Threshold'
        /// can be lowered to achieve that
        /// </summary>

        public void OnMoveToState(MoveToState moveToState)
        {
            if (!FastTick)
                return;

            if (DebugPlayerMoveToStatePhysics)
                Console.WriteLine(moveToState.RawMotionState);

            if (RecordCast.Enabled)
                RecordCast.OnMoveToState(moveToState);

            if (!PhysicsObj.IsMovingOrAnimating)
                PhysicsObj.UpdateTime = PhysicsTimer.CurrentTime;

            if (!PropertyManager.GetBool("client_movement_formula").Item || moveToState.StandingLongJump)
                OnMoveToState_ServerMethod(moveToState);
            else
                OnMoveToState_ClientMethod(moveToState);

            if (MagicState.IsCasting && MagicState.PendingTurnRelease && moveToState.RawMotionState.TurnCommand == 0)
                OnTurnRelease();
        }

        public void OnMoveToState_ClientMethod(MoveToState moveToState)
        {
            var rawState = moveToState.RawMotionState;
            var prevState = LastMoveToState?.RawMotionState ?? RawMotionState.None;

            var mvp = new Physics.Animation.MovementParameters();
            mvp.HoldKeyToApply = rawState.CurrentHoldKey;

            if (!PhysicsObj.IsMovingOrAnimating)
                PhysicsObj.UpdateTime = PhysicsTimer.CurrentTime;

            // ForwardCommand
            if (rawState.ForwardCommand != MotionCommand.Invalid)
            {
                // press new key
                if (prevState.ForwardCommand == MotionCommand.Invalid)
                {
                    PhysicsObj.DoMotion((uint)MotionCommand.Ready, mvp);
                    PhysicsObj.DoMotion((uint)rawState.ForwardCommand, mvp);
                }
                // press alternate key
                else if (prevState.ForwardCommand != rawState.ForwardCommand)
                {
                    PhysicsObj.DoMotion((uint)rawState.ForwardCommand, mvp);
                }
            }
            else if (prevState.ForwardCommand != MotionCommand.Invalid)
            {
                // release key
                PhysicsObj.StopMotion((uint)prevState.ForwardCommand, mvp, true);
            }

            // StrafeCommand
            if (rawState.SidestepCommand != MotionCommand.Invalid)
            {
                // press new key
                if (prevState.SidestepCommand == MotionCommand.Invalid)
                {
                    PhysicsObj.DoMotion((uint)rawState.SidestepCommand, mvp);
                }
                // press alternate key
                else if (prevState.SidestepCommand != rawState.SidestepCommand)
                {
                    PhysicsObj.DoMotion((uint)rawState.SidestepCommand, mvp);
                }
            }
            else if (prevState.SidestepCommand != MotionCommand.Invalid)
            {
                // release key
                PhysicsObj.StopMotion((uint)prevState.SidestepCommand, mvp, true);
            }

            // TurnCommand
            if (rawState.TurnCommand != MotionCommand.Invalid)
            {
                // press new key
                if (prevState.TurnCommand == MotionCommand.Invalid)
                {
                    PhysicsObj.DoMotion((uint)rawState.TurnCommand, mvp);
                }
                // press alternate key
                else if (prevState.TurnCommand != rawState.TurnCommand)
                {
                    PhysicsObj.DoMotion((uint)rawState.TurnCommand, mvp);
                }
            }
            else if (prevState.TurnCommand != MotionCommand.Invalid)
            {
                // release key
                PhysicsObj.StopMotion((uint)prevState.TurnCommand, mvp, true);
            }
        }

        public void OnMoveToState_ServerMethod(MoveToState moveToState)
        {
            var minterp = PhysicsObj.get_minterp();
            minterp.RawState.SetState(moveToState.RawMotionState);

            if (moveToState.StandingLongJump)
            {
                minterp.RawState.ForwardCommand = (uint)MotionCommand.Ready;
                minterp.RawState.SideStepCommand = 0;
            }

            var allowJump = minterp.motion_allows_jump(minterp.InterpretedState.ForwardCommand) == WeenieError.None;

            //PhysicsObj.cancel_moveto();

            minterp.apply_raw_movement(true, allowJump);
        }

        /// <summary>
        /// True only while UpdateObjectPhysics() is running on the world-thread tick.
        /// Load-bearing for teleports: command-initiated teleports (recalls, /teleto, etc.)
        /// run in action chains with InUpdate == false, which makes UpdatePlayerPosition()
        /// relocate the player immediately - any teleport logic comparing origin vs destination
        /// state must capture the origin BEFORE that relocation (see Player_Location.Teleport;
        /// a post-relocation compare here silently skipped the cross-instance client object
        /// flush until PR #174).
        /// </summary>
        public bool InUpdate;

        public override bool UpdateObjectPhysics()
        {
            try
            {
                stopwatch.Restart();

                bool landblockUpdate = false;

                InUpdate = true;

                // update position through physics engine
                if (RequestedLocation != null)
                {
                    landblockUpdate = UpdatePlayerPosition(RequestedLocation);
                    RequestedLocation = null;
                }

                if (FastTick && PhysicsObj.IsMovingOrAnimating || PhysicsObj.Velocity != Vector3.Zero)
                {
                    UpdatePlayerPhysics();

                    if (MoveToParams?.Callback != null && !PhysicsObj.IsMovingOrAnimating)
                        HandleMoveToCallback();
                }

                InUpdate = false;

                return landblockUpdate;
            }
            finally
            {
                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Player_Tick_UpdateObjectPhysics, elapsedSeconds);
                if (elapsedSeconds >= 1) // Yea, that ain't good....
                    log.Warn($"[PERFORMANCE][PHYSICS] {Guid}:{Name} took {(elapsedSeconds * 1000):N1} ms to process UpdateObjectPhysics() at loc: {Location}");
                else if (elapsedSeconds >= 0.010)
                    log.DebugFormat("[PERFORMANCE][PHYSICS] {0}:{1} took {2:N1} ms to process UpdateObjectPhysics() at loc: {3}", Guid, Name, (elapsedSeconds * 1000), Location);
            }
        }

        public void UpdatePlayerPhysics()
        {
            if (DebugPlayerMoveToStatePhysics)
                Console.WriteLine($"{Name}.UpdatePlayerPhysics({PhysicsObj.PartArray.Sequence.CurrAnim.Value.Anim.ID:X8})");

            //Console.WriteLine($"{PhysicsObj.Position.Frame.Origin}");
            //Console.WriteLine($"{PhysicsObj.Position.Frame.get_heading()}");

            PhysicsObj.update_object(Location.Instance);

            // sync ace position?
            Location.Rotation = PhysicsObj.Position.Frame.Orientation;

            if (!FastTick) return;

            // ensure PKLogout position is synced up for other players
            if (PKLogout)
            {
                EnqueueBroadcast(new GameMessageUpdateMotion(this, new Motion(MotionStance.NonCombat, MotionCommand.Ready)));
                PhysicsObj.StopCompletely(true);

                if (!PhysicsObj.IsMovingOrAnimating)
                {
                    SyncLocation();
                    SendUpdatePosition();
                }
            }

            // this fixes some differences between client movement (DoMotion/StopMotion) and server movement (apply_raw_movement)
            //
            // scenario: start casting a self-spell, and then immediately start holding the run forward key during the windup
            // on client: player will start running forward after the cast has completed
            // on server: player will stand still

            // this block of code can improve the sync between these 2 methods,
            // however there are some bugs that originate in acclient that cannot be resolved on the server
            // for example, equip a wand, and then start running forward in non-combat mode. switch to magic combat mode, and then release forward during the stance swap
            // the client will never send a 'client released forward' MoveToState in this scenario unfortunately.
            // because of this, it's better to have the 'client blip forward' bug without it, than to have the client invisibly running forward on the server.
            // commenting out this block because of this...

            /*if (!PhysicsObj.IsMovingOrAnimating && LastMoveToState != null)
            {
                // apply latest MoveToState, if applicable
                //if ((LastMoveToState.RawMotionState.Flags & (RawMotionFlags.ForwardCommand | RawMotionFlags.SideStepCommand | RawMotionFlags.TurnCommand)) != 0)
                if ((LastMoveToState.RawMotionState.Flags & RawMotionFlags.ForwardCommand) != 0 && LastMoveToState.RawMotionState.ForwardHoldKey == HoldKey.Invalid)
                {
                    if (DebugPlayerMoveToStatePhysics)
                        Console.WriteLine("Re-applying movement: " + LastMoveToState.RawMotionState.Flags);

                    OnMoveToState(LastMoveToState);

                    // re-broadcast MoveToState to other clients only
                    EnqueueBroadcast(false, new GameMessageUpdateMotion(this, CurrentMovementData));
                }
                LastMoveToState = null;
            }*/

            if (MagicState.IsCasting && MagicState.PendingTurnRelease)
                CheckTurn();
        }

        /// <summary>
        /// The maximum rate UpdatePosition packets from MoveToState will be broadcast for each player
        /// AutonomousPosition still always broadcasts UpdatePosition
        ///  
        /// The default value (1 second) was estimated from this retail video:
        /// https://youtu.be/o5lp7hWhtWQ?t=112
        /// 
        /// If you wish for players to glitch around less during powerslides, lower this value
        /// </summary>
        public static TimeSpan MoveToState_UpdatePosition_Threshold = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How long after a jump a grounded position still goes to observers with a fresh teleport sequence.
        /// Covers the landing itself, the next hop of a jump-run, and the first at-rest refresh after the run
        /// (positions refresh at ~1 Hz, the landing slide lasts ~1 s, so 1.5 s left a 1.3 m standing offset;
        /// measured 2026-08-17).
        /// </summary>
        public static TimeSpan TouchdownTeleportWindow = TimeSpan.FromSeconds(3);

        /// <summary>
        /// TRUE when the position about to be broadcast should reach observers as a teleport rather than as an
        /// interpolation node: the client reports itself on the ground, it jumped within TouchdownTeleportWindow,
        /// and the player_touchdown_teleport toggle is on. See PositionPack for why observers need this and
        /// SelfTeleportSequence for why the moving player must never receive it.
        /// </summary>
        private bool IsTouchdownTeleport()
        {
            return LastContact
                && DateTime.UtcNow - LastJumpTime < TouchdownTeleportWindow
                && PropertyManager.GetBool("player_touchdown_teleport").Item;
        }

        /// <summary>
        /// Used by physics engine to actually update a player position
        /// Automatically notifies clients of updated position
        /// </summary>
        /// <param name="newPosition">The new position being requested, before verification through physics engine</param>
        /// <returns>TRUE if object moves to a different landblock</returns>
        public bool UpdatePlayerPosition(ACE.Entity.Position newPosition, bool forceUpdate = false)
        {
            //Console.WriteLine($"{Name}.UpdatePlayerPhysics({newPosition}, {forceUpdate}, {Teleporting})");
            bool verifyContact = false;

            // possible bug: while teleporting, client can still send AutoPos packets from old landblock
            if (Teleporting && !forceUpdate) return false;

            // pre-validate movement
            if (!ValidateMovement(newPosition))
            {
                log.Error($"{Name}.UpdatePlayerPosition() - movement pre-validation failed from {Location} to {newPosition}");
                return false;
            }

            try
            {
                if (!forceUpdate) // This is needed beacuse this function might be called recursively
                    stopwatch.Restart();

                var success = true;

                var doorCheckRan = false;
                uint onPlaneCandidate = 0;

                if (PhysicsObj != null)
                {
                    var distSq = Location.SquaredDistanceTo(newPosition);

                    if (distSq > PhysicsGlobals.EpsilonSq)
                    {
                        /*var p = new Physics.Common.Position(newPosition);
                        var dist = PhysicsObj.Position.Distance(p);
                        Console.WriteLine($"Dist: {dist}");*/

                        if (newPosition.LandblockShort == 0x18A && Location.LandblockShort != 0x18A)
                            log.Info($"{Name} is getting swanky");

                        if (!Teleporting)
                        {
                            var blockDist = PhysicsObj.GetBlockDist(Location.Cell, newPosition.Cell);

                            // verify movement
                            if (distSq > MaxSpeedSq && blockDist > 1)
                            {
                                //Session.Network.EnqueueSend(new GameMessageSystemChat("Movement error", ChatMessageType.Broadcast));
                                log.Warn($"MOVEMENT SPEED: {Name} trying to move from {Location} to {newPosition}, speed: {Math.Sqrt(distSq)}");
                                return false;
                            }

                            // verify z-pos
                            if (blockDist == 0 && LastGroundPos != null && newPosition.PositionZ - LastGroundPos.PositionZ > 10 && DateTime.UtcNow - LastJumpTime > TimeSpan.FromSeconds(1) && GetCreatureSkill(Skill.Jump).Current < 1000)
                                verifyContact = true;

                            // verify closed doors - a client plugin can delete a door from its own world and walk
                            // through it. Player movement is client-authoritative (Location is assigned from
                            // newPosition below, and update_object_server force-sets RequestPos over the transition
                            // result), so this is the only place the server compares the requested path against door
                            // state it owns.
                            if (PropertyManager.GetBool("anti_blink_door_detection").Item && CloakStatus != CloakStatus.On)
                            {
                                var blockingDoor = CheckDoorCollision(Location, newPosition, out onPlaneCandidate);
                                doorCheckRan = true;

                                if (blockingDoor != null)
                                {
                                    HandleBlinkDetection(blockingDoor, newPosition);
                                    return false;
                                }
                            }
                        }

                        var curCell = LScape.get_landcell(newPosition.Cell, newPosition.Instance);
                        if (curCell != null)
                        {
                            //if (PhysicsObj.CurCell == null || curCell.ID != PhysicsObj.CurCell.ID)
                                //PhysicsObj.change_cell_server(curCell);

                            PhysicsObj.set_request_pos(newPosition.Pos, newPosition.Rotation, curCell, Location.LandblockId.Raw, newPosition.Instance);
                            if (FastTick)
                                success = PhysicsObj.update_object_server_new(newPosition.Instance);
                            else
                                success = PhysicsObj.update_object_server(newPosition.Instance);

                            if (PhysicsObj.CurCell == null && curCell.ID >> 16 != 0x18A)
                            {
                                PhysicsObj.CurCell = curCell;
                            }

                            if (verifyContact && IsJumping)
                            {
                                var blockDist = PhysicsObj.GetBlockDist(newPosition.Cell, LastGroundPos.Cell);

                                if (blockDist <= 1)
                                {
                                    log.Warn($"z-pos hacking detected for {Name}, lastGroundPos: {LastGroundPos.ToLOCString()} - requestPos: {newPosition.ToLOCString()}");
                                    Location = new ACE.Entity.Position(LastGroundPos);
                                    Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
                                    SendUpdatePosition();
                                    return false;
                                }
                            }

                            CheckMonsters();
                        }
                    }
                    else
                        PhysicsObj.Position.Frame.Orientation = newPosition.Rotation;
                }

                // double update path: landblock physics update -> updateplayerphysics() -> update_object_server() -> Teleport() -> updateplayerphysics() -> return to end of original branch
                if (Teleporting && !forceUpdate) return true;

                if (!success) return false;

                var landblockUpdate = Location.InstancedLandblock != newPosition.InstancedLandblock;

                Location = newPosition;

                // newPosition is only committed to Location here, past both later rejection paths above (the
                // z-hack rubber-band under verifyContact/IsJumping, and `if (!success) return false;`) - so the
                // on-door-plane candidate CheckDoorCollision computed for it is only now safe to commit.
                if (doorCheckRan)
                    CommitOnDoorPlaneRecord(onPlaneCandidate);

                if (RecordCast.Enabled)
                    RecordCast.Log($"CurPos: {Location.ToLOCString()}");

                if (RequestedLocationBroadcast || DateTime.UtcNow - LastUpdatePosition >= MoveToState_UpdatePosition_Threshold)
                {
                    if (IsTouchdownTeleport())
                    {
                        // the moving player gets a normal packet (its own teleport view frozen, see
                        // SelfTeleportSequence); observers get one whose ObjectTeleport sequence is newer, so
                        // their client places the copy on this landing point now instead of queueing it as an
                        // interpolation node it may only reach seconds later, via every stale node before it
                        Session.Network.EnqueueSend(new GameMessageUpdatePosition(this, false, PositionAudience.Self));
                        EnqueueBroadcast(false, new GameMessageUpdatePosition(this, false, PositionAudience.ObserversTeleport));
                        LastUpdatePosition = DateTime.UtcNow;
                    }
                    else
                        SendUpdatePosition();
                }
                else
                    Session.Network.EnqueueSend(new GameMessageUpdatePosition(this, false, PositionAudience.Self));

                if (!InUpdate)
                    LandblockManager.RelocateObjectForPhysics(this, true);

                return landblockUpdate;
            }
            finally
            {
                if (!forceUpdate) // This is needed beacuse this function might be called recursively
                {
                    var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    ServerPerformanceMonitor.AddToCumulativeEvent(ServerPerformanceMonitor.CumulativeEventHistoryType.Player_Tick_UpdateObjectPhysics, elapsedSeconds);
                    if (elapsedSeconds >= 0.100) // Yea, that ain't good....
                        log.Warn($"[PERFORMANCE][PHYSICS] {Guid}:{Name} took {(elapsedSeconds * 1000):N1} ms to process UpdatePlayerPosition() at loc: {Location}");
                    else if (elapsedSeconds >= 0.010)
                        log.DebugFormat("[PERFORMANCE][PHYSICS] {0}:{1} took {2:N1} ms to process UpdatePlayerPosition() at loc: {3}", Guid, Name, (elapsedSeconds * 1000), Location);
                }
            }
        }

        #region Anti-blink door detection

        /// <summary>
        /// Rate limit for the player-facing notice and the audit record, so a client that keeps re-sending a
        /// rejected position cannot spam either. The rubber-band itself is never rate limited.
        /// </summary>
        private DateTime lastBlinkNoticeTime = DateTime.MinValue;

        private static readonly TimeSpan BlinkNoticeCooldown = TimeSpan.FromSeconds(5);

        /// <summary>
        /// A landblock is 192 units on a side. Door and player positions are landblock-local, so a path that
        /// crosses a landblock boundary is only comparable once both ends are lifted into global coordinates.
        /// </summary>
        private const float LandblockLength = 192.0f;

        public static Vector2 GetGlobalPos(ACE.Entity.Position pos)
        {
            return new Vector2(pos.LandblockX * LandblockLength + pos.PositionX, pos.LandblockY * LandblockLength + pos.PositionY);
        }

        /// <summary>
        /// The door's blocking plane as a 2D segment: its width axis, centered on its origin, in global coords.
        /// UnitY transformed by the door's rotation is the direction it faces; the perpendicular of that is the
        /// axis the door spans, and therefore the line a player has to cross to get through it.
        /// </summary>
        public static (Vector2 Start, Vector2 End) GetDoorSegment(ACE.Entity.Position doorPos, float doorWidth)
        {
            var facing = Vector3.Transform(Vector3.UnitY, doorPos.Rotation);

            var facing2d = new Vector2(facing.X, facing.Y);

            // a door lying flat has no vertical plane to cross - no segment to test against
            if (facing2d.LengthSquared() < 1e-6f)
                return (Vector2.Zero, Vector2.Zero);

            facing2d = Vector2.Normalize(facing2d);

            var widthAxis = new Vector2(-facing2d.Y, facing2d.X);
            var center = GetGlobalPos(doorPos);
            var halfWidth = doorWidth * 0.5f;

            return (center - widthAxis * halfWidth, center + widthAxis * halfWidth);
        }

        /// <summary>
        /// The point where segments p1-p2 and p3-p4 cross, or null if they do not.
        /// </summary>
        public static Vector2? GetSegmentIntersection(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            var d1 = p2 - p1;
            var d2 = p4 - p3;

            var denominator = d1.X * d2.Y - d1.Y * d2.X;

            // parallel, or one of the segments is degenerate
            if (Math.Abs(denominator) < 1e-10f)
                return null;

            var delta = p3 - p1;

            var t = (delta.X * d2.Y - delta.Y * d2.X) / denominator;
            var u = (delta.X * d1.Y - delta.Y * d1.X) / denominator;

            if (t < 0.0f || t > 1.0f || u < 0.0f || u > 1.0f)
                return null;

            return p1 + d1 * t;
        }

        /// <summary>
        /// Squared distance from a point to the nearest point on segment a-b.
        /// </summary>
        public static float DistanceToSegmentSq(Vector2 point, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var lengthSq = ab.LengthSquared();

            if (lengthSq < 1e-10f)
                return (point - a).LengthSquared();

            var t = Vector2.Dot(point - a, ab) / lengthSq;

            t = Math.Max(0.0f, Math.Min(1.0f, t));

            return (point - (a + ab * t)).LengthSquared();
        }

        /// <summary>
        /// How close to a door's plane a player has to already be for that door to be ignored, in units.
        /// </summary>
        private const float DoorPlaneTolerance = 0.25f;

        private const float DoorPlaneToleranceSq = DoorPlaneTolerance * DoorPlaneTolerance;

        /// <summary>
        /// TRUE when a door's Z is within limit of the LOWER endpoint of the path. Using the lower end keeps a
        /// door on another storey skipped while a jump apex above the limit can no longer hide a door on the
        /// landing segment (the takeoff or landing end of every arc segment is at floor level).
        /// </summary>
        public static bool IsDoorWithinZ(float doorZ, float oldZ, float newZ, float zHeightLimit)
            => Math.Abs(doorZ - Math.Min(oldZ, newZ)) <= zHeightLimit;

        /// <summary>Door whose plane the last ACCEPTED position sat on (within DoorPlaneTolerance), 0 if none.</summary>
        private uint onDoorPlaneGuid;

        /// <summary>Unix time the player first arrived on that door's plane. Compared against Door.CloseTimestamp.</summary>
        private double onDoorPlaneSince;

        /// <summary>
        /// A player already on a closed door's plane is exempt from that door only if they got there before it
        /// closed - the "open doorway swung shut on me" case. Arriving after CloseTimestamp means the player walked
        /// onto the plane of a door that was already closed, which only a client with the door deleted can do.
        /// A door never closed since startup has CloseTimestamp 0 and is therefore always enforced.
        /// </summary>
        public static bool IsLegitimatelyOnDoorPlane(uint recordedDoorGuid, double onPlaneSince, uint doorGuid, double closeTimestamp)
            => recordedDoorGuid == doorGuid && onPlaneSince < closeTimestamp;

        /// <summary>
        /// Returns the closed door the requested movement passes through, or null if it passes through none.
        /// Also computes the on-door-plane candidate for the position movement is TARGETING, via
        /// <paramref name="onPlaneCandidateGuid"/> (0 if none) - it does NOT write onDoorPlaneGuid/onDoorPlaneSince
        /// itself, because this position is not yet accepted: UpdatePlayerPosition still has two later rejection
        /// paths after this call returns null (the z-hack rubber-band under verifyContact/IsJumping, and
        /// `if (!success) return false;` after update_object_server), either of which would otherwise let a
        /// rejected position stamp a stale timestamp that could later satisfy IsLegitimatelyOnDoorPlane for a
        /// door the player never actually reached. The caller commits the candidate only once the move sticks,
        /// via CommitOnDoorPlaneRecord. This scan only runs while anti_blink_door_detection is on, so the record
        /// goes stale while the flag is off - toggling it back on while someone stands in a doorway enforces
        /// against them once, which is acceptable.
        /// </summary>
        private Door CheckDoorCollision(ACE.Entity.Position oldPosition, ACE.Entity.Position newPosition, out uint onPlaneCandidateGuid)
        {
            onPlaneCandidateGuid = 0;

            if (CurrentLandblock == null || oldPosition == null || newPosition == null || PhysicsObj?.ObjMaint == null)
                return null;

            var debug = PropertyManager.GetBool("anti_blink_debug").Item;
            var doorWidth = (float)PropertyManager.GetDouble("anti_blink_door_width", 3.0).Item;
            var zHeightLimit = (float)PropertyManager.GetDouble("anti_blink_z_height_limit", 2.0).Item;

            var pathStart = GetGlobalPos(oldPosition);
            var pathEnd = GetGlobalPos(newPosition);

            var checkedDoors = 0;

            Door blockingDoor = null;

            // the visible set is built from this player's own landblock and cells, so it is already scoped to
            // their instance; the Instance comparison below is belt-and-braces against a realm copy leaking in
            foreach (var obj in PhysicsObj.ObjMaint.GetVisibleObjectsValues())
            {
                if (!(obj.WeenieObj?.WorldObject is Door door))
                    continue;

                // IsOpen goes false the instant Close() starts, but Ethereal only clears when the animation
                // finishes in FinalizeClose - a player already in the doorway as it swings shut is not blinking
                if (door.IsOpen || door.Ethereal == true)
                    continue;

                var doorPos = door.Location;

                if (doorPos == null || doorPos.Instance != oldPosition.Instance)
                    continue;

                // multi-floor guard: a door a storey above or below the lower end of this path is not on it
                if (!IsDoorWithinZ(doorPos.PositionZ, oldPosition.PositionZ, newPosition.PositionZ, zHeightLimit))
                    continue;

                checkedDoors++;

                var doorSegment = GetDoorSegment(doorPos, doorWidth);

                // If the player is ALREADY standing on this door's plane AND got there before it closed - they
                // were in an open doorway when it swung shut - then every move they make crosses it, in both
                // directions, and enforcing it would wedge them there permanently. A player who arrived on the
                // plane after CloseTimestamp got there through a door their client had already deleted.
                if (DistanceToSegmentSq(pathStart, doorSegment.Start, doorSegment.End) < DoorPlaneToleranceSq
                    && IsLegitimatelyOnDoorPlane(onDoorPlaneGuid, onDoorPlaneSince, door.Guid.Full, door.CloseTimestamp))
                {
                    if (debug)
                        log.Info($"[BLINK DEBUG] {Name} already on the plane of '{door.Name}' (0x{door.Guid.Full:X8}) since before it closed - not enforced");

                    continue;
                }

                var intersection = GetSegmentIntersection(pathStart, pathEnd, doorSegment.Start, doorSegment.End);

                if (intersection == null)
                    continue;

                if (debug)
                    log.Info($"[BLINK DEBUG] {Name} path crosses '{door.Name}' (0x{door.Guid.Full:X8}) at ({intersection.Value.X:F2}, {intersection.Value.Y:F2})");

                blockingDoor = door;
                break;
            }

            if (blockingDoor == null)
                onPlaneCandidateGuid = FindOnDoorPlaneCandidate(pathEnd, oldPosition, newPosition, doorWidth, zHeightLimit);

            if (debug && blockingDoor == null)
                log.Info($"[BLINK DEBUG] {Name} - {checkedDoors} closed doors in range, no crossing, {oldPosition.ToLOCString()} -> {newPosition.ToLOCString()}");

            return blockingDoor;
        }

        /// <summary>
        /// Finds the guid of the door (0 if none) whose plane the position movement is TARGETING would end up
        /// standing on. Candidates include OPEN doors too (unlike CheckDoorCollision's blocking scan), since a
        /// player can legitimately end up standing on the plane of a door that is currently open. Pure lookup -
        /// does not touch onDoorPlaneGuid/onDoorPlaneSince; see CheckDoorCollision's doc comment for why.
        /// </summary>
        private uint FindOnDoorPlaneCandidate(Vector2 pathEnd, ACE.Entity.Position oldPosition, ACE.Entity.Position newPosition, float doorWidth, float zHeightLimit)
        {
            foreach (var obj in PhysicsObj.ObjMaint.GetVisibleObjectsValues())
            {
                if (!(obj.WeenieObj?.WorldObject is Door door))
                    continue;

                var doorPos = door.Location;

                if (doorPos == null || doorPos.Instance != oldPosition.Instance)
                    continue;

                if (!IsDoorWithinZ(doorPos.PositionZ, oldPosition.PositionZ, newPosition.PositionZ, zHeightLimit))
                    continue;

                var doorSegment = GetDoorSegment(doorPos, doorWidth);

                if (DistanceToSegmentSq(pathEnd, doorSegment.Start, doorSegment.End) < DoorPlaneToleranceSq)
                    return door.Guid.Full;
            }

            return 0;
        }

        /// <summary>
        /// Pure transition for the on-door-plane record: 0 clears it; a new guid stamps `now`; the same guid
        /// leaves `since` untouched so a player who never left the plane keeps their original arrival time.
        /// </summary>
        public static (uint Guid, double Since) NextOnDoorPlaneRecord(uint recordedGuid, double recordedSince, uint candidateGuid, double now)
        {
            if (candidateGuid == 0)
                return (0, recordedSince);

            if (candidateGuid != recordedGuid)
                return (candidateGuid, now);

            return (recordedGuid, recordedSince);
        }

        /// <summary>
        /// Commits an on-door-plane candidate computed by CheckDoorCollision, once the position it was computed
        /// for has actually been accepted as the player's new Location. Applying NextOnDoorPlaneRecord here -
        /// not inside CheckDoorCollision - is what keeps a later-rejected position (the z-hack rubber-band, or
        /// a failed update_object_server) from stamping onDoorPlaneSince for a plane the player never reached.
        /// </summary>
        private void CommitOnDoorPlaneRecord(uint candidateGuid)
        {
            var next = NextOnDoorPlaneRecord(onDoorPlaneGuid, onDoorPlaneSince, candidateGuid, Time.GetUnixTime());

            onDoorPlaneGuid = next.Guid;
            onDoorPlaneSince = next.Since;
        }

        /// <summary>
        /// Rubber-bands the player back to their last valid position and records the attempt.
        /// </summary>
        private void HandleBlinkDetection(Door door, ACE.Entity.Position rejectedPosition)
        {
            var now = DateTime.UtcNow;

            if (now - lastBlinkNoticeTime >= BlinkNoticeCooldown)
            {
                lastBlinkNoticeTime = now;

                var doorName = door.Name ?? "unknown door";
                var doorLoc = door.Location?.ToLOCString() ?? "unknown";

                log.Warn($"[BLINK] {Name} (0x{Guid.Full:X8}, account {Account?.AccountName}) crossed closed door '{doorName}' at {doorLoc} - rejected {Location.ToLOCString()} -> {rejectedPosition.ToLOCString()}");

                PlayerManager.BroadcastToAuditChannel(null, $"[BLINK] {Name} (account {Account?.AccountName}) crossed closed door '{doorName}' at {doorLoc}");

                Session?.Network?.EnqueueSend(new GameMessageSystemChat("Blink detected, relocating to last known valid position", ChatMessageType.Broadcast));
            }

            // Location is left exactly as it is, and that IS the rubber-band: the assignment from newPosition
            // happens further down UpdatePlayerPosition, after this check has already returned false, so
            // Location still holds the last position the server accepted. All that is needed is to bump the
            // force-position sequence and tell the client to go back to where the server still has them.
            Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
            SendUpdatePosition();
        }

        #endregion

        private static HashSet<uint> buggedCells = new HashSet<uint>()
        {
            0xD6990112,
            0xD599012C
        };

        public bool ValidateMovement(ACE.Entity.Position newPosition)
        {
            if (CurrentLandblock == null)
                return false;

            if (!Teleporting && Location.LandblockShort != newPosition.Cell >> 16)
            {
                if ((Location.Cell & 0xFFFF) >= 0x100 && (newPosition.Cell & 0xFFFF) >= 0x100)
                {
                    if (!buggedCells.Contains(Location.Cell) || !buggedCells.Contains(newPosition.Cell))
                        return false;
                }

                if (CurrentLandblock.IsDungeon)
                {
                    var destBlock = LScape.get_landblock(newPosition.Cell, newPosition.Instance);
                    if (destBlock != null && destBlock.IsDungeon)
                        return false;
                }
            }
            return true;
        }


        public bool SyncLocationWithPhysics()
        {
            if (PhysicsObj.CurCell == null)
            {
                Console.WriteLine($"{Name}.SyncLocationWithPhysics(): CurCell is null!");
                return false;
            }

            var blockcell = PhysicsObj.Position.ObjCellID;
            var pos = PhysicsObj.Position.Frame.Origin;
            var rotate = PhysicsObj.Position.Frame.Orientation;

            var landblockUpdate = blockcell << 16 != CurrentLandblock.Id.Landblock || Location.Instance != CurrentLandblock.Instance;

            Location = new ACE.Entity.Position(blockcell, pos, rotate) { Instance = Location.Instance };

            return landblockUpdate;
        }

        private bool gagNoticeSent = false;

        public void GagsTick()
        {
            if (IsGagged)
            {
                if (!gagNoticeSent)
                {
                    SendGagNotice();
                    gagNoticeSent = true;
                }

                // a permanent gag never counts down; only @ungag ends it
                if (GagDuration >= PlayerManager.PermanentGagDurationSeconds)
                    return;

                // check for gag expiration, if expired, remove gag.
                GagDuration -= CachedHeartbeatInterval;

                if (GagDuration <= 0)
                {
                    IsGagged = false;
                    GagTimestamp = 0;
                    GagDuration = 0;
                    SaveBiotaToDatabase();
                    SendUngagNotice();
                    gagNoticeSent = false;
                }
            }
        }

        /// <summary>
        /// Prepare new action to run on this player
        /// </summary>
        public override void EnqueueAction(IAction action)
        {
            actionQueue.EnqueueAction(action);
        }

        /// <summary>
        /// Called every ~5 secs for equipped mana consuming items
        /// </summary>
        public void ManaConsumersTick()
        {
            if (!EquippedObjectsLoaded) return;

            foreach (var item in EquippedObjects.Values)
            {
                if (!item.IsAffecting)
                    continue;

                if (item.ItemCurMana == null || item.ItemMaxMana == null || item.ManaRate == null)
                    continue;

                var burnRate = -item.ManaRate.Value;

                if (LumAugItemManaUsage != 0)
                    burnRate *= GetNegativeRatingMod(LumAugItemManaUsage * 5);

                item.ItemManaRateAccumulator += (float)(burnRate * CachedHeartbeatInterval);

                if (item.ItemManaRateAccumulator < 1)
                    continue;

                var manaToBurn = (int)Math.Floor(item.ItemManaRateAccumulator);

                if (manaToBurn > item.ItemCurMana)
                    manaToBurn = item.ItemCurMana.Value;

                item.ItemCurMana -= manaToBurn;

                item.ItemManaRateAccumulator -= manaToBurn;

                if (item.ItemCurMana > 0)
                    CheckLowMana(item, burnRate);
                else
                    HandleManaDepleted(item);
            }
        }

        private bool CheckLowMana(WorldObject item, double burnRate)
        {
            const int lowManaWarningSeconds = 120;

            var secondsUntilEmpty = item.ItemCurMana / burnRate;

            if (secondsUntilEmpty > lowManaWarningSeconds)
            {
                item.ItemManaDepletionMessage = false;
                return false;
            }
            if (!item.ItemManaDepletionMessage)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"Your {item.Name} is low on Mana.", ChatMessageType.Magic));
                item.ItemManaDepletionMessage = true;
            }
            return true;
        }

        private void HandleManaDepleted(WorldObject item)
        {
            var msg = new GameMessageSystemChat($"Your {item.Name} is out of Mana.", ChatMessageType.Magic);
            var sound = new GameMessageSound(Guid, Sound.ItemManaDepleted);
            Session.Network.EnqueueSend(msg, sound);

            // unsure if these messages / sounds were ever sent in retail,
            // or if it just purged the enchantments invisibly
            // doing a delay here to prevent 'SpellExpired' sounds from overlapping with 'ItemManaDepleted'
            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(2.0f);
            actionChain.AddAction(this, () =>
            {
                foreach (var spellId in item.Biota.GetKnownSpellsIds(item.BiotaDatabaseLock))
                    RemoveItemSpell(item, (uint)spellId);
            });
            actionChain.EnqueueChain();

            item.OnSpellsDeactivated();
        }

        public override void HandleMotionDone(uint motionID, bool success)
        {
            //Console.WriteLine($"{Name}.HandleMotionDone({(MotionCommand)motionID}, {success})");

            if (!FastTick) return;

            if (FoodState.IsChugging)
                HandleMotionDone_UseConsumable(motionID, success);

            if (MagicState.IsCasting)
                HandleMotionDone_Magic(motionID, success);
        }
    }
}

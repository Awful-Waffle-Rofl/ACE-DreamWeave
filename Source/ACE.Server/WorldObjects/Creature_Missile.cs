using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;
using ACE.Server.Physics.Extensions;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        public float ReloadMissileAmmo(ActionChain actionChain = null)
        {
            var weapon = GetEquippedMissileWeapon();
            var ammo = GetEquippedAmmo();

            if (weapon == null || ammo == null) return 0.0f;

            var newChain = actionChain == null;
            if (newChain)
                actionChain = new ActionChain();

            var animLength = 0.0f;
            if (weapon.IsAmmoLauncher)
            {
                var animSpeed = GetAnimSpeed();
                //Console.WriteLine($"AnimSpeed: {animSpeed}");

                animLength = EnqueueMotionPersist(actionChain, MotionCommand.Reload, animSpeed);   // start pulling out next arrow
                EnqueueMotionPersist(actionChain, MotionCommand.Ready);    // finish reloading
            }

            // ensure ammo visibility for players
            actionChain.AddAction(this, () =>
            {
                if (CombatMode != CombatMode.Missile)
                    return;

                EnqueueActionBroadcast(p => p.TrackEquippedObject(this, ammo));

                var delayChain = new ActionChain();
                delayChain.AddDelaySeconds(0.001f);     // ensuring this message gets sent after player broadcasts above...
                delayChain.AddAction(this, () =>
                {
                    EnqueueBroadcast(new GameMessageParentEvent(this, ammo, ACE.Entity.Enum.ParentLocation.RightHand, ACE.Entity.Enum.Placement.RightHandCombat));
                });
                delayChain.EnqueueChain();
            });

            if (newChain)
                actionChain.EnqueueChain();

            var animLength2 = Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, MotionCommand.Reload, MotionCommand.Ready);
            //Console.WriteLine($"AnimLength: {animLength} + {animLength2}");

            return animLength + animLength2;
        }

        public Vector3 GetDir2D(Vector3 source, Vector3 dest)
        {
            var diff = dest - source;
            diff.Z = 0;
            return Vector3.Normalize(diff);
        }

        // PROTOTYPE: count/damage/spread-angle come from dedicated MultiShot* properties (WorldObject_Weapon.cs),
        // independent of melee's Cleaving. Range is intentionally NOT independently tunable - it always matches
        // the shooter's own current missile range (GetMaxMissileRange), same as the primary shot. The base width
        // stays code-level for now, not per-weapon tunable.
        public const float MultiShotBaseWidth = 1.5f;      // flat half-width of the fan near the shooter, in units

        // PROTOTYPE: extra targets must also be within this distance of the PRIMARY target (not just within the
        // shooter's angular fan out to the weapon's own max range) - otherwise a point-blank primary shot could
        // still acquire a sibling clear out near the edge of the weapon's range, which read as a "high arc to the
        // end of range" from a shot that visually only travelled a few feet. This clusters extra hits near where
        // the primary arrow actually lands, while the fan's forward/lateral checks (still measured from the
        // shooter) continue to enforce "never behind the shooter, never outside the weapon's own arc."
        public const float MultiShotTargetProximityRange = 20.0f;   // max distance (units) from the primary target

        /// <summary>
        /// Acquires up to totalTargets additional creatures for a multi-shot missile attack, using a
        /// trapezoid ("fan with a widened base") in front of the shooter, aimed at the original target's direction
        /// rather than the shooter's facing. The flat base avoids the standard cone-apex problem where an enemy
        /// standing close to the shooter but off to the side would otherwise register a large angular deviation
        /// and get incorrectly excluded.
        ///
        /// Unlike GetCleaveTarget, this does not spawn a real flying projectile per extra target - hits are
        /// resolved instantly (same as melee cleave calling DamageTarget directly), specifically so that one
        /// acquired creature standing in front of another can never "block" the other from being hit the way a
        /// physically-simulated projectile could (ProjectileCollisionHelper.OnCollideObject treats a collision
        /// with anything other than a projectile's assigned ProjectileTarget as a miss).
        ///
        /// PROTOTYPE: multi-shot never targets players (PvP exclusion) - see also the primary-target-is-a-player
        /// check in Player_Missile.LaunchMissile, which skips calling this at all in that case.
        /// </summary>
        public List<Creature> GetMultiShotTargets(Creature target, WorldObject weapon, int totalTargets)
        {
            var multiShotTargets = new List<Creature>();

            // the caller decides how many extra arrows there are (weapon count + Multishot class ability
            // rank, stacking); the weapon still shapes the fan via MultiShotSpreadAngle
            if (totalTargets <= 0)
                return multiShotTargets;

            var player = this as Player;

            var aimDir = GetDir2D(Location.Pos, target.Location.Pos);
            var rightDir = new Vector3(-aimDir.Y, aimDir.X, 0);

            var maxRange = GetMaxMissileRange();
            var spreadRate = (float)Math.Tan(weapon.MultiShotSpreadAngle * Math.PI / 180.0);

            // sort visible objects by ascending distance, same source GetCleaveTarget uses
            var visible = PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);
            visible.Sort(DistanceComparator);

            foreach (var obj in visible)
            {
                // multi-shot skips the original (player-selected) target - it already has its own real projectile
                if (obj.ID == target.PhysicsObj.ID)
                    continue;

                var creature = obj.WeenieObj.WorldObject as Creature;
                if (creature == null || creature.Teleporting || creature.IsDead)
                    continue;

                // PvP exclusion: multi-shot's extra hits never target players
                if (creature is Player)
                    continue;

                if (player != null && player.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                if (!creature.Attackable && creature.TargetingTactic == TargetingTactic.None || creature.Teleporting)
                    continue;

                if (creature is CombatPet && (player != null || this is CombatPet))
                    continue;

                var toCandidate = creature.Location.Pos - Location.Pos;
                toCandidate.Z = 0;

                var forwardDist = Vector3.Dot(toCandidate, aimDir);
                if (forwardDist < 0 || forwardDist > maxRange)
                    continue;

                var lateralDist = Math.Abs(Vector3.Dot(toCandidate, rightDir));
                if (lateralDist > MultiShotBaseWidth + forwardDist * spreadRate)
                    continue;

                // must also be near where the primary arrow actually lands, not just somewhere in the fan out
                // to the weapon's full range - see MultiShotTargetProximityRange
                var toPrimaryTarget = creature.Location.Pos - target.Location.Pos;
                toPrimaryTarget.Z = 0;
                if (toPrimaryTarget.Length() > MultiShotTargetProximityRange)
                    continue;

                // Terrain/walls should block a multi-shot hit; other creatures should not (2 enemies in a line
                // should each still get hit). NOT YET VERIFIED: whether IsDirectVisible's underlying physics
                // raycast treats intervening creatures as obstacles the same way it treats geometry - test this
                // explicitly (two enemies in a direct line) once this is running.
                if (!IsDirectVisible(creature))
                    continue;

                multiShotTargets.Add(creature);
                if (multiShotTargets.Count == totalTargets)
                    break;
            }

            return multiShotTargets;
        }

        /// <summary>
        /// Launches a projectile from player to target
        /// </summary>
        public WorldObject LaunchProjectile(WorldObject weapon, WorldObject ammo, WorldObject target, Vector3 origin, Quaternion orientation, Vector3 velocity)
        {
            var player = this as Player;

            if (!velocity.IsValid())
            {
                if (player != null)
                    player.SendWeenieError(WeenieError.YourAttackMisfired);

                return null;
            }

            var proj = WorldObjectFactory.CreateNewWorldObject(ammo.WeenieClassId);

            proj.ProjectileSource = this;
            proj.ProjectileTarget = target;

            proj.ProjectileLauncher = weapon;
            proj.ProjectileAmmo = ammo;

            proj.Location = new Position(Location);
            proj.Location.Pos = origin;
            proj.Location.Rotation = orientation;

            SetProjectilePhysicsState(proj, target, velocity);

            var success = LandblockManager.AddObject(proj);

            if (!success || proj.PhysicsObj == null)
            {
                if (!proj.HitMsg)
                {
                    if (player != null)
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat("Your missile attack hit the environment.", ChatMessageType.Broadcast));
                }

                proj.Destroy();
                return null;
            }

            if (!IsProjectileVisible(proj))
            {
                proj.OnCollideEnvironment();

                proj.Destroy();
                return null;
            }

            var pkStatus = player?.PlayerKillerStatus ?? PlayerKillerStatus.Creature;

            proj.EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(proj, PropertyInt.PlayerKillerStatus, (int)pkStatus));
            proj.EnqueueBroadcast(new GameMessageScript(proj.Guid, PlayScript.Launch, 0f));

            // detonate point-blank projectiles immediately
            /*var radsum = target.PhysicsObj.GetRadius() + proj.PhysicsObj.GetRadius();
            var dist = Vector3.Distance(origin, dest);
            if (dist < radsum)
            {
                Console.WriteLine($"Point blank");
                proj.OnCollideObject(target);
            }*/

            return proj;
        }

        public const float ProjSpawnHeight = 0.8454f;

        /// <summary>
        /// Returns the origin to spawn the projectile in the attacker local space
        /// </summary>
        public Vector3 GetProjectileSpawnOrigin(uint projectileWcid, MotionCommand motion)
        {
            var attackerRadius = PhysicsObj.GetPhysicsRadius();
            var projectileRadius = GetProjectileRadius(projectileWcid);

            //Console.WriteLine($"{Name} radius: {attackerRadius}");
            //Console.WriteLine($"Projectile {projectileWcid} radius: {projectileRadius}");

            var radsum = attackerRadius * 2.0f + projectileRadius * 2.0f + PhysicsGlobals.EPSILON;

            var origin = new Vector3(0, radsum, 0);

            // rotate by aim angle
            var angle = motion.GetAimAngle().ToRadians();
            var zRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle);

            origin = Vector3.Transform(origin, zRotation);

            origin.Z += Height * ProjSpawnHeight;

            return origin;
        }

        /// <summary>
        /// Returns the cached physics radius for a projectile wcid
        /// </summary>
        private static float GetProjectileRadius(uint projectileWcid)
        {
            if (ProjectileRadiusCache.TryGetValue(projectileWcid, out var radius))
                return radius;

            var weenie = DatabaseManager.World.GetCachedWeenie(projectileWcid);

            if (weenie == null)
            {
                log.Error($"Creature_Missile.GetProjectileRadius(): couldn't find projectile weenie {projectileWcid}");
                return 0.0f;
            }

            if (!weenie.PropertiesDID.TryGetValue(PropertyDataId.Setup, out var setupId))
            {
                log.Error($"Creature_Missile.GetProjectileRadius(): couldn't find SetupId for {weenie.WeenieClassId} - {weenie.ClassName}");
                return 0.0f;
            }

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupId);

            if (!weenie.PropertiesFloat.TryGetValue(PropertyFloat.DefaultScale, out var scale))
                scale = 1.0f;

            var result = (float)(setup.Spheres[0].Radius * scale);

            ProjectileRadiusCache.TryAdd(projectileWcid, result);

            return result;
        }

        // lowest value found in data / for starter bows
        public const float DefaultProjectileSpeed = 20.0f;

        public float GetProjectileSpeed()
        {
            var missileLauncher = GetEquippedMissileWeapon();

            var maxVelocity = missileLauncher?.MaximumVelocity ?? DefaultProjectileSpeed;

            if (maxVelocity == 0.0f)
            {
                log.Warn($"{Name}.GetMissileSpeed() - {missileLauncher.Name} ({missileLauncher.Guid}) has speed 0");

                maxVelocity = DefaultProjectileSpeed;
            }

            if (this is Player player && player.GetCharacterOption(CharacterOption.UseFastMissiles))
            {
                maxVelocity *= PropertyManager.GetDouble("fast_missile_modifier").Item;
            }

            // hard cap in physics engine
            maxVelocity = Math.Min(maxVelocity, PhysicsGlobals.MaxVelocity);

            //Console.WriteLine($"MaxVelocity: {maxVelocity}");

            return (float)maxVelocity;
        }

        public Vector3 GetAimVelocity(WorldObject target, float projectileSpeed)
        {
            var crossLandblock = Location.InstancedLandblock != target.Location.InstancedLandblock;

            // eye level -> target point
            var origin = crossLandblock ? Location.ToGlobal(false) : Location.Pos;
            origin.Z += Height * ProjSpawnHeight;

            var dest = crossLandblock ? target.Location.ToGlobal(false) : target.Location.Pos;
            dest.Z += target.Height / GetAimHeight(target);

            var dir = Vector3.Normalize(dest - origin);

            var velocity = GetProjectileVelocity(target, origin, dir, dest, projectileSpeed, out float time);

            return velocity;
        }

        public Vector3 CalculateProjectileVelocity(Vector3 localOrigin, WorldObject target, float projectileSpeed, out Vector3 origin, out Quaternion rotation, out float time)
        {
            var sourceLoc = PhysicsObj.Position.ACEPosition(PhysicsObj.CurInstance);
            var targetLoc = target.PhysicsObj.Position.ACEPosition(target.PhysicsObj.CurInstance);

            var crossLandblock = sourceLoc.InstancedLandblock != targetLoc.InstancedLandblock;

            var startPos = crossLandblock ? sourceLoc.ToGlobal(false) : sourceLoc.Pos;
            var endPos = crossLandblock ? targetLoc.ToGlobal(false) : targetLoc.Pos;

            var dir = Vector3.Normalize(endPos - startPos);

            var angle = Math.Atan2(-dir.X, dir.Y);

            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)angle);

            origin = sourceLoc.Pos + Vector3.Transform(localOrigin, rotation);

            startPos += Vector3.Transform(localOrigin, rotation);
            endPos.Z += target.Height / GetAimHeight(target);

            var velocity = GetProjectileVelocity(target, startPos, dir, endPos, projectileSpeed, out time);

            return velocity;
        }

        /// <summary>
        /// Updates the ammo count or destroys the ammo after launching the projectile.
        /// </summary>
        /// <param name="ammo">The equipped missile ammo object</param>
        public virtual void UpdateAmmoAfterLaunch(WorldObject ammo)
        {
            // hide previously held ammo
            EnqueueBroadcast(new GameMessagePickupEvent(ammo));

            // monsters have infinite ammo?

            /*if (ammo.StackSize == null || ammo.StackSize <= 1)
            {
                TryUnwieldObjectWithBroadcasting(ammo.Guid, out _, out _);
                ammo.Destroy();
            }
            else
            {
                ammo.SetStackSize(ammo.StackSize - 1);
                EnqueueBroadcast(new GameMessageSetStackSize(ammo));
            }*/
        }

        /// <summary>
        /// Calculates the velocity to launch the projectile from origin to dest
        /// </summary>
        public Vector3 GetProjectileVelocity(WorldObject target, Vector3 origin, Vector3 dir, Vector3 dest, float speed, out float time, bool useGravity = true)
        {
            time = 0.0f;
            Vector3 s0;
            float t0;

            var gravity = useGravity ? -PhysicsGlobals.Gravity : 0.00001f;

            var targetVelocity = target.PhysicsObj.CachedVelocity;

            if (!targetVelocity.Equals(Vector3.Zero))
            {
                if (this is Player player && !player.GetCharacterOption(CharacterOption.LeadMissileTargets))
                {
                    // fall through
                }
                else
                {
                    // use movement quartic solver
                    if (!PropertyManager.GetBool("trajectory_alt_solver").Item)
                    {
                        var numSolutions = Trajectory.solve_ballistic_arc(origin, speed, dest, targetVelocity, gravity, out s0, out _, out time);

                        if (numSolutions > 0)
                            return s0;
                    }
                    else
                        return Trajectory2.CalculateTrajectory(origin, dest, targetVelocity, speed, useGravity);
                }
            }

            // use stationary solver
            if (!PropertyManager.GetBool("trajectory_alt_solver").Item)
            {
                Trajectory.solve_ballistic_arc(origin, speed, dest, gravity, out s0, out _, out t0, out _);

                time = t0;
                return s0;
            }
            else
                return Trajectory2.CalculateTrajectory(origin, dest, Vector3.Zero, speed, useGravity);
        }

        /// <summary>
        /// Sets the physics state for a launched projectile
        /// </summary>
        public void SetProjectilePhysicsState(WorldObject obj, WorldObject target, Vector3 velocity)
        {
            obj.InitPhysicsObj();

            obj.ReportCollisions = true;
            obj.Missile = true;
            obj.AlignPath = true;
            obj.PathClipped = true;
            obj.Ethereal = false;
            obj.IgnoreCollisions = false;

            var pos = obj.Location.Pos;
            var rotation = obj.Location.Rotation;
            obj.PhysicsObj.Position.Frame.Origin = pos;
            obj.PhysicsObj.Position.Frame.Orientation = rotation;

            if (obj.HasMissileFlightPlacement)
                obj.Placement = ACE.Entity.Enum.Placement.MissileFlight;
            else
                obj.Placement = null;

            obj.CurrentMotionState = null;

            obj.PhysicsObj.Velocity = velocity;
            obj.PhysicsObj.ProjectileTarget = target.PhysicsObj;

            // Projectiles with RotationSpeed get omega values and "align path" turned off which
            // creates the nice swirling animation
            if ((obj.RotationSpeed ?? 0) != 0)
            {
                obj.AlignPath = false;
                obj.PhysicsObj.Omega = new Vector3((float)(Math.PI * 2 * obj.RotationSpeed), 0, 0);
            }

            obj.PhysicsObj.set_active(true);
        }

        public Sound GetLaunchMissileSound(WorldObject weapon)
        {
            switch (weapon.DefaultCombatStyle)
            {
                case CombatStyle.Bow:
                    return Sound.BowRelease;
                case CombatStyle.Crossbow:
                    return Sound.CrossbowRelease;
                default:
                    return Sound.ThrownWeaponRelease1;
            }
        }

        public const float MetersToYards = 1.094f;    // 1.09361
        public const float MissileRangeCap = 85.0f / MetersToYards;   // 85 yards = ~77.697 meters w/ ac formula
        public const float DefaultMaxVelocity = 20.0f;    // ?

        public float GetMaxMissileRange()
        {
            var weapon = GetEquippedMissileWeapon();
            var maxVelocity = weapon?.MaximumVelocity ?? DefaultMaxVelocity;

            var missileRange = (float)Math.Pow(maxVelocity, 2.0f) * 0.1020408163265306f;
            //var missileRange = (float)Math.Pow(maxVelocity, 2.0f) * 0.0682547266398198f;

            //var strengthMod = SkillFormula.GetAttributeMod((int)Strength.Current);
            //var maxRange = Math.Min(missileRange * strengthMod, MissileRangeCap);
            var maxRange = Math.Min(missileRange, MissileRangeCap);

            // any kind of other caps for monsters specifically?
            // throwing lugian rocks @ 85 yards seems a bit far...

            //Console.WriteLine($"{Name}.GetMaxMissileRange(): maxVelocity={maxVelocity}, strengthMod={strengthMod}, maxRange={maxRange}");

            // for client display
            /*var maxRangeYards = maxRange * MetersToYards;
            if (maxRangeYards >= 10.0f)
                maxRangeYards -= maxRangeYards % 5.0f;
            else
                maxRangeYards = (float)Math.Ceiling(maxRangeYards);

            Console.WriteLine($"Max range: {maxRange} ({maxRangeYards} yds.)");*/

            return maxRange;
        }

        public static MotionCommand GetAimLevel(Vector3 velocity)
        {
            // get z-angle?
            var zAngle = Vector3.Normalize(velocity).Z * 90.0f;

            var aimLevel = MotionCommand.AimLevel;

            if (zAngle >= 82.5f)
                aimLevel = MotionCommand.AimHigh90;
            else if (zAngle >= 67.5f)
                aimLevel = MotionCommand.AimHigh75;
            else if (zAngle >= 52.5f)
                aimLevel = MotionCommand.AimHigh60;
            else if (zAngle >= 37.5f)
                aimLevel = MotionCommand.AimHigh45;
            else if (zAngle >= 22.5f)
                aimLevel = MotionCommand.AimHigh30;
            else if (zAngle >= 7.5f)
                aimLevel = MotionCommand.AimHigh15;
            else if (zAngle > -7.5f)
                aimLevel = MotionCommand.AimLevel;
            else if (zAngle > -22.5f)
                aimLevel = MotionCommand.AimLow15;
            else if (zAngle > -37.5f)
                aimLevel = MotionCommand.AimLow30;
            else if (zAngle > -52.5f)
                aimLevel = MotionCommand.AimLow45;
            else if (zAngle > -67.5f)
                aimLevel = MotionCommand.AimLow60;
            else if (zAngle > -82.5f)
                aimLevel = MotionCommand.AimLow75;
            else
                aimLevel = MotionCommand.AimLow90;

            //Console.WriteLine($"Z Angle: {aimLevel.GetAimAngle()}");

            return aimLevel;
        }
    }
}

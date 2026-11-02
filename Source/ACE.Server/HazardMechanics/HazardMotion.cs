using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// One straight piece of motion: from <see cref="Start"/> at time <see cref="T0"/> to <see cref="End"/> at
    /// <see cref="EndTime"/>, at constant velocity. Pure value; times are run-relative seconds (ctx.Now).
    /// </summary>
    public readonly struct HazardLeg
    {
        public Vector3 Start { get; }
        public Vector3 End { get; }
        public double T0 { get; }
        public double Duration { get; }

        public HazardLeg(Vector3 start, Vector3 end, double t0, double duration)
        {
            Start = start;
            End = end;
            T0 = t0;
            Duration = double.IsFinite(duration) && duration > 0.0 ? duration : 0.0;
        }

        public double EndTime => T0 + Duration;

        /// <summary>(End - Start) / Duration; zero for a zero-length or zero-duration leg.</summary>
        public Vector3 Velocity => Duration > 0.0 ? (End - Start) / (float)Duration : Vector3.Zero;

        /// <summary>The computed position at <paramref name="t"/>, clamped to the leg's ends.</summary>
        public Vector3 PositionAt(double t)
        {
            if (Duration <= 0.0 || t >= EndTime)
                return End;

            if (t <= T0)
                return Start;

            return Vector3.Lerp(Start, End, (float)((t - T0) / Duration));
        }
    }

    /// <summary>What the driver must do for a moving hazard on this tick.</summary>
    public enum HazardMotionStep
    {
        /// <summary>Nothing: the current sub-leg is still running.</summary>
        None,

        /// <summary>A sub-leg boundary passed: broadcast <see cref="HazardPath.SentVelocity"/>. No snap.</summary>
        NewVelocity,

        /// <summary>The whole leg is over: snap to <see cref="HazardPath.End"/> and zero the velocity.</summary>
        Finished,
    }

    /// <summary>
    /// One leg as the driver runs it: the sub-legs it was split into, and what was last told to clients.
    ///
    /// WHY THE CLIENT PREDICTION. A sub-leg boundary is only noticed on the next driver tick (up to tick_ms late),
    /// and a client keeps flying the old velocity until then. Instead of sending the next sub-leg's IDEAL velocity
    /// (which would carry that error into every later sub-leg), the new velocity is aimed from where the client
    /// must be NOW (last sent position + last sent velocity x elapsed) at the sub-leg's end, arriving on time.
    /// The error therefore never accumulates past one tick. Damage never uses this - it uses the ideal
    /// <see cref="PositionAt"/>, per shared invariant 3.
    /// </summary>
    public sealed class HazardPath
    {
        private readonly HazardLeg[] subLegs;
        private readonly Vector3? fixedOmega;
        private readonly float rollRadius;

        public HazardPath(IReadOnlyList<HazardLeg> legs, Vector3? omega = null, float rollRadius = 0f, bool autoStop = true)
        {
            if (legs == null || legs.Count == 0)
                throw new ArgumentException("a path needs at least one leg", nameof(legs));

            subLegs = new HazardLeg[legs.Count];

            for (var i = 0; i < legs.Count; i++)
                subLegs[i] = legs[i];

            fixedOmega = omega;
            this.rollRadius = rollRadius;
            AutoStop = autoStop;

            SentIndex = 0;
            SentFrom = subLegs[0].Start;
            SentAt = subLegs[0].T0;
            SentVelocity = subLegs[0].Velocity;
            SentOmega = OmegaFor(SentVelocity);
        }

        public IReadOnlyList<HazardLeg> SubLegs => subLegs;

        public int SubLegCount => subLegs.Length;

        public double T0 => subLegs[0].T0;

        public double EndTime => subLegs[subLegs.Length - 1].EndTime;

        public Vector3 Start => subLegs[0].Start;

        public Vector3 End => subLegs[subLegs.Length - 1].End;

        /// <summary>When true the driver snaps and stops the hazard at <see cref="EndTime"/> (StopLeg).</summary>
        public bool AutoStop { get; }

        public int SentIndex { get; private set; }
        public Vector3 SentFrom { get; private set; }
        public double SentAt { get; private set; }
        public Vector3 SentVelocity { get; private set; }
        public Vector3 SentOmega { get; private set; }

        /// <summary>HazardMoveOptions.OnLegEnd, carried with the leg so the driver can call it before the end snap.</summary>
        public Action<HazardHandle, HazardPath> OnEnd { get; set; }

        /// <summary>The sub-leg running at <paramref name="t"/> (the last one once the path is over).</summary>
        public int IndexAt(double t)
        {
            for (var i = 0; i < subLegs.Length; i++)
                if (t < subLegs[i].EndTime)
                    return i;

            return subLegs.Length - 1;
        }

        /// <summary>The ideal (computed) position at <paramref name="t"/>. Damage uses this, never Location.</summary>
        public Vector3 PositionAt(double t) => subLegs[IndexAt(t)].PositionAt(t);

        /// <summary>Where the client must think the hazard is at <paramref name="t"/>, from what was last sent.</summary>
        public Vector3 PredictedClientPosition(double t) => SentFrom + SentVelocity * (float)Math.Max(0.0, t - SentAt);

        /// <summary>
        /// Advances the path to <paramref name="now"/>. Returns NewVelocity at most once per sub-leg boundary
        /// (with <see cref="SentVelocity"/>/<see cref="SentOmega"/> updated), Finished once the path is over.
        /// </summary>
        public HazardMotionStep Advance(double now)
        {
            if (now >= EndTime)
                return HazardMotionStep.Finished;

            var idx = IndexAt(now);

            if (idx == SentIndex)
                return HazardMotionStep.None;

            var predicted = PredictedClientPosition(now);
            var target = subLegs[idx].End;
            var v = HazardMotion.CorrectiveVelocity(predicted, target, subLegs[idx].EndTime - now);

            SentIndex = idx;
            SentFrom = predicted;
            SentAt = now;
            SentVelocity = v;
            SentOmega = OmegaFor(v);

            return HazardMotionStep.NewVelocity;
        }

        private Vector3 OmegaFor(Vector3 velocity)
        {
            if (fixedOmega.HasValue)
                return fixedOmega.Value;

            return rollRadius > 0f ? HazardMotion.RollOmega(velocity, rollRadius) : Vector3.Zero;
        }
    }

    /// <summary>
    /// Kinematics (pure, tested) and the two network sequences a moving hazard uses (live).
    ///
    /// HOW A HAZARD MOVES. Hazards are Generic, non-Static, Ethereal|IgnoreCollisions|Missile, gravity off
    /// (<see cref="PrepareMovingBody"/>: Missile turns off the engine's step-down, which otherwise dropped a
    /// hovering ball onto a platform and let floor friction stop it). The server must not simulate them, and
    /// WorldObject.UpdateObjectPhysics simulates an ACTIVE missile on every tick (and destroys it after
    /// ProjectileTimeout), so a moving hazard is kept INACTIVE: <see cref="Park"/> at spawn, on every snap and on
    /// every driver tick, and every leg clears the Active flag (<see cref="SendVelocity"/>). UpdateObjectPhysics
    /// tests that flag first and returns at once. So the server Location stays where the hazard was last
    /// SNAPPED, and every client flies it by the velocity in a VectorUpdate. (Before Missile, the guard was the
    /// non-missile rule - simulated only while animating or while InitialUpdates &lt;= 1 - plus the same Active
    /// flag; HazardContext still defers a MoveLeg in the spawn tick.)
    /// The two network sequences:
    ///   MoveLeg  = set PhysicsObj.Velocity/Omega and broadcast GameMessageVectorUpdate (no position message: a
    ///              non-teleport position update would be dropped or fought by the client);
    ///   StopLeg  = zero Velocity/Omega, reposition the server object the way Creature.FakeTeleport does
    ///              (PhysicsObj.SetPosition with Teleport, SyncLocation, SendUpdatePosition(adminMove: true), which
    ///              advances the ObjectTeleport sequence so clients snap), then broadcast the zero VectorUpdate.
    /// Damage uses the COMPUTED leg position (<see cref="HazardPath.PositionAt"/>), never Location.
    /// Known gap (accepted risk): a player who creates the object mid-leg sees it from the last snap point.
    /// </summary>
    public static class HazardMotion
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly Vector3 Up = Vector3.UnitZ;

        // ---- pure ---------------------------------------------------------------------------------------------

        public static HazardLeg Leg(Vector3 start, Vector3 end, double t0, double duration) => new HazardLeg(start, end, t0, duration);

        /// <summary>A ball rolling along the ground: omega = cross(up, v) / r. Zero for r &lt;= 0.</summary>
        public static Vector3 RollOmega(Vector3 velocity, float radius)
        {
            if (!(radius > 0f))
                return Vector3.Zero;

            var flat = new Vector3(velocity.X, velocity.Y, 0f);
            return Vector3.Cross(Up, flat) / radius;
        }

        /// <summary>
        /// The velocity that takes a client from <paramref name="from"/> to <paramref name="target"/> in
        /// <paramref name="secondsLeft"/> (floored at 50 ms so a late boundary cannot produce a huge jump).
        /// </summary>
        public static Vector3 CorrectiveVelocity(Vector3 from, Vector3 target, double secondsLeft)
        {
            var s = double.IsFinite(secondsLeft) ? Math.Max(0.05, secondsLeft) : 0.05;
            return (target - from) / (float)s;
        }

        /// <summary>How many ~<paramref name="subLegMetres"/> sub-legs a leg needs on terrain: ceil(2D length / metres), 1..MaxSubLegs.</summary>
        public static int SubLegCount(Vector3 start, Vector3 end, double subLegMetres = HazardDefaults.SubLegMetres)
        {
            var len = new Vector2(end.X - start.X, end.Y - start.Y).Length();

            if (!(subLegMetres > 0.0) || !float.IsFinite(len))
                return 1;

            return Math.Clamp((int)Math.Ceiling(len / subLegMetres), 1, HazardDefaults.MaxSubLegs);
        }

        /// <summary>
        /// UNISON: the one sub-leg count every member of a group must share - the largest any member needs. Every
        /// member then changes velocity at the same instants and arrives at the same instant, whatever its own
        /// terrain.
        /// </summary>
        public static int UnisonSubLegCount(IEnumerable<(Vector3 Start, Vector3 End)> members, double subLegMetres = HazardDefaults.SubLegMetres)
        {
            var n = 1;

            if (members == null)
                return n;

            foreach (var m in members)
                n = Math.Max(n, SubLegCount(m.Start, m.End, subLegMetres));

            return n;
        }

        /// <summary>
        /// Splits one leg into <paramref name="subLegs"/> equal-time pieces. Intermediate points take their z from
        /// <paramref name="heightAt"/>(x, y) (ground + offset on a terrain region); the two ends keep their own z.
        /// With no sampler the pieces lie on the straight line.
        /// </summary>
        public static List<HazardLeg> Split(Vector3 start, Vector3 end, double t0, double duration, int subLegs, Func<float, float, float> heightAt)
        {
            var n = Math.Clamp(subLegs, 1, HazardDefaults.MaxSubLegs);
            var d = double.IsFinite(duration) && duration > 0.0 ? duration : 0.0;
            var legs = new List<HazardLeg>(n);

            var prev = start;

            for (var i = 1; i <= n; i++)
            {
                Vector3 p;

                if (i == n)
                {
                    p = end;
                }
                else
                {
                    var f = (float)i / n;
                    p = Vector3.Lerp(start, end, f);

                    if (heightAt != null)
                    {
                        var z = heightAt(p.X, p.Y);

                        if (float.IsFinite(z))
                            p.Z = z;
                    }
                }

                legs.Add(new HazardLeg(prev, p, t0 + d * (i - 1) / n, d / n));
                prev = p;
            }

            return legs;
        }

        // ---- live (landblock thread only) --------------------------------------------------------------------

        /// <summary>
        /// The physics a moving hazard body needs, set BEFORE EnterWorld (HazardContext.Spawn): non-Static, gravity
        /// off, Ethereal, IgnoreCollisions, and MISSILE. Returns true when it had to strip a Static flag.
        ///
        /// WHY MISSILE (2026-10-18, the stuck-ball bug). Every non-Missile object's placement and movement run the
        /// engine's STEP-DOWN (ObjectInfo.Init: StepDown = !Missile; Transition.FindPlacementPosition steps the body
        /// down onto a walkable floor within its step-down reach). A ball hovering ball_z over a platform had its
        /// sphere 0.45 m (ball_z 2) or 1.45 m (ball_z 3) above the platform top, so the placement dropped it onto the
        /// platform (Pinnacle east block: requested z -45.75 and -44.75 both spawned at -46.2 = top -48 + radius 1.8,
        /// `cells --spawncheck`), AddPhysicsObj synced that z into Location, and the body came to rest Contact +
        /// OnWalkable. calc_friction then bleeds the velocity of any OnWalkable mover away within a tick or two while
        /// the omega keeps turning it: the ball spins in place. Over the pit the floor is out of step-down reach, which
        /// is why only the lanes over a platform stuck. A Missile has no step-down at all, so it keeps the exact
        /// hover z it was given and is never OnWalkable. Server side, an ACTIVE missile is simulated every tick and
        /// destroyed after WorldObject.ProjectileTimeout, so every moving hazard is also kept inactive
        /// (<see cref="Park"/>: at spawn, at every leg start and snap, and every driver tick).
        /// </summary>
        internal static bool PrepareMovingBody(WorldObject wo)
        {
            if (wo == null)
                return false;

            // A Static object never moves client-side. Content for moving hazards drops Static; this is defence in
            // depth so a mis-authored copy still moves instead of silently standing still.
            var state = wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.PhysicsState);
            var stripped = false;

            if (state.HasValue && (state.Value & (int)ACE.Entity.Enum.PhysicsState.Static) != 0)
            {
                wo.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.PhysicsState, state.Value & ~(int)ACE.Entity.Enum.PhysicsState.Static);
                stripped = true;
            }

            // Missile lives ONLY on PhysicsObj.State (WorldObject.Missile has no PropertyBool behind it, and
            // CalculatedPhysicsState drops the bit while PhysicsObj is null). Landblock.AddWorldObjectInternal would
            // build the PhysicsObj lazily INSIDE EnterWorld, after this runs, so build it now - the same order
            // Creature.SetProjectilePhysicsState uses for arrows (InitPhysicsObj, then Missile, then EnterWorld).
            // Location and ObjScale are already set by HazardContext.Spawn, and InitPhysicsObj reads both.
            if (wo.PhysicsObj == null)
                wo.InitPhysicsObj();

            wo.GravityStatus = false;
            wo.Ethereal = true;
            wo.IgnoreCollisions = true;
            wo.Missile = true;

            return stripped;
        }

        /// <summary>
        /// Keeps the SERVER from flying (or timing out) a moving hazard: clears the physics Active flag, which
        /// WorldObject.UpdateObjectPhysics tests before anything else. Clients are unaffected. Safe on null.
        /// </summary>
        internal static void Park(WorldObject wo)
        {
            var physics = wo?.PhysicsObj;

            if (physics != null && physics.is_active())
                physics.set_active(false);
        }

        /// <summary>
        /// MoveLeg's network half: set the server PhysicsObj's velocity and omega (fields, deliberately not
        /// set_velocity, which would mark the object Active) and broadcast one VectorUpdate.
        /// </summary>
        internal static void SendVelocity(WorldObject wo, Vector3 velocity, Vector3 omega)
        {
            var physics = wo?.PhysicsObj;

            if (physics == null || wo.IsDestroyed)
                return;

            physics.Velocity = velocity;
            physics.Omega = omega;

            // Keep the SERVER from flying it: UpdateObjectPhysics returns at once for an inactive object, which
            // makes its conditional simulation (IsAnimating || InitialUpdates <= 1) unreachable. Clients are not
            // affected - they fly the hazard from the VectorUpdate. Same move as SpellProjectile.ProjectileImpact.
            physics.set_active(false);

            wo.EnqueueBroadcast(new GameMessageVectorUpdate(wo));
        }

        /// <summary>The SetPosition flags of a leg-end snap (Creature.FakeTeleport's). Shared with the dat-backed snap test.</summary>
        internal const Physics.Common.SetPositionFlags SnapFlags = Physics.Common.SetPositionFlags.SendPositionEvent
            | Physics.Common.SetPositionFlags.Slide | Physics.Common.SetPositionFlags.Placement | Physics.Common.SetPositionFlags.Teleport;

        /// <summary>
        /// StopLeg's network half: zero velocity/omega, reposition the server object to <paramref name="at"/> the way
        /// Creature.FakeTeleport does (PhysicsObj.SetPosition with Teleport|Placement|Slide|SendPositionEvent,
        /// SyncLocation, SendUpdatePosition(true) - a teleport-sequence update clients snap to), then broadcast the
        /// zero VectorUpdate. Never leaves the OBJECT's own landblock (<paramref name="at"/> is local to
        /// <paramref name="landblock"/>; a target cell on any other landblock is refused - HazardContext.SnapObject
        /// routes a spanning region's anchor-frame point here). Returns false (with the reason) when the point has
        /// no cell; the velocity is still zeroed and broadcast so the client stops. A null landblock never moves it.
        /// </summary>
        internal static bool Snap(WorldObject wo, Landblock landblock, uint instance, Vector3 at, out string error)
        {
            error = null;
            var physics = wo?.PhysicsObj;

            if (physics == null || wo.IsDestroyed || wo.Location == null)
            {
                error = "object is not in the world";
                return false;
            }

            physics.Velocity = Vector3.Zero;
            physics.Omega = Vector3.Zero;

            var moved = false;

            // Never raised: a moving body is never lifted into the player plane (HazardGround.TryResolveCell's raise rule).
            if (HazardGround.TryResolveCell(landblock, instance, at, false, out var cell, out var resolved))
            {
                var newPosition = new Position(cell, resolved, wo.Location.Rotation) { Instance = instance };

                if (wo.Location.InstancedLandblock != newPosition.InstancedLandblock)
                {
                    error = $"snap target {newPosition.ToLOCString()} is not on the hazard's landblock";
                }
                else
                {
                    var setPosition = new Physics.Common.SetPosition(instance);
                    setPosition.Pos = new Physics.Common.Position(newPosition);
                    setPosition.Flags = SnapFlags;

                    var result = physics.SetPosition(setPosition);

                    if (result != Physics.Common.SetPositionError.OK)
                        log.Debug($"[HAZARD] snap of 0x{wo.Guid.Full:X8} to {newPosition.ToLOCString()} returned {result}");

                    wo.SyncLocation();
                    wo.SendUpdatePosition(true);
                    moved = true;
                }
            }
            else
            {
                error = $"no cell holds ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})";
            }

            // SetPosition can re-arm the server physics; a moving hazard (a Missile) must stay inactive (Park).
            Park(wo);

            wo.EnqueueBroadcast(new GameMessageVectorUpdate(wo));
            return moved;
        }
    }
}

using System;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

namespace ACE.Server.WorldEvents
{
    /// <summary>What one downward sweep found. <see cref="Landed"/> false means no usable surface.</summary>
    public readonly struct GroundProbeResult
    {
        public bool Landed { get; }

        /// <summary>The sweep could not start (no cell, a failed transition, or no progress at all): the start is likely inside geometry.</summary>
        public bool StartBlocked { get; }

        /// <summary>Where the sphere came to rest: its BOTTOM point, i.e. the surface height under it.</summary>
        public Vector3 Rest { get; }

        /// <summary>The outdoor or building cell the sphere came to rest in.</summary>
        public uint Cell { get; }

        /// <summary>Z of the contact plane normal (1 = flat). 0 when there was no contact plane.</summary>
        public float NormalZ { get; }

        public GroundProbeResult(bool landed, bool startBlocked, Vector3 rest, uint cell, float normalZ)
        {
            Landed = landed;
            StartBlocked = startBlocked;
            Rest = rest;
            Cell = cell;
            NormalZ = normalZ;
        }

        public static GroundProbeResult Blocked => new GroundProbeResult(false, true, Vector3.Zero, 0, 0f);
    }

    /// <summary>
    /// A throwaway physics sphere swept straight DOWN through the server's own collision geometry (terrain,
    /// buildings, static objects) with PhysicsObj's Transition, to find the first surface under a point.
    /// Used to stand the presence-ring wisps on whatever is really there - a roof included - rather than on
    /// the bare terrain AdjustMapCoords reads.
    ///
    /// This is ONE Transition from (x, y, startZ) to (x, y, endZ), not a simulated fall: there is no
    /// gravity, velocity or tick. The same machinery a falling player's per-frame transition uses stops the
    /// sphere at the first surface it meets; PuzzleWalkProbe.Settle is the precedent (a single downward
    /// Step of MaxDrop). The sphere sits with its centre one radius above the object's origin, so the
    /// resting origin IS the surface height.
    ///
    /// The probe body is a null PhysicsObj (no Setup, no weenie) with a Gravity | ReportCollisions state,
    /// built per call and never entered into the world, so nothing ever collides with it and it needs no
    /// cleanup. THREADING: the cells' shadow-object lists belong to the landblock, so a sweep must run on
    /// the landblock's own action queue (or offline, in a dat test).
    /// </summary>
    public static class WorldEventGroundProbe
    {
        /// <summary>The probe sphere's radius at ringScale 1.</summary>
        public const float BaseSphereRadius = 0.48f;

        /// <summary>How far above the first start a blocked sweep is retried.</summary>
        public const float RetryLiftMetres = 40f;

        /// <summary>How far below the terrain the sweep aims, so it always reaches the ground.</summary>
        public const float BelowTerrainMetres = 2f;

        /// <summary>Progress below this counts as "never moved" (a start inside geometry).</summary>
        private const float MinProgress = 0.05f;

        /// <summary>
        /// One sweep at landblock-local (<paramref name="x"/>, <paramref name="y"/>) of the outdoor landblock
        /// whose id is in the high 16 bits of <paramref name="landblockId"/>, from <paramref name="startZ"/> down
        /// to <paramref name="endZ"/>. Never throws.
        /// </summary>
        public static GroundProbeResult Sweep(uint landblockId, uint instance, float x, float y, float startZ, float endZ, float radius)
        {
            try
            {
                if (x < 0f || y < 0f || x >= 192f || y >= 192f || !(startZ > endZ) || !(radius > 0f))
                    return GroundProbeResult.Blocked;

                var cellX = (int)(x / 24f);
                var cellY = (int)(y / 24f);
                var cellId = (landblockId & 0xFFFF0000) | (uint)(cellX * 8 + cellY + 1);

                var beginCell = LScape.get_landcell(cellId, instance);

                if (beginCell == null)
                    return GroundProbeResult.Blocked;

                var body = PhysicsObj.makeNullObject(0, true);
                body.State = PhysicsState.Gravity | PhysicsState.ReportCollisions;

                var trans = Transition.MakeTransition(instance);

                if (trans == null)
                    return GroundProbeResult.Blocked;

                trans.InitObject(body, ObjectInfoState.Default);
                trans.InitSphere(1, new Sphere(new Vector3(0f, 0f, radius), radius), 1.0f);

                var begin = new Position(cellId, new AFrame(new Vector3(x, y, startZ), Quaternion.Identity));
                var end = new Position(cellId, new AFrame(new Vector3(x, y, endZ), Quaternion.Identity));

                trans.InitPath(beginCell, begin, end);

                var ok = trans.FindValidPosition();
                trans.CleanupTransition();

                if (!ok || trans.SpherePath.CurCell == null)
                    return GroundProbeResult.Blocked;

                var cur = trans.SpherePath.CurPos;
                var rest = cur.Frame.Origin;
                var info = trans.CollisionInfo;

                if (startZ - rest.Z < MinProgress)
                    return GroundProbeResult.Blocked;

                var contact = info.ContactPlaneValid || info.CollidedWithEnvironment;

                // Reaching the end point untouched is not a landing: endZ is below the terrain, so a sweep that
                // got there passed through the ground (a hole in the terrain mesh, or a cell the sweep never
                // tested), and that height means nothing.
                if (!contact || rest.Z <= endZ + MinProgress)
                    return new GroundProbeResult(false, false, rest, cur.ObjCellID, 0f);

                var normalZ = info.ContactPlaneValid ? info.ContactPlane.Normal.Z : 0f;

                return new GroundProbeResult(true, false, rest, cur.ObjCellID, normalZ);
            }
            catch (Exception)
            {
                return GroundProbeResult.Blocked;
            }
        }

        /// <summary>
        /// The ring recipe: sweep from <paramref name="terrainZ"/> + <paramref name="probeHeight"/> to just
        /// below the terrain; when that start is blocked (inside geometry), retry once
        /// <see cref="RetryLiftMetres"/> higher. <paramref name="attempts"/> reports how many sweeps ran.
        /// </summary>
        public static GroundProbeResult ProbeDown(uint landblockId, uint instance, float x, float y, float terrainZ,
            float probeHeight, float radius, out int attempts)
        {
            attempts = 1;

            var endZ = terrainZ - BelowTerrainMetres;
            var result = Sweep(landblockId, instance, x, y, terrainZ + probeHeight, endZ, radius);

            if (result.Landed || !result.StartBlocked)
                return result;

            attempts = 2;

            return Sweep(landblockId, instance, x, y, terrainZ + probeHeight + RetryLiftMetres, endZ, radius);
        }
    }
}

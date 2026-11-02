using System;
using System.Numerics;

using ACE.Server.Entity;
using ACE.Server.Physics.Util;

using Position = ACE.Entity.Position;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Heights and cells. The top half is PURE (plane vs terrain z, the raise-up search) and is what the tests
    /// target; the bottom half resolves a landblock-local point to a cell of one landblock and samples terrain, and
    /// is the only part that touches a live Landblock. The HazardSpan overloads take an ANCHOR-frame point and route
    /// it to the landblock that owns it (the run's own landblock for every single-landblock region).
    /// </summary>
    public static class HazardGround
    {
        /// <summary>The raise-up search climbs in steps of this many metres...</summary>
        public const float RaiseStep = 0.25f;

        /// <summary>...at most this many times (PvpLiveAdapters.ResolveMarkerCell's numbers: 1.5 m in all).</summary>
        public const int RaiseSteps = 6;

        // ---- pure ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Ground z at (x, y). Plane regions return the plane whatever the sampler says; terrain regions return
        /// <paramref name="terrainZ"/>(x, y), or the plane when no sampler is given or it returns a non-finite value.
        /// </summary>
        public static float GroundZ(HazardRegion region, float x, float y, Func<float, float, float> terrainZ)
        {
            if (region == null)
                return 0f;

            if (region.GroundMode == HazardGroundMode.Plane || terrainZ == null)
                return region.GroundPlaneZ;

            var z = terrainZ(x, y);
            return float.IsFinite(z) ? z : region.GroundPlaneZ;
        }

        /// <summary>Where a moving hazard flies at (x, y): the region's hazard plane, else ground + offset.</summary>
        public static float HazardZ(HazardRegion region, float x, float y, Func<float, float, float> terrainZ)
        {
            if (region == null)
                return 0f;

            if (region.HazardPlaneZ.HasValue)
                return region.HazardPlaneZ.Value;

            return GroundZ(region, x, y, terrainZ) + region.HazardOffset;
        }

        /// <summary>
        /// THE HOVER RULE (owner ruling 2026-10-07, after gate 2 showed client AND server colliding moving ethereal
        /// hazards with environment geometry - nothing travels through a solid): a <see cref="HazardFollow.Ground"/>
        /// body sits at GroundZ(x, y) + groundOffset in EVERY ground mode (plane and terrain), at its spawn point,
        /// every leg end and every sub-leg end, and its markers use the same z. A ball HOVERS: the offset
        /// (<see cref="HazardDefaults.BallHover"/>, arg "ball_z") lifts its collision sphere clear of every solid
        /// top so it can roll over blocks and bridges; its job is to hit players, not to touch the floor.
        /// <see cref="HazardFollow.HazardHeight"/> is HazardZ(x, y); <see cref="HazardFollow.Straight"/> keeps
        /// <paramref name="callerZ"/>.
        /// </summary>
        public static float FollowZ(HazardRegion region, HazardFollow follow, float groundOffset, float x, float y, float callerZ, Func<float, float, float> terrainZ)
        {
            switch (follow)
            {
                case HazardFollow.Ground:
                    return GroundZ(region, x, y, terrainZ) + (float.IsFinite(groundOffset) ? groundOffset : 0f);

                case HazardFollow.HazardHeight:
                    return HazardZ(region, x, y, terrainZ);

                default:
                    return callerZ;
            }
        }

        /// <summary><paramref name="p"/> with its z from <see cref="FollowZ"/> - the spawn/marker point of a following body.</summary>
        public static Vector3 FollowPoint(HazardRegion region, HazardFollow follow, float groundOffset, Vector3 p, Func<float, float, float> terrainZ)
            => new Vector3(p.X, p.Y, FollowZ(region, follow, groundOffset, p.X, p.Y, p.Z, terrainZ));

        /// <summary>
        /// The z sampler a leg's sub-leg ends use: null for Straight, else <see cref="FollowZ"/>. Non-null on a
        /// plane region too (the hover rule); whether a leg is SPLIT is <see cref="SplitsLegs"/>.
        /// </summary>
        public static Func<float, float, float> HeightAt(HazardRegion region, HazardFollow follow, float groundOffset, Func<float, float, float> terrainZ)
        {
            if (region == null || follow == HazardFollow.Straight)
                return null;

            return (x, y) => FollowZ(region, follow, groundOffset, x, y, float.NaN, terrainZ);
        }

        /// <summary>
        /// Whether a following leg is split into ~4 m sub-legs: only where its height can CHANGE along the way - a
        /// terrain region, and not a HazardHeight leg under a fixed hazard plane. A plane leg is one straight piece.
        /// </summary>
        public static bool SplitsLegs(HazardRegion region, HazardFollow follow)
            => region != null && region.GroundMode == HazardGroundMode.Terrain && follow != HazardFollow.Straight
               && !(follow == HazardFollow.HazardHeight && region.HazardPlaneZ.HasValue);

        /// <summary>Where a wisp may stand and where its pillar fires, for one desired point. See <see cref="PlaceWisp"/>.</summary>
        public readonly struct WispPlacement
        {
            public WispPlacement(Vector3 stand, Vector3 pillar, bool blocked, bool pillarFound)
            {
                Stand = stand;
                Pillar = pillar;
                Blocked = blocked;
                PillarFound = pillarFound;
            }

            /// <summary>Where the wisp body goes: the desired point, or the last open point before a solid on its path.</summary>
            public Vector3 Stand { get; }

            /// <summary>Where its pillar fires: the TRUE desired (x, y), at the desired z or raised onto the solid's top.</summary>
            public Vector3 Pillar { get; }

            /// <summary>A solid lies on the straight path from the current point to the desired one.</summary>
            public bool Blocked { get; }

            /// <summary>False when no open z was found above the desired point within maxRise (Pillar is then the desired point).</summary>
            public bool PillarFound { get; }
        }

        /// <summary>
        /// THE WISP RULE (owner ruling 2026-10-07): WISPS NEVER ENTER SOLIDS. A wisp only travels through open space
        /// at the hazard plane. Walking the straight path from <paramref name="current"/> (open) to
        /// <paramref name="desired"/> in <paramref name="step"/> pieces, the wisp STANDS at the last open sample
        /// before the first solid one, backed off <paramref name="clearance"/> towards <paramref name="current"/>
        /// (never past it) - or at <paramref name="desired"/> when the whole path is open. Its PILLAR is cast at the
        /// true desired (x, y), NO SAFE SPOTS: at the desired z when that is open, else raised in
        /// <see cref="RaiseStep"/> steps up to <paramref name="maxRise"/> metres onto the first open z - the
        /// solid's top. <paramref name="hasCell"/> is the moving-body test (live: HazardContext.HasCell with
        /// raise false). PURE. The wisps mechanic uses this for every formation point; the movement gate's case 4
        /// shows it live.
        /// </summary>
        public static WispPlacement PlaceWisp(Vector3 current, Vector3 desired, Func<Vector3, bool> hasCell,
            float clearance = 0.5f, float step = 0.25f, float maxRise = 8f)
        {
            if (hasCell == null || !(step > 0f))
                return new WispPlacement(current, desired, false, false);

            var path = desired - current;
            var length = path.Length();
            var samples = length > 0f ? (int)Math.Ceiling(length / step) : 0;

            var stand = desired;
            var blocked = false;

            for (var i = 0; i <= samples; i++)
            {
                var d = Math.Min(length, i * step);
                var p = length > 0f ? current + path * (d / length) : current;

                if (hasCell(p))
                    continue;

                blocked = true;

                var lastOpen = i == 0 ? 0f : Math.Min(length, (i - 1) * step);
                var back = Math.Max(0f, lastOpen - Math.Max(0f, clearance));

                stand = length > 0f ? current + path * (back / length) : current;
                break;
            }

            if (hasCell(desired))
                return new WispPlacement(stand, desired, blocked, true);

            var rises = (int)Math.Floor(Math.Max(0f, maxRise) / RaiseStep + 1e-4);

            for (var i = 1; i <= rises; i++)
            {
                var p = new Vector3(desired.X, desired.Y, desired.Z + i * RaiseStep);

                if (hasCell(p))
                    return new WispPlacement(stand, p, blocked, true);
            }

            return new WispPlacement(stand, desired, blocked, false);
        }

        /// <summary>
        /// The first cell that holds <paramref name="point"/> raised 0, 1, ... <see cref="RaiseSteps"/> times by
        /// <see cref="RaiseStep"/>, with the z it was found at. A point sunk into a floor or inside a solid is in
        /// NO cell at its own z (a cell's volume starts at its floor); raising it a little finds the cell above.
        /// False when no step finds one. <paramref name="lookup"/> is AdjustCell.GetCell or the outdoor probe.
        /// </summary>
        public static bool TryRaiseToCell(Func<Vector3, uint?> lookup, Vector3 point, out uint cell, out Vector3 resolved)
            => TryRaiseToCell(lookup, point, RaiseSteps, out cell, out resolved);

        /// <summary>As above with an explicit step count; 0 = the point itself only (no raise).</summary>
        public static bool TryRaiseToCell(Func<Vector3, uint?> lookup, Vector3 point, int steps, out uint cell, out Vector3 resolved)
        {
            cell = 0;
            resolved = point;

            if (lookup == null || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                return false;

            for (var i = 0; i <= Math.Max(0, steps); i++)
            {
                var p = new Vector3(point.X, point.Y, point.Z + i * RaiseStep);
                var c = lookup(p);

                if (c != null && c.Value != 0)
                {
                    cell = c.Value;
                    resolved = p;
                    return true;
                }
            }

            return false;
        }

        /// <summary>A lift below this many metres is float noise from a rotation that keeps z, not a real turn.</summary>
        public const float TurnedHostLiftEpsilon = 1e-4f;

        /// <summary>
        /// THE TURNED-HOST LIFT (2026-10-25, Speed Season 3 rain abort). A host whose origin stands ON THE FLOOR and is
        /// turned 180 degrees about x (MemoryLayout.BeamOrientation(true), so a hanging beam rises) carries its collision
        /// spheres with it: a sphere centre c above the origin lands at -c.z, BELOW the floor, and the server's spawn test
        /// (PhysicsObj.AdjustPosition on the first sphere's centre, then the placement insert on the first two) refuses it.
        /// Measured with `cells --spawncheck`: Effect Host Setup 0x02000176 at floor z refuses at rot (0,1,0,0) and
        /// spawns at identity. This returns how far to raise the origin so every turned centre sits at least where it
        /// would sit unturned: max over the centres of (c.z - (orientation * c).z), never negative. For the 180-degree
        /// turn about x that is 2 x the highest centre's z. 0 for identity (the unturned path is untouched), for no
        /// centres, and for rotations that keep z (a yaw), below <see cref="TurnedHostLiftEpsilon"/>.
        /// <paramref name="sphereCentres"/> are already scaled (<see cref="SphereCentres"/>). PURE.
        /// </summary>
        public static float TurnedHostLift(System.Collections.Generic.IReadOnlyList<Vector3> sphereCentres, Quaternion orientation)
        {
            if (sphereCentres == null || sphereCentres.Count == 0 || orientation == Quaternion.Identity)
                return 0f;

            var lift = 0f;

            foreach (var c in sphereCentres)
            {
                var d = c.Z - Vector3.Transform(c, orientation).Z;

                if (float.IsFinite(d) && d > lift)
                    lift = d;
            }

            return lift < TurnedHostLiftEpsilon ? 0f : lift;
        }

        /// <summary>
        /// Where a host's origin goes once the turned-host lift is applied: <paramref name="resolved"/> raised by
        /// <see cref="TurnedHostLift"/> for a GROUNDED, NON-MOVING host whose <paramref name="rotation"/> is not identity;
        /// <paramref name="resolved"/> itself otherwise. <paramref name="centres"/> is only invoked when a lift is possible,
        /// so an unturned, ceiling or moving spawn never pays the Setup lookup. HazardContext.Spawn's single use. PURE.
        /// </summary>
        public static Vector3 LiftedOrigin(bool grounded, bool moving, Func<System.Collections.Generic.IReadOnlyList<Vector3>> centres, Quaternion rotation, Vector3 resolved)
        {
            if (!grounded || moving || centres == null || rotation == Quaternion.Identity)
                return resolved;

            var lift = TurnedHostLift(centres(), rotation);

            return lift > 0f ? new Vector3(resolved.X, resolved.Y, resolved.Z + lift) : resolved;
        }

        /// <summary>The server's placement test sees only this many of a Setup's spheres (SpherePath.InitSphere clamps to 2).</summary>
        public const int PlacementSpheres = 2;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(HazardGround));

        /// <summary>Successful Setup reads only (null value = the Setup declares no spheres: the DummySphere case).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Vector3[]> setupCentres
            = new System.Collections.Concurrent.ConcurrentDictionary<uint, Vector3[]>();

        /// <summary>Setup ids whose read failed and has been logged; a failure is never cached, only its log line.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> setupFailuresLogged
            = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>();

        /// <summary>
        /// The collision-sphere centres the server's spawn test uses for a body on Setup <paramref name="setupId"/> at
        /// <paramref name="scale"/>, the way PhysicsObj.SetPosition builds them: the Setup's first
        /// <see cref="PlacementSpheres"/> spheres (the server's own Setup reader, WorldObject.TryGetSetup behind
        /// DBObj.GetSetup) scaled by the object's scale, or PhysicsGlobals.DummySphere at scale 1 when the Setup declares
        /// none. A successful read is cached per Setup id. An unreadable or invalid Setup gives an empty list (no lift),
        /// is NOT cached (a dat that loads later is read then), and is logged once per id.
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<Vector3> SphereCentres(uint setupId, float scale)
        {
            if (!setupCentres.TryGetValue(setupId, out var raw))
            {
                string failure = null;

                try
                {
                    // no portal dat: GetSetupModel would hand back its neutral fallback, which must not be cached as real
                    if (ACE.DatLoader.DatManager.PortalDat == null)
                        failure = "cannot be read (no portal dat loaded)";
                    else if (!ACE.Server.WorldObjects.WorldObject.TryGetSetup(setupId, out var model) || model == null)
                        failure = "not a readable Setup";
                    else if (model.Spheres == null || model.Spheres.Count == 0)
                        raw = null; // PhysicsObj.SetPosition's DummySphere case, which is never scaled
                    else
                    {
                        var n = Math.Min(PlacementSpheres, model.Spheres.Count);
                        raw = new Vector3[n];

                        for (var i = 0; i < n; i++)
                            raw[i] = model.Spheres[i].Origin;
                    }
                }
                catch (Exception ex)
                {
                    failure = "read threw " + ex.GetType().Name + ": " + ex.Message;
                }

                if (failure != null)
                {
                    if (setupFailuresLogged.TryAdd(setupId, 0))
                        log.Warn($"[HAZARD] turned-host lift: Setup 0x{setupId:X8} {failure}; no lift for it (logged once, not cached)");

                    return Array.Empty<Vector3>();
                }

                setupCentres[setupId] = raw;
            }

            if (raw == null)
                return new[] { ACE.Server.Physics.PhysicsGlobals.DummySphere.Center };

            if (!(float.IsFinite(scale) && scale > 0f))
                scale = 1f;

            var scaled = new Vector3[raw.Length];

            for (var i = 0; i < raw.Length; i++)
                scaled[i] = raw[i] * scale;

            return scaled;
        }

        // ---- live (landblock thread) ------------------------------------------------------------------------

        /// <summary>
        /// A terrain sampler for an outdoor landblock: (x, y) -> terrain z. Null for a dungeon (no terrain).
        /// </summary>
        public static Func<float, float, float> TerrainSampler(Landblock landblock)
        {
            if (landblock == null || landblock.IsDungeon || landblock.PhysicsLandblock == null)
                return null;

            var physics = landblock.PhysicsLandblock;
            return (x, y) => physics.GetZ(new Vector3(x, y, 0f));
        }

        /// <summary>
        /// Resolves a landblock-local point to a cell of THIS landblock (never a neighbour), raising it when it is
        /// inside a solid. Dungeons use AdjustCell (the lookup PuzzleGateManager.TryResolve and
        /// PvpLiveAdapters.PlaceZoneMarker use); outdoors the point must lie inside the 192 m block and
        /// Position.GetCell picks a building or land cell. <paramref name="resolved"/> carries the raised z.
        ///
        /// THE RAISE RULE (recon 2026-10-06, Pinnacle 0x0069: three solids with tops at -48.00 over a pit floor at
        /// -54.00; a point INSIDE a block at -48.5 or -51 is in no EnvCell and will not spawn, raised to
        /// -48.25..-47.5 it spawns in the block-top cell snapped to -47.995):
        ///   - <paramref name="raise"/> TRUE for STATIONARY things whose origin must be on whatever is solid below -
        ///     pillar and beam effect hosts. No safe spots: a pillar over a block MUST still fire, from its top.
        ///   - <paramref name="raise"/> FALSE for a MOVING hazard's body (wisp, ball): its spawn and its leg-end snap
        ///     never lift it into the player plane. A moving spawn with no cell is refused (the mechanic picks
        ///     another point - <see cref="HazardContext.HasCell"/>); a leg-end snap with no cell keeps the server
        ///     object at its last valid point. Its computed position (what damage uses) is the leg end either way.
        ///   - GATE 2 (2026-10-07, live): a moving ethereal hazard does NOT pass through environment geometry - the
        ///     client stops it at the solid and the server's placement slides the leg-end snap off it. So a moving
        ///     body must never be routed into a solid at all: balls hover clear of solid tops (<see cref="FollowZ"/>),
        ///     wisps stop at the solid's edge and fire their pillar on its top (<see cref="PlaceWisp"/>).
        /// </summary>
        public static bool TryResolveCell(Landblock landblock, uint instance, Vector3 point, bool raise, out uint cell, out Vector3 resolved)
        {
            cell = 0;
            resolved = point;

            if (landblock == null)
                return false;

            var lbid = landblock.Id.Landblock;

            if (landblock.IsDungeon)
            {
                var adjust = AdjustCell.Get(lbid, instance);
                return TryRaiseToCell(v => adjust.GetCell(v), point, raise ? RaiseSteps : 0, out cell, out resolved);
            }

            if (point.X < 0 || point.Y < 0 || point.X >= Position.BlockLength || point.Y >= Position.BlockLength)
                return false;

            return TryRaiseToCell(v =>
            {
                var probe = new Position(SkyDecorLayout.CellId(lbid, v.X, v.Y), v.X, v.Y, v.Z, 0f, 0f, 0f, 1f, instance);
                var c = probe.GetCell();
                return c == 0 ? (uint?)null : c;
            }, point, raise ? RaiseSteps : 0, out cell, out resolved);
        }

        // ---- live, through a span (landblock thread) -----------------------------------------------------------

        /// <summary>
        /// The terrain sampler for a run's region, in ANCHOR-frame (x, y). The identity span (a single-landblock
        /// region) is exactly <see cref="TerrainSampler(Landblock)"/> of the run's landblock. A spanning region asks
        /// the landblock that owns (x, y) (<see cref="HazardSpan.TryLocate"/>) at its local position; a point on no
        /// usable landblock samples NaN, which GroundZ turns into the region's plane z.
        /// </summary>
        internal static Func<float, float, float> TerrainSampler(HazardSpan span)
        {
            if (span == null)
                return null;

            if (!span.Spans)
                return TerrainSampler(span.Anchor);

            return (x, y) =>
            {
                if (!span.TryLocate(new Vector3(x, y, 0f), out var owner, out var local))
                    return float.NaN;

                // PhysicsLandblock first: it is null on a landblock that never initialised, and IsDungeon reads its dat.
                var physics = owner.PhysicsLandblock;

                if (physics == null || owner.IsDungeon)
                    return float.NaN;

                return physics.GetZ(new Vector3(local.X, local.Y, 0f));
            };
        }

        /// <summary>
        /// <see cref="TryResolveCell(Landblock, uint, Vector3, bool, out uint, out Vector3)"/> for an ANCHOR-frame
        /// point: the identity span resolves on the run's landblock exactly as before (<paramref name="owner"/> = the
        /// run's landblock, <paramref name="resolvedLocal"/> = <paramref name="resolvedAnchor"/>). A spanning region
        /// resolves on the landblock that owns the point, at its local position; false when that landblock is not
        /// usable this tick or no cell holds the point. <paramref name="resolvedLocal"/> is for the object's Position
        /// (with <paramref name="cell"/>); <paramref name="resolvedAnchor"/> is the same point in the anchor frame (what
        /// a mechanic's handle keeps).
        /// </summary>
        internal static bool TryResolveCell(HazardSpan span, Vector3 anchorPoint, bool raise,
            out Landblock owner, out uint cell, out Vector3 resolvedLocal, out Vector3 resolvedAnchor)
        {
            cell = 0;
            resolvedLocal = anchorPoint;
            resolvedAnchor = anchorPoint;

            if (span == null || !span.TryLocate(anchorPoint, out owner, out var local))
            {
                owner = null;
                return false;
            }

            if (!TryResolveCell(owner, span.Instance, local, raise, out cell, out resolvedLocal))
                return false;

            // the raise only changes z; the local -> anchor offset is the one TryLocate applied (none for the identity)
            resolvedAnchor = span.Spans ? new Vector3(anchorPoint.X, anchorPoint.Y, resolvedLocal.Z) : resolvedLocal;
            return true;
        }
    }
}

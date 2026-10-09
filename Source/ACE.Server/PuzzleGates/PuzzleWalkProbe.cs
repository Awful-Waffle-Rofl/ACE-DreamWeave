using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// puzzlegate-sweep's check c: walks a player-sized body through the server's own collision geometry with the
    /// physics Transition a moving player or monster gets (PhysicsObj.transition), without a player, a session or a
    /// world object. The body is a bare PhysicsObj built from the human Setup (<see cref="BodySetup"/>: its spheres and
    /// step-up / step-down heights), the same way WorldObject.IsDirectVisible builds its sight object; it never enters
    /// the world, so nothing else ever collides with it.
    ///
    /// Each step is one Transition from the body's last position with the last contact plane (so step-up, step-down,
    /// edge and wall slides behave as for a walking body); a step that ends without contact is followed by a drop
    /// (gravity) to the floor below. Objects in the cells collide as they would with a player: a spawned gate blocks,
    /// an ethereal lever does not.
    ///
    /// THREADING: the cells' shadow-object lists belong to the landblock, so a probe must run on that landblock's
    /// thread (puzzlegate-sweep runs each leg as its own landblock action), or offline with no landblock tick at all (tests).
    /// </summary>
    public sealed class PuzzleWalkProbe : IDisposable
    {
        /// <summary>Human male Setup: the body every player character collides with.</summary>
        public const uint BodySetup = 0x02000001;

        /// <summary>Horizontal length of one walking step (a few frames of a running player).</summary>
        public const float SegmentLength = 1.0f;

        /// <summary>How far below a point the floor may be (a drop, or settling onto a start point).</summary>
        public const float MaxDrop = 12.0f;

        /// <summary>A step that gains less than this toward the goal is a stall; <see cref="MaxStalls"/> in a row is blocked.</summary>
        public const float MinProgress = 0.05f;

        public const int MaxStalls = 3;

        /// <summary>A routed waypoint (a doorway centroid) counts as passed within this horizontal distance.</summary>
        public const float WaypointReach = 0.75f;

        private readonly PhysicsObj body;
        private readonly uint instance;

        /// <summary>The sight object WorldObject.IsDirectVisible casts (Setup 0x02000124, an arrow, as a Missile).</summary>
        public const uint SightSetup = 0x02000124;

        private readonly PhysicsObj sight;

        /// <summary>The body's state between steps.</summary>
        public readonly record struct BodyState(uint Cell, Vector3 Pos, bool Contact, bool OnWalkable, Plane ContactPlane, uint ContactPlaneCell);

        /// <summary>
        /// Objects the body walks through after they stopped a step (null = everything solid). puzzlegate-sweep passes
        /// <see cref="IsPassableDoor"/>: the retail doors a Thread run unlocks (ThreadDungeonSpawner.UnlockDoors) and its
        /// players open, never a puzzle gate part.
        /// </summary>
        private readonly Func<PhysicsObj, bool> passable;

        public PuzzleWalkProbe(uint instance, Func<PhysicsObj, bool> passable = null)
        {
            this.instance = instance;
            this.passable = passable;
            body = PhysicsObj.makeObject(BodySetup, 0, true);
            body.KnownInstance = instance;

            // A player's physics state (EdgeSlide | Gravity | ReportCollisions): the human weenie's PhysicsState, so
            // the Transition sees the same ObjectInfo flags a walking player's PhysicsObj.get_object_info builds.
            if (body != null)
                body.State = PlayerBodyState;

            sight = PhysicsObj.makeObject(SightSetup, 0, false, true);

            if (sight != null)
            {
                sight.KnownInstance = instance;
                sight.State |= PhysicsState.Missile;
            }
        }

        /// <summary>The physics state of a player body: weenie 1 (human) PhysicsState 4195336 = EdgeSlide | Gravity | ReportCollisions.</summary>
        public const PhysicsState PlayerBodyState = PhysicsState.EdgeSlide | PhysicsState.Gravity | PhysicsState.ReportCollisions;

        /// <summary>The bare body. Test seam: a dat test runs PhysicsObj.transition on it to compare against <see cref="Step"/>.</summary>
        internal PhysicsObj Body => body;

        /// <summary>Test seam: every door a successful retry was asked about, needed or not (what DoorsPassed used to report).</summary>
        internal readonly List<string> ConsultedDoors = new List<string>();

        public bool Ready => body?.PartArray != null && body.PartArray.GetNumSphere() > 0;

        public float StepUpHeight => body?.GetStepUpHeight() ?? 0f;

        public void Dispose()
        {
            try
            {
                body?.DestroyObject();
                sight?.DestroyObject();
            }
            catch
            {
                // bare objects that never entered the world: nothing to undo
            }
        }

        /// <summary>
        /// One leg: from <paramref name="startCell"/>/<paramref name="start"/> (settled onto the floor first) toward
        /// <paramref name="target"/>, straight, then through <paramref name="route"/> if the straight walk is blocked.
        /// <paramref name="allowedCell"/> false for a cell = the body passed the gate (null = no restriction).
        /// </summary>
        public ProbeLeg Walk(string label, string direction, uint startCell, Vector3 start, Vector3 target, IReadOnlyList<Vector3> route, Func<uint, bool> allowedCell)
        {
            if (!Ready)
                return ProbeLeg.Unchecked(label, direction, $"no body (Setup 0x{BodySetup:X8} did not load)");

            var settled = Settle(startCell, start);

            if (settled == null)
                return ProbeLeg.Blocked(label, direction, start, $"no floor within {MaxDrop:0} m under the start point");

            var straight = WalkThrough(settled.Value, Array.Empty<Vector3>(), target, allowedCell);

            if (straight.Outcome == ProbeOutcome.Pass || route == null || route.Count == 0)
                return straight with { Target = label, Direction = direction, Path = "straight" };

            var routed = WalkThrough(settled.Value, route, target, allowedCell);
            return routed with { Target = label, Direction = direction, Path = $"via {route.Count} doorway(s)", Reason = routed.Outcome == ProbeOutcome.Pass ? null : $"{routed.Reason} (straight: {straight.Reason})" };
        }

        /// <summary>
        /// True for a door a player can open in a Thread copy: a Door that is not a puzzle gate part (neither marked
        /// WorldObject.IsPuzzleGateObject nor one of <paramref name="gateParts"/>, the placement's own gate guids).
        /// </summary>
        public static bool IsPassableDoor(PhysicsObj obj, ICollection<uint> gateParts)
            => obj?.WeenieObj?.WorldObject is ACE.Server.WorldObjects.Door door && !door.IsPuzzleGateObject && (gateParts == null || !gateParts.Contains(obj.ID));

        // ---- what a player needs: standing room near a point, and arrival within use range ----------------------

        /// <summary>The return leg's arrival: back within this of the start point the out leg used.</summary>
        public const float StandRadius = 1.0f;

        /// <summary>How far from a curated point (an anchor, a target) the probe looks for standing room: a player approaching a lever row stands anywhere nearby.</summary>
        public const float StartRadius = 1.5f;

        /// <summary>The start search's ring spacing and angular step.</summary>
        public const float StartRingStep = 0.25f;

        public const int StartAngleStepDeg = 10;

        /// <summary>
        /// A standing point found by <see cref="FindStart"/> may sit at most the body's step-up height plus this far above
        /// or below the point it serves (<see cref="MaxStartRise"/>): a start a player cannot step back up to (or down
        /// from) is not where they stand to work that point.
        /// </summary>
        public const float StartRiseMargin = 0.1f;

        /// <summary>The farthest a start may sit BELOW the point it serves: step-up height + <see cref="StartRiseMargin"/>.</summary>
        public float MaxStartRise => StepUpHeight + StartRiseMargin;

        /// <summary>
        /// A start <paramref name="rise"/> above the point it serves (negative = below) is usable: at most the step-up
        /// height above (no margin upward: a player standing that high could not step back down onto the point and up
        /// again), and at most step-up + <see cref="StartRiseMargin"/> below. Pure.
        /// </summary>
        public static bool StartRiseOk(float rise, float stepUp) => rise <= stepUp && -rise <= stepUp + StartRiseMargin;

        /// <summary>The FindStart filter that takes no cell outside the region (the region pass alone runs).</summary>
        public static readonly Func<uint, bool> NeverAccept = _ => false;

        /// <summary>Did the body arrive? Reached, plus what was measured (said in the detail).</summary>
        public readonly record struct ArrivalCheck(bool Reached, string Measure);

        /// <summary>The arrival rule of a leg's final goal, asked after every step.</summary>
        public delegate ArrivalCheck ArrivalRule(BodyState at);

        public float BodyRadius => body?.GetRadius() ?? 0f;

        public float BodyHeight => body?.GetHeight() ?? 0f;

        /// <summary>
        /// Candidate standing points around <paramref name="point"/>, strictly nearest first: the point itself, then rings
        /// every <see cref="StartRingStep"/> out to <see cref="StartRadius"/>, each every <see cref="StartAngleStepDeg"/>
        /// degrees. <paramref name="approach"/> (horizontal, toward where a player comes from) only orders the points
        /// WITHIN a ring (nearest that direction first); it never puts a farther point ahead of a nearer one. Which
        /// candidates count first is FindStart's job (its region cell). Pure.
        /// </summary>
        public static IEnumerable<Vector3> StartCandidates(Vector3 point, Vector3? approach)
        {
            yield return point;

            var ringCount = (int)Math.Round(StartRadius / StartRingStep);
            var radii = Enumerable.Range(1, ringCount).Select(i => i * StartRingStep).ToArray();
            var axis = approach.HasValue && Horizontal(approach.Value) > 1e-4f
                ? Vector3.Normalize(new Vector3(approach.Value.X, approach.Value.Y, 0f))
                : new Vector3(1f, 0f, 0f);

            var near = new List<int> { 0 };
            for (var a = StartAngleStepDeg; a <= 90; a += StartAngleStepDeg) { near.Add(a); near.Add(-a); }
            var far = new List<int>();
            for (var a = 90 + StartAngleStepDeg; a < 180; a += StartAngleStepDeg) { far.Add(a); far.Add(-a); }
            far.Add(180);

            IEnumerable<Vector3> Ring(float r, IEnumerable<int> angles)
            {
                foreach (var a in angles)
                {
                    var rad = a * MathF.PI / 180f;
                    var dir = new Vector3(axis.X * MathF.Cos(rad) - axis.Y * MathF.Sin(rad), axis.X * MathF.Sin(rad) + axis.Y * MathF.Cos(rad), 0f);
                    yield return point + dir * r;
                }
            }

            foreach (var r in radii)
                foreach (var p in Ring(r, near.Concat(far)))
                    yield return p;
        }

        /// <summary>
        /// The cell that stands for the anchor's region: the cell holding the anchor itself; when the anchor is in no cell
        /// (inside a wall), the cell of the nearest start candidate on its APPROACH side (strictly ahead of the anchor
        /// along <paramref name="approach"/>, the side away from the gate). 0 = neither. <paramref name="how"/> says
        /// which, for the row's detail.
        /// </summary>
        public uint AnchorRegion(uint cell, Vector3 point, Vector3? approach, out Vector3 regionPoint, out string how)
        {
            regionPoint = point;
            var own = ResolveCell(cell, point);

            if (own != 0)
            {
                how = null;
                return own;
            }

            var axis = approach.HasValue && Horizontal(approach.Value) > 1e-4f ? Vector3.Normalize(new Vector3(approach.Value.X, approach.Value.Y, 0f)) : (Vector3?)null;

            if (axis == null)
            {
                how = "the anchor is in no cell and has no approach direction";
                return 0;
            }

            foreach (var candidate in StartCandidates(point, approach))
            {
                if (Vector3.Dot(candidate - point, axis.Value) <= 1e-3f)
                    continue;

                var c = ResolveCell(cell, candidate);

                if (c == 0)
                    continue;

                regionPoint = candidate;
                how = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "anchor in no cell; region = 0x{0:X8}, the cell of the nearest approach-side point ({1:0.00} m from the anchor)", c, Horizontal(candidate - point));
                return c;
            }

            how = string.Format(System.Globalization.CultureInfo.InvariantCulture, "the anchor is in no cell and no approach-side point within {0:0.0} m is in one", StartRadius);
            return 0;
        }

        /// <summary>
        /// The probe's start for a site anchor, in order: (1) the anchor stands in a cell: that is the region, and the
        /// start is the nearest standing point in it, else in a cell <paramref name="reachFor"/> proves reachable from
        /// it; (2) the anchor is in no cell (inside a wall): the site's DECLARED anchor cell is the region when a
        /// candidate stands in it (nearest first); (3) else the cell of the nearest approach-side candidate
        /// (<see cref="AnchorRegion"/>), with the same reachability rule as (1); (4) else none. <paramref name="reachFor"/>
        /// (region cell, region point) gives the cells a start outside the region may use; null from it = no proof.
        /// <paramref name="region"/> is 0 when no region was found; <paramref name="how"/> says which rule chose it.
        /// </summary>
        public BodyState? FindAnchorStart(uint declaredCell, Vector3 point, Vector3? approach, Func<uint, Vector3, Func<uint, bool>> reachFor,
            out uint region, out Func<uint, bool> reach, out string how, out float offset)
        {
            offset = 0f;
            reach = null;
            var own = ResolveCell(declaredCell, point);

            if (own != 0)
            {
                region = own;
                how = null;
                reach = reachFor?.Invoke(own, point);
                return FindStart(declaredCell, point, approach, own, reach, out offset);
            }

            if ((declaredCell & 0xFFFF) >= 0x100)
            {
                var inDeclared = FindStart(declaredCell, point, approach, declaredCell, NeverAccept, out offset);

                if (inDeclared != null)
                {
                    region = declaredCell;
                    how = string.Format(System.Globalization.CultureInfo.InvariantCulture, "anchor in no cell; region = its declared cell 0x{0:X8}", declaredCell);
                    return inDeclared;
                }
            }

            region = AnchorRegion(declaredCell, point, approach, out var regionPoint, out how);

            if (region == 0)
            {
                how = string.Format(System.Globalization.CultureInfo.InvariantCulture, "the anchor is in no cell, no standing room within {0:0.0} m in its declared cell 0x{1:X8}, and no approach-side point within {0:0.0} m is in a cell", StartRadius, declaredCell);
                return null;
            }

            reach = reachFor?.Invoke(region, regionPoint);
            return FindStart(declaredCell, point, approach, region, reach, out offset);
        }

        /// <summary>FindStart with the point's own cell as the region and no reachability filter (targets, tests).</summary>
        public BodyState? FindStart(uint cell, Vector3 point, Vector3? approach, out float offset)
            => FindStart(cell, point, approach, ResolveCell(cell, point) is var own && own != 0 ? own : cell, null, out offset);

        /// <summary>
        /// The nearest point within <see cref="StartRadius"/> of <paramref name="point"/> where the body stands: it settles
        /// onto a walkable floor within <see cref="MaxStartRise"/> of the point's height. Candidates whose standing cell
        /// is <paramref name="regionCell"/> come first, nearest first, and the first that stands ends the search; only
        /// then the rest, nearest first, each only when <paramref name="accept"/> takes its standing cell (null = any).
        /// A player walks up to a curated point and stands near it, never on it to the centimetre. Null = no standing
        /// room (a real content fault).
        /// </summary>
        public BodyState? FindStart(uint cell, Vector3 point, Vector3? approach, uint regionCell, Func<uint, bool> accept, out float offset)
        {
            offset = 0f;

            // Cells are resolved lazily, as the nearest-first search reaches each candidate, and kept for the second pass.
            var points = StartCandidates(point, approach).ToList();
            var cells = new uint?[points.Count];

            uint CellOf(int i) => cells[i] ??= ResolveCell(cell, points[i]);

            BodyState? Stand(Vector3 p, uint c)
            {
                var s = Settle(c, p);
                return s == null || !s.Value.Contact || !s.Value.OnWalkable || !StartRiseOk(s.Value.Pos.Z - point.Z, StepUpHeight) ? null : s;
            }

            foreach (var pass in new[] { true, false })
            {
                if (!pass && ReferenceEquals(accept, NeverAccept))
                    break;

                for (var i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var c = CellOf(i);

                    if (c == 0 || (c == regionCell) != pass)
                        continue;

                    var s = Stand(p, c);

                    if (s == null || (s.Value.Cell != regionCell && accept != null && !accept(s.Value.Cell)))
                        continue;

                    offset = Horizontal(p - point);
                    return s;
                }
            }

            return null;
        }

        /// <summary>
        /// Line of sight from the body's eye to a target's centre, built like WorldObject.IsDirectVisible: the same sight
        /// object (<see cref="SightSetup"/>, a Missile), raised to eye level (body height less the sight object's) and
        /// moved out past both radii, then one Transition AT THE TARGET'S CENTRE: seen when the cast arrives at the centre
        /// touching nothing. Every sweep target is either a point with no object (a shuffle spot no lever stands on) or
        /// the puzzle lever (wcid 1006851, PhysicsState 20 = Ethereal + IgnoreCollisions), which no cast can touch:
        /// PhysicsObj.FindObjCollisions returns OK for it before recording anything, so it never blocks its own sight.
        /// A collidable target object would block the cast at its own surface and read as not seen (a false fail, never
        /// a false pass); none is swept. The cast looks through exactly the objects the walk passes through (the same
        /// passable predicate, as Transition.IgnoresObject): openable retail doors, which a player opens on the way; a
        /// puzzle gate part is never passable, so it blocks sight as it blocks the walk.
        /// </summary>
        public bool CanSee(uint cell, Vector3 feet, Vector3 targetFeet, float targetHeight)
        {
            if (sight?.PartArray == null || body == null)
                return false;

            var eye = feet + new Vector3(0f, 0f, BodyHeight - sight.GetHeight());
            var centre = targetFeet + new Vector3(0f, 0f, targetHeight / 2f);
            var line = centre - eye;
            var length = line.Length();

            if (length < 1e-3f)
                return true;

            var dir = line / length;
            var from = eye + dir * (body.GetPhysicsRadius() + sight.GetPhysicsRadius());
            var beginCell = LScape.get_landcell(cell, instance);

            if (beginCell == null)
                return false;

            // Defensive and unproven: no test reaches it (removing it keeps every test green). The moved-out start can
            // fall outside the body's cell when the body stands pressed against a wall or at a cell boundary; the cast is
            // then begun at the eye itself, inside the body's cell, rather than from a point the transition would place
            // in the wrong cell.
            if (beginCell is EnvCell env && !env.point_in_cell(from))
                from = eye;

            var trans = Transition.MakeTransition(instance);
            trans.IgnoresObject = passable;

            trans.InitObject(sight, ObjectInfoState.Default | ObjectInfoState.PathClipped);
            trans.InitSphere(sight.PartArray.GetNumSphere(), sight.PartArray.GetSphere(), sight.Scale);
            trans.InitPath(beginCell, new Position(cell, new AFrame(from, Quaternion.Identity)), new Position(cell, new AFrame(centre, Quaternion.Identity)));

            var ok = trans.FindValidPosition();
            trans.CleanupTransition();

            var info = trans.CollisionInfo;

            if (info.CollidedWithEnvironment)
                return false;

            var touched = (info.CollideObject != null && info.CollideObject.Count > 0) || info.LastCollidedObject != null;

            return ok && trans.SpherePath.CurCell != null && !touched && Vector3.Distance(trans.SpherePath.CurPos.Frame.Origin, centre) < 0.05f;
        }

        /// <summary>The dungeon cell holding <paramref name="p"/> (looked up just above it, then at it); 0 = in no cell (inside a wall).</summary>
        private uint ResolveCell(uint near, Vector3 p)
        {
            if ((near & 0xFFFF) < 0x100)
                return near; // outdoor land cells: the transition finds the cell itself

            var adjust = ACE.Server.Physics.Util.AdjustCell.Get(near >> 16, instance);
            return adjust.GetCell(p + new Vector3(0f, 0f, 0.5f)) ?? adjust.GetCell(p) ?? 0u;
        }

        /// <summary>
        /// What a player needs to use the target: the server's own range check (Player_Move.CreateMoveToChain ->
        /// Landblock.WithinUseRadius -> WorldObject.IsWithinUseRadiusOf: Position.CylinderDistance between the body's
        /// cylinder and the target's) within the target's UseRadius less PuzzleSiteSweep.UseMargin, standing on a
        /// walkable floor, AND the target in sight (<see cref="CanSee"/>): a player clicks a lever they can see, not one
        /// behind a wall that happens to be within range. Sight is only cast once the range part holds.
        /// </summary>
        public ArrivalRule UseRange(uint targetCell, Vector3 target, UseReach reach)
        {
            var r = BodyRadius;
            var h = BodyHeight;

            return s =>
            {
                var d = PuzzleSiteSweep.UseDistance(r, h, s.Cell, s.Pos, reach, targetCell, target);
                bool? seen = d <= reach.UseRadius - PuzzleSiteSweep.UseMargin ? CanSee(s.Cell, s.Pos, target, reach.Height) : null;
                return PuzzleSiteSweep.UseArrival(d, reach, s.Contact && s.OnWalkable, seen);
            };
        }

        /// <summary>
        /// The return leg: back on a walkable floor within <see cref="StandRadius"/> (3D) of the start point the out leg
        /// used, and no more than this body's step-up height above or below it.
        /// </summary>
        public ArrivalRule ReturnRule(BodyState start)
        {
            var stepUp = StepUpHeight;
            return s => PuzzleSiteSweep.ReturnArrival(Vector3.Distance(s.Pos, start.Pos), s.Pos.Z - start.Pos.Z, stepUp, s.Contact && s.OnWalkable);
        }

        /// <summary>
        /// One leg from an already standing body: straight to <paramref name="target"/>, then via <paramref name="route"/>
        /// if the straight walk does not arrive. <paramref name="rule"/> decides arrival (null = the old point rule:
        /// within PuzzleSiteSweep.ProbeArrival on a walkable floor). <paramref name="end"/> is where the body stopped.
        /// </summary>
        public ProbeLeg WalkFrom(string label, string direction, BodyState start, Vector3 target, IReadOnlyList<Vector3> route, Func<uint, bool> allowedCell, ArrivalRule rule, out BodyState end)
            => WalkFrom(label, direction, start, target, route, allowedCell, rule, out end, out _);

        /// <summary>
        /// <see cref="WalkFrom(string, string, BodyState, Vector3, IReadOnlyList{Vector3}, Func{uint, bool}, ArrivalRule, out BodyState)"/>,
        /// also giving the positions the body stood in on the attempt it returns (the start first), for <see cref="Retrace"/>.
        /// </summary>
        public ProbeLeg WalkFrom(string label, string direction, BodyState start, Vector3 target, IReadOnlyList<Vector3> route, Func<uint, bool> allowedCell, ArrivalRule rule, out BodyState end, out List<BodyState> trail)
        {
            trail = new List<BodyState> { start };

            if (!Ready)
            {
                end = start;
                return ProbeLeg.Unchecked(label, direction, $"no body (Setup 0x{BodySetup:X8} did not load)");
            }

            var straight = WalkThrough(start, Array.Empty<Vector3>(), target, allowedCell, rule, out var straightEnd, trail);

            if (straight.Outcome == ProbeOutcome.Pass || route == null || route.Count == 0)
            {
                end = straightEnd;
                return straight with { Target = label, Direction = direction, Path = "straight" };
            }

            trail = new List<BodyState> { start };
            var routed = WalkThrough(start, route, target, allowedCell, rule, out end, trail);
            return routed with { Target = label, Direction = direction, Path = $"via {route.Count} doorway(s)", Reason = routed.Outcome == ProbeOutcome.Pass ? null : $"{routed.Reason} (straight: {straight.Reason})" };
        }

        /// <summary>
        /// Walks back the way an out leg came: through <paramref name="trail"/> (its recorded positions) in reverse, from
        /// <paramref name="from"/>, under <paramref name="rule"/>. A player can always walk back the way they came unless
        /// the way was one-way (a drop): this leg is what a return falls back to before it is called blocked.
        /// </summary>
        public ProbeLeg Retrace(string label, BodyState from, IReadOnlyList<BodyState> trail, Func<uint, bool> allowedCell, ArrivalRule rule, out BodyState end)
        {
            end = from;

            if (!Ready)
                return ProbeLeg.Unchecked(label, "back", $"no body (Setup 0x{BodySetup:X8} did not load)");

            if (trail == null || trail.Count == 0)
                return ProbeLeg.Blocked(label, "back", from.Pos, "no out-leg trail to retrace");

            var crumbs = new List<Vector3>();

            for (var i = trail.Count - 1; i >= 1; i--)
                if (crumbs.Count == 0 ? Horizontal(trail[i].Pos - from.Pos) > 0.3f : Horizontal(trail[i].Pos - crumbs[crumbs.Count - 1]) > 0.3f)
                    crumbs.Add(trail[i].Pos);

            var leg = WalkThrough(from, crumbs, trail[0].Pos, allowedCell, rule, out end, null, retracing: true);
            return leg with { Target = label, Direction = "back", Path = $"retraced the out leg ({crumbs.Count} point(s))" };
        }

        private ProbeLeg WalkThrough(BodyState from, IReadOnlyList<Vector3> route, Vector3 target, Func<uint, bool> allowedCell)
            => WalkThrough(from, route, target, allowedCell, null, out _, null);

        private ProbeLeg WalkThrough(BodyState from, IReadOnlyList<Vector3> route, Vector3 target, Func<uint, bool> allowedCell, ArrivalRule rule, out BodyState end, List<BodyState> trail, bool retracing = false)
        {
            var doors = new List<string>();
            var leg = WalkThroughCore(from, route, target, allowedCell, doors, rule, out end, trail, retracing);
            return doors.Count == 0 ? leg : leg with { DoorsPassed = doors };
        }

        private ProbeLeg WalkThroughCore(BodyState from, IReadOnlyList<Vector3> route, Vector3 target, Func<uint, bool> allowedCell, List<string> doors, ArrivalRule rule, out BodyState end, List<BodyState> trail = null, bool retracing = false)
        {
            var state = from;
            end = from;
            string Measure() => rule == null ? "" : "; " + rule(state).Measure;
            // Retracing: the crumbs are the body's own footsteps, so no push-through points between them.
            var goals = retracing ? (route ?? Array.Empty<Vector3>()).Select((p, k) => (p, false, k + 1)).Append((target, false, 0)).ToList() : Goals(from.Pos, route, target);

            for (var g = 0; g < goals.Count; g++)
            {
                var (goal, soft, doorway) = goals[g];
                var final = g == goals.Count - 1;
                var skip = false;
                var reach = final ? PuzzleSiteSweep.ProbeArrival : soft ? 0.3f : WaypointReach;
                var maxSteps = (int)Math.Ceiling(Horizontal(goal - state.Pos) / SegmentLength) * 3 + 10;
                var stalls = 0;
                var arrived = false;

                for (var i = 0; i < maxSteps; i++)
                {
                    var toGoal = Horizontal(goal - state.Pos);

                    // The final goal under a rule: arrived as soon as the rule says so (a lever is used from within its
                    // use range, not from on top of it); standing on the goal itself ends the walk either way.
                    if (final && rule != null ? (rule(state).Reached || toGoal < 0.05f) : toGoal <= reach)
                    {
                        arrived = true;
                        break;
                    }

                    var dir = new Vector3(goal.X - state.Pos.X, goal.Y - state.Pos.Y, 0f) / toGoal;
                    var offset = dir * Math.Min(SegmentLength, toGoal);
                    var next = Step(state, offset, null, out var obstruction);

                    // An openable door stopped the step: take it again with those doors out of the way, and name them.
                    if (passable != null && (next == null || toGoal - Horizontal(goal - next.Value.Pos) < MinProgress))
                    {
                        // IgnoresObject is asked about every shadow object in every cell the step touches, so the
                        // consulted set over-reports: keep only the doors this retry actually needed (below).
                        var consulted = new List<PhysicsObj>();
                        var retry = Step(state, offset, o =>
                        {
                            if (!passable(o))
                                return false;

                            if (!consulted.Contains(o))
                                consulted.Add(o);

                            return true;
                        }, out var retryObstruction);

                        if (retry != null && consulted.Count > 0
                            && (next == null || Horizontal(goal - retry.Value.Pos) < Horizontal(goal - next.Value.Pos) - MinProgress))
                        {
                            next = retry;
                            obstruction = retryObstruction;

                            foreach (var o in consulted)
                                if (!ConsultedDoors.Contains(DoorName(o)))
                                    ConsultedDoors.Add(DoorName(o));

                            foreach (var o in NeededDoors(state, offset, goal, consulted, retry.Value))
                            {
                                var name = DoorName(o);

                                if (!doors.Contains(name))
                                    doors.Add(name);
                            }
                        }
                    }

                    if (next == null && soft)
                    {
                        skip = true;
                        break;
                    }

                    if (next == null)
                    {
                        end = state;
                        return ProbeLeg.Blocked(null, null, state.Pos, "no valid position for the next step" + Obstruction(obstruction) + Measure());
                    }

                    if (allowedCell != null && !allowedCell(next.Value.Cell))
                    {
                        end = next.Value;
                        return ProbeLeg.Blocked(null, null, next.Value.Pos, $"passed the gate into 0x{next.Value.Cell:X8}");
                    }

                    if (!next.Value.Contact)
                    {
                        var landed = Settle(next.Value.Cell, next.Value.Pos);

                        if (landed == null)
                            return ProbeLeg.Blocked(null, null, next.Value.Pos, $"fell with no floor within {MaxDrop:0} m");

                        next = landed;

                        if (allowedCell != null && !allowedCell(next.Value.Cell))
                            return ProbeLeg.Blocked(null, null, next.Value.Pos, $"fell past the gate into 0x{next.Value.Cell:X8}");
                    }

                    var progress = toGoal - Horizontal(goal - next.Value.Pos);
                    stalls = progress < MinProgress ? stalls + 1 : 0;
                    state = next.Value;
                    trail?.Add(state);

                    if (stalls >= MaxStalls && soft)
                    {
                        skip = true;
                        break;
                    }

                    if (stalls >= MaxStalls)
                    {
                        end = state;
                        return ProbeLeg.Blocked(null, null, state.Pos, (final ? "stopped short of the target" : retracing ? $"stopped short of footstep {doorway}" : $"stopped short of doorway {doorway}") + Obstruction(obstruction) + Measure());
                    }
                }

                if (!arrived && !skip && !soft)
                {
                    end = state;
                    return ProbeLeg.Blocked(null, null, state.Pos, (final ? "did not reach the target" : retracing ? $"did not reach footstep {doorway}" : $"did not reach doorway {doorway}") + Measure());
                }
            }

            end = state;

            if (rule == null)
                return PuzzleSiteSweep.ClassifyArrival(state.Pos, state.Contact && state.OnWalkable, target);

            var check = rule(state);
            return check.Reached
                ? ProbeLeg.Passed(state.Pos) with { Measure = check.Measure }
                : ProbeLeg.Blocked(null, null, state.Pos, "not within reach: " + check.Measure);
        }

        /// <summary>How far past a routed doorway the body is steered before it turns toward the next goal.</summary>
        public const float DoorwayPushThrough = 1.0f;

        /// <summary>
        /// The goals of one walk: each routed doorway, then a SOFT push-through point <see cref="DoorwayPushThrough"/>
        /// beyond it along the line the body came in on, then the target. A body that reaches a doorway centroid within
        /// <see cref="WaypointReach"/> is often still on the near side of the portal; turning straight for the next goal
        /// from there walks it into the doorway's jamb (observed at Filos Doom 0x01500141 -> 0x0150013F and the r7
        /// doorway, 2026-10-07), where a greedy straight-line step makes no progress. A player walks through the
        /// doorway first. A soft goal that cannot be reached is skipped, never a failure. Pure.
        /// </summary>
        public static List<(Vector3 Point, bool Soft, int Doorway)> Goals(Vector3 start, IReadOnlyList<Vector3> route, Vector3 target)
        {
            var goals = new List<(Vector3, bool, int)>();
            var prev = start;

            for (var i = 0; i < (route?.Count ?? 0); i++)
            {
                var w = route[i];
                goals.Add((w, false, i + 1));

                var dir = new Vector3(w.X - prev.X, w.Y - prev.Y, 0f);

                if (dir.Length() > 1e-3f)
                    goals.Add((w + Vector3.Normalize(dir) * DoorwayPushThrough, true, i + 1));

                prev = w;
            }

            goals.Add((target, false, 0));
            return goals;
        }

        /// <summary>
        /// The doors a successful retry needed: drops each consulted door in turn and keeps it out of the set when the
        /// step makes the same progress without ignoring it. Greedy, so the result is a minimal (not necessarily the
        /// smallest) set that still reproduces the retry's end point.
        /// </summary>
        private List<PhysicsObj> NeededDoors(BodyState state, Vector3 offset, Vector3 goal, List<PhysicsObj> consulted, BodyState retry)
        {
            var needed = new List<PhysicsObj>(consulted);
            var target = Horizontal(goal - retry.Pos);

            foreach (var door in consulted)
            {
                var without = needed.Where(o => !ReferenceEquals(o, door)).ToList();
                var trial = Step(state, offset, o => without.Contains(o), out _);

                if (trial != null && Horizontal(goal - trial.Value.Pos) <= target + 0.01f)
                    needed = without;
            }

            return needed;
        }

        private static string DoorName(PhysicsObj o)
        {
            var wo = o.WeenieObj?.WorldObject;
            return wo != null ? $"wcid {wo.WeenieClassId}" : $"object 0x{o.ID:X8}";
        }

        private static string Obstruction(string name) => string.IsNullOrEmpty(name) ? " (geometry)" : $" ({name})";

        private static float Horizontal(Vector3 v) => new Vector2(v.X, v.Y).Length();

        /// <summary>Drops the body from just above <paramref name="pos"/> onto the floor below. Null = no floor (or no cell).</summary>
        public BodyState? Settle(uint cell, Vector3 pos)
        {
            var lifted = new BodyState(cell, pos + new Vector3(0, 0, 0.5f), false, false, default, 0);
            var landed = Step(lifted, new Vector3(0, 0, -(MaxDrop + 0.5f)), null, out _);

            return landed != null && landed.Value.Contact ? landed : null;
        }

        /// <summary>One Transition from <paramref name="from"/> by <paramref name="offset"/>, as PhysicsObj.transition builds it.</summary>
        public BodyState? Step(BodyState from, Vector3 offset, Func<PhysicsObj, bool> ignore, out string obstruction)
        {
            obstruction = null;

            var beginCell = LScape.get_landcell(from.Cell, instance);

            if (beginCell == null)
                return null;

            var trans = Transition.MakeTransition(instance);
            trans.IgnoresObject = ignore;
            // Mirrors PhysicsObj.get_object_info for this body (non-admin move, at rest, never in water or sliding).
            var state = ObjectInfoState.Default;

            if (body.State.HasFlag(PhysicsState.EdgeSlide))
                state |= ObjectInfoState.EdgeSlide;

            if (from.Contact)
            {
                trans.InitContactPlane(from.ContactPlaneCell, from.ContactPlane, false);
                state |= ObjectInfoState.Contact;

                if (from.OnWalkable)
                    state |= ObjectInfoState.OnWalkable;
            }

            if (body.PartArray != null && body.PartArray.AllowsFreeHeading())
                state |= ObjectInfoState.FreeRotate;

            if (body.State.HasFlag(PhysicsState.Missile))
                state |= ObjectInfoState.PathClipped;

            trans.InitObject(body, state);
            trans.InitSphere(body.PartArray.GetNumSphere(), body.PartArray.GetSphere(), body.Scale);

            var begin = new Position(from.Cell, new AFrame(from.Pos, Quaternion.Identity));
            var end = new Position(from.Cell, new AFrame(from.Pos + offset, Quaternion.Identity));
            trans.InitPath(beginCell, begin, end);

            var ok = trans.FindValidPosition();
            trans.CleanupTransition();

            var hit = trans.CollisionInfo.LastCollidedObject ?? trans.CollisionInfo.CollideObject?.LastOrDefault();

            if (hit != null)
            {
                var wo = hit.WeenieObj?.WorldObject;
                obstruction = wo != null ? $"{wo.Name} wcid {wo.WeenieClassId}" : $"object 0x{hit.ID:X8}";
            }

            if (!ok || trans.SpherePath.CurCell == null)
                return null;

            var collisions = trans.CollisionInfo;
            var cur = trans.SpherePath.CurPos;
            var contact = collisions.ContactPlaneValid;

            return new BodyState(cur.ObjCellID, cur.Frame.Origin, contact, contact && collisions.ContactPlane.Normal.Z >= PhysicsGlobals.FloorZ,
                collisions.ContactPlane, collisions.ContactPlaneCellID);
        }
    }
}

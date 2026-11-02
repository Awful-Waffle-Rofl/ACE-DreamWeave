using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>One thing on the ground that blocks a spot or a path: where (metres east and north of the anchor), how wide, what.</summary>
    public readonly struct AreaObstacle
    {
        public AreaObstacle(double x, double y, double radius, string kind)
        {
            X = x;
            Y = y;
            Radius = radius;
            Kind = kind;
        }

        public double X { get; }
        public double Y { get; }
        public double Radius { get; }

        /// <summary>building, static object, tree or rock.</summary>
        public string Kind { get; }
    }

    /// <summary>Where one portal stands after the check: compass bearing in degrees and distance from the anchor, and where it was asked for.</summary>
    public sealed class PortalSpot
    {
        public double Bearing { get; init; }
        public double Radius { get; init; }
        public double AskedBearing { get; init; }
        public double AskedRadius { get; init; }

        public bool Moved => Math.Abs(WorldEventSpawnArea.AngleDiff(Bearing, AskedBearing)) > 0.01 || Math.Abs(Radius - AskedRadius) > 0.01;
    }

    /// <summary>
    /// The clear-area check's answer for one anchor (goal-agnostic: any goal's start-spot rule may produce one). Built once,
    /// on the start command's thread, before the run exists; read-only after that.
    /// </summary>
    public sealed class WorldEventAreaPlan
    {
        public bool Ok { get; set; }

        /// <summary>Why the anchor fails (Ok false): the targets' ground, or too few portals.</summary>
        public string Why { get; set; }

        /// <summary>The portals kept, in ring order. Empty when the goal has no portals.</summary>
        public List<PortalSpot> Portals { get; } = new List<PortalSpot>();

        /// <summary>One line per adjustment ("portal 2 (135 deg, 30 m): a tree or rock on its path...; moved to 150 deg, 30 m").</summary>
        public List<string> Notes { get; } = new List<string>();

        public int Skipped { get; set; }

        public int Obstacles { get; set; }
    }

    /// <summary>
    /// What the clear-area check reads about the ground, in GLOBAL metres (landblock X * 192 + local X). The production
    /// implementation is <see cref="WorldEventDatLandscape"/> (client dat only, never a loaded landblock); tests hand in a
    /// fake. One instance serves one resolve on one thread.
    /// </summary>
    public interface IWorldEventLandscape
    {
        /// <summary>The obstacles within <paramref name="radius"/> of the global point, in metres relative to it; anything at or below <paramref name="ignoreRadius"/> is left out.</summary>
        IReadOnlyList<AreaObstacle> ObstaclesNear(double globalX, double globalY, double radius, double ignoreRadius);

        /// <summary>The ground at the global point is outdoors, dry land and a walkable slope.</summary>
        bool GroundOk(double globalX, double globalY);

        /// <summary>The terrain height at the global point, or null when it cannot be read.</summary>
        float? TerrainZ(double globalX, double globalY);
    }

    /// <summary>
    /// The Hold clear spawn area (protect.clearArea). Before a run places its scene, its anchor is checked:
    ///   * each knight's spot (and the anchor itself) needs targetClearance metres of open, walkable ground;
    ///   * each portal needs portalClearance metres, and its path in (from where the monsters appear to the two nearest
    ///     knights) pathClearance metres either side, on walkable ground with no water;
    ///   * obstacles are buildings, static objects and landscape scenery (trees, rocks), each a circle of its collision size;
    ///     anything no wider than ignoreRadius is ignored.
    /// A blocked portal is turned along its ring (5 degree steps up to maxPortalShiftDegrees) and moved in or out (2 m steps up
    /// to maxPortalShiftMetres), nearest change first, never within 25 degrees of another portal; one with no clear spot is
    /// skipped. The anchor passes when every knight's ground is clear and at least minPortals portals stand. A failing anchor
    /// is replaced by the nearest one that passes within searchRadius (rings 5 m apart, 12 bearings each).
    ///
    /// Ported from DreamWeave #17's WorldEventSpawnArea. Everything here is pure over an <see cref="IWorldEventLandscape"/>;
    /// unlike the contributor's version it never reads a physics landblock (see WorldEventHoldStartSpotRule for why).
    /// </summary>
    public static class WorldEventSpawnArea
    {
        /// <summary>The closest two portals may stand, in degrees around the ring.</summary>
        public const double MinPortalSeparationDegrees = 25;

        /// <summary>The smallest difference between two compass bearings, in degrees (-180 to 180).</summary>
        public static double AngleDiff(double a, double b)
        {
            var d = (a - b) % 360.0;

            if (d > 180)
                d -= 360;
            else if (d < -180)
                d += 360;

            return d;
        }

        /// <summary>A point <paramref name="distance"/> metres from the anchor at compass <paramref name="bearing"/> degrees (east, north).</summary>
        public static (double X, double Y) At(double bearing, double distance)
        {
            var r = bearing * Math.PI / 180.0;

            return (distance * Math.Sin(r), distance * Math.Cos(r));
        }

        /// <summary>The distance from point P to the segment AB.</summary>
        public static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1) : 0;
            var cx = ax + t * dx - px;
            var cy = ay + t * dy - py;

            return Math.Sqrt(cx * cx + cy * cy);
        }

        /// <summary>The first obstacle within <paramref name="clearance"/> of a spot, or null.</summary>
        public static AreaObstacle? BlockingSpot(double x, double y, double clearance, IReadOnlyList<AreaObstacle> obstacles)
        {
            foreach (var o in obstacles ?? Array.Empty<AreaObstacle>())
            {
                var dx = o.X - x;
                var dy = o.Y - y;

                if (Math.Sqrt(dx * dx + dy * dy) < o.Radius + clearance)
                    return o;
            }

            return null;
        }

        /// <summary>The first obstacle within <paramref name="clearance"/> of the segment AB, or null.</summary>
        public static AreaObstacle? BlockingPath(double ax, double ay, double bx, double by, double clearance, IReadOnlyList<AreaObstacle> obstacles)
        {
            foreach (var o in obstacles ?? Array.Empty<AreaObstacle>())
            {
                if (SegmentDistance(o.X, o.Y, ax, ay, bx, by) < o.Radius + clearance)
                    return o;
            }

            return null;
        }

        /// <summary>Whether the ground is walkable all along AB, sampled every 2 m (a null <paramref name="groundOk"/> passes).</summary>
        public static bool GroundAlong(double ax, double ay, double bx, double by, Func<double, double, bool> groundOk)
        {
            if (groundOk == null)
                return true;

            var length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            var steps = Math.Max(1, (int)Math.Ceiling(length / 2.0));

            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;

                if (!groundOk(ax + t * (bx - ax), ay + t * (by - ay)))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The check at one anchor: <paramref name="targets"/> are the knights' offsets, <paramref name="obstacles"/> and
        /// <paramref name="groundOk"/> the landscape around it in anchor-relative metres.
        /// </summary>
        public static WorldEventAreaPlan Plan(IReadOnlyList<WorldEventHoldRules.RingOffset> targets, PortalsDef portals, ClearAreaDef def,
            IReadOnlyList<AreaObstacle> obstacles, Func<double, double, bool> groundOk)
        {
            def ??= new ClearAreaDef();
            obstacles ??= Array.Empty<AreaObstacle>();

            var plan = new WorldEventAreaPlan { Obstacles = obstacles.Count };
            var knights = (targets ?? Array.Empty<WorldEventHoldRules.RingOffset>()).Select(t => ((double)t.Dx, (double)t.Dy)).ToList();

            // The knights (and the campfire in the middle): open, walkable ground around each.
            var spots = knights.Count > 0 ? knights.Concat(new[] { (0.0, 0.0) }).Distinct().ToList() : new List<(double, double)> { (0.0, 0.0) };

            foreach (var (x, y) in spots)
            {
                var blocker = BlockingSpot(x, y, def.TargetClearance, obstacles);

                if (blocker != null)
                {
                    plan.Why = $"a {blocker.Value.Kind} stands {Distance(x, y, blocker.Value.X, blocker.Value.Y):0.0} m from a knight's spot ({x:0}, {y:0})";
                    return plan;
                }

                if (!GroundAlong(x - def.TargetClearance / 2, y, x + def.TargetClearance / 2, y, groundOk)
                    || !GroundAlong(x, y - def.TargetClearance / 2, x, y + def.TargetClearance / 2, groundOk))
                {
                    plan.Why = $"the ground at a knight's spot ({x:0}, {y:0}) is water or too steep";
                    return plan;
                }
            }

            if (portals == null)
            {
                plan.Ok = true;
                return plan;
            }

            var count = Math.Max(1, portals.Count);
            var asked = WorldEventHoldRules.PortalBearings(count, portals.RotationDegrees).Select(r => r * 180.0 / Math.PI).ToList();
            var ringRadius = knights.Count > 0 ? knights.Max(k => Math.Sqrt(k.Item1 * k.Item1 + k.Item2 * k.Item2)) : 0;
            var minRadius = ringRadius + def.TargetClearance + def.PortalClearance;

            for (var i = 0; i < asked.Count; i++)
            {
                var placed = plan.Portals.Select(p => p.Bearing).ToList();
                PortalSpot found = null;
                string firstBlock = null;

                foreach (var (dDeg, dMetres) in ShiftOrder(def.MaxPortalShiftDegrees, def.MaxPortalShiftMetres))
                {
                    var bearing = ((asked[i] + dDeg) % 360 + 360) % 360;
                    var radius = portals.Radius + dMetres;

                    if (radius < minRadius || placed.Any(b => Math.Abs(AngleDiff(b, bearing)) < MinPortalSeparationDegrees))
                        continue;

                    var why = PortalBlock(bearing, radius, portals.Inset, knights, def, obstacles, groundOk);

                    if (why == null)
                    {
                        found = new PortalSpot { Bearing = bearing, Radius = radius, AskedBearing = asked[i], AskedRadius = portals.Radius };
                        break;
                    }

                    firstBlock ??= why;
                }

                if (found != null)
                {
                    plan.Portals.Add(found);

                    if (found.Moved)
                        plan.Notes.Add($"portal {i + 1} ({asked[i]:0} deg, {portals.Radius:0} m): {firstBlock}; moved to {found.Bearing:0} deg, {found.Radius:0} m");
                }
                else
                {
                    plan.Skipped++;
                    plan.Notes.Add($"portal {i + 1} ({asked[i]:0} deg, {portals.Radius:0} m): {firstBlock ?? "no room"}, and no clear spot within " +
                                   $"{def.MaxPortalShiftDegrees:0} deg or {def.MaxPortalShiftMetres:0} m; skipped");
                }
            }

            var needed = Math.Min(def.MinPortals, count);

            if (plan.Portals.Count < needed)
            {
                plan.Why = $"only {plan.Portals.Count} of {count} portals have a clear path to the knights (at least {needed} needed)";
                return plan;
            }

            plan.Ok = true;
            return plan;
        }

        /// <summary>Why a portal at this spot is blocked, or null when it is clear.</summary>
        public static string PortalBlock(double bearing, double radius, double inset, IReadOnlyList<(double X, double Y)> knights, ClearAreaDef def,
            IReadOnlyList<AreaObstacle> obstacles, Func<double, double, bool> groundOk)
        {
            var (px, py) = At(bearing, radius);

            var at = BlockingSpot(px, py, def.PortalClearance, obstacles);

            if (at != null)
                return $"a {at.Value.Kind} {Distance(px, py, at.Value.X, at.Value.Y):0.0} m away";

            if (groundOk != null && !groundOk(px, py))
                return "water or a steep slope under it";

            var (sx, sy) = At(bearing, Math.Max(1.0, radius - Math.Max(0, inset)));

            // The path in: to the two nearest knights (to the anchor when there are none).
            var ends = knights.Count == 0
                ? new List<(double X, double Y)> { (0.0, 0.0) }
                : knights.OrderBy(k => Distance(sx, sy, k.X, k.Y)).Take(2).ToList();

            foreach (var (kx, ky) in ends)
            {
                var onPath = BlockingPath(sx, sy, kx, ky, def.PathClearance, obstacles);

                if (onPath != null)
                    return $"a {onPath.Value.Kind} on its path to the knights";

                if (!GroundAlong(sx, sy, kx, ky, groundOk))
                    return "water or a steep slope on its path to the knights";
            }

            return null;
        }

        /// <summary>The shifts to try, nearest change first: none, then 5 degree and 2 m steps out to the limits.</summary>
        public static List<(double Degrees, double Metres)> ShiftOrder(double maxDegrees, double maxMetres)
        {
            var degrees = new List<double> { 0 };

            for (var d = 5.0; d <= maxDegrees + 1e-9; d += 5)
            {
                degrees.Add(d);
                degrees.Add(-d);
            }

            var metres = new List<double> { 0 };

            for (var m = 2.0; m <= maxMetres + 1e-9; m += 2)
            {
                metres.Add(-m);
                metres.Add(m);
            }

            return degrees.SelectMany(d => metres.Select(m => (d, m)))
                .OrderBy(s => Math.Abs(s.d) / 10.0 + Math.Abs(s.m) / 4.0)
                .ToList();
        }

        /// <summary>The anchors to try after the asked one: rings 5 m apart out to <paramref name="searchRadius"/>, 12 bearings each, nearest first.</summary>
        public static List<(double X, double Y)> SearchOffsets(double searchRadius)
        {
            var list = new List<(double, double)>();

            for (var r = 5.0; r <= searchRadius + 1e-9; r += 5)
                for (var b = 0; b < 360; b += 30)
                    list.Add(At(b + (r / 5 % 2 == 0 ? 15 : 0), r));

            return list;
        }

        /// <summary>The kept portals as ring offsets: each at its own bearing and distance, facing outward, for the portal parts.</summary>
        public static List<WorldEventHoldRules.RingOffset> SpotOffsets(IReadOnlyList<PortalSpot> spots)
        {
            return (spots ?? Array.Empty<PortalSpot>()).Select(s =>
            {
                var (x, y) = At(s.Bearing, s.Radius);

                return new WorldEventHoldRules.RingOffset((float)x, (float)y, WorldEventHoldRules.OutwardYaw(s.Bearing));
            }).ToList();
        }

        /// <summary>How far around an anchor the obstacles matter: the portal ring plus its shift and clearance, plus 10 m.</summary>
        public static double Reach(PortalsDef portals, double ringRadius, ClearAreaDef def) =>
            (portals?.Radius ?? ringRadius) + (def?.MaxPortalShiftMetres ?? 0) + (def?.PortalClearance ?? 0) + 10;

        /// <summary>The check at the global point (<paramref name="gx"/>, <paramref name="gy"/>) over <paramref name="landscape"/>.</summary>
        public static WorldEventAreaPlan CheckAt(IWorldEventLandscape landscape, double gx, double gy,
            IReadOnlyList<WorldEventHoldRules.RingOffset> targets, PortalsDef portals, ClearAreaDef def, double reach)
        {
            def ??= new ClearAreaDef();

            var obstacles = landscape?.ObstaclesNear(gx, gy, reach, def.IgnoreRadius) ?? Array.Empty<AreaObstacle>();

            return Plan(targets, portals, def, obstacles, landscape == null ? null : (x, y) => landscape.GroundOk(gx + x, gy + y));
        }

        /// <summary>
        /// The clear-area search from the global point (<paramref name="gx"/>, <paramref name="gy"/>): the point itself first,
        /// then <see cref="SearchOffsets"/> nearest first. True with the plan and where it holds (<paramref name="atX"/>,
        /// <paramref name="atY"/>, the asked point when it passed); false with <paramref name="why"/> (the asked point's reason).
        /// <paramref name="accept"/> may veto a candidate (e.g. the town reach rule); the asked point is never vetoed here.
        /// </summary>
        public static bool TrySearch(IWorldEventLandscape landscape, double gx, double gy, IReadOnlyList<WorldEventHoldRules.RingOffset> targets,
            PortalsDef portals, ClearAreaDef def, out WorldEventAreaPlan plan, out double atX, out double atY, out string why,
            Func<double, double, bool> accept = null)
        {
            def ??= new ClearAreaDef();
            atX = gx;
            atY = gy;
            why = null;

            var ringRadius = (targets ?? Array.Empty<WorldEventHoldRules.RingOffset>()).Select(t => Math.Sqrt(t.Dx * t.Dx + t.Dy * t.Dy)).DefaultIfEmpty(0).Max();
            var reach = Reach(portals, ringRadius, def);

            var first = CheckAt(landscape, gx, gy, targets, portals, def, reach);

            if (first.Ok)
            {
                plan = first;
                return true;
            }

            foreach (var (dx, dy) in SearchOffsets(def.SearchRadius))
            {
                var cx = gx + dx;
                var cy = gy + dy;

                if (landscape != null && !landscape.GroundOk(cx, cy))
                    continue;

                if (accept != null && !accept(cx, cy))
                    continue;

                var next = CheckAt(landscape, cx, cy, targets, portals, def, reach);

                if (!next.Ok)
                    continue;

                next.Notes.Insert(0, $"the asked spot failed ({first.Why}); moved {Math.Sqrt(dx * dx + dy * dy):0} m {WorldEventHoldRules.CompassSide(dx, dy)}");
                plan = next;
                atX = cx;
                atY = cy;
                return true;
            }

            plan = first;
            why = def.SearchRadius > 0 ? $"{first.Why}, and no open spot within {def.SearchRadius:0} m" : first.Why;
            return false;
        }

        private static double Distance(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }
}

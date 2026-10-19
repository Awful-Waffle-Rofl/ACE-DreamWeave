using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using LandCell = ACE.Server.Physics.Common.LandCell;
using LandDefs = ACE.Server.Physics.Common.LandDefs;
using LScape = ACE.Server.Physics.Common.LScape;
using PhysLandblock = ACE.Server.Physics.Common.Landblock;
using Position = ACE.Entity.Position;

using log4net;

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

        /// <summary>building, static, scenery or object.</summary>
        public string Kind { get; }
    }

    /// <summary>Where one portal stands after the check: compass bearing in degrees and distance from the anchor, and where it was asked for.</summary>
    public sealed class PortalSpot
    {
        public double Bearing { get; set; }
        public double Radius { get; set; }
        public double AskedBearing { get; set; }
        public double AskedRadius { get; set; }

        public bool Moved => Math.Abs(WorldEventSpawnArea.AngleDiff(Bearing, AskedBearing)) > 0.01 || Math.Abs(Radius - AskedRadius) > 0.01;
    }

    /// <summary>The check's answer for one anchor.</summary>
    public sealed class AreaPlan
    {
        public bool Ok { get; set; }

        /// <summary>Why the anchor fails (Ok false): the knights' ground, or too few portals.</summary>
        public string Why { get; set; }

        /// <summary>The portals kept, in ring order.</summary>
        public List<PortalSpot> Portals { get; } = new List<PortalSpot>();

        /// <summary>One line per adjustment ("portal 2 (135 deg, 30 m): a tree on its path; moved to 150 deg, 30 m").</summary>
        public List<string> Notes { get; } = new List<string>();

        public int Skipped { get; set; }

        public int Obstacles { get; set; }
    }

    /// <summary>
    /// The Hold clear spawn area (protect.clearArea, 2026-10-19). Before a run places its scene, its anchor is checked:
    ///   * each knight's spot (with its shrine and pennant) needs targetClearance metres of open, walkable ground;
    ///   * each portal needs portalClearance metres, and its path in (from where the monsters appear to the two nearest
    ///     knights) pathClearance metres either side, on walkable ground with no water;
    ///   * the obstacles are buildings, static objects, landscape scenery (trees, rocks) and placed objects (NPCs, signs,
    ///     portals) read from the physics landblocks, each as a circle of its collision size; anything smaller than
    ///     ignoreRadius (grass, flowers) is ignored.
    /// A blocked portal is turned along its ring (5 degree steps up to maxPortalShiftDegrees) and moved in or out (2 m steps
    /// up to maxPortalShiftMetres), nearest change first, never within 25 degrees of another portal; one with no clear spot
    /// is skipped. The anchor passes when every knight's ground is clear and at least minPortals portals stand. A failing
    /// anchor is replaced by the nearest one that passes within searchRadius (rings 5 m apart, 12 bearings each); with
    /// none, the start is refused with the reason.
    ///
    /// <see cref="Plan"/> is pure and tested; <see cref="Gather"/> and <see cref="TryResolve"/> read the live landscape.
    /// </summary>
    public static class WorldEventSpawnArea
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventSpawnArea));

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

        /// <summary>The distance from point P to the segment AB (D6 - pure).</summary>
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

        /// <summary>The first obstacle within <paramref name="clearance"/> of a spot (D6 - pure), or null.</summary>
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

        /// <summary>The first obstacle within <paramref name="clearance"/> of the segment AB (D6 - pure), or null.</summary>
        public static AreaObstacle? BlockingPath(double ax, double ay, double bx, double by, double clearance, IReadOnlyList<AreaObstacle> obstacles)
        {
            foreach (var o in obstacles ?? Array.Empty<AreaObstacle>())
            {
                if (SegmentDistance(o.X, o.Y, ax, ay, bx, by) < o.Radius + clearance)
                    return o;
            }

            return null;
        }

        /// <summary>Whether the ground is walkable all along AB, sampled every 2 m (D6 - pure over <paramref name="groundOk"/>).</summary>
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
        /// The check at one anchor (D6 - pure): <paramref name="targets"/> are the knights' offsets, <paramref name="obstacles"/>
        /// and <paramref name="groundOk"/> the landscape around it in anchor-relative metres.
        /// </summary>
        public static AreaPlan Plan(IReadOnlyList<WorldEventHoldRules.RingOffset> targets, PortalsDef portals, ClearAreaDef def,
            IReadOnlyList<AreaObstacle> obstacles, Func<double, double, bool> groundOk)
        {
            def ??= new ClearAreaDef();
            obstacles ??= Array.Empty<AreaObstacle>();

            var plan = new AreaPlan { Obstacles = obstacles.Count };
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

        /// <summary>Why a portal at this spot is blocked (D6 - pure), or null when it is clear.</summary>
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

        /// <summary>The shifts to try (D6 - pure), nearest change first: none, then 5 degree and 2 m steps out to the limits.</summary>
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

        /// <summary>The anchors to try after the asked one (D6 - pure): rings 5 m apart out to <paramref name="searchRadius"/>, 12 bearings each, nearest first.</summary>
        public static List<(double X, double Y)> SearchOffsets(double searchRadius)
        {
            var list = new List<(double, double)>();

            for (var r = 5.0; r <= searchRadius + 1e-9; r += 5)
                for (var b = 0; b < 360; b += 30)
                    list.Add(At(b + (r / 5 % 2 == 0 ? 15 : 0), r));

            return list;
        }

        /// <summary>The kept portals as ring offsets (D6 - pure): each at its own bearing and distance, facing outward, for the portal parts.</summary>
        public static List<WorldEventHoldRules.RingOffset> SpotOffsets(IReadOnlyList<PortalSpot> spots)
        {
            return (spots ?? Array.Empty<PortalSpot>()).Select(s =>
            {
                var (x, y) = At(s.Bearing, s.Radius);

                return new WorldEventHoldRules.RingOffset((float)x, (float)y, WorldEventHoldRules.OutwardYaw(s.Bearing));
            }).ToList();
        }

        private static double Distance(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        // ---- live ------------------------------------------------------------------------------------

        /// <summary>
        /// The obstacles within <paramref name="radius"/> of <paramref name="anchor"/> (live: the physics landblocks, loaded
        /// as needed), in anchor-relative metres. Players and monsters are left out (they move); so is anything smaller than
        /// <paramref name="ignoreRadius"/>.
        /// </summary>
        public static List<AreaObstacle> Gather(Position anchor, double radius, double ignoreRadius)
        {
            var result = new List<AreaObstacle>();

            if (anchor == null || anchor.Indoors)
                return result;

            var centre = anchor.ToGlobal();
            var seen = new HashSet<object>();

            var minX = (int)Math.Floor((centre.X - radius) / Position.BlockLength);
            var maxX = (int)Math.Floor((centre.X + radius) / Position.BlockLength);
            var minY = (int)Math.Floor((centre.Y - radius) / Position.BlockLength);
            var maxY = (int)Math.Floor((centre.Y + radius) / Position.BlockLength);

            for (var bx = Math.Max(0, minX); bx <= Math.Min(254, maxX); bx++)
            {
                for (var by = Math.Max(0, minY); by <= Math.Min(254, maxY); by++)
                {
                    PhysLandblock block;

                    try
                    {
                        block = LScape.get_landblock((uint)((bx << 24) | (by << 16) | 0xFFFF), anchor.Instance);
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"[WORLDEVENT] spawn area: landblock {bx:X2}{by:X2} could not be read", ex);
                        continue;
                    }

                    if (block == null)
                        continue;

                    void Add(PhysicsObj obj, string kind, double? fixedRadius = null)
                    {
                        if (obj == null || obj.Position == null || !seen.Add(obj))
                            return;

                        var r = fixedRadius ?? EffectiveRadius(obj);

                        if (!(r > ignoreRadius))
                            return;

                        var gx = bx * Position.BlockLength + obj.Position.Frame.Origin.X;
                        var gy = by * Position.BlockLength + obj.Position.Frame.Origin.Y;

                        // An object whose cell id names another landblock (it straddles) is measured from its own block.
                        var cell = obj.Position.ObjCellID;

                        if ((cell >> 16) != (uint)((bx << 8) | by) && (cell & 0xFFFF) < 0x100)
                        {
                            gx = ((cell >> 24) & 0xFF) * Position.BlockLength + obj.Position.Frame.Origin.X;
                            gy = ((cell >> 16) & 0xFF) * Position.BlockLength + obj.Position.Frame.Origin.Y;
                        }

                        var dx = gx - centre.X;
                        var dy = gy - centre.Y;

                        if (Math.Sqrt(dx * dx + dy * dy) - r > radius)
                            return;

                        result.Add(new AreaObstacle(dx, dy, r, kind));
                    }

                    // The landscape lists are fixed once loaded; the placed objects change on their landblock's thread, so a
                    // read that races one is skipped for that block rather than failing the check.
                    try
                    {
                        foreach (var b in (block.Buildings ?? new List<ACE.Server.Physics.Common.BuildingObj>()).ToList())
                            Add(b, "building", b?.GetRadius());

                        foreach (var o in (block.StaticObjects ?? new List<PhysicsObj>()).ToList())
                            Add(o, "static object");

                        foreach (var o in (block.Scenery ?? new List<PhysicsObj>()).ToList())
                            Add(o, "tree or rock");
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"[WORLDEVENT] spawn area: landblock {bx:X2}{by:X2} scenery could not be read", ex);
                    }

                    List<PhysicsObj> placed;

                    try
                    {
                        placed = (block.ServerObjects ?? new List<PhysicsObj>()).ToList();
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"[WORLDEVENT] spawn area: landblock {bx:X2}{by:X2} objects changed while read; skipped", ex);
                        continue;
                    }

                    foreach (var o in placed)
                    {
                        var wo = o?.WeenieObj?.WorldObject;

                        // Players and monsters move; an attackable creature is not scenery; a run's own props are not in the way.
                        if (wo == null || wo is Player || (wo is Creature c && c.Attackable) || wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.WorldEventId) != null)
                            continue;

                        Add(o, "placed object");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The collision size of a landscape object (live): its widest cylinder (a tree's trunk), else its spheres, else 0.6 of
        /// its bounding radius (a rock or a building piece with a BSP). 0 for something ethereal.
        /// </summary>
        public static double EffectiveRadius(PhysicsObj obj)
        {
            if (obj == null || obj.State.HasFlag(ACE.Entity.Enum.PhysicsState.Ethereal))
                return 0;

            var parts = obj.PartArray;

            if (parts == null)
                return 0;

            var scale = obj.Scale > 0 ? obj.Scale : 1.0f;

            try
            {
                if (parts.GetNumCylsphere() > 0)
                    return parts.GetCylSphere().Max(c => c.Radius) * scale;

                if (parts.GetNumSphere() > 0)
                    return parts.GetSphere().Max(s => s.Radius + new Vector2(s.Center.X, s.Center.Y).Length()) * scale;
            }
            catch
            {
                // fall through to the bounding radius
            }

            return obj.GetRadius() * 0.6;
        }

        /// <summary>Whether the ground at a spot (anchor-relative metres) is outdoors, dry land, not a building's cell interior, and walkable (live).</summary>
        public static bool GroundOk(Position anchor, double dx, double dy)
        {
            try
            {
                var centre = anchor.ToGlobal();
                var p = anchor.FromGlobal(new Vector3(centre.X + (float)dx, centre.Y + (float)dy, centre.Z));
                p.Instance = anchor.Instance;

                if (p.Indoors)
                    return false;

                var cell = LScape.get_landcell(p.GetOutdoorCell(), p.Instance) as LandCell;

                if (cell == null || cell.WaterType != LandDefs.WaterType.NotWater)
                    return false;

                return p.IsWalkable();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The check for a Hold run at <paramref name="anchor"/> (live). Ok: <paramref name="plan"/> holds the portals to place
        /// and <paramref name="moved"/> the anchor to use (null when the asked one passed). Not ok: <paramref name="why"/> says
        /// what blocked it, for the refusal. Logged either way.
        /// </summary>
        public static bool TryResolve(Position anchor, ProtectDef protect, int participants, string logTag,
            out AreaPlan plan, out Position moved, out string why)
        {
            plan = null;
            moved = null;
            why = null;

            var def = protect?.ClearArea;

            if (def == null || anchor == null)
                return true;

            if (anchor.Indoors)
            {
                why = "the spot is indoors";
                return false;
            }

            var (count, ringRadius) = protect.ResolveLayout(Math.Max(1, participants));
            var targets = WorldEventHoldRules.RingOffsets(count, ringRadius);
            var reach = (protect.Portals?.Radius ?? ringRadius) + def.MaxPortalShiftMetres + def.PortalClearance + 10;

            AreaPlan Check(Position at)
            {
                var obstacles = Gather(at, reach, def.IgnoreRadius);

                return Plan(targets, protect.Portals, def, obstacles, (x, y) => GroundOk(at, x, y));
            }

            var first = Check(anchor);

            if (first.Ok)
            {
                plan = first;
                LogPlan(logTag, anchor, null, plan);
                return true;
            }

            var centre = anchor.ToGlobal();

            foreach (var (dx, dy) in SearchOffsets(def.SearchRadius))
            {
                Position candidate;

                try
                {
                    candidate = anchor.FromGlobal(new Vector3(centre.X + (float)dx, centre.Y + (float)dy, centre.Z));
                    candidate.Instance = anchor.Instance;

                    if (candidate.Indoors || !GroundOk(candidate, 0, 0))
                        continue;

                    candidate.PositionZ = candidate.GetTerrainZ();
                }
                catch
                {
                    continue;
                }

                var next = Check(candidate);

                if (!next.Ok)
                    continue;

                plan = next;
                moved = candidate;
                plan.Notes.Insert(0, $"the asked spot failed ({first.Why}); moved {Math.Sqrt(dx * dx + dy * dy):0} m {WorldEventHoldRules.CompassSide(dx, dy)}");
                LogPlan(logTag, anchor, moved, plan);
                return true;
            }

            why = def.SearchRadius > 0
                ? $"{first.Why}, and no open spot within {def.SearchRadius:0} m"
                : first.Why;

            log.Info($"[WORLDEVENT] {logTag} spawn area at {anchor.ToLOCString()}: refused ({why}); {first.Obstacles} obstacle(s) within {reach:0} m");

            return false;
        }

        private static void LogPlan(string logTag, Position asked, Position moved, AreaPlan plan)
        {
            var notes = plan.Notes.Count == 0 ? "nothing to adjust" : string.Join("; ", plan.Notes);

            log.Info($"[WORLDEVENT] {logTag} spawn area at {(moved ?? asked).ToLOCString()}: clear, {plan.Portals.Count} portal(s) " +
                     $"[{string.Join(", ", plan.Portals.Select(p => $"{p.Bearing:0} deg {p.Radius:0} m"))}], {plan.Obstacles} obstacle(s) nearby; {notes}");
        }
    }
}

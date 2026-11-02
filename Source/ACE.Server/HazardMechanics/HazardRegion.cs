using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    public enum HazardShape
    {
        Rect,
        Circle,
    }

    /// <summary>How a region's ground height is found.</summary>
    public enum HazardGroundMode
    {
        /// <summary>A fixed z plane (dungeons, arenas): <see cref="HazardRegion.GroundPlaneZ"/>.</summary>
        Plane,

        /// <summary>The outdoor terrain height at (x, y). Outdoor regions only. Ignores buildings.</summary>
        Terrain,
    }

    /// <summary>
    /// A zone inside a region where hazards are never SPAWNED or PLACED (a boss pad, a portal). Balls and moving
    /// hazards still pass through it - exclusions are a placement rule, never a safe spot.
    /// </summary>
    public readonly struct HazardExclusion
    {
        public HazardShape Shape { get; }
        public float MinX { get; }
        public float MaxX { get; }
        public float MinY { get; }
        public float MaxY { get; }
        public float CenterX { get; }
        public float CenterY { get; }
        public float Radius { get; }

        private HazardExclusion(HazardShape shape, float minX, float maxX, float minY, float maxY, float cx, float cy, float r)
        {
            Shape = shape;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            CenterX = cx;
            CenterY = cy;
            Radius = r;
        }

        public static HazardExclusion Rect(float minX, float maxX, float minY, float maxY)
            => new HazardExclusion(HazardShape.Rect, Math.Min(minX, maxX), Math.Max(minX, maxX), Math.Min(minY, maxY), Math.Max(minY, maxY), 0, 0, 0);

        public static HazardExclusion Circle(float cx, float cy, float radius)
            => new HazardExclusion(HazardShape.Circle, 0, 0, 0, 0, cx, cy, Math.Max(0f, radius));

        public bool Contains(float x, float y)
        {
            if (Shape == HazardShape.Circle)
            {
                var dx = x - CenterX;
                var dy = y - CenterY;
                return dx * dx + dy * dy <= Radius * Radius;
            }

            return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        }
    }

    /// <summary>
    /// The ONLY source of geometry a mechanic may use (shared invariant 4): one landblock + instance, a 2D shape in
    /// landblock-local metres, how the ground height is found, where moving hazards fly, an optional ceiling,
    /// placement exclusions, and the z band that decides whether a player is "in" the region.
    ///
    /// LANDBLOCKS PER RUN (owner ruling 2026-10-20, revoking the old one-landblock-per-run rule for arenas). Every
    /// region is in the frame of ONE anchor landblock (<see cref="Landblock"/>, the landblock the run's driver ticks
    /// on). A single-landblock region (every constructor but <see cref="ArenaCircle"/>) is unchanged: an outdoor one
    /// whose bounds leave [0, 192) is REFUSED by <see cref="Validate"/>, never clipped, and a dungeon landblock has
    /// no such bound (its cells decide), so a dungeon region is only checked for sanity; a point with no cell is
    /// refused at spawn time instead. A SPANNING region (<see cref="ArenaCircle"/>, <see cref="SpansLandblocks"/>)
    /// may overhang its anchor by up to one landblock on each axis - bounds strictly inside (-192, 384), under 192 m
    /// per side, so it covers at most 2 x 2 landblocks - and HazardSpan routes each of its points to the covered
    /// landblock that owns it. Start refuses one whose covered landblocks are not all loaded; a moving body is never
    /// server-relocated across a landblock edge.
    ///
    /// Immutable and pure: no landblock reference, no PropertyManager, so it is fully testable.
    /// </summary>
    public sealed class HazardRegion
    {
        public string Name { get; }

        /// <summary>The 16-bit landblock id (e.g. 0x0069).</summary>
        public ushort Landblock { get; }

        public uint Instance { get; }

        /// <summary>True for an outdoor landblock: local x/y must stay inside [0, 192) (a spanning region: (-192, 384)) and terrain ground is allowed.</summary>
        public bool Outdoor { get; }

        public HazardShape Shape { get; }

        // rect
        public float MinX { get; }
        public float MaxX { get; }
        public float MinY { get; }
        public float MaxY { get; }

        // circle
        public float CenterX { get; }
        public float CenterY { get; }
        public float Radius { get; }

        public HazardGroundMode GroundMode { get; }

        /// <summary>The ground height when <see cref="GroundMode"/> is Plane. Ignored for Terrain.</summary>
        public float GroundPlaneZ { get; }

        /// <summary>
        /// A fixed z for moving hazards (wisps). Null: hazards fly at ground + <see cref="HazardOffset"/>, which on
        /// a terrain region follows the ground.
        /// </summary>
        public float? HazardPlaneZ { get; }

        /// <summary>Height above the ground for moving hazards when <see cref="HazardPlaneZ"/> is null.</summary>
        public float HazardOffset { get; }

        /// <summary>Optional ceiling. No ceiling -> beams placed on the ceiling are refused by the beams mechanic.</summary>
        public float? CeilingZ { get; }

        /// <summary>A player counts as in the region from GroundZ - BandBelow ...</summary>
        public float BandBelow { get; }

        /// <summary>... to GroundZ + BandAbove.</summary>
        public float BandAbove { get; }

        public IReadOnlyList<HazardExclusion> Exclusions { get; }

        /// <summary>
        /// True for a region that may reach into the landblocks around its anchor landblock (an arena that straddles
        /// a landblock edge). Its coordinates stay in the ANCHOR landblock's frame, so x/y may be negative or past
        /// 192. See <see cref="Validate"/> for the bounds. False (every non-<see cref="ArenaCircle"/> region): one landblock.
        /// </summary>
        public bool SpansLandblocks { get; }

        /// <summary>Absolute z floor of the "in the region" band. Null: the band follows the ground (GroundZ - BandBelow).</summary>
        public float? AbsoluteBandMinZ { get; }

        /// <summary>Absolute z ceiling of the "in the region" band. Null: the band follows the ground (GroundZ + BandAbove).</summary>
        public float? AbsoluteBandMaxZ { get; }

        /// <summary>
        /// True: a hazard hit also needs the player to be inside the region (2D shape and band), so "hit by the
        /// hazard" equals "inside the arena". False (today's behaviour for every existing region): the hit test is
        /// the hazard's own reach.
        /// </summary>
        public bool HitsRequireInside { get; }

        private HazardRegion(string name, ushort landblock, uint instance, bool outdoor, HazardShape shape,
            float minX, float maxX, float minY, float maxY, float cx, float cy, float radius,
            HazardGroundMode groundMode, float groundPlaneZ, float? hazardPlaneZ, float hazardOffset, float? ceilingZ,
            float bandBelow, float bandAbove, IReadOnlyList<HazardExclusion> exclusions,
            bool spansLandblocks = false, float? absoluteBandMinZ = null, float? absoluteBandMaxZ = null, bool hitsRequireInside = false)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "region" : name;
            Landblock = landblock;
            Instance = instance;
            Outdoor = outdoor;
            Shape = shape;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            CenterX = cx;
            CenterY = cy;
            Radius = radius;
            GroundMode = groundMode;
            GroundPlaneZ = groundPlaneZ;
            HazardPlaneZ = hazardPlaneZ;
            HazardOffset = hazardOffset;
            CeilingZ = ceilingZ;
            BandBelow = Math.Max(0f, bandBelow);
            BandAbove = Math.Max(0f, bandAbove);
            Exclusions = exclusions ?? Array.Empty<HazardExclusion>();
            SpansLandblocks = spansLandblocks;
            AbsoluteBandMinZ = absoluteBandMinZ;
            AbsoluteBandMaxZ = absoluteBandMaxZ;
            HitsRequireInside = hitsRequireInside;
        }

        // ---- construction ----------------------------------------------------------------------------------------

        public static HazardRegion Rect(string name, ushort landblock, uint instance, bool outdoor,
            float minX, float maxX, float minY, float maxY,
            HazardGroundMode groundMode, float groundPlaneZ, float? hazardPlaneZ, float hazardOffset, float? ceilingZ,
            float bandBelow, float bandAbove, IReadOnlyList<HazardExclusion> exclusions = null)
        {
            var x0 = Math.Min(minX, maxX);
            var x1 = Math.Max(minX, maxX);
            var y0 = Math.Min(minY, maxY);
            var y1 = Math.Max(minY, maxY);

            return new HazardRegion(name, landblock, instance, outdoor, HazardShape.Rect, x0, x1, y0, y1,
                (x0 + x1) * 0.5f, (y0 + y1) * 0.5f, 0f,
                groundMode, groundPlaneZ, hazardPlaneZ, hazardOffset, ceilingZ, bandBelow, bandAbove, exclusions);
        }

        public static HazardRegion Circle(string name, ushort landblock, uint instance, bool outdoor,
            float centerX, float centerY, float radius,
            HazardGroundMode groundMode, float groundPlaneZ, float? hazardPlaneZ, float hazardOffset, float? ceilingZ,
            float bandBelow, float bandAbove, IReadOnlyList<HazardExclusion> exclusions = null)
        {
            var r = Math.Max(0f, radius);

            return new HazardRegion(name, landblock, instance, outdoor, HazardShape.Circle,
                centerX - r, centerX + r, centerY - r, centerY + r, centerX, centerY, r,
                groundMode, groundPlaneZ, hazardPlaneZ, hazardOffset, ceilingZ, bandBelow, bandAbove, exclusions);
        }

        /// <summary>
        /// A circular arena that may straddle landblock edges (world event arenas). Outdoor, terrain ground, centred
        /// at (<paramref name="centerX"/>, <paramref name="centerY"/>) in the ANCHOR landblock's frame (the centre
        /// itself is normally inside it; the circle may overhang by up to one landblock). The "in the region" band is
        /// the absolute z range [<paramref name="bandMinZ"/>, <paramref name="bandMaxZ"/>] and hits require the
        /// player to be inside it (<see cref="HitsRequireInside"/>). Hazards fly at ground + <paramref name="hazardOffset"/>.
        /// Still has to pass <see cref="Validate"/>.
        /// </summary>
        public static HazardRegion ArenaCircle(string name, ushort anchorLandblock, uint instance, float centerX, float centerY,
            float radius, float hazardOffset, float bandMinZ, float bandMaxZ, IReadOnlyList<HazardExclusion> exclusions = null)
        {
            var r = Math.Max(0f, radius);
            var half = Math.Max(0f, (bandMaxZ - bandMinZ) * 0.5f);

            return new HazardRegion(name, anchorLandblock, instance, true, HazardShape.Circle,
                centerX - r, centerX + r, centerY - r, centerY + r, centerX, centerY, r,
                HazardGroundMode.Terrain, (bandMinZ + bandMaxZ) * 0.5f, null, hazardOffset, null, half, half, exclusions,
                spansLandblocks: true, absoluteBandMinZ: bandMinZ, absoluteBandMaxZ: bandMaxZ, hitsRequireInside: true);
        }

        /// <summary>
        /// An ad-hoc region centred on the caller (the /hazard command). Dungeon: ground = a plane at the caller's
        /// z. Outdoors: ground = terrain. Hazard height = ground + <paramref name="hz"/>. No ceiling.
        /// Returns null with an error for a bad spec; the result still has to pass <see cref="Validate"/>.
        /// </summary>
        public static HazardRegion AdHoc(ushort landblock, uint instance, bool outdoor, Vector3 caller, HazardShapeSpec spec, float hz, out string error)
        {
            error = null;

            if (!float.IsFinite(caller.X) || !float.IsFinite(caller.Y) || !float.IsFinite(caller.Z))
            {
                error = "caller position is not finite";
                return null;
            }

            if (!float.IsFinite(hz))
            {
                error = "hz is not a number";
                return null;
            }

            var mode = outdoor ? HazardGroundMode.Terrain : HazardGroundMode.Plane;

            if (spec.Shape == HazardShape.Circle)
                return Circle("adhoc", landblock, instance, outdoor, caller.X, caller.Y, spec.Radius,
                    mode, caller.Z, null, hz, null, HazardDefaults.BandBelow, HazardDefaults.BandAbove + Math.Max(0f, hz));

            var hw = spec.Width * 0.5f;
            var hh = spec.Height * 0.5f;

            return Rect("adhoc", landblock, instance, outdoor, caller.X - hw, caller.X + hw, caller.Y - hh, caller.Y + hh,
                mode, caller.Z, null, hz, null, HazardDefaults.BandBelow, HazardDefaults.BandAbove + Math.Max(0f, hz));
        }

        /// <summary>A copy stamped with a different instance (presets are authored instance-free).</summary>
        public HazardRegion WithInstance(uint instance)
            => new HazardRegion(Name, Landblock, instance, Outdoor, Shape, MinX, MaxX, MinY, MaxY, CenterX, CenterY, Radius,
                GroundMode, GroundPlaneZ, HazardPlaneZ, HazardOffset, CeilingZ, BandBelow, BandAbove, Exclusions,
                SpansLandblocks, AbsoluteBandMinZ, AbsoluteBandMaxZ, HitsRequireInside);

        // ---- validation ------------------------------------------------------------------------------------------

        /// <summary>
        /// Null when the region is usable; otherwise why not. Refuses (never clips) an outdoor region that leaves
        /// its one landblock, a zero-size shape, non-finite numbers, terrain ground in a dungeon, and a ceiling at
        /// or below the ground plane.
        /// </summary>
        public string Validate()
        {
            if (!AllFinite(MinX, MaxX, MinY, MaxY, CenterX, CenterY, Radius, GroundPlaneZ, HazardOffset, BandBelow, BandAbove)
                || (HazardPlaneZ.HasValue && !float.IsFinite(HazardPlaneZ.Value))
                || (CeilingZ.HasValue && !float.IsFinite(CeilingZ.Value))
                || (AbsoluteBandMinZ.HasValue && !float.IsFinite(AbsoluteBandMinZ.Value))
                || (AbsoluteBandMaxZ.HasValue && !float.IsFinite(AbsoluteBandMaxZ.Value)))
                return "region has a non-finite number";

            if (AbsoluteBandMinZ.HasValue && AbsoluteBandMaxZ.HasValue && AbsoluteBandMaxZ.Value < AbsoluteBandMinZ.Value)
                return "region z band is upside down (max below min)";

            if (Shape == HazardShape.Circle && !(Radius > 0f))
                return "circle radius must be greater than 0";

            if (Shape == HazardShape.Rect && (!(MaxX > MinX) || !(MaxY > MinY)))
                return "rect width and height must be greater than 0";

            if (GroundMode == HazardGroundMode.Terrain && !Outdoor)
                return "terrain ground is only for outdoor landblocks; a dungeon region needs a ground plane";

            if (SpansLandblocks)
            {
                // A spanning region stays in its anchor landblock's frame and may overhang by up to one landblock
                // (-192, 384); it may not be wider than one landblock on either axis.
                if (MinX <= -LandblockLength || MinY <= -LandblockLength || MaxX >= 2f * LandblockLength || MaxY >= 2f * LandblockLength)
                    return string.Format(CultureInfo.InvariantCulture,
                        "spanning region x {0:0.##}..{1:0.##} y {2:0.##}..{3:0.##} reaches more than one landblock past anchor 0x{4:X4} (-192..384); move the centre closer to the anchor landblock",
                        MinX, MaxX, MinY, MaxY, Landblock);

                if (MaxX - MinX >= LandblockLength || MaxY - MinY >= LandblockLength)
                    return string.Format(CultureInfo.InvariantCulture,
                        "spanning region is {0:0.##} x {1:0.##} m; each side must be under 192 m",
                        MaxX - MinX, MaxY - MinY);
            }
            else if (Outdoor && (MinX < 0f || MinY < 0f || MaxX >= LandblockLength || MaxY >= LandblockLength))
                return string.Format(CultureInfo.InvariantCulture,
                    "region x {0:0.##}..{1:0.##} y {2:0.##}..{3:0.##} leaves landblock 0x{4:X4} (0..192); one landblock per run - move closer to the middle or shrink it",
                    MinX, MaxX, MinY, MaxY, Landblock);

            if (CeilingZ.HasValue && GroundMode == HazardGroundMode.Plane && CeilingZ.Value <= GroundPlaneZ)
                return "ceiling must be above the ground plane";

            return null;
        }

        /// <summary>Outdoor landblock edge length in metres (ACE.Entity.Position.BlockLength).</summary>
        public const float LandblockLength = 192f;

        private static bool AllFinite(params float[] values)
        {
            foreach (var v in values)
                if (!float.IsFinite(v))
                    return false;

            return true;
        }

        // ---- geometry ------------------------------------------------------------------------------------------

        public Vector2 Center => new Vector2(CenterX, CenterY);

        /// <summary>2D containment in the region's shape (edges inclusive).</summary>
        public bool Contains(float x, float y)
        {
            if (Shape == HazardShape.Circle)
            {
                var dx = x - CenterX;
                var dy = y - CenterY;
                return dx * dx + dy * dy <= Radius * Radius;
            }

            return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        }

        public bool InExclusion(float x, float y)
        {
            for (var i = 0; i < Exclusions.Count; i++)
                if (Exclusions[i].Contains(x, y))
                    return true;

            return false;
        }

        /// <summary>A point a hazard may be PLACED at: inside the shape and outside every exclusion.</summary>
        public bool IsPlaceable(float x, float y) => Contains(x, y) && !InExclusion(x, y);

        /// <summary>
        /// Distance from an inside point to the nearest edge (0 on the edge, negative outside). For a rect, the
        /// nearest of the four sides; for a circle, radius minus distance from the centre.
        /// </summary>
        public float DistanceToEdge(float x, float y)
        {
            if (Shape == HazardShape.Circle)
                return Radius - new Vector2(x - CenterX, y - CenterY).Length();

            var inside = Math.Min(Math.Min(x - MinX, MaxX - x), Math.Min(y - MinY, MaxY - y));
            return inside;
        }

        /// <summary>The largest circle around the centre that fits inside the shape.</summary>
        public float InscribedRadius => Shape == HazardShape.Circle ? Radius : Math.Min(MaxX - MinX, MaxY - MinY) * 0.5f;

        /// <summary>
        /// WALL CLEARANCE: <paramref name="p"/> pulled to at least <paramref name="inset"/> metres inside the edge.
        /// Rect: x and y clamped to [Min + inset, Max - inset] (the centre line when the region is narrower than
        /// 2 x inset). Circle: pulled toward the centre onto radius R - inset (the centre when R &lt;= inset). A point
        /// already far enough inside is returned unchanged; a point outside is brought inside. Non-finite -> centre.
        /// </summary>
        public Vector2 ClampInside(Vector2 p, float inset)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                return Center;

            var d = float.IsFinite(inset) ? Math.Max(0f, inset) : 0f;

            if (Shape == HazardShape.Circle)
            {
                var r = Math.Max(0f, Radius - d);
                var off = p - Center;
                var len = off.Length();

                return len <= r ? p : (len > 0f ? Center + off / len * r : Center);
            }

            var x0 = MinX + d;
            var x1 = MaxX - d;
            var y0 = MinY + d;
            var y1 = MaxY - d;

            var x = x0 <= x1 ? Math.Clamp(p.X, x0, x1) : CenterX;
            var y = y0 <= y1 ? Math.Clamp(p.Y, y0, y1) : CenterY;

            return new Vector2(x, y);
        }

        /// <summary>
        /// Ground z at (x, y): the plane, or <paramref name="terrainZ"/>(x, y) for a terrain region. A terrain
        /// region with no sampler falls back to the plane (the pure tests' path).
        /// </summary>
        public float GroundZ(float x, float y, Func<float, float, float> terrainZ = null)
            => HazardGround.GroundZ(this, x, y, terrainZ);

        /// <summary>Where a moving hazard flies at (x, y): the hazard plane, or ground + offset.</summary>
        public float HazardZ(float x, float y, Func<float, float, float> terrainZ = null)
            => HazardGround.HazardZ(this, x, y, terrainZ);

        /// <summary>
        /// Is a player at <paramref name="p"/> in the region: inside the 2D shape AND z within
        /// [GroundZ - BandBelow, GroundZ + BandAbove]. Exclusions do NOT exempt a player (no safe spots).
        /// </summary>
        public bool ContainsPlayer(Vector3 p, Func<float, float, float> terrainZ = null)
            => Contains(p.X, p.Y) && InBand(p, terrainZ);

        /// <summary>Only the z half of <see cref="ContainsPlayer"/>.</summary>
        public bool InBand(Vector3 p, Func<float, float, float> terrainZ = null)
        {
            if (AbsoluteBandMinZ.HasValue && AbsoluteBandMaxZ.HasValue)
                return p.Z >= AbsoluteBandMinZ.Value && p.Z <= AbsoluteBandMaxZ.Value;

            var ground = GroundZ(p.X, p.Y, terrainZ);
            return p.Z >= ground - BandBelow && p.Z <= ground + BandAbove;
        }

        /// <summary>
        /// A uniformly random placeable point, or false after <paramref name="attempts"/> rejections (a region
        /// that is mostly exclusion). Rejection sampling from the bounding box. <paramref name="edgeInset"/> &gt; 0 also
        /// rejects points closer than that to the region edge (effect hosts: pass ctx.EdgeInset).
        /// </summary>
        public bool TrySamplePoint(Random rng, out Vector2 point, int attempts = 64, float edgeInset = 0f)
        {
            point = default;

            if (rng == null)
                return false;

            for (var i = 0; i < attempts; i++)
            {
                var x = MinX + (float)rng.NextDouble() * (MaxX - MinX);
                var y = MinY + (float)rng.NextDouble() * (MaxY - MinY);

                if (IsPlaceable(x, y) && DistanceToEdge(x, y) >= edgeInset)
                {
                    point = new Vector2(x, y);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A point on the boundary at perimeter fraction <paramref name="t"/> in [0, 1), with the unit inward
        /// normal there. Circle: angle 2 pi t from +x. Rect: walks the perimeter counter-clockwise from (MinX, MinY).
        /// </summary>
        public Vector2 EdgePoint(double t, out Vector2 inwardNormal)
        {
            t -= Math.Floor(t);

            if (Shape == HazardShape.Circle)
            {
                var a = 2.0 * Math.PI * t;
                var dir = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
                inwardNormal = -dir;
                return Center + dir * Radius;
            }

            var w = MaxX - MinX;
            var h = MaxY - MinY;
            var d = (float)(t * 2.0 * (w + h));

            if (d < w) { inwardNormal = new Vector2(0, 1); return new Vector2(MinX + d, MinY); }
            d -= w;
            if (d < h) { inwardNormal = new Vector2(-1, 0); return new Vector2(MaxX, MinY + d); }
            d -= h;
            if (d < w) { inwardNormal = new Vector2(0, -1); return new Vector2(MaxX - d, MaxY); }
            d -= w;
            inwardNormal = new Vector2(1, 0);
            return new Vector2(MinX, MaxY - d);
        }

        public string Describe()
        {
            var shape = Shape == HazardShape.Circle
                ? string.Format(CultureInfo.InvariantCulture, "circle r={0:0.##} at ({1:0.##}, {2:0.##})", Radius, CenterX, CenterY)
                : string.Format(CultureInfo.InvariantCulture, "rect x {0:0.##}..{1:0.##} y {2:0.##}..{3:0.##}", MinX, MaxX, MinY, MaxY);

            var ground = GroundMode == HazardGroundMode.Terrain
                ? "terrain"
                : string.Format(CultureInfo.InvariantCulture, "z {0:0.##}", GroundPlaneZ);

            var hazard = HazardPlaneZ.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "plane {0:0.##}", HazardPlaneZ.Value)
                : string.Format(CultureInfo.InvariantCulture, "ground+{0:0.##}", HazardOffset);

            var ceiling = CeilingZ.HasValue ? string.Format(CultureInfo.InvariantCulture, " ceiling {0:0.##}", CeilingZ.Value) : "";

            return $"{Name} lb=0x{Landblock:X4} inst=0x{Instance:X8} {shape} ground {ground} hazard {hazard}{ceiling} excl={Exclusions.Count}";
        }
    }

    /// <summary>A shape requested on the command line: region=circle:&lt;r&gt; or region=rect:&lt;w&gt;x&lt;h&gt;.</summary>
    public readonly struct HazardShapeSpec
    {
        public HazardShape Shape { get; }
        public float Radius { get; }
        public float Width { get; }
        public float Height { get; }

        private HazardShapeSpec(HazardShape shape, float radius, float width, float height)
        {
            Shape = shape;
            Radius = radius;
            Width = width;
            Height = height;
        }

        public static HazardShapeSpec Circle(float radius) => new HazardShapeSpec(HazardShape.Circle, radius, 0, 0);

        public static HazardShapeSpec Rect(float width, float height) => new HazardShapeSpec(HazardShape.Rect, 0, width, height);

        /// <summary>Parses "circle:12" or "rect:20x30" (metres, both &gt; 0, at most 192).</summary>
        public static bool TryParse(string text, out HazardShapeSpec spec, out string error)
        {
            spec = default;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "region is empty; expected circle:<r> or rect:<w>x<h>";
                return false;
            }

            var colon = text.IndexOf(':');

            if (colon <= 0)
            {
                error = $"region '{text}' is not circle:<r> or rect:<w>x<h>";
                return false;
            }

            var kind = text.Substring(0, colon).Trim().ToLowerInvariant();
            var dims = text.Substring(colon + 1).Trim();

            if (kind == "circle")
            {
                if (!TryMetres(dims, out var r))
                {
                    error = $"circle radius '{dims}' must be a number in (0, 192]";
                    return false;
                }

                spec = Circle(r);
                return true;
            }

            if (kind == "rect")
            {
                var x = dims.IndexOfAny(new[] { 'x', 'X' });

                if (x <= 0 || !TryMetres(dims.Substring(0, x), out var w) || !TryMetres(dims.Substring(x + 1), out var h))
                {
                    error = $"rect '{dims}' must be <w>x<h>, each a number in (0, 192]";
                    return false;
                }

                spec = Rect(w, h);
                return true;
            }

            error = $"region kind '{kind}' is not circle or rect";
            return false;
        }

        private static bool TryMetres(string s, out float value)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && float.IsFinite(value) && value > 0f && value <= HazardRegion.LandblockLength;
    }
}

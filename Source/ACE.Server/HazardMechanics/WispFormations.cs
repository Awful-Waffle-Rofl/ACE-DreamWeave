using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// A region shrunk by edge_inset on every side: the area wisp formations (and so pillars) may use. Pillar hosts
    /// are pulled this far inside the region edge anyway (HazardContext.SpawnTimedEffect), so computing every formation
    /// inside the shrunk shape means the pillar the players see and the point the damage test uses are the same
    /// point. Pure geometry of a <see cref="HazardRegion"/>.
    /// </summary>
    public readonly struct WispArena
    {
        private WispArena(HazardShape shape, float minX, float maxX, float minY, float maxY, float cx, float cy, float r)
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

        public HazardShape Shape { get; }
        public float MinX { get; }
        public float MaxX { get; }
        public float MinY { get; }
        public float MaxY { get; }
        public float CenterX { get; }
        public float CenterY { get; }
        public float Radius { get; }

        public Vector2 Center => new Vector2(CenterX, CenterY);

        public static WispArena Of(HazardRegion region, float inset)
        {
            var d = float.IsFinite(inset) ? Math.Max(0f, inset) : 0f;

            if (region.Shape == HazardShape.Circle)
                return new WispArena(HazardShape.Circle, region.CenterX - region.Radius, region.CenterX + region.Radius,
                    region.CenterY - region.Radius, region.CenterY + region.Radius, region.CenterX, region.CenterY, Math.Max(0f, region.Radius - d));

            var x0 = region.MinX + d;
            var x1 = region.MaxX - d;
            var y0 = region.MinY + d;
            var y1 = region.MaxY - d;

            if (x0 > x1)
                x0 = x1 = region.CenterX;

            if (y0 > y1)
                y0 = y1 = region.CenterY;

            return new WispArena(HazardShape.Rect, x0, x1, y0, y1, region.CenterX, region.CenterY, 0f);
        }

        public float Area => Shape == HazardShape.Circle ? (float)(Math.PI * Radius * Radius) : (MaxX - MinX) * (MaxY - MinY);

        public bool Contains(float x, float y, float eps = 1e-3f)
        {
            if (Shape == HazardShape.Circle)
            {
                var dx = x - CenterX;
                var dy = y - CenterY;
                var r = Radius + eps;
                return dx * dx + dy * dy <= r * r;
            }

            return x >= MinX - eps && x <= MaxX + eps && y >= MinY - eps && y <= MaxY + eps;
        }

        /// <summary>The extent across the region from <paramref name="side"/> to the opposite side (rect: height for N/S, width for E/W; circle: the diameter).</summary>
        public float Depth(WispCompass side)
        {
            if (Shape == HazardShape.Circle)
                return 2f * Radius;

            return side == WispCompass.N || side == WispCompass.S ? MaxY - MinY : MaxX - MinX;
        }

        /// <summary>The longest line perpendicular to the axis of <paramref name="side"/>: the extent ALONG a row measured from that side.</summary>
        public float LineLength(WispCompass side)
        {
            if (Shape == HazardShape.Circle)
                return 2f * Radius;

            return side == WispCompass.N || side == WispCompass.S ? MaxX - MinX : MaxY - MinY;
        }

        /// <summary>
        /// The part of the infinite line p + t x d (d a unit vector) inside the shape, as t0..t1. False when the line misses it.
        /// </summary>
        public bool ClipLine(Vector2 p, Vector2 d, out float t0, out float t1)
        {
            t0 = 0f;
            t1 = 0f;

            if (Shape == HazardShape.Circle)
            {
                var o = p - Center;
                var b = Vector2.Dot(o, d);
                var c = Vector2.Dot(o, o) - Radius * Radius;
                var disc = b * b - c;

                if (disc < 0f)
                    return false;

                var root = (float)Math.Sqrt(disc);
                t0 = -b - root;
                t1 = -b + root;
                return t1 >= t0;
            }

            t0 = float.NegativeInfinity;
            t1 = float.PositiveInfinity;

            if (!ClipAxis(p.X, d.X, MinX, MaxX, ref t0, ref t1))
                return false;

            if (!ClipAxis(p.Y, d.Y, MinY, MaxY, ref t0, ref t1))
                return false;

            return t1 >= t0 && float.IsFinite(t0) && float.IsFinite(t1);
        }

        private static bool ClipAxis(float p, float d, float min, float max, ref float t0, ref float t1)
        {
            if (Math.Abs(d) < 1e-6f)
                return p >= min - 1e-4f && p <= max + 1e-4f;

            var a = (min - p) / d;
            var b = (max - p) / d;

            if (a > b)
                (a, b) = (b, a);

            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
            return true;
        }
    }

    /// <summary>One formation: the XY spots wisps glide to (their pillars fire at the same XY), in formation order.</summary>
    public sealed class WispFormation
    {
        public WispFormation(string pattern, IReadOnlyList<Vector2> points, string detail = null)
        {
            Pattern = pattern;
            Points = points ?? Array.Empty<Vector2>();
            Detail = detail ?? "";
        }

        public string Pattern { get; }

        public IReadOnlyList<Vector2> Points { get; }

        /// <summary>A short human description for the log (which side, which offset, which gap slot).</summary>
        public string Detail { get; }
    }

    /// <summary>One cycle: a resolved pattern (never "mixed") and its steps - one formation, or the rows of a sweep.</summary>
    public sealed class WispCyclePlan
    {
        public WispCyclePlan(string pattern, IReadOnlyList<WispFormation> steps)
        {
            Pattern = pattern;
            Steps = steps;
        }

        public string Pattern { get; }

        public IReadOnlyList<WispFormation> Steps { get; }
    }

    /// <summary>
    /// THE GEOMETRY of every wisp pattern, from the region alone (shared invariants 4 and 6): pure statics over a
    /// <see cref="HazardRegion"/>, <see cref="WispSettings"/> and a passed-in Random, so every pattern works on a rect, a
    /// circle, an ad-hoc region and an outdoor region without a world.
    ///
    /// SHARED RULES, applied by one code path (<see cref="Spread"/>) for every line-shaped pattern:
    ///   - everything stays inside the region shrunk by edge_inset (<see cref="WispArena"/>);
    ///   - a spot inside an Exclusion is dropped, never moved;
    ///   - neighbouring wisps are at least <see cref="WispSettings.MinSpacing"/> apart (pillar discs MinGap apart), so the
    ///     count is cut when the line is too short for n wisps;
    ///   - a line across a CIRCLE is the chord at that offset, a line across a RECT runs wall to wall.
    ///
    /// DIRECTION. <c>dir</c> names the side a pattern is measured from. A ROW runs west-east, so it is measured from the
    /// north (+y) or south side (dir n or s): dir=n puts it at a random offset south of the north side. A COLUMN runs
    /// north-south, so it is measured from the east (+x) or west side (dir e or w). Both are the same line helper
    /// (<see cref="Line"/>); dir=random picks a side within the pattern's own axis. A SWEEP takes all four sides: it starts at
    /// the dir side and steps towards the opposite one.
    /// </summary>
    public static class WispFormations
    {
        public static WispCompass ParseSide(string text)
        {
            switch (text?.ToLowerInvariant())
            {
                case "n": return WispCompass.N;
                case "e": return WispCompass.E;
                case "s": return WispCompass.S;
                default: return WispCompass.W;
            }
        }

        /// <summary>The unit vector pointing from <paramref name="side"/> into the region.</summary>
        public static Vector2 Inward(WispCompass side)
        {
            switch (side)
            {
                case WispCompass.N: return new Vector2(0f, -1f);
                case WispCompass.S: return new Vector2(0f, 1f);
                case WispCompass.E: return new Vector2(-1f, 0f);
                default: return new Vector2(1f, 0f);
            }
        }

        // ---- the cycle -------------------------------------------------------------------------------------

        /// <summary>
        /// The plan for one cycle of <paramref name="pattern"/> ("mixed" picks one of <see cref="WispRules.Pool"/> with
        /// <paramref name="rng"/>). Reproducible per Random seed.
        /// </summary>
        public static WispCyclePlan Plan(HazardRegion region, WispSettings s, string pattern, Random rng)
        {
            var arena = WispArena.Of(region, s.EdgeInset);
            var asked = (pattern ?? WispRules.Scatter).ToLowerInvariant();

            if (asked == WispRules.Mixed)
            {
                var pool = WispRules.Pool(region, s);
                asked = pool[rng.Next(pool.Count)];
            }

            switch (asked)
            {
                case WispRules.Row:
                {
                    var side = PickSide(s, rng, WispRules.Row);
                    return Single(asked, Row(region, arena, s, side, RandomOffset(arena, side, s.MinSpacing, rng)));
                }

                case WispRules.Column:
                {
                    var side = PickSide(s, rng, WispRules.Column);
                    return Single(asked, Column(region, arena, s, side, RandomOffset(arena, side, s.MinSpacing, rng)));
                }

                case WispRules.Diagonal:
                    return Single(asked, Diagonal(region, arena, s, rng.Next(2) == 0));

                case WispRules.Ring:
                    return Single(asked, Ring(region, arena, s, rng.NextDouble() * 2.0 * Math.PI));

                case WispRules.Sweep:
                    return new WispCyclePlan(asked, Sweep(region, arena, s, PickSide(s, rng, WispRules.Sweep), rng));

                default:
                    return Single(WispRules.Scatter, Scatter(region, arena, s, rng));
            }
        }

        private static WispCyclePlan Single(string pattern, WispFormation f) => new WispCyclePlan(pattern, new[] { f });

        /// <summary>The dir side for a pattern: a row only n or s, a column only e or w, a sweep any; a dir outside the axis (or random) is picked at random within it.</summary>
        public static WispCompass PickSide(WispSettings s, Random rng, string pattern)
        {
            if (pattern == WispRules.Row)
                return s.Dir == "n" ? WispCompass.N : s.Dir == "s" ? WispCompass.S : (rng.Next(2) == 0 ? WispCompass.N : WispCompass.S);

            if (pattern == WispRules.Column)
                return s.Dir == "e" ? WispCompass.E : s.Dir == "w" ? WispCompass.W : (rng.Next(2) == 0 ? WispCompass.E : WispCompass.W);

            return s.Dir == "random" ? (WispCompass)rng.Next(4) : ParseSide(s.Dir);
        }

        /// <summary>True when no step of the plan has a spot: nothing would fire.</summary>
        public static bool IsEmpty(WispCyclePlan plan)
        {
            foreach (var step in plan.Steps)
                if (step.Points.Count > 0)
                    return false;

            return true;
        }

        // ---- lines (row, column, diagonal, sweep rows) ----------------------------------------------------

        /// <summary>A west-east row measured from the north or south <paramref name="side"/>, <paramref name="offset"/> metres in.</summary>
        public static WispFormation Row(HazardRegion region, WispArena arena, WispSettings s, WispCompass side, double offset)
            => Line(region, arena, s, side, offset, s.Count, false, null, WispRules.Row);

        /// <summary>A north-south column measured from the east or west <paramref name="side"/>: the SAME line helper as <see cref="Row"/>.</summary>
        public static WispFormation Column(HazardRegion region, WispArena arena, WispSettings s, WispCompass side, double offset)
            => Line(region, arena, s, side, offset, s.Count, false, null, WispRules.Column);

        /// <summary>
        /// THE ONE LINE HELPER. The line is perpendicular to the axis of <paramref name="side"/>, <paramref name="offset"/>
        /// metres in from that side of the (shrunk) region, clipped to the region: wall to wall on a rect, a chord on a
        /// circle. <paramref name="wantSlots"/> equal slots are laid along it (fewer when it is too short to keep
        /// MinSpacing); with <paramref name="rng"/> and <paramref name="gapSlot"/> one random slot is left empty.
        /// </summary>
        public static WispFormation Line(HazardRegion region, WispArena arena, WispSettings s, WispCompass side, double offset, int wantSlots,
            bool gapSlot, Random rng, string pattern)
        {
            var u = Inward(side);
            var v = new Vector2(-u.Y, u.X);
            var depth = arena.Depth(side);
            var d = (float)Math.Clamp(offset, 0.0, depth);
            var origin = arena.Center + u * (-depth * 0.5f + d);

            if (!arena.ClipLine(origin, v, out var t0, out var t1))
                return new WispFormation(pattern, Array.Empty<Vector2>(), $"side={side} offset={d:0.##} misses the region");

            return Spread(region, arena, s, origin, v, t0, t1, wantSlots, gapSlot, rng, pattern, $"side={side} offset={d:0.##}");
        }

        /// <summary>
        /// A line corner to corner: through the centre (cx, cy; default the region centre) along the rect's own diagonal
        /// (width x height, either of the two), or a 45 degree chord on a circle.
        /// </summary>
        public static WispFormation Diagonal(HazardRegion region, WispArena arena, WispSettings s, bool rising)
        {
            var dir = region.Shape == HazardShape.Rect
                ? Vector2.Normalize(new Vector2(region.MaxX - region.MinX, (rising ? 1f : -1f) * (region.MaxY - region.MinY)))
                : Vector2.Normalize(new Vector2(1f, rising ? 1f : -1f));

            var centre = s.CentreFor(region);

            if (!arena.ClipLine(centre, dir, out var t0, out var t1))
                return new WispFormation(WispRules.Diagonal, Array.Empty<Vector2>(), "diagonal misses the usable region");

            return Spread(region, arena, s, centre, dir, t0, t1, s.Count, false, null, WispRules.Diagonal, rising ? "rising" : "falling");
        }

        /// <summary>
        /// The shared slot layout. Slots are equal and centred along t0..t1, at least MinSpacing wide; with a gap slot
        /// one random slot is dropped (needs at least 2 slots, otherwise the step is empty). Spots in an Exclusion are dropped.
        /// </summary>
        private static WispFormation Spread(HazardRegion region, WispArena arena, WispSettings s, Vector2 origin, Vector2 dir, float t0, float t1,
            int wantSlots, bool gapSlot, Random rng, string pattern, string detail)
        {
            var length = Math.Max(0f, t1 - t0);
            var fit = (int)Math.Floor(length / s.MinSpacing + 1e-4);
            var slots = Math.Min(Math.Max(1, wantSlots), Math.Max(1, fit));
            var gap = -1;

            if (gapSlot)
            {
                if (slots < 2)
                    return new WispFormation(pattern, Array.Empty<Vector2>(), detail + " line too short for a gap lane");

                gap = rng.Next(slots);
                detail += $" gap={gap + 1}/{slots}";
            }

            var points = new List<Vector2>(slots);
            var width = length / slots;

            for (var k = 0; k < slots; k++)
            {
                if (k == gap)
                    continue;

                var p = origin + dir * (t0 + (k + 0.5f) * width);
                Keep(region, s, points, p);
            }

            return new WispFormation(pattern, points, detail);
        }

        /// <summary>Adds <paramref name="p"/> pulled inside the region by edge_inset, unless it lands in an Exclusion.</summary>
        private static void Keep(HazardRegion region, WispSettings s, List<Vector2> points, Vector2 p)
        {
            var q = region.ClampInside(p, s.EdgeInset);

            if (!region.InExclusion(q.X, q.Y))
                points.Add(q);
        }

        /// <summary>
        /// A random offset from <paramref name="side"/> whose line is at least <paramref name="minChord"/> long (a circle's
        /// chords shrink to nothing at the rim). Falls back to the middle after 16 tries.
        /// </summary>
        public static double RandomOffset(WispArena arena, WispCompass side, float minChord, Random rng)
        {
            var depth = arena.Depth(side);
            var u = Inward(side);
            var v = new Vector2(-u.Y, u.X);

            for (var i = 0; i < 16; i++)
            {
                var d = rng.NextDouble() * depth;
                var origin = arena.Center + u * (float)(-depth * 0.5 + d);

                if (arena.ClipLine(origin, v, out var t0, out var t1) && t1 - t0 >= minChord)
                    return d;
            }

            return depth * 0.5;
        }

        // ---- ring --------------------------------------------------------------------------------------------

        /// <summary>
        /// Wisps evenly spaced on a circle of ring_radius (default half the inscribed radius) round (cx, cy). The count is cut
        /// until neighbouring discs are MinGap apart; <paramref name="phase"/> turns the whole ring.
        /// </summary>
        public static WispFormation Ring(HazardRegion region, WispArena arena, WispSettings s, double phase)
        {
            var centre = s.CentreFor(region);
            var rr = s.RingRadiusFor(region);
            var count = Math.Max(1, Math.Min(s.Count, WispRules.RingCapacity(rr, s.MinSpacing)));
            var points = new List<Vector2>(count);

            for (var k = 0; k < count; k++)
            {
                var a = phase + 2.0 * Math.PI * k / count;
                Keep(region, s, points, centre + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * rr);
            }

            return new WispFormation(WispRules.Ring, points, string.Format(CultureInfo.InvariantCulture, "centre=({0:0.##},{1:0.##}) r={2:0.##} k={3}", centre.X, centre.Y, rr, count));
        }

        // ---- scatter -----------------------------------------------------------------------------------------

        /// <summary>
        /// Up to n random placeable spots, every pair at least MinSpacing (>= 2 x pillar_radius) apart, at most
        /// <see cref="WispRules.ScatterLimit"/> of them (pillars cover at most scatter_cover of the usable area). Fewer
        /// when the region cannot take n.
        /// </summary>
        public static WispFormation Scatter(HazardRegion region, WispArena arena, WispSettings s, Random rng)
        {
            var target = Math.Min(s.Count, Math.Max(1, WispRules.ScatterLimit(region, s)));
            var points = new List<Vector2>(target);
            var spacing2 = s.MinSpacing * s.MinSpacing;

            for (var attempt = 0; attempt < 600 && points.Count < target; attempt++)
            {
                if (!region.TrySamplePoint(rng, out var p, 16, s.EdgeInset))
                    continue;

                var ok = true;

                foreach (var q in points)
                {
                    if (Vector2.DistanceSquared(p, q) < spacing2)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                    points.Add(p);
            }

            return new WispFormation(WispRules.Scatter, points, $"{points.Count}/{target} spots");
        }

        // ---- sweep -------------------------------------------------------------------------------------------

        /// <summary>
        /// A row that steps across the region from <paramref name="side"/>: <c>steps</c> rows (auto: enough to tile the depth
        /// with bands of 2 x pillar_radius), each one random gap slot short of a full row. A step whose line is too short for
        /// a gap lane is empty (the mechanic skips it).
        /// </summary>
        public static IReadOnlyList<WispFormation> Sweep(HazardRegion region, WispArena arena, WispSettings s, WispCompass side, Random rng)
        {
            var depth = arena.Depth(side);
            var steps = SweepStepCount(arena, s, side);

            var rows = new List<WispFormation>(steps);

            for (var k = 0; k < steps; k++)
            {
                var offset = (k + 0.5) * depth / steps;
                var row = Line(region, arena, s, side, offset, s.Count + 1, true, rng, WispRules.Sweep);
                rows.Add(new WispFormation(WispRules.Sweep, row.Points, $"step {k + 1}/{steps} {row.Detail}"));
            }

            return rows;
        }

        /// <summary>The rows of a sweep from <paramref name="side"/>: <c>steps</c>, or enough bands of 2 x pillar_radius to tile the depth (at most 40).</summary>
        public static int SweepStepCount(WispArena arena, WispSettings s, WispCompass side)
        {
            var steps = s.Steps > 0 ? s.Steps : (int)Math.Ceiling(arena.Depth(side) / Math.Max(0.5f, 2f * s.PillarRadius));
            return Math.Clamp(steps, 1, 40);
        }

        // ---- where the wisps start ---------------------------------------------------------------------------

        /// <summary>
        /// Spawn spots for <paramref name="count"/> wisps: spread round the region edge (pulled in by the inset), each moved
        /// to a random placeable open spot when <paramref name="open"/> (a cell exists at wisp height) refuses it. Fewer when
        /// no open spot is found.
        /// </summary>
        public static IReadOnlyList<Vector2> SpawnPoints(HazardRegion region, int count, float inset, Random rng, Func<Vector2, bool> open)
        {
            var points = new List<Vector2>(count);

            for (var i = 0; i < count; i++)
            {
                var edge = region.ClampInside(region.EdgePoint((i + 0.5) / count, out _), inset);

                if (region.IsPlaceable(edge.X, edge.Y) && open(edge))
                {
                    points.Add(edge);
                    continue;
                }

                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (region.TrySamplePoint(rng, out var p, 8, inset) && open(p))
                    {
                        points.Add(p);
                        break;
                    }
                }
            }

            return points;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Pure geometry of the memory game: where the four pads stand, the ring of marker weenies round each, and where
    /// the playback beams may land. Everything comes from a <see cref="HazardRegion"/> (shared invariant 4) and a
    /// passed-in Random (shared invariant 6); no world, no PropertyManager. The ring follows the idea of the King of
    /// the Hill zone markers (BattlegroundZoneMarkers) but is implemented here so the hazard library takes no
    /// dependency on the Pvp namespace.
    /// </summary>
    public static class MemoryLayout
    {
        /// <summary>The fewest markers a pad ring holds, however wide the spacing.</summary>
        public const int MinRingMarkers = 8;

        /// <summary>The most markers a pad ring holds, however small the spacing.</summary>
        public const int MaxRingMarkers = 32;

        /// <summary>Playback beams keep at least this far OUTSIDE a pad's rim.</summary>
        public const float BeamClearance = 1.0f;

        /// <summary>Points on a pad rim sampled when checking the whole pad is placeable.</summary>
        private const int RimSamples = 16;

        /// <summary>Grid step of the deterministic first-fit search.</summary>
        private const float GridStep = 0.5f;

        private const float Epsilon = 1e-3f;

        // ---- the ring --------------------------------------------------------------------------------------------

        /// <summary>N = clamp(ceil(2 pi r / spacing), 8, 32): the markers on one pad's rim.</summary>
        public static int RingCount(float radius, float spacing)
        {
            if (!float.IsFinite(radius) || !float.IsFinite(spacing) || !(spacing > 0f) || !(radius > 0f))
                return MinRingMarkers;

            var raw = Math.Ceiling(2.0 * Math.PI * radius / spacing);

            if (raw <= MinRingMarkers)
                return MinRingMarkers;

            return raw >= MaxRingMarkers ? MaxRingMarkers : (int)raw;
        }

        /// <summary>Marker i at angle 2 pi i / count from +x, every one exactly <paramref name="radius"/> from the centre.</summary>
        public static IReadOnlyList<Vector2> RingPoints(Vector2 centre, float radius, int count)
        {
            var points = new List<Vector2>(Math.Max(0, count));

            for (var i = 0; i < count; i++)
            {
                var a = 2.0 * Math.PI * i / count;
                points.Add(new Vector2(centre.X + radius * (float)Math.Cos(a), centre.Y + radius * (float)Math.Sin(a)));
            }

            return points;
        }

        // ---- the pads --------------------------------------------------------------------------------------------

        /// <summary>
        /// The default pad centres, in colour order (red, blue, green, purple). Rect: the centres of its four
        /// quarters. Circle: four points at half the radius, 90 degrees apart, at 45, 135, 225 and 315 degrees
        /// (the centre of each quadrant).
        /// </summary>
        public static IReadOnlyList<Vector2> QuadrantCentres(HazardRegion region)
        {
            var list = new List<Vector2>(MemoryRules.PadCount);

            if (region.Shape == HazardShape.Circle)
            {
                var r = region.Radius * 0.5f;

                for (var k = 0; k < MemoryRules.PadCount; k++)
                {
                    var a = (45.0 + 90.0 * k) * Math.PI / 180.0;
                    list.Add(new Vector2(region.CenterX + r * (float)Math.Cos(a), region.CenterY + r * (float)Math.Sin(a)));
                }

                return list;
            }

            var w = region.MaxX - region.MinX;
            var h = region.MaxY - region.MinY;

            list.Add(new Vector2(region.MinX + w * 0.25f, region.MinY + h * 0.25f));
            list.Add(new Vector2(region.MinX + w * 0.75f, region.MinY + h * 0.25f));
            list.Add(new Vector2(region.MinX + w * 0.75f, region.MinY + h * 0.75f));
            list.Add(new Vector2(region.MinX + w * 0.25f, region.MinY + h * 0.75f));
            return list;
        }

        /// <summary>
        /// Is the whole pad (its centre and its rim) placeable, and its rim at least <paramref name="inset"/> inside
        /// the region edge? <paramref name="cellOk"/> (live: ctx.HasCell on the ground point, raised) is the
        /// world's say: the centre must have a cell and at least half the rim samples must, so a pad over solid
        /// geometry is refused instead of being invisible but live. Null: the region alone decides (Validate).
        /// </summary>
        public static bool PadFits(HazardRegion region, Vector2 centre, float padRadius, float inset, Func<Vector2, bool> cellOk = null)
        {
            if (!region.IsPlaceable(centre.X, centre.Y) || region.DistanceToEdge(centre.X, centre.Y) < padRadius + inset - Epsilon)
                return false;

            var rimWithCell = 0;

            foreach (var p in RingPoints(centre, padRadius, RimSamples))
            {
                if (!region.IsPlaceable(p.X, p.Y))
                    return false;

                if (cellOk != null && cellOk(p))
                    rimWithCell++;
            }

            return cellOk == null || (cellOk(centre) && rimWithCell >= RimSamples / 2);
        }

        /// <summary>
        /// Null when <paramref name="pads"/> are four acceptable pads, else why not: each must be placeable with its
        /// rim inset from the edge, and every two centres at least <paramref name="padGap"/> apart.
        /// </summary>
        public static string CheckPads(HazardRegion region, IReadOnlyList<Vector2> pads, float padRadius, float padGap, float inset, Func<Vector2, bool> cellOk = null)
        {
            if (pads == null || pads.Count != MemoryRules.PadCount)
                return "the memory game needs four pads";

            for (var i = 0; i < pads.Count; i++)
            {
                if (!PadFits(region, pads[i], padRadius, inset, cellOk))
                    return $"pad {i + 1} ({MemoryRules.ColourName(i)}) at ({pads[i].X:0.#}, {pads[i].Y:0.#}) does not fit: it must be placeable (not in an exclusion) and its rim {inset:0.#} m inside the region edge";

                for (var j = 0; j < i; j++)
                    if (Vector2.Distance(pads[i], pads[j]) < padGap - Epsilon)
                        return $"pads {j + 1} and {i + 1} are {Vector2.Distance(pads[i], pads[j]):0.##} m apart, under pad_gap={padGap:0.##}";
            }

            return null;
        }

        /// <summary>
        /// Four random placeable pads honouring the gap and the edge inset, or null when the tries run out. Uses
        /// <paramref name="rng"/> only.
        /// </summary>
        public static IReadOnlyList<Vector2> RandomPads(HazardRegion region, Random rng, float padRadius, float padGap, float inset, Func<Vector2, bool> cellOk = null)
        {
            for (var round = 0; round < 40; round++)
            {
                var pads = new List<Vector2>(MemoryRules.PadCount);

                while (pads.Count < MemoryRules.PadCount)
                {
                    var placed = false;

                    for (var attempt = 0; attempt < 80 && !placed; attempt++)
                    {
                        if (!region.TrySamplePoint(rng, out var p, 1, padRadius + inset) || !PadFits(region, p, padRadius, inset, cellOk))
                            continue;

                        var apart = true;

                        foreach (var q in pads)
                            if (Vector2.Distance(p, q) < padGap - Epsilon)
                                apart = false;

                        if (!apart)
                            continue;

                        pads.Add(p);
                        placed = true;
                    }

                    if (!placed)
                        break;
                }

                if (pads.Count == MemoryRules.PadCount)
                    return pads;
            }

            return null;
        }

        /// <summary>
        /// A deterministic fallback: scan the region on a 0.5 m grid for points where a whole pad fits, take the
        /// first, then repeatedly the one farthest from those already taken (at least padGap from each). Null when
        /// four such pads do not exist.
        /// </summary>
        public static IReadOnlyList<Vector2> FirstFitPads(HazardRegion region, float padRadius, float padGap, float inset, Func<Vector2, bool> cellOk = null)
        {
            var candidates = new List<Vector2>();
            var nx = (int)Math.Floor((region.MaxX - region.MinX) / GridStep);
            var ny = (int)Math.Floor((region.MaxY - region.MinY) / GridStep);

            for (var ix = 0; ix <= nx; ix++)
            {
                for (var iy = 0; iy <= ny; iy++)
                {
                    var p = new Vector2(region.MinX + ix * GridStep, region.MinY + iy * GridStep);

                    if (PadFits(region, p, padRadius, inset, cellOk))
                        candidates.Add(p);
                }
            }

            if (candidates.Count == 0)
                return null;

            var chosen = new List<Vector2> { candidates[0] };

            while (chosen.Count < MemoryRules.PadCount)
            {
                var best = -1;
                var bestScore = -1f;

                for (var i = 0; i < candidates.Count; i++)
                {
                    var nearest = float.MaxValue;

                    foreach (var c in chosen)
                        nearest = Math.Min(nearest, Vector2.Distance(candidates[i], c));

                    if (nearest >= padGap - Epsilon && nearest > bestScore)
                    {
                        best = i;
                        bestScore = nearest;
                    }
                }

                if (best < 0)
                    return null;

                chosen.Add(candidates[best]);
            }

            return chosen;
        }

        /// <summary>
        /// The pads a run uses. layout=quadrant: the quadrant centres when they fit, else null (the run is refused
        /// rather than silently moved). layout=random: four random pads, else the quadrant centres when they fit,
        /// else <see cref="FirstFitPads"/> - the SAME chain <see cref="Feasible"/> promises, so a region Validate
        /// accepts never fails here. <paramref name="cellOk"/> adds the world's cell test (see <see cref="PadFits"/>).
        /// </summary>
        public static IReadOnlyList<Vector2> Plan(HazardRegion region, bool random, Random rng, float padRadius, float padGap, float inset, Func<Vector2, bool> cellOk = null)
        {
            var quadrants = QuadrantCentres(region);
            var quadrantsFit = CheckPads(region, quadrants, padRadius, padGap, inset, cellOk) == null;

            if (!random)
                return quadrantsFit ? quadrants : null;

            return RandomPads(region, rng, padRadius, padGap, inset, cellOk)
                ?? (quadrantsFit ? quadrants : null)
                ?? FirstFitPads(region, padRadius, padGap, inset, cellOk);
        }

        /// <summary>
        /// The pure pre-flight Validate uses: null when <see cref="Plan"/> can succeed for this region, else a
        /// one-line reason. Feasible when the quadrants fit (random may also use first-fit), AND the playback beams
        /// have somewhere to land off the pads (<see cref="HasBeamRoom"/>).
        /// </summary>
        public static string Feasible(HazardRegion region, bool random, float padRadius, float padGap, float inset)
        {
            var quadrants = QuadrantCentres(region);
            var quadrantError = CheckPads(region, quadrants, padRadius, padGap, inset);
            IReadOnlyList<Vector2> pads = quadrants;

            if (quadrantError != null)
            {
                if (!random)
                    return $"region is too small (or too cluttered) for four pads at pad_radius={padRadius:0.##} pad_gap={padGap:0.##} edge_inset={inset:0.#}: {quadrantError}; enlarge the region, shrink the pads or try layout=random";

                pads = FirstFitPads(region, padRadius, padGap, inset);

                if (pads == null)
                    return $"region is too small (or too cluttered) to fit four pads at pad_radius={padRadius:0.##} pad_gap={padGap:0.##} edge_inset={inset:0.#}";
            }

            return HasBeamRoom(region, pads, padRadius, inset)
                ? null
                : "region has no room for the playback beams off the pads (every placeable point is on a pad or too close to the edge)";
        }

        /// <summary>
        /// Is there at least one point where a beam may land (placeable, <paramref name="inset"/> from the edge,
        /// further than pad radius + <see cref="BeamClearance"/> from every pad)? A 0.5 m grid scan, pure.
        /// </summary>
        public static bool HasBeamRoom(HazardRegion region, IReadOnlyList<Vector2> pads, float padRadius, float inset)
        {
            var clear = padRadius + BeamClearance;
            var nx = (int)Math.Floor((region.MaxX - region.MinX) / GridStep);
            var ny = (int)Math.Floor((region.MaxY - region.MinY) / GridStep);

            for (var ix = 0; ix <= nx; ix++)
            {
                for (var iy = 0; iy <= ny; iy++)
                {
                    var p = new Vector2(region.MinX + ix * GridStep, region.MinY + iy * GridStep);

                    if (!region.IsPlaceable(p.X, p.Y) || region.DistanceToEdge(p.X, p.Y) < inset - Epsilon)
                        continue;

                    var onPad = false;

                    foreach (var pad in pads)
                        if (Vector2.Distance(p, pad) <= clear)
                            onPad = true;

                    if (!onPad)
                        return true;
                }
            }

            return false;
        }

        // ---- playback beams --------------------------------------------------------------------------------------

        /// <summary>
        /// One random placeable point for a playback beam: inside the region (edge_inset from the edge), outside every
        /// exclusion, and not on a pad (further than pad radius + <see cref="BeamClearance"/> from every pad centre).
        /// False when the tries run out.
        /// </summary>
        public static bool TryBeamPoint(HazardRegion region, Random rng, IReadOnlyList<Vector2> pads, float padRadius, float inset, out Vector2 point)
        {
            var clear = padRadius + BeamClearance;

            for (var attempt = 0; attempt < 64; attempt++)
            {
                if (!region.TrySamplePoint(rng, out var p, 1, inset))
                    continue;

                var onPad = false;

                foreach (var pad in pads)
                    if (Vector2.Distance(p, pad) <= clear)
                        onPad = true;

                if (onPad)
                    continue;

                point = p;
                return true;
            }

            point = default;
            return false;
        }

        /// <summary>
        /// One beam point the WORLD accepts: up to <paramref name="candidates"/> region-valid candidates
        /// (<see cref="TryBeamPoint"/>) are offered to <paramref name="cellOk"/> (live: a cell holds the point) and
        /// the first accepted one wins. False when none is accepted, so a beam is never aimed at a solid and
        /// silently dropped. Null cellOk accepts the first candidate.
        /// </summary>
        public static bool PickBeamPoint(HazardRegion region, Random rng, IReadOnlyList<Vector2> pads, float padRadius, float inset,
            Func<Vector2, bool> cellOk, out Vector2 point, int candidates = 8)
        {
            for (var i = 0; i < candidates; i++)
            {
                if (!TryBeamPoint(region, rng, pads, padRadius, inset, out var p))
                    continue;

                if (cellOk == null || cellOk(p))
                {
                    point = p;
                    return true;
                }
            }

            point = default;
            return false;
        }

        /// <summary>
        /// beam_corners: one beam point per corner of a Rect region (ZoneRules.RegionCorners order), each clamped
        /// <paramref name="inset"/> inside the edge exactly as ZonesMechanic.PillarPoints places the zone pillars, and
        /// skipped when it lies on a pad (within pad radius + <see cref="BeamClearance"/>). Empty for any other shape. Pure.
        /// </summary>
        public static IReadOnlyList<Vector2> CornerBeamPoints(HazardRegion region, IReadOnlyList<Vector2> pads, float padRadius, float inset)
        {
            var points = new List<Vector2>();
            var clear = padRadius + BeamClearance;

            foreach (var corner in ZoneRules.RegionCorners(region))
            {
                var xy = region.ClampInside(corner, inset);
                var onPad = false;

                if (pads != null)
                    foreach (var pad in pads)
                        if (Vector2.Distance(xy, pad) <= clear)
                            onPad = true;

                if (!onPad)
                    points.Add(xy);
            }

            return points;
        }

        // ---- ceiling beams -----------------------------------------------------------------------------------------

        /// <summary>
        /// A ceiling beam's host hangs this far BELOW the ceiling face: its origin must still resolve to a cell
        /// (a point on or above the face is outside every cell), and the emitter is not clipped by the ceiling.
        /// </summary>
        public const float CeilingBeamOffset = 0.3f;

        /// <summary>The host position of a beam fired from the ceiling at (x, y): just under <paramref name="ceilingZ"/>.</summary>
        public static Vector3 CeilingBeamPoint(Vector2 xy, float ceilingZ) => new Vector3(xy.X, xy.Y, ceilingZ - CeilingBeamOffset);

        /// <summary>
        /// The beam host's orientation: identity, or turned 180 degrees about the horizontal x axis when
        /// <paramref name="flip"/>. MEASURED (client_portal.dat, 2026-10-18): the beam is a single quad particle mesh
        /// (GfxObjs 0x0100195E-61, vertices z -5.0 .. 0.0, x -0.4..0.5, y -0.5..0.5) whose emitters have zero offset and
        /// zero velocity and IsParentLocal set, so the mesh hangs 5 m BELOW the host origin at scale 1 (up to 8 m at
        /// the 1.6 final scale) and follows the host's rotation. A flip therefore makes it rise ABOVE the host.
        /// </summary>
        public static Quaternion BeamOrientation(bool flip)
            => flip ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Math.PI) : Quaternion.Identity;

        /// <summary>
        /// Whether a beam host is turned, from where it stands and the owner's toggle. The mesh hangs BELOW its origin, so
        /// a ceiling host stays unturned (the beam falls from the ceiling) and a floor host (floor beams, the echo on a
        /// pad) is turned so the beam RISES from the floor. <paramref name="swap"/> (beam_flip) inverts that choice.
        /// </summary>
        public static bool BeamTurned(bool onCeiling, bool swap) => (!onCeiling) != swap;

        /// <summary>
        /// The orientation of a host standing on the FLOOR with <paramref name="scriptId"/>: turned for a beam (its mesh
        /// hangs below the host, HazardDefaults.HangsBelowHost) so it rises; identity for any other effect.
        /// </summary>
        public static Quaternion FloorOrientation(uint scriptId) => BeamOrientation(HazardDefaults.HangsBelowHost(scriptId));
    }
}

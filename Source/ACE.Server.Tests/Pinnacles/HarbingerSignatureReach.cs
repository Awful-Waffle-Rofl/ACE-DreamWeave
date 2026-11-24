using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The reach checks of the alcove signatures (round 2 B2), shared by the pure model test (HarbingerSignatureTests) and the
    /// dat test on the real floor (HarbingerAlcoveRegionTests). Each answers "can this signature, run with these args, hit a
    /// player standing at this point", from the same rules the mechanics use: a ball hits within ball radius + player radius
    /// of its path (BallParams.Reach), a hailstone's burst within burst radius + player radius of its impact (RainDirector,
    /// HazardHitRules.InReach), a pool everyone standing inside its area (FillDirector over the run's region).
    /// </summary>
    internal static class HarbingerSignatureReach
    {
        /// <summary>
        /// A player's body radius: the player setup 0x02000001's spheres are r 0.48 (code review of f60469ed9). A standing
        /// player's centre is at least this far from a wall. The hit test's own player allowance stays the library's 0.5
        /// (HazardDefaults.PlayerRadius, BallParams.Reach).
        /// </summary>
        public const float PlayerDisc = 0.48f;

        /// <summary>
        /// The measured alcove floor as a model, in the alcove's own frame (u outward, v along HarbingerLayout.Perp, metres from
        /// the centre): the floor octagon of apothem 4.5 (HazardRegionPresets.HarbingerAlcoveWallDistance) and the doorway sill
        /// (u -4.5 to -5.0, 3.33 m wide), shrunk by <paramref name="inset"/> at every wall (the sill's open edge too).
        /// </summary>
        public static bool OnFloorModel(float u, float v, float inset = 0f)
        {
            var a = HazardRegionPresets.HarbingerAlcoveWallDistance - inset;
            var octagon = Math.Abs(u) <= a && Math.Abs(v) <= a && Math.Abs(u) + Math.Abs(v) <= a * MathF.Sqrt(2f);
            var sill = u >= -5.0f + inset && u <= -HazardRegionPresets.HarbingerAlcoveWallDistance && Math.Abs(v) <= HazardRegionPresets.HarbingerAlcoveDoorwayWidth * 0.5f - inset;

            return octagon || sill;
        }

        /// <summary>The floor model's points in landblock x, y on a <paramref name="step"/> grid.</summary>
        public static IEnumerable<Vector2> FloorModel(HarbingerAlcove alcove, float inset = 0f, float step = 0.1f)
        {
            var perp = HarbingerLayout.Perp(alcove.Outward);

            for (var u = -5.2f; u <= 5.2f + 1e-4f; u += step)
                for (var v = -5.2f; v <= 5.2f + 1e-4f; v += step)
                    if (OnFloorModel(u, v, inset))
                        yield return alcove.Centre + u * alcove.Outward + v * perp;
        }

        /// <summary>
        /// Every lane Ember roll may draw (BallPatterns.ClearLanes on both sides of the run's axis, each clear interval
        /// sampled every BallPatterns.ClearLaneStep m and at its end), as path segments.
        /// </summary>
        public static List<(Vector2 A, Vector2 B)> EmberLanes(HazardRegion r, BallParams p)
        {
            var lanes = new List<(Vector2, Vector2)>();

            foreach (var side in BallPatterns.AxisSides(p.Axis))
            {
                foreach (var (lo, hi) in BallPatterns.ClearLanes(r, side, p))
                {
                    for (var lat = lo; ; lat = Math.Min(hi, lat + BallPatterns.ClearLaneStep))
                    {
                        if (BallPatterns.RawLane(r, side, lat, p, out var a, out var b))
                            lanes.Add((a, b));

                        if (lat >= hi)
                            break;
                    }
                }
            }

            return lanes;
        }

        /// <summary>Ember roll: some lane of <paramref name="lanes"/> passes within <paramref name="reach"/> (BallParams.Reach) of <paramref name="pt"/>.</summary>
        public static bool EmberHits(IReadOnlyList<(Vector2 A, Vector2 B)> lanes, float reach, Vector2 pt)
            => lanes.Any(l => SegmentDistance(pt, l.A, l.B) <= reach + 1e-4f);

        /// <summary>Whiteout: the nearest impact the run may draw (anywhere edge_inset inside the circle) bursts within reach of <paramref name="pt"/>.</summary>
        public static bool WhiteoutHits(HazardRegion r, RainSettings s, Vector2 pt)
        {
            var nearest = Math.Max(0f, Vector2.Distance(pt, r.Center) - (r.Radius - s.EdgeInset));

            return nearest <= s.BurstRadius + s.PlayerRadius + 1e-4;
        }

        /// <summary>A pool at some slot of <paramref name="alcove"/> covers <paramref name="pt"/>.</summary>
        public static bool PoolCovers(HarbingerAlcove alcove, double size, Vector2 pt)
            => Enumerable.Range(0, HarbingerLayout.PoolSlotsUV.Count)
                .Select(i => HarbingerLayout.PoolArea(alcove, i, size))
                .Any(a => pt.X >= a.MinX - 1e-4f && pt.X <= a.MaxX + 1e-4f && pt.Y >= a.MinY - 1e-4f && pt.Y <= a.MaxY + 1e-4f);

        public static float SegmentDistance(Vector2 pt, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();
            var t = len2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(pt - a, ab) / len2, 0f, 1f);

            return Vector2.Distance(pt, a + t * ab);
        }

        /// <summary>The hazard region preset of <paramref name="element"/>'s alcove (instance 0).</summary>
        public static HazardRegion Preset(HarbingerElement element)
        {
            HazardRegionPresets.TryGet(HarbingerLayout.RegionPreset(HarbingerData.AlcoveOf(element)), 0, out var r);
            return r;
        }
    }
}

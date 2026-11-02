using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The footprint plus its one-landblock buffer ring (R15: the backstop zone starts off this set).</summary>
    public static class OutdoorFootprintBuffer
    {
        /// <summary>
        /// True when <paramref name="landblock"/> (0xXXYY) is a footprint member or one of its eight neighbours.
        /// The buffer is the ring of bare-terrain landblocks around the site: loaded up front by the footprint's adjacency
        /// loading, or on demand under dynamic_dungeons_outdoor_lazy_buffer_ring. This test is geometric and holds either way.
        /// </summary>
        public static bool Contains(ushort landblock, IReadOnlyCollection<ushort> footprint)
        {
            if (footprint == null)
                return false;

            var x = landblock >> 8;
            var y = landblock & 0xFF;

            foreach (var member in footprint)
            {
                if (Math.Abs((member >> 8) - x) <= 1 && Math.Abs((member & 0xFF) - y) <= 1)
                    return true;
            }

            return false;
        }
    }

    /// <summary>One wisp position: the landblock it lands on, the local x,y there, and the same point in global metres.</summary>
    public readonly record struct RingPoint(ushort Landblock, double X, double Y, double Gx, double Gy);

    /// <summary>The result of <see cref="OutdoorWispRingPlan.Build"/>. <see cref="Points"/> are the placeable ones.</summary>
    public sealed class RingPlan
    {
        public RingPlan(int total, List<RingPoint> points, int skippedOutsideFootprint, int skippedOffGrid)
        {
            Total = total;
            Points = points;
            SkippedOutsideFootprint = skippedOutsideFootprint;
            SkippedOffGrid = skippedOffGrid;
        }

        /// <summary>How many points the circle was divided into.</summary>
        public int Total { get; }

        public List<RingPoint> Points { get; }

        /// <summary>Points that land on a landblock outside the footprint plus buffer (logged, not placed).</summary>
        public int SkippedOutsideFootprint { get; }

        /// <summary>Points that fall off the 256 x 256 landblock grid.</summary>
        public int SkippedOffGrid { get; }

        /// <summary>The placeable points grouped by landblock, in first-seen order (each group is created on that landblock's own queue).</summary>
        public List<KeyValuePair<ushort, List<RingPoint>>> ByLandblock()
        {
            var order = new List<ushort>();
            var groups = new Dictionary<ushort, List<RingPoint>>();

            foreach (var p in Points)
            {
                if (!groups.TryGetValue(p.Landblock, out var list))
                {
                    groups[p.Landblock] = list = new List<RingPoint>();
                    order.Add(p.Landblock);
                }

                list.Add(p);
            }

            var result = new List<KeyValuePair<ushort, List<RingPoint>>>(order.Count);
            foreach (var lb in order)
                result.Add(new KeyValuePair<ushort, List<RingPoint>>(lb, groups[lb]));

            return result;
        }
    }

    /// <summary>
    /// Pure planning for the Unraveling wisp ring (design R21): points on the perimeter circle every
    /// <c>ring_spacing_m</c> metres, in global outdoor metres (R4), grouped by landblock. No world access.
    /// The WorldEventArena ring helper is not used for the plan: it clamps the count to a small maximum and works
    /// from a Position centre, while a 100 m circle at 8 m spacing needs about 79 points in global metres.
    /// </summary>
    public static class OutdoorWispRingPlan
    {
        /// <summary>Spacing floor in metres, so a misconfigured tunable cannot ask for thousands of objects.</summary>
        public const double MinSpacingM = 2.0;

        /// <summary>Hard cap on points per ring.</summary>
        public const int MaxPoints = 400;

        /// <summary>ceil(2 pi R / spacing), spacing floored at <see cref="MinSpacingM"/>, capped at <see cref="MaxPoints"/>; 0 for a bad radius or spacing.</summary>
        public static int PointCount(double radius, double spacingM)
        {
            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0 || double.IsNaN(spacingM) || double.IsInfinity(spacingM) || spacingM <= 0)
                return 0;

            var raw = Math.Ceiling(2.0 * Math.PI * radius / Math.Max(spacingM, MinSpacingM));
            return (int)Math.Min(MaxPoints, Math.Max(1.0, raw));
        }

        /// <summary>Marker i sits at angle 2 pi i / N from +x on the circle. Points on a landblock outside footprint plus buffer, or off the grid, are skipped and counted.</summary>
        public static RingPlan Build(OutdoorCircleDef circle, IReadOnlyCollection<ushort> footprint, double spacingM)
        {
            var count = circle == null ? 0 : PointCount(circle.Radius, spacingM);
            var points = new List<RingPoint>(count);
            var outside = 0;
            var offGrid = 0;

            if (count > 0)
            {
                var centre = OutdoorSiteGeometry.Centre(circle);

                for (var i = 0; i < count; i++)
                {
                    var theta = 2.0 * Math.PI * i / count;
                    var gx = centre.Gx + circle.Radius * Math.Cos(theta);
                    var gy = centre.Gy + circle.Radius * Math.Sin(theta);

                    if (!OutdoorSiteGeometry.TryToLocal(gx, gy, out var pos))
                    {
                        offGrid++;
                        continue;
                    }

                    if (!OutdoorFootprintBuffer.Contains(pos.Landblock, footprint))
                    {
                        outside++;
                        continue;
                    }

                    points.Add(new RingPoint(pos.Landblock, pos.X, pos.Y, gx, gy));
                }
            }

            return new RingPlan(count, points, outside, offGrid);
        }

        /// <summary>
        /// Indices into <paramref name="gx"/>/<paramref name="gy"/> of the (up to) <paramref name="take"/> markers nearest the
        /// player, nearest first; ties keep the lower index. Horizontal distance (R4).
        /// </summary>
        public static List<int> NearestIndices(IReadOnlyList<(double Gx, double Gy)> markers, double playerGx, double playerGy, int take)
        {
            var result = new List<int>();
            if (markers == null || take <= 0)
                return result;

            var order = new List<(double D, int I)>(markers.Count);
            for (var i = 0; i < markers.Count; i++)
            {
                var dx = markers[i].Gx - playerGx;
                var dy = markers[i].Gy - playerGy;
                order.Add((dx * dx + dy * dy, i));
            }

            order.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.I.CompareTo(b.I));

            for (var k = 0; k < order.Count && k < take; k++)
                result.Add(order[k].I);

            return result;
        }
    }
}

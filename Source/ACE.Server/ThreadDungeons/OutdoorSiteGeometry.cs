using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A position on the outdoor grid in the same terms the dungeon files use: a landblock plus local x,y
    /// (metres from that landblock's south-west corner). Immutable.
    /// </summary>
    public readonly struct OutdoorPos
    {
        public readonly ushort Landblock;
        public readonly double X;
        public readonly double Y;

        public OutdoorPos(ushort landblock, double x, double y) { Landblock = landblock; X = x; Y = y; }

        /// <summary>Global outdoor metres, east. See <see cref="OutdoorSiteGeometry"/> for the frame.</summary>
        public double Gx => OutdoorSiteGeometry.ToGlobalX(Landblock, X);

        /// <summary>Global outdoor metres, north.</summary>
        public double Gy => OutdoorSiteGeometry.ToGlobalY(Landblock, Y);
    }

    /// <summary>
    /// Pure geometry for outdoor sites. No PropertyManager, no world access.
    ///
    /// COORDINATE FRAME. A landblock id is 0xXXYY: XX = landblock >> 8 increases EAST, YY = landblock &amp; 0xFF
    /// increases NORTH (ACE.Entity LandblockId: East = X+1, North = Y+1; Position.BlockLength = 192, and
    /// Position.AdjustMapCoords rolls PositionX/PositionY over into the neighbouring X/Y block). A local
    /// (x, y) is metres from the landblock's south-west corner, 0..192. Global outdoor metres are
    ///   gx = XX * 192 + x,   gy = YY * 192 + y
    /// so a circle or polygon may straddle a landblock seam with no special casing. Local coordinates are
    /// NOT normalised: a local x of 200 on landblock 0x5456 is the same global point as x = 8 on 0x5556.
    /// </summary>
    public static class OutdoorSiteGeometry
    {
        public const double BlockLength = 192.0;

        public static double ToGlobalX(ushort landblock, double x) => (landblock >> 8) * BlockLength + x;
        public static double ToGlobalY(ushort landblock, double y) => (landblock & 0xFF) * BlockLength + y;

        public static (double Gx, double Gy) ToGlobal(ushort landblock, double x, double y)
            => (ToGlobalX(landblock, x), ToGlobalY(landblock, y));

        /// <summary>
        /// Global metres -> landblock + local x,y, normalised so local x,y fall in [0, 192). False when the
        /// point lies outside the 256 x 256 landblock grid.
        /// </summary>
        public static bool TryToLocal(double gx, double gy, out OutdoorPos pos)
        {
            pos = default;
            var bx = (int)Math.Floor(gx / BlockLength);
            var by = (int)Math.Floor(gy / BlockLength);
            if (bx < 0 || bx > 255 || by < 0 || by > 255) return false;
            pos = new OutdoorPos((ushort)((bx << 8) | by), gx - bx * BlockLength, gy - by * BlockLength);
            return true;
        }

        /// <summary>The circle's centre as a position.</summary>
        public static OutdoorPos Centre(OutdoorCircleDef c) => new OutdoorPos(c.Landblock, c.X, c.Y);

        /// <summary>
        /// HORIZONTAL distance between two positions in global metres. There is deliberately no z anywhere in this
        /// class's geometry: every ring distance (players, creatures, points, wisps, drops) is XY from the centre.
        /// </summary>
        public static double HorizontalDistance(OutdoorPos a, OutdoorPos b)
        {
            var dx = a.Gx - b.Gx;
            var dy = a.Gy - b.Gy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>True when <paramref name="pos"/> is inside or on the circle.</summary>
        public static bool Contains(OutdoorCircleDef c, OutdoorPos pos) => DistanceToEdge(c, pos) >= 0;

        /// <summary>Metres from <paramref name="pos"/> to the circle's edge: positive inside, zero on the edge, negative outside.</summary>
        public static double DistanceToEdge(OutdoorCircleDef c, OutdoorPos pos)
        {
            return c.Radius - HorizontalDistance(pos, Centre(c));
        }

        /// <summary>
        /// May a creature or marker stand here? Inside the perimeter circle, inside at least one include area
        /// (an EMPTY include list means the whole circle), and inside no exclude area. Include areas are
        /// always clipped to the circle.
        /// </summary>
        public static bool InIncludeExclude(OutdoorSiteDef site, OutdoorPos pos)
        {
            if (!Contains(site.Circle, pos)) return false;
            var gx = pos.Gx;
            var gy = pos.Gy;

            if (site.Include != null && site.Include.Count > 0)
            {
                var inAny = false;
                foreach (var a in site.Include)
                    if (AreaContains(a, gx, gy)) { inAny = true; break; }
                if (!inAny) return false;
            }

            if (site.Exclude != null)
                foreach (var a in site.Exclude)
                    if (AreaContains(a, gx, gy)) return false;

            return true;
        }

        /// <summary>Area membership in global metres. A malformed area (neither or a degenerate shape) contains nothing.</summary>
        public static bool AreaContains(OutdoorAreaDef a, double gx, double gy)
        {
            if (a == null) return false;
            if (a.Circle != null)
            {
                var dx = gx - a.Circle.Gx;
                var dy = gy - a.Circle.Gy;
                return dx * dx + dy * dy <= a.Circle.R * a.Circle.R;
            }
            if (a.Polygon != null && a.Polygon.Count >= 3)
                return PolygonContains(a.Polygon, gx, gy);
            return false;
        }

        /// <summary>Even-odd ray cast. Points exactly on an edge may fall either way.</summary>
        private static bool PolygonContains(List<List<double>> poly, double gx, double gy)
        {
            var inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (poly[i] == null || poly[j] == null || poly[i].Count < 2 || poly[j].Count < 2) return false;
                double xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
                if ((yi > gy) != (yj > gy) && gx < (xj - xi) * (gy - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Parses the entry/exit text "0xLLLLCCCC x y z [qw qx qy qz]" (the dungeon-file entry shape) to its
        /// cell and local x,y,z. The landblock is cell &gt;&gt; 16.
        /// </summary>
        public static bool TryParsePositionText(string text, out uint cell, out double x, out double y, out double z)
        {
            cell = 0; x = y = z = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return false;
            var cellText = p[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p[0].Substring(2) : p[0];
            if (!uint.TryParse(cellText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cell)) return false;
            return double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        /// <summary>Every landblock the circle's bounding box overlaps (the box [cx-r, cx+r] x [cy-r, cy+r], edges exclusive on the far side). -1 marks a tile off the 256 x 256 grid.</summary>
        public static IEnumerable<int> BoundingBoxLandblocks(OutdoorCircleDef c)
        {
            var centre = Centre(c);
            var minX = (int)Math.Floor((centre.Gx - c.Radius) / BlockLength);
            var maxX = (int)Math.Ceiling((centre.Gx + c.Radius) / BlockLength) - 1;
            var minY = (int)Math.Floor((centre.Gy - c.Radius) / BlockLength);
            var maxY = (int)Math.Ceiling((centre.Gy + c.Radius) / BlockLength) - 1;
            for (var bx = minX; bx <= maxX; bx++)
                for (var by = minY; by <= maxY; by++)
                    yield return (bx < 0 || bx > 255 || by < 0 || by > 255) ? -1 : ((bx << 8) | by);
        }
    }
}

using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// Plans the ring of King of the Hill zone markers (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"): evenly spaced points
    /// on the edge of the control zone, returned as <see cref="BattlegroundSealPiece"/>s so they go through the same
    /// fixture placement as the pen seals. Cosmetic only; scoring reads <see cref="KothZone"/>, never the markers. Pure.
    /// </summary>
    public static class BattlegroundZoneMarkers
    {
        /// <summary>The fewest markers a ring holds, however wide the spacing.</summary>
        public const int MinMarkers = 8;

        /// <summary>The most markers a ring holds, however large the zone.</summary>
        public const int MaxMarkers = 32;

        /// <summary>
        /// How far a point is pulled in when float rounding put it a hair outside the zone. A float ulp at the
        /// landblock-local magnitudes used here (under 256) is at most about 1.5e-5, so this always lands inside.
        /// </summary>
        private const double RoundingInset = 5e-5;

        /// <summary>
        /// The ring for a layout in its own (neutral) marker colour, <see cref="BattlegroundLayout.ZoneMarkerWcid"/>.
        /// See <see cref="Plan(BattlegroundLayout, KothZone, double, uint, double)"/>.
        /// </summary>
        public static IReadOnlyList<BattlegroundSealPiece> Plan(BattlegroundLayout layout, KothZone zone, double spacing)
            => Plan(layout, zone, spacing, layout?.ZoneMarkerWcid ?? 0);

        /// <summary>
        /// N = clamp(ceil(2 pi R / spacing), 8, 32) markers of <paramref name="wcid"/>, marker i at angle 2 pi i / N
        /// measured from +x, every one at horizontal distance R (the zone radius) from the zone centre, at the zone's centre
        /// z (the layout's ZoneZ when the zone comes from KothZoneFor), with the layout's wcid and landblock carried in
        /// CellId = (landblock &lt;&lt; 16) | <see cref="BattlegroundLayout.ZoneCellLow"/>, and identity rotation.
        /// Every point satisfies <see cref="KothZone.Contains"/> (the boundary is inclusive; a point float rounding put
        /// a hair outside is pulled in by at most <see cref="RoundingInset"/>). The spacing is clamped with
        /// <see cref="BattlegroundTunables.ClampMarkerSpacing"/>. Empty when the layout is null, the wcid is 0, or R is
        /// not a positive finite number.
        ///
        /// <para/>
        /// <paramref name="zOffset"/> (pvp_bg_koth_marker_z_offset, already clamped by the caller) is added to every marker's
        /// z AFTER the containment check, so it shifts the visuals only: the wisp model's glow sits well above its origin
        /// (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"), and a negative offset lowers it toward the floor. The scoring zone
        /// and the ring's x/y are unchanged, and the offset is not limited by the zone height.
        /// </summary>
        public static IReadOnlyList<BattlegroundSealPiece> Plan(BattlegroundLayout layout, KothZone zone, double spacing, uint wcid, double zOffset = 0.0)
        {
            var radius = zone.Radius;

            if (layout == null || wcid == 0 || double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0)
                return Array.Empty<BattlegroundSealPiece>();

            var count = Count(radius, spacing);
            var cellId = (layout.LandblockId << 16) | layout.ZoneCellLow;
            var z = (float)zone.CenterZ;
            var placedZ = (float)(zone.CenterZ + (double.IsNaN(zOffset) || double.IsInfinity(zOffset) ? 0.0 : zOffset));
            var pieces = new List<BattlegroundSealPiece>(count);

            for (var i = 0; i < count; i++)
            {
                var theta = 2.0 * Math.PI * i / count;
                var (x, y) = PointAt(zone, radius, theta);

                if (!zone.Contains(x, y, z))
                    (x, y) = PointAt(zone, radius - RoundingInset, theta);

                pieces.Add(new BattlegroundSealPiece(wcid, cellId, x, y, placedZ, 1f, 0f));
            }

            return pieces.AsReadOnly();
        }

        /// <summary>The marker count for a radius and a spacing: clamp(ceil(2 pi R / spacing), 8, 32). Pure.</summary>
        public static int Count(double radius, double spacing)
        {
            var s = BattlegroundTunables.ClampMarkerSpacing(spacing);
            var raw = Math.Ceiling(2.0 * Math.PI * radius / s);

            if (double.IsNaN(raw) || raw <= MinMarkers)
                return MinMarkers;

            return raw >= MaxMarkers ? MaxMarkers : (int)raw;
        }

        private static (float X, float Y) PointAt(KothZone zone, double r, double theta)
            => ((float)(zone.CenterX + r * Math.Cos(theta)), (float)(zone.CenterY + r * Math.Sin(theta)));
    }
}

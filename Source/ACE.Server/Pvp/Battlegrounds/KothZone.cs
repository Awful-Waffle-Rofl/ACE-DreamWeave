using System;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The King of the Hill control zone: a vertical cylinder around a centre, in the landblock-local frame
    /// (the same frame as <see cref="BattlegroundZoneSample"/> and <see cref="PvpSpawnPoint"/>). The caller builds it
    /// from the pvp_bg_koth_zone_* settings and the map's centre. Pure.
    /// </summary>
    public readonly record struct KothZone(double CenterX, double CenterY, double CenterZ, double Radius, double Height)
    {
        /// <summary>True when the point is within Radius horizontally and within Height vertically of the centre (both inclusive).</summary>
        public bool Contains(double x, double y, double z)
        {
            var dx = x - CenterX;
            var dy = y - CenterY;

            return Math.Sqrt(dx * dx + dy * dy) <= Radius && Math.Abs(z - CenterZ) <= Height;
        }
    }
}
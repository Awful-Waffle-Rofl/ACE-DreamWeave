using System;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The pure half of <see cref="HazardContext.HitPlayers"/> (shared invariant 3): damage size, reach, debounce.
    /// </summary>
    public static class HazardHitRules
    {
        /// <summary>
        /// ceil(pct x maxHealth), at least 1 when pct &gt; 0 and maxHealth &gt; 0; 0 when pct &lt;= 0 (visual-only
        /// hazards). No resistance, no rating: owner ruling ("2 hits in a row without heal is death" at 0.60).
        /// </summary>
        public static uint DamageFor(uint maxHealth, double pct)
        {
            if (!double.IsFinite(pct) || pct <= 0.0 || maxHealth == 0)
                return 0;

            var raw = Math.Ceiling(pct * maxHealth);

            if (raw >= uint.MaxValue)
                return uint.MaxValue;

            return Math.Max(1u, (uint)raw);
        }

        /// <summary>
        /// A flat damage number scaled by the victim's resist mod (the fill hit): round(flat x mod) away from zero, 0 for
        /// non-finite input or a product &lt;= 0, saturating at uint.MaxValue.
        /// </summary>
        public static uint FlatAmount(double flat, double resistMod)
        {
            if (!double.IsFinite(flat) || !double.IsFinite(resistMod))
                return 0;

            var product = flat * resistMod;

            if (!(product > 0.0))
                return 0;

            var rounded = Math.Round(product, MidpointRounding.AwayFromZero);

            if (rounded >= uint.MaxValue)
                return uint.MaxValue;

            return (uint)rounded;
        }

        /// <summary>2D (x, y) distance from the hazard centre to the player is within hazard radius + player radius.</summary>
        public static bool InReach(Vector3 centre, Vector3 player, double hazardRadius, double playerRadius)
        {
            var reach = Math.Max(0.0, hazardRadius) + Math.Max(0.0, playerRadius);
            var dx = player.X - centre.X;
            var dy = player.Y - centre.Y;
            return dx * dx + dy * dy <= reach * reach;
        }

        /// <summary>
        /// May this hazard hit this player again at <paramref name="now"/>, given its last hit at
        /// <paramref name="lastHit"/> (null = never)? A window of +infinity means once per hazard lifetime.
        /// </summary>
        public static bool WindowOpen(double? lastHit, double now, double windowSeconds)
        {
            if (!lastHit.HasValue)
                return true;

            if (double.IsPositiveInfinity(windowSeconds) || double.IsNaN(windowSeconds))
                return false;

            return now - lastHit.Value >= Math.Max(0.0, windowSeconds);
        }
    }
}

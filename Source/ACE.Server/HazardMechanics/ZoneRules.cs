using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The pure half of the zones mechanic (ZonesMechanic): the dwell damage ramp and one player's dwell clock.
    /// No landblock, no Player, no PropertyManager - tested on its own.
    ///
    /// THE RULE. A player standing within pillar radius + player radius of ANY pillar (several pillars in reach still
    /// count once) runs a dwell clock. The clock deals damage the moment the player enters (dwell 0) and every
    /// <c>tick</c> seconds of accumulated dwell after that, each hit pct(t) = pct + (pct_end - pct) x min(1, t / ramp)
    /// of max health, t = the dwell at that hit. Time outside does not add to the dwell. A player outside for
    /// <c>reset</c> seconds or more starts over (dwell 0, so the next entry hits for pct again); stepping out for less
    /// than that and back in resumes the same clock and the same cadence.
    /// </summary>
    public static class ZoneRules
    {
        /// <summary>Damage fraction for a hit at dwell <paramref name="dwell"/> seconds: linear from pct to pctEnd across ramp seconds, then flat. ramp &lt;= 0 means pctEnd at once.</summary>
        public static double PctAt(double dwell, double pct, double pctEnd, double ramp)
        {
            if (!(ramp > 0.0))
                return pctEnd;

            var f = Math.Min(1.0, Math.Max(0.0, dwell) / ramp);
            return pct + (pctEnd - pct) * f;
        }

        /// <summary>True when the 2D position is within radius + playerRadius of any of the pillar centres.</summary>
        public static bool InAnyZone(IReadOnlyList<Vector3> pillars, Vector3 player, double radius, double playerRadius)
        {
            if (pillars == null)
                return false;

            for (var i = 0; i < pillars.Count; i++)
            {
                if (HazardHitRules.InReach(pillars[i], player, radius, playerRadius))
                    return true;
            }

            return false;
        }

        /// <summary>The four corners of a Rect region, counter-clockwise from (MinX, MinY); empty for any other shape.</summary>
        public static IReadOnlyList<Vector2> RegionCorners(HazardRegion region)
        {
            if (region == null || region.Shape != HazardShape.Rect)
                return Array.Empty<Vector2>();

            return new[]
            {
                new Vector2(region.MinX, region.MinY),
                new Vector2(region.MaxX, region.MinY),
                new Vector2(region.MaxX, region.MaxY),
                new Vector2(region.MinX, region.MaxY),
            };
        }
    }

    /// <summary>
    /// One player's dwell clock (see <see cref="ZoneRules"/>). Call <see cref="Update"/> once per mechanic tick with
    /// whether the player is in reach of any pillar; it returns the damage fraction to deal this tick, or null.
    /// </summary>
    public sealed class ZoneClock
    {
        private readonly double tick;
        private readonly double pct;
        private readonly double pctEnd;
        private readonly double ramp;
        private readonly double reset;

        private double? last;
        private double dwell;
        private double outside;
        private double nextHitAt;
        private bool insideNow;

        public ZoneClock(double tick, double pct, double pctEnd, double ramp, double reset)
        {
            this.tick = Math.Max(0.01, tick);
            this.pct = pct;
            this.pctEnd = pctEnd;
            this.ramp = ramp;
            this.reset = Math.Max(0.0, reset);
        }

        /// <summary>Seconds spent inside since the clock last started over.</summary>
        public double Dwell => dwell;

        /// <summary>True once the player has been outside for the full reset time: the clock has started over and the owner may forget it.</summary>
        public bool Expired => !insideNow && outside >= reset;

        /// <summary>Advances the clock to <paramref name="now"/>; returns the pct of max health to deal now, else null.</summary>
        public double? Update(double now, bool inside)
        {
            var dt = last.HasValue ? Math.Max(0.0, now - last.Value) : 0.0;
            last = now;

            // only time spent inside counts as dwell: the gap since an OUTSIDE update never does
            var wasInside = insideNow;
            insideNow = inside;

            if (!inside)
            {
                outside += dt;

                if (outside >= reset)
                {
                    dwell = 0.0;
                    nextHitAt = 0.0;
                }

                return null;
            }

            outside = 0.0;
            if (wasInside)
                dwell += dt;

            if (dwell + 1e-9 < nextHitAt)
                return null;

            var hit = PctForHit();

            nextHitAt += tick;

            // a stalled driver does not owe the missed hits: skip to the next boundary after now
            while (nextHitAt <= dwell + 1e-9)
                nextHitAt += tick;

            return hit;
        }

        private double PctForHit() => ZoneRules.PctAt(dwell, pct, pctEnd, ramp);
    }
}

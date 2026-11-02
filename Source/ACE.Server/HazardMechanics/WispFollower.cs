using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Where a followed player has been: timed (x, y) samples, read back by time with linear interpolation. The follower
    /// wisp flies to where its target WAS <c>follow_lag</c> seconds ago, so a player who stands still is caught and a
    /// player who keeps moving is not (see <see cref="WispFollowRules"/>). Pure: the caller supplies every timestamp.
    ///
    /// Samples older than <c>retain</c> seconds before the newest one are dropped, except the single newest of them,
    /// which stays as the interpolation anchor so a read at the edge of the window still lands between two samples.
    /// </summary>
    public sealed class WispTrail
    {
        /// <summary>Retention for the longest lag (<c>follow_lag</c> clamps to 10 s) plus the 2 s margin.</summary>
        public const double DefaultRetainSeconds = 12.0;

        private readonly List<(double T, Vector2 P)> samples = new List<(double, Vector2)>();
        private readonly double retain;

        public WispTrail(double retainSeconds = DefaultRetainSeconds)
        {
            retain = Math.Max(0.0, retainSeconds);
        }

        public int Count => samples.Count;

        /// <summary>Forgets every sample (the target left the region or changed): the next sighting starts a fresh trail.</summary>
        public void Clear() => samples.Clear();

        /// <summary>Adds a sample. A sample at the same time as the newest replaces it; an older one is ignored (time never runs backwards here).</summary>
        public void Record(double t, Vector2 p)
        {
            if (samples.Count > 0)
            {
                var last = samples[samples.Count - 1].T;

                if (t < last)
                    return;

                if (t == last)
                {
                    samples[samples.Count - 1] = (t, p);
                    return;
                }
            }

            samples.Add((t, p));

            // drop what is older than the window, keeping the newest dropped sample as the anchor
            var cutoff = t - retain;

            while (samples.Count >= 2 && samples[1].T <= cutoff)
                samples.RemoveAt(0);
        }

        /// <summary>The target's position at <paramref name="t"/>: null with no samples, the first sample before the first, the last after the last, else the straight line between the two around it.</summary>
        public Vector2? At(double t)
        {
            if (samples.Count == 0)
                return null;

            if (t <= samples[0].T)
                return samples[0].P;

            var last = samples[samples.Count - 1];

            if (t >= last.T)
                return last.P;

            for (var i = 1; i < samples.Count; i++)
            {
                if (t > samples[i].T)
                    continue;

                var a = samples[i - 1];
                var b = samples[i];
                var span = b.T - a.T;

                return span <= 0.0 ? b.P : Vector2.Lerp(a.P, b.P, (float)((t - a.T) / span));
            }

            return last.P;
        }
    }

    /// <summary>
    /// The follower wisp's decisions (pure): where its next leg goes and when its next pillar is due.
    ///
    /// THE LAG IS THE TELEGRAPH. A leg of <c>step</c> seconds issued at time <c>now</c> ends at the target's position at
    /// <c>now + step - lag</c>, so on arrival the wisp stands where the target was <c>lag</c> seconds earlier. A player who
    /// has stood still for <c>lag</c> seconds is under it; a player who keeps moving at more than reach / lag m/s is not
    /// (reach = pillar_radius + player_radius). Every leg is the same <see cref="LegStep"/> long and the next one is issued
    /// one tick before the current one ends, so the wisp flows from leg to leg instead of stopping between them.
    ///
    /// THE BEAT. The follower has no clock of its own (2026-10-19 playtest: on its own 3 s cadence it read as a different
    /// tempo from the crew): it bursts at each step's FIRST crew burst, ramping with the crew's move and hold. At the
    /// crew's hold start it settles on <see cref="BeatPoint"/> and sits still there for the hold, as the crew does.
    /// </summary>
    public static class WispFollowRules
    {
        public const double DefaultLag = 2.0;

        /// <summary>The constant leg length in seconds.</summary>
        public const double LegStep = 0.5;

        /// <summary>One driver tick at the default cadence (hazard_mech_tick_ms 100): how early the next leg is issued.</summary>
        public const double DefaultTick = HazardDefaults.TickMs / 1000.0;

        /// <summary>The fastest the follower may fly, m/s: the escape_speed upper clamp, so it never outruns what a fleeing player could.</summary>
        public const double FollowMaxSpeed = HazardDefaults.MaxEscapeSpeed;

        /// <summary>
        /// The next leg, or null when none is due yet (the current leg ends more than one tick from now) or the trail is
        /// empty. <paramref name="legEndsAt"/> is when the leg in flight ends (negative infinity before the first leg).
        /// </summary>
        public static (Vector2 End, double Seconds)? NextLeg(WispTrail trail, double now, double lag, double step, double legEndsAt, double tick = DefaultTick)
        {
            if (trail == null || now < legEndsAt - tick - 1e-9)
                return null;

            var end = trail.At(now + step - lag);

            return end.HasValue ? (end.Value, step) : ((Vector2, double)?)null;
        }

        /// <summary>
        /// Where the follower bursts on a beat due at <paramref name="burstAt"/>: where its target stood
        /// <paramref name="lag"/> seconds before it. Null when the target was not yet being followed then (a fresh
        /// sighting gets at least one full lag of warning before its first burst), or the trail is empty.
        /// The point is fixed at <paramref name="now"/> (the crew's hold start): when lag is shorter than the time left
        /// to the burst, burstAt - lag is still in the future, so the query is clamped to <paramref name="now"/>, the
        /// newest position known, rather than relying on the trail's after-the-last-sample rule.
        /// </summary>
        public static Vector2? BeatPoint(WispTrail trail, double followStartedAt, double burstAt, double lag, double now)
        {
            if (trail == null || double.IsNaN(followStartedAt) || burstAt - lag < followStartedAt - 1e-9)
                return null;

            return trail.At(Math.Min(burstAt - lag, now));
        }
    }
}

using System;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// THE ONE RAMP every "start -> end over a series" arg uses (a ball wave's speed, lead and wave_gap across the waves,
    /// a stream's launch speed across its launches, a wisp cycle's move, hold and rest across the cycles). Pure.
    ///
    /// Index 1 gives the start, index count gives the end, the rest is linear in between. A series of one, or no end
    /// value (no "&lt;key&gt;_end" arg given), is the start value: a run that gives no end key behaves exactly as it did
    /// before ramps existed. An index outside 1..count is clamped to the nearest end, never extrapolated.
    /// </summary>
    public static class HazardRamp
    {
        /// <summary>Linear: index 1 -> start, index count -> end; count &lt;= 1 or end == null -> start; index clamped to [1, count].</summary>
        public static double At(double start, double? end, int index, int count)
            => end == null || count <= 1
                ? start
                : start + (end.Value - start) * Math.Clamp((index - 1) / (double)(count - 1), 0.0, 1.0);
    }
}

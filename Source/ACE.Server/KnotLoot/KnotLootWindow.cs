using System;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// Pure level-window math for Knot loot. Takes settings as a parameter; never reads the property store.
    /// An item stores only its trait position u in [0,1]; the window maps u onto a sub-range of the trait range.
    /// </summary>
    public static class KnotLootWindow
    {
        /// <summary>The window [Low, High] within [0,1] for a Knot level: start is linear in level (clamped at both anchors), end = min(1, start + width).</summary>
        public static (double Low, double High) Window(int knotLevel, KnotWindowSettings w)
        {
            double t;

            if (w.HighLevel <= w.LowLevel)
                t = knotLevel >= w.HighLevel ? 1.0 : 0.0;
            else
                t = Math.Clamp((double)(knotLevel - w.LowLevel) / (w.HighLevel - w.LowLevel), 0.0, 1.0);

            var start = w.LowStart + t * (w.HighStart - w.LowStart);

            return (start, Math.Min(1.0, start + w.Width));
        }

        /// <summary>Maps u (clamped to [0,1], NaN reads as 0) into the level window: p = start + u * (end - start).</summary>
        public static double Position(int knotLevel, double u, KnotWindowSettings w)
        {
            if (double.IsNaN(u))
                u = 0.0;

            u = Math.Clamp(u, 0.0, 1.0);

            var (low, high) = Window(knotLevel, w);

            return low + u * (high - low);
        }

        /// <summary>min + p * (max - min).</summary>
        public static double Lerp(double min, double max, double p) => min + p * (max - min);
    }
}
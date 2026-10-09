using System;

namespace ACE.Server.Managers
{
    /// <summary>The four world_tick_slow_log_* tunables, resolved and sanitised. See SlowTickReporter.</summary>
    public readonly struct SlowTickSettings
    {
        public readonly bool Enabled;

        /// <summary>Absolute floor: an iteration below this is never slow, however it compares to the median.</summary>
        public readonly double ThresholdMs;

        /// <summary>An iteration must also reach this multiple of the recent median. 0 = floor only.</summary>
        public readonly double MedianMultiplier;

        /// <summary>Least time between two emitted lines; slow iterations inside it are counted, not logged.</summary>
        public readonly long MinIntervalMs;

        public SlowTickSettings(bool enabled, double thresholdMs, double medianMultiplier, long minIntervalMs)
        {
            Enabled = enabled;
            ThresholdMs = double.IsNaN(thresholdMs) || thresholdMs < 0 ? 0 : thresholdMs;
            MedianMultiplier = double.IsNaN(medianMultiplier) || medianMultiplier < 0 ? 0 : medianMultiplier;
            MinIntervalMs = minIntervalMs < 0 ? 0 : minIntervalMs;
        }
    }

    public enum SlowTickVerdict
    {
        None,
        Emit,
        Suppressed,
    }

    public readonly struct SlowTickResult
    {
        public readonly SlowTickVerdict Verdict;

        /// <summary>Median of the samples BEFORE this one. Computed only when the floor tripped; 0 otherwise, or with no samples.</summary>
        public readonly double MedianMs;

        /// <summary>On Emit: slow iterations suppressed since the previous emit. On Suppressed: the running count including this one.</summary>
        public readonly int SuppressedCount;

        /// <summary>Largest total among those suppressed iterations, 0 when there were none.</summary>
        public readonly double SuppressedMaxMs;

        public SlowTickResult(SlowTickVerdict verdict, double medianMs, int suppressedCount, double suppressedMaxMs)
        {
            Verdict = verdict;
            MedianMs = medianMs;
            SuppressedCount = suppressedCount;
            SuppressedMaxMs = suppressedMaxMs;
        }
    }

    /// <summary>
    /// WaffleACE: pure "is this world iteration slow enough to explain" decision, plus the ring of recent
    /// totals its relative check compares against. No clock, no statics, no PropertyManager: the caller
    /// passes the time and the settings, so every rule is unit-testable.
    ///
    /// Rules, in order:
    ///   1. disabled -> None;
    ///   2. total below the absolute floor -> None (the cheap path: one compare, no median);
    ///   3. with a non-zero multiplier and at least <see cref="MinSamplesForRelative"/> samples, total below
    ///      multiplier x median -> None; with fewer samples, the floor alone decides;
    ///   4. inside MinIntervalMs of the last emit -> Suppressed (counted, max kept);
    ///   5. otherwise Emit, carrying and then resetting the suppressed count and max.
    ///
    /// Evaluate BEFORE AddSample, so a spike is judged against a median it has not yet moved.
    /// The median sorts a preallocated scratch copy and is only ever computed after the floor tripped, so a
    /// normal iteration costs one compare and the ring write. Allocation-free after construction.
    /// </summary>
    public sealed class SlowTickDetector
    {
        public const int Capacity = 1024;

        public const int MinSamplesForRelative = 32;

        private readonly double[] ring = new double[Capacity];
        private readonly double[] scratch = new double[Capacity];

        private int next;
        private int count;

        private bool hasEmitted;
        private long lastEmitMs;

        private int suppressedCount;
        private double suppressedMaxMs;

        public int SampleCount => count;

        public void AddSample(double totalMs)
        {
            ring[next] = totalMs;

            next++;
            if (next == Capacity)
                next = 0;

            if (count < Capacity)
                count++;
        }

        /// <summary>Median of the current samples; 0 with none. Allocation-free.</summary>
        public double Median()
        {
            if (count == 0)
                return 0;

            Array.Copy(ring, scratch, count);
            Array.Sort(scratch, 0, count);

            var mid = count / 2;

            if ((count & 1) == 1)
                return scratch[mid];

            return (scratch[mid - 1] + scratch[mid]) / 2.0;
        }

        public SlowTickResult Evaluate(double totalMs, long nowMs, in SlowTickSettings settings)
        {
            if (!settings.Enabled)
                return default;

            if (!(totalMs >= settings.ThresholdMs))
                return default;

            var median = Median();

            if (settings.MedianMultiplier > 0 && count >= MinSamplesForRelative && totalMs < settings.MedianMultiplier * median)
                return default;

            if (hasEmitted && nowMs - lastEmitMs < settings.MinIntervalMs)
            {
                suppressedCount++;

                if (totalMs > suppressedMaxMs)
                    suppressedMaxMs = totalMs;

                return new SlowTickResult(SlowTickVerdict.Suppressed, median, suppressedCount, suppressedMaxMs);
            }

            var result = new SlowTickResult(SlowTickVerdict.Emit, median, suppressedCount, suppressedMaxMs);

            hasEmitted = true;
            lastEmitMs = nowMs;
            suppressedCount = 0;
            suppressedMaxMs = 0;

            return result;
        }
    }
}

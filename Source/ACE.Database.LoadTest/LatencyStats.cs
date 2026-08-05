using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Percentile reporting for a batch of latency samples, in milliseconds.
    /// </summary>
    public static class LatencyStats
    {
        /// <summary>
        /// Records and prints a batch of latency samples. Every sample this class accepts is a duration in
        /// milliseconds, so the direction defaults to LowerIsBetter - that is the one place in the harness where
        /// direction is not spelled out per call site, and it is safe only because the input unit is fixed by
        /// this method's contract. Anything that is not a duration must go through Metrics.Record /
        /// Metrics.RecordSeries, which require an explicit direction.
        ///
        /// Pass Informational for a duration that is real but must never gate - a timing that measures a
        /// different code path than its caller intends, or one that cannot be sampled enough to judge.
        /// </summary>
        public static void Report(string label, IReadOnlyCollection<double> samplesMs,
            MetricDirection direction = MetricDirection.LowerIsBetter)
        {
            if (samplesMs.Count == 0)
            {
                Console.WriteLine($"{label}: no samples");
                return;
            }

            Metrics.RecordSeries(label, samplesMs, direction);

            var sorted = samplesMs.OrderBy(x => x).ToList();

            double Percentile(double p)
            {
                var index = (int)Math.Ceiling(p * sorted.Count) - 1;
                return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
            }

            Console.WriteLine(
                $"{label,-32} n={sorted.Count,-6} " +
                $"min={sorted[0],7:N1}ms  " +
                $"avg={sorted.Average(),7:N1}ms  " +
                $"p50={Percentile(0.50),7:N1}ms  " +
                $"p95={Percentile(0.95),7:N1}ms  " +
                $"p99={Percentile(0.99),7:N1}ms  " +
                $"max={sorted[^1],7:N1}ms");
        }
    }
}

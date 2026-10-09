using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Which way a metric has to move to be an improvement. Every metric declares this at RECORD time - the
    /// comparison tool must never guess it, because guessing is how a throughput metric that went UP got printed
    /// as "REGRESSED" (observed: "Deletes per sec 8,129.70 -> 10,376.06 (+27.6%) REGRESSED").
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MetricDirection
    {
        /// <summary>Durations, wall times, queue depths, mismatch counts - smaller is better.</summary>
        LowerIsBetter,

        /// <summary>Throughput, rows per second, ops per second, pass flags - bigger is better.</summary>
        HigherIsBetter,

        /// <summary>
        /// Recorded and printed, never gated. Two kinds of metric belong here:
        ///
        /// 1. Things that are not performance signals at all - a fixture invariant or a configuration echo (how
        ///    many items the burst was configured for, how many possessions the test character has, how many
        ///    mismatches a correctness phase found). These have no meaningful direction, and this value lets them
        ///    say so instead of being forced to declare a fake one.
        ///
        /// 2. Genuine performance numbers that structurally cannot be sampled enough to judge - a measurement
        ///    that is n=1 by construction, not by configuration, so no argument raises its sample count. Marking
        ///    those Informational is honest about the fact that they will never gate anything; leaving them as a
        ///    directional metric just makes `compare` print "insufficient samples" forever.
        ///
        /// Either way the number is still reported, so a human reading a diff can see it move.
        /// </summary>
        Informational,
    }

    public class MetricValue
    {
        // Scalar metrics (counts, ratios, single durations) use Value. Series metrics (from LatencyStats.Report)
        // use the percentile fields instead. A metric never uses both.
        public double? Value { get; set; }

        /// <summary>
        /// How many independent measurements back this number. For a series it is the sample count; for a scalar
        /// it is however many underlying operations the value aggregates (a throughput computed over 500 saves is
        /// backed by 500 operations, a single cold-read timing is backed by 1). Nullable only so reports written
        /// before this field existed still deserialize - a null here means "unknown", not "one".
        /// </summary>
        public int? Count { get; set; }

        /// <summary>
        /// Which way is better. Nullable only for backward compatibility with reports written before this field
        /// existed; `compare` falls back to a name heuristic for those and warns loudly about every one.
        /// </summary>
        public MetricDirection? Direction { get; set; }

        /// <summary>
        /// True for a metric that is a discrete, repeatable FACT rather than a measurement - a SQL statement
        /// count, a row count, a number of round trips. These carry no timing noise, so the minimum-sample gate
        /// is meaningless for them (n has nothing to do with their reliability) and any threshold at all is too
        /// loose: if the number changes, something changed. `compare` judges them at a 0% threshold and exempts
        /// them from the sample gate.
        ///
        /// Never inferred - a scenario has to say so, because being wrong here turns ordinary noise into a
        /// permanent false regression.
        ///
        /// Evidence this was added for: possession-load's "Possession read SQL statements" read exactly 68 on
        /// all six identical-build calibration runs (observed spread 0.0%), and is the single sharpest detector
        /// of an N+1 regression in the batched possession read - but at n=1 the sample gate refused to judge it,
        /// so the scenario contributed no coverage at all.
        /// </summary>
        public bool Deterministic { get; set; }

        public double? Min { get; set; }
        public double? Avg { get; set; }
        public double? P50 { get; set; }
        public double? P95 { get; set; }
        public double? P99 { get; set; }
        public double? Max { get; set; }
    }

    /// <summary>
    /// Collects every metric a scenario reports into a flat, scenario-scoped table, so a full suite run can be
    /// serialized to JSON and compared against a previous run later (see BenchmarkComparer). Program.cs calls
    /// SetScope(scenario.Name) before each scenario runs so metric names don't collide across a combined suite.
    /// </summary>
    public static class Metrics
    {
        private static readonly Dictionary<string, MetricValue> Values = new();

        private static string _scope = "";

        public static void SetScope(string scope) => _scope = scope;

        private static string Key(string name) => string.IsNullOrEmpty(_scope) ? name : $"{_scope}.{name}";

        /// <summary>
        /// Records a single scalar metric. `direction` is deliberately required: a metric whose direction is
        /// inferred at compare time is a metric that will eventually be reported backwards.
        /// </summary>
        /// <param name="sampleCount">
        /// How many independent operations the value aggregates. Defaults to 1, which is the safe default -
        /// a metric left at 1 is reported as "insufficient samples to judge" rather than flagged on one reading.
        /// </param>
        /// <param name="deterministic">
        /// Set only for a discrete repeatable fact (statement counts, row counts) - see MetricValue.Deterministic.
        /// It exempts the metric from the minimum-sample gate and judges it at a 0% threshold.
        /// </param>
        public static void Record(string name, double value, MetricDirection direction, int sampleCount = 1,
            bool deterministic = false)
        {
            Values[Key(name)] = new MetricValue
            {
                Value = value,
                Direction = direction,
                Count = sampleCount,
                Deterministic = deterministic,
            };
        }

        /// <summary>
        /// Records a percentile series. `direction` is required for the same reason as on Record.
        /// </summary>
        public static void RecordSeries(string name, IReadOnlyCollection<double> samplesMs, MetricDirection direction)
        {
            if (samplesMs.Count == 0)
                return;

            var sorted = samplesMs.OrderBy(x => x).ToList();

            double Percentile(double p)
            {
                var index = (int)Math.Ceiling(p * sorted.Count) - 1;
                return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
            }

            Values[Key(name)] = new MetricValue
            {
                Count = sorted.Count,
                Direction = direction,
                Min = sorted[0],
                Avg = sorted.Average(),
                P50 = Percentile(0.50),
                P95 = Percentile(0.95),
                P99 = Percentile(0.99),
                Max = sorted[^1]
            };
        }

        public static void Reset() => Values.Clear();

        public static IReadOnlyDictionary<string, MetricValue> Snapshot() => Values;
    }
}

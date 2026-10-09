using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// The observed run-to-run spread of one metric statistic, measured by running the standard suite K times
    /// against the SAME build (with reset-baseline.ps1 between runs). Anything inside this band is noise on this
    /// machine, not a code change.
    /// </summary>
    public class CalibrationEntry
    {
        /// <summary>"value", "p50" or "p95".</summary>
        public string Statistic { get; set; }

        public int Runs { get; set; }

        public double Min { get; set; }
        public double Median { get; set; }
        public double Max { get; set; }

        /// <summary>(Max - Min) / Median * 100. The full observed range, not a standard deviation - with K in the
        /// single digits the range is the only statistic that says anything useful.</summary>
        public double ObservedSpreadPercent { get; set; }

        /// <summary>The metric's within-run sample count, carried through for reference.</summary>
        public int? SampleCount { get; set; }
    }

    /// <summary>
    /// A per-metric noise floor for one machine. `compare` uses it in place of the single global --threshold, so a
    /// metric that is genuinely stable can be judged tightly while a metric that swings 60% between identical runs
    /// is not reported as a regression every time.
    ///
    /// MACHINE SPECIFIC. Disk, CPU, MySQL version and whatever else is running all move these numbers, so a
    /// profile is only valid on the machine that produced it, and only until that machine changes materially.
    /// Regenerate with `ACE.Database.LoadTest.exe calibrate --runs=K --yes` (see Program.RunCalibrate).
    /// </summary>
    public class CalibrationProfile
    {
        public string Label { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public string Machine { get; set; }
        public int Runs { get; set; }

        /// <summary>Keyed by MakeKey(metricKey, statistic).</summary>
        public Dictionary<string, CalibrationEntry> Entries { get; set; } = new();

        public static string MakeKey(string metricKey, string statistic) => $"{metricKey}|{statistic}";

        public bool TryGetEntry(string metricKey, string statistic, out CalibrationEntry entry)
        {
            entry = null;
            return Entries != null && Entries.TryGetValue(MakeKey(metricKey, statistic), out entry);
        }

        /// <summary>
        /// Builds a profile from K reports of the same build. A metric statistic is only included if every report
        /// carries it and its median is non-zero - a metric that appeared in some runs and not others has no
        /// meaningful spread, and dividing by a zero median produces an infinity that would disable the metric.
        /// </summary>
        public static CalibrationProfile Build(IReadOnlyList<BenchmarkReport> reports, string label, string machine)
        {
            if (reports == null || reports.Count < 2)
                throw new ArgumentException("Calibration needs at least 2 reports.", nameof(reports));

            var profile = new CalibrationProfile
            {
                Label = label,
                Machine = machine,
                GeneratedUtc = DateTime.UtcNow,
                Runs = reports.Count,
            };

            var keys = reports[0].Metrics.Keys.ToList();

            foreach (var metricKey in keys)
            {
                // Correctness gates are pass/fail, not measurements - a noise floor on them would be nonsense.
                if (metricKey.EndsWith(".Passed", StringComparison.Ordinal))
                    continue;

                foreach (var statistic in new[] { "value", "p50", "p95" })
                {
                    var samples = new List<double>();
                    int? sampleCount = null;
                    var complete = true;

                    foreach (var report in reports)
                    {
                        if (!report.Metrics.TryGetValue(metricKey, out var value))
                        {
                            complete = false;
                            break;
                        }

                        var stat = BenchmarkComparer.ReadStatistic(value, statistic);
                        if (stat == null)
                        {
                            complete = false;
                            break;
                        }

                        samples.Add(stat.Value);
                        sampleCount = value.Count;
                    }

                    if (!complete || samples.Count < 2)
                        continue;

                    var sorted = samples.OrderBy(x => x).ToList();
                    var median = sorted.Count % 2 == 1
                        ? sorted[sorted.Count / 2]
                        : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

                    if (median == 0)
                        continue;

                    profile.Entries[MakeKey(metricKey, statistic)] = new CalibrationEntry
                    {
                        Statistic = statistic,
                        Runs = sorted.Count,
                        Min = sorted[0],
                        Median = median,
                        Max = sorted[^1],
                        ObservedSpreadPercent = (sorted[^1] - sorted[0]) / median * 100,
                        SampleCount = sampleCount,
                    };
                }
            }

            return profile;
        }
    }
}

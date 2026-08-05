using System;
using System.Linq;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Renders a ComparisonResult to the console. Kept out of BenchmarkComparer so the comparison logic stays a
    /// pure function that unit tests can call without capturing stdout.
    /// </summary>
    public static class ComparisonPrinter
    {
        public static void Print(ComparisonResult result, CompareOptions options)
        {
            Console.WriteLine($"Baseline:  {result.BaselineLabel} ({result.BaselineTimestampUtc:u})");
            Console.WriteLine($"Candidate: {result.CandidateLabel} ({result.CandidateTimestampUtc:u})");

            if (options.Calibration != null)
            {
                var profile = options.Calibration;
                Console.WriteLine(
                    $"Calibration: {profile.Entries?.Count ?? 0} per-metric noise floors from {profile.Runs} runs " +
                    $"on '{profile.Machine}' ({profile.GeneratedUtc:u}), safety factor {options.CalibrationSafetyFactor:N2}.");

                if (!string.Equals(profile.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"  WARNING: that profile was calibrated on '{profile.Machine}' but this is " +
                        $"'{Environment.MachineName}'. Noise floors do not transfer between machines - regenerate it.");
                }
            }
            else
            {
                Console.WriteLine(
                    $"Calibration: none - using the flat --threshold={options.ThresholdPercent:N0}% for every metric. " +
                    "Run `calibrate` to measure this machine's real per-metric noise floor.");
            }

            Console.WriteLine();

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine($"WARNINGS ({result.Warnings.Count}):");
                foreach (var warning in result.Warnings)
                    Console.WriteLine($"  ! {warning}");
                Console.WriteLine();
            }

            foreach (var metric in result.Metrics)
                PrintMetric(metric);

            Console.WriteLine();
            PrintDrift(result);
            PrintSummary(result, options);
        }

        private static void PrintMetric(MetricComparison metric)
        {
            if (metric.MissingInCandidate)
            {
                Console.WriteLine($"  {metric.Key,-58} MISSING in candidate");
                return;
            }

            if (metric.IsCorrectnessGate)
            {
                Console.WriteLine($"  {metric.Key,-58} {metric.CorrectnessNote}");
                return;
            }

            var directionLabel = metric.Direction switch
            {
                MetricDirection.LowerIsBetter => "lower is better",
                MetricDirection.HigherIsBetter => "higher is better",
                _ => "informational",
            };

            if (metric.DirectionInferred)
                directionLabel += ", INFERRED";

            var sampleLabel = metric.Stats.FirstOrDefault()?.SampleCount is int n ? $", n={n}" : ", n=unknown";

            Console.WriteLine($"  {metric.Key,-58} ({directionLabel}{sampleLabel})");

            foreach (var stat in metric.Stats)
                PrintStat(stat);

            if (metric.Mixed)
            {
                Console.WriteLine(
                    "        ^ MIXED: p50 and p95 moved opposite ways. Both are real - a change that trades " +
                    "typical-case cost for tail cost (or the reverse) shows up exactly like this.");
            }
        }

        private static void PrintStat(StatComparison stat)
        {
            var sign = stat.PercentChange >= 0 ? "+" : "";
            var thresholdLabel = stat.Deterministic
                ? "vs   exact"
                : stat.ThresholdFromCalibration
                    ? $"vs {stat.ThresholdPercent,6:N1}% cal"
                    : $"vs {stat.ThresholdPercent,6:N1}%";

            var flag = stat.Outcome switch
            {
                ComparisonOutcome.Regressed => "  REGRESSED",
                ComparisonOutcome.Improved => "  IMPROVED",
                ComparisonOutcome.InsufficientSamples => $"  ({stat.Note})",
                ComparisonOutcome.BelowNoiseFloor => $"  ({stat.Note})",
                ComparisonOutcome.NotJudged => "  (informational)",
                _ => "",
            };

            Console.WriteLine(
                $"      {stat.Statistic,-6} {stat.Baseline,14:N2} -> {stat.Candidate,14:N2}  " +
                $"({sign}{stat.PercentChange:N1}%)  {thresholdLabel}{flag}");
        }

        private static void PrintDrift(ComparisonResult result)
        {
            var drift = result.Drift;

            if (drift == null || drift.JudgedStatistics == 0)
                return;

            Console.WriteLine(
                $"Whole-run drift: {drift.MovedWorse} of {drift.JudgedStatistics} statistics moved the bad way, " +
                $"median {drift.MedianSignedPercent:+0.0;-0.0}%.");

            if (drift.IsLopsided)
            {
                Console.WriteLine();
                Console.WriteLine("  NOTE: that is lopsided enough to look like a whole-run effect rather than per-metric");
                Console.WriteLine("  noise - one run landing on a busier or slower machine moves everything at once, and");
                Console.WriteLine("  several unrelated metrics then cross their thresholds together. Any regression below");
                Console.WriteLine("  is reported as measured and has NOT been suppressed, because a genuine across-the-board");
                Console.WriteLine("  regression is indistinguishable from this. Before acting on it, re-run the pair.");
            }

            Console.WriteLine();
        }

        private static void PrintSummary(ComparisonResult result, CompareOptions options)
        {
            var regressed = result.RegressedMetrics.ToList();
            var improved = result.ImprovedMetrics.ToList();
            var unjudgeable = result.UnjudgeableStats.ToList();

            Console.WriteLine(
                $"{improved.Count} improved, {regressed.Count} regressed, " +
                $"{unjudgeable.Count} statistic(s) not judged for want of samples.");

            if (unjudgeable.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Not judged (too few samples - these are blind spots, not passes):");
                foreach (var metric in result.Metrics.Where(m => m.Stats.Any(s => s.Outcome == ComparisonOutcome.InsufficientSamples)))
                {
                    foreach (var stat in metric.Stats.Where(s => s.Outcome == ComparisonOutcome.InsufficientSamples))
                        Console.WriteLine($"  - {metric.Key} [{stat.Statistic}] {stat.Note}");
                }
            }

            if (regressed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Regressed:");
                foreach (var metric in regressed)
                {
                    foreach (var stat in metric.Stats.Where(s => s.Outcome == ComparisonOutcome.Regressed))
                    {
                        Console.WriteLine(
                            $"  - {metric.Key} [{stat.Statistic}] {stat.Baseline:N2} -> {stat.Candidate:N2} " +
                            $"({stat.PercentChange:+0.0;-0.0}%) past {stat.ThresholdPercent:N1}%" +
                            (stat.ThresholdFromCalibration ? " (calibrated)" : ""));
                    }
                }
            }

            Console.WriteLine();

            if (result.HardFail)
            {
                Console.WriteLine("RESULT: FAIL - a correctness check regressed. Performance deltas are not meaningful until this is fixed.");
                return;
            }

            if (regressed.Count > 0)
            {
                Console.WriteLine("RESULT: REGRESSIONS DETECTED.");
                return;
            }

            Console.WriteLine("RESULT: PASS.");
        }
    }
}

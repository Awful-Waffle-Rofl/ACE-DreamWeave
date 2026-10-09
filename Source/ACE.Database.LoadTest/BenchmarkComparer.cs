using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Database.LoadTest
{
    public enum ComparisonOutcome
    {
        /// <summary>Moved, but not past the effective threshold.</summary>
        Unchanged,

        Improved,

        Regressed,

        /// <summary>Both readings are under --minMs, where percent math is timer noise.</summary>
        BelowNoiseFloor,

        /// <summary>Too few samples behind the number to say anything. Printed, never flagged.</summary>
        InsufficientSamples,

        /// <summary>Direction is Informational - a fixture invariant or config echo. Printed, never flagged.</summary>
        NotJudged,
    }

    public class StatComparison
    {
        /// <summary>"value" for a scalar metric, "p50" / "p95" for a series metric.</summary>
        public string Statistic { get; set; }

        public double Baseline { get; set; }
        public double Candidate { get; set; }
        public double PercentChange { get; set; }

        public int? SampleCount { get; set; }

        /// <summary>The threshold actually applied, after any calibration lookup.</summary>
        public double ThresholdPercent { get; set; }

        public bool ThresholdFromCalibration { get; set; }

        public ComparisonOutcome Outcome { get; set; }

        /// <summary>A discrete repeatable fact - judged at 0% and exempt from the sample gate.</summary>
        public bool Deterministic { get; set; }

        public string Note { get; set; }
    }

    public class MetricComparison
    {
        public string Key { get; set; }
        public MetricDirection Direction { get; set; }
        public bool DirectionInferred { get; set; }

        /// <summary>A ".Passed" key: judged as a correctness gate, never on a threshold.</summary>
        public bool IsCorrectnessGate { get; set; }

        public bool CorrectnessRegressed { get; set; }
        public string CorrectnessNote { get; set; }

        public bool MissingInCandidate { get; set; }

        public List<StatComparison> Stats { get; } = new();

        public bool Regressed => Stats.Any(s => s.Outcome == ComparisonOutcome.Regressed);
        public bool Improved => Stats.Any(s => s.Outcome == ComparisonOutcome.Improved);

        /// <summary>p50 and p95 disagreed. Interesting, not a contradiction - see the printer.</summary>
        public bool Mixed => Regressed && Improved;
    }

    /// <summary>
    /// How lopsided the whole comparison is. See ComparisonResult.Drift.
    /// </summary>
    public class DriftSummary
    {
        public int JudgedStatistics { get; set; }
        public int MovedWorse { get; set; }
        public int MovedBetter { get; set; }

        /// <summary>Median signed move, positive meaning "worse", across every judged statistic.</summary>
        public double MedianSignedPercent { get; set; }

        /// <summary>Share of judged statistics that moved the bad way, 0 to 1.</summary>
        public double WorseShare => JudgedStatistics == 0 ? 0 : (double)MovedWorse / JudgedStatistics;

        /// <summary>
        /// True when the comparison looks like a whole-run effect rather than per-metric noise: a lopsided
        /// majority moving one way AND a median move big enough to matter. Both conditions are required - a
        /// suite where everything drifted 0.3% one way is not interesting.
        /// </summary>
        public bool IsLopsided { get; set; }
    }

    public class CompareOptions
    {
        /// <summary>
        /// The coarse timing floor: the threshold for any metric with no calibration entry, and the minimum for
        /// any metric that has one.
        ///
        /// 30%, not 15%, and that is a measured choice rather than a concession. Measured identical-build spreads
        /// on this machine reach 28-44% on ordinary timing metrics (and 86% on biota-roundtrip.Save p95), so a 15%
        /// gate produced one to three false regressions on every single identical-build validation pair - four
        /// pairs, four disjoint metric sets. It was not buying detection, it was buying noise.
        ///
        /// What the gate actually has to catch, taken from what this workstream really produced: wins of 8x, 14x
        /// and 6x, a statement count collapsing 2,727 to 13, and an accidentally-shipped regression of 11-19x.
        /// Every one of those clears 30% by an order of magnitude. The precision a 15% floor implied does not
        /// correspond to any failure mode this codebase has actually exhibited.
        ///
        /// So timing floors are coarse by design and catch order-of-magnitude changes. Precision on a specific
        /// path comes from a deterministic counter (see MetricValue.Deterministic), not from a tighter timing
        /// floor - a statement count detects an N+1 regression at zero noise, where wall-clock needs a 30% move.
        /// </summary>
        public double ThresholdPercent { get; set; } = 30;

        /// <summary>Below this many milliseconds on BOTH sides, percent math is timer noise.</summary>
        public double MinMs { get; set; } = 1;

        /// <summary>
        /// Fewest samples a statistic may be backed by and still be flagged. Default 10: a scalar aggregating
        /// fewer than ten operations, or a percentile over fewer than ten samples, is one reading with extra
        /// steps, and two identical builds routinely differ by more than any useful threshold on those.
        /// </summary>
        public int MinSamples { get; set; } = 10;

        /// <summary>
        /// p95 needs more samples than p50 to mean anything. Metrics.RecordSeries computes a percentile as
        /// sorted[ceil(0.95*n)-1], and ceil(0.95*n) == n for every n &lt;= 19 - so below n=20 the "p95" IS the
        /// single largest sample. Judging a regression on one outlier is exactly the failure this gate exists
        /// to prevent, so p95 is only flagged at n &gt;= 20.
        /// </summary>
        public int MinSamplesForP95 { get; set; } = 20;

        /// <summary>Per-metric noise floors from `calibrate`. Null means "use ThresholdPercent everywhere".</summary>
        public CalibrationProfile Calibration { get; set; }

        /// <summary>
        /// Calibrated floors are multiplied by this. K runs observe a range, not the true range, so the observed
        /// spread is a lower bound on how far two identical builds can drift.
        /// </summary>
        public double CalibrationSafetyFactor { get; set; } = 1.5;

        /// <summary>
        /// A calibrated threshold is never allowed below this. Guards against overfitting to a handful of runs
        /// that happened to agree closely. Only reachable when CalibrationCanTighten is on.
        /// </summary>
        public double MinCalibratedThresholdPercent { get; set; } = 5;

        /// <summary>Share of judged statistics that must move the same way before drift is called lopsided.</summary>
        public double DriftLopsidedShare { get; set; } = 0.75;

        /// <summary>Median signed move that must also be exceeded before drift is called lopsided.</summary>
        public double DriftMedianPercent { get; set; } = 5;

        /// <summary>
        /// Whether a calibration entry may make a metric's threshold TIGHTER than ThresholdPercent. Off by
        /// default, and the default is not caution for its own sake - it is what the calibration data said.
        ///
        /// Measured on this harness (2026-07-29, 10 identical-build suite runs):
        /// `character-list-scaling.GetCharacter (all calls)` p50 ranged 4.190-4.324ms across the six calibration
        /// runs, a 3.1% spread that would have set a very tight threshold. Across all ten runs of the same build
        /// it ranged 4.100-4.680ms - 13.8%, about 4.5x wider. The observed range over K runs is a badly biased
        /// estimator of a distribution with occasional outliers, and it is biased in the dangerous direction:
        /// too narrow, producing exactly the false regression this whole change exists to eliminate.
        ///
        /// So calibration is used only to WIDEN the band on genuinely noisy metrics, which is the defect it was
        /// added to fix. Turn this on only with a K large enough that the widest per-metric spread has stopped
        /// growing between runs - and check that against the retained per-run reports, not by assumption.
        /// </summary>
        public bool CalibrationCanTighten { get; set; }
    }

    public class ComparisonResult
    {
        public string BaselineLabel { get; set; }
        public string CandidateLabel { get; set; }
        public DateTime BaselineTimestampUtc { get; set; }
        public DateTime CandidateTimestampUtc { get; set; }

        public List<MetricComparison> Metrics { get; } = new();

        /// <summary>Things the reader must see: inferred directions, missing sample counts, stale calibration.</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>A correctness gate flipped from passing to failing. Overrides every performance verdict.</summary>
        public bool HardFail { get; set; }

        public IEnumerable<MetricComparison> RegressedMetrics => Metrics.Where(m => m.Regressed);
        public IEnumerable<MetricComparison> ImprovedMetrics => Metrics.Where(m => m.Improved && !m.Regressed);

        public IEnumerable<StatComparison> UnjudgeableStats =>
            Metrics.SelectMany(m => m.Stats).Where(s => s.Outcome == ComparisonOutcome.InsufficientSamples);

        /// <summary>
        /// Whole-run drift: how many judged statistics moved the bad way, how many moved the good way, and the
        /// median signed move. Per-metric noise floors model each metric drifting independently, and that is not
        /// the only thing that happens - a whole suite run can land on a machine that is uniformly slower, and
        /// then many unrelated metrics cross their thresholds at once.
        ///
        /// Measured here 2026-07-29: across eight identical-build suite runs, seven clustered within 3.2% of each
        /// other and one came in 12.8% slower on the median statistic, with 34 of 40 statistics moving the same
        /// way. Compared against a neighbour that alone produced five "regressions".
        ///
        /// This is reported, never acted on. It cannot suppress a verdict, because a real across-the-board
        /// regression looks exactly the same and hiding it would be worse than the false positive.
        /// </summary>
        public DriftSummary Drift { get; set; } = new();

        /// <summary>The verdict. False on any hard fail or any regressed metric.</summary>
        public bool Passed => !HardFail && !Metrics.Any(m => m.Regressed);
    }

    /// <summary>
    /// Pure comparison logic for two suite reports: no database, no console, no files. Extracted out of
    /// Program.RunCompare so it can be unit tested, because every one of the three bugs this class was written to
    /// fix (direction guessed at compare time, p95-only judging that hid a 6x p50 regression, and no sample-count
    /// or noise floor at all) shipped inside an untested private method.
    /// </summary>
    public static class BenchmarkComparer
    {
        public static ComparisonResult Compare(BenchmarkReport baseline, BenchmarkReport candidate, CompareOptions options)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            options ??= new CompareOptions();

            var result = new ComparisonResult
            {
                BaselineLabel = baseline.Label,
                CandidateLabel = candidate.Label,
                BaselineTimestampUtc = baseline.TimestampUtc,
                CandidateTimestampUtc = candidate.TimestampUtc,
            };

            foreach (var (key, baselineValue) in baseline.Metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!candidate.Metrics.TryGetValue(key, out var candidateValue))
                {
                    result.Metrics.Add(new MetricComparison { Key = key, MissingInCandidate = true });
                    continue;
                }

                if (key.EndsWith(".Passed", StringComparison.Ordinal))
                {
                    result.Metrics.Add(CompareCorrectnessGate(key, baselineValue, candidateValue, result));
                    continue;
                }

                result.Metrics.Add(ComparePerformanceMetric(key, baselineValue, candidateValue, options, result));
            }

            result.Drift = SummarizeDrift(result, options);

            return result;
        }

        /// <summary>
        /// Measures how one-sided the whole comparison is, over every statistic that carries a real direction -
        /// including ones the sample gate declined to judge, since a machine-wide slowdown moves those too and
        /// excluding them would understate the effect. Informational metrics are excluded because "worse" is
        /// undefined for them.
        /// </summary>
        private static DriftSummary SummarizeDrift(ComparisonResult result, CompareOptions options)
        {
            var moves = new List<double>();

            foreach (var metric in result.Metrics)
            {
                if (metric.IsCorrectnessGate || metric.MissingInCandidate)
                    continue;

                if (metric.Direction == MetricDirection.Informational)
                    continue;

                foreach (var stat in metric.Stats)
                {
                    // Positive means "moved the bad way", whichever way that is for this metric.
                    moves.Add(metric.Direction == MetricDirection.LowerIsBetter ? stat.PercentChange : -stat.PercentChange);
                }
            }

            var summary = new DriftSummary
            {
                JudgedStatistics = moves.Count,
                MovedWorse = moves.Count(m => m > 0),
                MovedBetter = moves.Count(m => m <= 0),
            };

            if (moves.Count > 0)
            {
                var sorted = moves.OrderBy(x => x).ToList();
                summary.MedianSignedPercent = sorted.Count % 2 == 1
                    ? sorted[sorted.Count / 2]
                    : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

                var share = Math.Max(summary.WorseShare, 1 - summary.WorseShare);

                summary.IsLopsided = share >= options.DriftLopsidedShare
                                  && Math.Abs(summary.MedianSignedPercent) >= options.DriftMedianPercent;
            }

            return summary;
        }

        /// <summary>
        /// Correctness gates are judged exactly as they always were: a gate that was passing (1) and no longer is
        /// is a hard fail regardless of any threshold, and nothing else about a gate can fail the run.
        /// </summary>
        private static MetricComparison CompareCorrectnessGate(
            string key, MetricValue baselineValue, MetricValue candidateValue, ComparisonResult result)
        {
            var comparison = new MetricComparison { Key = key, IsCorrectnessGate = true, Direction = MetricDirection.HigherIsBetter };

            var wasPassing = baselineValue.Value == 1;
            var isPassing = candidateValue.Value == 1;

            if (wasPassing && !isPassing)
            {
                comparison.CorrectnessRegressed = true;
                comparison.CorrectnessNote = "REGRESSION: correctness check now FAILING (was passing)";
                result.HardFail = true;
            }
            else if (isPassing)
            {
                comparison.CorrectnessNote = wasPassing ? "still passing" : "now passing (was failing at baseline)";
            }
            else
            {
                comparison.CorrectnessNote = "still FAILING (was already failing at baseline)";
            }

            return comparison;
        }

        private static MetricComparison ComparePerformanceMetric(
            string key, MetricValue baselineValue, MetricValue candidateValue, CompareOptions options, ComparisonResult result)
        {
            var (direction, inferred) = ResolveDirection(key, baselineValue, candidateValue);

            var comparison = new MetricComparison { Key = key, Direction = direction, DirectionInferred = inferred };

            if (inferred)
            {
                result.Warnings.Add(
                    $"direction not recorded for '{key}' - guessed {direction} from its name (stale report; re-record the baseline)");
            }

            var isSeries = baselineValue.P50.HasValue || baselineValue.P95.HasValue
                        || candidateValue.P50.HasValue || candidateValue.P95.HasValue;

            var statistics = isSeries ? new[] { "p50", "p95" } : new[] { "value" };

            var sampleCount = ResolveSampleCount(baselineValue, candidateValue);

            if (sampleCount == null)
            {
                result.Warnings.Add(
                    $"sample count not recorded for '{key}' - judged anyway, the minimum-sample gate cannot protect it (stale report)");
            }

            foreach (var statistic in statistics)
            {
                var baselineStat = ReadStatistic(baselineValue, statistic);
                var candidateStat = ReadStatistic(candidateValue, statistic);

                if (baselineStat == null || candidateStat == null || baselineStat.Value == 0)
                    continue;

                // A metric only counts as deterministic if BOTH reports say so - a stale report that predates the
                // field must not have determinism assumed on its behalf.
                var deterministic = baselineValue.Deterministic && candidateValue.Deterministic;

                var stat = new StatComparison
                {
                    Statistic = statistic,
                    Baseline = baselineStat.Value,
                    Candidate = candidateStat.Value,
                    PercentChange = (candidateStat.Value - baselineStat.Value) / baselineStat.Value * 100,
                    SampleCount = sampleCount,
                    Deterministic = deterministic,
                };

                if (deterministic)
                {
                    // Any change at all is a change. No calibration, no threshold, no sample gate.
                    stat.ThresholdPercent = 0;
                    stat.ThresholdFromCalibration = false;
                }
                else
                {
                    stat.ThresholdPercent = ResolveThreshold(key, statistic, options, out var fromCalibration);
                    stat.ThresholdFromCalibration = fromCalibration;
                }

                stat.Outcome = Judge(stat, direction, sampleCount, statistic, options, out var note);
                stat.Note = note;

                comparison.Stats.Add(stat);
            }

            return comparison;
        }

        private static ComparisonOutcome Judge(
            StatComparison stat, MetricDirection direction, int? sampleCount, string statistic,
            CompareOptions options, out string note)
        {
            note = null;

            if (direction == MetricDirection.Informational)
            {
                note = "informational - not a performance signal, never flagged";
                return ComparisonOutcome.NotJudged;
            }

            // A deterministic metric carries no timing noise, so neither the sample gate nor the millisecond
            // floor applies to it - n says nothing about its reliability, and it is not a duration.
            if (stat.Deterministic)
            {
                note = "deterministic - any change is flagged";

                var move = direction == MetricDirection.LowerIsBetter ? stat.PercentChange : -stat.PercentChange;

                if (move > 0)
                    return ComparisonOutcome.Regressed;

                return move < 0 ? ComparisonOutcome.Improved : ComparisonOutcome.Unchanged;
            }

            var requiredSamples = statistic == "p95"
                ? Math.Max(options.MinSamples, options.MinSamplesForP95)
                : options.MinSamples;

            if (sampleCount != null && sampleCount.Value < requiredSamples)
            {
                note = $"insufficient samples to judge (n={sampleCount.Value}, need {requiredSamples})";
                return ComparisonOutcome.InsufficientSamples;
            }

            // Below minMs, percent math is meaningless (0.01ms -> 0.03ms is "+200%" and pure timer noise) -
            // still reported, just never flagged as a regression/improvement.
            var belowNoiseFloor = stat.Baseline < options.MinMs && stat.Candidate < options.MinMs;

            if (belowNoiseFloor)
            {
                if (Math.Abs(stat.PercentChange) > stat.ThresholdPercent)
                {
                    note = $"below the {options.MinMs:N0}ms floor, ignored";
                    return ComparisonOutcome.BelowNoiseFloor;
                }

                return ComparisonOutcome.Unchanged;
            }

            // The whole point of Defect 1: which sign is bad depends on the metric, and the metric says so.
            var badWayMove = direction == MetricDirection.LowerIsBetter ? stat.PercentChange : -stat.PercentChange;

            if (badWayMove > stat.ThresholdPercent)
                return ComparisonOutcome.Regressed;

            if (badWayMove < -stat.ThresholdPercent)
                return ComparisonOutcome.Improved;

            return ComparisonOutcome.Unchanged;
        }

        private static double ResolveThreshold(string key, string statistic, CompareOptions options, out bool fromCalibration)
        {
            fromCalibration = false;

            if (options.Calibration != null && options.Calibration.TryGetEntry(key, statistic, out var entry))
            {
                var calibrated = Math.Max(entry.ObservedSpreadPercent * options.CalibrationSafetyFactor,
                                          options.MinCalibratedThresholdPercent);

                // See CompareOptions.CalibrationCanTighten: by default calibration only widens.
                if (!options.CalibrationCanTighten)
                    calibrated = Math.Max(calibrated, options.ThresholdPercent);

                fromCalibration = calibrated != options.ThresholdPercent;
                return calibrated;
            }

            return options.ThresholdPercent;
        }

        /// <summary>
        /// The smaller of the two reports' sample counts, or null if either side does not record one. Null means
        /// "unknown", never "one" - a report written before the Count field existed must not be silently treated
        /// as an n=1 measurement.
        /// </summary>
        public static int? ResolveSampleCount(MetricValue baseline, MetricValue candidate)
        {
            if (baseline.Count == null || candidate.Count == null)
                return null;

            return Math.Min(baseline.Count.Value, candidate.Count.Value);
        }

        public static double? ReadStatistic(MetricValue value, string statistic) => statistic switch
        {
            "value" => value.Value,
            "p50" => value.P50,
            "p95" => value.P95,
            _ => null,
        };

        /// <summary>
        /// Uses the recorded direction when either report has one (candidate wins if they disagree, since it is
        /// the newer build's own declaration). Only falls back to the name heuristic for reports written before
        /// the Direction field existed - and every fallback raises a warning, so a stale baseline cannot silently
        /// produce a backwards verdict.
        /// </summary>
        public static (MetricDirection direction, bool inferred) ResolveDirection(
            string key, MetricValue baseline, MetricValue candidate)
        {
            if (candidate?.Direction != null)
                return (candidate.Direction.Value, false);

            if (baseline?.Direction != null)
                return (baseline.Direction.Value, false);

            return (InferDirectionFromName(key), true);
        }

        /// <summary>
        /// Backward-compatibility only. Every higher-is-better metric this harness has ever recorded names its
        /// rate in the metric name ("Throughput saves per sec", "Deletes per sec"), which is what makes this a
        /// usable fallback and not a coin flip - but it IS a guess, and callers warn about it.
        /// </summary>
        public static MetricDirection InferDirectionFromName(string key)
        {
            var name = (key ?? string.Empty).ToLowerInvariant();

            string[] higherIsBetterTokens = { "per sec", "per second", "/sec", "throughput", "ops per", "rows per" };

            return higherIsBetterTokens.Any(token => name.Contains(token))
                ? MetricDirection.HigherIsBetter
                : MetricDirection.LowerIsBetter;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.LoadTest;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Regression tests for the ACE.Database.LoadTest `compare` verdict. Pure logic - no MySQL, no files, nothing
    /// that can skip. Every test here exists because the untested version of this code shipped a confident, wrong
    /// verdict: throughput improvements printed as REGRESSED, a 6x p50 regression hidden behind a p95 improvement,
    /// and single-sample metrics flagged as if they meant something.
    /// </summary>
    [TestClass]
    public class BenchmarkComparerTests
    {
        // ---- builders -------------------------------------------------------------------------------------

        private static BenchmarkReport Report(string label, params (string key, MetricValue value)[] metrics) =>
            new()
            {
                Label = label,
                TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metrics = metrics.ToDictionary(m => m.key, m => m.value),
            };

        private static MetricValue Scalar(double value, MetricDirection direction, int count) =>
            new() { Value = value, Direction = direction, Count = count };

        private static MetricValue Series(double p50, double p95, MetricDirection direction, int count) =>
            new() { P50 = p50, P95 = p95, Min = p50, Max = p95, Avg = (p50 + p95) / 2, Direction = direction, Count = count };

        private static MetricComparison Find(ComparisonResult result, string key) =>
            result.Metrics.Single(m => m.Key == key);

        private static StatComparison Stat(ComparisonResult result, string key, string statistic) =>
            Find(result, key).Stats.Single(s => s.Statistic == statistic);

        private static CompareOptions Options() => new() { ThresholdPercent = 15, MinSamples = 10, MinSamplesForP95 = 20 };

        // ---- Defect 1: direction was hardcoded lower-is-better ---------------------------------------------

        [TestMethod]
        public void ThroughputMetricThatIncreased_IsImproved_NotRegressed()
        {
            // The exact observed misreport: "Deletes per sec 8,129.70 -> 10,376.06 (+27.6%) REGRESSED".
            var baseline = Report("a", ("scenario.Deletes per sec", Scalar(8129.70, MetricDirection.HigherIsBetter, 100)));
            var candidate = Report("b", ("scenario.Deletes per sec", Scalar(10376.06, MetricDirection.HigherIsBetter, 100)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Improved, Stat(result, "scenario.Deletes per sec", "value").Outcome);
            Assert.IsTrue(result.Passed, "a throughput increase must not fail the run");
        }

        [TestMethod]
        public void ThroughputMetricThatDropped_IsRegressed_NotImproved()
        {
            // The mirror-image misreport: "Throughput saves per sec 1,450.29 -> 967.86 (-33.3%) IMPROVED".
            var baseline = Report("a", ("scenario.Throughput saves per sec", Scalar(1450.29, MetricDirection.HigherIsBetter, 500)));
            var candidate = Report("b", ("scenario.Throughput saves per sec", Scalar(967.86, MetricDirection.HigherIsBetter, 500)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "scenario.Throughput saves per sec", "value").Outcome);
            Assert.IsFalse(result.Passed);
        }

        [TestMethod]
        public void LatencyMetricThatIncreased_IsStillRegressed()
        {
            var baseline = Report("a", ("scenario.Wall time ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));
            var candidate = Report("b", ("scenario.Wall time ms", Scalar(150, MetricDirection.LowerIsBetter, 500)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "scenario.Wall time ms", "value").Outcome);
        }

        [TestMethod]
        public void CandidateDirection_WinsOverBaselineDirection()
        {
            var baseline = Report("a", ("m.Rate", Scalar(100, MetricDirection.LowerIsBetter, 100)));
            var candidate = Report("b", ("m.Rate", Scalar(200, MetricDirection.HigherIsBetter, 100)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(MetricDirection.HigherIsBetter, Find(result, "m.Rate").Direction);
            Assert.AreEqual(ComparisonOutcome.Improved, Stat(result, "m.Rate", "value").Outcome);
        }

        [TestMethod]
        public void StaleReportWithNoDirection_FallsBackToTheNameHeuristic_AndWarns()
        {
            var baseline = Report("a", ("scenario.Deletes per sec", new MetricValue { Value = 8000, Count = 100 }));
            var candidate = Report("b", ("scenario.Deletes per sec", new MetricValue { Value = 10000, Count = 100 }));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            var metric = Find(result, "scenario.Deletes per sec");
            Assert.IsTrue(metric.DirectionInferred);
            Assert.AreEqual(MetricDirection.HigherIsBetter, metric.Direction);
            Assert.AreEqual(ComparisonOutcome.Improved, metric.Stats.Single().Outcome);
            Assert.IsTrue(result.Warnings.Any(w => w.Contains("scenario.Deletes per sec")),
                "a guessed direction must be named in the warnings, never applied silently");
        }

        [TestMethod]
        public void NameHeuristic_DefaultsToLowerIsBetter_ForAnythingThatIsNotARate()
        {
            Assert.AreEqual(MetricDirection.HigherIsBetter, BenchmarkComparer.InferDirectionFromName("x.Throughput saves per sec"));
            Assert.AreEqual(MetricDirection.HigherIsBetter, BenchmarkComparer.InferDirectionFromName("x.Deletes per sec"));
            Assert.AreEqual(MetricDirection.LowerIsBetter, BenchmarkComparer.InferDirectionFromName("x.Wall time ms"));
            Assert.AreEqual(MetricDirection.LowerIsBetter, BenchmarkComparer.InferDirectionFromName("x.Queue wait time"));
        }

        // ---- Defect 2: series metrics were judged on p95 only ----------------------------------------------

        [TestMethod]
        public void SeriesMetric_WhoseP50RegressedWhileP95Improved_IsFlagged()
        {
            // The observed miss: a 64.5% p95 "improvement" reported while p50 regressed 6x, unmentioned.
            var baseline = Report("a", ("scenario.Login latency", Series(p50: 2.0, p95: 100.0, MetricDirection.LowerIsBetter, 200)));
            var candidate = Report("b", ("scenario.Login latency", Series(p50: 12.0, p95: 35.5, MetricDirection.LowerIsBetter, 200)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            var metric = Find(result, "scenario.Login latency");

            Assert.AreEqual(2, metric.Stats.Count, "both p50 and p95 must be compared, not just p95");
            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "scenario.Login latency", "p50").Outcome);
            Assert.AreEqual(ComparisonOutcome.Improved, Stat(result, "scenario.Login latency", "p95").Outcome);
            Assert.IsTrue(metric.Regressed, "a p50 regression must count even when p95 improved");
            Assert.IsTrue(metric.Mixed, "opposite moves on the two percentiles must be surfaced, not hidden");
            Assert.IsFalse(result.Passed);
        }

        [TestMethod]
        public void SeriesMetric_WithOnlyATailRegression_IsStillFlagged()
        {
            var baseline = Report("a", ("scenario.Save", Series(p50: 4.0, p95: 8.0, MetricDirection.LowerIsBetter, 200)));
            var candidate = Report("b", ("scenario.Save", Series(p50: 4.1, p95: 20.0, MetricDirection.LowerIsBetter, 200)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Unchanged, Stat(result, "scenario.Save", "p50").Outcome);
            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "scenario.Save", "p95").Outcome);
            Assert.IsFalse(result.Passed);
        }

        [TestMethod]
        public void SeriesMetric_ImprovingOnBothPercentiles_Passes()
        {
            var baseline = Report("a", ("scenario.Save", Series(p50: 10.0, p95: 20.0, MetricDirection.LowerIsBetter, 200)));
            var candidate = Report("b", ("scenario.Save", Series(p50: 5.0, p95: 9.0, MetricDirection.LowerIsBetter, 200)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            var metric = Find(result, "scenario.Save");
            Assert.IsTrue(metric.Improved);
            Assert.IsFalse(metric.Regressed);
            Assert.IsFalse(metric.Mixed);
            Assert.IsTrue(result.Passed);
        }

        // ---- Defect 3: sample count captured but ignored, no real noise floor -------------------------------

        [TestMethod]
        public void MetricWithTooFewSamples_IsNotFlaggedEitherWay()
        {
            var baseline = Report("a", ("scenario.Login latency cold first read ms", Scalar(11.16, MetricDirection.LowerIsBetter, 1)));
            var candidate = Report("b", ("scenario.Login latency cold first read ms", Scalar(48.00, MetricDirection.LowerIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            var stat = Stat(result, "scenario.Login latency cold first read ms", "value");

            Assert.AreEqual(ComparisonOutcome.InsufficientSamples, stat.Outcome);
            Assert.IsTrue(stat.Note.Contains("n=1"), "the sample gap must be visible, not silently dropped");
            Assert.IsTrue(result.Passed, "an n=1 metric must not fail a run");
            Assert.AreEqual(1, result.UnjudgeableStats.Count());
        }

        [TestMethod]
        public void MetricWithTooFewSamples_IsNotReportedAsAnImprovementEither()
        {
            var baseline = Report("a", ("scenario.Something ms", Scalar(100, MetricDirection.LowerIsBetter, 5)));
            var candidate = Report("b", ("scenario.Something ms", Scalar(10, MetricDirection.LowerIsBetter, 5)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            var metric = Find(result, "scenario.Something ms");
            Assert.IsFalse(metric.Improved);
            Assert.IsFalse(metric.Regressed);
        }

        [TestMethod]
        public void P95_BelowTwentySamples_IsNotJudged_WhileP50Is()
        {
            // Metrics.RecordSeries computes a percentile as sorted[ceil(0.95*n)-1], which equals the LARGEST
            // sample for every n <= 19. A "p95" that is literally the max is one outlier, so it is not judged;
            // p50 over the same 15 samples still is.
            var baseline = Report("a", ("scenario.Login latency", Series(p50: 2.0, p95: 4.0, MetricDirection.LowerIsBetter, 15)));
            var candidate = Report("b", ("scenario.Login latency", Series(p50: 12.0, p95: 40.0, MetricDirection.LowerIsBetter, 15)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "scenario.Login latency", "p50").Outcome);

            var p95 = Stat(result, "scenario.Login latency", "p95");
            Assert.AreEqual(ComparisonOutcome.InsufficientSamples, p95.Outcome);
            Assert.IsTrue(p95.Note.Contains("need 20"));
        }

        [TestMethod]
        public void SmallerOfTheTwoSampleCounts_Governs()
        {
            var baseline = Report("a", ("scenario.X ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));
            var candidate = Report("b", ("scenario.X ms", Scalar(200, MetricDirection.LowerIsBetter, 3)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.InsufficientSamples, Stat(result, "scenario.X ms", "value").Outcome);
        }

        [TestMethod]
        public void MissingSampleCount_IsJudgedButWarnedAbout()
        {
            var baseline = Report("a", ("scenario.X ms", new MetricValue { Value = 100, Direction = MetricDirection.LowerIsBetter }));
            var candidate = Report("b", ("scenario.X ms", new MetricValue { Value = 200, Direction = MetricDirection.LowerIsBetter }));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "scenario.X ms", "value").Outcome);
            Assert.IsTrue(result.Warnings.Any(w => w.Contains("sample count")));
        }

        [TestMethod]
        public void BothReadingsBelowMinMs_AreNotFlagged()
        {
            var baseline = Report("a", ("scenario.Warm read", Series(p50: 0.01, p95: 0.01, MetricDirection.LowerIsBetter, 200)));
            var candidate = Report("b", ("scenario.Warm read", Series(p50: 0.03, p95: 0.03, MetricDirection.LowerIsBetter, 200)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.BelowNoiseFloor, Stat(result, "scenario.Warm read", "p50").Outcome);
            Assert.IsTrue(result.Passed);
        }

        // ---- Calibration ------------------------------------------------------------------------------------

        [TestMethod]
        public void CalibratedNoiseFloor_ReplacesTheGlobalThreshold()
        {
            var options = Options();
            options.CalibrationSafetyFactor = 1.0;
            options.Calibration = new CalibrationProfile
            {
                Machine = "test",
                Runs = 5,
                Entries = new Dictionary<string, CalibrationEntry>
                {
                    [CalibrationProfile.MakeKey("scenario.Queue wait time", "p50")] =
                        new() { Statistic = "p50", Runs = 5, Min = 200, Median = 250, Max = 350, ObservedSpreadPercent = 60 },
                },
            };

            var baseline = Report("a", ("scenario.Queue wait time", Series(p50: 200, p95: 400, MetricDirection.LowerIsBetter, 500)));
            var candidate = Report("b", ("scenario.Queue wait time", Series(p50: 260, p95: 410, MetricDirection.LowerIsBetter, 500)));

            var result = BenchmarkComparer.Compare(baseline, candidate, options);

            var p50 = Stat(result, "scenario.Queue wait time", "p50");
            Assert.IsTrue(p50.ThresholdFromCalibration);
            Assert.AreEqual(60, p50.ThresholdPercent, 0.001);
            Assert.AreEqual(ComparisonOutcome.Unchanged, p50.Outcome, "+30% is inside this metric's measured 60% noise band");

            var p95 = Stat(result, "scenario.Queue wait time", "p95");
            Assert.IsFalse(p95.ThresholdFromCalibration, "a statistic with no calibration entry falls back to --threshold");
            Assert.AreEqual(15, p95.ThresholdPercent, 0.001);
        }

        [TestMethod]
        public void CalibratedNoiseFloor_IsNeverAllowedBelowTheMinimum()
        {
            var options = Options();
            options.CalibrationSafetyFactor = 1.0;
            options.MinCalibratedThresholdPercent = 5;
            options.CalibrationCanTighten = true;
            options.Calibration = new CalibrationProfile
            {
                Machine = "test",
                Runs = 5,
                Entries = new Dictionary<string, CalibrationEntry>
                {
                    [CalibrationProfile.MakeKey("scenario.X ms", "value")] =
                        new() { Statistic = "value", Runs = 5, ObservedSpreadPercent = 0.2, Median = 100 },
                },
            };

            var baseline = Report("a", ("scenario.X ms", Scalar(100, MetricDirection.LowerIsBetter, 100)));
            var candidate = Report("b", ("scenario.X ms", Scalar(103, MetricDirection.LowerIsBetter, 100)));

            var result = BenchmarkComparer.Compare(baseline, candidate, options);

            var stat = Stat(result, "scenario.X ms", "value");
            Assert.AreEqual(5, stat.ThresholdPercent, 0.001);
            Assert.AreEqual(ComparisonOutcome.Unchanged, stat.Outcome);
        }

        [TestMethod]
        public void CalibrationByDefault_OnlyWidens_NeverTightens()
        {
            // A K-run observed range is a biased-narrow estimate of the true noise band (measured: a metric whose
            // six calibration runs spread 3.1% spread 13.8% over ten runs of the same build), so a tight
            // calibration entry must not be allowed to make the gate more sensitive than --threshold.
            var options = Options();
            options.CalibrationSafetyFactor = 1.0;
            options.Calibration = new CalibrationProfile
            {
                Machine = "test",
                Runs = 6,
                Entries = new Dictionary<string, CalibrationEntry>
                {
                    [CalibrationProfile.MakeKey("scenario.X ms", "value")] =
                        new() { Statistic = "value", Runs = 6, ObservedSpreadPercent = 3.1, Median = 100 },
                },
            };

            var baseline = Report("a", ("scenario.X ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));
            var candidate = Report("b", ("scenario.X ms", Scalar(114, MetricDirection.LowerIsBetter, 500)));

            var stat = Stat(BenchmarkComparer.Compare(baseline, candidate, options), "scenario.X ms", "value");

            Assert.AreEqual(15, stat.ThresholdPercent, 0.001, "a 3.1% calibrated spread must not tighten past --threshold");
            Assert.IsFalse(stat.ThresholdFromCalibration, "the threshold in force here came from --threshold, and must say so");
            Assert.AreEqual(ComparisonOutcome.Unchanged, stat.Outcome);
        }

        [TestMethod]
        public void CalibrationCanTighten_IsAvailableAsAnOptIn()
        {
            var options = Options();
            options.CalibrationSafetyFactor = 1.0;
            options.MinCalibratedThresholdPercent = 1;
            options.CalibrationCanTighten = true;
            options.Calibration = new CalibrationProfile
            {
                Machine = "test",
                Runs = 6,
                Entries = new Dictionary<string, CalibrationEntry>
                {
                    [CalibrationProfile.MakeKey("scenario.X ms", "value")] =
                        new() { Statistic = "value", Runs = 6, ObservedSpreadPercent = 3.1, Median = 100 },
                },
            };

            var baseline = Report("a", ("scenario.X ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));
            var candidate = Report("b", ("scenario.X ms", Scalar(114, MetricDirection.LowerIsBetter, 500)));

            var stat = Stat(BenchmarkComparer.Compare(baseline, candidate, options), "scenario.X ms", "value");

            Assert.AreEqual(3.1, stat.ThresholdPercent, 0.001);
            Assert.IsTrue(stat.ThresholdFromCalibration);
            Assert.AreEqual(ComparisonOutcome.Regressed, stat.Outcome);
        }

        [TestMethod]
        public void CalibrationProfile_MeasuresObservedSpreadAcrossRuns()
        {
            var reports = new List<BenchmarkReport>
            {
                Report("r1", ("s.X ms", Scalar(100, MetricDirection.LowerIsBetter, 50))),
                Report("r2", ("s.X ms", Scalar(120, MetricDirection.LowerIsBetter, 50))),
                Report("r3", ("s.X ms", Scalar(110, MetricDirection.LowerIsBetter, 50))),
            };

            var profile = CalibrationProfile.Build(reports, "cal", "test-machine");

            Assert.IsTrue(profile.TryGetEntry("s.X ms", "value", out var entry));
            Assert.AreEqual(100, entry.Min, 0.001);
            Assert.AreEqual(110, entry.Median, 0.001);
            Assert.AreEqual(120, entry.Max, 0.001);
            Assert.AreEqual((120 - 100) / 110.0 * 100, entry.ObservedSpreadPercent, 0.001);
            Assert.AreEqual(3, entry.Runs);
        }

        [TestMethod]
        public void CalibrationProfile_SkipsCorrectnessGates()
        {
            var reports = new List<BenchmarkReport>
            {
                Report("r1", ("s.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1))),
                Report("r2", ("s.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1))),
            };

            var profile = CalibrationProfile.Build(reports, "cal", "test-machine");

            Assert.AreEqual(0, profile.Entries.Count, "a pass/fail gate has no noise floor");
        }

        // ---- Deterministic metrics --------------------------------------------------------------------------

        private static MetricValue Deterministic(double value, MetricDirection direction, int count) =>
            new() { Value = value, Direction = direction, Count = count, Deterministic = true };

        [TestMethod]
        public void DeterministicMetric_IsJudgedAtNSmallerThanTheSampleGate()
        {
            // possession-load's SQL statement count is n=1 and read exactly 68 six times running. The sample gate
            // exists to stop timing noise being judged; a statement count has none, so n must not silence it.
            var baseline = Report("a", ("s.Possession read SQL statements", Deterministic(68, MetricDirection.LowerIsBetter, 1)));
            var candidate = Report("b", ("s.Possession read SQL statements", Deterministic(612, MetricDirection.LowerIsBetter, 1)));

            var stat = Stat(BenchmarkComparer.Compare(baseline, candidate, Options()), "s.Possession read SQL statements", "value");

            Assert.AreEqual(ComparisonOutcome.Regressed, stat.Outcome, "an N+1 regression at n=1 must still be caught");
            Assert.AreEqual(0, stat.ThresholdPercent, 0.001);
        }

        [TestMethod]
        public void DeterministicMetric_FlagsAnyChangeAtAll()
        {
            // Well inside the default 15% threshold, and still a regression: the number is supposed to be exact.
            var baseline = Report("a", ("s.Statements", Deterministic(68, MetricDirection.LowerIsBetter, 1)));
            var candidate = Report("b", ("s.Statements", Deterministic(69, MetricDirection.LowerIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Regressed, Stat(result, "s.Statements", "value").Outcome);
            Assert.IsFalse(result.Passed);
        }

        [TestMethod]
        public void DeterministicMetric_Unchanged_Passes()
        {
            var baseline = Report("a", ("s.Statements", Deterministic(68, MetricDirection.LowerIsBetter, 1)));
            var candidate = Report("b", ("s.Statements", Deterministic(68, MetricDirection.LowerIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Unchanged, Stat(result, "s.Statements", "value").Outcome);
            Assert.IsTrue(result.Passed);
        }

        [TestMethod]
        public void DeterministicMetric_FewerIsAnImprovement()
        {
            var baseline = Report("a", ("s.Statements", Deterministic(612, MetricDirection.LowerIsBetter, 1)));
            var candidate = Report("b", ("s.Statements", Deterministic(68, MetricDirection.LowerIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Improved, Stat(result, "s.Statements", "value").Outcome);
            Assert.IsTrue(result.Passed);
        }

        [TestMethod]
        public void Determinism_IsNotAssumedForAStaleReportThatLacksTheField()
        {
            // Only one side declares it. A report written before the field existed must not have determinism
            // assumed on its behalf, or ordinary noise becomes a permanent false regression.
            var baseline = Report("a", ("s.Statements", new MetricValue { Value = 68, Direction = MetricDirection.LowerIsBetter, Count = 1 }));
            var candidate = Report("b", ("s.Statements", Deterministic(69, MetricDirection.LowerIsBetter, 1)));

            var stat = Stat(BenchmarkComparer.Compare(baseline, candidate, Options()), "s.Statements", "value");

            Assert.IsFalse(stat.Deterministic);
            Assert.AreEqual(ComparisonOutcome.InsufficientSamples, stat.Outcome);
        }

        // ---- Whole-run drift diagnostic ---------------------------------------------------------------------

        [TestMethod]
        public void EverythingMovingTheSameWay_IsReportedAsLopsidedDrift()
        {
            // Measured signature of a machine-level slowdown: 34 of 40 statistics moved the bad way at once.
            var baseline = Report("a",
                ("s.A ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.D per sec", Scalar(100, MetricDirection.HigherIsBetter, 500)));

            var candidate = Report("b",
                ("s.A ms", Scalar(113, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(112, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(114, MetricDirection.LowerIsBetter, 500)),
                ("s.D per sec", Scalar(88, MetricDirection.HigherIsBetter, 500)));

            var drift = BenchmarkComparer.Compare(baseline, candidate, Options()).Drift;

            Assert.AreEqual(4, drift.JudgedStatistics);
            Assert.AreEqual(4, drift.MovedWorse);
            Assert.IsTrue(drift.IsLopsided);
            Assert.IsTrue(drift.MedianSignedPercent > 5);
        }

        [TestMethod]
        public void LopsidedDrift_DoesNotSuppressTheRegressionVerdict()
        {
            // A genuine across-the-board regression is indistinguishable from a slow machine, so drift must
            // never be allowed to turn a FAIL into a PASS.
            var baseline = Report("a",
                ("s.A ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.D ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));

            var candidate = Report("b",
                ("s.A ms", Scalar(140, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(150, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(160, MetricDirection.LowerIsBetter, 500)),
                ("s.D ms", Scalar(170, MetricDirection.LowerIsBetter, 500)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.IsTrue(result.Drift.IsLopsided);
            Assert.AreEqual(4, result.RegressedMetrics.Count());
            Assert.IsFalse(result.Passed, "drift is a diagnostic, never a suppressor");
        }

        [TestMethod]
        public void BalancedMovement_IsNotReportedAsDrift()
        {
            var baseline = Report("a",
                ("s.A ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.D ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));

            var candidate = Report("b",
                ("s.A ms", Scalar(104, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(97, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(103, MetricDirection.LowerIsBetter, 500)),
                ("s.D ms", Scalar(96, MetricDirection.LowerIsBetter, 500)));

            var drift = BenchmarkComparer.Compare(baseline, candidate, Options()).Drift;

            Assert.IsFalse(drift.IsLopsided);
        }

        [TestMethod]
        public void SmallUniformMovement_IsNotReportedAsDrift()
        {
            // All one way, but tiny - the seven clustered calibration runs looked like this and are not events.
            var baseline = Report("a",
                ("s.A ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(100, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));

            var candidate = Report("b",
                ("s.A ms", Scalar(101, MetricDirection.LowerIsBetter, 500)),
                ("s.B ms", Scalar(100.5, MetricDirection.LowerIsBetter, 500)),
                ("s.C ms", Scalar(101.5, MetricDirection.LowerIsBetter, 500)));

            var drift = BenchmarkComparer.Compare(baseline, candidate, Options()).Drift;

            Assert.AreEqual(3, drift.MovedWorse);
            Assert.IsFalse(drift.IsLopsided, "a uniform sub-threshold move is not an event");
        }

        // ---- The .Passed correctness gate, preserved exactly -------------------------------------------------

        [TestMethod]
        public void CorrectnessGate_FlippingFromPassingToFailing_IsAHardFail()
        {
            var baseline = Report("a", ("integrity-check.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1)));
            var candidate = Report("b", ("integrity-check.Passed", Scalar(0, MetricDirection.HigherIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.IsTrue(result.HardFail);
            Assert.IsFalse(result.Passed);
            Assert.IsTrue(Find(result, "integrity-check.Passed").CorrectnessRegressed);
        }

        [TestMethod]
        public void CorrectnessGate_IsJudgedRegardlessOfSampleCountOrThreshold()
        {
            // n=1 and a huge threshold must not buy a correctness regression a pass.
            var options = Options();
            options.ThresholdPercent = 10000;
            options.MinSamples = 1000;

            var baseline = Report("a", ("integrity-check.DeleteCascade.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1)));
            var candidate = Report("b", ("integrity-check.DeleteCascade.Passed", Scalar(0, MetricDirection.HigherIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, options);

            Assert.IsTrue(result.HardFail);
            Assert.IsFalse(result.Passed);
        }

        [TestMethod]
        public void CorrectnessGate_StillPassing_IsNotAFailure()
        {
            var baseline = Report("a", ("integrity-check.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1)));
            var candidate = Report("b", ("integrity-check.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.IsFalse(result.HardFail);
            Assert.IsTrue(result.Passed);
            Assert.AreEqual("still passing", Find(result, "integrity-check.Passed").CorrectnessNote);
        }

        [TestMethod]
        public void CorrectnessGate_GoingFromFailingToPassing_IsNotAFailure()
        {
            var baseline = Report("a", ("integrity-check.Passed", Scalar(0, MetricDirection.HigherIsBetter, 1)));
            var candidate = Report("b", ("integrity-check.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.IsFalse(result.HardFail);
            Assert.IsTrue(result.Passed);
        }

        [TestMethod]
        public void CorrectnessGate_AlreadyFailingAtBaseline_IsNotANewHardFail()
        {
            // Preserved exactly from the original: only a passing -> failing FLIP is a hard fail.
            var baseline = Report("a", ("integrity-check.Passed", Scalar(0, MetricDirection.HigherIsBetter, 1)));
            var candidate = Report("b", ("integrity-check.Passed", Scalar(0, MetricDirection.HigherIsBetter, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.IsFalse(result.HardFail);
            Assert.IsTrue(result.Passed);
        }

        [TestMethod]
        public void CorrectnessGate_HardFail_OutranksEveryPerformanceImprovement()
        {
            var baseline = Report("a",
                ("integrity-check.Passed", Scalar(1, MetricDirection.HigherIsBetter, 1)),
                ("scenario.Wall time ms", Scalar(100, MetricDirection.LowerIsBetter, 500)));

            var candidate = Report("b",
                ("integrity-check.Passed", Scalar(0, MetricDirection.HigherIsBetter, 1)),
                ("scenario.Wall time ms", Scalar(10, MetricDirection.LowerIsBetter, 500)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.Improved, Stat(result, "scenario.Wall time ms", "value").Outcome);
            Assert.IsTrue(result.HardFail);
            Assert.IsFalse(result.Passed, "a faster-but-wrong change must never pass");
        }

        // ---- Everything else --------------------------------------------------------------------------------

        [TestMethod]
        public void InformationalMetric_IsNeverFlagged()
        {
            var baseline = Report("a", ("scenario.Possessions loaded", Scalar(303, MetricDirection.Informational, 1)));
            var candidate = Report("b", ("scenario.Possessions loaded", Scalar(12, MetricDirection.Informational, 1)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(ComparisonOutcome.NotJudged, Stat(result, "scenario.Possessions loaded", "value").Outcome);
            Assert.IsTrue(result.Passed);
        }

        [TestMethod]
        public void MetricMissingFromTheCandidate_IsReportedButIsNotARegression()
        {
            var baseline = Report("a", ("scenario.Gone ms", Scalar(100, MetricDirection.LowerIsBetter, 100)));
            var candidate = Report("b", ("scenario.Other ms", Scalar(100, MetricDirection.LowerIsBetter, 100)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.IsTrue(Find(result, "scenario.Gone ms").MissingInCandidate);
            Assert.IsTrue(result.Passed);
        }

        [TestMethod]
        public void ZeroBaseline_IsSkippedRatherThanDividedBy()
        {
            var baseline = Report("a", ("scenario.X ms", Scalar(0, MetricDirection.LowerIsBetter, 100)));
            var candidate = Report("b", ("scenario.X ms", Scalar(50, MetricDirection.LowerIsBetter, 100)));

            var result = BenchmarkComparer.Compare(baseline, candidate, Options());

            Assert.AreEqual(0, Find(result, "scenario.X ms").Stats.Count);
            Assert.IsTrue(result.Passed);
        }
    }
}

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: SlowTickDetector's rules, in isolation. Pure - no clock, no PropertyManager.
    /// </summary>
    [TestClass]
    public class SlowTickDetectorTests
    {
        private static readonly SlowTickSettings Default = new SlowTickSettings(true, 100, 3.0, 10_000);

        private static SlowTickDetector WithSamples(int count, double value)
        {
            var d = new SlowTickDetector();

            for (var i = 0; i < count; i++)
                d.AddSample(value);

            return d;
        }

        [TestMethod]
        public void BelowTheFloorIsNeverSlow()
        {
            var d = WithSamples(100, 1);

            Assert.AreEqual(SlowTickVerdict.None, d.Evaluate(99.9, 0, in Default).Verdict);
        }

        [TestMethod]
        public void AboveTheFloorButUnderMultiplierTimesMedianIsNotSlow()
        {
            var d = WithSamples(100, 50); // median 50 -> relative bar 150

            Assert.AreEqual(SlowTickVerdict.None, d.Evaluate(140, 0, in Default).Verdict);
            Assert.AreEqual(SlowTickVerdict.Emit, d.Evaluate(150, 0, in Default).Verdict);
        }

        [TestMethod]
        public void MultiplierZeroMeansFloorOnly()
        {
            var d = WithSamples(100, 90);
            var floorOnly = new SlowTickSettings(true, 100, 0, 10_000);

            Assert.AreEqual(SlowTickVerdict.Emit, d.Evaluate(101, 0, in floorOnly).Verdict);
        }

        [TestMethod]
        public void UnderThirtyTwoSamplesTheFloorAloneDecides()
        {
            var d = WithSamples(SlowTickDetector.MinSamplesForRelative - 1, 90);

            Assert.AreEqual(SlowTickVerdict.Emit, d.Evaluate(101, 0, in Default).Verdict, "31 samples: relative check skipped");

            var e = WithSamples(SlowTickDetector.MinSamplesForRelative, 90);

            Assert.AreEqual(SlowTickVerdict.None, e.Evaluate(101, 0, in Default).Verdict, "32 samples: 101 < 3 x 90");
        }

        [TestMethod]
        public void InsideTheIntervalIsSuppressedAndCounted()
        {
            var d = new SlowTickDetector();

            Assert.AreEqual(SlowTickVerdict.Emit, d.Evaluate(200, 1_000, in Default).Verdict);

            var first = d.Evaluate(300, 2_000, in Default);
            Assert.AreEqual(SlowTickVerdict.Suppressed, first.Verdict);
            Assert.AreEqual(1, first.SuppressedCount);
            Assert.AreEqual(300.0, first.SuppressedMaxMs);

            var second = d.Evaluate(250, 5_000, in Default);
            Assert.AreEqual(SlowTickVerdict.Suppressed, second.Verdict);
            Assert.AreEqual(2, second.SuppressedCount);
            Assert.AreEqual(300.0, second.SuppressedMaxMs, "max is kept, not overwritten by a smaller one");
        }

        [TestMethod]
        public void TheNextEmitCarriesTheSuppressedTotalsAndResetsThem()
        {
            var d = new SlowTickDetector();

            d.Evaluate(200, 0, in Default);
            d.Evaluate(400, 1_000, in Default);
            d.Evaluate(300, 2_000, in Default);

            var emit = d.Evaluate(150, 10_000, in Default);
            Assert.AreEqual(SlowTickVerdict.Emit, emit.Verdict, "10 s after the first emit");
            Assert.AreEqual(2, emit.SuppressedCount);
            Assert.AreEqual(400.0, emit.SuppressedMaxMs);

            var next = d.Evaluate(150, 20_000, in Default);
            Assert.AreEqual(SlowTickVerdict.Emit, next.Verdict);
            Assert.AreEqual(0, next.SuppressedCount);
            Assert.AreEqual(0.0, next.SuppressedMaxMs);
        }

        [TestMethod]
        public void NotSlowIterationsDoNotCountAsSuppressed()
        {
            var d = new SlowTickDetector();

            d.Evaluate(200, 0, in Default);
            d.Evaluate(10, 1_000, in Default);

            Assert.AreEqual(0, d.Evaluate(200, 20_000, in Default).SuppressedCount);
        }

        [TestMethod]
        public void DisabledNeverEmits()
        {
            var d = new SlowTickDetector();
            var off = new SlowTickSettings(false, 0, 0, 0);

            Assert.AreEqual(SlowTickVerdict.None, d.Evaluate(1_000_000, 0, in off).Verdict);
        }

        [TestMethod]
        public void MedianIsExactForOddAndEvenCounts()
        {
            var d = new SlowTickDetector();
            Assert.AreEqual(0.0, d.Median(), "no samples");

            d.AddSample(5);
            d.AddSample(1);
            d.AddSample(3);
            Assert.AreEqual(3.0, d.Median(), "odd: middle of 1,3,5");

            d.AddSample(10);
            Assert.AreEqual(4.0, d.Median(), "even: mean of 3 and 5");
        }

        [TestMethod]
        public void TheRingWrapsAtCapacity()
        {
            var d = WithSamples(SlowTickDetector.Capacity, 1000);

            for (var i = 0; i < SlowTickDetector.Capacity; i++)
                d.AddSample(2);

            Assert.AreEqual(SlowTickDetector.Capacity, d.SampleCount);
            Assert.AreEqual(2.0, d.Median(), "every old 1000 must have been overwritten");
        }

        [TestMethod]
        public void MedianComputationDoesNotReorderTheRing()
        {
            var d = new SlowTickDetector();
            var half = SlowTickDetector.Capacity / 2;

            // oldest half 1000s, newest half 1s: an in-place sort would move the 1s into the OLDEST slots
            for (var i = 0; i < half; i++)
                d.AddSample(1000);
            for (var i = 0; i < half; i++)
                d.AddSample(1);

            Assert.AreEqual(500.5, d.Median());

            // overwrite the oldest half. Correct ring: 1s + 500s -> 250.5. Sorted-in-place ring: 1000s + 500s -> 750.
            for (var i = 0; i < half; i++)
                d.AddSample(500);

            Assert.AreEqual(250.5, d.Median());
        }

        [TestMethod]
        public void ASpikeIsJudgedBeforeItMovesTheMedian()
        {
            var d = WithSamples(SlowTickDetector.MinSamplesForRelative, 40); // median 40 -> bar 120

            var result = d.Evaluate(130, 0, in Default);

            Assert.AreEqual(SlowTickVerdict.Emit, result.Verdict);
            Assert.AreEqual(40.0, result.MedianMs, "the spike is not part of its own median");
        }

        [TestMethod]
        public void NegativeAndNaNSettingsAreSanitised()
        {
            var s = new SlowTickSettings(true, -5, double.NaN, -1);

            Assert.AreEqual(0.0, s.ThresholdMs);
            Assert.AreEqual(0.0, s.MedianMultiplier);
            Assert.AreEqual(0L, s.MinIntervalMs);
        }

        [TestMethod]
        public void ANaNTotalIsNeverSlow()
        {
            var d = new SlowTickDetector();

            Assert.AreEqual(SlowTickVerdict.None, d.Evaluate(double.NaN, 0, in Default).Verdict);
        }
    }
}

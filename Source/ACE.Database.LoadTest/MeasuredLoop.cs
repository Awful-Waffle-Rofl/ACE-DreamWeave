using System;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Runs a measured loop with a discarded warm-up prefix.
    ///
    /// The first few iterations of any measured loop in this harness are systematically slower than the rest -
    /// JIT of the path under test, EF model and query-plan construction, connection-pool growth. Folding those
    /// into the reported numbers does not just add noise, it biases small-N measurements upward, and it does so
    /// inconsistently between runs, which is what produces a wide identical-build spread on exactly the metric a
    /// batching change most needs to be able to trust.
    ///
    /// Measured 2026-07-29 before this existed: save-batch-crossover's first measured burst (N=1) had the widest
    /// identical-build spread in the whole suite at 33.7% over ten runs, and it was the only metric to be flagged
    /// as a regression in two independent identical-build validation pairs.
    ///
    /// Deliberately NOT applied to every scenario in this change. Adopting it elsewhere changes those scenarios'
    /// numbers and invalidates their calibration entries, so it is a per-scenario decision with a re-calibration
    /// attached.
    /// </summary>
    public static class MeasuredLoop
    {
        /// <summary>
        /// Default discarded warm-up iterations. Small on purpose: the aim is to pay for first-touch costs, not
        /// to run the measurement twice.
        /// </summary>
        public const int DefaultWarmupIterations = 3;

        /// <summary>
        /// Runs warmupIterations + iterations passes of measureOne and returns only the timings from the
        /// measured ones. measureOne receives the ABSOLUTE pass index, including warm-up passes, so a caller
        /// that needs distinct ids per pass can derive them from it without colliding with its own warm-up.
        /// </summary>
        public static double[] Run(int iterations, int warmupIterations, Func<int, double> measureOne)
        {
            if (measureOne == null)
                throw new ArgumentNullException(nameof(measureOne));

            if (iterations < 1)
                throw new ArgumentOutOfRangeException(nameof(iterations), "iterations must be at least 1.");

            if (warmupIterations < 0)
                throw new ArgumentOutOfRangeException(nameof(warmupIterations), "warmupIterations cannot be negative.");

            var measured = new double[iterations];

            for (var pass = 0; pass < warmupIterations + iterations; pass++)
            {
                var elapsedMs = measureOne(pass);

                if (pass >= warmupIterations)
                    measured[pass - warmupIterations] = elapsedMs;
            }

            return measured;
        }

        /// <summary>
        /// How many passes a caller must budget id space (or any other per-pass resource) for.
        /// </summary>
        public static int TotalPasses(int iterations, int warmupIterations) => iterations + warmupIterations;
    }
}

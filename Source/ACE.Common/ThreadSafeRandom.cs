using System;
using System.Threading;

namespace ACE.Common
{
    // important class, ensure unit tests pass for this
    // todo: implement exactly the way AC handles it.. which we'll never know unless we get original source code
    public static class ThreadSafeRandom
    {
        static readonly ThreadLocal<Random> random = new ThreadLocal<Random>(() => new Random());

        /// <summary>
        /// Replaces the CALLING thread's generator with one seeded by <paramref name="seed"/>, so the draws that follow on
        /// this thread are reproducible. Offline tools only (ACE.Content.Tools uplift-matchup seeds each prepared
        /// creature's wielded-treasure rolls with it); the server never calls it, and other threads are unaffected.
        /// </summary>
        public static void Reseed(int seed)
        {
            random.Value = new Random(seed);
        }

        /// <summary>
        /// Returns a random floating-point number that is greater than or equal to 'min', and less than 'max'.
        /// </summary>
        /// <param name="min">The value returned will be greater than or equal to 'min'</param>
        /// <param name="max">The value returned will be less than 'max'</param>
        public static double Next(float min, float max)
        {
            // for ranges other than 1, (max - upper bound) will be scaled by the range
            return random.Value.NextDouble() * (max - min) + min;
        }

        /// <summary>
        /// Returns a random integer between min and max, inclusive
        /// </summary>
        /// <param name="min">The minimum possible value to return</param>
        /// <param name="max">The maximum possible value to return</param>
        public static int Next(int min, int max)
        {
            return random.Value.Next(min, max + 1);
        }

        public static double NextInterval(float qualityMod)
        {
            return Math.Max(0.0, random.Value.NextDouble() - qualityMod);
        }

        /// <summary>
        /// The maximum possible double < 1.0
        /// </summary>
        private const double maxExclusive = 0.9999999999999999;

        public static double NextIntervalMax(float qualityMod)
        {
            return Math.Min(maxExclusive, random.Value.NextDouble() + qualityMod);
        }
    }
}

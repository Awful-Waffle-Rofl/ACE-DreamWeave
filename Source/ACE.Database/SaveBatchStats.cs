using System;
using System.Text;
using System.Threading;

namespace ACE.Database
{
    /// <summary>
    /// Pure observability instrumentation for the shard database save-batch path. Records how large
    /// SaveBiotaBatch calls are, how many items land on the cache-hit (individual-commit) path vs the
    /// shared-commit path, and how large the batches actually formed by SerializedShardDatabase's queue
    /// drain are (the drain caps/breaks batches, so the caller-passed size does not always equal the size
    /// that reaches the database).
    ///
    /// Thread-safe via Interlocked against fixed pre-sized arrays. No locks, no per-call allocation - safe
    /// to call from the hot save path.
    /// </summary>
    public static class SaveBatchStats
    {
        // Bucket boundaries: 1, 2, 3-4, 5-8, 9-16, 17-32, 33-64, 65-100, over 100
        private const int BucketCount = 9;

        private static long[] _callerBatchBuckets = new long[BucketCount];
        private static long[] _drainBatchBuckets = new long[BucketCount];
        private static long _totalCalls;
        private static long _totalItems;
        private static long _totalCachedItems;
        private static long _totalNonCachedItems;
        private static long _individualCacheHitCommits;

        private static long[] _parallelCallBuckets = new long[BucketCount];
        private static long _parallelCalls;
        private static long _parallelItems;
        private static long _parallelCachedItems;

        /// <summary>
        /// Records one SaveBiotaBatch call: the total item count, and how many of those items were
        /// cache hits (individual-commit path) vs non-cached (shared-commit path).
        /// </summary>
        public static void RecordBatch(int total, int cached, int nonCached)
        {
            Interlocked.Increment(ref _totalCalls);
            Interlocked.Add(ref _totalItems, total);
            Interlocked.Add(ref _totalCachedItems, cached);
            Interlocked.Add(ref _totalNonCachedItems, nonCached);

            if (cached > 0)
                Interlocked.Add(ref _individualCacheHitCommits, cached);

            Interlocked.Increment(ref _callerBatchBuckets[GetBucketIndex(total)]);
        }

        /// <summary>
        /// Records the size of a batch actually formed by SerializedShardDatabase's queue drain.
        /// </summary>
        public static void RecordDrainBatch(int size)
        {
            Interlocked.Increment(ref _drainBatchBuckets[GetBucketIndex(size)]);
        }

        /// <summary>
        /// Records one SaveBiotasInParallel call: how many biotas a caller handed over in a single request,
        /// and how many of those were already in the biota cache.
        ///
        /// This is the section that answers the design question: what a caller INTENDED to save together,
        /// measured before any chunking or batching reshapes it. The other two histograms record what reached
        /// the database, which is a different number and cannot substitute for this one.
        /// </summary>
        public static void RecordParallelCall(int total, int cached)
        {
            Interlocked.Increment(ref _parallelCalls);
            Interlocked.Add(ref _parallelItems, total);
            Interlocked.Add(ref _parallelCachedItems, cached);

            Interlocked.Increment(ref _parallelCallBuckets[GetBucketIndex(total)]);
        }

        /// <summary>
        /// Maps a count into one of the 9 fixed buckets. Never throws - 0/negative counts clamp into the
        /// smallest bucket, arbitrarily large counts clamp into the largest.
        /// </summary>
        private static int GetBucketIndex(int count)
        {
            if (count <= 1)
                return 0;
            if (count == 2)
                return 1;
            if (count <= 4)
                return 2;
            if (count <= 8)
                return 3;
            if (count <= 16)
                return 4;
            if (count <= 32)
                return 5;
            if (count <= 64)
                return 6;
            if (count <= 100)
                return 7;

            return 8;
        }

        private static readonly string[] BucketLabels =
        {
            "1        ",
            "2        ",
            "3-4      ",
            "5-8      ",
            "9-16     ",
            "17-32    ",
            "33-64    ",
            "65-100   ",
            "over 100 "
        };

        public static void Reset()
        {
            Interlocked.Exchange(ref _callerBatchBuckets, new long[BucketCount]);
            Interlocked.Exchange(ref _drainBatchBuckets, new long[BucketCount]);

            Interlocked.Exchange(ref _totalCalls, 0);
            Interlocked.Exchange(ref _totalItems, 0);
            Interlocked.Exchange(ref _totalCachedItems, 0);
            Interlocked.Exchange(ref _totalNonCachedItems, 0);
            Interlocked.Exchange(ref _individualCacheHitCommits, 0);

            Interlocked.Exchange(ref _parallelCallBuckets, new long[BucketCount]);
            Interlocked.Exchange(ref _parallelCalls, 0);
            Interlocked.Exchange(ref _parallelItems, 0);
            Interlocked.Exchange(ref _parallelCachedItems, 0);
        }

        public static string GetReport()
        {
            // Snapshot locals - these reads are not perfectly atomic as a group under concurrent writers,
            // but that's fine for an observability report; each individual field read is a clean 64-bit read.
            var callerBuckets = _callerBatchBuckets;
            var drainBuckets = _drainBatchBuckets;

            var totalCalls = Interlocked.Read(ref _totalCalls);
            var totalItems = Interlocked.Read(ref _totalItems);
            var totalCached = Interlocked.Read(ref _totalCachedItems);
            var totalNonCached = Interlocked.Read(ref _totalNonCachedItems);
            var individualCommits = Interlocked.Read(ref _individualCacheHitCommits);

            var parallelBuckets = _parallelCallBuckets;
            var parallelCalls = Interlocked.Read(ref _parallelCalls);
            var parallelItems = Interlocked.Read(ref _parallelItems);
            var parallelCached = Interlocked.Read(ref _parallelCachedItems);

            var sb = new StringBuilder();

            sb.AppendLine("NOTE: section 1 is what CALLERS hand over in one request. Sections 2 and 3 are what");
            sb.AppendLine("reached the database after chunking and queue-drain batching reshaped it. Section 1 is");
            sb.AppendLine("the number to use for sizing decisions; it is a property of the callers, not of the");
            sb.AppendLine("save implementation, so it stays comparable across changes to the batching itself.");
            sb.AppendLine();

            sb.AppendLine("=== 1. SaveBiotasInParallel caller batch sizes (what callers hand over at once) ===");
            sb.AppendLine($"SaveBiotasInParallel calls: {parallelCalls:N0}");
            sb.AppendLine($"Total biotas handed over:   {parallelItems:N0}");
            sb.AppendLine($"  Already cached:           {parallelCached:N0}");

            var parallelCachePct = parallelItems > 0 ? (double)parallelCached / parallelItems * 100.0 : 0.0;
            sb.AppendLine($"Cache-hit ratio:            {parallelCachePct:N1}%");

            var avgParallel = parallelCalls > 0 ? (double)parallelItems / parallelCalls : 0.0;
            sb.AppendLine($"Avg biotas/call:            {avgParallel:N2}");

            sb.AppendLine();
            sb.AppendLine("Bucket        Calls");
            for (var i = 0; i < BucketCount; i++)
                sb.AppendLine($"{BucketLabels[i]} {parallelBuckets[i]:N0}");

            sb.AppendLine();
            sb.AppendLine("=== 2. SaveBiotaBatch stats (caller-passed batch size) ===");
            sb.AppendLine($"SaveBiotaBatch calls: {totalCalls:N0}");
            sb.AppendLine($"Total items:          {totalItems:N0}");
            sb.AppendLine($"  Cached (individual-commit path):     {totalCached:N0}");
            sb.AppendLine($"  Non-cached (shared-commit path):     {totalNonCached:N0}");

            var cacheHitPct = totalItems > 0 ? (double)totalCached / totalItems * 100.0 : 0.0;
            sb.AppendLine($"Cache-hit ratio:      {cacheHitPct:N1}%");

            var avgItemsPerBatch = totalCalls > 0 ? (double)totalItems / totalCalls : 0.0;
            sb.AppendLine($"Avg items/call:       {avgItemsPerBatch:N2}");
            sb.AppendLine($"Extra individual commits paid (cache hits): {individualCommits:N0}");

            sb.AppendLine();
            sb.AppendLine("Bucket        Calls");
            for (var i = 0; i < BucketCount; i++)
                sb.AppendLine($"{BucketLabels[i]} {callerBuckets[i]:N0}");

            sb.AppendLine();
            sb.AppendLine("=== 3. SerializedShardDatabase drain batch sizes (actual DB batch size) ===");
            sb.AppendLine("Bucket        Batches");
            for (var i = 0; i < BucketCount; i++)
                sb.AppendLine($"{BucketLabels[i]} {drainBuckets[i]:N0}");

            return sb.ToString();
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// SerializedShardDatabase funnels every shard write through a single worker thread and one blocking queue
    /// (ACE.Database/SerializedShardDatabase.cs). This fires `count` saves from `concurrency` independent caller
    /// threads - simulating concurrent players saving independently - and reports how much of the total latency
    /// is queue wait vs. actual query execution, to quantify how badly that single thread backs up under load.
    /// </summary>
    public class QueueSaturationScenario : IScenario
    {
        public string Name => "queue-saturation";

        public string Description => "Measures SerializedShardDatabase queue wait time vs. execution time under concurrent saves. Args: --count=500 --concurrency=8 --propertyGroups=3";

        public void Run(ScenarioArgs args)
        {
            var count = args.GetInt("count", 500);
            var concurrency = args.GetInt("concurrency", 8);
            var propertyGroups = args.GetInt("propertyGroups", 3);

            Console.WriteLine($"Saving {count} synthetic biotas from {concurrency} concurrent caller threads (propertyGroups={propertyGroups})...");

            var queueWaitMs = new ConcurrentBag<double>();
            var executionMs = new ConcurrentBag<double>();
            var failures = 0;
            var remaining = new CountdownEvent(count);

            var wallStart = DateTime.UtcNow;

            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, i =>
            {
                var id = SyntheticBiotaFactory.TestGuidRangeStart + (uint)i;
                var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                var rwLock = new ReaderWriterLockSlim();

                DatabaseManager.Shard.SaveBiota(biota, rwLock,
                    success =>
                    {
                        if (!success)
                            Interlocked.Increment(ref failures);
                        remaining.Signal();
                    },
                    (queueWaitTime, executionTime) =>
                    {
                        queueWaitMs.Add(queueWaitTime.TotalMilliseconds);
                        executionMs.Add(executionTime.TotalMilliseconds);
                    });
            });

            remaining.Wait();
            var wallDuration = DateTime.UtcNow - wallStart;
            var throughput = count / wallDuration.TotalSeconds;

            // Execution time (below) is attributed per-item, so its meaning changes if the underlying save path
            // ever switches between per-item and batched execution (e.g. a batching write-queue implementation)
            // - these two are unambiguous no matter how execution is internally attributed, so they're the ones
            // to trust for a before/after compare of overall caller-facing cost.
            Metrics.Record("Wall time ms", wallDuration.TotalMilliseconds, MetricDirection.LowerIsBetter, count);
            Metrics.Record("Throughput saves per sec", throughput, MetricDirection.HigherIsBetter, count);

            Console.WriteLine();
            Console.WriteLine($"Wall time: {wallDuration.TotalSeconds:N1}s  ({throughput:N0} saves/sec)  failures={failures}");
            LatencyStats.Report("Queue wait time", queueWaitMs.ToArray());
            LatencyStats.Report("Execution time", executionMs.ToArray());

            Cleanup(count);
        }

        private static void Cleanup(int count)
        {
            Console.WriteLine();
            Console.WriteLine("Cleaning up test biotas...");

            var remaining = new CountdownEvent(1);
            var ids = new uint[count];
            for (var i = 0; i < count; i++)
                ids[i] = SyntheticBiotaFactory.TestGuidRangeStart + (uint)i;

            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();
        }
    }
}

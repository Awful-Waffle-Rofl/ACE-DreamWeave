using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// ShardDatabase.DoSaveBiota silently retries context.SaveChanges() exactly once on any exception before
    /// giving up (ACE.Database/ShardDatabase.cs). For a non-transient conflict (e.g. two writers racing to
    /// create the same guid), retrying the identical SaveChanges() against the identical conflicting state just
    /// fails the same way twice - it can't help, it only doubles latency on the failure path.
    ///
    /// This forces that exact race deterministically: `concurrency` threads all try to create the SAME new
    /// biota id at (as close to) the same instant, via BaseDatabase.SaveBiota directly (bypassing the single
    /// serialized queue, since the queue would just serialize these and eliminate the race - this needs genuine
    /// concurrent DB access, which is how SaveBiotasInParallel's internal fan-out actually calls this method).
    /// Only one writer's INSERT can win the primary key; the rest hit the retry-then-fail path.
    /// </summary>
    public class RetryCostScenario : IScenario
    {
        public string Name => "retry-cost";

        public string Description => "Races concurrent writers on the same new biota id to measure the cost of the silent retry-once-then-fail pattern. Args: --concurrency=20";

        public void Run(ScenarioArgs args)
        {
            var concurrency = args.GetInt("concurrency", 20);
            var id = SyntheticBiotaFactory.TestGuidRangeStart + 0x2000;

            Console.WriteLine($"Racing {concurrency} concurrent writers to create biota 0x{id:X8} at the same instant...");

            var gate = new ManualResetEventSlim(false);
            var results = new (bool success, double ms)[concurrency];
            var threads = new Thread[concurrency];

            for (var t = 0; t < concurrency; t++)
            {
                var index = t;

                threads[t] = new Thread(() =>
                {
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups: 3);
                    var rwLock = new ReaderWriterLockSlim();

                    gate.Wait();

                    var stopwatch = Stopwatch.StartNew();
                    var success = DatabaseManager.Shard.BaseDatabase.SaveBiota(biota, rwLock);
                    stopwatch.Stop();

                    results[index] = (success, stopwatch.Elapsed.TotalMilliseconds);
                });

                threads[t].Start();
            }

            Thread.Sleep(100); // let every thread reach the gate before releasing them all together
            gate.Set();

            foreach (var thread in threads)
                thread.Join();

            var succeeded = results.Where(r => r.success).Select(r => r.ms).ToArray();
            var failed = results.Where(r => !r.success).Select(r => r.ms).ToArray();

            Console.WriteLine($"{succeeded.Length} succeeded, {failed.Length} failed " +
                               "(expected: exactly one INSERT wins the primary key, the rest hit the retry-then-fail path).");
            Console.WriteLine();

            if (succeeded.Length > 0)
                LatencyStats.Report("Succeeded (1 SaveChanges attempt)", succeeded);

            if (failed.Length > 0)
            {
                LatencyStats.Report("Failed (retried, 2 SaveChanges attempts)", failed);

                if (succeeded.Length > 0)
                {
                    var ratio = failed.Average() / succeeded.Average();
                    Console.WriteLine();
                    Console.WriteLine($"Retry tax: failed calls took {ratio:N1}x as long as a successful call - and still ultimately failed.");
                }
            }

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiota(id, _ => remaining.Signal());
            remaining.Wait();
        }
    }
}

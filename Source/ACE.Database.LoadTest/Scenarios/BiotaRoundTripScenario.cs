using System;
using System.Diagnostics;
using System.Threading;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Isolates the per-property-table N+1 cost in ShardDatabase.GetBiota by varying how many property
    /// collections a synthetic object has populated (--propertyGroups, 0-7) and comparing a forced cache-miss
    /// read against a cache-warm read for the same object.
    /// </summary>
    public class BiotaRoundTripScenario : IScenario
    {
        public string Name => "biota-roundtrip";

        public string Description => "Times a single biota save + cold read + warm read. Args: --propertyGroups=7 --iterations=50";

        public void Run(ScenarioArgs args)
        {
            var propertyGroups = args.GetInt("propertyGroups", 7);
            var iterations = args.GetInt("iterations", 50);

            Console.WriteLine($"Round-tripping {iterations} synthetic biotas with propertyGroups={propertyGroups}...");

            var saveMs = new double[iterations];
            var coldReadMs = new double[iterations];
            var warmReadMs = new double[iterations];

            for (var i = 0; i < iterations; i++)
            {
                var id = SyntheticBiotaFactory.TestGuidRangeStart + 0x1000 + (uint)i;
                var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                var rwLock = new ReaderWriterLockSlim();

                var saveDone = new ManualResetEventSlim();
                var saveStopwatch = Stopwatch.StartNew();
                DatabaseManager.Shard.SaveBiota(biota, rwLock, _ => saveDone.Set());
                saveDone.Wait();
                saveStopwatch.Stop();
                saveMs[i] = saveStopwatch.Elapsed.TotalMilliseconds;

                // WARNING: this is NOT a cold read, despite doNotAddToCache. That flag only suppresses ADDING to
                // ShardDatabaseWithCaching's biota cache; GetBiota consults the cache and returns a hit BEFORE it
                // ever looks at the flag. The SaveBiota above put this id in the cache, so this call is a cache
                // hit that never touches the database.
                //
                // Measured 2026-07-29, 50 iterations each: ids this process never touched read in 1.3ms p50,
                // while this save-then-read pattern and the documented warm read below are both under 0.05ms and
                // indistinguishable from each other. The metric is named for what it actually measures.
                //
                // Making this a real cold read needs the save to skip the cache (doNotAddToCache on the SAVE, as
                // IntegrityCheckScenario phase 1 now does) or the read to happen in another process. Both change
                // what this scenario measures, so neither is done here - see CALIBRATION.md.
                var coldStopwatch = Stopwatch.StartNew();
                DatabaseManager.Shard.BaseDatabase.GetBiota(id, doNotAddToCache: true);
                coldStopwatch.Stop();
                coldReadMs[i] = coldStopwatch.Elapsed.TotalMilliseconds;

                // Prime the cache, then read again - this should be served from the in-memory biota cache.
                DatabaseManager.Shard.BaseDatabase.GetBiota(id);
                var warmStopwatch = Stopwatch.StartNew();
                DatabaseManager.Shard.BaseDatabase.GetBiota(id);
                warmStopwatch.Stop();
                warmReadMs[i] = warmStopwatch.Elapsed.TotalMilliseconds;
            }

            LatencyStats.Report("Save", saveMs);

            // Both read metrics are cache hits (see the comment at the read sites), so both are Informational:
            // they are recorded and printed, but they must not gate anything, because neither one measures the
            // database read path and a reader could otherwise take 0.05ms as evidence that reads are fast.
            // Renamed from "Cold read (bypass cache)" / "Warm read (cached)" on 2026-07-29 - the old names
            // claimed a distinction that does not exist. Renaming resets their history: these keys will show as
            // MISSING against any report taken before that date, which is correct, since the old numbers were
            // measuring something the names misdescribed.
            LatencyStats.Report("Cached read after save ms", coldReadMs, MetricDirection.Informational);
            LatencyStats.Report("Cached read after priming ms", warmReadMs, MetricDirection.Informational);

            Cleanup(iterations);
        }

        private static void Cleanup(int iterations)
        {
            Console.WriteLine();
            Console.WriteLine("Cleaning up test biotas...");

            var remaining = new CountdownEvent(1);
            var ids = new uint[iterations];
            for (var i = 0; i < iterations; i++)
                ids[i] = SyntheticBiotaFactory.TestGuidRangeStart + 0x1000 + (uint)i;

            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();
        }
    }
}

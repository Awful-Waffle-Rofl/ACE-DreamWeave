using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using ACE.Database;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// ShardDatabaseWithCaching keeps recently-touched biotas in memory together with a retained ShardDbContext.
    /// On a cache HIT, SaveBiota reuses that context and writes directly. On a MISS it must run StageBiota, which
    /// re-reads the whole object across every populated property table before writing. Player logins currently
    /// populate that cache one possession at a time; a proposed change would bulk-load possessions without
    /// caching them. The scenario that would regress under that change is an item self-buff round shortly after
    /// login, which dirties every worn armour piece and weapon at once. This measures that exact burst both ways.
    ///
    /// The cold arm's setup save uses doNotAddToCache: true so the measured burst is a genuine cache miss.
    /// Without that flag, ShardDatabaseWithCaching.SaveBiota adds every successfully-saved biota to the cache
    /// on its own miss path - so the setup save alone would already warm the cache for these ids, and the
    /// "cold" measurement below would silently be a second warm hit. doNotAddToCache only suppresses ADDING
    /// to the cache on a write/read; it does not make SaveBiota or GetBiota bypass a cache entry that is
    /// already there some other way, so setup order still matters - always create these ids for the first
    /// time via the doNotAddToCache save below, never via a plain SaveBiota/GetBiota call first.
    /// </summary>
    public class EquipBuffWriteBurstScenario : IScenario
    {
        public string Name => "equip-buff-write-burst";

        public string Description =>
            "Measures the cost of dirtying and saving a dozen equipped items (an item self-buff round) with the biota cache cold vs warm. Args: --items=12 --iterations=20 --propertyGroups=7";

        // Id layout (offsets from SyntheticBiotaFactory.TestGuidRangeStart):
        //   +0x20000 + iteration*0x1000 + i   cold-arm items - a distinct, never-before-read block per iteration
        //   +0x30000 + i                       warm-arm items - one fixed block, reused across all iterations
        // The two ranges never overlap and never touch the ranges used by other scenarios.
        private const uint ColdBaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x20000;
        private const uint ColdIterationStride = 0x1000;
        private const uint WarmBaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x30000;

        public void Run(ScenarioArgs args)
        {
            var items = args.GetInt("items", 12);
            var iterations = args.GetInt("iterations", 20);
            var propertyGroups = args.GetInt("propertyGroups", 7);

            if (items > ColdIterationStride)
                throw new ArgumentOutOfRangeException(nameof(items), "items must be <= 0x1000 to respect the reserved id layout.");

            // A configuration echo, so a report records the burst size it was taken at. Not a performance signal.
            Metrics.Record("Items per burst", items, MetricDirection.Informational);

            // COLD ARM must run before WARM ARM: the whole point of the cold measurement is a guaranteed cache
            // miss, and running warm first (or interleaved) would risk polluting the biota cache with ids the
            // cold arm also touches, or with cache eviction timing bleeding between arms.
            var coldSaveMs = new List<double>();
            var coldBurstWallMs = new double[iterations];

            Console.WriteLine($"COLD ARM: {iterations} iterations, {items} items each, distinct never-before-read ids per iteration...");

            for (var iter = 0; iter < iterations; iter++)
            {
                var iterationBaseId = ColdBaseId + (uint)iter * ColdIterationStride;

                // SETUP: create the fresh items for this iteration. Not part of the measurement. Saved with
                // doNotAddToCache: true so this setup save itself does not warm the cache for these ids - see
                // the class docstring for why that matters.
                for (var i = 0; i < items; i++)
                {
                    var id = iterationBaseId + (uint)i;
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                    var rwLock = new ReaderWriterLockSlim();
                    DatabaseManager.Shard.BaseDatabase.SaveBiota(biota, rwLock, doNotAddToCache: true);
                }

                // MEASURE: dirty and save every item - this is the self-buff round. The cache has never seen
                // these ids, so every save is a guaranteed miss.
                var burstStopwatch = Stopwatch.StartNew();
                var saveRemaining = new CountdownEvent(items);

                for (var i = 0; i < items; i++)
                {
                    var id = iterationBaseId + (uint)i;
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                    biota.PropertiesInt[PropertyInt.Value] = 1000 + iter * 100 + i;
                    var rwLock = new ReaderWriterLockSlim();

                    var itemStopwatch = Stopwatch.StartNew();
                    DatabaseManager.Shard.SaveBiota(biota, rwLock, _ =>
                    {
                        itemStopwatch.Stop();
                        lock (coldSaveMs)
                            coldSaveMs.Add(itemStopwatch.Elapsed.TotalMilliseconds);
                        saveRemaining.Signal();
                    });
                }

                saveRemaining.Wait();
                burstStopwatch.Stop();
                coldBurstWallMs[iter] = burstStopwatch.Elapsed.TotalMilliseconds;
            }

            LatencyStats.Report("Buff save latency cold ms", coldSaveMs);
            Metrics.Record("Buff burst wall time cold ms", Mean(coldBurstWallMs), MetricDirection.LowerIsBetter, iterations);
            Console.WriteLine();

            // WARM ARM: one fixed block of ids, read into the cache once, then reused (and re-cached) across
            // every measured iteration.
            Console.WriteLine($"WARM ARM: {iterations} iterations, {items} items, one fixed id block kept warm in cache...");

            var warmSetupRemaining = new CountdownEvent(items);
            for (var i = 0; i < items; i++)
            {
                var id = WarmBaseId + (uint)i;
                var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                var rwLock = new ReaderWriterLockSlim();
                DatabaseManager.Shard.SaveBiota(biota, rwLock, _ => warmSetupRemaining.Signal());
            }
            warmSetupRemaining.Wait();

            // Prime the cache the same way a per-item login read does - one GetBiota per possession.
            for (var i = 0; i < items; i++)
            {
                var id = WarmBaseId + (uint)i;
                DatabaseManager.Shard.BaseDatabase.GetBiota(id);
            }

            var warmSaveMs = new List<double>();
            var warmBurstWallMs = new double[iterations];

            for (var iter = 0; iter < iterations; iter++)
            {
                var burstStopwatch = Stopwatch.StartNew();
                var saveRemaining = new CountdownEvent(items);

                for (var i = 0; i < items; i++)
                {
                    var id = WarmBaseId + (uint)i;
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                    biota.PropertiesInt[PropertyInt.Value] = 2000 + iter * 100 + i;
                    var rwLock = new ReaderWriterLockSlim();

                    var itemStopwatch = Stopwatch.StartNew();
                    DatabaseManager.Shard.SaveBiota(biota, rwLock, _ =>
                    {
                        itemStopwatch.Stop();
                        lock (warmSaveMs)
                            warmSaveMs.Add(itemStopwatch.Elapsed.TotalMilliseconds);
                        saveRemaining.Signal();
                    });
                }

                saveRemaining.Wait();
                burstStopwatch.Stop();
                warmBurstWallMs[iter] = burstStopwatch.Elapsed.TotalMilliseconds;
            }

            LatencyStats.Report("Buff save latency warm ms", warmSaveMs);
            Metrics.Record("Buff burst wall time warm ms", Mean(warmBurstWallMs), MetricDirection.LowerIsBetter, iterations);

            Cleanup(items, iterations);
        }

        private static double Mean(double[] values)
        {
            if (values.Length == 0)
                return 0;

            double sum = 0;
            foreach (var v in values)
                sum += v;
            return sum / values.Length;
        }

        private static void Cleanup(int items, int iterations)
        {
            Console.WriteLine();
            Console.WriteLine("Cleaning up test biotas...");

            var ids = new List<uint>();

            for (var iter = 0; iter < iterations; iter++)
            {
                var iterationBaseId = ColdBaseId + (uint)iter * ColdIterationStride;
                for (var i = 0; i < items; i++)
                    ids.Add(iterationBaseId + (uint)i);
            }

            for (var i = 0; i < items; i++)
                ids.Add(WarmBaseId + (uint)i);

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();
        }
    }
}

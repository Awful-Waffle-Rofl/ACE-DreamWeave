using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ACE.Database;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// ShardDatabaseWithCaching.SaveBiotaBatch splits a multi-item save in two: items already in the biota cache
    /// are saved INDIVIDUALLY (N commits, no reads, each reusing its own retained per-object DbContext), while
    /// items not in the cache go into ONE shared DbContext (one batched pre-load read, then ONE commit for all of
    /// them). A warm (all-cached) batch of N therefore pays N commits and zero reads; a cold (all-uncached) batch
    /// of N pays one read and one commit. At small N the cache-hit path wins (no read at all, same commit count);
    /// at larger N the per-item commit cost eventually overtakes the single batched commit. This scenario sweeps
    /// batch size to find where that crossover actually falls, rather than assuming it.
    /// </summary>
    public class SaveBatchCrossoverScenario : IScenario
    {
        public string Name => "save-batch-crossover";

        public string Description =>
            "Finds the batch size at which one batched commit beats N per-item cache-hit commits. Args: --sizes=1,2,4,8,12,24,50,100 --iterations=20 --warmup=3";

        // Id layout (offsets from SyntheticBiotaFactory.TestGuidRangeStart):
        //   +0x40000 is the reserved base for this scenario; EquipBuffWriteBurstScenario tops out at +0x31000 and
        //   every other scenario's ranges sit well below that, so this leaves a clean gap.
        //   Within the reserved region, sizes are processed in the order given on the command line, and each size
        //   gets its own contiguous section sized to fit both arms. `passes` below is iterations + warmup, since
        //   the discarded warm-up passes each burn their own never-read block just like a measured one:
        //     [sectionBase, sectionBase + passes*size)                cold arm - one distinct block of `size`
        //                                                              ids per pass, never reused
        //     [sectionBase + passes*size, sectionBase + (passes+1)*size)   warm arm - one fixed block of `size`
        //                                                              ids, reused across all passes
        //   The next size's section starts right after the previous one ends, so sections never overlap each
        //   other, and ReservedRangeEnd bounds the whole scenario so it can never overlap unrelated scenarios'
        //   ranges either - see the overflow guard in Run().
        private const uint ReservedRangeStart = SyntheticBiotaFactory.TestGuidRangeStart + 0x40000;
        private const uint ReservedRangeEnd = SyntheticBiotaFactory.TestGuidRangeStart + 0xC0000; // exclusive

        public void Run(ScenarioArgs args)
        {
            var sizes = ParseSizes(args.GetString("sizes", "1,2,4,8,12,24,50,100"));
            var iterations = args.GetInt("iterations", 20);
            var warmup = args.GetInt("warmup", MeasuredLoop.DefaultWarmupIterations);
            var propertyGroups = args.GetInt("propertyGroups", 7);

            var allCreatedIds = new List<uint>();

            var results = new List<(int size, double coldAvgMs, double coldP50Ms, double warmAvgMs, double warmP50Ms)>();

            var sectionBase = ReservedRangeStart;

            foreach (var size in sizes)
            {
                // The cold arm needs one never-read id block per PASS, warm-up passes included; the warm arm
                // needs one block total however many passes it runs.
                var coldPasses = MeasuredLoop.TotalPasses(iterations, warmup);
                var sectionLength = (ulong)size * (ulong)(coldPasses + 1);

                if ((ulong)sectionBase + sectionLength > ReservedRangeEnd)
                {
                    throw new InvalidOperationException(
                        $"save-batch-crossover: requested sizes/iterations/warmup ({size}, {iterations}, {warmup}) " +
                        $"would overflow this scenario's reserved id range " +
                        $"(0x{ReservedRangeStart:X8}-0x{ReservedRangeEnd:X8}). " +
                        "Reduce --sizes/--iterations/--warmup or widen the reserved range.");
                }

                var coldBase = sectionBase;
                var warmBase = sectionBase + (uint)((ulong)size * (ulong)coldPasses);
                sectionBase += (uint)sectionLength;

                Console.WriteLine($"=== size={size} ===");

                // COLD ARM runs before WARM ARM for this size, and every size's cold arm runs before that size's
                // warm arm ever touches the cache: the whole point of the cold measurement is a guaranteed cache
                // miss on every save, and running warm first (or interleaved) risks the warm arm's cache entries
                // (or their eviction timing) bleeding into what should be a clean miss.
                var coldBurstMs = RunColdArm(coldBase, size, iterations, warmup, propertyGroups, allCreatedIds);
                var warmBurstMs = RunWarmArm(warmBase, size, iterations, warmup, propertyGroups, allCreatedIds);

                var coldAvg = Mean(coldBurstMs);
                var coldP50 = Percentile50(coldBurstMs);
                var warmAvg = Mean(warmBurstMs);
                var warmP50 = Percentile50(warmBurstMs);
                var ratio = warmP50 == 0 ? 0 : coldP50 / warmP50;

                Console.WriteLine(
                    $"size={size,-4} cold avg={coldAvg,7:N2}ms p50={coldP50,7:N2}ms   " +
                    $"warm avg={warmAvg,7:N2}ms p50={warmP50,7:N2}ms   cold/warm p50 ratio={ratio,6:N2}");
                Console.WriteLine();

                // All four aggregate `iterations` measured bursts, so that is the sample count behind each.
                Metrics.Record($"Cold burst ms N={size}", coldAvg, MetricDirection.LowerIsBetter, iterations);
                Metrics.Record($"Cold burst p50 ms N={size}", coldP50, MetricDirection.LowerIsBetter, iterations);
                Metrics.Record($"Warm burst ms N={size}", warmAvg, MetricDirection.LowerIsBetter, iterations);
                Metrics.Record($"Warm burst p50 ms N={size}", warmP50, MetricDirection.LowerIsBetter, iterations);

                results.Add((size, coldAvg, coldP50, warmAvg, warmP50));
            }

            var crossover = results.FirstOrDefault(r => r.coldP50Ms < r.warmP50Ms);

            if (crossover.size != 0)
                Console.WriteLine($"CROSSOVER: smallest size at which cold (batched) beats warm on p50 is N={crossover.size}.");
            else
                Console.WriteLine("CROSSOVER: none found - warm won on p50 at every tested size.");

            Cleanup(allCreatedIds);
        }

        private static double[] RunColdArm(uint coldBase, int size, int iterations, int warmup, int propertyGroups, List<uint> allCreatedIds)
        {
            Console.WriteLine($"COLD ARM: {warmup} discarded warm-up + {iterations} measured iterations, {size} items each, distinct never-before-read ids per iteration...");

            // The pass index is absolute (warm-up passes included), so every pass still gets its own never-read
            // id block and a warm-up pass can never hand a cached id to a measured one.
            return MeasuredLoop.Run(iterations, warmup, iter =>
            {
                var iterationBaseId = coldBase + (uint)(iter * size);

                var ids = new uint[size];
                for (var i = 0; i < size; i++)
                    ids[i] = iterationBaseId + (uint)i;

                lock (allCreatedIds)
                    allCreatedIds.AddRange(ids);

                // SETUP: seed the fresh items for this iteration with doNotAddToCache so they are genuinely absent
                // from the biota cache afterward. A naive seed (a plain SaveBiota/SaveBiotasInParallel call without
                // this flag) would cache them on the way in, and the "cold" measurement below would then silently
                // hit the cache-hit path instead of a real miss - this is exactly the bug that must not recur here.
                // Not part of the timed measurement.
                var seedBiotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();
                foreach (var id in ids)
                    seedBiotas.Add((SyntheticBiotaFactory.Create(id, propertyGroups), new ReaderWriterLockSlim()));

                var seedRemaining = new CountdownEvent(1);
                DatabaseManager.Shard.SaveBiotasInParallel(seedBiotas, _ => seedRemaining.Signal(), doNotAddToCache: true);
                seedRemaining.Wait();

                // MEASURE: dirty and save every item in ONE batched call - this is the whole burst a real caller
                // waits on. The cache has never seen these ids, so every save in this call is a guaranteed miss.
                var dirtyBiotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();
                foreach (var id in ids)
                {
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                    biota.PropertiesInt[PropertyInt.Value] = 1000 + iter;
                    dirtyBiotas.Add((biota, new ReaderWriterLockSlim()));
                }

                var burstStopwatch = Stopwatch.StartNew();
                var saveRemaining = new CountdownEvent(1);

                DatabaseManager.Shard.SaveBiotasInParallel(dirtyBiotas, _ => saveRemaining.Signal());

                saveRemaining.Wait();
                burstStopwatch.Stop();

                return burstStopwatch.Elapsed.TotalMilliseconds;
            });
        }

        private static double[] RunWarmArm(uint warmBase, int size, int iterations, int warmup, int propertyGroups, List<uint> allCreatedIds)
        {
            Console.WriteLine($"WARM ARM: {warmup} discarded warm-up + {iterations} measured iterations, {size} items, one fixed id block kept warm in cache...");

            var ids = new uint[size];
            for (var i = 0; i < size; i++)
                ids[i] = warmBase + (uint)i;

            lock (allCreatedIds)
                allCreatedIds.AddRange(ids);

            // Seed once, normally (no doNotAddToCache), so this block lands in the cache.
            var seedBiotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();
            foreach (var id in ids)
                seedBiotas.Add((SyntheticBiotaFactory.Create(id, propertyGroups), new ReaderWriterLockSlim()));

            var seedRemaining = new CountdownEvent(1);
            DatabaseManager.Shard.SaveBiotasInParallel(seedBiotas, _ => seedRemaining.Signal());
            seedRemaining.Wait();

            // Explicitly warm the cache the same way a per-item login read does - do not rely on the seed save
            // alone having cached them.
            foreach (var id in ids)
                DatabaseManager.Shard.BaseDatabase.GetBiota(id);

            // The warm arm reuses one fixed id block, so its warm-up passes need no extra id space - they just
            // dirty and re-save the same items a few more times before anything is recorded.
            return MeasuredLoop.Run(iterations, warmup, iter =>
            {
                var dirtyBiotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();
                foreach (var id in ids)
                {
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                    biota.PropertiesInt[PropertyInt.Value] = 2000 + iter;
                    dirtyBiotas.Add((biota, new ReaderWriterLockSlim()));
                }

                var burstStopwatch = Stopwatch.StartNew();
                var saveRemaining = new CountdownEvent(1);

                DatabaseManager.Shard.SaveBiotasInParallel(dirtyBiotas, _ => saveRemaining.Signal());

                saveRemaining.Wait();
                burstStopwatch.Stop();

                return burstStopwatch.Elapsed.TotalMilliseconds;
            });
        }

        private static List<int> ParseSizes(string raw)
        {
            var sizes = new List<int>();

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;

                sizes.Add(int.Parse(trimmed));
            }

            if (sizes.Count == 0)
                throw new ArgumentException("--sizes must contain at least one positive integer.");

            return sizes;
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

        private static double Percentile50(double[] values)
        {
            if (values.Length == 0)
                return 0;

            var sorted = values.OrderBy(x => x).ToList();
            var index = (int)Math.Ceiling(0.50 * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }

        private static void Cleanup(List<uint> ids)
        {
            Console.WriteLine();
            Console.WriteLine("Cleaning up test biotas...");

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// SerializedShardDatabase funnels every shard operation through one worker thread and one FIFO queue, and
    /// RemoveBiota is not batched - each sold item is its own context, its own SELECT, its own DELETE, and its own
    /// commit. A player login enqueues GetPossessedBiotasInParallel onto that same queue with no priority and no
    /// backpressure, so a mass vendor sale can park a login behind thousands of single-row deletes. This scenario
    /// produces three numbers: (1) a cold first read of the possession chain, taken once before either measured
    /// arm and used only to report the realistic first-login cost and to warm the biota cache; (2) login-read
    /// latency with an idle queue (warm cache); (3) login-read latency with a delete burst queued immediately
    /// ahead of it (also warm cache). Arms (2) and (3) are deliberately both warm so the difference between them
    /// isolates queue wait, not cache state.
    /// </summary>
    public class LoginUnderDeleteLoadScenario : IScenario
    {
        public string Name => "login-under-delete-load";

        public string Description =>
            "Measures player-login read latency (GetPossessedBiotasInParallel) while a vendor-sale delete burst is queued ahead of it. Args: --packs=3 --itemsPerPack=100 --deletes=600 --clusterSize=20 --samples=5";

        // Id layout (all offsets from SyntheticBiotaFactory.TestGuidRangeStart), chosen so the possession chain
        // and the per-sample delete burst never overlap:
        //   +0x0000                    the "character" id that owns the possession chain
        //   +0x0001 .. +0x00FF         pack container ids (up to 255 packs)
        //   +0x0100 .. +0xBFFF         items inside packs (up to itemsPerPack * packs, well within range)
        //   +0xC000 + sample*0x1000 .. delete-burst ids for that sample (up to 0x1000 = 4096 per sample)
        public void Run(ScenarioArgs args)
        {
            var packs = args.GetInt("packs", 3);
            var itemsPerPack = args.GetInt("itemsPerPack", 100);
            var deletes = args.GetInt("deletes", 600);
            var clusterSize = args.GetInt("clusterSize", 20);
            var samples = args.GetInt("samples", 5);
            var propertyGroups = args.GetInt("propertyGroups", 7);

            var baseId = SyntheticBiotaFactory.TestGuidRangeStart;
            var characterId = baseId;
            var packBaseId = baseId + 0x1;
            var itemBaseId = baseId + 0x100;
            var deleteBurstBaseId = baseId + 0xC000;

            if (deletes > 0x1000)
                throw new ArgumentOutOfRangeException(nameof(deletes), "deletes must be <= 0x1000 per sample to respect the reserved id layout.");

            Console.WriteLine($"Setting up possession chain: characterId=0x{characterId:X8}, packs={packs}, itemsPerPack={itemsPerPack} (propertyGroups={propertyGroups})...");

            var createdPossessionIds = new List<uint>();
            var setupRemaining = new CountdownEvent(1);
            var setupCount = packs + packs * itemsPerPack;
            setupRemaining.Reset(setupCount);

            for (var p = 0; p < packs; p++)
            {
                var packId = packBaseId + (uint)p;
                createdPossessionIds.Add(packId);

                var packBiota = SyntheticBiotaFactory.CreatePossession(packId, characterId, isContainer: true, propertyGroups);
                var packLock = new ReaderWriterLockSlim();
                DatabaseManager.Shard.SaveBiota(packBiota, packLock, _ => setupRemaining.Signal());

                for (var i = 0; i < itemsPerPack; i++)
                {
                    var itemId = itemBaseId + (uint)(p * itemsPerPack + i);
                    createdPossessionIds.Add(itemId);

                    var itemBiota = SyntheticBiotaFactory.CreatePossession(itemId, packId, isContainer: false, propertyGroups);
                    var itemLock = new ReaderWriterLockSlim();
                    DatabaseManager.Shard.SaveBiota(itemBiota, itemLock, _ => setupRemaining.Signal());
                }
            }

            setupRemaining.Wait();
            Console.WriteLine($"Created {createdPossessionIds.Count} possession biotas ({packs} packs + {packs * itemsPerPack} items).");
            Console.WriteLine();

            var expectedPossessionCount = packs + packs * itemsPerPack;

            // COLD FIRST READ: exactly one GetPossessedBiotasInParallel call, taken before either measured arm.
            // GetPossessedBiotasInParallel loads each possession with the virtual GetBiota(uint id), which on
            // ShardDatabaseWithCaching consults AND POPULATES the in-memory biota cache. So this call is also the
            // cache warm-up for both measured arms below. Both arms are deliberately warm: the question this
            // scenario answers is how much QUEUE WAIT a delete burst adds, so both arms must be in the same cache
            // state to isolate that effect. The realistic cold cost of a real first login is reported here,
            // separately, instead of leaking into either measured arm as a one-sample outlier.
            Console.WriteLine("Measuring cold first read (also warms the biota cache for both arms below)...");
            var coldDone = new ManualResetEventSlim();
            var coldStopwatch = Stopwatch.StartNew();

            DatabaseManager.Shard.GetPossessedBiotasInParallel(characterId, possessed =>
            {
                coldStopwatch.Stop();

                var actualCount = possessed.Inventory.Count + possessed.WieldedItems.Count;
                if (actualCount != expectedPossessionCount)
                {
                    Console.WriteLine(
                        $"WARNING: possession count mismatch - expected {expectedPossessionCount}, got {actualCount}. " +
                        "This measurement is not trustworthy - check the possession chain setup.");
                }

                // A fixture invariant (the possession chain this scenario built), not a performance signal.
                Metrics.Record("Possessions loaded", actualCount, MetricDirection.Informational);

                coldDone.Set();
            });

            coldDone.Wait();
            // Informational, not LowerIsBetter, because this is n=1 BY CONSTRUCTION and no --samples value
            // changes that: there is exactly one cold read per run, and it is also the cache warm-up for both
            // measured arms below, so taking a second one would not be cold. Its measured identical-build spread
            // is 128%, so as a gate it could only ever produce false verdicts. It is still recorded and printed
            // because the number is worth eyeballing in a diff - it just never votes.
            //
            // The proper cold-read coverage is a scenario that seeds in one process and reads in another; PR #352
            // adds exactly that (PossessionLoadScenario), and it belongs in StandardSuite once that stack lands.
            Metrics.Record("Login latency cold first read ms", coldStopwatch.Elapsed.TotalMilliseconds, MetricDirection.Informational);
            Console.WriteLine($"Login latency cold first read ms: {coldStopwatch.Elapsed.TotalMilliseconds:N2}");
            Console.WriteLine();

            // MEASURE A: idle queue (warm cache - see comment above).
            Console.WriteLine("Measuring login latency with an idle queue...");
            var idleLoginMs = new double[samples];

            for (var s = 0; s < samples; s++)
            {
                var done = new ManualResetEventSlim();
                var stopwatch = Stopwatch.StartNew();

                DatabaseManager.Shard.GetPossessedBiotasInParallel(characterId, possessed =>
                {
                    stopwatch.Stop();
                    done.Set();
                });

                done.Wait();
                idleLoginMs[s] = stopwatch.Elapsed.TotalMilliseconds;
            }

            LatencyStats.Report("Login latency idle ms", idleLoginMs);
            Console.WriteLine();

            // MEASURE B: under delete load.
            Console.WriteLine("Measuring login latency under a queued vendor-sale delete burst...");
            var underLoadLoginMs = new double[samples];
            var deleteBurstWallMs = new double[samples];

            for (var s = 0; s < samples; s++)
            {
                var sampleDeleteBaseId = deleteBurstBaseId + (uint)(s * 0x1000);

                // SETUP for this sample's delete burst - create throwaway biotas to be deleted below. This save
                // is not part of the measurement.
                var createRemaining = new CountdownEvent(deletes == 0 ? 1 : deletes);
                if (deletes == 0)
                    createRemaining.Signal();

                for (var i = 0; i < deletes; i++)
                {
                    var id = sampleDeleteBaseId + (uint)i;
                    var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                    var rwLock = new ReaderWriterLockSlim();
                    DatabaseManager.Shard.SaveBiota(biota, rwLock, _ => createRemaining.Signal());
                }
                createRemaining.Wait();

                // Enqueue the delete burst in clusters (models sell macros batching 10-30 items per transaction),
                // then IMMEDIATELY - without waiting for the deletes to finish - enqueue the login read behind them.
                var deleteRemaining = new CountdownEvent(deletes == 0 ? 1 : deletes);
                if (deletes == 0)
                    deleteRemaining.Signal();

                var burstStopwatch = Stopwatch.StartNew();

                for (var clusterStart = 0; clusterStart < deletes; clusterStart += clusterSize)
                {
                    var clusterEnd = Math.Min(clusterStart + clusterSize, deletes);
                    for (var i = clusterStart; i < clusterEnd; i++)
                    {
                        var id = sampleDeleteBaseId + (uint)i;
                        DatabaseManager.Shard.RemoveBiota(id, _ =>
                        {
                            deleteRemaining.Signal();
                        });
                    }
                }

                var loginDone = new ManualResetEventSlim();
                var loginStopwatch = Stopwatch.StartNew();

                DatabaseManager.Shard.GetPossessedBiotasInParallel(characterId, possessed =>
                {
                    loginStopwatch.Stop();
                    loginDone.Set();
                });

                loginDone.Wait();
                underLoadLoginMs[s] = loginStopwatch.Elapsed.TotalMilliseconds;

                deleteRemaining.Wait();
                burstStopwatch.Stop();
                deleteBurstWallMs[s] = burstStopwatch.Elapsed.TotalMilliseconds;
            }

            LatencyStats.Report("Login latency under delete load ms", underLoadLoginMs);

            var meanBurstWallMs = Mean(deleteBurstWallMs);
            Metrics.Record("Delete burst wall time ms", meanBurstWallMs, MetricDirection.LowerIsBetter, samples);
            Metrics.Record("Deletes per sec", deletes > 0 && meanBurstWallMs > 0 ? deletes / (meanBurstWallMs / 1000.0) : 0,
                MetricDirection.HigherIsBetter, samples);

            Console.WriteLine();
            Console.WriteLine($"Mean delete burst wall time: {meanBurstWallMs:N1}ms across {samples} samples ({deletes} deletes/sample).");

            Cleanup(createdPossessionIds, deleteBurstBaseId, samples, deletes);
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

        private static void Cleanup(List<uint> possessionIds, uint deleteBurstBaseId, int samples, int deletes)
        {
            Console.WriteLine();
            Console.WriteLine("Cleaning up test biotas...");

            var ids = new List<uint>(possessionIds);

            // Any leftover delete-burst rows (should normally be none, since MEASURE B deletes them all itself,
            // but clean up defensively in case a sample was interrupted).
            for (var s = 0; s < samples; s++)
            {
                var sampleBaseId = deleteBurstBaseId + (uint)(s * 0x1000);
                for (var i = 0; i < deletes; i++)
                    ids.Add(sampleBaseId + (uint)i);
            }

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();
        }
    }
}

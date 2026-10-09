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
    /// Player.DeepSave (ACE.Server/WorldObjects/Player_Inventory.cs) ends with an unconditional
    /// DatabaseManager.Shard.SaveBiotasInParallel(biotas, null) where "biotas" almost always holds exactly ONE
    /// item, and it is called once per item from three sites - remove-from-inventory, dequip, and
    /// move-to-off-player-container. A bulk player action over N items therefore enqueues N separate
    /// SaveBiotasInParallel calls.
    ///
    /// SerializedShardDatabase enqueues SaveBiotasInParallel as a plain Task, not as a batchable
    /// SaveBiotaQueueItem, and the worker's opportunistic batching only merges consecutive SaveBiotaQueueItem
    /// entries. So those N calls are N un-mergeable FIFO slots on the single shard worker thread, each opening its
    /// own context and committing its own transaction, and each blocking every other player's queued DB work -
    /// logins included - for its duration.
    ///
    /// This scenario measures two arms over the same seeded data:
    ///   ARM A - the current shape: N calls to SaveBiotasInParallel, one item per call, all enqueued without
    ///           waiting, then all N callbacks awaited.
    ///   ARM B - the proposed fix: ONE call to SaveBiotasInParallel holding all N items.
    /// and, immediately after firing each burst's enqueues and BEFORE waiting on them, a login-shaped read
    /// (GetPossessedBiotasInParallel) on the same queue - the number that actually decides whether this is
    /// player-visible. The probe is copied from login-under-delete-load so the two are directly comparable.
    ///
    /// CACHE STATE IS MEASURED BOTH WAYS, because it changes the answer. On the real sell path a player's
    /// possessions were loaded at login by GetInventoryInParallel, which caches them for
    /// ShardNonPlayerBiotaCacheTime. A player who logs in and immediately mass-sells hits WARM entries (the
    /// cache-hit save path reuses a retained per-object context and skips the StageBiota re-read, but still
    /// commits individually); a player online longer than the retention hits COLD entries (each save pays a full
    /// re-read). Cold arms are seeded via BaseDatabase.SaveBiota(..., doNotAddToCache: true), because
    /// ShardDatabaseWithCaching.SaveBiota caches on its MISS path - a scenario that seeds by saving normally
    /// leaves everything warm and silently measures warm against warm. doNotAddToCache suppresses ADDING to the
    /// cache; it does not bypass READING it, so the seed order matters: a cold id must never be touched by a
    /// plain SaveBiota/GetBiota before its measured burst.
    ///
    /// Every (size, cache state, arm, sample) combination gets its OWN disjoint block of ids, because a measured
    /// save adds its ids to the cache - reusing ids across combinations would warm a later cold arm.
    /// </summary>
    public class DeepSaveBurstScenario : IScenario
    {
        public string Name => "deepsave-burst";

        public string Description =>
            "Measures DeepSave's one-call-per-item SaveBiotasInParallel burst against a single batched call, cold and warm cache, with a login-shaped read queued behind each burst. Args: --sizes=1,5,12,50,100,300,600 --samples=3 --packs=3 --itemsPerPack=100 --propertyGroups=7";

        // Id layout, all offsets from SyntheticBiotaFactory.TestGuidRangeStart (which leaves 0xFFFFF of headroom).
        // Chosen to clear every range the other scenarios use (biota-roundtrip +0x1000,
        // login-under-delete-load +0x0000..+0x10FFF, equip-buff-write-burst +0x20000..+0x43FFF):
        //   +0x50000                     the "character" id owning the login-probe possession chain
        //   +0x50001 .. +0x500FF         pack container ids (up to 255 packs)
        //   +0x50100 .. +0x5BFFF         items inside those packs
        //   +0x60000 + block*0x1000      one 4096-id block per (size, cache state, arm, sample) combination
        private const uint CharacterId = SyntheticBiotaFactory.TestGuidRangeStart + 0x50000;
        private const uint PackBaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x50001;
        private const uint ItemBaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x50100;
        private const uint BurstBaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x60000;
        private const uint BurstBlockStride = 0x1000;

        /// <summary>
        /// Container id the seeded burst items start out in ("in the player's pack"). The measured save moves them
        /// to container 0, which is what a sell/drop/dequip actually changes - so the write is real work in the
        /// same property table the real path dirties, not a synthetic no-op.
        /// This deliberately points at an id that does not exist and is NOT part of the login probe's possession
        /// chain. Pointing it at a real pack id makes every burst item a member of that pack, and the login probe
        /// then loads the burst items too - its latency stops being a fixed-size login and grows with the burst.
        /// </summary>
        private const uint SeedContainerId = SyntheticBiotaFactory.TestGuidRangeStart + 0x5F000;

        private sealed class Combination
        {
            public int N;
            public bool Warm;
            public string Arm;
            public double[] BurstWallMs;
            public List<double> CallbackMs = new();
            public double[] IdleLoginMs;
            public double[] UnderLoadLoginMs;
        }

        public void Run(ScenarioArgs args)
        {
            var sizes = ParseSizes(args.GetString("sizes", "1,5,12,50,100,300,600"));
            var samples = args.GetInt("samples", 3);
            var packs = args.GetInt("packs", 3);
            var itemsPerPack = args.GetInt("itemsPerPack", 100);
            var propertyGroups = args.GetInt("propertyGroups", 7);
            var warmUpSize = args.GetInt("warmUpSize", 20);

            foreach (var size in sizes)
            {
                if (size > BurstBlockStride)
                    throw new ArgumentOutOfRangeException(nameof(sizes), $"size {size} exceeds the {BurstBlockStride} ids reserved per combination.");
            }

            // Two extra blocks for the discarded warm-up, one per arm.
            var blockCount = sizes.Count * 2 * 2 * samples + 2;
            if (BurstBaseId + (uint)blockCount * BurstBlockStride > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(sizes), "too many combinations for the reserved id range - reduce --sizes or --samples.");

            Console.WriteLine($"sizes=[{string.Join(",", sizes)}] samples={samples} propertyGroups={propertyGroups}");
            Console.WriteLine();

            var possessionIds = SetUpPossessionChain(packs, itemsPerPack, propertyGroups);

            // COLD FIRST READ of the possession chain: one GetPossessedBiotasInParallel, before any measurement.
            // It loads each possession through the virtual GetBiota, which populates the biota cache, so it is
            // also the warm-up for every login probe below. Reported separately rather than folded into an arm.
            Console.WriteLine("Measuring the possession chain's cold first read (also warms the cache for every login probe)...");
            var coldLoginMs = MeasureLogin(possessionIds.Count);
            // Informational, not LowerIsBetter: exactly one measurement by construction (it is itself the cache
            // warm-up for every login probe below), so no --samples value can raise its n and it could only ever
            // produce false verdicts. Printed, never gated - same treatment as the identically-named metric in
            // LoginUnderDeleteLoadScenario. The genuinely cold read lives in possession-load.
            Metrics.Record("Login latency cold first read ms", coldLoginMs, MetricDirection.Informational);
            Console.WriteLine($"Login latency cold first read ms: {coldLoginMs:N2}");
            Console.WriteLine();

            var results = new List<Combination>();
            var allBurstIds = new List<uint>();
            var blockIndex = 0;

            // WARM-UP, discarded. The first burst of a process pays JIT, first-connection and EF model-building
            // costs that have nothing to do with either arm; measured cold, an N=1 arm A first sample came out
            // several times its steady-state value purely from that. Both arms are exercised so neither carries
            // the penalty into its first recorded sample.
            Console.WriteLine("Warm-up burst (discarded)...");
            var warmUp = new Combination
            {
                N = warmUpSize,
                Warm = false,
                Arm = "warmup",
                BurstWallMs = new double[1],
                IdleLoginMs = new double[1],
                UnderLoadLoginMs = new double[1]
            };

            foreach (var warmUpArm in new[] { "A", "B" })
            {
                var warmUpBlockId = BurstBaseId + (uint)blockIndex * BurstBlockStride;
                blockIndex++;

                for (var i = 0; i < warmUpSize; i++)
                    allBurstIds.Add(warmUpBlockId + (uint)i);

                SeedBurstBlock(warmUpBlockId, warmUpSize, propertyGroups, false);

                if (warmUpArm == "A")
                    RunArmA(warmUp, warmUpBlockId, warmUpSize, propertyGroups, 0, possessionIds.Count);
                else
                    RunArmB(warmUp, warmUpBlockId, warmUpSize, propertyGroups, 0, possessionIds.Count);
            }

            Console.WriteLine();

            foreach (var n in sizes)
            {
                // Cold before warm within a size, and a fresh id block per sample, so no arm can be warmed by an
                // earlier one. The arm loop is inside the cache loop only for readable output ordering; the
                // blocks are disjoint either way.
                foreach (var warm in new[] { false, true })
                {
                    foreach (var arm in new[] { "A", "B" })
                    {
                        var combination = new Combination
                        {
                            N = n,
                            Warm = warm,
                            Arm = arm,
                            BurstWallMs = new double[samples],
                            IdleLoginMs = new double[samples],
                            UnderLoadLoginMs = new double[samples]
                        };

                        for (var s = 0; s < samples; s++)
                        {
                            var blockBaseId = BurstBaseId + (uint)blockIndex * BurstBlockStride;
                            blockIndex++;

                            for (var i = 0; i < n; i++)
                                allBurstIds.Add(blockBaseId + (uint)i);

                            SeedBurstBlock(blockBaseId, n, propertyGroups, warm);

                            // An idle-queue login read immediately before the burst. It doubles as the paired
                            // baseline for this exact sample and as a re-warm of the possession chain, so a long
                            // run cannot drift into cache expiry and inflate only the later under-load numbers.
                            combination.IdleLoginMs[s] = MeasureLogin(possessionIds.Count);

                            if (arm == "A")
                                RunArmA(combination, blockBaseId, n, propertyGroups, s, possessionIds.Count);
                            else
                                RunArmB(combination, blockBaseId, n, propertyGroups, s, possessionIds.Count);
                        }

                        results.Add(combination);
                        ReportCombination(combination);
                    }
                }
            }

            Console.WriteLine();
            PrintTable(results);

            Cleanup(possessionIds, allBurstIds);
        }

        /// <summary>
        /// ARM A - the current DeepSave shape. N separate SaveBiotasInParallel calls, each holding exactly one
        /// item, all enqueued without waiting; then the login probe is enqueued BEHIND them and only then are the
        /// N callbacks awaited.
        /// </summary>
        private static void RunArmA(Combination combination, uint blockBaseId, int n, int propertyGroups, int sample, int expectedPossessions)
        {
            var callbackMs = new double[n];
            var remaining = new CountdownEvent(n);
            var burstStopwatch = Stopwatch.StartNew();

            for (var i = 0; i < n; i++)
            {
                var index = i;
                var item = MakeDirtiedItem(blockBaseId + (uint)i, propertyGroups, sample);
                var itemStopwatch = Stopwatch.StartNew();

                DatabaseManager.Shard.SaveBiotasInParallel(new[] { item }, _ =>
                {
                    itemStopwatch.Stop();
                    callbackMs[index] = itemStopwatch.Elapsed.TotalMilliseconds;
                    remaining.Signal();
                });
            }

            combination.UnderLoadLoginMs[sample] = MeasureLogin(expectedPossessions);

            remaining.Wait();
            burstStopwatch.Stop();

            combination.BurstWallMs[sample] = burstStopwatch.Elapsed.TotalMilliseconds;
            combination.CallbackMs.AddRange(callbackMs);
        }

        /// <summary>
        /// ARM B - the proposed fix. ONE SaveBiotasInParallel call holding all N items, with the login probe
        /// enqueued behind it before waiting. Arm B contributes one callback latency per sample, not N.
        /// </summary>
        private static void RunArmB(Combination combination, uint blockBaseId, int n, int propertyGroups, int sample, int expectedPossessions)
        {
            var items = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>(n);
            for (var i = 0; i < n; i++)
                items.Add(MakeDirtiedItem(blockBaseId + (uint)i, propertyGroups, sample));

            var done = new ManualResetEventSlim();
            var burstStopwatch = Stopwatch.StartNew();
            var callbackStopwatch = Stopwatch.StartNew();

            DatabaseManager.Shard.SaveBiotasInParallel(items, _ =>
            {
                callbackStopwatch.Stop();
                done.Set();
            });

            combination.UnderLoadLoginMs[sample] = MeasureLogin(expectedPossessions);

            done.Wait();
            burstStopwatch.Stop();

            combination.BurstWallMs[sample] = burstStopwatch.Elapsed.TotalMilliseconds;
            combination.CallbackMs.Add(callbackStopwatch.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// Builds the runtime biota an arm actually saves, already dirtied the way the sell path dirties it:
        /// the item leaves its container. Fresh objects every sample so no arm can benefit from another's state.
        /// </summary>
        private static (ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock) MakeDirtiedItem(uint id, int propertyGroups, int sample)
        {
            var biota = SyntheticBiotaFactory.Create(id, propertyGroups);

            biota.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
            biota.PropertiesIID[PropertyInstanceId.Container] = 0;

            biota.PropertiesInt ??= new Dictionary<PropertyInt, int>();
            biota.PropertiesInt[PropertyInt.Value] = 5000 + sample;

            return (biota, new ReaderWriterLockSlim());
        }

        /// <summary>
        /// Creates this block's items in the state the measured burst will dirty. Cold blocks go through
        /// BaseDatabase.SaveBiota with doNotAddToCache: true - synchronously, off the queue, and without
        /// warming the cache. Warm blocks are saved normally (which caches them on the miss path) and then read
        /// back once through GetBiota, mirroring how a login populates the cache one possession at a time.
        /// </summary>
        private static void SeedBurstBlock(uint blockBaseId, int n, int propertyGroups, bool warm)
        {
            if (!warm)
            {
                for (var i = 0; i < n; i++)
                {
                    var biota = SyntheticBiotaFactory.Create(blockBaseId + (uint)i, propertyGroups);
                    biota.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
                    biota.PropertiesIID[PropertyInstanceId.Container] = SeedContainerId;

                    DatabaseManager.Shard.BaseDatabase.SaveBiota(biota, new ReaderWriterLockSlim(), doNotAddToCache: true);
                }

                return;
            }

            var remaining = new CountdownEvent(n);

            for (var i = 0; i < n; i++)
            {
                var biota = SyntheticBiotaFactory.Create(blockBaseId + (uint)i, propertyGroups);
                biota.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
                biota.PropertiesIID[PropertyInstanceId.Container] = SeedContainerId;

                DatabaseManager.Shard.SaveBiota(biota, new ReaderWriterLockSlim(), _ => remaining.Signal());
            }

            remaining.Wait();

            for (var i = 0; i < n; i++)
                DatabaseManager.Shard.BaseDatabase.GetBiota(blockBaseId + (uint)i);
        }

        /// <summary>
        /// The login-shaped probe, copied from login-under-delete-load: one GetPossessedBiotasInParallel on the
        /// same serialized queue, timed end to end from enqueue to callback.
        /// </summary>
        private static double MeasureLogin(int expectedPossessions)
        {
            var done = new ManualResetEventSlim();
            var stopwatch = Stopwatch.StartNew();

            DatabaseManager.Shard.GetPossessedBiotasInParallel(CharacterId, possessed =>
            {
                stopwatch.Stop();

                var actualCount = possessed.Inventory.Count + possessed.WieldedItems.Count;
                if (expectedPossessions > 0 && actualCount != expectedPossessions)
                {
                    Console.WriteLine(
                        $"WARNING: possession count mismatch - expected {expectedPossessions}, got {actualCount}. " +
                        "This measurement is not trustworthy - check the possession chain setup.");
                }

                done.Set();
            });

            done.Wait();

            return stopwatch.Elapsed.TotalMilliseconds;
        }

        private static List<uint> SetUpPossessionChain(int packs, int itemsPerPack, int propertyGroups)
        {
            Console.WriteLine($"Setting up the login-probe possession chain: characterId=0x{CharacterId:X8}, packs={packs}, itemsPerPack={itemsPerPack}...");

            var ids = new List<uint>();
            var remaining = new CountdownEvent(packs + packs * itemsPerPack);

            for (var p = 0; p < packs; p++)
            {
                var packId = PackBaseId + (uint)p;
                ids.Add(packId);

                DatabaseManager.Shard.SaveBiota(MakePossession(packId, CharacterId, true, propertyGroups), new ReaderWriterLockSlim(), _ => remaining.Signal());

                for (var i = 0; i < itemsPerPack; i++)
                {
                    var itemId = ItemBaseId + (uint)(p * itemsPerPack + i);
                    ids.Add(itemId);

                    DatabaseManager.Shard.SaveBiota(MakePossession(itemId, packId, false, propertyGroups), new ReaderWriterLockSlim(), _ => remaining.Signal());
                }
            }

            remaining.Wait();

            Console.WriteLine($"Created {ids.Count} possession biotas ({packs} packs + {packs * itemsPerPack} items).");
            Console.WriteLine();

            return ids;
        }

        /// <summary>
        /// GetInventoryInParallel finds inventory by querying BiotaPropertiesIID for
        /// Type == PropertyInstanceId.Container && Value == parentId, and recurses into any child whose
        /// WeenieType is Container. isContainer marks a pack (a recursion point) rather than a leaf item.
        /// </summary>
        private static ACE.Entity.Models.Biota MakePossession(uint id, uint containerId, bool isContainer, int propertyGroups)
        {
            var biota = SyntheticBiotaFactory.Create(id, propertyGroups);

            if (isContainer)
                biota.WeenieType = ACE.Entity.Enum.WeenieType.Container;

            biota.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
            biota.PropertiesIID[PropertyInstanceId.Container] = containerId;

            return biota;
        }

        private static void ReportCombination(Combination combination)
        {
            var label = $"N={combination.N} arm{combination.Arm} {(combination.Warm ? "warm" : "cold")}";

            LatencyStats.Report($"{label} save callback ms", combination.CallbackMs);
            LatencyStats.Report($"{label} login under load ms", combination.UnderLoadLoginMs);
            LatencyStats.Report($"{label} login idle ms", combination.IdleLoginMs);

            var meanWall = Mean(combination.BurstWallMs);

            // These two are derived from the same measurement and move in OPPOSITE directions - a shorter wall
            // time is a higher throughput. Copying the direction from the neighbouring line is silently wrong
            // here, which is the whole reason MetricDirection is a required parameter.
            Metrics.Record($"{label} burst wall ms", meanWall, MetricDirection.LowerIsBetter, combination.BurstWallMs.Length);
            Metrics.Record($"{label} saves per sec", meanWall > 0 ? combination.N / (meanWall / 1000.0) : 0,
                MetricDirection.HigherIsBetter, combination.BurstWallMs.Length);

            Console.WriteLine(
                $"{label,-22} wall mean={meanWall,9:N1}ms  spread={combination.BurstWallMs.Min(),8:N1}..{combination.BurstWallMs.Max(),-8:N1}ms  " +
                $"throughput={(meanWall > 0 ? combination.N / (meanWall / 1000.0) : 0),9:N1} saves/sec");
            Console.WriteLine();
        }

        private static void PrintTable(List<Combination> results)
        {
            Console.WriteLine("=== SUMMARY ===");
            Console.WriteLine($"{"N",6} {"arm",4} {"cache",6} {"wall mean ms",13} {"wall min ms",12} {"wall max ms",12} {"saves/sec",11} {"login idle ms",14} {"login load ms",14}");

            foreach (var r in results)
            {
                var meanWall = Mean(r.BurstWallMs);

                Console.WriteLine(
                    $"{r.N,6} {r.Arm,4} {(r.Warm ? "warm" : "cold"),6} " +
                    $"{meanWall,13:N1} {r.BurstWallMs.Min(),12:N1} {r.BurstWallMs.Max(),12:N1} " +
                    $"{(meanWall > 0 ? r.N / (meanWall / 1000.0) : 0),11:N1} " +
                    $"{Mean(r.IdleLoginMs),14:N1} {Mean(r.UnderLoadLoginMs),14:N1}");
            }
        }

        private static List<int> ParseSizes(string raw)
        {
            var sizes = new List<int>();

            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token.Trim(), out var size) || size <= 0)
                    throw new ArgumentException($"--sizes contains an invalid entry '{token}' - expected a comma-separated list of positive integers.");

                sizes.Add(size);
            }

            if (sizes.Count == 0)
                throw new ArgumentException("--sizes produced no sizes.");

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

        private static void Cleanup(List<uint> possessionIds, List<uint> burstIds)
        {
            Console.WriteLine();
            Console.WriteLine($"Cleaning up {possessionIds.Count + burstIds.Count} test biotas...");

            var ids = new List<uint>(possessionIds);
            ids.AddRange(burstIds);

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Isolates the player-login possession read (GetPossessedBiotasInParallel) with nothing else in the process.
    ///
    /// Why a separate scenario from login-under-delete-load: that scenario CREATES the possession chain in the same
    /// process that then reads it, and on a build where SaveBiota populates the biota cache the "cold first read"
    /// is served entirely from memory. This scenario splits seeding and reading into separate PROCESSES
    /// (--mode=seed then --mode=read), so a read-mode run starts with a provably empty in-memory biota cache and
    /// every sample is a genuine cold read.
    ///
    /// It also reports the actual number of SQL statements the read issued, taken from MySQL's global Questions
    /// counter on the same server, so the cost model can be checked against statement counts rather than derived.
    /// </summary>
    public class PossessionLoadScenario : IScenario
    {
        public string Name => "possession-load";

        public string Description =>
            "Cold-cache player-login possession read, seeded and read in separate processes. Args: --mode=seed|read|cleanup --packs=3 --itemsPerPack=100 --wielded=10 --propertyGroups=7 --samples=1";

        // Same id layout as login-under-delete-load's possession chain, but based off SeedDynamicRangeMin so a
        // seeded chain survives the transient-range cleanup other scenarios do.
        //   +0x0000            character id owning the chain
        //   +0x0001 .. +0x00FF pack container ids
        //   +0x0100 ..         items inside packs
        //   +0x8000 ..         items wielded directly by the character
        private const uint BaseId = SyntheticBiotaFactory.SeedDynamicRangeMin + 0x00090000;

        public void Run(ScenarioArgs args)
        {
            var mode = args.GetString("mode", "read");
            var packs = args.GetInt("packs", 3);
            var itemsPerPack = args.GetInt("itemsPerPack", 100);
            var wielded = args.GetInt("wielded", 10);
            var propertyGroups = args.GetInt("propertyGroups", 7);
            var samples = args.GetInt("samples", 1);

            var characterId = BaseId;
            var packBaseId = BaseId + 0x1;
            var itemBaseId = BaseId + 0x100;
            var wieldedBaseId = BaseId + 0x8000;

            switch (mode)
            {
                case "seed":
                    Seed(characterId, packBaseId, itemBaseId, wieldedBaseId, packs, itemsPerPack, wielded, propertyGroups);
                    break;

                case "cleanup":
                    Cleanup(packBaseId, itemBaseId, wieldedBaseId, packs, itemsPerPack, wielded);
                    break;

                case "readsave":
                    ReadThenSave(characterId, packs + packs * itemsPerPack, wielded, args.GetInt("saveCount", 0), args.GetBool("mutate", false), args.GetInt("saveRepeats", 1));
                    break;

                default:
                    Read(characterId, packs + packs * itemsPerPack, wielded, samples);
                    break;
            }
        }

        private static void Seed(uint characterId, uint packBaseId, uint itemBaseId, uint wieldedBaseId, int packs, int itemsPerPack, int wielded, int propertyGroups)
        {
            Console.WriteLine($"Seeding possession chain: characterId=0x{characterId:X8}, packs={packs}, itemsPerPack={itemsPerPack}, wielded={wielded}, propertyGroups={propertyGroups}...");

            var total = packs + packs * itemsPerPack + wielded;
            var remaining = new CountdownEvent(total);

            for (var p = 0; p < packs; p++)
            {
                var packId = packBaseId + (uint)p;

                var packBiota = SyntheticBiotaFactory.CreatePossession(packId, characterId, isContainer: true, propertyGroups);
                DatabaseManager.Shard.SaveBiota(packBiota, new ReaderWriterLockSlim(), _ => remaining.Signal());

                for (var i = 0; i < itemsPerPack; i++)
                {
                    var itemId = itemBaseId + (uint)(p * itemsPerPack + i);

                    var itemBiota = SyntheticBiotaFactory.CreatePossession(itemId, packId, isContainer: false, propertyGroups);
                    DatabaseManager.Shard.SaveBiota(itemBiota, new ReaderWriterLockSlim(), _ => remaining.Signal());
                }
            }

            // Wielded items exercise GetWieldedItemsInParallel, which is a separate code path from inventory -
            // it matches on PropertyInstanceId.Wielder and never nests.
            for (var w = 0; w < wielded; w++)
            {
                var wieldedBiota = SyntheticBiotaFactory.Create(wieldedBaseId + (uint)w, propertyGroups);

                wieldedBiota.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
                wieldedBiota.PropertiesIID[PropertyInstanceId.Wielder] = characterId;

                DatabaseManager.Shard.SaveBiota(wieldedBiota, new ReaderWriterLockSlim(), _ => remaining.Signal());
            }

            remaining.Wait();

            Console.WriteLine($"Seeded {total} possession biotas. Run --mode=read in a SEPARATE process for a cold-cache read.");
        }

        private static void Cleanup(uint packBaseId, uint itemBaseId, uint wieldedBaseId, int packs, int itemsPerPack, int wielded)
        {
            var ids = new List<uint>();

            for (var p = 0; p < packs; p++)
            {
                ids.Add(packBaseId + (uint)p);
                for (var i = 0; i < itemsPerPack; i++)
                    ids.Add(itemBaseId + (uint)(p * itemsPerPack + i));
            }

            for (var w = 0; w < wielded; w++)
                ids.Add(wieldedBaseId + (uint)w);

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(ids, _ => remaining.Signal(), null);
            remaining.Wait();

            Console.WriteLine($"Removed {ids.Count} possession biotas.");
        }

        private static void Read(uint characterId, int expectedInventory, int expectedWielded, int samples)
        {
            var readMs = new double[samples];
            var statements = new long[samples];

            for (var s = 0; s < samples; s++)
            {
                var before = SqlStatementCounter.Read();

                var done = new ManualResetEventSlim();
                var stopwatch = Stopwatch.StartNew();
                var inventoryCount = 0;
                var wieldedCount = 0;
                var propertyRowCount = 0;

                DatabaseManager.Shard.GetPossessedBiotasInParallel(characterId, possessed =>
                {
                    stopwatch.Stop();
                    inventoryCount = possessed.Inventory.Count;
                    wieldedCount = possessed.WieldedItems.Count;

                    // Counting property rows, not just objects: a bulk loader that returned the right number of
                    // biotas with empty property collections would otherwise look correct here.
                    foreach (var biota in possessed.Inventory)
                        propertyRowCount += CountPropertyRows(biota);
                    foreach (var biota in possessed.WieldedItems)
                        propertyRowCount += CountPropertyRows(biota);

                    done.Set();
                });

                done.Wait();

                var after = SqlStatementCounter.Read();

                readMs[s] = stopwatch.Elapsed.TotalMilliseconds;

                statements[s] = SqlStatementCounter.Delta(before, after);

                if (s == 0 && (inventoryCount != expectedInventory || wieldedCount != expectedWielded))
                {
                    Console.WriteLine(
                        $"WARNING: possession count mismatch - expected {expectedInventory} inventory / {expectedWielded} wielded, " +
                        $"got {inventoryCount} / {wieldedCount}. Seed the chain first with --mode=seed. " +
                        "This measurement is not trustworthy.");
                }

                Console.WriteLine($"  sample {s}: {readMs[s]:N2}ms, {inventoryCount} inventory + {wieldedCount} wielded, {propertyRowCount} property rows, {statements[s]} SQL statements");
            }

            LatencyStats.Report("Possession read ms", readMs);


            if (statements[0] >= 0)
            {
                double sum = 0;
                foreach (var v in statements)
                    sum += v;

                // Fewer round trips is the point of the batched possession read, so lower is better - and this is
                // a discrete FACT, not a timing: it read exactly 68 on all six identical-build calibration runs.
                // Marking it deterministic exempts it from the minimum-sample gate (n says nothing about a
                // statement count's reliability) and judges it at 0%, so an N+1 regression that pushed 68 back
                // into the hundreds is caught on the first run rather than being averaged away. Without this the
                // metric is n=1 and this whole scenario gates nothing.
                Metrics.Record("Possession read SQL statements", sum / samples, MetricDirection.LowerIsBetter,
                    samples, deterministic: true);
                Console.WriteLine($"Mean SQL statements per possession read: {sum / samples:N0}");
            }
        }

        /// <summary>
        /// The login read followed immediately by a save of everything it returned - the sequence that decides
        /// whether the biota cache is doing anything for the possession working set.
        ///
        /// A read that populates the cache makes the following save a cache hit (an in-place update against the
        /// cached object's own live context). A read that does not populate it makes that same save a cache miss,
        /// which SaveBiotaBatch stages into one shared context and commits once. Which of those is cheaper is not
        /// obvious, so it is measured rather than argued: this prints SaveBatchStats' cache-hit ratio next to the
        /// save's wall time.
        /// </summary>
        private static void ReadThenSave(uint characterId, int expectedInventory, int expectedWielded, int saveCount, bool mutate, int saveRepeats)
        {
            var readDone = new ManualResetEventSlim();
            var readStopwatch = Stopwatch.StartNew();
            List<ACE.Database.Models.Shard.Biota> possessions = null;

            DatabaseManager.Shard.GetPossessedBiotasInParallel(characterId, possessed =>
            {
                readStopwatch.Stop();

                possessions = new List<ACE.Database.Models.Shard.Biota>(possessed.Inventory);
                possessions.AddRange(possessed.WieldedItems);

                readDone.Set();
            });

            readDone.Wait();

            Console.WriteLine($"Read: {readStopwatch.Elapsed.TotalMilliseconds:N2}ms, {possessions.Count} possessions " +
                              $"(expected {expectedInventory + expectedWielded}).");

            // These come back as the EF shard Biota; the save path takes the runtime ACE.Entity.Models.Biota, so
            // they have to be converted. Passing the EF type here would not compile, which is the good case - the
            // bad case is converting the wrong direction, which does compile.
            if (saveCount > 0 && saveCount < possessions.Count)
                possessions = possessions.GetRange(0, saveCount);

            var toSave = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>(possessions.Count);
            foreach (var possession in possessions)
                toSave.Add((ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota(possession), new ReaderWriterLockSlim()));

            // saveRepeats > 1 exists because the FIRST write in a process pays EF's update-pipeline warm-up,
            // which at these sizes is bigger than the effect being measured. Report the later repeats.
            for (var r = 0; r < saveRepeats; r++)
            {
                // Saving an unchanged biota flatters the cache-hit path: UpdateDatabaseBiota finds no
                // modifications and SaveChanges() issues no SQL at all. --mutate makes every save a real write on
                // both paths, which is the case actual gameplay produces.
                if (mutate)
                {
                    foreach (var (biota, _) in toSave)
                    {
                        biota.PropertiesInt ??= new Dictionary<PropertyInt, int>();
                        biota.PropertiesInt[PropertyInt.EncumbranceVal] = (r * 7 + 1) & 0xFFFF;
                    }
                }

                SaveBatchStats.Reset();

                var saveStatementsBefore = SqlStatementCounter.Read();

                var saveDone = new ManualResetEventSlim();
                var saveStopwatch = Stopwatch.StartNew();

                DatabaseManager.Shard.SaveBiotasInParallel(toSave, _ =>
                {
                    saveStopwatch.Stop();
                    saveDone.Set();
                });

                saveDone.Wait();

                var saveStatements = SqlStatementCounter.Delta(saveStatementsBefore, SqlStatementCounter.Read());

                Console.WriteLine($"Save #{r} of {toSave.Count} possessions: {saveStopwatch.Elapsed.TotalMilliseconds:N2}ms, {saveStatements} SQL statements");
            }

            Console.WriteLine();
            Console.WriteLine(SaveBatchStats.GetReport());
        }

        /// <summary>
        /// Property rows actually attached to one EF biota, across the collections a possession populates.
        /// A loader that returned the right object count with empty collections would pass an object-count
        /// assertion and still be badly wrong, so this is the assertion that matters.
        /// </summary>
        private static int CountPropertyRows(Biota biota)
        {
            return (biota.BiotaPropertiesString?.Count ?? 0)
                 + (biota.BiotaPropertiesInt?.Count ?? 0)
                 + (biota.BiotaPropertiesInt64?.Count ?? 0)
                 + (biota.BiotaPropertiesFloat?.Count ?? 0)
                 + (biota.BiotaPropertiesBool?.Count ?? 0)
                 + (biota.BiotaPropertiesDID?.Count ?? 0)
                 + (biota.BiotaPropertiesIID?.Count ?? 0)
                 + (biota.BiotaPropertiesPosition?.Count ?? 0);
        }
    }
}

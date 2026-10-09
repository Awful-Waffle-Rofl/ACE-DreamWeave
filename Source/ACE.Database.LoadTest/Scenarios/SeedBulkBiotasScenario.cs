using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using ACE.Database;
using ACE.Entity.Enum;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Bulk-seeds generic biotas spread across a handful of WeenieType values, purely to inflate the total row
    /// count of the biota table - so `type-scan` has something realistic to full-table-scan against (ShardDatabase
    /// GetBiotasByType is flagged in its own code comment as unindexed).
    ///
    /// Goes straight to BaseDatabase.SaveBiotasInParallel, bypassing the single-threaded serialized queue, the
    /// same way ShardDatabaseOfflineTools' bulk operations do - this is a seeding operation, not something meant
    /// to simulate serialized in-game write pressure (that's what queue-saturation is for).
    /// </summary>
    public class SeedBulkBiotasScenario : IScenario
    {
        public string Name => "seed-bulk";

        public string Description => "Bulk-seeds generic biotas to inflate total table size. Args: --count=20000 --typeCount=5 --propertyGroups=1";

        public void Run(ScenarioArgs args)
        {
            var count = args.GetInt("count", 20000);
            var typeCount = Math.Max(1, args.GetInt("typeCount", 5));
            var propertyGroups = args.GetInt("propertyGroups", 1);

            var existingMax = DatabaseManager.Shard.BaseDatabase.GetMaxGuidFoundInRange(SyntheticBiotaFactory.SeedBulkRangeMin, SyntheticBiotaFactory.SeedBulkRangeMax);
            var start = existingMax == uint.MaxValue ? SyntheticBiotaFactory.SeedBulkRangeMin : existingMax + 1;

            Console.WriteLine($"Seeding {count} bulk biotas (ids 0x{start:X8}+) spread across {typeCount} WeenieType values...");

            var biotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>(count);

            for (var i = 0; i < count; i++)
            {
                var biota = SyntheticBiotaFactory.Create(start + (uint)i, propertyGroups);
                biota.WeenieType = (WeenieType)(1 + (i % typeCount));
                biotas.Add((biota, new ReaderWriterLockSlim()));
            }

            var stopwatch = Stopwatch.StartNew();
            var success = DatabaseManager.Shard.BaseDatabase.SaveBiotasInParallel(biotas);
            stopwatch.Stop();

            Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds:N1}s ({count / stopwatch.Elapsed.TotalSeconds:N0}/sec). Success: {success}.");
            Console.WriteLine($"Try: type-scan --type=1   (any value 1-{typeCount} was used)");
        }
    }
}

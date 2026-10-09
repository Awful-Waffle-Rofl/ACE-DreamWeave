using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Populates a landblock with synthetic static + dynamic objects so `landblock-population` has real data to
    /// measure. Static object guids must fall in that landblock's reserved static range (ShardDatabase.cs
    /// GetStaticObjectsByLandblock matches on guid range alone); dynamic objects are matched by a Location
    /// position inside the landblock's cell range instead, so their guids come from a separate persistent seed
    /// range (SyntheticBiotaFactory.SeedDynamicRangeMin/Max) that other scenarios never touch.
    ///
    /// Safe to re-run / seed multiple landblocks - it looks up the highest existing guid in the relevant range
    /// each time and continues after it, rather than always starting from the bottom.
    /// </summary>
    public class SeedLandblockScenario : IScenario
    {
        public string Name => "seed-landblock";

        public string Description => "Seeds static+dynamic objects for a landblock. Args: --landblock=0x7D64 --staticCount=200 --dynamicCount=300 --propertyGroups=3";

        public void Run(ScenarioArgs args)
        {
            var landblockId = (ushort)args.GetUInt("landblock", 0x7D64);
            var staticCount = Math.Min(args.GetInt("staticCount", 200), 4096);
            var dynamicCount = args.GetInt("dynamicCount", 300);
            var propertyGroups = args.GetInt("propertyGroups", 3);

            var staticMin = (uint)(0x70000 | landblockId) << 12;
            var staticMax = staticMin | 0xFFF;

            var existingStaticMax = DatabaseManager.Shard.BaseDatabase.GetMaxGuidFoundInRange(staticMin, staticMax);
            var staticStart = existingStaticMax == uint.MaxValue ? staticMin : existingStaticMax + 1;
            staticCount = (int)Math.Min(staticCount, staticMax - staticStart + 1);

            var existingDynamicMax = DatabaseManager.Shard.BaseDatabase.GetMaxGuidFoundInRange(SyntheticBiotaFactory.SeedDynamicRangeMin, SyntheticBiotaFactory.SeedDynamicRangeMax);
            var dynamicStart = existingDynamicMax == uint.MaxValue ? SyntheticBiotaFactory.SeedDynamicRangeMin : existingDynamicMax + 1;

            Console.WriteLine($"Seeding landblock 0x{landblockId:X4}: {staticCount} static objects (0x{staticStart:X8}+), {dynamicCount} dynamic objects (0x{dynamicStart:X8}+)...");

            var biotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();

            for (var i = 0; i < staticCount; i++)
                biotas.Add((SyntheticBiotaFactory.Create(staticStart + (uint)i, propertyGroups), new ReaderWriterLockSlim()));

            for (var i = 0; i < dynamicCount; i++)
                biotas.Add((SyntheticBiotaFactory.CreateDynamic(dynamicStart + (uint)i, landblockId, (uint)i, propertyGroups), new ReaderWriterLockSlim()));

            // SaveBiotasInParallel fans out internally and reports one aggregate result, not one per biota.
            var remaining = new CountdownEvent(1);
            var allSucceeded = true;

            DatabaseManager.Shard.SaveBiotasInParallel(biotas, success =>
            {
                allSucceeded = success;
                remaining.Signal();
            });

            remaining.Wait();

            Console.WriteLine(allSucceeded
                ? $"Done. {biotas.Count} objects saved."
                : "One or more objects failed to save - check the server log.");
            Console.WriteLine($"Try: landblock-population --landblock=0x{landblockId:X4}");
        }
    }
}

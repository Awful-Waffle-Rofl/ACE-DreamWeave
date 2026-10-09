using System;
using System.Diagnostics;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// ShardDatabase.GetBiota (ACE.Database/ShardDatabase.cs) reassembles a biota with one query per populated
    /// property table (up to ~24 round trips per object). Landblock population loads call GetBiota once per
    /// static/dynamic object in the landblock, so this cost multiplies by object count. This times a full
    /// landblock load cold (empty biota cache) and then repeats it warm, to isolate cache benefit from the
    /// underlying per-object query fan-out cost.
    /// </summary>
    public class LandblockPopulationScenario : IScenario
    {
        public string Name => "landblock-population";

        public string Description => "Times static+dynamic object population load for a landblock. Args: --landblock=0x7D64 --iterations=5";

        public void Run(ScenarioArgs args)
        {
            var landblockId = (ushort)args.GetUInt("landblock", 0x7D64); // Yaraq by default
            var iterations = args.GetInt("iterations", 5);

            Console.WriteLine($"Loading landblock 0x{landblockId:X4}...");

            var stopwatch = Stopwatch.StartNew();
            var statics = DatabaseManager.Shard.BaseDatabase.GetStaticObjectsByLandblock(landblockId);
            var dynamics = DatabaseManager.Shard.BaseDatabase.GetDynamicObjectsByLandblock(landblockId, 0);
            stopwatch.Stop();

            var objectCount = statics.Count + dynamics.Count;

            Console.WriteLine($"Cold load: {objectCount} objects ({statics.Count} static, {dynamics.Count} dynamic) in {stopwatch.Elapsed.TotalMilliseconds:N0}ms " +
                               $"({(objectCount == 0 ? 0 : stopwatch.Elapsed.TotalMilliseconds / objectCount):N2}ms/object)");

            if (objectCount == 0)
            {
                Console.WriteLine("No objects found - pick a landblock id that exists in this shard database.");
                return;
            }

            Console.WriteLine($"Repeating {iterations}x with a warm biota cache...");

            var warmMs = new double[iterations];

            for (var i = 0; i < iterations; i++)
            {
                var warmStopwatch = Stopwatch.StartNew();
                DatabaseManager.Shard.BaseDatabase.GetStaticObjectsByLandblock(landblockId);
                DatabaseManager.Shard.BaseDatabase.GetDynamicObjectsByLandblock(landblockId, 0);
                warmStopwatch.Stop();

                warmMs[i] = warmStopwatch.Elapsed.TotalMilliseconds;
            }

            LatencyStats.Report("Warm reload", warmMs);
        }
    }
}

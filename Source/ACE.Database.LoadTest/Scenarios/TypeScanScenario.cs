using System;
using System.Diagnostics;
using System.Linq;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using Microsoft.EntityFrameworkCore;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// ShardDatabase.GetBiotasByType is flagged in its own code comment as "currently unindexed", and also does
    /// the same per-row GetBiota N+1 fan-out as landblock loading. This isolates the two costs: a raw WHERE
    /// scan against the biota table (no fan-out), vs. the full GetBiotasByType call (scan + fan-out), so we can
    /// tell how much of the total cost is the missing index vs. the already-quantified N+1 pattern.
    /// </summary>
    public class TypeScanScenario : IScenario
    {
        public string Name => "type-scan";

        public string Description => "Times GetBiotasByType, isolating the raw unindexed scan from the N+1 fan-out. Args: --type=1";

        public void Run(ScenarioArgs args)
        {
            var type = (WeenieType)args.GetInt("type", 1);
            var iType = (int)type;

            int matchingCount;
            double rawScanMs;

            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var stopwatch = Stopwatch.StartNew();
                matchingCount = context.Biota.Count(r => r.WeenieType == iType);
                stopwatch.Stop();

                rawScanMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            Console.WriteLine($"Raw WHERE WeenieType={iType} scan: {matchingCount} matching rows in {rawScanMs:N1}ms (no N+1 fan-out, just the table scan).");

            var fullStopwatch = Stopwatch.StartNew();
            var biotas = DatabaseManager.Shard.BaseDatabase.GetBiotasByType(type);
            fullStopwatch.Stop();

            var fullMs = fullStopwatch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"Full GetBiotasByType (scan + N+1 GetBiota fan-out): {biotas.Count} objects in {fullMs:N0}ms " +
                               $"({(biotas.Count == 0 ? 0 : fullMs / biotas.Count):N2}ms/object).");

            if (rawScanMs > 0)
                Console.WriteLine($"Fan-out overhead: {fullMs - rawScanMs:N0}ms on top of the raw scan ({fullMs / rawScanMs:N1}x the scan alone).");
        }
    }
}

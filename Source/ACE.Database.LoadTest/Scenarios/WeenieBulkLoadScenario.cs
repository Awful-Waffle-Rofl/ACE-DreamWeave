using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Adapter;
using ACE.Database.Models.World;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// Read-only benchmark of the bulk weenie loader against the configured WORLD database: the same wcids loaded
    /// and converted four ways - the legacy per-id reader, the core with one id at a time (today's miss path with
    /// weenie_bulk_load on), and PrefetchWeenies in chunks with the self-check off and at its default sample - each
    /// reporting wall ms per weenie, SQL statements, and bytes the server sent per weenie.
    /// <para />
    /// Statements and bytes come from MySQL's global Questions and Bytes_sent counters, so they are only meaningful
    /// with nothing else talking to the same server (see SqlStatementCounter). Every arm runs once unmeasured first,
    /// so all four are measured against the same warm buffer pool and connection pool.
    /// </summary>
    public class WeenieBulkLoadScenario : IScenario
    {
        public string Name => "weenie-bulk-load";

        public string Description => "Read-only: per-id vs bulk weenie loading from the world DB (ms, statements, bytes per weenie). Args: --count=2000 --chunk=250 --selfcheck=50";

        public void Run(ScenarioArgs args)
        {
            var count = args.GetInt("count", 2000);
            var chunkSize = args.GetInt("chunk", WorldDatabase.DefaultWeenieChunkSize);
            var selfCheck = args.GetInt("selfcheck", (int)WeenieBulkLoadSettings.DefaultSelfCheckSample);

            List<uint> allIds;
            using (var context = new WorldDbContext())
                allIds = context.Weenie.AsNoTracking().Select(w => w.ClassId).OrderBy(id => id).ToList();

            // Spread across the whole id range rather than one contiguous block, so creatures, items and fork ids mix.
            var step = Math.Max(1, allIds.Count / Math.Max(1, count));
            var ids = allIds.Where((_, i) => i % step == 0).Take(count).ToList();

            Console.WriteLine($"World database has {allIds.Count} weenies; benchmarking {ids.Count} (every {step}th), chunk {chunkSize}, self-check sample {selfCheck}.");
            Console.WriteLine();

            var reference = new WorldDatabase();

            var arms = new (string Name, Action Load)[]
            {
                ("per-id legacy", () =>
                {
                    foreach (var id in ids)
                        using (var context = NewNoTracking())
                            Convert(reference.GetWeenieLegacy(context, id));
                }),
                ("per-id core", () =>
                {
                    foreach (var id in ids)
                        using (var context = NewNoTracking())
                            Convert(reference.GetWeeniesCore(context, new[] { id }).GetValueOrDefault(id));
                }),
                ("bulk selfcheck=0", () => new WorldDatabaseWithEntityCache().PrefetchWeenies(ids, Timeout.InfiniteTimeSpan, chunkSize, 0)),
                ($"bulk selfcheck={selfCheck}", () => new WorldDatabaseWithEntityCache().PrefetchWeenies(ids, Timeout.InfiniteTimeSpan, chunkSize, selfCheck)),
            };

            foreach (var arm in arms)
                arm.Load(); // warm-up, unmeasured

            Console.WriteLine($"{"arm",-22} {"total ms",10} {"ms/weenie",10} {"statements",11} {"stmt/weenie",12} {"bytes/weenie",13}");

            foreach (var arm in arms)
            {
                var before = ReadStatus();
                var stopwatch = Stopwatch.StartNew();

                arm.Load();

                stopwatch.Stop();
                var after = ReadStatus();

                // The second ReadStatus is itself one statement; discount it, as SqlStatementCounter.Delta does.
                var statements = before.Questions < 0 || after.Questions < 0 ? -1 : after.Questions - before.Questions - 1;
                var bytes = before.BytesSent < 0 || after.BytesSent < 0 ? -1 : after.BytesSent - before.BytesSent;
                var ms = stopwatch.Elapsed.TotalMilliseconds;

                Console.WriteLine($"{arm.Name,-22} {ms,10:N0} {ms / ids.Count,10:N3} {statements,11:N0} {(double)statements / ids.Count,12:N2} {(double)bytes / ids.Count,13:N0}");

                Metrics.Record($"{arm.Name} ms per weenie", ms / ids.Count, MetricDirection.LowerIsBetter, ids.Count);
                Metrics.Record($"{arm.Name} SQL statements", statements, MetricDirection.LowerIsBetter, 1, deterministic: true);
                Metrics.Record($"{arm.Name} bytes per weenie", (double)bytes / ids.Count, MetricDirection.Informational, ids.Count);
            }
        }

        private static WorldDbContext NewNoTracking()
        {
            var context = new WorldDbContext();
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return context;
        }

        private static void Convert(Weenie weenie)
        {
            if (weenie != null)
                WeenieConverter.ConvertToEntityWeenie(weenie);
        }

        /// <summary>Global Questions and Bytes_sent in one statement; -1 each when unreadable.</summary>
        private static (long Questions, long BytesSent) ReadStatus()
        {
            try
            {
                using var context = new WorldDbContext();
                var connection = context.Database.GetDbConnection();

                if (connection.State != ConnectionState.Open)
                    connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SHOW GLOBAL STATUS WHERE Variable_name IN ('Questions', 'Bytes_sent')";

                long questions = -1, bytesSent = -1;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetString(0) == "Questions")
                        questions = long.Parse(reader.GetString(1));
                    else if (reader.GetString(0) == "Bytes_sent")
                        bytesSent = long.Parse(reader.GetString(1));
                }

                return (questions, bytesSent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (could not read global status: {ex.Message})");
                return (-1, -1);
            }
        }
    }
}

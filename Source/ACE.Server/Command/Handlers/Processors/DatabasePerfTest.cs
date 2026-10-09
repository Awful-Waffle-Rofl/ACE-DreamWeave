using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers.Processors
{
    class DatabasePerfTest
    {
        public const int DefaultBiotasTestCount = 1000;

        private readonly List<uint> testWeenies = new List<uint>
        {
            1,      // Clay
            9035,   // Exarch Plate Girth
            9034,   // Exarch Plate Coat
            27361,  // Palenqual's Ukira of the Vortex
            29947,  // Bracelet of Creature Enchantments
        };

        private static bool SessionIsStillInWorld(Session session)
        {
            return session.State == Network.Enum.SessionState.WorldConnected && session.Player != null;
        }

        public void RunAsync(Session session, int biotasPerTest = DefaultBiotasTestCount)
        {
            Task.Run(() => Run(session, biotasPerTest));
        }

        private void Run(Session session, int biotasPerTest)
        {
            CommandHandlerHelper.WriteOutputInfo(session, $"Starting Shard Database Performance Tests.\nBiotas per test: {biotasPerTest}\nThis may take several minutes to complete...\nCurrent database queue count: {DatabaseManager.Shard.QueueCount}");


            // Get the current queue wait time
            bool responseReceived = false;

            DatabaseManager.Shard.GetCurrentQueueWaitTime(result =>
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Current database queue wait time: {result.TotalMilliseconds:N0} ms");
                responseReceived = true;
            });

            while (!responseReceived)
                Thread.Sleep(1);


            // Generate Individual WorldObjects
            var biotas = new Collection<(Biota biota, ReaderWriterLockSlim rwLock)>();

            for (int i = 0; i < biotasPerTest; i++)
            {
                var worldObject = WorldObjectFactory.CreateNewWorldObject(testWeenies[i % testWeenies.Count]);
                biotas.Add((worldObject.Biota, worldObject.BiotaDatabaseLock));
            }


            // Add biotasPerTest biotas individually
            long trueResults = 0;
            long falseResults = 0;
            var startTime = DateTime.UtcNow;
            var initialQueueWaitTime = TimeSpan.Zero;
            var totalQueryExecutionTime = TimeSpan.Zero;
            foreach (var biota in biotas)
            {
                DatabaseManager.Shard.SaveBiota(biota.biota, biota.rwLock, result =>
                {
                    if (result)
                        Interlocked.Increment(ref trueResults);
                    else
                        Interlocked.Increment(ref falseResults);
                }, (queueWaitTime, queryExecutionTime) =>
                {
                    if (initialQueueWaitTime == TimeSpan.Zero)
                        initialQueueWaitTime = queueWaitTime;

                    totalQueryExecutionTime += queryExecutionTime;
                });
            }

            while (Interlocked.Read(ref trueResults) + Interlocked.Read(ref falseResults) < biotas.Count)
                Thread.Sleep(1);

            var endTime = DateTime.UtcNow;
            ReportResult(session, "individual add", biotasPerTest, (endTime - startTime), initialQueueWaitTime, totalQueryExecutionTime, trueResults, falseResults);


            // Update biotasPerTest biotas individually
            if (session == null || SessionIsStillInWorld(session))
            {
                ModifyBiotas(biotas);

                trueResults = 0;
                falseResults = 0;
                startTime = DateTime.UtcNow;
                initialQueueWaitTime = TimeSpan.Zero;
                totalQueryExecutionTime = TimeSpan.Zero;

                foreach (var biota in biotas)
                {
                    DatabaseManager.Shard.SaveBiota(biota.biota, biota.rwLock, result =>
                    {
                        if (result)
                            Interlocked.Increment(ref trueResults);
                        else
                            Interlocked.Increment(ref falseResults);
                    }, (queueWaitTime, queryExecutionTime) =>
                    {
                        if (initialQueueWaitTime == TimeSpan.Zero)
                            initialQueueWaitTime = queueWaitTime;

                        totalQueryExecutionTime += queryExecutionTime;
                    });
                }

                while (Interlocked.Read(ref trueResults) + Interlocked.Read(ref falseResults) < biotas.Count)
                    Thread.Sleep(1);

                endTime = DateTime.UtcNow;
                ReportResult(session, "individual save", biotasPerTest, (endTime - startTime), initialQueueWaitTime, totalQueryExecutionTime, trueResults, falseResults);
            }


            // Delete biotasPerTest biotas individually
            trueResults = 0;
            falseResults = 0;
            startTime = DateTime.UtcNow;
            initialQueueWaitTime = TimeSpan.Zero;
            totalQueryExecutionTime = TimeSpan.Zero;

            foreach (var biota in biotas)
            {
                DatabaseManager.Shard.RemoveBiota(biota.biota.Id, result =>
                {
                    if (result)
                        Interlocked.Increment(ref trueResults);
                    else
                        Interlocked.Increment(ref falseResults);
                }, (queueWaitTime, queryExecutionTime) =>
                {
                    if (initialQueueWaitTime == TimeSpan.Zero)
                        initialQueueWaitTime = queueWaitTime;

                    totalQueryExecutionTime += queryExecutionTime;
                });
            }

            while (Interlocked.Read(ref trueResults) + Interlocked.Read(ref falseResults) < biotas.Count)
                Thread.Sleep(1);

            endTime = DateTime.UtcNow;
            ReportResult(session, "individual remove", biotasPerTest, (endTime - startTime), initialQueueWaitTime, totalQueryExecutionTime, trueResults, falseResults);


            if (session != null && !SessionIsStillInWorld(session))
                return;

            // Generate Bulk WorldObjects
            biotas.Clear();

            for (int i = 0; i < biotasPerTest; i++)
            {
                var worldObject = WorldObjectFactory.CreateNewWorldObject(testWeenies[i % testWeenies.Count]);
                biotas.Add((worldObject.Biota, worldObject.BiotaDatabaseLock));
            }


            // Add biotasPerTest biotas in bulk
            trueResults = 0;
            falseResults = 0;
            startTime = DateTime.UtcNow;
            initialQueueWaitTime = TimeSpan.Zero;
            totalQueryExecutionTime = TimeSpan.Zero;
            DatabaseManager.Shard.SaveBiotasInParallel(biotas, result =>
            {
                if (result)
                    Interlocked.Increment(ref trueResults);
                else
                    Interlocked.Increment(ref falseResults);
            }, (queueWaitTime, queryExecutionTime) =>
            {
                if (initialQueueWaitTime == TimeSpan.Zero)
                    initialQueueWaitTime = queueWaitTime;

                totalQueryExecutionTime += queryExecutionTime;
            });

            while (Interlocked.Read(ref trueResults) + Interlocked.Read(ref falseResults) < 1)
                Thread.Sleep(1);

            endTime = DateTime.UtcNow;
            ReportResult(session, "bulk add", biotasPerTest, (endTime - startTime), initialQueueWaitTime, totalQueryExecutionTime, trueResults, falseResults);


            // Update biotasPerTest biotas in bulk
            if (session == null || SessionIsStillInWorld(session))
            {
                ModifyBiotas(biotas);

                trueResults = 0;
                falseResults = 0;
                startTime = DateTime.UtcNow;
                initialQueueWaitTime = TimeSpan.Zero;
                totalQueryExecutionTime = TimeSpan.Zero;
                DatabaseManager.Shard.SaveBiotasInParallel(biotas, result =>
                {
                    if (result)
                        Interlocked.Increment(ref trueResults);
                    else
                        Interlocked.Increment(ref falseResults);
                }, (queueWaitTime, queryExecutionTime) =>
                {
                    if (initialQueueWaitTime == TimeSpan.Zero)
                        initialQueueWaitTime = queueWaitTime;

                    totalQueryExecutionTime += queryExecutionTime;
                });

                while (Interlocked.Read(ref trueResults) + Interlocked.Read(ref falseResults) < 1)
                    Thread.Sleep(1);

                endTime = DateTime.UtcNow;
                ReportResult(session, "bulk save", biotasPerTest, (endTime - startTime), initialQueueWaitTime, totalQueryExecutionTime, trueResults, falseResults);
            }


            // Delete biotasPerTest biotas in bulk
            trueResults = 0;
            falseResults = 0;
            startTime = DateTime.UtcNow;
            initialQueueWaitTime = TimeSpan.Zero;
            totalQueryExecutionTime = TimeSpan.Zero;

            var ids = biotas.Select(r => r.biota.Id).ToList();

            DatabaseManager.Shard.RemoveBiotasInParallel(ids, result =>
            {
                if (result)
                    Interlocked.Increment(ref trueResults);
                else
                    Interlocked.Increment(ref falseResults);
            }, (queueWaitTime, queryExecutionTime) =>
            {
                if (initialQueueWaitTime == TimeSpan.Zero)
                    initialQueueWaitTime = queueWaitTime;

                totalQueryExecutionTime += queryExecutionTime;
            });

            while (Interlocked.Read(ref trueResults) + Interlocked.Read(ref falseResults) < 1)
                Thread.Sleep(1);

            endTime = DateTime.UtcNow;
            ReportResult(session, "bulk remove", biotasPerTest, (endTime - startTime), initialQueueWaitTime, totalQueryExecutionTime, trueResults, falseResults);


            if (session != null && !SessionIsStillInWorld(session))
                return;

            CommandHandlerHelper.WriteOutputInfo(session, "Database Performance Tests Completed");
        }

        // Sentinel properties reserved for internal/debug use, never touched by normal gameplay - safe to churn here.
        private const PropertyInt SentinelInt = PropertyInt.PCAPRecordedPlacement;
        private const PropertyInt64 SentinelInt64 = PropertyInt64.VerifyXp;
        private const PropertyInstanceId SentinelIID = PropertyInstanceId.PCAPRecordedObjectIID;
        private const PropertyDataId SentinelDID = PropertyDataId.PCAPRecordedWeenieHeader;
        private const PropertyFloat SentinelFloat = PropertyFloat.PCAPRecordedWorkmanship;
        private const PropertyBool SentinelBool = PropertyBool.HouseEvicted;
        private const PropertyString SentinelString = PropertyString.PCAPRecordedCurrentMotionState;

        private static void ModifyBiotas(ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> biotas)
        {
            foreach (var (biota, rwLock) in biotas)
            {
                // Update an existing record (if this weenie happens to have one of that property type)
                if (biota.PropertiesInt?.Count > 0)
                {
                    var key = biota.PropertiesInt.Keys.First();
                    biota.SetProperty(key, biota.PropertiesInt[key] + 1, rwLock, out _);
                }

                if (biota.PropertiesInt64?.Count > 0)
                {
                    var key = biota.PropertiesInt64.Keys.First();
                    biota.SetProperty(key, biota.PropertiesInt64[key] + 1, rwLock, out _);
                }

                if (biota.PropertiesIID?.Count > 0)
                {
                    var key = biota.PropertiesIID.Keys.First();
                    biota.SetProperty(key, biota.PropertiesIID[key] + 1, rwLock, out _);
                }

                if (biota.PropertiesDID?.Count > 0)
                {
                    var key = biota.PropertiesDID.Keys.First();
                    biota.SetProperty(key, biota.PropertiesDID[key] + 1, rwLock, out _);
                }

                if (biota.PropertiesFloat?.Count > 0)
                {
                    var key = biota.PropertiesFloat.Keys.First();
                    biota.SetProperty(key, biota.PropertiesFloat[key] + 1, rwLock, out _);
                }

                if (biota.PropertiesBool?.Count > 0)
                {
                    var key = biota.PropertiesBool.Keys.First();
                    biota.SetProperty(key, !biota.PropertiesBool[key], rwLock, out _);
                }

                if (biota.PropertiesString?.Count > 0)
                {
                    var key = biota.PropertiesString.Keys.First();
                    biota.SetProperty(key, biota.PropertiesString[key] + " test", rwLock, out _);
                }

                // Remove a sentinel record, in case a prior pass through this method left one behind
                biota.TryRemoveProperty(SentinelInt, rwLock);
                biota.TryRemoveProperty(SentinelInt64, rwLock);
                biota.TryRemoveProperty(SentinelIID, rwLock);
                biota.TryRemoveProperty(SentinelDID, rwLock);
                biota.TryRemoveProperty(SentinelFloat, rwLock);
                biota.TryRemoveProperty(SentinelBool, rwLock);
                biota.TryRemoveProperty(SentinelString, rwLock);

                // Add a new record
                biota.SetProperty(SentinelInt, 0, rwLock, out _);
                biota.SetProperty(SentinelInt64, 0L, rwLock, out _);
                biota.SetProperty(SentinelIID, 0u, rwLock, out _);
                biota.SetProperty(SentinelDID, 0u, rwLock, out _);
                biota.SetProperty(SentinelFloat, 0d, rwLock, out _);
                biota.SetProperty(SentinelBool, false, rwLock, out _);
                biota.SetProperty(SentinelString, "", rwLock, out _);
            }
        }

        private static void ReportResult(Session session, string testDescription, int biotasPerTest, TimeSpan duration, TimeSpan queueWaitTime, TimeSpan totalQueryExecutionTime, long trueResults, long falseResults)
        {
            if (session != null && !SessionIsStillInWorld(session))
                return;

            CommandHandlerHelper.WriteOutputInfo(session, $"{biotasPerTest} {testDescription.PadRight(17)} Duration: {duration.TotalSeconds.ToString("N1").PadLeft(5)} s. Queue Wait Time: {queueWaitTime.TotalMilliseconds.ToString("N0").PadLeft(3)} ms. Average Execution Time: {(totalQueryExecutionTime.TotalMilliseconds / biotasPerTest).ToString("N0").PadLeft(3)} ms. Success/Fail: {trueResults}/{falseResults}.", ChatMessageType.System);
        }
    }
}

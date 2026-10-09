using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// The correctness counterpart to the performance scenarios, in three phases. Phase 1 round-trips a
    /// richly-populated biota (save, then read it back through GetBiota from a genuine cache miss) and
    /// deep-compares every property table against what was written. Phase 2 deletes several such biotas through
    /// the BATCH delete path and proves nothing is left behind in any child table. Phase 3 is the UPDATE
    /// counterpart of phase 1 and gates the batch pre-load. Performance numbers are meaningless if any of them
    /// fails - always run this alongside the performance suite, not instead of it. Phase 1 is the check that
    /// should catch a subtle bug introduced by a future rewrite of ShardDatabase.GetBiota's N+1 property-table
    /// pattern; phase 2 is the check that should catch a batched delete that stops cascading to the child tables.
    ///
    /// EVERY phase here must assert against state that actually reached the database. That is easy to lose by
    /// accident: ShardDatabaseWithCaching serves GetBiota out of the biota cache regardless of the
    /// doNotAddToCache argument, so any phase that saves through a cache-populating path and then reads the same
    /// id back is comparing the staged in-memory object against itself and cannot fail. Phase 1 was written that
    /// way and was tautological until 2026-07-29; phases 2 and 3 avoid it by querying a fresh ShardDbContext
    /// directly (CountRowsFor and CompareAgainstDatabase). Phase 1 now avoids it by keeping its save out of the
    /// cache and asserting that the id really is absent from the cache before it reads.
    /// </summary>
    public class IntegrityCheckScenario : IScenario
    {
        public string Name => "integrity-check";

        public string Description => "Round-trips a richly-populated biota and deep-compares every property table, then proves a batched delete leaves no orphaned child rows. No args.";

        public void Run(ScenarioArgs args)
        {
            RunRoundTripPhase();

            Console.WriteLine();

            RunDeleteCascadePhase();

            Console.WriteLine();

            RunBatchUpdatePhase();
        }

        private static void RunRoundTripPhase()
        {
            var id = SyntheticBiotaFactory.TestGuidRangeStart + 0x3000;
            var original = SyntheticBiotaFactory.CreateRich(id);
            var rwLock = new ReaderWriterLockSlim();

            Console.WriteLine("PHASE 1: save/read round trip.");
            Console.WriteLine($"Saving richly-populated biota 0x{id:X8}...");

            // Start from a known-clean slate so this really is the insert branch even if an earlier run was
            // interrupted before its own cleanup, and so nothing is left in the biota cache under this id.
            var wipeDone = new ManualResetEventSlim();
            DatabaseManager.Shard.RemoveBiota(id, _ => wipeDone.Set());
            wipeDone.Wait();

            // doNotAddToCache on the SAVE is what makes this phase test anything at all, and it is not an
            // optimization. ShardDatabaseWithCaching.GetBiota consults the biota cache and returns a hit BEFORE it
            // looks at its own doNotAddToCache argument - that flag only suppresses ADDING to the cache, it never
            // bypasses it on read. So a cache-populating save followed by a "forced cold read" of the same id is
            // served straight back out of memory, and the comparison below then checks the staged object against
            // itself and passes even if SaveChanges() wrote nothing at all.
            //
            // Measured 2026-07-29: with the previous cache-populating save, corrupting the persisted Name row
            // directly through a ShardDbContext and re-running this read returned the ORIGINAL value and the phase
            // passed. With the save below it returns the corrupted value and the phase fails, which is the whole
            // point of a correctness gate.
            //
            // This is the same trap already documented on CompareAgainstDatabase for phase 3.
            var saveSuccess = DatabaseManager.Shard.BaseDatabase.SaveBiota(original, rwLock, doNotAddToCache: true);

            if (!saveSuccess)
            {
                Console.WriteLine("FAIL: the save itself failed.");
                Metrics.Record("Passed", 0, MetricDirection.HigherIsBetter);
                Metrics.Record("MismatchCount", -1, MetricDirection.Informational);
                return;
            }

            var failures = new List<string>();

            // Self-guard: prove the read below really is a cache miss. If some future change repopulates the
            // cache on this path, this phase must fail loudly rather than quietly go back to comparing staged
            // state against itself - that failure mode is invisible in the output and survived until it was
            // probed for deliberately.
            var cacheKeys = (DatabaseManager.Shard.BaseDatabase as ShardDatabaseWithCaching)?.GetBiotaCacheKeys();

            if (cacheKeys != null && cacheKeys.Contains(id))
            {
                failures.Add(
                    $"0x{id:X8} is in the biota cache before the read-back, so GetBiota would be served from " +
                    "memory and this phase would compare the staged object against itself. The save must not " +
                    "populate the cache.");
            }

            Console.WriteLine("Reading it back through GetBiota, from a genuine cache miss...");

            // GetBiota returns the EF ACE.Database.Models.Shard.Biota (BiotaPropertiesX navigation collections),
            // not the runtime ACE.Entity.Models.Biota that SaveBiota takes - the same type-confusion trap that
            // broke DatabasePerfTest.cs. Convert back to the runtime shape before comparing like for like.
            var roundTrippedEfBiota = DatabaseManager.Shard.BaseDatabase.GetBiota(id, doNotAddToCache: true);
            var roundTripped = roundTrippedEfBiota == null ? null : ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota(roundTrippedEfBiota);

            if (roundTripped == null)
                failures.Add("GetBiota returned null after a successful save.");
            else
            {
                CompareDict(original.PropertiesString, roundTripped.PropertiesString, "PropertiesString", failures);
                CompareDict(original.PropertiesInt, roundTripped.PropertiesInt, "PropertiesInt", failures);
                CompareDict(original.PropertiesInt64, roundTripped.PropertiesInt64, "PropertiesInt64", failures);
                CompareDict(original.PropertiesFloat, roundTripped.PropertiesFloat, "PropertiesFloat", failures);
                CompareDict(original.PropertiesBool, roundTripped.PropertiesBool, "PropertiesBool", failures);
                CompareDict(original.PropertiesDID, roundTripped.PropertiesDID, "PropertiesDID", failures);
                CompareDict(original.PropertiesIID, roundTripped.PropertiesIID, "PropertiesIID", failures);
                ComparePositions(original.PropertiesPosition, roundTripped.PropertiesPosition, failures);
                CompareDict(original.HousePermissions, roundTripped.HousePermissions, "HousePermissions", failures);
                CompareAllegiance(original.PropertiesAllegiance, roundTripped.PropertiesAllegiance, failures);
                CompareEmotes(original.PropertiesEmote, roundTripped.PropertiesEmote, failures);
            }

            if (failures.Count == 0)
            {
                Console.WriteLine("PASS: every property table round-tripped exactly.");
                Metrics.Record("Passed", 1, MetricDirection.HigherIsBetter);
            }
            else
            {
                Console.WriteLine($"FAIL: {failures.Count} mismatch(es):");
                foreach (var failure in failures)
                    Console.WriteLine($"  - {failure}");

                Metrics.Record("Passed", 0, MetricDirection.HigherIsBetter);
            }

            Metrics.Record("MismatchCount", failures.Count, MetricDirection.Informational);

            var remaining = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiota(id, _ => remaining.Signal());
            remaining.Wait();
        }

        /// <summary>
        /// PHASE 2 - the delete counterpart of the round trip above. Saves several richly-populated biotas, deletes
        /// them all through the BATCH delete path, then queries the shard database directly to prove that not one
        /// row survives in biota or in any child table those biotas populated.
        /// This exists because the worst possible outcome of batching deletes is a silent one: a set-based DELETE
        /// that stopped cascading would leave every child table full of orphaned rows while the parent row - the
        /// only thing any normal read path looks at - is gone, so nothing else in this harness would notice.
        /// The pre-delete counts are asserted too. A table that held no rows to begin with cannot demonstrate a
        /// cascade, so an empty "before" is reported as a failure of this check rather than allowed to pass
        /// vacuously.
        /// </summary>
        private static void RunDeleteCascadePhase()
        {
            const int count = 5;

            var baseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x3100;
            var ids = new List<uint>();
            for (var i = 0; i < count; i++)
                ids.Add(baseId + (uint)i);

            Console.WriteLine("PHASE 2: batched delete cascade.");
            Console.WriteLine($"Saving {count} richly-populated biotas 0x{ids[0]:X8}-0x{ids[count - 1]:X8}...");

            var saveRemaining = new CountdownEvent(count);
            var saveFailures = 0;

            foreach (var id in ids)
            {
                var biota = SyntheticBiotaFactory.CreateRich(id);
                var rwLock = new ReaderWriterLockSlim();

                DatabaseManager.Shard.SaveBiota(biota, rwLock, result =>
                {
                    if (!result)
                        Interlocked.Increment(ref saveFailures);

                    saveRemaining.Signal();
                });
            }

            saveRemaining.Wait();

            if (saveFailures > 0)
            {
                Console.WriteLine($"FAIL: {saveFailures} of {count} setup saves failed - cannot test the delete cascade.");
                Metrics.Record("DeleteCascade.Passed", 0, MetricDirection.HigherIsBetter);
                Metrics.Record("DeleteCascade.OrphanCount", -1, MetricDirection.Informational);
                return;
            }

            // Capture the emote row ids while the rows still exist. biota_properties_emote_action has no ObjectId
            // of its own - it hangs off biota_properties_emote.id - so remembering which emote ids to look for
            // afterward is the only way to prove that SECOND cascade hop actually happened.
            List<uint> emoteIds;
            using (var context = new ACE.Database.Models.Shard.ShardDbContext())
                emoteIds = context.BiotaPropertiesEmote.Where(r => ids.Contains(r.ObjectId)).Select(r => r.Id).ToList();

            var before = CountRowsFor(ids, emoteIds);

            Console.WriteLine("Rows present before the delete:");
            foreach (var row in before)
                Console.WriteLine($"  {row.Table,-38} {row.Count}");

            Console.WriteLine($"Deleting all {count} through the batch path (RemoveBiotasInParallel)...");

            var deleteDone = new CountdownEvent(1);
            var deleteSuccess = false;

            DatabaseManager.Shard.RemoveBiotasInParallel(ids, result =>
            {
                deleteSuccess = result;
                deleteDone.Signal();
            }, null);

            deleteDone.Wait();

            var after = CountRowsFor(ids, emoteIds);

            var failures = new List<string>();

            if (!deleteSuccess)
                failures.Add("RemoveBiotasInParallel reported failure.");

            foreach (var row in before)
            {
                if (row.Count == 0)
                    failures.Add($"{row.Table}: no rows existed before the delete, so this table proves nothing - the check is vacuous for it.");
            }

            foreach (var row in after)
            {
                if (row.Count != 0)
                    failures.Add($"{row.Table}: {row.Count} row(s) survived the batched delete (orphaned).");
            }

            if (failures.Count == 0)
            {
                Console.WriteLine($"PASS: all {before.Count} tables held rows before the batched delete and zero after.");
                Metrics.Record("DeleteCascade.Passed", 1, MetricDirection.HigherIsBetter);
            }
            else
            {
                Console.WriteLine($"FAIL: {failures.Count} problem(s):");
                foreach (var failure in failures)
                    Console.WriteLine($"  - {failure}");

                Console.WriteLine("Rows remaining after the delete:");
                foreach (var row in after)
                    Console.WriteLine($"  {row.Table,-38} {row.Count}");

                Metrics.Record("DeleteCascade.Passed", 0, MetricDirection.HigherIsBetter);
            }

            Metrics.Record("DeleteCascade.OrphanCount", failures.Count, MetricDirection.Informational);
        }

        /// <summary>
        /// PHASE 3 - the UPDATE counterpart of phase 1's insert round trip, and the gate on batch pre-loading.
        /// SaveBiotaBatch loads the existing rows for the whole batch in one pass and stages each item against its
        /// pre-loaded entity. Those entities must stay tracked by the batch's context: BiotaUpdater mutates them
        /// and calls context.BiotaPropertiesX.Remove(...) on their children, so if the pre-load were ever switched
        /// to AsNoTracking, SaveChanges() would commit a partial update or none at all, silently and with no error.
        /// Phase 1 cannot catch that, because it only ever inserts a brand new row.
        /// The batch deliberately mixes rows that already exist with rows that do not, so the pre-load's
        /// hit-and-miss handling is exercised in one commit: a miss must still mean "insert".
        /// </summary>
        private static void RunBatchUpdatePhase()
        {
            var baseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x3200;
            var updatedIds = new List<uint> { baseId, baseId + 1 };
            var insertedIds = new List<uint> { baseId + 2, baseId + 3 };
            var allIds = updatedIds.Concat(insertedIds).ToList();

            Console.WriteLine("PHASE 3: batched update round trip.");

            // Start from a known-clean slate, so the "inserted" pair really does exercise the insert branch even
            // if an earlier run was interrupted before its own cleanup.
            var wipeDone = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(allIds, _ => wipeDone.Signal(), null);
            wipeDone.Wait();

            var models = new Dictionary<uint, Biota>();
            var locks = new Dictionary<uint, ReaderWriterLockSlim>();

            foreach (var id in allIds)
            {
                models[id] = SyntheticBiotaFactory.CreateRich(id);
                locks[id] = new ReaderWriterLockSlim();
            }

            Console.WriteLine($"Seeding {updatedIds.Count} biotas for the batch to UPDATE (kept out of the biota cache)...");

            // doNotAddToCache is not an optimization here, it is what makes this phase test anything at all.
            // ShardDatabaseWithCaching.SaveBiotaBatch routes any item that IS in the biota cache to the
            // single-item SaveBiota path, against that item's own retained context - such an item never reaches
            // the batch pre-load. Seeding through the ordinary cache-populating path therefore produced a phase
            // that passed even with the pre-load forced to NoTracking, because the updated rows were cache hits
            // and the only rows left for the batch were brand new inserts, which do not depend on tracking.
            // Keeping both the seed and the update out of the cache is what forces the updates down the
            // pre-loaded path this phase exists to guard.
            var seedDone = new CountdownEvent(1);
            var seedSuccess = false;

            DatabaseManager.Shard.SaveBiotasInParallel(updatedIds.Select(id => (models[id], locks[id])), result =>
            {
                seedSuccess = result;
                seedDone.Signal();
            }, doNotAddToCache: true);

            seedDone.Wait();

            if (!seedSuccess)
            {
                Console.WriteLine("FAIL: the seed saves failed - cannot test the batched update.");
                Metrics.Record("BatchUpdate.Passed", 0, MetricDirection.HigherIsBetter);
                Metrics.Record("BatchUpdate.MismatchCount", -1, MetricDirection.Informational);
                return;
            }

            // Three distinct kinds of change per table, because they fail differently: CHANGE an existing value,
            // ADD a key that was not there, and REMOVE one that was. The removal is the sharpest of the three -
            // it is the one that goes through context.BiotaPropertiesInt.Remove(...) and therefore the one that
            // silently does nothing if the pre-loaded entity is not tracked.
            foreach (var id in updatedIds)
            {
                var model = models[id];

                model.PropertiesString[PropertyString.Name] = $"Updated_{id:X8}";
                model.PropertiesInt[PropertyInt.Value] = 4242;
                model.PropertiesInt[PropertyInt.StackSize] = 7;
                model.PropertiesInt.Remove(PropertyInt.Mass);
                model.PropertiesFloat[PropertyFloat.HealthRate] = 9.75;
                model.PropertiesFloat.Remove(PropertyFloat.HeartbeatTimestamp);
                model.PropertiesPosition[PositionType.Location].ObjCellId = 0x7D640099;
            }

            Console.WriteLine($"Saving all {allIds.Count} in one batch ({updatedIds.Count} updates + {insertedIds.Count} inserts)...");

            var saveDone = new CountdownEvent(1);
            var saveSuccess = false;

            DatabaseManager.Shard.SaveBiotasInParallel(allIds.Select(id => (models[id], locks[id])), result =>
            {
                saveSuccess = result;
                saveDone.Signal();
            }, doNotAddToCache: true);

            saveDone.Wait();

            var failures = new List<string>();

            if (!saveSuccess)
                failures.Add("SaveBiotasInParallel reported failure.");

            foreach (var id in allIds)
            {
                var label = updatedIds.Contains(id) ? "updated" : "inserted";
                CompareAgainstDatabase(id, $"0x{id:X8} ({label})", models[id], failures);
            }

            if (failures.Count == 0)
            {
                Console.WriteLine($"PASS: all {allIds.Count} biotas match on disk after the batched save, including removed keys.");
                Metrics.Record("BatchUpdate.Passed", 1, MetricDirection.HigherIsBetter);
            }
            else
            {
                Console.WriteLine($"FAIL: {failures.Count} mismatch(es):");
                foreach (var failure in failures)
                    Console.WriteLine($"  - {failure}");

                Metrics.Record("BatchUpdate.Passed", 0, MetricDirection.HigherIsBetter);
            }

            Metrics.Record("BatchUpdate.MismatchCount", failures.Count, MetricDirection.Informational);

            var cleanupDone = new CountdownEvent(1);
            DatabaseManager.Shard.RemoveBiotasInParallel(allIds, _ => cleanupDone.Signal(), null);
            cleanupDone.Wait();
        }

        /// <summary>
        /// Compares one biota's expected state against what is actually ON DISK, read straight off a fresh
        /// ShardDbContext.
        /// This does NOT go through ShardDatabase.GetBiota, and that is the entire point. GetBiota's
        /// doNotAddToCache flag only suppresses ADDING to ShardDatabaseWithCaching's biota cache - it does not
        /// bypass the cache on READ, so a biota just written by the batch is served back from memory. Comparing
        /// against that would compare the staged object with itself and pass even if SaveChanges() wrote nothing.
        /// </summary>
        private static void CompareAgainstDatabase(uint id, string label, Biota expected, List<string> failures)
        {
            using (var context = new ACE.Database.Models.Shard.ShardDbContext())
            {
                if (!context.Biota.Any(r => r.Id == id))
                {
                    failures.Add($"{label}: no biota row on disk after the batched save.");
                    return;
                }

                var actualStrings = context.BiotaPropertiesString.Where(r => r.ObjectId == id).ToDictionary(r => (int)r.Type, r => r.Value);
                var actualInts = context.BiotaPropertiesInt.Where(r => r.ObjectId == id).ToDictionary(r => (int)r.Type, r => r.Value);
                var actualFloats = context.BiotaPropertiesFloat.Where(r => r.ObjectId == id).ToDictionary(r => (int)r.Type, r => r.Value);
                var actualPositions = context.BiotaPropertiesPosition.Where(r => r.ObjectId == id).ToDictionary(r => (int)r.PositionType, r => r.ObjCellId);

                CompareDict(ToRaw(expected.PropertiesString), actualStrings, $"{label} PropertiesString", failures);
                CompareDict(ToRaw(expected.PropertiesInt), actualInts, $"{label} PropertiesInt", failures);
                CompareDict(ToRaw(expected.PropertiesFloat), actualFloats, $"{label} PropertiesFloat", failures);

                var expectedPositions = (expected.PropertiesPosition ?? new Dictionary<PositionType, ACE.Entity.Models.PropertiesPosition>())
                    .ToDictionary(kvp => (int)kvp.Key, kvp => kvp.Value.ObjCellId);

                CompareDict(expectedPositions, actualPositions, $"{label} PropertiesPosition ObjCellId", failures);
            }
        }

        /// <summary>
        /// Re-keys a runtime property dictionary from its enum key to the raw numeric key the database stores, so
        /// it can be compared directly against rows read off a ShardDbContext.
        /// </summary>
        private static Dictionary<int, TValue> ToRaw<TKey, TValue>(IDictionary<TKey, TValue> source)
        {
            var result = new Dictionary<int, TValue>();

            if (source == null)
                return result;

            foreach (var kvp in source)
                result[Convert.ToInt32(kvp.Key)] = kvp.Value;

            return result;
        }

        /// <summary>
        /// Counts rows for the given biota ids in biota and in every child table SyntheticBiotaFactory.CreateRich
        /// populates, straight off a fresh ShardDbContext - deliberately not through any ShardDatabase read path,
        /// so a cache or a reassembly bug in that layer cannot mask a row that is really still on disk.
        /// </summary>
        private static List<(string Table, int Count)> CountRowsFor(List<uint> ids, List<uint> emoteIds)
        {
            using (var context = new ACE.Database.Models.Shard.ShardDbContext())
            {
                return new List<(string, int)>
                {
                    ("biota", context.Biota.Count(r => ids.Contains(r.Id))),
                    ("biota_properties_string", context.BiotaPropertiesString.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_int", context.BiotaPropertiesInt.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_int64", context.BiotaPropertiesInt64.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_float", context.BiotaPropertiesFloat.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_bool", context.BiotaPropertiesBool.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_d_i_d", context.BiotaPropertiesDID.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_i_i_d", context.BiotaPropertiesIID.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_position", context.BiotaPropertiesPosition.Count(r => ids.Contains(r.ObjectId))),
                    ("biota_properties_emote", context.BiotaPropertiesEmote.Count(r => ids.Contains(r.ObjectId))),
                    // Keyed by the parent emote row, not by ObjectId - this is the second cascade hop.
                    ("biota_properties_emote_action", emoteIds.Count == 0 ? 0 : context.BiotaPropertiesEmoteAction.Count(r => emoteIds.Contains(r.EmoteId))),
                    // The two non-ObjectId-keyed tables: the biota's own id lands in AllegianceId / HouseId.
                    ("biota_properties_allegiance", context.BiotaPropertiesAllegiance.Count(r => ids.Contains(r.AllegianceId))),
                    ("house_permission", context.HousePermission.Count(r => ids.Contains(r.HouseId))),
                };
            }
        }

        private static void CompareDict<TKey, TValue>(IDictionary<TKey, TValue> expected, IDictionary<TKey, TValue> actual, string label, List<string> failures)
        {
            expected ??= new Dictionary<TKey, TValue>();
            actual ??= new Dictionary<TKey, TValue>();

            foreach (var kvp in expected)
            {
                if (!actual.TryGetValue(kvp.Key, out var actualValue))
                    failures.Add($"{label}: missing key {kvp.Key} (expected {kvp.Value})");
                else if (!Equals(kvp.Value, actualValue))
                    failures.Add($"{label}: {kvp.Key} expected {kvp.Value}, got {actualValue}");
            }

            foreach (var key in actual.Keys)
            {
                if (!expected.ContainsKey(key))
                    failures.Add($"{label}: unexpected extra key {key} = {actual[key]}");
            }
        }

        /// <summary>
        /// PropertiesAllegiance has no Equals override, so the generic CompareDict's Equals() check would always
        /// report a mismatch (reference equality) - compare its two fields explicitly instead. This is keyed by
        /// AllegianceId in the underlying table, one of the two non-ObjectId-keyed property tables PopulateBiotaCollections
        /// has to get right.
        /// </summary>
        private static void CompareAllegiance(IDictionary<uint, PropertiesAllegiance> expected, IDictionary<uint, PropertiesAllegiance> actual, List<string> failures)
        {
            expected ??= new Dictionary<uint, PropertiesAllegiance>();
            actual ??= new Dictionary<uint, PropertiesAllegiance>();

            foreach (var kvp in expected)
            {
                if (!actual.TryGetValue(kvp.Key, out var a))
                {
                    failures.Add($"PropertiesAllegiance: missing key {kvp.Key}");
                    continue;
                }

                if (kvp.Value.Banned != a.Banned || kvp.Value.ApprovedVassal != a.ApprovedVassal)
                    failures.Add($"PropertiesAllegiance: {kvp.Key} expected Banned={kvp.Value.Banned},ApprovedVassal={kvp.Value.ApprovedVassal}, got Banned={a.Banned},ApprovedVassal={a.ApprovedVassal}");
            }

            foreach (var key in actual.Keys)
                if (!expected.ContainsKey(key))
                    failures.Add($"PropertiesAllegiance: unexpected extra key {key}");
        }

        /// <summary>
        /// PropertiesEmote is the nested one-to-many-under-a-one-to-many case (BiotaPropertiesEmote ->
        /// BiotaPropertiesEmoteAction) - the one PopulateBiotaCollections handles with a chunked .Include() rather
        /// than a second independent batch query, so this exercises that specific path.
        /// </summary>
        private static void CompareEmotes(ICollection<PropertiesEmote> expected, ICollection<PropertiesEmote> actual, List<string> failures)
        {
            var expectedList = (expected ?? new List<PropertiesEmote>()).ToList();
            var actualList = (actual ?? new List<PropertiesEmote>()).ToList();

            if (expectedList.Count != actualList.Count)
            {
                failures.Add($"PropertiesEmote: expected {expectedList.Count} entries, got {actualList.Count}");
                return;
            }

            for (var i = 0; i < expectedList.Count; i++)
            {
                var e = expectedList[i];
                var a = actualList[i];

                if (e.Category != a.Category || Math.Abs(e.Probability - a.Probability) > 0.0001f)
                {
                    failures.Add($"PropertiesEmote[{i}]: expected Category={e.Category},Probability={e.Probability}, got Category={a.Category},Probability={a.Probability}");
                    continue;
                }

                if (e.PropertiesEmoteAction.Count != a.PropertiesEmoteAction.Count)
                {
                    failures.Add($"PropertiesEmote[{i}].PropertiesEmoteAction: expected {e.PropertiesEmoteAction.Count} entries, got {a.PropertiesEmoteAction.Count}");
                    continue;
                }

                for (var j = 0; j < e.PropertiesEmoteAction.Count; j++)
                {
                    var ea = e.PropertiesEmoteAction[j];
                    var aa = a.PropertiesEmoteAction[j];

                    if (ea.Type != aa.Type || ea.Message != aa.Message)
                        failures.Add($"PropertiesEmote[{i}].PropertiesEmoteAction[{j}]: expected Type={ea.Type},Message={ea.Message}, got Type={aa.Type},Message={aa.Message}");
                }
            }
        }

        private static void ComparePositions(IDictionary<PositionType, PropertiesPosition> expected, IDictionary<PositionType, PropertiesPosition> actual, List<string> failures)
        {
            expected ??= new Dictionary<PositionType, PropertiesPosition>();
            actual ??= new Dictionary<PositionType, PropertiesPosition>();

            foreach (var kvp in expected)
            {
                if (!actual.TryGetValue(kvp.Key, out var a))
                {
                    failures.Add($"PropertiesPosition: missing key {kvp.Key}");
                    continue;
                }

                var e = kvp.Value;

                if (e.ObjCellId != a.ObjCellId || e.PositionX != a.PositionX || e.PositionY != a.PositionY || e.PositionZ != a.PositionZ ||
                    e.RotationW != a.RotationW || e.RotationX != a.RotationX || e.RotationY != a.RotationY || e.RotationZ != a.RotationZ)
                {
                    failures.Add($"PropertiesPosition: {kvp.Key} mismatch (expected ObjCellId=0x{e.ObjCellId:X8}, got 0x{a.ObjCellId:X8})");
                }
            }

            foreach (var key in actual.Keys)
            {
                if (!expected.ContainsKey(key))
                    failures.Add($"PropertiesPosition: unexpected extra key {key}");
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common;
using ACE.Database.Adapter;
using ACE.Database.Models.World;
using ACE.Database.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database
{
    public class WorldDatabaseWithEntityCache : WorldDatabase
    {
        // WorldDatabase's own logger is private static, so the derived class needs its own.
        private static readonly log4net.ILog cacheLog = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // =====================================
        // Weenie
        // =====================================

        private readonly ConcurrentDictionary<uint /* WCID */, ACE.Entity.Models.Weenie> weenieCache = new ConcurrentDictionary<uint /* WCID */, ACE.Entity.Models.Weenie>();

        private readonly ConcurrentDictionary<string /* Class Name */, uint /* WCID */> weenieClassNameToClassIdCache = new ConcurrentDictionary<string, uint>();

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest<para />
        /// This will also update the weenie cache.
        /// </summary>
        public override Weenie GetWeenie(WorldDbContext context, uint weenieClassId)
        {
            var weenie = base.GetWeenie(context, weenieClassId);

            // If the weenie doesn't exist in the cache, we'll add it.
            if (weenie != null)
            {
                weenieCache[weenieClassId] = WeenieConverter.ConvertToEntityWeenie(weenie);
                weenieClassNameToClassIdCache[weenie.ClassName.ToLower()] = weenie.ClassId;
            }
            else
                weenieCache[weenieClassId] = null;

            return weenie;
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest<para />
        /// This will also update the weenie cache.
        /// </summary>
        public override List<Weenie> GetAllWeenies()
        {
            var weenies = base.GetAllWeenies();

            // Add the weenies to the cache
            foreach (var weenie in weenies)
            {
                weenieCache[weenie.ClassId] = WeenieConverter.ConvertToEntityWeenie(weenie);
                weenieClassNameToClassIdCache[weenie.ClassName.ToLower()] = weenie.ClassId;
            }

            return weenies;
        }


        /// <summary>
        /// This will make sure every weenie in the database has been read and cached.<para />
        /// This function may take 10+ seconds to complete.
        /// </summary>
        public void CacheAllWeenies()
        {
            GetAllWeenies();

            PopulateWeenieSpecificCaches();
        }

        /// <summary>
        /// Returns the number of weenies currently cached.
        /// </summary>
        public int GetWeenieCacheCount()
        {
            return weenieCache.Count(r => r.Value != null);
        }

        public void ClearWeenieCache()
        {
            // Write lock + generation bump: a bulk chunk read before this clear must not publish after it (see
            // PublishChunk). Clears are rare admin operations, so waiting out an in-flight publish costs nothing.
            weenieCachePublishLock.EnterWriteLock();
            try
            {
                System.Threading.Interlocked.Increment(ref weenieCacheGeneration);

                weenieCache.Clear();
                weenieClassNameToClassIdCache.Clear();

                weenieSpecificCachesPopulated = false;
            }
            finally
            {
                weenieCachePublishLock.ExitWriteLock();
            }
        }

        private static long weenieCacheMissTotal;

        [ThreadStatic]
        private static long weenieCacheMissesOnThread;

        /// <summary>
        /// Process-wide count of <see cref="GetCachedWeenie(uint)"/> calls that found nothing cached and fell
        /// through to a synchronous world-database read. Monotonic; read it twice and subtract for an interval.
        /// </summary>
        public static long WeenieCacheMissTotal => System.Threading.Interlocked.Read(ref weenieCacheMissTotal);

        /// <summary>
        /// The same count, but only for misses taken on the CALLING thread. A landblock action runs start to finish
        /// on one thread, so the difference between two reads taken around a piece of synchronous work is exactly
        /// the misses that work caused, with nothing another landblock thread did mixed in. Monotonic per thread.
        /// </summary>
        public static long WeenieCacheMissesOnCurrentThread => weenieCacheMissesOnThread;

        // ------------------------------------------------------------------------------------------------------
        // Miss attribution (Threads clear-time perf, 2026-09-19): WHICH wcids are missing, not just how many.
        // ------------------------------------------------------------------------------------------------------

        /// <summary>The most distinct wcids <see cref="TopMissedWcids"/> tracks. Past it, further NEW wcids are counted in <see cref="MissedWcidOverflow"/> only.</summary>
        public const int MissAttributionCapacity = 4096;

        private static readonly ConcurrentDictionary<uint /* WCID */, int /* misses */> missedWcidCounts = new ConcurrentDictionary<uint, int>();

        private static long missedWcidOverflow;

        private static volatile bool missAttributionEnabled = true;

        /// <summary>
        /// Whether a miss records WHICH wcid missed. On by default; the work is one ConcurrentDictionary update on a
        /// path that is about to run a synchronous multi-table world-DB read, so it is noise next to the miss itself.
        /// </summary>
        public static bool MissAttributionEnabled
        {
            get => missAttributionEnabled;
            set => missAttributionEnabled = value;
        }

        /// <summary>New wcids that missed after the dictionary was full.</summary>
        public static long MissedWcidOverflow => System.Threading.Interlocked.Read(ref missedWcidOverflow);

        /// <summary>Distinct wcids currently tracked.</summary>
        public static int MissedWcidCount => missedWcidCounts.Count;

        /// <summary>
        /// Counts one miss against <paramref name="weenieClassId"/>. Bounded: an unknown wcid past
        /// <see cref="MissAttributionCapacity"/> only bumps <see cref="MissedWcidOverflow"/>, so a pathological
        /// workload cannot grow this without limit. Safe from any number of threads.
        /// </summary>
        public static void RecordMissedWcid(uint weenieClassId)
        {
            if (!missAttributionEnabled)
                return;

            // The Count read races with other threads, so the capacity is a bound within one add, not an exact
            // ceiling; what matters is that it stops the dictionary growing with the workload.
            if (missedWcidCounts.ContainsKey(weenieClassId) || missedWcidCounts.Count < MissAttributionCapacity)
                missedWcidCounts.AddOrUpdate(weenieClassId, 1, (_, count) => count + 1);
            else
                System.Threading.Interlocked.Increment(ref missedWcidOverflow);
        }

        /// <summary>The <paramref name="count"/> most-missed wcids, most misses first, as a snapshot.</summary>
        public static List<(uint Wcid, int Misses)> TopMissedWcids(int count)
        {
            if (count < 1)
                return new List<(uint, int)>();

            return missedWcidCounts.ToArray()
                .Select(kv => (Wcid: kv.Key, Misses: kv.Value))
                .OrderByDescending(m => m.Misses)
                .ThenBy(m => m.Wcid)
                .Take(count)
                .ToList();
        }

        /// <summary>Drops everything <see cref="RecordMissedWcid"/> has collected.</summary>
        public static void ResetMissAttribution()
        {
            missedWcidCounts.Clear();
            System.Threading.Interlocked.Exchange(ref missedWcidOverflow, 0);
        }

        /// <summary>
        /// Weenies will have all their collections populated except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public ACE.Entity.Models.Weenie GetCachedWeenie(uint weenieClassId)
        {
            if (weenieCache.TryGetValue(weenieClassId, out var value))
                return value;

            System.Threading.Interlocked.Increment(ref weenieCacheMissTotal);
            weenieCacheMissesOnThread++;
            RecordMissedWcid(weenieClassId);

            GetWeenie(weenieClassId); // This will add the result into the caches

            weenieCache.TryGetValue(weenieClassId, out value);

            return value;
        }

        /// <summary>
        /// Weenies will have all their collections populated except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public ACE.Entity.Models.Weenie GetCachedWeenie(string weenieClassName)
        {
            if (weenieClassNameToClassIdCache.TryGetValue(weenieClassName.ToLower(), out var value))
                return GetCachedWeenie(value); // This will add the result into the caches

            GetWeenie(weenieClassName); // This will add the result into the caches

            weenieClassNameToClassIdCache.TryGetValue(weenieClassName.ToLower(), out value);

            return GetCachedWeenie(value); // This will add the result into the caches
        }

        public bool ClearCachedWeenie(uint weenieClassId)
        {
            // Same publish-window rule as ClearWeenieCache: /import clears one wcid after changing its rows, and a bulk
            // chunk read before that must not put the old rows back.
            weenieCachePublishLock.EnterWriteLock();
            try
            {
                System.Threading.Interlocked.Increment(ref weenieCacheGeneration);

                return weenieCache.TryRemove(weenieClassId, out _);
            }
            finally
            {
                weenieCachePublishLock.ExitWriteLock();
            }
        }


        // ------------------------------------------------------------------------------------------------------
        // Bulk weenie loading (2026-09-21, PR A): fill the cache for many wcids with one query per table per chunk.
        // ------------------------------------------------------------------------------------------------------

        /// <summary>How many bulk chunks may read the world database at once, across every caller.</summary>
        public const int BulkGateConcurrency = 2;

        /// <summary>
        /// Bounds concurrent bulk reads. Acquired PER CHUNK, never for a whole call, so a long boot warm-up yields
        /// between chunks and a landblock prefetch (gateWait zero: try once, skip if busy) is never stuck behind it.
        /// Internal so a test can hold it.
        /// </summary>
        internal readonly System.Threading.SemaphoreSlim BulkGate = new System.Threading.SemaphoreSlim(BulkGateConcurrency, BulkGateConcurrency);

        /// <summary>
        /// Bumped by both clears, under the publish lock's write side. A chunk captures it before reading and publishes
        /// only if it is unchanged, so rows read before a clear never land after it.
        /// </summary>
        private long weenieCacheGeneration;

        /// <summary>
        /// Closes the window between a chunk's generation check and its TryAdds. Publishes hold the READ side (they do
        /// not block each other or any cache read - GetCachedWeenie never touches this lock); clears hold the WRITE
        /// side. Chosen over a post-publish recheck with instance-exact conditional removes because it has no window
        /// at all, needs no undo path, and costs a clear only the length of an in-flight publish (a few hundred
        /// TryAdds). The lazy per-id path still writes with the indexer outside this lock, exactly as before.
        /// </summary>
        private readonly System.Threading.ReaderWriterLockSlim weenieCachePublishLock = new System.Threading.ReaderWriterLockSlim(System.Threading.LockRecursionPolicy.NoRecursion);

        /// <summary>Test seam: runs on each chunk's freshly read entities, before conversion and the self-check.</summary>
        internal Action<IReadOnlyDictionary<uint, Weenie>> BulkChunkReadHook;

        /// <summary>Test seam: runs after a chunk is read and checked, just before it takes the publish lock.</summary>
        internal Action BulkChunkBeforePublishHook;

        /// <summary>Test seam: runs inside the publish loop, under the read lock, before each wcid's TryAdd.</summary>
        internal Action<uint> BulkChunkPublishHook;

        private static long bulkLoadedTotal;
        private static long bulkGateTimeoutTotal;
        private static long bulkIdsSkippedTotal;
        private static long bulkChunksRejectedTotal;
        private static long bulkChunksStaleTotal;
        private static long bulkChunksFailedTotal;
        private static long bulkChunksPublishFailedTotal;

        /// <summary>Process-wide chunks whose publish threw (the wcids not yet added load lazily). Monotonic.</summary>
        public static long BulkChunksPublishFailedTotal => System.Threading.Interlocked.Read(ref bulkChunksPublishFailedTotal);

        /// <summary>Process-wide weenies published by bulk loads. Monotonic. Separate from, and never added to, the miss counters.</summary>
        public static long BulkLoadedTotal => System.Threading.Interlocked.Read(ref bulkLoadedTotal);

        /// <summary>Process-wide bulk calls that gave up waiting for the gate. Monotonic.</summary>
        public static long BulkGateTimeoutTotal => System.Threading.Interlocked.Read(ref bulkGateTimeoutTotal);

        /// <summary>Process-wide wcids a bulk call skipped because of a gate timeout (they load lazily instead). Monotonic.</summary>
        public static long BulkIdsSkippedTotal => System.Threading.Interlocked.Read(ref bulkIdsSkippedTotal);

        /// <summary>Process-wide chunks rejected by the self-check. Monotonic. Anything above zero is a fidelity bug.</summary>
        public static long BulkChunksRejectedTotal => System.Threading.Interlocked.Read(ref bulkChunksRejectedTotal);

        /// <summary>Process-wide chunks dropped because a cache clear raced them. Monotonic.</summary>
        public static long BulkChunksStaleTotal => System.Threading.Interlocked.Read(ref bulkChunksStaleTotal);

        /// <summary>Process-wide chunks dropped because their read threw. Monotonic.</summary>
        public static long BulkChunksFailedTotal => System.Threading.Interlocked.Read(ref bulkChunksFailedTotal);

        /// <summary>
        /// Loads every listed wcid that is not already cached into the weenie cache, one query per table per chunk,
        /// so the caller's own per-id loop that follows is all cache hits. Never replaces a cached instance (TryAdd
        /// only: live WorldObjects share collections with the cached weenie), caches missing wcids as null exactly as
        /// the per-id path does, and updates the class-name map. The by-type and scroll caches are untouched.
        /// <para />
        /// Per chunk: take the gate (waiting at most <paramref name="gateWait"/>; on a timeout the call stops and the
        /// remaining wcids fall back to the lazy path), re-filter against the cache, read through a fresh NoTracking
        /// context, convert, self-check a sample against legacy per-id reads, and publish only if nothing mismatched
        /// and no clear ran meanwhile. A chunk that throws, mismatches or goes stale publishes NOTHING; its wcids
        /// simply load lazily later. Nothing here ever clears a cache entry.
        /// <para />
        /// A no-op returning Disabled when weenie_bulk_load is off.
        /// </summary>
        /// <param name="gateWait">How long to wait for the gate, per chunk. TimeSpan.Zero for anything on a world
        /// thread; System.Threading.Timeout.InfiniteTimeSpan for a background warm-up that must finish.</param>
        /// <param name="selfCheckSample">Override of weenie_bulk_selfcheck_sample (tests, the benchmark).</param>
        public WeenieBulkLoadResult PrefetchWeenies(IEnumerable<uint> wcids, TimeSpan gateWait, int chunkSize = DefaultWeenieChunkSize, long? selfCheckSample = null)
        {
            if (chunkSize < 1)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = new WeenieBulkLoadResult();

            var distinct = new HashSet<uint>();
            foreach (var wcid in wcids)
            {
                if (wcid != 0)
                    distinct.Add(wcid);
            }

            result.Requested = distinct.Count;

            if (!WeenieBulkLoadSettings.Enabled)
            {
                result.Disabled = true;
                result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                return result;
            }

            var pending = distinct.Where(id => !weenieCache.ContainsKey(id)).ToList();
            result.AlreadyCached = distinct.Count - pending.Count;

            var sample = selfCheckSample ?? WeenieBulkLoadSettings.SelfCheckSample;
            var attempted = 0;

            foreach (var chunk in pending.Chunk(chunkSize))
            {
                if (!BulkGate.Wait(gateWait))
                {
                    result.GateTimeouts++;
                    result.IdsSkipped = pending.Count - attempted;

                    System.Threading.Interlocked.Increment(ref bulkGateTimeoutTotal);
                    System.Threading.Interlocked.Add(ref bulkIdsSkippedTotal, result.IdsSkipped);

                    cacheLog.Info($"[WEENIE BULK] gate busy after {gateWait.TotalMilliseconds:N0} ms: {result.IdsSkipped} of {pending.Count} wcids left to the lazy path");
                    break;
                }

                try
                {
                    LoadAndPublishChunk(chunk, sample, result);
                }
                finally
                {
                    BulkGate.Release();
                }

                attempted += chunk.Length;
            }

            result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private void LoadAndPublishChunk(uint[] chunk, long sample, WeenieBulkLoadResult result)
        {
            // Re-filter now that the gate is held: a lazy miss or another prefetch may have cached some of these.
            var ids = chunk.Where(id => !weenieCache.ContainsKey(id)).ToArray();

            if (ids.Length == 0)
                return;

            result.ChunksAttempted++;

            var generation = System.Threading.Interlocked.Read(ref weenieCacheGeneration);

            Dictionary<uint, ACE.Entity.Models.Weenie> converted;

            try
            {
                using (var context = new WorldDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var entities = GetWeeniesCore(context, ids, ids.Length);

                    BulkChunkReadHook?.Invoke(entities);

                    converted = new Dictionary<uint, ACE.Entity.Models.Weenie>(entities.Count);
                    foreach (var kvp in entities)
                        converted[kvp.Key] = WeenieConverter.ConvertToEntityWeenie(kvp.Value);

                    if (!SelfCheckChunk(context, converted, sample, result))
                    {
                        result.ChunksRejected++;
                        System.Threading.Interlocked.Increment(ref bulkChunksRejectedTotal);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                result.ChunksFailed++;
                System.Threading.Interlocked.Increment(ref bulkChunksFailedTotal);

                cacheLog.Warn($"[WEENIE BULK] chunk of {ids.Length} wcids (first {ids[0]}) failed and was dropped; they load lazily instead: {ex.GetBaseException().Message}");
                return;
            }

            // Contained like the read above: a publish failure costs this chunk only. PrefetchWeenies carries on with
            // the next chunk, and GetCachedWeenies keeps its never-partial contract because the lazy per-id path
            // covers every wcid this chunk did not get into the cache.
            //
            // A failure PART-WAY through the publish leaves the TryAdds already made in place, and that is safe:
            // every instance in `converted` came from a chunk that passed the self-check above, TryAdd never
            // replaces an existing entry, and the generation check ran under the read lock before the first add,
            // so no clear can have landed in between. The cache ends up holding a correct subset of the chunk,
            // the same state as if the rest had simply not been prefetched.
            try
            {
                BulkChunkBeforePublishHook?.Invoke();

                PublishChunk(ids, converted, generation, result);
            }
            catch (Exception ex)
            {
                result.ChunksPublishFailed++;
                System.Threading.Interlocked.Increment(ref bulkChunksPublishFailedTotal);

                cacheLog.Warn($"[WEENIE BULK] publishing a chunk of {ids.Length} wcids (first {ids[0]}) failed; any already added stay (self-checked, never replacing), the rest load lazily: {ex.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// Compares up to <paramref name="sample"/> of the chunk's converted weenies with a legacy per-id read through
        /// the NON-virtual GetWeenieLegacy (a virtual read would write the cache). True when all match. Runs BEFORE
        /// anything is published, so a mismatch costs the chunk, never a cached instance.
        /// </summary>
        private bool SelfCheckChunk(WorldDbContext context, Dictionary<uint, ACE.Entity.Models.Weenie> converted, long sample, WeenieBulkLoadResult result)
        {
            if (sample <= 0 || converted.Count == 0)
                return true;

            IEnumerable<uint> picks = converted.Keys;

            if (sample < converted.Count)
                picks = converted.Keys.OrderBy(_ => Random.Shared.Next()).Take((int)sample);

            foreach (var wcid in picks.ToList())
            {
                var reference = GetWeenieLegacy(context, wcid);
                var referenceConverted = reference == null ? null : WeenieConverter.ConvertToEntityWeenie(reference);

                result.SelfChecked++;

                var difference = WeenieFidelity.FirstDifference(converted[wcid], referenceConverted);

                if (difference != null)
                {
                    result.SelfCheckMismatches++;
                    result.FirstMismatch ??= $"wcid {wcid}: {difference}";

                    cacheLog.Warn($"[WEENIE BULK] self-check MISMATCH, chunk of {converted.Count} not published (its wcids load lazily): wcid {wcid} bulk vs per-id at {difference}");
                    return false;
                }
            }

            return true;
        }

        private void PublishChunk(uint[] ids, Dictionary<uint, ACE.Entity.Models.Weenie> converted, long generation, WeenieBulkLoadResult result)
        {
            weenieCachePublishLock.EnterReadLock();
            try
            {
                if (System.Threading.Interlocked.Read(ref weenieCacheGeneration) != generation)
                {
                    result.ChunksStale++;
                    System.Threading.Interlocked.Increment(ref bulkChunksStaleTotal);
                    return;
                }

                var loaded = 0;

                try
                {
                    foreach (var id in ids)
                    {
                        BulkChunkPublishHook?.Invoke(id);

                        if (converted.TryGetValue(id, out var weenie))
                        {
                            if (weenieCache.TryAdd(id, weenie))
                                loaded++;
                            else
                                result.LostRace++;

                            weenieClassNameToClassIdCache[weenie.ClassName.ToLower()] = id;
                        }
                        else if (weenieCache.TryAdd(id, null))
                            result.NegativelyCached++;
                        else
                            result.LostRace++;
                    }

                    result.ChunksPublished++;
                }
                finally
                {
                    // Counted even when the loop threw part-way, so Loaded and BulkLoadedTotal match what is in the cache.
                    result.Loaded += loaded;
                    System.Threading.Interlocked.Add(ref bulkLoadedTotal, loaded);
                }
            }
            finally
            {
                weenieCachePublishLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns every listed wcid (0 excluded), weenie or null, ALWAYS complete: it prefetches first and then reads
        /// each wcid through <see cref="GetCachedWeenie(uint)"/>, so anything a gate timeout, a failed or rejected
        /// chunk, or weenie_bulk_load=false left uncached is read by the ordinary per-id path. Never partial.
        /// </summary>
        public Dictionary<uint, ACE.Entity.Models.Weenie> GetCachedWeenies(IEnumerable<uint> wcids, TimeSpan gateWait)
        {
            return GetCachedWeenies(wcids, gateWait, out _);
        }

        /// <inheritdoc cref="GetCachedWeenies(IEnumerable{uint}, TimeSpan)"/>
        public Dictionary<uint, ACE.Entity.Models.Weenie> GetCachedWeenies(IEnumerable<uint> wcids, TimeSpan gateWait, out WeenieBulkLoadResult prefetch)
        {
            var ids = wcids.Where(id => id != 0).Distinct().ToList();

            prefetch = PrefetchWeenies(ids, gateWait);

            var result = new Dictionary<uint, ACE.Entity.Models.Weenie>(ids.Count);

            foreach (var id in ids)
                result[id] = GetCachedWeenie(id);

            return result;
        }

        /// <summary>
        /// A reference read: the legacy per-id reader through a fresh NoTracking context, converted, touching NO cache
        /// (it calls the non-virtual GetWeenieLegacy, never the caching override). For self-checks and tests.
        /// </summary>
        public ACE.Entity.Models.Weenie ReadWeenieUncached(uint weenieClassId)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var weenie = GetWeenieLegacy(context, weenieClassId);

                return weenie == null ? null : WeenieConverter.ConvertToEntityWeenie(weenie);
            }
        }

        /// <summary>True when <paramref name="weenieClassId"/> is cached, as a weenie or as a cached miss. Reads nothing.</summary>
        public bool IsWeenieCached(uint weenieClassId) => weenieCache.ContainsKey(weenieClassId);

        /// <summary>
        /// The cached instance for <paramref name="weenieClassId"/> without ever reading the database or counting a
        /// miss; null when absent or cached as missing. For tests and diagnostics.
        /// </summary>
        public ACE.Entity.Models.Weenie PeekCachedWeenie(uint weenieClassId) => weenieCache.TryGetValue(weenieClassId, out var weenie) ? weenie : null;



        private bool weenieSpecificCachesPopulated;

        private readonly ConcurrentDictionary<WeenieType, List<ACE.Entity.Models.Weenie>> weenieCacheByType = new ConcurrentDictionary<WeenieType, List<ACE.Entity.Models.Weenie>>();

        // ConcurrentDictionary, not Dictionary: GetScrollWeenie writes this on a miss, and its callers are the loot
        // factory's scroll path (LootGenerationFactory_Scroll.CreateRandomScroll), which runs on every landblock
        // thread at once. The plain Dictionary it used to be was a real data race, and it is also what kept the
        // Threads loot warm-up out of the scroll path (2026-09-19).
        private readonly ConcurrentDictionary<uint /* Spell ID */, ACE.Entity.Models.Weenie> scrollsBySpellID = new ConcurrentDictionary<uint, ACE.Entity.Models.Weenie>();

        private void PopulateWeenieSpecificCaches()
        {
            // populate weenieCacheByType
            foreach (var weenie in weenieCache.Values)
            {
                if (weenie == null)
                    continue;

                if (!weenieCacheByType.TryGetValue(weenie.WeenieType, out var weenies))
                {
                    weenies = new List<ACE.Entity.Models.Weenie>();
                    weenieCacheByType[weenie.WeenieType] = weenies;
                }

                if (!weenies.Contains(weenie))
                    weenies.Add(weenie);
            }

            // populate scrollsBySpellID
            foreach (var weenie in weenieCache.Values)
            {
                if (weenie == null)
                    continue;

                if (weenie.WeenieType == WeenieType.Scroll)
                {
                    if (weenie.PropertiesDID.TryGetValue(PropertyDataId.Spell, out var value))
                        scrollsBySpellID[value] = weenie;
                }
            }

            weenieSpecificCachesPopulated = true;
        }

        public List<ACE.Entity.Models.Weenie> GetRandomWeeniesOfType(int weenieTypeId, int count)
        {
            if (!weenieCacheByType.TryGetValue((WeenieType)weenieTypeId, out var weenies))
            {
                if (!weenieSpecificCachesPopulated)
                {
                    using (var context = new WorldDbContext())
                    {
                        var results = context.Weenie
                            .AsNoTracking()
                            .Where(r => r.Type == weenieTypeId)
                            .ToList();

                        weenies = new List<ACE.Entity.Models.Weenie>();

                        if (results.Count == 0)
                            return weenies;

                        for (int i = 0; i < count; i++)
                        {
                            var index = ThreadSafeRandom.Next(0, results.Count - 1);

                            var weenie = GetCachedWeenie(results[index].ClassId);

                            weenies.Add(weenie);
                        }

                        return weenies;
                    }
                }

                weenies = new List<ACE.Entity.Models.Weenie>();
                weenieCacheByType[(WeenieType)weenieTypeId] = weenies;
            }

            if (weenies.Count == 0)
                return new List<ACE.Entity.Models.Weenie>();

            {
                var results = new List<ACE.Entity.Models.Weenie>();

                for (int i = 0; i < count; i++)
                {
                    var index = ThreadSafeRandom.Next(0, weenies.Count - 1);

                    var weenie = GetCachedWeenie(weenies[index].WeenieClassId);

                    results.Add(weenie);
                }

                return results;
            }
        }

        private static long scrollWeenieMissTotal;

        [ThreadStatic]
        private static long scrollWeenieMissesOnThread;

        /// <summary>
        /// Process-wide count of <see cref="GetScrollWeenie"/> calls that found nothing cached and ran the scroll
        /// join query. These are invisible to <see cref="WeenieCacheMissTotal"/>, which counts only the wcid cache.
        /// </summary>
        public static long ScrollWeenieMissTotal => System.Threading.Interlocked.Read(ref scrollWeenieMissTotal);

        /// <summary>The same count, on the CALLING thread only (see <see cref="WeenieCacheMissesOnCurrentThread"/>).</summary>
        public static long ScrollWeenieMissesOnCurrentThread => scrollWeenieMissesOnThread;

        public ACE.Entity.Models.Weenie GetScrollWeenie(uint spellID)
        {
            if (!scrollsBySpellID.TryGetValue(spellID, out var weenie))
            {
                if (!weenieSpecificCachesPopulated)
                {
                    System.Threading.Interlocked.Increment(ref scrollWeenieMissTotal);
                    scrollWeenieMissesOnThread++;

                    using (var context = new WorldDbContext())
                    {
                        var query = from weenieRecord in context.Weenie
                                    join did in context.WeeniePropertiesDID on weenieRecord.ClassId equals did.ObjectId
                                    where weenieRecord.Type == (int)WeenieType.Scroll && did.Type == (ushort)PropertyDataId.Spell && did.Value == spellID
                                    select weenieRecord;

                        var result = query.FirstOrDefault();

                        if (result == null) return null;

                        weenie = WeenieConverter.ConvertToEntityWeenie(result);

                        scrollsBySpellID[spellID] = weenie;
                    }
                }
            }

            return weenie;
        }

        private volatile HashSet<uint> rareGemSpellIds;
        private readonly object rareGemSpellIdsLock = new object();

        /// <summary>
        /// Survival challenge (WaffleACE): the set of spell ids cast by rare gems - every Gem-type weenie that
        /// carries a RareId and a Spell DID. Computed once from the world DB and cached.
        /// <para/>
        /// This is the BROAD CANDIDATE set, not a gem-exclusive one. Roughly half the ids it returns are ordinary
        /// player-castable spells (the level-8 Incantation line and the "Aura of Incantation" family), and
        /// ace_world ships Scroll weenies teaching them, so a consumer must not treat a spell-id match as proof a
        /// buff came from a gem. The gem-exclusive half is the Prodigal set, and what separates the two is the
        /// spell's SpellCategory - which comes from the client dat, not from this database, so it cannot be
        /// filtered here. ACE.Server narrows the set by category in ACE.Server.Entity.RareGemSpells; that is what
        /// Player.StripRareGemBuffs consumes.
        /// </summary>
        public HashSet<uint> GetRareGemSpellIds()
        {
            if (rareGemSpellIds != null)
                return rareGemSpellIds;

            lock (rareGemSpellIdsLock)
            {
                if (rareGemSpellIds != null)
                    return rareGemSpellIds;

                using (var context = new WorldDbContext())
                {
                    var query = from spell in context.WeeniePropertiesDID.AsNoTracking()
                                join w in context.Weenie on spell.ObjectId equals w.ClassId
                                join rare in context.WeeniePropertiesInt on spell.ObjectId equals rare.ObjectId
                                where w.Type == (int)WeenieType.Gem
                                   && spell.Type == (ushort)PropertyDataId.Spell
                                   && rare.Type == (ushort)PropertyInt.RareId
                                select spell.Value;

                    rareGemSpellIds = new HashSet<uint>(query.Distinct().ToList());
                }

                return rareGemSpellIds;
            }
        }

        private readonly ConcurrentDictionary<string, uint> creatureWeenieNamesLowerInvariantCache = new ConcurrentDictionary<string, uint>();

        public bool IsCreatureNameInWorldDatabase(string name)
        {
            if (creatureWeenieNamesLowerInvariantCache.TryGetValue(name.ToLowerInvariant(), out _))
                return true;

            using (var context = new WorldDbContext())
            {
                return IsCreatureNameInWorldDatabase(context, name);
            }
        }

        public bool IsCreatureNameInWorldDatabase(WorldDbContext context, string name)
        {
            var query = from weenieRecord in context.Weenie
                        join stringProperty in context.WeeniePropertiesString on weenieRecord.ClassId equals stringProperty.ObjectId
                        where weenieRecord.Type == (int)WeenieType.Creature && stringProperty.Type == (ushort)PropertyString.Name && stringProperty.Value == name
                        select weenieRecord;

            var weenie = query
                .Include(r => r.WeeniePropertiesString)
                .AsNoTracking()
                .FirstOrDefault();

            if (weenie == null)
                return false;

            var weenieName = weenie.GetProperty(PropertyString.Name).ToLowerInvariant();

            creatureWeenieNamesLowerInvariantCache.TryAdd(weenieName, weenie.ClassId);

            return true;
        }


        // =====================================
        // CookBook
        // =====================================

        private readonly Dictionary<uint /* source WCID */, Dictionary<uint /* target WCID */, CookBook>> cookbookCache = new Dictionary<uint, Dictionary<uint, CookBook>>();

        private readonly Dictionary<uint, Recipe> recipeCache = new Dictionary<uint, Recipe>();

        public override CookBook GetCookbook(WorldDbContext context, uint sourceWeenieClassId, uint targetWeenieClassId)
        {
            var cookbook = base.GetCookbook(context, sourceWeenieClassId, targetWeenieClassId);

            lock (cookbookCache)
            {
                // We double check before commiting the recipe.
                // We could be in this lock, and queued up behind us is an attempt to add a result for the same source:target pair.
                if (cookbookCache.TryGetValue(sourceWeenieClassId, out var sourceRecipes))
                {
                    if (!sourceRecipes.ContainsKey(targetWeenieClassId))
                        sourceRecipes.Add(targetWeenieClassId, cookbook);
                }
                else
                    cookbookCache.Add(sourceWeenieClassId, new Dictionary<uint, CookBook>() { { targetWeenieClassId, cookbook } });
            }

            if (cookbook != null)
            {
                // build secondary index for RecipeManager_New caching
                lock (recipeCache)
                {
                    if (!recipeCache.ContainsKey(cookbook.RecipeId))
                        recipeCache.Add(cookbook.RecipeId, cookbook.Recipe);
                }
            }
            return cookbook;
        }

        public override List<CookBook> GetAllCookbooks()
        {
            var cookbooks = base.GetAllCookbooks();

            // Add the cookbooks to the cache
            lock (cookbookCache)
            {
                foreach (var cookbook in cookbooks)
                {
                    // We double check before commiting the recipe.
                    // We could be in this lock, and queued up behind us is an attempt to add a result for the same source:target pair.
                    if (cookbookCache.TryGetValue(cookbook.SourceWCID, out var sourceRecipes))
                    {
                        if (!sourceRecipes.ContainsKey(cookbook.TargetWCID))
                            sourceRecipes.Add(cookbook.TargetWCID, cookbook);
                    }
                    else
                        cookbookCache.Add(cookbook.SourceWCID, new Dictionary<uint, CookBook>() { { cookbook.TargetWCID, cookbook } });
                }
            }

            // build secondary index for RecipeManager_New caching
            lock (recipeCache)
            {
                foreach (var cookbook in cookbooks)
                {
                    if (!recipeCache.ContainsKey(cookbook.RecipeId))
                        recipeCache.Add(cookbook.RecipeId, cookbook.Recipe);
                }
            }

            return cookbooks;
        }


        public void CacheAllCookbooks()
        {
            GetAllCookbooks();
        }

        /// <summary>
        /// Returns the number of Cookbooks currently cached.
        /// </summary>
        public int GetCookbookCacheCount()
        {
            lock (cookbookCache)
                return cookbookCache.Count(r => r.Value != null);
        }

        public void ClearCookbookCache()
        {
            lock (cookbookCache)
                cookbookCache.Clear();

            lock (recipeCache)
                recipeCache.Clear();
        }

        public CookBook GetCachedCookbook(uint sourceWeenieClassId, uint targetWeenieClassId)
        {
            lock (cookbookCache)
            {
                if (cookbookCache.TryGetValue(sourceWeenieClassId, out var recipesForSource))
                {
                    if (recipesForSource.TryGetValue(targetWeenieClassId, out var value))
                        return value;
                }
            }
            return GetCookbook(sourceWeenieClassId, targetWeenieClassId);  // This will add the result into the cache
        }

        public Recipe GetCachedRecipe(uint recipeId)
        {
            lock (recipeCache)
            {
                if (recipeCache.TryGetValue(recipeId, out var recipe))
                    return recipe;
            }
            return GetRecipe(recipeId);  // This will add the result in the cache
        }

        public override Recipe GetRecipe(WorldDbContext context, uint recipeId)
        {
            var recipe = base.GetRecipe(context, recipeId);

            lock (recipeCache)
            {
                if (!recipeCache.ContainsKey(recipeId))
                    recipeCache.Add(recipeId, recipe);
            }
            return recipe;
        }

        // =====================================
        // Encounter
        // =====================================

        private readonly ConcurrentDictionary<ushort /* Landblock */, List<Encounter>> cachedEncounters = new ConcurrentDictionary<ushort, List<Encounter>>();

        /// <summary>
        /// Returns the number of Encounters currently cached.
        /// </summary>
        public int GetEncounterCacheCount()
        {
            return cachedEncounters.Count(r => r.Value != null);
        }

        /// <summary>
        /// Base-world encounters for a landblock. Virtual so tests can stand this layer up
        /// without a database; production callers get the cached query below.
        /// </summary>
        public virtual List<Encounter> GetCachedEncountersByLandblock(ushort landblock)
        {
            if (cachedEncounters.TryGetValue(landblock, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.Encounter
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblock)
                    .ToList();

                cachedEncounters.TryAdd(landblock, results);
                return results;
            }
        }

        /// <summary>
        /// Realms Phase 4: encounters for a landblock as seen from a realm. v1 is suppression
        /// only - a realm either gets the base encounters or none at all - and it follows the
        /// same precedence as the statics path: realm 0 short-circuits, then a per-block rule
        /// row decides in both directions, then the realm's default_strip_encounters, then
        /// inherit. Shipped as an overload so a future per-realm encounter table is a change to
        /// this body rather than to every caller.
        /// <para />
        /// No version stamp is needed here because this method caches nothing of its own: the
        /// decision is recomputed on every call from the rule and default caches.
        /// </summary>
        public List<Encounter> GetCachedEncountersByLandblock(ushort landblock, ushort realmId)
        {
            if (realmId == 0)
                return GetCachedEncountersByLandblock(landblock);

            if (ResolveStripEncounters(GetRealmContentSnapshot(), realmId, landblock))
                return new List<Encounter>();

            return GetCachedEncountersByLandblock(landblock);
        }

        public bool ClearCachedEncountersByLandblock(ushort landblock)
        {
            return cachedEncounters.TryRemove(landblock, out _);
        }

        public bool ClearCachedEvent(string eventName)
        {
            return cachedEvents.TryRemove(eventName.ToLower(), out _);
        }


        // =====================================
        // Event
        // =====================================

        private readonly ConcurrentDictionary<string /* Event Name */, Event> cachedEvents = new ConcurrentDictionary<string, Event>();

        public override List<Event> GetAllEvents(WorldDbContext context)
        {
            var events = base.GetAllEvents(context);

            foreach (var result in events)
                cachedEvents[result.Name.ToLower()] = result;

            return events;
        }

        /// <summary>
        /// Returns the number of Events currently cached.
        /// </summary>
        public int GetEventsCacheCount()
        {
            return cachedEvents.Count(r => r.Value != null);
        }

        public Event GetCachedEvent(string name)
        {
            var nameToLower = name.ToLower();

            if (cachedEvents.TryGetValue(nameToLower, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.Event
                    .AsNoTracking()
                    .FirstOrDefault(r => r.Name.ToLower() == nameToLower);

                cachedEvents[nameToLower] = result;
                return result;
            }
        }


        // =====================================
        // HousePortal
        // =====================================

        private readonly ConcurrentDictionary<uint /* House ID */, List<HousePortal>> cachedHousePortals = new ConcurrentDictionary<uint, List<HousePortal>>();

        /// <summary>
        /// This takes under ? second to complete.
        /// </summary>
        public void CacheAllHousePortals()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.HousePortal
                    .AsNoTracking()
                    .AsEnumerable()
                    .GroupBy(r => r.HouseId);

                foreach (var result in results)
                    cachedHousePortals[result.Key] = result.ToList();
            }
        }

        public List<HousePortal> GetCachedHousePortals(uint houseId)
        {
            if (cachedHousePortals.TryGetValue(houseId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.HousePortal
                    .AsNoTracking()
                    .Where(p => p.HouseId == houseId)
                    .ToList();

                cachedHousePortals[houseId] = results;

                return results;
            }
        }


        private readonly ConcurrentDictionary<uint /* Landblock */, List<HousePortal>> cachedHousePortalsByLandblock = new ConcurrentDictionary<uint, List<HousePortal>>();

        public List<HousePortal> GetCachedHousePortalsByLandblock(uint landblockId)
        {
            if (cachedHousePortalsByLandblock.TryGetValue(landblockId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.HousePortal
                    .AsNoTracking()
                    .Where(p => landblockId == p.ObjCellId >> 16)
                    .ToList();

                cachedHousePortalsByLandblock[landblockId] = results;

                return results;
            }
        }


        // =====================================
        // LandblockInstance
        // =====================================

        private readonly ConcurrentDictionary<ushort /* Landblock */, List<LandblockInstance>> cachedLandblockInstances = new ConcurrentDictionary<ushort, List<LandblockInstance>>();

        /// <summary>
        /// Realms Phase 3: per-(realm, landblock) content. Keyed (realmId &lt;&lt; 16) | landblock.
        /// A cached entry may be the realm's override rows, or the base list when the
        /// realm has no override for that landblock.
        /// <para />
        /// Realms Phase 4: each entry also carries the realmLandblockRulesVersion it was
        /// resolved against. An entry stamped with a superseded version is stale by
        /// definition and is treated as a cache miss - see GetCachedInstancesByLandblock.
        /// </summary>
        private readonly ConcurrentDictionary<uint /* (realmId << 16) | landblock */, (long Version, List<LandblockInstance> Results)> cachedRealmLandblockInstances = new ConcurrentDictionary<uint, (long, List<LandblockInstance>)>();

        /// <summary>
        /// Returns the number of LandblockInstances currently cached.
        /// </summary>
        public int GetLandblockInstancesCacheCount()
        {
            return cachedLandblockInstances.Count(r => r.Value != null);
        }

        /// <summary>
        /// Clears the cached landblock instances for all landblocks
        /// </summary>
        public void ClearCachedLandblockInstances()
        {
            cachedLandblockInstances.Clear();
            cachedRealmLandblockInstances.Clear();
        }

        /// <summary>
        /// Clears the cached landblock instances for a specific landblock
        /// </summary>
        public bool ClearCachedInstancesByLandblock(ushort landblock)
        {
            foreach (var key in cachedRealmLandblockInstances.Keys)
            {
                if ((ushort)(key & 0xFFFF) == landblock)
                    cachedRealmLandblockInstances.TryRemove(key, out _);
            }

            return cachedLandblockInstances.TryRemove(landblock, out _);
        }

        public List<LandblockInstance> GetCachedInstancesByLandblock(WorldDbContext context, ushort landblock)
        {
            if (cachedLandblockInstances.TryGetValue(landblock, out var value))
                return value;

            var results = context.LandblockInstance
                .Include(r => r.LandblockInstanceLink)
                .AsNoTracking()
                .Where(r => r.Landblock == landblock)
                .ToList();

            cachedLandblockInstances.TryAdd(landblock, results.ToList());

            return cachedLandblockInstances[landblock];
        }

        /// <summary>
        /// Returns statics spawn map and their links for the landblock.
        /// Virtual so tests can stand the realm resolution above this layer up without a
        /// database; production callers get the cached query.
        /// </summary>
        public virtual List<LandblockInstance> GetCachedInstancesByLandblock(ushort landblock)
        {
            using (var context = new WorldDbContext())
                return GetCachedInstancesByLandblock(context, landblock);
        }

        /// <summary>
        /// Realms Phase 3/4: content for a landblock as seen from a realm, in this precedence:
        /// 1. realm 0 is the base world and short-circuits before any data lookup at all;
        /// 2. the realm has authored override rows for this landblock -> they replace the base rows;
        /// 3. a realm_landblock_rule row EXISTS for the pair -> its strip_statics decides, in both
        ///    directions: a row with strip_statics = 0 inherits retail on this block even when the
        ///    realm's default is to strip;
        /// 4. otherwise the realm's default_strip_statics;
        /// 5. no realm row at all -> the base (retail) content, unchanged.
        /// <para />
        /// A live /reload-realms may land at any point inside this method, so each entry is
        /// stamped with the strip-configuration version it was resolved against and a superseded
        /// stamp counts as a miss. Without that, a resolver that had already read the old rules
        /// or defaults could insert its answer after the reload cleared the cache, and the
        /// landblock would serve wrongly stripped (or wrongly un-stripped) content until the next
        /// reload or restart. There is deliberately no lock on this path: it runs on every
        /// landblock activation, and locking it would serialize concurrent activation.
        /// </summary>
        public List<LandblockInstance> GetCachedInstancesByLandblock(ushort landblock, ushort realmId)
        {
            if (realmId == 0)
                return GetCachedInstancesByLandblock(landblock);

            var key = ((uint)realmId << 16) | landblock;

            // ONE snapshot reference for the whole resolution. Everything below reads only this
            // object, so a reload landing mid-resolution cannot show us a rule from one
            // generation next to a default from another.
            var snapshot = GetRealmContentSnapshot();

            if (cachedRealmLandblockInstances.TryGetValue(key, out var cached) && cached.Version == snapshot.Version)
                return cached.Results;

            var realmRows = GetRealmInstancesByLandblock(landblock, realmId);

            List<LandblockInstance> results;

            if (realmRows.Count > 0)
                results = realmRows;
            else if (ResolveStripStatics(snapshot, realmId, landblock))
                results = new List<LandblockInstance>();
            else
                results = GetCachedInstancesByLandblock(landblock);

            // indexer rather than GetOrAdd: an entry left behind by an older rule version has
            // to be replaced, not deferred to. If two resolvers race, the loser's stamp is
            // simply older and the next read re-resolves - the cache is self-healing either way
            cachedRealmLandblockInstances[key] = (snapshot.Version, results);

            // what we resolved, not what is in the dictionary: those can differ under a race,
            // and this result is the one consistent with the snapshot we resolved against
            return results;
        }


        // =====================================
        // RealmLandblockRule / realm strip defaults
        // =====================================

        /// <summary>
        /// Realms Phase 4: ONE generation of the realm strip configuration - the per-block rules
        /// and the per-realm defaults together, plus the version they were loaded under.
        /// <para />
        /// Immutable once constructed. Both maps are ordinary Dictionaries rather than concurrent
        /// ones precisely because nothing ever writes to them after publication: a reload builds a
        /// whole new instance instead of mutating this one.
        /// </summary>
        private sealed class RealmContentSnapshot
        {
            /// <summary>Keyed (realmId &lt;&lt; 16) | landblock. A missing key means the block has no rule of its own and the realm default decides.</summary>
            public readonly Dictionary<uint, RealmLandblockRule> Rules;

            /// <summary>Keyed by realm id. A missing key means the realm has no row, which means inherit.</summary>
            public readonly Dictionary<ushort, (bool StripStatics, bool StripEncounters)> Defaults;

            public readonly long Version;

            public RealmContentSnapshot(Dictionary<uint, RealmLandblockRule> rules, Dictionary<ushort, (bool StripStatics, bool StripEncounters)> defaults, long version)
            {
                Rules = rules;
                Defaults = defaults;
                Version = version;
            }
        }

        /// <summary>
        /// The current strip configuration, or null before the first load.
        /// <para />
        /// INVARIANT: this reference is assigned exactly once per generation, after BOTH maps are
        /// fully built, and readers take it ONCE at the top of a resolution and use only that
        /// object. That is what makes the rules and the defaults a single atomic unit. Publishing
        /// the two maps separately - even under this lock - lets a lock-free reader pair new rules
        /// with old defaults, which resolves a landblock to content that is wrong under both
        /// generations and, because realm-aware resolution runs once per landblock activation,
        /// sticks until that landblock unloads. Never reintroduce an incremental publish here.
        /// </summary>
        private volatile RealmContentSnapshot realmContentSnapshot;

        private readonly object realmLandblockRuleLoadLock = new object();

        /// <summary>
        /// Loads (or reloads) every realm landblock rule and every realm's strip defaults in one
        /// pass, publishes them as a single new snapshot, and drops the per-realm instance cache,
        /// whose entries were resolved against the configuration being replaced.
        /// Returns the number of per-block rules cached.
        /// </summary>
        public int CacheAllRealmLandblockRules()
        {
            lock (realmLandblockRuleLoadLock)
                return LoadRealmLandblockRules();
        }

        /// <summary>
        /// Returns the number of realm landblock rules currently cached. Deliberately does NOT
        /// force a load - it exists for the /serverstatus diagnostic, which must not be able to
        /// trigger database work.
        /// </summary>
        public int GetRealmLandblockRuleCacheCount()
        {
            return realmContentSnapshot?.Rules.Count ?? 0;
        }

        /// <summary>
        /// The current snapshot, loading it on first use. The whole (small) configuration is
        /// loaded in one pass rather than per pair, so a rule can never arrive after a
        /// (realm, landblock) pair has already been resolved and cached against its absence -
        /// that would otherwise stick for the life of the process.
        /// </summary>
        private RealmContentSnapshot GetRealmContentSnapshot()
        {
            var snapshot = realmContentSnapshot;

            if (snapshot != null)
                return snapshot;

            lock (realmLandblockRuleLoadLock)
            {
                if (realmContentSnapshot == null)
                    LoadRealmLandblockRules();

                return realmContentSnapshot;
            }
        }

        /// <summary>
        /// The rule for a (realm, landblock) pair, or null when the pair has no rule of its own
        /// and the realm's defaults therefore decide. Realm 0 never has a rule.
        /// </summary>
        public RealmLandblockRule GetRealmLandblockRule(ushort realmId, ushort landblock)
        {
            if (realmId == 0)
                return null;

            GetRealmContentSnapshot().Rules.TryGetValue(((uint)realmId << 16) | landblock, out var rule);

            return rule;
        }

        /// <summary>
        /// A realm's default strip flags, or (false, false) when the realm has no row - which is
        /// "inherit", the behaviour of every realm before these columns existed.
        /// Realm 0 is the base world: it short-circuits here as well, so a stray defaults row on
        /// realm 0 can never affect the base world.
        /// </summary>
        public (bool StripStatics, bool StripEncounters) GetRealmStripDefaults(ushort realmId)
        {
            if (realmId == 0)
                return (false, false);

            GetRealmContentSnapshot().Defaults.TryGetValue(realmId, out var defaults);

            return defaults;
        }

        /// <summary>
        /// The strip decision for one (realm, landblock) pair once authored content has been
        /// ruled out: an existing per-block rule row decides in BOTH directions, and only in its
        /// absence does the realm default apply. Statics and encounters share this shape
        /// deliberately, so the two can never drift apart.
        /// <para />
        /// Takes the snapshot as an argument rather than fetching it, so that one resolution reads
        /// exactly one generation - the caller's - and cannot silently pick up a newer one partway
        /// through.
        /// </summary>
        private static bool ResolveStripStatics(RealmContentSnapshot snapshot, ushort realmId, ushort landblock)
        {
            if (snapshot.Rules.TryGetValue(((uint)realmId << 16) | landblock, out var rule))
                return rule.StripStatics;

            return snapshot.Defaults.TryGetValue(realmId, out var defaults) && defaults.StripStatics;
        }

        /// <summary>
        /// The encounter half of ResolveStripStatics, with identical precedence.
        /// </summary>
        private static bool ResolveStripEncounters(RealmContentSnapshot snapshot, ushort realmId, ushort landblock)
        {
            if (snapshot.Rules.TryGetValue(((uint)realmId << 16) | landblock, out var rule))
                return rule.StripEncounters;

            return snapshot.Defaults.TryGetValue(realmId, out var defaults) && defaults.StripEncounters;
        }

        /// <summary>
        /// Caller must hold realmLandblockRuleLoadLock.
        /// </summary>
        private int LoadRealmLandblockRules()
        {
            // both reads happen before anything is published, so a failure here leaves the
            // previous snapshot in place rather than a half-replaced configuration
            var rules = GetAllRealmLandblockRules();
            var realms = GetAllRealms();

            var ruleMap = new Dictionary<uint, RealmLandblockRule>();

            foreach (var rule in rules)
                ruleMap[((uint)rule.RealmId << 16) | rule.Landblock] = rule;

            var defaultMap = new Dictionary<ushort, (bool StripStatics, bool StripEncounters)>();

            // realm 0 is skipped on the way in as well as on the way out: the base world has no
            // defaults, and a stray row for it must not be able to acquire any
            foreach (var realm in realms)
            {
                if (realm.Id == 0)
                    continue;

                defaultMap[realm.Id] = (realm.DefaultStripStatics, realm.DefaultStripEncounters);
            }

            // THE publish: one reference assignment to a volatile field, after both maps are
            // complete. Everything a reader needs arrives in that single store, so there is no
            // window in which a reader can see half of this generation. See the field's invariant.
            realmContentSnapshot = new RealmContentSnapshot(ruleMap, defaultMap, (realmContentSnapshot?.Version ?? 0) + 1);

            // only to bound growth: staleness itself is handled by the per-entry version stamp,
            // so an entry written after this clear by a resolver already in flight is harmless
            cachedRealmLandblockInstances.Clear();

            return ruleMap.Count;
        }


        private readonly ConcurrentDictionary<ushort /* Landblock */, uint /* House GUID */> cachedBasementHouseGuids = new ConcurrentDictionary<ushort, uint>();

        public uint GetCachedBasementHouseGuid(ushort landblock)
        {
            if (cachedBasementHouseGuids.TryGetValue(landblock, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.LandblockInstance
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblock
                                && r.WeenieClassId != 11730 /* Exclude House Portal */
                                && r.WeenieClassId != 278   /* Exclude Door */
                                && r.WeenieClassId != 568   /* Exclude Door (entry) */
                                && !r.IsLinkChild)
                    .FirstOrDefault();

                if (result == null)
                    return 0;

                cachedBasementHouseGuids[landblock] = result.Guid;

                return result.Guid;
            }
        }


        // =====================================
        // PointsOfInterest
        // =====================================

        private readonly ConcurrentDictionary<string, PointsOfInterest> cachedPointsOfInterest = new ConcurrentDictionary<string, PointsOfInterest>();

        /// <summary>
        /// Retrieves all points of interest from the database and adds/updates the points of interest cache entries with every point of interest retrieved.
        /// 57 entries cached in 00:00:00.0057937
        /// </summary>
        public void CacheAllPointsOfInterest()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.PointsOfInterest
                    .AsNoTracking();

                foreach (var result in results)
                    cachedPointsOfInterest[result.Name.ToLower()] = result;
            }
        }

        /// <summary>
        /// Returns the number of PointsOfInterest currently cached.
        /// </summary>
        public int GetPointsOfInterestCacheCount()
        {
            return cachedPointsOfInterest.Count(r => r.Value != null);
        }

        /// <summary>
        /// Returns the PointsOfInterest cache.
        /// </summary>
        public ConcurrentDictionary<string, PointsOfInterest> GetPointsOfInterestCache()
        {
            return new ConcurrentDictionary<string, PointsOfInterest>(cachedPointsOfInterest);
        }

        public PointsOfInterest GetCachedPointOfInterest(string name)
        {
            var nameToLower = name.ToLower();

            if (cachedPointsOfInterest.TryGetValue(nameToLower, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.PointsOfInterest
                    .AsNoTracking()
                    .FirstOrDefault(r => r.Name.ToLower() == nameToLower);

                cachedPointsOfInterest[nameToLower] = result;
                return result;
            }
        }


        // =====================================
        // Quest
        // =====================================

        private readonly ConcurrentDictionary<string, Quest> cachedQuest = new ConcurrentDictionary<string, Quest>();

        public bool ClearCachedQuest(string questName)
        {
            return cachedQuest.TryRemove(questName, out _);
        }

        /// <summary>
        /// Inserts or replaces a quest in the in-memory cache without touching the database.
        /// Intended for tests and tools that need a deterministic world quest definition
        /// (MaxSolves / MinDelta) with no live world database. Mirrors <see cref="ClearCachedQuest"/>.
        /// </summary>
        public void AddCachedQuest(Quest quest)
        {
            cachedQuest[quest.Name] = quest;
        }

        public Quest GetCachedQuest(string questName)
        {
            if (cachedQuest.TryGetValue(questName, out var quest))
                return quest;

            using (var context = new WorldDbContext())
            {
                quest = context.Quest.FirstOrDefault(q => q.Name.Equals(questName));
                cachedQuest[questName] = quest;

                return quest;
            }
        }


        // =====================================
        // Recipe
        // =====================================


        // =====================================
        // Spell
        // =====================================

        private readonly ConcurrentDictionary<uint /* Spell ID */, Spell> spellCache = new ConcurrentDictionary<uint, Spell>();

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public void CacheAllSpells()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.Spell
                    .AsNoTracking();

                foreach (var result in results)
                    spellCache[result.Id] = result;
            }
        }

        /// <summary>
        /// Returns the number of Spells currently cached.
        /// </summary>
        public int GetSpellCacheCount()
        {
            return spellCache.Count(r => r.Value != null);
        }

        public void ClearSpellCache()
        {
            spellCache.Clear();
        }

        public Spell GetCachedSpell(uint spellId)
        {
            if (spellCache.TryGetValue(spellId, out var spell))
                return spell;

            using (var context = new WorldDbContext())
            {
                var result = context.Spell
                    .AsNoTracking()
                    .FirstOrDefault(r => r.Id == spellId);

                spellCache[spellId] = result;
                return result;
            }
        }



        // =====================================
        // TreasureDeath
        // =====================================

        private readonly ConcurrentDictionary<uint /* Data ID */, TreasureDeath> cachedDeathTreasure = new ConcurrentDictionary<uint, TreasureDeath>();

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public void CacheAllTreasuresDeath()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.TreasureDeath
                    .AsNoTracking();

                foreach (var result in results)
                    cachedDeathTreasure[result.TreasureType] = result;
            }
        }

        /// <summary>
        /// Returns the number of TreasureDeath currently cached.
        /// </summary>
        public int GetDeathTreasureCacheCount()
        {
            return cachedDeathTreasure.Count(r => r.Value != null);
        }

        public TreasureDeath GetCachedDeathTreasure(uint dataId)
        {
            if (cachedDeathTreasure.TryGetValue(dataId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.TreasureDeath
                    .AsNoTracking()
                    .FirstOrDefault(r => r.TreasureType == dataId);

                cachedDeathTreasure[dataId] = result;
                return result;
            }
        }


        // =====================================
        // TreasureMaterial
        // =====================================

        // The Key is the Material Code (derived from PropertyInt.TsysMaterialData)
        // The Value is a list of all
        private Dictionary<int /* Material Code */, Dictionary<int /* Tier */, List<TreasureMaterialBase>>> cachedTreasureMaterialBase;
        
        public void CacheAllTreasureMaterialBase()
        {
            using (var context = new WorldDbContext())
            {
                var table = new Dictionary<int, Dictionary<int, List<TreasureMaterialBase>>>();

                var results = context.TreasureMaterialBase.Where(i => i.Probability > 0).ToList();

                foreach (var result in results)
                {
                    if (!table.TryGetValue((int)result.MaterialCode, out var materialCode))
                    {
                        materialCode = new Dictionary<int, List<TreasureMaterialBase>>();
                        table.Add((int)result.MaterialCode, materialCode);
                    }
                    if (!materialCode.TryGetValue((int)result.Tier, out var chances))
                    {
                        chances = new List<TreasureMaterialBase>();
                        materialCode.Add((int)result.Tier, chances);
                    }
                    chances.Add(result.Clone());
                }
                TreasureMaterialBase_Normalize(table);

                cachedTreasureMaterialBase = table;
            }
        }

        private const float NormalizeEpsilon = 0.00001f;

        private void TreasureMaterialBase_Normalize(Dictionary<int, Dictionary<int, List<TreasureMaterialBase>>> materialBase)
        {
            foreach (var kvp in materialBase)
            {
                var materialCode = kvp.Key;
                var tiers = kvp.Value;

                foreach (var kvp2 in tiers)
                {
                    var tier = kvp2.Key;
                    var list = kvp2.Value;

                    var totalProbability = list.Sum(i => i.Probability);

                    if (Math.Abs(1.0f - totalProbability) < NormalizeEpsilon)
                        continue;

                    //Console.WriteLine($"TotalProbability {totalProbability} found for TreasureMaterialBase {materialCode} tier {tier}");

                    var factor = 1.0f / totalProbability;

                    foreach (var item in list)
                        item.Probability *= factor;

                    /*totalProbability = list.Sum(i => i.Probability);

                    Console.WriteLine($"After: {totalProbability}");*/
                }
            }
        }

        public List<TreasureMaterialBase> GetCachedTreasureMaterialBase(int materialCode, int tier)
        {
            if (cachedTreasureMaterialBase == null)
                CacheAllTreasureMaterialBase();

            if (cachedTreasureMaterialBase.TryGetValue(materialCode, out var tiers) && tiers.TryGetValue(tier, out var treasureMaterialBase))
                return treasureMaterialBase;
            else
                return null;
        }


        private Dictionary<int /* Material ID */, Dictionary<int /* Color Code */, List<TreasureMaterialColor>>> cachedTreasureMaterialColor;
        
        public void CacheAllTreasureMaterialColor()
        {
            using (var context = new WorldDbContext())
            {
                var table = new Dictionary<int, Dictionary<int, List<TreasureMaterialColor>>>();

                var results = context.TreasureMaterialColor.ToList();

                foreach (var result in results)
                {
                    if (!table.TryGetValue((int)result.MaterialId, out var colorCodes))
                    {
                        colorCodes = new Dictionary<int, List<TreasureMaterialColor>>();
                        table.Add((int)result.MaterialId, colorCodes);
                    }
                    if (!colorCodes.TryGetValue((int)result.ColorCode, out var list))
                    {
                        list = new List<TreasureMaterialColor>();
                        colorCodes.Add((int)result.ColorCode, list);
                    }
                    list.Add(result.Clone());
                }

                TreasureMaterialColor_Normalize(table);

                cachedTreasureMaterialColor = table;
            }
        }

        private void TreasureMaterialColor_Normalize(Dictionary<int, Dictionary<int, List<TreasureMaterialColor>>> materialColor)
        {
            foreach (var kvp in materialColor)
            {
                var material = kvp.Key;
                var colorCodes = kvp.Value;

                foreach (var kvp2 in colorCodes)
                {
                    var colorCode = kvp2.Key;
                    var list = kvp2.Value;

                    var totalProbability = list.Sum(i => i.Probability);

                    if (Math.Abs(1.0f - totalProbability) < NormalizeEpsilon)
                        continue;

                    //Console.WriteLine($"TotalProbability {totalProbability} found for TreasureMaterialColor {(MaterialType)material} ColorCode {colorCode}");

                    var factor = 1.0f / totalProbability;

                    foreach (var item in list)
                        item.Probability *= factor;

                    /*totalProbability = list.Sum(i => i.Probability);

                    Console.WriteLine($"After: {totalProbability}");*/
                }
            }
        }

        public List<TreasureMaterialColor> GetCachedTreasureMaterialColors(int materialId, int tsysColorCode)
        {
            if (cachedTreasureMaterialColor == null)
                CacheAllTreasureMaterialColor();

            if (cachedTreasureMaterialColor.TryGetValue(materialId, out var colorCodes) && colorCodes.TryGetValue(tsysColorCode, out var result))
                return result;
            else
                return null;
        }


        // The Key is the Material Group (technically a MaterialId, but more generic...e.g. "Material.Metal", "Material.Cloth", etc.)
        // The Value is a list of all
        private Dictionary<int /* Material Group */, Dictionary<int /* Tier */, List<TreasureMaterialGroups>>> cachedTreasureMaterialGroups;

        public void CacheAllTreasureMaterialGroups()
        {
            using (var context = new WorldDbContext())
            {
                var table = new Dictionary<int, Dictionary<int, List<TreasureMaterialGroups>>>();

                var results = context.TreasureMaterialGroups.ToList();

                foreach (var result in results)
                {
                    if (!table.TryGetValue((int)result.MaterialGroup, out var tiers))
                    {
                        tiers = new Dictionary<int, List<TreasureMaterialGroups>>();
                        table.Add((int)result.MaterialGroup, tiers);
                    }
                    if (!tiers.TryGetValue((int)result.Tier, out var list))
                    {
                        list = new List<TreasureMaterialGroups>();
                        tiers.Add((int)result.Tier, list);
                    }
                    list.Add(result.Clone());
                }
                TreasureMaterialGroups_Normalize(table);

                cachedTreasureMaterialGroups = table;
            }
        }

        private void TreasureMaterialGroups_Normalize(Dictionary<int, Dictionary<int, List<TreasureMaterialGroups>>> materialGroups)
        {
            foreach (var kvp in materialGroups)
            {
                var materialGroup = kvp.Key;
                var tiers = kvp.Value;

                foreach (var kvp2 in tiers)
                {
                    var tier = kvp2.Key;
                    var list = kvp2.Value;

                    var totalProbability = list.Sum(i => i.Probability);

                    if (Math.Abs(1.0f - totalProbability) < NormalizeEpsilon)
                        continue;

                    //Console.WriteLine($"TotalProbability {totalProbability} found for TreasureMaterialGroup {(MaterialType)materialGroup} tier {tier}");

                    var factor = 1.0f / totalProbability;

                    foreach (var item in list)
                        item.Probability *= factor;

                    /*totalProbability = list.Sum(i => i.Probability);

                    Console.WriteLine($"After: {totalProbability}");*/
                }
            }
        }

        public List<TreasureMaterialGroups> GetCachedTreasureMaterialGroup(int materialGroup, int tier)
        {
            if (cachedTreasureMaterialGroups == null)
                CacheAllTreasureMaterialGroups();

            if (cachedTreasureMaterialGroups.TryGetValue(materialGroup, out var tiers) && tiers.TryGetValue(tier, out var treasureMaterialGroup))
                return treasureMaterialGroup;
            else
                return null;
        }


        // =====================================
        // TreasureWielded
        // =====================================

        private readonly ConcurrentDictionary<uint /* Data ID */, List<TreasureWielded>> cachedWieldedTreasure = new ConcurrentDictionary<uint, List<TreasureWielded>>();

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public void CacheAllTreasureWielded()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.TreasureWielded
                    .AsNoTracking()
                    .AsEnumerable()
                    .GroupBy(r => r.TreasureType);

                foreach (var result in results)
                    cachedWieldedTreasure[result.Key] = result.ToList();
            }
        }

        /// <summary>
        /// Returns the number of TreasureWielded currently cached.
        /// </summary>
        public int GetWieldedTreasureCacheCount()
        {
            return cachedWieldedTreasure.Count(r => r.Value != null);
        }


        public List<TreasureWielded> GetCachedWieldedTreasure(uint dataId)
        {
            if (cachedWieldedTreasure.TryGetValue(dataId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.TreasureWielded
                    .AsNoTracking()
                    .Where(r => r.TreasureType == dataId)
                    .ToList();

                cachedWieldedTreasure[dataId] = results;
                return results;
            }
        }

        public void ClearWieldedTreasureCache()
        {
            cachedWieldedTreasure.Clear();
        }


        // =====================================
        // SkyDecorRegion (WaffleACE fork)
        // =====================================

        /// <summary>
        /// Every ENABLED sky decor region, loaded in one pass the first time a landblock asks and held
        /// until <see cref="ClearSkyDecorRegionCache"/>. The whole table is a handful of rows and every
        /// outdoor landblock activation reads all of them (it has to test each for coverage), so there is
        /// no per-landblock key worth having. The stored reference is swapped atomically, never mutated,
        /// so a reader that got the old list keeps a consistent snapshot while a reload rebuilds it.
        /// </summary>
        private volatile List<SkyDecorRegion> cachedSkyDecorRegions;

        private readonly object skyDecorRegionLock = new object();

        /// <summary>
        /// All enabled sky decor regions. Never null; an empty list when the table is empty, and also
        /// when the table does not exist yet (a server running against a world DB that predates the
        /// 2026-08-20 migration must load landblocks normally, not throw on every one of them).
        /// </summary>
        public List<SkyDecorRegion> GetCachedSkyDecorRegions()
        {
            var cached = cachedSkyDecorRegions;

            if (cached != null)
                return cached;

            lock (skyDecorRegionLock)
            {
                // another thread may have populated it while this one waited
                cached = cachedSkyDecorRegions;

                if (cached != null)
                    return cached;

                List<SkyDecorRegion> results;

                try
                {
                    using (var context = new WorldDbContext())
                    {
                        results = context.SkyDecorRegion
                            .AsNoTracking()
                            .Where(r => r.Enabled)
                            .OrderBy(r => r.Id)
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    cacheLog.Warn($"GetCachedSkyDecorRegions: could not read sky_decor_region - no sky decor will spawn. Apply Database/Updates/World/2026-08-20-01-Add-Sky-Decor-Region.sql if this world database predates it.", ex);

                    results = new List<SkyDecorRegion>();
                }

                cachedSkyDecorRegions = results;

                return results;
            }
        }

        /// <summary>
        /// Returns the number of SkyDecorRegions currently cached (-1 = not loaded yet).
        /// </summary>
        public int GetSkyDecorRegionCacheCount()
        {
            var cached = cachedSkyDecorRegions;

            return cached?.Count ?? -1;
        }

        /// <summary>
        /// Drops the region cache. The next landblock activation (or /sky-decor reload) re-reads the table.
        /// </summary>
        public void ClearSkyDecorRegionCache()
        {
            lock (skyDecorRegionLock)
                cachedSkyDecorRegions = null;
        }
    }
}

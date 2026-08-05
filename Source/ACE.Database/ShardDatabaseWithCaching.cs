using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common;
using ACE.Database.Models.Shard;
using ACE.Entity;

namespace ACE.Database
{
    public class ShardDatabaseWithCaching : ShardDatabase
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public TimeSpan PlayerBiotaRetentionTime { get; set; }
        public TimeSpan NonPlayerBiotaRetentionTime { get; set; }

        public ShardDatabaseWithCaching(TimeSpan playerBiotaRetentionTime, TimeSpan nonPlayerBiotaRetentionTime)
        {
            PlayerBiotaRetentionTime = playerBiotaRetentionTime;
            NonPlayerBiotaRetentionTime = nonPlayerBiotaRetentionTime;
        }


        private class CacheObject<T>
        {
            public DateTime LastSeen;
            public ShardDbContext Context;
            public T CachedObject;
        }

        private readonly object biotaCacheMutex = new object();

        private readonly Dictionary<uint, CacheObject<Biota>> biotaCache = new Dictionary<uint, CacheObject<Biota>>();

        private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);

        private DateTime lastMaintenanceInterval;

        /// <summary>
        /// Make sure this is called from within a lock(biotaCacheMutex)
        /// </summary>
        private void TryPerformMaintenance()
        {
            if (lastMaintenanceInterval + MaintenanceInterval > DateTime.UtcNow)
                return;

            var removals = new Collection<uint>();

            foreach (var kvp in biotaCache)
            {
                if (ObjectGuid.IsPlayer(kvp.Key))
                {
                    if (kvp.Value.LastSeen + PlayerBiotaRetentionTime < DateTime.UtcNow)
                        removals.Add(kvp.Key);
                }
                else
                {
                    if (kvp.Value.LastSeen + NonPlayerBiotaRetentionTime < DateTime.UtcNow)
                        removals.Add(kvp.Key);
                }
            }

            foreach (var removal in removals)
                biotaCache.Remove(removal);

            lastMaintenanceInterval = DateTime.UtcNow;
        }

        private void TryAddToCache(ShardDbContext context, Biota biota)
        {
            lock (biotaCacheMutex)
            {
                if (ObjectGuid.IsPlayer(biota.Id))
                {
                    if (PlayerBiotaRetentionTime > TimeSpan.Zero)
                        biotaCache[biota.Id] = new CacheObject<Biota> {LastSeen = DateTime.UtcNow, Context = context, CachedObject = biota};
                }
                else if (NonPlayerBiotaRetentionTime > TimeSpan.Zero)
                    biotaCache[biota.Id] = new CacheObject<Biota> {LastSeen = DateTime.UtcNow, Context = context, CachedObject = biota};
            }
        }

        public List<uint> GetBiotaCacheKeys()
        {
            lock (biotaCacheMutex)
                return biotaCache.Keys.ToList();
        }


        public override Biota GetBiota(ShardDbContext context, uint id, bool doNotAddToCache = false)
        {
            lock (biotaCacheMutex)
            {
                TryPerformMaintenance();

                if (biotaCache.TryGetValue(id, out var cachedBiota))
                {
                    cachedBiota.LastSeen = DateTime.UtcNow;

                    return cachedBiota.CachedObject;
                }
            }

            var biota = GetBiotaCore(context, id);

            if (biota != null && !doNotAddToCache)
                TryAddToCache(context, biota);

            return biota;
        }

        public override Biota GetBiota(uint id, bool doNotAddToCache = false)
        {
            if (ObjectGuid.IsPlayer(id))
            {
                if (PlayerBiotaRetentionTime > TimeSpan.Zero)
                {
                    var context = new ShardDbContext();

                    var biota = GetBiota(context, id, doNotAddToCache); // This will add the result into the caches

                    return biota;
                }
            }
            else if (NonPlayerBiotaRetentionTime > TimeSpan.Zero)
            {
                var context = new ShardDbContext();

                var biota = GetBiota(context, id, doNotAddToCache); // This will add the result into the caches

                return biota;
            }

            return base.GetBiota(id, doNotAddToCache);
        }

        public override bool SaveBiota(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, bool doNotAddToCache = false)
        {
            CacheObject<Biota> cachedBiota;

            lock (biotaCacheMutex)
                biotaCache.TryGetValue(biota.Id, out cachedBiota);

            if (cachedBiota != null)
            {
                cachedBiota.LastSeen = DateTime.UtcNow;

                // A cached entry's context is NOT necessarily private to that one biota. SaveBiotaBatch's
                // non-cached path commits many items on ONE shared context and then caches every one of them
                // against that same context, so up to MaxSaveBiotaBatchSize cache entries can point at a single
                // ShardDbContext. EF Core contexts are not thread-safe, so the concurrent cache-hit pass in
                // SaveBiotaBatch has to serialize per context, and the context instance is the only thing every
                // entry sharing it agrees on. Entries whose context genuinely is private - everything cached by
                // this miss path below, and everything cached by GetBiota - never contend on this lock, so the
                // single-threaded callers pay one uncontended lock and nothing else.
                lock (cachedBiota.Context)
                {
                    rwLock.EnterReadLock();
                    try
                    {
                        ACE.Database.Adapter.BiotaUpdater.UpdateDatabaseBiota(cachedBiota.Context, biota, cachedBiota.CachedObject);
                    }
                    finally
                    {
                        rwLock.ExitReadLock();
                    }

                    return DoSaveBiota(cachedBiota.Context, cachedBiota.CachedObject);
                }
            }

            // Biota does not exist in the cache

            var context = new ShardDbContext();

            var existingBiota = StageBiota(context, biota, rwLock);

            if (DoSaveBiota(context, existingBiota))
            {
                if (!doNotAddToCache)
                    TryAddToCache(context, existingBiota);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Cache hits keep their own live, retained per-object context and go through the unchanged single-item
        /// SaveBiota - they're not folded into the shared batch context below, because that would leave a second
        /// tracked copy of the same row on a different context. Only cache misses are staged together and
        /// committed once.
        ///
        /// The cache-hit subset is committed CONCURRENTLY over the database thread pool. That fan-out used to
        /// exist in SaveBiotasInParallel; routing that method through here turned it into a serial loop, so an
        /// all-warm save of N items paid N round trips one after another.
        ///
        /// The fan-out is only worth anything because this method no longer caches what it commits. It used to
        /// call TryAddToCache once per committed item against the ONE context they all shared, so a batch of N
        /// left N cache entries pointing at a single ShardDbContext. That made three separate problems: saving
        /// any one of those entries ran SaveChanges() on a context tracking up to 99 other biotas, flushing
        /// whatever was pending on them; the context plus every tracked graph stayed pinned for the whole
        /// retention window; and concurrent saves over such a set are simply illegal, since EF Core contexts are
        /// not thread-safe. The last one is not theoretical - fanning out before removing the shared cache-add
        /// produced "A second operation was started on this context instance" and returned false for the losers.
        ///
        /// Not caching a batch-committed item follows the precedent the bulk loaders already set: they too
        /// deliberately skip populating the biota cache as a side effect. The cache is still populated by
        /// GetBiota and by the single-item SaveBiota miss path, both of which give an entry a genuinely private
        /// context. SaveBiota's cache-hit branch keeps a per-context lock anyway, as a guard rather than a
        /// requirement: with this method no longer sharing contexts into the cache, no path currently creates a
        /// shared entry, and the lock is what makes a future one merely slow instead of silently corrupt.
        /// </summary>
        public override List<bool> SaveBiotaBatch(IList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> items, bool doNotAddToCache = false)
        {
            var results = new bool[items.Count];
            var cachedIndices = new List<int>();
            var nonCachedIndices = new List<int>();

            // ONE lock for the whole partition pass rather than one acquisition per item. The loop body is a plain
            // dictionary membership test with no database call inside it - the same shape CountCachedBiotas in this
            // class already uses - and taking it once keeps every save, serial or parallel, strictly outside the
            // lock instead of interleaved with N acquire/release pairs.
            // The partition is a routing hint, never a correctness decision: SaveBiota re-checks the cache under
            // this same lock, so an entry evicted between the probe and its save just takes the miss path there.
            lock (biotaCacheMutex)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (biotaCache.ContainsKey(items[i].biota.Id))
                        cachedIndices.Add(i);
                    else
                        nonCachedIndices.Add(i);
                }
            }

            SaveBatchStats.RecordBatch(items.Count, cachedIndices.Count, nonCachedIndices.Count);

            if (cachedIndices.Count > 0)
            {
                // Writes land on distinct, pre-allocated indices of results, so it stays index-aligned with items.
                // A concurrent collection would lose that alignment, and distinct-element writes into an array are
                // safe without one.
                Parallel.ForEach(cachedIndices, ConfigManager.Config.Server.Threading.DatabaseParallelOptions, i =>
                {
                    results[i] = SaveBiota(items[i].biota, items[i].rwLock, doNotAddToCache);
                });
            }

            if (nonCachedIndices.Count > 0)
            {
                // Disposed deterministically, unlike before: nothing outlives this call now that committed items
                // are not put into the biota cache, so no cache entry can still be holding this context.
                bool committed;

                using (var context = new ShardDbContext())
                {
                    // One pre-load pass for the whole non-cached subset, as in ShardDatabase.SaveBiotaBatch. Cached
                    // items are excluded on purpose and not merely as an optimization: they were already written
                    // above through their own retained context, and pulling them into THIS context would leave a
                    // second tracked copy of the same row on a different context.
                    var existingBiotas = GetBiotasCore(context, nonCachedIndices.Select(i => items[i].biota.Id).ToList());

                    foreach (var index in nonCachedIndices)
                    {
                        var item = items[index];

                        existingBiotas.TryGetValue(item.biota.Id, out var existingBiota);

                        SetBiotaPopulatedCollections(StageBiota(context, item.biota, item.rwLock, existingBiota));
                    }

                    committed = CommitContext(context, $"SaveBiotaBatch of {nonCachedIndices.Count} non-cached item(s)");
                }

                if (committed)
                {
                    // Deliberately NOT added to the biota cache - see the remarks on this method. doNotAddToCache
                    // is still honoured on both paths that can cache, which are the fan-out above and the
                    // per-item fallback below.
                    foreach (var index in nonCachedIndices)
                        results[index] = true;
                }
                else
                {
                    log.Warn($"[DATABASE] SaveBiotaBatch of {nonCachedIndices.Count} non-cached item(s) failed twice as a batch; falling back to per-item saves.");

                    foreach (var index in nonCachedIndices)
                    {
                        var item = items[index];
                        results[index] = SaveBiota(item.biota, item.rwLock, doNotAddToCache);
                    }
                }
            }

            return results.ToList();
        }

        /// <summary>
        /// Observability only. Takes biotaCacheMutex for a plain membership loop with no database call inside,
        /// which is the same lock SaveBiota already takes per item - so this adds N dictionary lookups, not a
        /// new contention surface. Never influences what gets written.
        /// </summary>
        protected override int CountCachedBiotas(IList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> items)
        {
            var count = 0;

            lock (biotaCacheMutex)
            {
                foreach (var item in items)
                {
                    if (biotaCache.ContainsKey(item.biota.Id))
                        count++;
                }
            }

            return count;
        }

        public override bool RemoveBiota(uint id)
        {
            lock (biotaCacheMutex)
                biotaCache.Remove(id);

            return base.RemoveBiota(id);
        }

        /// <summary>
        /// Same shape as the single-item override above: evict first, then delegate. The mutex is taken once for
        /// the whole batch rather than per id, and is deliberately released before the database call - holding it
        /// across the delete would block every concurrent cache lookup for the duration of the transaction.
        /// </summary>
        public override List<bool> RemoveBiotaBatch(IList<uint> ids)
        {
            lock (biotaCacheMutex)
            {
                foreach (var id in ids)
                    biotaCache.Remove(id);
            }

            return base.RemoveBiotaBatch(ids);
        }
    }
}

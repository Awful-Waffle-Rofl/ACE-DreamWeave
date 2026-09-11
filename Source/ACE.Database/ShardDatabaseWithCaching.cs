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

            /// <summary>
            /// Set by DiscardCachedBiota when a save on this entry failed. Read and written only while holding
            /// lock (Context), so a thread that was already waiting on that lock with a reference to this
            /// entry can see that the entry is gone and must not touch the context.
            /// </summary>
            public bool Discarded;
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

        /// <summary>
        /// Returns whether the cache actually TOOK the context, which is not the same question as whether it
        /// was asked to: a zero retention time for this biota's kind stores nothing. Callers that own the
        /// context have to know which happened, because the cache is the only thing that would otherwise keep
        /// it alive - a context neither cached nor disposed is a leaked MySQL connection.
        /// </summary>
        private bool TryAddToCache(ShardDbContext context, Biota biota)
        {
            lock (biotaCacheMutex)
            {
                if (ObjectGuid.IsPlayer(biota.Id))
                {
                    if (PlayerBiotaRetentionTime > TimeSpan.Zero)
                    {
                        biotaCache[biota.Id] = new CacheObject<Biota> {LastSeen = DateTime.UtcNow, Context = context, CachedObject = biota};

                        return true;
                    }
                }
                else if (NonPlayerBiotaRetentionTime > TimeSpan.Zero)
                {
                    biotaCache[biota.Id] = new CacheObject<Biota> {LastSeen = DateTime.UtcNow, Context = context, CachedObject = biota};

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The context SaveBiota's cache-miss path stages into. Exists as an overridable seam so a test can
        /// hand in a context it is able to inspect afterwards, which is the only way to assert the disposal
        /// contract below without a live MySQL instance. Production behaviour is a plain new ShardDbContext().
        /// </summary>
        protected virtual ShardDbContext CreateShardDbContext()
        {
            return new ShardDbContext();
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

                    // ownership of the context transfers to the cache ONLY via TryAddToCache,
                    // which doNotAddToCache suppresses. With the flag set nothing retains it, on
                    // a cache hit or a miss, so this method still owns it and must dispose it.
                    // Safe for the returned biota: no lazy-loading proxies are configured on any
                    // context, and GetBiotaCore has already materialized every child collection
                    // through PopulateBiotaCollections before this returns.
                    if (doNotAddToCache)
                        context.Dispose();

                    return biota;
                }
            }
            else if (NonPlayerBiotaRetentionTime > TimeSpan.Zero)
            {
                var context = new ShardDbContext();

                var biota = GetBiota(context, id, doNotAddToCache); // This will add the result into the caches

                // same ownership rule as the player branch above
                if (doNotAddToCache)
                    context.Dispose();

                return biota;
            }

            return base.GetBiota(id, doNotAddToCache);
        }

        /// <summary>
        /// Drops a cache entry whose save did not commit, and disposes its context once nothing else in the
        /// cache is pointing at it. Must be called while holding lock(cachedBiota.Context).
        ///
        /// A failed SaveChanges() does NOT undo anything in the change tracker: every row UpdateDatabaseBiota
        /// staged stays Added / Modified / Deleted on that context. A cache entry keeps its context for the
        /// whole retention window, so leaving a failed entry in the cache means the NEXT save of the same biota
        /// runs UpdateDatabaseBiota against a graph that still contains rows in the Added state - and removing
        /// an Added dependent detaches it, which EF Core's navigation fixup immediately reflects by taking it
        /// out of the principal's navigation collection. That is what turned one duplicate-entry failure into
        /// a run of "Collection was modified; enumeration operation may not execute." on 2026-09-07/08 in prod,
        /// each one another disconnected player. BiotaUpdater no longer enumerates a collection it removes from,
        /// so the crash is gone either way, but a dirty retained context is independently wrong: it would
        /// replay the failed writes on top of some later, unrelated save of the same biota.
        ///
        /// Dropping the entry costs one biota read - the next save takes the miss path and re-stages against a
        /// context built from the current database row.
        ///
        /// The entry is only removed if it is STILL the one that failed; GetBiota or the miss path may have
        /// replaced it in the meantime, and a replacement is clean. The Discarded flag is what makes disposal
        /// safe: a thread that read this same entry out of the cache before we removed it is blocked on
        /// lock(Context) and will see the flag rather than a disposed context.
        ///
        /// Lock order: this is the only place that takes biotaCacheMutex while holding a cached context's
        /// monitor. Nothing takes them in the other order - SaveBiota releases biotaCacheMutex before locking
        /// the context - so there is no inversion.
        /// </summary>
        private void DiscardCachedBiota(uint id, CacheObject<Biota> cachedBiota)
        {
            cachedBiota.Discarded = true;

            bool contextStillCached;

            lock (biotaCacheMutex)
            {
                if (biotaCache.TryGetValue(id, out var current) && ReferenceEquals(current, cachedBiota))
                    biotaCache.Remove(id);

                contextStillCached = biotaCache.Values.Any(v => ReferenceEquals(v.Context, cachedBiota.Context));
            }

            log.Warn($"[DATABASE] Dropping the cached biota entry for 0x{id:X8} because its save did not commit; its ShardDbContext still holds the failed changes and must not be reused.");

            if (!contextStillCached)
                cachedBiota.Context.Dispose();
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
                    // Another thread saving this same id may have discarded the entry while we waited on the
                    // lock; its context can already be disposed, so fall through to the miss path.
                    if (!cachedBiota.Discarded)
                    {
                        var saved = false;

                        try
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

                            saved = DoSaveBiota(cachedBiota.Context, cachedBiota.CachedObject);

                            return saved;
                        }
                        finally
                        {
                            // Covers a false return AND a throw out of either call above.
                            if (!saved)
                                DiscardCachedBiota(biota.Id, cachedBiota);
                        }
                    }
                }
            }

            // Biota does not exist in the cache, or the entry we found was just discarded by a failed save

            // This context is disposed on EVERY exit except the one where the cache took ownership of it, and
            // that exception is why there is no using statement here: a retained context has to outlive this
            // method by the whole retention window. Every other exit - a false return, a throw out of
            // StageBiota or DoSaveBiota, doNotAddToCache, or a zero retention time for this kind of biota -
            // leaves nothing holding the context, so failing to dispose it leaks one pooled MySQL connection.
            //
            // That leak used to be nearly unreachable for a warm biota, because a failed cache-hit save left
            // its dirty entry in the cache and the next attempt reused that same context. DiscardCachedBiota
            // now drops the entry instead, so every retry of a persistently-failing biota arrives HERE. A
            // player is at least bounded by BiotaSaveFailed disconnecting them (Player_Tick), but a creature,
            // corpse or container has no such circuit breaker and would leak one connection per autosave
            // heartbeat until the pool ran dry.
            var context = CreateShardDbContext();
            var contextRetainedByCache = false;

            try
            {
                var existingBiota = StageBiota(context, biota, rwLock);

                if (DoSaveBiota(context, existingBiota))
                {
                    if (!doNotAddToCache)
                        contextRetainedByCache = TryAddToCache(context, existingBiota);

                    return true;
                }

                return false;
            }
            finally
            {
                if (!contextRetainedByCache)
                    context.Dispose();
            }
        }

        /// <summary>
        /// Cache hits keep their own live, retained per-object context and go through the single-item SaveBiota -
        /// they're not folded into the shared batch context below, because that would leave a second tracked copy
        /// of the same row on a different context. Only cache misses are staged together and committed once.
        /// Note that the fanned-out branch therefore inherits SaveBiota's failure handling too: a cache-hit save
        /// that does not commit discards its cache entry (DiscardCachedBiota), so the next attempt for that id
        /// re-stages on a clean context rather than reusing the dirty one.
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

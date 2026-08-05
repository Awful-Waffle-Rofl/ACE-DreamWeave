using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Database.Entity;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;

namespace ACE.Database
{
    public class SerializedShardDatabase
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This is the base database that SerializedShardDatabase is a wrapper for.
        /// </summary>
        public readonly ShardDatabase BaseDatabase;

        // Owned by ShardDatabase, since SaveBiotasInParallel chunks by the same cap without going through this
        // queue at all. Aliased here so the drain loop below reads the same as it always did.
        private const int MaxSaveBiotaBatchSize = ShardDatabase.MaxSaveBiotaBatchSize;

        /// <summary>
        /// Removes share the save cap rather than getting an independently-tuned number. The cap is not about how
        /// much work a batch can do cheaply - it is about bounding how long one batch may monopolize the single
        /// worker thread and delay whatever is queued behind it, and that budget is the same whichever kind of work
        /// is holding the thread. A delete does strictly less work per item than a save, so reusing 100 here is if
        /// anything conservative. Kept as a separate named constant so the two can be tuned apart later without
        /// hunting down which call sites meant which.
        /// </summary>
        private const int MaxRemoveBiotaBatchSize = MaxSaveBiotaBatchSize;

        /// <summary>
        /// A pending SaveBiota call, queued in place of a plain Task so the worker thread can recognize it as
        /// batchable and opportunistically merge it with other pending SaveBiota calls into one SaveChanges().
        /// </summary>
        private sealed class SaveBiotaQueueItem
        {
            public uint BiotaId;
            public ACE.Entity.Models.Biota Biota;
            public ReaderWriterLockSlim RwLock;
            public DateTime InitialCallTime;
            public Action<bool> Callback;
            public Action<TimeSpan, TimeSpan> PerformanceResults;
        }

        /// <summary>
        /// The delete-side counterpart of SaveBiotaQueueItem: a pending RemoveBiota call, queued so the worker
        /// thread can merge consecutive removes into one transaction of set-based DELETEs. A mass vendor sale
        /// enqueues one of these per sold item.
        /// </summary>
        private sealed class RemoveBiotaQueueItem
        {
            public uint BiotaId;
            public DateTime InitialCallTime;
            public Action<bool> Callback;
            public Action<TimeSpan, TimeSpan> PerformanceResults;
        }

        /// <summary>
        /// Everything queued is either a plain Task (unchanged behavior, run standalone), a SaveBiotaQueueItem, or
        /// a RemoveBiotaQueueItem (both batchable, but only with their own kind). Keeping this as one queue/one
        /// worker thread preserves every existing FIFO ordering guarantee - only how the worker executes runs of
        /// same-kind items changes, not their order.
        /// </summary>
        private readonly struct QueueEntry
        {
            public readonly Task Task;
            public readonly SaveBiotaQueueItem SaveBiotaItem;
            public readonly RemoveBiotaQueueItem RemoveBiotaItem;

            public QueueEntry(Task task)
            {
                Task = task;
                SaveBiotaItem = null;
                RemoveBiotaItem = null;
            }

            public QueueEntry(SaveBiotaQueueItem saveBiotaItem)
            {
                Task = null;
                SaveBiotaItem = saveBiotaItem;
                RemoveBiotaItem = null;
            }

            public QueueEntry(RemoveBiotaQueueItem removeBiotaItem)
            {
                Task = null;
                SaveBiotaItem = null;
                RemoveBiotaItem = removeBiotaItem;
            }

            public bool IsBatchableSaveBiota => SaveBiotaItem != null;

            public bool IsBatchableRemoveBiota => RemoveBiotaItem != null;
        }

        private readonly BlockingCollection<QueueEntry> _queue = new BlockingCollection<QueueEntry>();

        private Thread _workerThread;

        internal SerializedShardDatabase(ShardDatabase shardDatabase)
        {
            BaseDatabase = shardDatabase;
        }

        public void Start()
        {
            _workerThread = new Thread(DoWork);
            _workerThread.Name = "Serialized Shard Database";
            _workerThread.Start();
        }

        public void Stop()
        {
            _queue.CompleteAdding();
            _workerThread.Join();
        }

        private void DoWork()
        {
            while (!_queue.IsAddingCompleted)
            {
                try
                {
                    var first = _queue.Take();

                    // ORDERING INVARIANT - the reason this whole class exists is that one queue drained by one
                    // thread guarantees strict FIFO across every shard operation, and batching must not weaken
                    // that. A batch may only ever absorb CONSECUTIVE entries of the SAME kind. The first entry of
                    // any other kind terminates the drain, is held over, and runs immediately after the batch -
                    // it is never pulled ahead of, or pushed behind, work that was enqueued before it.
                    // The case that makes this non-negotiable is a SaveBiota for id X followed by a RemoveBiota
                    // for X: that is exactly what a vendor sale enqueues. If the remove were ever reordered ahead
                    // of the save, the delete would run against a row the save then re-inserts, and a sold item
                    // would resurrect itself in the database.
                    QueueEntry? heldOver = null;

                    if (first.IsBatchableSaveBiota)
                    {
                        var batch = new List<SaveBiotaQueueItem> { first.SaveBiotaItem };
                        var idsInBatch = new HashSet<uint> { first.SaveBiotaItem.BiotaId };

                        // Drain any other SaveBiota items already sitting in the queue, non-blocking, so they can be
                        // committed together in one SaveChanges() instead of one round trip each. Stop at the first
                        // non-batchable item, or the first same-id duplicate (two independent events can enqueue two
                        // saves for the same object microseconds apart - staging both into one context would throw
                        // on the second Add/duplicate-tracked-entity before any callback fires, silently failing the
                        // whole batch) - either way, that item is held over and run immediately after this batch,
                        // preserving exact FIFO order.
                        while (batch.Count < MaxSaveBiotaBatchSize && _queue.TryTake(out var next))
                        {
                            if (next.IsBatchableSaveBiota && idsInBatch.Add(next.SaveBiotaItem.BiotaId))
                            {
                                batch.Add(next.SaveBiotaItem);
                            }
                            else
                            {
                                heldOver = next;
                                break;
                            }
                        }

                        RunSaveBiotaBatch(batch);
                    }
                    else if (first.IsBatchableRemoveBiota)
                    {
                        var batch = new List<RemoveBiotaQueueItem> { first.RemoveBiotaItem };

                        // Same drain as saves, minus the same-id guard: a delete stages nothing into a context, so
                        // an id named twice in one DELETE ... IN (...) is harmless and idempotent. Note this means
                        // only a non-remove entry can terminate this drain, which is precisely the ordering rule -
                        // a save for an id already in this batch stops it and runs after, never inside it.
                        while (batch.Count < MaxRemoveBiotaBatchSize && _queue.TryTake(out var next))
                        {
                            if (next.IsBatchableRemoveBiota)
                            {
                                batch.Add(next.RemoveBiotaItem);
                            }
                            else
                            {
                                heldOver = next;
                                break;
                            }
                        }

                        RunRemoveBiotaBatch(batch);
                    }
                    else
                    {
                        RunStandalone(first.Task);
                    }

                    if (heldOver.HasValue)
                        RunHeldOver(heldOver.Value);
                }
                catch (ObjectDisposedException)
                {
                    // the _queue has been disposed, we're good
                    break;
                }
                catch (InvalidOperationException)
                {
                    // _queue is empty and CompleteForAdding has been called -- we're done here
                    break;
                }
            }
        }

        /// <summary>
        /// Runs the single entry a batch drain had to stop at, on its own, immediately after that batch - which is
        /// what keeps FIFO exact, since the entry was already taken off the queue. It deliberately does NOT start a
        /// drain of its own: anything enqueued after it is still in the queue and gets picked up, in order, by the
        /// next iteration of the worker loop.
        /// </summary>
        private void RunHeldOver(QueueEntry entry)
        {
            if (entry.IsBatchableSaveBiota)
                RunSaveBiotaBatch(new List<SaveBiotaQueueItem> { entry.SaveBiotaItem });
            else if (entry.IsBatchableRemoveBiota)
                RunRemoveBiotaBatch(new List<RemoveBiotaQueueItem> { entry.RemoveBiotaItem });
            else
                RunStandalone(entry.Task);
        }

        private void RunStandalone(Task t)
        {
            try
            {
                t.Start();
                t.Wait();
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] DoWork task failed with exception: {ex}");
                // perhaps add failure callbacks?
                // swallow for now.  can't block other db work because 1 fails.
            }
        }

        private void RunSaveBiotaBatch(List<SaveBiotaQueueItem> batch)
        {
            SaveBatchStats.RecordDrainBatch(batch.Count);

            try
            {
                if (batch.Count == 1)
                {
                    // Skip SaveBiotaBatch for the very common light-load case - it would otherwise pay a second
                    // full retry-once cycle on top of SaveBiota's own, for no batching benefit at size 1.
                    var item = batch[0];
                    var executeStart = DateTime.UtcNow;

                    var result = BaseDatabase.SaveBiota(item.Biota, item.RwLock);

                    var executeEnd = DateTime.UtcNow;

                    item.Callback?.Invoke(result);
                    item.PerformanceResults?.Invoke(executeStart - item.InitialCallTime, executeEnd - executeStart);
                }
                else
                {
                    var executeStart = DateTime.UtcNow;

                    var results = BaseDatabase.SaveBiotaBatch(batch.Select(b => (b.Biota, b.RwLock)).ToList());

                    var executeEnd = DateTime.UtcNow;

                    for (var i = 0; i < batch.Count; i++)
                    {
                        batch[i].Callback?.Invoke(results[i]);
                        batch[i].PerformanceResults?.Invoke(executeStart - batch[i].InitialCallTime, executeEnd - executeStart);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] DoWork batch of {batch.Count} SaveBiota item(s) failed with exception: {ex}");
            }
        }

        private void RunRemoveBiotaBatch(List<RemoveBiotaQueueItem> batch)
        {
            try
            {
                if (batch.Count == 1)
                {
                    // Skip RemoveBiotaBatch at size 1 for the same reason the save path does - the batch wrapper
                    // would otherwise pay a second full retry-once cycle on top of RemoveBiota's own, for no
                    // batching benefit. RemoveBiota is virtual, so the caching subclass still evicts.
                    var item = batch[0];
                    var executeStart = DateTime.UtcNow;

                    var result = BaseDatabase.RemoveBiota(item.BiotaId);

                    var executeEnd = DateTime.UtcNow;

                    item.Callback?.Invoke(result);
                    item.PerformanceResults?.Invoke(executeStart - item.InitialCallTime, executeEnd - executeStart);
                }
                else
                {
                    var executeStart = DateTime.UtcNow;

                    var results = BaseDatabase.RemoveBiotaBatch(batch.Select(b => b.BiotaId).ToList());

                    var executeEnd = DateTime.UtcNow;

                    for (var i = 0; i < batch.Count; i++)
                    {
                        batch[i].Callback?.Invoke(results[i]);
                        batch[i].PerformanceResults?.Invoke(executeStart - batch[i].InitialCallTime, executeEnd - executeStart);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] DoWork batch of {batch.Count} RemoveBiota item(s) failed with exception: {ex}");
            }
        }


        public int QueueCount => _queue.Count;

        public void GetCurrentQueueWaitTime(Action<TimeSpan> callback)
        {
            var initialCallTime = DateTime.UtcNow;

            _queue.Add(new QueueEntry(new Task(() =>
            {
                callback?.Invoke(DateTime.UtcNow - initialCallTime);
            })));
        }


        /// <summary>
        /// Will return uint.MaxValue if no records were found within the range provided.
        /// </summary>
        public void GetMaxGuidFoundInRange(uint min, uint max, Action<uint> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetMaxGuidFoundInRange(min, max);
                callback?.Invoke(result);
            })));
        }

        /// <summary>
        /// This will return available id's, in the form of sequence gaps starting from min.<para />
        /// If a gap is just 1 value wide, then both start and end will be the same number.
        /// </summary>
        public void GetSequenceGaps(uint min, uint limitAvailableIDsReturned, Action<List<(uint start, uint end)>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetSequenceGaps(min, limitAvailableIDsReturned);
                callback?.Invoke(result);
            })));
        }


        public void SaveBiota(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new SaveBiotaQueueItem
            {
                BiotaId = biota.Id,
                Biota = biota,
                RwLock = rwLock,
                InitialCallTime = DateTime.UtcNow,
                Callback = callback
            }));
        }

        public void SaveBiota(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, Action<bool> callback, Action<TimeSpan, TimeSpan> performanceResults)
        {
            _queue.Add(new QueueEntry(new SaveBiotaQueueItem
            {
                BiotaId = biota.Id,
                Biota = biota,
                RwLock = rwLock,
                InitialCallTime = DateTime.UtcNow,
                Callback = callback,
                PerformanceResults = performanceResults
            }));
        }


        public void SaveBiotasInParallel(IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> biotas, Action<bool> callback, bool doNotAddToCache = false)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SaveBiotasInParallel(biotas, doNotAddToCache);
                callback?.Invoke(result);
            })));
        }

        public void SaveBiotasInParallel(IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> biotas, Action<bool> callback, Action<TimeSpan, TimeSpan> performanceResults, bool doNotAddToCache = false)
        {
            var initialCallTime = DateTime.UtcNow;

            _queue.Add(new QueueEntry(new Task(() =>
            {
                var taskStartTime = DateTime.UtcNow;
                var result = BaseDatabase.SaveBiotasInParallel(biotas, doNotAddToCache);
                var taskCompletedTime = DateTime.UtcNow;
                callback?.Invoke(result);
                performanceResults?.Invoke(taskStartTime - initialCallTime, taskCompletedTime - taskStartTime);
            })));
        }

        public void RemoveBiota(uint id, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new RemoveBiotaQueueItem
            {
                BiotaId = id,
                InitialCallTime = DateTime.UtcNow,
                Callback = callback
            }));
        }

        public void RemoveBiota(uint id, Action<bool> callback, Action<TimeSpan, TimeSpan> performanceResults)
        {
            _queue.Add(new QueueEntry(new RemoveBiotaQueueItem
            {
                BiotaId = id,
                InitialCallTime = DateTime.UtcNow,
                Callback = callback,
                PerformanceResults = performanceResults
            }));
        }

        /// <summary>
        /// One aggregate operation with one callback, so it stays a plain queued Task rather than becoming N
        /// batchable entries - but the work it does underneath is now BaseDatabase.RemoveBiotaBatch (one
        /// transaction of set-based DELETEs), not a Parallel.ForEach over single-row deletes.
        /// </summary>
        public void RemoveBiotasInParallel(IEnumerable<uint> ids, Action<bool> callback, Action<TimeSpan, TimeSpan> performanceResults)
        {
            var initialCallTime = DateTime.UtcNow;

            _queue.Add(new QueueEntry(new Task(() =>
            {
                var taskStartTime = DateTime.UtcNow;
                var result = BaseDatabase.RemoveBiotasInParallel(ids);
                var taskCompletedTime = DateTime.UtcNow;
                callback?.Invoke(result);
                performanceResults?.Invoke(taskStartTime - initialCallTime, taskCompletedTime - taskStartTime);
            })));
        }


        public void GetPossessedBiotasInParallel(uint id, Action<PossessedBiotas> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var c = BaseDatabase.GetPossessedBiotasInParallel(id);
                callback?.Invoke(c);
            })));
        }

        public void GetInventoryInParallel(uint parentId, bool includedNestedItems, Action<List<Biota>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var c = BaseDatabase.GetInventoryInParallel(parentId, includedNestedItems);
                callback?.Invoke(c);
            })));

        }


        public void IsCharacterNameAvailable(string name, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.IsCharacterNameAvailable(name);
                callback?.Invoke(result);
            })));
        }

        public void GetCharacters(uint accountId, bool includeDeleted, Action<List<Character>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetCharacters(accountId, includeDeleted);
                callback?.Invoke(result);
            })));
        }

        public void GetCharacter(uint characterId, Action<Character> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetCharacter(characterId);
                callback?.Invoke(result);
            })));
        }

        public void SaveCharacter(Character character, ReaderWriterLockSlim rwLock, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SaveCharacter(character, rwLock);
                callback?.Invoke(result);
            })));
        }

        public void RenameCharacter(Character character, string newName, ReaderWriterLockSlim rwLock, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.RenameCharacter(character, newName, rwLock);
                callback?.Invoke(result);
            })));
        }

        public void SetCharacterAccessLevelByName(string name, AccessLevel accessLevel, Action<uint> callback)
        {
            // TODO
            throw new NotImplementedException();
        }


        public void AddCharacterInParallel(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim biotaLock, IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> possessions, Character character, ReaderWriterLockSlim characterLock, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.AddCharacterInParallel(biota, biotaLock, possessions, character, characterLock);
                callback?.Invoke(result);
            })));
        }
    }
}

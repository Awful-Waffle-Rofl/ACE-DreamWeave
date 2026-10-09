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

        /// <summary>Guards Stop against a second call. See Stop.</summary>
        private int _stopped;

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

        /// <summary>
        /// One-shot: the first caller stops the worker, every later caller is a no-op.
        ///
        /// Stop is now reachable twice on the same exit. The shutdown paths in ServerManager call it
        /// explicitly, and Environment.Exit then re-enters Program.OnProcessExit, which calls
        /// DatabaseManager.Stop() again. A second CompleteAdding + Join is believed harmless, but that
        /// is unguarded framework behaviour rather than a documented guarantee, and the second call now
        /// lands inside process exit where a throw has nowhere useful to go. Make it a no-op by
        /// construction instead of relying on the belief.
        /// </summary>
        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _stopped, 1, 0) != 0)
                return;

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

        /// <summary>
        /// Invokes one queued item's callback, and never lets it escape.
        ///
        /// A callback belongs to somebody else's code, and one that throws must not take the rest of a
        /// batch's callbacks with it - which is exactly what an unwrapped invocation inside the loops
        /// below did, because the enclosing catch answered nobody at all.
        /// </summary>
        private static void Answer(Action<bool> callback, bool result)
        {
            if (callback == null)
                return;

            try
            {
                callback(result);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] a queued database callback threw while being told {result}: {ex}");
            }
        }

        private static void Report(Action<TimeSpan, TimeSpan> performanceResults, TimeSpan queued, TimeSpan executed)
        {
            if (performanceResults == null)
                return;

            try
            {
                performanceResults(queued, executed);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] a queued database performance callback threw: {ex}");
            }
        }

        private void RunSaveBiotaBatch(List<SaveBiotaQueueItem> batch)
        {
            SaveBatchStats.RecordDrainBatch(batch.Count);

            // Which items have already been told their outcome. A callback is a promise made EXACTLY
            // once per queued save: telling an item false after it has already been told true would
            // re-enqueue a retry for a biota that did persist.
            var answered = new bool[batch.Count];

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

                    answered[0] = true;
                    Answer(item.Callback, result);
                    Report(item.PerformanceResults, executeStart - item.InitialCallTime, executeEnd - executeStart);
                }
                else
                {
                    var executeStart = DateTime.UtcNow;

                    var results = BaseDatabase.SaveBiotaBatch(batch.Select(b => (b.Biota, b.RwLock)).ToList());

                    var executeEnd = DateTime.UtcNow;

                    for (var i = 0; i < batch.Count; i++)
                    {
                        answered[i] = true;
                        Answer(batch[i].Callback, results[i]);
                        Report(batch[i].PerformanceResults, executeStart - batch[i].InitialCallTime, executeEnd - executeStart);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] DoWork batch of {batch.Count} SaveBiota item(s) failed with exception: {ex}");

                // FAIL THE CALLBACKS, do not drop them (fix round 2, F2). BaseDatabase.SaveBiota can
                // throw - StageBiota reaches GetBiotaCore, and the caching subclass has its own
                // dereferences - and a save whose callback never fires is not "a save that failed", it
                // is a save whose caller is still waiting. AccountVaultStore is the case that made this
                // load-bearing: a deposit's retry is enrolled ONLY from this callback, eviction is
                // declined only while that retry list is non-empty, and SaveAndWait blocks the world
                // tick thread for its full timeout. A dropped callback there is a vault item whose
                // biota row stays an orphan with nothing left to re-save it.
                //
                // Everything unanswered is told false, including a batch that threw part way: which
                // members of a failed SaveBiotaBatch actually landed is not knowable from here, and the
                // two errors are not symmetric. A false negative costs a re-save of an unchanged biota.
                // A false positive costs the item.
                for (var i = 0; i < batch.Count; i++)
                {
                    if (answered[i])
                        continue;

                    answered[i] = true;
                    Answer(batch[i].Callback, false);
                }
            }
        }

        private void RunRemoveBiotaBatch(List<RemoveBiotaQueueItem> batch)
        {
            var answered = new bool[batch.Count];

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

                    answered[0] = true;
                    Answer(item.Callback, result);
                    Report(item.PerformanceResults, executeStart - item.InitialCallTime, executeEnd - executeStart);
                }
                else
                {
                    var executeStart = DateTime.UtcNow;

                    var results = BaseDatabase.RemoveBiotaBatch(batch.Select(b => b.BiotaId).ToList());

                    var executeEnd = DateTime.UtcNow;

                    for (var i = 0; i < batch.Count; i++)
                    {
                        answered[i] = true;
                        Answer(batch[i].Callback, results[i]);
                        Report(batch[i].PerformanceResults, executeStart - batch[i].InitialCallTime, executeEnd - executeStart);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] DoWork batch of {batch.Count} RemoveBiota item(s) failed with exception: {ex}");

                // Same as the save path above, and this is the "perhaps add failure callbacks?" note
                // RunStandalone still carries. A remove whose callback never fires leaves its caller
                // believing the row is still there, or still waiting to be told either way.
                for (var i = 0; i < batch.Count; i++)
                {
                    if (answered[i])
                        continue;

                    answered[i] = true;
                    Answer(batch[i].Callback, false);
                }
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

        /// <summary>
        /// Proving Grounds: Speed - queues one completed run for insertion into `character_speed_run`.
        /// <para/>
        /// Deliberately on the generic Task path (like SaveCharacter and GetCharacter) rather than a
        /// batchable queue-item type: a completion is a rare, one-off write, so there is nothing to
        /// amortise and a new batchable kind would only add a held-over case to the worker loop.
        /// </summary>
        public void AddSpeedRun(CharacterSpeedRun row, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.AddSpeedRun(row);
                callback?.Invoke(result);
            })));
        }

        // Class Ability Point (CAP) audit ledger - `character_cap_ledger` / `character_cap_audit`.
        //
        // All four take the generic non-batched Task path, exactly like AddSpeedRun above and for the
        // same reason: a CAP mutation is a rare, one-off write (a learn, a respec, a purchase), so
        // there is nothing to amortise, and a new batchable queue-item kind would only add another
        // held-over case to the worker loop for no measurable saving.
        //
        // THE TRADE THIS TAKES, stated so it is not rediscovered later. The write is QUEUED and
        // fire-and-forget: the caller pays only an enqueue onto a BlockingCollection, so a CAP
        // mutation can never stall a landblock tick on a MySQL round trip, and the worker's strict
        // FIFO order means ledger rows land in the same order as the biota saves they describe.
        // The cost is that a queued row can be LOST on an unclean shutdown - the worker drains the
        // BlockingCollection and a Stop() mid-queue drops whatever is left. That is acceptable for a
        // forensic log and is strictly better than the zero rows the shard records today. The
        // synchronous alternative buys that last row back at the price of a simulation-thread stall
        // on every learn, purchase and respec, which is a worse trade.
        //
        // Every callback is nullable and the DAO already logs and swallows, so a caller that does not
        // care about the outcome passes null and nothing anywhere unwinds on a failed ledger write.

        /// <summary>
        /// Queues one CAP mutation for insertion into `character_cap_ledger`.
        /// </summary>
        public void AddCapLedgerRow(CharacterCapLedger row, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.AddCapLedgerRow(row);
                callback?.Invoke(result);
            })));
        }

        /// <summary>
        /// Queues one character's CAP audit summary upsert into `character_cap_audit`.
        /// </summary>
        public void UpsertCapAudit(CharacterCapAudit row, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.UpsertCapAudit(row);
                callback?.Invoke(result);
            })));
        }

        /// <summary>
        /// Queues a read of one character's most recent CAP ledger rows. The callback receives NULL
        /// if the read FAILED and an empty list if the character has no recorded history - see
        /// ShardDatabase_CapLedger.cs's read failure contract.
        /// </summary>
        public void GetCapLedger(uint characterId, int limit, Action<List<CharacterCapLedger>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetCapLedger(characterId, limit);
                callback?.Invoke(result);
            })));
        }

        /// <summary>
        /// Queues a single-row-by-id read of one character's CAP audit summary. THREE OUTCOMES - see
        /// ShardDatabase_CapLedger.cs's GetCapAudit for the full contract: the callback's second
        /// parameter (found) is true with a non-null row when the character has a recorded audit
        /// (possibly balanced), true with a null row when the character has never been audited, and
        /// false with a null row when the read FAILED.
        /// </summary>
        public void GetCapAudit(uint characterId, Action<CharacterCapAudit, bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetCapAudit(characterId, out var found);
                callback?.Invoke(result, found);
            })));
        }

        /// <summary>
        /// Queues a read of every currently-unbalanced CAP audit summary row. The callback receives
        /// NULL if the read FAILED and an empty list if no character is out of balance.
        /// </summary>
        public void GetCapAuditFailures(int limit, Action<List<CharacterCapAudit>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetCapAuditFailures(limit);
                callback?.Invoke(result);
            })));
        }

        // PvP Arena (DreamWeave) - `character_pvp_rating`, `pvp_match`, `pvp_match_participant`.
        //
        // Both take the generic non-batched Task path, like AddSpeedRun and the CAP ledger above: a
        // resolved match is a rare one-off write and the ratings read happens once at boot, so there is
        // nothing to amortise and a new batchable queue-item kind would only add a held-over case to
        // the worker loop. The worker's strict FIFO order is what keeps two matches that resolve back
        // to back for the same player landing in the order they resolved, so the later absolute rating
        // upsert is the one that sticks.
        //
        // Neither callback runs on the world thread. RunStandalone Start()s the Task (a thread-pool
        // thread) and the database worker Wait()s on it, so the callback is serialized with every
        // other queued operation but is still off-thread. A caller that touches world state from the
        // callback must marshal back to the world thread itself. A throwing callback is logged and
        // swallowed by RunStandalone and does not undo the write.

        /// <summary>
        /// Queues the boot read of every rating row. The callback receives a list (empty when the
        /// table does not exist yet) or NULL when the read FAILED - see
        /// ShardDatabase_PvpArena.GetAllPvpRatings for why those two must not be conflated.
        /// </summary>
        public void GetAllPvpRatings(Action<List<PvpRatingRecord>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetAllPvpRatings();
                callback?.Invoke(result);
            })));
        }

        /// <summary>
        /// Queues one resolved match for writing: the match row, its participants and every rating
        /// upsert, in one SaveChanges. The callback receives (result, new match id). The id is 0 unless
        /// the result is Saved. Per the DAO's caller contract, a caller never resubmits on Failed or
        /// Ambiguous - see ShardDatabase_PvpArena.SavePvpMatchResult.
        ///
        /// The records are COPIED here, on the caller's thread, before the entry is queued. The write
        /// runs later on the worker thread, and the copy means the caller may reuse or mutate its own
        /// records the moment this returns without racing the worker.
        /// </summary>
        public void SavePvpMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback)
        {
            var matchCopy = match?.Clone();
            var participantsCopy = participants?.Select(p => p?.Clone()).ToList();
            var ratingsCopy = ratingUpserts?.Select(r => r?.Clone()).ToList();

            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SavePvpMatchResult(matchCopy, participantsCopy, ratingsCopy, out var matchId);
                callback?.Invoke(result, matchId);
            })));
        }

        // PvP Template Facets - `pvp_template`, `pvp_template_history` and the participant template stamps.
        //
        // All take the generic non-batched Task path, like the arena calls above: an admin snapshot, an
        // enable/modes change and a per-match stamp are rare one-off operations. The callbacks run off the
        // world thread (see the arena block's remarks); a caller touching world state must marshal back.
        // Records are COPIED on the caller's thread before queueing, for the same reason SavePvpMatchResult
        // copies its records.

        /// <summary>Queues a read of every template row. The callback receives (rows, status); see ShardDatabase_PvpTemplates.GetAllPvpTemplates.</summary>
        public void GetAllPvpTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetAllPvpTemplates(out var status);
                callback?.Invoke(result, status);
            })));
        }

        /// <summary>Queues a read of one template row by key.</summary>
        public void GetPvpTemplate(string templateKey, Action<PvpTemplateRecord, PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetPvpTemplate(templateKey, out var status);
                callback?.Invoke(result, status);
            })));
        }

        /// <summary>Queues a read of one history row by (key, version).</summary>
        public void GetPvpTemplateHistory(string templateKey, uint version, Action<PvpTemplateRecord, PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetPvpTemplateHistory(templateKey, version, out var status);
                callback?.Invoke(result, status);
            })));
        }

        /// <summary>Queues one snapshot write. The callback receives (status, assigned version).</summary>
        public void SavePvpTemplateSnapshot(PvpTemplateRecord snapshot, Action<PvpTemplateStoreStatus, uint> callback)
        {
            var copy = snapshot?.Clone();

            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SavePvpTemplateSnapshot(copy, out var version);
                callback?.Invoke(result, version);
            })));
        }

        public void SetPvpTemplateEnabled(string templateKey, bool enabled, Action<PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SetPvpTemplateEnabled(templateKey, enabled);
                callback?.Invoke(result);
            })));
        }

        public void SetPvpTemplateModes(string templateKey, string modes, Action<PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SetPvpTemplateModes(templateKey, modes);
                callback?.Invoke(result);
            })));
        }

        public void SetPvpTemplateDisplayName(string templateKey, string displayName, Action<PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SetPvpTemplateDisplayName(templateKey, displayName);
                callback?.Invoke(result);
            })));
        }

        /// <summary>Queues the boot read of each character's latest stamped template per ladder (for /top).</summary>
        public void GetLatestPvpParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetLatestPvpParticipantTemplates(out var status);
                callback?.Invoke(result, status);
            })));
        }

        /// <summary>
        /// Queues the participant template stamps for a match already saved by SavePvpMatchResult. Queue it from
        /// that save's callback (or any time after it is queued): the worker's strict FIFO order guarantees the
        /// UPDATE runs after the INSERT it targets.
        /// </summary>
        public void SetPvpMatchParticipantTemplates(uint matchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback)
        {
            var copy = stamps?.Select(s => s?.Clone()).ToList();

            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SetPvpMatchParticipantTemplates(matchId, copy);
                callback?.Invoke(result);
            })));
        }

        /// <summary>
        /// Player Facets. Rare one-off reads and writes (a switch, not a hot path), so like
        /// AddSpeedRun this takes the generic non-batched Task path rather than a batchable queue-item
        /// type.
        /// </summary>
        public void GetCharacterFacets(uint characterId, Action<List<CharacterFacet>> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.GetCharacterFacets(characterId);
                callback?.Invoke(result);
            })));
        }

        public void SaveCharacterFacet(CharacterFacet row, Action<bool> callback)
        {
            _queue.Add(new QueueEntry(new Task(() =>
            {
                var result = BaseDatabase.SaveCharacterFacet(row);
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

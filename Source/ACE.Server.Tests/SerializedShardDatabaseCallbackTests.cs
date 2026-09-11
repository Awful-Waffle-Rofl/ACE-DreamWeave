using System;
using System.Collections.Generic;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Fix round 2, F2: SerializedShardDatabase's queue drain must ANSWER every queued item, including
    /// when the database call throws.
    ///
    /// RunSaveBiotaBatch invoked its callbacks inside a try whose catch invoked none of them, so a
    /// throwing BaseDatabase.SaveBiota left every caller in that batch waiting forever for an answer
    /// that never came. That is not the same thing as a save that failed:
    ///
    ///  - AccountVaultStore enrols a deposit's retry ONLY from this callback, and declines idle
    ///    eviction only while that retry list is non-empty - so a dropped callback means the retry is
    ///    never enrolled, the store is evicted normally, and the vault item's biota row stays the
    ///    orphan the sell path flushed, with nothing left anywhere that would ever re-save it;
    ///  - AccountVaultStore.SaveAndWait blocks the WORLD TICK THREAD until its callback arrives or its
    ///    five second timeout expires. A dropped callback costs the full timeout, every time.
    ///
    /// This file lives in ACE.Server.Tests rather than ACE.Database.Tests deliberately:
    /// ACE.Database.Tests needs a live MySQL instance (see AccountTests.TestSetup), and the point here
    /// is a ShardDatabase whose save THROWS, which needs no database at all. SerializedShardDatabase's
    /// constructor is internal, so ACE.Database.csproj grants InternalsVisibleTo to this assembly, the
    /// same way ACE.Server.csproj already does.
    ///
    /// Nothing here touches a DbContext: the fake overrides every virtual the drain loop calls, and the
    /// biotas are bare ACE.Entity.Models.Biota instances (the RUNTIME shape, with property
    /// dictionaries - never ACE.Database.Models.Shard.Biota, whose EF navigation collections would need
    /// a context).
    /// </summary>
    [TestClass]
    public class SerializedShardDatabaseCallbackTests
    {
        /// <summary>
        /// A ShardDatabase whose save and remove paths throw on demand. Every method the drain loop
        /// reaches is virtual on ShardDatabase, so no reflection is needed and no base implementation
        /// ever runs.
        /// </summary>
        private sealed class ThrowingShardDatabase : ShardDatabase
        {
            public bool ThrowFromSave;
            public bool ThrowFromSaveBatch;
            public bool ThrowFromRemove;
            public bool ThrowFromRemoveBatch;

            public int SaveCalls;
            public int SaveBatchCalls;

            public override bool SaveBiota(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, bool doNotAddToCache = false)
            {
                SaveCalls++;

                if (ThrowFromSave)
                    throw new InvalidOperationException("simulated shard failure saving a biota");

                return true;
            }

            public override List<bool> SaveBiotaBatch(IList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> items, bool doNotAddToCache = false)
            {
                SaveBatchCalls++;

                if (ThrowFromSaveBatch)
                    throw new InvalidOperationException("simulated shard failure saving a batch of biotas");

                var results = new List<bool>();

                for (var i = 0; i < items.Count; i++)
                    results.Add(true);

                return results;
            }

            public override bool RemoveBiota(uint id)
            {
                if (ThrowFromRemove)
                    throw new InvalidOperationException("simulated shard failure removing a biota");

                return true;
            }

            public override List<bool> RemoveBiotaBatch(IList<uint> ids)
            {
                if (ThrowFromRemoveBatch)
                    throw new InvalidOperationException("simulated shard failure removing a batch of biotas");

                var results = new List<bool>();

                for (var i = 0; i < ids.Count; i++)
                    results.Add(true);

                return results;
            }
        }

        private const int CallbackWaitMs = 5000;

        private static ACE.Entity.Models.Biota MakeBiota(uint id)
        {
            return new ACE.Entity.Models.Biota { Id = id, WeenieClassId = 1, WeenieType = ACE.Entity.Enum.WeenieType.Generic };
        }

        /// <summary>
        /// The single-item path: one queued SaveBiota whose database call throws must still have its
        /// callback invoked, with false.
        /// </summary>
        [TestMethod]
        public void SaveBiota_WhenTheSaveThrows_StillInvokesTheCallbackWithFalse()
        {
            var db = new ThrowingShardDatabase { ThrowFromSave = true };
            var serialized = new SerializedShardDatabase(db);

            var answered = new ManualResetEventSlim(false);
            bool? result = null;

            serialized.SaveBiota(MakeBiota(0x70000101), new ReaderWriterLockSlim(), ok =>
            {
                result = ok;
                answered.Set();
            });

            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs),
                    "the callback was never invoked at all. A queued save whose database call throws must still be ANSWERED - its caller is blocked or waiting on that answer, and a dropped callback is not a failed save, it is a save nobody is ever told about.");
            }
            finally
            {
                serialized.Stop();
            }

            Assert.AreEqual(false, result, "a save that threw must be reported as a failure, not as a success");
            Assert.AreEqual(1, db.SaveCalls, "sanity: the throwing save must actually have been reached");
        }

        /// <summary>
        /// The BATCH path, which is the one that carries the multiplier: a throwing SaveBiotaBatch used
        /// to drop up to a hundred callbacks at once.
        ///
        /// Every item is queued BEFORE the worker thread starts, so the drain deterministically merges
        /// them into one batch rather than depending on timing.
        ///
        /// All three are told false, including any that might in principle have landed before the throw:
        /// which members of a failed batch committed is not knowable from the drain loop, and the two
        /// errors are not symmetric. A false negative costs a re-save of an unchanged biota; a false
        /// positive costs the item.
        /// </summary>
        [TestMethod]
        public void SaveBiotaBatch_WhenTheBatchThrows_InvokesEveryCallbackWithFalse()
        {
            var db = new ThrowingShardDatabase { ThrowFromSaveBatch = true };
            var serialized = new SerializedShardDatabase(db);

            const int count = 3;

            var answered = new CountdownEvent(count);
            var results = new bool?[count];

            for (var i = 0; i < count; i++)
            {
                var index = i;

                serialized.SaveBiota(MakeBiota((uint)(0x70000201 + i)), new ReaderWriterLockSlim(), ok =>
                {
                    results[index] = ok;
                    answered.Signal();
                });
            }

            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs),
                    $"only {count - answered.CurrentCount} of {count} callbacks were invoked. A throwing batch must answer every item it took off the queue, not silently drop the lot.");
            }
            finally
            {
                serialized.Stop();
            }

            for (var i = 0; i < count; i++)
                Assert.AreEqual(false, results[i], $"callback {i} must be told false");

            Assert.AreEqual(1, db.SaveBatchCalls, "sanity: the three saves must have merged into one batch, or this is testing the single-item path three times");
        }

        /// <summary>
        /// The control, and it is not decoration: without it the fix above is indistinguishable from
        /// "always report false". A save that SUCCEEDS must still be told true.
        /// </summary>
        [TestMethod]
        public void SaveBiota_WhenTheSaveSucceeds_StillInvokesTheCallbackWithTrue()
        {
            var db = new ThrowingShardDatabase();
            var serialized = new SerializedShardDatabase(db);

            var answered = new ManualResetEventSlim(false);
            bool? result = null;

            serialized.SaveBiota(MakeBiota(0x70000301), new ReaderWriterLockSlim(), ok =>
            {
                result = ok;
                answered.Set();
            });

            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs), "the callback must be invoked for a successful save too");
            }
            finally
            {
                serialized.Stop();
            }

            Assert.AreEqual(true, result);
        }

        /// <summary>
        /// One callback throwing must not stop its siblings being answered. Before the fix the loop was
        /// unwrapped, so the first throwing callback aborted the rest and the enclosing catch answered
        /// nobody - the same dropped-callback failure, reached from the other direction.
        /// </summary>
        [TestMethod]
        public void SaveBiotaBatch_WhenOneCallbackThrows_TheOthersAreStillInvoked()
        {
            var db = new ThrowingShardDatabase();
            var serialized = new SerializedShardDatabase(db);

            var answered = new CountdownEvent(2);
            var lateCallbackRan = false;

            serialized.SaveBiota(MakeBiota(0x70000401), new ReaderWriterLockSlim(), ok =>
            {
                answered.Signal();
                throw new InvalidOperationException("a callback that misbehaves");
            });

            serialized.SaveBiota(MakeBiota(0x70000402), new ReaderWriterLockSlim(), ok =>
            {
                lateCallbackRan = true;
                answered.Signal();
            });

            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs),
                    "a throwing callback must not take its siblings' callbacks with it - each invocation is wrapped so one caller's bug cannot silence another's answer.");
            }
            finally
            {
                serialized.Stop();
            }

            Assert.IsTrue(lateCallbackRan);
        }

        /// <summary>
        /// The remove path has the same shape and the same consequence - a caller told nothing about a
        /// delete believes the row is still there - so it gets the same treatment and the same test.
        /// </summary>
        [TestMethod]
        public void RemoveBiota_WhenTheRemoveThrows_StillInvokesTheCallbackWithFalse()
        {
            var db = new ThrowingShardDatabase { ThrowFromRemove = true };
            var serialized = new SerializedShardDatabase(db);

            var answered = new ManualResetEventSlim(false);
            bool? result = null;

            serialized.RemoveBiota(0x70000501, ok =>
            {
                result = ok;
                answered.Set();
            });

            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs), "a queued remove whose database call throws must still be answered");
            }
            finally
            {
                serialized.Stop();
            }

            Assert.AreEqual(false, result);
        }
    }
}

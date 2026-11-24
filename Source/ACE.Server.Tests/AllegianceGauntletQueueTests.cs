using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SerializedShardDatabase's Allegiance Gauntlet queue wrappers, without a database. A recording ShardDatabase
    /// overrides the six virtual Gauntlet writes and keeps exactly what the worker hands it.
    ///
    /// ENQUEUE-TIME COPY. Each row test enqueues while the worker is NOT yet started, then mutates the caller's row,
    /// and only then starts the worker. The DAO call therefore runs strictly after the mutation, so a wrapper that
    /// queued the caller's own object (no copy at enqueue) is seen with the mutated values and fails, every run, with
    /// no timing window.
    ///
    /// CLAIM TOKEN. The claim wrapper must pass the caller's token through unchanged, so a resubmit after Failed can
    /// reuse it and read its own committed row back as OwnRowOnRetry.
    /// </summary>
    [TestClass]
    public class AllegianceGauntletQueueTests
    {
        private const int CallbackWaitMs = 5000;

        private sealed class RecordingShardDatabase : ShardDatabase
        {
            public readonly List<object> Received = new List<object>();

            public readonly List<string> Tokens = new List<string>();

            /// <summary>(account, UTC day) -> holding token: just enough of the claim table to model a lost ack.</summary>
            public readonly Dictionary<(uint, DateTime), string> Claims = new Dictionary<(uint, DateTime), string>();

            /// <summary>When set, the next claim INSERT commits its row but reports Failed, as a lost acknowledgement does.</summary>
            public bool LoseNextClaimAck;

            public override bool InsertGauntletRun(AllegianceGauntletRun row, out uint runId)
            {
                Received.Add(row);
                runId = 42;
                return true;
            }

            public override bool SaveGauntletRunEnd(AllegianceGauntletRun row)
            {
                Received.Add(row);
                return true;
            }

            public override bool SaveGauntletWave(AllegianceGauntletWave row)
            {
                Received.Add(row);
                return true;
            }

            public override bool UpsertGauntletRecord(AllegianceGauntletRecord row)
            {
                Received.Add(row);
                return true;
            }

            public override GauntletClaimInsertResult TryInsertGauntletDailyClaim(uint accountId, DateTime date, uint runId, uint characterId, string claimToken)
            {
                Tokens.Add(claimToken);

                var key = (accountId, date.Date);

                if (Claims.TryGetValue(key, out var holder))
                    return holder == claimToken ? GauntletClaimInsertResult.OwnRowOnRetry : GauntletClaimInsertResult.AlreadyClaimedToday;

                Claims[key] = claimToken;

                if (LoseNextClaimAck)
                {
                    LoseNextClaimAck = false;
                    return GauntletClaimInsertResult.Failed;
                }

                return GauntletClaimInsertResult.Inserted;
            }

            public override bool DeleteGauntletDailyClaim(uint accountId, DateTime date, uint runId, uint characterId, out int deleted)
            {
                deleted = 0;
                return true;
            }
        }

        /// <summary>Enqueue with the worker stopped, mutate, then start the worker, wait for the callback, stop.</summary>
        private static T EnqueueMutateThenRun<T>(Action<SerializedShardDatabase, ManualResetEventSlim> enqueue, Action mutate) where T : class
        {
            var db = new RecordingShardDatabase();
            var serialized = new SerializedShardDatabase(db);
            using var answered = new ManualResetEventSlim(false);

            enqueue(serialized, answered);
            mutate();

            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs), "the queued item ran and answered");
            }
            finally
            {
                serialized.Stop();
            }

            Assert.AreEqual(1, db.Received.Count);
            var received = db.Received[0] as T;
            Assert.IsNotNull(received, $"the DAO received a {typeof(T).Name}");
            return received;
        }

        private static AllegianceGauntletRun Run() => new AllegianceGauntletRun
        {
            RunId = 7, AvatarId = 3, SiteId = "site-a", StartedAt = new DateTime(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc),
            ScoreCenti = 100, WavesCleared = 2, CratesClaimed = 1,
        };

        [TestMethod]
        public void InsertGauntletRun_CopiesRowAtEnqueue()
        {
            var row = Run();
            var newId = 0u;

            var received = EnqueueMutateThenRun<AllegianceGauntletRun>(
                (s, done) => s.InsertGauntletRun(row, (ok, id) => { newId = id; done.Set(); }),
                () => { row.ScoreCenti = 999; row.SiteId = "site-b"; row.AvatarId = 9; });

            Assert.AreNotSame(row, received, "the DAO must get the enqueue-time copy, not the caller's live row");
            Assert.AreEqual(100u, received.ScoreCenti);
            Assert.AreEqual("site-a", received.SiteId);
            Assert.AreEqual(3u, received.AvatarId);
            Assert.AreEqual(42u, newId, "the callback receives the DAO's id");
        }

        [TestMethod]
        public void SaveGauntletRunEnd_CopiesRowAtEnqueue()
        {
            var row = Run();

            var received = EnqueueMutateThenRun<AllegianceGauntletRun>(
                (s, done) => s.SaveGauntletRunEnd(row, ok => done.Set()),
                () => { row.ScoreCenti = 999; row.WavesCleared = 50; row.CratesClaimed = 9; });

            Assert.AreNotSame(row, received);
            Assert.AreEqual(100u, received.ScoreCenti);
            Assert.AreEqual((ushort)2, received.WavesCleared);
            Assert.AreEqual((ushort)1, received.CratesClaimed);
        }

        [TestMethod]
        public void SaveGauntletWave_CopiesRowAtEnqueue()
        {
            var row = new AllegianceGauntletWave { RunId = 7, Wave = 3, Level = 220, Killed = 18, Family = "Olthoi" };

            var received = EnqueueMutateThenRun<AllegianceGauntletWave>(
                (s, done) => s.SaveGauntletWave(row, ok => done.Set()),
                () => { row.Wave = 4; row.Killed = 1; row.Family = "Drudge"; });

            Assert.AreNotSame(row, received);
            Assert.AreEqual((ushort)3, received.Wave);
            Assert.AreEqual((ushort)18, received.Killed);
            Assert.AreEqual("Olthoi", received.Family);
        }

        [TestMethod]
        public void UpsertGauntletRecord_CopiesRowAtEnqueue()
        {
            var row = new AllegianceGauntletRecord { AvatarId = 3, BestCenti = 500, RunCount = 2, LastRunAt = new DateTime(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc) };

            var received = EnqueueMutateThenRun<AllegianceGauntletRecord>(
                (s, done) => s.UpsertGauntletRecord(row, ok => done.Set()),
                () => { row.BestCenti = 9999; row.RunCount = 3; row.LastRunAt = null; });

            Assert.AreNotSame(row, received);
            Assert.AreEqual(500u, received.BestCenti);
            Assert.AreEqual(2u, received.RunCount);
            Assert.IsNotNull(received.LastRunAt, "the cooldown the caller enqueued is the one written");
        }

        [TestMethod]
        public void Claim_CallerToken_PassedThroughUnchanged()
        {
            var db = new RecordingShardDatabase();
            var serialized = new SerializedShardDatabase(db);
            var token = ShardDatabase.NewGauntletClaimToken();
            using var answered = new ManualResetEventSlim(false);

            serialized.TryInsertGauntletDailyClaim(1, DateTime.UtcNow, 2, 3, r => answered.Set(), token);
            serialized.Start();

            try
            {
                Assert.IsTrue(answered.Wait(CallbackWaitMs));
            }
            finally
            {
                serialized.Stop();
            }

            CollectionAssert.AreEqual(new[] { token }, db.Tokens);
        }

        [TestMethod]
        public void Claim_NoToken_FreshTokenPerCall()
        {
            var db = new RecordingShardDatabase();
            var serialized = new SerializedShardDatabase(db);
            using var both = new CountdownEvent(2);

            serialized.TryInsertGauntletDailyClaim(1, DateTime.UtcNow, 2, 3, r => both.Signal());
            serialized.TryInsertGauntletDailyClaim(4, DateTime.UtcNow, 5, 6, r => both.Signal());
            serialized.Start();

            try
            {
                Assert.IsTrue(both.Wait(CallbackWaitMs));
            }
            finally
            {
                serialized.Stop();
            }

            Assert.AreEqual(2, db.Tokens.Count);
            Assert.IsTrue(Regex.IsMatch(db.Tokens[0], "^[0-9a-f]{32}$"));
            Assert.IsTrue(Regex.IsMatch(db.Tokens[1], "^[0-9a-f]{32}$"));
            Assert.AreNotEqual(db.Tokens[0], db.Tokens[1]);
        }

        /// <summary>
        /// F5: a Failed answer whose INSERT had in fact committed, then a resubmit through the queue. With the SAME
        /// token the resubmit is recognized as the caller's own row; the control shows a fresh token would have read
        /// as AlreadyClaimedToday, consuming the day with no crate given.
        /// </summary>
        [TestMethod]
        public void Claim_FailedThenSameTokenResubmit_OwnRowOnRetry()
        {
            var db = new RecordingShardDatabase { LoseNextClaimAck = true };
            var serialized = new SerializedShardDatabase(db);
            var token = ShardDatabase.NewGauntletClaimToken();
            var day = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
            var results = new List<GauntletClaimInsertResult>();
            using var all = new CountdownEvent(3);

            serialized.TryInsertGauntletDailyClaim(1, day, 2, 3, r => { lock (results) results.Add(r); all.Signal(); }, token);
            serialized.TryInsertGauntletDailyClaim(1, day, 2, 3, r => { lock (results) results.Add(r); all.Signal(); }, token);
            serialized.TryInsertGauntletDailyClaim(1, day, 2, 3, r => { lock (results) results.Add(r); all.Signal(); });
            serialized.Start();

            try
            {
                Assert.IsTrue(all.Wait(CallbackWaitMs));
            }
            finally
            {
                serialized.Stop();
            }

            CollectionAssert.AreEqual(new[]
            {
                GauntletClaimInsertResult.Failed,
                GauntletClaimInsertResult.OwnRowOnRetry,
                GauntletClaimInsertResult.AlreadyClaimedToday,
            }, results, "FIFO: lost ack, same-token resubmit, fresh-token control");
        }
    }
}

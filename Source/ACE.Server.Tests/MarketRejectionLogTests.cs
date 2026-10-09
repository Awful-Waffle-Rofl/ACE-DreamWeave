using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The rejection log's own mechanics, with no MarketManager in the picture: enqueue, the cap and
    /// its drop counter, the enabled flag, and the drain.
    ///
    /// The writer thread is deliberately never started here. It is started only by the production
    /// MarketManager.Initialize(), which is what keeps a background flush from racing these
    /// assertions and keeps PropertyManager off the test process's read path.
    /// </summary>
    [TestClass]
    public class MarketRejectionLogTests
    {
        private static readonly MarketActor Actor = new MarketActor(4242, 0x50000901, "Rejecttester");

        [TestInitialize]
        public void Setup() => MarketRejectionLog.ResetForTests();

        [TestCleanup]
        public void Teardown() => MarketRejectionLog.ResetForTests();

        private static void RecordOne(MarketError error = MarketError.InvalidPrice)
            => MarketRejectionLog.Record(MarketRejectOperation.List, error, MarketChannel.InGame, Actor, null, null, 9001, 1, 5);

        [TestMethod]
        public void Record_UnderTheCap_QueuesOneRowPerCall()
        {
            RecordOne();
            RecordOne(MarketError.ItemNotFound);

            var rows = MarketRejectionLog.Snapshot();

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(2, MarketRejectionLog.Stats.QueueDepth);
            Assert.AreEqual(0L, MarketRejectionLog.Stats.DroppedSinceBoot);

            Assert.AreEqual("invalid_price", rows[0].ReasonCode);
            Assert.AreEqual("item_not_found", rows[1].ReasonCode);
        }

        [TestMethod]
        public void Record_CapturesTheActorAndTheChannel()
        {
            MarketRejectionLog.Record(MarketRejectOperation.Buy, MarketError.VaultFull, MarketChannel.Web, Actor,
                                      77, 0x60000001, 9500, 3, 250, "detail line");

            var row = MarketRejectionLog.Snapshot().Single();

            Assert.AreEqual((int)MarketRejectOperation.Buy, row.Operation);
            Assert.AreEqual("vault_full", row.ReasonCode);
            Assert.AreEqual(Actor.AccountId, row.AccountId);
            Assert.AreEqual(Actor.CharacterGuid, row.CharacterGuid);
            Assert.AreEqual("Rejecttester", row.CharacterName);
            Assert.AreEqual((uint?)77u, row.ListingId);
            Assert.AreEqual((uint?)0x60000001u, row.ItemGuid);
            Assert.AreEqual(9500u, row.Wcid);
            Assert.AreEqual(3, row.Count);
            Assert.AreEqual(250L, row.PriceMmd);
            Assert.AreEqual((int)MarketChannel.Web, row.Channel);
            Assert.AreEqual("detail line", row.Detail);

            // Invariant 6: the timestamp is assigned in C#, never left to the store default.
            Assert.AreNotEqual(default(DateTime), row.Timestamp);
            Assert.IsTrue((DateTime.UtcNow - row.Timestamp).TotalMinutes < 1);
        }

        [TestMethod]
        public void Record_OverTheCap_IncrementsDroppedAndDoesNotThrow()
        {
            for (var i = 0; i < MarketRejectionLog.QueueCap; i++)
                RecordOne();

            Assert.AreEqual(MarketRejectionLog.QueueCap, MarketRejectionLog.Stats.QueueDepth);
            Assert.AreEqual(0L, MarketRejectionLog.Stats.DroppedSinceBoot);

            RecordOne();
            RecordOne();

            Assert.AreEqual(MarketRejectionLog.QueueCap, MarketRejectionLog.Stats.QueueDepth,
                "the queue must not grow past its cap");
            Assert.AreEqual(2L, MarketRejectionLog.Stats.DroppedSinceBoot);
        }

        [TestMethod]
        public void Record_WhenDisabled_DropsSilentlyWithoutCountingIt()
        {
            MarketRejectionLog.SetEnabledForTests(false);

            RecordOne();

            Assert.AreEqual(0, MarketRejectionLog.Stats.QueueDepth);

            // NOT a drop: a drop means the audit trail lost something it wanted, and the operator
            // turning recording off is not that. Counting it would make the status line lie.
            Assert.AreEqual(0L, MarketRejectionLog.Stats.DroppedSinceBoot);
        }

        [TestMethod]
        public void Drain_WritesEveryQueuedRowAndEmptiesTheQueue()
        {
            var repo = new FakeMarketRepository();

            for (var i = 0; i < 5; i++)
                RecordOne();

            MarketRejectionLog.DrainForTests(repo);

            Assert.AreEqual(5, repo.RejectedAttempts.Count);
            Assert.AreEqual(0, MarketRejectionLog.Stats.QueueDepth);
            Assert.AreEqual(0, MarketRejectionLog.Snapshot().Count);
        }

        /// <summary>
        /// A burst larger than one batch must still drain completely - the writer batches at 500 rows
        /// per SaveChanges, so a single-batch drain would strand everything past the first 500.
        /// </summary>
        [TestMethod]
        public void Drain_BurstLargerThanOneBatch_DrainsCompletelyInSeveralBatches()
        {
            var repo = new FakeMarketRepository();

            for (var i = 0; i < 1201; i++)
                RecordOne();

            MarketRejectionLog.DrainForTests(repo);

            Assert.AreEqual(1201, repo.RejectedAttempts.Count);
            Assert.AreEqual(0, MarketRejectionLog.Stats.QueueDepth);
            Assert.AreEqual(3, repo.AddRejectedAttemptsCalls, "1201 rows should batch as 500 + 500 + 201");
        }

        /// <summary>
        /// A failed write LOSES the batch rather than requeueing it. Requeueing against an unreachable
        /// shard is an unbounded retry loop that fills the queue and starts dropping anyway; the DAO
        /// has already logged the loss loudly.
        /// </summary>
        [TestMethod]
        public void Drain_WhenTheWriteFails_DropsTheBatchRatherThanLooping()
        {
            var repo = new FakeMarketRepository { FailAddRejectedAttempts = true };

            for (var i = 0; i < 3; i++)
                RecordOne();

            MarketRejectionLog.DrainForTests(repo);

            Assert.AreEqual(0, repo.RejectedAttempts.Count);
            Assert.AreEqual(0, MarketRejectionLog.Stats.QueueDepth);
            Assert.AreEqual(1, repo.AddRejectedAttemptsCalls);
        }

        [TestMethod]
        public void Record_NeverStoresTheErrorOrdinal()
        {
            // Invariant 1. Every code the log can write is a published wire code, so inserting a new
            // MarketError member can never renumber history that has already been written.
            foreach (MarketError error in Enum.GetValues(typeof(MarketError)))
            {
                if (error == MarketError.None || error == MarketError.Disabled)
                    continue;

                MarketRejectionLog.ResetForTests();
                RecordOne(error);

                var row = MarketRejectionLog.Snapshot().Single();

                Assert.AreEqual(MarketErrorCodes.ToCode(error), row.ReasonCode);
                Assert.IsFalse(int.TryParse(row.ReasonCode, out _), "a reason code must never be an ordinal");
            }
        }

        [TestMethod]
        public void Stats_ReportTheQueueAndTheDropCounter()
        {
            var before = MarketRejectionLog.Stats;

            Assert.IsFalse(before.WriterRunning, "the unit-test process must never start the writer thread");
            Assert.IsTrue(before.RecordingEnabled);
            Assert.AreEqual(30, before.RetentionDays);

            RecordOne();

            Assert.AreEqual(1, MarketRejectionLog.Stats.QueueDepth);
        }
    }
}

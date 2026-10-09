using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Round trip of ShardDatabase.UpsertCapAudit (ShardDatabase_CapLedger.cs) against a real shard
    /// database.
    ///
    /// THIS WRITES ROWS, into character_cap_audit, and deletes its own rows again afterwards. It
    /// therefore runs ONLY when the environment variable <see cref="OptInVariable"/> is set to 1,
    /// following PvpArenaRoundTripTests - a promise that Config.js's Shard block points at a
    /// DISPOSABLE database. Without it every test here reports Inconclusive.
    ///
    /// Character ids are drawn from a random high range per run so parallel or leftover rows cannot
    /// collide.
    ///
    /// These tests exist to prove the fix for the DBNull.Value positional-argument bug in
    /// UpsertCapAudit: EF Core's ExecuteSqlRaw cannot infer a store type for the CLR type DBNull
    /// when it is passed as a bare {n} placeholder argument, so a BALANCED character (whose
    /// FirstDetectedAt is null) threw on every call. Before the fix, UpsertCapAudit_Balanced_*
    /// below must FAIL with that exact provider exception.
    /// </summary>
    [TestClass]
    public class CapLedgerAuditTests
    {
        public const string OptInVariable = "ACE_DISPOSABLE_SHARD_DB";

        private static ShardDatabase RequireDisposableShard()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Assert.Inconclusive($"Skipped: set {OptInVariable}=1 only when Config.js points at a disposable shard database. This test writes rows.");

            TestEnvironment.InitializeConfig();

            var shardDb = new ShardDatabase();

            bool reachable;

            try
            {
                reachable = shardDb.Exists(false);
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive("Skipped: the shard database is not reachable with the configured Config.js.");

            return shardDb;
        }

        private static void DeleteRow(uint characterId)
        {
            using var context = new ShardDbContext();

            context.CharacterCapAudit.Where(r => r.CharacterId == characterId).ExecuteDelete();
        }

        private static CharacterCapAudit ReadRow(uint characterId)
        {
            using var context = new ShardDbContext();

            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            return context.CharacterCapAudit.SingleOrDefault(r => r.CharacterId == characterId);
        }

        /// <summary>
        /// The case that throws before the fix: a balanced character's FirstDetectedAt is null,
        /// which CapLedger.FirstDetectedAtToWrite returns for a balanced character, and that null
        /// must upsert successfully and persist as NULL in first_Detected_At.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void UpsertCapAudit_Balanced_InsertsRowWithNullFirstDetectedAt()
        {
            var shardDb = RequireDisposableShard();

            var characterId = 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);

            try
            {
                var row = new CharacterCapAudit
                {
                    CharacterId = characterId,
                    CharacterName = "BalancedOne",
                    TotalEarned = 40,
                    Available = 10,
                    OwnedCost = 30,
                    SinkSpend = 0,
                    Unexplained = 0,
                    OrphanRows = 0,
                    RankDivergences = 0,
                    FirstDetectedAt = null,
                    LastCheckedAt = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)
                };

                var ok = shardDb.UpsertCapAudit(row);

                Assert.IsTrue(ok, "a balanced character's upsert (null FirstDetectedAt) must succeed");

                var readBack = ReadRow(characterId);

                Assert.IsNotNull(readBack);
                Assert.IsNull(readBack.FirstDetectedAt, "a balanced character must persist a NULL first_Detected_At");
                Assert.AreEqual(0, readBack.Unexplained);
                Assert.AreEqual("BalancedOne", readBack.CharacterName);
                Assert.AreEqual(40, readBack.TotalEarned);
                Assert.AreEqual(10, readBack.Available);
                Assert.AreEqual(30, readBack.OwnedCost);
                Assert.AreEqual(0, readBack.SinkSpend);
                Assert.AreEqual(0, readBack.OrphanRows);
                Assert.AreEqual(0, readBack.RankDivergences);
                Assert.AreEqual(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc), readBack.LastCheckedAt);
            }
            finally
            {
                DeleteRow(characterId);
            }
        }

        /// <summary>
        /// An out-of-balance character writes its timestamp into first_Detected_At, and every column
        /// round-trips.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void UpsertCapAudit_OutOfBalance_WritesFirstDetectedAt()
        {
            var shardDb = RequireDisposableShard();

            var characterId = 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);
            var firstDetected = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

            try
            {
                var row = new CharacterCapAudit
                {
                    CharacterId = characterId,
                    CharacterName = "OutOfBalanceOne",
                    TotalEarned = 40,
                    Available = 10,
                    OwnedCost = 25,
                    SinkSpend = 0,
                    Unexplained = 5,
                    OrphanRows = 1,
                    RankDivergences = 2,
                    FirstDetectedAt = firstDetected,
                    LastCheckedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)
                };

                var ok = shardDb.UpsertCapAudit(row);

                Assert.IsTrue(ok);

                var readBack = ReadRow(characterId);

                Assert.IsNotNull(readBack);
                Assert.AreEqual(firstDetected, readBack.FirstDetectedAt);
                Assert.AreEqual(5, readBack.Unexplained);
                Assert.AreEqual(1, readBack.OrphanRows);
                Assert.AreEqual(2, readBack.RankDivergences);
            }
            finally
            {
                DeleteRow(characterId);
            }
        }

        /// <summary>
        /// The silent-corruption risk: first_Detected_At is never erased once set. An out-of-balance
        /// upsert followed by a later BALANCED upsert for the same character (FirstDetectedAt == null
        /// on the second call, as CapLedger.FirstDetectedAtToWrite returns once a character returns to
        /// balance) must leave the ORIGINAL timestamp in place, via
        /// COALESCE(first_Detected_At, VALUES(first_Detected_At)). Every other column still updates to
        /// the second call's values.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void UpsertCapAudit_BalancedAfterOutOfBalance_PreservesOriginalFirstDetectedAt()
        {
            var shardDb = RequireDisposableShard();

            var characterId = 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);
            var firstDetected = new DateTime(2026, 9, 28, 3, 0, 0, DateTimeKind.Utc);
            var secondCheck = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

            try
            {
                var first = new CharacterCapAudit
                {
                    CharacterId = characterId,
                    CharacterName = "WasOutOfBalance",
                    TotalEarned = 40,
                    Available = 10,
                    OwnedCost = 25,
                    SinkSpend = 0,
                    Unexplained = 5,
                    OrphanRows = 1,
                    RankDivergences = 1,
                    FirstDetectedAt = firstDetected,
                    LastCheckedAt = firstDetected
                };

                Assert.IsTrue(shardDb.UpsertCapAudit(first));

                var second = new CharacterCapAudit
                {
                    CharacterId = characterId,
                    CharacterName = "NowBalanced",
                    TotalEarned = 40,
                    Available = 10,
                    OwnedCost = 30,
                    SinkSpend = 0,
                    Unexplained = 0,
                    OrphanRows = 0,
                    RankDivergences = 0,
                    FirstDetectedAt = null,
                    LastCheckedAt = secondCheck
                };

                var ok = shardDb.UpsertCapAudit(second);

                Assert.IsTrue(ok, "the second (balanced, null FirstDetectedAt) upsert must also succeed");

                var readBack = ReadRow(characterId);

                Assert.IsNotNull(readBack);
                Assert.AreEqual(firstDetected, readBack.FirstDetectedAt, "the original first-seen timestamp must survive a later balanced upsert");
                Assert.AreEqual("NowBalanced", readBack.CharacterName, "every other column still overwrites");
                Assert.AreEqual(0, readBack.Unexplained);
                Assert.AreEqual(30, readBack.OwnedCost);
                Assert.AreEqual(secondCheck, readBack.LastCheckedAt);
            }
            finally
            {
                DeleteRow(characterId);
            }
        }
    }
}

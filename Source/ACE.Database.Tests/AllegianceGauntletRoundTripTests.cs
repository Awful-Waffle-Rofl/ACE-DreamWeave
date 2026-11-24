using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// ShardDatabase_AllegianceGauntlet.cs against a real shard database: the absolute-value record and wave upserts,
    /// the AUTO_INCREMENT run insert, the startup close of open runs, and the daily account claim's duplicate refusal
    /// and own-row retry recognition.
    ///
    /// THIS WRITES ROWS. It runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/> is 1, a promise that
    /// Config.js's Shard block points at a DISPOSABLE database with
    /// Database/Updates/Shard/2026-11-21-02-Add-Allegiance-Gauntlet.sql applied. Every avatar and account id is random
    /// in a high range per test, every run comes from InsertGauntletRun, and every row a test writes is deleted in its
    /// finally. CloseOpenRuns_SetsRestartOnlyOnOpenRows closes EVERY open run row in the table, which is why the
    /// database must be disposable.
    /// </summary>
    [TestClass]
    public class AllegianceGauntletRoundTripTests
    {
        private static ShardDatabase RequireDisposableShard()
        {
            if (Environment.GetEnvironmentVariable(PvpArenaRoundTripTests.OptInVariable) != "1")
                Assert.Inconclusive($"Skipped: set {PvpArenaRoundTripTests.OptInVariable}=1 only when Config.js points at a disposable shard database. This test writes rows.");

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

        private static readonly Random random = new Random();

        // High, random ranges so a test's ids never meet real rows or another test's.
        private static uint NewAvatarId() => 0xE0000000u + (uint)random.Next(0, 0x0FFFFF00);

        private static uint NewAccount() => 0xF0000000u + (uint)random.Next(0, 0x0FFFFF00);

        private static readonly DateTime Start = new DateTime(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc);

        private static uint InsertRun(ShardDatabase db, uint avatarId, DateTime startedAt)
        {
            Assert.IsTrue(db.InsertGauntletRun(new AllegianceGauntletRun { AvatarId = avatarId, SiteId = "test-site", StartedAt = startedAt, FavorPrice = 500, Ranked = 1 }, out var id), "InsertGauntletRun");
            Assert.AreNotEqual(0u, id);
            return id;
        }

        private static AllegianceGauntletRun RunRow(uint runId)
        {
            using var context = new ShardDbContext();
            return context.AllegianceGauntletRun.AsNoTracking().Single(r => r.RunId == runId);
        }

        private static void CleanupRuns(params uint[] runIds)
        {
            using var context = new ShardDbContext();
            context.AllegianceGauntletWave.Where(w => runIds.Contains(w.RunId)).ExecuteDelete();
            context.AllegianceGauntletRun.Where(r => runIds.Contains(r.RunId)).ExecuteDelete();
        }

        private static void CleanupRecord(uint avatarId)
        {
            using var context = new ShardDbContext();
            context.AllegianceGauntletRecord.Where(r => r.AvatarId == avatarId).ExecuteDelete();
        }

        private static void CleanupClaims(uint accountId)
        {
            using var context = new ShardDbContext();
            context.AllegianceGauntletClaim.Where(c => c.AccountId == accountId).ExecuteDelete();
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void UpsertRecord_TwiceSameValues_Idempotent()
        {
            var db = RequireDisposableShard();
            var avatarId = NewAvatarId();

            try
            {
                var before = DateTime.UtcNow.AddSeconds(-2);
                var record = new AllegianceGauntletRecord
                {
                    AvatarId = avatarId, BestCenti = 1234, BestParticipants = 6, BestRunId = 77,
                    BestAt = Start.AddMinutes(30), LastRunAt = Start, RunCount = 3,
                };

                Assert.IsTrue(db.UpsertGauntletRecord(record), "first write creates the row");
                Assert.IsTrue(db.UpsertGauntletRecord(record), "second write of the same values");

                var rows = db.GetAllGauntletRecords();
                Assert.IsNotNull(rows, "the read must not fail");

                var row = rows.Single(r => r.AvatarId == avatarId);
                Assert.AreEqual(1234u, row.BestCenti, "absolute, not doubled");
                Assert.AreEqual(3u, row.RunCount, "absolute, not doubled");
                Assert.AreEqual((ushort)6, row.BestParticipants);
                Assert.AreEqual(77u, row.BestRunId);
                Assert.AreEqual(Start.AddMinutes(30), row.BestAt);
                Assert.AreEqual(Start, row.LastRunAt, "the cooldown survives a reload");
                Assert.IsTrue(row.UpdatedAt >= before && row.UpdatedAt <= DateTime.UtcNow.AddSeconds(2), $"updated_At {row.UpdatedAt:u} is UTC now");

                // A later write replaces the values in place.
                record.BestCenti = 2000;
                record.RunCount = 4;
                Assert.IsTrue(db.UpsertGauntletRecord(record));

                row = db.GetAllGauntletRecords().Single(r => r.AvatarId == avatarId);
                Assert.AreEqual(2000u, row.BestCenti);
                Assert.AreEqual(4u, row.RunCount);
            }
            finally
            {
                CleanupRecord(avatarId);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void InsertRun_ReturnsId()
        {
            var db = RequireDisposableShard();
            var avatarId = NewAvatarId();
            uint first = 0, second = 0;

            try
            {
                first = InsertRun(db, avatarId, Start);
                second = InsertRun(db, avatarId, Start.AddDays(1));

                Assert.AreNotEqual(first, second, "each run gets its own id");

                var row = RunRow(first);
                Assert.AreEqual(avatarId, row.AvatarId);
                Assert.AreEqual("test-site", row.SiteId);
                Assert.AreEqual(Start, row.StartedAt);
                Assert.AreEqual(500ul, row.FavorPrice);
                Assert.IsNull(row.EndedAt, "a new run is open");
                Assert.IsNull(row.EndReason);
                Assert.AreEqual((byte)1, row.Ranked);

                // The progress write lands on the row, and an unchanged re-run still reports success (found rows).
                var progress = new AllegianceGauntletRun
                {
                    RunId = first, AvatarId = avatarId, SiteId = "ignored", StartedAt = Start.AddYears(-1),
                    ActiveAt = Start.AddMinutes(1), EndedAt = Start.AddMinutes(20), EndReason = 0,
                    WavesCleared = 7, ScoreCenti = 755, Participants = 5, Eligible = 4, PeakHeadcount = 6,
                    FavorPrice = 1, FavorCommitted = 1, CratesClaimed = 2, Ranked = 0,
                };

                Assert.IsTrue(db.SaveGauntletRunEnd(progress));
                Assert.IsTrue(db.SaveGauntletRunEnd(progress), "an unchanged re-run is still a success");

                row = RunRow(first);
                Assert.AreEqual(Start.AddMinutes(20), row.EndedAt);
                Assert.AreEqual((byte)0, row.EndReason);
                Assert.AreEqual((ushort)7, row.WavesCleared);
                Assert.AreEqual(755u, row.ScoreCenti);
                Assert.AreEqual((ushort)2, row.CratesClaimed);
                Assert.AreEqual((byte)1, row.FavorCommitted);
                Assert.AreEqual((byte)0, row.Ranked);
                Assert.AreEqual("test-site", row.SiteId, "start columns are never rewritten");
                Assert.AreEqual(Start, row.StartedAt, "start columns are never rewritten");
                Assert.AreEqual(500ul, row.FavorPrice, "start columns are never rewritten");

                Assert.IsFalse(db.SaveGauntletRunEnd(new AllegianceGauntletRun { RunId = uint.MaxValue - 1 }), "a run that does not exist is refused");
                Assert.IsFalse(db.InsertGauntletRun(new AllegianceGauntletRun { AvatarId = avatarId, SiteId = "" }, out var none));
                Assert.AreEqual(0u, none);
            }
            finally
            {
                CleanupRuns(first, second);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void CloseOpenRuns_SetsRestartOnlyOnOpenRows()
        {
            var db = RequireDisposableShard();
            var avatarId = NewAvatarId();
            uint open = 0, ended = 0;

            try
            {
                open = InsertRun(db, avatarId, Start);
                ended = InsertRun(db, avatarId, Start.AddHours(1));

                var endedAt = Start.AddHours(1).AddMinutes(15);
                Assert.IsTrue(db.SaveGauntletRunEnd(new AllegianceGauntletRun { RunId = ended, EndedAt = endedAt, EndReason = 0, Ranked = 1 }));

                var now = new DateTime(2026, 11, 22, 4, 5, 6, DateTimeKind.Utc).AddMilliseconds(900);

                Assert.IsTrue(db.CloseOpenGauntletRuns(now, out var closed));
                Assert.IsTrue(closed >= 1, $"closed {closed}");

                var openRow = RunRow(open);
                Assert.AreEqual(ShardDatabase.GauntletEndReasonRestart, openRow.EndReason, "an open run closes as Restart");
                Assert.AreEqual(new DateTime(2026, 11, 22, 4, 5, 6, DateTimeKind.Utc), openRow.EndedAt, "ended_At is now, UTC, truncated to the second");

                var endedRow = RunRow(ended);
                Assert.AreEqual((byte)0, endedRow.EndReason, "an ended run keeps its own reason");
                Assert.AreEqual(endedAt, endedRow.EndedAt, "an ended run keeps its own ended_At");

                // A second close matches nothing of ours: both rows are now ended.
                Assert.IsTrue(db.CloseOpenGauntletRuns(now.AddHours(1), out _));
                Assert.AreEqual(new DateTime(2026, 11, 22, 4, 5, 6, DateTimeKind.Utc), RunRow(open).EndedAt, "a re-run never moves an ended row");
            }
            finally
            {
                CleanupRuns(open, ended);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveWave_RetrySameRow_NoDuplicate()
        {
            var db = RequireDisposableShard();
            var runId = InsertRun(db, NewAvatarId(), Start);

            try
            {
                var wave = new AllegianceGauntletWave
                {
                    RunId = runId, Wave = 3, Level = 220, Headcount = 6, Planned = 18, Killed = 18,
                    Seconds = 74.5f, Outcome = 0, FractionCenti = 100, PlayerDeaths = 1, FighterDeaths = 0, Family = "Olthoi",
                };

                Assert.IsTrue(db.SaveGauntletWave(wave));
                Assert.IsTrue(db.SaveGauntletWave(wave), "a retried write of the same row");

                // A later write of the same (run, wave) replaces its values in place.
                wave.Killed = 12;
                wave.Outcome = 1;
                wave.FractionCenti = 66;
                wave.Family = null;
                Assert.IsTrue(db.SaveGauntletWave(wave));

                Assert.IsTrue(db.SaveGauntletWave(new AllegianceGauntletWave { RunId = runId, Wave = 4, Level = 230, Seconds = 1f }), "another wave is its own row");

                using var context = new ShardDbContext();
                var rows = context.AllegianceGauntletWave.AsNoTracking().Where(w => w.RunId == runId).OrderBy(w => w.Wave).ToList();

                Assert.AreEqual(2, rows.Count, "one row per (run, wave), never a duplicate");
                Assert.AreEqual((ushort)12, rows[0].Killed);
                Assert.AreEqual((byte)1, rows[0].Outcome);
                Assert.AreEqual((byte)66, rows[0].FractionCenti);
                Assert.AreEqual(74.5f, rows[0].Seconds);
                Assert.AreEqual(220u, rows[0].Level);
                Assert.IsNull(rows[0].Family);
                Assert.AreEqual((ushort)4, rows[1].Wave);
            }
            finally
            {
                CleanupRuns(runId);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void InsertClaim_SameAccountSameDay_Duplicate()
        {
            var db = RequireDisposableShard();
            var account = NewAccount();
            var morning = new DateTime(2026, 11, 21, 0, 0, 1, DateTimeKind.Utc);

            try
            {
                Assert.AreEqual(GauntletClaimInsertResult.Inserted, db.TryInsertGauntletDailyClaim(account, morning, 101, 0x50000001));

                // Another run, another character of the same account, later the same UTC day: refused.
                Assert.AreEqual(GauntletClaimInsertResult.AlreadyClaimedToday, db.TryInsertGauntletDailyClaim(account, morning.AddHours(23).AddMinutes(59), 202, 0x50000002));

                // Even the same run and character with a NEW attempt token is a second claim, not a retry.
                Assert.AreEqual(GauntletClaimInsertResult.AlreadyClaimedToday, db.TryInsertGauntletDailyClaim(account, morning, 101, 0x50000001));

                // Control: the next UTC day is free, and another account the same day is free.
                Assert.AreEqual(GauntletClaimInsertResult.Inserted, db.TryInsertGauntletDailyClaim(account, morning.AddDays(1), 303, 0x50000001));

                var other = NewAccount();
                try
                {
                    Assert.AreEqual(GauntletClaimInsertResult.Inserted, db.TryInsertGauntletDailyClaim(other, morning, 101, 0x50000009));
                }
                finally
                {
                    CleanupClaims(other);
                }

                using (var context = new ShardDbContext())
                {
                    var rows = context.AllegianceGauntletClaim.AsNoTracking().Where(c => c.AccountId == account).OrderBy(c => c.ClaimDate).ToList();
                    Assert.AreEqual(2, rows.Count);
                    Assert.AreEqual(morning.Date, rows[0].ClaimDate, "the key is the UTC date");
                    Assert.AreEqual(101u, rows[0].RunId, "the refused claims never overwrote the holder");
                    Assert.AreEqual(32, rows[0].ClaimToken.Length);
                }

                // The undo removes only the matching run's row, so the day is free again.
                Assert.IsTrue(db.DeleteGauntletDailyClaim(account, morning, 202, 0x50000002, out var deleted));
                Assert.AreEqual(0, deleted, "another run's undo never frees the holder's day");
                Assert.IsTrue(db.DeleteGauntletDailyClaim(account, morning.AddHours(5), 101, 0x50000001, out deleted));
                Assert.AreEqual(1, deleted);
                Assert.AreEqual(GauntletClaimInsertResult.Inserted, db.TryInsertGauntletDailyClaim(account, morning, 404, 0x50000003), "the undone day is claimable again");
            }
            finally
            {
                CleanupClaims(account);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void InsertClaim_RetryOwnRow_RecognizedAsSuccess()
        {
            var db = RequireDisposableShard();
            var account = NewAccount();
            var day = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
            var token = ShardDatabase.NewGauntletClaimToken();

            try
            {
                Assert.AreEqual(GauntletClaimInsertResult.Inserted, db.TryInsertGauntletDailyClaim(account, day, 101, 0x50000001, token));

                // What the retrying strategy's second INSERT sees when the first one committed and its ack was lost:
                // the same bound token collides with its own row.
                Assert.AreEqual(GauntletClaimInsertResult.OwnRowOnRetry, db.TryInsertGauntletDailyClaim(account, day, 101, 0x50000001, token));

                // Control: a different token is a different attempt, and is refused.
                Assert.AreEqual(GauntletClaimInsertResult.AlreadyClaimedToday, db.TryInsertGauntletDailyClaim(account, day, 101, 0x50000001, ShardDatabase.NewGauntletClaimToken()));

                // Invalid input never writes.
                Assert.AreEqual(GauntletClaimInsertResult.Failed, db.TryInsertGauntletDailyClaim(account, day.AddDays(1), 101, 0x50000001, "short"));
                Assert.AreEqual(GauntletClaimInsertResult.Failed, db.TryInsertGauntletDailyClaim(0, day.AddDays(1), 101, 0x50000001));

                using var context = new ShardDbContext();
                Assert.AreEqual(1, context.AllegianceGauntletClaim.AsNoTracking().Count(c => c.AccountId == account), "one row; the retry wrote nothing more");
            }
            finally
            {
                CleanupClaims(account);
            }
        }

        /// <summary>
        /// F5 through the real queue and table: the first attempt's row is committed directly (standing in for an
        /// INSERT that committed but answered Failed), then the caller resubmits through SerializedShardDatabase. The
        /// same token reads back as OwnRowOnRetry; the control, a resubmit with no token (a fresh one), reads back as
        /// AlreadyClaimedToday.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void QueueClaim_FailedThenSameTokenResubmit_OwnRowOnRetry()
        {
            var db = RequireDisposableShard();
            var account = NewAccount();
            var day = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
            var token = ShardDatabase.NewGauntletClaimToken();
            var serialized = new SerializedShardDatabase(db);
            serialized.Start();

            try
            {
                Assert.AreEqual(GauntletClaimInsertResult.Inserted, db.TryInsertGauntletDailyClaim(account, day, 101, 0x50000001, token), "the attempt whose answer was lost");

                GauntletClaimInsertResult? sameToken = null, freshToken = null;
                using var done = new System.Threading.CountdownEvent(2);

                serialized.TryInsertGauntletDailyClaim(account, day, 101, 0x50000001, r => { sameToken = r; done.Signal(); }, token);
                serialized.TryInsertGauntletDailyClaim(account, day, 101, 0x50000001, r => { freshToken = r; done.Signal(); });

                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "both queued claims answered");
                Assert.AreEqual(GauntletClaimInsertResult.OwnRowOnRetry, sameToken);
                Assert.AreEqual(GauntletClaimInsertResult.AlreadyClaimedToday, freshToken);
            }
            finally
            {
                serialized.Stop();
                CleanupClaims(account);
            }
        }
    }
}

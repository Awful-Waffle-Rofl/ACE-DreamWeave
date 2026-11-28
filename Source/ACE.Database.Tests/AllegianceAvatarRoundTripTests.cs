using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using MySqlConnector;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// ShardDatabase_AllegianceAvatar.cs against a real shard database: the absolute-value upserts behind
    /// SaveAvatarBatch, the monarch_Guid UNIQUE key and the in-batch guid handover, the day filter on
    /// GetAvatarContributionsSince, and the explicit UTC timestamps.
    ///
    /// THIS WRITES ROWS. It runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/> is 1, a promise that
    /// Config.js's Shard block points at a DISPOSABLE database with
    /// Database/Updates/Shard/2026-11-20-01-Add-Allegiance-Avatar.sql and 2026-11-21-01-Add-Allegiance-Avatar-Look.sql applied
    /// (and, for the daily-column round trip, 2026-11-25-02-Add-Allegiance-Avatar-Daily-Purchases.sql).
    /// Every Avatar a test needs comes from
    /// InsertAvatar (so its avatar_Id is unique to the test and the AUTO_INCREMENT counter is never pushed up by a
    /// hand-picked id), every account id and monarch guid is random per test, and every row a test writes is
    /// deleted in its finally.
    /// </summary>
    [TestClass]
    public class AllegianceAvatarRoundTripTests
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

        // High, random ranges so a test's guids and accounts never meet real rows or another test's.
        private static uint NewGuid() => 0xE0000000u + (uint)random.Next(0, 0x0FFFFF00);

        private static uint NewAccount() => 0xF0000000u + (uint)random.Next(0, 0x0FFFFF00);

        private static uint Insert(ShardDatabase db, uint? monarchGuid, ulong lifetimeXp = 0)
        {
            Assert.IsTrue(db.InsertAvatar(new AllegianceAvatar { MonarchGuid = monarchGuid, LifetimeXp = lifetimeXp }, out var id), "InsertAvatar");
            Assert.AreNotEqual(0u, id);
            return id;
        }

        private static AllegianceAvatar AvatarRow(uint avatarId)
        {
            using var context = new ShardDbContext();
            return context.AllegianceAvatar.AsNoTracking().Single(a => a.AvatarId == avatarId);
        }

        private static List<AllegianceAvatarContribution> ContributionRows(uint avatarId)
        {
            using var context = new ShardDbContext();
            return context.AllegianceAvatarContribution.AsNoTracking().Where(c => c.AvatarId == avatarId).OrderBy(c => c.Day).ToList();
        }

        private static void Cleanup(params uint[] avatarIds)
        {
            using var context = new ShardDbContext();
            context.AllegianceAvatarContribution.Where(c => avatarIds.Contains(c.AvatarId)).ExecuteDelete();
            context.AllegianceAvatar.Where(a => avatarIds.Contains(a.AvatarId)).ExecuteDelete();
        }

        /// <summary>A detached snapshot, the shape Task 6's manager hands to SaveAvatarBatch: CreatedAt left at default.</summary>
        private static AllegianceAvatar Snapshot(uint avatarId, uint? monarchGuid, ulong lifetimeXp, ulong favor = 0)
            => new AllegianceAvatar { AvatarId = avatarId, MonarchGuid = monarchGuid, LifetimeXp = lifetimeXp, Favor = favor };

        private static AllegianceAvatarContribution Contribution(uint avatarId, uint account, DateTime day, ulong raw, ulong credited, uint lastCharacter = 0x50000001)
            => new AllegianceAvatarContribution { AvatarId = avatarId, AccountId = account, Day = day, RawXp = raw, Credited = credited, LastCharacterId = lastCharacter };

        private static readonly AllegianceAvatar[] NoAvatars = Array.Empty<AllegianceAvatar>();

        private static readonly AllegianceAvatarContribution[] NoRows = Array.Empty<AllegianceAvatarContribution>();

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_IsIdempotent_RunTwiceSameRows()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var account = NewAccount();
            var id = Insert(db, guid);
            var today = DateTime.UtcNow.Date;

            try
            {
                var avatars = new[] { Snapshot(id, guid, 1234, 56) };
                var rows = new[] { Contribution(id, account, today, 700, 500) };

                Assert.IsTrue(db.SaveAvatarBatch(avatars, rows), "first save");
                Assert.IsTrue(db.SaveAvatarBatch(avatars, rows), "second save of the same values");

                var contribution = ContributionRows(id).Single();
                Assert.AreEqual(500ul, contribution.Credited, "credited holds the value, not twice the value");
                Assert.AreEqual(700ul, contribution.RawXp);

                var avatar = AvatarRow(id);
                Assert.AreEqual(1234ul, avatar.LifetimeXp, "lifetime_Xp holds the value, not twice the value");
                Assert.AreEqual(56ul, avatar.Favor);
                Assert.AreEqual(guid, avatar.MonarchGuid);
            }
            finally
            {
                Cleanup(id);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_UpdatesExistingDayRow()
        {
            var db = RequireDisposableShard();
            var account = NewAccount();
            var id = Insert(db, NewGuid());
            var today = DateTime.UtcNow.Date;

            try
            {
                Assert.IsTrue(db.SaveAvatarBatch(NoAvatars, new[] { Contribution(id, account, today, 100, 100, 0x50000001) }));

                // The same (avatar, account, day) later in the day, passed with a time of day and a later character:
                // the row is updated in place to the new absolute values, and the time of day is dropped.
                Assert.IsTrue(db.SaveAvatarBatch(NoAvatars, new[] { Contribution(id, account, today.AddHours(15).AddMinutes(7), 400, 250, 0x50000002) }));

                var row = ContributionRows(id).Single();
                Assert.AreEqual(today, row.Day);
                Assert.AreEqual(400ul, row.RawXp);
                Assert.AreEqual(250ul, row.Credited);
                Assert.AreEqual(0x50000002u, row.LastCharacterId);
            }
            finally
            {
                Cleanup(id);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void InsertAvatar_SecondRowForSameMonarch_Fails()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var first = Insert(db, guid);
            uint second = 0;
            uint released1 = 0;
            uint released2 = 0;

            try
            {
                Assert.IsFalse(db.InsertAvatar(new AllegianceAvatar { MonarchGuid = guid }, out second), "allegiance_avatar_monarch_uidx refuses a second bound row");
                Assert.AreEqual(0u, second);

                // Control: released (NULL) rows never collide, so the same call with no monarch succeeds twice.
                released1 = Insert(db, null);
                released2 = Insert(db, null);
            }
            finally
            {
                Cleanup(new[] { first, second, released1, released2 }.Where(i => i != 0).ToArray());
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void InsertAvatar_StampsUtcTimestamps()
        {
            var db = RequireDisposableShard();
            var before = DateTime.UtcNow.AddSeconds(-2);
            var id = Insert(db, NewGuid());

            try
            {
                var row = AvatarRow(id);
                var after = DateTime.UtcNow.AddSeconds(2);

                Assert.IsTrue(row.CreatedAt >= before && row.CreatedAt <= after, $"created_At {row.CreatedAt:u} is UTC now");
                Assert.IsTrue(row.UpdatedAt >= before && row.UpdatedAt <= after, $"updated_At {row.UpdatedAt:u} is UTC now");
            }
            finally
            {
                Cleanup(id);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_UpdateStampsUpdatedAtAndKeepsCreatedAt()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var id = Insert(db, guid);
            var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            try
            {
                using (var context = new ShardDbContext())
                    context.AllegianceAvatar.Where(a => a.AvatarId == id).ExecuteUpdate(s => s.SetProperty(a => a.CreatedAt, old).SetProperty(a => a.UpdatedAt, old));

                var before = DateTime.UtcNow.AddSeconds(-2);

                // A detached snapshot with CreatedAt and UpdatedAt at default: neither may reach the row as MinValue.
                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(id, guid, 99) }, NoRows));

                var row = AvatarRow(id);
                Assert.AreEqual(old, row.CreatedAt, "created_At is left alone on an update");
                Assert.AreNotEqual(old, row.UpdatedAt, "updated_At changes on an update");
                Assert.IsTrue(row.UpdatedAt >= before && row.UpdatedAt <= DateTime.UtcNow.AddSeconds(2), $"updated_At {row.UpdatedAt:u} is UTC now");
                Assert.AreEqual(99ul, row.LifetimeXp);
            }
            finally
            {
                Cleanup(id);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_GuidHandoverInOneBatch_Succeeds()
        {
            var db = RequireDisposableShard();
            var g = NewGuid();
            var a = Insert(db, g);
            var b = Insert(db, null);

            try
            {
                // A releases G and B takes G, with the taker FIRST in the input.
                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(b, g, 10), Snapshot(a, null, 20) }, NoRows), "taker listed first");
                Assert.AreEqual(g, AvatarRow(b).MonarchGuid);
                Assert.IsNull(AvatarRow(a).MonarchGuid);

                // And back again, with the releaser FIRST in the input.
                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(b, null, 11), Snapshot(a, g, 21) }, NoRows), "releaser listed first");
                Assert.AreEqual(g, AvatarRow(a).MonarchGuid);
                Assert.IsNull(AvatarRow(b).MonarchGuid);

                // A releases G by moving to a new guid H while B takes G.
                var h = NewGuid();
                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(b, g, 12), Snapshot(a, h, 22) }, NoRows), "releaser moves to a new guid");
                Assert.AreEqual(g, AvatarRow(b).MonarchGuid);
                Assert.AreEqual(h, AvatarRow(a).MonarchGuid);

                // A swap: each takes the guid the other releases.
                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(a, g, 23), Snapshot(b, h, 13) }, NoRows), "swap");
                Assert.AreEqual(g, AvatarRow(a).MonarchGuid);
                Assert.AreEqual(h, AvatarRow(b).MonarchGuid);
                Assert.AreEqual(23ul, AvatarRow(a).LifetimeXp);
            }
            finally
            {
                Cleanup(a, b);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_GuidHeldOutsideTheBatch_FailsAndWritesNothing()
        {
            var db = RequireDisposableShard();
            var g = NewGuid();
            var account = NewAccount();
            var holder = Insert(db, g);
            var other = Insert(db, null, 5);

            try
            {
                Assert.IsFalse(db.SaveAvatarBatch(new[] { Snapshot(other, g, 999) }, new[] { Contribution(other, account, DateTime.UtcNow.Date, 1, 1) }), "G is still bound to an Avatar outside the batch");

                Assert.AreEqual(g, AvatarRow(holder).MonarchGuid, "the holder keeps G");
                Assert.IsNull(AvatarRow(other).MonarchGuid, "the pre-clear rolled back with the transaction");
                Assert.AreEqual(5ul, AvatarRow(other).LifetimeXp, "nothing in the batch committed");
                Assert.AreEqual(0, ContributionRows(other).Count, "the contribution row rolled back too");
            }
            finally
            {
                Cleanup(holder, other);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetAvatarContributionsSince_FiltersByDay()
        {
            var db = RequireDisposableShard();
            var account = NewAccount();
            var id = Insert(db, NewGuid());
            var today = DateTime.UtcNow.Date;

            try
            {
                Assert.IsTrue(db.SaveAvatarBatch(NoAvatars, new[]
                {
                    Contribution(id, account, today.AddDays(-2), 1, 1),
                    Contribution(id, account, today.AddDays(-1), 2, 2),
                    Contribution(id, account, today, 3, 3),
                    Contribution(id, account + 1, today, 4, 4),
                }));

                // A since-value with a time of day still means the whole of that day.
                var since = db.GetAvatarContributionsSince(today.AddDays(-1).AddHours(13));
                Assert.IsNotNull(since);

                var mine = since.Where(c => c.AvatarId == id).OrderBy(c => c.Credited).ToList();
                CollectionAssert.AreEqual(new ulong[] { 2, 3, 4 }, mine.Select(c => c.Credited).ToArray(), "yesterday and today, not two days ago");

                var all = db.GetAvatarContributionsSince(today.AddDays(-30));
                Assert.AreEqual(4, all.Count(c => c.AvatarId == id), "control: the older row is there to be filtered");
            }
            finally
            {
                Cleanup(id);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetAllAvatars_ReturnsInsertedRows()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var id = Insert(db, guid, 42);

            try
            {
                var all = db.GetAllAvatars();
                Assert.IsNotNull(all);

                var mine = all.Single(a => a.AvatarId == id);
                Assert.AreEqual(guid, mine.MonarchGuid);
                Assert.AreEqual(42ul, mine.LifetimeXp);
            }
            finally
            {
                Cleanup(id);
            }
        }

        /// <summary>
        /// A snapshot whose avatar_Id does not exist, carrying a guid another Avatar holds. An upsert would hit the
        /// monarch index and overwrite the holder; the UPDATE-by-id matches 0 rows and rolls the whole batch back.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_UnknownAvatarId_ReturnsFalse_AndLeavesHolderUnchanged()
        {
            var db = RequireDisposableShard();
            var g = NewGuid();
            var account = NewAccount();
            var holder = Insert(db, g, 5);
            var other = Insert(db, NewGuid(), 6);

            // AUTO_INCREMENT never gets near this range, and InsertAvatar is the only way rows are founded.
            var unknown = 0xFFFFFF00u + (uint)random.Next(0, 0xF0);

            try
            {
                Assert.IsFalse(db.SaveAvatarBatch(
                    new[] { Snapshot(other, null, 60), Snapshot(unknown, g, 999, 999) },
                    new[] { Contribution(other, account, DateTime.UtcNow.Date, 1, 1) }), "an avatar_Id with no row fails the batch");

                var held = AvatarRow(holder);
                Assert.AreEqual(g, held.MonarchGuid, "the holder keeps G");
                Assert.AreEqual(5ul, held.LifetimeXp, "the holder was not overwritten");
                Assert.AreEqual(0ul, held.Favor);

                Assert.IsNotNull(AvatarRow(other).MonarchGuid, "the other Avatar's pre-clear rolled back");
                Assert.AreEqual(6ul, AvatarRow(other).LifetimeXp, "the other Avatar's write rolled back");
                Assert.AreEqual(0, ContributionRows(other).Count, "the contribution row rolled back");

                using (var context = new ShardDbContext())
                    Assert.IsFalse(context.AllegianceAvatar.Any(a => a.AvatarId == unknown), "no row was founded for the unknown id");
            }
            finally
            {
                Cleanup(holder, other, unknown);
            }
        }

        /// <summary>
        /// The row-count check counts FOUND rows: MySqlConnector's UseAffectedRows defaults to false. Probed directly
        /// with a no-op UPDATE (deterministic, unlike two saves that may or may not fall in the same second), then
        /// through SaveAvatarBatch with the same snapshot twice.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SaveAvatarBatch_UnchangedRerun_StillMatchesOneRow()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var id = Insert(db, guid, 3);

            try
            {
                using (var context = new ShardDbContext())
                {
                    var found = context.Database.ExecuteSqlRaw(
                        "UPDATE `allegiance_avatar` SET `lifetime_Xp` = `lifetime_Xp` WHERE `avatar_Id` = {0}", id);
                    Assert.AreEqual(1, found, "a no-op UPDATE reports the found row, not 0 changed rows");
                }

                var snapshot = new[] { Snapshot(id, guid, 3) };
                Assert.IsTrue(db.SaveAvatarBatch(snapshot, NoRows), "first save");
                Assert.IsTrue(db.SaveAvatarBatch(snapshot, NoRows), "unchanged re-run");
                Assert.AreEqual(3ul, AvatarRow(id).LifetimeXp);
            }
            finally
            {
                Cleanup(id);
            }
        }

        /// <summary>
        /// The queue path Task 6 actually calls: SerializedShardDatabase's InsertAvatar and SaveAvatarBatch reach the
        /// DAO on the worker and hand the outcome to the callback. The worker is held on a gated item while the save
        /// is enqueued and the caller's lists are cleared, so the save can only see what was copied at enqueue.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SerializedQueue_InsertThenSaveBatch_ReachesTheDao()
        {
            var db = RequireDisposableShard();
            var serialized = new SerializedShardDatabase(db);
            serialized.Start();

            var guid = NewGuid();
            var account = NewAccount();
            uint id = 0;
            uint blockerId = 0;
            using var gate = new System.Threading.ManualResetEventSlim();

            try
            {
                using var inserted = new System.Threading.ManualResetEventSlim();
                var insertOk = false;

                serialized.InsertAvatar(new AllegianceAvatar { MonarchGuid = guid }, (ok, newId) => { insertOk = ok; id = newId; inserted.Set(); });
                Assert.IsTrue(inserted.Wait(TimeSpan.FromSeconds(30)), "insert callback ran");
                Assert.IsTrue(insertOk);
                Assert.AreNotEqual(0u, id);

                // Hold the worker: this item's callback runs on the worker's thread and blocks until the gate opens.
                using var blocking = new System.Threading.ManualResetEventSlim();
                serialized.InsertAvatar(new AllegianceAvatar(), (ok, newId) => { blockerId = newId; blocking.Set(); gate.Wait(TimeSpan.FromSeconds(30)); });
                Assert.IsTrue(blocking.Wait(TimeSpan.FromSeconds(30)), "the worker is held");

                using var saved = new System.Threading.ManualResetEventSlim();
                var saveOk = false;

                var avatars = new List<AllegianceAvatar> { Snapshot(id, guid, 777) };
                var rows = new List<AllegianceAvatarContribution> { Contribution(id, account, DateTime.UtcNow.Date, 900, 777) };

                serialized.SaveAvatarBatch(avatars, rows, ok => { saveOk = ok; saved.Set(); });
                avatars.Clear();
                rows.Clear();
                gate.Set();

                Assert.IsTrue(saved.Wait(TimeSpan.FromSeconds(30)), "save callback ran");
                Assert.IsTrue(saveOk);
                Assert.AreEqual(777ul, AvatarRow(id).LifetimeXp);
                Assert.AreEqual(777ul, ContributionRows(id).Single().Credited);
            }
            finally
            {
                gate.Set();
                serialized.Stop();
                Cleanup(new[] { id, blockerId }.Where(i => i != 0).ToArray());
            }
        }

        /// <summary>
        /// look_Json rides both write paths: InsertAvatar (EF, through CopyAvatarForWrite) and SaveAvatarBatch (the raw
        /// UpdateAvatarSql, which silently drops any column it does not name). Read back through GetAllAvatars, the
        /// startup load.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void RoundTrip_LookJson_survives_save_and_load()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            const string founded = "{\"v\":1}";
            const string dressed = "{\"v\":1,\"body\":{\"heritage\":2},\"pieces\":[{\"wcid\":2587,\"slot\":512}]}";

            Assert.IsTrue(db.InsertAvatar(new AllegianceAvatar { MonarchGuid = guid, LookJson = founded }, out var id), "InsertAvatar");

            try
            {
                Assert.AreEqual(founded, AvatarRow(id).LookJson, "InsertAvatar writes look_Json");

                var snapshot = Snapshot(id, guid, 10);
                snapshot.LookJson = dressed;
                Assert.IsTrue(db.SaveAvatarBatch(new[] { snapshot }, NoRows));

                Assert.AreEqual(dressed, AvatarRow(id).LookJson, "SaveAvatarBatch writes look_Json");
                Assert.AreEqual(dressed, db.GetAllAvatars().Single(a => a.AvatarId == id).LookJson, "the startup load reads it back");
            }
            finally
            {
                Cleanup(id);
            }
        }

        /// <summary>
        /// level_Xp rides both write paths: InsertAvatar (EF) and SaveAvatarBatch (the raw UpdateAvatarSql). The pool is an
        /// unsigned 64-bit value, so a value past int range and the ulong maximum must both survive the round trip.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void RoundTrip_LevelXp_survives_insert_and_save()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();

            Assert.IsTrue(db.InsertAvatar(new AllegianceAvatar { MonarchGuid = guid, LevelXp = 123_456_789_012UL }, out var id), "InsertAvatar");

            try
            {
                Assert.AreEqual(123_456_789_012UL, AvatarRow(id).LevelXp, "InsertAvatar writes level_Xp");

                var snapshot = Snapshot(id, guid, 10);
                snapshot.LevelXp = ulong.MaxValue;
                Assert.IsTrue(db.SaveAvatarBatch(new[] { snapshot }, NoRows));

                Assert.AreEqual(ulong.MaxValue, AvatarRow(id).LevelXp, "SaveAvatarBatch writes level_Xp");
                Assert.AreEqual(ulong.MaxValue, db.GetAllAvatars().Single(a => a.AvatarId == id).LevelXp, "the startup load reads it back");
            }
            finally
            {
                Cleanup(id);
            }
        }

        /// <summary>
        /// NULL is the default look: an Avatar founded without one stays NULL (never an empty string), and a save
        /// with a NULL look clears a stored one, which is how a reset-look reaches the row.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void RoundTrip_null_LookJson_stays_null()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var id = Insert(db, guid);

            try
            {
                Assert.IsNull(AvatarRow(id).LookJson, "InsertAvatar without a look leaves look_Json NULL");

                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(id, guid, 1) }, NoRows));
                Assert.IsNull(AvatarRow(id).LookJson, "a save without a look keeps it NULL");

                var dressed = Snapshot(id, guid, 2);
                dressed.LookJson = "{\"v\":1}";
                Assert.IsTrue(db.SaveAvatarBatch(new[] { dressed }, NoRows));
                Assert.IsNotNull(AvatarRow(id).LookJson, "control: the look was stored");

                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(id, guid, 3) }, NoRows));
                Assert.IsNull(AvatarRow(id).LookJson, "a save with a NULL look clears the stored one");
            }
            finally
            {
                Cleanup(id);
            }
        }

        // ---- DB-free pins on the up-front refusal. These run in CI. ----

        /// <summary>
        /// UpdateAvatarSql is raw SQL, so an entity column it does not name is silently never written by
        /// SaveAvatarBatch while InsertAvatar (EF) still writes it. Every EF-mapped column except the key and
        /// created_At must appear as `col` = @param, and AvatarParameters must bind that @param.
        /// CATCHES: a new column (look_Json was the first) wired into the entity and the store but not the UPDATE.
        /// </summary>
        [TestMethod]
        public void UpdateAvatarSql_WritesEveryMappedColumn_AndBindsEachParameter()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none", new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            using var ctx = new ShardDbContext(options);
            var entity = ctx.Model.FindEntityType(typeof(AllegianceAvatar));
            var sql = ShardDatabase.UpdateAvatarSql;
            var bound = ShardDatabase.AvatarParameters(new AllegianceAvatar { AvatarId = 1 }, DateTime.UtcNow)
                .Cast<MySqlParameter>().Select(p => p.ParameterName).ToList();

            StringAssert.Contains(sql, "WHERE `avatar_Id` = @avatarId");
            Assert.IsFalse(sql.Contains("`created_At`"), "created_At is never touched by an update");

            foreach (var column in entity.GetProperties().Select(p => p.GetColumnName()).Where(c => c != "avatar_Id" && c != "created_At"))
            {
                var set = System.Text.RegularExpressions.Regex.Match(sql, $"`{column}` = (@\\w+)");
                Assert.IsTrue(set.Success, $"UpdateAvatarSql never writes `{column}`");
                CollectionAssert.Contains(bound, set.Groups[1].Value, $"`{column}` is set from {set.Groups[1].Value}, which AvatarParameters never binds");
            }

            CollectionAssert.Contains(bound, "@avatarId");
            Assert.AreEqual(bound.Count, bound.Distinct().Count(), "no parameter bound twice");
        }

        /// <summary>The look is bound verbatim, and a NULL look binds DBNull (the default look), never an empty string.</summary>
        [TestMethod]
        public void AvatarParameters_BindsLookJson_VerbatimOrDbNull()
        {
            static object LookValue(string look) => ShardDatabase.AvatarParameters(new AllegianceAvatar { AvatarId = 1, LookJson = look }, DateTime.UtcNow)
                .Cast<MySqlParameter>().Single(p => p.ParameterName == "@lookJson").Value;

            Assert.AreEqual("{\"v\":1}", LookValue("{\"v\":1}"));
            Assert.AreEqual(DBNull.Value, LookValue(null));
        }

        /// <summary>The level pool is bound as its own parameter, distinct from lifetime_Xp, and carries the full unsigned range.</summary>
        [TestMethod]
        public void AvatarParameters_BindsLevelXp_AsItsOwnParameter()
        {
            var bound = ShardDatabase.AvatarParameters(new AllegianceAvatar { AvatarId = 1, LifetimeXp = 5, LevelXp = ulong.MaxValue }, DateTime.UtcNow)
                .Cast<MySqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value);

            Assert.AreEqual(ulong.MaxValue, bound["@levelXp"]);
            Assert.AreEqual(5UL, bound["@lifetimeXp"]);
            StringAssert.Contains(ShardDatabase.UpdateAvatarSql, "`level_Xp` = @levelXp");
        }

        /// <summary>The three daily columns are bound as their own parameters; a never-stamped instant binds DBNull, never MinValue.</summary>
        [TestMethod]
        public void AvatarParameters_BindsTheDailyColumns()
        {
            var bought = new DateTime(2026, 11, 25, 17, 30, 5, DateTimeKind.Utc);
            var called = new DateTime(2026, 11, 25, 18, 0, 0, DateTimeKind.Utc);
            var bound = ShardDatabase.AvatarParameters(new AllegianceAvatar { AvatarId = 1, BlessingBoughtAt = bought, LastEventAt = called, EventsOnDay = 2 }, DateTime.UtcNow)
                .Cast<MySqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value);

            Assert.AreEqual(bought, bound["@blessingBoughtAt"]);
            Assert.AreEqual(called, bound["@lastEventAt"]);
            Assert.AreEqual((byte)2, bound["@eventsOnDay"]);

            var never = ShardDatabase.AvatarParameters(new AllegianceAvatar { AvatarId = 1 }, DateTime.UtcNow)
                .Cast<MySqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value);
            Assert.AreEqual(DBNull.Value, never["@blessingBoughtAt"]);
            Assert.AreEqual(DBNull.Value, never["@lastEventAt"]);
            Assert.AreEqual((byte)0, never["@eventsOnDay"]);
        }

        /// <summary>
        /// The daily columns ride both write paths (InsertAvatar through EF, SaveAvatarBatch through the raw UpdateAvatarSql),
        /// and a save with them cleared writes NULL back. Requires 2026-11-25-02-Add-Allegiance-Avatar-Daily-Purchases.sql applied.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void RoundTrip_DailyColumns_survive_insert_and_save()
        {
            var db = RequireDisposableShard();
            var guid = NewGuid();
            var bought = new DateTime(2026, 11, 25, 17, 30, 5, DateTimeKind.Utc);
            var called = new DateTime(2026, 11, 25, 18, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(db.InsertAvatar(new AllegianceAvatar { MonarchGuid = guid, BlessingBoughtAt = bought }, out var id), "InsertAvatar");

            try
            {
                var inserted = AvatarRow(id);
                Assert.AreEqual(bought, inserted.BlessingBoughtAt, "InsertAvatar writes blessing_Bought_At");
                Assert.IsNull(inserted.LastEventAt);
                Assert.AreEqual((byte)0, inserted.EventsOnDay, "events_On_Day defaults to 0");

                var snapshot = Snapshot(id, guid, 10);
                snapshot.BlessingBoughtAt = bought;
                snapshot.LastEventAt = called;
                snapshot.EventsOnDay = 3;
                Assert.IsTrue(db.SaveAvatarBatch(new[] { snapshot }, NoRows));

                var loaded = db.GetAllAvatars().Single(a => a.AvatarId == id);
                Assert.AreEqual(bought, loaded.BlessingBoughtAt, "SaveAvatarBatch keeps blessing_Bought_At");
                Assert.AreEqual(called, loaded.LastEventAt, "SaveAvatarBatch writes last_Event_At");
                Assert.AreEqual((byte)3, loaded.EventsOnDay, "SaveAvatarBatch writes events_On_Day");

                Assert.IsTrue(db.SaveAvatarBatch(new[] { Snapshot(id, guid, 11) }, NoRows));
                var cleared = AvatarRow(id);
                Assert.IsNull(cleared.BlessingBoughtAt, "a save without a stamp writes NULL");
                Assert.IsNull(cleared.LastEventAt);
                Assert.AreEqual((byte)0, cleared.EventsOnDay);
            }
            finally
            {
                Cleanup(id);
            }
        }

        [TestMethod]
        public void ValidateAvatarBatch_AcceptsADistinctBatch()
        {
            Assert.IsNull(ShardDatabase.ValidateAvatarBatch(
                new[] { Snapshot(1, 10, 0), Snapshot(2, null, 0), Snapshot(3, null, 0) },
                new[] { Contribution(1, 7, new DateTime(2026, 11, 20), 1, 1), Contribution(1, 7, new DateTime(2026, 11, 21), 1, 1) }));
        }

        [TestMethod]
        public void ValidateAvatarBatch_RefusesDuplicatesAndUnassignedIds()
        {
            var day = new DateTime(2026, 11, 20);

            Assert.IsNotNull(ShardDatabase.ValidateAvatarBatch(new AllegianceAvatar[] { null }, NoRows), "null avatar");
            Assert.IsNotNull(ShardDatabase.ValidateAvatarBatch(NoAvatars, new AllegianceAvatarContribution[] { null }), "null row");
            Assert.IsNotNull(ShardDatabase.ValidateAvatarBatch(new[] { Snapshot(0, 10, 0) }, NoRows), "avatar_Id 0 was never assigned by InsertAvatar");
            Assert.IsNotNull(ShardDatabase.ValidateAvatarBatch(new[] { Snapshot(1, 10, 0), Snapshot(1, 11, 0) }, NoRows), "same avatar twice");
            Assert.IsNotNull(ShardDatabase.ValidateAvatarBatch(new[] { Snapshot(1, 10, 0), Snapshot(2, 10, 0) }, NoRows), "same monarch guid twice");
            Assert.IsNotNull(ShardDatabase.ValidateAvatarBatch(NoAvatars, new[] { Contribution(1, 7, day, 1, 1), Contribution(1, 7, day.AddHours(5), 2, 2) }), "same (avatar, account, day) twice, once with a time of day");
        }
    }
}

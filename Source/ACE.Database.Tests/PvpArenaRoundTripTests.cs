using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Round trip of the PvP Arena DAO (ShardDatabase_PvpArena.cs) against a real shard database.
    ///
    /// THIS WRITES ROWS. It inserts into pvp_match, pvp_match_participant and character_pvp_rating
    /// and deletes its own rows again afterwards. It therefore runs ONLY when the environment variable
    /// <see cref="OptInVariable"/> is set to 1, which is a promise that Config.js's Shard block points
    /// at a DISPOSABLE database with Database/Updates/Shard/2026-09-25-02-Add-Pvp-Arena.sql applied.
    /// Without it every test here reports Inconclusive, because TestEnvironment otherwise falls back to
    /// the developer's real Source\ACE.Server\Config.js, and that must never be written to.
    ///
    /// Character ids are drawn from a random high range per run so parallel or leftover rows cannot
    /// collide.
    /// </summary>
    [TestClass]
    public class PvpArenaRoundTripTests
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

        private static void DeleteRows(uint matchId, params uint[] characterIds)
        {
            using var context = new ShardDbContext();

            if (matchId != 0)
            {
                context.PvpMatchParticipant.Where(p => p.MatchId == matchId).ExecuteDelete();
                context.PvpMatch.Where(m => m.Id == matchId).ExecuteDelete();
            }

            context.CharacterPvpRating.Where(r => characterIds.Contains(r.CharacterId)).ExecuteDelete();
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SavePvpMatchResult_InsertsThenUpserts_AndGetAllPvpRatingsReadsItBack()
        {
            var shardDb = RequireDisposableShard();

            var baseId = 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);
            var a = baseId;
            var b = baseId + 1;
            uint firstMatch = 0, secondMatch = 0;

            var ended = new DateTime(2026, 9, 25, 20, 0, 0, DateTimeKind.Utc);

            try
            {
                // First match: both rows are new, so this exercises the INSERT side of the upsert.
                var ok = shardDb.SavePvpMatchResult(
                    new PvpMatchRecord { Mode = "arena_1v1", Ladder = "arena_1v1", Map = "test_map", StartedAt = ended.AddMinutes(-3), EndedAt = ended, Outcome = "decided", EndReason = "elimination", Rated = true },
                    new[]
                    {
                        new PvpMatchParticipantRecord { CharacterId = a, CharacterName = "Alpha", Team = 0, Placement = 1, Result = "win", RatingBefore = 1500, RatingAfter = 1520, Kills = 1 },
                        new PvpMatchParticipantRecord { CharacterId = b, CharacterName = "Bravo", Team = 1, Placement = 2, Result = "loss", RatingBefore = 1500, RatingAfter = 1480, Deaths = 1 },
                    },
                    new[]
                    {
                        new PvpRatingRecord { CharacterId = a, Ladder = "arena_1v1", CharacterName = "Alpha", Rating = 1520, Games = 1, Wins = 1, Peak = 1520, LastMatchAt = ended },
                        new PvpRatingRecord { CharacterId = b, Ladder = "arena_1v1", CharacterName = "Bravo", Rating = 1480, Games = 1, Losses = 1, Peak = 1500, LastMatchAt = ended },
                    },
                    out firstMatch);

                Assert.AreEqual(PvpMatchSaveResult.Saved, ok);
                Assert.AreNotEqual(0u, firstMatch);

                // Second match: both rows exist, so this exercises the UPDATE side.
                ok = shardDb.SavePvpMatchResult(
                    new PvpMatchRecord { Mode = "arena_1v1", Ladder = "arena_1v1", Map = "test_map", StartedAt = ended.AddMinutes(7), EndedAt = ended.AddMinutes(10), Outcome = "decided", EndReason = "forfeit", Rated = true },
                    new[]
                    {
                        new PvpMatchParticipantRecord { CharacterId = a, CharacterName = "Alpha Renamed", Team = 0, Placement = 2, Result = "loss", RatingBefore = 1520, RatingAfter = 1498, ForfeitReason = "logout" },
                        new PvpMatchParticipantRecord { CharacterId = b, CharacterName = "Bravo", Team = 1, Placement = 1, Result = "win", RatingBefore = 1480, RatingAfter = 1502 },
                    },
                    new[]
                    {
                        new PvpRatingRecord { CharacterId = a, Ladder = "arena_1v1", CharacterName = "Alpha Renamed", Rating = 1498, Games = 2, Wins = 1, Losses = 1, Peak = 1520, LastMatchAt = ended.AddMinutes(10) },
                        new PvpRatingRecord { CharacterId = b, Ladder = "arena_1v1", CharacterName = "Bravo", Rating = 1502, Games = 2, Wins = 1, Losses = 1, Peak = 1502, LastMatchAt = ended.AddMinutes(10) },
                    },
                    out secondMatch);

                Assert.AreEqual(PvpMatchSaveResult.Saved, ok);
                Assert.IsTrue(secondMatch > firstMatch);

                var ratings = shardDb.GetAllPvpRatings();

                Assert.IsNotNull(ratings, "the read must not have failed");

                var ra = ratings.Single(r => r.CharacterId == a && r.Ladder == "arena_1v1");
                Assert.AreEqual("Alpha Renamed", ra.CharacterName, "the name snapshot is refreshed by the upsert");
                Assert.AreEqual(1498, ra.Rating);
                Assert.AreEqual(2, ra.Games);
                Assert.AreEqual(1, ra.Wins);
                Assert.AreEqual(1, ra.Losses);
                Assert.AreEqual(1520, ra.Peak);
                Assert.AreEqual(ended.AddMinutes(10), ra.LastMatchAt);
                Assert.AreEqual(DateTimeKind.Utc, ra.LastMatchAt.Kind);

                Assert.AreEqual(1502, ratings.Single(r => r.CharacterId == b && r.Ladder == "arena_1v1").Rating);

                using (var context = new ShardDbContext())
                {
                    var participants = context.PvpMatchParticipant.AsNoTracking().Where(p => p.MatchId == secondMatch).OrderBy(p => p.Placement).ToList();

                    Assert.AreEqual(2, participants.Count, "both participants landed with the generated match id");
                    Assert.AreEqual(b, participants[0].CharacterId);
                    Assert.AreEqual("logout", participants[1].ForfeitReason);

                    var match = context.PvpMatch.AsNoTracking().Single(m => m.Id == secondMatch);
                    Assert.AreEqual("forfeit", match.EndReason);
                    Assert.IsTrue(match.Rated);
                }
            }
            finally
            {
                DeleteRows(firstMatch, a, b);
                DeleteRows(secondMatch);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SavePvpMatchResult_RefusedBatch_WritesNothing()
        {
            var shardDb = RequireDisposableShard();

            var id = 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFFF0);

            try
            {
                var ok = shardDb.SavePvpMatchResult(
                    new PvpMatchRecord { Mode = "arena_1v1", Ladder = "arena_1v1", Map = "test_map", EndedAt = DateTime.UtcNow, Outcome = "decided", EndReason = "elimination" },
                    new[]
                    {
                        new PvpMatchParticipantRecord { CharacterId = id, CharacterName = "Dup", Result = "win" },
                        new PvpMatchParticipantRecord { CharacterId = id, CharacterName = "Dup", Result = "loss" },
                    },
                    new[] { new PvpRatingRecord { CharacterId = id, Ladder = "arena_1v1", CharacterName = "Dup", Rating = 1600 } },
                    out var matchId);

                Assert.AreEqual(PvpMatchSaveResult.Failed, ok);
                Assert.AreEqual(0u, matchId);

                var ratings = shardDb.GetAllPvpRatings();
                Assert.IsNotNull(ratings);
                Assert.IsFalse(ratings.Any(r => r.CharacterId == id), "a refused batch must not have upserted its rating");
            }
            finally
            {
                DeleteRows(0, id);
            }
        }
    }
}

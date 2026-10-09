using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PvP Arena storage shape - the character_pvp_rating, pvp_match and pvp_match_participant tables
    /// created by Database/Updates/Shard/2026-09-25-02-Add-Pvp-Arena.sql, their EF mapping, and the
    /// pure parts of ShardDatabase_PvpArena.cs (validation and record mapping).
    ///
    /// Same split as CapLedgerSchemaTests / RewardClaimSchemaTests: ACE.Server.Tests has no database,
    /// so these catch an entity/migration mismatch at build time. The round trip against a real MySQL
    /// lives in ACE.Database.Tests (PvpArenaRoundTripTests) and only runs on an opted-in disposable
    /// shard.
    /// </summary>
    [TestClass]
    public class PvpArenaSchemaTests
    {
        private const string MigrationFile = "2026-09-25-02-Add-Pvp-Arena.sql";

        private static readonly Dictionary<string, string> ExpectedRatingColumns = new Dictionary<string, string>
        {
            { "CharacterId",   "character_Id" },
            { "Ladder",        "ladder" },
            { "CharacterName", "character_Name" },
            { "Rating",        "rating" },
            { "Games",         "games" },
            { "Wins",          "wins" },
            { "Losses",        "losses" },
            { "Draws",         "draws" },
            { "Peak",          "peak" },
            { "LastMatchAt",   "last_Match_At" },
        };

        private static readonly Dictionary<string, string> ExpectedMatchColumns = new Dictionary<string, string>
        {
            { "Id",        "id" },
            { "Mode",      "mode" },
            { "Ladder",    "ladder" },
            { "Map",       "map" },
            { "StartedAt", "started_At" },
            { "EndedAt",   "ended_At" },
            { "Outcome",   "outcome" },
            { "EndReason", "end_Reason" },
            { "Rated",     "rated" },
        };

        private static readonly Dictionary<string, string> ExpectedParticipantColumns = new Dictionary<string, string>
        {
            { "MatchId",       "match_Id" },
            { "CharacterId",   "character_Id" },
            { "CharacterName", "character_Name" },
            { "Team",          "team" },
            { "Placement",     "placement" },
            { "Result",        "result" },
            { "RatingBefore",  "rating_Before" },
            { "RatingAfter",   "rating_After" },
            { "Kills",         "kills" },
            { "Deaths",        "deaths" },
            { "ForfeitReason", "forfeit_Reason" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Migration()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile));

        /// <summary>The migration with comments removed, so prose cannot satisfy or break an assertion.</summary>
        private static string Statements()
        {
            var sql = Regex.Replace(Migration(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(sql, @"(?m)--.*$", string.Empty);
        }

        /// <summary>The body of one CREATE TABLE statement, up to its terminating semicolon.</summary>
        private static string TableBody(string table)
        {
            var sql = Statements();
            var start = sql.IndexOf($"CREATE TABLE IF NOT EXISTS `{table}`", StringComparison.Ordinal);

            Assert.IsTrue(start >= 0, $"no CREATE TABLE IF NOT EXISTS `{table}`");

            var end = sql.IndexOf(';', start);

            Assert.IsTrue(end > start, $"`{table}` statement is not terminated");

            return sql.Substring(start, end - start);
        }

        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        // ---- migration ---------------------------------------------------------------------------

        [TestMethod]
        public void Migration_CreatesExactlyTheThreeTables_Idempotently()
        {
            var statements = Statements();

            Assert.AreEqual(3, Regex.Matches(statements, @"CREATE TABLE IF NOT EXISTS").Count);
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.AreEqual(3, statements.Count(c => c == ';'), "exactly three statements. TableBody finds each statement's end by its semicolon, so a semicolon inside a COMMENT string would cut it short. (The applier itself sends the whole file as one MySqlCommand - Program_DbUpdates.PatchDatabase - and does not split.)");
        }

        [TestMethod]
        public void Migration_HasNoForeignKeyOrDangerousConstructs()
        {
            var statements = Statements();

            Assert.IsFalse(statements.Contains("FOREIGN KEY"), "no FK to character (or anywhere) - deliberate, see the migration header");
            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER - it is a mysql client command, not SQL, and the applier sends the file to the server as one MySqlCommand");
            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.IsFalse(statements.Contains("CURRENT_TIMESTAMP"), "no datetime default - it would stamp the database server's LOCAL time");
            Assert.IsFalse(Regex.IsMatch(statements, @"ADD (COLUMN|INDEX)\s+IF NOT EXISTS", RegexOptions.IgnoreCase),
                "ADD COLUMN/INDEX IF NOT EXISTS is MariaDB-only");
        }

        [TestMethod]
        public void Migration_DeclaresTheKeysAndIndexes()
        {
            var rating = TableBody("character_pvp_rating");
            StringAssert.Contains(rating, "PRIMARY KEY (`character_Id`, `ladder`)");
            StringAssert.Contains(rating, "KEY `character_pvp_rating_ladder_idx` (`ladder`, `rating`)");

            var match = TableBody("pvp_match");
            StringAssert.Contains(match, "PRIMARY KEY (`id`)");
            StringAssert.Contains(match, "KEY `pvp_match_ended_idx` (`ended_At`)");
            Assert.IsTrue(Regex.IsMatch(match, @"`id`\s+int unsigned\s+NOT NULL AUTO_INCREMENT"));

            var participant = TableBody("pvp_match_participant");
            StringAssert.Contains(participant, "PRIMARY KEY (`match_Id`, `character_Id`)");
            StringAssert.Contains(participant, "KEY `pvp_match_participant_character_idx` (`character_Id`, `match_Id`)");
        }

        [TestMethod]
        public void Migration_DeclaresEveryMappedColumn_InItsOwnTable()
        {
            foreach (var pair in ExpectedRatingColumns)
                StringAssert.Contains(TableBody("character_pvp_rating"), $"`{pair.Value}`", $"character_pvp_rating is missing {pair.Value}");

            foreach (var pair in ExpectedMatchColumns)
                StringAssert.Contains(TableBody("pvp_match"), $"`{pair.Value}`", $"pvp_match is missing {pair.Value}");

            foreach (var pair in ExpectedParticipantColumns)
                StringAssert.Contains(TableBody("pvp_match_participant"), $"`{pair.Value}`", $"pvp_match_participant is missing {pair.Value}");
        }

        [TestMethod]
        public void Migration_NullabilityMatchesTheEntities()
        {
            // The only nullable columns are the ones whose CLR type is nullable (or an optional string).
            var match = TableBody("pvp_match");
            Assert.IsTrue(Regex.IsMatch(match, @"`started_At`\s+datetime\s+NULL\b"), "started_At is NULL for a match that never reached Live");
            Assert.IsTrue(Regex.IsMatch(match, @"`ended_At`\s+datetime\s+NOT NULL"));

            var participant = TableBody("pvp_match_participant");
            Assert.IsTrue(Regex.IsMatch(participant, @"`rating_Before`\s+int\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(participant, @"`rating_After`\s+int\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(participant, @"`forfeit_Reason`\s+varchar\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(participant, @"`placement`\s+int\s+NOT NULL"));

            var rating = TableBody("character_pvp_rating");
            Assert.IsTrue(Regex.IsMatch(rating, @"`ladder`\s+varchar\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(rating, @"`last_Match_At`\s+datetime\s+NOT NULL"));
        }

        // ---- EF model ------------------------------------------------------------------------------

        private static void AssertColumns(Type clrType, string table, Dictionary<string, string> expected)
        {
            using var ctx = CreateContext();

            var e = ctx.Model.FindEntityType(clrType);

            Assert.IsNotNull(e, $"{clrType.Name} is not in the ShardDbContext model");
            Assert.AreEqual(table, e.GetTableName());

            // Both directions: every mapped scalar property is in the map, and every map entry is mapped.
            CollectionAssert.AreEquivalent(expected.Keys.ToList(), e.GetProperties().Select(p => p.Name).ToList(),
                $"{clrType.Name}'s mapped properties and the expected column map have diverged");

            foreach (var pair in expected)
                Assert.AreEqual(pair.Value, e.FindProperty(pair.Key).GetColumnName(), $"{clrType.Name}.{pair.Key}");
        }

        [TestMethod]
        public void EfModel_MapsEveryColumn()
        {
            AssertColumns(typeof(CharacterPvpRating), "character_pvp_rating", ExpectedRatingColumns);
            AssertColumns(typeof(PvpMatch), "pvp_match", ExpectedMatchColumns);
            AssertColumns(typeof(PvpMatchParticipant), "pvp_match_participant", ExpectedParticipantColumns);
        }

        [TestMethod]
        public void EfModel_KeysIndexesAndValueGeneration()
        {
            using var ctx = CreateContext();

            var rating = ctx.Model.FindEntityType(typeof(CharacterPvpRating));
            CollectionAssert.AreEqual(new[] { "CharacterId", "Ladder" }, rating.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "Ladder", "Rating" },
                rating.GetIndexes().Single(i => i.GetDatabaseName() == "character_pvp_rating_ladder_idx").Properties.Select(p => p.Name).ToArray());
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, rating.FindProperty("CharacterId").ValueGenerated,
                "the rating key is supplied by the server, never generated");

            var match = ctx.Model.FindEntityType(typeof(PvpMatch));
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd, match.FindProperty("Id").ValueGenerated);
            Assert.IsTrue(match.FindProperty("StartedAt").IsNullable);
            Assert.IsFalse(match.FindProperty("Rated").GetDefaultValueSql() != null,
                "no store default on Rated - EF would then omit an explicit false from the INSERT");

            var participant = ctx.Model.FindEntityType(typeof(PvpMatchParticipant));
            CollectionAssert.AreEqual(new[] { "MatchId", "CharacterId" }, participant.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "CharacterId", "MatchId" },
                participant.GetIndexes().Single(i => i.GetDatabaseName() == "pvp_match_participant_character_idx").Properties.Select(p => p.Name).ToArray());
            Assert.IsTrue(participant.FindProperty("RatingBefore").IsNullable);
            Assert.IsTrue(participant.FindProperty("RatingAfter").IsNullable);
        }

        /// <summary>
        /// The one-SaveChanges write depends on EF knowing that participant.MatchId is the match's id,
        /// so the generated AUTO_INCREMENT value is propagated to the participant rows.
        /// </summary>
        [TestMethod]
        public void EfModel_ParticipantMatchIdIsTheMatchForeignKey()
        {
            using var ctx = CreateContext();

            var participant = ctx.Model.FindEntityType(typeof(PvpMatchParticipant));
            var fk = participant.GetForeignKeys().Single();

            Assert.AreEqual(typeof(PvpMatch), fk.PrincipalEntityType.ClrType);
            CollectionAssert.AreEqual(new[] { "MatchId" }, fk.Properties.Select(p => p.Name).ToArray());
            Assert.AreEqual("Participants", fk.PrincipalToDependent?.Name);
        }

        [TestMethod]
        public void ShardDbContext_DeclaresAllThreeDbSets()
        {
            foreach (var name in new[] { "CharacterPvpRating", "PvpMatch", "PvpMatchParticipant" })
                Assert.IsNotNull(typeof(ShardDbContext).GetProperty(name, BindingFlags.Public | BindingFlags.Instance), $"ShardDbContext.{name} DbSet is missing");
        }

        // ---- the DAO's pure parts ------------------------------------------------------------------

        private static PvpMatchParticipantRecord Participant(uint id) => new PvpMatchParticipantRecord { CharacterId = id, CharacterName = $"p{id}", Result = "win" };

        private static PvpRatingRecord Rating(uint id, string ladder) => new PvpRatingRecord { CharacterId = id, Ladder = ladder, CharacterName = $"p{id}", Rating = 1500 };

        [TestMethod]
        public void Validate_AcceptsAnOrdinaryBatch_AndAnEmptyOne()
        {
            Assert.IsNull(ShardDatabase.ValidatePvpMatchResult(
                new[] { Participant(1), Participant(2) },
                new[] { Rating(1, "arena_1v1"), Rating(2, "arena_1v1") }));

            Assert.IsNull(ShardDatabase.ValidatePvpMatchResult(
                Array.Empty<PvpMatchParticipantRecord>(), Array.Empty<PvpRatingRecord>()),
                "an unrated canceled match carries no rating upserts, and that is legal");

            // The same character on two ladders is two distinct rows, not a duplicate.
            Assert.IsNull(ShardDatabase.ValidatePvpMatchResult(
                new[] { Participant(1) },
                new[] { Rating(1, "arena_1v1"), Rating(1, "arena_2v2") }));
        }

        [TestMethod]
        public void Validate_RefusesDuplicatesNullsAndMissingLadder()
        {
            Assert.IsNotNull(ShardDatabase.ValidatePvpMatchResult(new[] { Participant(1), Participant(1) }, Array.Empty<PvpRatingRecord>()),
                "the same character twice in one match would violate the participant primary key");

            Assert.IsNotNull(ShardDatabase.ValidatePvpMatchResult(Array.Empty<PvpMatchParticipantRecord>(), new[] { Rating(1, "arena_1v1"), Rating(1, "arena_1v1") }),
                "two upserts for one (character, ladder) are ambiguous");

            Assert.IsNotNull(ShardDatabase.ValidatePvpMatchResult(new PvpMatchParticipantRecord[] { null }, Array.Empty<PvpRatingRecord>()));
            Assert.IsNotNull(ShardDatabase.ValidatePvpMatchResult(Array.Empty<PvpMatchParticipantRecord>(), new PvpRatingRecord[] { null }));
            Assert.IsNotNull(ShardDatabase.ValidatePvpMatchResult(Array.Empty<PvpMatchParticipantRecord>(), new[] { Rating(1, "") }));
        }

        [TestMethod]
        public void Mapping_ConvertsLocalTimesToUtc_AndStampsAnUnsetEnd()
        {
            var local = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Local);

            var entity = ShardDatabase.ToEntity(new PvpMatchRecord { Mode = "arena_1v1", Ladder = "arena_1v1", Map = "m", StartedAt = local, EndedAt = local, Outcome = "decided", EndReason = "elimination", Rated = true });

            Assert.AreEqual(local.ToUniversalTime(), entity.StartedAt);
            Assert.AreEqual(local.ToUniversalTime(), entity.EndedAt);
            Assert.IsTrue(entity.Rated);

            var unset = ShardDatabase.ToEntity(new PvpMatchRecord { Mode = "arena_1v1", Ladder = "arena_1v1", Map = "m", Outcome = "canceled", EndReason = "declined" });

            Assert.IsNull(unset.StartedAt, "a match that never reached Live keeps a NULL start");
            Assert.AreNotEqual(default, unset.EndedAt, "an unset end must be stamped - the column has no default");
            Assert.IsTrue((DateTime.UtcNow - unset.EndedAt).Duration() < TimeSpan.FromMinutes(1));
        }

        [TestMethod]
        public void Mapping_RatingRoundTripsAndReadsBackAsUtc()
        {
            var row = new CharacterPvpRating { CharacterId = 7, Ladder = "arena_ffa" };
            var when = new DateTime(2026, 9, 25, 18, 30, 0, DateTimeKind.Utc);

            ShardDatabase.CopyInto(row, new PvpRatingRecord { CharacterId = 7, Ladder = "arena_ffa", CharacterName = "Seven", Rating = 1612, Games = 11, Wins = 6, Losses = 4, Draws = 1, Peak = 1640 }, when);

            // Stored the way EF hands a datetime back: Unspecified kind.
            row.LastMatchAt = DateTime.SpecifyKind(row.LastMatchAt, DateTimeKind.Unspecified);

            var record = ShardDatabase.ToRecord(row);

            Assert.AreEqual(7u, record.CharacterId);
            Assert.AreEqual("arena_ffa", record.Ladder);
            Assert.AreEqual("Seven", record.CharacterName);
            Assert.AreEqual(1612, record.Rating);
            Assert.AreEqual(11, record.Games);
            Assert.AreEqual(6, record.Wins);
            Assert.AreEqual(4, record.Losses);
            Assert.AreEqual(1, record.Draws);
            Assert.AreEqual(1640, record.Peak);
            Assert.AreEqual(when, record.LastMatchAt);
            Assert.AreEqual(DateTimeKind.Utc, record.LastMatchAt.Kind);
        }

        [TestMethod]
        public void Records_CloneIsIndependentOfTheOriginal()
        {
            // SerializedShardDatabase.SavePvpMatchResult copies the records before queueing, so a caller
            // mutating its own record afterwards cannot race the worker.
            var original = new PvpMatchParticipantRecord { CharacterId = 1, CharacterName = "a", Kills = 2 };
            var copy = original.Clone();

            original.Kills = 99;
            original.CharacterName = "b";

            Assert.AreEqual(2, copy.Kills);
            Assert.AreEqual("a", copy.CharacterName);
            Assert.AreNotSame(original, copy);

            var rating = new PvpRatingRecord { Rating = 1500 };
            var ratingCopy = rating.Clone();
            rating.Rating = 1;
            Assert.AreEqual(1500, ratingCopy.Rating);

            var match = new PvpMatchRecord { Rated = true };
            var matchCopy = match.Clone();
            match.Rated = false;
            Assert.IsTrue(matchCopy.Rated);
        }

        [TestMethod]
        public void MissingTableDetection_IgnoresUnrelatedExceptions()
        {
            Assert.IsFalse(ShardDatabase.IsMissingTableError(new InvalidOperationException("x")));
            Assert.IsFalse(ShardDatabase.IsMissingTableError(new Exception("outer", new TimeoutException("inner"))));
            Assert.IsFalse(ShardDatabase.IsMissingTableError(null));
        }

        /// <summary>
        /// The positive control for the test above: a real MySqlException carrying ER_NO_SUCH_TABLE,
        /// wrapped the way EF surfaces it, IS recognised, and one carrying a different code is not.
        /// MySqlConnector does not expose a public constructor, so it is built by reflection. If a
        /// package upgrade removes that constructor this reports Inconclusive rather than passing.
        /// </summary>
        [TestMethod]
        public void MissingTableDetection_RecognisesError1146InsideAWrapper()
        {
            var ctor = typeof(MySqlConnector.MySqlException)
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length == 2 && p[0].ParameterType == typeof(MySqlConnector.MySqlErrorCode) && p[1].ParameterType == typeof(string);
                });

            if (ctor == null)
                Assert.Inconclusive("MySqlConnector no longer has a (MySqlErrorCode, string) constructor to build the control exception with.");

            var noSuchTable = (Exception)ctor.Invoke(new object[] { MySqlConnector.MySqlErrorCode.NoSuchTable, "Table 'ace_shard.character_pvp_rating' doesn't exist" });
            var otherError = (Exception)ctor.Invoke(new object[] { MySqlConnector.MySqlErrorCode.AccessDenied, "denied" });

            Assert.AreEqual(1146, (int)MySqlConnector.MySqlErrorCode.NoSuchTable);
            Assert.IsTrue(ShardDatabase.IsMissingTableError(noSuchTable));
            Assert.IsTrue(ShardDatabase.IsMissingTableError(new InvalidOperationException("wrapper", noSuchTable)));
            Assert.IsFalse(ShardDatabase.IsMissingTableError(otherError), "any other MySQL error is a FAILED read, not a missing table");
        }

        private static Exception BuildMySqlException(MySqlConnector.MySqlErrorCode code, string message)
        {
            var ctor = typeof(MySqlConnector.MySqlException)
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length == 2 && p[0].ParameterType == typeof(MySqlConnector.MySqlErrorCode) && p[1].ParameterType == typeof(string);
                });

            if (ctor == null)
                Assert.Inconclusive("MySqlConnector no longer has a (MySqlErrorCode, string) constructor to build the control exception with.");

            return (Exception)ctor.Invoke(new object[] { code, message });
        }

        /// <summary>
        /// A duplicate key after a lost commit acknowledgement means the match may already be recorded,
        /// so it must surface as Ambiguous and never as a definite Failed. Every other error, including
        /// other MySQL errors, is Failed.
        /// </summary>
        [TestMethod]
        public void SaveFailureClassifier_DuplicateKeyIsAmbiguous_AnythingElseIsFailed()
        {
            var duplicate = BuildMySqlException(MySqlConnector.MySqlErrorCode.DuplicateKeyEntry,
                "Duplicate entry '4026531841-arena_1v1' for key 'character_pvp_rating.PRIMARY'");

            Assert.AreEqual(1062, (int)MySqlConnector.MySqlErrorCode.DuplicateKeyEntry);

            // Bare, and wrapped the way EF surfaces it from SaveChanges.
            Assert.AreEqual(PvpMatchSaveResult.Ambiguous, ShardDatabase.ClassifySaveFailure(duplicate));
            Assert.AreEqual(PvpMatchSaveResult.Ambiguous, ShardDatabase.ClassifySaveFailure(
                new DbUpdateException("An error occurred while saving the entity changes.", duplicate)));
            Assert.AreEqual(PvpMatchSaveResult.Ambiguous, ShardDatabase.ClassifySaveFailure(
                new InvalidOperationException("strategy wrapper", new DbUpdateException("inner", duplicate))));

            Assert.AreEqual(PvpMatchSaveResult.Failed, ShardDatabase.ClassifySaveFailure(
                new DbUpdateException("x", BuildMySqlException(MySqlConnector.MySqlErrorCode.NoSuchTable, "missing"))));
            Assert.AreEqual(PvpMatchSaveResult.Failed, ShardDatabase.ClassifySaveFailure(
                new DbUpdateException("x", BuildMySqlException(MySqlConnector.MySqlErrorCode.DataTooLong, "too long"))));
            Assert.AreEqual(PvpMatchSaveResult.Failed, ShardDatabase.ClassifySaveFailure(new TimeoutException("t")));
            Assert.AreEqual(PvpMatchSaveResult.Failed, ShardDatabase.ClassifySaveFailure(new InvalidOperationException("x")));
        }
    }
}

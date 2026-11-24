using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The four Allegiance Gauntlet tables' shape - catches an entity/migration mismatch at build time. Pure file and
    /// EF-metadata checks: no database connection, no PropertyManager reads.
    /// </summary>
    [TestClass]
    public class AllegianceGauntletSchemaTests
    {
        private const string MigrationFile = "2026-11-21-02-Add-Allegiance-Gauntlet.sql";

        private static readonly string[] Tables =
        {
            "allegiance_gauntlet_record", "allegiance_gauntlet_run", "allegiance_gauntlet_wave", "allegiance_gauntlet_claim",
        };

        // Copied from AllegianceAvatarSchemaTests.RepoRoot: walk up from the test output to the repo root.
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

        private static string Statements()
        {
            var sql = Migration();

            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"(?m)--.*$", string.Empty);

            return sql;
        }

        /// <summary>The CREATE TABLE statement for one table, comments stripped.</summary>
        private static string TableDdl(string table)
        {
            var m = Regex.Match(Statements(), $@"CREATE TABLE IF NOT EXISTS `{Regex.Escape(table)}` \((.*?)\)\s*ENGINE=", RegexOptions.Singleline);
            Assert.IsTrue(m.Success, $"no CREATE TABLE IF NOT EXISTS `{table}`");
            return m.Groups[1].Value;
        }

        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        [TestMethod]
        public void Migration_IsIdempotent_FourTables_NoDrop()
        {
            var sql = Migration();

            foreach (var table in Tables)
                StringAssert.Contains(sql, $"CREATE TABLE IF NOT EXISTS `{table}`");

            var statements = Statements();

            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
            Assert.IsFalse(statements.Contains("FOREIGN KEY"));
            Assert.IsFalse(Regex.IsMatch(statements, @"\b(ALTER|INSERT|UPDATE|DELETE|TRUNCATE)\b"), "only ever adds tables");
            Assert.AreEqual(4, statements.Count(c => c == ';'), "exactly four statements");
            Assert.AreEqual(4, Regex.Matches(statements, @"CREATE TABLE IF NOT EXISTS").Count);

            Assert.IsTrue(statements.All(c => c < 0x80), "ASCII only");
        }

        [TestMethod]
        public void RecordAndRun_ColumnTypes_MatchSpec()
        {
            var record = TableDdl("allegiance_gauntlet_record");

            StringAssert.Contains(record, "PRIMARY KEY (`avatar_Id`)");
            Assert.IsFalse(record.Contains("AUTO_INCREMENT"), "the record is keyed by the Avatar, never generated");
            Assert.IsTrue(Regex.IsMatch(record, @"`best_Centi`\s+int unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(record, @"`best_Participants`\s+smallint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(record, @"`best_Run_Id`\s+int unsigned\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(record, @"`last_Run_At`\s+datetime\s+NULL\b"), "the cooldown is NULL until the first commit");
            Assert.IsTrue(Regex.IsMatch(record, @"`run_Count`\s+int unsigned\s+NOT NULL DEFAULT 0"));

            var run = TableDdl("allegiance_gauntlet_run");

            Assert.IsTrue(Regex.IsMatch(run, @"`run_Id`\s+int unsigned\s+NOT NULL\s+AUTO_INCREMENT"), "run_Id is the AUTO_INCREMENT key");
            StringAssert.Contains(run, "PRIMARY KEY (`run_Id`)");
            StringAssert.Contains(run, "KEY `allegiance_gauntlet_run_avatar_idx` (`avatar_Id`, `started_At`)");
            Assert.IsTrue(Regex.IsMatch(run, @"`site_Id`\s+varchar\(64\)\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(run, @"`started_At`\s+datetime\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(run, @"`ended_At`\s+datetime\s+NULL\b"), "an open run has ended_At NULL");
            Assert.IsTrue(Regex.IsMatch(run, @"`end_Reason`\s+tinyint unsigned\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(run, @"`favor_Price`\s+bigint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(run, @"`favor_Committed`\s+tinyint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(run, @"`ranked`\s+tinyint unsigned\s+NOT NULL DEFAULT 1"));
            Assert.AreEqual(2, ShardDatabase.GauntletEndReasonRestart, "spec: end_Reason 2 is Restart");
        }

        [TestMethod]
        public void WaveKey_IsRunWave()
        {
            var wave = TableDdl("allegiance_gauntlet_wave");

            StringAssert.Contains(wave, "PRIMARY KEY (`run_Id`, `wave`)");
            Assert.IsFalse(wave.Contains("AUTO_INCREMENT"));
            Assert.IsTrue(Regex.IsMatch(wave, @"`wave`\s+smallint unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(wave, @"`seconds`\s+float\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(wave, @"`fraction_Centi`\s+tinyint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(wave, @"`family`\s+varchar\(64\)\s+NULL\b"));
            Assert.AreEqual(1, Regex.Matches(wave, @"PRIMARY KEY").Count);
            Assert.AreEqual(0, Regex.Matches(wave, @"UNIQUE KEY").Count);
        }

        [TestMethod]
        public void ClaimKey_IsAccountDate()
        {
            var claim = TableDdl("allegiance_gauntlet_claim");

            // The daily account cap (owner ruling 2026-11-21): the PRIMARY KEY alone refuses a second claim the same day.
            StringAssert.Contains(claim, "PRIMARY KEY (`account_Id`, `claim_Date`)");
            Assert.IsTrue(Regex.IsMatch(claim, @"`claim_Date`\s+date\s+NOT NULL"), "the key is a DATE, so the time of day cannot make two rows distinct");
            Assert.IsTrue(Regex.IsMatch(claim, @"`run_Id`\s+int unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(claim, @"`character_Id`\s+int unsigned\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(claim, @"`claim_Token`\s+char\(32\)\s+CHARACTER SET ascii COLLATE ascii_bin NOT NULL"));
            Assert.AreEqual(0, Regex.Matches(claim, @"UNIQUE KEY").Count, "no second key: one claim per account per day, any run, any character");

            using var ctx = CreateContext();
            var c = ctx.Model.FindEntityType(typeof(AllegianceGauntletClaim));
            CollectionAssert.AreEqual(new[] { "account_Id", "claim_Date" }, c.FindPrimaryKey().Properties.Select(p => p.GetColumnName()).ToArray());
            Assert.AreEqual("date", c.FindProperty("ClaimDate").GetColumnType());
        }

        [TestMethod]
        public void EfModel_MapsColumnNames()
        {
            using var ctx = CreateContext();

            AssertEntity(ctx, typeof(AllegianceGauntletRecord), "allegiance_gauntlet_record", new[] { "avatar_Id" }, new[]
            {
                ("AvatarId", "avatar_Id"), ("BestCenti", "best_Centi"), ("BestParticipants", "best_Participants"),
                ("BestRunId", "best_Run_Id"), ("BestAt", "best_At"), ("LastRunAt", "last_Run_At"),
                ("RunCount", "run_Count"), ("UpdatedAt", "updated_At"),
            });

            AssertEntity(ctx, typeof(AllegianceGauntletRun), "allegiance_gauntlet_run", new[] { "run_Id" }, new[]
            {
                ("RunId", "run_Id"), ("AvatarId", "avatar_Id"), ("MonarchGuid", "monarch_Guid"), ("SiteId", "site_Id"),
                ("StartedAt", "started_At"), ("ActiveAt", "active_At"), ("EndedAt", "ended_At"), ("EndReason", "end_Reason"),
                ("WavesCleared", "waves_Cleared"), ("ScoreCenti", "score_Centi"), ("Participants", "participants"),
                ("Eligible", "eligible"), ("PeakHeadcount", "peak_Headcount"), ("FavorPrice", "favor_Price"),
                ("FavorCommitted", "favor_Committed"), ("CratesClaimed", "crates_Claimed"), ("Ranked", "ranked"),
            });

            AssertEntity(ctx, typeof(AllegianceGauntletWave), "allegiance_gauntlet_wave", new[] { "run_Id", "wave" }, new[]
            {
                ("RunId", "run_Id"), ("Wave", "wave"), ("Level", "level"), ("Headcount", "headcount"),
                ("Planned", "planned"), ("Killed", "killed"), ("Seconds", "seconds"), ("Outcome", "outcome"),
                ("FractionCenti", "fraction_Centi"), ("PlayerDeaths", "player_Deaths"), ("FighterDeaths", "fighter_Deaths"),
                ("Family", "family"),
            });

            AssertEntity(ctx, typeof(AllegianceGauntletClaim), "allegiance_gauntlet_claim", new[] { "account_Id", "claim_Date" }, new[]
            {
                ("AccountId", "account_Id"), ("ClaimDate", "claim_Date"), ("RunId", "run_Id"),
                ("CharacterId", "character_Id"), ("ClaimToken", "claim_Token"), ("ClaimedAt", "claimed_At"),
            });

            var run = ctx.Model.FindEntityType(typeof(AllegianceGauntletRun));
            var avatarIndex = run.GetIndexes().Single(i => i.GetDatabaseName() == "allegiance_gauntlet_run_avatar_idx");
            Assert.IsFalse(avatarIndex.IsUnique);
            CollectionAssert.AreEqual(new[] { "AvatarId", "StartedAt" }, avatarIndex.Properties.Select(p => p.Name).ToArray());
            Assert.IsTrue(run.FindProperty("EndedAt").IsNullable, "an open run has ended_At NULL");
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd, run.FindProperty("RunId").ValueGenerated, "run_Id is AUTO_INCREMENT");

            var record = ctx.Model.FindEntityType(typeof(AllegianceGauntletRecord));
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, record.FindProperty("AvatarId").ValueGenerated, "the record is keyed by the Avatar");

            // Every column the migration declares is mapped, and vice versa.
            foreach (var table in Tables)
            {
                var ddlColumns = Regex.Matches(TableDdl(table), @"(?m)^\s*`([A-Za-z_]+)`\s+[a-z]").Select(m => m.Groups[1].Value).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                var entity = ctx.Model.GetEntityTypes().Single(e => e.GetTableName() == table);
                var efColumns = entity.GetProperties().Select(p => p.GetColumnName()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                CollectionAssert.AreEqual(ddlColumns, efColumns, $"{table}: migration columns [{string.Join(", ", ddlColumns)}] vs EF [{string.Join(", ", efColumns)}]");
            }

            Assert.IsNotNull(ctx.AllegianceGauntletRecord);
            Assert.IsNotNull(ctx.AllegianceGauntletRun);
            Assert.IsNotNull(ctx.AllegianceGauntletWave);
            Assert.IsNotNull(ctx.AllegianceGauntletClaim);
        }

        [TestMethod]
        public void ValidateGauntletRun_RefusesBadRows()
        {
            Assert.IsNotNull(ShardDatabase.ValidateGauntletRun(null));
            Assert.IsNotNull(ShardDatabase.ValidateGauntletRun(new AllegianceGauntletRun { AvatarId = 0, SiteId = "a" }));
            Assert.IsNotNull(ShardDatabase.ValidateGauntletRun(new AllegianceGauntletRun { AvatarId = 1, SiteId = "" }));
            Assert.IsNotNull(ShardDatabase.ValidateGauntletRun(new AllegianceGauntletRun { AvatarId = 1, SiteId = new string('s', 65) }));
            Assert.IsNull(ShardDatabase.ValidateGauntletRun(new AllegianceGauntletRun { AvatarId = 1, SiteId = new string('s', 64) }));
        }

        [TestMethod]
        public void CopyGauntletRun_NormalisesTimestampsToUtcSeconds_AndDetaches()
        {
            var local = new DateTime(2026, 11, 21, 10, 30, 15, DateTimeKind.Local).AddMilliseconds(700);
            var row = new AllegianceGauntletRun { RunId = 7, AvatarId = 3, SiteId = "s", StartedAt = local, EndedAt = local };

            var copy = ShardDatabase.CopyGauntletRun(row);

            Assert.AreNotSame(row, copy);
            Assert.AreEqual(DateTimeKind.Utc, copy.StartedAt.Kind);
            Assert.AreEqual(0, copy.StartedAt.Millisecond, "truncated, never rounded, to the second");
            Assert.AreEqual(local.ToUniversalTime().AddMilliseconds(-700), copy.StartedAt);
            Assert.AreEqual(copy.StartedAt, copy.EndedAt);
            Assert.AreEqual(default(DateTime), ShardDatabase.CopyGauntletRun(new AllegianceGauntletRun()).StartedAt, "a default StartedAt stays default, so the insert can stamp now");

            row.ScoreCenti = 999;
            Assert.AreEqual(0u, copy.ScoreCenti, "the queue's enqueue-time copy is independent of the live row");
        }

        [TestMethod]
        public void NewGauntletClaimToken_Is32Hex_AndFreshEachCall()
        {
            var a = ShardDatabase.NewGauntletClaimToken();
            var b = ShardDatabase.NewGauntletClaimToken();

            Assert.AreEqual(32, a.Length);
            Assert.IsTrue(Regex.IsMatch(a, "^[0-9a-f]{32}$"));
            Assert.AreNotEqual(a, b);
        }

        private static void AssertEntity(ShardDbContext ctx, Type type, string table, string[] key, (string Prop, string Column)[] columns)
        {
            var e = ctx.Model.FindEntityType(type);

            Assert.IsNotNull(e, type.Name);
            Assert.AreEqual(table, e.GetTableName());
            CollectionAssert.AreEqual(key, e.FindPrimaryKey().Properties.Select(p => p.GetColumnName()).ToArray(), $"{table} key");

            foreach (var (prop, column) in columns)
                Assert.AreEqual(column, e.FindProperty(prop)?.GetColumnName(), $"{type.Name}.{prop}");

            Assert.AreEqual(columns.Length, e.GetProperties().Count(), $"{table}: no unmapped extra columns");
        }
    }
}

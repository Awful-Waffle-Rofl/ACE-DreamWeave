using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars;

namespace ACE.Server.Tests
{
    /// <summary>allegiance_avatar and allegiance_avatar_contribution shape - catches an entity/migration mismatch at build time.</summary>
    [TestClass]
    public class AllegianceAvatarSchemaTests
    {
        private const string MigrationFile = "2026-11-20-01-Add-Allegiance-Avatar.sql";

        private const string LookMigrationFile = "2026-11-21-01-Add-Allegiance-Avatar-Look.sql";
        private const string LevelMigrationFile = "2026-11-23-01-Add-Allegiance-Avatar-Level-Xp.sql";
        private const string DailyMigrationFile = "2026-11-25-02-Add-Allegiance-Avatar-Daily-Purchases.sql";

        // Copied from RewardClaimSchemaTests.RepoRoot: walk up from the test output to the repo root.
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

        private static string LookMigration()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", LookMigrationFile));

        private static string Statements()
        {
            var sql = Migration();

            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"(?m)--.*$", string.Empty);

            return sql;
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
        public void Migration_IsIdempotentAndCreatesBothTables()
        {
            var sql = Migration();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `allegiance_avatar`");
            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `allegiance_avatar_contribution`");
            StringAssert.Contains(sql, "UNIQUE KEY `allegiance_avatar_monarch_uidx` (`monarch_Guid`)");

            Assert.IsTrue(Regex.IsMatch(sql, @"`avatar_Id`\s+int unsigned\s+NOT NULL\s+AUTO_INCREMENT"),
                "allegiance_avatar.avatar_Id is the AUTO_INCREMENT key");
            Assert.IsTrue(Regex.IsMatch(sql, @"`monarch_Guid`\s+int unsigned\s+NULL\b"),
                "monarch_Guid must be NULLable: an absorbed or retired Avatar releases its monarch");
            Assert.IsTrue(Regex.IsMatch(sql, @"`state`\s+tinyint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`lifetime_Xp`\s+bigint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`favor`\s+bigint unsigned\s+NOT NULL DEFAULT 0"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`last_Decay_Day`\s+date\s+NULL\b"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`day`\s+date\s+NOT NULL"));

            var statements = Statements();

            Assert.IsFalse(statements.Contains('@'), "no session variables in a shard migration");
            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
            Assert.IsFalse(statements.Contains("FOREIGN KEY"));
            Assert.AreEqual(2, statements.Count(c => c == ';'), "exactly two statements");
        }

        [TestMethod]
        public void Migration_ContributionKeyIsAvatarAccountDay()
        {
            var sql = Migration();

            StringAssert.Contains(sql, "PRIMARY KEY (`avatar_Id`, `account_Id`, `day`)");
            StringAssert.Contains(sql, "KEY `allegiance_avatar_contribution_day_idx` (`avatar_Id`, `day`)");
        }

        [TestMethod]
        public void EfModel_MapsBothTablesWithTheMigrationsColumnNames()
        {
            using var ctx = CreateContext();

            var a = ctx.Model.FindEntityType(typeof(AllegianceAvatar));

            Assert.IsNotNull(a);
            Assert.AreEqual("allegiance_avatar", a.GetTableName());
            CollectionAssert.AreEqual(new[] { "AvatarId" }, a.FindPrimaryKey().Properties.Select(p => p.Name).ToArray());

            var expectedAvatarColumns = new[]
            {
                ("AvatarId", "avatar_Id"), ("MonarchGuid", "monarch_Guid"), ("State", "state"),
                ("LifetimeXp", "lifetime_Xp"), ("LevelXp", "level_Xp"), ("Favor", "favor"), ("HeirGuid", "heir_Guid"),
                ("SeatHouseGuid", "seat_House_Guid"), ("SeatCell", "seat_Cell"), ("SeatInstance", "seat_Instance"),
                ("SeatX", "seat_X"), ("SeatY", "seat_Y"), ("SeatZ", "seat_Z"),
                ("SeatQw", "seat_Qw"), ("SeatQx", "seat_Qx"), ("SeatQy", "seat_Qy"), ("SeatQz", "seat_Qz"),
                ("SeatedAt", "seated_At"), ("AbsorbedIntoId", "absorbed_Into_Id"),
                ("LastContributionAt", "last_Contribution_At"), ("LastDecayDay", "last_Decay_Day"),
                ("LookJson", "look_Json"),
                ("BlessingBoughtAt", "blessing_Bought_At"), ("LastEventAt", "last_Event_At"), ("EventsOnDay", "events_On_Day"),
                ("CreatedAt", "created_At"), ("UpdatedAt", "updated_At"),
            };

            foreach (var (prop, column) in expectedAvatarColumns)
                Assert.AreEqual(column, a.FindProperty(prop)?.GetColumnName(), $"AllegianceAvatar.{prop}");

            Assert.AreEqual(expectedAvatarColumns.Length, a.GetProperties().Count(), "no unmapped extra columns");

            var monarchIndex = a.GetIndexes().Single(i => i.GetDatabaseName() == "allegiance_avatar_monarch_uidx");
            Assert.IsTrue(monarchIndex.IsUnique, "monarch_Guid is UNIQUE: one live Avatar per monarch");
            CollectionAssert.AreEqual(new[] { "MonarchGuid" }, monarchIndex.Properties.Select(p => p.Name).ToArray());
            Assert.IsTrue(a.FindProperty("MonarchGuid").IsNullable, "monarch_Guid is NULL once absorbed or retired");
            Assert.AreEqual("date", a.FindProperty("LastDecayDay").GetColumnType());
            Assert.AreEqual("mediumtext", a.FindProperty("LookJson").GetColumnType());
            Assert.IsTrue(a.FindProperty("LookJson").IsNullable, "look_Json is NULL for the default look");
            Assert.AreEqual("datetime", a.FindProperty("BlessingBoughtAt").GetColumnType());
            Assert.AreEqual("datetime", a.FindProperty("LastEventAt").GetColumnType());
            Assert.IsTrue(a.FindProperty("BlessingBoughtAt").IsNullable, "blessing_Bought_At is NULL until the first purchase");
            Assert.IsTrue(a.FindProperty("LastEventAt").IsNullable, "last_Event_At is NULL until the first private event");
            Assert.IsFalse(a.FindProperty("EventsOnDay").IsNullable, "events_On_Day is NOT NULL DEFAULT 0");

            var c = ctx.Model.FindEntityType(typeof(AllegianceAvatarContribution));

            Assert.IsNotNull(c);
            Assert.AreEqual("allegiance_avatar_contribution", c.GetTableName());

            var key = c.FindPrimaryKey().Properties.Select(p => p.GetColumnName()).ToArray();
            CollectionAssert.AreEqual(new[] { "avatar_Id", "account_Id", "day" }, key);

            Assert.AreEqual("raw_Xp", c.FindProperty("RawXp").GetColumnName());
            Assert.AreEqual("credited", c.FindProperty("Credited").GetColumnName());
            Assert.AreEqual("last_Character_Id", c.FindProperty("LastCharacterId").GetColumnName());
            Assert.AreEqual("date", c.FindProperty("Day").GetColumnType());
            Assert.AreEqual(6, c.GetProperties().Count(), "no unmapped extra columns");

            var dayIndex = c.GetIndexes().Single(i => i.GetDatabaseName() == "allegiance_avatar_contribution_day_idx");
            Assert.IsFalse(dayIndex.IsUnique);
            CollectionAssert.AreEqual(new[] { "AvatarId", "Day" }, dayIndex.Properties.Select(p => p.Name).ToArray());

            Assert.IsNotNull(ctx.AllegianceAvatar);
            Assert.IsNotNull(ctx.AllegianceAvatarContribution);
        }

        /// <summary>
        /// The look migration adds one nullable column after last_Decay_Day, guarded so a second run is a no-op.
        /// CATCHES: the guard simplified to a plain top-level ALTER (throws on the second boot), or the column made
        /// NOT NULL with a default, which would read back as a stored look instead of the default one.
        /// </summary>
        [TestMethod]
        public void LookMigration_AddsANullableColumn_Rerunnably()
        {
            var sql = LookMigration();

            StringAssert.Contains(sql, "ADD COLUMN `look_Json` MEDIUMTEXT NULL ");
            StringAssert.Contains(sql, "AFTER `last_Decay_Day`");
            Assert.IsFalse(sql.Contains("MEDIUMTEXT NOT NULL"), "look_Json must be NULLable: NULL is the default look");

            StringAssert.Contains(sql, "information_schema.COLUMNS",
                "MySQL has no ADD COLUMN IF NOT EXISTS, so the add needs a column-existence guard");
            StringAssert.Contains(sql, "TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'look_Json'");
            StringAssert.Contains(sql, "PREPARE stmt FROM @sql;");
            StringAssert.Contains(sql, "EXECUTE stmt;");
            StringAssert.Contains(sql, "DEALLOCATE PREPARE stmt;");

            Assert.IsFalse(Regex.IsMatch(sql, @"^\s*ALTER TABLE", RegexOptions.Multiline),
                "an unguarded top-level ALTER TABLE throws on the second run");
            Assert.IsFalse(sql.Contains("DELIMITER"), "no stored procedure: the applier's mysql CLI cannot parse it");
            Assert.IsFalse(sql.Contains("DROP"), "a shard migration must never DROP");

            Assert.IsTrue(string.CompareOrdinal(LookMigrationFile, MigrationFile) > 0,
                "the look migration must sort after the migration that creates allegiance_avatar");
        }

        /// <summary>The level pool column: a guarded, re-runnable add of a NOT NULL bigint that defaults to 0 (every existing Avatar starts at level 1).</summary>
        [TestMethod]
        public void LevelMigration_AddsTheNotNullPoolColumn_Rerunnably()
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", LevelMigrationFile));

            Assert.IsTrue(Regex.IsMatch(sql, @"ADD COLUMN `level_Xp` bigint unsigned NOT NULL DEFAULT 0 "));
            StringAssert.Contains(sql, "AFTER `lifetime_Xp`");
            StringAssert.Contains(sql, "information_schema.COLUMNS");
            StringAssert.Contains(sql, "TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'level_Xp'");
            StringAssert.Contains(sql, "PREPARE stmt FROM @sql;");
            Assert.IsFalse(Regex.IsMatch(sql, @"^\s*ALTER TABLE", RegexOptions.Multiline), "an unguarded top-level ALTER TABLE throws on the second run");
            Assert.IsFalse(sql.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsTrue(string.CompareOrdinal(LevelMigrationFile, LookMigrationFile) > 0, "the level migration must sort after the look migration");
        }

        /// <summary>LevelXp travels between the entity and AvatarState in both directions, including the top of the range.</summary>
        [TestMethod]
        public void ShardAvatarStore_maps_LevelXp_both_ways()
        {
            var state = ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 7, LevelXp = 123_456_789_012UL });
            Assert.AreEqual(123_456_789_012UL, state.LevelXp, "FromEntity carries level_Xp");
            Assert.AreEqual(123_456_789_012UL, ShardAvatarStore.ToEntity(state).LevelXp, "ToEntity carries LevelXp");

            var max = ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 8, LevelXp = ulong.MaxValue });
            Assert.AreEqual(ulong.MaxValue, ShardAvatarStore.ToEntity(max).LevelXp);
        }

        /// <summary>LookJson travels verbatim between the entity and AvatarState, in both directions, and NULL stays NULL.</summary>
        [TestMethod]
        public void ShardAvatarStore_maps_LookJson_both_ways()
        {
            const string look = "{\"v\":1,\"body\":{\"heritage\":2},\"pieces\":[]}";

            var state = ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 7, LookJson = look });
            Assert.AreEqual(look, state.LookJson, "FromEntity carries look_Json");

            var back = ShardAvatarStore.ToEntity(state);
            Assert.AreEqual(look, back.LookJson, "ToEntity carries LookJson");

            var unset = ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 8 });
            Assert.IsNull(unset.LookJson, "a NULL column is the default look, not an empty document");
            Assert.IsNull(ShardAvatarStore.ToEntity(unset).LookJson, "the default look writes NULL back");
        }

        /// <summary>
        /// The daily-purchase migration: three guarded, re-runnable adds, in order after look_Json, with the types the entity maps.
        /// CATCHES: a guard dropped (the second boot throws), a NOT NULL datetime (every existing Avatar would read as stamped),
        /// a nullable count, or the file sorting before the table exists.
        /// </summary>
        [TestMethod]
        public void DailyMigration_AddsTheThreeColumns_Rerunnably()
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", DailyMigrationFile));

            StringAssert.Contains(sql, "ADD COLUMN `blessing_Bought_At` datetime NULL ");
            StringAssert.Contains(sql, "ADD COLUMN `last_Event_At` datetime NULL ");
            StringAssert.Contains(sql, "ADD COLUMN `events_On_Day` tinyint unsigned NOT NULL DEFAULT 0 ");
            StringAssert.Contains(sql, "AFTER `look_Json`");
            StringAssert.Contains(sql, "AFTER `blessing_Bought_At`");
            StringAssert.Contains(sql, "AFTER `last_Event_At`");

            foreach (var column in new[] { "blessing_Bought_At", "last_Event_At", "events_On_Day" })
                StringAssert.Contains(sql, $"TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = '{column}'", $"{column} needs its own existence guard");

            Assert.AreEqual(3, Regex.Matches(sql, @"PREPARE stmt FROM @sql;").Count, "one guarded statement per column");
            Assert.AreEqual(3, Regex.Matches(sql, @"EXECUTE stmt;").Count);
            Assert.AreEqual(3, Regex.Matches(sql, @"DEALLOCATE PREPARE stmt;").Count);
            Assert.AreEqual(3, Regex.Matches(sql, @"IF\(@col = 0,").Count, "every ALTER sits behind its count");

            Assert.IsFalse(Regex.IsMatch(sql, @"^\s*ALTER TABLE", RegexOptions.Multiline), "an unguarded top-level ALTER TABLE throws on the second run");
            Assert.IsFalse(sql.Contains("DELIMITER"), "no stored procedure: the applier's mysql CLI cannot parse it");
            Assert.IsFalse(sql.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsTrue(sql.All(c => c < 128), "ASCII only");
            Assert.IsTrue(string.CompareOrdinal(DailyMigrationFile, LevelMigrationFile) > 0, "the daily migration must sort after the level migration");
        }

        /// <summary>The three daily columns travel between the entity and AvatarState both ways; the instants come back as UTC.</summary>
        [TestMethod]
        public void ShardAvatarStore_maps_the_daily_columns_both_ways()
        {
            var bought = new DateTime(2026, 11, 25, 17, 30, 5, DateTimeKind.Unspecified);
            var called = new DateTime(2026, 11, 25, 18, 0, 0, DateTimeKind.Unspecified);

            var state = ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 7, BlessingBoughtAt = bought, LastEventAt = called, EventsOnDay = 3 });

            Assert.AreEqual(bought, state.BlessingBoughtUtc);
            Assert.AreEqual(DateTimeKind.Utc, state.BlessingBoughtUtc.Value.Kind, "EF reads datetime as Unspecified; the store marks it UTC");
            Assert.AreEqual(called, state.LastEventUtc);
            Assert.AreEqual(DateTimeKind.Utc, state.LastEventUtc.Value.Kind);
            Assert.AreEqual((byte)3, state.EventsOnDay);

            var back = ShardAvatarStore.ToEntity(state);
            Assert.AreEqual(bought, back.BlessingBoughtAt, "ToEntity carries blessing_Bought_At");
            Assert.AreEqual(called, back.LastEventAt, "ToEntity carries last_Event_At");
            Assert.AreEqual((byte)3, back.EventsOnDay, "ToEntity carries events_On_Day");

            var never = ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 8 });
            Assert.IsNull(never.BlessingBoughtUtc);
            Assert.IsNull(never.LastEventUtc);
            Assert.AreEqual((byte)0, never.EventsOnDay);
            Assert.IsNull(ShardAvatarStore.ToEntity(never).BlessingBoughtAt, "never bought writes NULL back");
            Assert.IsNull(ShardAvatarStore.ToEntity(never).LastEventAt);
        }
    }
}

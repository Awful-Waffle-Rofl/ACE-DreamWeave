using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CAP audit ledger schema shape - the character_cap_ledger and character_cap_audit tables
    /// created by Database/Updates/Shard/2026-09-10-00-Add-Character-Cap-Ledger.sql.
    ///
    /// Same split as CharacterFacetSchemaTests / AccountMuleFormSchemaTests: these assertions catch
    /// an entity/migration mismatch at build time, and the expensive half (that the SQL applies
    /// twice in a row against a real MySQL) stays a manual step, because ACE.Server.Tests
    /// deliberately has no database.
    /// </summary>
    [TestClass]
    public class CapLedgerSchemaTests
    {
        private const string MigrationFile = "2026-09-10-00-Add-Character-Cap-Ledger.sql";

        /// <summary>Round 2's one-shot incident correction for the two prod characters.</summary>
        private const string CorrectionFile = "2026-09-10-01-Correct-Cap-Available-Rez-Bro.sql";

        /// <summary>The marker row that makes the correction apply exactly once per DATABASE.</summary>
        private const string CorrectionMarker = "correct-cap-available-2026-09-10";

        /// <summary>
        /// CharacterCapLedger's CLR property name -> the quoted column name the migration must
        /// declare. Kept explicit rather than derived, so adding a property to the entity without
        /// adding its column here fails the equivalence assertion below instead of passing silently.
        /// </summary>
        private static readonly Dictionary<string, string> ExpectedLedgerColumns = new Dictionary<string, string>
        {
            { "Id",              "`id`" },
            { "CharacterId",     "`character_Id`" },
            { "CharacterName",   "`character_Name`" },
            { "Ts",              "`ts`" },
            { "Reason",          "`reason`" },
            { "BatchId",         "`batch_Id`" },
            { "DeltaAvailable",  "`delta_Available`" },
            { "DeltaTotal",      "`delta_Total`" },
            { "AvailableAfter",  "`available_After`" },
            { "TotalAfter",      "`total_After`" },
            { "OwnedCostAfter",  "`owned_Cost_After`" },
            { "Ability",         "`ability`" },
            { "RankAfter",       "`rank_After`" },
            { "Detail",          "`detail`" },
        };

        /// <summary>
        /// CharacterCapAudit's CLR property name -> the quoted column name the migration must
        /// declare.
        /// </summary>
        private static readonly Dictionary<string, string> ExpectedAuditColumns = new Dictionary<string, string>
        {
            { "CharacterId",      "`character_Id`" },
            { "CharacterName",    "`character_Name`" },
            { "TotalEarned",      "`total_Earned`" },
            { "Available",        "`available`" },
            { "OwnedCost",        "`owned_Cost`" },
            { "SinkSpend",        "`sink_Spend`" },
            { "Unexplained",      "`unexplained`" },
            { "OrphanRows",       "`orphan_Rows`" },
            { "RankDivergences",  "`rank_Divergences`" },
            { "FirstDetectedAt",  "`first_Detected_At`" },
            { "LastCheckedAt",    "`last_Checked_At`" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string MigrationText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile));

        private static string CorrectionText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", CorrectionFile));

        /// <summary>
        /// The correction script with every /* ... */ block removed, so an assertion about what the
        /// SQL DOES is not satisfied (or broken) by what the header prose says about it. The header
        /// discusses 9018 at length precisely because the script must not write it.
        /// </summary>
        private static string CorrectionBody()
            => System.Text.RegularExpressions.Regex.Replace(CorrectionText(), @"/\*.*?\*/", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);

        private static int Count(string haystack, string needle)
        {
            var n = 0;
            var i = haystack.IndexOf(needle, StringComparison.Ordinal);

            while (i >= 0)
            {
                n++;
                i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
            }

            return n;
        }

        [TestMethod]
        public void Migration_CreatesBothTables_Idempotently()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `character_cap_ledger`");
            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `character_cap_audit`");

            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"CREATE TABLE\s+`"),
                "every CREATE TABLE must be IF NOT EXISTS");
        }

        [TestMethod]
        public void Migration_HasNoForeignKeyOrDangerousConstructs()
        {
            var sql = MigrationText();

            Assert.IsFalse(sql.Contains("FOREIGN KEY"), "neither table takes an FK to character - deliberate, see the migration header");
            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(sql.Contains("DELIMITER"), "no stored-procedure shape - the mysql CLI applier cannot parse it");
            Assert.IsFalse(sql.Contains("CREATE PROCEDURE"), "no stored procedure");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"ADD (COLUMN|INDEX)\s+IF NOT EXISTS", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                "ADD COLUMN/INDEX IF NOT EXISTS is MariaDB-only and must not appear against deployed MySQL 8.0");
        }

        [TestMethod]
        public void Migration_DeclaresEveryExpectedColumn()
        {
            var sql = MigrationText();

            foreach (var pair in ExpectedLedgerColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for CharacterCapLedger.{pair.Key}");

            foreach (var pair in ExpectedAuditColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for CharacterCapAudit.{pair.Key}");
        }

        [TestMethod]
        public void Migration_DeclaresAllThreeExpectedIndexes()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "KEY `character_cap_ledger_character_idx` (`character_Id`, `id`)");
            StringAssert.Contains(sql, "KEY `character_cap_ledger_ts_idx` (`ts`)");
            StringAssert.Contains(sql, "KEY `character_cap_ledger_reason_idx` (`reason`)");
        }

        [TestMethod]
        public void LedgerEntity_ColumnNamesMatchTheMigration_BothDirections()
        {
            var sql = MigrationText();

            var clrProperties = typeof(CharacterCapLedger)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            CollectionAssert.AreEquivalent(ExpectedLedgerColumns.Keys.ToList(), clrProperties,
                "CharacterCapLedger's CLR properties and the expected column map have diverged");

            foreach (var pair in ExpectedLedgerColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for {pair.Key}");
        }

        [TestMethod]
        public void AuditEntity_ColumnNamesMatchTheMigration_BothDirections()
        {
            var sql = MigrationText();

            var clrProperties = typeof(CharacterCapAudit)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            CollectionAssert.AreEquivalent(ExpectedAuditColumns.Keys.ToList(), clrProperties,
                "CharacterCapAudit's CLR properties and the expected column map have diverged");

            foreach (var pair in ExpectedAuditColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for {pair.Key}");
        }

        // ---- Round 2's one-shot incident correction (2026-09-10-01) -----------------------------

        [TestMethod]
        public void Correction_IsGuardedByItsMarkerAndCarriesNoDangerousConstructs()
        {
            var sql = CorrectionText();

            StringAssert.Contains(sql, CorrectionMarker,
                "the correction must be guarded by its ace_shard_migration_marker row - it is NOT idempotent by construction, and re-running it would credit the points twice");

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `ace_shard_migration_marker`",
                "the marker table has to be created here too - this script may be the first to need it on a fresh shard");

            StringAssert.Contains(sql, "START TRANSACTION");
            StringAssert.Contains(sql, "COMMIT;");

            // These two are asserted over the comment-stripped body, not the raw text: the header has
            // to be able to explain WHY there is no delimiter statement, and a test that forbade the
            // word outright would be a test against the documentation rather than against the SQL.
            var body = CorrectionBody();

            Assert.IsFalse(body.Contains("DELIMITER"),
                "no delimiter statement in the executable SQL - the boot patcher sends the whole file as one command and does not support one");

            Assert.IsFalse(body.Contains("DROP TABLE"),
                "a shard migration must never DROP a table");
        }

        [TestMethod]
        public void Correction_CreditsBothCharactersBehindTheNameInterlock()
        {
            var body = CorrectionBody();

            Assert.AreEqual(2, Count(body, "INSERT INTO `biota_properties_int`"),
                "exactly two property writes are expected, one per affected character");

            // The interlock: an INSERT ... SELECT driven off the `character` row, so a wrong id
            // matches nothing and credits nobody rather than crediting a stranger. The un-aliased
            // "AND `name` = '" form appears only on those two statements (the ledger inserts use the
            // aliased `c`.`name`), so counting it counts the interlocked credits.
            Assert.AreEqual(2, Count(body, "AND `name` = '"),
                "each credit must carry its own AND `name` = '...' safety interlock");

            StringAssert.Contains(body, "AND `name` = 'Rez'");
            StringAssert.Contains(body, "AND `name` = 'Bro'");
            StringAssert.Contains(body, "1342177315");
            StringAssert.Contains(body, "1342177612");

            // Covers Rez (no 9017 row - the INSERT branch) and Bro (an existing row - the UPDATE
            // branch) with one statement each, against biota_properties_int's (object_Id, type) PK.
            Assert.AreEqual(2, Count(body, "ON DUPLICATE KEY UPDATE `value` = `value` +"),
                "the credit must be applied as value = value + delta so it is immune to the balance having moved since diagnosis");
        }

        /// <summary>
        /// THE CENTRAL SAFETY PROPERTY, and it is stated as the invariant that actually holds rather
        /// than as the absence of a string, following AccountVaultDaoShapeTests' reasoning about
        /// keyword-presence assertions.
        ///
        /// The naive assertion would be "the file does not contain 9018". That is not achievable AND
        /// correct at the same time: the ledger rows' `total_After` has to be read back from the real
        /// row rather than hardcoded, and that read names 9018. So the assertion is the thing that
        /// matters instead - every mention of 9018 in the executable SQL is a READ inside that
        /// subselect, and none of them is a write. A statement that wrote 9018 would add an occurrence
        /// that does not match the read form and fail the equality below.
        ///
        /// Why it matters: TotalClassAbilityPointsEarned gates Tier 2/3 access through
        /// Player.MeetsClassAbilityTierUnlock, so crediting it would hand both characters tier access
        /// they did not earn on top of the point they are owed. The repo owner was explicit that 9018
        /// is untouched.
        /// </summary>
        [TestMethod]
        public void Correction_Writes9017Only_And9018IsOnlyEverRead()
        {
            var body = CorrectionBody();

            StringAssert.Contains(body, "9017", "the correction must credit AvailableClassAbilityPoints (9017)");

            var total = Count(body, "9018");
            var reads = Count(body, "AND `type` = 9018)");

            Assert.AreEqual(2, total,
                $"expected 9018 to appear exactly twice in the executable SQL (one total_After read per character), found {total}");

            Assert.AreEqual(total, reads,
                "every mention of 9018 must be a READ inside the total_After subselect. TotalClassAbilityPointsEarned " +
                "gates Tier 2/3 unlocks, so this script must never write it.");
        }

        [TestMethod]
        public void Correction_RecordsBothCreditsInTheLedger()
        {
            var body = CorrectionBody();

            Assert.AreEqual(2, Count(body, "INSERT INTO `character_cap_ledger`"),
                "one ledger row per corrected character, so the restore is itself part of the audit trail");

            Assert.AreEqual(2, Count(body, "'admin_correct'"),
                "both ledger rows must carry the admin_correct reason code (CapLedgerReason.AdminCorrect)");

            // available_After / total_After are read back from the actual rows rather than hardcoded,
            // so they stay correct even if the balances moved between diagnosis and application.
            Assert.AreEqual(4, Count(body, "COALESCE((SELECT `value` FROM `biota_properties_int`"),
                "available_After and total_After must each be read back per character, not hardcoded");
        }

        [TestMethod]
        public void ShardDbContext_DeclaresBothDbSets()
        {
            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("CharacterCapLedger", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.CharacterCapLedger DbSet is missing");

            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("CharacterCapAudit", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.CharacterCapAudit DbSet is missing");
        }
    }
}

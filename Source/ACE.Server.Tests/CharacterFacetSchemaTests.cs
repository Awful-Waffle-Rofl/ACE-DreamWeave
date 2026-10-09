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
    /// Player Facets schema shape - the character_facet table, originally created (as character_loadout)
    /// by Database/Updates/Shard/2026-09-05-00-Add-Character-Loadout.sql and renamed to character_facet
    /// by the later, separate 2026-09-06-01-Rename-Character-Loadout-To-Character-Facet.sql. The
    /// original creation migration is never edited (it may already be applied on stage), so this class
    /// still validates the entity's columns, PK and FK-absence against that original file's literal
    /// `character_loadout` table name; only the table's final name changed, not its shape.
    ///
    /// The column checks read the CREATE file CONCATENATED with every later ALTER that adds a column
    /// (currently just 2026-09-06-02-Add-Character-Facet-Attrs.sql, which adds attrs_Json for the
    /// per-facet attribute redistribution). That is an addition to the concatenation, NOT a repointing
    /// of the frozen file above: a column added by a follow-up migration is just as much part of the
    /// table's declared shape, and the entity cannot tell which file declared it. Add the next such
    /// file here rather than editing an applied one.
    ///
    /// Same split as AccountMuleFormSchemaTests: these assertions catch an entity/migration mismatch at
    /// build time, and the expensive half (that the SQL applies twice in a row against a real MySQL)
    /// stays a manual step, because ACE.Server.Tests deliberately has no database.
    /// </summary>
    [TestClass]
    public class CharacterFacetSchemaTests
    {
        /// <summary>
        /// The FROZEN original creation migration. Never repointed - it is merged and may already be
        /// applied on stage, so it is never edited and the assertions below still read its literal
        /// `character_loadout` table name. See this class's summary.
        /// </summary>
        private const string CreateMigrationFile = "2026-09-05-00-Add-Character-Loadout.sql";

        /// <summary>
        /// The later ALTER that adds attrs_Json (per-facet attribute redistribution). A SECOND file in
        /// the concatenation below, not a replacement for the first: a column added by a follow-up
        /// migration lives in a different file from the CREATE, so a single-file read would fail the
        /// Contains assertion for that column while the entity legitimately declares it.
        /// </summary>
        private const string AttrsMigrationFile = "2026-09-06-02-Add-Character-Facet-Attrs.sql";

        /// <summary>
        /// CLR property name -> the quoted column name the migrations must declare. Kept explicit
        /// rather than derived, so adding a property to the entity without adding its column here
        /// fails the equivalence assertion below instead of passing silently.
        /// </summary>
        private static readonly Dictionary<string, string> ExpectedColumns = new Dictionary<string, string>
        {
            { "CharacterId",   "`character_Id`" },
            { "Slot",          "`slot`" },
            { "Name",          "`name`" },
            { "SkillsJson",    "`skills_Json`" },
            { "AbilitiesJson", "`abilities_Json`" },
            { "EquipJson",     "`equip_Json`" },
            { "AttrsJson",     "`attrs_Json`" },
            { "UpdatedAt",     "`updated_At`" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string ShardMigrationText(string file)
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", file));

        /// <summary>
        /// The table's shape as the migrations actually build it: the frozen CREATE plus every later
        /// ALTER that adds a column. A column added by a follow-up file is just as much part of the
        /// declared shape as one in the CREATE, and the entity cannot tell the two apart.
        /// </summary>
        private static string MigrationText()
            => ShardMigrationText(CreateMigrationFile) + "\n" + ShardMigrationText(AttrsMigrationFile);

        [TestMethod]
        public void Migration_CreatesTheTable_Idempotently()
        {
            var sql = MigrationText();

            StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS `character_loadout`", "missing idempotent create for character_loadout (the original table name, renamed to character_facet by a later migration)");

            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"CREATE TABLE\s+`"),
                "every CREATE TABLE must be IF NOT EXISTS");
        }

        [TestMethod]
        public void Migration_DeclaresNoForeignKey()
        {
            // Deliberate, matching character_speed_run and the four account_vault* tables: a row must
            // survive the character being deleted rather than cascading away.
            Assert.IsFalse(MigrationText().Contains("FOREIGN KEY"),
                "character_facet takes no FK to character");
        }

        [TestMethod]
        public void Migration_KeysOnCharacterAndSlot()
        {
            StringAssert.Contains(MigrationText(), "PRIMARY KEY (`character_Id`, `slot`)",
                "one row per character per slot - the composite key is what enforces it");
        }

        [TestMethod]
        public void Entity_ColumnNamesMatchTheMigration()
        {
            var sql = MigrationText();

            var clrProperties = typeof(CharacterFacet)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            CollectionAssert.AreEquivalent(ExpectedColumns.Keys.ToList(), clrProperties,
                "CharacterFacet's CLR properties and the expected column map have diverged");

            foreach (var pair in ExpectedColumns)
                StringAssert.Contains(sql, pair.Value, $"migration does not declare {pair.Value} for {pair.Key}");

            Assert.IsNotNull(typeof(ShardDbContext).GetProperty("CharacterFacet", BindingFlags.Public | BindingFlags.Instance),
                "ShardDbContext.CharacterFacet DbSet is missing");
        }

        /// <summary>
        /// MySQL has no ALTER TABLE ... ADD COLUMN IF NOT EXISTS, so a bare ADD COLUMN would throw on
        /// the second run and stop the boot patcher for anyone who had already applied it. The guard is
        /// an information_schema.COLUMNS count driving a SET / PREPARE / EXECUTE / DEALLOCATE block,
        /// copied from 2026-09-04-00-Add-Account-Vault-Barrel.sql.
        ///
        /// CATCHES: the guard being simplified away to a plain ALTER, which reads as equivalent and is
        /// not. The re-run only stays a no-op because @col is 1 the second time and the prepared
        /// statement degrades to SELECT 1.
        /// </summary>
        [TestMethod]
        public void AttrsMigration_GuardsTheAddColumn_SoItIsRerunnable()
        {
            var sql = ShardMigrationText(AttrsMigrationFile);

            StringAssert.Contains(sql, "information_schema.COLUMNS",
                "the ADD COLUMN must be guarded by a column-existence check - MySQL has no ADD COLUMN IF NOT EXISTS");

            StringAssert.Contains(sql, "COLUMN_NAME = 'attrs_Json'");
            StringAssert.Contains(sql, "TABLE_NAME = 'character_facet'");

            StringAssert.Contains(sql, "PREPARE stmt FROM @sql;");
            StringAssert.Contains(sql, "DEALLOCATE PREPARE stmt;");

            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*ALTER TABLE", System.Text.RegularExpressions.RegexOptions.Multiline),
                "an unguarded top-level ALTER TABLE would throw on the second run; the statement must go through the prepared-statement guard");

            // Not a stored procedure: the mysql CLI the applier uses cannot parse the DELIMITER-based
            // shape 2026-07-10-00-Add-Biota-Position-Instance.sql gets away with only because that file
            // is specifically excluded from CI's import step.
            Assert.IsFalse(sql.Contains("DELIMITER"), "a stored-procedure migration cannot be applied by the CLI");
            Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
        }

        /// <summary>
        /// attrs_Json is the one JSON column on this table that is NULLABLE, and that is load-bearing
        /// rather than an oversight: NULL is how a row written before this column existed says so, and
        /// the apply path has to tell that apart from a real stored arrangement.
        ///
        /// CATCHES: someone "tidying" the column into NOT NULL with a '{}' or '' default to match its
        /// three siblings. That default would read back as a real arrangement summing to zero, which the
        /// conservation check would then refuse - stranding every pre-upgrade slot.
        /// </summary>
        [TestMethod]
        public void AttrsMigration_DeclaresTheColumnNullable()
        {
            var sql = ShardMigrationText(AttrsMigrationFile);

            StringAssert.Contains(sql, "`attrs_Json` text NULL",
                "attrs_Json must be NULLABLE - NULL is the marker for a row that predates per-facet attributes");

            Assert.IsFalse(sql.Contains("`attrs_Json` text NOT NULL"));
        }
    }
}

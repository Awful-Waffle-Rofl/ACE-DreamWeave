/* Realms Phase 4: realm-level strip defaults.

   realm_landblock_rule says what one landblock does in one realm. These two columns say what
   every UNRULED landblock does in a realm, so a realm that wants replacement everywhere (the
   dungeon-mode outdoor copy) is one row edit rather than a per-block manifest.

   Precedence, resolved in WorldDatabaseWithEntityCache:
     realm 0 -> always the base world, these columns are never consulted
     authored landblock_instance_realm rows -> they win
     a realm_landblock_rule row for the block -> ITS flags decide, either way. A row with
       strip_statics = 0 is the punch-through: "inherit retail on this block" even when the
       realm default says strip.
     otherwise -> these defaults
     no realm row at all -> inherit

   BOTH COLUMNS DEFAULT TO 0 AND THIS SCRIPT DELIBERATELY FLIPS NOTHING. Every existing realm
   keeps behaving byte-identically after it runs; turning a realm's default on is content SQL
   and ships separately.
 *
 * RE-RUNNABLE, AND THAT IS REQUIRED. Two different runners execute this file:
 *   - the server's boot patcher (Program_DbUpdates.PatchDatabase), which sends the whole file as one
 *     MySqlCommand - no DELIMITER support, so no stored procedure;
 *   - CI's "Import the real world database" step, `for f in Database/Updates/World/*.sql; do
 *     mysql ... < "$f"; done`, where the CLI splits on the `;` inside a BEGIN/END body without a
 *     DELIMITER change - again no stored procedure.
 * MySQL 8.0 has no ADD COLUMN IF NOT EXISTS, so the guard below is the intersection of the two: a
 * plain SET / PREPARE / EXECUTE around an information_schema check. It needs `@`-variables, which
 * the boot patcher's connection enables via AllowUserVariables=true.
 *
 * The guard is not decoration. applied_updates.txt lives in the BUILD OUTPUT directory while the
 * database is shared, so a second checkout, a wiped bin, or a restored backup all present a database
 * that already carries this change under a ledger that has never seen it. Since #830 a script that
 * throws is (correctly) not recorded, so an unguarded ALTER fails on EVERY boot and blocks every
 * later script in this directory - which is exactly what this file did before the guard.
 *
 * Do NOT reach for the stored-procedure idiom used in
 * Database/Updates/Shard/2026-07-10-00-Add-Biota-Position-Instance.sql. That file can afford a shape
 * the CLI cannot parse only because it is SPECIFICALLY EXCLUDED from CI's import step - read the
 * comment there. The World updates are not excluded, and copying that idiom here has broken CI
 * twice (runs 32428363801 and 32447163145, both "ERROR 1064"). */

SET @strip_statics := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'realm' AND COLUMN_NAME = 'default_strip_statics');
SET @strip_statics_ddl := IF(@strip_statics > 0, 'DO 1', 'ALTER TABLE `realm` ADD COLUMN `default_strip_statics` BIT(1) NOT NULL DEFAULT b''0'' COMMENT ''Default for landblocks in this realm with no realm_landblock_rule row: 1 = load no base landblock_instance content'' AFTER `property_count_randomized`');
PREPARE strip_statics_stmt FROM @strip_statics_ddl;
EXECUTE strip_statics_stmt;
DEALLOCATE PREPARE strip_statics_stmt;

SET @strip_encounters := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'realm' AND COLUMN_NAME = 'default_strip_encounters');
SET @strip_encounters_ddl := IF(@strip_encounters > 0, 'DO 1', 'ALTER TABLE `realm` ADD COLUMN `default_strip_encounters` BIT(1) NOT NULL DEFAULT b''0'' COMMENT ''Default for landblocks in this realm with no realm_landblock_rule row: 1 = spawn no encounters'' AFTER `default_strip_statics`');
PREPARE strip_encounters_stmt FROM @strip_encounters_ddl;
EXECUTE strip_encounters_stmt;
DEALLOCATE PREPARE strip_encounters_stmt;

/* Proving Grounds: Speed - the clock can now start on a lever inside the dungeon instead of on
 * arrival. `start_wcid` on `speed_season` names the wcid of the object whose activation starts the
 * clock; 0 (the default) means the clock starts on arrival, which is the existing behaviour, so
 * every existing row keeps working unchanged with no data edit required.
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

SET @start_wcid := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'speed_season' AND COLUMN_NAME = 'start_wcid');
SET @start_wcid_ddl := IF(@start_wcid > 0, 'DO 1', 'ALTER TABLE `speed_season` ADD COLUMN `start_wcid` int unsigned NOT NULL DEFAULT 0 COMMENT ''Wcid of the object whose activation starts the clock; 0 means the clock starts on arrival'' AFTER `objective_wcid`');
PREPARE start_wcid_stmt FROM @start_wcid_ddl;
EXECUTE start_wcid_stmt;
DEALLOCATE PREPARE start_wcid_stmt;

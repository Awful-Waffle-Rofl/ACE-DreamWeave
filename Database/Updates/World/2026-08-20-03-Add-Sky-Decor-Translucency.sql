/* Sky decor gets a per-cloud opacity dial: `translucency_min` / `translucency_max` on
 * `sky_decor_region`, rolled per cloud (see SkyDecorLayout.TranslucencyRoll) and applied to the
 * spawned disc's PropertyFloat.Translucency (76). 0 = opaque, 1 = fully invisible - the same
 * direction the client already uses for every other translucent object in the world.
 *
 * Both columns default to 0, so every existing row keeps its current fully-opaque look with no data
 * edit required. This migration only adds the knob; it does not turn it.
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

SET @translucency_min := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sky_decor_region' AND COLUMN_NAME = 'translucency_min');
SET @translucency_min_ddl := IF(@translucency_min > 0, 'DO 1', 'ALTER TABLE `sky_decor_region` ADD COLUMN `translucency_min` FLOAT NOT NULL DEFAULT 0 AFTER `min_separation`');
PREPARE translucency_min_stmt FROM @translucency_min_ddl;
EXECUTE translucency_min_stmt;
DEALLOCATE PREPARE translucency_min_stmt;

SET @translucency_max := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sky_decor_region' AND COLUMN_NAME = 'translucency_max');
SET @translucency_max_ddl := IF(@translucency_max > 0, 'DO 1', 'ALTER TABLE `sky_decor_region` ADD COLUMN `translucency_max` FLOAT NOT NULL DEFAULT 0 AFTER `translucency_min`');
PREPARE translucency_max_stmt FROM @translucency_max_ddl;
EXECUTE translucency_max_stmt;
DEALLOCATE PREPARE translucency_max_stmt;

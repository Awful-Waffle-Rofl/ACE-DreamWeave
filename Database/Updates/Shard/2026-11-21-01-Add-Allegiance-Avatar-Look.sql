/* Allegiance Avatar look (/avatar dress).
 *
 * Adds `look_Json` to `allegiance_avatar`: the Avatar's whole look as one versioned JSON document
 * (body properties plus the equipped pieces), written only by a dress or a reset and read once at
 * startup with the rest of the Avatar's state. NULL means the default look, so every Avatar founded
 * before this column existed keeps its default look and a reset stores a body-less look so its cooldown survives a restart.
 *
 * MEDIUMTEXT rather than TEXT: the dress path caps the document at a fixed size well under TEXT's
 * 64 KB, but a cap that is a code constant should not be one schema change away from truncation.
 *
 * This file must sort AFTER 2026-11-20-01-Add-Allegiance-Avatar.sql, which creates the table.
 * applied_updates.txt tracks migrations by FILENAME and they run in filename order.
 *
 * RE-RUNNABLE. The phase 1 file is idempotent through CREATE TABLE IF NOT EXISTS, but MySQL has no
 * ALTER TABLE ... ADD COLUMN IF NOT EXISTS, so the add is guarded by an information_schema.COLUMNS
 * count and executed through the SET / PREPARE / EXECUTE / DEALLOCATE idiom of
 * 2026-09-06-02-Add-Character-Facet-Attrs.sql. After the first successful run the column exists, @col
 * is 1, the prepared statement is a harmless `SELECT 1`, and no ALTER is attempted. No stored
 * procedure: the mysql CLI the applier uses cannot parse that shape.
 *
 * No data change, no FK, no backfill. Every existing row gets NULL.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*.
 */

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'look_Json');

SET @sql := IF(@col = 0,
  'ALTER TABLE `allegiance_avatar` ADD COLUMN `look_Json` MEDIUMTEXT NULL COMMENT ''Versioned JSON look document. NULL = the default look'' AFTER `last_Decay_Day`',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

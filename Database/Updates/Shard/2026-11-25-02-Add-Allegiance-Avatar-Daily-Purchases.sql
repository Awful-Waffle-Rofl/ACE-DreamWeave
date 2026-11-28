/* Allegiance Avatar daily purchases.
 *
 * Adds three columns to `allegiance_avatar` for the once-per-Avatar-day purchases. The Avatar day turns over at
 * allegiance_avatar_day_reset_hour in allegiance_avatar_day_reset_timezone (12:00 AM America/New_York by default,
 * DST-aware); the day itself is computed in the server, never in SQL, so the columns hold plain UTC instants.
 *
 *   blessing_Bought_At  datetime NULL                          UTC instant the Allegiance Blessing was last bought (not yet sold)
 *   last_Event_At       datetime NULL                          UTC instant a private World Event last reached Active
 *   events_On_Day       tinyint unsigned NOT NULL DEFAULT 0    private World Events that reached Active on last_Event_At's
 *                                                              Avatar day; read as 0 once that day has passed
 *
 * This file must sort AFTER 2026-11-20-01-Add-Allegiance-Avatar.sql, which creates the table, and after
 * 2026-11-23-01-Add-Allegiance-Avatar-Level-Xp.sql. applied_updates.txt tracks migrations by FILENAME and they run in
 * filename order. 2026-11-25-01 is taken by an unrelated migration, hence -02.
 *
 * RE-RUNNABLE. MySQL has no ALTER TABLE ... ADD COLUMN IF NOT EXISTS, so each add is guarded by an
 * information_schema.COLUMNS count and executed through the SET / PREPARE / EXECUTE / DEALLOCATE idiom of
 * 2026-11-23-01-Add-Allegiance-Avatar-Level-Xp.sql. After the first successful run each column exists, its count is
 * 1, the prepared statement is a harmless `SELECT 1`, and no ALTER is attempted. No stored procedure: the mysql CLI
 * the applier uses cannot parse that shape.
 *
 * No backfill: every existing Avatar starts with nothing bought and no event called today.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs Database/Updates/*.
 */

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'blessing_Bought_At');

SET @sql := IF(@col = 0,
  'ALTER TABLE `allegiance_avatar` ADD COLUMN `blessing_Bought_At` datetime NULL COMMENT ''Allegiance Avatar: UTC instant the Allegiance Blessing was last bought'' AFTER `look_Json`',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'last_Event_At');

SET @sql := IF(@col = 0,
  'ALTER TABLE `allegiance_avatar` ADD COLUMN `last_Event_At` datetime NULL COMMENT ''Allegiance Avatar: UTC instant a private World Event last reached Active'' AFTER `blessing_Bought_At`',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'events_On_Day');

SET @sql := IF(@col = 0,
  'ALTER TABLE `allegiance_avatar` ADD COLUMN `events_On_Day` tinyint unsigned NOT NULL DEFAULT 0 COMMENT ''Allegiance Avatar: private World Events that reached Active on the Avatar day of last_Event_At'' AFTER `last_Event_At`',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

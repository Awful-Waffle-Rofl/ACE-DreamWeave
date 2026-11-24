/* Allegiance Avatar level pool.
 *
 * Adds `level_Xp` to `allegiance_avatar`: the Avatar's own raw XP pool. Every XP grant a sworn member
 * receives adds allegiance_avatar_level_xp_fraction (default 0.01) of itself to the pool, and the Avatar's
 * level is the player-chart level at that pool total. LifetimeXp (contribution points) keeps accruing for
 * the ledger and Favor but no longer drives the level.
 *
 * This file must sort AFTER 2026-11-20-01-Add-Allegiance-Avatar.sql, which creates the table (and after
 * 2026-11-21-01-Add-Allegiance-Avatar-Look.sql, whose last_Decay_Day / look_Json columns it follows).
 * applied_updates.txt tracks migrations by FILENAME and they run in filename order.
 *
 * RE-RUNNABLE. MySQL has no ALTER TABLE ... ADD COLUMN IF NOT EXISTS, so the add is guarded by an
 * information_schema.COLUMNS count and executed through the SET / PREPARE / EXECUTE / DEALLOCATE idiom of
 * 2026-11-21-01-Add-Allegiance-Avatar-Look.sql. After the first successful run the column exists, @col
 * is 1, the prepared statement is a harmless `SELECT 1`, and no ALTER is attempted. No stored
 * procedure: the mysql CLI the applier uses cannot parse that shape.
 *
 * No backfill: every existing Avatar starts its pool at 0 (level 1).
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*.
 */

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'allegiance_avatar' AND COLUMN_NAME = 'level_Xp');

SET @sql := IF(@col = 0,
  'ALTER TABLE `allegiance_avatar` ADD COLUMN `level_Xp` bigint unsigned NOT NULL DEFAULT 0 COMMENT ''Allegiance Avatar level pool: level_xp_fraction of every XP grant members receive'' AFTER `lifetime_Xp`',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

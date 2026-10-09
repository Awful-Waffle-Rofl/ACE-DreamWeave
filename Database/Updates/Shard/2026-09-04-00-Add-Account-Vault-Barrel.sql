/* Barrel - a player-invoked soft delete for vault items
 * (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6).
 *
 * account_vault.kind marks WHICH KIND of container an index row points at. 0 is an ordinary vault
 * container, 1 is the account's barrel. The barrel is one more container rather than a parallel
 * system, so container creation, the spawn filter and biota loading are all reused - but it must be
 * excluded from every path that iterates the vault list, or barreled items count against capacity
 * and show up in the player's vault.
 *
 * account_vault_barrel is the audit row, and it deliberately OUTLIVES the item: purged_At is
 * stamped when retention destroys the biota, and the row stays so an investigation can still answer
 * "what did this player throw away". No FK to character; name columns are snapshots.
 *
 * Idempotent, timestamps UTC.
 */

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault' AND COLUMN_NAME = 'kind');

SET @sql := IF(@col = 0,
  'ALTER TABLE `account_vault` ADD COLUMN `kind` int NOT NULL DEFAULT 0 COMMENT ''0 vault container, 1 barrel container''',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS `account_vault_barrel` (
  `id`                     int unsigned  NOT NULL AUTO_INCREMENT,
  `account_Id`             int unsigned  NOT NULL,
  `wcid`                   int unsigned  NOT NULL,
  `item_Guid`              int unsigned  NULL COMMENT 'NULL for a ledger barreling, which has no biota',
  `count`                  bigint        NOT NULL COMMENT 'Units barreled',
  `item_Name`              varchar(255)  NOT NULL COMMENT 'Snapshot at barrel time',
  `barreled_At`            datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `actor_Character_Guid`   int unsigned  NOT NULL,
  `actor_Character_Name`   varchar(255)  NOT NULL COMMENT 'Snapshot at barrel time',
  `restored_At`            datetime      NULL COMMENT 'Set when an admin restored it; a restored row is never restorable again',
  `purged_At`              datetime      NULL COMMENT 'Set when retention destroyed the item; the row itself is kept',
  PRIMARY KEY (`id`),
  KEY `account_vault_barrel_account_idx` (`account_Id`),
  KEY `account_vault_barrel_open_idx` (`restored_At`, `purged_At`, `barreled_At`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Barrel - soft-deleted vault items. The row outlives the item on purpose';

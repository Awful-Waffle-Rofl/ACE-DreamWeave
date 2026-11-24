/* Salvage Roller - pick-from-N batch reroll station (Docs/SalvageRoller/DESIGN.md 5.2 and 5.9).
 *
 * character_salvage_batch: one open batch per character, enforced by the UNIQUE key on
 * character_Id. state 0 Escrowing, 1 Pending, 2 Taking. payload is the versioned candidate JSON,
 * NULL until the batch is committed to Pending.
 *
 * character_salvage_batch_item: every object a batch holds in escrow, keyed by its guid. role 0 is
 * the target, 1 a Hammer. A Hammer row is deleted when the Hammer is consumed at commit. The orphan
 * purge (ShardDatabaseOfflineTools.PurgeOrphanedBiotasInParallel) exempts every guid in this table,
 * because a crash between the sell path's detach and the escrow save leaves the item with no
 * parent, and that purge would otherwise delete it.
 *
 * salvage_roller_audit: append-only refund record. Nothing in the feature updates or deletes a
 * row. detail is self-explanatory JSON (full item snapshots, guids, wcids, names, charges, state
 * before and after), so a refund can be worked out from the row alone.
 *
 * The escrow containers themselves are ordinary account_vault rows with kind = 2; that table is
 * not changed here.
 *
 * No FK to character or account, following the account_vault tables: a character purge deletes
 * these rows explicitly (ShardDatabaseOfflineTools.PurgeCharacter) in the same SaveChanges that
 * deletes the character.
 *
 * Idempotent, timestamps UTC (written explicitly by the server; the column defaults are a backstop).
 */

CREATE TABLE IF NOT EXISTS `character_salvage_batch` (
  `id`              bigint unsigned   NOT NULL AUTO_INCREMENT,
  `character_Id`    int unsigned      NOT NULL,
  `account_Id`      int unsigned      NOT NULL COMMENT 'Owner of the escrow store that holds the items',
  `item_Guid`       int unsigned      NOT NULL COMMENT 'The target item, its guid preserved end to end',
  `item_Wcid`       int unsigned      NOT NULL,
  `action`          tinyint unsigned  NOT NULL COMMENT '1 TourmalineReroll, 2 AmethystSwap, 3 ObsidianConvert, 4 ObsidianReroll, 5 SerpentineReroll',
  `state`           tinyint unsigned  NOT NULL DEFAULT 0 COMMENT '0 Escrowing, 1 Pending, 2 Taking',
  `chosen_Seq`      int               NULL COMMENT 'Set when the batch moves to Taking',
  `roll_Count`      int               NOT NULL COMMENT '1..100',
  `created_At`      datetime          NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `payload`         mediumtext        NULL COMMENT 'Versioned candidate JSON, NULL until Pending',
  PRIMARY KEY (`id`),
  UNIQUE KEY `character_salvage_batch_character_uidx` (`character_Id`),
  KEY `character_salvage_batch_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Salvage Roller - one open batch per character';

CREATE TABLE IF NOT EXISTS `character_salvage_batch_item` (
  `guid`            int unsigned      NOT NULL COMMENT 'Escrowed object guid, exempt from the orphan purge',
  `batch_Id`        bigint unsigned   NOT NULL,
  `role`            tinyint unsigned  NOT NULL COMMENT '0 Target, 1 Hammer',
  PRIMARY KEY (`guid`),
  KEY `character_salvage_batch_item_batch_idx` (`batch_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Salvage Roller - objects a batch holds in escrow';

CREATE TABLE IF NOT EXISTS `salvage_roller_audit` (
  `id`              bigint unsigned   NOT NULL AUTO_INCREMENT,
  `batch_Id`        bigint unsigned   NULL COMMENT 'NULL for an event written before the batch row exists',
  `character_Id`    int unsigned      NOT NULL,
  `account_Id`      int unsigned      NOT NULL,
  `character_Name`  varchar(255)      NOT NULL COMMENT 'Snapshot at event time',
  `event`           tinyint unsigned  NOT NULL COMMENT 'SalvageRollerAuditEvent',
  `item_Guid`       int unsigned      NULL,
  `created_At`      datetime          NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `detail`          mediumtext        NOT NULL COMMENT 'Self-explanatory JSON',
  PRIMARY KEY (`id`),
  KEY `salvage_roller_audit_character_idx` (`character_Id`),
  KEY `salvage_roller_audit_batch_idx` (`batch_Id`),
  KEY `salvage_roller_audit_item_idx` (`item_Guid`),
  KEY `salvage_roller_audit_time_idx` (`created_At`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Salvage Roller - append-only refund audit trail';

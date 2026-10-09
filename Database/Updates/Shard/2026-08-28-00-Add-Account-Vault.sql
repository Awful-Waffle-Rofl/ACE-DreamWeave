/* Mule Vendor - per-account private item storage.
 *
 * Four tables, all owned by Docs/MuleVendor/DESIGN.md section 6.
 *
 * `account_vault` is one row per vault CONTAINER. The container itself is an ordinary biota; this
 * table is the account-to-container index, so the store can find every vault an account owns without
 * scanning biotas. `container_Guid` is unique: two rows pointing at one container would let the
 * free-slot search insert into the same vault twice under two identities.
 *
 * `account_vault_stack` is the collapsed-stackable ledger. There is NO biota behind these rows: an
 * item provably identical to its weenie template is destroyed on deposit and re-created on withdraw
 * (DESIGN section 8), which is what lets 10,000 healing kits cost one row instead of 10,000 biotas.
 * `count` is total UNITS held and is deliberately bigint, independent of the item's MaxStackSize.
 *
 * `account_vault_grant` mirrors HousePermission's guid-to-bool shape. `can_Withdraw` false means
 * deposit-only.
 *
 * `account_vault_log` is the audit trail. It is not theft prevention; it is what makes a theft report
 * answerable and a dupe recoverable, and a system whose proposition is "your good gear is safe here"
 * cannot ship without it. Retention is unbounded in v1.
 *
 * No FK to `character` anywhere, deliberate and load-bearing: a vault, a grant, and an audit
 * row must all survive the character being deleted. That is also why every name column is a SNAPSHOT
 * taken at write time rather than a live join. This follows the precedent set by `character_speed_run`.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * All timestamps are UTC. The server always writes them explicitly as DateTime.UtcNow; the
 * CURRENT_TIMESTAMP defaults are backstops only and stamp the DATABASE SERVER'S LOCAL time, so any row
 * that falls back to one is off by the local UTC offset. Always write them explicitly.
 *
 * Idempotent: safe to re-run. Shard updates are tracked by FILENAME against
 * DatabaseSetupScripts/Updates/Shard/applied_updates.txt, with no ledger table, which is exactly why
 * re-runnability is mandatory rather than polite. Only ever adds tables.
 */

CREATE TABLE IF NOT EXISTS `account_vault` (
  `id`              int unsigned  NOT NULL AUTO_INCREMENT,
  `account_Id`      int unsigned  NOT NULL,
  `container_Guid`  int unsigned  NOT NULL COMMENT 'The vault container biota guid. Unique - one row per container',
  `created_At`      datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `account_vault_container_uidx` (`container_Guid`),
  KEY `account_vault_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Mule Vendor - account to vault-container index. No FK to character by design';

CREATE TABLE IF NOT EXISTS `account_vault_stack` (
  `id`          int unsigned  NOT NULL AUTO_INCREMENT,
  `account_Id`  int unsigned  NOT NULL,
  `wcid`        int unsigned  NOT NULL,
  `count`       bigint        NOT NULL COMMENT 'Total UNITS held, independent of the item MaxStackSize',
  PRIMARY KEY (`id`),
  UNIQUE KEY `account_vault_stack_uidx` (`account_Id`, `wcid`),
  KEY `account_vault_stack_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Mule Vendor - collapsed-stackable ledger. No biota behind these rows';

CREATE TABLE IF NOT EXISTS `account_vault_grant` (
  `id`                       int unsigned  NOT NULL AUTO_INCREMENT,
  `owner_Account_Id`         int unsigned  NOT NULL,
  `grantee_Character_Guid`   int unsigned  NOT NULL,
  `grantee_Character_Name`   varchar(255)  NOT NULL COMMENT 'Snapshot at grant time - survives rename/delete',
  `can_Withdraw`             bit(1)        NOT NULL DEFAULT b'0' COMMENT 'FALSE means deposit-only',
  `granted_At`               datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `account_vault_grant_uidx` (`owner_Account_Id`, `grantee_Character_Guid`),
  KEY `account_vault_grant_owner_idx` (`owner_Account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Mule Vendor - sharing grants. Mirrors HousePermission guid-to-bool shape';

CREATE TABLE IF NOT EXISTS `account_vault_log` (
  `id`                     int unsigned  NOT NULL AUTO_INCREMENT,
  `owner_Account_Id`       int unsigned  NOT NULL,
  `actor_Character_Guid`   int unsigned  NOT NULL,
  `actor_Character_Name`   varchar(255)  NOT NULL COMMENT 'Snapshot at action time',
  `action`                 int           NOT NULL COMMENT 'AccountVaultAction: 0 Deposit, 1 Withdraw, 2 Grant, 3 Revoke, 4 Return',
  `wcid`                   int unsigned  NOT NULL,
  `item_Guid`              int unsigned  NULL COMMENT 'NULL for a ledger (collapsed stackable) row, which has no biota',
  `item_Name`              varchar(255)  NOT NULL,
  `count`                  bigint        NOT NULL,
  `timestamp`              datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `account_vault_log_owner_idx` (`owner_Account_Id`),
  KEY `account_vault_log_time_idx` (`timestamp`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Mule Vendor - audit trail. Unbounded retention in v1';

/* Account vault settings - the account_vault_setting table.
 *
 * One row per ACCOUNT holding that account's /mule vault preferences. The first setting is
 * salvage_Combine, which backs `/mule autosalvage on|off`: whether salvage bags deposited into the
 * vault are combined automatically. Later account-wide vault settings become further columns on
 * this same row, which is why the table carries a generic name rather than one per setting.
 *
 * A MISSING ROW MEANS EVERY SETTING IS AT ITS DEFAULT (salvage_Combine ON). That decision belongs to
 * the server's store layer, not to this table: the DAO reports "no row" and the store applies the
 * default. Most accounts will never have a row, because a row is only written when a player changes
 * a setting.
 *
 * `account_Id` is the PRIMARY KEY with no surrogate id and no AUTO_INCREMENT, following
 * `account_bank` and `account_mule_form`: the account IS the identity of the row. Writes are a
 * single INSERT ... ON DUPLICATE KEY UPDATE, so changing a setting is atomic at the row.
 *
 * `salvage_Combine` is bit(1), following account_vault_grant.can_Withdraw: b'1' = ON, b'0' = OFF. The column default matches the store-layer
 * default for a missing row, so a row inserted without the column means the same thing as no row.
 *
 * `set_By_Character_Guid` is a SNAPSHOT of the character that last changed a setting. Audit only,
 * no FK, not read by any behaviour.
 *
 * `updated_At` is UTC, stamped explicitly by the writer (UTC_TIMESTAMP()). The CURRENT_TIMESTAMP
 * default is only a backstop, following account_capacity_upgrade.
 *
 * No FK to `character` or `account`, deliberate, following the account_vault* tables,
 * account_bank and account_mule_form: a setting must survive the character that set it being
 * deleted.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * Idempotent: safe to re-run. Shard updates are tracked by FILENAME against
 * DatabaseSetupScripts/Updates/Shard/applied_updates.txt, with no ledger table, which is exactly why
 * re-runnability is mandatory rather than polite.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*.
 */

CREATE TABLE IF NOT EXISTS `account_vault_setting` (
  `account_Id`             int unsigned  NOT NULL,
  `salvage_Combine`        bit(1)        NOT NULL DEFAULT b'1' COMMENT 'TRUE means combine deposited salvage automatically (default)',
  `set_By_Character_Guid`  int unsigned  NOT NULL COMMENT 'Snapshot of the character that last changed a setting. Audit only, no FK',
  `updated_At`             datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT 'UTC, stamped by the writer',
  PRIMARY KEY (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Account-wide vault settings. One row per account; no row means defaults. No FK by design';

/* Mule Form Token - the account_mule_form table.
 *
 * Owned by Docs/MuleVendor/2026-09-01-mule-form-token-design.md. One row per ACCOUNT recording the
 * creature form that account's /mule vendor wears. A player attunes a Beast Effigy to a monster,
 * lands the killing blow on 100 of them, and uses the completed token; that write lands here, and
 * every later /mule summon on any character of the account reads it.
 *
 * `account_Id` is the PRIMARY KEY with no surrogate id and no AUTO_INCREMENT, following
 * `account_bank`: the account IS the identity of the row, and a second key column would only be
 * another name for it. Writes are a single INSERT ... ON DUPLICATE KEY UPDATE, so replacing a look
 * is atomic at the row.
 *
 * Why a table rather than a property on the vault container: AccountVaultStore owns a LIST of vault
 * containers, created as earlier ones fill, so there is no single biota that could carry an
 * account-wide value, and picking "the oldest" would tie the look to a container the empty-vault
 * reaper may one day remove.
 *
 * `form_Name` and `set_By_Character_Guid` are SNAPSHOTS taken at write time. form_Name is what the
 * donor weenie was called when the look was saved; nothing joins to it and nothing keys on it. It
 * exists so an operator reading this table can tell what a wcid means without a world-database
 * lookup. set_By_Character_Guid is audit only and is not read by any behaviour.
 *
 * No FK to `character` or `account`, deliberate and load-bearing, following the four account_vault*
 * tables and account_bank: the look must survive the character that set it being deleted.
 *
 * `set_Unix_Time` is a double holding seconds since the Unix epoch, matching ACE's own biota
 * timestamps rather than the datetime/CURRENT_TIMESTAMP pattern the vault and bank tables use. It is
 * written explicitly by the server; there is no default, because a row with no time is a bug and
 * should look like one.
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

CREATE TABLE IF NOT EXISTS `account_mule_form` (
  `account_Id`             int unsigned  NOT NULL,
  `form_Wcid`              int unsigned  NOT NULL COMMENT 'WeenieClassId of the donor creature whose body the mule wears',
  `form_Name`              varchar(64)   NOT NULL COMMENT 'Snapshot of the donor name at write time. Operator convenience; nothing keys on it',
  `set_By_Character_Guid`  int unsigned  NOT NULL COMMENT 'Snapshot of the character that set the look. Audit only, no FK',
  `set_Unix_Time`          double        NOT NULL COMMENT 'Seconds since the Unix epoch, written explicitly by the server',
  PRIMARY KEY (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Account-wide mule vendor appearance. One row per account. No FK to character by design';

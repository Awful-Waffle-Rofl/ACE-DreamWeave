/* Mule Vendor - give account_vault_log an index that matches the query it actually serves.
 *
 * `/mule log` runs, once per invocation, on the world tick thread:
 *
 *     WHERE owner_Account_Id = ? ORDER BY timestamp DESC, id DESC LIMIT n
 *
 * (ShardDatabase_AccountVault.GetAccountVaultLog). The table shipped with
 * `account_vault_log_owner_idx (owner_Account_Id)` and `account_vault_log_time_idx (timestamp)` as two
 * SEPARATE indexes, and neither serves that query: MySQL takes the owner index and then filesorts the
 * whole owner partition to satisfy the ORDER BY. The composite below can be walked in reverse and
 * stopped at LIMIT, so the sort disappears.
 *
 * Why this is not a micro-optimisation. Retention is unbounded by design - the table's own COMMENT
 * says "Unbounded retention in v1" - and one row is written per deposited item, so a 40-item deposit
 * writes 40 rows. An active account's partition therefore grows without bound, and the filesort gets
 * slower forever, on a world thread, for a command any player can invoke. The 5-second cooldown on
 * the read commands bounds how OFTEN this runs; it does nothing about how long one run takes.
 *
 * The standalone timestamp index is deliberately KEPT rather than dropped. It has no reader in the
 * server today, but an admin investigating an incident across all accounts wants exactly that shape,
 * and the write cost of one extra index on an append-only audit table is not worth the argument.
 *
 * Idempotent: guards the ADD INDEX behind an information_schema check so re-running the script (a
 * deploy that resets applied_updates.txt, a container without a persisted Config volume, or a manual
 * re-apply) is a no-op instead of failing with "Duplicate key name". MySQL 8.0 has no
 * CREATE INDEX IF NOT EXISTS, so the guard has to be written by hand. This file uses a static-body
 * stored procedure (no client DELIMITER, no @-user-variables) that the server's multi-statement
 * parser runs as one batch - the same shape as 2026-07-10-00-Add-Biota-Position-Instance.sql.
 *
 * The paragraph below is why that shape was chosen, and it is now HISTORY, not a live constraint:
 * PatchDatabase's connection string sets AllowUserVariables=true, so SET / PREPARE / EXECUTE works
 * at boot as well as through the CLI. DELIMITER is still unsupported by both runners. New files
 * should use the SET / PREPARE / EXECUTE guard (see
 * Database/Updates/World/2026-08-27-00-Add-Speed-Season-Start-Wcid.sql), which needs no CI
 * exclusion; this file keeps the procedure because rewriting an applied migration buys nothing.
 *
 * An earlier draft of THIS file used SET @var plus PREPARE/EXECUTE/DEALLOCATE. It ran fine through
 * the `mysql` CLI and threw at boot, because MySqlConnector treats @name as a PARAMETER placeholder
 * rather than a user variable. It failed silently as far as the operator is concerned: PatchDatabase
 * appends the filename to applied_updates.txt whether or not ExecuteScript threw, so a failed
 * migration is marked applied and never retried.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*. Do NOT pipe this file through the `mysql` CLI (`mysql < file.sql`): the CLI
 * splits on the `;` inside the procedure body and needs a DELIMITER change, which the boot patcher
 * neither uses nor allows.
 *
 * This is a separate file rather than an edit to 2026-08-28-00-Add-Account-Vault.sql on purpose: that
 * file creates the table with `CREATE TABLE IF NOT EXISTS`, so on any shard that has already applied
 * it an index added in place would be silently skipped forever.
 */

DROP PROCEDURE IF EXISTS `ace_add_account_vault_log_owner_time_idx`;
CREATE PROCEDURE `ace_add_account_vault_log_owner_time_idx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'account_vault_log'
      AND `INDEX_NAME` = 'account_vault_log_owner_time_idx'
  ) THEN
    ALTER TABLE `account_vault_log` ADD INDEX `account_vault_log_owner_time_idx` (`owner_Account_Id`, `timestamp`);
  END IF;
END;
CALL `ace_add_account_vault_log_owner_time_idx`();
DROP PROCEDURE IF EXISTS `ace_add_account_vault_log_owner_time_idx`;

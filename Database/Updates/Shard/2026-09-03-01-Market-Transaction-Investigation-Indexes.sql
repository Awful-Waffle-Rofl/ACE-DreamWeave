/* Market - composite indexes on market_transaction for the /marketadmin investigation surface.
 *
 * market_transaction shipped with three separate single-column indexes (buyer_Account_Id,
 * seller_Account_Id, timestamp) and none of them serves the query /marketadmin actually runs:
 *
 *     WHERE buyer_Account_Id = ? OR seller_Account_Id = ?  ORDER BY timestamp DESC, id DESC  LIMIT n
 *     WHERE wcid = ?                                       ORDER BY timestamp DESC, id DESC  LIMIT n
 *
 * MySQL takes one of the single-column indexes and then filesorts the whole partition to satisfy
 * the ORDER BY. The composites below can be walked in reverse and stopped at the LIMIT, so the sort
 * disappears. Same reasoning, and the same shape, as
 * 2026-08-28-02-Account-Vault-Log-Index.sql - see that file for the full argument.
 *
 * The existing single-column indexes are deliberately KEPT rather than dropped. market_transaction
 * is append-only and small relative to biota tables, and the write cost of an extra index there is
 * not worth an argument about it.
 *
 * This is a SEPARATE FILE rather than an edit to 2026-08-30-01-Add-Market-Transaction.sql on
 * purpose: that file creates the table with CREATE TABLE IF NOT EXISTS, so on any shard that has
 * already applied it an index added in place would be silently skipped forever.
 *
 * Idempotent: each ADD INDEX is guarded behind an information_schema.STATISTICS check, because
 * MySQL 8.0 has no CREATE INDEX IF NOT EXISTS. The guard uses a static-body stored procedure with
 * no client DELIMITER and no @-user-variables, which is the one form the boot patcher runs
 * correctly - MySqlConnector treats @name as a PARAMETER placeholder, not a user variable.
 *
 * Get this right the first time. This once said a throwing script is recorded anyway and silently
 * skipped forever; that stopped being true at #830 and the correction matters, because the real
 * behaviour is worse in a different direction. A script that throws is NOT recorded in
 * applied_updates.txt and IS retried on the next boot (Program_DbUpdates.cs:635-637 returns Failure
 * with RecordedFileName null; :759-760 appends only when that is non-null) - but the same result
 * carries Stop and :762-766 BREAKS the loop, so every later Shard update script is skipped for that
 * boot too. One unguarded statement here blocks the whole Shard update directory on every boot until
 * a human fixes it, which is why every DDL below sits behind its own information_schema guard.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates), the only path that runs
 * Database/Updates/*. Do NOT pipe this file through the `mysql` CLI: the CLI splits on the `;`
 * inside a procedure body and needs a DELIMITER change the boot patcher neither uses nor allows.
 */

DROP PROCEDURE IF EXISTS `ace_add_market_transaction_buyer_time_idx`;
CREATE PROCEDURE `ace_add_market_transaction_buyer_time_idx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_transaction'
      AND `INDEX_NAME` = 'market_transaction_buyer_time_idx'
  ) THEN
    ALTER TABLE `market_transaction` ADD INDEX `market_transaction_buyer_time_idx` (`buyer_Account_Id`, `timestamp`);
  END IF;
END;
CALL `ace_add_market_transaction_buyer_time_idx`();
DROP PROCEDURE IF EXISTS `ace_add_market_transaction_buyer_time_idx`;

DROP PROCEDURE IF EXISTS `ace_add_market_transaction_seller_time_idx`;
CREATE PROCEDURE `ace_add_market_transaction_seller_time_idx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_transaction'
      AND `INDEX_NAME` = 'market_transaction_seller_time_idx'
  ) THEN
    ALTER TABLE `market_transaction` ADD INDEX `market_transaction_seller_time_idx` (`seller_Account_Id`, `timestamp`);
  END IF;
END;
CALL `ace_add_market_transaction_seller_time_idx`();
DROP PROCEDURE IF EXISTS `ace_add_market_transaction_seller_time_idx`;

DROP PROCEDURE IF EXISTS `ace_add_market_transaction_wcid_time_idx`;
CREATE PROCEDURE `ace_add_market_transaction_wcid_time_idx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_transaction'
      AND `INDEX_NAME` = 'market_transaction_wcid_time_idx'
  ) THEN
    ALTER TABLE `market_transaction` ADD INDEX `market_transaction_wcid_time_idx` (`wcid`, `timestamp`);
  END IF;
END;
CALL `ace_add_market_transaction_wcid_time_idx`();
DROP PROCEDURE IF EXISTS `ace_add_market_transaction_wcid_time_idx`;

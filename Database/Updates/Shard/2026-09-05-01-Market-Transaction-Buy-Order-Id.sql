/* Market - a fill of a Wanted buy order is recorded in market_transaction like a sale
 * (Docs/Market/WANTED-DESIGN.md section 5.2). buy_order_Id names the order; listing_Id stays
 * NOT NULL and a fill row carries 0 there (no listing can have id 0).
 * Guarded through information_schema inside a stored procedure rather than SET/PREPARE, because
 * MarketSchemaTests refuses any session variable in a market migration. Idempotent.
 */

DROP PROCEDURE IF EXISTS `ace_add_market_transaction_buy_order_id`;
CREATE PROCEDURE `ace_add_market_transaction_buy_order_id`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_transaction'
      AND `COLUMN_NAME` = 'buy_order_Id'
  ) THEN
    ALTER TABLE `market_transaction`
      ADD COLUMN `buy_order_Id` int unsigned NULL COMMENT 'Wanted order this row filled; NULL for an ordinary sale' AFTER `listing_Id`;
  END IF;
END;
CALL `ace_add_market_transaction_buy_order_id`();
DROP PROCEDURE IF EXISTS `ace_add_market_transaction_buy_order_id`;

DROP PROCEDURE IF EXISTS `ace_add_market_transaction_buy_order_idx`;
CREATE PROCEDURE `ace_add_market_transaction_buy_order_idx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_transaction'
      AND `INDEX_NAME` = 'market_transaction_buy_order_idx'
  ) THEN
    ALTER TABLE `market_transaction` ADD INDEX `market_transaction_buy_order_idx` (`buy_order_Id`);
  END IF;
END;
CALL `ace_add_market_transaction_buy_order_idx`();
DROP PROCEDURE IF EXISTS `ace_add_market_transaction_buy_order_idx`;

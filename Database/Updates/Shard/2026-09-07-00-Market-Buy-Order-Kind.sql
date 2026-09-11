/* Market - a Wanted buy order may name a salvage HAMMER as well as a salvage bag
 * (Docs/Market/WANTED-DESIGN.md section 5.1, extended). order_Kind is 0 SalvageBag, 1 SalvageHammer
 * (ACE.Server.Managers.Market.MarketBuyOrderKind); every row written before this column existed
 * reads 0, which is what all of them were, so the DEFAULT is 0 and the column is NOT NULL.
 *
 * THE UNIQUE KEY IS REPLACED, NOT ADDED TO. market_buy_order_active_uidx was
 * (buyer_Account_Id, active_Material) and gave one Active order per account and material; it now
 * spans the kind as well, so one buyer may hold an Active Granite BAG order and an Active Granite
 * HAMMER order at the same time and no more than one of each. active_Material stays nullable and
 * NULL except while Active, which is the NULL-is-distinct trick that keeps the key off closed rows -
 * the same emulation market_listing uses for active_Ledger_Wcid.
 *
 * THE DROP AND THE ADD ARE TWO SEPARATE ALTERs, EACH WITH ITS OWN GUARD, and that is deliberate.
 * Combining them into one ALTER that drops and re-adds the SAME index name is the tidier statement
 * and would leave no window without a key, but whether a given server accepts it - rather than
 * rejecting it or refusing to fall back to ALGORITHM=COPY - is not something this migration can
 * verify before it runs on a real shard. The cost of splitting is a sub-second window with no
 * unique key, at boot, before the world opens, on a table that already has the in-memory placement
 * claim in MarketManager_Orders in front of it. The cost of being wrong about the combined form is
 * in the next paragraph, and it is not proportionate.
 *
 * Guarded through information_schema inside stored procedures rather than SET/PREPARE, because
 * MarketSchemaTests refuses any session variable in a market migration. Idempotent in both
 * directions: the column guard keys on the column, the DROP guard on the old key's shape, and the
 * ADD guard on the new key's absence, so a second run is a no-op and an interrupted first run
 * resumes correctly from either half.
 */

DROP PROCEDURE IF EXISTS `ace_add_market_buy_order_kind`;
CREATE PROCEDURE `ace_add_market_buy_order_kind`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_buy_order'
      AND `COLUMN_NAME` = 'order_Kind'
  ) THEN
    ALTER TABLE `market_buy_order`
      ADD COLUMN `order_Kind` tinyint unsigned NOT NULL DEFAULT 0 COMMENT '0 SalvageBag, 1 SalvageHammer' AFTER `material_Type`;
  END IF;
END;
CALL `ace_add_market_buy_order_kind`();
DROP PROCEDURE IF EXISTS `ace_add_market_buy_order_kind`;

/* STEP 2a: drop the OLD key, and only while it is still the old one. The guard is "the index exists
 * AND order_Kind is not one of its columns", so this runs exactly once ever: after step 2b the index
 * exists WITH order_Kind and the condition is false, and on a shard where the key is already absent
 * (rebuilt by hand, or an interrupted earlier run) it is false too and nothing is dropped.
 *
 * EVERY DDL HERE IS GUARDED, and the reason is the boot patcher's failure mode since #830. A script
 * that throws is NOT recorded in applied_updates.txt and IS retried on the next boot
 * (Program_DbUpdates.cs:635-637 returns Failure with RecordedFileName null; :759-760 appends only
 * when that is non-null) - but the same result carries Stop, and :762-766 BREAKS the loop, so every
 * later Shard update script is skipped for that boot as well. An unguarded DROP INDEX against a key
 * that is not there therefore does not just fail itself: it blocks the entire Shard update directory
 * on every boot until a human intervenes.
 */
DROP PROCEDURE IF EXISTS `ace_drop_market_buy_order_active_uidx`;
CREATE PROCEDURE `ace_drop_market_buy_order_active_uidx`()
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_buy_order'
      AND `INDEX_NAME` = 'market_buy_order_active_uidx'
  ) AND NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_buy_order'
      AND `INDEX_NAME` = 'market_buy_order_active_uidx'
      AND `COLUMN_NAME` = 'order_Kind'
  ) THEN
    ALTER TABLE `market_buy_order` DROP INDEX `market_buy_order_active_uidx`;
  END IF;
END;
CALL `ace_drop_market_buy_order_active_uidx`();
DROP PROCEDURE IF EXISTS `ace_drop_market_buy_order_active_uidx`;

/* STEP 2b: add the WIDENED key, whenever it is absent. Guarded on absence alone rather than on
 * "step 2a just ran", so it also covers the shard whose key was never there to begin with - the
 * absent-index case step 2a deliberately leaves alone.
 *
 * This is the only moment the table is without its unique key. It is bounded by the two statements
 * either side of it, runs at boot before the world opens, and the in-memory placement claim in
 * MarketManager_Orders is the defence that actually holds the line against concurrent placements
 * anyway - the key has always been the backstop, never the primary guard (see WANTED-DESIGN 5.1).
 */
DROP PROCEDURE IF EXISTS `ace_add_market_buy_order_active_uidx`;
CREATE PROCEDURE `ace_add_market_buy_order_active_uidx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_buy_order'
      AND `INDEX_NAME` = 'market_buy_order_active_uidx'
  ) THEN
    ALTER TABLE `market_buy_order`
      ADD UNIQUE KEY `market_buy_order_active_uidx` (`buyer_Account_Id`, `active_Material`, `order_Kind`);
  END IF;
END;
CALL `ace_add_market_buy_order_active_uidx`();
DROP PROCEDURE IF EXISTS `ace_add_market_buy_order_active_uidx`;

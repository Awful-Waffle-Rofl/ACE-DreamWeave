/* Market and Mule Vendor - a counted item-CLASS line can be listed on the market and fed to the
 * barrel. Five nullable columns and one unique key, across two tables.
 *
 * market_listing:
 *   1. `class_Key` (char(32) NULL). For a CLASS listing, the listed line's DISPLAY id: 32 lowercase
 *      hex characters, the first 16 bytes of a SHA-256 over VaultCollapse.ClassDisplayGroupKey (the
 *      line's wcid, Structure, workmanship bucket and Name). It is NOT an account_vault_class
 *      class_Key: one drawn line can stand for several of those rows, and which one is the
 *      representative changes as members come and go, so a member key cannot anchor a listing.
 *      NULL for a stored-item listing and for a ledger listing, which keeps both exactly as before.
 *   2. `active_Class_Key` (char(32) NULL). Equals class_Key while the listing is Active, NULL
 *      otherwise - the same NULL-is-distinct emulation of a partial unique index that
 *      active_Item_Guid and active_Ledger_Wcid already use.
 *   3. UNIQUE KEY `market_listing_active_class_uidx` (`seller_Account_Id`, `active_Class_Key`): one
 *      Active listing per seller per class line. A class listing leaves active_Ledger_Wcid NULL, so
 *      a seller may hold a ledger listing of wcid W and class listings of W's lines side by side.
 *
 * account_vault_barrel:
 *   4. `class_Key` (char(32) NULL). For a class barreling, the account_vault_class class_Key of the
 *      MEMBER row the items came off (one barrel row per member). NULL for every other barreling.
 *   5. `canonical_Form` (text NULL). That member's canonical form, so a restore can re-create the
 *      member row if it was reaped to zero in the meantime. The class key is a hash of exactly
 *      this text.
 *   6. `value` (bigint NULL). The summed pooled shares that left the member's total_Value, so a
 *      restore puts back exactly that value. NULL for every non-class barreling.
 *   A class row keeps item_Guid NULL like a ledger row. A restore dispatches on class_Key BEFORE the
 *   null-guid ledger branch, which is what stops a class barreling crediting the stack ledger.
 *
 * ROLLBACK: a server build from before this file reads a class listing as a LEDGER listing of its
 * wcid, and a class barrel row as a ledger barreling. The runbook closes Active class listings
 * before any rollback; see the PR that added this file.
 *
 * Guarded through information_schema inside stored procedures rather than SET/PREPARE, because
 * MarketSchemaTests refuses any session variable in a market migration (same idiom as
 * 2026-09-07-00-Market-Buy-Order-Kind.sql). Every DDL sits behind its own guard, so a second run is
 * a no-op and an interrupted first run resumes from where it stopped. A throwing Shard script stops
 * the whole update loop for that boot, so an unguarded ALTER here would block every later script.
 * No backfill: no existing row is a class listing or a class barreling.
 */

DROP PROCEDURE IF EXISTS `ace_add_market_listing_class_key`;
CREATE PROCEDURE `ace_add_market_listing_class_key`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_listing'
      AND `COLUMN_NAME` = 'class_Key'
  ) THEN
    ALTER TABLE `market_listing`
      ADD COLUMN `class_Key` char(32) NULL COMMENT 'Class listing only: the listed line display id. NULL for a stored item or a ledger stack' AFTER `active_Ledger_Wcid`;
  END IF;
END;
CALL `ace_add_market_listing_class_key`();
DROP PROCEDURE IF EXISTS `ace_add_market_listing_class_key`;

DROP PROCEDURE IF EXISTS `ace_add_market_listing_active_class_key`;
CREATE PROCEDURE `ace_add_market_listing_active_class_key`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_listing'
      AND `COLUMN_NAME` = 'active_Class_Key'
  ) THEN
    ALTER TABLE `market_listing`
      ADD COLUMN `active_Class_Key` char(32) NULL COMMENT 'Equals class_Key while an Active class listing, else NULL' AFTER `class_Key`;
  END IF;
END;
CALL `ace_add_market_listing_active_class_key`();
DROP PROCEDURE IF EXISTS `ace_add_market_listing_active_class_key`;

DROP PROCEDURE IF EXISTS `ace_add_market_listing_active_class_uidx`;
CREATE PROCEDURE `ace_add_market_listing_active_class_uidx`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'market_listing'
      AND `INDEX_NAME` = 'market_listing_active_class_uidx'
  ) THEN
    ALTER TABLE `market_listing`
      ADD UNIQUE KEY `market_listing_active_class_uidx` (`seller_Account_Id`, `active_Class_Key`);
  END IF;
END;
CALL `ace_add_market_listing_active_class_uidx`();
DROP PROCEDURE IF EXISTS `ace_add_market_listing_active_class_uidx`;

DROP PROCEDURE IF EXISTS `ace_add_account_vault_barrel_class_key`;
CREATE PROCEDURE `ace_add_account_vault_barrel_class_key`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'account_vault_barrel'
      AND `COLUMN_NAME` = 'class_Key'
  ) THEN
    ALTER TABLE `account_vault_barrel`
      ADD COLUMN `class_Key` char(32) NULL COMMENT 'Class barreling only: the account_vault_class member key the items came off';
  END IF;
END;
CALL `ace_add_account_vault_barrel_class_key`();
DROP PROCEDURE IF EXISTS `ace_add_account_vault_barrel_class_key`;

DROP PROCEDURE IF EXISTS `ace_add_account_vault_barrel_canonical_form`;
CREATE PROCEDURE `ace_add_account_vault_barrel_canonical_form`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'account_vault_barrel'
      AND `COLUMN_NAME` = 'canonical_Form'
  ) THEN
    ALTER TABLE `account_vault_barrel`
      ADD COLUMN `canonical_Form` text NULL COMMENT 'Class barreling only: the member canonical form, so a restore can re-create a reaped member';
  END IF;
END;
CALL `ace_add_account_vault_barrel_canonical_form`();
DROP PROCEDURE IF EXISTS `ace_add_account_vault_barrel_canonical_form`;

DROP PROCEDURE IF EXISTS `ace_add_account_vault_barrel_value`;
CREATE PROCEDURE `ace_add_account_vault_barrel_value`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'account_vault_barrel'
      AND `COLUMN_NAME` = 'value'
  ) THEN
    ALTER TABLE `account_vault_barrel`
      ADD COLUMN `value` bigint NULL COMMENT 'Class barreling only: the summed pooled shares that left the member total_Value';
  END IF;
END;
CALL `ace_add_account_vault_barrel_value`();
DROP PROCEDURE IF EXISTS `ace_add_account_vault_barrel_value`;

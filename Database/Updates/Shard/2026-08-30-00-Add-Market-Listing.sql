/* Market - active and closed listings (Docs/Market/DESIGN.md section 5.1).
 * A listing is a FLAG, not a move: the item stays in the seller's vault until purchase.
 * active_Item_Guid / active_Ledger_Wcid emulate a partial unique index via NULL-is-distinct.
 * No FK to character - listings must survive character deletion; name columns are snapshots.
 * Idempotent, timestamps UTC, only ever adds a table.
 */

CREATE TABLE IF NOT EXISTS `market_listing` (
  `id`                      int unsigned  NOT NULL AUTO_INCREMENT,
  `seller_Account_Id`       int unsigned  NOT NULL,
  `seller_Character_Guid`   int unsigned  NOT NULL,
  `seller_Character_Name`   varchar(255)  NOT NULL COMMENT 'Snapshot at listing time',
  `item_Guid`               int unsigned  NULL COMMENT 'NULL for a collapsed vault ledger stack',
  `active_Item_Guid`        int unsigned  NULL COMMENT 'Equals item_Guid while Active, else NULL',
  `active_Ledger_Wcid`      int unsigned  NULL COMMENT 'Equals wcid while Active ledger listing, else NULL',
  `wcid`                    int unsigned  NOT NULL,
  `count`                   int           NOT NULL COMMENT 'Listed units; decrements on partial sale',
  `price_Mmd`               bigint        NOT NULL COMMENT 'Per unit, in Trade Note (250,000)',
  `status`                  int           NOT NULL COMMENT '0 Active, 1 Sold, 2 Delisted, 3 Invalidated',
  `created_At`              datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `closed_At`               datetime      NULL,
  `snapshot_Json`           text          NOT NULL COMMENT 'Searchable projection captured at listing time',
  PRIMARY KEY (`id`),
  UNIQUE KEY `market_listing_active_item_uidx` (`active_Item_Guid`),
  UNIQUE KEY `market_listing_active_ledger_uidx` (`seller_Account_Id`, `active_Ledger_Wcid`),
  KEY `market_listing_seller_idx` (`seller_Account_Id`),
  KEY `market_listing_status_idx` (`status`),
  KEY `market_listing_wcid_idx` (`wcid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Market - listings. Flag only; no FK to character by design';

/* Market - transaction history (Docs/Market/DESIGN.md section 5.2).
 * Inserted Pending before any value moves, updated exactly once to a terminal status; also the
 * boot-recovery record for a purchase interrupted mid-flight.
 * No FK to character or market_listing - history must survive both. Names are snapshots.
 * Idempotent, timestamps UTC, only ever adds a table.
 */

CREATE TABLE IF NOT EXISTS `market_transaction` (
  `id`                      int unsigned  NOT NULL AUTO_INCREMENT,
  `listing_Id`              int unsigned  NOT NULL,
  `buyer_Account_Id`        int unsigned  NOT NULL,
  `buyer_Character_Guid`    int unsigned  NOT NULL,
  `buyer_Character_Name`    varchar(255)  NOT NULL COMMENT 'Snapshot at purchase time',
  `seller_Account_Id`       int unsigned  NOT NULL,
  `seller_Character_Guid`   int unsigned  NOT NULL,
  `seller_Character_Name`   varchar(255)  NOT NULL COMMENT 'Snapshot at purchase time',
  `wcid`                    int unsigned  NOT NULL,
  `item_Name`               varchar(255)  NOT NULL,
  `count`                   int           NOT NULL,
  `price_Mmd_Total`         bigint        NOT NULL COMMENT 'count * per-unit price at purchase time',
  `timestamp`               datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `channel`                 int           NOT NULL COMMENT '0 web, 1 ingame',
  `status`                  int           NOT NULL COMMENT '0 Pending, 1 Completed, 2 Refunded, 3 Failed',
  PRIMARY KEY (`id`),
  KEY `market_transaction_buyer_idx` (`buyer_Account_Id`),
  KEY `market_transaction_seller_idx` (`seller_Account_Id`),
  KEY `market_transaction_time_idx` (`timestamp`),
  KEY `market_transaction_status_idx` (`status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Market - purchase history and boot-recovery record. No FK by design';

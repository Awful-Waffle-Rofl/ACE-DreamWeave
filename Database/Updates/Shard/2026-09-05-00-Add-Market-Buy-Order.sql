/* Market - Wanted buy orders (Docs/Market/WANTED-DESIGN.md section 5.1).
 * An order is an escrow-funded standing offer for full salvage bags of one material. escrow_Mmd is
 * the money still held for it; while Active it equals count_Remaining * price_Mmd.
 * active_Material equals material_Type while Active and is NULL otherwise, so the UNIQUE key below
 * gives one Active order per (account, material) via NULL-is-distinct - the same emulation
 * market_listing uses for active_Ledger_Wcid.
 * No FK to character or account - the escrow refund needs the row to outlive both. Names are snapshots.
 * Idempotent, timestamps UTC, only ever adds a table.
 */

CREATE TABLE IF NOT EXISTS `market_buy_order` (
  `id`                      int unsigned  NOT NULL AUTO_INCREMENT,
  `buyer_Account_Id`        int unsigned  NOT NULL,
  `buyer_Character_Guid`    int unsigned  NOT NULL COMMENT 'The IMarketWallet key for the debit and every refund',
  `buyer_Character_Name`    varchar(255)  NOT NULL COMMENT 'Snapshot at placement time',
  `material_Type`           int           NOT NULL COMMENT 'ACE.Entity.Enum.MaterialType value',
  `wcid`                    int unsigned  NOT NULL COMMENT 'The salvage bag wcid for that material at placement, informational',
  `price_Mmd`               bigint        NOT NULL COMMENT 'Per bag, in Trade Note (250,000)',
  `count_Total`             int           NOT NULL,
  `count_Remaining`         int           NOT NULL COMMENT 'Decrements on fill; the order closes Filled at 0',
  `escrow_Mmd`              bigint        NOT NULL COMMENT 'MMD still held; == count_Remaining * price_Mmd while Active',
  `status`                  int           NOT NULL COMMENT '0 Pending, 1 Active, 2 Filled, 3 Cancelled, 4 Expired, 5 Failed, 6 DebitLedgerUnknown',
  `active_Material`         int           NULL COMMENT 'Equals material_Type while Active, else NULL',
  `created_At`              datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `expires_At`              datetime      NOT NULL,
  `closed_At`               datetime      NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `market_buy_order_active_uidx` (`buyer_Account_Id`, `active_Material`),
  KEY `market_buy_order_status_idx` (`status`),
  KEY `market_buy_order_material_idx` (`material_Type`),
  KEY `market_buy_order_expires_idx` (`expires_At`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Market - Wanted buy orders. Escrow-funded; no FK by design';

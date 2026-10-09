/* Account capacity upgrades - the account_capacity_upgrade and account_capacity_upgrade_purchase tables.
 *
 * A player buys permanent, ACCOUNT-WIDE capacity with banked pyreals: /mule upgrade adds mule vault
 * entries and /market upgrade adds active market listing slots. Each kind has its own counter.
 *
 * account_capacity_upgrade holds one row per (account, kind) with how many upgrades of that kind the
 * account owns. upgrade_Kind is ACE.Database.CapacityUpgradeKind (1 = MuleVault, 2 = MarketListings);
 * 0 is never a valid kind. The count moves ONLY inside ShardDatabase.TryPurchaseCapacityUpgrade's
 * transaction, as a guarded `upgrade_Count = upgrade_Count + 1 WHERE upgrade_Count = <expected>`, in the
 * same transaction as the bank debit, so a price quoted against a stale count is refused rather than
 * charged.
 *
 * account_capacity_upgrade_purchase is the audit ledger, one row per purchase. purchase_Token is a
 * per-attempt random value and the PRIMARY KEY: the purchase transaction runs inside EF's retrying
 * execution strategy, which may re-run it after a commit whose acknowledgement was lost, and the first
 * thing a re-run does is look for its own token. Finding it means the purchase already committed.
 * upgrade_Number is the count AFTER the purchase. cost_Mmd and cost_Pyreals are snapshots of the price
 * charged; nothing recomputes them.
 *
 * No FK to account or character, deliberate, following account_bank and account_mule_form: the
 * upgrades belong to the account and must survive the buying character being deleted.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * Idempotent: safe to re-run. Shard updates are tracked by FILENAME only, with no ledger table.
 */

CREATE TABLE IF NOT EXISTS `account_capacity_upgrade` (
  `account_Id`     int unsigned      NOT NULL,
  `upgrade_Kind`   tinyint unsigned  NOT NULL COMMENT 'CapacityUpgradeKind: 1 MuleVault, 2 MarketListings. Never 0',
  `upgrade_Count`  int unsigned      NOT NULL DEFAULT 0 COMMENT 'Upgrades of this kind the account owns',
  `updated_At`     datetime          NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT 'UTC, stamped by the writer',
  PRIMARY KEY (`account_Id`, `upgrade_Kind`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Account-wide capacity upgrade counts. One row per account and kind. No FK by design';

CREATE TABLE IF NOT EXISTS `account_capacity_upgrade_purchase` (
  `purchase_Token`   char(32)          CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'per-attempt token, recognizes a retried purchase',
  `account_Id`       int unsigned      NOT NULL,
  `upgrade_Kind`     tinyint unsigned  NOT NULL COMMENT 'CapacityUpgradeKind',
  `upgrade_Number`   int unsigned      NOT NULL COMMENT 'The count AFTER this purchase',
  `cost_Mmd`         bigint            NOT NULL COMMENT 'Price snapshot in MMD',
  `cost_Pyreals`     bigint            NOT NULL COMMENT 'Pyreals debited from the account bank',
  `character_Guid`   int unsigned      NOT NULL COMMENT 'Buying character. Audit only, no FK',
  `purchased_At`     datetime          NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT 'UTC, stamped by the writer',
  PRIMARY KEY (`purchase_Token`),
  KEY `account_capacity_upgrade_purchase_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Audit ledger of capacity upgrade purchases. No FK by design';

/* Market - rejected listing, delist and purchase attempts (Docs/Market/DESIGN.md section 5.5).
 *
 * Why this table exists. Every refusal in MarketManager.List, MarketManager.Delist and step 0 of
 * MarketManager.Buy returns a MarketResult.Fail and writes NOTHING anywhere: no row, no log line.
 * A player dispute of the form "I tried to buy this and it failed" is therefore uninvestigable -
 * there is no record that the attempt was ever made. This table is that record.
 *
 * WHAT IS DELIBERATELY NOT RECORDED HERE:
 *   - MarketError.Disabled. That is the operator's kill switch being off, not a player event; one
 *     flipped switch would otherwise write a row per click for every player on the shard.
 *   - Transport-layer refusals inside MarketApiHost (bad shared key, expired bearer, rate-limit
 *     429). Those have no resolved actor, so a row would name nobody.
 *   - Anything that reaches the Pending row in market_transaction. Past step 0 the purchase IS
 *     recorded, and market_transaction is the authority on it; duplicating it here would give an
 *     investigator two disagreeing records of one event.
 *
 * reason_Code is the STRING from MarketErrorCodes.ToCode, never the MarketError ordinal: the wire
 * codes are a published contract (Docs/Market/market-api-v1.yaml) while the ordinals are implicit
 * and renumber silently when a member is inserted. The one synthetic value is 'repository_refused',
 * for the AddListing failure at MarketManager.List, where the player is told already_listed but
 * nothing was written either way.
 *
 * No FK to character or market_listing - a rejected attempt must survive both. Names are snapshots.
 *
 * All timestamps are UTC and are written explicitly as DateTime.UtcNow by the server. The
 * CURRENT_TIMESTAMP default is a backstop only and stamps the DATABASE SERVER'S LOCAL time, so any
 * row that falls back to it is off by the local UTC offset.
 *
 * Retention is bounded, unlike account_vault_log: MarketRejectionLog prunes rows older than the
 * market_reject_retention_days tunable (default 30) once an hour.
 *
 * Idempotent, only ever adds a table. Shard updates are tracked by FILENAME against
 * DatabaseSetupScripts/Updates/Shard/applied_updates.txt, and the boot patcher marks a script
 * applied whether or not it threw, so re-runnability is mandatory rather than polite.
 */

CREATE TABLE IF NOT EXISTS `market_rejected_attempt` (
  `id`               int unsigned  NOT NULL AUTO_INCREMENT,
  `operation`        int           NOT NULL COMMENT 'MarketRejectOperation: 0 List, 1 Buy, 2 Delist',
  `reason_Code`      varchar(32)   NOT NULL COMMENT 'MarketErrorCodes.ToCode string, or repository_refused',
  `account_Id`       int unsigned  NOT NULL COMMENT '0 means unresolved',
  `character_Guid`   int unsigned  NOT NULL COMMENT '0 means unknown - the web delist path carries no character',
  `character_Name`   varchar(255)  NOT NULL COMMENT 'Snapshot at attempt time; empty string when unknown',
  `listing_Id`       int unsigned  NULL COMMENT 'NULL when the attempt named no listing',
  `item_Guid`        int unsigned  NULL COMMENT 'NULL for a ledger attempt or when unknown',
  `wcid`             int unsigned  NOT NULL DEFAULT 0,
  `count`            int           NOT NULL DEFAULT 0,
  `price_Mmd`        bigint        NOT NULL DEFAULT 0 COMMENT 'Per unit as the actor asked for it, in Trade Note (250,000)',
  `channel`          int           NOT NULL COMMENT '0 web, 1 ingame - same encoding as market_transaction.channel',
  `timestamp`        datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `detail`           varchar(255)  NULL COMMENT 'Free text for the few codes a bare reason cannot explain',
  PRIMARY KEY (`id`),
  KEY `market_rejected_attempt_account_time_idx` (`account_Id`, `timestamp`),
  KEY `market_rejected_attempt_time_idx` (`timestamp`),
  KEY `market_rejected_attempt_listing_idx` (`listing_Id`),
  KEY `market_rejected_attempt_reason_time_idx` (`reason_Code`, `timestamp`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Market - refused attempts that never reached market_transaction. No FK by design';

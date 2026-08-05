-- ace_analytics schema (monitoring initiative, Docs/Monitoring/DESIGN.md §5).
-- Applied idempotently by AnalyticsDatabase at startup; safe to re-run.
-- This file is embedded in ACE.Server (see ACE.Server.csproj) and is the canonical DDL.

-- Live state, overwritten every flush (current online roster).
CREATE TABLE IF NOT EXISTS `live_roster` (
    `character_id` INT UNSIGNED  NOT NULL,
    `name`         VARCHAR(64)   NOT NULL,
    `level`        INT           NOT NULL,
    `landblock`    INT           NOT NULL,
    `updated_utc`  DATETIME      NOT NULL,
    PRIMARY KEY (`character_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Live per-landblock population (only blocks with players > 0), overwritten every flush.
CREATE TABLE IF NOT EXISTS `live_landblock_pop` (
    `landblock`   INT       NOT NULL,
    `players`     INT       NOT NULL,
    `updated_utc` DATETIME  NOT NULL,
    PRIMARY KEY (`landblock`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-1: append one row per earning character per flush interval. Rates are derived
-- (SUM(xp_gained)/SUM(secs)*3600 over a trailing window). Volume is bounded by online
-- count, not kill rate. `secs` is the ACTUAL elapsed since the last flush.
CREATE TABLE IF NOT EXISTS `char_rate_interval` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `character_id` INT UNSIGNED    NOT NULL,
    `name`         VARCHAR(64)     NOT NULL,
    `ts_utc`       DATETIME        NOT NULL,
    `secs`         DOUBLE          NOT NULL,
    `xp_gained`    BIGINT          NOT NULL,
    `lum_gained`   BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_char_ts` (`character_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-2: item transfers (trades + player-to-player gives). One row per item moved, per
-- direction. `value` is the item's value at transfer time for value-weighted mule detection
-- (aggregate into directed from->to edges over a window). Long-retention audit trail.
CREATE TABLE IF NOT EXISTS `item_flow_event` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `ts_utc`       DATETIME        NOT NULL,
    `kind`         VARCHAR(8)      NOT NULL,   -- 'trade' | 'give'
    `from_id`      INT UNSIGNED    NOT NULL,
    `from_name`    VARCHAR(64)     NOT NULL,
    `to_id`        INT UNSIGNED    NOT NULL,   -- 0 if unknown/NPC
    `to_name`      VARCHAR(64)     NOT NULL,
    `to_is_player` TINYINT(1)      NOT NULL,
    `item_wcid`    INT UNSIGNED    NOT NULL,
    `item_name`    VARCHAR(128)    NOT NULL,
    `stack_size`   INT             NOT NULL,
    `value`        BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_from` (`from_id`, `ts_utc`),
    KEY `ix_to` (`to_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Tier-2: currency transfers (bank transfers). Long-retention audit trail.
CREATE TABLE IF NOT EXISTS `currency_flow_event` (
    `id`        BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `ts_utc`    DATETIME        NOT NULL,
    `kind`      VARCHAR(16)     NOT NULL,   -- 'bank_transfer'
    `from_id`   INT UNSIGNED    NOT NULL,
    `from_name` VARCHAR(64)     NOT NULL,
    `to_id`     INT UNSIGNED    NOT NULL,
    `to_name`   VARCHAR(64)     NOT NULL,
    `currency`  VARCHAR(24)     NOT NULL,
    `amount`    BIGINT          NOT NULL,
    PRIMARY KEY (`id`),
    KEY `ix_ts` (`ts_utc`),
    KEY `ix_from` (`from_id`, `ts_utc`),
    KEY `ix_to` (`to_id`, `ts_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

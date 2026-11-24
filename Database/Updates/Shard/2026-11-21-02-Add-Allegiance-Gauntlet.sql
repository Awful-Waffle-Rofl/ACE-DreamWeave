/* Allegiance Gauntlet - the open-ended wave fight an Allegiance Avatar's members buy with Favor.
 * allegiance_gauntlet_record holds one row per Avatar that has ever committed a run: its best score
 * and the cooldown (last_Run_At, written at commit, so a restart or a crash cannot reset it).
 * allegiance_gauntlet_run holds one row per run, written at start and rewritten with absolute
 * values as the run progresses. A row with ended_At NULL is open; startup closes every open row as
 * end_Reason 2 (Restart). end_Reason: 0 TimeLimit, 1 Aborted, 2 Restart, 3 InstanceLost, 4 Admin.
 * allegiance_gauntlet_wave holds one row per (run, wave), for the tuning pass. outcome: 0 Cleared,
 * 1 TimedOut.
 * allegiance_gauntlet_claim enforces the daily account crate cap (owner ruling 2026-11-21): one
 * crate per account per UTC day, across every allegiance's runs. PRIMARY KEY (account_Id,
 * claim_Date) makes a second claim the same day a 1062. claim_Token is a per-attempt random value
 * that lets the writer recognize its own row when a retried INSERT reports a duplicate against a
 * row it already committed, as in reward_claim.
 * All timestamps are written explicitly in UTC by the DAO; the CURRENT_TIMESTAMP defaults are the
 * database server's local time and are never relied on.
 * No FK, no backfill. Idempotent, only ever adds tables.
 */

CREATE TABLE IF NOT EXISTS `allegiance_gauntlet_record` (
  `avatar_Id`             int unsigned      NOT NULL,
  `best_Centi`            int unsigned      NOT NULL DEFAULT 0,
  `best_Participants`     smallint unsigned NOT NULL DEFAULT 0,
  `best_Run_Id`           int unsigned      NULL,
  `best_At`               datetime          NULL,
  `last_Run_At`           datetime          NULL COMMENT 'the cooldown, written at commit',
  `run_Count`             int unsigned      NOT NULL DEFAULT 0,
  `updated_At`            datetime          NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`avatar_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Allegiance Gauntlet best score and cooldown, one row per Avatar';

CREATE TABLE IF NOT EXISTS `allegiance_gauntlet_run` (
  `run_Id`                int unsigned      NOT NULL AUTO_INCREMENT,
  `avatar_Id`             int unsigned      NOT NULL,
  `monarch_Guid`          int unsigned      NULL COMMENT 'snapshot at start',
  `site_Id`               varchar(64)       NOT NULL,
  `started_At`            datetime          NOT NULL,
  `active_At`             datetime          NULL,
  `ended_At`              datetime          NULL COMMENT 'NULL while the run is open',
  `end_Reason`            tinyint unsigned  NULL COMMENT '0 TimeLimit, 1 Aborted, 2 Restart, 3 InstanceLost, 4 Admin',
  `waves_Cleared`         smallint unsigned NOT NULL DEFAULT 0,
  `score_Centi`           int unsigned      NOT NULL DEFAULT 0,
  `participants`          smallint unsigned NOT NULL DEFAULT 0,
  `eligible`              smallint unsigned NOT NULL DEFAULT 0,
  `peak_Headcount`        smallint unsigned NOT NULL DEFAULT 0,
  `favor_Price`           bigint unsigned   NOT NULL DEFAULT 0,
  `favor_Committed`       tinyint unsigned  NOT NULL DEFAULT 0,
  `crates_Claimed`        smallint unsigned NOT NULL DEFAULT 0,
  `ranked`                tinyint unsigned  NOT NULL DEFAULT 1,
  PRIMARY KEY (`run_Id`),
  KEY `allegiance_gauntlet_run_avatar_idx` (`avatar_Id`, `started_At`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Allegiance Gauntlet run history';

CREATE TABLE IF NOT EXISTS `allegiance_gauntlet_wave` (
  `run_Id`                int unsigned      NOT NULL,
  `wave`                  smallint unsigned NOT NULL,
  `level`                 int unsigned      NOT NULL,
  `headcount`             smallint unsigned NOT NULL,
  `planned`               smallint unsigned NOT NULL,
  `killed`                smallint unsigned NOT NULL,
  `seconds`               float             NOT NULL,
  `outcome`               tinyint unsigned  NOT NULL COMMENT '0 Cleared, 1 TimedOut',
  `fraction_Centi`        tinyint unsigned  NOT NULL DEFAULT 0,
  `player_Deaths`         smallint unsigned NOT NULL DEFAULT 0,
  `fighter_Deaths`        smallint unsigned NOT NULL DEFAULT 0,
  `family`                varchar(64)       NULL,
  PRIMARY KEY (`run_Id`, `wave`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Allegiance Gauntlet wave history, one row per run and wave';

CREATE TABLE IF NOT EXISTS `allegiance_gauntlet_claim` (
  `account_Id`            int unsigned      NOT NULL,
  `claim_Date`            date              NOT NULL COMMENT 'UTC day',
  `run_Id`                int unsigned      NOT NULL,
  `character_Id`          int unsigned      NOT NULL,
  `claim_Token`           char(32)          CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'per-attempt token, recognizes a retried insert',
  `claimed_At`            datetime          NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`account_Id`, `claim_Date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Allegiance Gauntlet daily account crate cap, one row per account per UTC day';

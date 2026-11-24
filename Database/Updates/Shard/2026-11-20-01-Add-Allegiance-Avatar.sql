/* Allegiance Avatar - one persistent Avatar per allegiance, fed by its members' XP.
 * allegiance_avatar holds one row per Avatar ever founded and is never deleted. monarch_Guid is
 * the live binding: UNIQUE so a monarch binds at most one Avatar, and NULL once the Avatar is
 * absorbed into another (state 1, absorbed_Into_Id set) or retired (state 2). InnoDB admits any
 * number of NULLs under a UNIQUE key, so released rows never collide.
 * state: 0 Bound, 1 Absorbed, 2 Retired.
 * The seat_* columns are the Keeper's own position at /avatar seat time, so no per-mansion-model
 * placement table is needed. All seat columns are NULL while the Avatar is unseated.
 * allegiance_avatar_contribution holds one row per (avatar, account, day). Contribution is keyed
 * by account, never by character; last_Character_Id is informational only. raw_Xp is what the
 * account earned that day, credited is what reached the Avatar after the daily curve.
 * No FK, no backfill. Idempotent, only ever adds tables.
 */

CREATE TABLE IF NOT EXISTS `allegiance_avatar` (
  `avatar_Id`             int unsigned     NOT NULL AUTO_INCREMENT,
  `monarch_Guid`          int unsigned     NULL COMMENT 'bound monarch character guid, NULL once absorbed or retired',
  `state`                 tinyint unsigned NOT NULL DEFAULT 0 COMMENT '0 Bound, 1 Absorbed, 2 Retired',
  `lifetime_Xp`           bigint unsigned  NOT NULL DEFAULT 0,
  `favor`                 bigint unsigned  NOT NULL DEFAULT 0,
  `heir_Guid`             int unsigned     NULL,
  `seat_House_Guid`       int unsigned     NULL,
  `seat_Cell`             int unsigned     NULL,
  `seat_Instance`         int unsigned     NULL,
  `seat_X`                float            NULL,
  `seat_Y`                float            NULL,
  `seat_Z`                float            NULL,
  `seat_Qw`               float            NULL,
  `seat_Qx`               float            NULL,
  `seat_Qy`               float            NULL,
  `seat_Qz`               float            NULL,
  `seated_At`             datetime         NULL,
  `absorbed_Into_Id`      int unsigned     NULL COMMENT 'avatar_Id this Avatar was absorbed into',
  `last_Contribution_At`  datetime         NULL,
  `last_Decay_Day`        date             NULL,
  `created_At`            datetime         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_At`            datetime         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`avatar_Id`),
  UNIQUE KEY `allegiance_avatar_monarch_uidx` (`monarch_Guid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Allegiance Avatars, one row per Avatar ever founded';

CREATE TABLE IF NOT EXISTS `allegiance_avatar_contribution` (
  `avatar_Id`             int unsigned     NOT NULL,
  `account_Id`            int unsigned     NOT NULL,
  `day`                   date             NOT NULL,
  `raw_Xp`                bigint unsigned  NOT NULL,
  `credited`              bigint unsigned  NOT NULL,
  `last_Character_Id`     int unsigned     NOT NULL,
  PRIMARY KEY (`avatar_Id`, `account_Id`, `day`),
  KEY `allegiance_avatar_contribution_day_idx` (`avatar_Id`, `day`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Daily per-account XP contribution to an Allegiance Avatar';

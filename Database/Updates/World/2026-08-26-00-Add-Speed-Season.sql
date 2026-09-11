/* Proving Grounds: Speed - season definitions.
 *
 * A row is one content-authored season of the speed challenge: which dungeon it runs in, where the
 * entry portal sends players, which single wcid can finish a run (an altar/lever/pedestal/exit
 * portal flagged with PropertyBool.SpeedChallengeGoal, or a boss creature flagged with
 * PropertyBool.SpeedChallengeBoss), and the window the season is live in.
 *
 * `id` is content-authored, not auto-increment, because it is cached on the player biota as
 * PropertyInt.SpeedChallengeSeasonId (a convenience "which season was this best time set under" cache
 * only - character_speed_run remains the record of truth). Signed int to match PropertyInt.
 *
 * The ACTIVE season is the one row where `starts_at` <= now < `ends_at`. A gap between two seasons'
 * windows means no active season, and the entry portal (PropertyBool.SpeedChallengeEntry) refuses
 * use rather than falling back to a stale season. Overlapping rows are a content error - resolved by
 * picking the row with the latest `starts_at`, with a warning logged when SpeedSeasonManager loads
 * the table at boot.
 *
 * BOTH `starts_at` AND `ends_at` ARE UTC. MySQL `datetime` carries no timezone, and SpeedSeasonManager
 * compares them against DateTime.UtcNow - the same clock ACE.Common's Time.GetUnixTime() uses. Author
 * season rows in UTC. Do NOT write them with MySQL's NOW(), which returns the database server's local
 * time and would shift a rotation by the local UTC offset.
 *
 * `objective_wcid` is the ONLY goal object or boss wcid that may finish a run under this season - see
 * PropertyBool.SpeedChallengeGoal / SpeedChallengeBoss for the gating this enforces.
 *
 * `level_floor` is advisory only in v1: nothing scales enemies to it (see
 * Docs/ProvingGroundsSpeed/DESIGN.md section 12 for the deferred level-scaling phase and the reserved,
 * unused PropertyBool 9045 SpeedChallengeScaledCreature).
 *
 * Idempotent: safe to re-run. Only ever adds the table.
 */

CREATE TABLE IF NOT EXISTS `speed_season` (
  `id`              int             NOT NULL COMMENT 'Content-authored season id, cached on the player biota via PropertyInt.SpeedChallengeSeasonId',
  `name`            varchar(64)     NOT NULL COMMENT 'Season display name',
  `dungeon_name`    varchar(64)     NOT NULL COMMENT 'Dungeon display name shown on the portal and the board header',
  `obj_cell_id`     int unsigned    NOT NULL COMMENT 'Entry position cell',
  `origin_x`        float           NOT NULL,
  `origin_y`        float           NOT NULL,
  `origin_z`        float           NOT NULL,
  `angles_w`        float           NOT NULL,
  `angles_x`        float           NOT NULL,
  `angles_y`        float           NOT NULL,
  `angles_z`        float           NOT NULL,
  `realm_id`        smallint unsigned NOT NULL DEFAULT 0 COMMENT '0 is the base retail world',
  `objective_wcid`  int unsigned    NOT NULL COMMENT 'The only goal object or boss wcid that may finish a run this season',
  `level_floor`     int             NOT NULL DEFAULT 0 COMMENT 'Recommended level floor, advisory only in v1 - v1 does not scale enemies, see DESIGN.md section 12',
  `starts_at`       datetime        NOT NULL,
  `ends_at`         datetime        NOT NULL,
  PRIMARY KEY (`id`),
  KEY `speed_season_window_idx` (`starts_at`, `ends_at`),
  UNIQUE KEY `speed_season_name_uidx` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Proving Grounds Speed season definitions - the active season is starts_at <= NOW() < ends_at';

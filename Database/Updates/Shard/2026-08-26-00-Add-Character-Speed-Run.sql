/* Proving Grounds: Speed - completed run history.
 *
 * Unlike the other three Proving Grounds arenas (survival, wave, DPS), whose scores live as
 * properties on the character biota, the speed board's RECORD OF TRUTH is this table. Each row is
 * one completed speed run. Rows are append-only: never updated and never deleted by gameplay. The
 * in-memory board cache the board command reads from is derived from this table, not the other way
 * around.
 *
 * `character_Name` is a SNAPSHOT taken at the moment the run completes, not a live join to
 * `character`. This is deliberate - a later character rename or character delete must never blank
 * out a historical winner line on the board.
 *
 * No foreign key to `character`, also deliberate: this table must survive the character being
 * deleted, which is the whole reason the name is snapshotted above instead of looked up live.
 *
 * `centiseconds` is elapsed run time in hundredths of a second. LOWER IS BETTER here, the opposite
 * of every other Proving Grounds board.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * `completed_At` IS UTC. The server always writes it explicitly as DateTime.UtcNow, matching the clock
 * ACE.Common's Time.GetUnixTime() uses. The CURRENT_TIMESTAMP column default is a backstop for a row
 * inserted with the value left unset, and it stamps the DATABASE SERVER'S LOCAL time - so any row that
 * falls back to it is off by the local UTC offset. Always write the column explicitly.
 *
 * Idempotent: safe to re-run. Only ever adds the table.
 */

CREATE TABLE IF NOT EXISTS `character_speed_run` (
  `id`               int unsigned    NOT NULL AUTO_INCREMENT,
  `character_Id`     int unsigned    NOT NULL,
  `character_Name`   varchar(255)    NOT NULL COMMENT 'Snapshot at completion time - survives character rename/delete',
  `season_Id`        int             NOT NULL,
  `centiseconds`     bigint          NOT NULL COMMENT 'Elapsed run time in hundredths of a second - LOWER IS BETTER',
  `character_Level`  int             NOT NULL,
  `completed_At`     datetime        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `character_speed_run_season_idx` (`season_Id`, `centiseconds`),
  KEY `character_speed_run_character_idx` (`character_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Proving Grounds Speed completed-run history - the record of truth for the speed board, append-only, no FK to character';

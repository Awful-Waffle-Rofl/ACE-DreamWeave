/* PvP Arena (DreamWeave) - ratings and match history. See Docs/Pvp/DESIGN.md, section "Storage".
 *
 * Three tables:
 *
 * `character_pvp_rating` - one row per (character, ladder). Upserted when a rated match resolves,
 * with ABSOLUTE values the server computed (rating, games, wins/losses/draws, peak), never deltas.
 * Rating decay is worked out when a rating is READ and is written back only by that player's next
 * match, so the stored `rating` can be higher than the value a reader shows. `ladder` is a short
 * code such as 'arena_1v1', 'arena_2v2' or 'arena_ffa'. The (`ladder`, `rating`) index serves the
 * top-N-per-ladder read (ORDER BY `rating` DESC within one ladder).
 *
 * `pvp_match` - append-only, one row per resolved match. Never updated and never deleted by
 * gameplay. `started_At` is NULL for a match that never reached Live (a cancel before the fight).
 * `outcome` and `end_Reason` are readable lowercase codes chosen by the server, not int enums, so
 * the table answers a dispute or dodge audit at a MySQL prompt without the C# enum on hand (the
 * same reasoning as character_cap_ledger.reason).
 *
 * `pvp_match_participant` - append-only, one row per character per match, keyed (`match_Id`,
 * `character_Id`). `placement` is 1 for a winner (FFA survivors share 1). `rating_Before` and
 * `rating_After` are NULL when the match was unrated for that participant. The
 * (`character_Id`, `match_Id`) index serves "this character's match history".
 *
 * The match row and its participant rows are written together with the rating upserts in ONE
 * SaveChanges (ShardDatabase_PvpArena.cs), which is its own implicit transaction, so a match never
 * lands without its participants or its rating changes. There is no database foreign key from
 * `pvp_match_participant`.`match_Id` to `pvp_match`.`id`, matching every other fork-custom shard
 * table (account_vault_stack and friends carry none either). EF still knows the relationship, which
 * is how the participant rows learn the new AUTO_INCREMENT id inside that one SaveChanges.
 *
 * No foreign key to `character` on any table, same reasoning as character_speed_run and
 * character_cap_ledger: a rating row and a match's history must survive the character being
 * deleted. `character_Name` is a SNAPSHOT taken at write time for exactly that reason, so a later
 * rename or delete never blanks out a historical line.
 *
 * Every datetime IS UTC. The server always writes it explicitly (DateTime.UtcNow). There is no
 * CURRENT_TIMESTAMP default on any of them, deliberately: that default stamps the DATABASE
 * SERVER'S LOCAL time, and a NOT NULL column with no default makes a missing value a loud insert
 * error instead of a silently skewed row.
 *
 * Code columns are ascii_bin so key matching is exact and ordinal, matching reward_claim.
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * Idempotent: safe to re-run. Only ever adds the three tables.
 */

CREATE TABLE IF NOT EXISTS `character_pvp_rating` (
  `character_Id`     int unsigned    NOT NULL,
  `ladder`           varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Ladder code, e.g. arena_1v1, arena_2v2, arena_ffa',
  `character_Name`   varchar(255)    NOT NULL COMMENT 'Snapshot at last write - survives character rename/delete',
  `rating`           int             NOT NULL COMMENT 'Stored (undecayed) rating - decay is applied on read',
  `games`            int             NOT NULL DEFAULT 0,
  `wins`             int             NOT NULL DEFAULT 0,
  `losses`           int             NOT NULL DEFAULT 0,
  `draws`            int             NOT NULL DEFAULT 0,
  `peak`             int             NOT NULL COMMENT 'Highest stored rating ever reached on this ladder',
  `last_Match_At`    datetime        NOT NULL COMMENT 'UTC, always written explicitly',
  PRIMARY KEY (`character_Id`, `ladder`),
  KEY `character_pvp_rating_ladder_idx` (`ladder`, `rating`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='PvP arena ratings - one upserted row per character per ladder, no FK to character';

CREATE TABLE IF NOT EXISTS `pvp_match` (
  `id`               int unsigned    NOT NULL AUTO_INCREMENT,
  `mode`             varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Mode key, e.g. arena_1v1',
  `ladder`           varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Ladder code the match was scored on',
  `map`              varchar(64)     NOT NULL COMMENT 'Arena map key the match was fought on',
  `started_At`       datetime        NULL COMMENT 'UTC. NULL when the match never reached Live',
  `ended_At`         datetime        NOT NULL COMMENT 'UTC, always written explicitly',
  `outcome`          varchar(16)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Readable code chosen by the server, e.g. decided, draw, canceled',
  `end_Reason`       varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Readable code chosen by the server, e.g. elimination, timeout, forfeit',
  `rated`            bit(1)          NOT NULL DEFAULT b'0',
  PRIMARY KEY (`id`),
  KEY `pvp_match_ended_idx` (`ended_At`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='PvP arena match history - append-only, one row per resolved match';

CREATE TABLE IF NOT EXISTS `pvp_match_participant` (
  `match_Id`         int unsigned    NOT NULL COMMENT 'pvp_match.id (no FK, see the migration header)',
  `character_Id`     int unsigned    NOT NULL,
  `character_Name`   varchar(255)    NOT NULL COMMENT 'Snapshot at match time - survives character rename/delete',
  `team`             int             NOT NULL,
  `placement`        int             NOT NULL COMMENT '1 = winner, FFA survivors share 1',
  `result`           varchar(16)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Readable code chosen by the server, e.g. win, loss, draw',
  `rating_Before`    int             NULL COMMENT 'NULL when the match was unrated for this participant',
  `rating_After`     int             NULL COMMENT 'NULL when the match was unrated for this participant',
  `kills`            int             NOT NULL DEFAULT 0,
  `deaths`           int             NOT NULL DEFAULT 0,
  `forfeit_Reason`   varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NULL COMMENT 'NULL unless this participant forfeited, e.g. logout, left, command',
  PRIMARY KEY (`match_Id`, `character_Id`),
  KEY `pvp_match_participant_character_idx` (`character_Id`, `match_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='PvP arena match participants - append-only, one row per character per match, no FK to character';

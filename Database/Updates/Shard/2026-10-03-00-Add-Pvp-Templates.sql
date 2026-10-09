/* PvP Template Facets - template storage, the per-participant template stamp, and the one-time arena
 * rating reset. See Docs/Pvp/TEMPLATES.md, "Data model".
 *
 * Four changes, all re-runnable:
 *
 *   1. `pvp_template` - one row per template key. `definition_Json` is the parsed-and-reserialized
 *      PvpTemplateDefinition (schema-versioned JSON, ACE.Server.Pvp.Templates.PvpTemplateJson), taken
 *      by /pvptemplate snapshot from an OFFLINE character on the pvp_template_account. `version` is
 *      bumped by every snapshot of the same key. `modes` is a comma-separated list of mode keys
 *      (arena_1v1, arena_2v2, arena_ffa) offering the template; empty offers it nowhere. `enabled` is
 *      the admin switch. A match never reads this table after dispatch: the coordinator freezes the
 *      parsed definition onto each participant's binding, so a re-snapshot never changes a match in
 *      progress.
 *
 *   2. `pvp_template_history` - append-only, one row per (key, version) ever snapshotted, so a
 *      dispute about "which build was that" can be answered from the stamp on the participant row.
 *      Nothing in gameplay updates or deletes a row.
 *
 *   3. `pvp_match_participant` gains `template_Key` and `template_Version` (both NULL for every match
 *      played before templates, and for any row written without one). Each is a guarded
 *      information_schema check plus PREPARE/EXECUTE, the idiom of
 *      Database/Updates/Shard/2026-09-24-01-Account-Vault-Log-Value-And-Class.sql: MySQL 8.0 has no
 *      ADD COLUMN IF NOT EXISTS, and Program_DbUpdates stops the whole patch pass at a throwing script.
 *
 *   4. ONE-TIME arena rating reset (owner ruling 2026-10-03: "Arena ratings reset once when templates
 *      go live"). Every `character_pvp_rating` row on the three arena ladders is deleted. It must run
 *      exactly once, and the applied-updates ledger alone cannot promise that: a fresh server bin
 *      starts with an empty ledger and would replay this file. So the reset is keyed on the SCHEMA
 *      instead - it runs only while `pvp_match_participant`.`template_Key` does not exist yet, and it
 *      runs BEFORE change 3 adds that column. The first run deletes and then adds the column; every
 *      later run sees the column and deletes nothing. If the script dies between the two, the rerun
 *      deletes again, which is still correct (no match can be rated in between without the column).
 *      Match history (`pvp_match`, `pvp_match_participant`) is kept: only the ladders restart.
 *
 * Every datetime is UTC, written explicitly by the server. No CURRENT_TIMESTAMP default, for the reason
 * the 2026-09-25-02-Add-Pvp-Arena.sql header gives. Key and mode columns are ascii_bin so matching is
 * exact and ordinal. No foreign keys, matching every other fork-custom shard table.
 *
 * THE SERVER BOOTS WITHOUT THIS MIGRATION. ShardDatabase_PvpTemplates.cs treats a missing table as
 * "no templates" (template features refuse, nothing throws), and the participant stamp is written by a
 * separate guarded UPDATE that tolerates the missing columns, so an unmigrated shard still saves
 * matches exactly as before.
 */

CREATE TABLE IF NOT EXISTS `pvp_template` (
  `template_Key`          varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Template key, lowercase [a-z0-9_-]',
  `display_Name`          varchar(64)     NOT NULL COMMENT 'Shown beside a player name on results, the announcement and /top',
  `source_Character_Id`   int unsigned    NOT NULL COMMENT 'Character the definition was snapshotted from (no FK)',
  `source_Character_Name` varchar(255)    NOT NULL COMMENT 'Snapshot of that character name at snapshot time',
  `version`               int unsigned    NOT NULL COMMENT 'Bumped by every snapshot of this key, matching pvp_template_history.version',
  `enabled`               bit(1)          NOT NULL DEFAULT b'0',
  `modes`                 varchar(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT '' COMMENT 'Comma-separated mode keys offering this template',
  `definition_Json`       mediumtext      NOT NULL COMMENT 'Schema-versioned PvpTemplateDefinition JSON',
  `snapshot_At`           datetime        NOT NULL COMMENT 'UTC, always written explicitly',
  `snapshot_By`           varchar(255)    NOT NULL COMMENT 'Admin who ran the snapshot',
  PRIMARY KEY (`template_Key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='PvP template facets - one row per template key, no FK to character';

CREATE TABLE IF NOT EXISTS `pvp_template_history` (
  `template_Key`          varchar(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `version`               int unsigned    NOT NULL,
  `definition_Json`       mediumtext      NOT NULL,
  `snapshot_At`           datetime        NOT NULL COMMENT 'UTC, always written explicitly',
  `snapshot_By`           varchar(255)    NOT NULL,
  PRIMARY KEY (`template_Key`, `version`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='PvP template facets - append-only history, one row per snapshot';

-- 4. The one-time rating reset. Keyed on the ABSENCE of the column change 3 adds; see the header.
SET @pvpt_has_key := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pvp_match_participant' AND COLUMN_NAME = 'template_Key');
SET @pvpt_has_ratings := (SELECT COUNT(*) FROM information_schema.TABLES
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_pvp_rating');
SET @pvpt_reset_ddl := IF(@pvpt_has_key > 0 OR @pvpt_has_ratings = 0, 'DO 1',
  'DELETE FROM `character_pvp_rating` WHERE `ladder` IN (''arena_1v1'', ''arena_2v2'', ''arena_ffa'')');
PREPARE pvpt_reset_stmt FROM @pvpt_reset_ddl;
EXECUTE pvpt_reset_stmt;
DEALLOCATE PREPARE pvpt_reset_stmt;

-- 3. The participant stamp columns. Guarded per column, and skipped entirely on a shard that has no
-- pvp_match_participant table yet (the arena migration has not run), where there is nothing to alter.
SET @pvpt_has_participant := (SELECT COUNT(*) FROM information_schema.TABLES
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pvp_match_participant');

SET @pvpt_key_ddl := IF(@pvpt_has_key > 0 OR @pvpt_has_participant = 0, 'DO 1',
  'ALTER TABLE `pvp_match_participant` ADD COLUMN `template_Key` varchar(32) CHARACTER SET ascii COLLATE ascii_bin NULL COMMENT ''Template the participant fought on. NULL before templates''');
PREPARE pvpt_key_stmt FROM @pvpt_key_ddl;
EXECUTE pvpt_key_stmt;
DEALLOCATE PREPARE pvpt_key_stmt;

SET @pvpt_has_version := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pvp_match_participant' AND COLUMN_NAME = 'template_Version');
SET @pvpt_version_ddl := IF(@pvpt_has_version > 0 OR @pvpt_has_participant = 0, 'DO 1',
  'ALTER TABLE `pvp_match_participant` ADD COLUMN `template_Version` int unsigned NULL COMMENT ''pvp_template_history.version the participant fought on. NULL before templates''');
PREPARE pvpt_version_stmt FROM @pvpt_version_ddl;
EXECUTE pvpt_version_stmt;
DEALLOCATE PREPARE pvpt_version_stmt;

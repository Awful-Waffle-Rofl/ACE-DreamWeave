/* Character Sheet - public link slugs (Docs/CharacterSheet/DESIGN.md section 4.1).
 * A row means the character's sheet is public. Delete = hidden. New slug = rotated.
 * No FK to character: the read path refuses deleted characters itself.
 * Idempotent, timestamps UTC, only ever adds a table.
 */

CREATE TABLE IF NOT EXISTS `character_sheet_link` (
  `character_Id`  int unsigned  NOT NULL,
  `slug`          varchar(16)   CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT '10 chars base62, random, never derived from the guid',
  `created_At`    datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`character_Id`),
  UNIQUE KEY `character_sheet_link_slug_uidx` (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Character Sheet - opt-in public link per character';

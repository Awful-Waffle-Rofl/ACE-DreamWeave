/* ACRealms port Phase 2: the realm registry table (schema matches ACRealms' `realm`
   table for forward compatibility). Rulesets are stubbed in this port, so the
   ruleset-link and realm-properties tables are deliberately NOT created.
   Realm 0 is the implicit base world and needs no row. */

CREATE TABLE IF NOT EXISTS `realm` (
  `id` smallint unsigned NOT NULL DEFAULT 0 COMMENT 'Unique Realm Id within the Shard',
  `type` smallint unsigned NOT NULL,
  `name` text NOT NULL COMMENT 'Name of this realm',
  `parent_realm_id` smallint unsigned NULL,
  `property_count_randomized` smallint unsigned NULL COMMENT 'Reserved for rulesets (unused while rulesets are stubbed)',
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Dynamic Realm of a Shard/World';

/* Realms Phase 3: per-realm content overrides.
   Mirrors landblock_instance / landblock_instance_link with a leading realm_id.
   When a realm has rows for a landblock, they REPLACE the base rows for landblocks
   loaded in that realm's instances; otherwise base content loads unchanged.
   Kept as separate tables so a world-database release never MERGES with fork content. An upstream
   reimport DOES drop them, though: it recreates the entire ace_world schema, which took stage down
   on 2026-09-17. That is why the boot path clears the World applied-updates ledger after a reimport,
   so this migration re-runs and rebuilds these tables in the same boot. */

CREATE TABLE IF NOT EXISTS `landblock_instance_realm` (
  `realm_id` smallint unsigned NOT NULL COMMENT 'Realm this content belongs to',
  `guid` int unsigned NOT NULL COMMENT 'Unique Id of this Instance (allocate top-down from the landblock static band; never reuse a base-content guid)',
  `landblock` int GENERATED ALWAYS AS ((`obj_Cell_Id` >> 16)) VIRTUAL,
  `weenie_Class_Id` int unsigned NOT NULL COMMENT 'Weenie Class Id of object to spawn',
  `obj_Cell_Id` int unsigned NOT NULL,
  `origin_X` float NOT NULL,
  `origin_Y` float NOT NULL,
  `origin_Z` float NOT NULL,
  `angles_W` float NOT NULL,
  `angles_X` float NOT NULL,
  `angles_Y` float NOT NULL,
  `angles_Z` float NOT NULL,
  `is_Link_Child` bit(1) NOT NULL COMMENT 'Is this a child link for any other instances?',
  `last_Modified` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`realm_id`,`guid`),
  KEY `realm_instance_landblock_idx` (`realm_id`,`landblock`),
  CONSTRAINT `realm_instance_realm` FOREIGN KEY (`realm_id`) REFERENCES `realm` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Per-realm Weenie Instances for each Landblock';

CREATE TABLE IF NOT EXISTS `landblock_instance_link_realm` (
  `realm_id` smallint unsigned NOT NULL COMMENT 'Realm this link belongs to',
  `parent_GUID` int unsigned NOT NULL COMMENT 'GUID of parent instance',
  `child_GUID` int unsigned NOT NULL COMMENT 'GUID of child instance',
  `last_Modified` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`realm_id`,`parent_GUID`,`child_GUID`),
  KEY `realm_child_idx` (`realm_id`,`child_GUID`),
  CONSTRAINT `realm_instance_link` FOREIGN KEY (`realm_id`,`parent_GUID`) REFERENCES `landblock_instance_realm` (`realm_id`,`guid`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Per-realm Weenie Instance Links';

/* Realms Phase 4: per-(realm, landblock) content rules.
   A row is a manifest entry saying "in realm R, landblock L does NOT inherit the base
   world's content". An ABSENT row means the landblock is not stripped and inherits
   retail content exactly as it does today, so this table is purely additive and every
   existing realm landblock keeps its current behaviour.
   strip_statics suppresses base landblock_instance rows; authored landblock_instance_realm
   rows still win over the rule, so a stripped landblock can be repopulated per realm.
   strip_encounters suppresses the landblock's encounter spawns for that realm.
   Kept as a separate table so world-database releases/reimports never touch it. */

CREATE TABLE IF NOT EXISTS `realm_landblock_rule` (
  `realm_id` smallint unsigned NOT NULL COMMENT 'Realm this rule belongs to (realm 0 is the base world and is never stripped)',
  `landblock` smallint unsigned NOT NULL COMMENT '16-bit landblock id, e.g. 0x019E',
  `strip_statics` bit(1) NOT NULL DEFAULT b'0' COMMENT 'When set, this landblock loads no base landblock_instance content in this realm',
  `strip_encounters` bit(1) NOT NULL DEFAULT b'0' COMMENT 'When set, this landblock spawns no encounters in this realm',
  `last_Modified` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`realm_id`,`landblock`),
  CONSTRAINT `realm_landblock_rule_realm` FOREIGN KEY (`realm_id`) REFERENCES `realm` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Per-realm landblock content rules (stripped outdoor realm copies)';

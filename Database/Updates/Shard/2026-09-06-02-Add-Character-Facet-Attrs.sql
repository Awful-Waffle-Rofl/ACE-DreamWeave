/* Player Facets - per-facet attribute redistribution.
 *
 * Adds `attrs_Json` to `character_facet`: the six primary attributes' InitLevel (what
 * CreatureAttribute.StartingValue reads and what AttributeTransferDevice moves), serialized by
 * FacetSnapshot.SerializeAttributes as a bare object keyed by attribute NAME:
 *
 *   {"Strength":100,"Endurance":100,"Coordination":100,"Quickness":10,"Focus":10,"Self":10}
 *
 * Names rather than PropertyAttribute enum values, matching abilities_Json's idiom and for the same
 * reason: a future enum reordering cannot silently repoint a stored facet at a different attribute.
 *
 * SCOPE. Only the REDISTRIBUTION is per-facet. The XP-bought half of an attribute (CPSpent and
 * LevelFromCP, read as CreatureAttribute.ExperienceSpent/Ranks) stays global, as do the stored vital
 * records in biota_properties_attribute_2nd, augmentations, enlightenment, the spellbook and level.
 * InitLevel is not bought with XP, which is precisely why this column can exist without touching the
 * spendable-pool arithmetic in FacetPools.
 *
 * NULLABLE, unlike the other three JSON columns on this table, and deliberately so. There is no
 * honest default: NULL means "this row predates per-facet attributes", which the apply path has to
 * distinguish from a real stored arrangement. A row read with NULL here keeps the character's LIVE
 * arrangement rather than applying anything, so an already-populated slot upgrades in place the first
 * time it is switched away from. An empty string or a '{}' default would instead look like a real
 * arrangement whose sum is zero, which the conservation check would then have to refuse.
 *
 * This file must sort AFTER 2026-09-06-01-Rename-Character-Loadout-To-Character-Facet.sql, which is
 * the migration that gives the table its `character_facet` name. applied_updates.txt tracks
 * migrations by FILENAME and they run in filename order, so 02 following 01 is what guarantees the
 * table exists under this name by the time this runs.
 *
 * RE-RUNNABLE. Proved by construction, not just by inspection: MySQL has no
 * ALTER TABLE ... ADD COLUMN IF NOT EXISTS, so the add is guarded by an information_schema.COLUMNS
 * count and executed through the SET / PREPARE / EXECUTE / DEALLOCATE idiom taken from
 * Database/Updates/Shard/2026-09-04-00-Add-Account-Vault-Barrel.sql. After the first successful run
 * the column exists, so @col is 1, the prepared statement is a harmless `SELECT 1`, and no ALTER is
 * attempted. Do NOT reach for the stored-procedure idiom in
 * Database/Updates/Shard/2026-07-10-00-Add-Biota-Position-Instance.sql: that file can afford a shape
 * the `mysql` CLI cannot parse only because it is specifically excluded from CI's import step.
 *
 * No data change. Every existing character_facet row keeps its skills, abilities, equip and name, and
 * gets NULL here.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*.
 */

SET @col := (SELECT COUNT(*) FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_facet' AND COLUMN_NAME = 'attrs_Json');

SET @sql := IF(@col = 0,
  'ALTER TABLE `character_facet` ADD COLUMN `attrs_Json` text NULL COMMENT ''Six primary attribute InitLevel values, keyed by name. NULL = row predates per-facet attributes; keep the live arrangement''',
  'SELECT 1');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

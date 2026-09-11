/* Player Facets - rename the character_loadout table (and its facet_* tunables) to their new name.
 *
 * The feature this table backs was renamed from "Player Loadouts" to "Player Facets" (see
 * Docs/Facets/DESIGN.md section 0). This is a CREATE-then-RENAME pair rather than an edit to
 * 2026-09-05-00-Add-Character-Loadout.sql, because that migration (and the repair migration that
 * follows it, 2026-09-06-00-Repair-Loadout-Double-Parented-Items.sql) is already merged and may
 * already be applied on stage: applied_updates.txt tracks migrations by FILENAME, and migrations run
 * in filename order, so editing or renaming an already-applied file would either be silently ignored
 * (the ledger already has that filename) or, worse, break the ledger's ordering guarantee for anyone
 * who has not yet applied it. This file must sort AFTER both existing loadout migrations (2026-09-05
 * and 2026-09-06-00) because the repair migration still refers to `character_loadout` by name and
 * must run against the pre-rename table.
 *
 * Renames:
 *   - `character_loadout` table -> `character_facet` (CharacterFacetPartial.cs's entity.ToTable)
 *   - config_properties_boolean row `loadout_enabled` -> `facet_enabled`
 *   - config_properties_long rows `loadout_slot2_level` / `loadout_slot3_level` / `loadout_slot4_level`
 *     -> `facet_slot2_level` / `facet_slot3_level` / `facet_slot4_level`
 *   - config_properties_string rows `loadout_allowlist` / `loadout_allowlist_name` -> `facet_allowlist`
 *     / `facet_allowlist_name`
 * No column, data or id changes - only names. Every persisted `character_loadout` row, and every
 * operator-set tunable row, survives the rename with its data intact.
 *
 * The tunable carryover matters because a config_properties_* row beats the built-in Property<T>
 * default (PropertyManager), so an operator who had already set e.g. loadout_slot2_level would
 * otherwise have that override silently orphaned under the old key while the server reads the new
 * key's built-in default instead.
 *
 * RE-RUNNABLE. Proved by construction, not just by inspection:
 *   - The table rename is guarded by an information_schema.TABLES check and only fires when
 *     `character_loadout` exists AND `character_facet` does not; MySQL has no
 *     RENAME TABLE IF EXISTS, so the guard is a SET / PREPARE / EXECUTE / DEALLOCATE block, the same
 *     shape used by Database/Updates/Shard/2026-09-04-00-Add-Account-Vault-Barrel.sql. After the
 *     first successful run `character_loadout` no longer exists, so a second run's condition is
 *     false, the prepared statement is a harmless `SELECT 1`, and no rename is attempted.
 *   - Each tunable carryover is a DELETE-then-UPDATE pair keyed on the row's own PRIMARY KEY (`key`):
 *     the DELETE removes the OLD-key row only when a NEW-key row already exists (collision-safe: the
 *     already-present target row wins, the stale source row is discarded rather than left behind or
 *     fought over); the UPDATE then renames any remaining OLD-key row to the NEW key. On a second
 *     run the OLD key no longer exists in either branch (it was either renamed or deleted by the
 *     first run), so both the DELETE and the UPDATE match zero rows.
 *   - Do NOT reach for the stored-procedure idiom in
 *     Database/Updates/Shard/2026-07-10-00-Add-Biota-Position-Instance.sql: that file can afford a
 *     shape the `mysql` CLI cannot parse only because it is specifically excluded from CI's import
 *     step. This file follows 2026-09-04-00-Add-Account-Vault-Barrel.sql's plain
 *     SET / PREPARE / EXECUTE / DEALLOCATE guard instead, which needs no such exclusion.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*.
 */

SET @old_table_exists := (
  SELECT COUNT(*) FROM information_schema.TABLES
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_loadout'
);
SET @new_table_exists := (
  SELECT COUNT(*) FROM information_schema.TABLES
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'character_facet'
);
SET @rename_table_sql := IF(@old_table_exists > 0 AND @new_table_exists = 0,
  'RENAME TABLE `character_loadout` TO `character_facet`',
  'SELECT 1');

PREPARE rename_table_stmt FROM @rename_table_sql;
EXECUTE rename_table_stmt;
DEALLOCATE PREPARE rename_table_stmt;

-- config_properties_boolean: loadout_enabled -> facet_enabled
DELETE t1 FROM `config_properties_boolean` t1
WHERE t1.`key` = 'loadout_enabled'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_boolean`) t2 WHERE t2.`key` = 'facet_enabled');

UPDATE `config_properties_boolean`
SET `key` = 'facet_enabled'
WHERE `key` = 'loadout_enabled';

-- config_properties_long: loadout_slot2_level / loadout_slot3_level / loadout_slot4_level -> facet_*
DELETE t1 FROM `config_properties_long` t1
WHERE t1.`key` = 'loadout_slot2_level'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_long`) t2 WHERE t2.`key` = 'facet_slot2_level');

UPDATE `config_properties_long`
SET `key` = 'facet_slot2_level'
WHERE `key` = 'loadout_slot2_level';

DELETE t1 FROM `config_properties_long` t1
WHERE t1.`key` = 'loadout_slot3_level'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_long`) t2 WHERE t2.`key` = 'facet_slot3_level');

UPDATE `config_properties_long`
SET `key` = 'facet_slot3_level'
WHERE `key` = 'loadout_slot3_level';

DELETE t1 FROM `config_properties_long` t1
WHERE t1.`key` = 'loadout_slot4_level'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_long`) t2 WHERE t2.`key` = 'facet_slot4_level');

UPDATE `config_properties_long`
SET `key` = 'facet_slot4_level'
WHERE `key` = 'loadout_slot4_level';

-- config_properties_string: loadout_allowlist / loadout_allowlist_name -> facet_*
DELETE t1 FROM `config_properties_string` t1
WHERE t1.`key` = 'loadout_allowlist'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_string`) t2 WHERE t2.`key` = 'facet_allowlist');

UPDATE `config_properties_string`
SET `key` = 'facet_allowlist'
WHERE `key` = 'loadout_allowlist';

DELETE t1 FROM `config_properties_string` t1
WHERE t1.`key` = 'loadout_allowlist_name'
  AND EXISTS (SELECT 1 FROM (SELECT `key` FROM `config_properties_string`) t2 WHERE t2.`key` = 'facet_allowlist_name');

UPDATE `config_properties_string`
SET `key` = 'facet_allowlist_name'
WHERE `key` = 'loadout_allowlist_name';

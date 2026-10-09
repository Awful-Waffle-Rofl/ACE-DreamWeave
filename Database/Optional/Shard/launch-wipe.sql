-- ===========================================================================================
-- Dreamweave LAUNCH WIPE (2026-10-02). Operator-run, NEVER auto-applied.
--
-- Spec of record: Docs/LAUNCH-2026-10-02.md, sections 1-3. Run ONLY through tools/launch-wipe.sh,
-- never by feeding this file to a mysql client by hand. The wrapper does four things this file
-- depends on and cannot do itself:
--
--   1. It creates the scratch schema `ace_wipe_scratch` and every table and procedure below that
--      lives in it. That is DDL, DDL implicitly commits, so it has to happen BEFORE the
--      transaction this file runs in. (Real tables, not TEMPORARY ones: MySQL cannot reopen a
--      TEMPORARY table twice in one statement, and the keep closure needs exactly that.)
--   2. It fills `ace_wipe_scratch.keep_char` (the characters that survive), `keep_guid_extra`
--      (--keep-guid roots), `house_link` (a copy of the world's landblock_instance_link) and
--      `weenie_name` (world weenie class names, so the review list never needs a biota's own
--      Name string, which on a corpse is a player's name).
--   3. It connects with the SHARD schema as the default database (this file never says USE) and
--      sets @p_auth_db to the auth schema name before sourcing this file.
--   4. It wraps this file in SET autocommit=0 / START TRANSACTION ... and then COMMIT (apply) or
--      ROLLBACK (dry run). The last statement of this file is CALL ace_wipe_scratch.lw_gate(),
--      which SIGNALs when any gate or verify check is non-zero; the mysql client then aborts
--      without reaching COMMIT, and closing the connection rolls the transaction back.
--
-- WHAT IT DOES (delete by COMPLEMENT: compute what survives, delete everything else)
--
--   guard     refuse if any biota id lies outside the player / static / dynamic guid ranges.
--   keep      roots = kept characters' player biotas; static biotas of houses owned by a kept
--             character plus that house's world link children (hooks, storage, slumlord, boot
--             spot, house portal); allegiance biotas whose Monarch is kept; --keep-guid roots.
--             Closure = every biota reachable DOWN Container / Wielder instance ids from a root.
--   classify  every top-level biota (no Container and no Wielder) into exactly one category:
--             player_keep / player_wipe, static_admin_house / static_other, allegiance_keep /
--             allegiance_wipe, vault (an account_vault.container_Guid), corpse (wherever it lies),
--             vault_landblock_unlinked (on the vault landblock with no account_vault row),
--             ground_item, no_location_orphan, kept_extra (--keep-guid), unclassified.
--             `unclassified` > 0 fails the gate.
--   delete    biota outside the closure (every biota_properties_* child cascades); character
--             rows outside keep_char (character_properties_* cascade); every WIPE table; the
--             non-kept rows of every CLEAN table.
--   scrub     references left on kept rows that point at wiped characters or objects.
--   accounts  every ace_auth account at accessLevel Advocate..Developer is set to Player.
--   config    world_closed is set true (upsert). No other config row may change.
--   verify    every check must be 0, then lw_gate().
--
-- Why the reference scrub must be complete: the player guid allocator starts at
-- MAX(player-range biota id) + 1 (GuidManager.PlayerGuidAllocator, reading
-- ShardDatabase.GetMaxGuidFoundInRange), so a launch character can be handed a wiped player's
-- guid and would inherit any reference still pointing at it.
--
-- Idempotent: a second run on an already-wiped shard deletes nothing and passes every check.
--
-- Output is counts only, plus the names of the kept (admin) characters. The review list of
-- corpses, ground items, non-house statics and unlinked objects on the vault landblock prints
-- guid, wcid, world weenie class name and
-- landblock - never a biota's own Name, never a player's name.
--
-- The C# guard Source/ACE.Server.Tests/LaunchWipeScriptTests.cs parses the two frozen blocks
-- below: every shard table (Database/Base/ShardBase.sql plus every CREATE TABLE under
-- Database/Updates/Shard, following renames) must appear in the inventory exactly once, and every
-- @c_ constant must equal the C# member it names. The body may use only those variables, never
-- bare numbers, for enum values and guid range bounds.
-- ===========================================================================================

-- INVENTORY-BEGIN
-- ace_shard_migration_marker KEEP
-- config_properties_boolean KEEP
-- config_properties_double KEEP
-- config_properties_long KEEP
-- config_properties_string KEEP
-- biota CLEAN
-- biota_properties_allegiance CLEAN
-- biota_properties_anim_part CLEAN
-- biota_properties_attribute CLEAN
-- biota_properties_attribute_2nd CLEAN
-- biota_properties_body_part CLEAN
-- biota_properties_book CLEAN
-- biota_properties_book_page_data CLEAN
-- biota_properties_bool CLEAN
-- biota_properties_create_list CLEAN
-- biota_properties_d_i_d CLEAN
-- biota_properties_emote CLEAN
-- biota_properties_emote_action CLEAN
-- biota_properties_enchantment_registry CLEAN
-- biota_properties_event_filter CLEAN
-- biota_properties_float CLEAN
-- biota_properties_generator CLEAN
-- biota_properties_i_i_d CLEAN
-- biota_properties_int CLEAN
-- biota_properties_int64 CLEAN
-- biota_properties_palette CLEAN
-- biota_properties_position CLEAN
-- biota_properties_skill CLEAN
-- biota_properties_spell_book CLEAN
-- biota_properties_string CLEAN
-- biota_properties_texture_map CLEAN
-- character CLEAN
-- character_properties_contract_registry CLEAN
-- character_properties_fill_comp_book CLEAN
-- character_properties_friend_list CLEAN
-- character_properties_quest_registry CLEAN
-- character_properties_shortcut_bar CLEAN
-- character_properties_spell_bar CLEAN
-- character_properties_squelch CLEAN
-- character_properties_title_book CLEAN
-- house_permission CLEAN
-- account_bank_fold CLEAN
-- character_facet CLEAN
-- character_loadout CLEAN
-- character_sheet_link CLEAN
-- character_cap_ledger CLEAN
-- character_cap_audit CLEAN
-- account_vault WIPE
-- account_vault_stack WIPE
-- account_vault_grant WIPE
-- account_vault_log WIPE
-- account_vault_barrel WIPE
-- account_vault_class WIPE
-- account_bank WIPE
-- account_mule_form WIPE
-- account_capacity_upgrade WIPE
-- account_capacity_upgrade_purchase WIPE
-- market_listing WIPE
-- market_transaction WIPE
-- market_rejected_attempt WIPE
-- market_buy_order WIPE
-- reward_claim WIPE
-- thread_puzzle_ip_ledger WIPE
-- character_speed_run WIPE
-- character_pvp_rating WIPE
-- pvp_match WIPE
-- pvp_match_participant WIPE
-- pvp_template KEEP
-- pvp_template_history KEEP
-- INVENTORY-END
--
-- Inventory notes:
--   character_loadout is the PRE-RENAME name of character_facet
--   (Database/Updates/Shard/2026-09-06-01-Rename-Character-Loadout-To-Character-Facet.sql). It is
--   listed because a shard whose ledger re-ran 2026-09-05-00 carries BOTH tables (observed on a
--   local dev shard, 2026-09-28); it is cleaned like character_facet when present.
--   config_properties_boolean is KEEP except for the single world_closed upsert below, and the
--   verify block checksums every config row other than world_closed before and after.
--   The applied-updates ledger is a FILE (applied_updates.txt), not a table; nothing here
--   touches it, and the schema is not changed, so it stays correct.
--   pvp_template and pvp_template_history are KEEP: they hold admin-authored PvP template
--   definitions (frozen JSON), server content rather than character state. source_Character_Id is
--   informational only and is never resolved, so a wiped template character leaves nothing dangling.

-- ---------------------------------------------------------------- constants (frozen format)
-- Decimal on purpose: a 0x literal assigned to a user variable is a BINARY STRING in MySQL, not
-- a number, and would compare wrongly.
SET @c_ObjectGuid_PlayerMin = 1342177281;
SET @c_ObjectGuid_PlayerMax = 1610612735;
SET @c_ObjectGuid_StaticObjectMin = 1879048192;
SET @c_ObjectGuid_StaticObjectMax = 2147483647;
SET @c_ObjectGuid_DynamicMin = 2147483648;
SET @c_ObjectGuid_DynamicMax = 4294967294;
SET @c_PropertyInstanceId_Container = 2;
SET @c_PropertyInstanceId_Wielder = 3;
SET @c_PropertyInstanceId_Allegiance = 24;
SET @c_PropertyInstanceId_Monarch = 26;
SET @c_PropertyInstanceId_HouseOwner = 32;
SET @c_WeenieType_Corpse = 14;
SET @c_WeenieType_Allegiance = 30;
SET @c_WeenieType_SlumLord = 55;
SET @c_PositionType_Location = 1;
SET @c_PropertyInt64_BankedPyreals = 9004;
SET @c_PropertyInt64_BankedLuminance = 9005;
SET @c_PropertyInt64_BankedLegendaryKeys = 9015;
SET @c_PropertyInt64_BankedPromissoryNotes = 9016;
SET @c_AccessLevel_Player = 0;
SET @c_AccessLevel_Advocate = 1;
SET @c_AccessLevel_Developer = 4;
SET @c_DefaultLong_account_vault_landblock = 32767;

-- ---------------------------------------------------------------- parameters
-- @p_auth_db is set by the wrapper. A missing value fails the gate here, before anything is read.
INSERT INTO ace_wipe_scratch.check_result (check_name, n)
VALUES ('param_auth_db_missing', IF(@p_auth_db IS NULL OR @p_auth_db = '', 1, 0));
CALL ace_wipe_scratch.lw_gate();

SET @lw_auth = CONCAT('`', REPLACE(@p_auth_db, '`', '``'), '`');

-- ---------------------------------------------------------------- config checksum (before)
-- Every config row except world_closed, order-independent. Recomputed in the verify block.
SET @lw_cfg_before = (
      (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 'b', `key`, `value` + 0, COALESCE(`description`, '<null>')))), 0) FROM config_properties_boolean WHERE `key` <> 'world_closed')
    + (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 'd', `key`, CAST(`value` AS CHAR), COALESCE(`description`, '<null>')))), 0) FROM config_properties_double)
    + (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 'l', `key`, CAST(`value` AS CHAR), COALESCE(`description`, '<null>')))), 0) FROM config_properties_long)
    + (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 's', `key`, `value`, COALESCE(`description`, '<null>')))), 0) FROM config_properties_string));
SET @lw_cfg_rows_before = (SELECT COUNT(*) FROM config_properties_boolean WHERE `key` <> 'world_closed')
    + (SELECT COUNT(*) FROM config_properties_double)
    + (SELECT COUNT(*) FROM config_properties_long)
    + (SELECT COUNT(*) FROM config_properties_string);

-- ---------------------------------------------------------------- a. guard
-- Fail fast: an id outside every known range is not something this script can reason about.
INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'guard_biota_id_out_of_range', COUNT(*)
FROM biota
WHERE NOT (   `id` BETWEEN @c_ObjectGuid_PlayerMin       AND @c_ObjectGuid_PlayerMax
           OR `id` BETWEEN @c_ObjectGuid_StaticObjectMin AND @c_ObjectGuid_StaticObjectMax
           OR `id` BETWEEN @c_ObjectGuid_DynamicMin      AND @c_ObjectGuid_DynamicMax);
CALL ace_wipe_scratch.lw_gate();

-- ---------------------------------------------------------------- b. vault containers
-- Vault containers are ordinary backpacks (AccountVaultStore.VaultContainerWcid), so they are
-- identified by account_vault.container_Guid or by a Location on the vault landblock, never by
-- wcid. account_vault is a fork table; guarded.
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault') = 1,
    'INSERT IGNORE INTO ace_wipe_scratch.vault_container (id) SELECT `container_Guid` FROM `account_vault`', 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

-- The landblock the server uses: account_vault_landblock masked to 16 bits, a configured 0
-- replaced by the registered default (AccountVaultStore, VaultLandblock). Both are matched,
-- as AccountVaultSpawnFilter does, because containers created before a change keep the old one.
SET @lw_vault_lb_configured = (SELECT `value` & 65535 FROM config_properties_long WHERE `key` = 'account_vault_landblock');
SET @lw_vault_lb = IF(@lw_vault_lb_configured IS NULL OR @lw_vault_lb_configured = 0, @c_DefaultLong_account_vault_landblock, @lw_vault_lb_configured);

-- ---------------------------------------------------------------- c. top-level biotas
INSERT INTO ace_wipe_scratch.top_level (id, weenie_Type, wcid)
SELECT b.`id`, b.`weenie_Type`, b.`weenie_Class_Id`
FROM biota b
WHERE NOT EXISTS (SELECT 1 FROM biota_properties_i_i_d i
                  WHERE i.`object_Id` = b.`id`
                    AND i.`type` IN (@c_PropertyInstanceId_Container, @c_PropertyInstanceId_Wielder));

UPDATE ace_wipe_scratch.top_level t
JOIN biota_properties_position p ON p.`object_Id` = t.`id` AND p.`position_Type` = @c_PositionType_Location
SET t.`landblock` = p.`obj_Cell_Id` >> 16;

-- ---------------------------------------------------------------- d. keep roots
-- Kept characters' own player biotas.
INSERT IGNORE INTO ace_wipe_scratch.keep_root (id, reason)
SELECT k.`id`, 'player' FROM ace_wipe_scratch.keep_char k JOIN biota b ON b.`id` = k.`id`;

-- Houses owned by a kept character. The anchor is the HOUSE's own static guid, found either on
-- a static biota carrying HouseOwner (the house itself, or its slumlord) or as the world link
-- parent of such a slumlord, so an owned house is found even if only one of the two was saved.
INSERT IGNORE INTO ace_wipe_scratch.house_anchor (id)
SELECT i.`object_Id`
FROM biota_properties_i_i_d i
JOIN ace_wipe_scratch.keep_char k ON k.`id` = i.`value`
WHERE i.`type` = @c_PropertyInstanceId_HouseOwner
  AND i.`object_Id` BETWEEN @c_ObjectGuid_StaticObjectMin AND @c_ObjectGuid_StaticObjectMax;

INSERT IGNORE INTO ace_wipe_scratch.house_anchor (id)
SELECT l.`parent_Guid`
FROM ace_wipe_scratch.house_anchor a
JOIN ace_wipe_scratch.house_link l ON l.`child_Guid` = a.`id`;

-- The anchors and their link children (hooks, storage, slumlord, boot spot, house portal), where
-- a biota exists. A child with no biota needs none: it rebuilds from the world at activation.
INSERT IGNORE INTO ace_wipe_scratch.keep_root (id, reason)
SELECT b.`id`, 'house' FROM ace_wipe_scratch.house_anchor a JOIN biota b ON b.`id` = a.`id`;

INSERT IGNORE INTO ace_wipe_scratch.keep_root (id, reason)
SELECT b.`id`, 'house_link'
FROM ace_wipe_scratch.house_anchor a
JOIN ace_wipe_scratch.house_link l ON l.`parent_Guid` = a.`id`
JOIN biota b ON b.`id` = l.`child_Guid`;

-- Allegiances whose monarch is kept (AllegianceManager finds the biota by weenie type + Monarch).
INSERT IGNORE INTO ace_wipe_scratch.keep_root (id, reason)
SELECT b.`id`, 'allegiance'
FROM biota b
JOIN biota_properties_i_i_d i ON i.`object_Id` = b.`id` AND i.`type` = @c_PropertyInstanceId_Monarch
JOIN ace_wipe_scratch.keep_char k ON k.`id` = i.`value`
WHERE b.`weenie_Type` = @c_WeenieType_Allegiance;

-- Operator extras (--keep-guid), where they exist.
INSERT IGNORE INTO ace_wipe_scratch.keep_root (id, reason)
SELECT b.`id`, 'keep_guid' FROM ace_wipe_scratch.keep_guid_extra x JOIN biota b ON b.`id` = x.`id`;

-- ---------------------------------------------------------------- e. keep closure
-- Down Container / Wielder: an object survives when its container or wielder survives.
-- UNION (distinct) terminates on a cycle.
INSERT IGNORE INTO ace_wipe_scratch.keep_biota (id)
WITH RECURSIVE lw_closure (id) AS (
    SELECT r.`id` FROM ace_wipe_scratch.keep_root r
    UNION
    SELECT i.`object_Id`
    FROM biota_properties_i_i_d i
    JOIN lw_closure c ON i.`value` = c.`id`
    WHERE i.`type` IN (@c_PropertyInstanceId_Container, @c_PropertyInstanceId_Wielder)
)
SELECT id FROM lw_closure;

-- ---------------------------------------------------------------- c. classify (continued)
UPDATE ace_wipe_scratch.top_level t
LEFT JOIN ace_wipe_scratch.keep_biota kb ON kb.`id` = t.`id`
LEFT JOIN ace_wipe_scratch.vault_container vc ON vc.`id` = t.`id`
LEFT JOIN ace_wipe_scratch.keep_guid_extra kx ON kx.`id` = t.`id`
SET t.`category` = CASE
    WHEN t.`id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax
        THEN IF(kb.`id` IS NOT NULL, 'player_keep', 'player_wipe')
    WHEN t.`id` BETWEEN @c_ObjectGuid_StaticObjectMin AND @c_ObjectGuid_StaticObjectMax
        THEN IF(kb.`id` IS NOT NULL, IF(kx.`id` IS NOT NULL, 'kept_extra', 'static_admin_house'), 'static_other')
    WHEN t.`id` BETWEEN @c_ObjectGuid_DynamicMin AND @c_ObjectGuid_DynamicMax THEN CASE
        WHEN kx.`id` IS NOT NULL THEN 'kept_extra'
        WHEN t.`weenie_Type` = @c_WeenieType_Allegiance
            THEN IF(kb.`id` IS NOT NULL, 'allegiance_keep', 'allegiance_wipe')
        -- Only a real account_vault row makes an object a vault. A corpse is a corpse wherever it
        -- lies, and anything else merely sitting on the vault landblock gets its own label so it
        -- reaches the R5 review list instead of disappearing under 'vault'.
        WHEN vc.`id` IS NOT NULL THEN 'vault'
        WHEN t.`weenie_Type` = @c_WeenieType_Corpse THEN 'corpse'
        WHEN t.`landblock` IN (@lw_vault_lb, @c_DefaultLong_account_vault_landblock) THEN 'vault_landblock_unlinked'
        WHEN t.`landblock` IS NOT NULL THEN 'ground_item'
        ELSE 'no_location_orphan'
        END
    ELSE 'unclassified'
    END;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'gate_unclassified_top_level', COUNT(*) FROM ace_wipe_scratch.top_level WHERE `category` IS NULL OR `category` = 'unclassified';

-- ---------------------------------------------------------------- dry-run report (before deletes)
SELECT k.`name` AS kept_character, CONCAT('0x', HEX(k.`id`)) AS guid, k.`account_Id`, k.`access_Level`,
       IF(b.`id` IS NULL, 'NO BIOTA', 'ok') AS biota
FROM ace_wipe_scratch.keep_char k LEFT JOIN biota b ON b.`id` = k.`id`
ORDER BY k.`name`;

SELECT r.`reason` AS keep_root_reason, COUNT(*) AS roots FROM ace_wipe_scratch.keep_root r GROUP BY r.`reason` ORDER BY r.`reason`;

SELECT t.`category`, COUNT(*) AS top_level_biotas FROM ace_wipe_scratch.top_level t GROUP BY t.`category` ORDER BY t.`category`;

SELECT t.`category`, t.`weenie_Type`, COUNT(*) AS top_level_biotas
FROM ace_wipe_scratch.top_level t GROUP BY t.`category`, t.`weenie_Type` ORDER BY t.`category`, t.`weenie_Type`;

SELECT
    (SELECT COUNT(*) FROM biota)                         AS biota_before,
    (SELECT COUNT(*) FROM ace_wipe_scratch.keep_biota)   AS biota_kept,
    (SELECT COUNT(*) FROM biota) - (SELECT COUNT(*) FROM ace_wipe_scratch.keep_biota) AS biota_to_delete,
    (SELECT COUNT(*) FROM `character`)                   AS character_rows_before,
    (SELECT COUNT(*) FROM ace_wipe_scratch.keep_char)    AS character_rows_kept,
    (SELECT COUNT(*) FROM biota b
      JOIN biota_properties_i_i_d i ON i.`object_Id` = b.`id` AND i.`type` IN (@c_PropertyInstanceId_Container, @c_PropertyInstanceId_Wielder)
      LEFT JOIN biota p ON p.`id` = i.`value`
      WHERE p.`id` IS NULL)                              AS nested_orphans_before;

-- R5 review list: every corpse, ground item, non-house static and object on the vault landblock
-- with no account_vault row that will be deleted, plus any --keep-guid extras. World class name
-- only; a biota's own Name can carry a player's name.
SELECT t.`category`, CONCAT('0x', HEX(t.`id`)) AS guid, t.`wcid`, COALESCE(w.`class_Name`, '?') AS weenie_class,
       IF(t.`landblock` IS NULL, '-', CONCAT('0x', LPAD(HEX(t.`landblock`), 4, '0'))) AS landblock
FROM ace_wipe_scratch.top_level t
LEFT JOIN ace_wipe_scratch.weenie_name w ON w.`class_Id` = t.`wcid`
WHERE t.`category` IN ('corpse', 'ground_item', 'static_other', 'vault_landblock_unlinked', 'kept_extra')
ORDER BY t.`category`, t.`landblock`, t.`id`;

-- ---------------------------------------------------------------- f. deletes
DELETE b FROM biota b LEFT JOIN ace_wipe_scratch.keep_biota kb ON kb.`id` = b.`id` WHERE kb.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('delete biota (children cascade)', ROW_COUNT());

DELETE c FROM `character` c LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = c.`id` WHERE k.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('delete character (properties cascade)', ROW_COUNT());

DELETE hp FROM house_permission hp LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = hp.`player_Guid` WHERE k.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('clean house_permission guests', ROW_COUNT());

-- WIPE tables (all fork tables; each guarded so the script runs on a shard behind master).
SET @lw_t = 'account_vault';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_stack';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_grant';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_log';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_barrel';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_class';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_bank';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_mule_form';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_capacity_upgrade';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_capacity_upgrade_purchase';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_listing';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_transaction';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_rejected_attempt';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_buy_order';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'reward_claim';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'thread_puzzle_ip_ledger';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_speed_run';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_pvp_rating';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'pvp_match';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'pvp_match_participant';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('wipe ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

-- CLEAN fork tables: keep only rows of kept characters. @lw_c is the character-id column.
SET @lw_t = 'account_bank_fold'; SET @lw_c = 'character_Guid';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE r FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('clean ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_facet'; SET @lw_c = 'character_Id';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE r FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('clean ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_loadout'; SET @lw_c = 'character_Id';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE r FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('clean ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_sheet_link'; SET @lw_c = 'character_Id';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE r FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('clean ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_cap_ledger'; SET @lw_c = 'character_Id';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE r FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('clean ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_cap_audit'; SET @lw_c = 'character_Id';
SET @lw_x = (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t);
SET @lw_sql = IF(@lw_x = 1, CONCAT('DELETE r FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES (CONCAT('clean ', @lw_t), IF(@lw_x = 1, ROW_COUNT(), -1));
DEALLOCATE PREPARE lw_stmt;

-- R3 on kept characters: banked pyreals / luminance / keys / notes properties removed.
DELETE FROM biota_properties_int64
WHERE `object_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax
  AND `type` IN (@c_PropertyInt64_BankedPyreals, @c_PropertyInt64_BankedLuminance,
                 @c_PropertyInt64_BankedLegendaryKeys, @c_PropertyInt64_BankedPromissoryNotes);
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('delete banked int64 on kept characters', ROW_COUNT());

-- ---------------------------------------------------------------- g. reference scrub
-- Player-range instance ids on surviving objects that name a character that is not kept
-- (patron, monarch, allowed wielder / activator, house owner / monarch, creator, and so on).
DELETE i FROM biota_properties_i_i_d i
LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = i.`value`
WHERE i.`value` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('scrub player-range instance ids', ROW_COUNT());

-- Allegiance links to an allegiance biota that no longer exists.
DELETE i FROM biota_properties_i_i_d i
LEFT JOIN biota b ON b.`id` = i.`value`
WHERE i.`type` = @c_PropertyInstanceId_Allegiance AND b.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('scrub allegiance links', ROW_COUNT());

DELETE f FROM character_properties_friend_list f
LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = f.`friend_Id`
WHERE k.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('scrub friend rows', ROW_COUNT());

DELETE s FROM character_properties_squelch s
LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = s.`squelch_Character_Id`
WHERE s.`squelch_Character_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('scrub squelch rows', ROW_COUNT());

DELETE s FROM character_properties_shortcut_bar s
LEFT JOIN biota b ON b.`id` = s.`shortcut_Object_Id`
WHERE b.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('scrub shortcut rows', ROW_COUNT());

-- Zeroed, never passed through (Docs/ProdCharSeed/TECH-DESIGN.md, author_Id). 0 is schema-legal
-- and is not the IOU sentinel (uint.MaxValue), which is outside the player range anyway.
UPDATE biota_properties_book_page_data p
LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = p.`author_Id`
SET p.`author_Id` = 0
WHERE p.`author_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('zero book author ids', ROW_COUNT());

-- ---------------------------------------------------------------- accounts
SET @lw_sql = CONCAT('UPDATE ', @lw_auth, '.`account` SET `accessLevel` = ', @c_AccessLevel_Player,
                     ' WHERE `accessLevel` BETWEEN ', @c_AccessLevel_Advocate, ' AND ', @c_AccessLevel_Developer);
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt;
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('demote accounts Advocate..Developer to Player', ROW_COUNT());
DEALLOCATE PREPARE lw_stmt;

-- ---------------------------------------------------------------- config
-- The launch boot comes up closed (Program.cs reads world_closed at startup; the row wins over
-- the code default). Only `value` is touched on an existing row.
INSERT INTO config_properties_boolean (`key`, `value`, `description`)
VALUES ('world_closed', b'1', 'enable this to startup world as a closed to players world')
ON DUPLICATE KEY UPDATE `value` = b'1';
INSERT INTO ace_wipe_scratch.action_count (step, n) VALUES ('upsert world_closed = true', ROW_COUNT());

SELECT a.`step`, a.`n` AS rows_affected FROM ace_wipe_scratch.action_count a ORDER BY a.`seq`;

-- ---------------------------------------------------------------- h. verify (every n must be 0)
INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_character_outside_keep', COUNT(*)
FROM `character` c LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = c.`id` WHERE k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_player_biota_outside_keep', COUNT(*)
FROM biota b LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = b.`id`
WHERE b.`id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_container_or_wielder_to_missing_biota', COUNT(*)
FROM biota_properties_i_i_d i LEFT JOIN biota b ON b.`id` = i.`value`
WHERE i.`type` IN (@c_PropertyInstanceId_Container, @c_PropertyInstanceId_Wielder) AND b.`id` IS NULL;

-- Statics are exempt: a static biota with no Location is rebuilt at its world placement
-- (WorldObjectFactory.CreateNewWorldObjects restores it from the landblock_instance row).
INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_top_level_without_location', COUNT(*)
FROM biota b
WHERE b.`weenie_Type` <> @c_WeenieType_Allegiance
  AND NOT (b.`id` BETWEEN @c_ObjectGuid_StaticObjectMin AND @c_ObjectGuid_StaticObjectMax)
  AND NOT EXISTS (SELECT 1 FROM biota_properties_i_i_d i WHERE i.`object_Id` = b.`id`
                  AND i.`type` IN (@c_PropertyInstanceId_Container, @c_PropertyInstanceId_Wielder))
  AND NOT EXISTS (SELECT 1 FROM biota_properties_position p WHERE p.`object_Id` = b.`id`
                  AND p.`position_Type` = @c_PositionType_Location);

-- HouseManager builds the rent queue and the owned-house list from SlumLord biotas carrying
-- HouseOwner; one left pointing at a wiped owner would hold the house and log at every boot.
INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_slumlord_owner_not_kept', COUNT(*)
FROM biota b
JOIN biota_properties_i_i_d i ON i.`object_Id` = b.`id` AND i.`type` = @c_PropertyInstanceId_HouseOwner
LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = i.`value`
WHERE b.`weenie_Type` = @c_WeenieType_SlumLord AND k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_dangling_player_iid', COUNT(*)
FROM biota_properties_i_i_d i LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = i.`value`
WHERE i.`value` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_allegiance_link_to_missing_biota', COUNT(*)
FROM biota_properties_i_i_d i LEFT JOIN biota b ON b.`id` = i.`value`
WHERE i.`type` = @c_PropertyInstanceId_Allegiance AND b.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_allegiance_member_not_kept', COUNT(*)
FROM biota_properties_allegiance a LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = a.`character_Id`
WHERE k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_house_permission_guest_not_kept', COUNT(*)
FROM house_permission hp LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = hp.`player_Guid` WHERE k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_friend_not_kept', COUNT(*)
FROM character_properties_friend_list f LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = f.`friend_Id` WHERE k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_squelch_not_kept', COUNT(*)
FROM character_properties_squelch s LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = s.`squelch_Character_Id`
WHERE s.`squelch_Character_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_shortcut_to_missing_biota', COUNT(*)
FROM character_properties_shortcut_bar s LEFT JOIN biota b ON b.`id` = s.`shortcut_Object_Id` WHERE b.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_book_author_not_kept', COUNT(*)
FROM biota_properties_book_page_data p LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = p.`author_Id`
WHERE p.`author_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL;

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_banked_int64_remaining', COUNT(*)
FROM biota_properties_int64
WHERE `object_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax
  AND `type` IN (@c_PropertyInt64_BankedPyreals, @c_PropertyInt64_BankedLuminance,
                 @c_PropertyInt64_BankedLegendaryKeys, @c_PropertyInt64_BankedPromissoryNotes);

-- WIPE tables empty (absent tables count 0).
SET @lw_t = 'account_vault';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_stack';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_grant';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_log';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_barrel';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_vault_class';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_bank';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_mule_form';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_capacity_upgrade';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'account_capacity_upgrade_purchase';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_listing';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_transaction';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_rejected_attempt';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'market_buy_order';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'reward_claim';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'thread_puzzle_ip_ledger';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_speed_run';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_pvp_rating';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'pvp_match';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'pvp_match_participant';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_wipe_nonempty ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '`'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

-- CLEAN fork tables hold kept characters only.
SET @lw_t = 'account_bank_fold'; SET @lw_c = 'character_Guid';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_clean_not_kept ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_facet'; SET @lw_c = 'character_Id';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_clean_not_kept ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_loadout'; SET @lw_c = 'character_Id';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_clean_not_kept ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_sheet_link'; SET @lw_c = 'character_Id';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_clean_not_kept ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_cap_ledger'; SET @lw_c = 'character_Id';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_clean_not_kept ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

SET @lw_t = 'character_cap_audit'; SET @lw_c = 'character_Id';
SET @lw_sql = IF((SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @lw_t) = 1,
    CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_clean_not_kept ', @lw_t, ''', COUNT(*) FROM `', @lw_t, '` r LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = r.`', @lw_c, '` WHERE k.`id` IS NULL'), 'DO 0');
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

-- Accounts: nobody left between Player and Admin.
SET @lw_sql = CONCAT('INSERT INTO ace_wipe_scratch.check_result (check_name, n) SELECT ''verify_account_staff_level_remaining'', COUNT(*) FROM ',
                     @lw_auth, '.`account` WHERE `accessLevel` BETWEEN ', @c_AccessLevel_Advocate, ' AND ', @c_AccessLevel_Developer);
PREPARE lw_stmt FROM @lw_sql; EXECUTE lw_stmt; DEALLOCATE PREPARE lw_stmt;

-- Config: world_closed is true, and nothing else changed.
INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_world_closed_not_true',
       IF((SELECT COUNT(*) FROM config_properties_boolean WHERE `key` = 'world_closed' AND `value` = b'1') = 1, 0, 1);

SET @lw_cfg_after = (
      (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 'b', `key`, `value` + 0, COALESCE(`description`, '<null>')))), 0) FROM config_properties_boolean WHERE `key` <> 'world_closed')
    + (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 'd', `key`, CAST(`value` AS CHAR), COALESCE(`description`, '<null>')))), 0) FROM config_properties_double)
    + (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 'l', `key`, CAST(`value` AS CHAR), COALESCE(`description`, '<null>')))), 0) FROM config_properties_long)
    + (SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', 's', `key`, `value`, COALESCE(`description`, '<null>')))), 0) FROM config_properties_string));
SET @lw_cfg_rows_after = (SELECT COUNT(*) FROM config_properties_boolean WHERE `key` <> 'world_closed')
    + (SELECT COUNT(*) FROM config_properties_double)
    + (SELECT COUNT(*) FROM config_properties_long)
    + (SELECT COUNT(*) FROM config_properties_string);

INSERT INTO ace_wipe_scratch.check_result (check_name, n)
SELECT 'verify_config_changed_other_than_world_closed',
       IF(@lw_cfg_before = @lw_cfg_after AND @lw_cfg_rows_before = @lw_cfg_rows_after, 0, 1);

-- Informational, never gated: dynamic-range instance ids on survivors that point at no biota
-- (dynamic guids are reused from sequence gaps), and enchantments cast by a wiped character.
SELECT
    (SELECT COUNT(*) FROM biota_properties_i_i_d i LEFT JOIN biota b ON b.`id` = i.`value`
      WHERE i.`value` BETWEEN @c_ObjectGuid_DynamicMin AND @c_ObjectGuid_DynamicMax AND b.`id` IS NULL) AS info_dynamic_iid_to_missing_biota,
    (SELECT COUNT(*) FROM biota_properties_enchantment_registry e LEFT JOIN ace_wipe_scratch.keep_char k ON k.`id` = e.`caster_Object_Id`
      WHERE e.`caster_Object_Id` BETWEEN @c_ObjectGuid_PlayerMin AND @c_ObjectGuid_PlayerMax AND k.`id` IS NULL) AS info_enchantment_caster_not_kept,
    (SELECT COUNT(*) FROM biota)       AS biota_after,
    (SELECT COUNT(*) FROM `character`) AS character_rows_after;

SELECT c.`check_name`, c.`n` FROM ace_wipe_scratch.check_result c ORDER BY c.`check_name`;

-- The decision. Signals (and so aborts the session before COMMIT) if any check above is non-zero.
CALL ace_wipe_scratch.lw_gate();

/* Mule Vendor - /mule search all: "which mules are shared with THIS character".
 *
 * Three changes to account_vault_grant, each behind its own information_schema guard:
 *
 *   1. KEY account_vault_grant_grantee_idx (grantee_Character_Guid). The unique key leads with
 *      owner_Account_Id, so the grantee-keyed read (ShardDatabase.GetAccountVaultGrantsForGrantee)
 *      was a full scan without it.
 *   2. granted_By_Character_Guid / granted_By_Character_Name, NULLABLE: the owner-account character
 *      that issued the grant. /mule search all labels a shared mule after that character, never
 *      after some other character on the owner's account. TryGrantCore stamps both from now on.
 *   3. A one-time backfill of those two columns from the latest account_vault_log Grant row
 *      (action 2) for the same owner and grantee - WriteLog records a grant with item_Guid = the
 *      grantee's guid and actor = the granting character. A grant with no such audit row (an audit
 *      write that failed, which the DAO swallows on purpose) stays NULL and is labelled neutrally.
 *
 * RE-RUNNABLE, AND THAT IS REQUIRED. MySQL 8.0 has no ADD COLUMN / CREATE INDEX IF NOT EXISTS, and
 * the boot patcher sends this whole file as one MySqlCommand (no DELIMITER, so no stored procedure
 * needed here), so each DDL is a SET / PREPARE / EXECUTE around an information_schema check - the
 * pattern of Database/Updates/World/2026-08-27-00-Add-Speed-Season-Start-Wcid.sql and
 * Database/Updates/Shard/2026-09-04-00-Add-Account-Vault-Barrel.sql. The `@` variables rely on the
 * patcher connection's AllowUserVariables=true. The backfill only touches rows whose granter is
 * still NULL, so a second run changes nothing it already set.
 *
 * Timestamps are not touched. No FK, matching the rest of the vault tables: the name column is a
 * snapshot so a label still reads after the granting character is deleted.
 */

SET @grantee_idx := (SELECT COUNT(*) FROM information_schema.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault_grant' AND INDEX_NAME = 'account_vault_grant_grantee_idx');
SET @grantee_idx_ddl := IF(@grantee_idx > 0, 'DO 1',
  'ALTER TABLE `account_vault_grant` ADD INDEX `account_vault_grant_grantee_idx` (`grantee_Character_Guid`)');
PREPARE grantee_idx_stmt FROM @grantee_idx_ddl;
EXECUTE grantee_idx_stmt;
DEALLOCATE PREPARE grantee_idx_stmt;

SET @granted_by_guid := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault_grant' AND COLUMN_NAME = 'granted_By_Character_Guid');
SET @granted_by_guid_ddl := IF(@granted_by_guid > 0, 'DO 1',
  'ALTER TABLE `account_vault_grant` ADD COLUMN `granted_By_Character_Guid` int unsigned NULL COMMENT ''Owner-account character that issued the grant; NULL when unknown''');
PREPARE granted_by_guid_stmt FROM @granted_by_guid_ddl;
EXECUTE granted_by_guid_stmt;
DEALLOCATE PREPARE granted_by_guid_stmt;

SET @granted_by_name := (SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'account_vault_grant' AND COLUMN_NAME = 'granted_By_Character_Name');
SET @granted_by_name_ddl := IF(@granted_by_name > 0, 'DO 1',
  'ALTER TABLE `account_vault_grant` ADD COLUMN `granted_By_Character_Name` varchar(255) NULL COMMENT ''Snapshot of the granting character name at grant time''');
PREPARE granted_by_name_stmt FROM @granted_by_name_ddl;
EXECUTE granted_by_name_stmt;
DEALLOCATE PREPARE granted_by_name_stmt;

UPDATE `account_vault_grant` g
SET
  g.`granted_By_Character_Guid` = (
    SELECT l.`actor_Character_Guid` FROM `account_vault_log` l
    WHERE l.`owner_Account_Id` = g.`owner_Account_Id` AND l.`action` = 2 AND l.`item_Guid` = g.`grantee_Character_Guid`
    ORDER BY l.`timestamp` DESC, l.`id` DESC LIMIT 1),
  g.`granted_By_Character_Name` = (
    SELECT l.`actor_Character_Name` FROM `account_vault_log` l
    WHERE l.`owner_Account_Id` = g.`owner_Account_Id` AND l.`action` = 2 AND l.`item_Guid` = g.`grantee_Character_Guid`
    ORDER BY l.`timestamp` DESC, l.`id` DESC LIMIT 1)
WHERE g.`granted_By_Character_Guid` IS NULL;

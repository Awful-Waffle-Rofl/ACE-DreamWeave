/* ACRealms port Phase 2: which landblock instance a persisted position belongs to.
   NULL means instance 0 (the base world), so existing rows are untouched.

   Idempotent: guards the ADD COLUMN behind an information_schema check so re-running
   the script (a deploy that resets applied_updates.txt, a container without a persisted
   Config volume, or a manual re-apply) is a no-op instead of failing with
   "Duplicate column name 'instance'". MySQL 8.0 has no ADD COLUMN IF NOT EXISTS, so the
   guard has to be written by hand, and this file uses a static-body stored procedure (no
   client DELIMITER, no @-user-variables) that the server's multi-statement parser runs as
   one batch.

   That shape is what forces the CI exclusion below, and it is no longer the only option:
   PatchDatabase's connection string now sets AllowUserVariables=true, so a SET / PREPARE /
   EXECUTE guard works there too and needs no CI exclusion. DELIMITER is still unsupported
   either way. Prefer the SET / PREPARE / EXECUTE shape for NEW files - see
   Database/Updates/World/2026-08-27-00-Add-Speed-Season-Start-Wcid.sql. This file keeps the
   stored procedure because rewriting an already-applied migration buys nothing.

   Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
   Database/Updates/*. Do NOT pipe this file through the `mysql` CLI (`mysql < file.sql`):
   the CLI splits on the `;` inside the procedure body and needs a DELIMITER change, which
   the boot patcher neither uses nor allows. */

DROP PROCEDURE IF EXISTS `ace_add_biota_position_instance`;
CREATE PROCEDURE `ace_add_biota_position_instance`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'biota_properties_position'
      AND `COLUMN_NAME` = 'instance'
  ) THEN
    ALTER TABLE `biota_properties_position` ADD COLUMN `instance` INT UNSIGNED NULL DEFAULT NULL AFTER `position_Type`;
  END IF;
END;
CALL `ace_add_biota_position_instance`();
DROP PROCEDURE IF EXISTS `ace_add_biota_position_instance`;

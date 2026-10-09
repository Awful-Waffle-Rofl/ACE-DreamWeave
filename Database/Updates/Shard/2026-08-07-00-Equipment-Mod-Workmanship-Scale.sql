/* Equipment mods gained a workmanship term on 2026-08-07: an item's mod row now stores a ROLL FRACTION
   (potency x workmanship/10) rather than a bare potency. See Docs/EquipmentMods/DESIGN.md section 7.

   New rolls are stamped correctly by the server. This script brings ALREADY-MODDED items onto the same
   footing, so a piece modded before the change is not permanently better than an identical piece modded
   after it.

       value = value * clamp(ItemWorkmanship, 0, 10) / 10

   MEASURED IMPACT, not estimated. On stage the day this was written: 980 modded items, ItemWorkmanship
   averaging 9.99 across them, range 6-10. So the overwhelming majority of rows move by ~0-1% and a
   workmanship 10 item does not move at all. What this closes is a low-workmanship piece keeping mods it
   could no longer earn.

   ITEMS WITH NO WORKMANSHIP SCALE TO ZERO AND THEIR MOD ROWS ARE DELETED. That is deliberate and was
   explicitly approved: 4 such items existed on stage. They cannot be re-modded either (the server now
   refuses a workmanship-less target), so the alternative was leaving them permanently better than anything
   obtainable. The rows are DELETED rather than written as 0.0 because a zeroed row is a dead row that still
   renders on the appraisal panel and still counts toward the item's mod count - the same "clear it, never
   SetProperty(0)" rule the mod systems follow everywhere else.

   NOT A NO-OP TO RE-RUN, WHICH IS WHY THE MARKER EXISTS. Every other guard in this directory keys off
   information_schema, because a schema change can be detected by looking at the schema. A DATA transform
   cannot: a stored 0.7 is indistinguishable from an already-scaled 1.0-on-workmanship-7 and an unscaled
   potency of 0.7. Running twice would scale by workmanship SQUARED. The boot patcher re-runs a file whenever
   applied_updates.txt is absent (a container without a persisted Config volume - see
   reference: Program_DbUpdates.PatchDatabase), so the marker must live in the shard database itself.

   The marker is its own table rather than a row in config_properties_boolean deliberately: PropertyManager
   caches every row of that table and rewrites it, so a marker there is exposed to code that has no idea it
   is load-bearing. Nothing but this script touches ace_shard_migration_marker.

   Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs Database/Updates/*.
   Do NOT pipe this file through the `mysql` CLI (`mysql < file.sql`): the CLI splits on the `;` inside the
   procedure body and needs a DELIMITER change, which the boot patcher neither uses nor allows. */

CREATE TABLE IF NOT EXISTS `ace_shard_migration_marker` (
  `name` VARCHAR(128) NOT NULL,
  `applied_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS `ace_equipment_mod_workmanship_scale`;
CREATE PROCEDURE `ace_equipment_mod_workmanship_scale`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM `ace_shard_migration_marker` WHERE `name` = 'equipment-mod-workmanship-scale'
  ) THEN

    /* Scale every stored equipment-mod value by its item's workmanship. The LEFT JOIN is what makes the
       no-workmanship case fall to zero rather than being skipped: COALESCE gives 0, and 0/10 is 0.

       PropertyInt 105 is ItemWorkmanship. The runtime accessor (WorldObject.Workmanship) divides that by
       NumItemsInMaterial (PropertyInt 170) before use, which this deliberately does NOT reproduce - it was
       verified first that NO modded item on stage carries 170 at all, so the two agree on every row this
       touches, and reproducing a divisor that is always 1 would only add a way to get it wrong.

       biota_properties_int is keyed on (object_Id, type), so the join is one-to-one and cannot fan out. */
    UPDATE `biota_properties_float` `f`
      LEFT JOIN `biota_properties_int` `w`
        ON `w`.`object_Id` = `f`.`object_Id` AND `w`.`type` = 105
    SET `f`.`value` = `f`.`value` * (LEAST(GREATEST(COALESCE(`w`.`value`, 0), 0), 10) / 10.0)
    WHERE `f`.`type` BETWEEN 8100 AND 8199;

    /* A row that scaled to nothing is removed rather than left as a dead 0.0 that still renders. Covers the
       no-workmanship items above, and any row whose potency was already zero. */
    DELETE FROM `biota_properties_float`
    WHERE `type` BETWEEN 8100 AND 8199 AND `value` <= 0;

    INSERT INTO `ace_shard_migration_marker` (`name`) VALUES ('equipment-mod-workmanship-scale');

  END IF;
END;
CALL `ace_equipment_mod_workmanship_scale`();
DROP PROCEDURE IF EXISTS `ace_equipment_mod_workmanship_scale`;

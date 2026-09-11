/* Equipment mods gained a born-with record on 2026-08-26: Obsidian now pays out against
   PropertyInt 9046-9055 (GearDamageOriginal .. GearPKDamageResistRatingOriginal), the value each of the ten
   gear ratings had when the item was created, instead of against the live rating. See
   ACE.Server/EquipmentMods/EquipmentModManager.cs, OriginalGearRatingProperties, for why.

   New items are stamped by the server (WorldObjectFactory.CreateNewWorldObject from the weenie template,
   LootGenerationFactory.TryMutateGearRating from the rolled value). This script stamps everything that
   already exists. Without it every rated item on the shard would read as born with nothing and convert into
   zero mods - a silent loss of something players already hold, not a balance change.

       9046 <- 370 GearDamage                 9051 <- 375 GearCritDamageResist
       9047 <- 371 GearDamageResist           9052 <- 376 GearHealingBoost
       9048 <- 372 GearCrit                   9053 <- 379 GearMaxHealth
       9049 <- 373 GearCritResist             9054 <- 383 GearPKDamageRating
       9050 <- 374 GearCritDamage             9055 <- 384 GearPKDamageResistRating

   THE COPY IS ONLY CORRECT BECAUSE NO GEM HAS EVER BEEN APPLIED, and that window closes the moment one is.
   The premise is that every gear rating currently on the shard is natural - rolled by lootgen or authored on
   a weenie. The thing that could make it false is the retail Luminous/Empowered Amber gems (recipes
   8904-8923), which add rating points to a finished item; a crafted point copied into a stamp becomes
   convertible, which is the exact hole the stamps were added to close. Confirmed with the repo owner on
   2026-08-26 that no player has one of these gems yet.

   NOT A NO-OP TO RE-RUN, WHICH IS WHY THE MARKER EXISTS - and the failure mode is not the usual
   double-application one. Every statement below is INSERT IGNORE against a PRIMARY KEY of (object_Id, type),
   so re-running today changes nothing. The hazard is re-running LATER: an item that has since taken a gem
   carries a crafted rating with no stamp beside it, and a second pass would stamp it and hand the player a
   free mod. The boot patcher re-runs a file whenever applied_updates.txt is absent (a container without a
   persisted Config volume - see Program_DbUpdates.PatchDatabase), so the marker has to live in the shard
   database itself. Same table, and the same reasoning, as 2026-08-07-00-Equipment-Mod-Workmanship-Scale.sql;
   read that file's remarks for why it is a dedicated table rather than a config_properties_boolean row.

   Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs Database/Updates/*.
   Do NOT pipe this file through the `mysql` CLI (`mysql < file.sql`): the CLI splits on the `;` inside the
   procedure body and needs a DELIMITER change, which the boot patcher neither uses nor allows. */

CREATE TABLE IF NOT EXISTS `ace_shard_migration_marker` (
  `name` VARCHAR(128) NOT NULL,
  `applied_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS `ace_backfill_original_gear_ratings`;
CREATE PROCEDURE `ace_backfill_original_gear_ratings`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM `ace_shard_migration_marker` WHERE `name` = 'backfill-original-gear-ratings'
  ) THEN

    /* One statement per rating rather than one JOIN over a mapping table: the ten pairs are a fixed,
       hand-verified list (each id checked against ACE.Entity/Enum/Properties/PropertyInt.cs on 2026-08-26),
       and spelling them out is what makes a wrong pairing visible on review.

       `value` > 0 skips the dead zero rows some biotas carry - a stamp of 0 would be a row that says
       "born with nothing" where no row already says exactly that, and the runtime treats absent and zero
       alike (StampOriginalGearRatings writes no row for a zero rating). */
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9046, `value` FROM `biota_properties_int` WHERE `type` = 370 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9047, `value` FROM `biota_properties_int` WHERE `type` = 371 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9048, `value` FROM `biota_properties_int` WHERE `type` = 372 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9049, `value` FROM `biota_properties_int` WHERE `type` = 373 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9050, `value` FROM `biota_properties_int` WHERE `type` = 374 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9051, `value` FROM `biota_properties_int` WHERE `type` = 375 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9052, `value` FROM `biota_properties_int` WHERE `type` = 376 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9053, `value` FROM `biota_properties_int` WHERE `type` = 379 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9054, `value` FROM `biota_properties_int` WHERE `type` = 383 AND `value` > 0;
    INSERT IGNORE INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT `object_Id`, 9055, `value` FROM `biota_properties_int` WHERE `type` = 384 AND `value` > 0;

    /* An ALREADY-CONVERTED item must not be re-stamped. Conversion spends the stamps and clears the live
       ratings, so a converted piece normally has neither and the statements above pass it by. The case this
       covers is a piece converted BEFORE this change landed: it kept whatever ratings it was not able to
       spend, or took a gem afterwards, and those live ratings have just been stamped as though natural.
       GearModCapacity (9033) is written by a conversion and never cleared, so its presence is the durable
       record that this item has already been paid out for. */
    DELETE `s` FROM `biota_properties_int` `s`
      JOIN `biota_properties_int` `c`
        ON `c`.`object_Id` = `s`.`object_Id` AND `c`.`type` = 9033 AND `c`.`value` > 0
      WHERE `s`.`type` BETWEEN 9046 AND 9055;

    INSERT INTO `ace_shard_migration_marker` (`name`) VALUES ('backfill-original-gear-ratings');

  END IF;
END;
CALL `ace_backfill_original_gear_ratings`();
DROP PROCEDURE IF EXISTS `ace_backfill_original_gear_ratings`;

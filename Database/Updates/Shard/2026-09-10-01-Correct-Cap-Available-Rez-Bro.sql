/* CAP shortfall incident, 2026-09-10: restore the unexplained Class Ability Points two prod
   characters are missing. Round 2 of the CAP audit ledger initiative.

   Dated -01 so the boot patcher's filename ordering runs it AFTER -00-Add-Character-Cap-Ledger.sql,
   which creates `character_cap_ledger`. Both tables therefore exist by the time the ledger row below
   is inserted; reversing the two names would make this script fail on a fresh shard.

   WHAT IS WRONG. The CAP ledger identity is
   TotalClassAbilityPointsEarned - AvailableClassAbilityPoints - ownedRankCost - sinkSpend = 0.
   Two of 252 characters do not balance (re-verified twice against ace_prod at diagnosis time):

       Rez, character id 1342177315: Total (9018) = 22, NO 9017 row at all (so Available = 0),
                                     owned ranks priced 21  ->  1 point unexplained
       Bro, character id 1342177612: Total (9018) = 9, Available (9017) = 2,
                                     owned ranks priced 5   ->  2 points unexplained

   This script credits those 1 and 2 points back. It does NOT attempt to explain them: nothing on the
   shard recorded a single CAP mutation before round 1, which is precisely why the ledger was built,
   and the losing event is unrecoverable. The leading structural suspicion (a learn's point debit
   persisting while its character-side rank write does not, since the two go through two independent
   save paths) is a HYPOTHESIS, not a finding - it is what the ledger's `rank_After` column exists to
   confirm or refute going forward, and nothing here should be read as having established it.

   9017 ONLY. TotalClassAbilityPointsEarned (9018) is deliberately left completely untouched - the
   repo owner was explicit about this - and the obvious vehicle is therefore unusable:
   Player.GrantClassAbilityPoints raises BOTH counters, and 9018 is what
   Player.MeetsClassAbilityTierUnlock reads to gate Tier 2 and Tier 3 access
   (class_ability_tier2_cap_required / class_ability_tier3_cap_required). Crediting through the grant
   path would silently loosen those gates for both characters, handing them tier access they had not
   earned on top of the point they are owed. A restore has to move the spendable balance and nothing
   else, which is what the two statements below do.

   THE `AND name =` CLAUSE IS A SAFETY INTERLOCK, not decoration. The whole per-character statement is
   an INSERT ... SELECT driven off the `character` row, so if either id is wrong (a typo, or a shard
   whose guids differ) the SELECT matches nothing and NOTHING is inserted or updated. Without it a
   mistyped id would silently credit a stranger, and on a shard where that id belongs to somebody else
   the mistake would be invisible. is_Deleted = 0 is part of the same interlock.

   ON DUPLICATE KEY UPDATE covers BOTH cases in one statement, because `biota_properties_int`'s
   PRIMARY KEY is (`object_Id`, `type`) - verified in Database/Base/ShardBase.sql:459. Rez has no 9017
   row, so that statement takes the INSERT branch and creates one holding the delta; Bro has one, so
   his takes the UPDATE branch and adds the delta to the existing value. The `value` = `value` + delta
   form also makes the CREDIT ITSELF immune to the character's state having moved since diagnosis: it
   adds to whatever is there rather than writing an absolute number computed days earlier.

   The one column that CAN drift is informational. `owned_Cost_After` on the two ledger rows is
   written as the hand-verified figures 21 (Rez) and 5 (Bro). Those were verified at diagnosis time on
   2026-09-10 and are stale if either character respecs, unlearns or learns anything between now and
   the boot at which this applies. That is accepted: the column is a forensic snapshot, not an input
   to anything, and every other figure on the row (`available_After`, `total_After`, and the credit
   itself) is read back from the actual rows rather than hardcoded.

   SAFE AGAINST THE IN-MEMORY BIOTA CACHE, because nothing is loaded yet when this runs. The boot
   patcher is invoked from Source/ACE.Server/Program.cs:265-266 (the AutoApplyDatabaseUpdates call
   guarded by ConfigManager.Config.Offline.AutoApplyDatabaseUpdates), while DatabaseManager.Initialize()
   is Program.cs:318 and DatabaseManager.Start() is Program.cs:328 - both AFTER it. So at the moment
   these statements run there is no ShardDatabaseWithCaching, no biota cache entry, no landblock and no
   session that could hold a stale copy of either character and write it back over the credit.

   `populated_Collection_Flags` NEEDS NO TOUCH, for two independent reasons. Both characters already
   carry `biota_properties_int` rows (each has a 9018 row - that is how the shortfall was measured), so
   the BiotaPropertiesInt bit (0x8000) is already set on both and Rez's new 9017 row does not turn on a
   collection that was previously empty. And the field is not authoritative anyway: it is recomputed
   from the staged entity's collections on every save path by
   ShardDatabase.SetBiotaPopulatedCollections (ShardDatabase.cs:195), called from DoSaveBiota
   (ShardDatabase.cs:568), SaveBiotaBatch (ShardDatabase.cs:656) and ShardDatabaseWithCaching.cs:411.

   THE MARKER IS REQUIRED RATHER THAN SELF-DETECTION. This script is NOT idempotent by construction -
   re-running it would credit the same points a second time - and the boot patcher's own
   applied_updates.txt lives in the BUILD OUTPUT while the database is shared, so a container recreate
   or a fresh checkout presents a database that already carries these changes to a ledger that has
   never seen them. The marker row lives in the shard database itself, which is the only place that
   travels with the data. Same table and same reasoning as
   2026-08-26-00-Backfill-Original-Gear-Ratings.sql and 2026-09-01-00-Add-Account-Bank.sql.

   NO DELIMITER STATEMENT. The boot patcher (Program_DbUpdates.PatchDatabase) sends the whole file as
   a single command, so a delimiter change is both unnecessary and unsupported. The procedure idiom is safe for
   a SHARD file specifically: the only place this repo pipes update SQL through the `mysql` CLI is
   .github/workflows/build-test.yml:88, which globs `Database/Updates/World/*.sql` and nothing else -
   no workflow pipes Database/Updates/Shard through a CLI at all. (The remark about needing no stored
   procedure in 2026-09-06-00-Repair-Loadout-Double-Parented-Items.sql is about why THAT script is
   self-detecting and needs no guard; it is not a prohibition on the idiom.)

   Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
   Database/Updates/*. Do NOT pipe this file through the `mysql` CLI. */

CREATE TABLE IF NOT EXISTS `ace_shard_migration_marker` (
  `name` VARCHAR(128) NOT NULL,
  `applied_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS `ace_correct_cap_available_2026_09_10`;
CREATE PROCEDURE `ace_correct_cap_available_2026_09_10`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM `ace_shard_migration_marker` WHERE `name` = 'correct-cap-available-2026-09-10'
  ) THEN

    START TRANSACTION;

    /* ---- Rez, character id 1342177315: +1 to AvailableClassAbilityPoints (9017) ---------------- */

    INSERT INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT 1342177315, 9017, 1
        FROM `character` WHERE `id` = 1342177315 AND `name` = 'Rez' AND `is_Deleted` = 0
      ON DUPLICATE KEY UPDATE `value` = `value` + 1;

    INSERT INTO `character_cap_ledger`
      (`character_Id`, `character_Name`, `ts`, `reason`, `batch_Id`, `delta_Available`, `delta_Total`,
       `available_After`, `total_After`, `owned_Cost_After`, `ability`, `rank_After`, `detail`)
      SELECT
        1342177315,
        `c`.`name`,
        UTC_TIMESTAMP(),
        'admin_correct',
        NULL,
        1,
        0,
        COALESCE((SELECT `value` FROM `biota_properties_int` WHERE `object_Id` = 1342177315 AND `type` = 9017), 0),
        COALESCE((SELECT `value` FROM `biota_properties_int` WHERE `object_Id` = 1342177315 AND `type` = 9018), 0),
        21,
        NULL,
        NULL,
        'CAP shortfall incident 2026-09-10: restored 1 unexplained point; spendable balance only'
        FROM `character` `c`
       WHERE `c`.`id` = 1342177315 AND `c`.`name` = 'Rez' AND `c`.`is_Deleted` = 0;

    /* ---- Bro, character id 1342177612: +2 to AvailableClassAbilityPoints (9017) ---------------- */

    INSERT INTO `biota_properties_int` (`object_Id`, `type`, `value`)
      SELECT 1342177612, 9017, 2
        FROM `character` WHERE `id` = 1342177612 AND `name` = 'Bro' AND `is_Deleted` = 0
      ON DUPLICATE KEY UPDATE `value` = `value` + 2;

    INSERT INTO `character_cap_ledger`
      (`character_Id`, `character_Name`, `ts`, `reason`, `batch_Id`, `delta_Available`, `delta_Total`,
       `available_After`, `total_After`, `owned_Cost_After`, `ability`, `rank_After`, `detail`)
      SELECT
        1342177612,
        `c`.`name`,
        UTC_TIMESTAMP(),
        'admin_correct',
        NULL,
        2,
        0,
        COALESCE((SELECT `value` FROM `biota_properties_int` WHERE `object_Id` = 1342177612 AND `type` = 9017), 0),
        COALESCE((SELECT `value` FROM `biota_properties_int` WHERE `object_Id` = 1342177612 AND `type` = 9018), 0),
        5,
        NULL,
        NULL,
        'CAP shortfall incident 2026-09-10: restored 2 unexplained points; spendable balance only'
        FROM `character` `c`
       WHERE `c`.`id` = 1342177612 AND `c`.`name` = 'Bro' AND `c`.`is_Deleted` = 0;

    INSERT INTO `ace_shard_migration_marker` (`name`) VALUES ('correct-cap-available-2026-09-10');

    COMMIT;

  END IF;
END;
CALL `ace_correct_cap_available_2026_09_10`();
DROP PROCEDURE IF EXISTS `ace_correct_cap_available_2026_09_10`;

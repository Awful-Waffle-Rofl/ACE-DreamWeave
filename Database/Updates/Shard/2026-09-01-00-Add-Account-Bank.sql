/* Account-wide banked pyreals - the two account_bank* tables.
 *
 * Owned by Docs/AccountBank/DESIGN.md. Pyreals move off the per-character biota property
 * PropertyInt64.BankedPyreals (9004) and into one row per ACCOUNT, so every character on an account
 * spends and deposits into the same pool. Luminance, legendary keys and promissory notes are NOT
 * touched by this change and stay per-character on their own 9005-9007 properties.
 *
 * `account_bank` is the pool. One row per account, `account_Id` as the PRIMARY KEY - there is no
 * surrogate id, because the account IS the identity of the row and an AUTO_INCREMENT column would
 * only be a second name for it. `banked_Pyreals` is bigint for the same reason the vault ledger's
 * count is: a banked balance has no ceiling of its own and an int would cap a hoard at ~2.1 billion,
 * which is reachable and is not a limit anyone chose.
 *
 * The balance is only ever moved by a guarded UPDATE that carries its bounds in the WHERE clause
 * (ACE.Database/ShardDatabase_AccountBank.cs), never by a read-modify-write in C#. That is what makes
 * the row the single adjudicator of every deposit, withdraw and vendor spend, and it is why there is
 * no CHECK constraint here: the guard reports a refusal as "zero rows matched", which a CHECK cannot
 * do without throwing.
 *
 * `account_bank_fold` is the migration's idempotency key and its audit trail in one. One row per
 * CHARACTER whose 9004 balance has been folded into its account's pool, keyed by `character_Guid`, so
 * a character can be folded exactly once no matter how many times the bulk SQL and the per-login lazy
 * fold race each other. `amount` records what was owed, which is what makes a crash between the claim
 * and the credit hand-repairable rather than silent.
 *
 * No FK to `character` anywhere, deliberate and load-bearing, following `account_vault` and
 * `character_speed_run`: a fold row must survive the character being deleted, and the bulk migration
 * deliberately folds DELETED characters' balances too (see the fold block below).
 *
 * All timestamps are UTC. The server writes them explicitly; the CURRENT_TIMESTAMP defaults are
 * backstops only and stamp the DATABASE SERVER'S LOCAL time, so any row that falls back to one is off
 * by the local UTC offset.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * Idempotent: safe to re-run. Shard updates are tracked by FILENAME against
 * DatabaseSetupScripts/Updates/Shard/applied_updates.txt, with no ledger table, which is exactly why
 * re-runnability is mandatory rather than polite.
 *
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*. Do NOT pipe this file through the `mysql` CLI (`mysql < file.sql`): the CLI
 * splits on the `;` inside the procedure body added by the fold block and needs a DELIMITER change,
 * which the boot patcher neither uses nor allows.
 */

CREATE TABLE IF NOT EXISTS `account_bank` (
  `account_Id`      int unsigned  NOT NULL,
  `banked_Pyreals`  bigint        NOT NULL DEFAULT 0 COMMENT 'The account-wide pyreal pool. Only ever moved by a guarded UPDATE',
  `updated_At`      datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Account-wide banked pyreals. One row per account. No FK to character by design';

CREATE TABLE IF NOT EXISTS `account_bank_fold` (
  `character_Guid`  int unsigned  NOT NULL COMMENT 'The character whose 9004 balance was folded. One fold per character, ever',
  `account_Id`      int unsigned  NOT NULL,
  `amount`          bigint        NOT NULL COMMENT 'What was owed at fold time - the record that makes a crash mid-fold repairable',
  `folded_At`       datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`character_Guid`),
  KEY `account_bank_fold_account_idx` (`account_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Idempotency key and audit for the 9004 -> account_bank fold. No FK to character by design';

/* ---------------------------------------------------------------------------------------------
   THE FOLD: every existing per-character biota property 9004 (PropertyInt64.BankedPyreals) is
   summed into its account's pool and then removed.

   DELETED CHARACTERS ARE INCLUDED, DELIBERATELY. The join below is `character` to
   biota_properties_int64, and ACE's character deletion is a soft delete - the row survives with
   is_Deleted set - so a player who deleted an alt holding banked pyreals gets that balance back
   into the account pool rather than losing it. This is the owner's decision and it is the whole
   reason the fold table takes no FK to `character`.

   ORDER, AND WHY IT IS NOT THE OBVIOUS ONE. The natural spelling is: claim every character, then
   credit SUM(amount) over the whole fold table. That is WRONG the moment any character has already
   been folded by the per-login lazy path in Player_Bank.InitAccountBank - those rows are already
   paid for, and summing the whole table pays for them a second time. Since the lazy path exists
   precisely because this script may silently never run (the boot patcher records a FAILED script as
   applied), that is not a corner case, it is the expected state on any shard that has been running.

   So the credit comes FIRST and takes its amounts straight from the live 9004 rows of characters
   that have NO fold row yet; the claim rows for exactly that set are written next; and the 9004
   rows are dropped last. All four statements are inside one transaction, so "characters with no
   fold row" means the same set in the first two statements and nothing can observe a half-applied
   fold. The DELETE deliberately covers every folded character, including ones the lazy path folded
   in an earlier session, because their 9004 row is stale either way.

   Value 0 rows are dropped as noise - they carry no money and would otherwise sit there forever
   making the runbook's "trends to 0" check meaningless. A NEGATIVE 9004 is left alone on purpose:
   it cannot be credited, no code path can produce one, and it is evidence.

   NOT A NO-OP TO RE-RUN, WHICH IS WHY THE MARKER EXISTS. The boot patcher re-runs a file whenever
   applied_updates.txt is absent (a container without a persisted Config volume - see
   Program_DbUpdates.PatchDatabase), so the marker has to live in the shard database itself. Same
   table, and the same reasoning, as 2026-08-26-00-Backfill-Original-Gear-Ratings.sql.
   --------------------------------------------------------------------------------------------- */

CREATE TABLE IF NOT EXISTS `ace_shard_migration_marker` (
  `name` VARCHAR(128) NOT NULL,
  `applied_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS `ace_fold_banked_pyreals_to_account`;
CREATE PROCEDURE `ace_fold_banked_pyreals_to_account`()
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM `ace_shard_migration_marker` WHERE `name` = 'fold-banked-pyreals-to-account'
  ) THEN

    START TRANSACTION;

    /* 1. Credit each account with the sum of its UNFOLDED characters' balances. The NOT EXISTS is
          what keeps a character the lazy per-login path already paid for from being paid again. */
    INSERT INTO `account_bank` (`account_Id`, `banked_Pyreals`, `updated_At`)
      SELECT `c`.`account_Id`, SUM(`i`.`value`), UTC_TIMESTAMP()
        FROM `character` `c`
        JOIN `biota_properties_int64` `i` ON `i`.`object_Id` = `c`.`id` AND `i`.`type` = 9004
       WHERE `i`.`value` > 0
         AND NOT EXISTS (SELECT 1 FROM `account_bank_fold` `f` WHERE `f`.`character_Guid` = `c`.`id`)
       GROUP BY `c`.`account_Id`
      ON DUPLICATE KEY UPDATE
        `banked_Pyreals` = `banked_Pyreals` + VALUES(`banked_Pyreals`),
        `updated_At` = UTC_TIMESTAMP();

    /* 2. Record what was folded, for exactly the set credited above - the same predicate, in the
          same transaction, so the two cannot disagree. INSERT IGNORE leaves any pre-existing claim
          row (and its original amount) untouched. */
    INSERT IGNORE INTO `account_bank_fold` (`character_Guid`, `account_Id`, `amount`, `folded_At`)
      SELECT `c`.`id`, `c`.`account_Id`, `i`.`value`, UTC_TIMESTAMP()
        FROM `character` `c`
        JOIN `biota_properties_int64` `i` ON `i`.`object_Id` = `c`.`id` AND `i`.`type` = 9004
       WHERE `i`.`value` > 0;

    /* 3. Drop the now-stale per-character property, for every folded character - including the ones
          folded in an earlier session by the lazy path, whose row is stale for the same reason. */
    DELETE `i` FROM `biota_properties_int64` `i`
      JOIN `account_bank_fold` `f` ON `f`.`character_Guid` = `i`.`object_Id`
     WHERE `i`.`type` = 9004;

    /* 4. And the zero rows, which carry no money and are pure noise. Negatives are left alone. */
    DELETE FROM `biota_properties_int64` WHERE `type` = 9004 AND `value` = 0;

    INSERT INTO `ace_shard_migration_marker` (`name`) VALUES ('fold-banked-pyreals-to-account');

    COMMIT;

  END IF;
END;
CALL `ace_fold_banked_pyreals_to_account`();
DROP PROCEDURE IF EXISTS `ace_fold_banked_pyreals_to_account`;

/* Class Ability Point (CAP) audit ledger, round 1 of the CAP audit ledger initiative.
 *
 * Why this exists: two prod characters hold fewer AvailableClassAbilityPoints than the ledger
 * identity (TotalClassAbilityPointsEarned - ownedCost - sinkSpend) allows, and the losing event
 * is unrecoverable because nothing anywhere records a CAP mutation - every CAP write site
 * (Learn/Unlearn, milestone/enlightenment grants, Luminance/XP purchases, voucher buy/apply/
 * refund, the vendor-currency debit, facet switches, admin grants) only ever touches the two
 * biota properties directly. This migration builds the persistence for that record. A later
 * round funnels every write site through one mutator that appends to `character_cap_ledger`,
 * and credits the two affected characters back.
 *
 * `character_cap_ledger` is the append-only event log: one row per CAP mutation, carrying the
 * before/after state needed to reconstruct the identity above without replaying every row.
 * `character_cap_audit` is a small upserted summary table, one row per CAP-holding character,
 * used by an incident sweep to flag which characters are currently out of balance (unexplained
 * <> 0) without re-scanning the whole ledger every time.
 *
 * `reason` is a varchar, not an int enum column, and that is a DELIBERATE divergence from
 * account_vault_log.Action (see AccountVaultPartial.cs), which IS an int specifically so a future
 * enum member can never break rows already written. This table makes the opposite call because
 * its primary consumer is different: the AccountVaultLog reason is read back and re-interpreted
 * by C# (AccountVaultAction), while this ledger's primary consumer is a human at a MySQL prompt
 * during a CAP-shortfall incident, with no access to the C# enum at all - a readable code
 * ('voucher_refund_retired') answers the question at the prompt, an opaque integer does not. The
 * C# side (CapLedgerReason.ToCode) still pins a fixed string per member so the column stays a
 * closed, spell-checked set in practice; only the storage representation differs from
 * account_vault_log's choice, not the discipline behind it.
 *
 * No foreign key to `character` on either table, same reasoning as character_speed_run and the
 * account_vault_* tables: a ledger row (and an audit summary row) must survive the character
 * being deleted, since a deleted character with an unexplained CAP shortfall is exactly the kind
 * of history an incident investigation still needs to read.
 *
 * `character_Name` on the ledger is a SNAPSHOT taken at write time, not a live join to
 * `character`, for the same no-FK reason - a later rename or delete must never blank out a
 * historical ledger line.
 *
 * `ts` IS UTC. The writer always sets it explicitly (matching ACE.Common's Time.GetUnixTime()
 * clock). The CURRENT_TIMESTAMP column default is only a backstop for a row inserted with the
 * value left unset, and it stamps the DATABASE SERVER'S LOCAL time - so a row that falls back to
 * it is off by the local UTC offset. Always write the column explicitly.
 *
 * `batch_Id` groups the N rows one full respec emits (ClassAbilityTrainer.HandleRespec unlearns
 * every learned ability in one pass) so an incident read can tell "N separate spends" from "one
 * respec that touched N abilities" - NULL for every non-batched reason.
 *
 * Column casing intentionally matches ACE's existing shard tables (e.g. `biota_Id`).
 *
 * Idempotent: safe to re-run. Only ever adds the two tables.
 */

CREATE TABLE IF NOT EXISTS `character_cap_ledger` (
  `id`                int unsigned    NOT NULL AUTO_INCREMENT,
  `character_Id`      int unsigned    NOT NULL,
  `character_Name`    varchar(255)    NOT NULL COMMENT 'Snapshot at write time - survives character rename/delete',
  `ts`                datetime        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `reason`            varchar(32)     NOT NULL COMMENT 'Lowercase snake code from CapLedgerReason.ToCode - readable at a MySQL prompt with no C# enum on hand',
  `batch_Id`          char(32)        NULL COMMENT 'Groups the N rows one full respec emits; NULL for every non-batched reason',
  `delta_Available`   int             NOT NULL COMMENT 'Signed change to AvailableClassAbilityPoints',
  `delta_Total`       int             NOT NULL COMMENT 'Signed change to TotalClassAbilityPointsEarned',
  `available_After`   int             NOT NULL,
  `total_After`       int             NOT NULL,
  `owned_Cost_After`  int             NOT NULL COMMENT 'Cumulative cost of all ranks owned after this mutation',
  `ability`           varchar(64)     NULL COMMENT 'Ability Name, for ability-scoped reasons',
  `rank_After`        int             NULL COMMENT 'Rank of `ability` after this mutation',
  `detail`            varchar(255)    NULL,
  PRIMARY KEY (`id`),
  KEY `character_cap_ledger_character_idx` (`character_Id`, `id`),
  KEY `character_cap_ledger_ts_idx` (`ts`),
  KEY `character_cap_ledger_reason_idx` (`reason`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='CAP audit ledger - append-only, one row per class-ability-point mutation, no FK to character';

CREATE TABLE IF NOT EXISTS `character_cap_audit` (
  `character_Id`          int unsigned    NOT NULL,
  `character_Name`        varchar(255)    NOT NULL COMMENT 'Snapshot at last-checked time - survives character rename/delete',
  `total_Earned`          int             NOT NULL,
  `available`             int             NOT NULL,
  `owned_Cost`            int             NOT NULL,
  `sink_Spend`            int             NOT NULL,
  `unexplained`           int             NOT NULL COMMENT 'total_Earned - available - owned_Cost - sink_Spend; nonzero flags a shortfall',
  `orphan_Rows`           int             NOT NULL,
  `rank_Divergences`      int             NOT NULL,
  `first_Detected_At`     datetime        NULL,
  `last_Checked_At`       datetime        NOT NULL,
  PRIMARY KEY (`character_Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='CAP audit summary - one upserted row per CAP-holding character, no FK to character';

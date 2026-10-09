/* Thread puzzle-gate IP ledger - durable per-key fail and lockout rows for the Thread puzzle-gate
 * fail policy. Read once at server start into the in-memory cache
 * (Source/ACE.Server/ThreadDungeons/ThreadPuzzleIpLedger.cs). Every check is answered from memory
 * and every write lands in memory first, then here on a background task. The table only stores:
 * the threshold, the fail window and the lockout length are owned by the policy code, never here.
 *
 * ip_Key is the policy key the caller computes: an IPv4 address ("203.0.113.7"), an IPv6 /64
 * prefix string, or an account fallback ("acct:12345"). ascii_bin so lookups compare ordinally,
 * exactly as the in-memory dictionary does.
 * kind: 1 = fail (one wrong answer at at_Utc), 2 = lockout (applied at at_Utc, ends at until_Utc).
 * until_Utc is NULL on fail rows. All times are UTC, whole seconds.
 * account_Id, character_Id, run_Id, run_Start_Group and ip_Address are audit-only: nothing reads
 * them back for a decision. run_Id is the run instance id (recycles across restarts),
 * run_Start_Group is the per-gem-use GUID ("N" format) that DungeonRunTelemetry also records.
 *
 * Rows are pruned by the server (fails older than the retention window, lockouts already ended),
 * so the table stays small. No FK, no backfill. Idempotent, only ever adds a table.
 * Apply via the server boot patcher (AutoApplyDatabaseUpdates) - the only path that runs
 * Database/Updates/*.
 */

CREATE TABLE IF NOT EXISTS `thread_puzzle_ip_ledger` (
  `id`               bigint unsigned   NOT NULL AUTO_INCREMENT,
  `ip_Key`           varchar(45)       CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'policy key: IPv4, IPv6 /64 prefix, or acct:<id>',
  `kind`             tinyint unsigned  NOT NULL COMMENT '1 = fail, 2 = lockout',
  `at_Utc`           datetime          NOT NULL COMMENT 'when the fail happened or the lockout was applied',
  `until_Utc`        datetime          NULL COMMENT 'lockout end, NULL on fail rows',
  `account_Id`       int unsigned      NULL COMMENT 'audit only',
  `character_Id`     int unsigned      NULL COMMENT 'audit only',
  `run_Id`           int unsigned      NULL COMMENT 'audit only: run instance id, recycles across restarts',
  `run_Start_Group`  char(32)          CHARACTER SET ascii COLLATE ascii_bin NULL COMMENT 'audit only: per-gem-use GUID',
  `ip_Address`       varchar(45)       NULL COMMENT 'audit only: the raw remote address',
  PRIMARY KEY (`id`),
  KEY `thread_puzzle_ip_ledger_key_kind_at_idx` (`ip_Key`, `kind`, `at_Utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Thread puzzle-gate per-IP fails and lockouts';

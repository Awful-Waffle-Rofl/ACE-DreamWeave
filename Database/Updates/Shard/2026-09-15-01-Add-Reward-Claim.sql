/* Reward claim ledger - once per account and once per IP for a hardcoded set of claim keys.
 * Written by the ClaimRewardOnce emote (EmoteType 9003). The allowed keys live in code
 * (Source/ACE.Server/Entity/RewardClaims/RewardClaimAllowlist.cs), never in this table.
 * PRIMARY KEY (claim_Key, account_Id) makes a second claim from one account a 1062.
 * UNIQUE KEY (claim_Key, ip_Key) makes a second claim from one normalized address a 1062.
 * ip_Key is NULL for a session exempt from the IP active-player limit, and InnoDB admits any
 * number of NULLs under a UNIQUE key, so an exempt claim never consumes the address.
 * claim_Token is a per-attempt random value that lets the writer recognize its own row when a
 * retried INSERT reports a duplicate against a row it already committed.
 * No FK, no backfill. Idempotent, only ever adds a table.
 */

CREATE TABLE IF NOT EXISTS `reward_claim` (
  `claim_Key`     varchar(64)   CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'allowlisted claim key, case-sensitive',
  `account_Id`    int unsigned  NOT NULL,
  `ip_Key`        varchar(45)   CHARACTER SET ascii COLLATE ascii_bin NULL COMMENT 'normalized remote address, NULL when the session was IP-limit exempt',
  `ip_Address`    varchar(45)   NULL COMMENT 'audit copy of the remote address, kept even when exempt',
  `character_Id`  int unsigned  NOT NULL,
  `npc_Wcid`      int unsigned  NOT NULL,
  `claim_Token`   char(32)      CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'per-attempt token, recognizes a retried insert',
  `claimed_At`    datetime      NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`claim_Key`, `account_Id`),
  UNIQUE KEY `reward_claim_ip_uidx` (`claim_Key`, `ip_Key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Once-per-account and once-per-IP reward claims';

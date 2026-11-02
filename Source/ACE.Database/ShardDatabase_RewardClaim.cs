using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>What one reward_claim INSERT did.</summary>
    public enum RewardClaimInsertResult
    {
        /// <summary>The row was written by this call.</summary>
        Inserted,

        /// <summary>
        /// MySQL refused the row with 1062: an existing row already holds this (key, account) or this
        /// (key, ip). That row MAY be this attempt's own, committed by an earlier try whose ack was
        /// lost - the caller must read the conflicting rows and compare claim tokens before denying.
        /// </summary>
        Duplicate,

        /// <summary>Any other failure. Whether anything committed is unknown.</summary>
        Failed
    }

    /// <summary>What one account's reward_claim re-key (<see cref="ShardDatabase.TryRekeyRewardClaims"/>) did.</summary>
    public sealed class RewardClaimRekeyResult
    {
        /// <summary>Rows of the account with a non-null ip_Key, i.e. the rows the re-key looked at.</summary>
        public int Examined { get; set; }

        /// <summary>Rows moved to their new household key.</summary>
        public int Rekeyed { get; set; }

        /// <summary>Rows whose new (claim_Key, ip_Key) was already held by another account, so ip_Key went NULL.</summary>
        public int NulledByCollision { get; set; }

        /// <summary>Rows with no usable key at all (no stored address, or one that does not parse), so ip_Key went NULL.</summary>
        public int NulledNoKey { get; set; }

        /// <summary>Rows already on their target key.</summary>
        public int Unchanged { get; set; }
    }

    /// <summary>
    /// Reward claim ledger DAO (the reward_claim table). A ledger, not a biota operation, so like the
    /// account bank DAO it deliberately does NOT run through SerializedShardDatabase: every call opens
    /// its own ShardDbContext.
    ///
    /// The claim itself is one plain INSERT adjudicated by the table's two unique keys, so it needs no
    /// transaction: the database both applies and refuses in a single statement. The one multi-statement
    /// write, <see cref="TryRekeyRewardClaims"/>, opens its transaction INSIDE the execution strategy:
    /// ShardDbContext.OnConfiguring enables EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses
    /// a bare user-initiated transaction outright (see the repo CLAUDE.md).
    /// </summary>
    public partial class ShardDatabase
    {
        private const string InsertRewardClaimSql =
            "INSERT INTO `reward_claim` (`claim_Key`, `account_Id`, `ip_Key`, `ip_Address`, `character_Id`, `npc_Wcid`, `claim_Token`, `claimed_At`) " +
            "VALUES (@claimKey, @accountId, @ipKey, @ipAddress, @characterId, @npcWcid, @claimToken, UTC_TIMESTAMP())";

        /// <summary>
        /// Attempts to record one claim. A plain INSERT, never an upsert: the PRIMARY KEY
        /// (claim_Key, account_Id) and the UNIQUE KEY (claim_Key, ip_Key) turn a repeat into MySQL 1062,
        /// which is reported as <see cref="RewardClaimInsertResult.Duplicate"/>. A NULL
        /// <c>row.IpKey</c> is bound as SQL NULL, which the UNIQUE key never compares equal.
        /// </summary>
        public RewardClaimInsertResult TryInsertRewardClaim(RewardClaim row)
        {
            if (row == null || string.IsNullOrEmpty(row.ClaimKey) || row.AccountId == 0 || string.IsNullOrEmpty(row.ClaimToken))
            {
                log.Error("[REWARDCLAIM] TryInsertRewardClaim called with a null row, an empty key, account 0, or an empty token.");
                return RewardClaimInsertResult.Failed;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    // Explicit MySqlParameters rather than {0} placeholders, so the NULL ip_Key is bound as
                    // DBNull.Value by construction instead of depending on how EF maps a null CLR argument.
                    context.Database.ExecuteSqlRaw(InsertRewardClaimSql,
                        new MySqlParameter("@claimKey", row.ClaimKey),
                        new MySqlParameter("@accountId", row.AccountId),
                        new MySqlParameter("@ipKey", (object)row.IpKey ?? DBNull.Value),
                        new MySqlParameter("@ipAddress", (object)row.IpAddress ?? DBNull.Value),
                        new MySqlParameter("@characterId", row.CharacterId),
                        new MySqlParameter("@npcWcid", row.NpcWcid),
                        new MySqlParameter("@claimToken", row.ClaimToken));
                }

                return RewardClaimInsertResult.Inserted;
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                return RewardClaimInsertResult.Duplicate;
            }
            catch (Exception ex)
            {
                log.Error($"[REWARDCLAIM] TryInsertRewardClaim failed for key {row.ClaimKey}, account {row.AccountId}: {ex.GetFullMessage()}");
                return RewardClaimInsertResult.Failed;
            }
        }

        /// <summary>
        /// The rows for <paramref name="claimKey"/> that match EITHER <paramref name="accountId"/> OR
        /// <paramref name="ipKey"/> (a null filter matches nothing on its side). With both filters null,
        /// every row for the key. FALSE means the read FAILED; TRUE with an empty list means no match.
        /// </summary>
        public bool TryGetRewardClaims(string claimKey, uint? accountId, string ipKey, out List<RewardClaim> rows)
        {
            rows = null;

            if (string.IsNullOrEmpty(claimKey))
            {
                rows = new List<RewardClaim>();
                return true;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    rows = FilterRewardClaims(context.RewardClaim, claimKey, accountId, ipKey)
                        .OrderBy(r => r.ClaimedAt)
                        .ToList();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[REWARDCLAIM] TryGetRewardClaims failed for key {claimKey}, account {accountId?.ToString() ?? "-"}, ip {ipKey ?? "-"}: {ex.GetFullMessage()}");
                rows = null;
                return false;
            }
        }

        /// <summary>
        /// Deletes the WHOLE rows for <paramref name="claimKey"/> that match <paramref name="accountId"/>
        /// OR <paramref name="ipKey"/>, which frees both the account and the IP each row held for that key.
        /// Refuses (FALSE, nothing deleted) when both filters are null, so a mistyped call can never wipe a
        /// whole key. FALSE otherwise means the delete FAILED.
        /// </summary>
        public bool DeleteRewardClaims(string claimKey, uint? accountId, string ipKey, out int deleted)
        {
            deleted = 0;

            if (string.IsNullOrEmpty(claimKey) || (!accountId.HasValue && ipKey == null))
            {
                log.Error("[REWARDCLAIM] DeleteRewardClaims refused: an empty key, or neither an account nor an ip filter.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    deleted = FilterRewardClaims(context.RewardClaim, claimKey, accountId, ipKey).ExecuteDelete();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[REWARDCLAIM] DeleteRewardClaims failed for key {claimKey}, account {accountId?.ToString() ?? "-"}, ip {ipKey ?? "-"}: {ex.GetFullMessage()}");
                return false;
            }
        }

        private const string UpdateRewardClaimIpKeySql =
            "UPDATE `reward_claim` SET `ip_Key` = @ipKey WHERE `claim_Key` = @claimKey AND `account_Id` = @accountId";

        /// <summary>
        /// Re-keys every reward_claim row of <paramref name="accountId"/> whose ip_Key is NOT NULL (a NULL ip_Key
        /// was written under a bypass and is never touched). For each row, <paramref name="decide"/>(row, false)
        /// names the target ip_Key. A row already on its target is left alone. Otherwise the row is UPDATEd, and
        /// the table's UNIQUE (claim_Key, ip_Key) adjudicates: if another account already holds the target, MySQL
        /// refuses with 1062 and the row is written with <paramref name="decide"/>(row, true) instead (NULL, by the
        /// carry-forward rules). The row itself is never deleted, so the PRIMARY KEY (claim_Key, account_Id) keeps
        /// the account from claiming the key twice.
        ///
        /// Atomic per account: the rows are rewritten in one transaction, opened INSIDE the execution strategy.
        /// The delegate is idempotent - it re-reads the rows and every UPDATE writes an absolute value decided from
        /// the row's own claim_Key and ip_Address - so a strategy retry converges on the same final state (only the
        /// counts of a retry that follows a lost commit ack can under-report). A 1062 rolls back only its own
        /// statement in InnoDB, so the transaction survives the collision probe.
        ///
        /// The read is a plain (non-locking) snapshot read, deliberately NOT FOR UPDATE: no index leads with
        /// account_Id, so a locking read would scan the clustered index and next-key-lock every row of the table
        /// until commit, stalling every concurrent claim INSERT. The lock would buy nothing: each UPDATE is
        /// addressed by the PRIMARY KEY and writes an absolute value, and the UNIQUE key adjudicates collisions at
        /// write time against whatever is committed then. A row deleted meanwhile updates nothing. A claim the
        /// same account inserts meanwhile is simply not in the snapshot; a locking read would only have delayed
        /// that INSERT until after the commit, leaving it equally un-rekeyed, so neither shape covers it. Re-keys
        /// of one account are serialized by the caller (IpLimitCommands' single carry-forward chain).
        ///
        /// FALSE means the re-key FAILED and nothing was written; <paramref name="result"/> is then null.
        /// Never throws.
        /// </summary>
        public bool TryRekeyRewardClaims(uint accountId, Func<RewardClaim, bool, string> decide, out RewardClaimRekeyResult result)
        {
            result = null;

            if (accountId == 0 || decide == null)
            {
                log.Error("[REWARDCLAIM] TryRekeyRewardClaims called with account 0 or no decision.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    var strategy = context.Database.CreateExecutionStrategy();

                    result = strategy.Execute(() =>
                    {
                        var counts = new RewardClaimRekeyResult();

                        using (var transaction = context.Database.BeginTransaction())
                        {
                            // Ordered by claim_Key so the processing order is deterministic (and testable).
                            var rows = context.RewardClaim
                                .AsNoTracking()
                                .Where(r => r.AccountId == accountId && r.IpKey != null)
                                .OrderBy(r => r.ClaimKey)
                                .ToList();

                            counts.Examined = rows.Count;

                            foreach (var row in rows)
                            {
                                var target = decide(row, false);

                                if (string.Equals(target, row.IpKey, StringComparison.Ordinal))
                                {
                                    counts.Unchanged++;
                                    continue;
                                }

                                if (target == null)
                                {
                                    SetRewardClaimIpKey(context, row, null);
                                    counts.NulledNoKey++;
                                    continue;
                                }

                                try
                                {
                                    SetRewardClaimIpKey(context, row, target);
                                    counts.Rekeyed++;
                                }
                                catch (Exception ex) when (IsDuplicateKeyError(ex))
                                {
                                    // A fallback that is itself held throws again and rolls the whole account back.
                                    SetRewardClaimIpKey(context, row, decide(row, true));
                                    counts.NulledByCollision++;
                                }
                            }

                            transaction.Commit();
                        }

                        return counts;
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[REWARDCLAIM] TryRekeyRewardClaims failed for account {accountId}; nothing was re-keyed: {ex.GetFullMessage()}");
                result = null;
                return false;
            }
        }

        private static void SetRewardClaimIpKey(ShardDbContext context, RewardClaim row, string ipKey)
        {
            context.Database.ExecuteSqlRaw(UpdateRewardClaimIpKeySql,
                new MySqlParameter("@ipKey", (object)ipKey ?? DBNull.Value),
                new MySqlParameter("@claimKey", row.ClaimKey),
                new MySqlParameter("@accountId", row.AccountId));
        }

        private static IQueryable<RewardClaim> FilterRewardClaims(IQueryable<RewardClaim> source, string claimKey, uint? accountId, string ipKey)
        {
            var query = source.Where(r => r.ClaimKey == claimKey);

            if (!accountId.HasValue && ipKey == null)
                return query;

            if (accountId.HasValue && ipKey != null)
            {
                var account = accountId.Value;
                return query.Where(r => r.AccountId == account || r.IpKey == ipKey);
            }

            if (accountId.HasValue)
            {
                var account = accountId.Value;
                return query.Where(r => r.AccountId == account);
            }

            return query.Where(r => r.IpKey == ipKey);
        }
    }
}

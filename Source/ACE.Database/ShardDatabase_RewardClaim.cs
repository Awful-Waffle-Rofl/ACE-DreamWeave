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

    /// <summary>
    /// Reward claim ledger DAO (the reward_claim table). A ledger, not a biota operation, so like the
    /// account bank DAO it deliberately does NOT run through SerializedShardDatabase: every call opens
    /// its own ShardDbContext and runs exactly one statement.
    ///
    /// NOTHING here may open a user-initiated EF transaction. ShardDbContext.OnConfiguring enables
    /// EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses one outright (see the repo
    /// CLAUDE.md). The claim is one plain INSERT adjudicated by the table's two unique keys, so it needs
    /// no transaction: the database both applies and refuses in a single statement.
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

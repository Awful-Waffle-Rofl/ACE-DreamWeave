using System.Collections.Generic;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Entity.RewardClaims
{
    /// <summary>The reward_claim persistence seam, so the service and the admin command can be tested against a fake.</summary>
    public interface IRewardClaimStore
    {
        RewardClaimInsertResult TryInsert(RewardClaim row);

        /// <summary>Rows for the key matching the account OR the ip (null filters match nothing). FALSE = the read failed.</summary>
        bool TryGetClaims(string claimKey, uint? accountId, string ipKey, out List<RewardClaim> rows);

        /// <summary>Deletes whole matching rows. FALSE = refused or failed.</summary>
        bool Delete(string claimKey, uint? accountId, string ipKey, out int deleted);
    }

    /// <summary>Production store: the shard DAO, reached through the base database so it bypasses SerializedShardDatabase.</summary>
    public sealed class ShardRewardClaimStore : IRewardClaimStore
    {
        public RewardClaimInsertResult TryInsert(RewardClaim row)
            => DatabaseManager.Shard.BaseDatabase.TryInsertRewardClaim(row);

        public bool TryGetClaims(string claimKey, uint? accountId, string ipKey, out List<RewardClaim> rows)
            => DatabaseManager.Shard.BaseDatabase.TryGetRewardClaims(claimKey, accountId, ipKey, out rows);

        public bool Delete(string claimKey, uint? accountId, string ipKey, out int deleted)
            => DatabaseManager.Shard.BaseDatabase.DeleteRewardClaims(claimKey, accountId, ipKey, out deleted);
    }
}

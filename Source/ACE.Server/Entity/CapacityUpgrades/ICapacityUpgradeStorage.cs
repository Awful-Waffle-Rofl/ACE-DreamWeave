using System;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;

namespace ACE.Server.Entity.CapacityUpgrades
{
    /// <summary>
    /// AccountCapacityUpgradeManager's view of the two account_capacity_upgrade* tables. One method per
    /// DAO method, same signatures, so <see cref="ShardCapacityUpgradeStorage"/> is a thin delegation
    /// and the tests can drive a fake with no MySQL instance anywhere.
    /// </summary>
    public interface ICapacityUpgradeStorage
    {
        /// <summary>
        /// Every count the account owns, keyed by kind; an absent kind is 0. Ok = false means the READ
        /// FAILED, which is never "owns nothing".
        /// </summary>
        (bool Ok, Dictionary<CapacityUpgradeKind, int> Counts) GetAccountCapacityUpgrades(uint accountId);

        /// <summary>
        /// Debit, guarded increment and purchase row in one transaction. See
        /// <see cref="ShardDatabase.TryPurchaseCapacityUpgrade"/> for the contract of each result.
        /// <paramref name="newBalance"/> is meaningful only for Applied.
        /// </summary>
        CapacityUpgradePurchaseResult TryPurchaseCapacityUpgrade(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, string token, uint characterGuid, out long newBalance);
    }

    /// <summary>
    /// Production storage: a straight delegation to the DAO on <see cref="DatabaseManager.Shard"/>'s
    /// base <see cref="ShardDatabase"/>. Ledger operations, not biota operations, so they deliberately
    /// do NOT go through SerializedShardDatabase's worker thread.
    /// </summary>
    public class ShardCapacityUpgradeStorage : ICapacityUpgradeStorage
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static ShardDatabase Db => DatabaseManager.Shard.BaseDatabase;

        public (bool Ok, Dictionary<CapacityUpgradeKind, int> Counts) GetAccountCapacityUpgrades(uint accountId)
        {
            try
            {
                return Db.GetAccountCapacityUpgrades(accountId);
            }
            catch (Exception ex)
            {
                log.Error($"[UPGRADE] GetAccountCapacityUpgrades({accountId}) failed: {ex.GetFullMessage()}");
                return (false, null);
            }
        }

        public CapacityUpgradePurchaseResult TryPurchaseCapacityUpgrade(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, string token, uint characterGuid, out long newBalance)
        {
            // Deliberately NOT wrapped, the same choice ShardAccountBankBackend makes for its adjust: the
            // DAO classifies its own failures, and anything that still escapes surfaces in the manager,
            // whose bank gate reports it as unknown.
            return Db.TryPurchaseCapacityUpgrade(accountId, kind, expectedCount, costMmd, costPyreals, token, characterGuid, out newBalance);
        }
    }
}

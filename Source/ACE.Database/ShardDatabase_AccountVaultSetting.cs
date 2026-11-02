using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Account vault settings - the account_vault_setting table
    /// (Database/Updates/Shard/2026-10-09-00-Add-Account-Vault-Setting.sql).
    ///
    /// An index operation, not a biota operation, so like ShardDatabase_AccountVault and
    /// ShardDatabase_AccountMuleForm it deliberately does not run through SerializedShardDatabase's
    /// worker thread. The caller serializes per account itself.
    ///
    /// NOTHING in this file may open a user-initiated EF transaction. ShardDbContext.OnConfiguring
    /// enables EnableRetryOnFailure and MySqlRetryingExecutionStrategy refuses one outright. Both
    /// statements here are a single LINQ read and a single ExecuteSqlRaw, each its own implicit
    /// transaction.
    ///
    /// READ FAILURE CONTRACT. The read returns a two-channel answer rather than a bare reference,
    /// because for a single-row lookup "no row" and "the read failed" are both naturally null and
    /// they mean opposite things. Every caller branches on Ok before it branches on Row.
    ///
    /// MISSING ROW = DEFAULTS. This DAO does not decide what a missing row means; it reports
    /// <c>(true, null)</c> and stops. The store layer above it is where "no row" becomes
    /// "salvage combine ON" (and the default for any later setting column). Keeping the default out
    /// of here means a failed read can never be mistaken for, or cached as, a default.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Writes one account's full settings row, creating it if absent. A single
        /// INSERT ... ON DUPLICATE KEY UPDATE against the account_Id PRIMARY KEY, so it is atomic at
        /// the row. updated_At is stamped here with UTC_TIMESTAMP(); the entity's value is ignored.
        /// </summary>
        private const string UpsertAccountVaultSettingSql =
            "INSERT INTO `account_vault_setting` (`account_Id`, `salvage_Combine`, `set_By_Character_Guid`, `updated_At`) " +
            "VALUES ({0}, {1}, {2}, UTC_TIMESTAMP()) " +
            "ON DUPLICATE KEY UPDATE `salvage_Combine` = VALUES(`salvage_Combine`), " +
            "`set_By_Character_Guid` = VALUES(`set_By_Character_Guid`), `updated_At` = VALUES(`updated_At`)";

        /// <summary>
        /// One account's vault settings row.
        ///
        /// Returns <c>(true, null)</c> when the account has no row - the ordinary state of every
        /// account that has never changed a setting, which the store layer reads as "all defaults" -
        /// and <c>(false, null)</c> when the READ FAILED. Never throws to the caller.
        /// </summary>
        public (bool Ok, AccountVaultSetting Row) GetAccountVaultSetting(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return (true, context.AccountVaultSetting.FirstOrDefault(s => s.AccountId == accountId));
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULTSETTING] GetAccountVaultSetting failed for account {accountId}: {ex.GetFullMessage()}");
                return (false, null);
            }
        }

        /// <summary>
        /// Saves or replaces one account's settings row. Every setting column is written from
        /// <paramref name="row"/>, so a caller changing one setting must pass the others at their
        /// current values. Returns false if the write failed; never throws to the caller.
        /// </summary>
        public bool UpsertAccountVaultSetting(AccountVaultSetting row)
        {
            if (row == null)
            {
                log.Error("[VAULTSETTING] UpsertAccountVaultSetting called with a null row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Database.ExecuteSqlRaw(
                        UpsertAccountVaultSettingSql,
                        row.AccountId,
                        row.SalvageCombine,
                        row.SetByCharacterGuid);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULTSETTING] UpsertAccountVaultSetting failed for account {row.AccountId}, salvageCombine {row.SalvageCombine}: {ex.GetFullMessage()}");
                return false;
            }
        }
    }
}

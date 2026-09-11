using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Mule Form Token - the account_mule_form table
    /// (Docs/MuleVendor/2026-09-01-mule-form-token-design.md section 5.2).
    ///
    /// An index operation, not a biota operation, so like ShardDatabase_AccountVault it deliberately
    /// does not run through SerializedShardDatabase's worker thread. The caller is AccountVaultStore,
    /// which serializes per store itself.
    ///
    /// NOTHING in this file may open a user-initiated EF transaction. ShardDbContext.OnConfiguring
    /// enables EnableRetryOnFailure and MySqlRetryingExecutionStrategy refuses one outright. Both
    /// statements here are a single LINQ read and a single ExecuteSqlRaw, each its own implicit
    /// transaction.
    ///
    /// READ FAILURE CONTRACT. The read returns a two-channel answer rather than a bare reference,
    /// because for a single-row lookup "no row" and "the read failed" are both naturally null and
    /// they mean opposite things: no row is the ordinary state of an account that has never earned a
    /// form, while a failure must be logged and must not be cached as a decision. Every caller
    /// branches on Ok before it branches on Row.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Replaces one account's saved look, creating the row if absent. A single
        /// INSERT ... ON DUPLICATE KEY UPDATE against the account_Id PRIMARY KEY, so it is atomic at
        /// the row and needs no read-modify-write: the previous look is simply overwritten, and there
        /// is no accumulated value here that a lost update could destroy.
        /// </summary>
        private const string UpsertAccountMuleFormSql =
            "INSERT INTO `account_mule_form` (`account_Id`, `form_Wcid`, `form_Name`, `set_By_Character_Guid`, `set_Unix_Time`) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}) " +
            "ON DUPLICATE KEY UPDATE `form_Wcid` = VALUES(`form_Wcid`), `form_Name` = VALUES(`form_Name`), " +
            "`set_By_Character_Guid` = VALUES(`set_By_Character_Guid`), `set_Unix_Time` = VALUES(`set_Unix_Time`)";

        /// <summary>
        /// One account's saved mule form.
        ///
        /// Returns <c>(true, null)</c> when the account has no saved look, which is the ordinary state
        /// of every account that has never completed a token, and <c>(false, null)</c> when the READ
        /// FAILED. The two must never be conflated: the caller caches "no look" for the store's
        /// lifetime, and caching a transient database failure that way would silently strip an
        /// earned look from every summon until the process restarts.
        /// </summary>
        public (bool Ok, AccountMuleForm Row) GetAccountMuleForm(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return (true, context.AccountMuleForm.FirstOrDefault(f => f.AccountId == accountId));
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MULEFORM] GetAccountMuleForm failed for account {accountId}: {ex.GetFullMessage()}");
                return (false, null);
            }
        }

        /// <summary>
        /// Saves or replaces one account's look. Returns false if the write failed, in which case the
        /// caller must leave its cached look alone and tell the player the vault is unavailable.
        /// </summary>
        public bool UpsertAccountMuleForm(AccountMuleForm row)
        {
            if (row == null)
            {
                log.Error("[MULEFORM] UpsertAccountMuleForm called with a null row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Database.ExecuteSqlRaw(
                        UpsertAccountMuleFormSql,
                        row.AccountId,
                        row.FormWcid,
                        row.FormName ?? string.Empty,
                        row.SetByCharacterGuid,
                        row.SetUnixTime);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MULEFORM] UpsertAccountMuleForm failed for account {row.AccountId}, wcid {row.FormWcid}: {ex.GetFullMessage()}");
                return false;
            }
        }
    }
}

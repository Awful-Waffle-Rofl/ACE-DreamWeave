using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Read-only account_vault scans that exist for OFFLINE MEASUREMENT rather than for the running
    /// vault, kept in their own file so they cannot collide with ShardDatabase_AccountVault.cs.
    ///
    /// Nothing here is on any player's path. The one caller is the vault-class dry run, an admin
    /// command whose whole contract is that it mutates nothing.
    ///
    /// Same read-failure contract as ShardDatabase_AccountVault.cs, and for the same reason: NULL means
    /// the read FAILED and an EMPTY list means there genuinely are none. A caller that conflated them
    /// would report "no accounts hold a vault" after a transient database blip and the measurement
    /// would read as a decisive zero.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Every account id that owns at least one vault container, ascending.
        ///
        /// Returns NULL if the read failed. Distinct at the database rather than in memory, because an
        /// account with many vaults would otherwise return one row per container.
        /// </summary>
        public List<uint> GetAllAccountVaultAccountIds()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AccountVault.Select(v => v.AccountId).Distinct().OrderBy(id => id).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] GetAllAccountVaultAccountIds failed: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}

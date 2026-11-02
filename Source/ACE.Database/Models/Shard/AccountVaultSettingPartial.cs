using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Account vault settings: the account_vault_setting table created by
    // Database/Updates/Shard/2026-10-09-00-Add-Account-Vault-Setting.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following AccountMuleFormPartial.cs
    // and AccountCapacityUpgradePartial.cs. This is the established shape for a FORK-custom shard
    // table; the scaffolding flow described in CLAUDE.md applies to ACE's own tables.

    /// <summary>
    /// One account's /mule vault preferences. Keyed by account_Id: the account is the identity of the
    /// row, so there is no surrogate id, and changing a setting is an upsert against that key.
    ///
    /// A missing row means every setting is at its default. That default is applied by the server's
    /// store layer, not here; the DAO only reports whether a row exists.
    /// </summary>
    public partial class AccountVaultSetting
    {
        public uint AccountId { get; set; }

        /// <summary>
        /// Whether salvage deposited into the vault is combined automatically
        /// (<c>/mule autosalvage on|off</c>). Stored as bit(1); the column default is b'1' (ON),
        /// matching the store-layer default for an account with no row.
        /// </summary>
        public bool SalvageCombine { get; set; } = true;

        /// <summary>Snapshot of the character that last changed a setting. Audit only, no FK, never read.</summary>
        public uint SetByCharacterGuid { get; set; }

        /// <summary>
        /// UTC. Stamped by the writer with UTC_TIMESTAMP(); the value on an entity passed to the upsert
        /// is ignored. The column default is a local-time backstop only.
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// A member-for-member copy. The store builds its upsert from a copy of the row it last read, so a
        /// column added later is carried through a change to an older one instead of being reset to its
        /// default; MemberwiseClone copies every column without this method having to list them.
        /// </summary>
        public AccountVaultSetting ShallowCopy() => (AccountVaultSetting)MemberwiseClone();
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AccountVaultSetting> AccountVaultSetting { get; set; }

        internal static void ConfigureAccountVaultSetting(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountVaultSetting>(entity =>
            {
                entity.ToTable("account_vault_setting");
                entity.HasKey(e => e.AccountId).HasName("PRIMARY");

                // ValueGeneratedNever: account_Id is supplied by the caller and the column carries no
                // AUTO_INCREMENT. EF infers store generation for an integer key by convention, and an
                // inferred store-generated key would make it omit the column from an INSERT.
                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                // The SQL DEFAULT b'1' is a backstop only, not told to EF, matching how
                // account_vault_grant.can_Withdraw and pvp_template.enabled are mapped.
                entity.Property(e => e.SalvageCombine)
                    .HasColumnName("salvage_Combine")
                    .HasColumnType("bit(1)");
                entity.Property(e => e.SetByCharacterGuid).HasColumnName("set_By_Character_Guid");
                entity.Property(e => e.UpdatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("updated_At");
            });
        }
    }
}

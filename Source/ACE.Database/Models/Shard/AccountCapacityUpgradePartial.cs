using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Account capacity upgrades: the two tables created by
    // Database/Updates/Shard/2026-09-17-00-Add-Account-Capacity-Upgrade.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following AccountMuleFormPartial.cs
    // and AccountBankPartial.cs - the established shape for a FORK-custom shard table.

    /// <summary>
    /// How many capacity upgrades of one kind one account owns. Keyed by (account_Id, upgrade_Kind).
    ///
    /// NOTHING should read this entity to compute a new count and write it back. The count moves only
    /// inside ShardDatabase.TryPurchaseCapacityUpgrade's transaction, as a guarded increment in the
    /// same commit as the bank debit.
    /// </summary>
    public partial class AccountCapacityUpgrade
    {
        public uint AccountId { get; set; }

        /// <summary>ACE.Database.CapacityUpgradeKind, stored as tinyint unsigned.</summary>
        public byte UpgradeKind { get; set; }

        public uint UpgradeCount { get; set; }

        /// <summary>UTC. The column default is a local-time backstop; writers stamp it explicitly.</summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// One capacity upgrade purchase. Keyed by the per-attempt purchase token, which is what lets a
    /// retried purchase transaction recognize that it already committed.
    /// </summary>
    public partial class AccountCapacityUpgradePurchase
    {
        /// <summary>32 hex chars, random per attempt.</summary>
        public string PurchaseToken { get; set; }

        public uint AccountId { get; set; }

        /// <summary>ACE.Database.CapacityUpgradeKind, stored as tinyint unsigned.</summary>
        public byte UpgradeKind { get; set; }

        /// <summary>The account's count for this kind AFTER this purchase.</summary>
        public uint UpgradeNumber { get; set; }

        public long CostMmd { get; set; }

        public long CostPyreals { get; set; }

        /// <summary>The buying character. Audit only, no FK.</summary>
        public uint CharacterGuid { get; set; }

        /// <summary>UTC. The column default is a local-time backstop; writers stamp it explicitly.</summary>
        public DateTime PurchasedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AccountCapacityUpgrade> AccountCapacityUpgrade { get; set; }
        public virtual DbSet<AccountCapacityUpgradePurchase> AccountCapacityUpgradePurchase { get; set; }

        internal static void ConfigureAccountCapacityUpgrade(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountCapacityUpgrade>(entity =>
            {
                entity.ToTable("account_capacity_upgrade");
                entity.HasKey(e => new { e.AccountId, e.UpgradeKind }).HasName("PRIMARY");

                // ValueGeneratedNever on both key columns: both are supplied by the caller and neither
                // carries AUTO_INCREMENT. EF infers store generation for an integer key by convention,
                // and an inferred store-generated key would make it omit the column from an INSERT.
                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                entity.Property(e => e.UpgradeKind).HasColumnName("upgrade_Kind").ValueGeneratedNever();
                entity.Property(e => e.UpgradeCount).HasColumnName("upgrade_Count");
                entity.Property(e => e.UpdatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("updated_At");
            });

            modelBuilder.Entity<AccountCapacityUpgradePurchase>(entity =>
            {
                entity.ToTable("account_capacity_upgrade_purchase");
                entity.HasKey(e => e.PurchaseToken).HasName("PRIMARY");
                entity.HasIndex(e => e.AccountId, "account_capacity_upgrade_purchase_account_idx");

                entity.Property(e => e.PurchaseToken).IsRequired().HasColumnName("purchase_Token").HasMaxLength(32).IsFixedLength().ValueGeneratedNever();
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.UpgradeKind).HasColumnName("upgrade_Kind");
                entity.Property(e => e.UpgradeNumber).HasColumnName("upgrade_Number");
                entity.Property(e => e.CostMmd).HasColumnName("cost_Mmd");
                entity.Property(e => e.CostPyreals).HasColumnName("cost_Pyreals");
                entity.Property(e => e.CharacterGuid).HasColumnName("character_Guid");
                entity.Property(e => e.PurchasedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("purchased_At");
            });
        }
    }
}

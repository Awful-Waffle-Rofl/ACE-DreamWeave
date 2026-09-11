using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Mule Vendor: the four fork tables created by
    // Database/Updates/Shard/2026-08-28-00-Add-Account-Vault.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following
    // CharacterSpeedRunPartial.cs. This is the established shape for a FORK-custom shard table; the
    // scaffolding flow described in CLAUDE.md applies to ACE's own tables.

    /// <summary>
    /// One vault container owned by an account. The container itself is an ordinary Container biota;
    /// this row is the account-to-container index. No FK to character - deliberate, see the migration
    /// header.
    /// </summary>
    public partial class AccountVault
    {
        public uint Id { get; set; }

        public uint AccountId { get; set; }

        /// <summary>The vault container biota's guid. Unique across the table.</summary>
        public uint ContainerGuid { get; set; }

        /// <summary>UTC. Always written explicitly; the column default is a local-time backstop.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Which kind of container this row indexes: 0 an ordinary vault container, 1 the account's
        /// barrel. Kept as an int rather than an enum for the same reason account_vault_log.Action is
        /// (see AccountVaultAction): a future member must not break rows already written.
        /// </summary>
        public int Kind { get; set; }
    }

    /// <summary>
    /// The collapsed-stackable ledger. NO biota exists behind these rows: a deposited item provably
    /// identical to its weenie template is destroyed and re-created on withdraw (DESIGN section 8).
    /// Count is total UNITS, independent of MaxStackSize.
    /// </summary>
    public partial class AccountVaultStack
    {
        public uint Id { get; set; }

        public uint AccountId { get; set; }

        public uint Wcid { get; set; }

        public long Count { get; set; }
    }

    /// <summary>
    /// A sharing grant. Mirrors HousePermission's guid-to-bool shape. CanWithdraw false is
    /// deposit-only. GranteeCharacterName is a snapshot so a revoke listing still names a deleted
    /// character.
    /// </summary>
    public partial class AccountVaultGrant
    {
        public uint Id { get; set; }

        public uint OwnerAccountId { get; set; }

        public uint GranteeCharacterGuid { get; set; }

        public string GranteeCharacterName { get; set; }

        public bool CanWithdraw { get; set; }

        public DateTime GrantedAt { get; set; }
    }

    /// <summary>
    /// One audited action against a store. Append-only; nothing in gameplay updates or deletes a row.
    /// Action is an int, not the AccountVaultAction enum, so a future member cannot break existing
    /// rows.
    /// </summary>
    public partial class AccountVaultLog
    {
        public uint Id { get; set; }

        public uint OwnerAccountId { get; set; }

        public uint ActorCharacterGuid { get; set; }

        public string ActorCharacterName { get; set; }

        public int Action { get; set; }

        public uint Wcid { get; set; }

        /// <summary>Null for a ledger row, which has no biota behind it.</summary>
        public uint? ItemGuid { get; set; }

        public string ItemName { get; set; }

        public long Count { get; set; }

        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// One barreling: a vault item the player destroyed, restorable by an administrator until
    /// retention purges it.
    ///
    /// The row OUTLIVES the item. PurgedAt is stamped when the retention sweep destroys the biota,
    /// and the row stays, because "what did this player throw away" has to remain answerable after
    /// the item itself is gone. RestoredAt and PurgedAt are mutually exclusive and both terminal:
    /// a row carrying either is not restorable.
    /// </summary>
    public partial class AccountVaultBarrel
    {
        public uint Id { get; set; }

        public uint AccountId { get; set; }

        public uint Wcid { get; set; }

        /// <summary>NULL for a ledger barreling, which has no biota behind it by definition.</summary>
        public uint? ItemGuid { get; set; }

        public long Count { get; set; }

        /// <summary>Snapshot at barrel time, so the row still names the item after it is destroyed.</summary>
        public string ItemName { get; set; }

        /// <summary>UTC. Always written explicitly; the column default is a local-time backstop.</summary>
        public DateTime BarreledAt { get; set; }

        public uint ActorCharacterGuid { get; set; }

        public string ActorCharacterName { get; set; }

        public DateTime? RestoredAt { get; set; }

        public DateTime? PurgedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AccountVault> AccountVault { get; set; }
        public virtual DbSet<AccountVaultStack> AccountVaultStack { get; set; }
        public virtual DbSet<AccountVaultGrant> AccountVaultGrant { get; set; }
        public virtual DbSet<AccountVaultLog> AccountVaultLog { get; set; }
        public virtual DbSet<AccountVaultBarrel> AccountVaultBarrel { get; set; }

        internal static void ConfigureAccountVault(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountVault>(entity =>
            {
                entity.ToTable("account_vault");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ContainerGuid, "account_vault_container_uidx").IsUnique();
                entity.HasIndex(e => e.AccountId, "account_vault_account_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.ContainerGuid).HasColumnName("container_Guid");
                entity.Property(e => e.CreatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("created_At");

                // 0 an ordinary vault container, 1 the account's barrel. Added by
                // Database/Updates/Shard/2026-09-04-00-Add-Account-Vault-Barrel.sql, whose column
                // default of 0 is what makes every pre-existing row an ordinary vault.
                entity.Property(e => e.Kind).HasColumnName("kind");
            });
        }

        internal static void ConfigureAccountVaultBarrel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountVaultBarrel>(entity =>
            {
                entity.ToTable("account_vault_barrel");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.AccountId, "account_vault_barrel_account_idx");
                entity.HasIndex(e => new { e.RestoredAt, e.PurgedAt, e.BarreledAt }, "account_vault_barrel_open_idx");

                // Every column is named explicitly: the table's spelling is snake-with-caps
                // (item_Guid, barreled_At), which EF's own conventions do not infer.
                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
                entity.Property(e => e.Count).HasColumnName("count");
                entity.Property(e => e.ItemName)
                    .IsRequired()
                    .HasColumnName("item_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.BarreledAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("barreled_At");
                entity.Property(e => e.ActorCharacterGuid).HasColumnName("actor_Character_Guid");
                entity.Property(e => e.ActorCharacterName)
                    .IsRequired()
                    .HasColumnName("actor_Character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.RestoredAt)
                    .HasColumnType("datetime")
                    .HasColumnName("restored_At");
                entity.Property(e => e.PurgedAt)
                    .HasColumnType("datetime")
                    .HasColumnName("purged_At");
            });
        }

        internal static void ConfigureAccountVaultStack(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountVaultStack>(entity =>
            {
                entity.ToTable("account_vault_stack");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.AccountId, e.Wcid }, "account_vault_stack_uidx").IsUnique();

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.Count).HasColumnName("count");
            });
        }

        internal static void ConfigureAccountVaultGrant(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountVaultGrant>(entity =>
            {
                entity.ToTable("account_vault_grant");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.OwnerAccountId, e.GranteeCharacterGuid }, "account_vault_grant_uidx").IsUnique();

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.OwnerAccountId).HasColumnName("owner_Account_Id");
                entity.Property(e => e.GranteeCharacterGuid).HasColumnName("grantee_Character_Guid");
                entity.Property(e => e.GranteeCharacterName)
                    .IsRequired()
                    .HasColumnName("grantee_Character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.CanWithdraw)
                    .HasColumnName("can_Withdraw")
                    .HasColumnType("bit(1)");
                entity.Property(e => e.GrantedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("granted_At");
            });
        }

        internal static void ConfigureAccountVaultLog(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountVaultLog>(entity =>
            {
                entity.ToTable("account_vault_log");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.OwnerAccountId, "account_vault_log_owner_idx");
                entity.HasIndex(e => e.Timestamp, "account_vault_log_time_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.OwnerAccountId).HasColumnName("owner_Account_Id");
                entity.Property(e => e.ActorCharacterGuid).HasColumnName("actor_Character_Guid");
                entity.Property(e => e.ActorCharacterName)
                    .IsRequired()
                    .HasColumnName("actor_Character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.Action).HasColumnName("action");
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
                entity.Property(e => e.ItemName)
                    .IsRequired()
                    .HasColumnName("item_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.Count).HasColumnName("count");
                entity.Property(e => e.Timestamp)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("timestamp");
            });
        }
    }
}

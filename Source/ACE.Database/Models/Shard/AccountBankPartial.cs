using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Account-wide banked pyreals: the two fork tables created by
    // Database/Updates/Shard/2026-09-01-00-Add-Account-Bank.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following AccountVaultPartial.cs
    // and CharacterSpeedRunPartial.cs. This is the established shape for a FORK-custom shard table;
    // the scaffolding flow described in CLAUDE.md applies to ACE's own tables.

    /// <summary>
    /// One account's pyreal pool - the account-wide replacement for the per-character
    /// PropertyInt64.BankedPyreals (9004). Keyed by account_Id: the account is the identity of the
    /// row, so there is no surrogate id.
    ///
    /// NOTHING should read this entity to compute a new balance and write it back. The balance is
    /// moved only by ShardDatabase.TryAdjustAccountBank's guarded UPDATE, which applies the delta and
    /// adjudicates its bounds in one statement. A read-modify-write here would lose a concurrent
    /// delta, and a lost delta on this table is duplicated or destroyed currency.
    /// </summary>
    public partial class AccountBank
    {
        public uint AccountId { get; set; }

        /// <summary>
        /// Total banked pyreals for the whole account. bigint, not int: a banked balance has no
        /// ceiling of its own, and an int would cap a hoard at ~2.1 billion, which is reachable.
        /// </summary>
        public long BankedPyreals { get; set; }

        /// <summary>UTC. The column default is a local-time backstop; writers stamp it explicitly.</summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// One character whose old per-character 9004 balance has been folded into its account's pool.
    /// Keyed by character_Guid, which is what makes the fold happen exactly once per character no
    /// matter how the bulk migration and the per-login lazy fold interleave.
    ///
    /// <see cref="Amount"/> is not decoration: if the process dies between claiming the row and
    /// crediting the pool, this row is the only record of what was owed.
    /// </summary>
    public partial class AccountBankFold
    {
        public uint CharacterGuid { get; set; }

        public uint AccountId { get; set; }

        public long Amount { get; set; }

        /// <summary>UTC. The column default is a local-time backstop; writers stamp it explicitly.</summary>
        public DateTime FoldedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AccountBank> AccountBank { get; set; }
        public virtual DbSet<AccountBankFold> AccountBankFold { get; set; }

        internal static void ConfigureAccountBank(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountBank>(entity =>
            {
                entity.ToTable("account_bank");
                entity.HasKey(e => e.AccountId);

                // ValueGeneratedNever, unlike every other key in this file: account_Id is supplied by
                // the caller and the column carries no AUTO_INCREMENT. EF infers generation for an
                // integer key by convention, and an inferred store-generated key would make it omit
                // the column from an INSERT.
                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                entity.Property(e => e.BankedPyreals).HasColumnName("banked_Pyreals");
                entity.Property(e => e.UpdatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("updated_At");
            });
        }

        internal static void ConfigureAccountBankFold(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountBankFold>(entity =>
            {
                entity.ToTable("account_bank_fold");
                entity.HasKey(e => e.CharacterGuid);
                entity.HasIndex(e => e.AccountId, "account_bank_fold_account_idx");

                entity.Property(e => e.CharacterGuid).HasColumnName("character_Guid").ValueGeneratedNever();
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.Amount).HasColumnName("amount");
                entity.Property(e => e.FoldedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("folded_At");
            });
        }
    }
}

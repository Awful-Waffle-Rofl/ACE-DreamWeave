using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Mule Form Token: the account_mule_form table created by
    // Database/Updates/Shard/2026-09-01-00-Add-Account-Mule-Form.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following AccountBankPartial.cs
    // and AccountVaultPartial.cs. This is the established shape for a FORK-custom shard table; the
    // scaffolding flow described in CLAUDE.md applies to ACE's own tables.

    /// <summary>
    /// The creature form one account's /mule vendor wears. Keyed by account_Id: the account is the
    /// identity of the row, so there is no surrogate id, and replacing a look is an upsert against
    /// that key rather than a delete plus an insert.
    ///
    /// Cosmetic data only. Nothing here may ever be able to refuse a mule summon: a missing row, a
    /// stale wcid or a failed read all fall through to the default vendor look
    /// (see MuleSummonHandler and Docs/MuleVendor/2026-09-01-mule-form-token-design.md section 8).
    /// </summary>
    public partial class AccountMuleForm
    {
        public uint AccountId { get; set; }

        /// <summary>
        /// WeenieClassId of the donor creature. May name a weenie that no longer exists in the world
        /// database; the summon path treats that as "no form", never as an error.
        /// </summary>
        public uint FormWcid { get; set; }

        /// <summary>
        /// Snapshot of the donor's name at write time. Operator convenience only - nothing joins to
        /// it, nothing keys on it, and it is never refreshed if the donor weenie is renamed.
        /// </summary>
        public string FormName { get; set; }

        /// <summary>Snapshot of the character that set the look. Audit only, no FK, never read.</summary>
        public uint SetByCharacterGuid { get; set; }

        /// <summary>Seconds since the Unix epoch. Written explicitly; the column carries no default.</summary>
        public double SetUnixTime { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AccountMuleForm> AccountMuleForm { get; set; }

        internal static void ConfigureAccountMuleForm(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountMuleForm>(entity =>
            {
                entity.ToTable("account_mule_form");
                entity.HasKey(e => e.AccountId);

                // ValueGeneratedNever: account_Id is supplied by the caller and the column carries no
                // AUTO_INCREMENT. EF infers store generation for an integer key by convention, and an
                // inferred store-generated key would make it omit the column from an INSERT.
                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                entity.Property(e => e.FormWcid).HasColumnName("form_Wcid");
                entity.Property(e => e.FormName).HasColumnName("form_Name").HasMaxLength(64);
                entity.Property(e => e.SetByCharacterGuid).HasColumnName("set_By_Character_Guid");
                entity.Property(e => e.SetUnixTime).HasColumnName("set_Unix_Time");
            });
        }
    }
}

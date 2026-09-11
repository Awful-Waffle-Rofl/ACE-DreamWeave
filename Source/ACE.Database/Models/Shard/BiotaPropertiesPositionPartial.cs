using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // ACRealms port Phase 2: the `instance` column added by
    // Database/Updates/Shard/2026-07-10-00-Add-Biota-Position-Instance.sql.
    // Kept in partial classes so the scaffolded files stay untouched; a future
    // Scaffold-DbContext run will fold this in (see Scaffolding Notes.txt).
    public partial class BiotaPropertiesPosition
    {
        /// <summary>
        /// The landblock instance this position belongs to. Null means instance 0 (the base world).
        /// </summary>
        public uint? Instance { get; set; }
    }

    public partial class ShardDbContext
    {
        partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BiotaPropertiesPosition>(entity =>
            {
                entity.Property(e => e.Instance).HasColumnName("instance");
            });

            ConfigureCharacterSpeedRun(modelBuilder);

            ConfigureAccountVault(modelBuilder);
            ConfigureAccountVaultStack(modelBuilder);
            ConfigureAccountVaultGrant(modelBuilder);
            ConfigureAccountVaultLog(modelBuilder);
            ConfigureAccountVaultBarrel(modelBuilder);

            ConfigureMarketListing(modelBuilder);
            ConfigureMarketTransaction(modelBuilder);
            ConfigureMarketRejectedAttempt(modelBuilder);
            ConfigureMarketBuyOrder(modelBuilder);
            ConfigureAccountBank(modelBuilder);
            ConfigureAccountBankFold(modelBuilder);

            ConfigureAccountMuleForm(modelBuilder);

            ConfigureCharacterFacet(modelBuilder);

            ConfigureCharacterCapLedger(modelBuilder);
            ConfigureCharacterCapAudit(modelBuilder);
        }
    }
}

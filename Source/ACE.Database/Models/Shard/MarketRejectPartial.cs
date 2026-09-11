using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Market: hand-written partial for the rejected-attempt audit table, following MarketPartial.cs.
    // Unlike MarketListing / MarketTransaction there is no same-named runtime DTO, so this type needs
    // no alias at its call sites.

    /// <summary>
    /// One refused listing, delist or purchase attempt that never reached market_transaction
    /// (Docs/Market/DESIGN.md section 5.5).
    ///
    /// DELIBERATELY NOT RECORDED: MarketError.Disabled (the operator kill switch, not a player
    /// event), transport-layer refusals inside MarketApiHost (bad shared key, expired bearer,
    /// rate-limit 429 - no resolved actor), and anything that reaches the Pending row in
    /// market_transaction (that table is the authority on it).
    /// </summary>
    public partial class MarketRejectedAttempt
    {
        public uint Id { get; set; }

        /// <summary>MarketRejectOperation: 0 List, 1 Buy, 2 Delist.</summary>
        public int Operation { get; set; }

        /// <summary>
        /// The MarketErrorCodes.ToCode STRING, or the single synthetic value repository_refused.
        /// Never the MarketError ordinal - those are implicit and renumber when a member is inserted.
        /// </summary>
        public string ReasonCode { get; set; }

        /// <summary>0 means unresolved.</summary>
        public uint AccountId { get; set; }

        /// <summary>0 means unknown - the web delist path carries no character.</summary>
        public uint CharacterGuid { get; set; }

        /// <summary>Snapshot at attempt time; empty string when unknown.</summary>
        public string CharacterName { get; set; }

        public uint? ListingId { get; set; }

        public uint? ItemGuid { get; set; }

        public uint Wcid { get; set; }

        public int Count { get; set; }

        /// <summary>Per unit as the actor asked for it, in Trade Note (250,000).</summary>
        public long PriceMmd { get; set; }

        /// <summary>0 web, 1 ingame - the same encoding as market_transaction.channel.</summary>
        public int Channel { get; set; }

        public DateTime Timestamp { get; set; }

        /// <summary>Free text for the few codes a bare reason cannot explain. Null otherwise.</summary>
        public string Detail { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<MarketRejectedAttempt> MarketRejectedAttempt { get; set; }

        internal static void ConfigureMarketRejectedAttempt(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MarketRejectedAttempt>(entity =>
            {
                entity.ToTable("market_rejected_attempt");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.AccountId, e.Timestamp }, "market_rejected_attempt_account_time_idx");
                entity.HasIndex(e => e.Timestamp, "market_rejected_attempt_time_idx");
                entity.HasIndex(e => e.ListingId, "market_rejected_attempt_listing_idx");
                entity.HasIndex(e => new { e.ReasonCode, e.Timestamp }, "market_rejected_attempt_reason_time_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.Operation).HasColumnName("operation");
                entity.Property(e => e.ReasonCode)
                    .IsRequired()
                    .HasColumnName("reason_Code")
                    .HasMaxLength(32);
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.CharacterGuid).HasColumnName("character_Guid");
                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.ListingId).HasColumnName("listing_Id");
                entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.Count).HasColumnName("count");
                entity.Property(e => e.PriceMmd).HasColumnName("price_Mmd");
                entity.Property(e => e.Channel).HasColumnName("channel");
                entity.Property(e => e.Timestamp)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("timestamp");
                entity.Property(e => e.Detail)
                    .HasColumnName("detail")
                    .HasMaxLength(255);
            });
        }
    }
}

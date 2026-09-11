using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Market: hand-written partials for the two fork tables, following AccountVaultPartial.cs.
    // NAME COLLISION on purpose with ACE.Server.Managers.Market's DTOs - alias as
    // ShardMarketListing / ShardMarketTransaction in any file that touches both.

    /// <summary>One listing - a flag over a vault item, not a move.</summary>
    public partial class MarketListing
    {
        public uint Id { get; set; }

        public uint SellerAccountId { get; set; }

        public uint SellerCharacterGuid { get; set; }

        public string SellerCharacterName { get; set; }

        /// <summary>Null for a collapsed vault ledger stack (no biota).</summary>
        public uint? ItemGuid { get; set; }

        /// <summary>Equals <see cref="ItemGuid"/> while Active, null otherwise. UNIQUE.</summary>
        public uint? ActiveItemGuid { get; set; }

        /// <summary>Equals <see cref="Wcid"/> while an Active ledger listing, null otherwise.</summary>
        public uint? ActiveLedgerWcid { get; set; }

        public uint Wcid { get; set; }

        public int Count { get; set; }

        /// <summary>Per unit, in Trade Note (250,000).</summary>
        public long PriceMmd { get; set; }

        /// <summary>0 Active, 1 Sold, 2 Delisted, 3 Invalidated.</summary>
        public int Status { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string SnapshotJson { get; set; }
    }

    /// <summary>One purchase - inserted Pending, updated once to a terminal status.</summary>
    public partial class MarketTransaction
    {
        public uint Id { get; set; }

        public uint ListingId { get; set; }

        /// <summary>Wanted order this row filled; null for an ordinary sale.</summary>
        public uint? BuyOrderId { get; set; }

        public uint BuyerAccountId { get; set; }

        public uint BuyerCharacterGuid { get; set; }

        public string BuyerCharacterName { get; set; }

        public uint SellerAccountId { get; set; }

        public uint SellerCharacterGuid { get; set; }

        public string SellerCharacterName { get; set; }

        public uint Wcid { get; set; }

        public string ItemName { get; set; }

        public int Count { get; set; }

        public long PriceMmdTotal { get; set; }

        public DateTime Timestamp { get; set; }

        /// <summary>0 web, 1 ingame.</summary>
        public int Channel { get; set; }

        /// <summary>0 Pending, 1 Completed, 2 Refunded, 3 Failed.</summary>
        public int Status { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<MarketListing> MarketListing { get; set; }
        public virtual DbSet<MarketTransaction> MarketTransaction { get; set; }

        internal static void ConfigureMarketListing(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MarketListing>(entity =>
            {
                entity.ToTable("market_listing");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ActiveItemGuid, "market_listing_active_item_uidx").IsUnique();
                entity.HasIndex(e => new { e.SellerAccountId, e.ActiveLedgerWcid }, "market_listing_active_ledger_uidx").IsUnique();
                entity.HasIndex(e => e.SellerAccountId, "market_listing_seller_idx");
                entity.HasIndex(e => e.Status, "market_listing_status_idx");
                entity.HasIndex(e => e.Wcid, "market_listing_wcid_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.SellerAccountId).HasColumnName("seller_Account_Id");
                entity.Property(e => e.SellerCharacterGuid).HasColumnName("seller_Character_Guid");
                entity.Property(e => e.SellerCharacterName)
                    .IsRequired()
                    .HasColumnName("seller_Character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
                entity.Property(e => e.ActiveItemGuid).HasColumnName("active_Item_Guid");
                entity.Property(e => e.ActiveLedgerWcid).HasColumnName("active_Ledger_Wcid");
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.Count).HasColumnName("count");
                entity.Property(e => e.PriceMmd).HasColumnName("price_Mmd");
                entity.Property(e => e.Status).HasColumnName("status");
                entity.Property(e => e.CreatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("created_At");
                entity.Property(e => e.ClosedAt)
                    .HasColumnType("datetime")
                    .HasColumnName("closed_At");
                entity.Property(e => e.SnapshotJson)
                    .IsRequired()
                    .HasColumnType("text")
                    .HasColumnName("snapshot_Json");
            });
        }

        internal static void ConfigureMarketTransaction(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MarketTransaction>(entity =>
            {
                entity.ToTable("market_transaction");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.BuyerAccountId, "market_transaction_buyer_idx");
                entity.HasIndex(e => e.SellerAccountId, "market_transaction_seller_idx");
                entity.HasIndex(e => e.Timestamp, "market_transaction_time_idx");
                entity.HasIndex(e => e.Status, "market_transaction_status_idx");
                entity.HasIndex(e => e.BuyOrderId, "market_transaction_buy_order_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.ListingId).HasColumnName("listing_Id");
                entity.Property(e => e.BuyOrderId).HasColumnName("buy_order_Id");
                entity.Property(e => e.BuyerAccountId).HasColumnName("buyer_Account_Id");
                entity.Property(e => e.BuyerCharacterGuid).HasColumnName("buyer_Character_Guid");
                entity.Property(e => e.BuyerCharacterName)
                    .IsRequired()
                    .HasColumnName("buyer_Character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.SellerAccountId).HasColumnName("seller_Account_Id");
                entity.Property(e => e.SellerCharacterGuid).HasColumnName("seller_Character_Guid");
                entity.Property(e => e.SellerCharacterName)
                    .IsRequired()
                    .HasColumnName("seller_Character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.ItemName)
                    .IsRequired()
                    .HasColumnName("item_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.Count).HasColumnName("count");
                entity.Property(e => e.PriceMmdTotal).HasColumnName("price_Mmd_Total");
                entity.Property(e => e.Timestamp)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("timestamp");
                entity.Property(e => e.Channel).HasColumnName("channel");
                entity.Property(e => e.Status).HasColumnName("status");
            });
        }
    }
}

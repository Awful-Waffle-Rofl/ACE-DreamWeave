using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // NAME COLLISION on purpose with ACE.Server.Managers.Market.MarketBuyOrder - alias this one as
    // ShardMarketBuyOrder in any file that touches both, exactly as MarketPartial.cs asks.

    /// <summary>One Wanted buy order - an escrow-funded standing offer for full salvage bags of one material.</summary>
    public partial class MarketBuyOrder
    {
        public uint Id { get; set; }
        public uint BuyerAccountId { get; set; }
        public uint BuyerCharacterGuid { get; set; }
        public string BuyerCharacterName { get; set; }
        public int MaterialType { get; set; }
        /// <summary>
        /// 0 SalvageBag, 1 SalvageHammer (ACE.Server.Managers.Market.MarketBuyOrderKind). Every row
        /// written before this column existed reads 0, which is what they all were.
        /// </summary>
        public byte OrderKind { get; set; }
        /// <summary>The bag wcid for a bag order, the HAMMER wcid for a hammer order. Informational.</summary>
        public uint Wcid { get; set; }
        /// <summary>Per bag, in Trade Note (250,000).</summary>
        public long PriceMmd { get; set; }
        public int CountTotal { get; set; }
        public int CountRemaining { get; set; }
        /// <summary>MMD still held for this order. While Active, equals CountRemaining * PriceMmd.</summary>
        public long EscrowMmd { get; set; }
        /// <summary>0 Pending, 1 Active, 2 Filled, 3 Cancelled, 4 Expired, 5 Failed, 6 DebitLedgerUnknown.</summary>
        public int Status { get; set; }
        /// <summary>Equals <see cref="MaterialType"/> while Active, null otherwise. Part of a UNIQUE key that also spans <see cref="OrderKind"/>.</summary>
        public int? ActiveMaterial { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? ClosedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<MarketBuyOrder> MarketBuyOrder { get; set; }

        internal static void ConfigureMarketBuyOrder(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MarketBuyOrder>(entity =>
            {
                entity.ToTable("market_buy_order");
                entity.HasKey(e => e.Id);
                // Spans the kind as well, so one buyer may hold an Active BAG order and an Active
                // HAMMER order for the same material at once. active_Material stays NULL except while
                // Active, which is what keeps the key from applying to closed rows.
                entity.HasIndex(e => new { e.BuyerAccountId, e.ActiveMaterial, e.OrderKind }, "market_buy_order_active_uidx").IsUnique();
                entity.HasIndex(e => e.Status, "market_buy_order_status_idx");
                entity.HasIndex(e => e.MaterialType, "market_buy_order_material_idx");
                entity.HasIndex(e => e.ExpiresAt, "market_buy_order_expires_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.BuyerAccountId).HasColumnName("buyer_Account_Id");
                entity.Property(e => e.BuyerCharacterGuid).HasColumnName("buyer_Character_Guid");
                entity.Property(e => e.BuyerCharacterName).IsRequired().HasColumnName("buyer_Character_Name").HasMaxLength(255);
                entity.Property(e => e.MaterialType).HasColumnName("material_Type");
                // No HasDefaultValue: the column's DEFAULT 0 exists for rows written before it did, and
                // EF must always send the kind explicitly rather than treat a written 0 as "unset".
                entity.Property(e => e.OrderKind).HasColumnName("order_Kind");
                entity.Property(e => e.Wcid).HasColumnName("wcid");
                entity.Property(e => e.PriceMmd).HasColumnName("price_Mmd");
                entity.Property(e => e.CountTotal).HasColumnName("count_Total");
                entity.Property(e => e.CountRemaining).HasColumnName("count_Remaining");
                entity.Property(e => e.EscrowMmd).HasColumnName("escrow_Mmd");
                entity.Property(e => e.Status).HasColumnName("status");
                entity.Property(e => e.ActiveMaterial).HasColumnName("active_Material");
                entity.Property(e => e.CreatedAt).HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").HasColumnName("created_At");
                entity.Property(e => e.ExpiresAt).HasColumnType("datetime").HasColumnName("expires_At");
                entity.Property(e => e.ClosedAt).HasColumnType("datetime").HasColumnName("closed_At");
            });
        }
    }
}

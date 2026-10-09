using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Reward claim ledger: hand-written partial, following CharacterSheetLinkPartial.cs.
    // Table created by Database/Updates/Shard/2026-09-15-01-Add-Reward-Claim.sql.

    /// <summary>One granted claim of an allowlisted reward key by one account (and, unless exempt, one IP).</summary>
    public partial class RewardClaim
    {
        /// <summary>Allowlisted claim key. Part of the PRIMARY KEY and of the (key, ip) UNIQUE key.</summary>
        public string ClaimKey { get; set; }

        public uint AccountId { get; set; }

        /// <summary>Normalized remote address. NULL when the claiming session was exempt from the IP active-player limit.</summary>
        public string IpKey { get; set; }

        /// <summary>Audit copy of the remote address, recorded even for an exempt session.</summary>
        public string IpAddress { get; set; }

        public uint CharacterId { get; set; }

        public uint NpcWcid { get; set; }

        /// <summary>32 hex chars, random per attempt. Lets a retried insert recognize its own committed row.</summary>
        public string ClaimToken { get; set; }

        /// <summary>UTC; stamped by the INSERT itself.</summary>
        public DateTime ClaimedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<RewardClaim> RewardClaim { get; set; }

        internal static void ConfigureRewardClaim(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RewardClaim>(entity =>
            {
                entity.ToTable("reward_claim");
                entity.HasKey(e => new { e.ClaimKey, e.AccountId }).HasName("PRIMARY");
                entity.HasIndex(e => new { e.ClaimKey, e.IpKey }, "reward_claim_ip_uidx").IsUnique();
                entity.Property(e => e.ClaimKey).IsRequired().HasColumnName("claim_Key").HasMaxLength(64);
                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                entity.Property(e => e.IpKey).HasColumnName("ip_Key").HasMaxLength(45);
                entity.Property(e => e.IpAddress).HasColumnName("ip_Address").HasMaxLength(45);
                entity.Property(e => e.CharacterId).HasColumnName("character_Id");
                entity.Property(e => e.NpcWcid).HasColumnName("npc_Wcid");
                entity.Property(e => e.ClaimToken).IsRequired().HasColumnName("claim_Token").HasMaxLength(32).IsFixedLength();
                entity.Property(e => e.ClaimedAt).HasColumnName("claimed_At").HasColumnType("datetime");
            });
        }
    }
}
